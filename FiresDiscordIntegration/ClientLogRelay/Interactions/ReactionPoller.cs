using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using FiresCore.ClientLogRelay.Interactions;
using FiresDiscordIntegration.ClientLogRelay.Consumers;
using FiresDiscordIntegration.ClientLogRelay.Webhook;
using FiresDiscordIntegration.Discord;

namespace FiresDiscordIntegration.ClientLogRelay.Interactions
{
    /// <summary>
    /// Periodically polls Discord for new reactions on registered snapshot messages and
    /// posts the requested artifact(s) to the webhook.
    ///
    /// Three reaction types are supported:
    /// <list type="bullet">
    /// <item>(<see cref="DiscordWebhookConsumer.EMOJI_LOG"/>)    - full BepInEx log</item>
    /// <item>(<see cref="DiscordWebhookConsumer.EMOJI_ERRORS"/>) - errors + warnings report</item>
    /// <item>(<see cref="DiscordWebhookConsumer.EMOJI_MODS"/>)   - client mod list + diff</item>
    /// </list>
    ///
    /// Large files (&gt; 8 MB) are automatically split into sequential chunks so they stay
    /// within Discord's webhook upload limit for non-boosted servers.
    /// </summary>
    public sealed class ReactionPoller : MonoBehaviour
    {
        private const int RequestTimeoutSeconds = 15;

        /// <summary>
        /// ConsumerId marking a registered message as a VAngarde server-list card (not a player login
        /// snapshot). A MODS reaction on such a message serves the live four-list dump instead of the
        /// player's cached log artifacts; the other emoji are ignored for it.
        /// </summary>
        public const string ServerListConsumerId = "vangarde_serverlists";

        /// <summary>
        /// Console-safe label for a request emoji so log lines render in any encoding.
        /// The Windows console default (CP437/CP1252) mangles multibyte UTF-8 emoji bytes
        /// (the 4-byte envelope emoji prints as four stray glyphs, the 3-byte no-entry emoji as three). Discord API calls still use the
        /// raw emoji; this helper only affects what's printed to the BepInEx log window.
        /// </summary>
        private static string EmojiLabel(string emoji)
        {
            if (emoji == DiscordWebhookConsumer.EMOJI_LOG)        return "LOG";
            if (emoji == DiscordWebhookConsumer.EMOJI_ERRORS)     return "ERRORS";
            if (emoji == DiscordWebhookConsumer.EMOJI_MODS)       return "MODS";
            if (emoji == DiscordWebhookConsumer.EMOJI_DISCONNECT) return "DISCONNECT";
            if (emoji == DiscordWebhookConsumer.EMOJI_RESTART)    return "RESTART";
            return emoji;
        }

        // The bot polls each tracked message's reactions on a fixed cadence;
        // POLL_INTERVAL_SECONDS is the gap between full cycles.  When Discord
        // returns a 429 we back off for RATE_LIMIT_BACKOFF_SECONDS before the
        // next attempt to avoid getting our IP shadow-banned for a longer
        // stretch.
        //
        // Cadence was tuned to keep us comfortably under Discord's per-message
        // reaction bucket even after the four-emoji pre-react burst that fires
        // when a fresh snapshot lands.  60s between cycles + a 90s per-message
        // warmup (see PostRegistrationWarmupSeconds) effectively means a newly
        // posted message isn't polled until its pre-react bucket has fully
        // recovered, which eliminated the 429 -> 120s backoff loop that the
        // operator's BepInEx log was filling up with.
        private const float POLL_INTERVAL_SECONDS       = 60f;
        private const float RATE_LIMIT_BACKOFF_SECONDS  = 300f;

        // After a snapshot is registered (which fires four PUT /reactions +
        // a DELETE cleanup), skip polling that message until the per-message
        // reaction bucket recovers.  90s is comfortably past Discord's per-
        // bucket reset window for the reactions route - when this guard was
        // absent, the first poll cycle after a fresh login reliably hit 429.
        private static readonly System.TimeSpan PostRegistrationWarmupSeconds
            = System.TimeSpan.FromSeconds(90);

