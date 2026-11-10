using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using FiresCore.ClientLogRelay;
using FiresCore.ClientLogRelay.Interactions;
using FiresCore.ClientLogRelay.Transport;
using FiresDiscordIntegration.ClientLogRelay.Consumers;
using FiresDiscordIntegration.ClientLogRelay.Interactions;
using FiresDiscordIntegration.ClientLogRelay.Webhook;
using FiresDiscordIntegration.Discord;

namespace FiresDiscordIntegration.ClientLogRelay
{
    /// <summary>
    /// Wires the drop-in <see cref="ClientLogRelay"/> module into this mod using the
    /// push-model prescribed by <c>ClientLogRelay/README.md</c>: the client sends an
    /// unsolicited snapshot of its <c>LogOutput.log</c> + plugin list to the server
    /// shortly after the peer-info handshake completes. There is no request/response
    /// dance - one RPC name, one direction.
    ///
    /// On a dedicated server:
    ///   1. Registers the Disk + Discord consumers with <see cref="ClientLogRelay"/>.
    ///   2. Registers a routed RPC "VAG_SubmitClientLog" that deserialises the payload,
    ///      builds a <see cref="ClientLogArtifacts"/>, and hands it to
    ///      <see cref="ClientLogRelay.ReportArtifacts"/>.
    ///
    /// On a client:
    ///   1. After <c>ZNet.RPC_PeerInfo</c> confirms the connection, a coroutine waits a
    ///      few seconds for BepInEx-side init to settle, then uses
    ///      <see cref="ClientLogCollector.TryCollect"/> to snapshot
    ///      <c>LogOutput.log</c> + <c>Chainloader.PluginInfos</c>, and pushes them to the
    ///      server via <see cref="ZRoutedRpc.InvokeRoutedRPC(long, string, object[])"/>
    ///      targeting <see cref="ZRoutedRpc.GetServerPeerID"/>.
    ///
    /// Webhook URLs and toggles are bound under the [Client Log Relay] / [Server Status]
    /// config sections. <see cref="PrimaryWebhookUrl"/> is the server admin's Discord;
    /// <see cref="SecondaryWebhookUrl"/> is an optional second destination (e.g. remote
    /// diagnostics) that receives a copy of every login snapshot. Both default to empty -
    /// no Discord traffic happens until an admin opts in.
    /// </summary>
    [HarmonyPatch]
    public static class ClientLogRelayIntegration
    {
        // Extra wait after the player is ready, so the push lands outside the spawn burst.
        private const float PostSpawnGraceSeconds = 2f;
        // A package this large is still sent, but logged as a warning (Steam's per-message limit is 512 KB).
        private const int LargePackageWarnBytes = 420000;
        // How often timed-out chunked transfers are dropped on the server.
        private const float TransferCleanupIntervalSeconds = 30f;
        // Lets disconnect cleanup finish before the disconnect log is posted.
        private const float DisconnectPostDelaySeconds = 1f;

        // Subfolder under {BepInEx config}/ used by ClientLogRelayPaths.
        // Logs land in {BepInEx config}/FiresDiscordIntegration/ClientLogs/.
        private const string MOD_ID = "FiresDiscordIntegration";

        // Short display label used in Discord embeds, file headers, and log breadcrumbs.
        private const string PLUGIN_BRAND = "FiresDiscordIntegration";

        // Fallback used as the webhook author name when the bot's @me identity hasn't
        // resolved yet.  Discord webhooks reject any username containing "discord"
        // (case-insensitive) with HTTP 400 - that's why PLUGIN_BRAND can't be used
        // here.  Once DiscordBotIdentity has the bot's actual @me name (e.g.
        // "Verdant's Ascent ChatBot"), ResolveWebhookName returns that and this
        // fallback is no longer hit.
        private const string FallbackWebhookDisplayName = "Server Relay";

        // Resolve the webhook author name shown in Discord.  Order:
        //   1. Admin-set WebhookDisplayName (any non-empty value other than the
        //      stock "Valheim Server" placeholder).
        //   2. The bot's own @me username from DiscordBotIdentity once cached.
        //   3. FallbackWebhookDisplayName.
        // Used for BOTH the initial login-snapshot POST (DiscordWebhookConsumer)
        // AND the reaction-triggered follow-up POSTs (ReactionPoller) so the
        // same author name appears on every message in the thread.
        private static string ResolveWebhookName()
        {
            string explicitName = DiscordIntegrationConfig.WebhookDisplayName?.Value;
            string fallback = !string.IsNullOrEmpty(explicitName) && explicitName != "Valheim Server"
                ? explicitName
                : FallbackWebhookDisplayName;
            return DiscordBotIdentity.ResolveUsername(fallback: fallback);
        }

        private static string ResolveAvatarUrl()
        {
            string explicitAvatar = DiscordIntegrationConfig.WebhookAvatarUrl?.Value;
            if (!string.IsNullOrEmpty(explicitAvatar)) return explicitAvatar;
            return DiscordBotIdentity.ResolveAvatarUrl(fallback: null);
        }

        // Login-snapshot ownership coordination used to live here: VAngarde claimed
        // priority 0, this integration claimed priority 100 so VAngarde's parallel
        // Discord consumer would self-suppress.  That coordination is no longer
        // needed - VAngarde no longer registers a Discord login consumer at all
        // (only the disk + reaction handler).  This integration owns the only
        // login Discord consumer, end of story.

        // Single routed RPC name. Kept short to save ZPackage bytes.
        private const string RPC_SUBMIT = "VAG_SubmitClientLog";
        private const string RPC_UPDATE = "VAG_UpdateClientLog";

        // How long the client waits after peer-info completion before reading LogOutput.log
        // and pushing it. Gives BepInEx / Jotunn / other mods a moment to finish their own
        // connect-time logging so the snapshot captures as much as possible.
        private const float CLIENT_PUSH_DELAY_SECONDS = 15f;

        // How often the client re-pushes its log to keep the server's disk copy current.
        // This runs silently - no Discord post, just a disk overwrite.
        private const float CLIENT_REPUSH_INTERVAL_SECONDS = 15f * 60f; // 15 minutes

        // === Anti-spam debounce (server side) ===
        private const float GLOBAL_MIN_POST_INTERVAL_SECONDS = 10f;

        // Log bytes per routed RPC, sent one per frame. Steam allows 512 KB per message, but the server copies each
        // payload several times on the main thread (socket, routed RPC, this handler), so a 400 KB chunk cost the
        // server over 100 ms per call under memory pressure; small chunks spread that over frames.
        private const int CHUNK_BYTES = 64 * 1024;

        // ====================== CONFIG SHIM ======================
        // All configs live in DiscordIntegrationConfig (bound centrally by
        // ConfigManager.Initialize).  This block is only typed accessors so the
        // rest of this file (ported verbatim from Ghetto Networking - which was
        // working with multi-MB logs) doesn't need to know where the values come
        // from.  Removing ClientLogRelay from Ghetto was a Thunderstore-scope
        // requirement, NOT a code problem - we want the same behaviour here.

        private static bool MasterEnabled
            => DiscordIntegrationConfig.NotifyClientLoginArtifacts?.Value ?? true;

        private static string PrimaryWebhookUrl
            => DiscordIntegrationConfig.ResolveWebhookUrl(WebhookCategory.ClientLog) ?? string.Empty;

