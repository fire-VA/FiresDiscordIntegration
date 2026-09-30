using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresDiscordIntegration.Discord
{
    /// <summary>
    /// ExpandWorldPrefabs integration: an EWP rule that pokes a <c>Player</c> with parameter
    /// <c>screenshot</c> (or <c>screenshot:some caption</c>) makes that player's client capture its screen
    /// and post it to the progress webhook — so operators can wire screenshots to ANY EWP trigger (boss
    /// kills, events, zone entry) from yaml alone, no extra EWP entry for Player needed.
    ///
    /// Wiring: every targeted EWP action funnels through <c>ExpandWorld.Prefab.Manager.Handle(ActionType,
    /// string[], ZDO)</c> (verified against ExpandWorldPrefabs.dll IL: the ZDOID overload and DelayedPoke
    /// both forward here), so a postfix filtered to <c>ActionType.Poke</c> (=5) observes every delivered
    /// poke. The patch is CONDITIONAL — <see cref="Prepare"/> returns false when EWP isn't installed and
    /// PatchAll skips the class silently, so EWP stays an optional dependency.
    /// </summary>
    [HarmonyPatch]
    internal static class EwpScreenshotPoke
    {
        private const string TriggerParameter = "screenshot";
        internal const string RpcName = "VAG_PokeShot";
        private const int PokeActionType = 5; // ExpandWorld.Prefab.ActionType.Poke (verified via Cecil)
        private const float PerPlayerCooldownSeconds = 5f;

        private static readonly int PlayerPrefabHash = "Player".GetStableHashCode();
        private static readonly Dictionary<long, float> _lastSent = new Dictionary<long, float>();
        private static MethodBase _target;

        private static bool Prepare()
        {
            try
            {
                var managerType = AccessTools.TypeByName("ExpandWorld.Prefab.Manager");
                if (managerType == null) return false; // EWP not installed - skip silently

                foreach (var m in AccessTools.GetDeclaredMethods(managerType))
                {
                    if (m.Name != "Handle") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 3 && ps[1].ParameterType == typeof(string[]) && ps[2].ParameterType == typeof(ZDO))
                    {
                        _target = m;
                        break;
                    }
                }
                if (_target == null)
                {
                    Debug.LogWarning("[Discord] ExpandWorldPrefabs found but Manager.Handle(ActionType, string[], ZDO) is missing (EWP layout changed?) - poke-screenshot integration disabled.");
                    return false;
                }

                Debug.Log("[Discord] ExpandWorldPrefabs detected - poke a Player with parameter 'screenshot' (or 'screenshot:caption') to trigger a Discord screenshot.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] EwpScreenshotPoke Prepare failed: {ex.Message}");
                return false;
            }
        }

        private static MethodBase TargetMethod() => _target;

        private static void Postfix(object type, string[] args, ZDO zdo)
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (Convert.ToInt32(type) != PokeActionType) return;
                if (zdo == null || args == null || args.Length == 0) return;
                if (DiscordIntegrationConfig.EwpPokeScreenshots?.Value != true) return;
                if (zdo.GetPrefab() != PlayerPrefabHash) return;

                // The caption rides the poke 'parameter' as ONE token. EWP splits parameters on spaces, so
                // multi-word captions use underscores ("screenshot:Troll_slain") - the '_' become spaces when
                // the Discord message is printed (OnPlayerTriggeredScreenshot), keeping this a single arg.
                string reason = null;
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    if (string.IsNullOrEmpty(a)) continue;
                    if (string.Equals(a, TriggerParameter, StringComparison.OrdinalIgnoreCase)) { reason = string.Empty; break; }
                    if (a.StartsWith(TriggerParameter + ":", StringComparison.OrdinalIgnoreCase))
                    {
                        reason = a.Substring(TriggerParameter.Length + 1);
                        break;
                    }
                }
                if (reason == null) return;
                if (reason.Length > 64) reason = reason.Substring(0, 64);

                // Resolve which peer owns this player ZDO.
                ZNetPeer target = null;
                foreach (var peer in ZNet.instance.GetPeers())
                    if (peer != null && peer.m_characterID == zdo.m_uid) { target = peer; break; }
                if (target == null) return;

                // EWP rules can misfire in loops - never hammer a client with capture requests.
                float now = Time.realtimeSinceStartup;
                if (_lastSent.TryGetValue(target.m_uid, out float last) && now - last < PerPlayerCooldownSeconds) return;
                _lastSent[target.m_uid] = now;

                var pkg = new ZPackage();
                pkg.Write(reason);
                ZRoutedRpc.instance.InvokeRoutedRPC(target.m_uid, RpcName, pkg);
                FiresLogger.LogVerbose($"[Discord] EWP poke-screenshot -> {target.m_playerName}" +
                    (reason.Length > 0 ? $" ({reason})" : ""));
            }
            catch (Exception ex) { Debug.LogWarning($"[Discord] EwpScreenshotPoke postfix failed: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Client receiver for the poke-screenshot request. Registered UNCONDITIONALLY (separate class so the
    /// EWP-conditional Prepare above can't skip it) - the client doesn't care what triggered the request,
    /// only that the SERVER asked for a capture.
    /// </summary>
    [HarmonyPatch]
    internal static class PokeShotClient
    {
        private const float CaptureDelaySeconds = 0.15f;

        [HarmonyPatch(typeof(ZNet), "Awake")]
        [HarmonyPostfix]
        private static void ZNet_Awake_Postfix(ZNet __instance)
        {
            try
            {
                if (__instance == null || __instance.IsServer()) return;
                ZRoutedRpc.instance?.Register<ZPackage>(EwpScreenshotPoke.RpcName, OnTakeShot);
            }
            catch (Exception ex) { Debug.LogWarning($"[Discord] PokeShot register failed: {ex.Message}"); }
        }

        private static void OnTakeShot(long sender, ZPackage pkg)
        {
            try
            {
                // Only the server may request captures - ignore spoofed client-to-client sends.
                if (ZRoutedRpc.instance == null || sender != ZRoutedRpc.instance.GetServerPeerID()) return;
                if (Player.m_localPlayer == null) return;

                string reason = pkg.ReadString();
                if (reason != null && reason.Length > 64) reason = reason.Substring(0, 64);

                ShotPipeline.CaptureAndSend(ShotPipeline.ShotKind.Triggered, reason ?? string.Empty,
                    string.Empty, 0f, CaptureDelaySeconds);
            }
            catch (Exception ex) { Debug.LogWarning($"[Discord] PokeShot capture failed: {ex.Message}"); }
        }
    }
}
