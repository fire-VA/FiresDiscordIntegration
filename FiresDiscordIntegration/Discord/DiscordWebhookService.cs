using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace FiresDiscordIntegration.Discord
{
    /// <summary>
    /// Low-level Discord webhook HTTP service.
    ///
    /// Handles:
    ///  - Webhook URL validation
    ///  - JSON payload construction (both basic content and rich embeds)
    ///  - Fire-and-forget HTTP POST via UnityWebRequest
    ///  - Rate-limit awareness (429 retry-after)
    ///
    /// Uses UnityWebRequest exclusively - no System.Net.Http dependency.
    /// This avoids the Mono deadlock that occurs when System.Net.Http.dll
    /// is loaded during Harmony patch resolution at startup.
    ///
    /// All POST calls are dispatched as coroutines on a persistent helper
    /// MonoBehaviour so they work on both client and dedicated server.
    /// </summary>
    public static class DiscordWebhookService
    {
        private static float _rateLimitUntil;
        private static CoroutineHost _host;

        /// <summary>
        /// Validate that a URL looks like a real Discord webhook endpoint.
        /// </summary>
        public static bool IsValidWebhookUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith("https://"))
                return false;
            return url.Contains("discord.com/api/webhooks/") ||
                   url.Contains("discordapp.com/api/webhooks/");
        }

        // A webhook URL, or a "discord-bot-channel:<id>" target (test shots posted AS THE BOT, Fire 09-29 22:5x).
        internal static bool IsTarget(string url) => IsValidWebhookUrl(url) || BotChannelTarget.TryParse(url, out _);

        // Where a target posts, and the bot's Authorization header when it is a bot channel (null for a webhook). A bot
        // can't override its name or avatar, so those payload fields are dropped for it.
        private static string PostUrlFor(string target, Dictionary<string, object> payload, out string auth, out string botChannel)
        {
            auth = null;
            if (!BotChannelTarget.TryParse(target, out botChannel)) return target;
            auth = BotChannelTarget.AuthHeader();
            if (payload != null) { payload.Remove("username"); payload.Remove("avatar_url"); }
            return BotChannelTarget.MessagesUrl(botChannel);
        }

        /// <summary>
        /// Discord webhook content limit (API max is 2000 characters).
        /// We use 1990 to leave room for potential encoding overhead.
        /// </summary>
        private const int MaxContentLength = 1990;

        /// <summary>
        /// Send a simple text message to a Discord webhook.
        /// Messages longer than 1990 characters are split into multiple
        /// webhook posts at word boundaries.
        /// </summary>
        public static void SendMessage(
            string webhookUrl,
            string content,
            string username = null,
            string avatarUrl = null,
            IReadOnlyList<string> forceAllowedRoleIds = null)
        {
            if (!IsTarget(webhookUrl)) return;
            if (string.IsNullOrEmpty(content)) return;

            var chunks = SplitMessage(content, MaxContentLength);

            foreach (var chunk in chunks)
            {
                var payload = new Dictionary<string, object>
                {
                    { "content", chunk },
                    { "allowed_mentions", BuildAllowedMentions(forceAllowedRoleIds) },
                };

                if (!string.IsNullOrEmpty(username))
                    payload["username"] = username;
                if (!string.IsNullOrEmpty(avatarUrl))
                    payload["avatar_url"] = avatarUrl;

                EnqueuePost(webhookUrl, payload);
            }
        }

        // Discord webhook payload's allowed_mentions field controls what actually
        // pings.  Two relevant modes:
        //
        //   parse: ["roles", "users"]   — only pings roles that are flagged as
        //       "Allow anyone to @mention this role" in Discord; non-mentionable
        //       roles render as silent <@&id> text.  This is the default we use
        //       for plain in-game shouts where we don't know which specific
        //       role IDs (if any) the player typed.
        //
        //   roles: [id, id, ...]        — explicitly allows those role IDs to
        //       ping, bypassing the non-mentionable flag.  We use this when the
        //       mention resolver successfully converted "@name" into a specific
        //       <@&ROLE_ID> — at that point the operator clearly meant to ping
        //       that role and shouldn't have to also flip the mentionable
        //       toggle in Discord.
        //
        // The two modes are mutually exclusive for any given mention type; if
        // we send both, Discord ignores `parse` for roles and only honours the
        // explicit list.
        private static Dictionary<string, object> BuildAllowedMentions(IReadOnlyList<string> forceAllowedRoleIds)
        {
            if (forceAllowedRoleIds != null && forceAllowedRoleIds.Count > 0)
            {
                return new Dictionary<string, object>
                {
                    { "roles", forceAllowedRoleIds },
                    { "parse", new List<string> { "users" } }, // still allow @user pings via parse
                };
            }
            return new Dictionary<string, object>
            {
                { "parse", new List<string> { "roles", "users" } },
            };
        }

        /// <summary>
        /// Split a message into chunks that fit within maxLen characters.
        /// Splits at newline or space boundaries when possible.
        /// </summary>
        private static List<string> SplitMessage(string content, int maxLen)
        {
            var result = new List<string>();
            if (content.Length <= maxLen)
            {
                result.Add(content);
                return result;
            }

            int pos = 0;
            while (pos < content.Length)
            {
                int remaining = content.Length - pos;
                if (remaining <= maxLen)
                {
                    result.Add(content.Substring(pos));
                    break;
                }

                // Find a good break point (prefer newline, then space)
                int breakAt = -1;
                int searchEnd = pos + maxLen;

                // Look for last newline in range
                int nl = content.LastIndexOf('\n', searchEnd - 1, maxLen);
                if (nl > pos)
                    breakAt = nl + 1; // include the newline in the current chunk

                // Fall back to last space
                if (breakAt < 0)
                {
                    int sp = content.LastIndexOf(' ', searchEnd - 1, maxLen);
                    if (sp > pos)
                        breakAt = sp + 1;
                }

                // No good break point - hard split
                if (breakAt <= pos)
                    breakAt = searchEnd;

                result.Add(content.Substring(pos, breakAt - pos));
                pos = breakAt;
            }

            return result;
        }

        /// <summary>
        /// Send a rich embed message to a Discord webhook.
        /// </summary>
        public static void SendEmbed(string webhookUrl, DiscordEmbed embed, string username = null, string avatarUrl = null)
        {
            SendEmbed(webhookUrl, embed, username, avatarUrl, null);
        }

        /// <summary>
        /// Send a rich embed message to a Discord webhook.
        /// When complete (success or failure), <paramref name="onComplete"/> is invoked.
        /// </summary>
        public static void SendEmbed(string webhookUrl, DiscordEmbed embed,
            string username, string avatarUrl, Action onComplete)
        {
            if (!IsTarget(webhookUrl)) { onComplete?.Invoke(); return; }
            if (embed == null) { onComplete?.Invoke(); return; }

            var embedDict = embed.ToDictionary();
            var payload = new Dictionary<string, object>
            {
                { "embeds", new List<Dictionary<string, object>> { embedDict } }
            };

            if (!string.IsNullOrEmpty(username))
                payload["username"] = username;
            if (!string.IsNullOrEmpty(avatarUrl))
                payload["avatar_url"] = avatarUrl;

            EnqueuePost(webhookUrl, payload, onComplete);
        }

        /// <summary>
        /// Send a rich embed; <paramref name="onResult"/> gets the HTTP status code when the post ends (0 = no response,
        /// -1 = never sent: no target, no embed, or the payload didn't serialise).
        /// </summary>
        public static void SendEmbedWithResult(string webhookUrl, DiscordEmbed embed,
            string username, string avatarUrl, Action<long> onResult)
        {
            if (!IsTarget(webhookUrl) || embed == null) { onResult?.Invoke(-1); return; }

            var payload = new Dictionary<string, object>
            {
                { "embeds", new List<Dictionary<string, object>> { embed.ToDictionary() } }
            };
            if (!string.IsNullOrEmpty(username))
                payload["username"] = username;
            if (!string.IsNullOrEmpty(avatarUrl))
                payload["avatar_url"] = avatarUrl;

            EnqueuePost(webhookUrl, payload, null, onResult);
        }

        /// <summary>
        /// Send a rich embed with one or more file attachments via multipart/form-data.
        /// Discord webhooks support file uploads when using multipart encoding.
        /// </summary>
        public static void SendEmbedWithFiles(string webhookUrl, DiscordEmbed embed,
            List<DiscordFileAttachment> files, string username = null, string avatarUrl = null)
        {
            SendEmbedWithFiles(webhookUrl, embed, files, username, avatarUrl, null);
        }

        /// <summary>
        /// Send a rich embed with file attachments. When the upload completes (success or failure),
        /// the <paramref name="onComplete"/> callback is invoked on the main thread.
        /// Used by VAngarde to defer player kicks until the Discord webhook has been sent.
        /// </summary>
        public static void SendEmbedWithFiles(string webhookUrl, DiscordEmbed embed,
            List<DiscordFileAttachment> files, string username, string avatarUrl,
            Action onComplete)
        {
            if (!IsTarget(webhookUrl))
            {
                onComplete?.Invoke();
                return;
            }
            if (embed == null && (files == null || files.Count == 0))
            {
                onComplete?.Invoke();
                return;
            }

            EnsureHost();
            if (_host == null)
            {
                onComplete?.Invoke();
                return;
            }
            _host.StartCoroutine(PostMultipartCoroutine(webhookUrl, embed, files, username, avatarUrl, onComplete));
        }

        private static void EnqueuePost(string webhookUrl, Dictionary<string, object> payload,
            Action onComplete = null, Action<long> onResult = null)
        {
            EnsureHost();
            if (_host == null) { onResult?.Invoke(-1); onComplete?.Invoke(); return; }
            _host.StartCoroutine(PostCoroutine(webhookUrl, payload, onComplete, onResult));
        }

        private static IEnumerator PostCoroutine(string webhookUrl, Dictionary<string, object> payload,
            Action onComplete = null, Action<long> onResult = null)
        {
            long code = 0;
            // Respect rate limits
            if (Time.unscaledTime < _rateLimitUntil)
            {
                float wait = _rateLimitUntil - Time.unscaledTime;
                if (wait > 0f && wait < 30f)
                    yield return new WaitForSecondsRealtime(wait);
            }

            string postUrl = PostUrlFor(webhookUrl, payload, out string auth, out string botChannel);
            string json;
            try
            {
                json = JsonConvert.SerializeObject(payload);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] JSON serialize error: {ex.Message}");
                onResult?.Invoke(-1);
                yield break;
            }

            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);

            using (var request = new UnityWebRequest(postUrl, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                if (auth != null) request.SetRequestHeader("Authorization", auth);
                request.timeout = 10;

                yield return request.SendWebRequest();

                code = request.responseCode;

                if (code >= 200 && code < 300)
                {
                    LogVerbose("Webhook sent successfully");
                    if (botChannel != null) Debug.Log($"[Discord] test post sent to channel {BotChannelTarget.Short(botChannel)} (bot)");
                }
                else if (code == 429)
                {
                    string body = request.downloadHandler?.text ?? "";
                    LogVerbose($"Rate limited by Discord: {body}");

                    try
                    {
                        var rateLimitInfo = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
                        if (rateLimitInfo != null && rateLimitInfo.ContainsKey("retry_after"))
                        {
                            double retryAfter = Convert.ToDouble(rateLimitInfo["retry_after"]);
                            _rateLimitUntil = Time.unscaledTime + (float)retryAfter + 0.5f;
                        }
                        else
                        {
                            _rateLimitUntil = Time.unscaledTime + 5f;
                        }
                    }
                    catch
                    {
                        _rateLimitUntil = Time.unscaledTime + 5f;
                    }
                }
                else if (request.result == UnityWebRequest.Result.ConnectionError ||
                         request.result == UnityWebRequest.Result.ProtocolError)
                {
                    Debug.LogWarning($"[Discord] Webhook failed ({code}): {request.error}");
                }
                else if (code > 0)
                {
                    string body = request.downloadHandler?.text ?? request.error ?? "unknown";
                    Debug.LogWarning($"[Discord] Webhook failed ({code}): {body}");
                }
            }

            try { onResult?.Invoke(code); }
            catch (Exception ex) { Debug.LogWarning($"[Discord] webhook result handler threw {ex.GetType().Name}: {ex.Message}"); }
            onComplete?.Invoke();
        }

        private static IEnumerator PostMultipartCoroutine(string webhookUrl, DiscordEmbed embed,
            List<DiscordFileAttachment> files, string username, string avatarUrl,
            Action onComplete = null)
        {
            // Respect rate limits
            if (Time.unscaledTime < _rateLimitUntil)
            {
                float wait = _rateLimitUntil - Time.unscaledTime;
                if (wait > 0f && wait < 30f)
                    yield return new WaitForSecondsRealtime(wait);
            }

            // Build the payload_json part (embed + username + avatar)
            string payloadJson;
            string multipartUrl = webhookUrl, multipartAuth = null, multipartBot = null;
            try
            {
                var payload = new Dictionary<string, object>();
                if (embed != null)
                    payload["embeds"] = new List<Dictionary<string, object>> { embed.ToDictionary() };
                if (!string.IsNullOrEmpty(username))
                    payload["username"] = username;
                if (!string.IsNullOrEmpty(avatarUrl))
                    payload["avatar_url"] = avatarUrl;
                multipartUrl = PostUrlFor(webhookUrl, payload, out multipartAuth, out multipartBot);
                payloadJson = JsonConvert.SerializeObject(payload);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] JSON serialize error (multipart): {ex.Message}");
                yield break;
            }

            // Build multipart form
            var form = new List<IMultipartFormSection>();
            form.Add(new MultipartFormDataSection("payload_json", payloadJson, "application/json"));

            if (files != null)
            {
                for (int i = 0; i < files.Count; i++)
                {
                    var file = files[i];
                    if (file.Content != null && file.Content.Length > 0)
                    {
                        form.Add(new MultipartFormFileSection(
                            $"files[{i}]", file.Content, file.FileName, file.ContentType));
                    }
                }
            }

            using (var request = UnityWebRequest.Post(multipartUrl, form))
            {
                if (multipartAuth != null) request.SetRequestHeader("Authorization", multipartAuth);
                request.timeout = 30;

                yield return request.SendWebRequest();

                long code = request.responseCode;

                if (code >= 200 && code < 300)
                {
                    LogVerbose("Webhook (multipart) sent successfully");
                    if (multipartBot != null) Debug.Log($"[Discord] test shot posted to channel {BotChannelTarget.Short(multipartBot)} (bot)");
                }
                else if (code == 429)
                {
                    string body = request.downloadHandler?.text ?? "";
                    LogVerbose($"Rate limited by Discord (multipart): {body}");

                    try
                    {
                        var rateLimitInfo = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
                        if (rateLimitInfo != null && rateLimitInfo.ContainsKey("retry_after"))
                        {
                            double retryAfter = Convert.ToDouble(rateLimitInfo["retry_after"]);
                            _rateLimitUntil = Time.unscaledTime + (float)retryAfter + 0.5f;
                        }
                        else
                        {
                            _rateLimitUntil = Time.unscaledTime + 5f;
                        }
                    }
                    catch
                    {
                        _rateLimitUntil = Time.unscaledTime + 5f;
                    }
                }
                else if (request.result == UnityWebRequest.Result.ConnectionError ||
                         request.result == UnityWebRequest.Result.ProtocolError)
                {
                    Debug.LogWarning($"[Discord] Webhook (multipart) failed ({code}): {request.error}");
                }
                else if (code > 0)
                {
                    string body = request.downloadHandler?.text ?? request.error ?? "unknown";
                    Debug.LogWarning($"[Discord] Webhook (multipart) failed ({code}): {body}");
                }
            }

            // Notify caller that the upload is done (or failed) so deferred actions can proceed
            onComplete?.Invoke();
        }

        private static void EnsureHost()
        {
            if (_host != null) return;
            var go = new GameObject("DiscordWebhookHost");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _host = go.AddComponent<CoroutineHost>();
        }

        private static void LogVerbose(string msg)
        {
            if (DiscordIntegrationConfig.VerboseLogging != null &&
                DiscordIntegrationConfig.VerboseLogging.Value)
                Debug.Log($"[Discord] {msg}");
        }

        /// <summary>
        /// Minimal MonoBehaviour to host coroutines for webhook POST requests.
        /// </summary>
        private class CoroutineHost : MonoBehaviour { }
    }

    /// <summary>
    /// Builder for Discord embed objects.
    /// Fluent API for constructing rich webhook messages.
    /// </summary>
    public class DiscordEmbed
    {
        public string Title;
        public string Description;
        public int Color = 3447003; // Discord blurple default
        public string AuthorName;
        public string AuthorIconUrl;
        public string FooterText;
        public string ThumbnailUrl;
        public string ImageUrl;
        public List<EmbedField> Fields = new List<EmbedField>();

        public struct EmbedField
        {
            public string Name;
            public string Value;
            public bool Inline;
        }

        public DiscordEmbed SetTitle(string title) { Title = title; return this; }
        public DiscordEmbed SetDescription(string desc) { Description = desc; return this; }
        public DiscordEmbed SetColor(int color) { Color = color; return this; }
        public DiscordEmbed SetAuthor(string name, string iconUrl = null) { AuthorName = name; AuthorIconUrl = iconUrl; return this; }
        public DiscordEmbed SetFooter(string text) { FooterText = text; return this; }
        public DiscordEmbed SetThumbnail(string url) { ThumbnailUrl = url; return this; }
        public DiscordEmbed SetImage(string url) { ImageUrl = url; return this; }
        public DiscordEmbed AddField(string name, string value, bool inline = false)
        {
            Fields.Add(new EmbedField { Name = name, Value = value, Inline = inline });
            return this;
        }

        /// <summary>
        /// Convert to a dictionary suitable for JSON serialization.
        /// </summary>
        public Dictionary<string, object> ToDictionary()
        {
            var dict = new Dictionary<string, object>();

            if (!string.IsNullOrEmpty(Title))
            {
                dict["title"] = Title.Length > 256 ? Title.Substring(0, 253) + "..." : Title;
            }

            if (!string.IsNullOrEmpty(Description))
            {
                dict["description"] = Description.Length > 4096
                    ? Description.Substring(0, 4093) + "..."
                    : Description;
            }

            dict["color"] = Color;

            if (!string.IsNullOrEmpty(AuthorName))
            {
                var author = new Dictionary<string, object> { { "name", AuthorName } };
                if (!string.IsNullOrEmpty(AuthorIconUrl))
                    author["icon_url"] = AuthorIconUrl;
                dict["author"] = author;
            }

            if (!string.IsNullOrEmpty(FooterText))
            {
                dict["footer"] = new Dictionary<string, object>
                {
                    { "text", FooterText.Length > 2048 ? FooterText.Substring(0, 2045) + "..." : FooterText }
                };
            }

            if (!string.IsNullOrEmpty(ThumbnailUrl))
                dict["thumbnail"] = new Dictionary<string, object> { { "url", ThumbnailUrl } };

            if (!string.IsNullOrEmpty(ImageUrl))
                dict["image"] = new Dictionary<string, object> { { "url", ImageUrl } };

            if (Fields.Count > 0)
            {
                var fieldList = new List<Dictionary<string, object>>();
                foreach (var f in Fields)
                {
                    fieldList.Add(new Dictionary<string, object>
                    {
                        { "name", string.IsNullOrEmpty(f.Name) ? "\u200B" : f.Name },
                        { "value", string.IsNullOrEmpty(f.Value) ? "\u200B" : f.Value },
                        { "inline", f.Inline }
                    });
                }
                dict["fields"] = fieldList;
            }

            dict["timestamp"] = DateTime.UtcNow.ToString("o");

            return dict;
        }
    }

    /// <summary>
    /// Represents a file attachment for Discord webhook multipart uploads.
    /// </summary>
    public class DiscordFileAttachment
    {
        public string FileName;
        public byte[] Content;
        public string ContentType;

        public DiscordFileAttachment(string fileName, byte[] content, string contentType = "text/plain")
        {
            FileName = fileName;
            Content = content;
            ContentType = contentType;
        }

        public DiscordFileAttachment(string fileName, string textContent, string contentType = "text/plain")
        {
            FileName = fileName;
            Content = Encoding.UTF8.GetBytes(textContent ?? "");
            ContentType = contentType;
        }
    }

    /// <summary>
    /// Common embed colors for Discord notifications.
    /// </summary>
    public static class DiscordColors
    {
        public const int Green = 5763719;      // Server start, player join
        public const int Red = 15548997;        // Server stop, player leave, death
        public const int Yellow = 16776960;     // Warnings
        public const int Blue = 3447003;        // Info / default
        public const int Orange = 15105570;     // Anti-cheat violations
        public const int White = 16777215;      // Chat messages
        public const int Purple = 10181046;     // Events / raids
        public const int DarkRed = 10038562;    // Anti-cheat kicks
    }
}