        private static string SecondaryWebhookUrl
            => string.Empty; // not exposed by RPG maker config currently

        private static bool AutoSendFullLog
            => DiscordIntegrationConfig.AttachFullClientLogOnLogin?.Value ?? false;

        private static bool AttachModListOnLogin
            => DiscordIntegrationConfig.AttachModListOnLogin?.Value ?? true;

        private static bool AttachErrorsWarningsOnLogin
            => DiscordIntegrationConfig.AttachErrorsWarningsOnLogin?.Value ?? true;

        private static bool OnlyNotifyLoginIfErrorsOrWarnings
            => DiscordIntegrationConfig.OnlyNotifyLoginIfErrorsOrWarnings?.Value ?? false;

        private static bool LogRequestReactionEnabled
            => DiscordIntegrationConfig.EnableLogRequestReaction?.Value ?? true;

        private static string BotTokenValue => FiresDiscordIntegration.Utilities.BotTokenFile.Token;

        private static string ServerManagerWebhookUrl
            => DiscordIntegrationConfig.ServerManagerWebhookUrl?.Value ?? string.Empty;

        private static string ServerManagerChannelIdValue
        {
            get
            {
                var dedicated = DiscordIntegrationConfig.ServerManagerChannelId?.Value;
                if (!string.IsNullOrEmpty(dedicated)) return dedicated;
                return DiscordIntegrationConfig.StatusChannelId?.Value ?? string.Empty;
            }
        }

        private static readonly HashSet<long> _postedPeers = new HashSet<long>();
        private static readonly object _debounceLock = new object();
        private static DateTime _lastPostUtc = DateTime.MinValue;

        // Client-side: set once per ZNet session so we only push one snapshot per connect.
        // Reset whenever a fresh ZNet is awakened (i.e. on reconnect / menu round-trip).
        private static bool _clientPushedThisSession;

        private static bool _initialized;
        private static bool _isServerSide;

        // The server-side consumers and pollers start once per process, even when the
        // role flips between client and server sessions.
        private static bool _serverStarted;

        internal static bool RelayActive => _initialized && (!_isServerSide || MasterEnabled);

        public static void InitServer()
        {
            if (_initialized && _isServerSide) return;
            _initialized = true;
            _isServerSide = true;
            if (_serverStarted) return;
            _serverStarted = true;
            ClientLogRelayWorker.Prepare();

            if (!MasterEnabled)
            {
                FiresLogger.LogVerbose("[ClientLogRelay] Disabled by config (Discord.ClientLogs.NotifyClientLoginArtifacts=false) - skipping initialisation.");
                return;
            }

            // Register each consumer independently so a failure in one cannot take the
            // other down. Server-only deployments simply end up with an empty ClientLogs
            // folder if no clients ever push, which is harmless.
            TryRun("RegisterDiskConsumer",    RegisterDiskConsumer);
            TryRun("RegisterDiscordConsumer", RegisterDiscordConsumer);
            TryRun("StartReactionPoller",     StartReactionPoller);
            TryRun("StartServerStatusPoster", StartServerStatusPoster);
            FiresLogger.LogVerbose("[ClientLogRelay] Server-side integration initialised.");
        }

        public static void InitClient()
        {
            if (_initialized && !_isServerSide) return;
            _initialized = true;
            _isServerSide = false;
            FiresLogger.LogVerbose("[ClientLogRelay] Client-side integration initialised (will push log on connect).");
        }

        /// <summary>
        /// Safe wrapper around a named init step. Any exception is caught and logged so
        /// that one failed consumer/registration never aborts the rest of the module or
        /// the plugin's <c>Awake</c>.
        /// </summary>
        private static void TryRun(string label, Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ClientLogRelay] {label} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Root directory for persisted client-log artifacts:
        /// <c>{BepInEx config}/FiresDiscordIntegration/ClientLogs/</c>. The path helper creates
        /// it on first access, so we don't need to pre-create it manually.
        /// </summary>
        public static string ClientLogsRoot
            => ClientLogRelayPaths.GetDefaultClientLogsDir(MOD_ID);

        private static void RegisterDiskConsumer()
        {
            var disk = new DiskConsumer(
                consumerId:       "FiresDiscordIntegration.ClientLogRelay.Disk",
                rootDirResolver:  () => ClientLogRelayPaths.GetDefaultClientLogsDir(MOD_ID),
                enabledGate:      () => true);

            FiresCore.ClientLogRelay.ClientLogRelay.RegisterConsumer(disk);
            FiresLogger.LogVerbose($"[ClientLogRelay] Disk consumer will persist artifacts under: {ClientLogsRoot}");
        }

        private static void RegisterDiscordConsumer()
        {
            // Primary: server admin's own Discord. Carries the interactive features
            // (reaction-based on-demand full-log requests, bot pre-react).
            var primary = new DiscordWebhookConsumer("FiresDiscordIntegration.ClientLogRelay.Discord.Primary")
            {
                EnabledGate              = () => MasterEnabled
                                                 && !string.IsNullOrEmpty(PrimaryWebhookUrl),
                WebhookUrl               = () => PrimaryWebhookUrl,
                WebhookName              = ResolveWebhookName,
                AvatarUrl                = ResolveAvatarUrl,
                BrandLabel               = () => PLUGIN_BRAND,
                AttachFullLog            = () => AutoSendFullLog,
                AttachModList            = () => AttachModListOnLogin,
                AttachErrorsWarnings     = () => AttachErrorsWarningsOnLogin,
                OnlyIfErrorsOrWarnings   = () => OnlyNotifyLoginIfErrorsOrWarnings,
                EnableLogRequestReaction = () => LogRequestReactionEnabled,
                BotToken                 = () => BotTokenValue,
                ServerName               = () => ZNet.instance != null ? ZNet.instance.GetWorldName() : "Unknown",
            };
            FiresCore.ClientLogRelay.ClientLogRelay.RegisterConsumer(primary);

            // Secondary: optional remote-diagnostics destination. Receives the same
            // login snapshot fire-and-forget - no reaction wiring (the poller can only
            // watch one channel) and no bot token (bot is configured for the primary's
            // guild). Silently no-ops while the secondary URL is empty.
            var secondary = new DiscordWebhookConsumer("FiresDiscordIntegration.ClientLogRelay.Discord.Secondary")
            {
                EnabledGate              = () => MasterEnabled
                                                 && !string.IsNullOrEmpty(SecondaryWebhookUrl),
                WebhookUrl               = () => SecondaryWebhookUrl,
                WebhookName              = ResolveWebhookName,
                AvatarUrl                = ResolveAvatarUrl,
                BrandLabel               = () => PLUGIN_BRAND,
                AttachFullLog            = () => AutoSendFullLog,
                AttachModList            = () => AttachModListOnLogin,
                AttachErrorsWarnings     = () => AttachErrorsWarningsOnLogin,
                OnlyIfErrorsOrWarnings   = () => OnlyNotifyLoginIfErrorsOrWarnings,
                EnableLogRequestReaction = () => false,
                BotToken                 = () => null,
                ServerName               = () => ZNet.instance != null ? ZNet.instance.GetWorldName() : "Unknown",
            };
            FiresCore.ClientLogRelay.ClientLogRelay.RegisterConsumer(secondary);
        }

