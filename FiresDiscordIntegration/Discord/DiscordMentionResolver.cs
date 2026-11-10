using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace FiresDiscordIntegration.Discord
{
    /// <summary>
    /// Resolves @mentions in chat messages to Discord mention syntax.
    ///
    /// When a player types "@admin" or "@everyone" in-game chat, this converts
    /// it to the proper Discord format so the webhook triggers an actual ping:
    ///   @rolename  -> &lt;@&amp;ROLE_ID&gt;
    ///   @everyone  -> @everyone (already works natively)
    ///   @here      -> @here (already works natively)
    ///
    /// Role names are fetched from the Discord API using the bot token and
    /// cached. Matching is case-insensitive.
    /// </summary>
    public static class DiscordMentionResolver
    {
        private const int ShortRequestTimeoutSeconds = 10;
        private const int RequestTimeoutSeconds = 15;
        // How often a caller waiting for the role/member cache re-checks it.
        private const float CacheWarmPollSeconds = 0.2f;

        private const string DiscordApiBase = "https://discord.com/api/v10";

        // Cached role name -> role ID mapping (lowercase name -> ID string)
        private static Dictionary<string, string> _roleMap;
        private static bool _rolesFetched;
        private static bool _fetchInProgress;

        // Cached user display name -> user ID mapping for @user resolution.
        // Display name precedence is nick > global_name > username (mirrors
        // what players actually see / type in Discord).  Multiple users with
        // the same display name collapse to first-write-wins; the hint
        // overlay will at least make this visible to the player.
        private static Dictionary<string, string> _userMap;
        private static bool _membersFetched;

        // Role color + position by role ID, captured during the role fetch
        // and reused while walking the member list to compute each user's
        // top-coloured-role hex (the same logic Discord uses to render a
        // member's name).  Discord returns role color as a 24-bit RGB int;
        // 0 means "no color, fall through to the role below."
        private struct RolePositionColor
        {
            public int Color;
            public int Position;
        }
        private static Dictionary<string, RolePositionColor> _roleColorPosById;
        // A single supervisor coroutine drives the entire pre-warm: it kicks
        // off a fetch, waits for it to land, and re-tries on failure or when
        // the bot token / guild id hasn't been configured yet.  Tracked here
        // so a second FetchRoles() call (e.g. from a chat @mention before the
        // supervisor has finished a successful fetch) doesn't spawn a
        // duplicate worker.  ClearCache() resets the flag so the supervisor
        // can restart cleanly across a server-stop / server-start cycle.
        private static bool _supervisorStarted;

        // Interval between supervisor attempts when the cache is still cold.
        // Short enough that an admin who set the bot token / guild id after
        // server boot only waits a short time before the first @mention can
        // resolve; long enough that a misconfigured server isn't spamming
        // Discord's /guilds/{id}/roles endpoint.
        private const float SupervisorRetrySeconds = 30f;

        // How long between successful refreshes — even after the cache is
        // warm we re-fetch periodically so role renames on the Discord side
        // get picked up without a server restart.  30 minutes is comfortably
        // below the typical "I renamed a role and want it to mention" admin
        // patience window and well above Discord's per-route rate limit.
        private const float SupervisorRefreshSeconds = 30f * 60f;

        /// <summary>
        /// Resolve @mentions in a message string to Discord mention syntax.
        /// </summary>
        /// <summary>
        /// Result of an @mention resolution pass.  <see cref="Message"/> is the rewritten
        /// text with every recognised <c>@name</c> converted to Discord's
        /// <c>&lt;@&amp;ROLE_ID&gt;</c> (role) or <c>&lt;@USER_ID&gt;</c> (user) syntax.
        /// <see cref="ResolvedRoleIds"/> is the list of role IDs that conversion
        /// produced — pass these as Discord's <c>allowed_mentions.roles</c> on the
        /// webhook POST so the role actually pings, even when the role is marked as
        /// non-mentionable on the Discord side.  <see cref="ResolvedUserIds"/> is the
        /// matching list for user mentions; user pings are allowed by Discord's
        /// default parse-list policy, but we surface the IDs so callers that want
        /// strict allowlisting can use them.  Empty lists mean there was nothing to
        /// resolve (or no match was found) and the caller can fall back to default
        /// <c>allowed_mentions</c>.
        /// </summary>
        public struct MentionResolutionResult
        {
            public string Message;
            public IReadOnlyList<string> ResolvedRoleIds;
            public IReadOnlyList<string> ResolvedUserIds;
        }

        /// <summary>
        /// True once the role cache has been populated from the Discord API at least
        /// once this session.  False while the very first fetch is still in flight (or
        /// before it has been kicked off).  Read by callers that want to defer chat
        /// relays until the cache is warm so the very first @mention of a session
        /// resolves to a real ping instead of going out as literal text.
        /// </summary>
        public static bool HasRoles
            => _rolesFetched && _roleMap != null && _roleMap.Count > 0;

        /// <summary>
        /// True once the member fetch has run at least once this session,
        /// regardless of outcome (success, 403 from missing intent, parse
        /// error, etc.).  The chat-relay wait uses this as its "we've had a
        /// chance to populate user mentions" signal: a guild without the
        /// Server Members intent grants no users, but we still don't want
        /// every @-message to burn the full wait timeout — the supervisor
        /// will keep retrying, but the in-flight chat send has nothing
        /// further to gain by blocking.
        /// </summary>
        public static bool HasAttemptedMembersFetch => _membersFetched;

        /// <summary>
        /// True once the user cache has actually been populated from a
        /// successful member fetch (distinct from
        /// <see cref="HasAttemptedMembersFetch"/>, which can be true with an
        /// empty cache when the bot is missing the Server Members intent).
        /// </summary>
        public static bool HasUsers
            => _membersFetched && _userMap != null && _userMap.Count > 0;

        /// <summary>
        /// Coroutine that ensures the mention-target caches are warm before
        /// invoking <paramref name="onReady"/>.  Waits until BOTH the role
        /// fetch has populated and the member fetch has at least attempted
        /// once (success or graceful failure), with a hard timeout so a
        /// misconfigured server can't hang chat indefinitely.  The callback
        /// is invoked exactly once in every case — even on timeout — so the
        /// caller doesn't leak a pending chat-relay coroutine.
        /// </summary>
        public static IEnumerator WaitForRolesThen(float timeoutSeconds, Action onReady)
        {
            if (onReady == null) yield break;

            if (HasRoles && HasAttemptedMembersFetch)
            {
                onReady();
                yield break;
            }

            // Idempotent — if the supervisor coroutine is already running (and
            // mid-retry because the bot token / guild id were still empty at
            // plugin Awake), this is a no-op and we just poll the gates below.
            FetchRoles();

            float deadline = Time.unscaledTime + Mathf.Max(0.1f, timeoutSeconds);
            while (Time.unscaledTime < deadline && !(HasRoles && HasAttemptedMembersFetch))
                yield return new WaitForSecondsRealtime(CacheWarmPollSeconds);

            onReady();
        }

        public static MentionResolutionResult ResolveMentions(string message, bool resolveRoles = true)
        {
            if (string.IsNullOrEmpty(message))
                return EmptyResult(message);
            if (!message.Contains("@"))
                return EmptyResult(message);

            // Idempotent: re-arms the supervisor if it's not running yet so an
            // @mention typed before the very first fetch lands can still
            // trigger the cold start.
            FetchRoles();

            bool haveRoles = _roleMap != null && _roleMap.Count > 0;
            bool haveUsers = _userMap != null && _userMap.Count > 0;

            if (!haveRoles && !haveUsers)
            {
                Debug.Log("[Discord] @mention detected but both role and user caches are empty (DiscordGuildId " +
                          "/ BotToken not configured, the Server Members privileged intent isn't granted, or the " +
                          "initial fetch has not completed yet). Mention will be sent as literal text this " +
                          "round; subsequent messages will resolve once the fetches land.");
                return EmptyResult(message);
            }

            var resolvedRoleIds = new List<string>();
            var resolvedUserIds = new List<string>();
            // Capture word chars AND hyphens after the '@' so role / user names
            // that contain dashes (e.g. "Co-Owner", "Trial-Mod", "some-handle")
            // get pulled in as one token.  Without the '-', `@co-owner`
            // truncates at "co", prefix-matches "Co-Owner", and leaves "-owner"
            // sitting as literal text after the resolved ping.
            string rewritten = Regex.Replace(message, @"@([\w-]+)", match =>
            {
                string typedName = match.Groups[1].Value;

                // @everyone and @here are server-wide native mentions Discord
                // honours in webhook content — don't rewrite them.
                if (string.Equals(typedName, "everyone", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(typedName, "here", StringComparison.OrdinalIgnoreCase))
                    return match.Value;

                // Roles win ties: a typed token that names both a role and a
                // user (rare but possible) resolves to the role.  Server-wide
                // pings are usually the intent when the same name exists in
                // both buckets.
                if (haveRoles && resolveRoles)
                {
                    string roleId = ResolveRoleIdForTypedName(typedName);
                    if (roleId != null)
                    {
                        if (!resolvedRoleIds.Contains(roleId))
                            resolvedRoleIds.Add(roleId);
                        return $"<@&{roleId}>";
                    }
                }

                if (haveUsers)
                {
                    string userId = ResolveUserIdForTypedName(typedName);
                    if (userId != null)
                    {
                        if (!resolvedUserIds.Contains(userId))
                            resolvedUserIds.Add(userId);
                        return $"<@{userId}>";
                    }
                }

                // Resolution failed.  Log what's available so the operator can
                // see why the mention didn't fire — usually means neither cache
                // has a matching entry.
                int roleCount = _roleMap?.Count ?? 0;
                int userCount = _userMap?.Count ?? 0;
                Debug.Log($"[Discord] @{typedName} did not match any role or user " +
                          $"({roleCount} role(s), {userCount} user(s) in cache). Type a prefix/substring of an " +
                          $"existing role or user display name, or check the bot has the Server Members " +
                          $"privileged intent for user resolution to work.");
                return match.Value;
            });

            return new MentionResolutionResult
            {
                Message         = rewritten,
                ResolvedRoleIds = resolvedRoleIds,
                ResolvedUserIds = resolvedUserIds,
            };
        }

        private static MentionResolutionResult EmptyResult(string message)
            => new MentionResolutionResult
            {
                Message         = message,
                ResolvedRoleIds = EmptyIds,
                ResolvedUserIds = EmptyIds,
            };

        private static readonly IReadOnlyList<string> EmptyIds = new List<string>(0);

        // Renders a comma-joined preview of role names, capped so a guild with
        // a few dozen roles doesn't dump a huge log line on every mention miss.
        private const int RoleNamePreviewLimit = 8;
        private static string SummariseRoleNames(IEnumerable<string> roleNames)
        {
            var picked = new List<string>(RoleNamePreviewLimit);
            int total = 0;
            foreach (var name in roleNames)
            {
                total++;
                if (picked.Count < RoleNamePreviewLimit) picked.Add(name);
            }
            string head = string.Join(", ", picked);
            return total > picked.Count ? $"{head}, ... ({total - picked.Count} more)" : head;
        }

        // Three-tier role lookup so players don't have to type the exact role name:
        //   1. Exact case-insensitive match on the full role name (fastest, lossless).
        //   2. Prefix match — "@admin" picks up "Admins", "Admin Team", "Administrator".
        //   3. Substring match — "@admin" picks up "Server Admin".
        // When more than one role matches a softer tier, the *shortest* matching role
        // name wins on the assumption that it's the closest to what the player meant.
        // That keeps "@admin" away from a niche "admin-only-pls-no" role when a plain
        // "Admin" role exists.
        private static string ResolveRoleIdForTypedName(string typedName)
        {
            if (_roleMap.TryGetValue(typedName, out string exact))
                return exact;

            string prefixHit = FindShortestMatching(_roleMap, typedName, MatchKind.Prefix);
            if (prefixHit != null) return prefixHit;

            return FindShortestMatching(_roleMap, typedName, MatchKind.Substring);
        }

        // Same 3-tier ranking as role resolution: exact > prefix > substring,
        // with shorter names winning ties on the assumption that they're
        // closer to what the player typed.  Wrapping it in
        // FindShortestMatching keeps the two paths consistent so a behaviour
        // tweak (e.g. "exact wins on case-sensitive only") lands once.
        private static string ResolveUserIdForTypedName(string typedName)
        {
            if (_userMap.TryGetValue(typedName, out string exact))
                return exact;

            string prefixHit = FindShortestMatching(_userMap, typedName, MatchKind.Prefix);
            if (prefixHit != null) return prefixHit;

            return FindShortestMatching(_userMap, typedName, MatchKind.Substring);
        }

        private enum MatchKind { Prefix, Substring }

        // Generic over the underlying name→id map so the same matching loop
        // serves both role and user resolution paths.  The map is expected
        // to use OrdinalIgnoreCase keys (both _roleMap and _userMap do).
        private static string FindShortestMatching(
            Dictionary<string, string> map, string typedName, MatchKind kind)
        {
            string winningName = null;
            string winningId   = null;
            foreach (var kv in map)
            {
                string name = kv.Key;
                bool isHit = kind == MatchKind.Prefix
                    ? name.StartsWith(typedName, StringComparison.OrdinalIgnoreCase)
                    : name.IndexOf(typedName, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isHit) continue;

                if (winningName == null || name.Length < winningName.Length)
                {
                    winningName = name;
                    winningId   = kv.Value;
                }
            }
            return winningId;
        }

        /// <summary>
        /// Idempotent entry point that ensures the role cache stays warm.  The
        /// first call spawns a supervisor coroutine that retries the
        /// <c>/guilds/{id}/roles</c> fetch until it lands (handling the
        /// "operator hadn't set the bot token / guild id yet at server
        /// boot" case) and then refreshes periodically so a Discord-side
        /// role rename is picked up without a server restart.  Subsequent
        /// calls are no-ops while the supervisor is alive.  Safe to call from
        /// plugin Awake — it doesn't need ZNet to be up, just a coroutine
        /// host (which <see cref="EnsureHost"/> provides on demand).
        /// </summary>
        public static void FetchRoles()
        {
            if (_supervisorStarted) return;
            EnsureHost();
            if (_host == null) return;

            _supervisorStarted = true;
            _host.StartCoroutine(SupervisorCoroutine());
        }

        // Drives the entire role-fetch lifecycle on a single coroutine so the
        // retry/refresh state never desyncs from the in-flight HTTP request.
        // Loops forever (subject to ClearCache resetting _supervisorStarted on
        // server shutdown) and breaks the cycle into two phases:
        //
        //   1. Cold-start: spin every SupervisorRetrySeconds until the bot
        //      token + guild id are both configured and the fetch returns at
        //      least one role.  This is the phase that runs at boot time, so
        //      a server with a slightly-delayed config has the cache warm by
        //      the time the first player opens chat.
        //   2. Warm-refresh: once the cache is populated, sleep for
        //      SupervisorRefreshSeconds and refresh.  Catches Discord-side
        //      role renames / additions without needing a server restart.
        //
        // A failed warm-refresh drops back to the cold-start cadence so a
        // transient outage doesn't leave the cache stale-forever.
        private static IEnumerator SupervisorCoroutine()
        {
            // Phase 1 — cold start.
            while (!HasRoles)
            {
                yield return TryFetchOnce();
                if (HasRoles) break;
                yield return new WaitForSecondsRealtime(SupervisorRetrySeconds);
            }

            // Phase 2 — warm refresh loop.
            while (true)
            {
                yield return new WaitForSecondsRealtime(SupervisorRefreshSeconds);
                yield return TryFetchOnce();
                // If the refresh wiped the cache (parse error, 5xx), drop
                // back to phase-1 cadence until we recover.
                while (!HasRoles)
                {
                    yield return new WaitForSecondsRealtime(SupervisorRetrySeconds);
                    yield return TryFetchOnce();
                }
            }
        }

        // One pass of the actual REST calls.  Bails early when the token or
        // guild id are still empty so the supervisor can re-try once the
        // operator finishes configuring the bot.  Roles always fire; the
        // member fetch only fires after roles land (members depend on role
        // metadata for the display-color computation).  A failed member
        // fetch doesn't roll back the role result — they're independent
        // concerns and @role mentions stay useful even when the Server
        // Members intent isn't granted yet.
        private static IEnumerator TryFetchOnce()
        {
            if (_fetchInProgress) yield break;

            string token = FiresDiscordIntegration.Utilities.BotTokenFile.Token;
            string guildId = DiscordIntegrationConfig.DiscordGuildId?.Value;
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(guildId))
                yield break;

            _fetchInProgress = true;
            yield return FetchRolesCoroutine(token, guildId);

            // Member fetch only makes sense when the role fetch succeeded
            // — without role positions we can't compute member display
            // colours, and without role IDs at all the @user resolver
            // would be useless anyway (we wouldn't know who's still in the
            // guild).  Recheck HasRoles here instead of just _rolesFetched
            // because a parse error inside FetchRolesCoroutine flips the
            // flag back to false.
            if (HasRoles)
            {
                _fetchInProgress = true; // FetchRolesCoroutine flipped it off
                yield return FetchMembersCoroutine(token, guildId);
                _fetchInProgress = false;
            }
        }

        private static IEnumerator FetchRolesCoroutine(string token, string guildId)
        {
            string url = $"{DiscordApiBase}/guilds/{guildId}/roles";

            using (var request = UnityWebRequest.Get(url))
            {
                request.SetRequestHeader("Authorization", $"Bot {token}");
                request.timeout = ShortRequestTimeoutSeconds;

                yield return request.SendWebRequest();

                if (request.responseCode == 200)
                {
                    try
                    {
                        var roles = JArray.Parse(request.downloadHandler.text);
                        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        // Built alongside the name→id map so the in-game mention-hint
                        // overlay (on clients) can show role names in their proper
                        // Discord color.  Discord's API returns color as a 24-bit RGB
                        // int; 0 means "no color, inherit lower role".
                        var summaries = new List<DiscordRoleSummary>(roles.Count);
                        // role-id → (color, position) so the upcoming member
                        // fetch can compute each user's display color the same
                        // way Discord does (highest-position role with a
                        // non-zero color).
                        var colorPosById = new Dictionary<string, RolePositionColor>(StringComparer.Ordinal);

                        foreach (var role in roles)
                        {
                            string roleName = role.Value<string>("name");
                            string roleId = role.Value<string>("id");
                            int colorRgb = role.Value<int?>("color") ?? 0;
                            int position = role.Value<int?>("position") ?? 0;

                            if (string.IsNullOrEmpty(roleName) || string.IsNullOrEmpty(roleId)) continue;

                            if (!map.ContainsKey(roleName))
                                map[roleName] = roleId;

                            string colorHex = colorRgb == 0 ? null : string.Format("#{0:X6}", colorRgb);
                            summaries.Add(new DiscordRoleSummary(roleName, colorHex));

                            colorPosById[roleId] = new RolePositionColor
                            {
                                Color    = colorRgb,
                                Position = position,
                            };
                        }

                        _roleMap = map;
                        _roleColorPosById = colorPosById;
                        _rolesFetched = true;

                        // Update the shared client-visible cache and push it to every
                        // connected peer so a player joining mid-session also sees the
                        // hint.  PeerInfo postfix fires its own SendToPeer for late
                        // joiners on the server side; this broadcast covers anyone
                        // who was already connected when the fetch completed.  The
                        // user list is pushed separately once the member fetch
                        // lands; pushing roles immediately means @rolename hints
                        // work even on guilds where the Server Members intent
                        // isn't granted.
                        DiscordRoleNameCache.Set(summaries);
                        DiscordRoleNameSync.BroadcastToAllPeers(summaries);

                        if (map.Count > 0)
                            Debug.Log($"[Discord] Fetched {map.Count} role(s) for @mention resolution: {SummariseRoleNames(map.Keys)}");
                        else
                            Debug.LogWarning("[Discord] Role fetch succeeded but guild has no roles. " +
                                           "@mentions will not resolve to pings.");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Discord] Failed to parse roles: {ex.Message}");
                        _rolesFetched = false; // Allow retry
                    }
                }
                else
                {
                    Debug.LogWarning($"[Discord] Failed to fetch roles ({request.responseCode}): " +
                                     $"{request.downloadHandler?.text ?? request.error}");
                    _rolesFetched = false; // Allow retry
                }

                _fetchInProgress = false;
            }
        }

        // ──── Member fetch ──────────────────────────────────────────────
        // /guilds/{id}/members returns up to 1000 members per page, paged
        // with ?after={highest_user_id_on_previous_page}.  Bot needs the
        // Server Members privileged intent — without it the endpoint
        // returns 403 and we proceed with an empty user map (the
        // BotTokenFile docs already tell operators to flip that toggle).
        //
        // Default page cap of 10 (10 000 members) is a safety valve so a
        // misconfigured / very large guild doesn't loop forever — Discord's
        // hard limit per request is 1000 and the bucket is 10 req/10s, so
        // 10 pages is fast and well within the rate-limit envelope.
        private const int MemberPageSize     = 1000;
        private const int MemberPageHardCap  = 10;
        private const float MemberPageGapSec = 0.5f;

        private static IEnumerator FetchMembersCoroutine(string token, string guildId)
        {
            var nameToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var summaries = new List<DiscordUserSummary>();
            string afterId = "0";
            int pages = 0;

            while (pages < MemberPageHardCap)
            {
                pages++;
                string url = $"{DiscordApiBase}/guilds/{guildId}/members?limit={MemberPageSize}&after={afterId}";

                int membersInPage = 0;
                bool requestFailed = false;
                string highestIdSeen = afterId;

                using (var request = UnityWebRequest.Get(url))
                {
                    request.SetRequestHeader("Authorization", $"Bot {token}");
                    request.timeout = RequestTimeoutSeconds;
                    yield return request.SendWebRequest();

                    if (request.responseCode == 403)
                    {
                        Debug.LogWarning("[Discord] Member fetch returned 403 — the bot is missing the " +
                                         "Server Members privileged intent. Enable it under your bot's " +
                                         "application page in the Discord developer portal (Bot → " +
                                         "Privileged Gateway Intents → SERVER MEMBERS INTENT). @user " +
                                         "resolution will stay disabled until then; @role still works.");
                        requestFailed = true;
                    }
                    else if (request.responseCode != 200)
                    {
                        Debug.LogWarning($"[Discord] Failed to fetch members page {pages} " +
                                         $"({request.responseCode}): " +
                                         $"{request.downloadHandler?.text ?? request.error}");
                        requestFailed = true;
                    }
                    else
                    {
                        JArray members = null;
                        try { members = JArray.Parse(request.downloadHandler.text); }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[Discord] Failed to parse members page {pages}: {ex.Message}");
                            requestFailed = true;
                        }

                        if (members != null)
                        {
                            membersInPage = members.Count;
                            foreach (var memberTok in members)
                            {
                                var member = memberTok as JObject;
                                if (member == null) continue;

                                var user = member["user"] as JObject;
                                if (user == null) continue;

                                string userId = user.Value<string>("id");
                                if (string.IsNullOrEmpty(userId)) continue;

                                // Track the highest user ID in this page for
                                // the next request's `after` cursor.  Discord
                                // returns ID strings of arbitrary digit length;
                                // Ordinal compare works because all IDs are
                                // the same length (snowflakes) for any
                                // realistic guild.
                                if (string.Compare(userId, highestIdSeen, StringComparison.Ordinal) > 0)
                                    highestIdSeen = userId;

                                // Skip bots (including our own bot) so the hint
                                // doesn't surface bot accounts as mention
                                // targets; nobody types "@MEE6" expecting a
                                // ping that goes anywhere useful.
                                bool isBot = user.Value<bool?>("bot") ?? false;
                                if (isBot) continue;

                                string displayName = BestDisplayName(member, user);
                                if (string.IsNullOrEmpty(displayName)) continue;

                                // First-write-wins on display-name collisions
                                // (two players using the same nickname).  Rare
                                // but possible; the hint overlay will still
                                // show only the cached one, and the operator
                                // can disambiguate by typing more of the name
                                // until a unique substring is reached.
                                if (!nameToId.ContainsKey(displayName))
                                    nameToId[displayName] = userId;

                                string colorHex = ComputeMemberColorHex(member["roles"] as JArray);
                                summaries.Add(new DiscordUserSummary(displayName, colorHex));
                            }
                        }
                    }
                }

                if (requestFailed)
                {
                    // Mark the attempt complete (so the supervisor moves on
                    // to the refresh loop) but DON'T overwrite a previously
                    // good cache with an empty result — keeps the last
                    // successful snapshot live until the next refresh tick.
                    if (_userMap == null)
                    {
                        _userMap = nameToId;
                        DiscordRoleNameCache.SetUsers(summaries);
                        DiscordRoleNameSync.BroadcastUsersToAllPeers(summaries);
                    }
                    _membersFetched = true;
                    yield break;
                }

                // Last page when we got fewer members than the requested page
                // size, or when no new IDs came in (defensive — Discord
                // shouldn't ever return the same page twice).
                if (membersInPage < MemberPageSize) break;
                if (highestIdSeen == afterId) break;

                afterId = highestIdSeen;
                yield return new WaitForSecondsRealtime(MemberPageGapSec);
            }

            _userMap = nameToId;
            _membersFetched = true;
            DiscordRoleNameCache.SetUsers(summaries);
            DiscordRoleNameSync.BroadcastUsersToAllPeers(summaries);

            if (pages >= MemberPageHardCap && summaries.Count >= MemberPageHardCap * MemberPageSize)
                Debug.LogWarning($"[Discord] Member fetch stopped at the {MemberPageHardCap}-page hard cap " +
                                 $"({MemberPageHardCap * MemberPageSize} members). Larger guilds will need " +
                                 $"this cap raised.");
            else
                Debug.Log($"[Discord] Fetched {summaries.Count} user(s) across {pages} page(s) for @user resolution.");
        }

        // Pick the best label for an @mention target: server-specific nickname
        // wins (it's what other members see in the channel), then the user's
        // self-chosen global display name, then their unique username as a
        // last resort.  Matches the resolution order Discord clients use
        // when rendering mentions, so what the player types tends to match
        // what they see in chat.
        private static string BestDisplayName(JObject member, JObject user)
        {
            string nick = member.Value<string>("nick");
            if (!string.IsNullOrEmpty(nick)) return nick;

            string globalName = user.Value<string>("global_name");
            if (!string.IsNullOrEmpty(globalName)) return globalName;

            return user.Value<string>("username");
        }

        // Discord's display-color rule: the member renders in the color of
        // their highest-position role with a non-zero color value.  We
        // computed (color, position) per role during the role fetch and
        // stash it in _roleColorPosById, so this loop is a single scan over
        // the member's role IDs.  Returns null when the member has no
        // colored role (the hint overlay falls back to its neutral grey).
        private static string ComputeMemberColorHex(JArray memberRoleIds)
        {
            if (memberRoleIds == null || _roleColorPosById == null) return null;

            int highestPosition = -1;
            int colorAtHighest  = 0;

            foreach (var roleIdTok in memberRoleIds)
            {
                string roleId = roleIdTok?.ToString();
                if (string.IsNullOrEmpty(roleId)) continue;
                if (!_roleColorPosById.TryGetValue(roleId, out var rpc)) continue;
                if (rpc.Color == 0) continue;
                if (rpc.Position <= highestPosition) continue;

                highestPosition = rpc.Position;
                colorAtHighest  = rpc.Color;
            }

            return highestPosition < 0 ? null : string.Format("#{0:X6}", colorAtHighest);
        }

        /// <summary>
        /// Clear cached roles + users (e.g. on server shutdown).  Also
        /// re-arms <see cref="_supervisorStarted"/> so the next
        /// <see cref="FetchRoles"/> call can spawn a fresh supervisor
        /// coroutine — important across a server-stop / server-start cycle on
        /// a listen host where the plugin process keeps running but ZNet
        /// tears down and re-comes-up.
        /// </summary>
        public static void ClearCache()
        {
            _roleMap = null;
            _roleColorPosById = null;
            _rolesFetched = false;
            _userMap = null;
            _membersFetched = false;
            _fetchInProgress = false;
            _supervisorStarted = false;
        }

        private static MonoBehaviour _host;

        /// <summary>
        /// Returns the MonoBehaviour the resolver uses for coroutines so callers
        /// (e.g. the chat-relay path waiting on role readiness) can park their
        /// own coroutines on the same host instead of allocating a second one.
        /// </summary>
        internal static MonoBehaviour CoroutineHostInstance()
        {
            EnsureHost();
            return _host;
        }

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
            var go = new GameObject("DiscordMentionResolverHost");
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

        private class CoroutineHost : MonoBehaviour { }
    }
}
