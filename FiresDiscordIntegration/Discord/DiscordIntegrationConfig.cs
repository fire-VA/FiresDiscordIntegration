using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using FiresCore.Sync;

namespace FiresDiscordIntegration.Discord
{
    /// <summary>
    /// BepInEx config entries for Discord webhook integration.
    /// All entries are server-locked via ConfigSync.
    /// </summary>
    public static class DiscordIntegrationConfig
    {
        // ------ Master Toggle ------
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> VerboseLogging;

        // ------ Webhook URLs ------
        public static ConfigEntry<string> GeneralWebhookUrl;
        public static ConfigEntry<string> AntiCheatWebhookUrl;
        public static ConfigEntry<string> ChatWebhookUrl;
        public static ConfigEntry<string> ClientLogWebhookUrl;

        // ------ Event Toggles ------
        public static ConfigEntry<bool> NotifyServerStart;
        public static ConfigEntry<bool> NotifyServerStop;
        public static ConfigEntry<bool> NotifyServerSave;
        public static ConfigEntry<bool> NotifyPlayerJoin;
        public static ConfigEntry<bool> NotifyPlayerLeave;
        public static ConfigEntry<bool> NotifyPlayerDeath;
        public static ConfigEntry<bool> NotifyPlayerShout;
        public static ConfigEntry<bool> NotifyPlayerSay;
        public static ConfigEntry<bool> NotifyPlayerWhisper;
        public static ConfigEntry<bool> NotifyRandomEvent;
        public static ConfigEntry<bool> NotifyNewDay;
        public static ConfigEntry<bool> NotifyAntiCheatKick;
        public static ConfigEntry<bool> NotifyAntiCheatViolation;
        public static ConfigEntry<bool> NotifyAdminCheatDetected;
        public static ConfigEntry<bool> NotifyAdminCommandSnapshot;
        public static ConfigEntry<bool> NotifyServerListChanged;
        public static ConfigEntry<string> ScreenshotsWebhookUrl;
        public static ConfigEntry<string> TestShotsWebhookUrl;
        public static ConfigEntry<string> TestShotsChannelId;
        public static ConfigEntry<string> TestPlayerNamePrefixes;
        public static ConfigEntry<bool> DeathScreenshots;
        public static ConfigEntry<bool> AchievementScreenshots;
        public static ConfigEntry<bool> VanillaAchievementScreenshots;
        public static ConfigEntry<bool> AllowAchievementsOnModdedProfile;
        public static ConfigEntry<bool> SkillLevelUpScreenshots;
        public static ConfigEntry<int> SkillLevelUpMinLevel;
        public static ConfigEntry<int> SkillLevelUpStep;
        public static ConfigEntry<bool> EwpPokeScreenshots;
        public static ConfigEntry<bool> BridgeScreenshots;
        public static ConfigEntry<bool> SendModListOnKick;
        public static ConfigEntry<bool> SendBepInExLogOnKick;
        public static ConfigEntry<bool> NotifyHourlySummary;
        public static ConfigEntry<bool> NotifyDailySummary;
        public static ConfigEntry<bool> EnableStatusHeartbeat;
        public static ConfigEntry<float> StatusHeartbeatIntervalMinutes;

        // ------ Client Log Forwarding (per-login) ------
        public static ConfigEntry<bool> NotifyClientLoginArtifacts;
        public static ConfigEntry<bool> AttachFullClientLogOnLogin;
        public static ConfigEntry<bool> AttachModListOnLogin;
        public static ConfigEntry<bool> AttachErrorsWarningsOnLogin;
        public static ConfigEntry<bool> OnlyNotifyLoginIfErrorsOrWarnings;
        public static ConfigEntry<bool>   EnableLogRequestReaction;
        public static ConfigEntry<string> BotAdminDiscordIds;

        // ------ Server Manager (private admin control panel: live status + restart/stop) ------
        public static ConfigEntry<string> ServerManagerWebhookUrl;
        public static ConfigEntry<string> ServerManagerChannelId;

        // ------ Appearance ------
        public static ConfigEntry<string> WebhookDisplayName;
        public static ConfigEntry<string> WebhookAvatarUrl;

