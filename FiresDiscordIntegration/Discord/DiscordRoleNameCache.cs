using System;
using System.Collections.Generic;

namespace FiresDiscordIntegration.Discord
{
    /// <summary>
    /// One Discord role's data as seen from inside Valheim: just the name and the
    /// hex color used to render the name in chat (e.g. "#7289DA").  IDs are kept
    /// server-side only — clients never need them because they don't talk to the
    /// Discord API directly.  ColorHex is null when Discord reports the role as
    /// color 0 (the "no color, inherit from role-below-this-one" sentinel).
    /// </summary>
    public readonly struct DiscordRoleSummary
    {
        public string Name     { get; }
        public string ColorHex { get; }

        public DiscordRoleSummary(string name, string colorHex)
        {
            Name     = name ?? string.Empty;
            ColorHex = string.IsNullOrEmpty(colorHex) ? null : colorHex;
        }
    }

    /// <summary>
    /// One Discord user as seen by the @-mention hint: the best display name
    /// to type after the '@' (nickname > global_name > username) plus the
    /// color the user renders with in Discord (computed from their highest-
    /// position role with a non-zero color).  Identical shape to
    /// <see cref="DiscordRoleSummary"/> but kept as a separate type so the
    /// resolver can hand the right list to the right consumer without an
    /// accidental cross-up between role IDs and user IDs.
    /// </summary>
    public readonly struct DiscordUserSummary
    {
        public string Name     { get; }
        public string ColorHex { get; }

        public DiscordUserSummary(string name, string colorHex)
        {
            Name     = name ?? string.Empty;
            ColorHex = string.IsNullOrEmpty(colorHex) ? null : colorHex;
        }
    }

    /// <summary>
    /// Shared cache of Discord mention targets (role names + user display
    /// names, with their respective colors).  Lives on both server and client:
    /// the server populates it from the bot's <c>/guilds/{id}/roles</c> +
    /// <c>/guilds/{id}/members</c> fetches and pushes via ZRoutedRpc; clients
    /// receive that broadcast and store it here so the in-game mention-hint
    /// overlay has something to filter.
    ///
    /// Reads are lock-free — both lists are replaced wholesale on each refresh
    /// (typical case is one refresh per session + one push per connecting
    /// client) so no in-place mutation races with readers.
    /// </summary>
    public static class DiscordRoleNameCache
    {
        private static readonly IReadOnlyList<DiscordRoleSummary> EmptyRoles = new List<DiscordRoleSummary>(0);
        private static readonly IReadOnlyList<DiscordUserSummary> EmptyUsers = new List<DiscordUserSummary>(0);

        private static IReadOnlyList<DiscordRoleSummary> _roles = EmptyRoles;
        private static IReadOnlyList<DiscordUserSummary> _users = EmptyUsers;

        public static IReadOnlyList<DiscordRoleSummary> Roles => _roles;
        public static IReadOnlyList<DiscordUserSummary> Users => _users;

        public static void Set(IReadOnlyList<DiscordRoleSummary> roles)
        {
            _roles = roles ?? EmptyRoles;
        }

        /// <summary>
        /// Replace the user-name cache wholesale.  Called by the server after
        /// a <c>/guilds/{id}/members</c> fetch lands, and by clients after
        /// the role-name sync RPC delivers the user list alongside the role
        /// list.
        /// </summary>
        public static void SetUsers(IReadOnlyList<DiscordUserSummary> users)
        {
            _users = users ?? EmptyUsers;
        }

        public static void Clear()
        {
            _roles = EmptyRoles;
            _users = EmptyUsers;
        }

