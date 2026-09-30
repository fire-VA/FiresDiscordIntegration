using HarmonyLib;
using System;
using System.Collections.Generic;
using FiresDiscordIntegration.Discord;
using UnityEngine;

namespace FiresDiscordIntegration.ChatRelay
{
    /// <summary>
    /// Client→server relay for Valheim's "local" chat (Talker.Type.Normal aka /say)
    /// and Whisper.  Vanilla routes those via Talker.RPC_Say, a ZNetView RPC on
    /// the Player ZDO.  ZNetView RPCs only fire on machines that have instantiated
    /// the target ZDO, and a dedicated server never spawns the Player object — so
    /// the server's Chat.OnNewChatMessage hook (which feeds the Discord webhook)
    /// never sees Say/Whisper on a dedi.  This RPC closes that gap: the sender's
    /// machine pushes a (name, text, type) packet to the server every time
    /// Chat.SendText fires for a Say/Whisper, and the server replays it through
    /// the regular OnPlayerChat pipeline.  Shouts still go through the existing
    /// ZRoutedRpc "ChatMessage" path that the server already processes; relaying
    /// them through this RPC too would just double-post.
    /// </summary>
    internal static class ChatRelayRpc
    {
        internal const string RpcName = "FiresDiscord_ChatRelay";

        private static bool _registered;

        public static void RegisterRpcHandlers()
        {
            if (_registered) return;
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.Register<ZPackage>(RpcName, OnReceive);
            _registered = true;
        }

        public static void ClearRegistration() { _registered = false; }

        // Server-side handler.  We do all of the filtering here (rather than
        // refusing to send on the client) so a stale client running against a
        // newer server still has its messages relayed; the server is the
        // authoritative point where "should this chat type go to Discord?" is
        // decided anyway because the [Discord.Events] toggles are server-side.
        private static void OnReceive(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            string playerName;
            string text;
            int    typeRaw;
            try
            {
                playerName = pkg.ReadString();
                text       = pkg.ReadString();
                typeRaw    = pkg.ReadInt();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] ChatRelayRpc: malformed packet from peer {sender}: {ex.Message}");
                return;
            }

            var senderType = (Talker.Type)typeRaw;
            DiscordIntegrationCore.ChatLineType lineType;
            if      (senderType == Talker.Type.Normal)  lineType = DiscordIntegrationCore.ChatLineType.Say;
            else if (senderType == Talker.Type.Whisper) lineType = DiscordIntegrationCore.ChatLineType.Whisper;
            else
            {
                // Shouts already arrive via the regular OnNewChatMessage path on
                // the server; ignore so we don't duplicate the post.  Ping
                // (type 3) is map-marker, never relayed.
                return;
            }

            if (string.IsNullOrEmpty(playerName) || string.IsNullOrEmpty(text)) return;

