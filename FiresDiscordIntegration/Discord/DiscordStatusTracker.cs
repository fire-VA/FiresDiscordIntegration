using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FiresDiscordIntegration.Discord
{
    /// <summary>
    /// Tracks server activity metrics over rolling hourly windows for
    /// the Discord hourly status report.
    ///
    /// All data is in-memory only - resets on server restart.
    /// The tracker is ticked from <see cref="DiscordIntegrationPatches"/>
    /// via ZNet.Update, and individual events are fed in from the same patches.
    /// </summary>
    public static class DiscordStatusTracker
    {
        // ============================================================
        // Hourly counters (reset each report)
        // ============================================================
        private static int _joins;
        private static int _leaves;
        private static int _deaths;
        private static int _shouts;
        private static int _eventsStarted;
        private static int _worldSaves;

        // ============================================================
        // Session-lifetime tracking
        // ============================================================
        private static DateTime _serverStartTimeUtc;
        private static DateTime _lastReportTimeUtc;
        private static DateTime _lastHeartbeatTimeUtc;
        private static int _peakPlayersThisHour;
        private static int _totalJoinsSession;
        private static int _totalDeathsSession;

        // ============================================================
        // Daily counters (reset at midnight EST)
        // ============================================================
        // Eastern Time is UTC-5 (EST) / UTC-4 (EDT). We use a fixed EST offset
        // so the report always fires at midnight clock-time on the east coast.
        // During EDT the report fires at 01:00 EDT, which is acceptable — the
        // alternative (using the OS timezone) is not available on all server OSes.
        private const int EasternStandardUtcOffsetHours = -5;
        private static readonly TimeSpan EasternOffset = TimeSpan.FromHours(EasternStandardUtcOffsetHours);

        // The hourly report fires when this many minutes have elapsed since the
        // last one; the daily report fires when the EST hour-of-day matches
        // this value AND the EST calendar day has rolled over since the last
        // daily was sent (so we don't fire twice if the timer ticks twice in
        // the same midnight window).
        private const double HourlyReportIntervalMinutes = 60.0;
        private const int    DailyReportHourEst          = 0; // midnight EST
        private static int _dailyJoins;
        private static int _dailyLeaves;
        private static int _dailyDeaths;
        private static int _dailyShouts;
        private static int _dailyEvents;
        private static int _dailyWorldSaves;
        private static int _dailyPeakPlayers;
        private static double _dailyPlayMinutes;
        private static readonly List<string> _dailyPlayerNames = new List<string>();
        // The EST calendar date on which the last daily report was sent
        private static int _lastDailyReportEstDay = -1;

        // Per-player session time tracking
        // Key: platformId (steamId), Value: join timestamp (UTC)
        private static readonly Dictionary<string, DateTime> _activeSessions =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // Tracks total play-minutes accumulated this hour from players who left
        private static double _playMinutesThisHour;

        // Names of unique players who connected this hour (for the embed list)
        private static readonly List<string> _playerNamesThisHour = new List<string>();

        /// <summary>
        /// Initialize the tracker. Called once when the server starts.
        /// </summary>
        public static void Initialize()
        {
            _serverStartTimeUtc   = DateTime.UtcNow;
            _lastReportTimeUtc    = DateTime.UtcNow;
            _lastHeartbeatTimeUtc = DateTime.UtcNow;
            // Seed the daily report day so the server doesn't fire immediately on start
            _lastDailyReportEstDay = GetEstNow().Day;
            ResetHourlyCounters();
            ResetDailyCounters();
        }

        /// <summary>
        /// Shutdown and clear all state.
        /// </summary>
        public static void Shutdown()
        {
            _activeSessions.Clear();
            _playerNamesThisHour.Clear();
            _dailyPlayerNames.Clear();
            ResetHourlyCounters();
            ResetDailyCounters();
        }

        // ============================================================
        // Event recording (called from DiscordIntegrationPatches)
        // ============================================================

        public static void RecordJoin(string playerName, string platformId)
        {
            _joins++;
            _totalJoinsSession++;
            _dailyJoins++;

            if (!string.IsNullOrEmpty(playerName) && !_playerNamesThisHour.Contains(playerName))
                _playerNamesThisHour.Add(playerName);

            if (!string.IsNullOrEmpty(playerName) && !_dailyPlayerNames.Contains(playerName))
                _dailyPlayerNames.Add(playerName);

            if (!string.IsNullOrEmpty(platformId))
                _activeSessions[platformId] = DateTime.UtcNow;

            UpdatePeakPlayers();
        }

        public static void RecordLeave(string playerName, string platformId)
        {
            _leaves++;
            _dailyLeaves++;

            // Accumulate play time
            if (!string.IsNullOrEmpty(platformId) && _activeSessions.TryGetValue(platformId, out var joinTime))
            {
                double minutes = (DateTime.UtcNow - joinTime).TotalMinutes;
                _playMinutesThisHour += minutes;
                _dailyPlayMinutes   += minutes;
                _activeSessions.Remove(platformId);
            }
        }

        public static void RecordDeath()
        {
            _deaths++;
            _totalDeathsSession++;
            _dailyDeaths++;
        }

        public static void RecordShout()
        {
            _shouts++;
            _dailyShouts++;
        }

        public static void RecordEventStarted()
        {
            _eventsStarted++;
            _dailyEvents++;
        }

        public static void RecordWorldSave()
        {
            _worldSaves++;
            _dailyWorldSaves++;
        }

        // ============================================================
        // Hourly tick (called from ZNet.Update postfix)
        // ============================================================

        /// <summary>
        /// Check if an hour has elapsed since the last report and fire
        /// the summary if so. Call this frequently (e.g. every ZNet.Update).
        /// Also checks if it's time to send a status heartbeat.
        /// </summary>
        public static void Tick()
        {
            // NOTE: The periodic "Server Status" heartbeat is RETIRED.
            // It overlapped with the hourly summary AND with the new live-updating
            // ServerHeartbeat (ClientLogRelay.Interactions.ServerHeartbeat) that
            // posts a single rolling message with restart/stop reactions to the
            // private Server Manager channel. EnableStatusHeartbeat is still bound
            // for backward-compatible config files but no longer drives any output.

            // Hourly summary
            if ((DateTime.UtcNow - _lastReportTimeUtc).TotalMinutes >= HourlyReportIntervalMinutes)
            {
                SendHourlySummary();
                _lastReportTimeUtc = DateTime.UtcNow;
            }

            // Daily summary at midnight EST (UTC-5)
            DateTime estNow = GetEstNow();
            // Fire once per calendar day: when the current EST day differs from the last
            // reported day AND we are past midnight (hour == 0) to avoid firing mid-day
            // on a server that was offline at midnight and just restarted.
            if (estNow.Hour == DailyReportHourEst && estNow.Day != _lastDailyReportEstDay)
            {
                SendDailySummary(estNow);
                _lastDailyReportEstDay = estNow.Day;
            }
        }

        // ============================================================
        // Summary builder
        // ============================================================

        /// <summary>
        /// Sends a lightweight status heartbeat to the status channel.
        /// Shows server uptime and currently online players.
        /// Triggers cleanup of messages older than 24 hours in the status channel.
        /// </summary>
        private static void SendStatusHeartbeat()
        {
            if (!DiscordIntegrationCore.IsActive()) return;
            if (DiscordIntegrationConfig.EnableStatusHeartbeat?.Value != true) return;

            try
            {
                TimeSpan uptime = DateTime.UtcNow - _serverStartTimeUtc;
                int currentOnline = GetCurrentPlayerCount();
                int day = GetInGameDay();

                // Build status embed
                var embed = new DiscordEmbed()
                    .SetTitle("Server Status")
                    .SetColor(currentOnline > 0 ? DiscordColors.Green : DiscordColors.Yellow)
                    .AddField("Server", GetServerName(), true)
                    .AddField("Uptime", FormatDuration(uptime), true)
                    .AddField("Players Online", currentOnline.ToString(), true);

                if (day >= 0)
                {
                    embed.AddField("In-Game Day", day.ToString(), true);
                }

                // Add online player list if there are players
                string onlineList = GetOnlinePlayerList();
                if (!string.IsNullOrEmpty(onlineList))
                {
                    embed.AddField("Currently Online", onlineList);
                }
                else
                {
                    embed.AddField("Currently Online", "*No players connected*");
                }

                embed.SetFooter($"Last updated: {DateTime.Now:HH:mm:ss}");

                var url = DiscordIntegrationConfig.ResolveWebhookUrl(WebhookCategory.General);
                string username = DiscordIntegrationConfig.WebhookDisplayName?.Value ?? "Valheim Server";
                string avatarUrl = DiscordIntegrationConfig.WebhookAvatarUrl?.Value;
                DiscordWebhookService.SendEmbed(url, embed, username,
                    string.IsNullOrEmpty(avatarUrl) ? null : avatarUrl);

                LogVerbose($"Sent status heartbeat: uptime={FormatDuration(uptime)}, online={currentOnline}");

                // Clean up messages older than 24 hours in the status channel
                TriggerOldMessageCleanup();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] Failed to send status heartbeat: {ex.Message}");
            }
        }

        /// <summary>
        /// Triggers cleanup of messages older than 24 hours in the status channel.
        /// Only runs if bot listener is enabled (needs bot token for deletion).
        /// </summary>
        private static void TriggerOldMessageCleanup()
        {
            if (DiscordIntegrationConfig.EnableBotListener?.Value != true) return;

            string botToken = FiresDiscordIntegration.Utilities.BotTokenFile.Token;
            string statusChannelId = DiscordIntegrationConfig.StatusChannelId?.Value;

            // Fall back to chat channel if status channel not configured
            if (string.IsNullOrEmpty(statusChannelId))
                statusChannelId = DiscordIntegrationConfig.ChatChannelId?.Value;

            if (string.IsNullOrEmpty(botToken) || string.IsNullOrEmpty(statusChannelId))
                return;

            // Start the cleanup coroutine on the Discord bot listener's host
            DiscordBotListener.TriggerMessageCleanup(statusChannelId, TimeSpan.FromHours(24));
        }

        private static void SendHourlySummary()
        {
            if (!DiscordIntegrationCore.IsActive()) return;
            if (DiscordIntegrationConfig.NotifyHourlySummary?.Value != true) return;

            try
            {
                // Calculate play time for players still online (don't remove them)
                double onlinePlayMinutes = 0;
                foreach (var kvp in _activeSessions)
                {
                    onlinePlayMinutes += (DateTime.UtcNow - kvp.Value).TotalMinutes;
                }

                double totalPlayMinutes = _playMinutesThisHour + onlinePlayMinutes;
                TimeSpan uptime = DateTime.UtcNow - _serverStartTimeUtc;
                int currentOnline = GetCurrentPlayerCount();

                var embed = new DiscordEmbed()
                    .SetTitle("\U0001F4CA Hourly Server Report")
                    .SetColor(DiscordColors.Blue)
                    .AddField("Uptime", FormatDuration(uptime), true)
                    .AddField("Online Now", currentOnline.ToString(), true)
                    .AddField("Peak This Hour", _peakPlayersThisHour.ToString(), true)
                    .AddField("Joins", _joins.ToString(), true)
                    .AddField("Leaves", _leaves.ToString(), true)
                    .AddField("Deaths", _deaths.ToString(), true)
                    .AddField("Shouts", _shouts.ToString(), true)
                    .AddField("Events", _eventsStarted.ToString(), true)
                    .AddField("World Saves", _worldSaves.ToString(), true);

                // Total playtime across all players this hour
                if (totalPlayMinutes > 0)
                {
                    embed.AddField("Total Play Time", FormatDuration(TimeSpan.FromMinutes(totalPlayMinutes)), true);
                }

                // Session totals
                embed.AddField("Session Joins", _totalJoinsSession.ToString(), true);
                embed.AddField("Session Deaths", _totalDeathsSession.ToString(), true);

                // Player list (who was active this hour)
                if (_playerNamesThisHour.Count > 0)
                {
                    string names = string.Join(", ", _playerNamesThisHour.Take(20));
                    if (_playerNamesThisHour.Count > 20)
                        names += $" (+{_playerNamesThisHour.Count - 20} more)";
                    embed.AddField("Active Players This Hour", names);
                }

                // Currently online
                string onlineList = GetOnlinePlayerList();
                if (!string.IsNullOrEmpty(onlineList))
                {
                    embed.AddField("Currently Online", onlineList);
                }

                // In-game day
                int day = GetInGameDay();
                if (day >= 0)
                {
                    embed.AddField("In-Game Day", day.ToString(), true);
                }

                string serverName = GetServerName();
                embed.SetFooter($"{serverName} \u2022 Next report in 1 hour");

                var url = DiscordIntegrationConfig.ResolveWebhookUrl(WebhookCategory.General);
                ResolveBotIdentity(out string username, out string avatarUrl);
                DiscordWebhookService.SendEmbed(url, embed, username, avatarUrl);

                LogVerbose($"Sent hourly summary: {_joins}j/{_leaves}l/{_deaths}d, peak={_peakPlayersThisHour}, online={currentOnline}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] Failed to send hourly summary: {ex.Message}");
            }

            // Reset hourly counters but keep session-lifetime data
            ResetHourlyCounters();
        }

        // ============================================================
        // Daily summary builder
        // ============================================================

        private static void SendDailySummary(DateTime estNow)
        {
            if (!DiscordIntegrationCore.IsActive()) return;
            if (DiscordIntegrationConfig.NotifyDailySummary?.Value != true) return;

            try
            {
                // Include currently-online players' accumulated play time
                double onlinePlayMinutes = 0;
                foreach (var kvp in _activeSessions)
                    onlinePlayMinutes += (DateTime.UtcNow - kvp.Value).TotalMinutes;

                double totalPlayMinutes = _dailyPlayMinutes + onlinePlayMinutes;
                TimeSpan uptime        = DateTime.UtcNow - _serverStartTimeUtc;
                int currentOnline      = GetCurrentPlayerCount();

                // Yesterday's date label in EST (the day we just finished)
                DateTime yesterday = estNow.AddDays(-1);
                string dateLabel   = yesterday.ToString("MMMM d, yyyy");

                var embed = new DiscordEmbed()
                    .SetTitle($"\U0001F4C5 Daily Server Report - {dateLabel}")
                    .SetColor(DiscordColors.Purple)
                    .AddField("Server Uptime", FormatDuration(uptime), true)
                    .AddField("Online Now",    currentOnline.ToString(), true)
                    .AddField("Peak Players",  _dailyPeakPlayers.ToString(), true)
                    .AddField("Joins",         _dailyJoins.ToString(), true)
                    .AddField("Leaves",        _dailyLeaves.ToString(), true)
                    .AddField("Deaths",        _dailyDeaths.ToString(), true)
                    .AddField("Shouts",        _dailyShouts.ToString(), true)
                    .AddField("Raids",         _dailyEvents.ToString(), true)
                    .AddField("World Saves",   _dailyWorldSaves.ToString(), true);

                if (totalPlayMinutes > 0)
                    embed.AddField("Total Play Time", FormatDuration(TimeSpan.FromMinutes(totalPlayMinutes)), true);

                // Session totals since server start
                embed.AddField("Session Joins",  _totalJoinsSession.ToString(), true);
                embed.AddField("Session Deaths", _totalDeathsSession.ToString(), true);

                // Unique players active today
                if (_dailyPlayerNames.Count > 0)
                {
                    string names = string.Join(", ", _dailyPlayerNames.Take(30));
                    if (_dailyPlayerNames.Count > 30)
                        names += $" (+{_dailyPlayerNames.Count - 30} more)";
                    embed.AddField($"Players Active Today ({_dailyPlayerNames.Count})", names);
                }

                // Currently online at rollover
                string onlineList = GetOnlinePlayerList();
                if (!string.IsNullOrEmpty(onlineList))
                    embed.AddField("Currently Online", onlineList);

                int day = GetInGameDay();
                if (day >= 0)
                    embed.AddField("In-Game Day", day.ToString(), true);

                embed.SetFooter($"{GetServerName()} \u2022 Next daily report in ~24 hours (midnight EST)");

                var url = DiscordIntegrationConfig.ResolveWebhookUrl(WebhookCategory.General);
                ResolveBotIdentity(out string username, out string avatarUrl);
                DiscordWebhookService.SendEmbed(url, embed, username, avatarUrl);

                LogVerbose($"Sent daily summary for {dateLabel}: " +
                           $"{_dailyJoins}j/{_dailyLeaves}l/{_dailyDeaths}d, " +
                           $"peak={_dailyPeakPlayers}, online={currentOnline}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] Failed to send daily summary: {ex.Message}");
            }

            ResetDailyCounters();
        }

        // ============================================================
        // Helpers
        // ============================================================

        // Resolve the username + avatar to post hourly/daily reports under so they
        // match every other Discord embed in the mod (player join/leave, login
        // snapshots, etc.) instead of falling back to Discord's generic
        // "Captain Hook" robot identity. Order: explicit config override →
        // resolved bot identity (from /users/@me) → "Valheim Server" / null.
        private static void ResolveBotIdentity(out string username, out string avatarUrl)
        {
            string explicitName = DiscordIntegrationConfig.WebhookDisplayName?.Value;
            username = !string.IsNullOrEmpty(explicitName)
                ? explicitName
                : DiscordBotIdentity.ResolveUsername(fallback: "Valheim Server");

            string explicitAvatar = DiscordIntegrationConfig.WebhookAvatarUrl?.Value;
            avatarUrl = !string.IsNullOrEmpty(explicitAvatar)
                ? explicitAvatar
                : DiscordBotIdentity.ResolveAvatarUrl(fallback: null);
        }

        private static void ResetDailyCounters()
        {
            _dailyJoins       = 0;
            _dailyLeaves      = 0;
            _dailyDeaths      = 0;
            _dailyShouts      = 0;
            _dailyEvents      = 0;
            _dailyWorldSaves  = 0;
            _dailyPeakPlayers = GetCurrentPlayerCount();
            _dailyPlayMinutes = 0;
            _dailyPlayerNames.Clear();

            // Add currently-online players so they appear in tomorrow's report
            if (ZNet.instance != null)
            {
                var peers = ZNet.instance.GetPeers();
                if (peers != null)
                {
                    foreach (var peer in peers)
                    {
                        if (!string.IsNullOrEmpty(peer?.m_playerName))
                            _dailyPlayerNames.Add(peer.m_playerName);
                    }
                }
            }
        }

        /// <summary>
        /// Returns the current wall-clock time in Eastern Standard Time (UTC-5).
        /// We use a fixed offset rather than TimeZoneInfo because dedicated Linux
        /// servers often lack the Windows timezone database.
        /// </summary>
        private static DateTime GetEstNow()
        {
            return DateTime.UtcNow + EasternOffset;
        }

        private static void ResetHourlyCounters()
        {
            _joins = 0;
            _leaves = 0;
            _deaths = 0;
            _shouts = 0;
            _eventsStarted = 0;
            _worldSaves = 0;
            _peakPlayersThisHour = GetCurrentPlayerCount();
            _playMinutesThisHour = 0;
            _playerNamesThisHour.Clear();

            // Re-seed active session timers so play time resets for the new window
            // but players remain tracked
            var keys = _activeSessions.Keys.ToList();
            var now = DateTime.UtcNow;
            foreach (var key in keys)
                _activeSessions[key] = now;
        }

        private static void UpdatePeakPlayers()
        {
            int current = GetCurrentPlayerCount();
            if (current > _peakPlayersThisHour)
                _peakPlayersThisHour = current;
            if (current > _dailyPeakPlayers)
                _dailyPeakPlayers = current;
        }

        private static int GetCurrentPlayerCount()
        {
            try
            {
                return ZNet.instance?.GetPeers()?.Count ?? 0;
            }
            catch { return 0; }
        }

        private static string GetOnlinePlayerList()
        {
            try
            {
                var peers = ZNet.instance?.GetPeers();
                if (peers == null || peers.Count == 0) return null;

                var names = new List<string>();
                foreach (var peer in peers)
                {
                    if (!string.IsNullOrEmpty(peer?.m_playerName))
                        names.Add(peer.m_playerName);
                }

                if (names.Count == 0) return null;

                string result = string.Join(", ", names.Take(20));
                if (names.Count > 20)
                    result += $" (+{names.Count - 20} more)";
                return result;
            }
            catch { return null; }
        }

        private static int GetInGameDay()
        {
            try
            {
                if (EnvMan.instance == null || ZNet.instance == null) return -1;
                return EnvMan.instance.GetDay(ZNet.instance.GetTimeSeconds());
            }
            catch { return -1; }
        }

        private static string GetServerName() => FiresDiscordIntegration.Utilities.ServerNameResolver.GetOperatorServerName();

        private static string FormatDuration(TimeSpan ts)
        {
            if (ts.TotalDays >= 1)
                return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours}h {ts.Minutes}m";
            return $"{ts.Minutes}m";
        }

        private static void LogVerbose(string msg)
        {
            if (DiscordIntegrationConfig.VerboseLogging != null &&
                DiscordIntegrationConfig.VerboseLogging.Value)
                Debug.Log($"[Discord] {msg}");
        }
    }
}
