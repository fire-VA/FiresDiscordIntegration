using System;
using System.Collections;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace FiresDiscordIntegration.Discord
{
    /// <summary>
    /// Resolves the configured Discord bot's display name + avatar URL via
    /// <c>GET /users/@me</c> and caches them, so every webhook post in the mod can
    /// default to the bot's *actual* identity rather than Discord's generic
    /// "Captain Hook" robot avatar.
    ///
    /// Why this exists:
    ///   - The old DiscordCrashDetector posted via the bot API
    ///     (<c>POST /channels/{id}/messages</c> with <c>Authorization: Bot {token}</c>)
    ///     so Discord rendered the message AS the bot, with the bot's profile icon.
    ///   - Most other Discord traffic (player join/leave, chat relay, hourly summary,
    ///     anti-cheat kicks, client log snapshots) goes through webhooks, where
    ///     Discord uses the *webhook's* avatar unless the payload sets
    ///     <c>avatar_url</c> + <c>username</c>. With nothing set, Discord defaults to
    ///     the generic robot icon and "Captain Hook" name - a different look than
    ///     the bot-API messages.
    ///   - This resolver fetches the bot's real avatar URL + username once and
    ///     caches them, so callers can pass them as defaults to webhook posts and
    ///     get a consistent bot identity across every Discord surface in the mod.
    ///
    /// Usage:
    ///   <code>
    ///   string username = DiscordBotIdentity.ResolveUsername(fallback: "Valheim Server");
    ///   string avatarUrl = DiscordBotIdentity.ResolveAvatarUrl(fallback: null);
    ///   DiscordWebhookService.SendEmbed(url, embed, username, avatarUrl);
    ///   </code>
    /// </summary>
    public static class DiscordBotIdentity
    {
        private const int RequestTimeoutSeconds = 10;

        private const string DiscordApiBase = "https://discord.com/api/v10";

        // Cached results from the first successful @me query.
        private static string _cachedUsername;
        private static string _cachedAvatarUrl;
        private static bool   _resolved;
        private static bool   _resolveInFlight;

        // Re-resolve if the bot token changes (admin edits config) - we keep a copy
        // of the token used to populate the cache so we can detect that.
        private static string _cachedForToken;

        /// <summary>
        /// Returns the cached bot username if we've successfully fetched it,
        /// otherwise <paramref name="fallback"/>.  Never blocks; if the cache is
        /// empty this kicks off an async fetch and returns the fallback for now.
        /// </summary>
        public static string ResolveUsername(string fallback)
        {
            EnsureResolved();
            if (_resolved && !string.IsNullOrEmpty(_cachedUsername))
                return _cachedUsername;
            return fallback;
        }

        /// <summary>
        /// Returns the cached bot avatar URL if we've successfully fetched it,
        /// otherwise <paramref name="fallback"/>.
        /// </summary>
        public static string ResolveAvatarUrl(string fallback)
        {
            EnsureResolved();
            if (_resolved && !string.IsNullOrEmpty(_cachedAvatarUrl))
                return _cachedAvatarUrl;
            return fallback;
        }

        /// <summary>
        /// Force a refresh on the next call - used when the admin changes the
        /// bot token at runtime.
        /// </summary>
        public static void Invalidate()
        {
            _resolved = false;
            _resolveInFlight = false;
            _cachedUsername = null;
            _cachedAvatarUrl = null;
            _cachedForToken = null;
        }

        // ── Internal ────────────────────────────────────────────────────────

        private static void EnsureResolved()
        {
            string token = FiresDiscordIntegration.Utilities.BotTokenFile.Token;
            if (string.IsNullOrEmpty(token)) return;

            // If the token changed, re-resolve.
            if (_resolved && _cachedForToken != token)
            {
                Invalidate();
            }

            if (_resolved || _resolveInFlight) return;
            _resolveInFlight = true;
            _cachedForToken = token;

            // Need a MonoBehaviour to host the coroutine.  Prefer the plugin instance
            // (always alive from BepInEx Awake onwards) so we can pre-warm the cache
            // BEFORE ZNet exists - by the time the first webhook fires (DiscordIntegrationCore
            // .OnServerStart from ZNet.LoadWorld) we've already had several seconds to
            // resolve.  Falls back to ZNet.instance if the plugin reference is somehow
            // missing.
            MonoBehaviour host = (MonoBehaviour)FiresDiscordIntegrationPlugin.Instance ?? ZNet.instance;
            if (host == null)
            {
                _resolveInFlight = false;
                return;
            }

            host.StartCoroutine(FetchIdentityCoroutine(token));
        }

        private static IEnumerator FetchIdentityCoroutine(string token)
        {
            string url = $"{DiscordApiBase}/users/@me";
            using (var req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("Authorization", $"Bot {token}");
                req.timeout = RequestTimeoutSeconds;
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        var json = JObject.Parse(req.downloadHandler?.text ?? "{}");
                        string id = json.Value<string>("id");
                        string username = json.Value<string>("username");
                        string avatarHash = json.Value<string>("avatar");

                        _cachedUsername = string.IsNullOrEmpty(username) ? null : username;

                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(avatarHash))
                        {
                            // Discord avatar CDN format. .gif if hash starts with "a_",
                            // otherwise .png. We always grab .png for max compatibility.
                            _cachedAvatarUrl = $"https://cdn.discordapp.com/avatars/{id}/{avatarHash}.png?size=128";
                        }

                        _resolved = true;
                        Debug.Log($"[DiscordBotIdentity] Resolved bot identity: username='{_cachedUsername}', avatar={(string.IsNullOrEmpty(_cachedAvatarUrl) ? "<none>" : "<set>")}");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[DiscordBotIdentity] Failed to parse @me response: {ex.Message}");
                    }
                }
                else
                {
                    Debug.LogWarning($"[DiscordBotIdentity] /users/@me failed ({req.responseCode}): {req.error}");
                }
            }

            _resolveInFlight = false;
        }
    }
}
