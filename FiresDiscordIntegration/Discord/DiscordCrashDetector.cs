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
using Debug = UnityEngine.Debug;

namespace FiresDiscordIntegration.Discord
{
    /// <summary>
    /// Server crash detection via a live-updating Discord status message.
    ///
    /// <b>How it works:</b>
    /// <list type="number">
    ///   <item>On server start, posts a "Server Online" embed to the configured
    ///         status channel via the <b>Bot API</b>
    ///         (<c>POST /channels/{StatusChannelId}/messages</c>).</item>
    ///   <item>Every 60 seconds, EDITs that same message with fresh uptime, player
    ///         count, and heartbeat timestamp via
    ///         <c>PATCH /channels/{id}/messages/{msg}</c>.</item>
    ///   <item>On clean shutdown, edits the message to "Server Offline" and writes
    ///         "stopped" to a sentinel file.</item>
    ///   <item>If the server crashes, the message stops updating - the stale heartbeat
    ///         timestamp makes the crash obvious at a glance.</item>
    ///   <item>A background PowerShell process monitors the server PID. If it exits
    ///         while the sentinel still says "running", it POSTs a crash notification
    ///         to the General webhook within seconds.</item>
    /// </list>
    ///
    /// <b>Requirements:</b> BotToken + StatusChannelId in config. The bot must be
    /// invited to your Discord server and have <c>Send Messages</c> permission in
    /// the status channel. If <c>StatusChannelId</c> is empty, falls back to
    /// <c>ChatChannelId</c>.
    /// </summary>
    public static class DiscordCrashDetector
    {
        private const int RequestTimeoutSeconds = 15;
        private const int ShortRequestTimeoutSeconds = 10;

        private const string DiscordApiBase = "https://discord.com/api/v10";
        private const float HeartbeatIntervalSeconds = 60f;
        private const string SentinelFileName = "server_heartbeat.json";

        private static string _sentinelPath;
        private static string _statusMessageId;
        private static string _botToken;
        private static string _channelId;
        private static Coroutine _heartbeatCoroutine;
        private static MonoBehaviour _host;
        private static DateTime _serverStartUtc;
        private static bool _cleanShutdown;

        // ============================================================
        // Public API
        // ============================================================

        public static void OnServerStart()
        {
            if (!DiscordIntegrationCore.IsActive()) return;

            // The live-updating "Server Online" heartbeat message is now owned by
            // ClientLogRelay.Interactions.ServerHeartbeat (it has restart/stop reaction
            // controls and posts to a separate private admin channel via the
            // ServerManagerWebhookUrl config). We keep the crash notifier sentinel here
            // because it's the watchdog that fires a webhook from a *separate* process
            // when the server crashes - the new ServerHeartbeat would be dead in that
            // scenario, so the sentinel is still load-bearing.
            InitSentinel();
            SpawnCrashNotifier();
        }

        public static void OnServerStop()
        {
            _cleanShutdown = true;

            // Update sentinel so the background notifier knows it was clean
            try { WriteSentinel("stopped"); } catch { }

            // Stop the heartbeat coroutine
            if (_heartbeatCoroutine != null && _host != null)
            {
                _host.StopCoroutine(_heartbeatCoroutine);
                _heartbeatCoroutine = null;
            }

            // Edit the status message to show offline (synchronous - coroutines
            // don't run during OnApplicationQuit)
            if (!string.IsNullOrEmpty(_statusMessageId) &&
                !string.IsNullOrEmpty(_botToken) && !string.IsNullOrEmpty(_channelId))
            {
                try
                {
                    EditStatusMessageSync(_statusMessageId, BuildOfflineEmbed());
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Discord] CrashDetector: failed to edit status on shutdown: {ex.Message}");
                }
            }
        }

        // ============================================================
        // Status Message Lifecycle (Bot API)
        // ============================================================

