using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresDiscordIntegration.Discord
{
    /// <summary>
    /// Pushes the server's known-Discord-role list (name + color) to every
    /// connected client so the in-game @-mention hint overlay has something to
    /// show.  The server is the authoritative source — only it has the bot
    /// token and can talk to Discord's REST API; clients receive the same
    /// snapshot the server-side mention resolver uses.
    ///
    /// One ZRoutedRpc, two trigger points:
    ///   • <see cref="BroadcastToAllPeers"/> — called once per server-side
    ///     fetch (typically a few seconds after the bot listener starts) so
    ///     already-connected clients get the refresh.
    ///   • <see cref="SendToPeer"/> — called from the existing PeerInfo
    ///     postfix in ClientLogRelay so a freshly-connected client gets the
    ///     current list as part of their handshake, without having to wait
    ///     for the next refresh.
    /// </summary>
    public static class DiscordRoleNameSync
    {
        // Two independent RPC streams so a role refresh doesn't have to wait
        // for a (potentially much bigger) user refresh and vice versa.  Each
        // payload is a flat name/color string array — see BuildPackage for
        // the wire format.
        internal const string RpcName      = "FiresDiscord_RoleNames";
        internal const string RpcNameUsers = "FiresDiscord_UserNames";

        // Tracks whether we've registered the RPC handler this server session.
        // ZRoutedRpc.Register survives until the ZNet teardown; double-registering
        // would no-op but is also pointless.
        private static bool _registered;

        public static void RegisterRpcHandlers()
        {
            if (_registered) return;
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.Register<ZPackage>(RpcName,      OnReceiveRoles);
            ZRoutedRpc.instance.Register<ZPackage>(RpcNameUsers, OnReceiveUsers);
            _registered = true;
        }

        public static void ClearRegistration()
        {
            _registered = false;
        }

        public static void BroadcastToAllPeers(IReadOnlyList<DiscordRoleSummary> roles)
        {
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcName, BuildPackage(roles));
        }

        public static void SendToPeer(long peerId, IReadOnlyList<DiscordRoleSummary> roles)
        {
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(peerId, RpcName, BuildPackage(roles));
        }

        public static void BroadcastUsersToAllPeers(IReadOnlyList<DiscordUserSummary> users)
        {
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcNameUsers, BuildUserPackage(users));
        }

        public static void SendUsersToPeer(long peerId, IReadOnlyList<DiscordUserSummary> users)
        {
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(peerId, RpcNameUsers, BuildUserPackage(users));
        }

        // Receiver side (clients).  The server runs the same handler harmlessly
        // when it broadcasts to ZRoutedRpc.Everybody — its DiscordRoleNameCache
        // ends up holding the same list the mention resolver already has, which
        // matters for listen-server hosts who run both server + client roles in
        // the same process and need the cache for their own chat overlay.
        private static void OnReceiveRoles(long _sender, ZPackage pkg)
        {
            if (pkg == null) return;
            int count = pkg.ReadInt();
            if (count < 0 || count > MaxAcceptedRoleCount)
            {
                Debug.LogWarning($"[Discord] RoleNameSync: received absurd role count {count}, ignoring packet.");
                return;
            }

            var roles = new List<DiscordRoleSummary>(count);
            for (int i = 0; i < count; i++)
            {
                string name = pkg.ReadString();
                string color = pkg.ReadString();
                if (string.IsNullOrEmpty(name)) continue;
                roles.Add(new DiscordRoleSummary(name, color));
            }
            DiscordRoleNameCache.Set(roles);
            Debug.Log($"[Discord] RoleNameSync: cached {roles.Count} role name(s) for the @-mention hint overlay.");
        }

        // Symmetric to OnReceiveRoles but writes to the user list.  Reaches
        // the same DiscordRoleNameCache so a single FilterForHint call on
        // the client side can see both buckets.
        private static void OnReceiveUsers(long _sender, ZPackage pkg)
        {
            if (pkg == null) return;
            int count = pkg.ReadInt();
            if (count < 0 || count > MaxAcceptedUserCount)
            {
                Debug.LogWarning($"[Discord] RoleNameSync: received absurd user count {count}, ignoring packet.");
                return;
            }

            var users = new List<DiscordUserSummary>(count);
            for (int i = 0; i < count; i++)
            {
                string name = pkg.ReadString();
                string color = pkg.ReadString();
                if (string.IsNullOrEmpty(name)) continue;
                users.Add(new DiscordUserSummary(name, color));
            }
            DiscordRoleNameCache.SetUsers(users);
            Debug.Log($"[Discord] RoleNameSync: cached {users.Count} user display name(s) for the @-mention hint overlay.");
        }

        // Sanity cap so a malformed packet can't allocate gigabytes.  A Discord
        // guild's hard role limit is 250 — picking 1024 as a buffer past that.
        private const int MaxAcceptedRoleCount = 1024;

        // Higher cap for the user list since guilds can run into the tens of
        // thousands.  Matches the resolver's MemberPageHardCap * MemberPageSize
        // ceiling so a packet within the server's own fetch budget is
        // accepted, while an obviously-bogus payload is rejected.
        private const int MaxAcceptedUserCount = 50_000;

        private static ZPackage BuildPackage(IReadOnlyList<DiscordRoleSummary> roles)
        {
            var pkg = new ZPackage();
            pkg.Write(roles?.Count ?? 0);
            if (roles == null) return pkg;
            foreach (var role in roles)
            {
                pkg.Write(role.Name ?? string.Empty);
                pkg.Write(role.ColorHex ?? string.Empty);
            }
            return pkg;
        }

        // Same wire shape as BuildPackage — name/color string pairs.  Two
        // builders instead of a templated one because DiscordRoleSummary and
        // DiscordUserSummary are distinct types and we'd rather pay one line
        // of duplication than introduce a shared interface that complicates
        // the readonly-struct story.
        private static ZPackage BuildUserPackage(IReadOnlyList<DiscordUserSummary> users)
        {
            var pkg = new ZPackage();
            pkg.Write(users?.Count ?? 0);
            if (users == null) return pkg;
            foreach (var user in users)
            {
                pkg.Write(user.Name ?? string.Empty);
                pkg.Write(user.ColorHex ?? string.Empty);
            }
            return pkg;
        }
    }

    /// <summary>
    /// Hooks ZNet.Awake on every side (server, dedicated, listen, client) and
    /// registers the role-name RPC handler.  Separate Harmony patch from the
    /// existing ClientLogRelay one so the role-sync feature can be reasoned
    /// about in isolation — pulling the hook into a single file would make
    /// either feature harder to disable cleanly.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), "Awake")]
    internal static class DiscordRoleNameSync_RegisterOnZNetAwake
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            DiscordRoleNameSync.RegisterRpcHandlers();
        }
    }

    /// <summary>
    /// Server-side: when a client finishes the PeerInfo handshake, push the
    /// currently-cached Discord role list to *that specific* peer.  The
    /// broadcast in the mention-resolver fetch already covers peers who were
    /// already connected at refresh time, but a player who joins mid-session
    /// after the bot fetched roles wouldn't get the list otherwise.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
    internal static class DiscordRoleNameSync_PushOnPeerInfo
    {
        [HarmonyPostfix]
        public static void Postfix(ZNet __instance, ZRpc rpc)
        {
            try
            {
                if (__instance == null || rpc == null) return;
                if (!__instance.IsServer()) return;

                ZNetPeer peer = __instance.GetPeer(rpc);
                if (peer == null || peer.m_uid == 0) return;

                var roles = DiscordRoleNameCache.Roles;
                if (roles.Count > 0)
                    DiscordRoleNameSync.SendToPeer(peer.m_uid, roles);

                // Users are pushed separately because the member fetch is
                // strictly slower (paginated) than the role fetch — by the
                // time a player finishes their handshake the role list might
                // be live while the user list is still mid-fetch.  Sending
                // the role list independently lets the @role hint work
                // immediately for late joiners.
                var users = DiscordRoleNameCache.Users;
                if (users.Count > 0)
                    DiscordRoleNameSync.SendUsersToPeer(peer.m_uid, users);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[Discord] RoleNameSync: PeerInfo postfix threw: {ex.Message}");
            }
        }
    }
}