        private static void StartReactionPoller()
        {
            string token = BotTokenValue;
            if (string.IsNullOrEmpty(token))
            {
                FiresLogger.LogVerbose("[ClientLogRelay] No bot token configured \u2014 reaction poller disabled.");
                return;
            }

            if (string.IsNullOrEmpty(PrimaryWebhookUrl))
            {
                FiresLogger.LogVerbose("[ClientLogRelay] No primary webhook URL configured \u2014 reaction poller disabled.");
                return;
            }

            ReactionPoller.Create(
                botTokenResolver:       () => BotTokenValue,
                webhookUrlResolver:     () => PrimaryWebhookUrl,
                clientLogsRootResolver: () => ClientLogsRoot,
                webhookNameResolver:    ResolveWebhookName);

            FiresLogger.LogVerbose("[ClientLogRelay] Reaction poller started \u2014 polling every 60s for log requests (90s warmup per fresh snapshot to dodge Discord's reactions rate-limit bucket).");
        }

        private static void StartServerStatusPoster()
        {
            string token = BotTokenValue;
            string channelId = ServerManagerChannelIdValue;

            if (string.IsNullOrEmpty(token))
            {
                FiresLogger.LogVerbose("[ClientLogRelay] No bot token configured (Discord.BotListener.BotToken) - Server Manager panel disabled.");
                return;
            }

            if (string.IsNullOrEmpty(channelId))
            {
                FiresLogger.LogVerbose("[ClientLogRelay] No channel ID configured (Discord.ServerManager.ServerManagerChannelId or Discord.BotListener.StatusChannelId) - Server Manager panel disabled.");
                return;
            }

            // Don't call ServerHeartbeat.OnServerStart here - wait for ZNet to be ready.
            // Webhook URL is optional - empty = post via bot API for proper bot avatar.
            FiresLogger.LogVerbose("[ClientLogRelay] Server Manager panel configured - will start when ZNet is ready.");
        }