        // ------ Discord -> Game Chat Relay (Bot Listener) ------
        public static ConfigEntry<bool> EnableBotListener;
        // Bot token VALUE is intentionally NOT a ConfigEntry - it lives at the Valheim
        // install root in FiresDiscordIntegration_BotToken.cfg, outside the
        // BepInEx/config/ directory that Thunderstore Mod Manager and r2modman
        // include in profile / modpack exports.  See Utilities.BotTokenFile.
        // Read everywhere via BotTokenFile.Token.  BotTokenPath below holds only the
        // PATH to that file, which is not a secret; BotTokenFile accepts it only inside
        // the Valheim install and refuses BepInEx/config or BepInEx/plugins.
        public static ConfigEntry<string> BotTokenPath;
        public static ConfigEntry<bool> EnableServerCommands;
        public static ConfigEntry<string> CommandPrefix;
        public static ConfigEntry<string> CommandUserIds;
        public static ConfigEntry<string> CommandRoleIds;
        public static ConfigEntry<string> AdminListPath;
        public static ConfigEntry<string> ChatChannelId;
        public static ConfigEntry<string> StatusChannelId;
        public static ConfigEntry<float> BotPollIntervalSeconds;
        public static ConfigEntry<string> DiscordGuildId;

        /// <summary>
        /// Remove any orphaned 'BotToken' key left in the config by an older version. BepInEx keeps
        /// unbound keys forever in <c>OrphanedEntries</c> (and rewrites them on every Save), so a text-only
        /// delete wouldn't stick - we pull it out of that dictionary (internal, via reflection) then Save.
        /// </summary>
        private static void StripLegacyBotTokenKey(ConfigFile config)
        {
            try
            {
                var prop = typeof(ConfigFile).GetProperty("OrphanedEntries",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!(prop?.GetValue(config) is System.Collections.IDictionary orphans) || orphans.Count == 0)
                    return;

                var doomed = new List<object>();
                foreach (System.Collections.DictionaryEntry entry in orphans)
                {
                    var key = entry.Key?.GetType().GetProperty("Key")?.GetValue(entry.Key) as string;
                    if (string.Equals(key, "BotToken", StringComparison.OrdinalIgnoreCase))
                        doomed.Add(entry.Key);
                }
                if (doomed.Count == 0) return;

                foreach (var k in doomed) orphans.Remove(k);
                config.Save();
                UnityEngine.Debug.Log("[FiresDiscordIntegration] Removed a leftover 'BotToken' key from the shared " +
                    "config. The bot token is read ONLY from FiresDiscordIntegration_BotToken.cfg (game root or " +
                    "BepInEx root) so it can never ride along in a packaged/shared config.");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[FiresDiscordIntegration] Legacy BotToken cleanup failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Bind all Discord config entries.
        /// Called from ConfigManager.Initialize().
        /// </summary>
        public static void Initialize(ConfigFile config)
        {
            // The bot token is NEVER a config entry (it can only be read from the external
            // FiresDiscordIntegration_BotToken.cfg, see BotTokenFile). Strip any leftover 'BotToken' key an
            // older version wrote into this shared config, so a packaged / shared config can never carry it.
            StripLegacyBotTokenKey(config);

            // Master Toggle
            Enabled = config.Bind("Discord", "Enabled", false,
                "Enable Discord webhook integration. Only active on server/host.");
            VerboseLogging = config.Bind("Discord", "VerboseLogging", false,
                "Enable detailed Discord integration debug logging.");

            // Webhook URLs
            GeneralWebhookUrl = config.Bind("Discord.Webhooks", "GeneralWebhookUrl", "",
                "Discord webhook URL for general events (join, leave, death, server status). " +
                "Must be a valid Discord webhook URL (https://discord.com/api/webhooks/...).");
            AntiCheatWebhookUrl = config.Bind("Discord.Webhooks", "AntiCheatWebhookUrl", "",
                "Discord webhook URL for anti-cheat notifications (kicks, violations). " +
                "Falls back to GeneralWebhookUrl if empty.");
            ChatWebhookUrl = config.Bind("Discord.Webhooks", "ChatWebhookUrl", "",
                "Discord webhook URL for in-game chat relay (shout messages). " +
                "Falls back to GeneralWebhookUrl if empty.");
            ClientLogWebhookUrl = config.Bind("Discord.Webhooks", "ClientLogWebhookUrl", "",
                "Discord webhook URL for per-login client log forwarding (full BepInEx log, mod list, " +
                "errors+warnings report). Fires on every successful client login so admins can audit " +
                "what mods the client is running and whether their session is clean. " +
                "Falls back to GeneralWebhookUrl if empty.");
            ScreenshotsWebhookUrl = config.Bind("Discord.Webhooks", "ScreenshotsWebhookUrl", "",
                "Discord webhook URL for ALL screenshot posts - deaths, achievements, skill level-ups, and " +
                "EWP-triggered screenshots all go here. Falls back to GeneralWebhookUrl if empty.");
            TestShotsWebhookUrl = config.Bind("Discord.Webhooks", "TestShotsWebhookUrl", "",
                "Discord webhook URL for screenshots other mods request through the Core bridge (test tools, " +
                "bots, load shots), so test output can go to a testing channel instead of the players' one. " +
                "Falls back to ScreenshotsWebhookUrl, then GeneralWebhookUrl, if empty.");
            TestShotsChannelId = config.Bind("Discord.Webhooks", "TestShotsChannelId", "",
                "Discord channel id for test shots, posted AS THE BOT (the bot token file; the bot needs Send Messages, Embed " +
                "Links and Attach Files there). When set, everything that would go to TestShotsWebhookUrl (Core-bridge " +
                "screenshots, test bots' deaths, achievements and level-ups) goes to this channel instead. Empty = the webhook.");
            TestPlayerNamePrefixes = config.Bind("Discord.Webhooks", "TestPlayerNamePrefixes", "",
                "Comma-separated player-name prefixes (case-insensitive) of test bots. Their deaths, achievements, " +
                "skill level-ups and screenshots post to TestShotsWebhookUrl instead of ScreenshotsWebhookUrl. " +
                "Empty = every player is a real player.");

            // Event Toggles
            NotifyServerStart = config.Bind("Discord.Events", "NotifyServerStart", true,
                "Send webhook when server starts.");
            NotifyServerStop = config.Bind("Discord.Events", "NotifyServerStop", true,
                "Send webhook when server stops.");
            NotifyServerSave = config.Bind("Discord.Events", "NotifyServerSave", false,
                "Send webhook when world is saved. Disabled by default (frequent).");
            NotifyPlayerJoin = config.Bind("Discord.Events", "NotifyPlayerJoin", true,
                "Send webhook when a player joins.");
            NotifyPlayerLeave = config.Bind("Discord.Events", "NotifyPlayerLeave", true,
                "Send webhook when a player leaves.");
            NotifyPlayerDeath = config.Bind("Discord.Events", "NotifyPlayerDeath", true,
                "Send webhook when a player dies.");
            DeathScreenshots = config.Bind("Discord.Events", "DeathScreenshots", true,
                "Attach a screenshot of the dying player's screen to the death webhook. The client captures " +
                "the shot at the moment of death and ships it to the server (synced to clients).");
            AchievementScreenshots = config.Bind("Discord.Events", "AchievementScreenshots", true,
                "Post achievement completions (with a screenshot of the player's screen at the moment) to the " +
                "progress webhook. Requires FiresRPGmaker on the client for achievement detection - without it " +
                "nothing fires (synced to clients).");
            VanillaAchievementScreenshots = config.Bind("Discord.Events", "VanillaAchievementScreenshots", true,
                "Also post Valheim's OWN achievements, the same way. Note that Valheim only unlocks these when the " +
                "game does not consider the session modded, so on a modded profile nothing unlocks unless " +
                "AllowAchievementsOnModdedProfile below is on (synced to clients).");
            AllowAchievementsOnModdedProfile = config.Bind("Discord.Events", "AllowAchievementsOnModdedProfile", false,
                "Let Valheim record achievement progress even though mods are loaded. Vanilla switches achievements " +
                "off whenever Game.isModded is set, which mods like Jotunn do, so vanilla achievements never unlock " +
                "on a modded server. Turning this ON unlocks REAL Steam achievements on each player's public Steam " +
                "profile, permanently - the game disables them on purpose, so treat this as a server-wide house rule. " +
                "Off by default (synced to clients).");
            SkillLevelUpScreenshots = config.Bind("Discord.Events", "SkillLevelUpScreenshots", true,
                "Post skill level-ups (with a screenshot showing the level-up toast) to the progress webhook " +
                "(synced to clients).");
            SkillLevelUpMinLevel = config.Bind("Discord.Events", "SkillLevelUpMinLevel", 10,
                "Only announce skill level-ups at or above this level (spam control; synced to clients).");
            SkillLevelUpStep = config.Bind("Discord.Events", "SkillLevelUpStep", 5,
                "Only announce skill levels divisible by this (10, 15, 20... with the default 5). " +
                "Set 1 to announce every level (synced to clients).");
            EwpPokeScreenshots = config.Bind("Discord.Events", "EwpPokeScreenshots", true,
                "ExpandWorldPrefabs integration: an EWP rule that pokes a Player with parameter 'screenshot' " +
                "(or 'screenshot:caption') makes that player's client capture its screen and post it to the " +
                "progress webhook. Inert when EWP isn't installed. Per-player 5s cooldown.");
            BridgeScreenshots = config.Bind("Discord.Events", "BridgeScreenshots", true,
                "Let other Fires mods request a captioned screenshot through the FiresUnifiedCore " +
                "ProgressEvents.ScreenshotRequested bridge (e.g. FiresDebugginTools posting one alongside " +
                "each automatic perf census). Inert when nothing raises it. Per-client cooldown.");
            NotifyPlayerShout = config.Bind("Discord.Events", "NotifyPlayerShout", true,
                "Send webhook when a player shouts in chat (/s, global range).");
            NotifyPlayerSay = config.Bind("Discord.Events", "NotifyPlayerSay", true,
                "Send webhook when a player uses normal local-range chat (/say, the default Enter-key chat).");
            NotifyPlayerWhisper = config.Bind("Discord.Events", "NotifyPlayerWhisper", true,
                "Send webhook when a player whispers (/w, private to nearest player). " +
                "Turn off if your server treats whispers as private and shouldn't surface them in Discord.");
            NotifyRandomEvent = config.Bind("Discord.Events", "NotifyRandomEvent", true,
                "Send webhook when a random event (raid) starts/stops.");
            NotifyNewDay = config.Bind("Discord.Events", "NotifyNewDay", false,
                "Send webhook on each new in-game day. Disabled by default (frequent).");
            NotifyAntiCheatKick = config.Bind("Discord.Events", "NotifyAntiCheatKick", true,
                "Send webhook when VAngarde kicks a player.");
            NotifyAntiCheatViolation = config.Bind("Discord.Events", "NotifyAntiCheatViolation", true,
                "Send webhook when VAngarde detects a violation (even in log-only mode).");
            NotifyAdminCheatDetected = config.Bind("Discord.Events", "NotifyAdminCheatDetected", true,
                "Send webhook when an admin trips a behavioral cheat detection (logged, not kicked - monitor mode).");
            NotifyAdminCommandSnapshot = config.Bind("Discord.Events", "NotifyAdminCommandSnapshot", true,
                "Send webhook when an admin toggles a cheat/debug command (devcommands/debugmode/god/ghost/fly/nocost).");
            NotifyServerListChanged = config.Bind("Discord.Events", "NotifyServerListChanged", true,
                "Send a readout to the anti-cheat channel when a server-side VAngarde mod list changes " +
                "(enforced/exact-match, whitelist, server-only, blacklist), so players can see what's allowed.");
            SendModListOnKick = config.Bind("Discord.Events", "SendModListOnKick", true,
                "Attach the kicked player's mod list as a text file to the kick webhook. " +
                "Useful for debugging which mods caused the violation.");
            SendBepInExLogOnKick = config.Bind("Discord.Events", "SendBepInExLogOnKick", true,
                "Attach the kicked client's BepInEx output log to the kick webhook. " +
                "The client sends the tail of their log with their challenge response. " +
                "Falls back to the server's log if the client's log wasn't received.");
            NotifyHourlySummary = config.Bind("Discord.Events", "NotifyHourlySummary", true,
                "Send a rich status embed every hour with player activity, uptime, and session stats.");
            NotifyDailySummary = config.Bind("Discord.Events", "NotifyDailySummary", true,
                "Send a daily recap embed at midnight Eastern Time (EST/EDT) with a full 24-hour summary.");
            EnableStatusHeartbeat = config.Bind("Discord.Events", "EnableStatusHeartbeat", false,
                "Send periodic status heartbeat messages to the status channel showing server uptime and online players. " +
                "Automatically cleans up messages older than 24 hours. Independent from hourly summaries.");
            StatusHeartbeatIntervalMinutes = config.Bind("Discord.Events", "StatusHeartbeatIntervalMinutes", 15f,
                "How often (in minutes) to send status heartbeat updates. Range: 1-60. Default: 15 minutes.");

            // Client Log Forwarding (per-login)
            NotifyClientLoginArtifacts = config.Bind("Discord.ClientLogs", "NotifyClientLoginArtifacts", false,
                "Send a Discord embed with the client's BepInEx log, mod list, and errors/warnings summary " +
                "on every successful client login. Routes to ClientLogWebhookUrl (or GeneralWebhookUrl).");
            AttachFullClientLogOnLogin = config.Bind("Discord.ClientLogs", "AttachFullClientLogOnLogin", false,
                "Attach the client's full BepInEx LogOutput.log as a .log file. Can be large (1-10 MB). " +
                "Only applies while EnableLogRequestReaction is off; with it on, the log is fetched on demand " +
                "by reacting to the login snapshot instead.");
            AttachModListOnLogin = config.Bind("Discord.ClientLogs", "AttachModListOnLogin", true,
                "Attach the client's mod list (GUID=Version) as a .txt file. " +
                "Only applies while EnableLogRequestReaction is off.");
            AttachErrorsWarningsOnLogin = config.Bind("Discord.ClientLogs", "AttachErrorsWarningsOnLogin", true,
                "Attach the client log report (known problems plus grouped errors and warnings) as a .txt file. " +
                "Only applies while EnableLogRequestReaction is off.");
            OnlyNotifyLoginIfErrorsOrWarnings = config.Bind("Discord.ClientLogs", "OnlyNotifyLoginIfErrorsOrWarnings", false,
                "If true, only post the login snapshot when the client's log has at least one error or warning. " +
                "Recommended for busy servers to reduce Discord noise. Skipped logins are still saved to disk, " +
                "but have no snapshot to react to.");
            EnableLogRequestReaction = config.Bind("Discord.ClientLogs", "EnableLogRequestReaction", true,
                "Post login snapshots without attachments and add reactions that let the Discord users in " +
                "BotAdminDiscordIds request the full log, the log report or the mod lists, or have the session " +
                "log posted when the player disconnects. Needs the bot token file. When off, the Attach settings " +
                "above choose which files are attached instead.");
            BotAdminDiscordIds = config.Bind("Discord.ClientLogs", "BotAdminDiscordIds", "",
                "Comma-separated Discord user IDs allowed to act through reactions: requesting logs, log reports, " +
                "mod lists and disconnect capture on login snapshots, and restarting or stopping the server from " +
                "the Server Manager panel. The bundled valheim_server_wrapper.bat reads the same list before it " +
                "restarts a stopped server. Reactions from anyone else are ignored, and an empty list blocks every " +
                "request. The full-list reaction on VAngarde server-list cards stays open to everyone. " +
                "Right-click a user in Discord (with developer mode on) -> Copy User ID.");

            ServerManagerWebhookUrl = config.Bind("Discord.ServerManager", "ServerManagerWebhookUrl", "",
                "Optional webhook URL for the Server Manager panel: the live-updating 'Server Online' message " +
                "(player list, uptime, last heartbeat) with the restart/stop reactions. Leave empty to post as the " +
                "bot instead. The crash notice posted after an unexpected exit needs this URL. Only users in " +
                "[Discord.ClientLogs] BotAdminDiscordIds can restart or stop the server, but keep the panel in a " +
                "private admin-only channel because it lists the players online.");
            ServerManagerChannelId = config.Bind("Discord.ServerManager", "ServerManagerChannelId", "",
                "Discord channel ID for the Server Manager control panel. " +
                "Right-click the channel in Discord (developer mode on) -> Copy Channel ID. " +
                "Required so the bot can edit the rolling heartbeat message and poll reactions. " +
                "Falls back to BotListener.StatusChannelId if empty.");

            // Appearance
            WebhookDisplayName = config.Bind("Discord.Appearance", "WebhookDisplayName", "Valheim Server",
                "Display name shown on Discord webhook messages.");
            WebhookAvatarUrl = config.Bind("Discord.Appearance", "WebhookAvatarUrl", "",
                "Avatar image URL for webhook messages. Leave empty for default.");

            // Discord -> Game Relay (Bot Listener)
            EnableBotListener = config.Bind("Discord.BotListener", "EnableBotListener", false,
                "Enable the Discord bot listener that relays messages from a Discord channel into the game server chat. " +
                "Requires a valid bot token (set in FiresDiscordIntegration_BotToken.cfg at the Valheim install root, " +
                "NOT in this config file) and a ChatChannelId set below.");
            ChatChannelId = config.Bind("Discord.BotListener", "ChatChannelId", "",
                "Discord Channel ID to listen for messages. Right-click the channel in Discord -> Copy Channel ID. " +
                "This should be the same channel your ChatWebhookUrl posts to.");
            StatusChannelId = config.Bind("Discord.BotListener", "StatusChannelId", "",
                "Discord Channel ID where the crash detector posts its live-updating status message. " +
                "The bot must have Send Messages permission in this channel. " +
                "Leave empty to use ChatChannelId instead.");
            BotPollIntervalSeconds = config.Bind("Discord.BotListener", "BotPollIntervalSeconds", 3f,
                "How often (in seconds) to poll Discord for new messages. Lower = faster relay, more API calls. " +
                "Range: 1-30. Discord rate limit is ~5 requests/second.");
            DiscordGuildId = config.Bind("Discord.BotListener", "DiscordGuildId", "",
                "Your Discord Server (Guild) ID. Required for @mention resolution in chat webhooks. " +
                "Right-click your server name in Discord -> Copy Server ID.");
            BotTokenPath = config.Bind("Discord.BotListener", "BotTokenPath", "",
                "Full path to the bot-token file, for rented servers that only let you upload to certain folders. " +
                "Empty = the usual locations (Valheim install root, then the BepInEx root). This setting holds the " +
                "PATH ONLY, never the token. It must be inside the Valheim install or the BepInEx folder. A path inside " +
                "BepInEx/config or BepInEx/plugins is REFUSED, because " +
                "those folders are packaged into r2modman / Thunderstore exports and the token would be shared. " +
                "Safest of all on a rented host: set the " + Utilities.BotTokenFile.TokenEnvironmentVariable +
                " environment variable instead and leave this empty.");

            EnableServerCommands = config.Bind("Discord.BotListener", "EnableServerCommands", false,
                "Let allow-listed Discord users run server commands in the chat channel: players, kick, ban, " +
                "unban, admin add/remove. Requires the bot listener and a token. Off by default.");
            CommandPrefix = config.Bind("Discord.BotListener", "CommandPrefix", "!",
                "Prefix that marks a Discord message as a server command, e.g. '!players'.");
            CommandUserIds = config.Bind("Discord.BotListener", "CommandUserIds", "",
                "Comma-separated Discord USER IDs allowed to run server commands. Right-click a user -> Copy User ID. " +
                "Empty and no allowed roles = nobody can run them, which is the safe default.");
            CommandRoleIds = config.Bind("Discord.BotListener", "CommandRoleIds", "",
                "Comma-separated Discord ROLE IDs allowed to run server commands. Anyone holding one of these roles " +
                "may kick and ban, so keep it to your staff roles.");
            AdminListPath = config.Bind("Discord.BotListener", "AdminListPath", "",
                "Optional full path to the server's adminlist.txt, for hosts that keep it outside the save folder. " +
                "It must be inside the Valheim install or the BepInEx folder. " +
                "Empty = the list Valheim itself uses. 'admin add' always updates Valheim's live list so it applies " +
                "immediately; when this is set the same id is also written to that file.");
        }

        /// <summary>
        /// Registers Discord config entries with ConfigSync for server-locking.
        /// </summary>
        public static void BindToSync(ConfigSync configSync)
        {
            if (configSync == null) return;

            configSync.AddConfigEntry(Enabled);
            configSync.AddConfigEntry(VerboseLogging);
            // NOTE: Webhook URLs, BotToken, ChatChannelId, and DiscordGuildId are
            // intentionally NOT synced. They contain secrets (bot token) and server
            // infrastructure details (webhook URLs, channel/guild IDs) that clients
            // should never receive. These are read server-side only.
            configSync.AddConfigEntry(NotifyServerStart);
            configSync.AddConfigEntry(NotifyServerStop);
            configSync.AddConfigEntry(NotifyServerSave);
            configSync.AddConfigEntry(NotifyPlayerJoin);
            configSync.AddConfigEntry(NotifyPlayerLeave);
            configSync.AddConfigEntry(NotifyPlayerDeath);
            configSync.AddConfigEntry(DeathScreenshots);
            configSync.AddConfigEntry(AchievementScreenshots);
            configSync.AddConfigEntry(VanillaAchievementScreenshots);
            configSync.AddConfigEntry(AllowAchievementsOnModdedProfile);
            configSync.AddConfigEntry(SkillLevelUpScreenshots);
            configSync.AddConfigEntry(SkillLevelUpMinLevel);
            configSync.AddConfigEntry(SkillLevelUpStep);
            configSync.AddConfigEntry(EwpPokeScreenshots);
            configSync.AddConfigEntry(BridgeScreenshots);
            configSync.AddConfigEntry(NotifyPlayerShout);
            configSync.AddConfigEntry(NotifyPlayerSay);
            configSync.AddConfigEntry(NotifyPlayerWhisper);
            configSync.AddConfigEntry(NotifyRandomEvent);
            configSync.AddConfigEntry(NotifyNewDay);
            configSync.AddConfigEntry(NotifyAntiCheatKick);
            configSync.AddConfigEntry(NotifyAntiCheatViolation);
            configSync.AddConfigEntry(NotifyAdminCheatDetected);
            configSync.AddConfigEntry(NotifyAdminCommandSnapshot);
            configSync.AddConfigEntry(NotifyServerListChanged);
            configSync.AddConfigEntry(SendModListOnKick);
            configSync.AddConfigEntry(SendBepInExLogOnKick);
            configSync.AddConfigEntry(NotifyHourlySummary);
            configSync.AddConfigEntry(NotifyDailySummary);
            configSync.AddConfigEntry(EnableStatusHeartbeat);
            configSync.AddConfigEntry(StatusHeartbeatIntervalMinutes);
            configSync.AddConfigEntry(WebhookDisplayName);
            configSync.AddConfigEntry(WebhookAvatarUrl);

            // Client-log forwarding toggles (webhook URL intentionally not synced - server secret)
            configSync.AddConfigEntry(NotifyClientLoginArtifacts);
            configSync.AddConfigEntry(AttachFullClientLogOnLogin);
            configSync.AddConfigEntry(AttachModListOnLogin);
            configSync.AddConfigEntry(AttachErrorsWarningsOnLogin);
            configSync.AddConfigEntry(OnlyNotifyLoginIfErrorsOrWarnings);
            configSync.AddConfigEntry(EnableLogRequestReaction);
            // BotAdminDiscordIds is server-only; not synced so
            // clients don't see (or try to act on) the admin allowlist.
        }

        /// <summary>
        /// Resolve which webhook URL to use for a given category.
        /// Falls back to GeneralWebhookUrl if the category-specific URL is empty.
        /// </summary>
        public static WebhookCategory ShotCategoryFor(string playerName)
        {
            string prefixes = TestPlayerNamePrefixes?.Value;
            if (string.IsNullOrEmpty(prefixes) || string.IsNullOrEmpty(playerName)) return WebhookCategory.Screenshots;
            foreach (var p in prefixes.Split(','))
            {
                string prefix = p.Trim();
                if (prefix.Length > 0 && playerName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return WebhookCategory.TestShots;
            }
            return WebhookCategory.Screenshots;
        }

        public static string ResolveWebhookUrl(WebhookCategory category)
        {
            string url = null;
            switch (category)
            {
                case WebhookCategory.AntiCheat:
                    url = AntiCheatWebhookUrl?.Value;
                    break;
                case WebhookCategory.Chat:
                    url = ChatWebhookUrl?.Value;
                    break;
                case WebhookCategory.ClientLog:
                    url = ClientLogWebhookUrl?.Value;
                    break;
                case WebhookCategory.Screenshots:
                    url = ScreenshotsWebhookUrl?.Value;
                    break;
                case WebhookCategory.TestShots:
                    // Fire (09-29 22:5x): test shots go to a private channel through the bot. The target is a
                    // "discord-bot-channel:<id>" address both posters understand (BotChannelTarget); without a bot token it
                    // stays on the webhook, so nothing is lost.
                    string testChannel = BotChannelTarget.CleanId(TestShotsChannelId?.Value);
                    if (testChannel != null && !string.IsNullOrEmpty(FiresDiscordIntegration.Utilities.BotTokenFile.Token))
                        return BotChannelTarget.Scheme + testChannel;
                    url = TestShotsWebhookUrl?.Value;
                    if (string.IsNullOrEmpty(url)) url = ScreenshotsWebhookUrl?.Value;
                    break;
            }

            if (string.IsNullOrEmpty(url))
                url = GeneralWebhookUrl?.Value;

            return url;
        }
    }

    /// <summary>
    /// A post target that is a channel written to AS THE BOT ("discord-bot-channel:&lt;id&gt;"), instead of a webhook URL.
    /// ResolveWebhookUrl hands it out for test shots when TestShotsChannelId is set; MinimalWebhookPoster.Post and
    /// DiscordWebhookService turn it into POST /channels/{id}/messages with the bot token. The token never leaves FDI.
    /// </summary>
    public static class BotChannelTarget
    {
        public const string Scheme = "discord-bot-channel:";
        private const string ApiBase = "https://discord.com/api/v10/channels/";

        // A Discord snowflake: 17-20 digits. Anything else is ignored (null).
        public static string CleanId(string raw)
        {
            string id = (raw ?? string.Empty).Trim();
            if (id.Length < 17 || id.Length > 20) return null;
            foreach (char ch in id) if (ch < '0' || ch > '9') return null;
            return id;
        }

        public static bool TryParse(string target, out string channelId)
        {
            channelId = null;
            if (string.IsNullOrEmpty(target) || !target.StartsWith(Scheme, StringComparison.Ordinal)) return false;
            channelId = CleanId(target.Substring(Scheme.Length));
            return channelId != null;
        }

        public static string MessagesUrl(string channelId) => ApiBase + channelId + "/messages";

        // "...7602658334": the tail only, for log lines.
        public static string Short(string channelId) =>
            string.IsNullOrEmpty(channelId) || channelId.Length <= 10 ? channelId : "…" + channelId.Substring(channelId.Length - 10);

        public static string AuthHeader() => "Bot " + FiresDiscordIntegration.Utilities.BotTokenFile.Token;
    }

    /// <summary>
    /// Webhook URL category for routing to the correct webhook endpoint.
    /// </summary>
    public enum WebhookCategory
    {
        General,
        AntiCheat,
        Chat,
        ClientLog,
        Screenshots,
        TestShots
    }
}
