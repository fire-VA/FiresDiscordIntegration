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
    ///  - RandEventSystem.SetRandomEvent → Random event start / stop
    ///
    /// All patches check IsServer() before sending webhooks.
    /// All patches are wrapped in try/catch to prevent Harmony failures from
    /// cascading and blocking game startup.
    /// </summary>
    [HarmonyPatch]
    public static class DiscordIntegrationPatches
    {
        // The shout de-dup table is cleared once it holds more entries than this.
        private const int MaxRecentShouts = 64;

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
                // DiscordCrashDetector is deliberately not started: ServerHeartbeat
                // owns the sentinel file and the crash notifier. A second writer
                // would drop the "messageId" field the server-wrapper bat reads to
                // find the live Discord status message for restart reactions.
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

                if (type == Talker.Type.Shout)
                {
                    RelayShout(sender?.Name, senderID, text, "Chat");
                    return;
                }

                // A sender with no name in its UserInfo still has one on its peer.
                // Security: the name comes from the sender's connection (the packet's UserInfo can claim anyone).
                string name = FiresDiscordIntegration.ChatRelay.ChatSecurity.SenderName(senderID, sender?.Name);
                if (!RelayableChat(name, text, senderID)) return;
                if (!FiresDiscordIntegration.ChatRelay.ChatSecurity.Allow(senderID))
                {
                    FiresLogger.LogWarning($"[Discord] chat rate limit hit for peer {senderID}; line not relayed.");
                    return;
                }
                if (type == Talker.Type.Normal)
                {
                    DiscordIntegrationCore.OnPlayerChat(name, text, DiscordIntegrationCore.ChatLineType.Say);
                }
                else if (type == Talker.Type.Whisper)
                {
                    DiscordIntegrationCore.OnPlayerChat(name, text, DiscordIntegrationCore.ChatLineType.Whisper);
                }
                else
                {
                    FiresLogger.LogDebug($"[Discord] Chat patch skipped: type {type} is not a relayable chat type (Ping ignored on purpose)");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] OnNewChatMessage webhook error: {ex.Message}");
            }
        }

        // Shouts need their own hooks. Valheim sends a shout ONCE PER RECIPIENT: Chat.SendText ->
        // CheckPermissionsAndSendChatMessageRPCsAsync -> InvokeRoutedRPC(recipient, "ChatMessage") for each permitted player. The
        // shouter's own copy never leaves its machine, and the server only routes the others on (RouteRPC), so the server's Chat
        // never handles a shout. Shouts are relayed three ways, de-duplicated by RelayShout:
        // - the shouter's client sends it to the server (ChatRelayRpc, as Say/Whisper always were; needs FDI on that client);
        // - a copy the server routes on to another player (RouteRPC below; works for a client without FDI while anyone else is on);
        // - a copy the server itself handles (HandleRoutedRPC; a listen host's own shout).
        private static readonly int ChatMessageHash = "ChatMessage".GetStableHashCode();
        private static bool _loggedRpcShoutPath;

        [HarmonyPatch(typeof(ZRoutedRpc), "HandleRoutedRPC")]
        [HarmonyPrefix]
        public static void ZRoutedRpc_HandleRoutedRPC_Prefix(ZRoutedRpc.RoutedRPCData data) => ShoutFromRoutedRpc(data, "handled ChatMessage");

        [HarmonyPatch(typeof(ZRoutedRpc), "RouteRPC")]
        [HarmonyPrefix]
        public static void ZRoutedRpc_RouteRPC_Prefix(ZRoutedRpc.RoutedRPCData rpcData) => ShoutFromRoutedRpc(rpcData, "routed ChatMessage");

        private static void ShoutFromRoutedRpc(ZRoutedRpc.RoutedRPCData data, string via)
        {
            if (data == null || data.m_methodHash != ChatMessageHash) return;
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                // Register<Vector3, int, UserInfo, string>: the order ZRpc serialised them in.
                var pkg = new ZPackage(data.m_parameters.GetArray());
                pkg.ReadVector3();
                var type = (Talker.Type)pkg.ReadInt();
                var user = new UserInfo();
                user.Deserialize(ref pkg);
                string text = pkg.ReadString();
                if (type != Talker.Type.Shout) return;
                if (!_loggedRpcShoutPath)
                {
                    _loggedRpcShoutPath = true;
                    Debug.Log($"[Discord] shout relay: first ChatMessage shout seen on the server ({via}).");
                }
                RelayShout(user.Name, data.m_senderPeerID, text, via);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] ChatMessage shout relay error ({via}): {ex.Message}");
            }
        }

        // One shout reaches the server several ways (the client relay, one routed copy per other player, a listen host's own copy):
        // one post per sender and text inside this window.
        private const float ShoutDedupSeconds = 5f;
        private static readonly Dictionary<string, float> _recentShouts = new Dictionary<string, float>();

        internal static void RelayShout(string name, long senderPeer, string text, string via)
        {
            // Security: the name comes from the sender's connection, never from the packet.
            name = FiresDiscordIntegration.ChatRelay.ChatSecurity.SenderName(senderPeer, name);
            if (!RelayableChat(name, text, senderPeer)) return;
            float now = Time.realtimeSinceStartup;
            string key = name + "\n" + text;
            if (_recentShouts.TryGetValue(key, out float seen) && now - seen < ShoutDedupSeconds)
            {
                // Logged too, so the debug log shows every path that saw the shout, not only the one that posted it.
                FiresLogger.LogDebug($"[Discord] shout from '{name}' (peer {senderPeer}) also arrived via {via}: already relayed, not posted again");
                return;
            }
            if (_recentShouts.Count > MaxRecentShouts) _recentShouts.Clear();
            _recentShouts[key] = now;
            if (!FiresDiscordIntegration.ChatRelay.ChatSecurity.Allow(senderPeer))
            {
                FiresLogger.LogWarning($"[Discord] shout rate limit hit for peer {senderPeer}; not relayed.");
                return;
            }

            FiresLogger.LogDebug($"[Discord] shout from '{name}' (peer {senderPeer}) via {via}");
            DiscordStatusTracker.RecordShout();
            bool relays = DiscordIntegrationCore.WillRelay(DiscordIntegrationCore.ChatLineType.Shout, text);
            DiscordIntegrationCore.OnPlayerChat(name, text, DiscordIntegrationCore.ChatLineType.Shout);
            if (relays) FiresDiscordIntegration.ChatRelay.ChatRelayRpc.TellRelayed(senderPeer, text);
        }

        // Skips are said once each at the normal log level: a server whose chat never reaches Discord can be read from a plain log.
        private static readonly HashSet<string> _loggedSkips = new HashSet<string>();

        private static bool RelayableChat(string name, string text, long senderPeer)
        {
            if (string.IsNullOrEmpty(name))
            {
                SkipOnce("empty sender name", $"a chat line from peer {senderPeer} has no sender name, in its UserInfo or on its peer");
                return false;
            }
            if (string.IsNullOrEmpty(text)) return false;
            // Don't echo Discord bot relay messages back to Discord: those are lines the bot listener already sent into the game,
            // and reflecting them would loop between Discord and the chat channel. RelayToGameChat writes the sender as
            // "[Discord]" or "[Discord]|#RRGGBB"; the prefix covers both.
            if (name.StartsWith("[Discord]", StringComparison.OrdinalIgnoreCase))
            {
                FiresLogger.LogVerbose("[Discord] Chat patch skipped: echo from Discord relay");
                return false;
            }
            return true;
        }

        private static void SkipOnce(string key, string why)
        {
            if (!_loggedSkips.Add(key)) return;
            Debug.Log($"[Discord] chat relay skipped: {why} (said once per session)");
        }

        private static string PeerName(long peerId) => ZNet.instance?.GetPeer(peerId)?.m_playerName;

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
        // Random Events (post start/stop + count for the hourly summary)
        // ────────────────────────────────────────────────────────────

        // SetRandomEvent is where vanilla starts and ends every random event ("Random event set:<name>" in the log); the
        // server runs it whatever its clients have installed. SetActiveEvent is not enough: it only gets an event where a
        // local player stands in the area, which never happens on a dedicated server.
        [HarmonyPatch(typeof(RandEventSystem), "SetRandomEvent")]
        [HarmonyPrefix]
        public static void RandEventSystem_SetRandomEvent_Prefix(RandomEvent ___m_randomEvent, out RandomEvent __state)
        {
            __state = ___m_randomEvent;
        }

        [HarmonyPatch(typeof(RandEventSystem), "SetRandomEvent")]
        [HarmonyPostfix]
        public static void RandEventSystem_SetRandomEvent_Postfix(RandomEvent ___m_randomEvent, RandomEvent __state)
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                RandomEvent before = __state, now = ___m_randomEvent;
                // Re-setting the running event (same name) is neither an end nor a start.
                bool same = before != null && now != null && before.m_name == now.m_name;
                if (before != null && !same)
                    DiscordIntegrationCore.OnRandomEventStop(before.m_name, before.m_endMessage);
                if (now != null && !same)
                {
                    DiscordStatusTracker.RecordEventStarted();
                    DiscordIntegrationCore.OnRandomEventStart(now.m_name, now.m_startMessage, now.m_pos);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] random event hook threw {ex.GetType().Name}: {ex.Message}");
            }
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
