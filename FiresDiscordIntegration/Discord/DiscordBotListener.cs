    using System;
    using System.Collections;
    using System.Collections.Generic;
    using Newtonsoft.Json.Linq;
    using Splatform;
    using UnityEngine;
    using UnityEngine.Networking;

    namespace FiresDiscordIntegration.Discord
    {
        /// <summary>
        /// Polls a Discord channel via the Bot HTTP API and relays new messages
        /// into the Valheim server chat.
        ///
        /// Uses GET /api/channels/{id}/messages?after={last_id}&amp;limit=10
        /// with the Bot token for auth. Runs as a coroutine on a persistent
        /// MonoBehaviour (same pattern as DiscordWebhookService).
        ///
        /// Only active on the server when both Discord integration and the
        /// bot listener are enabled in config.
        /// </summary>
        public static class DiscordBotListener
        {
            private const string DiscordApiBase = "https://discord.com/api/v10";

            private static MonoBehaviour _host;
            private static Coroutine _pollCoroutine;
            private static string _lastMessageId;
            private static bool _running;

            // The webhook bot's own user ID - messages from this ID are ignored
            // to prevent echo (server sends chat to Discord, bot reads it back).
            private static string _selfBotUserId;
            private static bool _selfIdResolved;

            // Also ignore messages from webhook authors (type 0 = default, but
            // webhook messages have author.bot=true and webhook_id set).
            // We track the webhook ID extracted from the ChatWebhookUrl to filter those.
            private static string _webhookId;

            internal static bool Running => _running;

            /// <summary>
            /// Start polling if config is valid. Called from DiscordIntegrationPatches
            /// after server load.
            /// </summary>
            public static void Start()
            {
                if (_running) return;

                if (!DiscordIntegrationCore.IsActive()) return;
                if (DiscordIntegrationConfig.EnableBotListener?.Value != true) return;

                string token = FiresDiscordIntegration.Utilities.BotTokenFile.Token;
                string channelId = DiscordIntegrationConfig.ChatChannelId?.Value;

                if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(channelId))
                {
                    Debug.LogWarning("[Discord] Bot listener enabled but BotToken or ChatChannelId is empty");
                    return;
                }

                // Extract webhook ID from the ChatWebhookUrl to filter echo
                _webhookId = ExtractWebhookId(DiscordIntegrationConfig.ChatWebhookUrl?.Value);

                EnsureHost();
                if (_host == null) return;

                _running = true;
                _selfIdResolved = false;
                _selfBotUserId = null;

                // Resolve our own bot user ID first
                _host.StartCoroutine(ResolveSelfUserId(token));

                // Pre-warm Discord role cache for @mention resolution
                DiscordMentionResolver.FetchRoles();

                _pollCoroutine = _host.StartCoroutine(PollLoop(token, channelId));
                Debug.Log($"[Discord] Bot listener started - polling channel {channelId}");
            }

            /// <summary>
            /// Stop polling. Called on server shutdown.
            /// </summary>
            public static void Stop()
            {
                _running = false;
                if (_pollCoroutine != null && _host != null)
                {
                    _host.StopCoroutine(_pollCoroutine);
                    _pollCoroutine = null;
                }
                _lastMessageId = null;
                _selfIdResolved = false;
                _selfBotUserId = null;
                DiscordMentionResolver.ClearCache();
                DiscordRoleColorResolver.ClearCache();
                Debug.Log("[Discord] Bot listener stopped");
            }

            private static IEnumerator ResolveSelfUserId(string token)
            {
                string url = $"{DiscordApiBase}/users/@me";

                using (var request = UnityWebRequest.Get(url))
                {
                    request.SetRequestHeader("Authorization", $"Bot {token}");
                    request.timeout = 10;

                    yield return request.SendWebRequest();

                    if (request.responseCode == 200)
                    {
                        try
                        {
                            var json = JObject.Parse(request.downloadHandler.text);
                            _selfBotUserId = json["id"]?.ToString();
                            _selfIdResolved = true;
                            LogVerbose($"Bot user ID resolved: {_selfBotUserId}");
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[Discord] Failed to parse bot user info: {ex.Message}");
                            _selfIdResolved = true; // proceed anyway, just won't filter self
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"[Discord] Failed to resolve bot user ID ({request.responseCode}): {request.error}");
                        _selfIdResolved = true;
                    }
                }
            }

            private static IEnumerator PollLoop(string token, string channelId)
            {
                // Initial seed: fetch the latest message ID so we don't replay history
                yield return SeedLastMessageId(token, channelId);

                float interval = Mathf.Clamp(
                    DiscordIntegrationConfig.BotPollIntervalSeconds?.Value ?? 3f, 1f, 30f);

                while (_running)
                {
                    yield return new WaitForSecondsRealtime(interval);

                    if (!_running) break;
                    if (!DiscordIntegrationCore.IsActive()) continue;

                    yield return FetchNewMessages(token, channelId);
                }
            }

            /// <summary>
            /// Fetch the most recent message ID so we only relay NEW messages going forward.
            /// </summary>
            private static IEnumerator SeedLastMessageId(string token, string channelId)
            {
                string url = $"{DiscordApiBase}/channels/{channelId}/messages?limit=1";

                using (var request = UnityWebRequest.Get(url))
                {
                    request.SetRequestHeader("Authorization", $"Bot {token}");
                    request.timeout = 10;

                    yield return request.SendWebRequest();

                    if (request.responseCode == 200)
                    {
                        try
                        {
                            string responseText = request.downloadHandler?.text;
                            if (string.IsNullOrEmpty(responseText)) yield break;

                            var messages = JArray.Parse(responseText);
                            if (messages != null && messages.Count > 0)
                            {
                                var first = messages[0] as JObject;
                                _lastMessageId = first?.Value<string>("id");
                                LogVerbose($"Seeded last message ID: {_lastMessageId}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[Discord] Failed to seed message ID: {ex.Message}\n{ex.StackTrace}");
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"[Discord] Failed to seed messages ({request.responseCode}): " +
                                         $"{request.downloadHandler?.text ?? request.error}");
                    }
                }
            }

            private static IEnumerator FetchNewMessages(string token, string channelId)
            {
                string url;
                if (!string.IsNullOrEmpty(_lastMessageId))
                    url = $"{DiscordApiBase}/channels/{channelId}/messages?after={_lastMessageId}&limit=10";
                else
                    url = $"{DiscordApiBase}/channels/{channelId}/messages?limit=1";

                using (var request = UnityWebRequest.Get(url))
                {
                    request.SetRequestHeader("Authorization", $"Bot {token}");
                    request.timeout = 10;

                    yield return request.SendWebRequest();

                    if (request.responseCode == 200)
                    {
                        // The parse phase is wrapped in try/catch because JArray.Parse and
                        // JObject member access can throw on malformed Discord responses.
                        // The relay phase needs `yield return` for the per-author role-color
                        // fetch, and C# forbids yield-return inside a try-with-catch
                        // (CS1626) - so we collect everything in the parse phase, then
                        // walk the collected list outside the try to do the yields.
                        var pendingRelay = new List<PendingRelay>();
                        string newestMessageId = null;
                        bool parseOk = true;

                        try
                        {
                            string responseText = request.downloadHandler?.text;
                            if (string.IsNullOrEmpty(responseText))
                            {
                                LogVerbose("Bot poll returned empty response body");
                                parseOk = false;
                            }
                            else
                            {
                                var messages = JArray.Parse(responseText);
                                if (messages != null && messages.Count > 0)
                                {
                                    // Discord returns messages newest-first; we want to relay
                                    // in chronological (oldest-first) order so the chat reads
                                    // top-to-bottom the way it was typed.
                                    for (int i = messages.Count - 1; i >= 0; i--)
                                    {
                                        TryQueueRelay(messages[i] as JObject, pendingRelay);
                                    }

                                    var newest = messages[0] as JObject;
                                    newestMessageId = newest?.Value<string>("id");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[Discord] Failed to parse messages: {ex.Message}\n{ex.StackTrace}");
                            parseOk = false;
                        }

                        if (parseOk)
                        {
                            foreach (var entry in pendingRelay)
                            {
                                yield return DiscordRoleColorResolver.EnsureUserFetched(entry.AuthorId);
                                DiscordRoleColorResolver.TryGetUserColorHex(entry.AuthorId, out string colorHex);
                                RelayToGameChat(entry.AuthorName, entry.Content, colorHex);
                            }
                            if (!string.IsNullOrEmpty(newestMessageId))
                                _lastMessageId = newestMessageId;
                        }
                    }
                    else if (request.responseCode == 429)
                    {
                        LogVerbose("Bot listener rate limited, backing off");
                        yield return new WaitForSecondsRealtime(5f);
                    }
                    else if (request.responseCode > 0)
                    {
                        Debug.LogWarning($"[Discord] Bot poll failed ({request.responseCode}): " +
                                         $"{request.downloadHandler?.text ?? request.error}");
                    }
                }
            }

            // What the poll-loop's parse phase passes to the relay phase: just
            // the fields RelayToGameChat needs, plus the author ID so we can
            // look up their role color before relaying.
            private struct PendingRelay
            {
                public string AuthorId;
                public string AuthorName;
                public string Content;
            }

            // Pulls the underlying playable image URL out of every embed Discord
            // attached to the message.  Order of preference per embed:
            //   1. image.proxy_url  - Discord's CDN-proxied copy (fastest, cached)
            //   2. image.url        - origin server (Tenor / Giphy CDN)
            //   3. thumbnail.url    - usually a smaller preview but still a GIF
            // video.url is intentionally skipped: Discord gifv embeds set it to
            // a .mp4 file, which our GIF decoder can't read.  Any embed whose
            // resolved URL doesn't look like an image extension is dropped so a
            // YouTube unfurl doesn't pollute the relayed chat line.
            //
            // When the message content already contains a Tenor/Giphy/Klipy
            // share URL we DON'T append the embed URL: the in-game
            // ChatMediaEmbed.ResolveMediaUrl path knows how to upgrade those
            // share URLs into animated GIFs, and Discord's own embed for the
            // same share is a still-frame poster (or a Discord-signed proxy
            // wrapping one).  Appending the poster on top of the share URL
            // produced duplicate, static-only embeds in chat.
            private static string AppendEmbeddedMediaUrls(string content, JArray embeds)
            {
                if (embeds == null || embeds.Count == 0) return content;
                if (ContentHasResolvableShareUrl(content)) return content;

                foreach (var embedToken in embeds)
                {
                    var embed = embedToken as JObject;
                    if (embed == null) continue;
                    string mediaUrl = ExtractPlayableImageUrl(embed);
                    if (string.IsNullOrEmpty(mediaUrl)) continue;
                    if (!LooksLikeImageUrl(mediaUrl))    continue;
                    content = string.IsNullOrEmpty(content) ? mediaUrl : content + " " + mediaUrl;
                }
                return content;
            }

            // True if the message body already contains a share-URL pattern that
            // ChatMediaEmbed.ResolveMediaUrl handles natively.  Match list lives
            // here (not in ChatMediaDetector) because the cost of pulling it
            // into a shared module is more code than the three substring tests.
            private static bool ContentHasResolvableShareUrl(string content)
            {
                if (string.IsNullOrEmpty(content)) return false;
                return content.IndexOf("tenor.com/view/",  StringComparison.OrdinalIgnoreCase) >= 0
                    || content.IndexOf("giphy.com/gifs/",  StringComparison.OrdinalIgnoreCase) >= 0
                    || content.IndexOf("klipy.com/gifs/",  StringComparison.OrdinalIgnoreCase) >= 0;
            }

            private static string ExtractPlayableImageUrl(JObject embed)
            {
                string fromImage = ReadUrl(embed["image"] as JObject, preferProxy: true);
                if (!string.IsNullOrEmpty(fromImage)) return fromImage;
                string fromThumb = ReadUrl(embed["thumbnail"] as JObject, preferProxy: true);
                return fromThumb;
            }

            private static string ReadUrl(JObject node, bool preferProxy)
            {
                if (node == null) return null;
                if (preferProxy)
                {
                    string proxied = node.Value<string>("proxy_url");
                    if (!string.IsNullOrEmpty(proxied)) return proxied;
                }
                return node.Value<string>("url");
            }

            private static bool LooksLikeImageUrl(string url)
            {
                if (string.IsNullOrEmpty(url)) return false;
                // Strip any query string so ".gif?ex=..." still matches.
                int q = url.IndexOf('?');
                string path = q >= 0 ? url.Substring(0, q) : url;
                return path.EndsWith(".gif",  StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".png",  StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".jpg",  StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
            }

            // Decides whether a single message from the poll response should be
            // relayed and, if so, adds it to the pending list.  Pure function:
            // no yields, no HTTP - all of that happens after the parse phase.
            //
            // A message is dropped when it has no content AND no attachments,
            // when it's from our own bot (echo prevention), when it's from any
            // webhook (those are messages WE posted via the Discord webhook),
            // or when it's from any other bot.  Attachment URLs get appended
            // to the content so they reach the in-game chat-media detector.
            private static void TryQueueRelay(JObject msg, List<PendingRelay> pending)
            {
                if (msg == null) return;

                string content      = msg.Value<string>("content");
                string webhookMsgId = msg.Value<string>("webhook_id");

                var author = msg["author"] as JObject;
                string authorId = author?.Value<string>("id");
                bool   isBot    = author != null && author["bot"] != null && author.Value<bool>("bot");

                var attachments    = msg["attachments"] as JArray;
                bool hasAttachments = attachments != null && attachments.Count > 0;
                if (string.IsNullOrEmpty(content) && !hasAttachments) return;

                if (!string.IsNullOrEmpty(_selfBotUserId) && authorId == _selfBotUserId) return;
                if (!string.IsNullOrEmpty(webhookMsgId)) return;
                if (isBot) return;

                // Server commands are consumed here - a "!kick someone" must never reach game chat.
                if (DiscordServerCommands.TryHandle(msg, content, authorId,
                        (msg["author"] as JObject)?.Value<string>("global_name")
                        ?? StripDiscriminator((msg["author"] as JObject)?.Value<string>("username") ?? "Unknown")))
                    return;

                string authorName = author?.Value<string>("global_name");
                if (string.IsNullOrEmpty(authorName))
                {
                    authorName = author?.Value<string>("username") ?? "Unknown";
                    authorName = StripDiscriminator(authorName);
                }

                if (hasAttachments)
                {
                    foreach (var att in attachments)
                    {
                        string attUrl = att?.Value<string>("url");
                        if (string.IsNullOrEmpty(attUrl)) continue;
                        content = string.IsNullOrEmpty(content) ? attUrl : content + " " + attUrl;
                    }
                }

                // Tenor / Giphy / Klipy "view" URLs get unfurled by Discord into
                // an embeds[] entry - the actual playable image lives in
                // image.url (preferred) or thumbnail.url.  Without this step our
                // in-game chat embed would download the share URL's HTML page,
                // fail to decode it as a GIF, and only render the static
                // poster frame.  Appending the unfurled media URL lets the
                // ChatMediaEmbed + GifDecoder pair download the actual animated
                // GIF and play all its frames.
                content = AppendEmbeddedMediaUrls(content, msg["embeds"] as JArray);

                if (string.IsNullOrEmpty(content)) return;

                content = DiscordEmojiConverter.ConvertEmoji(content);
                pending.Add(new PendingRelay
                {
                    AuthorId   = authorId,
                    AuthorName = authorName,
                    Content    = content,
                });
            }

            // Cap per chat-RPC payload.  Valheim's chat RPC is ultimately tunneled
            // through Steam (packet limit ~512 KB) so this isn't a hard wire limit
            // - it's a readability cap so a long Discord message doesn't smear
            // across one wall of text in the in-game chat window.
            private const int MaxChatChunkLength = 400;

            /// <summary>
            /// Inject a message into the Valheim server chat as a Normal-type
            /// say from a synthetic "[Discord]" sender.
            ///
            /// HOW IT WORKS (after 2026-05-30 fix):
            ///   * <see cref="DiscordFakePlayerInjector"/> adds a fake
            ///     PlayerInfo (UserId = <see cref="DiscordFakePlayerInjector.FakeUserId"/>)
            ///     to <see cref="ZNet.m_players"/> server-side. SendPlayerList
            ///     propagates this to every client, vanilla included.
            ///   * Receiving clients call
            ///     <see cref="ZNet.TryGetPlayerByPlatformUserID"/> in
            ///     Terminal.AddString and succeed because the fake entry is in
            ///     their local player list. The chat scrollback receives the
            ///     entry under the "[Discord]" display name.
            ///   * Talker.Type.Normal (vs the previous Talker.Type.Shout)
            ///     renders as a small white floating bubble instead of the
            ///     yellow uppercase shout (which moved with the camera and
            ///     was suppressed entirely under NoMap).
            ///   * Color hex is smuggled through UserInfo.Name - modded
            ///     clients running ChatPatches read it back via
            ///     <see cref="ChatPatches"/>.ReadDiscordAuthorColorOrDefault.
            ///     Vanilla clients ignore the encoded suffix harmlessly.
            ///
            /// Long messages are still split into multiple chat RPCs at word
            /// boundaries rather than truncated.
            /// </summary>

            // Sentinel delimiter inserted between the visible "[Discord]" label and
            // the smuggled-along color hex in UserInfo.Name.  '|' is chosen because
            // Valheim's chat sanitizer leaves it alone (only '<' and '>' get
            // replaced) and it can't appear in a Discord-resolved color hex string.
            internal const char DiscordUserInfoNameDelimiter = '|';
            internal const string DiscordUserInfoNamePrefix  = "[Discord]";

            private static void RelayToGameChat(string discordUsername, string message, string nameColorHex)
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (ZRoutedRpc.instance == null) return;

                var senderPlatformId = DiscordFakePlayerInjector.FakeUserId;

                // We can't put TMP color tags ( <color=#HEX>...</color> ) directly
                // in the chat text: Valheim sanitizes incoming chat by replacing
                // every '<' and '>' with spaces to stop rich-text injection from
                // arbitrary players. That mangles the prefix into
                // "COLOR=#HEX :name: /COLOR". Smuggle the color hex through
                // UserInfo.Name instead - modded ChatPatches on receiving
                // clients reads it back to recolor the buffer entry; vanilla
                // clients ignore the delimiter-prefixed suffix.
                string encodedName = string.IsNullOrEmpty(nameColorHex)
                    ? DiscordUserInfoNamePrefix
                    : DiscordUserInfoNamePrefix + DiscordUserInfoNameDelimiter + nameColorHex;
                var userInfo = new UserInfo
                {
                    Name   = encodedName,
                    UserId = senderPlatformId,
                };

                // Discord username + colon goes IN the message body. The chat
                // scrollback sender name is already "[Discord]" (from the fake
                // PlayerInfo), so prefixing the body with "[Discord]" again
                // would duplicate the brand. Format here is just
                // "discord_username: message", rendered as
                // "[Discord]: discord_username: message" by the chat UI.
                string prefix = $"{discordUsername}: ";

                // Split long messages into chunks rather than truncating
                var chunks = SplitChatMessage(message, MaxChatChunkLength - prefix.Length);

                foreach (var chunk in chunks)
                {
                    string chatText = prefix + chunk;
                    LogVerbose($"Relaying to game chat: {chatText}");

                    ZRoutedRpc.instance.InvokeRoutedRPC(
                        ZRoutedRpc.Everybody,
                        "ChatMessage",
                        new Vector3(0f, 1000f, 0f),
                        (int)Talker.Type.Normal,
                        userInfo,
                        chatText,
                        senderPlatformId.ToString()
                    );
                }
            }

            /// <summary>
            /// Split a message into chunks that fit within maxLen characters.
            /// Prefers splitting at newline or space boundaries.
            /// </summary>
            private static List<string> SplitChatMessage(string message, int maxLen)
            {
                if (maxLen < 50) maxLen = 50; // safety floor

                var result = new List<string>();
                if (string.IsNullOrEmpty(message))
                {
                    result.Add(string.Empty);
                    return result;
                }

                if (message.Length <= maxLen)
                {
                    result.Add(message);
                    return result;
                }

                int pos = 0;
                while (pos < message.Length)
                {
                    int remaining = message.Length - pos;
                    if (remaining <= maxLen)
                    {
                        result.Add(message.Substring(pos));
                        break;
                    }

                    int searchEnd = pos + maxLen;

                    // Prefer newline break
                    int breakAt = -1;
                    int nl = message.LastIndexOf('\n', searchEnd - 1, maxLen);
                    if (nl > pos)
                        breakAt = nl + 1;

                    // Fall back to space
                    if (breakAt < 0)
                    {
                        int sp = message.LastIndexOf(' ', searchEnd - 1, maxLen);
                        if (sp > pos)
                            breakAt = sp + 1;
                    }

                    // Hard split if no good boundary
                    if (breakAt <= pos)
                        breakAt = searchEnd;

                    result.Add(message.Substring(pos, breakAt - pos));
                    pos = breakAt;
                }

                return result;
            }

            /// <summary>
            /// Get the server's own Steam Game Server ID from ZNet.m_serverSteamID (private static).
            /// </summary>
            private static string _cachedServerSteamId;
            private static string GetServerSteamId()
            {
                if (_cachedServerSteamId != null) return _cachedServerSteamId;

                try
                {
                    var field = typeof(ZNet).GetField("m_serverSteamID",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    if (field != null)
                    {
                        ulong steamId = (ulong)field.GetValue(null);
                        if (steamId != 0)
                        {
                            _cachedServerSteamId = steamId.ToString();
                            LogVerbose($"Resolved server Steam ID: {_cachedServerSteamId}");
                            return _cachedServerSteamId;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Discord] Failed to get server Steam ID via reflection: {ex.Message}");
                }

                // Fallback: try getting the server's host socket hostname
                try
                {
                    var hostSocketField = typeof(ZNet).GetField("m_hostSocket",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (hostSocketField != null && ZNet.instance != null)
                    {
                        var hostSocket = hostSocketField.GetValue(ZNet.instance) as ISocket;
                        if (hostSocket != null)
                        {
                            string hostName = hostSocket.GetHostName();
                            if (!string.IsNullOrEmpty(hostName))
                            {
                                _cachedServerSteamId = hostName;
                                LogVerbose($"Resolved server Steam ID from host socket: {_cachedServerSteamId}");
                                return _cachedServerSteamId;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Discord] Failed to get server host socket: {ex.Message}");
                }

                return null;
            }

            /// <summary>
            /// Strip Discord discriminator suffix from a username.
            /// Handles both old format "name#1234" and underscore format "name_1234".
            /// </summary>
            private static string StripDiscriminator(string username)
            {
                if (string.IsNullOrEmpty(username)) return username;

                // Old format: name#1234
                int hashIdx = username.LastIndexOf('#');
                if (hashIdx > 0 && hashIdx < username.Length - 1)
                {
                    string suffix = username.Substring(hashIdx + 1);
                    if (suffix.Length <= 4 && int.TryParse(suffix, out _))
                        return username.Substring(0, hashIdx);
                }

                // Underscore format: name_1234
                int underIdx = username.LastIndexOf('_');
                if (underIdx > 0 && underIdx < username.Length - 1)
                {
                    string suffix = username.Substring(underIdx + 1);
                    if (suffix.Length <= 4 && int.TryParse(suffix, out _))
                        return username.Substring(0, underIdx);
                }

                return username;
            }

            private static string ExtractWebhookId(string webhookUrl)
            {
                if (string.IsNullOrEmpty(webhookUrl)) return null;
                // URL format: https://discord.com/api/webhooks/{id}/{token}
                try
                {
                    var uri = new Uri(webhookUrl);
                    var segments = uri.AbsolutePath.Split('/');
                    // segments: ["", "api", "webhooks", "{id}", "{token}"]
                    for (int i = 0; i < segments.Length - 1; i++)
                    {
                        if (segments[i] == "webhooks" && i + 1 < segments.Length)
                            return segments[i + 1];
                    }
                }
                catch { }
                return null;
            }

            private static void EnsureHost()
            {
                if (_host != null) return;
                // Reuse the webhook service host if possible
                var existing = GameObject.Find("DiscordWebhookHost");
                if (existing != null)
                {
                    _host = existing.GetComponent<MonoBehaviour>();
                    if (_host != null) return;
                }

                var go = new GameObject("DiscordBotListenerHost");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                _host = go.AddComponent<CoroutineHost>();
            }

            /// <summary>Posts a message to the chat channel as the bot - used for server-command replies.</summary>
            public static void PostToChatChannel(string content)
            {
                if (string.IsNullOrEmpty(content)) return;
                string token = FiresDiscordIntegration.Utilities.BotTokenFile.Token;
                string channelId = DiscordIntegrationConfig.ChatChannelId?.Value;
                if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(channelId)) return;

                EnsureHost();
                if (_host == null) return;
                _host.StartCoroutine(PostMessage(token, channelId, content));
            }

            private static IEnumerator PostMessage(string token, string channelId, string content)
            {
                string url = $"{DiscordApiBase}/channels/{channelId}/messages";
                string payload = new JObject { ["content"] = content, ["allowed_mentions"] = new JObject { ["parse"] = new JArray() } }.ToString();
                using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
                {
                    request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(payload));
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Authorization", "Bot " + token);
                    request.SetRequestHeader("Content-Type", "application/json");
                    yield return request.SendWebRequest();
                    if (request.responseCode < 200 || request.responseCode >= 300)
                        Debug.LogWarning($"[Discord] Command reply failed: HTTP {request.responseCode}");
                }
            }

            /// <summary>
            /// Triggers cleanup of messages older than the specified age in the given channel.
            /// Fetches recent messages and deletes those created before the cutoff time.
            /// </summary>
            public static void TriggerMessageCleanup(string channelId, TimeSpan maxAge)
            {
                if (string.IsNullOrEmpty(channelId)) return;

                string token = FiresDiscordIntegration.Utilities.BotTokenFile.Token;
                if (string.IsNullOrEmpty(token)) return;

                EnsureHost();
                if (_host == null) return;

                _host.StartCoroutine(CleanupOldMessages(token, channelId, maxAge));
            }

            /// <summary>
            /// Coroutine that fetches recent messages from a channel and deletes those older than maxAge.
            /// Fetches up to 100 messages (Discord limit) and deletes in batches.
            /// </summary>
            private static IEnumerator CleanupOldMessages(string token, string channelId, TimeSpan maxAge)
            {
                DateTime cutoffTime = DateTime.UtcNow - maxAge;
                string url = $"{DiscordApiBase}/channels/{channelId}/messages?limit=100";

                using (var request = UnityWebRequest.Get(url))
                {
                    request.SetRequestHeader("Authorization", $"Bot {token}");
                    request.timeout = 15;

                    yield return request.SendWebRequest();

                    if (request.responseCode != 200)
                    {
                        LogVerbose($"Failed to fetch messages for cleanup ({request.responseCode}): {request.error}");
                        yield break;
                    }

                    JArray messages = null;
                    try
                    {
                        messages = JArray.Parse(request.downloadHandler.text);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Discord] Message cleanup parse failed: {ex.Message}");
                        yield break;
                    }

                    int deletedCount = 0;
                    foreach (var msg in messages)
                    {
                        var msgObj = msg as JObject;
                        if (msgObj == null) continue;

                        string messageId = msgObj.Value<string>("id");
                        string timestampStr = msgObj.Value<string>("timestamp");

                        if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(timestampStr))
                            continue;

                        // Parse Discord timestamp (ISO 8601 format)
                        DateTime messageTime;
                        if (!DateTime.TryParse(timestampStr, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind, out messageTime))
                            continue;

                        // Delete if older than cutoff
                        if (messageTime < cutoffTime)
                        {
                            yield return DeleteMessage(token, channelId, messageId);
                            deletedCount++;
                            // Small delay to avoid rate limiting
                            yield return new WaitForSecondsRealtime(0.5f);
                        }
                    }

                    if (deletedCount > 0)
                    {
                        LogVerbose($"Cleaned up {deletedCount} old message(s) from status channel");
                    }
                }
            }

            /// <summary>
            /// Deletes a single message from a channel using the Discord API.
            /// </summary>
            private static IEnumerator DeleteMessage(string token, string channelId, string messageId)
            {
                string url = $"{DiscordApiBase}/channels/{channelId}/messages/{messageId}";

                using (var request = UnityWebRequest.Delete(url))
                {
                    request.SetRequestHeader("Authorization", $"Bot {token}");
                    request.timeout = 10;

                    yield return request.SendWebRequest();

                    if (request.responseCode != 204 && request.responseCode != 404)
                    {
                        LogVerbose($"Failed to delete message {messageId} ({request.responseCode}): {request.error}");
                    }
                }
            }

            private static void LogVerbose(string msg)
            {
                if (DiscordIntegrationConfig.VerboseLogging != null &&
                    DiscordIntegrationConfig.VerboseLogging.Value)
                    Debug.Log($"[Discord] {msg}");
            }

            private class CoroutineHost : MonoBehaviour { }
        }
    }