        // PollLoop fires once the bot listener is alive but doesn't start
        // polling until this delay has passed.  Gives the bot user-id resolver
        // time to land its first /users/@me, so the very first PollReaction
        // can already filter out the bot's own reactions instead of treating
        // them as real user requests.
        private const float InitialPollStartupDelaySeconds = 30f;

        // Inside one cycle we walk a list of tracked messages, and for each
        // message we GET the user list for 4 reaction emoji in turn.  Spacing
        // the per-emoji and per-message GETs apart keeps us under Discord's
        // per-route bucket; the numbers were tuned for 10 messages in a 60s
        // cycle.
        private const float InterEmojiPollGapSeconds   = 0.75f;
        private const float InterMessagePollGapSeconds = 3.0f;

        // Cooldown between consecutive webhook POSTs that fire as a result of
        // a reaction (e.g. "send LOG" then "send ERRORS" clicked back-to-back).
        // Discord's webhook bucket is more forgiving than the reactions bucket,
        // so this is shorter than the inter-poll delays.
        private const float PostPublishCooldownSeconds = 1.5f;

        private static int   _consecutiveRateLimits   = 0;
        private static float _lastRateLimitTime       = 0f;

        // Discord webhook file-size ceiling on a non-boosted server is 8 MiB.
        // The multipart envelope (boundary, headers, JSON payload) adds a few
        // hundred bytes on top of the raw attachment; we reserve 64 KiB as a
        // generous margin so the POST never trips the limit even when the
        // embed has long text fields.
        private const int DiscordFreeWebhookFileLimitBytes = 8 * 1024 * 1024;
        private const int MultipartEnvelopeMarginBytes     = 64 * 1024;
        private const int MAX_ATTACHMENT_BYTES = DiscordFreeWebhookFileLimitBytes - MultipartEnvelopeMarginBytes;

        private Func<string> _botTokenResolver;
        private Func<string> _webhookUrlResolver;
        private Func<string> _clientLogsRootResolver;
        private Func<string> _webhookNameResolver;

        private readonly HashSet<string> _fulfilled = new HashSet<string>(StringComparer.Ordinal);

        private string _botUserId;
        private bool _botUserIdResolved;

        // The three emoji we poll for, pre-encoded for the REST URL.
        private static readonly string[] _emojiRaw = {
            DiscordWebhookConsumer.EMOJI_LOG,
            DiscordWebhookConsumer.EMOJI_ERRORS,
            DiscordWebhookConsumer.EMOJI_MODS,
            DiscordWebhookConsumer.EMOJI_DISCONNECT,
        };
        private static readonly string[] _emojiEncoded = _emojiRaw
            .Select(e => Uri.EscapeDataString(e)).ToArray();

        public static ReactionPoller Create(
            Func<string> botTokenResolver,
            Func<string> webhookUrlResolver,
            Func<string> clientLogsRootResolver,
            Func<string> webhookNameResolver = null)
        {
            var go = new GameObject("ClientLogRelay_ReactionPoller");
            DontDestroyOnLoad(go);
            var poller = go.AddComponent<ReactionPoller>();
            poller._botTokenResolver = botTokenResolver;
            poller._webhookUrlResolver = webhookUrlResolver;
            poller._clientLogsRootResolver = clientLogsRootResolver;
            poller._webhookNameResolver = webhookNameResolver;
            return poller;
        }

        private void Start()
        {
            StartCoroutine(PollLoop());
        }

