using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresDiscordIntegration.Discord
{
    /// <summary>
    /// Shared client-to-server screenshot pipeline for event posts (death / achievement / skill level-up).
    /// The client captures its screen (end-of-frame after a small delay so the moment's UI — ragdoll,
    /// achievement toast, level-up message — is visible), downsizes to 1280w, JPG-encodes and ships it over
    /// ONE kind-tagged chunked routed RPC. When capture fails (or a capture is already in flight) the event
    /// still ships with an empty image so the server posts a text-only embed — the message never depends on
    /// the picture. The server reassembles per (sender, kind) and routes by kind.
    ///
    /// ScreenCapture / ImageConversion are reflection-invoked: compile-time refs to those Unity modules pull
    /// netstandard 2.1 into this netstandard 2.0 build and fail CS1705.
    /// </summary>
    [HarmonyPatch]
    internal static class ShotPipeline
    {
        internal enum ShotKind
        {
            Death = 0,
            Achievement = 1,
            SkillLevelUp = 2,
            Triggered = 3,   // operator-triggered (EWP poke etc.); metaA = reason/caption
            Bridge = 4,      // requested by another mod over the Core bridge (test tools); metaA = caption
        }

        private const string RpcName = "VAG_EventShot";
        private const int MaxWidth = 1280;
        private const int JpgQuality = 78;
        private const int ChunkSize = 400 * 1024;      // matches the client-log push framing headroom
        private const int MaxTotalBytes = 6 * 1024 * 1024;
        private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(60);

        private static bool _busy;

        // ── Server: reassembly state, keyed per sender+kind so interleaved events can't corrupt each other ──
        private sealed class Transfer
        {
            public byte[][] Chunks;
            public int Received;
            public string MetaA;
            public string MetaB;
            public float MetaC;
            public DateTime LastUtc;
        }

        private static readonly Dictionary<long, Transfer> _transfers = new Dictionary<long, Transfer>();
        private static readonly object _lock = new object();

        [HarmonyPatch(typeof(ZNet), "Awake")]
        [HarmonyPostfix]
        private static void ZNet_Awake_Postfix(ZNet __instance)
        {
            try
            {
                if (__instance == null || !__instance.IsServer()) return;
                ZRoutedRpc.instance?.Register<ZPackage>(RpcName, OnServerReceive);
            }
            catch (Exception ex) { Debug.LogWarning($"[Discord] EventShot RPC register failed: {ex.Message}"); }
        }

        // ── Client entry point ────────────────────────────────────────────────────────────────────────
        /// <summary>Capture after <paramref name="delaySeconds"/> and ship as <paramref name="kind"/> with metadata.</summary>
        internal static void CaptureAndSend(ShotKind kind, string metaA, string metaB, float metaC, float delaySeconds)
        {
            try
            {
                if (ZNet.instance == null || ZNet.instance.IsServer()) return;
                if (_busy)
                {
                    // A capture is already in flight (e.g. two skill-ups in one swing) — still deliver the
                    // event, just without its own picture.
                    SendBytes(kind, metaA, metaB, metaC, Array.Empty<byte>());
                    return;
                }
                _busy = true;
                FiresCore.Async.MainThreadDispatcher.StartRoutine(CaptureRoutine(kind, metaA, metaB, metaC, delaySeconds));
            }
            catch (Exception ex)
            {
                _busy = false;
                Debug.LogWarning($"[Discord] EventShot start failed: {ex.Message}");
            }
        }

        private static IEnumerator CaptureRoutine(ShotKind kind, string metaA, string metaB, float metaC, float delaySeconds)
        {
            yield return new WaitForSeconds(delaySeconds);
            yield return new WaitForEndOfFrame();

            byte[] jpg = null;
            try
            {
                var shot = CaptureScreenshotReflected();
                if (shot != null)
                {
                    var scaled = DownscaleIfNeeded(shot);
                    jpg = EncodeToJpgReflected(scaled, JpgQuality);
                    if (scaled != shot) UnityEngine.Object.Destroy(scaled);
                    UnityEngine.Object.Destroy(shot);
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[Discord] EventShot capture failed: {ex.Message}"); }

            if (jpg == null || jpg.Length > MaxTotalBytes) jpg = Array.Empty<byte>();

            yield return SendBytesRoutine(kind, metaA, metaB, metaC, jpg);
            _busy = false;
        }

        private static void SendBytes(ShotKind kind, string metaA, string metaB, float metaC, byte[] jpg)
            => FiresCore.Async.MainThreadDispatcher.StartRoutine(SendBytesRoutine(kind, metaA, metaB, metaC, jpg));

        private static IEnumerator SendBytesRoutine(ShotKind kind, string metaA, string metaB, float metaC, byte[] jpg)
        {
            long serverId = ZRoutedRpc.instance != null ? ZRoutedRpc.instance.GetServerPeerID() : 0L;
            if (serverId == 0L) yield break;

            int totalChunks = Math.Max(1, (jpg.Length + ChunkSize - 1) / ChunkSize);
            for (int i = 0; i < totalChunks; i++)
            {
                int offset = i * ChunkSize;
                int size = Math.Max(0, Math.Min(ChunkSize, jpg.Length - offset));
                var chunk = new byte[size];
                if (size > 0) Array.Copy(jpg, offset, chunk, 0, size);

                var pkg = new ZPackage();
                pkg.Write((int)kind);
                pkg.Write(metaA ?? string.Empty);
                pkg.Write(metaB ?? string.Empty);
                pkg.Write(metaC);
                pkg.Write(i);
                pkg.Write(totalChunks);
                pkg.Write(chunk);
                try { ZRoutedRpc.instance.InvokeRoutedRPC(serverId, RpcName, pkg); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Discord] EventShot send failed: {ex.Message}");
                    yield break;
                }
                yield return null;
            }
        }

        // ── Server: reassemble + route ────────────────────────────────────────────────────────────────
        private static void OnServerReceive(long sender, ZPackage pkg)
        {
            try
            {
                int kindRaw = pkg.ReadInt();
                string metaA = pkg.ReadString();
                string metaB = pkg.ReadString();
                float metaC = pkg.ReadSingle();
                int chunkIndex = pkg.ReadInt();
                int totalChunks = pkg.ReadInt();
                byte[] chunk = pkg.ReadByteArray();

                if (kindRaw < 0 || kindRaw > (int)ShotKind.Bridge) return;
                if (chunkIndex < 0 || totalChunks <= 0 || totalChunks > MaxTotalBytes / ChunkSize + 1
                    || chunkIndex >= totalChunks || chunk == null)
                    return;
                if (metaA.Length > 128) metaA = metaA.Substring(0, 128);
                if (metaB.Length > 256) metaB = metaB.Substring(0, 256);

                var kind = (ShotKind)kindRaw;
                long key = sender * 8 + kindRaw; // kind-count headroom so interleaved kinds never share a slot

                byte[] complete = null;
                lock (_lock)
                {
                    CleanupStale();
                    if (!_transfers.TryGetValue(key, out var t) || t.Chunks.Length != totalChunks)
                    {
                        t = new Transfer { Chunks = new byte[totalChunks][] };
                        _transfers[key] = t;
                    }
                    if (t.Chunks[chunkIndex] == null) t.Received++;
                    t.Chunks[chunkIndex] = chunk;
                    t.MetaA = metaA; t.MetaB = metaB; t.MetaC = metaC;
                    t.LastUtc = DateTime.UtcNow;

                    if (t.Received == totalChunks)
                    {
                        int total = 0;
                        foreach (var c in t.Chunks) total += c.Length;
                        if (total <= MaxTotalBytes && totalChunks == 1)
                        {
                            complete = t.Chunks[0]; // a one-chunk shot is already whole; no second copy
                        }
                        else if (total <= MaxTotalBytes)
                        {
                            complete = new byte[total];
                            int off = 0;
                            foreach (var c in t.Chunks) { Array.Copy(c, 0, complete, off, c.Length); off += c.Length; }
                        }
                        _transfers.Remove(key);
                    }
                }
                if (complete == null) return;

                var peer = ZNet.instance?.GetPeer(sender);
                string playerName = peer?.m_playerName ?? "Unknown";
                string platformId = peer?.m_socket?.GetHostName() ?? "unknown";

                switch (kind)
                {
                    case ShotKind.Death:
                        DeathScreenshots.OnShotArrived(playerName, platformId, complete);
                        break;
                    case ShotKind.Achievement:
                        DiscordIntegrationCore.OnPlayerAchievement(playerName, platformId, metaA, metaB, complete);
                        break;
                    case ShotKind.SkillLevelUp:
                        DiscordIntegrationCore.OnPlayerSkillLevelUp(playerName, platformId, metaA, (int)metaC, complete);
                        break;
                    case ShotKind.Triggered:
                        DiscordIntegrationCore.OnPlayerTriggeredScreenshot(playerName, platformId, metaA, complete);
                        break;
                    case ShotKind.Bridge:
                        DiscordIntegrationCore.OnPlayerTriggeredScreenshot(playerName, platformId, metaA, complete,
                            WebhookCategory.TestShots);
                        break;
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[Discord] EventShot receive failed: {ex.Message}"); }
        }

        private static void CleanupStale()
        {
            List<long> stale = null;
            var now = DateTime.UtcNow;
            foreach (var kv in _transfers)
                if (now - kv.Value.LastUtc > TransferTimeout)
                    (stale ?? (stale = new List<long>())).Add(kv.Key);
            if (stale != null) foreach (var id in stale) _transfers.Remove(id);
        }

        // ── Image helpers (client) ────────────────────────────────────────────────────────────────────
        private static Texture2D DownscaleIfNeeded(Texture2D src)
        {
            if (src.width <= MaxWidth) return src;

            int dw = MaxWidth;
            int dh = Mathf.Max(1, Mathf.RoundToInt(src.height * (MaxWidth / (float)src.width)));
            var srcPx = src.GetPixels32();
            var dstPx = new Color32[dw * dh];
            int sw = src.width, sh = src.height;

            for (int y = 0; y < dh; y++)
            {
                int sy0 = y * sh / dh;
                int sy1 = Math.Max(sy0, (y + 1) * sh / dh - 1);
                for (int x = 0; x < dw; x++)
                {
                    int sx0 = x * sw / dw;
                    int sx1 = Math.Max(sx0, (x + 1) * sw / dw - 1);
                    int r = 0, g = 0, b = 0, n = 0;
                    for (int sy = sy0; sy <= sy1; sy++)
                        for (int sx = sx0; sx <= sx1; sx++)
                        {
                            var p = srcPx[sy * sw + sx];
                            r += p.r; g += p.g; b += p.b; n++;
                        }
                    dstPx[y * dw + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
                }
            }

            var dst = new Texture2D(dw, dh, TextureFormat.RGBA32, false);
            dst.SetPixels32(dstPx);
            return dst;
        }

        private static MethodInfo _captureShot;
        private static MethodInfo _encodeJpg;
        private static bool _reflectionResolved;

        private static void ResolveReflection()
        {
            if (_reflectionResolved) return;
            _reflectionResolved = true;
            var cap = Type.GetType("UnityEngine.ScreenCapture, UnityEngine.ScreenCaptureModule");
            _captureShot = cap?.GetMethod("CaptureScreenshotAsTexture", new[] { typeof(int) });
            var img = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
            _encodeJpg = img?.GetMethod("EncodeToJPG", new[] { typeof(Texture2D), typeof(int) });
        }

        private static Texture2D CaptureScreenshotReflected()
        {
            ResolveReflection();
            if (_captureShot == null) return null;
            try { return (Texture2D)_captureShot.Invoke(null, new object[] { 1 }); }
            catch { return null; }
        }

        private static byte[] EncodeToJpgReflected(Texture2D tex, int quality)
        {
            ResolveReflection();
            if (_encodeJpg == null) return null;
            try { return (byte[])_encodeJpg.Invoke(null, new object[] { tex, quality }); }
            catch { return null; }
        }
    }
}
