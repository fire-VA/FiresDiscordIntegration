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
        public static ConfigEntry<string> EventsWebhookUrl;

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
        public static ConfigEntry<string> ChatRelayIgnorePrefixes;
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
            Enabled = config.Bind("Discord", "Enabled", false, Vis("Turn the Discord integration on. Only runs on the server or host."));
            VerboseLogging = config.Bind("Discord", "VerboseLogging", false, Hid("Write extra Discord debug lines to the log."));

            // Webhook URLs
            GeneralWebhookUrl = config.Bind("Discord.Webhooks", "GeneralWebhookUrl", "", Adv("Discord webhook URL for general posts (joins, leaves, deaths, server status)."));
            AntiCheatWebhookUrl = config.Bind("Discord.Webhooks", "AntiCheatWebhookUrl", "", Adv("Webhook URL for anti-cheat posts. Empty uses the general webhook."));
            ChatWebhookUrl = config.Bind("Discord.Webhooks", "ChatWebhookUrl", "", Adv("Webhook URL for the in-game chat relay. Empty uses the general webhook."));
            ClientLogWebhookUrl = config.Bind("Discord.Webhooks", "ClientLogWebhookUrl", "", Adv("Webhook URL for player login log reports. Empty uses the general webhook."));
            EventsWebhookUrl = config.Bind("Discord.Webhooks", "EventsWebhookUrl", "", Adv("Webhook URL for raid start and end posts. Empty uses the general webhook."));
            ScreenshotsWebhookUrl = config.Bind("Discord.Webhooks", "ScreenshotsWebhookUrl", "", Adv("Webhook URL for all screenshot posts. Empty uses the general webhook."));
            TestShotsWebhookUrl = config.Bind("Discord.Webhooks", "TestShotsWebhookUrl", "", Hid("Webhook URL for test-tool screenshots. Empty uses the screenshots webhook."));
            TestShotsChannelId = config.Bind("Discord.Webhooks", "TestShotsChannelId", "", Hid("Channel ID where the bot posts test screenshots. Empty uses the test webhook."));
            TestPlayerNamePrefixes = config.Bind("Discord.Webhooks", "TestPlayerNamePrefixes", "", Hid("Comma-separated name prefixes of test bots; their posts go to the test channel."));

            // Event Toggles
            NotifyServerStart = config.Bind("Discord.Events", "NotifyServerStart", true, Vis("Post when the server starts."));
            NotifyServerStop = config.Bind("Discord.Events", "NotifyServerStop", true, Vis("Post when the server stops."));
            NotifyServerSave = config.Bind("Discord.Events", "NotifyServerSave", false, Vis("Post when the world saves."));
            NotifyPlayerJoin = config.Bind("Discord.Events", "NotifyPlayerJoin", true, Vis("Post when a player joins."));
            NotifyPlayerLeave = config.Bind("Discord.Events", "NotifyPlayerLeave", true, Vis("Post when a player leaves."));
            NotifyPlayerDeath = config.Bind("Discord.Events", "NotifyPlayerDeath", true, Vis("Post when a player dies."));
            DeathScreenshots = config.Bind("Discord.Events", "DeathScreenshots", true, Vis("Add a screenshot of the player's screen to death posts."));
            AchievementScreenshots = config.Bind("Discord.Events", "AchievementScreenshots", true, Vis("Post achievements with a screenshot. Needs FiresRPGmaker on the player's game."));
            VanillaAchievementScreenshots = config.Bind("Discord.Events", "VanillaAchievementScreenshots", true, Vis("Also post Valheim's own achievements with a screenshot."));
            AllowAchievementsOnModdedProfile = config.Bind("Discord.Events", "AllowAchievementsOnModdedProfile", false, Vis("Let Valheim unlock its real Steam achievements while mods are loaded. Applies to every player."));
            SkillLevelUpScreenshots = config.Bind("Discord.Events", "SkillLevelUpScreenshots", true, Vis("Post skill level-ups with a screenshot."));
            SkillLevelUpMinLevel = config.Bind("Discord.Events", "SkillLevelUpMinLevel", 10, Adv("Only post skill level-ups at or above this level."));
            SkillLevelUpStep = config.Bind("Discord.Events", "SkillLevelUpStep", 5, Adv("Only post skill levels divisible by this number. 1 posts every level."));
            EwpPokeScreenshots = config.Bind("Discord.Events", "EwpPokeScreenshots", true, Vis("Let ExpandWorldPrefabs rules ask for a player screenshot post."));
            BridgeScreenshots = config.Bind("Discord.Events", "BridgeScreenshots", true, Vis("Let other Fires mods ask for a screenshot post."));
            NotifyPlayerShout = config.Bind("Discord.Events", "NotifyPlayerShout", true, Vis("Relay shouts to Discord."));
            NotifyPlayerSay = config.Bind("Discord.Events", "NotifyPlayerSay", true, Vis("Relay normal chat to Discord."));
            NotifyPlayerWhisper = config.Bind("Discord.Events", "NotifyPlayerWhisper", true, Vis("Relay whispers to Discord."));
            ChatRelayIgnorePrefixes = config.Bind("Discord.Events", "ChatRelayIgnorePrefixes", "", Adv("Comma-separated words; chat lines starting with one are not relayed."));
            NotifyRandomEvent = config.Bind("Discord.Events", "NotifyRandomEvent", true, Vis("Post when a raid starts or ends."));
            NotifyNewDay = config.Bind("Discord.Events", "NotifyNewDay", false, Vis("Post each new in-game day."));
            NotifyAntiCheatKick = config.Bind("Discord.Events", "NotifyAntiCheatKick", true, Vis("Post when VAngarde kicks a player."));
            NotifyAntiCheatViolation = config.Bind("Discord.Events", "NotifyAntiCheatViolation", true, Vis("Post when VAngarde detects a violation."));
            NotifyAdminCheatDetected = config.Bind("Discord.Events", "NotifyAdminCheatDetected", true, Vis("Post when an admin trips a cheat detection."));
            NotifyAdminCommandSnapshot = config.Bind("Discord.Events", "NotifyAdminCommandSnapshot", true, Vis("Post when an admin toggles a cheat command such as god or fly."));
            NotifyServerListChanged = config.Bind("Discord.Events", "NotifyServerListChanged", true, Vis("Post when the server's allowed mod lists change."));
            SendModListOnKick = config.Bind("Discord.Events", "SendModListOnKick", true, Vis("Attach the kicked player's mod list to the kick post."));
            SendBepInExLogOnKick = config.Bind("Discord.Events", "SendBepInExLogOnKick", true, Vis("Attach the kicked player's log to the kick post."));
            NotifyHourlySummary = config.Bind("Discord.Events", "NotifyHourlySummary", true, Vis("Post an hourly server summary."));
            NotifyDailySummary = config.Bind("Discord.Events", "NotifyDailySummary", true, Vis("Post a daily recap at midnight Eastern time."));
            EnableStatusHeartbeat = config.Bind("Discord.Events", "EnableStatusHeartbeat", false, Vis("Post a regular status message with uptime and players online."));
            StatusHeartbeatIntervalMinutes = config.Bind("Discord.Events", "StatusHeartbeatIntervalMinutes", 15f, Adv("Minutes between status messages (1 to 60)."));

            // Client Log Forwarding (per-login)
            NotifyClientLoginArtifacts = config.Bind("Discord.ClientLogs", "NotifyClientLoginArtifacts", false, Vis("Post a log report each time a player logs in."));
            AttachFullClientLogOnLogin = config.Bind("Discord.ClientLogs", "AttachFullClientLogOnLogin", false, Vis("Attach the player's full log to the login post (can be large)."));
            AttachModListOnLogin = config.Bind("Discord.ClientLogs", "AttachModListOnLogin", true, Vis("Attach the player's mod list to the login post."));
            AttachErrorsWarningsOnLogin = config.Bind("Discord.ClientLogs", "AttachErrorsWarningsOnLogin", true, Vis("Attach the player's errors and warnings to the login post."));
            OnlyNotifyLoginIfErrorsOrWarnings = config.Bind("Discord.ClientLogs", "OnlyNotifyLoginIfErrorsOrWarnings", false, Vis("Only post logins whose log has errors or warnings."));
            EnableLogRequestReaction = config.Bind("Discord.ClientLogs", "EnableLogRequestReaction", true, Vis("Let bot admins fetch logs by reacting to the login post instead of attaching files. Needs the bot token."));
            BotAdminDiscordIds = config.Bind("Discord.ClientLogs", "BotAdminDiscordIds", "", Adv("Comma-separated Discord user IDs allowed to use the bot reactions and restart or stop the server."));

            ServerManagerWebhookUrl = config.Bind("Discord.ServerManager", "ServerManagerWebhookUrl", "", Adv("Webhook URL for the Server Manager panel. Empty posts as the bot. Use a private admin channel."));
            ServerManagerChannelId = config.Bind("Discord.ServerManager", "ServerManagerChannelId", "", Adv("Channel ID for the Server Manager panel. Empty uses the status channel."));

            // Appearance
            WebhookDisplayName = config.Bind("Discord.Appearance", "WebhookDisplayName", "Valheim Server", Adv("Name shown on the mod's Discord posts."));
            WebhookAvatarUrl = config.Bind("Discord.Appearance", "WebhookAvatarUrl", "", Adv("Image URL for the posts' avatar. Empty uses the default."));

            // Discord -> Game Relay (Bot Listener)
            EnableBotListener = config.Bind("Discord.BotListener", "EnableBotListener", false, Vis("Relay messages from a Discord channel into game chat. Needs the bot token file and a chat channel ID."));
            ChatChannelId = config.Bind("Discord.BotListener", "ChatChannelId", "", Adv("Channel ID to relay into game chat."));
            StatusChannelId = config.Bind("Discord.BotListener", "StatusChannelId", "", Adv("Channel ID for the live status message. Empty uses the chat channel."));
            BotPollIntervalSeconds = config.Bind("Discord.BotListener", "BotPollIntervalSeconds", 3f, Adv("Seconds between checks for new Discord messages (1 to 30)."));
            DiscordGuildId = config.Bind("Discord.BotListener", "DiscordGuildId", "", Adv("Your Discord server ID, used to show @mentions."));
            BotTokenPath = config.Bind("Discord.BotListener", "BotTokenPath", "", Adv("Optional path to the bot token file (path only, never the token). Empty uses the usual locations."));

            EnableServerCommands = config.Bind("Discord.BotListener", "EnableServerCommands", false, Vis("Let allowed Discord users run server commands (players, kick, ban, admin) from the chat channel."));
            CommandPrefix = config.Bind("Discord.BotListener", "CommandPrefix", "!", Adv("Character that marks a Discord message as a server command."));
            CommandUserIds = config.Bind("Discord.BotListener", "CommandUserIds", "", Adv("Comma-separated Discord user IDs allowed to run server commands."));
            CommandRoleIds = config.Bind("Discord.BotListener", "CommandRoleIds", "", Adv("Comma-separated Discord role IDs allowed to run server commands."));
            AdminListPath = config.Bind("Discord.BotListener", "AdminListPath", "", Adv("Optional path to adminlist.txt. Empty uses Valheim's own list."));
        }

        // Description helpers: everyday settings show by default, setup values (URLs, IDs,
        // tuning numbers) sit under Advanced, and test/debug keys stay out of the manager.
        private static ConfigDescription Vis(string d) => new ConfigDescription(d);
        private static ConfigDescription Adv(string d) => new ConfigDescription(d, null, new ConfigurationManagerAttributes { IsAdvanced = true });
        private static ConfigDescription Hid(string d) => new ConfigDescription(d, null, new ConfigurationManagerAttributes { Browsable = false });

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
                case WebhookCategory.Events:
                    url = EventsWebhookUrl?.Value;
                    break;
                case WebhookCategory.Screenshots:
                    url = ScreenshotsWebhookUrl?.Value;
                    break;
                case WebhookCategory.TestShots:
                    // Test shots go to a private channel through the bot. The target is a
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
        TestShots,
        Events
    }
}