        // ====================================================================
        // Harmony entry points - register server RPC once ZNet is up; on the client,
        // reset the per-session push-dedupe flag so a reconnect can push again.
        // Both postfixes are guarded so any failure is swallowed locally and cannot
        // propagate into Valheim's ZNet code.
        // ====================================================================
        [HarmonyPatch(typeof(ZNet), "Awake")]
        [HarmonyPostfix]
        public static void ZNet_Awake_Postfix(ZNet __instance)
        {
            try
            {
                // Init from the ZNet role on every Awake: the same process can host a
                // world after playing as a client, so the role is re-read each session.
                // InitServer/InitClient are no-ops when the role is unchanged; the
                // per-session RPCs below are re-registered either way.
                if (__instance != null && __instance.IsServer())
                    InitServer();
                else
                    InitClient();

                if (ZRoutedRpc.instance == null)
                {
                    Debug.LogWarning("[ClientLogRelay] ZNet.Awake postfix fired but ZRoutedRpc.instance is null - RPC registration skipped.");
                    return;
                }

                if (_isServerSide)
                {
                    ZRoutedRpc.instance.Register<ZPackage>(RPC_SUBMIT, OnServerReceiveLog);
                    ZRoutedRpc.instance.Register<ZPackage>(RPC_UPDATE, OnServerReceiveLogUpdate);
                    FiresLogger.LogVerbose($"[ClientLogRelay] Registered server-side handlers for '{RPC_SUBMIT}' + '{RPC_UPDATE}'.");

                    // Start periodic cleanup of timed-out chunked transfers
                    ZNet.instance.StartCoroutine(PeriodicTransferCleanup());

                    // Start ServerHeartbeat now that ZNet is ready.  Webhook URL is
                    // OPTIONAL - when empty, ServerHeartbeat falls back to the bot API
                    // path (POST /channels/{id}/messages) which posts AS the bot, so
                    // the message inherits the bot's avatar + name.  We only need the
                    // bot token + a channel ID.
                    string token = BotTokenValue;
                    string statusUrl = ServerManagerWebhookUrl;       // may be empty
                    string channelId = ServerManagerChannelIdValue;
                    if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(channelId))
                    {
                        ServerHeartbeat.OnServerStart(
                            botToken:          token,
                            statusWebhookUrl:  statusUrl,
                            channelIdOverride: channelId,
                            webhookName:       $"{PLUGIN_BRAND} Relay",
                            brandLabel:        PLUGIN_BRAND);
                        FiresLogger.LogVerbose("[ClientLogRelay] Server heartbeat + control panel started.");
                    }
                }
                else
                {
                    // Fresh ZNet session on the client - allow a new push.
                    _clientPushedThisSession = false;
                    FiresLogger.LogVerbose("[ClientLogRelay] Client-side ZNet awake \u2014 push-on-connect armed.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ClientLogRelay] ZNet.Awake postfix threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Client-side only: ZNet.RPC_PeerInfo fires here when the server has sent us its
        // peer info, i.e. the handshake completed successfully. Schedule the push. The
        // server's own RPC_PeerInfo invocation also runs this postfix, but _isServerSide
        // gates it off so server-only deployments simply do nothing here.
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        [HarmonyPostfix]
        public static void ZNet_RPC_PeerInfo_Postfix(ZNet __instance, ZRpc rpc)
        {
            try
            {
                if (_isServerSide || __instance == null || rpc == null) return;
                if (_clientPushedThisSession)
                {
                    FiresLogger.LogVerbose("[ClientLogRelay] PeerInfo postfix fired but client already pushed this session \u2014 skipping.");
                    return;
                }

                _clientPushedThisSession = true;
                FiresLogger.LogVerbose($"[ClientLogRelay] PeerInfo handshake complete \u2014 scheduling log push in {CLIENT_PUSH_DELAY_SECONDS:F0}s.");
                __instance.StartCoroutine(DelayedClientPush());
                __instance.StartCoroutine(PeriodicClientRepush());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ClientLogRelay] ZNet.RPC_PeerInfo postfix threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Client side: the server keeps a pushed log only while its relay is on. NotifyClientLoginArtifacts is a synced
        // entry, so this reads the server's value (the config sync arrives at connect, long before a push). With it off
        // the server dropped every push for lack of consumers, so the local read and the upload are skipped.
        private static bool ServerKeepsClientLogs()
        {
            if (MasterEnabled) return true;
            FiresLogger.LogVerbose("[ClientLogRelay] The server's client-log relay is off (Discord.ClientLogs.NotifyClientLoginArtifacts); not sending this client's log.");
            return false;
        }

        private static IEnumerator DelayedClientPush()
        {
            // Wait for the player to be fully spawned + a small additional grace
            // window before pushing. The flat 15-second delay was firing during
            // the worst possible frames - dungeon spawn, mod-config sync, initial
            // territory load, vanilla prefab instantiation - all of which already
            // saturate the main thread. Layering a multi-MB synchronous file read
            // + a tight 13-chunk RPC dispatch on top tipped the keep-alive past
            // its 2-minute ZRpc timeout and the server killed the connection.
            //
            // Waiting for PlayerSpawnGate ensures we push only when the engine
            // has finished its post-spawn finalisation - no more competition with
            // dungeon load, no more "frozen at Spawning dungeon" symptom.
            // The push reads the local LogOutput.log (potentially multi-MB)
            // shortly after spawn; these verbose milestones show when that
            // work overlaps other login activity.
            FiresLogger.LogVerbose("[ClientLogRelay][DIAG] DelayedClientPush ENTER (waiting for PlayerSpawnGate to open)");

            // Wait for the spawn gate, retrying in 30-second chunks instead of
            // giving up after one window.  Heavy-modded worlds can take minutes
            // to reach the "I have arrived" point \u2014 the local player isn't
            // safely writable to until then, but cancelling outright meant a
            // slow-loading client never delivered its log at all.  The retry
            // keeps polling until the player is genuinely in-world; a hard cap
            // at PushReadyMaxAttempts * PushReadyAttemptSeconds (10 minutes
            // total) protects against a never-spawning edge case (e.g. player
            // disconnects mid-load) so we don't leak a coroutine forever.
            const float PushReadyAttemptSeconds = 30f;
            const float PushReadyAttemptDeadlineMarginSeconds = 5f;
            const int   PushReadyMaxAttempts                  = 20;

            bool playerReady = false;
            int  attempt     = 0;
            while (!playerReady && attempt < PushReadyMaxAttempts)
            {
                attempt++;
                bool thisAttemptReady = false;
                FiresCore.Lifecycle.PlayerSpawnGate.RunWhenLocalReady(
                    PushReadyAttemptSeconds, _ => { thisAttemptReady = true; });

                float attemptDeadlineAt = Time.realtimeSinceStartup
                                        + PushReadyAttemptSeconds
                                        + PushReadyAttemptDeadlineMarginSeconds;
                while (!thisAttemptReady && Time.realtimeSinceStartup < attemptDeadlineAt)
                    yield return null;

                if (thisAttemptReady)
                {
                    playerReady = true;
                    FiresLogger.LogVerbose($"[ClientLogRelay] Player ready on attempt {attempt}; proceeding with log push.");
                    break;
                }

                FiresLogger.LogVerbose($"[ClientLogRelay] Player still not ready after {PushReadyAttemptSeconds:F0}s " +
                          $"(attempt {attempt}/{PushReadyMaxAttempts}) \u2014 retrying.");
            }

            if (!playerReady)
            {
                Debug.LogWarning(
                    $"[ClientLogRelay] Player never became ready after {PushReadyMaxAttempts} attempts " +
                    $"({(PushReadyMaxAttempts * PushReadyAttemptSeconds) / 60f:F0} min total) \u2014 " +
                    $"giving up on log push for this session.");
                yield break;
            }

            FiresLogger.LogVerbose("[ClientLogRelay][DIAG] DelayedClientPush playerReady=true, entering 2s post-ready grace");

            // Small additional grace so we land outside the spawn-burst window.
            yield return new WaitForSeconds(PostSpawnGraceSeconds);

            FiresLogger.LogVerbose("[ClientLogRelay][DIAG] DelayedClientPush 2s grace elapsed, about to call PushLogToServerCoroutine");

            if (ZNet.instance == null)
            {
                FiresLogger.LogVerbose("[ClientLogRelay] ZNet.instance gone before push \u2014 cancelling.");
                yield break;
            }

            if (ZRoutedRpc.instance == null)
            {
                Debug.LogWarning("[ClientLogRelay] ZRoutedRpc.instance null at push time \u2014 cancelling.");
                yield break;
            }

            long serverId = ZRoutedRpc.instance.GetServerPeerID();
            if (serverId == 0L)
            {
                Debug.LogWarning("[ClientLogRelay] ServerPeerID is 0 at push time \u2014 connection no longer valid; cancelling.");
                yield break;
            }

            // Drive the chunk send via a coroutine so we can yield between
            // chunks instead of hammering Steam in one frame.
            yield return ZNet.instance.StartCoroutine(PushLogToServerCoroutine(serverId));
        }

        /// <summary>
        /// Coroutine that yields between every chunk send so we don't blast 5+MB at
        /// Steam's send queue in a single frame.  Same pattern UILayoutSyncRPC.DrainSendQueue
        /// uses for the same reason: hammering ZRoutedRpc.InvokeRoutedRPC in a tight
        /// loop trips the per-frame Steam buffer cap and stalls the main thread long
        /// enough that ZRpc keep-alive misses its window and the server kills the
        /// connection.
        /// </summary>
        private static IEnumerator PushLogToServerCoroutine(long serverId)
        {
            if (!ServerKeepsClientLogs())
                yield break;

            FiresLogger.LogVerbose($"[ClientLogRelay] PushLogToServer entered (serverId={serverId}) - reading local LogOutput.log...");

            byte[] logBytes = null;
            Dictionary<string, string> modList = null;
            try
            {
                ClientLogCollector.TryCollect(out logBytes, out modList);
                if (modList == null) modList = new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);
                FiresLogger.LogVerbose($"[ClientLogRelay] PushLogToServer read complete - logBytes={logBytes?.Length ?? 0}B, mods={modList.Count}.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ClientLogRelay] TryCollect failed: {ex.Message}");
                yield break;
            }

            // Yield once after the (potentially large) sync file read so the engine
            // can get a frame in before we start dispatching network packets.
            yield return null;

            // Steamworks.NET version can differ between clients; isolate so a bad
            // assembly bind there cannot abort the whole push.
            string steamId = string.Empty;
            try { steamId = GetOwnSteamId() ?? string.Empty; }
            catch (Exception sidEx)
            {
                FiresLogger.LogVerbose($"[ClientLogRelay] SteamID unavailable ({sidEx.GetType().Name}: {sidEx.Message}); pushing without it.");
            }

            logBytes = logBytes ?? new byte[0];
            int logSize = logBytes.Length;

            // CHUNKING: metadata first, then N pure data chunks of CHUNK_BYTES.
            const int CHUNK_SIZE = CHUNK_BYTES;
            int totalChunks = logSize == 0 ? 1 : (logSize + CHUNK_SIZE - 1) / CHUNK_SIZE;

            FiresLogger.LogVerbose($"[ClientLogRelay] Pushing log to server: {logSize}B in {totalChunks} chunk(s) + metadata, mods={modList.Count}");

            // STEP 1: metadata (mod list + steam id)
            {
                ZPackage metaPayload = new ZPackage();
                metaPayload.Write(-1); // marker: metadata, not a chunk
                metaPayload.Write(totalChunks);
                metaPayload.Write(modList.Count);
                foreach (var kv in modList)
                {
                    metaPayload.Write(kv.Key ?? string.Empty);
                    metaPayload.Write(kv.Value ?? string.Empty);
                }
                metaPayload.Write(steamId ?? string.Empty);

                byte[] metaData = metaPayload.GetArray();
                if (metaData != null && metaData.Length > LargePackageWarnBytes)
                    Debug.LogWarning($"[ClientLogRelay] Metadata ZPackage is {metaData.Length}B - very large mod list!");

                if (ZRoutedRpc.instance == null)
                {
                    Debug.LogWarning("[ClientLogRelay] ZRoutedRpc gone before metadata send - aborting push.");
                    yield break;
                }

                ZRoutedRpc.instance.InvokeRoutedRPC(serverId, RPC_SUBMIT, metaPayload);
                FiresLogger.LogVerbose($"[ClientLogRelay]  -> Sent metadata ({metaData?.Length ?? 0}B total, {modList.Count} mods)");
            }

            // Yield after metadata so Steam's send buffer drains a frame before
            // we start firing data chunks.
            yield return null;

            // STEP 2: data chunks - ONE PER FRAME so we never queue more than a
            // single ~400KB packet onto Steam's outbound buffer per Update tick.
            for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
            {
                if (ZRoutedRpc.instance == null)
                {
                    Debug.LogWarning($"[ClientLogRelay] ZRoutedRpc gone at chunk {chunkIndex + 1}/{totalChunks} - aborting push.");
                    yield break;
                }

                ZPackage payload = new ZPackage();
                payload.Write(chunkIndex);
                payload.Write(totalChunks);

                int offset = chunkIndex * CHUNK_SIZE;
                int size = Math.Min(CHUNK_SIZE, logSize - offset);
                byte[] chunk = new byte[size];
                if (size > 0)
                    Array.Copy(logBytes, offset, chunk, 0, size);
                payload.Write(chunk);

                byte[] packageData = payload.GetArray();
                if (packageData != null && packageData.Length > LargePackageWarnBytes)
                    Debug.LogWarning($"[ClientLogRelay] CRITICAL: Chunk {chunkIndex + 1} ZPackage is {packageData.Length}B!");

                ZRoutedRpc.instance.InvokeRoutedRPC(serverId, RPC_SUBMIT, payload);

                if (totalChunks > 1)
                    FiresLogger.LogVerbose($"[ClientLogRelay]  -> Sent chunk {chunkIndex + 1}/{totalChunks} ({size}B data, {packageData?.Length ?? 0}B total)");

                yield return null; // <-- the critical bit: one chunk per frame
            }

            Debug.Log($"[ClientLogRelay] Push complete: {logSize}B in {totalChunks} chunk(s)");
        }

        /// <summary>
        /// Coroutine that re-pushes the client log every 15 minutes via <see cref="RPC_UPDATE"/>
        /// so the server's cached disk copy stays current throughout the session.
        /// </summary>
        private static IEnumerator PeriodicClientRepush()
        {
            while (true)
            {
                yield return new WaitForSeconds(CLIENT_REPUSH_INTERVAL_SECONDS);

                // Skip this iteration if not connected, but keep trying
                if (ZNet.instance == null || ZRoutedRpc.instance == null)
                {
                    FiresLogger.LogVerbose("[ClientLogRelay] Repush skipped: ZNet or ZRoutedRpc is null (will retry next interval)");
                    continue;  // Skip this iteration, keep looping
                }

                if (!ServerKeepsClientLogs())
                    continue;

                long serverId = ZRoutedRpc.instance.GetServerPeerID();
                if (serverId == 0L)
                {
                    FiresLogger.LogVerbose("[ClientLogRelay] Repush skipped: not connected to server (will retry next interval)");
                    continue;  // Skip this iteration, keep looping
                }

                yield return ZNet.instance.StartCoroutine(RepushLogToServerCoroutine(serverId));
            }
        }

        /// <summary>
        /// Server-side coroutine that periodically cleans up timed-out chunked transfers.
        /// </summary>
        private static IEnumerator PeriodicTransferCleanup()
        {
            while (true)
            {
                yield return new WaitForSeconds(TransferCleanupIntervalSeconds);
                ClientLogChunkedTransfer.CleanupTimedOutTransfers();
            }
        }

        /// <summary>
        /// Yielding-coroutine variant of RepushLogToServer.  Same one-chunk-per-frame
        /// pacing as PushLogToServerCoroutine - 15-minute repush of a now-multi-MB log
        /// is exactly the kind of thing that, blasted in a single frame, makes Steam's
        /// outbound buffer hiccup mid-session.
        /// </summary>
        private static IEnumerator RepushLogToServerCoroutine(long serverId)
        {
            byte[] logBytes = null;
            try
            {
                Dictionary<string, string> modList;
                ClientLogCollector.TryCollect(out logBytes, out modList);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ClientLogRelay] Repush TryCollect failed: {ex.Message}");
                yield break;
            }

            // Yield once after the sync read so the engine can ship a frame before
            // network packets queue up.
            yield return null;

            string steamId = string.Empty;
            try { steamId = GetOwnSteamId() ?? string.Empty; }
            catch { /* Steamworks not available */ }

            logBytes = logBytes ?? new byte[0];
            int logSize = logBytes.Length;

            // Small logs go in a single message (legacy single-packet wire format).
            if (logSize <= CHUNK_BYTES)
            {
                if (ZRoutedRpc.instance == null) yield break;
                ZPackage payload = new ZPackage();
                payload.Write(logBytes);
                payload.Write(steamId);
                ZRoutedRpc.instance.InvokeRoutedRPC(serverId, RPC_UPDATE, payload);
                FiresLogger.LogVerbose($"[ClientLogRelay] Re-pushed '{RPC_UPDATE}' to server: {logSize}B (single message)");
                yield break;
            }

            // Large log - chunked, ONE PER FRAME.
            const int CHUNK_SIZE = CHUNK_BYTES;
            int totalChunks = (logSize + CHUNK_SIZE - 1) / CHUNK_SIZE;

            FiresLogger.LogVerbose($"[ClientLogRelay] Re-pushing large log to server: {logSize}B in {totalChunks} chunk(s)");

            for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
            {
                if (ZRoutedRpc.instance == null) yield break;

                ZPackage payload = new ZPackage();
                payload.Write(chunkIndex);
                payload.Write(totalChunks);

                int offset = chunkIndex * CHUNK_SIZE;
                int size = Math.Min(CHUNK_SIZE, logSize - offset);
                byte[] chunk = new byte[size];
                if (size > 0) Array.Copy(logBytes, offset, chunk, 0, size);
                payload.Write(chunk);
                payload.Write(steamId);

                ZRoutedRpc.instance.InvokeRoutedRPC(serverId, RPC_UPDATE, payload);

                if (totalChunks > 1)
                    FiresLogger.LogVerbose($"[ClientLogRelay]  -> Re-pushed chunk {chunkIndex + 1}/{totalChunks} ({size}B)");

                yield return null; // <-- yield between chunks
            }

            FiresLogger.LogVerbose($"[ClientLogRelay] Re-push complete: {logSize}B in {totalChunks} chunk(s)");
        }

        /// <summary>
        /// Clears all server-side debounce state so every currently connected peer is
        /// eligible for a fresh post. Intended for manual dev-console invocation.
        /// </summary>
        public static void ResetDebounce()
        {
            lock (_debounceLock)
            {
                int p = _postedPeers.Count;
                _postedPeers.Clear();
                _lastPostUtc = DateTime.MinValue;
                Debug.Log($"[ClientLogRelay] Debounce reset (cleared {p} posted peers).");
            }
        }

        // ====================================================================
        // Server side: receive log from a client and hand off to the relay.
        // ====================================================================
        private static void OnServerReceiveLog(long sender, ZPackage pkg)
        {
            try
            {
                // No consumer is registered while the relay is off; a client that pushes anyway gets nothing kept.
                if (!MasterEnabled) return;

                if (pkg == null)
                {
                    Debug.LogWarning($"[ClientLogRelay] Submit from peer {sender} had a null payload.");
                    return;
                }

                // Read chunk header
                int chunkIndex = pkg.ReadInt();
                int totalChunks = pkg.ReadInt();

                // NEW PROTOCOL: chunkIndex = -1 means this is metadata
                if (chunkIndex == -1)
                {
                    // This is the metadata message (mod list + steam ID)
                    int modCount = pkg.ReadInt();
                    if (modCount < 0) modCount = 0;
                    if (modCount > MaxMetadataMods)
                    {
                        FiresLogger.LogWarning($"[ClientLogRelay] Dropped metadata from peer {sender}: {modCount} mods (cap {MaxMetadataMods}).");
                        return;
                    }
                    Dictionary<string, string> metaModList = new Dictionary<string, string>(modCount, StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < modCount; i++)
                    {
                        string guid = pkg.ReadString();
                        string ver = pkg.ReadString();
                        if (!string.IsNullOrEmpty(guid)) metaModList[guid] = ver ?? string.Empty;
                    }
                    string metaSteamId = null;
                    try { metaSteamId = pkg.ReadString(); } catch { }

                    // Store metadata for this peer (will be used when chunks arrive)
                    ClientLogChunkedTransfer.StoreMetadata(sender, totalChunks, metaModList, metaSteamId);

                    FiresLogger.LogVerbose($"[ClientLogRelay] \u2190 Received metadata from peer {sender}: {metaModList.Count} mods, expecting {totalChunks} chunk(s)");
                    return;
                }

                // This is a regular data chunk
                byte[] chunkData = pkg.ReadByteArray();

                FiresLogger.LogVerbose($"[ClientLogRelay] \u2190 Received chunk {chunkIndex + 1}/{totalChunks} from peer {sender} ({chunkData.Length}B)");

                // Process chunk through chunked transfer system (no metadata in chunks anymore)
                var result = ClientLogChunkedTransfer.ReceiveChunk(
                    sender, chunkIndex, totalChunks, chunkData, null, null);

                // If not complete yet, return and wait for more chunks
                if (result == null)
                {
                    return;
                }

                // Transfer complete - extract data from result
                byte[] fullLog = result.LogBytes;
                Dictionary<string, string> modList = result.ModList;
                string reportedSteamId = result.SteamId;
                if (modList == null) modList = new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);

                FiresLogger.LogVerbose($"[ClientLogRelay] \u2190 Complete log received from peer {sender}: {fullLog.Length}B, {modList.Count} mods");

                string playerName = GetPeerPlayerName(sender);
                string platformId = PeerPlatformId(sender, reportedSteamId);

                // Debounce layer 1: per-peer once per session.
                lock (_debounceLock)
                {
                    if (!_postedPeers.Add(sender))
                    {
                        FiresLogger.LogVerbose($"[ClientLogRelay] Dropping duplicate submission from '{playerName}' ({platformId}) \u2014 already posted this session.");
                        return;
                    }

                    // Debounce layer 2: global minimum interval between any two posts.
                    TimeSpan sinceLast = DateTime.UtcNow - _lastPostUtc;
                    if (sinceLast.TotalSeconds < GLOBAL_MIN_POST_INTERVAL_SECONDS)
                    {
                        // Re-allow this peer so a later, non-throttled submission isn't swallowed.
                        _postedPeers.Remove(sender);
                        FiresLogger.LogVerbose($"[ClientLogRelay] Rate-limiting post from '{playerName}' ({platformId}); {sinceLast.TotalSeconds:F1}s since last post (floor {GLOBAL_MIN_POST_INTERVAL_SECONDS:F0}s).");
                        return;
                    }

                    _lastPostUtc = DateTime.UtcNow;
                }

                // Snapshot the server's own plugin list so the consumer can produce a
                // client-vs-server diff alongside the per-player mod list. Safe on the
                // dedicated server - ClientLogCollector just walks Chainloader.PluginInfos.
                Dictionary<string, string> serverMods = null;
                try { serverMods = ClientLogCollector.BuildLocalModList(); }
                catch (Exception ex)
                {
                    FiresLogger.LogVerbose($"[ClientLogRelay] Could not enumerate server-side mod list: {ex.GetType().Name}: {ex.Message}");
                }

                var artifacts = new ClientLogArtifacts(
                    platformId, playerName, fullLog, modList,
                    serverMods, PLUGIN_BRAND);

                // Track peer->identity for disconnect hook.
                lock (_debounceLock)
                {
                    _peerToPlatformId[sender] = platformId;
                    _peerToPlayerName[sender] = playerName;
                }

                // Parsing the log, diffing the mod lists and writing the folder can stall a server frame for
                // hundreds of ms, so the worker runs the fan-out; the Discord consumers post back on the main thread.
                int clientModCount = modList.Count;
                int serverModCount = serverMods != null ? serverMods.Count : 0;
                ClientLogRelayWorker.Enqueue(() =>
                {
                    FiresCore.ClientLogRelay.ClientLogRelay.ReportArtifacts(artifacts);
                    FiresLogger.LogInfo($"[ClientLogRelay] Processed log from '{playerName}' ({platformId}): {fullLog.Length} bytes, {clientModCount} client mods, {serverModCount} server mods.");
                });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ClientLogRelay] Failed to process submitted log: {ex.Message}");
            }
        }

