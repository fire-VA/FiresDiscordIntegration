using System;
using System.Collections;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using FiresCore.Logging;
using FiresCore.Sync;
using FiresDiscordIntegration.ClientLogRelay;
using FiresDiscordIntegration.ClientLogRelay.Interactions;
using FiresDiscordIntegration.Discord;
using FiresDiscordIntegration.Utilities;

namespace FiresDiscordIntegration
{
    /// <summary>
    /// FiresDiscordIntegration â€” standalone Discord â†” Valheim integration mod.
    ///
    /// Hosts:
    ///   â€¢ DiscordIntegrationCore â€” webhook posting, bot listener, status tracker, mention
    ///     resolver, emoji converter, crash detector. Server-side only.
    ///   â€¢ ClientLogRelay â€” chunked clientâ†’server log transport, Discord webhook consumer,
    ///     disk consumer, server-driven log-request reactions, server heartbeat sentinel
    ///     for the auto-restart wrapper bat.
    ///   â€¢ Server wrapper bat (deployed alongside the DLL) â€” polls Discord for restart/stop
    ///     reactions, reads sentinel paths from this mod's config section.
    ///
    ///     Hard-removed from this build (vs. the FiresRPGmaker version we forked from):
    ///   â€¢ VAngarde anti-cheat reporting hooks â€” anti-cheat lives in FiresRPGmaker, the
    ///     Discord anti-cheat webhook channel still exists but the mod-validator code
    ///     paths that fed it are not part of this standalone build.
    ///
    ///     Soft-deps:
    ///   â€¢ Newtonsoft.Json â€” required by webhook poster + bot listener for Discord REST
    ///     payload (de)serialization. Usually present via Jotunn or BepInEx itself.
    ///     </summary>
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency("com.Fire.FiresUnifiedCore", BepInDependency.DependencyFlags.HardDependency)]
    // Soft-dep so BepInEx loads ExpandWorldPrefabs BEFORE us when it's installed - EwpScreenshotPoke's
    // conditional Prepare() resolves EWP types at PatchAll time and silently skips if they aren't loaded
    // yet (which is exactly what happened when we loaded first: line-126 vs line-285 in the boot log).
    [BepInDependency("expand_world_prefabs", BepInDependency.DependencyFlags.SoftDependency)]
    public class FiresDiscordIntegrationPlugin : BaseUnityPlugin
    {
        public const string PluginGUID    = "com.Fire.FiresDiscordIntegration";
        public const string PluginName    = "FiresDiscordIntegration";
        public const string PluginVersion = "1.0.31";

        public static FiresDiscordIntegrationPlugin Instance;
        public static readonly Harmony Harmony = new Harmony(PluginGUID);
        internal static BepInEx.Logging.ManualLogSource Log;

        public ConfigSync configSync;

        private void Awake()
        {
            Log = Logger;
            try { FiresDiscordBanner.PrintBig(); }
            catch (Exception ex) { Debug.LogWarning($"[{PluginName}] Big banner failed: {ex.Message}"); }

            Instance = this;

            // Config-sync scaffold (server locks the Discord webhook URLs, event flags,
            // bot listener channel IDs, etc. so clients can't override).
            configSync = new ConfigSync(PluginGUID)
            {
                DisplayName            = PluginName,
                CurrentVersion         = PluginVersion,
                MinimumRequiredVersion = PluginVersion,
            };
            // Minimal mod-level config (verbose flag + any future cross-cutting knobs).
            // Module-specific configs are bound by each module's own *Config.cs.
            ConfigManager.Instance.Initialize(Config);

            // Module configs â€” these bind their own config entries into Config, then
            // register them with configSync for server-locking.
            DiscordIntegrationConfig.Initialize(Config);
            DiscordIntegrationConfig.BindToSync(configSync);

            // PatchAll wires up every [HarmonyPatch] class in the assembly. That
            // includes DiscordIntegrationPatches, DiscordCrashDetector, DiscordBotListener,
            // DiscordStatusTracker, and ClientLogRelayIntegration itself â€” each of which
            // has Harmony patches on ZNet.Awake / ZNet.LoadWorld / Game lifecycle events
            // that handle their own self-initialization at the right moment (server vs
            // client). We don't need an explicit Discord/ClientLogRelay Init call here;
            // the patches do it when ZNet is actually available.
            Harmony.PatchAll();

            // Register the cross-mod Discord sink so producer mods (e.g. FiresVAngarde) can route
            // anti-cheat / character events through FUC's IDiscordSink contract instead of taking a
            // hard dep on this mod. The facade no-ops when no sink is registered.
            FiresCore.Bridge.DiscordSink.Register(new Discord.DiscordSinkAdapter());

            // Pre-warm DiscordBotIdentity. The /users/@me HTTP fetch is async and the
            // first webhook post that touches BotIdentity returns the fallback if the
            // fetch isn't complete yet. The ServerHeartbeat status panel is the
            // earliest webhook consumer in the boot sequence â€” without this pre-warm,
            // its FIRST post gets the configured fallback username + null avatar_url,
            // which Discord renders with the generic robot icon instead of the bot's
            // actual avatar. Discord doesn't update username/avatar on subsequent
            // edits, so the wrong avatar then sticks on the rolling status message
            // for the whole session. Calling ResolveUsername here kicks off the fetch
            // immediately in Awake; by the time ZNet.LoadWorld fires (many seconds
            // later) and ServerHeartbeat starts posting, identity has cached.
            try { Discord.DiscordBotIdentity.ResolveUsername(fallback: PluginName); }
            catch (Exception ex) { Debug.LogWarning($"[{PluginName}] BotIdentity pre-warm failed: {ex.Message}"); }

            // Pre-warm the @-mention role-name cache the same way.  The
            // Discord /guilds/{id}/roles fetch is async and on a freshly-booted
            // server typically lands in ~1-2 seconds â€” but Chat.SendText on a
            // Shout can fire long before ZNet.LoadWorld's OnServerStart hook
            // would have kicked the fetch off, and a chat message that
            // contains an '@' otherwise ships to Discord as literal text on
            // the first try.  Starting the supervisor coroutine here means
            // the cache is almost always warm by the time the first player
            // connects.  Safe on clients too: BotTokenFile.Token is empty
            // there (it loads only in the dedicated server executable), so the
            // supervisor sits idle waiting for a token that will never come â€”
            // clients get their role cache from the server via
            // DiscordRoleNameSync's RoutedRPC, not from a direct fetch.
            try { Discord.DiscordMentionResolver.FetchRoles(); }
            catch (Exception ex) { Debug.LogWarning($"[{PluginName}] MentionResolver pre-warm failed: {ex.Message}"); }

            // Drop the bundled server wrapper bat into BepInEx/config/FiresDiscordIntegration/
            // on first load so operators can find it alongside the mod's config files and
            // copy it to their dedicated-server install root. Existing copies are never
            // clobbered (operator edits stay intact across mod updates).
            WrapperBatExtractor.ExtractIfMissing();

            try { Utilities.FdiHelpContent.Register(); }
            catch (System.Exception ex) { Log.LogWarning($"Help registration failed: {ex.Message}"); }

            Log.LogInfo($"v{PluginVersion} loaded.");

            // Compact "ready" banner + Discord-link summary box â€” deferred to
            // world-load time (ZNetScene up) so it bookends the load the same
            // way the other Fires* mods do (BIG = early-load, compact =
            // world-load). Runs headless too: no WaitForEndOfFrame, which
            // never fires in batchmode on a dedicated server.
            StartCoroutine(EmitCompactBannerWhenZNetReady());
        }

        // Waits for ZNetScene + its prefab list to be live (same readiness
        // signal FGN uses), then emits the load-summary box and the compact
        // loaded banner. Failure is non-fatal â€” falls back to a plain
        // "loaded" log so the load event is still recorded.
        private IEnumerator EmitCompactBannerWhenZNetReady()
        {
            while (ZNetScene.instance == null
                   || ZNetScene.instance.m_prefabs == null
                   || ZNetScene.instance.m_prefabs.Count == 0)
            {
                yield return null;
            }
            // One extra frame so ZNet.LoadWorld-driven subsystem starts
            // (bot listener, heartbeat) have settled before we sample them.
            yield return null;

            EmitDiscordLoadSummary();

            try { FiresDiscordBanner.Print(); }
            catch (Exception ex)
            {
                Logger.LogInfo($"{PluginName} v{PluginVersion} loaded. (banner failed: {ex.Message})");
            }
        }

        private static void EmitDiscordLoadSummary()
        {
            try
            {
                bool heartbeat = ServerHeartbeat.HeartbeatArmed;
                LoadSummary.For(PluginName).EmitMiniBox("ðŸ“¡ DISCORD LINK", new[]
                {
                    $"bot listener: {(DiscordBotListener.Running ? "on" : "off")}",
                    $"crash detector: {(ServerHeartbeat.CrashNotifierArmed ? "on" : "off")}",
                    heartbeat ? $"heartbeat: {ServerHeartbeat.IntervalSeconds:0}s" : "heartbeat: off",
                    // 1.0.26: "relay: off" was read as the CHAT relay being off; it is the client log relay.
                    ClientLogRelayIntegration.RelayActive ? "client log relay: on" : "client log relay: off",
                    ChatRelaySummary(out string chatDetail),
                });
                // The box cuts long lines; the full chat relay state and its shout hooks go in one plain line.
                Debug.Log("[Discord] " + chatDetail);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"discord summary failed: {ex.Message}"); }
        }

        // Where in-game chat goes: the webhook it resolves to (Chat, else General), the chat types switched on and whether the
        // three shout hooks (1.0.28) are patched.
        private static string ChatRelaySummary(out string detail)
        {
            string hooks = $"shout hooks: {Hooked(typeof(Chat), nameof(Chat.SendText), "client relay")}, "
                + $"{Hooked(typeof(ZRoutedRpc), "RouteRPC", "server route")}, {Hooked(typeof(ZRoutedRpc), "HandleRoutedRPC", "server handle")}";
            if (!DiscordIntegrationCore.IsActive()) { detail = "chat relay: off (Discord disabled); " + hooks; return "chat relay: off"; }
            string webhook = !string.IsNullOrEmpty(DiscordIntegrationConfig.ChatWebhookUrl?.Value) ? "ChatWebhookUrl"
                : !string.IsNullOrEmpty(DiscordIntegrationConfig.GeneralWebhookUrl?.Value) ? "GeneralWebhookUrl" : null;
            if (webhook == null) { detail = "chat relay: OFF - set ChatWebhookUrl or GeneralWebhookUrl; " + hooks; return "chat relay: OFF (no webhook)"; }
            var kinds = new System.Collections.Generic.List<string>();
            if (DiscordIntegrationConfig.NotifyPlayerShout?.Value == true) kinds.Add("shout");
            if (DiscordIntegrationConfig.NotifyPlayerSay?.Value == true) kinds.Add("say");
            if (DiscordIntegrationConfig.NotifyPlayerWhisper?.Value == true) kinds.Add("whisper");
            detail = $"chat relay: on via {webhook} ({(kinds.Count > 0 ? string.Join(", ", kinds) : "no chat types on")}); {hooks}";
            return "chat relay: on";
        }

        private static string Hooked(Type type, string method, string label)
        {
            var target = AccessTools.Method(type, method);
            var info = target != null ? HarmonyLib.Harmony.GetPatchInfo(target) : null;
            bool ours = info != null && (System.Linq.Enumerable.Any(info.Prefixes, p => p.owner == PluginGUID)
                                         || System.Linq.Enumerable.Any(info.Postfixes, p => p.owner == PluginGUID));
            return ours ? label : "NO " + label;
        }

        private void OnDestroy()
        {
            ClientLogRelayWorker.Drain();

            if (configSync != null)
                configSync = null;

            Instance = null;
        }
    }
}
