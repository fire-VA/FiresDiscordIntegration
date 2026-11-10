using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace FiresDiscordIntegration.Discord
{
    /// <summary>
    /// Resolves a Discord user's display color from their guild member roles.
    /// Discord renders a member's name using the color of their highest-position
    /// role that has a non-zero color value; this class replicates that on the
    /// server so we can wrap relayed chat usernames with the same color.
    ///
    /// Two REST endpoints are involved:
    ///   GET /guilds/{guild.id}/roles                — fetched once per session,
    ///       gives us (id, color, position) for every role in the guild.
    ///   GET /guilds/{guild.id}/members/{user.id}    — fetched per author the
    ///       first time we see them, gives us the role IDs assigned to them.
    /// Both are cached for the rest of the session.  ClearCache() is called from
    /// DiscordBotListener.Stop().
    /// </summary>
    public static class DiscordRoleColorResolver
    {
        private const string DiscordApiBase           = "https://discord.com/api/v10";
        private const int    DiscordRestTimeoutSec    = 10;
        private const int    HttpOk                   = 200;
        private const int    NoColorSentinel          = 0;          // Discord uses 0 to mean "fall through to a lower role's color"
        private const float  RolesFetchRetryCooldownSec = 30f;      // throttle retries after a failed roles fetch
        private const string ColorHexFormat           = "#{0:X6}";

        private struct RoleColor
        {
            public int Color;
            public int Position;
        }

        private static Dictionary<string, RoleColor> _rolesById;
        private static bool  _rolesFetched;
        private static bool  _rolesFetchInProgress;
        private static float _rolesFetchLastAttempt;

        // Per-user cache.  Value may be null — that's a CACHED NEGATIVE result
        // meaning the user has no colored role, distinct from "we haven't asked
        // yet" (key absent).  ContainsKey is the way to distinguish.
        private static readonly Dictionary<string, string> _userColorHexById =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Returns the cached display-color hex (e.g. "#7289DA") for a Discord
        /// user, or null if the user has no colored role.  Returns false when
        /// we haven't fetched member info for this user yet — call
        /// <see cref="EnsureUserFetched"/> first and yield on it.
        /// </summary>
        public static bool TryGetUserColorHex(string userId, out string hex)
        {
            hex = null;
            if (string.IsNullOrEmpty(userId)) return false;
            return _userColorHexById.TryGetValue(userId, out hex);
        }

        /// <summary>
        /// Coroutine: fetch the guild's role catalog (once per session) and
        /// then the given user's member info (once per user per session),
        /// caching the resulting color hex.  Yields until both fetches are
        /// done; subsequent calls for the same user are no-ops.
        /// </summary>
        public static IEnumerator EnsureUserFetched(string userId)
        {
            if (string.IsNullOrEmpty(userId)) yield break;
            if (_userColorHexById.ContainsKey(userId)) yield break;

            yield return EnsureRolesFetched();
            if (!_rolesFetched || _rolesById == null) yield break;

            string token   = FiresDiscordIntegration.Utilities.BotTokenFile.Token;
            string guildId = DiscordIntegrationConfig.DiscordGuildId?.Value;
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(guildId)) yield break;

            yield return FetchMember(token, guildId, userId);
        }

        public static void ClearCache()
        {
            _rolesById              = null;
            _rolesFetched           = false;
            _rolesFetchInProgress   = false;
            _rolesFetchLastAttempt  = 0f;
            _userColorHexById.Clear();
        }

        private static IEnumerator EnsureRolesFetched()
        {
            if (_rolesFetched)            yield break;
            if (_rolesFetchInProgress)    yield break;
            if (Time.unscaledTime - _rolesFetchLastAttempt < RolesFetchRetryCooldownSec)
                yield break;

            string token   = FiresDiscordIntegration.Utilities.BotTokenFile.Token;
            string guildId = DiscordIntegrationConfig.DiscordGuildId?.Value;
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(guildId)) yield break;

            _rolesFetchInProgress  = true;
            _rolesFetchLastAttempt = Time.unscaledTime;
            try
            {
                yield return FetchRoles(token, guildId);
            }
            finally
            {
                _rolesFetchInProgress = false;
            }
        }

        private static IEnumerator FetchRoles(string token, string guildId)
        {
            string url = $"{DiscordApiBase}/guilds/{guildId}/roles";
            using (var req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("Authorization", $"Bot {token}");
                req.timeout = DiscordRestTimeoutSec;
                yield return req.SendWebRequest();

                if (req.responseCode != HttpOk)
                {
                    Debug.LogWarning($"[Discord] RoleColor: roles fetch failed ({req.responseCode}): " +
                                     $"{req.downloadHandler?.text ?? req.error}");
                    yield break;
                }

                JArray rolesJson;
                try { rolesJson = JArray.Parse(req.downloadHandler.text); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Discord] RoleColor: roles parse failed: {ex.Message}");
                    yield break;
                }

                var map = new Dictionary<string, RoleColor>(StringComparer.Ordinal);
                foreach (var role in rolesJson)
                {
                    string id = role.Value<string>("id");
                    if (string.IsNullOrEmpty(id)) continue;
                    map[id] = new RoleColor
                    {
                        Color    = role.Value<int?>("color")    ?? NoColorSentinel,
                        Position = role.Value<int?>("position") ?? 0,
                    };
                }
                _rolesById    = map;
                _rolesFetched = true;
            }
        }

        private static IEnumerator FetchMember(string token, string guildId, string userId)
        {
            string url = $"{DiscordApiBase}/guilds/{guildId}/members/{userId}";
            using (var req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("Authorization", $"Bot {token}");
                req.timeout = DiscordRestTimeoutSec;
                yield return req.SendWebRequest();

                // 403 = bot lacks the Server Members intent / scope.
                // 404 = user isn't in this guild (e.g. left after posting).
                // Either way, we leave the cache empty and let a future message retry.
                if (req.responseCode != HttpOk)
                {
                    Debug.LogWarning($"[Discord] RoleColor: member {userId} fetch failed ({req.responseCode}): " +
                                     $"{req.downloadHandler?.text ?? req.error}");
                    yield break;
                }

                JObject member;
                try { member = JObject.Parse(req.downloadHandler.text); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Discord] RoleColor: member parse failed: {ex.Message}");
                    yield break;
                }

                var roleIds = member["roles"] as JArray;
                _userColorHexById[userId] = roleIds == null ? null : ComputeDisplayColorHex(roleIds);
            }
        }

        private static string ComputeDisplayColorHex(JArray memberRoleIds)
        {
            int highestPosition = -1;
            int colorAtHighest  = NoColorSentinel;

            foreach (var roleIdToken in memberRoleIds)
            {
                string roleId = roleIdToken?.ToString();
                if (string.IsNullOrEmpty(roleId)) continue;
                if (!_rolesById.TryGetValue(roleId, out RoleColor role)) continue;
                if (role.Color == NoColorSentinel) continue;
                if (role.Position <= highestPosition) continue;

                highestPosition = role.Position;
                colorAtHighest  = role.Color;
            }

            return highestPosition < 0 ? null : string.Format(ColorHexFormat, colorAtHighest);
        }
    }
}
