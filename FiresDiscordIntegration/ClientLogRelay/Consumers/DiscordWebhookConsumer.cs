using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using FiresCore.ClientLogRelay;
using FiresCore.ClientLogRelay.Interactions;
using FiresDiscordIntegration.ClientLogRelay.Webhook;
using FiresLogAnalysis;
using FiresLogAnalysis.Problems;

namespace FiresDiscordIntegration.ClientLogRelay.Consumers
{
    /// <summary>
    /// Stock consumer that posts a Discord embed snapshot each time a client logs in.
    ///
    /// By default only the summary embed is posted - no file attachments. Three reaction
    /// buttons are pre-added by the bot so admins can request specific artifacts on demand:
    /// <list type="bullet">
    /// <item><c>:envelope_with_arrow:</c> - full BepInEx log</item>
    /// <item><c>:no_entry:</c> - errors + warnings report</item>
    /// <item><c>:jigsaw:</c> - client mod list + client-vs-server diff</item>
    /// </list>
    /// </summary>
    public sealed class DiscordWebhookConsumer : IClientLogConsumer
    {
        // Reaction emoji constants - single source of truth for consumer + poller.
        public const string EMOJI_LOG    = "\uD83D\uDCE9"; // :envelope_with_arrow:
        public const string EMOJI_ERRORS      = "\u26D4";        // :no_entry:
        public const string EMOJI_MODS        = "\uD83E\uDDE9"; // :jigsaw:
        public const string EMOJI_DISCONNECT  = "\u267B\uFE0F"; // :recycle:
        public const string EMOJI_RESTART     = "\uD83D\uDD04"; // :arrows_counterclockwise:

        public string ConsumerId { get; }

        public Func<bool>   EnabledGate;
        public Func<string> WebhookUrl;
        public Func<string> WebhookName;
        public Func<string> BrandLabel;
        public Func<string> AvatarUrl;
        public Func<bool>   AttachFullLog;
        public Func<bool>   AttachModList;
        public Func<bool>   AttachErrorsWarnings;
        public Func<bool>   OnlyIfErrorsOrWarnings;
        public Func<string> ServerName;

        /// <summary>
        /// When true, file attachments are withheld and three reaction buttons are
        /// pre-added so admins can request artifacts on demand.
        /// </summary>
        public Func<bool>   EnableLogRequestReaction;

        /// <summary>
        /// (Optional) Discord bot token. Required for pre-adding reactions and for the
        /// <see cref="Interactions.ReactionPoller"/> to detect admin clicks.
        /// </summary>
        public Func<string> BotToken;

        public DiscordWebhookConsumer(string consumerId)
        {
            ConsumerId = consumerId ?? throw new ArgumentNullException(nameof(consumerId));
        }

