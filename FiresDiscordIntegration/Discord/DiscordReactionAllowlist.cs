using System;
using System.Collections.Generic;

namespace FiresDiscordIntegration.Discord
{
    /// <summary>
    /// The one check for Discord reactions that act on the server: client-log requests, disconnect capture, and
    /// restarting or stopping the server. Reads BotAdminDiscordIds on every call, and an empty list allows nobody.
    /// </summary>
    internal static class DiscordReactionAllowlist
    {
        public const int MaxReactionUsersPerRequest = 100;

        private const char ListedIdSeparator = ',';
        private const string EmptyListWarning =
            "Ignored a Discord reaction because [Discord.ClientLogs] BotAdminDiscordIds is empty. Add the Discord " +
            "user IDs allowed to request client logs and restart or stop the server. With VerboseLogging on, every " +
            "ignored reaction logs the reacting user's ID.";

        private static bool _emptyListWarningLogged;

        public static bool Allows(string discordUserId)
        {
            foreach (string listedId in ReadListedIds())
            {
                if (string.Equals(listedId, discordUserId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        public static void LogIgnoredReaction(string reactionDescription)
        {
            FiresLogger.LogVerbose($"{reactionDescription} ignored: not in BotAdminDiscordIds");
            if (_emptyListWarningLogged || ReadListedIds().Count > 0) return;
            _emptyListWarningLogged = true;
            FiresLogger.LogWarning(EmptyListWarning);
        }

        private static List<string> ReadListedIds()
        {
            var listedIds = new List<string>();
            foreach (string rawId in DiscordIntegrationConfig.BotAdminDiscordIds.Value.Split(ListedIdSeparator))
            {
                string trimmedId = rawId.Trim();
                if (trimmedId.Length > 0) listedIds.Add(trimmedId);
            }
            return listedIds;
        }
    }
}
