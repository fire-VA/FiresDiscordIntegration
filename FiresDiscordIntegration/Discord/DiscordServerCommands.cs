using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace FiresDiscordIntegration.Discord
{
    /// <summary>
    /// Server commands typed in the Discord chat channel: who is online, kick, ban, unban, and admin add/remove.
    /// Runs server-side only, inside the bot listener's message loop, and consumes the message so a command never
    /// relays into game chat.
    ///
    /// Anyone who can post in that channel can type these, so the gate is an explicit allow-list of Discord user
    /// ids and role ids; with both empty nobody qualifies and the commands are inert even when enabled. Kick and
    /// ban go through vanilla's own ZNet calls, and admin add/remove goes through the very list Valheim reads, so
    /// a change applies immediately rather than at the next restart.
    /// </summary>
    internal static class DiscordServerCommands
    {
        private const int MaxListedPlayers = 60;
        private const long DiscordEpochMs = 1420070400000L;

        private static readonly string[] Verbs = { "players", "who", "online", "kick", "ban", "unban", "admin", "fireshelp" };

        // The moment command handling became live, used to refuse anything typed earlier. The listener normally
        // seeds its cursor from the newest message and only asks for messages after it, but when that seed call
        // fails it falls back to fetching the newest message outright - which would run whatever command happens
        // to be sitting at the bottom of the channel. A kick or ban must never fire because a server restarted.
        private static readonly DateTime CommandsLiveUtc = DateTime.UtcNow;

        public static bool TryHandle(JObject message, string content, string authorId, string authorName)
        {
            try
            {
                if (DiscordIntegrationConfig.EnableServerCommands?.Value != true) return false;
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return false;
                if (string.IsNullOrWhiteSpace(content)) return false;

                string prefix = DiscordIntegrationConfig.CommandPrefix?.Value;
                if (string.IsNullOrWhiteSpace(prefix)) prefix = "!";
                if (!content.StartsWith(prefix, StringComparison.Ordinal)) return false;

                string[] parts = content.Substring(prefix.Length)
                    .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return false;

                string verb = parts[0].ToLowerInvariant();
                if (Array.IndexOf(Verbs, verb) < 0) return false;

                if (PredatesThisSession(message))
                {
                    FiresLogger.LogInfo($"[Discord] Ignored '{verb}' from {authorName} - typed before command handling started; commands never replay.");
                    return true;
                }

                if (!IsAllowed(message, authorId))
                {
                    Reply($"{authorName}: you are not on this server's Discord command allow-list.");
                    FiresLogger.LogInfo($"[Discord] Refused '{verb}' from {authorName} ({authorId}) - not allow-listed.");
                    return true;
                }

                Execute(verb, parts, authorName);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] Server command failed: {ex.Message}");
                return false;
            }
        }

        private static void Execute(string verb, string[] parts, string authorName)
        {
            string argument = parts.Length > 1 ? string.Join(" ", parts.Skip(1).ToArray()).Trim() : string.Empty;
            switch (verb)
            {
                case "players":
                case "who":
                case "online":
                    Reply(BuildPlayerList());
                    return;
                case "kick":
                    RunAgainstPlayer(verb, argument, authorName, target => ZNet.instance.Kick(target));
                    return;
                case "ban":
                    RunAgainstPlayer(verb, argument, authorName, target => ZNet.instance.Ban(target));
                    return;
                case "unban":
                    if (string.IsNullOrEmpty(argument)) { Reply("Usage: unban <player id>"); return; }
                    ZNet.instance.Unban(argument);
                    Announce($"**{argument}** was unbanned by {authorName}.");
                    return;
                case "admin":
                    HandleAdmin(parts, authorName);
                    return;
                default:
                    Reply(HelpText());
                    return;
            }
        }

        private static void RunAgainstPlayer(string verb, string argument, string authorName, Action<string> act)
        {
            if (string.IsNullOrEmpty(argument)) { Reply($"Usage: {verb} <player name or id>"); return; }
            string resolved = ResolveOnlinePlayer(argument) ?? argument;
            act(resolved);
            Announce($"**{resolved}** was {(verb == "kick" ? "kicked" : "banned")} by {authorName}.");
            FiresLogger.LogInfo($"[Discord] {authorName} ran {verb} on '{resolved}'.");
        }

        /// <summary>Matches a name fragment against the online list so staff can type a partial name.</summary>
        private static string ResolveOnlinePlayer(string wanted)
        {
            var players = ZNet.instance.GetPlayerList();
            if (players == null) return null;
            foreach (var player in players)
            {
                if (string.Equals(player.m_name, wanted, StringComparison.OrdinalIgnoreCase)) return player.m_name;
                if (string.Equals(player.m_userInfo.m_id.ToString(), wanted, StringComparison.OrdinalIgnoreCase)) return wanted;
            }
            foreach (var player in players)
            {
                if (!string.IsNullOrEmpty(player.m_name) &&
                    player.m_name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                    return player.m_name;
            }
            return null;
        }

        private static string BuildPlayerList()
        {
            var players = ZNet.instance.GetPlayerList();
            var real = new List<ZNet.PlayerInfo>();
            if (players != null)
            {
                foreach (var player in players)
                {
                    // The relay's synthetic "[Discord]" entry is in this list too; it is not a person.
                    if (player.m_userInfo.m_id.ToString() == DiscordFakePlayerInjector.FakeUserId.ToString()) continue;
                    real.Add(player);
                }
            }
            if (real.Count == 0) return "Nobody is online.";

            var text = new StringBuilder();
            text.Append($"**{real.Count} online**");
            int shown = Math.Min(real.Count, MaxListedPlayers);
            for (int i = 0; i < shown; i++)
            {
                string name = string.IsNullOrEmpty(real[i].m_name) ? "(unnamed)" : real[i].m_name;
                text.Append($"\n\u2022 {name}  `{real[i].m_userInfo.m_id}`");
            }
            if (real.Count > shown) text.Append($"\n\u2026and {real.Count - shown} more.");
            return text.ToString();
        }

        private static void HandleAdmin(string[] parts, string authorName)
        {
            string action = parts.Length > 1 ? parts[1].ToLowerInvariant() : string.Empty;
            string id = parts.Length > 2 ? parts[2].Trim() : string.Empty;

            if (action == "list")
            {
                var current = ReadAdminList();
                Reply(current.Count == 0 ? "The admin list is empty." : "**Admins**\n" + string.Join("\n", current.Select(a => "\u2022 `" + a + "`").ToArray()));
                return;
            }
            if ((action != "add" && action != "remove") || string.IsNullOrEmpty(id))
            {
                Reply("Usage: admin add <steam id> | admin remove <steam id> | admin list");
                return;
            }

            // Security: an admin id is a platform id (letters, digits, _ and -), never free text.
            if (id.Length > 64 || !System.Text.RegularExpressions.Regex.IsMatch(id, @"^[A-Za-z0-9_-]+$"))
            {
                FiresLogger.LogWarning($"[Discord] admin {action} refused: the id is not a platform id ({id.Length} chars).");
                Reply("That is not a valid platform id (letters, digits, _ and - only, up to 64).");
                return;
            }

            var adminList = LiveAdminList();
            if (adminList == null) { Reply("This server did not expose its admin list, so nothing was changed."); return; }

            if (action == "add") adminList.Add(id); else adminList.Remove(id);
            PushAdminListToClients();
            MirrorToConfiguredFile(action, id);
            Announce($"`{id}` was {(action == "add" ? "added to" : "removed from")} the admin list by {authorName}.");
            FiresLogger.LogInfo($"[Discord] {authorName} ran admin {action} for '{id}'.");
        }

        private static SyncedList LiveAdminList()
        {
            try { return (SyncedList)AccessTools.Field(typeof(ZNet), "m_adminList").GetValue(ZNet.instance); }
            catch (Exception ex) { Debug.LogWarning($"[Discord] Could not reach the admin list: {ex.Message}"); return null; }
        }

        private static List<string> ReadAdminList()
        {
            var list = LiveAdminList();
            try { return list != null ? new List<string>(list.GetList()) : new List<string>(); }
            catch { return new List<string>(); }
        }

        private static void PushAdminListToClients()
        {
            try { AccessTools.Method(typeof(ZNet), "SendAdminList")?.Invoke(ZNet.instance, null); }
            catch (Exception ex) { Debug.LogWarning($"[Discord] Admin list push failed: {ex.Message}"); }
        }

        /// <summary>Also writes the operator's own adminlist file, for hosts that keep one outside the save folder.</summary>
        private static void MirrorToConfiguredFile(string action, string id)
        {
            string path = DiscordIntegrationConfig.AdminListPath?.Value;
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                path = Path.GetFullPath(path.Trim());
                if (!Utilities.BotTokenFile.IsInsideGameFolders(path))
                {
                    Debug.LogWarning($"[Discord] AdminListPath '{path}' is outside the Valheim install and the BepInEx folder; not written.");
                    Reply($"Valheim's own admin list was updated. `{path}` was not written: AdminListPath must be inside the Valheim install or the BepInEx folder.");
                    return;
                }
                var lines = File.Exists(path) ? new List<string>(File.ReadAllLines(path)) : new List<string>();
                lines.RemoveAll(line => string.Equals(line.Trim(), id, StringComparison.OrdinalIgnoreCase));
                if (action == "add") lines.Add(id);
                File.WriteAllLines(path, lines.ToArray());
                FiresLogger.LogInfo($"[Discord] Mirrored admin {action} to {path}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] Could not write AdminListPath '{path}': {ex.Message}");
                Reply($"Valheim's own admin list was updated, but writing `{path}` failed: {ex.Message}");
            }
        }

        /// <summary>A Discord id is a snowflake whose top 42 bits are the post time, so the message dates itself.</summary>
        private static bool PredatesThisSession(JObject message)
        {
            string id = message?.Value<string>("id");
            if (string.IsNullOrEmpty(id) || !long.TryParse(id, out long snowflake) || snowflake <= 0) return false;
            DateTime posted = DateTimeOffset.FromUnixTimeMilliseconds((snowflake >> 22) + DiscordEpochMs).UtcDateTime;
            return posted < CommandsLiveUtc;
        }

        private static bool IsAllowed(JObject message, string authorId)
        {
            if (!string.IsNullOrEmpty(authorId) && Ids(DiscordIntegrationConfig.CommandUserIds?.Value).Contains(authorId))
                return true;

            var allowedRoles = Ids(DiscordIntegrationConfig.CommandRoleIds?.Value);
            if (allowedRoles.Count == 0) return false;
            var roles = (message?["member"] as JObject)?["roles"] as JArray;
            if (roles == null) return false;
            foreach (var role in roles)
            {
                string roleId = role?.ToString();
                if (!string.IsNullOrEmpty(roleId) && allowedRoles.Contains(roleId)) return true;
            }
            return false;
        }

        private static HashSet<string> Ids(string csv)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(csv)) return set;
            foreach (string part in csv.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                set.Add(part.Trim());
            return set;
        }

        private static string HelpText() =>
            "**Server commands**\n" +
            "\u2022 `players` \u2014 who is online\n" +
            "\u2022 `kick <name or id>`\n" +
            "\u2022 `ban <name or id>` / `unban <id>`\n" +
            "\u2022 `admin add <steam id>` / `admin remove <steam id>` / `admin list`";

        private static void Reply(string text) => DiscordBotListener.PostToChatChannel(text);

        private static void Announce(string text)
        {
            Reply(text);
            FiresLogger.LogInfo("[Discord] " + text.Replace("**", ""));
        }
    }
}