        /// <summary>
        /// Filters the cached mention targets (roles + users) for the in-game
        /// hint overlay.  Same 3-tier ranking the server-side resolver uses so
        /// the hint preview matches what will actually ping once the player
        /// hits Enter:
        ///   1. Exact case-insensitive match (roles, then users)
        ///   2. Names that start with the typed text (roles, then users)
        ///   3. Names that contain the typed text (roles, then users)
        /// Within tiers, shorter names sort first (assumed closer to the
        /// player's intent — "Admin" beats "admin-only-pls-no" for "@adm").
        /// Roles get a slight ordering preference over users so the (usually
        /// smaller, broader-reach) role list isn't drowned out when a guild
        /// has a lot of members.  Returns <see cref="DiscordRoleSummary"/>
        /// for hint display — the user-vs-role distinction is irrelevant for
        /// the panel (it just renders coloured "@name" rows).
        /// </summary>
        public static List<DiscordRoleSummary> FilterForHint(string typedAfterAt, int maxResults)
        {
            var rolesSnap = _roles;
            var usersSnap = _users;
            if (maxResults <= 0 || (rolesSnap.Count == 0 && usersSnap.Count == 0))
                return new List<DiscordRoleSummary>(0);

            // No filter text: show roles first (small set, broader reach),
            // then users alphabetical, capped at maxResults.  Avoids dumping
            // a full member list the moment the player types '@'.
            if (string.IsNullOrEmpty(typedAfterAt))
                return TakeAlphabeticalCombined(rolesSnap, usersSnap, maxResults);

            var exact     = new List<DiscordRoleSummary>();
            var prefix    = new List<DiscordRoleSummary>();
            var substring = new List<DiscordRoleSummary>();

            // Roles first within each tier (see method-level comment for why).
            CollectMatches(rolesSnap, typedAfterAt, exact, prefix, substring);
            CollectUserMatches(usersSnap, typedAfterAt, exact, prefix, substring);

            SortByLengthThenName(prefix);
            SortByLengthThenName(substring);

            var result = new List<DiscordRoleSummary>(maxResults);
            AppendUpTo(result, exact,     maxResults);
            AppendUpTo(result, prefix,    maxResults);
            AppendUpTo(result, substring, maxResults);
            return result;
        }

        private static void CollectMatches(
            IReadOnlyList<DiscordRoleSummary> source, string typed,
            List<DiscordRoleSummary> exact,
            List<DiscordRoleSummary> prefix,
            List<DiscordRoleSummary> substring)
        {
            foreach (var item in source)
            {
                if (string.IsNullOrEmpty(item.Name)) continue;
                if (string.Equals(item.Name, typed, StringComparison.OrdinalIgnoreCase))
                {
                    exact.Add(item);
                    continue;
                }
                if (item.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                {
                    prefix.Add(item);
                    continue;
                }
                if (item.Name.IndexOf(typed, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    substring.Add(item);
                }
            }
        }

        // Wraps user summaries in DiscordRoleSummary on the way into the
        // hint bucket so the hint UI can iterate one type.  Tiny allocation
        // — only called for matches, not for the full member list.
        private static void CollectUserMatches(
            IReadOnlyList<DiscordUserSummary> source, string typed,
            List<DiscordRoleSummary> exact,
            List<DiscordRoleSummary> prefix,
            List<DiscordRoleSummary> substring)
        {
            foreach (var user in source)
            {
                if (string.IsNullOrEmpty(user.Name)) continue;
                var view = new DiscordRoleSummary(user.Name, user.ColorHex);
                if (string.Equals(user.Name, typed, StringComparison.OrdinalIgnoreCase))
                {
                    exact.Add(view);
                    continue;
                }
                if (user.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                {
                    prefix.Add(view);
                    continue;
                }
                if (user.Name.IndexOf(typed, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    substring.Add(view);
                }
            }
        }

        private static List<DiscordRoleSummary> TakeAlphabeticalCombined(
            IReadOnlyList<DiscordRoleSummary> roles,
            IReadOnlyList<DiscordUserSummary> users,
            int max)
        {
            var result = new List<DiscordRoleSummary>(max);

            // Always show roles first.  On a large guild a max-8 hint full of
            // users would never surface @everyone / @here / staff roles to
            // the player; reserving the role slots prevents that.
            var rolesCopy = new List<DiscordRoleSummary>(roles);
            rolesCopy.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var r in rolesCopy)
            {
                if (result.Count >= max) return result;
                result.Add(r);
            }

            var usersCopy = new List<DiscordUserSummary>(users);
            usersCopy.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var u in usersCopy)
            {
                if (result.Count >= max) return result;
                result.Add(new DiscordRoleSummary(u.Name, u.ColorHex));
            }
            return result;
        }

        private static void SortByLengthThenName(List<DiscordRoleSummary> roles)
        {
            roles.Sort((a, b) =>
            {
                int byLength = a.Name.Length.CompareTo(b.Name.Length);
                if (byLength != 0) return byLength;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
        }

        private static void AppendUpTo(List<DiscordRoleSummary> dest, List<DiscordRoleSummary> src, int totalLimit)
        {
            foreach (var role in src)
            {
                if (dest.Count >= totalLimit) return;
                dest.Add(role);
            }
        }
    }
}
