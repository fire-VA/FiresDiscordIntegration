using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using FiresDiscordIntegration.Discord;
using Debug = UnityEngine.Debug;

namespace FiresDiscordIntegration.ClientLogRelay.Interactions
{
    /// <summary>
    /// Server crash detection via a live-updating Discord status message.
    /// Based on the proven VerdantsAscent DiscordCrashDetector implementation.
    /// </summary>
    public static class ServerHeartbeat
    {
        private const int RequestTimeoutSeconds = 15;
        private const int ShortRequestTimeoutSeconds = 10;
        // Pause after every few deletes to stay under Discord's rate limit.
        private const float DeleteBatchPauseSeconds = 0.5f;
        // Gives the status message id time to be set before reactions are seeded.
        private const float SeedReactionsDelaySeconds = 2f;
        // Gives the status message time to be posted and seeded before polling starts.
        private const float ReactionPollStartDelaySeconds = 10f;
        private const float ReactionPollIntervalSeconds = 30f;
        // Lets the status edit and the flag file land before the process quits.
        private const float QuitAfterFlagSeconds = 3f;

        private const string DiscordApiBase = "https://discord.com/api/v10";
        private const float HeartbeatIntervalSeconds = 60f;
        private const string SentinelFileName = "server_heartbeat.json";

        // Sentinel lives at BepInEx/config/FiresDiscordIntegration/server_heartbeat.json.
        // The wrapper bat reads from this path and the watchdog/restart logic depends
        // on it matching what the bat expects.
        private const string SentinelDirName = "FiresDiscordIntegration";

        private const string EMOJI_RESTART = "\uD83D\uDD04";
        private const string EMOJI_STOP = "\u26D4";

        private static string _sentinelPath;
        private static string _statusMessageId;
        private static string _botToken;
        private static string _channelId;
        private static string _statusWebhookUrl;
        private static string _webhookName;
        private static string _brandLabel;
        private static string _botUserId;
        private static Coroutine _heartbeatCoroutine;
        private static MonoBehaviour _host;
        private static DateTime _serverStartUtc;
        private static bool _cleanShutdown;
        private static readonly HashSet<string> _fulfilledReactions = new HashSet<string>(StringComparer.Ordinal);

        internal static bool HeartbeatArmed { get; private set; }
        internal static bool CrashNotifierArmed { get; private set; }
        internal static float IntervalSeconds => HeartbeatIntervalSeconds;

        public static void OnServerStart(string botToken, string statusWebhookUrl,
            string channelIdOverride, string webhookName, string brandLabel)
        {
            _botToken = botToken;
            _statusWebhookUrl = statusWebhookUrl;
            _webhookName = webhookName;
            _brandLabel = brandLabel;
            _channelId = channelIdOverride;

            if (string.IsNullOrEmpty(_botToken) || string.IsNullOrEmpty(_channelId))
            {
                FiresLogger.LogVerbose("[ServerHeartbeat] BotToken or StatusChannelId not set - heartbeat disabled.");
                InitSentinel();
                SpawnCrashNotifier();
                return;
            }
            HeartbeatArmed = true;

            _serverStartUtc = DateTime.UtcNow;
            _cleanShutdown = false;
            _statusMessageId = null;

            InitSentinel();
            EnsureHost();
            if (_host == null) return;

            FiresLogger.LogVerbose($"[ServerHeartbeat] Starting - server start time: {_serverStartUtc:yyyy-MM-dd HH:mm:ss} UTC");

            // Delete old status message (with stale reactions) and post a fresh one
            _host.StartCoroutine(CleanupAndPostNewMessage());

            SpawnCrashNotifier();
            _host.StartCoroutine(ReactionPollLoop());
        }

        public static void OnServerStop()
        {
            _cleanShutdown = true;
            try { WriteSentinel("stopped"); } catch { }

            if (_heartbeatCoroutine != null && _host != null)
            {
                _host.StopCoroutine(_heartbeatCoroutine);
                _heartbeatCoroutine = null;
            }

            // Edit the live panel to Offline.  Either a webhook URL OR
            // (bot token + channel id) is enough - webhook auth lives in
            // the URL itself, so we don't need _botToken in that case.
            // Historically this required both, which is also why the panel
            // never flipped on shutdown when only the webhook was set up.
            bool canEdit = !string.IsNullOrEmpty(_statusMessageId)
                        && (!string.IsNullOrEmpty(_statusWebhookUrl)
                            || (!string.IsNullOrEmpty(_botToken) && !string.IsNullOrEmpty(_channelId)));
            if (canEdit)
            {
                try { EditStatusMessageSync(_statusMessageId, BuildOfflineEmbed()); }
                catch (Exception ex) { Debug.LogWarning($"[ServerHeartbeat] Failed to edit status on shutdown: {ex.Message}"); }
            }
            else
            {
                FiresLogger.LogVerbose("[ServerHeartbeat] OnServerStop: no status message id or auth available \u2014 skipping offline edit.");
            }
        }