        // ================================================================
        //  Main loop
        // ================================================================
        private IEnumerator PollLoop()
        {
            yield return new WaitForSeconds(InitialPollStartupDelaySeconds);

            while (true)
            {
                // Check if we're in rate limit backoff
                if (_consecutiveRateLimits > 0)
                {
                    float timeSinceLimit = Time.realtimeSinceStartup - _lastRateLimitTime;
                    if (timeSinceLimit < RATE_LIMIT_BACKOFF_SECONDS)
                    {
                        float waitTime = RATE_LIMIT_BACKOFF_SECONDS - timeSinceLimit;
                        Debug.Log($"[ClientLogRelay] ReactionPoller: Rate limited, backing off for {waitTime:F0}s (attempt {_consecutiveRateLimits})");
                        yield return new WaitForSeconds(waitTime);
                    }
                }

                yield return new WaitForSeconds(POLL_INTERVAL_SECONDS);

                string botToken = _botTokenResolver?.Invoke();
                if (string.IsNullOrEmpty(botToken))
                    continue;

                if (!_botUserIdResolved)
                {
                    yield return ResolveBotUserId(botToken);
                    _botUserIdResolved = true;
                }

                var contexts = LogRequestRegistry.Snapshot();
                if (contexts == null || contexts.Length == 0)
                    continue;

                // Poll all messages, but with delays to avoid rate limits
                // With 3s between messages and 0.75s between emojis:
                // 10 messages = ~40 seconds to poll all (within 60s cycle)
                int polledCount = 0;
                int skippedWarmup = 0;

                foreach (var ctx in contexts)
                {
                    if (string.IsNullOrEmpty(ctx.ChannelId) || string.IsNullOrEmpty(ctx.MessageId))
                        continue;

                    // Don't poll a message we (or rather, the bot) JUST pre-
                    // reacted on - the four PUT /reactions/@me calls used up
                    // the per-message reaction bucket and immediately polling
                    // it would trip a 429 -> 120s backoff cycle.  The
                    // reactions persist on Discord; we just defer reading
                    // them until the bucket has recovered.
                    if (LogRequestRegistry.IsWithinRegistrationWindow(
                            ctx.MessageId, PostRegistrationWarmupSeconds))
                    {
                        skippedWarmup++;
                        continue;
                    }

                    // Poll each emoji independently with delays between requests
                    bool hitRateLimit = false;
                    for (int i = 0; i < _emojiRaw.Length; i++)
                    {
                        bool wasRateLimited = false;
                        yield return PollReaction(botToken, ctx, _emojiRaw[i], _emojiEncoded[i], (limited) => wasRateLimited = limited);

                        if (wasRateLimited)
                        {
                            hitRateLimit = true;
                            Debug.Log($"[ClientLogRelay] ReactionPoller: Hit rate limit after {polledCount} messages, pausing for this cycle");
                            break; // Stop polling this message
                        }

                        if (i < _emojiRaw.Length - 1)
                            yield return new WaitForSeconds(InterEmojiPollGapSeconds);
                    }

                    if (hitRateLimit)
                        break; // Stop polling entirely this cycle

                    polledCount++;

                    yield return new WaitForSeconds(InterMessagePollGapSeconds);
                }

                if (polledCount > 0 || skippedWarmup > 0)
                {
                    // Only log when there's actually something to report so the
                    // BepInEx console doesn't gain a metronome tick every 60s
                    // for the lifetime of the server.
                    string suffix = skippedWarmup > 0
                        ? $" (skipped {skippedWarmup} warming-up)"
                        : "";
                    FiresLogger.LogVerbose($"[ClientLogRelay] ReactionPoller: Polled {polledCount} message(s) this cycle{suffix}");
                }
            }
        }