        private static IEnumerator PostStatusMessage()
        {
            var embed = BuildOnlineEmbed();
            string json = BuildEmbedPayload(embed);

            string url = $"{DiscordApiBase}/channels/{_channelId}/messages";

            using (var req = new UnityWebRequest(url, "POST"))
            {
                byte[] body = Encoding.UTF8.GetBytes(json);
                req.uploadHandler = new UploadHandlerRaw(body);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Authorization", $"Bot {_botToken}");
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
                            LogVerbose($"status message posted to channel {_channelId} (msg ID: {_statusMessageId})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Discord] CrashDetector: posted but failed to parse message ID: {ex.Message}");
                    }
                }
                else
                {
                    string errBody = req.downloadHandler?.text ?? req.error;
                    Debug.LogWarning($"[Discord] CrashDetector: failed to post status message ({req.responseCode}): {errBody}");

                    if (req.responseCode == 403)
                    {
                        Debug.LogWarning("[Discord] CrashDetector: 403 Forbidden - the bot does not have Send Messages " +
                                         $"permission in channel {_channelId}. Add the bot to that channel in your " +
                                         "Discord server settings -> Channels -> Permissions.");
                    }
                    else if (req.responseCode == 404)
                    {
                        Debug.LogWarning($"[Discord] CrashDetector: 404 - channel {_channelId} not found. " +
                                         "Check that StatusChannelId (or ChatChannelId) is correct.");
                    }
                }
            }

            // Start heartbeat loop
            _heartbeatCoroutine = _host.StartCoroutine(HeartbeatLoop());
        }

        private static IEnumerator HeartbeatLoop()
        {
            while (!_cleanShutdown)
            {
                yield return new WaitForSecondsRealtime(HeartbeatIntervalSeconds);
                if (_cleanShutdown) yield break;

                // Update sentinel timestamp
                try { WriteSentinel("running"); } catch { }

                // Edit the Discord status message
                if (string.IsNullOrEmpty(_statusMessageId)) continue;

                var embed = BuildOnlineEmbed();
                string json = BuildEmbedPayload(embed);
                string url = $"{DiscordApiBase}/channels/{_channelId}/messages/{_statusMessageId}";

                using (var req = new UnityWebRequest(url, "PATCH"))
                {
                    byte[] body = Encoding.UTF8.GetBytes(json);
                    req.uploadHandler = new UploadHandlerRaw(body);
                    req.downloadHandler = new DownloadHandlerBuffer();
                    req.SetRequestHeader("Authorization", $"Bot {_botToken}");
                    req.SetRequestHeader("Content-Type", "application/json");
                    req.timeout = ShortRequestTimeoutSeconds;

                    yield return req.SendWebRequest();

                    if (req.responseCode == 429)
                    {
                        LogVerbose("Status message edit rate limited");
                    }
                    else if (req.responseCode != 200)
                    {
                        LogVerbose($"Status message edit failed ({req.responseCode}): {req.downloadHandler?.text ?? req.error}");
                    }
                }
            }
        }

        // ============================================================
        // Embed Builders
        // ============================================================

        private static Dictionary<string, object> BuildOnlineEmbed()
        {
            string serverName = GetServerName();

            int playerCount = 0;
            try { playerCount = ZNet.instance?.GetPeers()?.Count ?? 0; } catch { }

            var uptime = DateTime.UtcNow - _serverStartUtc;
            string uptimeStr = uptime.TotalHours >= 1
                ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m"
                : $"{(int)uptime.TotalMinutes}m";

            return new Dictionary<string, object>
            {
                { "title", "\U0001F7E2 Server Online" },
                { "description", $"**{serverName}** is running." },
                { "color", 5763719 }, // Green
                { "fields", new List<object>
                    {
                        new Dictionary<string, object> { { "name", "Players" }, { "value", playerCount.ToString() }, { "inline", true } },
                        new Dictionary<string, object> { { "name", "Uptime" }, { "value", uptimeStr }, { "inline", true } },
                        new Dictionary<string, object> { { "name", "Last Heartbeat" }, { "value", DateTime.UtcNow.ToString("HH:mm:ss") + " UTC" }, { "inline", true } },
                    }
                },
                { "footer", new Dictionary<string, object> { { "text", $"Updates every 60s \u00b7 {serverName} CrashDetector" } } }
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
                { "title", "\U0001F534 Server Offline" },
                { "description", $"**{serverName}** shut down cleanly." },
                { "color", 15548997 }, // Red
                { "fields", new List<object>
                    {
                        new Dictionary<string, object> { { "name", "Session Duration" }, { "value", uptimeStr }, { "inline", true } },
                        new Dictionary<string, object> { { "name", "Stopped At" }, { "value", DateTime.UtcNow.ToString("HH:mm:ss") + " UTC" }, { "inline", true } },
                    }
                },
                { "footer", new Dictionary<string, object> { { "text", $"{serverName} CrashDetector" } } }
            };
        }

        private static string BuildEmbedPayload(Dictionary<string, object> embed)
        {
            var payload = new Dictionary<string, object>
            {
                { "embeds", new List<object> { embed } }
            };
            return JsonConvert.SerializeObject(payload);
        }

        // ============================================================
        // Synchronous edit for OnApplicationQuit
        // ============================================================

        private static void EditStatusMessageSync(string messageId, Dictionary<string, object> embed)
        {
            string json = BuildEmbedPayload(embed);
            string url = $"{DiscordApiBase}/channels/{_channelId}/messages/{messageId}";

            try
            {
                using (var client = new System.Net.WebClient())
                {
                    client.Headers.Add("Authorization", $"Bot {_botToken}");
                    client.Headers.Add("Content-Type", "application/json");
                    client.UploadString(url, "PATCH", json);
                }
            }
            catch { }
        }

        // ============================================================
        // Sentinel file
        // ============================================================

        private static void InitSentinel()
        {
            try
            {
                string dir = Path.Combine(BepInEx.Paths.ConfigPath, "FiresRPGmaker");
                Directory.CreateDirectory(dir);
                _sentinelPath = Path.Combine(dir, SentinelFileName);
                WriteSentinel("running");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] CrashDetector sentinel write failed: {ex.Message}");
            }
        }

        private static void WriteSentinel(string status)
        {
            if (string.IsNullOrEmpty(_sentinelPath)) return;
            int pid = Process.GetCurrentProcess().Id;
            string json = $"{{\"status\":\"{status}\",\"heartbeat\":\"{DateTime.UtcNow:O}\",\"pid\":{pid}}}";
            File.WriteAllText(_sentinelPath, json);
        }

        // ============================================================
        // Background Crash Notifier (uses webhook URL - no bot needed)
        // ============================================================

        private static void SpawnCrashNotifier()
        {
            try
            {
                string webhookUrl = DiscordIntegrationConfig.ResolveWebhookUrl(WebhookCategory.General);
                if (!DiscordWebhookService.IsValidWebhookUrl(webhookUrl)) return;
                if (string.IsNullOrEmpty(_sentinelPath)) return;

                int pid = Process.GetCurrentProcess().Id;
                string serverName = GetServerName();

                string sSentinel = _sentinelPath.Replace("'", "''");
                string sWebhook = webhookUrl.Replace("'", "''");
                string sName = serverName.Replace("'", "''").Replace("\"", "\\\"");
                string sDisplay = (DiscordIntegrationConfig.WebhookDisplayName?.Value ?? "Valheim Server").Replace("'", "''");

                string ps =
                    "try{ (Get-Process -Id " + pid + " -EA SilentlyContinue).WaitForExit() }catch{}\n" +
                    "Start-Sleep 3\n" +
                    "$s=Get-Content '" + sSentinel + "' -Raw -EA SilentlyContinue\n" +
                    "if($s -notmatch '\"running\"'){exit}\n" +
                    "$t=(Get-Date).ToUniversalTime().ToString('HH:mm:ss')\n" +
                    "$emoji=[char]::ConvertFromUtf32(0x1F4A5)\n" +
                    "$b=@{username='" + sDisplay + "';embeds=@(@{title=\"\"$emoji Server Crash Detected\"\";description=\"\"**" + sName + "** crashed or was killed.\"\";color=15105570;fields=@(@{name='Detected At';value=\"\"$t UTC\"\";inline=$true});footer=@{text='" + sName + " CrashDetector'}})}|ConvertTo-Json -Depth 5 -Compress\n" +
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
                    LogVerbose($"background notifier spawned (watchdog PID {proc.Id}, monitoring server PID {pid})");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] CrashDetector: failed to spawn crash notifier: {ex.Message}");
            }
        }

        // ============================================================
        // Helpers
        // ============================================================

        private static string GetServerName() => FiresDiscordIntegration.Utilities.ServerNameResolver.GetOperatorServerName();

        private static void EnsureHost()
        {
            if (_host != null) return;
            var existing = GameObject.Find("DiscordBotListenerHost");
            if (existing != null)
            {
                _host = existing.GetComponent<MonoBehaviour>();
                if (_host != null) return;
            }
            existing = GameObject.Find("DiscordWebhookHost");
            if (existing != null)
            {
                _host = existing.GetComponent<MonoBehaviour>();
                if (_host != null) return;
            }
            var go = new GameObject("DiscordCrashDetectorHost");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _host = go.AddComponent<CoroutineHost>();
        }

        private static void LogVerbose(string msg)
        {
            if (DiscordIntegrationConfig.VerboseLogging?.Value == true)
                Debug.Log($"[Discord] CrashDetector: {msg}");
        }

        private class CoroutineHost : MonoBehaviour { }
    }
}