        // ====================================================================
        // Server side: silent log update (periodic re-push from client).
        // Overwrites ALL disk artifacts (log, errors/warnings, etc.) so that
        // on-demand reactions and disconnect capture always read the latest data.
        // No Discord post, no debounce check.
        // NEW: Now supports chunked updates for large logs!
        // ====================================================================
        private static void OnServerReceiveLogUpdate(long sender, ZPackage pkg)
        {
            try
            {
                // The cached copies feed the relay's Discord reactions and disconnect posts, which are off with it.
                if (pkg == null || !MasterEnabled) return;

                // Check if this is a chunked update (new protocol) or single message (legacy)
                // We peek at the first int to see if it's a chunk index
                pkg.SetPos(0); // Reset to start
                int firstInt = pkg.ReadInt();
                pkg.SetPos(0); // Reset again for actual reading

                byte[] log;
                string reportedSteamId = null;

                // If firstInt is 0-999, it's probably a chunk index (chunked protocol)
                // If it's > 100000, it's probably the byte array length (legacy protocol)
                bool isChunked = firstInt >= 0 && firstInt < 1000;

                if (isChunked)
                {
                    // NEW CHUNKED PROTOCOL
                    int chunkIndex = pkg.ReadInt();
                    int totalChunks = pkg.ReadInt();
                    byte[] chunkData = pkg.ReadByteArray();
                    try { reportedSteamId = pkg.ReadString(); } catch { }

                    FiresLogger.LogVerbose($"[ClientLogRelay] \u2190 Received update chunk {chunkIndex + 1}/{totalChunks} from peer {sender} ({chunkData.Length}B)");

                    // Use chunked transfer system for updates too
                    var result = ClientLogChunkedTransfer.ReceiveUpdateChunk(
                        sender, chunkIndex, totalChunks, chunkData, reportedSteamId);

                    // If not complete yet, return and wait for more chunks
                    if (result == null)
                    {
                        return;
                    }

                    log = result.LogBytes;
                    reportedSteamId = result.SteamId;
                    FiresLogger.LogVerbose($"[ClientLogRelay] \u2190 Complete update received from peer {sender}: {log.Length}B");
                }
                else
                {
                    // LEGACY SINGLE-MESSAGE PROTOCOL (backward compat)
                    // Security: cap the size before reading, and allow one legacy update per peer per minute.
                    if (firstInt < 0 || firstInt > LegacyUpdateMaxBytes || firstInt > pkg.Size() - 4)
                    {
                        FiresLogger.LogWarning($"[ClientLogRelay] Dropped legacy log update from peer {sender}: {firstInt} bytes (cap {LegacyUpdateMaxBytes}).");
                        return;
                    }
                    lock (_debounceLock)
                    {
                        if (_lastLegacyUpdateUtc.Count > 512) _lastLegacyUpdateUtc.Clear();
                        if (_lastLegacyUpdateUtc.TryGetValue(sender, out DateTime lastUtc)
                            && (DateTime.UtcNow - lastUtc).TotalSeconds < LegacyUpdateMinIntervalSeconds)
                        {
                            FiresLogger.LogWarning($"[ClientLogRelay] Dropped legacy log update from peer {sender}: rate limit ({LegacyUpdateMinIntervalSeconds:F0}s).");
                            return;
                        }
                        _lastLegacyUpdateUtc[sender] = DateTime.UtcNow;
                    }
                    log = pkg.ReadByteArray();
                    try { reportedSteamId = pkg.ReadString(); } catch { }
                }

                string playerName = GetPeerPlayerName(sender);
                string platformId = PeerPlatformId(sender, reportedSteamId);

                if (log == null || log.Length == 0)
                {
                    FiresLogger.LogVerbose($"[ClientLogRelay] Update from '{playerName}' ({platformId}) had empty log \u2014 skipping.");
                    return;
                }

                string logsRoot = ClientLogsRoot;
                if (!string.IsNullOrEmpty(logsRoot))
                {
                    Dictionary<string, string> serverMods = null;
                    try { serverMods = ClientLogCollector.BuildLocalModList(); }
                    catch { /* not critical for an update */ }

                    // Same cost as the login snapshot, so the worker does it, queued behind this
                    // player's earlier jobs so the folder is rewritten in order.
                    ClientLogRelayWorker.Enqueue(() => RewriteCachedArtifacts(logsRoot, platformId, playerName, log, serverMods));
                }

                // Track the mapping from peer UID -> platformId for disconnect lookup.
                lock (_debounceLock)
                {
                    _peerToPlatformId[sender] = platformId;
                    _peerToPlayerName[sender] = playerName;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ClientLogRelay] Failed to process log update: {ex.Message}");
            }
        }