        private static IEnumerator CleanupAndPostNewMessage()
        {
            FiresLogger.LogVerbose("[ServerHeartbeat] Cleaning up old status messages...");

            // Fetch recent messages from the status channel
            string url = $"{DiscordApiBase}/channels/{_channelId}/messages?limit=50";

            using (var req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("Authorization", $"Bot {_botToken}");
                req.timeout = RequestTimeoutSeconds;
                yield return req.SendWebRequest();

                if (req.responseCode == 200)
                {
                    JArray messages;
                    try
                    {
                        messages = JArray.Parse(req.downloadHandler.text);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ServerHeartbeat] Failed to parse messages: {ex.Message}");
                        messages = null;
                    }

                    if (messages != null && messages.Count > 0)
                    {
                        int deletedCount = 0;

                        // Look for and delete old server status messages
                        foreach (var msg in messages)
                        {
                            var msgObj = msg as JObject;
                            if (msgObj == null) continue;

                            // Check if this is a bot message
                            var author = msgObj["author"] as JObject;
                            if (author == null) continue;

                            bool isBot = author["bot"]?.Value<bool>() ?? false;
                            if (!isBot) continue;

                            // Check if it has server status embeds
                            var embeds = msgObj["embeds"] as JArray;
                            if (embeds == null || embeds.Count == 0) continue;

                            bool isServerStatus = false;
                            foreach (var embedItem in embeds)
                            {
                                var embedObj = embedItem as JObject;
                                string title = embedObj?["title"]?.ToString() ?? "";

                                if (title.Contains("Server Online") ||
                                    title.Contains("Server Offline") ||
                                    title.Contains("Server Restarting") ||
                                    title.Contains("Server Stopped") ||
                                    title.Contains("Server Crash"))
                                {
                                    isServerStatus = true;
                                    break;
                                }
                            }

                            if (!isServerStatus) continue;

                            // Delete this old status message
                            string messageId = msgObj["id"]?.ToString();
                            if (string.IsNullOrEmpty(messageId)) continue;

                            string deleteUrl = $"{DiscordApiBase}/channels/{_channelId}/messages/{messageId}";
                            using (var deleteReq = UnityWebRequest.Delete(deleteUrl))
                            {
                                deleteReq.SetRequestHeader("Authorization", $"Bot {_botToken}");
                                deleteReq.timeout = ShortRequestTimeoutSeconds;
                                yield return deleteReq.SendWebRequest();

                                if (deleteReq.responseCode == 204)
                                {
                                    deletedCount++;
                                    FiresLogger.LogVerbose($"[ServerHeartbeat] Deleted old status message: {messageId}");
                                }
                            }

                            // Small delay to avoid rate limiting
                            if (deletedCount > 0 && deletedCount % 3 == 0)
                                yield return new WaitForSeconds(DeleteBatchPauseSeconds);
                        }

                        if (deletedCount > 0)
                            FiresLogger.LogVerbose($"[ServerHeartbeat] Cleaned up {deletedCount} old status message(s)");
                    }
                }
                else
                {
                    Debug.LogWarning($"[ServerHeartbeat] Failed to fetch messages ({req.responseCode})");
                }
            }

            // Post fresh status message
            yield return PostStatusMessage();
        }

