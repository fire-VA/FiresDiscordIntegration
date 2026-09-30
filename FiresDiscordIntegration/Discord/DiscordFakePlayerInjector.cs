using System;
using HarmonyLib;
using Splatform;
using UnityEngine;

namespace FiresDiscordIntegration.Discord
{
    /// <summary>
    /// Server-only patch that appends a synthetic "[Discord]" entry to
    /// <see cref="ZNet.m_players"/> every time the player list rebuilds.
    /// Propagated to every connected client (vanilla included) via the
    /// vanilla SendPlayerList → RPC_PlayerList round-trip, so the
    /// synthetic entry lives in each client's local <c>m_players</c>
    /// list with the same fixed <see cref="FakeUserId"/>.
    ///
    /// WHY: Vanilla <see cref="Terminal.AddString(PlatformUserID, string, Talker.Type, bool)"/>
    /// resolves the sender's display name via
    /// <see cref="ZNet.TryGetPlayerByPlatformUserID"/>. On a dedicated
    /// server the server itself is NEVER in <c>m_players</c> (see
    /// ZNet.UpdatePlayerList — the local-user entry is gated on
    /// <c>SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null</c>),
    /// so a chat message sent with the server's own PlatformUserID as
    /// sender silently fails the lookup and never makes it into the
    /// chat scrollback. This injector gives us a real, lookup-able
    /// sender identity dedicated to Discord-relay traffic — no client
    /// mod required.
    ///
    /// SIDE EFFECTS:
    ///   * The connected-player list UI shows a "[Discord]" entry as if
    ///     a player named "[Discord]" were logged in. Visual only.
    ///   * Anything iterating <c>ZNet.m_players</c> sees one extra
    ///     entry. Most consumers skip it harmlessly because its
    ///     <see cref="ZNet.PlayerInfo.m_characterID"/> is
    ///     <see cref="ZDOID.None"/> (no character → no position, no
    ///     ZDO). Mods that don't guard on <c>ZDOID.None</c> can match
    ///     against <see cref="FakeUserId"/> to filter.
    /// </summary>
    [HarmonyPatch]
    public static class DiscordFakePlayerInjector
    {
        /// <summary>Display name shown for the synthetic entry.</summary>
        public const string FakeDisplayName = "[Discord]";

        /// <summary>
        /// Platform string for the fake entry's <see cref="PlatformUserID"/>.
        /// Distinct from "Steam" / "Xbox" / "PlayStation" so the synthetic
        /// ID cannot collide with any real connected user.
        /// </summary>
        private const string FakePlatform = "Discord";

        /// <summary>
        /// Stable identifier portion of <see cref="FakeUserId"/>. Any unique
        /// string works; this label is human-readable in case the ID surfaces
        /// in a log somewhere.
        ///
        /// Kept SHORT deliberately. The connected-players panel renders the raw
        /// PlatformUserID ("<c>Platform_identifier</c>") in a fixed-width column, so
        /// "FiresDiscordRelay" made the entry wrap onto a second line and pushed the
        /// list out of shape. "Discord_Relay" fits on one line.
        /// </summary>
        private const string FakeIdentifier = "Relay";

        // Built once, reused everywhere. Equality on PlatformUserID is
        // (platform, id) string-compare, so two constructions with the same
        // inputs will match.
        private static PlatformUserID? _cachedFakeUserId;

        /// <summary>
        /// The synthetic PlatformUserID used as the sender for Discord-relayed
        /// chat. Stable across the session — clients receive this exact same
        /// ID via SendPlayerList, so their local
        /// <see cref="ZNet.TryGetPlayerByPlatformUserID"/> calls succeed
        /// against it.
        /// </summary>
        public static PlatformUserID FakeUserId
        {
            get
            {
                if (!_cachedFakeUserId.HasValue)
                {
                    _cachedFakeUserId = new PlatformUserID(new Platform(FakePlatform), FakeIdentifier);
                }
                return _cachedFakeUserId.Value;
            }
        }

        /// <summary>
        /// Postfix on <see cref="ZNet.UpdatePlayerList"/>. Runs after vanilla
        /// has cleared and refilled <c>m_players</c> from the local user (if
        /// graphical) and the connected peers. We append the synthetic entry
        /// last so the existing fill loop is untouched.
        ///
        /// Server-only by design — only the server's player list is the
        /// authoritative source that SendPlayerList serializes. Patching the
        /// same method on clients would inject a duplicate entry that the
        /// next RPC_PlayerList wipes out anyway.
        ///
        /// <para>
        /// <b>Priority.Last</b> is load-bearing. Server Devcommands also
        /// postfixes <c>UpdatePlayerList</c> (Server_UpdatePrivatePositions)
        /// and iterates <c>m_players</c> looking up each entry's
        /// <c>m_characterID</c> in a peer dictionary keyed by characterID —
        /// but the dictionary excludes <c>ZDOID.None</c>, so when SDC sees
        /// our synthetic entry (which is intentionally <c>ZDOID.None</c>) the
        /// lookup fails and SDC error-spams "Unable to find the peer to set
        /// private position." every tick (~2s cadence). Running our postfix
        /// last means SDC iterates the clean list first (no error), then we
        /// append the synthetic entry — which is still present when
        /// <c>SendPlayerList</c> serializes, so the Discord-relay chat
        /// lookup keeps working. No SDC-side changes needed.
        /// </para>
        /// </summary>
        [HarmonyPatch(typeof(ZNet), "UpdatePlayerList")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void ZNet_UpdatePlayerList_Postfix(ZNet __instance)
        {
            try
            {
                if (__instance == null || !__instance.IsServer()) return;
                if (__instance.m_players == null) return;

                // Idempotency guard: UpdatePlayerList runs every time
                // SendPlayerList is invoked (peer join/leave, position
                // refresh tick). Vanilla clears m_players at the start, so
                // we won't accumulate duplicates within a single call —
                // but be defensive in case future patches keep state.
                for (int i = 0; i < __instance.m_players.Count; i++)
                {
                    if (__instance.m_players[i].m_userInfo.m_id == FakeUserId)
                        return;
                }

                // ZNet.PlayerInfo and ZNet.CrossNetworkUserInfo are both
                // structs in vanilla; initializer syntax flows naturally.
                // Valheim 1.0 moved m_serverAssignedDisplayName off PlayerInfo and onto the
                // nested CrossNetworkUserInfo — vanilla's own PlayerInfo.ToString reads it
                // from there now.
                //
                // EVERY string here must be non-null. ZNet.WritePlayerInfo — the serializer
                // SendPlayerList actually calls — writes m_name and all four CrossNetworkUserInfo
                // strings straight into the ZPackage with NO null guard (unlike PlayerInfo.Write,
                // which does `?? string.Empty`). A struct field we leave unset is null, so a single
                // missed string throws ArgumentNullException inside ZNet.Update EVERY tick and
                // aborts the player-list send. 1.0's m_playfabId did exactly that. If a future
                // patch adds another string to either struct, set it here too.
                var entry = new ZNet.PlayerInfo
                {
                    m_name = FakeDisplayName,
                    m_characterID = ZDOID.None,
                    m_publicPosition = false,
                    m_position = Vector3.zero,
                    m_userInfo = new ZNet.CrossNetworkUserInfo
                    {
                        m_id = FakeUserId,
                        m_displayName = FakeDisplayName,
                        m_serverAssignedDisplayName = FakeDisplayName,
                        m_playfabId = string.Empty,
                    },
                };

                __instance.m_players.Add(entry);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresDiscordIntegration] DiscordFakePlayerInjector postfix failed: {ex.Message}");
            }
        }
    }
}