        // Worker thread: rebuilds and rewrites one player's cached artifacts from a silent re-push.
        private static void RewriteCachedArtifacts(string logsRoot, string platformId, string playerName,
            byte[] log, Dictionary<string, string> serverMods)
        {
            // Re-read the existing mod list from the previously saved modlist.txt
            // so we can rebuild the full artifact set without the client re-sending it.
            var modList = new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);
            string safeName = playerName ?? "unknown";
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            safeName = string.Concat(safeName.Split(invalid));
            string safePid = (platformId ?? "unknown").Replace(":", "_").Replace("/", "_").Replace("\\", "_");
            string existingModListPath = System.IO.Path.Combine(logsRoot, $"{safeName}_{safePid}", "modlist.txt");
            if (System.IO.File.Exists(existingModListPath))
            {
                try
                {
                    foreach (var line in System.IO.File.ReadAllLines(existingModListPath))
                    {
                        if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                        int eq = line.IndexOf('=');
                        if (eq > 0)
                            modList[line.Substring(0, eq)] = line.Substring(eq + 1);
                    }
                }
                catch { /* best-effort */ }
            }

            var artifacts = new ClientLogArtifacts(
                platformId, playerName, log, modList,
                serverMods, PLUGIN_BRAND);

            FiresCore.ClientLogRelay.ClientLogRelay.AnalyzeLog(artifacts);
            if (modList.Count > 0)
                FiresCore.ClientLogRelay.ClientLogRelay.ComputeModDiff(artifacts);