        // ================================================================
        //  Bot identity
        // ================================================================
        private IEnumerator ResolveBotUserId(string botToken)
        {
            using (var req = UnityWebRequest.Get("https://discord.com/api/v10/users/@me"))
            {
                req.SetRequestHeader("Authorization", $"Bot {botToken}");
                req.timeout = RequestTimeoutSeconds;
                yield return req.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
                bool ok = req.result == UnityWebRequest.Result.Success;
#else
                bool ok = !(req.result == UnityWebRequest.Result.ConnectionError) && !(req.result == UnityWebRequest.Result.ProtocolError);
#endif
                if (ok && req.downloadHandler != null)
                {
                    try
                    {
                        var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(req.downloadHandler.text);
                        if (obj != null && obj.TryGetValue("id", out var idObj) && idObj != null)
                        {
                            _botUserId = idObj.ToString();
                            Debug.Log($"[ClientLogRelay] ReactionPoller resolved bot user id: {_botUserId}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ClientLogRelay] ReactionPoller /users/@me parse failed: {ex.Message}");
                    }
                }
                else
                {
                    Debug.LogWarning($"[ClientLogRelay] ReactionPoller /users/@me failed ({req.responseCode}): {req.error}");
                }
            }
        }

        // ================================================================
        //  Poll a single emoji on a single message
        // ================================================================
        private IEnumerator PollReaction(string botToken, LogRequestContext ctx, string emojiRaw, string emojiEncoded, System.Action<bool> rateLimitCallback = null)
        {
            string url = $"https://discord.com/api/v10/channels/{ctx.ChannelId}/messages/{ctx.MessageId}/reactions/{emojiEncoded}?limit={DiscordReactionAllowlist.MaxReactionUsersPerRequest}";
            using (var req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("Authorization", $"Bot {botToken}");
                req.timeout = RequestTimeoutSeconds;
                yield return req.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
                bool ok = req.result == UnityWebRequest.Result.Success;
#else
                bool ok = !(req.result == UnityWebRequest.Result.ConnectionError) && !(req.result == UnityWebRequest.Result.ProtocolError);
#endif
                if (!ok)
                {
                    if (req.responseCode == 429)
                    {
                        // Rate limited - notify caller and track it
                        _consecutiveRateLimits++;
                        _lastRateLimitTime = Time.realtimeSinceStartup;
                        rateLimitCallback?.Invoke(true);
                        Debug.LogWarning($"[ClientLogRelay] ReactionPoller rate limited (429) - backing off (consecutive: {_consecutiveRateLimits})");
                    }
                    else if (req.responseCode != 404)
                    {
                        Debug.LogWarning($"[ClientLogRelay] ReactionPoller GET reactions failed ({req.responseCode}): {req.error}");
                    }
                    yield break;
                }

                // Success - reset rate limit counter
                if (_consecutiveRateLimits > 0)
                {
                    Debug.Log($"[ClientLogRelay] ReactionPoller: Rate limit cleared after {_consecutiveRateLimits} consecutive 429s");
                    _consecutiveRateLimits = 0;
                }
                rateLimitCallback?.Invoke(false);

                JArray users;
                try { users = JArray.Parse(req.downloadHandler.text); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ClientLogRelay] ReactionPoller parse failed: {ex.Message}");
                    yield break;
                }

                foreach (var userToken in users)
                {
                    string userId = userToken["id"]?.ToString();
                    if (string.IsNullOrEmpty(userId)) continue;

                    // Skip the bot's own reaction.
                    if (!string.IsNullOrEmpty(_botUserId) && userId == _botUserId)
                        continue;

                    // Unique key per message + user + emoji so clicking two different
                    // reactions on the same message both get fulfilled.
                    string key = $"{ctx.MessageId}:{userId}:{emojiRaw}";
                    if (_fulfilled.Contains(key))
                        continue;

                    _fulfilled.Add(key);
                    string userName = userToken["username"]?.ToString() ?? userId;
                    // Translate the raw emoji bytes to a readable label so the BepInEx
                    // console window (typically CP437/CP1252 on Windows) doesn't render
                    // multibyte UTF-8 emoji as one stray glyph per byte. The actual
                    // emojiRaw still goes to the Discord API on the fulfill side.
                    string emojiLabel = EmojiLabel(emojiRaw);
                    if (!IsRequestAllowed(ctx, userId))
                    {
                        DiscordReactionAllowlist.LogIgnoredReaction(
                            $"[ClientLogRelay] ReactionPoller: {emojiLabel} from '{userName}' ({userId}) on {ctx.PlayerName} ({ctx.PlatformId})");
                        continue;
                    }

                    Debug.Log($"[ClientLogRelay] ReactionPoller: '{userName}' reacted {emojiLabel} on {ctx.PlayerName} ({ctx.PlatformId})");

                    yield return Fulfill(ctx, emojiRaw, userName, userId);
                }
            }
        }

