using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace FiresDiscordIntegration.ChatRelay
{
    /// <summary>
    /// Security helpers for the chat -> Discord relay: the sender's name comes from its connection (never the packet),
    /// each sender is rate-limited, and the text is capped and stripped of mass / role pings before it reaches a webhook.
    /// </summary>
    internal static class ChatSecurity
    {
        internal const int MaxTextLength = 500;
        internal const int MaxNameLength = 40;

        // Token bucket per sender: BurstLines lines, refilled at one line per RefillSeconds.
        private const float BurstLines = 5f;
        private const float RefillSeconds = 2f;
        private const int MaxTrackedSenders = 512;
        private static readonly Dictionary<long, float[]> _buckets = new Dictionary<long, float[]>();

        private static readonly Regex MassMention = new Regex(@"@(everyone|here)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RoleTag = new Regex(@"<@&\d+>", RegexOptions.Compiled);

        /// <summary>
        /// The name to post for a chat line from <paramref name="senderPeer"/>: the connected peer's own player name.
        /// The server's own lines (a listen host) keep the local name. Null when the sender is not a known peer.
        /// </summary>
        internal static string SenderName(long senderPeer, string claimed)
        {
            if (ZNet.instance == null) return null;
            var peer = ZNet.instance.GetPeer(senderPeer);
            if (peer != null) return string.IsNullOrEmpty(peer.m_playerName) ? null : peer.m_playerName;
            if (senderPeer != 0L && senderPeer == ZDOMan.GetSessionID())
            {
                var profile = Game.instance != null ? Game.instance.GetPlayerProfile() : null;
                string local = profile?.GetName();
                return string.IsNullOrEmpty(local) ? claimed : local;
            }
            return null;
        }

        /// <summary>True when the sender still has rate-limit budget; consumes one line.</summary>
        internal static bool Allow(long senderPeer)
        {
            float now = Time.realtimeSinceStartup;
            if (!_buckets.TryGetValue(senderPeer, out var b))
            {
                if (_buckets.Count > MaxTrackedSenders) _buckets.Clear();
                b = new[] { BurstLines, now };
                _buckets[senderPeer] = b;
            }
            b[0] = Mathf.Min(BurstLines, b[0] + (now - b[1]) / RefillSeconds);
            b[1] = now;
            if (b[0] < 1f) return false;
            b[0] -= 1f;
            return true;
        }

        /// <summary>Caps the length and strips @everyone / @here and raw role tags.</summary>
        internal static string SanitizeText(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.Length > max) text = text.Substring(0, max);
            text = MassMention.Replace(text, m => m.Groups[1].Value);
            text = RoleTag.Replace(text, "");
            return text;
        }
    }
}