        private static IEnumerator PostStatusMessage()
        {
            var embed = BuildOnlineEmbed();
            string json = BuildEmbedPayload(embed);

            // Use webhook API instead of bot API to allow custom username/avatar
            string url = !string.IsNullOrEmpty(_statusWebhookUrl)
                ? $"{_statusWebhookUrl}?wait=true"  // Webhook with wait=true returns message object
                : $"{DiscordApiBase}/channels/{_channelId}/messages";  // Fallback to bot API

            using (var req = new UnityWebRequest(url, "POST"))
            {
                byte[] body = Encoding.UTF8.GetBytes(json);
                req.uploadHandler = new UploadHandlerRaw(body);
                req.downloadHandler = new DownloadHandlerBuffer();

                // Only set Authorization header if using bot API (not webhook)
                if (string.IsNullOrEmpty(_statusWebhookUrl))
                {
                    req.SetRequestHeader("Authorization", $"Bot {_botToken}");
                }

                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = RequestTimeoutSeconds;

                yield return req.SendWebRequest();

                if (req.responseCode == 200 || req.responseCode == 201)
                {
                    try
                    {
                        string responseText = req.downloadHandler?.text;
                        if (!string.IsNullOrEmpty(responseText))
                        {
                            var resp = JObject.Parse(responseText);
                            _statusMessageId = resp.Value<string>("id");
                            FiresLogger.LogVerbose($"[ServerHeartbeat] Status message posted to channel {_channelId} (msg ID: {_statusMessageId})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ServerHeartbeat] Posted but failed to parse message ID: {ex.Message}");
                    }
                }
                else
                {
                    string errBody = req.downloadHandler?.text ?? req.error;
                    Debug.LogWarning($"[ServerHeartbeat] Failed to post status message ({req.responseCode}): {errBody}");
                }
            }

            // Start heartbeat loop
            _heartbeatCoroutine = _host.StartCoroutine(HeartbeatLoop());

            // Seed reactions immediately (don't wait for poll loop)
            _host.StartCoroutine(SeedReactions());
        }

        private static IEnumerator SeedReactions()
        {
            // Wait a moment for message ID to be set
            yield return new WaitForSeconds(SeedReactionsDelaySeconds);

            if (string.IsNullOrEmpty(_statusMessageId) || string.IsNullOrEmpty(_channelId))
            {
                Debug.LogWarning("[ServerHeartbeat] Cannot seed reactions - message ID or channel ID missing");
                yield break;
            }

            FiresLogger.LogVerbose($"[ServerHeartbeat] Seeding control reactions on message {_statusMessageId}");
            yield return AddReaction(_channelId, _statusMessageId, EMOJI_RESTART);
            yield return AddReaction(_channelId, _statusMessageId, EMOJI_STOP);
            FiresLogger.LogVerbose("[ServerHeartbeat] Control reactions seeded successfully");
        }

        // A heartbeat edit that fails on Discord's side (no response, 429, or 5xx - "503 upstream connect error ... connection
        // termination" is Discord's edge dropping the request) is retried inside the tick with backoff, honouring Retry-After.
        // The next tick edits again anyway, so one failed tick is not news: a Warning only after HeartbeatWarnAfterFailedTicks
        // failed ticks in a row, then at most once an hour, and one Info line when an edit works again.
        private const int   HeartbeatEditAttempts         = 3;
        private const float HeartbeatRetryBaseSeconds     = 2f;
        private const float HeartbeatRetryMaxSeconds      = 20f;
        private const int   HeartbeatWarnAfterFailedTicks = 3;
        private const float HeartbeatWarnRepeatSeconds    = 3600f;
        private static int    _heartbeatFailedTicks;
        private static string _heartbeatLastError;
        private static float  _heartbeatLastWarnAt = float.NegativeInfinity;
        private static float  _heartbeatHoldUntil;

        private static IEnumerator HeartbeatLoop()
        {
            _heartbeatFailedTicks = 0;
            _heartbeatLastError = null;
            _heartbeatHoldUntil = 0f;
            while (!_cleanShutdown)
            {
                yield return new WaitForSecondsRealtime(HeartbeatIntervalSeconds);
                if (_cleanShutdown) yield break;

                try { WriteSentinel("running"); } catch { }

                if (string.IsNullOrEmpty(_statusMessageId)) continue;
                // A Retry-After longer than a tick: skip the edits it covers.
                if (Time.realtimeSinceStartup < _heartbeatHoldUntil) continue;

                var embed = BuildOnlineEmbed();
                string json = BuildEmbedPayload(embed);
                byte[] body = Encoding.UTF8.GetBytes(json);

                string url = BuildEditUrl(_statusMessageId, out bool needsBotAuth);

                bool ok = false;
                long code = 0;
                string error = null;
                for (int attempt = 0; attempt < HeartbeatEditAttempts; attempt++)
                {
                    float retryAfter = 0f;
                    using (var req = new UnityWebRequest(url, "PATCH"))
                    {
                        req.uploadHandler = new UploadHandlerRaw(body);
                        req.downloadHandler = new DownloadHandlerBuffer();

                        // Only set Authorization header for the bot-API path; the
                        // webhook path is auth'd by its embedded token.
                        if (needsBotAuth)
                            req.SetRequestHeader("Authorization", $"Bot {_botToken}");

                        req.SetRequestHeader("Content-Type", "application/json");
                        req.timeout = ShortRequestTimeoutSeconds;
                        yield return req.SendWebRequest();

                        code = req.responseCode;
                        ok = EditSucceeded(req);
                        if (!ok)
                        {
                            string text;
                            try { text = req.downloadHandler?.text; }
                            catch { text = null; }
                            error = TrimForLog(string.IsNullOrEmpty(text) ? req.error : text);
                            retryAfter = ParseRetryAfter(req, text);
                        }
                    }
                    if (ok || _cleanShutdown) break;

                    bool transient = code == 0 || code == HttpStatusRateLimited || code >= 500;
                    if (!transient || attempt == HeartbeatEditAttempts - 1) break;
                    if (retryAfter > HeartbeatRetryMaxSeconds)
                    {
                        _heartbeatHoldUntil = Time.realtimeSinceStartup + retryAfter;
                        break;
                    }
                    float wait = retryAfter > 0f
                        ? retryAfter + RateLimitWaitBufferSeconds
                        : HeartbeatRetryBaseSeconds * (1 << attempt);
                    FiresLogger.LogVerbose($"[ServerHeartbeat] Heartbeat edit failed ({code}), retry {attempt + 1} in {wait:F1}s");
                    yield return new WaitForSecondsRealtime(wait);
                }
                NoteHeartbeatEdit(ok, code, error);
            }
        }

        private static bool EditSucceeded(UnityWebRequest req)
        {
#if UNITY_2020_1_OR_NEWER
            return req.result == UnityWebRequest.Result.Success;
#else
            return req.result != UnityWebRequest.Result.ConnectionError && req.result != UnityWebRequest.Result.ProtocolError;
#endif
        }

        private static void NoteHeartbeatEdit(bool ok, long code, string error)
        {
            if (ok)
            {
                if (_heartbeatFailedTicks > 0)
                    FiresLogger.LogInfo($"[ServerHeartbeat] Heartbeat edit recovered after {_heartbeatFailedTicks} failed tick(s) (last {_heartbeatLastError})");
                _heartbeatFailedTicks = 0;
                _heartbeatLastError = null;
                return;
            }

            _heartbeatFailedTicks++;
            _heartbeatLastError = $"{code}: {error}";
            float now = Time.realtimeSinceStartup;
            if (_heartbeatFailedTicks >= HeartbeatWarnAfterFailedTicks && now - _heartbeatLastWarnAt >= HeartbeatWarnRepeatSeconds)
            {
                _heartbeatLastWarnAt = now;
                Debug.LogWarning($"[ServerHeartbeat] Heartbeat edit failed {_heartbeatFailedTicks} ticks in a row (last {_heartbeatLastError}); " +
                                 "still retrying every tick, next warning in an hour at the earliest");
            }
            else
            {
                FiresLogger.LogVerbose($"[ServerHeartbeat] Heartbeat edit failed ({_heartbeatLastError}), {_heartbeatFailedTicks} tick(s) in a row");
            }
        }

        // Computes the PATCH endpoint for editing the live status message.
        // When the message was posted via a webhook (_statusWebhookUrl set),
        // we MUST edit it via the same webhook URL - the bot-API
        // /channels/{id}/messages/{id} PATCH endpoint can't author-override
        // a webhook message, so it returns 403 even when the bot user has
        // Manage Messages.  Falls back to the bot endpoint only when no
        // webhook URL is configured (i.e. the original post itself used the
        // bot endpoint).  The out flag tells the caller whether to attach a
        // Bot Authorization header; webhook URLs auth via the token in the
        // path and reject Authorization headers.
        private static string BuildEditUrl(string messageId, out bool needsBotAuth)
        {
            if (!string.IsNullOrEmpty(_statusWebhookUrl))
            {
                // Webhook URL format: https://discord.com/api/webhooks/{id}/{token}
                // We append /messages/{messageId} for an edit.  Defensive
                // split-and-check protects against a malformed URL ending up
                // as a botched PATCH; on a bad URL we fall back to bot API.
                var parts = _statusWebhookUrl.Split(new[] { "/webhooks/" }, StringSplitOptions.None);
                if (parts.Length == 2)
                {
                    needsBotAuth = false;
                    return $"{_statusWebhookUrl}/messages/{messageId}";
                }
            }
            needsBotAuth = true;
            return $"{DiscordApiBase}/channels/{_channelId}/messages/{messageId}";
        }

        private static IEnumerator ReactionPollLoop()
        {
            // Wait for message to be posted and reactions to be seeded
            yield return new WaitForSeconds(ReactionPollStartDelaySeconds);
            yield return ResolveBotUserId();

            string[] emojis = { EMOJI_RESTART, EMOJI_STOP };

            while (!_cleanShutdown)
            {
                yield return new WaitForSecondsRealtime(ReactionPollIntervalSeconds); // Long enough to stay clear of Discord rate limits
                if (_cleanShutdown) yield break;
                if (string.IsNullOrEmpty(_statusMessageId) || string.IsNullOrEmpty(_channelId))
                    continue;

                foreach (string emoji in emojis)
                {
                    string encoded = Uri.EscapeDataString(emoji);
                    string url = $"{DiscordApiBase}/channels/{_channelId}/messages/{_statusMessageId}/reactions/{encoded}?limit={DiscordReactionAllowlist.MaxReactionUsersPerRequest}";

                    using (var req = UnityWebRequest.Get(url))
                    {
                        req.SetRequestHeader("Authorization", $"Bot {_botToken}");
                        req.timeout = ShortRequestTimeoutSeconds;
                        yield return req.SendWebRequest();
                        if (req.responseCode != 200) continue;

                        string body = req.downloadHandler?.text;
                        if (string.IsNullOrEmpty(body)) continue;

                        JArray users;
                        try { users = JArray.Parse(body); }
                        catch { continue; }
                        if (users == null) continue;

                        foreach (var userToken in users)
                        {
                            string userId = (userToken as JObject)?.Value<string>("id");
                            if (string.IsNullOrEmpty(userId)) continue;
                            if (!string.IsNullOrEmpty(_botUserId) && userId == _botUserId) continue;

                            string key = $"{_statusMessageId}:{userId}:{emoji}";
                            if (_fulfilledReactions.Contains(key)) continue;
                            _fulfilledReactions.Add(key);

                            string userName = (userToken as JObject)?["username"]?.ToString() ?? userId;
                            if (!DiscordReactionAllowlist.Allows(userId))
                            {
                                DiscordReactionAllowlist.LogIgnoredReaction(
                                    $"[ServerHeartbeat] {ControlReactionLabel(emoji)} from '{userName}' ({userId})");
                                continue;
                            }

                            FiresLogger.LogVerbose($"[ServerHeartbeat] '{userName}' clicked {emoji}");

                            if (emoji == EMOJI_RESTART)
                            {
                                yield return DoRestart(userName);
                                yield break;
                            }
                            else if (emoji == EMOJI_STOP)
                            {
                                yield return DoStop(userName);
                                yield break;
                            }
                        }
                    }
                }
            }
        }

        private static string ControlReactionLabel(string emoji) => emoji == EMOJI_RESTART ? "RESTART" : "STOP";

        private static IEnumerator ResolveBotUserId()
        {
            string url = $"{DiscordApiBase}/users/@me";
            using (var req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("Authorization", $"Bot {_botToken}");
                req.timeout = ShortRequestTimeoutSeconds;
                yield return req.SendWebRequest();

                if (req.responseCode == 200)
                {
                    try
                    {
                        var json = JObject.Parse(req.downloadHandler.text);
                        _botUserId = json["id"]?.ToString();
                        FiresLogger.LogVerbose($"[ServerHeartbeat] Bot user ID resolved: {_botUserId}");
                    }
                    catch { }
                }
            }
        }

        // Discord's per-route bucket on PUT /reactions/{emoji}/@me 429s back-to-back
        // calls.  When that happens Discord returns the wait duration in the
        // Retry-After header (and the JSON body's retry_after field, which is what
        // shows up in /channels/.../reactions/.../@me responses).  Honor it, retry,
        // give up after MaxReactionAttempts so a persistent failure can't loop the
        // coroutine forever.
        private const int   MaxReactionAttempts            = 3;
        private const int   HttpStatusRateLimited          = 429;
        private const int   HttpStatusSuccessRangeStart    = 200;
        private const int   HttpStatusSuccessRangeEndExcl  = 300;
        private const int   AddReactionTimeoutSeconds      = 10;
        private const int   ResponseBodyLogMaxChars        = 300;
        private const float DefaultRateLimitWaitSeconds    = 1f;
        private const float RateLimitWaitBufferSeconds     = 0.15f;

        private static IEnumerator AddReaction(string channelId, string messageId, string emoji)
        {
            string encoded = Uri.EscapeDataString(emoji);
            string url = $"{DiscordApiBase}/channels/{channelId}/messages/{messageId}/reactions/{encoded}/@me";

            for (int attempt = 0; attempt < MaxReactionAttempts; attempt++)
            {
                long   responseCode;
                string errorMsg;
                string responseBody;
                float  retryAfterSeconds = 0f;

                using (var req = UnityWebRequest.Put(url, Array.Empty<byte>()))
                {
                    req.method = "PUT";
                    req.SetRequestHeader("Authorization", $"Bot {_botToken}");
                    req.timeout = AddReactionTimeoutSeconds;
                    yield return req.SendWebRequest();

                    responseCode = req.responseCode;
                    errorMsg = req.error;
                    try { responseBody = req.downloadHandler?.text; }
                    catch { responseBody = null; }

                    if (responseCode == HttpStatusRateLimited)
                        retryAfterSeconds = ParseRetryAfter(req, responseBody);
                }

                if (responseCode >= HttpStatusSuccessRangeStart && responseCode < HttpStatusSuccessRangeEndExcl)
                    yield break;

                bool canRetry = responseCode == HttpStatusRateLimited
                             && attempt < MaxReactionAttempts - 1;
                if (canRetry)
                {
                    float wait = (retryAfterSeconds > 0f ? retryAfterSeconds : DefaultRateLimitWaitSeconds)
                               + RateLimitWaitBufferSeconds;
                    FiresLogger.LogVerbose($"[ServerHeartbeat] AddReaction {emoji} rate-limited, retrying in {wait:F2}s");
                    yield return new WaitForSeconds(wait);
                    continue;
                }

                Debug.LogWarning($"[ServerHeartbeat] AddReaction {emoji} failed ({responseCode}): " +
                                 $"{errorMsg} | body: {TrimForLog(responseBody)}");
                yield break;
            }
        }

        // Discord's 429 response carries the wait time in two places: the
        // Retry-After response header (seconds, decimal) and a retry_after field
        // in the JSON body.  Either may be missing depending on which proxy hop
        // serialised the response, so check the header first and fall back to
        // the body.  Returns 0 if neither is parseable; the caller substitutes
        // DefaultRateLimitWaitSeconds in that case.
        private static float ParseRetryAfter(UnityWebRequest req, string responseBody)
        {
            string header = req.GetResponseHeader("Retry-After");
            if (!string.IsNullOrEmpty(header) &&
                float.TryParse(header,
                               System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture,
                               out float fromHeader))
            {
                return fromHeader;
            }

            if (string.IsNullOrEmpty(responseBody)) return 0f;
            try
            {
                var json = JObject.Parse(responseBody);
                var node = json["retry_after"];
                return node != null ? node.Value<float>() : 0f;
            }
            catch
            {
                return 0f;
            }
        }

        private static string TrimForLog(string body)
        {
            if (string.IsNullOrEmpty(body)) return "<empty>";
            if (body.Length <= ResponseBodyLogMaxChars) return body;
            return body.Substring(0, ResponseBodyLogMaxChars) + "...";
        }

        private static IEnumerator DoRestart(string reqByName)
        {
            Debug.Log($"[ServerHeartbeat] Server RESTART requested by '{reqByName}'");
            _cleanShutdown = true;
            try { WriteSentinel("restarting"); } catch { }

            var embed = new Dictionary<string, object>
            {
                { "title", "\uD83D\uDD04 Server Restarting..." },
                { "description", $"Restart requested by {reqByName}" },
                { "color", 15105570 }
            };
            yield return EditStatusMessageCoroutine(_statusMessageId, embed);

            WriteFlag("restart_requested.flag", $"Restart requested by {reqByName} at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");

            yield return new WaitForSeconds(QuitAfterFlagSeconds);
            Application.Quit();
        }

        private static IEnumerator DoStop(string reqByName)
        {
            Debug.Log($"[ServerHeartbeat] Server STOP requested by '{reqByName}'");
            _cleanShutdown = true;
            try { WriteSentinel("stopped"); } catch { }

            var embed = new Dictionary<string, object>
            {
                { "title", "\u26D4 Server Stopped" },
                { "description", $"Stop requested by {reqByName}" },
                { "color", 15548997 }
            };
            yield return EditStatusMessageCoroutine(_statusMessageId, embed);

            WriteFlag("stop_requested.flag", $"Stop requested by {reqByName} at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");

            yield return new WaitForSeconds(QuitAfterFlagSeconds);
            Application.Quit();
        }

        private static Dictionary<string, object> BuildOnlineEmbed()
        {
            string serverName = GetServerName();
            int playerCount = 0;
            string playerNames = "*No players online*";

            try
            {
                // Thread-safe: snapshot the peer list to avoid collection modification during iteration
                List<ZNetPeer> peerSnapshot = null;
                if (ZNet.instance != null)
                {
                    var peers = ZNet.instance.GetPeers();
                    if (peers != null && peers.Count > 0)
                    {
                        // Create a snapshot to avoid concurrent modification exceptions
                        peerSnapshot = new List<ZNetPeer>(peers);
                    }
                }

                if (peerSnapshot != null && peerSnapshot.Count > 0)
                {
                    playerCount = peerSnapshot.Count;
                    var names = new List<string>();

                    foreach (var peer in peerSnapshot)
                    {
                        // Extra safety: peer could become null between snapshot and iteration
                        if (peer != null && !string.IsNullOrEmpty(peer.m_playerName))
                        {
                            names.Add("\u2022 " + peer.m_playerName);
                        }
                    }

                    if (names.Count > 0)
                    {
                        playerNames = string.Join("\n", names);
                    }
                }
            }
            catch (Exception ex)
            {
                // Log the error so we can debug if this is the crash source
                Debug.LogWarning($"[ServerHeartbeat] BuildOnlineEmbed exception: {ex.Message}");
            }

            var uptime = DateTime.UtcNow - _serverStartUtc;
            string uptimeStr = uptime.TotalHours >= 1
                ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m"
                : $"{(int)uptime.TotalMinutes}m";

            return new Dictionary<string, object>
            {
                { "title", "\uD83D\uDFE2 Server Online" },
                { "description", $"**{serverName}** is running." },
                { "color", 5763719 },
                { "fields", new List<object>
                    {
                        new Dictionary<string, object> { { "name", $"Players Online ({playerCount})" }, { "value", playerNames }, { "inline", false } },
                        new Dictionary<string, object> { { "name", "Uptime" }, { "value", uptimeStr }, { "inline", true } },
                        new Dictionary<string, object> { { "name", "Last Heartbeat" }, { "value", DateTime.UtcNow.ToString("HH:mm:ss") + " UTC" }, { "inline", true } },
                    }
                },
                { "footer", new Dictionary<string, object> { { "text", $"Updates every 60s \u2014 {_brandLabel ?? "ServerHeartbeat"}" } } }
            };
        }

        private static Dictionary<string, object> BuildOfflineEmbed()
        {
            string serverName = GetServerName();
            var uptime = DateTime.UtcNow - _serverStartUtc;
            string uptimeStr = uptime.TotalHours >= 1
                ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m"
                : $"{(int)uptime.TotalMinutes}m";

            return new Dictionary<string, object>
            {
                { "title", "\uD83D\uDD34 Server Offline" },
                { "description", $"**{serverName}** shut down cleanly." },
                { "color", 15548997 },
                { "fields", new List<object>
                    {
                        new Dictionary<string, object> { { "name", "Session Duration" }, { "value", uptimeStr }, { "inline", true } },
                        new Dictionary<string, object> { { "name", "Stopped At" }, { "value", DateTime.UtcNow.ToString("HH:mm:ss") + " UTC" }, { "inline", true } },
                    }
                },
                { "footer", new Dictionary<string, object> { { "text", _brandLabel ?? "ServerHeartbeat" } } }
            };
        }

        private static string BuildEmbedPayload(Dictionary<string, object> embed)
        {
            // The username/avatar fields only apply when posting via webhook (bot API
            // ignores them and uses the bot's own profile, which is what we actually
            // want for the proper ChatBot icon).  For the webhook fallback we resolve
            // BOTH from the configured Discord bot's actual identity (/users/@me)
            // via DiscordBotIdentity.  Falls back to the explicit WebhookDisplayName
            // and WebhookAvatarUrl config values if the resolver hasn't completed
            // yet (typical for the very first heartbeat post on startup).  This
            // mirrors DiscordIntegrationCore.GetUsername / GetAvatarUrl so every
            // Discord surface in the mod inherits the bot's real name + avatar.
            var payload = new Dictionary<string, object>
            {
                { "embeds", new List<object> { embed } }
            };

            // Username: prefer the bot's actual @me name, then the admin's explicit
            // WebhookDisplayName override, then the brand label, then a final default.
            string explicitName = FiresDiscordIntegration.Discord.DiscordIntegrationConfig.WebhookDisplayName?.Value;
            string fallbackName = !string.IsNullOrEmpty(explicitName) && explicitName != "Valheim Server"
                ? explicitName
                : (_webhookName ?? "Valheim Server");
            string displayName = FiresDiscordIntegration.Discord.DiscordBotIdentity.ResolveUsername(fallback: fallbackName);
            payload["username"] = displayName;

            // Avatar: prefer admin override, then bot's actual avatar.
            string explicitAvatar = FiresDiscordIntegration.Discord.DiscordIntegrationConfig.WebhookAvatarUrl?.Value;
            string avatar = !string.IsNullOrEmpty(explicitAvatar)
                ? explicitAvatar
                : FiresDiscordIntegration.Discord.DiscordBotIdentity.ResolveAvatarUrl(fallback: null);
            if (!string.IsNullOrEmpty(avatar))
                payload["avatar_url"] = avatar;

            return JsonConvert.SerializeObject(payload);
        }

        private static IEnumerator EditStatusMessageCoroutine(string messageId, Dictionary<string, object> embed)
        {
            if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(_channelId))
                yield break;

            string json = BuildEmbedPayload(embed);
            string url  = BuildEditUrl(messageId, out bool needsBotAuth);

            using (var req = new UnityWebRequest(url, "PATCH"))
            {
                byte[] body = Encoding.UTF8.GetBytes(json);
                req.uploadHandler = new UploadHandlerRaw(body);
                req.downloadHandler = new DownloadHandlerBuffer();
                if (needsBotAuth)
                    req.SetRequestHeader("Authorization", $"Bot {_botToken}");
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = ShortRequestTimeoutSeconds;
                yield return req.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
                if (req.result != UnityWebRequest.Result.Success)
#else
                if ((req.result == UnityWebRequest.Result.ConnectionError) || (req.result == UnityWebRequest.Result.ProtocolError))
#endif
                {
                    Debug.LogWarning($"[ServerHeartbeat] Status edit failed ({req.responseCode}): {req.downloadHandler?.text ?? req.error}");
                }
            }
        }