        private static bool IsRequestAllowed(LogRequestContext ctx, string userId)
            => ctx.ConsumerId == ServerListConsumerId || DiscordReactionAllowlist.Allows(userId);

        // ================================================================
        //  Dispatch by emoji
        // ================================================================
        private IEnumerator Fulfill(LogRequestContext ctx, string emoji, string reqByName, string reqById)
        {
            // Server-list card: serves live list data via the FUC snapshot provider - no player folder,
            // and only the MODS emoji is meaningful on it.
            if (ctx.ConsumerId == ServerListConsumerId)
            {
                if (emoji == DiscordWebhookConsumer.EMOJI_MODS)
                    yield return FulfillServerLists(ctx, reqByName);
                yield break;
            }

            string logsRoot = _clientLogsRootResolver?.Invoke();
            if (string.IsNullOrEmpty(logsRoot)) yield break;

            string folder = BuildPlayerFolder(logsRoot, ctx);

            if (emoji == DiscordWebhookConsumer.EMOJI_LOG)
                yield return FulfillFullLog(ctx, folder, reqByName);
            else if (emoji == DiscordWebhookConsumer.EMOJI_ERRORS)
                yield return FulfillErrorsWarnings(ctx, folder, reqByName);
            else if (emoji == DiscordWebhookConsumer.EMOJI_MODS)
                yield return FulfillMods(ctx, folder, reqByName);
            else if (emoji == DiscordWebhookConsumer.EMOJI_DISCONNECT)
                FulfillDisconnect(ctx, reqByName);
        }

        // ================================================================
        //  Full log
        // ================================================================
        private IEnumerator FulfillFullLog(LogRequestContext ctx, string folder, string reqByName)
        {
            string logPath = Path.Combine(folder, "LogOutput.log");
            byte[] logBytes = SafeReadFile(logPath);
            if (logBytes == null || logBytes.Length == 0)
            {
                Debug.LogWarning($"[ClientLogRelay] ReactionPoller: no cached log for {ctx.PlatformId} at '{logPath}'");
                yield break;
            }

            string stamp = ctx.CapturedUtc.ToString("yyyyMMdd_HHmmss");
            string safeId = SafePlatformId(ctx);

            // Chunk if necessary.
            var chunks = ChunkBytes(logBytes, MAX_ATTACHMENT_BYTES);
            for (int i = 0; i < chunks.Count; i++)
            {
                string suffix = chunks.Count == 1 ? "" : $"_part{i + 1}of{chunks.Count}";
                string fileName = $"client_log_{safeId}_{stamp}{suffix}.log";
                string title = chunks.Count == 1
                    ? "\uD83D\uDCE9 Full Log \u2014 Requested"
                    : $"\uD83D\uDCE9 Full Log \u2014 Part {i + 1}/{chunks.Count}";

                var embed = new MinimalWebhookPoster.Embed()
                    .SetTitle(title)
                    .SetColor(3447003)
                    .AddField("\uD83D\uDC64 Player",   ctx.PlayerName,           true)
                    .AddField("\uD83D\uDD94 Steam ID", ctx.PlatformId ?? "?",    true)
                    .AddField("\uD83D\uDCC4 Size",     FormatBytes(chunks[i].Length), true)
                    .SetFooter($"Requested by {reqByName}");

                var files = new List<MinimalWebhookPoster.Attachment>
                {
                    new MinimalWebhookPoster.Attachment(fileName, chunks[i], "text/plain")
                };

                yield return PostAndWait(embed, files);
            }

            Debug.Log($"[ClientLogRelay] ReactionPoller: posted full log for {ctx.PlatformId} " +
                      $"({logBytes.Length} bytes, {chunks.Count} part(s)), requested by {reqByName}");
        }

