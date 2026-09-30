using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresDiscordIntegration.Discord
{
    /// <summary>
    /// Harmony patches for Discord webhook integration.
    ///
    /// Hooks into the same game events as DiscordControl:
    ///  - ZNet.LoadWorld       → ServerStart + tracker init
    ///  - ZNet.SaveWorld       → ServerSave
    ///  - Game.OnApplicationQuit → ServerStop
    ///  - ZNet.RPC_CharacterID → PlayerJoin / PlayerDeath
    ///  - ZNet.RPC_Disconnect  → PlayerLeave
    ///  - Chat.OnNewChatMessage → PlayerShout
    ///  - EnvMan.UpdateTriggers → NewDay
    ///  - ZNet.Update          → Hourly summary tick
    ///  - RandEventSystem.SetActiveEvent → Random event start
    ///
    /// All patches check IsServer() before sending webhooks.
    /// All patches are wrapped in try/catch to prevent Harmony failures from
    /// cascading and blocking game startup.
    /// </summary>
    [HarmonyPatch]
    public static class DiscordIntegrationPatches
    {
        // Track which players have joined (by Steam ID) to distinguish
        // first RPC_CharacterID (join) from subsequent ones (death/respawn).
        private static readonly HashSet<string> _joinedPlayers = new HashSet<string>();

        // Cached reflection for EnvMan.GetCurrentDay() which is private
        private static MethodInfo _envManGetCurrentDay;

        // ────────────────────────────────────────────────────────────
        // Server Start
        // ────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(ZNet), "LoadWorld")]
        [HarmonyPostfix]
        public static void ZNet_LoadWorld_Postfix()
        {
            try
            {
                DiscordStatusTracker.Initialize();
                // DiscordCrashDetector.OnServerStart() removed — it was the
                // legacy crash detector superseded by ServerHeartbeat
                // (Modules/ClientLogRelay/Interactions/ServerHeartbeat.cs).
                // Both wrote to the SAME sentinel file
                // (BepInEx\config\FiresRPGmaker\server_heartbeat.json) but
                // DiscordCrashDetector's JSON format omitted the "messageId"
                // field that the server-wrapper bat reads to find the live
                // Discord status message for restart-reaction polling. With
                // both writers active, whichever wrote LAST on the path won
                // — and OnApplicationQuit fired DiscordCrashDetector's
                // WriteSentinel("stopped") AFTER ServerHeartbeat's, blowing
                // away messageId and breaking the bat's "click restart in
                // Discord" loop ("ERROR: No message ID in sentinel").
                // ServerHeartbeat handles both sentinel writes AND its own
                // PowerShell crash notifier, so DiscordCrashDetector is
                // entirely redundant. Removing the call here disables the
                // duplicate writer; the class itself stays in the tree
                // pending the warnings-cleanup pass that deletes it.
                DiscordIntegrationCore.OnServerStart();
                DiscordBotListener.Start();
                DiscordBridgeProvider.Start();   // Core's DiscordBridge: private tools post / watch channels as the bot
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] LoadWorld webhook error: {ex.Message}");
            }
        }

        // ────────────────────────────────────────────────────────────
        // Server Save
        // ────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(ZNet), "SaveWorld")]
        [HarmonyPostfix]
        public static void ZNet_SaveWorld_Postfix()
        {
            try
            {
                DiscordStatusTracker.RecordWorldSave();
                DiscordIntegrationCore.OnServerSave();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] SaveWorld webhook error: {ex.Message}");
            }
        }

        // ────────────────────────────────────────────────────────────
        // Server Stop
        // ────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(Game), "OnApplicationQuit")]
        [HarmonyPostfix]
        public static void Game_OnApplicationQuit_Postfix()
        {
            try
            {
                // ServerHeartbeat.OnServerStop replaces the orphaned
                // DiscordCrashDetector.OnServerStop. The previous wiring
                // never invoked ServerHeartbeat.OnServerStop, so on a
                // clean shutdown the live Discord status message was
                // never edited to "Server Offline" by the heartbeat path
                // (only DiscordIntegrationCore's one-off webhook posted
                // a fresh embed). It also meant the heartbeat's last
                // sentinel write — which had the correct messageId for
                // the bat's restart-reaction polling — never happened
                // on shutdown, leaving stale sentinel data.
                FiresDiscordIntegration.ClientLogRelay.Interactions.ServerHeartbeat.OnServerStop();
                DiscordBotListener.Stop();
                DiscordBridgeProvider.Stop();
                DiscordIntegrationCore.OnServerStop();
                DiscordStatusTracker.Shutdown();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] OnApplicationQuit webhook error: {ex.Message}");
            }
        }

        // ────────────────────────────────────────────────────────────
        // Player Join / Death
        // ────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(ZNet), "RPC_CharacterID")]
        [HarmonyPostfix]
        public static void ZNet_RPC_CharacterID_Postfix(ZNet __instance, ZRpc rpc, ZDOID characterID)
        {
            try
            {
                if (!__instance.IsServer()) return;

                ZNetPeer peer = __instance.GetPeer(rpc);
                if (peer == null) return;

                string hostName = peer.m_socket.GetHostName();

                if (_joinedPlayers.Add(hostName))
                {
                    DiscordStatusTracker.RecordJoin(peer.m_playerName, hostName);
                    DiscordIntegrationCore.OnPlayerJoin(peer.m_playerName, hostName);
                }
                else if (peer.m_characterID.ID != 0)
                {
                    // A repeat CharacterID is only a death if the dying client actually reported one
                    // (VAG_DeathInfo fires from Player.OnDeath on every death; every client runs this
                    // mod via the VAngarde enforced list). Without a report this is a re-login whose
                    // disconnect we never observed (crash/timeout) - not a death.
                    if (DeathScreenshots.TryGetDeathInfo(hostName) != null)
                    {
                        DiscordStatusTracker.RecordDeath();
                        DiscordIntegrationCore.OnPlayerDeath(peer.m_playerName, hostName);
                        DeathScreenshots.ConsumeDeathInfo(hostName);
                    }
                    else
                    {
                        FiresLogger.LogVerbose($"[Discord] CharacterID repeat for '{peer.m_playerName}' without a client death report - treating as re-login, no death post.");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] RPC_CharacterID webhook error: {ex.Message}");
            }
        }

        // ────────────────────────────────────────────────────────────
        // Player Leave
        // ────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(ZNet), "RPC_Disconnect")]
        [HarmonyPrefix]
        public static void ZNet_RPC_Disconnect_Prefix(ZNet __instance, ZRpc rpc)
        {
            try
            {
                if (!__instance.IsServer()) return;

                ZNetPeer peer = __instance.GetPeer(rpc);
                if (peer == null || peer.m_uid == 0L) return;

                string hostName = peer.m_socket.GetHostName();
                if (_joinedPlayers.Remove(hostName))
                {
                    DiscordStatusTracker.RecordLeave(peer.m_playerName, hostName);
                    DiscordIntegrationCore.OnPlayerLeave(peer.m_playerName, hostName);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] RPC_Disconnect webhook error: {ex.Message}");
            }
        }

        // Every disconnect path funnels through ZNet.Disconnect - the graceful RPC_Disconnect (which the
        // prefix above already handled, making this Remove a no-op), but ALSO socket closes, timeouts and
        // kicks that never send the farewell RPC. Without this, a crashed/quit player stayed in
        // _joinedPlayers and their NEXT login's CharacterID was misread as a death. Prefix: the peer's
        // socket must still be alive to read its host name (Disconnect disposes it).
        [HarmonyPatch(typeof(ZNet), "Disconnect")]
        [HarmonyPrefix]
        public static void ZNet_Disconnect_Prefix(ZNet __instance, ZNetPeer peer)
        {
            try
            {
                if (__instance == null || !__instance.IsServer() || peer == null) return;

                string hostName = peer.m_socket?.GetHostName();
                if (string.IsNullOrEmpty(hostName)) return;

                if (_joinedPlayers.Remove(hostName))
                {
                    DiscordStatusTracker.RecordLeave(peer.m_playerName, hostName);
                    DiscordIntegrationCore.OnPlayerLeave(peer.m_playerName, hostName);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] Disconnect webhook error: {ex.Message}");
            }
        }

        // ────────────────────────────────────────────────────────────
        // Chat (Shout)
        // ────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(Chat), "OnNewChatMessage")]
        [HarmonyPrefix]
        public static void Chat_OnNewChatMessage_Prefix(
            GameObject go, long senderID, Vector3 pos,
            Talker.Type type, UserInfo sender, string text)
        {
            try
            {
                // Print one line per incoming chat event before any filtering, so a
                // missing relay can be diagnosed from the BepInEx log with General.
                // VerboseLogging enabled.  This is the entry point to verify whether
                // the patch even fires for /say (Talker.Type.Normal) vs /s (Shout)
                // vs /w (Whisper).
                FiresLogger.LogVerbose($"[Discord] Chat patch saw type={type} sender='{sender?.Name ?? "<null>"}' text='{text ?? "<null>"}'");

                if (ZNet.instance == null || !ZNet.instance.IsServer())
                {
                    FiresLogger.LogVerbose("[Discord] Chat patch skipped: not on server");
                    return;
                }
                if (string.IsNullOrEmpty(sender?.Name))
                {
                    FiresLogger.LogVerbose("[Discord] Chat patch skipped: empty sender name");
                    return;
                }
                if (string.IsNullOrEmpty(text))
                {
                    FiresLogger.LogVerbose("[Discord] Chat patch skipped: empty text");
                    return;
                }

                // Don't echo Discord bot relay messages back to Discord — those are
                // shouts the bot listener already sent into the game; reflecting them
                // would loop the message between Discord and the chat channel.  The
                // sender Name is the encoded "[Discord]" / "[Discord]|#RRGGBB" form
                // RelayToGameChat writes; matching the prefix covers both spellings.
                if (sender.Name.StartsWith("[Discord]", StringComparison.OrdinalIgnoreCase))
                {
                    FiresLogger.LogVerbose("[Discord] Chat patch skipped: echo from Discord relay");
                    return;
                }

                if (type == Talker.Type.Shout)
                {
                    DiscordStatusTracker.RecordShout();
                    DiscordIntegrationCore.OnPlayerChat(sender.Name, text, DiscordIntegrationCore.ChatLineType.Shout);
                }
                else if (type == Talker.Type.Normal)
                {
                    DiscordIntegrationCore.OnPlayerChat(sender.Name, text, DiscordIntegrationCore.ChatLineType.Say);
                }
                else if (type == Talker.Type.Whisper)
                {
                    DiscordIntegrationCore.OnPlayerChat(sender.Name, text, DiscordIntegrationCore.ChatLineType.Whisper);
                }
                else
                {
                    Debug.Log($"[Discord] Chat patch skipped: type {type} is not a relayable chat type (Ping ignored on purpose)");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] OnNewChatMessage webhook error: {ex.Message}");
            }
        }

        // ────────────────────────────────────────────────────────────
        // New Day
        // ────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(EnvMan), "UpdateTriggers")]
        [HarmonyPostfix]
        public static void EnvMan_UpdateTriggers_Postfix(float oldDayFraction, float newDayFraction)
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (EnvMan.instance == null) return;

                // Detect dawn crossing (0.2→0.25→0.3 range like DiscordControl)
                if (oldDayFraction > 0.2f && oldDayFraction < 0.25f &&
                    newDayFraction > 0.25f && newDayFraction < 0.3f)
                {
                    int day = GetCurrentDay();
                    if (day >= 0)
                        DiscordIntegrationCore.OnNewDay(day);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] UpdateTriggers webhook error: {ex.Message}");
            }
        }

        /// <summary>
        /// Get current day from EnvMan via reflection (GetCurrentDay is private).
        /// Falls back to public GetDay() if available.
        /// </summary>
        private static int GetCurrentDay()
        {
            try
            {
                // Try public GetDay() first (available in some Valheim versions)
                return EnvMan.instance.GetDay(ZNet.instance.GetTimeSeconds());
            }
            catch
            {
                try
                {
                    // Fall back to private GetCurrentDay() via reflection
                    if (_envManGetCurrentDay == null)
                    {
                        _envManGetCurrentDay = typeof(EnvMan).GetMethod("GetCurrentDay",
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    }
                    if (_envManGetCurrentDay != null)
                        return (int)_envManGetCurrentDay.Invoke(EnvMan.instance, null);
                }
                catch { }
                return -1;
            }
        }

        // ────────────────────────────────────────────────────────────
        // Random Events (track for hourly summary)
        // ────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(RandEventSystem), "SetActiveEvent")]
        [HarmonyPostfix]
        public static void RandEventSystem_SetActiveEvent_Postfix(RandomEvent ___m_activeEvent)
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (___m_activeEvent != null)
                    DiscordStatusTracker.RecordEventStarted();
            }
            catch { }
        }

        // ────────────────────────────────────────────────────────────
        // Hourly Summary Tick (piggyback on ZNet.Update)
        // ────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(ZNet), "Update")]
        [HarmonyPostfix]
        public static void ZNet_Update_Postfix(ZNet __instance)
        {
            try
            {
                if (!__instance.IsServer()) return;
                DiscordStatusTracker.Tick();
            }
            catch { }
        }

        // ────────────────────────────────────────────────────────────
        // Cleanup on server shutdown (clear join tracking)
        // ────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        [HarmonyPostfix]
        public static void ZNet_OnDestroy_Postfix()
        {
            _joinedPlayers.Clear();
            DiscordBotListener.Stop();
            DiscordStatusTracker.Shutdown();
        }
    }
}