        public void OnClientArtifacts(ClientLogArtifacts artifacts)
        {
            // The relay's worker thread runs the fan-out, and posting starts a coroutine, which only the main thread may do.
            if (ClientLogRelayWorker.OnWorkerThread)
            {
                FiresCore.Async.MainThreadDispatcher.Enqueue(() => OnClientArtifacts(artifacts));
                return;
            }

            if (EnabledGate != null && !EnabledGate()) return;

            string url = WebhookUrl?.Invoke();
            if (!MinimalWebhookPoster.IsValidWebhookUrl(url))
                return;

            var analysis = artifacts.LogAnalysis;
            if (OnlyIfErrorsOrWarnings != null && OnlyIfErrorsOrWarnings()
                && !HasErrorsOrWarnings(analysis))
                return;

            int color = EmbedColorFor(analysis);

            string stamp = artifacts.CapturedUtc.ToString("yyyyMMdd_HHmmss");
            string safeId = artifacts.SafePlatformId;
            string brand = BrandLabel?.Invoke();
            bool hasServerMods = artifacts.ServerMods != null && artifacts.ServerMods.Count > 0;
            bool hasDiff = artifacts.ModDiff != null;
            bool enableReaction = EnableLogRequestReaction != null && EnableLogRequestReaction();

            // --- Build embed ---
            string title = hasServerMods
                ? (string.IsNullOrEmpty(brand) ? "\uD83E\uDEB5 Client Login Snapshot" : $"\uD83E\uDEB5 {brand} \u2014 Client Login Snapshot")
                : "\uD83E\uDEB5 Client Login \u2014 Log Snapshot";

            var embed = new MinimalWebhookPoster.Embed()
                .SetTitle(title)
                .SetColor(color)
                .AddField("\uD83D\uDC64 Player",      artifacts.PlayerName,                         true)
                .AddField("\uD83D\uDD94 Steam ID",    artifacts.PlatformId ?? "Unknown",            true)
                .AddField("\uD83E\uDDE9 Client Mods", (artifacts.ModList?.Count ?? 0).ToString(),   true);

            if (hasServerMods)
                embed.AddField("\uD83E\uDDE9 Server Mods", artifacts.ServerMods.Count.ToString(), true);

            if (analysis == null)
                embed.AddField("\u26A0\uFE0F Log Analysis", LogAnalysisFailedFieldValue, true);
            else
                embed.AddField("\uD83D\uDD34 Errors",   analysis.ErrorCount.ToString(),   true)
                    .AddField("\u26A0\uFE0F Warnings", analysis.WarningCount.ToString(), true);

            var modsWithIssues = FindModsWithIssues(analysis, artifacts.ModList);
            if (modsWithIssues.Count > 0)
                embed.AddField("\uD83D\uDCAC Mods w/ Issues", modsWithIssues.Count.ToString(), true);

            embed.AddField("\uD83D\uDCC4 Log Size", FormatBytes(artifacts.LogBytes?.Length ?? 0), true);

            if (analysis != null && analysis.Problems.Count > 0)
                embed.AddField($"\uD83D\uDEA8 Problems ({analysis.Problems.Count})", FormatCappedList(analysis.Problems, FormatProblemLine), false);

            if (modsWithIssues.Count > 0)
                embed.AddField("\u26A0\uFE0F Problem Mods", FormatCappedList(modsWithIssues, FormatProblemModLine), false);

            if (hasDiff)
            {
                var d = artifacts.ModDiff;
                string diffSummary = $"client-only: {d.ClientOnly.Count} server-only: {d.ServerOnly.Count} mismatched: {d.VersionMismatches.Count}";
                embed.AddField("\uD83D\uDD04 Mod Diff (C vs S)", diffSummary, false);

                if (d.VersionMismatches.Count > 0)
                {
                    int clientBehind = 0;
                    int serverBehind = 0;
                    foreach (var m in d.VersionMismatches)
                    {
                        int cmp = CompareVersions(m.ClientVersion, m.ServerVersion);
                        if (cmp < 0) clientBehind++;
                        else if (cmp > 0) serverBehind++;
                        else { clientBehind++; serverBehind++; } // unparseable - flag both
                    }
                    string updateSummary = $"\u2B06\uFE0F Client: {clientBehind} \u2502 \u2B06\uFE0F Server: {serverBehind}";
                    embed.AddField("\uD83D\uDD27 Needs Update", updateSummary, false);
                }
            }

            // --- Footer ---
            string serverName = ServerName?.Invoke();
            string footerLabel = string.IsNullOrEmpty(brand) ? "ClientLogRelay" : brand;
            string footerText;
            if (enableReaction)
            {
                string hint = $"{EMOJI_LOG} Log \u2502 {EMOJI_ERRORS} Errors \u2502 {EMOJI_MODS} Mods \u2502 {EMOJI_DISCONNECT} Disconnect";
                footerText = string.IsNullOrEmpty(serverName)
                    ? hint
                    : $"{footerLabel} \u2014 {serverName}\n{hint}";
            }
            else if (!string.IsNullOrEmpty(serverName))
            {
                footerText = $"{footerLabel} \u2014 {serverName}";
            }
            else
            {
                footerText = footerLabel;
            }
            if (!string.IsNullOrEmpty(footerText)) embed.SetFooter(footerText);

            // --- Attachments (only when reaction mode is OFF) ---
            var files = new List<MinimalWebhookPoster.Attachment>();

            if (!enableReaction)
            {
                if ((AttachFullLog == null || AttachFullLog())
                    && artifacts.LogBytes != null && artifacts.LogBytes.Length > 0)
                {
                    files.Add(new MinimalWebhookPoster.Attachment(
                        $"client_log_{safeId}_{stamp}.log",
                        artifacts.LogBytes, "text/plain"));
                }

                if ((AttachModList == null || AttachModList())
                    && artifacts.ModList != null && artifacts.ModList.Count > 0)
                {
                    files.Add(new MinimalWebhookPoster.Attachment(
                        $"modlist_{safeId}_{stamp}.txt", BuildModListText(artifacts)));
                }

                if ((AttachErrorsWarnings == null || AttachErrorsWarnings())
                    && !string.IsNullOrEmpty(artifacts.ErrorsWarningsReport))
                {
                    files.Add(new MinimalWebhookPoster.Attachment(
                        $"errors_warnings_{safeId}_{stamp}.txt",
                        artifacts.ErrorsWarningsReport));
                }

                if (artifacts.ModDiff != null && !string.IsNullOrEmpty(artifacts.ModDiff.Report))
                {
                    files.Add(new MinimalWebhookPoster.Attachment(
                        $"mod_diff_{safeId}_{stamp}.txt",
                        artifacts.ModDiff.Report));
                }
            }

            // --- Post ---
            string botToken = BotToken?.Invoke();

            MinimalWebhookPoster.Post(url, embed, files,
                WebhookName?.Invoke(), AvatarUrl?.Invoke(),
                onPosted: (messageId, channelId) =>
                {
                    if (enableReaction && !string.IsNullOrEmpty(messageId))
                    {
                        LogRequestRegistry.Register(
                            new LogRequestContext(
                                messageId:   messageId,
                                platformId:  artifacts.PlatformId,
                                playerName:  artifacts.PlayerName,
                                capturedUtc: artifacts.CapturedUtc,
                                consumerId:  ConsumerId,
                                channelId:   channelId));

                        // Pre-add all three reaction buttons sequentially so
                        // Discord doesn't rate-limit and drop any.
                        if (!string.IsNullOrEmpty(botToken) && !string.IsNullOrEmpty(channelId))
                        {
                            MinimalWebhookPoster.AddReactions(botToken, channelId, messageId,
                                EMOJI_LOG, EMOJI_ERRORS, EMOJI_MODS, EMOJI_DISCONNECT);

                            // Clean up old client log messages (keep only the 10 most recent)
                            MinimalWebhookPoster.StartCleanupOldClientLogs(botToken, channelId, keepCount: 10);
                        }
                    }
                });

            Debug.Log($"[ClientLogRelay:{ConsumerId}] Posted login snapshot for {artifacts.PlatformId} " +
                      $"({files.Count} attachment(s), {DescribeIssueCounts(analysis)})");
        }

