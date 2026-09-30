using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresDiscordIntegration.Discord
{
    /// <summary>
    /// Death event handling on top of <see cref="ShotPipeline"/>: the dying player's CLIENT reports its
    /// last hit (cause/attacker/damage — a tiny always-sent RPC) and, when enabled, captures a death
    /// screenshot which the pipeline ships to the server. The SERVER posts the death embed the moment the
    /// image arrives; the respawn-based fallback (DiscordIntegrationPatches) only posts when no screenshot
    /// covered the death, and ONLY when a death report exists — a repeat CharacterID without one is a
    /// re-login, not a death (this is what stopped logout/reconnects from posting fake deaths).
    /// </summary>
    [HarmonyPatch]
    internal static class DeathScreenshots
    {
        private const string InfoRpcName = "VAG_DeathInfo";
        private const float CaptureDelaySeconds = 0.7f;
        private static readonly TimeSpan DedupeTtl = TimeSpan.FromMinutes(10);

        private static readonly Dictionary<string, DateTime> _recentPosts =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new object();

        /// <summary>What the dying client reported as its last hit — cause, attacker display name, damage.</summary>
        internal sealed class DeathInfo
        {
            public HitData.HitType HitType;
            public string Attacker;
            public float Damage;
            public DateTime WhenUtc;
        }

        // Latest death info per platformId (each death overwrites; TTL'd). Read by BOTH post paths, so the
        // cause survives whichever fires: the immediate screenshot post or the respawn-based fallback.
        private static readonly Dictionary<string, DeathInfo> _deathInfo =
            new Dictionary<string, DeathInfo>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Death info reported for this player within the TTL, or null.</summary>
        internal static DeathInfo TryGetDeathInfo(string platformId)
        {
            if (string.IsNullOrEmpty(platformId)) return null;
            lock (_lock)
            {
                if (!_deathInfo.TryGetValue(platformId, out var info)) return null;
                if (DateTime.UtcNow - info.WhenUtc > DedupeTtl) { _deathInfo.Remove(platformId); return null; }
                return info;
            }
        }

        /// <summary>
        /// Drop the player's death report once the respawn-side handling has used it, so a later
        /// re-login inside the TTL can never replay it as another death.
        /// </summary>
        internal static void ConsumeDeathInfo(string platformId)
        {
            if (string.IsNullOrEmpty(platformId)) return;
            lock (_lock) { _deathInfo.Remove(platformId); }
        }

        // ── RPC registration (server receives; clients only send) ────────────────────────────────────
        [HarmonyPatch(typeof(ZNet), "Awake")]
        [HarmonyPostfix]
        private static void ZNet_Awake_Postfix(ZNet __instance)
        {
            try
            {
                if (__instance == null || !__instance.IsServer()) return;
                ZRoutedRpc.instance?.Register<ZPackage>(InfoRpcName, OnServerReceiveInfo);
            }
            catch (Exception ex) { Debug.LogWarning($"[Discord] DeathInfo RPC register failed: {ex.Message}"); }
        }

        // ── Client: report cause + capture at death ──────────────────────────────────────────────────
        [HarmonyPatch(typeof(Player), "OnDeath")]
        [HarmonyPostfix]
        private static void Player_OnDeath_Postfix(Player __instance)
        {
            try
            {
                if (__instance == null || __instance != Player.m_localPlayer) return;
                if (ZNet.instance == null || ZNet.instance.IsServer()) return; // dedicated-server clients only
                if (DiscordIntegrationConfig.NotifyPlayerDeath?.Value != true) return;

                // Cause-of-death always ships (tiny), even with screenshots disabled — sent FIRST so it's
                // cached server-side before the (slower) screenshot completes and triggers the post.
                SendDeathInfo(__instance);

                if (DiscordIntegrationConfig.DeathScreenshots?.Value != true) return;
                ShotPipeline.CaptureAndSend(ShotPipeline.ShotKind.Death, string.Empty, string.Empty, 0f, CaptureDelaySeconds);
            }
            catch (Exception ex) { Debug.LogWarning($"[Discord] Death capture start failed: {ex.Message}"); }
        }

        private static readonly FieldInfo _lastHitField = AccessTools.Field(typeof(Character), "m_lastHit");

        private static void SendDeathInfo(Player player)
        {
            try
            {
                long serverId = ZRoutedRpc.instance != null ? ZRoutedRpc.instance.GetServerPeerID() : 0L;
                if (serverId == 0L) return;

                var hit = _lastHitField?.GetValue(player) as HitData;
                var hitType = hit?.m_hitType ?? HitData.HitType.Undefined;
                float damage = hit?.GetTotalDamage() ?? 0f;

                // Attacker display name resolved CLIENT-side so creature tokens localize ("$enemy_troll" -> "Troll").
                string attacker = string.Empty;
                var attackerChar = hit?.GetAttacker();
                if (attackerChar != null)
                {
                    attacker = attackerChar.GetHoverName();
                    if (!string.IsNullOrEmpty(attacker) && attacker.IndexOf('$') >= 0)
                        attacker = Localization.instance.Localize(attacker);
                }

                var pkg = new ZPackage();
                pkg.Write((int)hitType);
                pkg.Write(attacker ?? string.Empty);
                pkg.Write(damage);
                ZRoutedRpc.instance.InvokeRoutedRPC(serverId, InfoRpcName, pkg);
            }
            catch (Exception ex) { Debug.LogWarning($"[Discord] Death info send failed: {ex.Message}"); }
        }

        // ── Server: cause-report receive + screenshot arrival (routed from ShotPipeline) ─────────────
        private static void OnServerReceiveInfo(long sender, ZPackage pkg)
        {
            try
            {
                if (DiscordIntegrationConfig.NotifyPlayerDeath?.Value != true) return;

                int hitType = pkg.ReadInt();
                string attacker = pkg.ReadString();
                float damage = pkg.ReadSingle();
                if (hitType < 0 || hitType > (int)HitData.HitType.AshlandsOcean) hitType = 0;
                if (attacker != null && attacker.Length > 64) attacker = attacker.Substring(0, 64);
                if (float.IsNaN(damage) || float.IsInfinity(damage) || damage < 0f) damage = 0f;

                var peer = ZNet.instance?.GetPeer(sender);
                string platformId = peer?.m_socket?.GetHostName();
                if (string.IsNullOrEmpty(platformId)) return;

                lock (_lock)
                {
                    _deathInfo[platformId] = new DeathInfo
                    {
                        HitType = (HitData.HitType)hitType,
                        Attacker = attacker ?? string.Empty,
                        Damage = damage,
                        WhenUtc = DateTime.UtcNow
                    };
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[Discord] DeathInfo receive failed: {ex.Message}"); }
        }

        /// <summary>A completed death screenshot arrived (called by ShotPipeline on the server).</summary>
        internal static void OnShotArrived(string playerName, string platformId, byte[] jpg)
        {
            if (DiscordIntegrationConfig.DeathScreenshots?.Value != true) return;
            // An empty image means the client's capture failed — stay silent; the respawn-based
            // fallback still posts the death (with cause) so nothing is lost.
            if (jpg == null || jpg.Length == 0) return;

            MarkPosted(platformId);
            DiscordIntegrationCore.OnPlayerDeathScreenshot(playerName, platformId, jpg);
        }

        // ── Dedupe between the screenshot post and the respawn-based fallback post ──────────────────
        private static void MarkPosted(string platformId)
        {
            if (string.IsNullOrEmpty(platformId)) return;
            lock (_lock) { _recentPosts[platformId] = DateTime.UtcNow; }
        }

        /// <summary>True (and consumes the mark) when a screenshot death post already covered this player's death.</summary>
        internal static bool TryConsumeRecentPost(string platformId)
        {
            if (string.IsNullOrEmpty(platformId)) return false;
            lock (_lock)
            {
                if (!_recentPosts.TryGetValue(platformId, out var when)) return false;
                _recentPosts.Remove(platformId);
                return DateTime.UtcNow - when < DedupeTtl;
            }
        }
    }
}