            Debug.Log($"[Discord] ChatRelayRpc: received {lineType} from '{playerName}' (peer {sender}) — relaying.");
            DiscordIntegrationCore.OnPlayerChat(playerName, text, lineType);
        }
    }

    /// <summary>
    /// Hooks ZNet.Awake on every side and registers the chat-relay RPC handler.
    /// Symmetric to the Discord role-name sync registration pattern so both
    /// chat-side RPCs come up the same way regardless of server/client role.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), "Awake")]
    internal static class ChatRelayRpc_RegisterOnZNetAwake
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            try { ChatRelayRpc.RegisterRpcHandlers(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] ChatRelayRpc.RegisterRpcHandlers threw: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Harmony patches that decorate vanilla chat with Discord coloring + media embeds.
    /// Ported from the FiresNPCs ChatSystem module, minus the channel-switcher UI
    /// (Local/Global/Group/Clan) which depended on the guild API that doesn't exist
    /// here.  This mod only ships the Discord-relay-relevant hooks:
    ///
    ///   - <see cref="Chat_OnNewChatMessage_Postfix"/> detects image / GIF URLs in
    ///     incoming chat, strips them from the visible buffer, wraps Discord-relayed
    ///     messages in Discord blue, and queues the media for the embed overlay.
    ///   - <see cref="Chat_Update_Postfix"/> prunes destroyed embeds each frame so
    ///     the manager doesn't leak references to dead GameObjects.
    ///   - <see cref="Chat_OnDestroy_Postfix"/> tears down the overlay on scene
    ///     change / logout so it doesn't carry across into the next session.
    /// </summary>
    [HarmonyPatch]
    public static class ChatPatches
    {
        // Discord's official "blurple" brand color, used here to recolor any chat
        // line containing "[Discord]" so relayed messages stand out from in-game
        // chatter at a glance.  TMP rich-text accepts a #RRGGBB hex.
        private const string DiscordBrandColorHex   = "#7289DA";
        // Subdued gray for the transient "Loading..." placeholder shown while
        // a chat-media embed is being fetched; replaces itself when the image
        // or GIF actually appears, so it doesn't need to look prominent.
        private const string MediaLoadingColorHex   = "#888888";
        private const string DiscordPrefixToken     = "[Discord]"; // matched case-insensitively
        // Vanilla shouts uppercase the chat text before it lands in the buffer,
        // so the relayed "[Discord]" prefix shows up as "[DISCORD]".  Both spellings
        // are matched in the buffer scan below.
        private const string DiscordPrefixTokenUpper = "[DISCORD]";

        // Mirror of the encoding DiscordBotListener.RelayToGameChat uses to smuggle
        // each Discord author's role color through UserInfo.Name (it can't put TMP
        // <color=#HEX> tags in the chat body — Valheim sanitises '<' / '>' away).
        // The sender's UserInfo.Name reads as "[Discord]" or "[Discord]|#RRGGBB"
        // depending on whether the user has a colored Discord role.
        private const string DiscordSenderNamePrefix    = "[Discord]";
        private const char   DiscordSenderNameDelimiter = '|';


        // Splits a relayed-from-Discord chat buffer entry into three TMP-coloured
        // spans so the line reads correctly in-game:
        //
        //   <color=blue>[Discord] <color=role>username</color>: message</color>
        //
        // The username sits inside the outer Discord-blue wrap, so TMP renders the
        // role color over just the name, then reverts to blue for the body.  When
        // the author has no role color (the resolver returned the brand blue), the
        // nested wrap is skipped — wrapping the same color twice adds nothing and
        // bloats the buffer.  The original color tags on the entry are stripped
        // first so a stale wrap from a chat-history reload doesn't double up.
        //
        // The "[DISCORD] {USERNAME}: {MESSAGE}" shape is the one DiscordBotListener
        // produces; Talker.Shout uppercases the text on the wire, hence the case-
        // insensitive prefix match.  We find the FIRST ": " after the prefix and
        // treat everything between as the username — a Discord display name that
        // literally contains ": " would mis-split, but that's vanishingly rare and
        // the fallback still wraps the whole line in blue.
        private static string RecolorDiscordChatLine(string entry, string authorColorHex)
        {
            entry = System.Text.RegularExpressions.Regex.Replace(entry, @"<color=[^>]*>", "");
            entry = entry.Replace("</color>", "");

            bool authorHasOwnColor = !string.Equals(authorColorHex, DiscordBrandColorHex,
                                                    System.StringComparison.OrdinalIgnoreCase);
            if (!authorHasOwnColor)
                return $"<color={DiscordBrandColorHex}>{entry}</color>";

            int prefixIdx = entry.IndexOf(DiscordPrefixTokenUpper, System.StringComparison.OrdinalIgnoreCase);
            if (prefixIdx < 0)
                return $"<color={DiscordBrandColorHex}>{entry}</color>";

            int afterPrefix = prefixIdx + DiscordPrefixTokenUpper.Length;
            // Skip whitespace AND a colon — the chat entry can show up as
            // either "[Discord] username: msg" (old text-prefix layout) or
            // "[Discord]: username: msg" (new fake-player layout where the
            // sender name supplies the brand and the text body starts with
            // the Discord username). Both share the same intent: advance
            // past the brand to the username.
            while (afterPrefix < entry.Length &&
                   (entry[afterPrefix] == ' ' || entry[afterPrefix] == ':'))
                afterPrefix++;

            int separatorIdx = entry.IndexOf(": ", afterPrefix, System.StringComparison.Ordinal);
            if (separatorIdx <= afterPrefix)
                return $"<color={DiscordBrandColorHex}>{entry}</color>";

            string beforeUsername = entry.Substring(0, afterPrefix);
            string usernamePart   = entry.Substring(afterPrefix, separatorIdx - afterPrefix);
            string fromSeparator  = entry.Substring(separatorIdx);

            return $"<color={DiscordBrandColorHex}>{beforeUsername}"
                 + $"<color={authorColorHex}>{usernamePart}</color>"
                 + $"{fromSeparator}</color>";
        }

        // Decodes the "[Discord]" / "[Discord]|#RRGGBB" encoding that
        // DiscordBotListener.RelayToGameChat writes into UserInfo.Name.  Returns
        // the per-user role color (with leading '#') when present, otherwise the
        // Discord brand blue so unranked / role-less authors still get a clearly-
        // Discord-coloured line instead of inheriting the local chat color.
        private static string ReadDiscordAuthorColorOrDefault(UserInfo sender)
        {
            string name = sender?.Name;
            if (string.IsNullOrEmpty(name)) return DiscordBrandColorHex;
            if (!name.StartsWith(DiscordSenderNamePrefix, System.StringComparison.Ordinal))
                return DiscordBrandColorHex;

            int delimIdx = name.IndexOf(DiscordSenderNameDelimiter);
            if (delimIdx < 0 || delimIdx >= name.Length - 1) return DiscordBrandColorHex;

            string encoded = name.Substring(delimIdx + 1).Trim();
            if (encoded.Length == 0) return DiscordBrandColorHex;
            return encoded.StartsWith("#") ? encoded : "#" + encoded;
        }

        [HarmonyPatch(typeof(Chat), "OnNewChatMessage")]
        [HarmonyPostfix]
        private static void Chat_OnNewChatMessage_Postfix(
            GameObject go, long senderID, Vector3 pos,
            Talker.Type type, UserInfo sender, string text)
        {
            List<ChatMediaDetector.DetectedMedia> mediaUrls = null;
            try
            {
                mediaUrls = ChatMediaDetector.ExtractImageUrls(text);
            }
            catch { }

            bool hasMedia = mediaUrls != null && mediaUrls.Count > 0;
            // Detect Discord relay messages either by the legacy in-body
            // "[Discord]" token OR by the encoded sender Name set by the
            // fake-player relay path. The text-body check is kept for
            // backward compat with old-format messages that still float
            // around in chat history.
            bool isDiscord =
                (text != null
                    && text.IndexOf(DiscordPrefixToken, System.StringComparison.OrdinalIgnoreCase) >= 0)
                || (sender != null
                    && !string.IsNullOrEmpty(sender.Name)
                    && sender.Name.StartsWith(DiscordSenderNamePrefix, System.StringComparison.OrdinalIgnoreCase));

            // Pulled from UserInfo.Name when the line came from a Discord relay.
            // Falls back to the Discord brand color when the Discord user has no
            // colored role.
            string discordAuthorColorHex = isDiscord
                ? ReadDiscordAuthorColorOrDefault(sender)
                : null;

            if (isDiscord || hasMedia)
            {
                try
                {
                    if (Chat.instance != null)
                    {
                        var chatBuffer = Chat.instance.m_chatBuffer;
                        if (chatBuffer != null && chatBuffer.Count > 0)
                        {
                            int lastIdx = chatBuffer.Count - 1;
                            string lastEntry = chatBuffer[lastIdx];

                            if (hasMedia)
                            {
                                lastEntry = ChatMediaDetector.StripMediaUrls(lastEntry);
                            }

                            if (isDiscord && lastEntry.IndexOf(DiscordPrefixTokenUpper, System.StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                lastEntry = RecolorDiscordChatLine(lastEntry, discordAuthorColorHex);
                            }

                            chatBuffer[lastIdx] = lastEntry;
                            Chat.instance.UpdateChat();
                        }
                    }
                }
                catch { }
            }

            if (hasMedia)
            {
                try
                {
                    if (Chat.instance != null)
                    {
                        bool hasGif = false;
                        foreach (var m in mediaUrls)
                        {
                            if (m.Type == ChatMediaDetector.MediaType.AnimatedGif)
                            { hasGif = true; break; }
                        }
                        string note = hasGif
                            ? $"<color={MediaLoadingColorHex}><i>Loading GIF…</i></color>"
                            : $"<color={MediaLoadingColorHex}><i>Loading image…</i></color>";
                        Chat.instance.m_chatBuffer?.Add(note);
                        Chat.instance.UpdateChat();
                    }
                }
                catch { }

                foreach (var media in mediaUrls)
                {
                    ChatMediaEmbedManager.QueueEmbed(media.Url, media.Type);
                }
            }
        }

        [HarmonyPatch(typeof(Chat), "Update")]
        [HarmonyPostfix]
        private static void Chat_Update_Postfix()
        {
            ChatMediaEmbedManager.PruneDestroyed();
        }

        [HarmonyPatch(typeof(Chat), "OnDestroy")]
        [HarmonyPostfix]
        private static void Chat_OnDestroy_Postfix()
        {
            try
            {
                ChatMediaEmbedManager.ClearAll();
                ChatMediaEmbedManager.ResetOverlay();
            }
            catch { }
        }

        // Spin up the @mention hint overlay once Chat is alive.  The overlay's
        // own Update is gated on Chat.instance.m_input being focused, so a chat
        // window that's never opened costs nothing besides a couple of empty
        // Update calls.
        [HarmonyPatch(typeof(Chat), "Awake")]
        [HarmonyPostfix]
        private static void Chat_Awake_Postfix()
        {
            try { ChatMentionHint.EnsureCreated(); }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[FiresDiscordIntegration] ChatMentionHint create failed: {ex.Message}");
            }
        }

        // Client-side bridge for Say + Whisper.  Vanilla's Talker.RPC_Say has a
        // hard `if (Player.m_localPlayer == null) return;` guard, so on a
        // dedicated server (no local player) Say and Whisper never reach
        // Chat.OnNewChatMessage on the server — which is where our existing
        // OnNewChatMessage prefix decides what to push to Discord.  Patching
        // Chat.SendText on the sender's machine lets us forward those two
        // chat types over a tiny custom routed RPC so the server-side relay
        // can still post them.
        //
        // Shouts are intentionally skipped: vanilla already routes them via
        // ZRoutedRpc.InvokeRoutedRPC("ChatMessage", ...) which the dedicated
        // server processes locally, so the existing OnNewChatMessage prefix
        // already covers them.  Relaying shouts through this RPC too would
        // double-post.
        //
        // Listen-server hosts (host = player = server) DO see Say/Whisper via
        // Talker.RPC_Say firing locally on the host's player ZDO, so the
        // existing prefix catches them.  Skipping the relay when the sender
        // is also the server avoids a second post in that case.
        [HarmonyPatch(typeof(Chat), nameof(Chat.SendText))]
        [HarmonyPostfix]
        private static void Chat_SendText_Postfix(Talker.Type type, string text)
        {
            try
            {
                if (type != Talker.Type.Normal && type != Talker.Type.Whisper) return;
                if (string.IsNullOrEmpty(text)) return;

                var localPlayer = Player.m_localPlayer;
                if (localPlayer == null) return;

                if (ZNet.instance == null) return;

                // Listen-server / host scenario: the existing OnNewChatMessage
                // path already feeds Say/Whisper into Discord because the
                // host's local Talker.RPC_Say fires successfully.  Pushing
                // another packet from here would duplicate the post.
                if (ZNet.instance.IsServer()) return;

                if (ZRoutedRpc.instance == null) return;
                long serverId = ZRoutedRpc.instance.GetServerPeerID();
                if (serverId == 0L) return;

                string playerName = localPlayer.GetPlayerName();
                if (string.IsNullOrEmpty(playerName)) playerName = "Unknown";

                var pkg = new ZPackage();
                pkg.Write(playerName);
                pkg.Write(text);
                pkg.Write((int)type);
                ZRoutedRpc.instance.InvokeRoutedRPC(serverId, ChatRelayRpc.RpcName, pkg);
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[FiresDiscordIntegration] Chat.SendText postfix threw: {ex.Message}");
            }
        }
    }
}
