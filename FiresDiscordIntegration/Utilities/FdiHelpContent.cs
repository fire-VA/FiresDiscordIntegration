using UnityEngine;

namespace FiresDiscordIntegration.Utilities
{
    // FDI's sections for the shared FiresCore help panel (registration only).
    // NOTE: never print webhook URLs, channel/guild IDs, or the bot token in help
    // text - reference config KEYS and file names only.
    internal static class FdiHelpContent
    {
        private const string ModId = "FiresDiscordIntegration";
        private static bool _registered;

        public static void Register()
        {
            if (_registered || Application.isBatchMode) return;
            _registered = true;

            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Discord Integration", 0, BuildOverview);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Server Setup", 1, BuildSetup, adminOnly: true);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Events & Screenshots", 2, BuildEvents, adminOnly: true);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Bot, Panel & Client Logs", 3, BuildBot, adminOnly: true);
        }

        private static void BuildOverview(FiresCore.Help.HelpContentWriter w)
        {
            w.Header("Discord Integration");
            w.Paragraph("The bridge between the server and its Discord. As a player you'll notice:");
            w.Bullet("Your shouts, says, and whispers can relay to Discord channels (and Discord chat " +
                "can flow back into the game).");
            w.Bullet("Deaths post to Discord as an embed \u2014 cause, attacker, damage \u2014 with a screenshot " +
                "captured from YOUR screen the moment you died.");
            w.Bullet("Achievements, big skill level-ups, joins/leaves, and world events can post too.");
            w.Paragraph("Everything is configured server-side; there is nothing you need to set up as " +
                "a player.");
        }

        private static void BuildSetup(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("Server Setup");
            w.SubHeader("Webhooks");
            w.Paragraph("Everything posts through Discord webhooks configured under [Discord.Webhooks]: " +
                "GeneralWebhookUrl, plus optional dedicated ChatWebhookUrl, ScreenshotsWebhookUrl, " +
                "AntiCheatWebhookUrl, and ClientLogWebhookUrl (each falls back to General when empty). " +
                "These are SECRETS \u2014 they are deliberately never synced to clients; keep them out of " +
                "modpack exports.");
            w.Bullet("[Discord] Enabled \u2014 master switch (server/host only)");
            w.Bullet("[Discord.Appearance] WebhookDisplayName / WebhookAvatarUrl \u2014 how posts look");
            w.AdminDivider();

            w.SubHeader("The Bot Token");
            w.Paragraph("The two-way bot (Discord \u2192 game chat, reaction controls) needs a bot token. " +
                "It is NEVER a config entry: it lives in its own file, " +
                "FiresDiscordIntegration_BotToken.cfg (key BotToken), at the server install root \u2014 " +
                "deliberately OUTSIDE BepInEx/config so config exports can't leak it. The server " +
                "scrubs any stray BotToken key found in old configs.");
            w.Bullet("Rented server? Set the FIRES_DISCORD_BOT_TOKEN environment variable instead \u2014 " +
                "nothing on disk, nothing to leak.");
            w.Bullet("Or point BotTokenPath at your own file inside the Valheim install. That setting holds the " +
                "PATH only, never the token, and a path inside BepInEx/config or BepInEx/plugins is refused, " +
                "because those are the folders that get packaged into profile and modpack exports.");
        }

        private static void BuildEvents(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("Events & Screenshots");
            w.Paragraph("Every post type has its own toggle under [Discord.Events]:");
            w.Bullet("Server lifecycle \u2014 NotifyServerStart / Stop / Save, EnableStatusHeartbeat + interval");
            w.Bullet("Players \u2014 NotifyPlayerJoin / Leave / Death, NotifyRandomEvent, NotifyNewDay");
            w.Bullet("Chat relay \u2014 NotifyPlayerShout / Say / Whisper (each direction-configurable)");
            w.Bullet("Summaries \u2014 NotifyHourlySummary, NotifyDailySummary (midnight ET)");
            w.Bullet("Anti-cheat \u2014 NotifyAntiCheatKick / Violation, NotifyAdminCheatDetected, " +
                "NotifyAdminCommandSnapshot, SendModListOnKick, SendBepInExLogOnKick");
            w.AdminDivider();

            w.SubHeader("Screenshots");
            w.Paragraph("Captured on the CLIENT and shipped to the server, which authors the embed:");
            w.Bullet("DeathScreenshots \u2014 the dying player's screen ~0.7s after death, posted with " +
                "cause/attacker/damage (a respawn fallback posts the death even if no image arrives)");
            w.Bullet("AchievementScreenshots \u2014 FiresRPGmaker's achievements (needs it on the client)");
            w.Bullet("VanillaAchievementScreenshots \u2014 Valheim's own achievements. Vanilla only unlocks " +
                "these when it does not consider the session modded, so on a modded server nothing fires " +
                "unless AllowAchievementsOnModdedProfile is on \u2014 and that unlocks REAL Steam " +
                "achievements on each player's profile, permanently. Off by default.");
            w.Bullet("SkillLevelUpScreenshots \u2014 gated by SkillLevelUpMinLevel and Step");
            w.Bullet("EwpPokeScreenshots \u2014 Expand World Prefabs poke events");
        }

        private static void BuildBot(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("Bot, Panel & Client Logs");
            w.SubHeader("Two-Way Chat Bot");
            w.Bullet("[Discord.BotListener] EnableBotListener + ChatChannelId \u2014 relay a Discord " +
                "channel's messages into server chat, with mentions, role colors, and emoji resolved.");
            w.Bullet("BotPollIntervalSeconds \u2014 polling cadence (default 3s).");
            w.AdminDivider();

            w.SubHeader("Server Commands from Discord");
            w.Bullet("EnableServerCommands \u2014 off by default. Typed in the chat channel with " +
                "CommandPrefix (default '!'): players, kick, ban, unban, admin add/remove/list.");
            w.Bullet("Anyone who can post in that channel could type them, so they only run for Discord " +
                "ids in CommandUserIds or holders of a role in CommandRoleIds. Both empty = nobody.");
            w.Bullet("A command never reaches game chat, and every use is logged with who ran it.");
            w.Bullet("admin add updates the list Valheim actually reads, so it applies at once. Set " +
                "AdminListPath as well if your host keeps adminlist.txt somewhere of its own inside the " +
                "Valheim install.");
            w.AdminDivider();

            w.SubHeader("Server-Manager Panel");
            w.Paragraph("A private admin channel ([Discord.ServerManager] ChannelId, optional " +
                "WebhookUrl) gets a live status message with reaction controls \u2014 users in " +
                "BotAdminDiscordIds can restart/stop the server from Discord, backed by the shipped " +
                "valheim_server_wrapper.bat.");
            w.AdminDivider();

            w.SubHeader("Client-Log Relay");
            w.Bullet("[Discord.ClientLogs] \u2014 a snapshot per player connect with errors, warnings and " +
                "known problems. EnableLogRequestReaction adds reactions to request the full log, log report, " +
                "mod lists or disconnect capture; with it off, the Attach settings pick the files to attach.");
            w.Bullet("OnlyNotifyLoginIfErrorsOrWarnings \u2014 skip the snapshot for clean logins.");
            w.Bullet("BotAdminDiscordIds \u2014 the Discord user IDs whose reactions count, for client logs " +
                "and restart/stop. Empty blocks everyone.");
        }
    }
}