        // Synchronous edit used from OnServerStop / OnApplicationQuit.  Unity
        // is tearing down at that point so a coroutine can't reliably finish
        // - WebClient.UploadString blocks the OnApplicationQuit thread until
        // Discord responds, which is exactly what we want for "make sure
        // the panel says offline before the process exits".  Mirrors
        // BuildEditUrl's routing so a webhook-posted message gets edited via
        // its webhook URL (the bot endpoint 403s on webhook-authored
        // messages, which is why the panel never flipped to Offline in the
        // old codepath).  Errors are logged instead of swallowed so a real
        // failure is visible in the BepInEx log.
        private static void EditStatusMessageSync(string messageId, Dictionary<string, object> embed)
        {
            if (string.IsNullOrEmpty(messageId)) return;

            string json = BuildEmbedPayload(embed);
            string url  = BuildEditUrl(messageId, out bool needsBotAuth);

            try
            {
                using (var client = new System.Net.WebClient())
                {
                    if (needsBotAuth)
                        client.Headers.Add("Authorization", $"Bot {_botToken}");
                    client.Headers.Add("Content-Type", "application/json");
                    client.UploadString(url, "PATCH", json);
                }
                FiresLogger.LogVerbose($"[ServerHeartbeat] Status panel edited synchronously to offline (message {messageId}).");
            }
            catch (System.Net.WebException webEx)
            {
                // Surface the response body - Discord's 4xx errors carry
                // their actual reason there (e.g. "Cannot edit a message
                // authored by another user" for a webhook/bot mix-up), and
                // hiding it made this bug invisible for months.
                string body = "<no response>";
                try
                {
                    using (var resp = webEx.Response)
                    using (var stream = resp?.GetResponseStream())
                    {
                        if (stream != null)
                            using (var reader = new StreamReader(stream))
                                body = reader.ReadToEnd();
                    }
                }
                catch { }
                Debug.LogWarning($"[ServerHeartbeat] Sync status edit failed ({(int)((System.Net.HttpWebResponse)webEx.Response)?.StatusCode}): {body}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ServerHeartbeat] Sync status edit threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void InitSentinel()
        {
            try
            {
                string dir = Path.Combine(BepInEx.Paths.ConfigPath, SentinelDirName);
                Directory.CreateDirectory(dir);
                _sentinelPath = Path.Combine(dir, SentinelFileName);

                WriteSentinel("running");
                FiresLogger.LogVerbose($"[ServerHeartbeat] Sentinel initialised at {_sentinelPath}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ServerHeartbeat] Sentinel init failed: {ex.Message}");
            }
        }

        private static void WriteSentinel(string status)
        {
            if (string.IsNullOrEmpty(_sentinelPath)) return;
            int pid = Process.GetCurrentProcess().Id;
            string messageId = _statusMessageId ?? "";
            string json = $"{{\"status\":\"{status}\",\"heartbeat\":\"{DateTime.UtcNow:O}\",\"pid\":{pid},\"messageId\":\"{messageId}\"}}";
            File.WriteAllText(_sentinelPath, json);
        }

        private static void SpawnCrashNotifier()
        {
            try
            {
                if (string.IsNullOrEmpty(_statusWebhookUrl) || string.IsNullOrEmpty(_sentinelPath))
                    return;

                int pid = Process.GetCurrentProcess().Id;
                string serverName = GetServerName();

                string sSentinel = _sentinelPath.Replace("'", "''");
                string sWebhook = _statusWebhookUrl.Replace("'", "''");
                string sName = serverName.Replace("'", "''").Replace("\"", "\\\"");
                string sDisplay = (_webhookName ?? "Valheim Server").Replace("'", "''");

                string ps =
                    "try{ (Get-Process -Id " + pid + " -EA SilentlyContinue).WaitForExit() }catch{}\n" +
                    "Start-Sleep 3\n" +
                    "$s=Get-Content '" + sSentinel + "' -Raw -EA SilentlyContinue\n" +
                    "if($s -notmatch '\"running\"'){exit}\n" +
                    "$t=(Get-Date).ToUniversalTime().ToString('HH:mm:ss')\n" +
                    "$emoji=[char]::ConvertFromUtf32(0x1F4A5)\n" +
                    "$b=@{username='" + sDisplay + "';embeds=@(@{title=\"\"$emoji Server Crash Detected\"\";description=\"\"**" + sName + "** crashed or was killed.\"\";color=15105570;fields=@(@{name='Detected At';value=\"\"$t UTC\"\";inline=$true});footer=@{text='" + (_brandLabel ?? "ServerHeartbeat") + "'}})}|ConvertTo-Json -Depth 5 -Compress\n" +
                    "try{(New-Object Net.WebClient).UploadString('" + sWebhook + "','POST',$b)|Out-Null}catch{}\n" +
                    "try{Set-Content '" + sSentinel + "' '{\"status\":\"crashed\"}' -EA SilentlyContinue}catch{}";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command -",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                };

                var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.StandardInput.Write(ps);
                    proc.StandardInput.Close();
                    CrashNotifierArmed = true;
                    FiresLogger.LogVerbose($"[ServerHeartbeat] Crash notifier spawned (watchdog PID {proc.Id}, monitoring server PID {pid})");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ServerHeartbeat] Failed to spawn crash notifier: {ex.Message}");
            }
        }

        private static void WriteFlag(string filename, string content)
        {
            try
            {
                // Write to BepInEx\plugins\ root, not the mod subfolder
                string pluginsDir = Path.Combine(BepInEx.Paths.BepInExRootPath, "plugins");
                string flagPath = Path.Combine(pluginsDir, filename);
                File.WriteAllText(flagPath, content);
                FiresLogger.LogVerbose($"[ServerHeartbeat] Wrote flag: {flagPath}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ServerHeartbeat] Failed to write flag '{filename}': {ex.Message}");
            }
        }

        private static string GetServerName() => FiresDiscordIntegration.Utilities.ServerNameResolver.GetOperatorServerName();

        private static void EnsureHost()
        {
            if (_host != null) return;
            var go = new GameObject("ServerHeartbeat_Host");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _host = go.AddComponent<HeartbeatHost>();
        }

        private class HeartbeatHost : MonoBehaviour { }
    }
}
