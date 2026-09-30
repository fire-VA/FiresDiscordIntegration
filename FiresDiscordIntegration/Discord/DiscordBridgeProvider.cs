using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using FiresCore.Bridge;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace FiresDiscordIntegration.Discord
{
    /// <summary>
    /// FDI's side of Core's <see cref="DiscordBridge"/> (Fire 09-29 23:1x: FDI is public, so folder work lives in a private
    /// tool that reaches Discord "through core"). Network only: posts to a channel AS THE BOT and polls watched channels,
    /// raising <see cref="DiscordBridge.RaiseChannelMessage"/> for each new message. Server-side (set at world load, cleared at
    /// quit). The bot token never leaves FDI.
    /// </summary>
    public static class DiscordBridgeProvider
    {
        private const string ApiBase = "https://discord.com/api/v10";
        private const float WatchSeconds = 2f;
        private const int TextLimit = 1990;

        private sealed class Host : MonoBehaviour { }
        private static Host _host;
        private static readonly Dictionary<string, Coroutine> _watching = new Dictionary<string, Coroutine>();

        public static void Start()
        {
            if (_host != null) return;
            if (string.IsNullOrEmpty(FiresDiscordIntegration.Utilities.BotTokenFile.Token)) { Debug.Log("[Discord] bridge: no bot token; private tools can't post"); return; }
            var go = new GameObject("FDI_DiscordBridge");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _host = go.AddComponent<Host>();
            DiscordBridge.PostAsBot = PostAsBot;
            DiscordBridge.Watch = Watch;
            DiscordBridge.Unwatch = Unwatch;
            Debug.Log("[Discord] bridge: ready (post as the bot, watch channels)");
        }

        public static void Stop()
        {
            DiscordBridge.PostAsBot = null;
            DiscordBridge.Watch = null;
            DiscordBridge.Unwatch = null;
            if (_host != null) { _host.StopAllCoroutines(); UnityEngine.Object.Destroy(_host.gameObject); _host = null; }
            _watching.Clear();
        }

        private static void PostAsBot(string channelId, string text, string replyTo, string fileName, byte[] file, string contentType, Action<bool, string> done)
        {
            string channel = BotChannelTarget.CleanId(channelId);
            if (channel == null || _host == null) { done?.Invoke(false, "bad channel id or the bridge is stopped"); return; }
            _host.StartCoroutine(Post(channel, text ?? string.Empty, replyTo, fileName, file, contentType, done));
        }

        private static IEnumerator Post(string channel, string text, string replyTo, string fileName, byte[] file, string contentType, Action<bool, string> done)
        {
            if (text.Length > TextLimit) text = text.Substring(0, TextLimit);
            var payload = new Dictionary<string, object>
            {
                ["content"] = text,
                ["allowed_mentions"] = new Dictionary<string, object> { ["parse"] = new List<string>() },
            };
            if (BotChannelTarget.CleanId(replyTo) != null)
                payload["message_reference"] = new Dictionary<string, object> { ["message_id"] = replyTo, ["fail_if_not_exists"] = false };
            var form = new List<IMultipartFormSection> { new MultipartFormDataSection("payload_json", JsonConvert.SerializeObject(payload), "application/json") };
            if (file != null && file.Length > 0)
                form.Add(new MultipartFormFileSection("files[0]", file, string.IsNullOrEmpty(fileName) ? "file.bin" : fileName, string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType));
            bool ok = false; string result;
            using (var req = UnityWebRequest.Post(BotChannelTarget.MessagesUrl(channel), form))
            {
                req.SetRequestHeader("Authorization", BotChannelTarget.AuthHeader());
                req.timeout = 120;
                yield return req.SendWebRequest();
                if (req.responseCode >= 200 && req.responseCode < 300)
                {
                    ok = true; result = null;
                    try { result = JObject.Parse(req.downloadHandler.text).Value<string>("id"); } catch { }
                }
                else
                {
                    string body = null; try { body = req.downloadHandler?.text; } catch { }
                    if (!string.IsNullOrEmpty(body) && body.Length > 300) body = body.Substring(0, 300);
                    result = $"HTTP {req.responseCode}: {req.error} {body}".Trim();
                    Debug.LogWarning($"[Discord] bridge post to channel {BotChannelTarget.Short(channel)} FAILED: HTTP {req.responseCode}");
                }
            }
            try { done?.Invoke(ok, result); } catch (Exception ex) { Debug.LogWarning($"[Discord] bridge post callback threw: {ex.Message}"); }
        }

        private static void Watch(string channelId)
        {
            string channel = BotChannelTarget.CleanId(channelId);
            if (channel == null || _host == null || _watching.ContainsKey(channel)) return;
            _watching[channel] = _host.StartCoroutine(WatchLoop(channel));
            Debug.Log($"[Discord] bridge: watching channel {BotChannelTarget.Short(channel)} every {WatchSeconds:0} s");
        }

        private static void Unwatch(string channelId)
        {
            string channel = BotChannelTarget.CleanId(channelId);
            if (channel == null || _host == null || !_watching.TryGetValue(channel, out var co)) return;
            if (co != null) _host.StopCoroutine(co);
            _watching.Remove(channel);
        }

        private static UnityWebRequest Get(string url)
        {
            var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("Authorization", BotChannelTarget.AuthHeader());
            req.timeout = 15;
            return req;
        }

        // Starts after the newest message (nothing typed before the watch began is raised), then every WatchSeconds
        // raises each new message oldest first.
        private static IEnumerator WatchLoop(string channel)
        {
            string last = null;
            using (var seed = Get($"{ApiBase}/channels/{channel}/messages?limit=1"))
            {
                yield return seed.SendWebRequest();
                if (seed.responseCode == 200) { try { var arr = JArray.Parse(seed.downloadHandler.text); if (arr.Count > 0) last = arr[0].Value<string>("id"); } catch { } }
                else Debug.LogWarning($"[Discord] bridge: can't read channel {BotChannelTarget.Short(channel)} (HTTP {seed.responseCode}); the bot needs View Channel + Read Message History");
            }
            while (true)
            {
                yield return new WaitForSecondsRealtime(WatchSeconds);
                if (last == null)
                {
                    using (var s = Get($"{ApiBase}/channels/{channel}/messages?limit=1"))
                    {
                        yield return s.SendWebRequest();
                        if (s.responseCode == 200) { try { var arr = JArray.Parse(s.downloadHandler.text); last = arr.Count > 0 ? arr[0].Value<string>("id") : "0"; } catch { } }
                    }
                    continue;
                }
                using (var req = Get($"{ApiBase}/channels/{channel}/messages?after={last}&limit=20"))
                {
                    yield return req.SendWebRequest();
                    if (req.responseCode == 429) { yield return new WaitForSecondsRealtime(5f); continue; }
                    if (req.responseCode != 200) continue;
                    JArray arr = null;
                    try { arr = JArray.Parse(req.downloadHandler.text); } catch { }
                    if (arr == null) continue;
                    for (int i = arr.Count - 1; i >= 0; i--)   // Discord returns newest first
                    {
                        var msg = arr[i] as JObject;
                        if (msg == null) continue;
                        string id = msg.Value<string>("id");
                        if (Newer(id, last)) last = id;
                        var author = msg["author"] as JObject;
                        bool isBot = msg["webhook_id"] != null || (author?["bot"] != null && author.Value<bool>("bot"));
                        DiscordBridge.RaiseChannelMessage(channel, id, author?.Value<string>("id") ?? "", isBot, msg.Value<string>("content") ?? string.Empty);
                    }
                }
            }
        }

        private static bool Newer(string a, string b) =>
            a != null && (b == null || a.Length > b.Length || (a.Length == b.Length && string.CompareOrdinal(a, b) > 0));
    }
}