        internal static string BuildModListText(ClientLogArtifacts artifacts)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Client Mod List \u2014 {artifacts.PlayerName} ({artifacts.PlatformId})");
            sb.AppendLine($"# Captured: {artifacts.CapturedUtc:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"# Count:    {artifacts.ModList.Count}");
            sb.AppendLine();
            foreach (var kv in artifacts.ModList.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append(kv.Key).Append('=').AppendLine(kv.Value);
            return sb.ToString();
        }

        private sealed class ModIssueHit
        {
            public string DisplayName;
            public int    ErrorCount;
            public int    WarningCount;
        }

        private const int ListFieldMaxLines = 5;
        private const int ListLineMaxTextLength = 120;
        private const string ListLineBullet = "\u2022 ";
        private const string TruncatedTextSuffix = "\u2026";
        private const string LogAnalysisFailedFieldValue = "Failed, reason in the errors report";

        private const int ErrorColor   = 15548997;
        private const int WarningColor = 15105570;
        private const int CleanColor   = 5763719;

        private static readonly HashSet<string> SourcesThatAreNotMods =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            string.Empty,
            "Unity Log",
            "BepInEx",
        };

        private static bool HasErrorsOrWarnings(LogAnalysis analysis) =>
            analysis == null
            || analysis.ErrorCount > 0
            || analysis.WarningCount > 0
            || analysis.WorstSeverity >= ProblemSeverity.Warning;

        private static int EmbedColorFor(LogAnalysis analysis)
        {
            if (analysis == null)
                return WarningColor;
            if (analysis.ErrorCount > 0 || analysis.WorstSeverity == ProblemSeverity.Critical)
                return ErrorColor;
            return HasErrorsOrWarnings(analysis) ? WarningColor : CleanColor;
        }

        private static string DescribeIssueCounts(LogAnalysis analysis) =>
            analysis == null
                ? "log analysis failed"
                : $"problems={analysis.Problems.Count} err={analysis.ErrorCount} warn={analysis.WarningCount}";