        // ================================================================
        //  Errors + warnings
        // ================================================================
        private IEnumerator FulfillErrorsWarnings(LogRequestContext ctx, string folder, string reqByName)
        {
            string path = Path.Combine(folder, "errors_warnings.txt");
            byte[] bytes = SafeReadFile(path);
            if (bytes == null || bytes.Length == 0)
            {
                Debug.LogWarning($"[ClientLogRelay] ReactionPoller: no errors_warnings.txt for {ctx.PlatformId}");
                yield break;
            }

            string stamp = ctx.CapturedUtc.ToString("yyyyMMdd_HHmmss");
            string safeId = SafePlatformId(ctx);

            var embed = new MinimalWebhookPoster.Embed()
                .SetTitle("\u26D4 Errors + Warnings \u2014 Requested")
                .SetColor(15548997)
                .AddField("\uD83D\uDC64 Player",   ctx.PlayerName,        true)
                .AddField("\uD83D\uDD94 Steam ID", ctx.PlatformId ?? "?", true)
                .AddField("\uD83D\uDCC4 Size",     FormatBytes(bytes.Length), true)
                .SetFooter($"Requested by {reqByName}");

            var files = new List<MinimalWebhookPoster.Attachment>
            {
                new MinimalWebhookPoster.Attachment($"errors_warnings_{safeId}_{stamp}.txt", bytes, "text/plain")
            };

            yield return PostAndWait(embed, files);
            Debug.Log($"[ClientLogRelay] ReactionPoller: posted errors/warnings for {ctx.PlatformId}, requested by {reqByName}");
        }

        // ================================================================
        //  Mod list + diff
        // ================================================================
        private IEnumerator FulfillMods(LogRequestContext ctx, string folder, string reqByName)
        {
            string stamp = ctx.CapturedUtc.ToString("yyyyMMdd_HHmmss");
            string safeId = SafePlatformId(ctx);

            byte[] modListBytes = SafeReadFile(Path.Combine(folder, "modlist.txt"));
            byte[] modDiffBytes = SafeReadFile(Path.Combine(folder, "mod_diff.txt"));

            bool hasModList = modListBytes != null && modListBytes.Length > 0;
            bool hasDiff = modDiffBytes != null && modDiffBytes.Length > 0;
            if (!hasModList && !hasDiff)
            {
                Debug.LogWarning($"[ClientLogRelay] ReactionPoller: no mod artifacts for {ctx.PlatformId}");
                yield break;
            }

            var embed = new MinimalWebhookPoster.Embed()
                .SetTitle("\uD83E\uDDE9 Mod Lists \u2014 Requested")
                .SetColor(5763719)
                .AddField("\uD83D\uDC64 Player",   ctx.PlayerName,        true)
                .AddField("\uD83D\uDD94 Steam ID", ctx.PlatformId ?? "?", true)
                .SetFooter($"Requested by {reqByName}");

            var files = new List<MinimalWebhookPoster.Attachment>();
            if (hasModList)
                files.Add(new MinimalWebhookPoster.Attachment($"modlist_{safeId}_{stamp}.txt", modListBytes, "text/plain"));
            if (hasDiff)
                files.Add(new MinimalWebhookPoster.Attachment($"mod_diff_{safeId}_{stamp}.txt", modDiffBytes, "text/plain"));

            yield return PostAndWait(embed, files);
            Debug.Log($"[ClientLogRelay] ReactionPoller: posted mod artifacts for {ctx.PlatformId}, requested by {reqByName}");
        }

        // ================================================================
        //  Disconnect capture
        // ================================================================
        private void FulfillDisconnect(LogRequestContext ctx, string reqByName)
        {
            // Just register the player - the actual log post happens when they disconnect
            // (handled by the Harmony patch in ClientLogRelayIntegration).
            DisconnectLogRegistry.Register(ctx.PlatformId, ctx.PlayerName, reqByName);
        }