            // Write all artifact files (log, modlist, errors_warnings, mod_diff).
            ClientLogArtifactWriter.Write(logsRoot, artifacts);

            FiresLogger.LogVerbose($"[ClientLogRelay] \u2190 Updated all cached artifacts for '{playerName}' ({platformId}): {log.Length} bytes.");
        }

        // Peer UID -> platformId mapping so the disconnect hook knows who just left.
        // Populated by both OnServerReceiveLog and OnServerReceiveLogUpdate.
        private const int LegacyUpdateMaxBytes = 8 * 1024 * 1024;
        private const double LegacyUpdateMinIntervalSeconds = 60.0;
        private const int MaxMetadataMods = 2048;
        private static readonly Dictionary<long, DateTime> _lastLegacyUpdateUtc = new Dictionary<long, DateTime>();

        private static readonly Dictionary<long, string> _peerToPlatformId
            = new Dictionary<long, string>();
        private static readonly Dictionary<long, string> _peerToPlayerName
            = new Dictionary<long, string>();

        // ====================================================================
        // Server side: detect peer disconnect and post log if registered.
        // ====================================================================
        [HarmonyPatch(typeof(ZNet), "Disconnect")]
        [HarmonyPrefix]
        public static void ZNet_Disconnect_Prefix(ZNetPeer peer)
        {
            try
            {
                if (!_isServerSide || peer == null) return;

                // Cancel any pending chunked transfer from this peer
                ClientLogChunkedTransfer.CancelTransfer(peer.m_uid);

                string platformId;
                string playerName;
                lock (_debounceLock)
                {
                    _peerToPlatformId.TryGetValue(peer.m_uid, out platformId);
                    _peerToPlayerName.TryGetValue(peer.m_uid, out playerName);
                    _peerToPlatformId.Remove(peer.m_uid);
                    _peerToPlayerName.Remove(peer.m_uid);
                    _postedPeers.Remove(peer.m_uid); // allow re-post on next connect
                }

                if (string.IsNullOrEmpty(platformId))
                    platformId = peer.m_uid.ToString();
                if (string.IsNullOrEmpty(playerName))
                    playerName = string.IsNullOrEmpty(peer.m_playerName) ? "unknown" : peer.m_playerName;

                var entry = DisconnectLogRegistry.TakeIfRegistered(platformId);
                if (entry == null) return;

                Debug.Log($"[ClientLogRelay] Player '{playerName}' ({platformId}) disconnected \u2014 posting session log (requested by {entry.RequestedByName}).");

                // CRITICAL: Don't block the disconnect process with file I/O and HTTP requests!
                // Schedule the log posting as a coroutine to run after disconnect completes.
                if (ZNet.instance != null)
                {
                    ZNet.instance.StartCoroutine(PostDisconnectLogAsync(platformId, playerName, entry.RequestedByName));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ClientLogRelay] Disconnect hook threw: {ex.Message}");
            }
        }

        private static IEnumerator PostDisconnectLogAsync(string platformId, string playerName, string requestedByName)
        {
            // Small delay to ensure disconnect cleanup is complete
            yield return new WaitForSeconds(DisconnectPostDelaySeconds);

            // The worker reads the cached log after any rewrite still queued for this player, then
            // hands the bytes back to the main thread, where the webhook post starts its coroutine.
            ClientLogRelayWorker.Enqueue(() =>
            {
                byte[] logBytes = ReadCachedSessionLog(platformId, playerName);
                if (logBytes == null) return;
                FiresCore.Async.MainThreadDispatcher.Enqueue(() => PostDisconnectLog(platformId, playerName, requestedByName, logBytes));
            });
        }

