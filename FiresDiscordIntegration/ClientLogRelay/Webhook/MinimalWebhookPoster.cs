using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace FiresDiscordIntegration.ClientLogRelay.Webhook
{
    /// <summary>
    /// Minimal, self-contained Discord webhook poster used exclusively by the
    /// <see cref="Consumers.DiscordWebhookConsumer"/>. Intentionally NOT shared with any other
    /// Discord infrastructure so this module stays copy-pasteable into other mods.
    ///
    /// Supports one operation: <c>POST multipart/form-data</c> with an embed JSON payload
    /// plus zero or more file attachments. That is all the client-log pipeline needs.
    ///
    /// Zero dependencies beyond <c>UnityEngine</c>, <c>UnityEngine.Networking</c>, and
    /// <c>Newtonsoft.Json</c> (shipped with every Valheim BepInEx install).
    /// </summary>
    public static class MinimalWebhookPoster
    {
        private const int UploadTimeoutSeconds = 30;
        private const int RequestTimeoutSeconds = 15;
        private const int DeleteTimeoutSeconds = 10;

        private const string BoundaryPrefix = "------ClientLogRelayBoundary";

        // Discord enforces a per-route rate limit on PUT /reactions/{emoji}/@me and
        // on DELETE /messages/{id}; back-to-back calls within the same bucket
        // get 429'd.  These delays space the calls just enough to stay under
        // the bucket without making the pre-react sequence feel sluggish.
        private const float ReactionPutCooldownSeconds  = 0.5f;
        private const float MessageDeleteCooldownSeconds = 0.5f;

        // Wait between posting the embed and starting the cleanup-old-messages
        // sweep.  Discord's eventual consistency means a freshly-posted message
        // may not be in the channel listing immediately; this gives Discord a
        // moment to settle so the cleanup sweep sees the new message and
        // counts it among the "keep most recent" set instead of deleting it.
        private const float PostMessageCleanupDelaySeconds = 2f;

        private static CoroutineHost _host;

        public sealed class Embed
        {
            public string Title;
            public string Description;
            public int    Color = 3447003; // Discord "blue"
            public string Footer;
            public string ImageUrl; // supports "attachment://<filename>" bound to a same-post file
            public readonly List<(string name, string value, bool inline)> Fields = new List<(string, string, bool)>();

            public Embed SetTitle(string title) { Title = title; return this; }
            public Embed SetDescription(string desc) { Description = desc; return this; }
            public Embed SetColor(int rgbInt) { Color = rgbInt; return this; }
            public Embed SetFooter(string footer) { Footer = footer; return this; }
            public Embed SetImage(string url) { ImageUrl = url; return this; }
            public Embed AddField(string name, string value, bool inline = false)
            {
                Fields.Add((name, value, inline));
                return this;
            }
        }

        public sealed class Attachment
        {
            public string FileName;
            public byte[] Content;
            public string ContentType = "text/plain";

            public Attachment(string fileName, byte[] bytes, string contentType = "text/plain")
            {
                FileName = fileName; Content = bytes ?? Array.Empty<byte>(); ContentType = contentType;
            }
            public Attachment(string fileName, string text, string contentType = "text/plain")
                : this(fileName, Encoding.UTF8.GetBytes(text ?? string.Empty), contentType) { }
        }

        // No URL means relay posts are off: one Debug line, once. A URL that is set but malformed is a config mistake:
        // one Warning per distinct value, naming only its length and start (the rest of a webhook URL is its token).
        private static bool s_noUrlNoted;
        private static readonly HashSet<string> s_badUrlsWarned = new HashSet<string>(StringComparer.Ordinal);
        private const int UrlHintLength = 24;

        private static void NoteSkippedPost(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                if (s_noUrlNoted)
                    return;
                s_noUrlNoted = true;
                FiresLogger.LogDebug("[ClientLogRelay] No webhook URL set: relay posts are off.");
                return;
            }
            if (!s_badUrlsWarned.Add(url))
                return;
            string hint = url.Length > UrlHintLength ? url.Substring(0, UrlHintLength) + "..." : url;
            FiresLogger.LogWarning($"[ClientLogRelay] Webhook URL is not a Discord webhook (\"{hint}\", {url.Length} characters); "
                + "posts to it are skipped. Expected https://discord.com/api/webhooks/... or a discord-bot-channel:<id> target.");
        }

        public static bool IsValidWebhookUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.Ordinal))
                return false;
            return url.IndexOf("discord.com/api/webhooks/", StringComparison.Ordinal) >= 0
                || url.IndexOf("discordapp.com/api/webhooks/", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// Fire-and-forget POST. Kept for callers that don't need the message id back.
        /// </summary>
        public static void Post(string webhookUrl, Embed embed, List<Attachment> files,
            string username = null, string avatarUrl = null)
        {
            Post(webhookUrl, embed, files, username, avatarUrl, onPosted: null);
        }

        /// <summary>
        /// POST and invoke <paramref name="onPosted"/> on the main thread once Discord
        /// confirms receipt. The callback receives the Discord message snowflake id and
        /// channel id so callers can wire it into <c>LogRequestRegistry</c> and add
        /// reactions. Either value may be <c>null</c> if the POST failed or the response
        /// didn't include them.
        ///
        /// Internally the webhook URL is upgraded to <c>?wait=true</c> so Discord returns
        /// the created message as JSON; otherwise Discord replies 204 with no body.
        /// </summary>
        public static void Post(string webhookUrl, Embed embed, List<Attachment> files,
            string username, string avatarUrl, Action<string, string> onPosted)
        {
            // A "discord-bot-channel:<id>" target (test shots) posts AS THE BOT to that channel: the bot
            // API has no username/avatar override, so those are dropped.
            string botChannel = null;
            if (FiresDiscordIntegration.Discord.BotChannelTarget.TryParse(webhookUrl, out botChannel))
            {
                username = null;
                avatarUrl = null;
            }
            else if (!IsValidWebhookUrl(webhookUrl))
            {
                NoteSkippedPost(webhookUrl);
                onPosted?.Invoke(null, null);
                return;
            }

            // The body (the embed's JSON plus every attachment's bytes) is built on the relay's worker thread: building a
            // screenshot post on the main thread cost a server frame tens of ms. The request still starts on the main
            // thread, a frame or two later, in the order the posts were made.
            ClientLogRelayWorker.Prepare();
            ClientLogRelayWorker.Enqueue(() =>
            {
                string boundary = BoundaryPrefix + Guid.NewGuid().ToString("N");
                byte[] body = null;
                try { body = BuildMultipart(boundary, embed, files, username, avatarUrl); }
                catch (Exception ex) { FiresLogger.LogWarning($"[ClientLogRelay] Webhook body build failed: {ex.Message}"); }

                FiresCore.Async.MainThreadDispatcher.Enqueue(() =>
                {
                    if (body == null)
                    {
                        onPosted?.Invoke(null, null);
                        return;
                    }
                    EnsureHost();
                    _host.StartCoroutine(PostCoroutine(webhookUrl, boundary, body, onPosted, botChannel));
                });
            });
        }

        private static IEnumerator PostCoroutine(string webhookUrl, string boundary, byte[] body,
            Action<string, string> onPosted, string botChannel = null)
        {

            // Always request ?wait=true so Discord returns the full message object with
            // its id. A few extra bytes in the response body, no observable latency
            // difference, and required for the reaction-based log-request feature to
            // know which snapshot message a reaction is attached to. (The bot API always returns the message.)
            string url = botChannel != null
                ? FiresDiscordIntegration.Discord.BotChannelTarget.MessagesUrl(botChannel)
                : webhookUrl.IndexOf('?') >= 0
                    ? webhookUrl + "&wait=true"
                    : webhookUrl + "?wait=true";

            using (var req = new UnityWebRequest(url, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(body);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "multipart/form-data; boundary=" + boundary);
                if (botChannel != null) req.SetRequestHeader("Authorization", FiresDiscordIntegration.Discord.BotChannelTarget.AuthHeader());
                req.timeout = UploadTimeoutSeconds;

                yield return req.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
                bool ok = req.result == UnityWebRequest.Result.Success;
#else
                bool ok = !(req.result == UnityWebRequest.Result.ConnectionError) && !(req.result == UnityWebRequest.Result.ProtocolError);
#endif
                if (!ok)
                {
                    // Surface Discord's response body so 400s (which usually mean a payload
                    // shape issue) reveal their actual cause. UnityWebRequest.error is just
                    // the HTTP status string ("HTTP/1.1 400 Bad Request") — the JSON body
                    // contains the real diagnosis (e.g. "Invalid Form Body", which fields).
                    string respBody = null;
                    try { respBody = req.downloadHandler?.text; } catch { /* best-effort */ }
                    string trimmed = string.IsNullOrEmpty(respBody) ? "<empty>"
                        : (respBody.Length > 500 ? respBody.Substring(0, 500) + "…" : respBody);
                    Debug.LogWarning(botChannel != null
                        ? $"[Discord] test shot to channel {FiresDiscordIntegration.Discord.BotChannelTarget.Short(botChannel)} (bot) FAILED ({req.responseCode}): {req.error} | body: {trimmed}"
                        : $"[ClientLogRelay] Webhook POST failed ({req.responseCode}): {req.error} | body: {trimmed}");
                    onPosted?.Invoke(null, null);
                    yield break;
                }
                if (botChannel != null)
                    Debug.Log($"[Discord] test shot posted to channel {FiresDiscordIntegration.Discord.BotChannelTarget.Short(botChannel)} (bot)");

                string messageId = null;
                string channelId = null;
                try
                {
                    string respText = req.downloadHandler?.text;
                    if (!string.IsNullOrEmpty(respText))
                    {
                        var obj = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(respText);
                        if (obj != null)
                        {
                            if (obj.TryGetValue("id", out var idObj) && idObj != null)
                                messageId = idObj.ToString();
                            if (obj.TryGetValue("channel_id", out var chObj) && chObj != null)
                                channelId = chObj.ToString();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ClientLogRelay] Webhook response parse failed: {ex.Message}");
                }

                try { onPosted?.Invoke(messageId, channelId); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ClientLogRelay] onPosted callback threw: {ex.Message}");
                }
            }
        }

        private static byte[] BuildMultipart(string boundary, Embed embed, List<Attachment> files,
            string username, string avatarUrl)
        {
            var payload = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(username))  payload["username"] = username;
            if (!string.IsNullOrEmpty(avatarUrl)) payload["avatar_url"] = avatarUrl;

            if (embed != null)
            {
                var embedObj = new Dictionary<string, object>();
                if (!string.IsNullOrEmpty(embed.Title))       embedObj["title"] = embed.Title;
                if (!string.IsNullOrEmpty(embed.Description)) embedObj["description"] = embed.Description;
                embedObj["color"] = embed.Color;
                if (!string.IsNullOrEmpty(embed.Footer))
                    embedObj["footer"] = new Dictionary<string, object> { ["text"] = embed.Footer };
                if (!string.IsNullOrEmpty(embed.ImageUrl))
                    embedObj["image"] = new Dictionary<string, object> { ["url"] = embed.ImageUrl };

                if (embed.Fields.Count > 0)
                {
                    var fieldList = new List<object>(embed.Fields.Count);
                    foreach (var f in embed.Fields)
                    {
                        fieldList.Add(new Dictionary<string, object>
                        {
                            ["name"]   = f.name ?? string.Empty,
                            ["value"]  = string.IsNullOrEmpty(f.value) ? "-" : f.value,
                            ["inline"] = f.inline,
                        });
                    }
                    embedObj["fields"] = fieldList;
                }

                payload["embeds"] = new[] { embedObj };
            }

            string payloadJson = JsonConvert.SerializeObject(payload);

            byte[] boundaryBytes = Encoding.UTF8.GetBytes("--" + boundary + "\r\n");
            byte[] newline       = Encoding.UTF8.GetBytes("\r\n");
            byte[] closing       = Encoding.UTF8.GetBytes("--" + boundary + "--\r\n");

            // The parts are collected first so the body is ONE exact-size array: a growing MemoryStream plus its
            // final ToArray allocated about three times the attachment size per post.
            var parts = new List<byte[]>(6 + (files?.Count ?? 0) * 4);

            // payload_json part
            parts.Add(boundaryBytes);
            parts.Add(Encoding.UTF8.GetBytes(
                "Content-Disposition: form-data; name=\"payload_json\"\r\n" +
                "Content-Type: application/json\r\n\r\n"));
            parts.Add(Encoding.UTF8.GetBytes(payloadJson));
            parts.Add(newline);

            if (files != null)
            {
                for (int i = 0; i < files.Count; i++)
                {
                    var f = files[i];
                    parts.Add(boundaryBytes);
                    string header =
                        $"Content-Disposition: form-data; name=\"files[{i}]\"; filename=\"{f.FileName}\"\r\n" +
                        $"Content-Type: {f.ContentType}\r\n\r\n";
                    parts.Add(Encoding.UTF8.GetBytes(header));
                    parts.Add(f.Content);
                    parts.Add(newline);
                }
            }

            parts.Add(closing);

            int total = 0;
            foreach (var part in parts) total += part.Length;
            var body = new byte[total];
            int offset = 0;
            foreach (var part in parts)
            {
                Buffer.BlockCopy(part, 0, body, offset, part.Length);
                offset += part.Length;
            }
            return body;
        }

        /// <summary>
        /// PUTs a single reaction on an existing Discord message using a bot token.
        /// </summary>
        public static void AddReaction(string botToken, string channelId, string messageId, string emoji)
        {
            if (string.IsNullOrEmpty(botToken) || string.IsNullOrEmpty(channelId)
                || string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(emoji))
                return;

            EnsureHost();
            _host.StartCoroutine(AddReactionCoroutine(botToken, channelId, messageId, emoji));
        }

        /// <summary>
        /// Adds multiple reactions to a message **sequentially** in a single coroutine,
        /// with a delay between each PUT to stay within Discord's per-route rate limit.
        /// Use this instead of calling <see cref="AddReaction"/> in a loop - parallel
        /// coroutines will race and Discord will 429-reject all but the first.
        /// </summary>
        public static void AddReactions(string botToken, string channelId, string messageId, params string[] emojis)
        {
            if (string.IsNullOrEmpty(botToken) || string.IsNullOrEmpty(channelId)
                || string.IsNullOrEmpty(messageId) || emojis == null || emojis.Length == 0)
                return;

            EnsureHost();
            _host.StartCoroutine(AddReactionsCoroutine(botToken, channelId, messageId, emojis));
        }

        private static IEnumerator AddReactionsCoroutine(string botToken, string channelId, string messageId, string[] emojis)
        {
            for (int i = 0; i < emojis.Length; i++)
            {
                if (string.IsNullOrEmpty(emojis[i])) continue;
                yield return AddReactionCoroutine(botToken, channelId, messageId, emojis[i]);
            }
        }

        private static IEnumerator AddReactionCoroutine(string botToken, string channelId, string messageId, string emoji)
        {
            string encodedEmoji = Uri.EscapeDataString(emoji);
            string url = $"https://discord.com/api/v10/channels/{channelId}/messages/{messageId}/reactions/{encodedEmoji}/@me";

            using (var req = new UnityWebRequest(url, "PUT"))
            {
                // PUT with no body - skip uploadHandler entirely to avoid the
                // "Content-Length is managed automatically" warning.
                req.downloadHandler = new DownloadHandlerBuffer();
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
                    Debug.LogWarning($"[ClientLogRelay] AddReaction failed ({req.responseCode}): {req.error}");
                }
                else
                {
                    Debug.Log($"[ClientLogRelay] Bot pre-reacted with {emoji} on message {messageId}");
                }
            }

            yield return new WaitForSeconds(ReactionPutCooldownSeconds);
        }

        /// <summary>
        /// Starts a background coroutine to clean up old client log messages in the specified
        /// channel, keeping only the most recent <paramref name="keepCount"/> messages.
        /// This ensures the ReactionPoller only needs to check a fixed, manageable number of messages.
        /// </summary>
        public static void StartCleanupOldClientLogs(string botToken, string channelId, int keepCount = 10)
        {
            if (string.IsNullOrEmpty(botToken) || string.IsNullOrEmpty(channelId))
                return;

            EnsureHost();
            _host.StartCoroutine(CleanupOldClientLogsCoroutine(botToken, channelId, keepCount));
        }

        private static IEnumerator CleanupOldClientLogsCoroutine(string botToken, string channelId, int keepCount)
        {
            yield return new WaitForSeconds(PostMessageCleanupDelaySeconds);

            string url = $"https://discord.com/api/v10/channels/{channelId}/messages?limit=50";
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
                    if (req.responseCode != 403 && req.responseCode != 404)
                        Debug.LogWarning($"[ClientLogRelay] Failed to fetch messages for cleanup ({req.responseCode})");
                    yield break;
                }

                Newtonsoft.Json.Linq.JArray messages;
                try
                {
                    messages = Newtonsoft.Json.Linq.JArray.Parse(req.downloadHandler.text);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ClientLogRelay] Failed to parse messages for cleanup: {ex.Message}");
                    yield break;
                }

                if (messages == null || messages.Count <= keepCount)
                    yield break; // Nothing to clean up

                // Collect client log snapshot messages
                var clientLogMessages = new List<string>();
                foreach (var msg in messages)
                {
                    var msgObj = msg as Newtonsoft.Json.Linq.JObject;
                    if (msgObj == null) continue;

                    // Check if this is a bot message
                    var author = msgObj["author"] as Newtonsoft.Json.Linq.JObject;
                    if (author == null) continue;

                    var botProp = author["bot"];
                    bool isBot = botProp != null && botProp.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && (bool)botProp;
                    if (!isBot) continue;

                    // Check if it has embeds that look like client log snapshots
                    var embeds = msgObj["embeds"] as Newtonsoft.Json.Linq.JArray;
                    if (embeds == null || embeds.Count == 0) continue;

                    bool isClientLog = false;
                    foreach (var embedItem in embeds)
                    {
                        var embedObj = embedItem as Newtonsoft.Json.Linq.JObject;
                        string title = embedObj?["title"]?.ToString() ?? "";
                        string desc = embedObj?["description"]?.ToString() ?? "";

                        // Match client login snapshot messages
                        // Typically have "Client Login" or "Snapshot" in title, or player info in description
                        if (title.Contains("Client") || title.Contains("Login") || title.Contains("Snapshot") ||
                            desc.Contains("Platform ID") || desc.Contains("Captured"))
                        {
                            isClientLog = true;
                            break;
                        }
                    }

                    if (isClientLog)
                    {
                        string messageId = msgObj["id"]?.ToString();
                        if (!string.IsNullOrEmpty(messageId))
                            clientLogMessages.Add(messageId);
                    }
                }

                // Delete messages beyond the keep limit
                int toDelete = clientLogMessages.Count - keepCount;
                if (toDelete <= 0)
                    yield break;

                Debug.Log($"[ClientLogRelay] Found {clientLogMessages.Count} client log messages, deleting oldest {toDelete} to keep {keepCount}");

                // Skip the first keepCount (most recent), delete the rest
                for (int i = keepCount; i < clientLogMessages.Count; i++)
                {
                    string messageId = clientLogMessages[i];
                    string deleteUrl = $"https://discord.com/api/v10/channels/{channelId}/messages/{messageId}";

                    using (var delReq = UnityWebRequest.Delete(deleteUrl))
                    {
                        delReq.SetRequestHeader("Authorization", $"Bot {botToken}");
                        delReq.timeout = DeleteTimeoutSeconds;
                        yield return delReq.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
                        ok = delReq.result == UnityWebRequest.Result.Success;
#else
                        ok = !(delReq.result == UnityWebRequest.Result.ConnectionError) && !(delReq.result == UnityWebRequest.Result.ProtocolError);
#endif

                        if (ok || delReq.responseCode == 404)
                        {
                            // Success or already deleted
                        }
                        else if (delReq.responseCode != 403)
                        {
                            Debug.LogWarning($"[ClientLogRelay] Failed to delete message {messageId} ({delReq.responseCode})");
                        }
                    }

                    yield return new WaitForSeconds(MessageDeleteCooldownSeconds);
                }

                Debug.Log($"[ClientLogRelay] Client log cleanup complete - kept {keepCount} most recent messages");
            }
        }

        private static void EnsureHost()
        {
            if (_host != null) return;
            var go = new GameObject("ClientLogRelay_WebhookHost");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _host = go.AddComponent<CoroutineHost>();
        }

        /// <summary>Internal MonoBehaviour that hosts coroutines for webhook POSTs.</summary>
        public sealed class CoroutineHost : MonoBehaviour { }
    }
}