        // ================================================================
        //  Server mod lists (VAngarde list-card messages)
        // ================================================================
        private IEnumerator FulfillServerLists(LogRequestContext ctx, string reqByName)
        {
            // Live dump from the anti-cheat mod via the FUC bridge; null when VAngarde isn't installed.
            string text = FiresCore.Bridge.DiscordSink.GetServerListSnapshot();
            if (string.IsNullOrEmpty(text))
            {
                Debug.LogWarning("[ClientLogRelay] ReactionPoller: no server-list snapshot provider available");
                yield break;
            }

            var embed = new MinimalWebhookPoster.Embed()
                .SetTitle("\uD83E\uDDE9 Server Mod Lists \u2014 Requested")
                .SetColor(5763719)
                .SetDescription("Current enforced / whitelist / server-only / blacklist state, live from the server.")
                .SetFooter($"Requested by {reqByName}");

            var files = new List<MinimalWebhookPoster.Attachment>
            {
                new MinimalWebhookPoster.Attachment("vangarde_server_lists.txt", text, "text/plain")
            };

            // Post to the anti-cheat channel (where the list card lives), not the client-log webhook.
            string url = FiresDiscordIntegration.Discord.DiscordIntegrationConfig.ResolveWebhookUrl(
                FiresDiscordIntegration.Discord.WebhookCategory.AntiCheat);
            if (string.IsNullOrEmpty(url) || !MinimalWebhookPoster.IsValidWebhookUrl(url))
            {
                url = _webhookUrlResolver?.Invoke();
                if (string.IsNullOrEmpty(url) || !MinimalWebhookPoster.IsValidWebhookUrl(url)) yield break;
            }

            MinimalWebhookPoster.Post(url, embed, files, _webhookNameResolver?.Invoke(), null);
            yield return new WaitForSeconds(PostPublishCooldownSeconds);
            Debug.Log($"[ClientLogRelay] ReactionPoller: posted server mod lists, requested by {reqByName}");
        }

        // ================================================================
        // ================================================================
        //  Helpers
        // ================================================================
        private IEnumerator PostAndWait(MinimalWebhookPoster.Embed embed, List<MinimalWebhookPoster.Attachment> files)
        {
            string webhookUrl = _webhookUrlResolver?.Invoke();
            if (string.IsNullOrEmpty(webhookUrl) || !MinimalWebhookPoster.IsValidWebhookUrl(webhookUrl))
                yield break;

            MinimalWebhookPoster.Post(webhookUrl, embed, files,
                _webhookNameResolver?.Invoke(), null);

            yield return new WaitForSeconds(PostPublishCooldownSeconds);
        }

        private static string BuildPlayerFolder(string logsRoot, LogRequestContext ctx)
        {
            string safePid = (ctx.PlatformId ?? "unknown").Replace(":", "_").Replace("/", "_").Replace("\\", "_");
            string safeName = ctx.PlayerName ?? "unknown";
            if (!string.IsNullOrEmpty(safeName))
            {
                var invalid = Path.GetInvalidFileNameChars();
                safeName = string.Concat(safeName.Split(invalid));
            }
            return Path.Combine(logsRoot, $"{safeName}_{safePid}");
        }

        private static string SafePlatformId(LogRequestContext ctx)
            => (ctx.PlatformId ?? "unknown").Replace(":", "_").Replace("/", "_").Replace("\\", "_");

        private static byte[] SafeReadFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ClientLogRelay] ReactionPoller: read failed '{path}': {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Splits <paramref name="data"/> into chunks of at most <paramref name="maxBytes"/>.
        /// Returns a single-element list when the data already fits.
        /// </summary>
        private static List<byte[]> ChunkBytes(byte[] data, int maxBytes)
        {
            if (data == null || data.Length == 0)
                return new List<byte[]>(0);

            if (data.Length <= maxBytes)
                return new List<byte[]>(1) { data };

            var chunks = new List<byte[]>();
            int offset = 0;
            while (offset < data.Length)
            {
                int len = Math.Min(maxBytes, data.Length - offset);
                var chunk = new byte[len];
                Buffer.BlockCopy(data, offset, chunk, 0, len);
                chunks.Add(chunk);
                offset += len;
            }
            return chunks;
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
    }
}