        private static List<ModIssueHit> FindModsWithIssues(LogAnalysis analysis, IReadOnlyDictionary<string, string> modList)
        {
            if (analysis == null)
                return new List<ModIssueHit>();

            var hitsBySource = new Dictionary<string, ModIssueHit>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in analysis.MessageGroups)
            {
                if (SourcesThatAreNotMods.Contains(group.Source))
                    continue;

                if (!hitsBySource.TryGetValue(group.Source, out var hit))
                {
                    hit = new ModIssueHit { DisplayName = ResolveModDisplayName(group.Source, modList) };
                    hitsBySource.Add(group.Source, hit);
                }

                if (group.IsError)
                    hit.ErrorCount += group.Occurrences;
                else
                    hit.WarningCount += group.Occurrences;
            }

            var hits = new List<ModIssueHit>(hitsBySource.Values);
            hits.Sort(CompareMostErrorsThenMostWarnings);
            return hits;
        }

        private static int CompareMostErrorsThenMostWarnings(ModIssueHit left, ModIssueHit right)
        {
            int byErrors = right.ErrorCount.CompareTo(left.ErrorCount);
            return byErrors != 0 ? byErrors : right.WarningCount.CompareTo(left.WarningCount);
        }

        // Tries to find a mod GUID whose last dot-separated segment matches the
        // source tag after collapsing separators + case.  When found, returns
        // the matched segment ("VAInventory" from "com.Fire.VAInventory") so
        // the embed shows the human-readable mod name.  When no match, returns
        // the raw tag so the user still sees what produced the issues.
        private static string ResolveModDisplayName(string sourceTag, IReadOnlyDictionary<string, string> modList)
        {
            if (modList == null || modList.Count == 0) return sourceTag;
            string normalisedTag = StripSeparatorsAndLower(sourceTag);
            if (normalisedTag.Length == 0) return sourceTag;

            foreach (var modEntry in modList)
            {
                string guid = modEntry.Key;
                if (string.IsNullOrEmpty(guid)) continue;
                int lastDot = guid.LastIndexOf('.');
                string lastSegment = lastDot >= 0 ? guid.Substring(lastDot + 1) : guid;
                if (StripSeparatorsAndLower(lastSegment) == normalisedTag)
                    return lastSegment;
            }
            return sourceTag;
        }

        private static string StripSeparatorsAndLower(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '_' || c == '-' || c == '.' || c == ' ') continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static string FormatProblemModLine(ModIssueHit hit)
        {
            string displayName = TruncateForListLine(hit.DisplayName);
            if (hit.ErrorCount > 0 && hit.WarningCount > 0)
                return $"{ListLineBullet}{displayName} ({AnalysisText.Count(hit.ErrorCount, "error")}, {AnalysisText.Count(hit.WarningCount, "warning")})";
            if (hit.ErrorCount > 0)
                return $"{ListLineBullet}{displayName} ({AnalysisText.Count(hit.ErrorCount, "error")})";
            return $"{ListLineBullet}{displayName} ({AnalysisText.Count(hit.WarningCount, "warning")})";
        }

        private static string FormatProblemLine(Problem problem) =>
            $"[{AnalysisText.SeverityLabel(problem.Severity)}] {TruncateForListLine(problem.Title)}{AnalysisText.Times(problem.Occurrences)}";

        private static string FormatCappedList<T>(IReadOnlyList<T> items, Func<T, string> formatLine)
        {
            var field = new StringBuilder();
            int shownCount = Math.Min(items.Count, ListFieldMaxLines);
            for (int i = 0; i < shownCount; i++)
                field.AppendLine(formatLine(items[i]));
            if (items.Count > shownCount)
                field.AppendLine($"*({items.Count - shownCount} more)*");
            return field.ToString().TrimEnd();
        }

        private static string TruncateForListLine(string text) =>
            text.Length <= ListLineMaxTextLength
                ? text
                : text.Substring(0, ListLineMaxTextLength - TruncatedTextSuffix.Length) + TruncatedTextSuffix;

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return $"{v:0.##} {units[u]}";
        }

        /// <summary>
        /// Compares two dot-separated version strings (e.g. "1.2.3" vs "1.3.0").
        /// Returns &lt; 0 if <paramref name="a"/> is older, &gt; 0 if newer, 0 if equal
        /// or if either string is unparseable.
        /// </summary>
        private static int CompareVersions(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
            var pa = a.Split('.');
            var pb = b.Split('.');
            int len = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < len; i++)
            {
                int va = 0, vb = 0;
                if (i < pa.Length) int.TryParse(pa[i], out va);
                if (i < pb.Length) int.TryParse(pb[i], out vb);
                if (va != vb) return va.CompareTo(vb);
            }
            return 0;
        }
    }
}