        // Worker thread: the player's cached LogOutput.log, or null when there is none. Worker lines go through
        // FiresLogger: BepInEx only hears Unity's untagged Debug.Log from the main thread.
        private static byte[] ReadCachedSessionLog(string platformId, string playerName)
        {
            try
            {
                string logsRoot = ClientLogsRoot;
                if (string.IsNullOrEmpty(logsRoot)) return null;

                string safeName = playerName ?? "unknown";
                var invalid = System.IO.Path.GetInvalidFileNameChars();
                safeName = string.Concat(safeName.Split(invalid));
                string safePid = (platformId ?? "unknown").Replace(":", "_").Replace("/", "_").Replace("\\", "_");
                string logPath = System.IO.Path.Combine(logsRoot, $"{safeName}_{safePid}", "LogOutput.log");

                if (!System.IO.File.Exists(logPath))
                {
                    FiresLogger.LogWarning($"[ClientLogRelay] No cached log at '{logPath}' for disconnect post.");
                    return null;
                }

                byte[] logBytes = System.IO.File.ReadAllBytes(logPath);
                return logBytes.Length == 0 ? null : logBytes;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[ClientLogRelay] Failed to read the cached log for the disconnect post: {ex.Message}");
                return null;
            }
        }

        private static void PostDisconnectLog(string platformId, string playerName, string requestedByName, byte[] logBytes)
        {
            try
            {
                string safePid = (platformId ?? "unknown").Replace(":", "_").Replace("/", "_").Replace("\\", "_");

                var targetUrls = new System.Collections.Generic.List<string>(2);
                foreach (var candidate in new[] { PrimaryWebhookUrl, SecondaryWebhookUrl })
                {
                    if (!string.IsNullOrEmpty(candidate)
                        && MinimalWebhookPoster.IsValidWebhookUrl(candidate))
                    {
                        targetUrls.Add(candidate);
                    }
                }
                if (targetUrls.Count == 0) return;

                string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

                var embed = new MinimalWebhookPoster.Embed()
                    .SetTitle("\u267B\uFE0F Session Log \u2014 Player Disconnected")
                    .SetColor(3066993) // green
                    .AddField("\uD83D\uDC64 Player",   playerName,        true)
                    .AddField("\uD83D\uDD94 Steam ID", platformId ?? "?", true)
                    .AddField("\uD83D\uDCC4 Log Size", FormatBytes(logBytes.Length), true)
                    .SetFooter($"Requested by {requestedByName}");

                // Chunk if large (same 8 MB limit as the reaction poller)
                const int MAX_BYTES = 8 * 1024 * 1024 - 64 * 1024;
                if (logBytes.Length <= MAX_BYTES)
                {
                    var files = new System.Collections.Generic.List<MinimalWebhookPoster.Attachment>
                    {
                        new MinimalWebhookPoster.Attachment($"session_log_{safePid}_{stamp}.log", logBytes, "text/plain")
                    };
                    foreach (var url in targetUrls)
                        MinimalWebhookPoster.Post(url, embed, files, ResolveWebhookName(), null);
                }
                else
                {
                    int partNum = 0;
                    int offset = 0;
                    while (offset < logBytes.Length)
                    {
                        partNum++;
                        int len = Math.Min(MAX_BYTES, logBytes.Length - offset);
                        var chunk = new byte[len];
                        Buffer.BlockCopy(logBytes, offset, chunk, 0, len);
                        offset += len;

                        int totalParts = (logBytes.Length + MAX_BYTES - 1) / MAX_BYTES;
                        var partEmbed = new MinimalWebhookPoster.Embed()
                            .SetTitle($"\u267B\uFE0F Session Log \u2014 Part {partNum}/{totalParts}")
                            .SetColor(3066993)
                            .AddField("\uD83D\uDC64 Player", playerName, true)
                            .AddField("\uD83D\uDCC4 Size", FormatBytes(chunk.Length), true)
                            .SetFooter($"Requested by {requestedByName}");

                        var files = new System.Collections.Generic.List<MinimalWebhookPoster.Attachment>
                        {
                            new MinimalWebhookPoster.Attachment($"session_log_{safePid}_{stamp}_part{partNum}.log", chunk, "text/plain")
                        };
                        foreach (var url in targetUrls)
                            MinimalWebhookPoster.Post(url, partEmbed, files, ResolveWebhookName(), null);
                    }
                }

                Debug.Log($"[ClientLogRelay] Posted disconnect session log for '{playerName}' ({platformId}), {logBytes.Length} bytes.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ClientLogRelay] Failed to post disconnect log: {ex.Message}");
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return $"{v:0.##} {units[u]}";
        }

        // ====================================================================
        // Helpers
        // ====================================================================
        // Security: the platform id that keys a player's log folder and disconnect log comes from the sender's own
        // connection (as DeathScreenshots does), never from the id the client reports; it is reduced to a safe file name.
        private const int MaxPlatformIdLength = 64;
        private static string PeerPlatformId(long sender, string reported)
        {
            var peer = ZNet.instance?.GetPeer(sender);
            string id = peer?.m_socket?.GetHostName();
            if (string.IsNullOrEmpty(id)) id = sender.ToString();
            if (!string.IsNullOrEmpty(reported) && reported != id)
                FiresLogger.LogDebug($"[ClientLogRelay] peer {sender} reported platform id '{SafeFileToken(reported)}'; using its connection id.");
            return SafeFileToken(id);
        }

        private static string SafeFileToken(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            var sb = new System.Text.StringBuilder(Math.Min(s.Length, MaxPlatformIdLength));
            for (int i = 0; i < s.Length && sb.Length < MaxPlatformIdLength; i++)
            {
                char c = s[i];
                sb.Append((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' ? c : '_');
            }
            return sb.Length == 0 ? "unknown" : sb.ToString();
        }

        private static string GetPeerPlayerName(long uid)
        {
            if (ZNet.instance == null) return "unknown";
            foreach (ZNetPeer p in ZNet.instance.GetPeers())
            {
                if (p != null && p.m_uid == uid)
                    return string.IsNullOrEmpty(p.m_playerName) ? "unknown" : p.m_playerName;
            }
            return "unknown";
        }

        /// <summary>
        /// Returns the local user's SteamID64 as a string, or empty if Steamworks isn't
        /// available (e.g. Crossplay/Xbox, non-Steam launches, server-role calls, or a
        /// Steamworks.NET version mismatch between profiles). Uses reflection so this
        /// method can be JIT-compiled even when Steamworks.NET fails to bind.
        /// </summary>
        private static string GetOwnSteamId()
        {
            try
            {
                Type steamApi = Type.GetType("Steamworks.SteamAPI, Steamworks.NET")
                                ?? AccessTools.TypeByName("Steamworks.SteamAPI");
                Type steamUser = Type.GetType("Steamworks.SteamUser, Steamworks.NET")
                                 ?? AccessTools.TypeByName("Steamworks.SteamUser");
                if (steamApi == null || steamUser == null) return string.Empty;

                var isRunning = steamApi.GetMethod("IsSteamRunning",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (isRunning == null) return string.Empty;
                object running = isRunning.Invoke(null, null);
                if (!(running is bool b) || !b) return string.Empty;

                var getId = steamUser.GetMethod("GetSteamID",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (getId == null) return string.Empty;
                object idObj = getId.Invoke(null, null);
                if (idObj == null) return string.Empty;

                // CSteamID is a struct; prefer the m_SteamID field, fall back to ToString().
                var field = idObj.GetType().GetField("m_SteamID",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (field != null)
                {
                    object val = field.GetValue(idObj);
                    if (val != null) return val.ToString();
                }
                return idObj.ToString();
            }
            catch { /* Steamworks not available / version mismatch */ }
            return string.Empty;
        }
    }
}
