using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using FiresCore.Bridge;

namespace FiresDiscordIntegration.Discord
{
    /// <summary>
    /// Central hub for Discord webhook integration.
    ///
    /// All game events route through here. Each method:
    ///  1. Checks if the module is enabled
    ///  2. Checks if the specific event type is enabled
    ///  3. Resolves the correct webhook URL (general / anti-cheat / chat)
    ///  4. Builds the embed or text payload
    ///  5. Fires the webhook via DiscordWebhookService
    ///
    /// This class is server-only. All public methods silently no-op on clients.
    /// </summary>
    public static class DiscordIntegrationCore
    {
        // ============================================================
        // Guards
        // ============================================================

        /// <summary>
        /// Returns true if Discord integration should be active.
        /// Only runs on the server and only when enabled in config.
        /// </summary>
        public static bool IsActive()
        {
            if (DiscordIntegrationConfig.Enabled == null || !DiscordIntegrationConfig.Enabled.Value)
                return false;
            if (ZNet.instance == null || !ZNet.instance.IsServer())
                return false;
            return true;
        }

        /// <summary>
        /// Resolved display name for webhook posts.
        /// Priority order:
        ///   1. <c>WebhookDisplayName</c> config (admin-set explicit override)
        ///   2. The configured bot's actual username from <c>/users/@me</c>
        ///   3. Hard-coded "Valheim Server" fallback
        /// This means every webhook post in the mod inherits the bot's identity
        /// (e.g. "Verdant's Ascent ChatBot") without the admin having to fill in
        /// WebhookDisplayName + WebhookAvatarUrl manually.
        /// </summary>
        private static string GetUsername()
        {
            string explicitName = DiscordIntegrationConfig.WebhookDisplayName?.Value;
            if (!string.IsNullOrEmpty(explicitName) && explicitName != "Valheim Server")
                return explicitName;
            return DiscordBotIdentity.ResolveUsername(fallback: explicitName ?? "Valheim Server");
        }

        /// <summary>
        /// Resolved avatar URL for webhook posts.  Same priority as
        /// <see cref="GetUsername"/>: admin-set <c>WebhookAvatarUrl</c> config wins
        /// over the bot's actual avatar URL fetched from <c>/users/@me</c>.
        /// </summary>
        private static string GetAvatarUrl()
        {
            var url = DiscordIntegrationConfig.WebhookAvatarUrl?.Value;
            if (!string.IsNullOrEmpty(url)) return url;
            return DiscordBotIdentity.ResolveAvatarUrl(fallback: null);
        }

        private static string GetServerName() => Utilities.ServerNameResolver.GetOperatorServerName();

        // Identifies which BepInEx log got attached to a kick embed.  Three
        // sources are tried in order (live client log -> cached client log ->
        // server's own log), and the source determines both the filename prefix
        // (so an admin sorting the channel by attachment name can tell them
        // apart) and the embed field label (so the same admin reading the
        // message can see whether the data is fresh, stale, or fallback).
        private enum AttachedLogSource
        {
            ClientFresh,
            ClientCached,
            ServerFallback,
        }

        private static string LogSourceFilenamePrefix(AttachedLogSource source) => source switch
        {
            AttachedLogSource.ClientFresh    => "client",
            AttachedLogSource.ClientCached   => "client_cached",
            AttachedLogSource.ServerFallback => "server",
            _ => "log",
        };

        private static string LogSourceFieldLabel(AttachedLogSource source) => source switch
        {
            AttachedLogSource.ClientFresh    => "Client Log",
            AttachedLogSource.ClientCached   => "Client Log (cached)",
            AttachedLogSource.ServerFallback => "Server Log (fallback)",
            _ => "Log",
        };

        // ============================================================
        // Server Events
        // ============================================================

        public static void OnServerStart()
        {
            if (!IsActive()) return;

            // Pre-warm Discord role cache for @mention resolution in chat webhooks.
            // This runs on server start regardless of bot listener being enabled,
            // so that @admin etc. in player shouts resolve to real Discord pings.
            DiscordMentionResolver.FetchRoles();

            if (DiscordIntegrationConfig.NotifyServerStart?.Value != true) return;

            var url = DiscordIntegrationConfig.ResolveWebhookUrl(WebhookCategory.General);
            var embed = new DiscordEmbed()
                .SetTitle("\U0001F7E2 Server Started")
                .SetDescription($"**{GetServerName()}** is now online.")
                .SetColor(DiscordColors.Green)
                .SetFooter(GetServerName());

            DiscordWebhookService.SendEmbed(url, embed, GetUsername(), GetAvatarUrl());
            LogVerbose("Sent ServerStart webhook");
        }

        public static void OnServerStop()
        {
            if (!IsActive()) return;
            if (DiscordIntegrationConfig.NotifyServerStop?.Value != true) return;

            var url = DiscordIntegrationConfig.ResolveWebhookUrl(WebhookCategory.General);
            var embed = new DiscordEmbed()
                .SetTitle("\U0001F534 Server Stopped")
                .SetDescription($"**{GetServerName()}** has gone offline.")
                .SetColor(DiscordColors.Red)
                .SetFooter(GetServerName());

            DiscordWebhookService.SendEmbed(url, embed, GetUsername(), GetAvatarUrl());
            LogVerbose("Sent ServerStop webhook");
        }

        public static void OnServerSave()
        {
            if (!IsActive()) return;
            if (DiscordIntegrationConfig.NotifyServerSave?.Value != true) return;

            var url = DiscordIntegrationConfig.ResolveWebhookUrl(WebhookCategory.General);
            DiscordWebhookService.SendMessage(url,
                $"\U0001F4BE **World saved** - {GetServerName()}",
                GetUsername(), GetAvatarUrl());
            LogVerbose("Sent ServerSave webhook");
        }

        // ============================================================
        // Player Events
        // ============================================================

        public static void OnPlayerJoin(string playerName, string steamId)
        {
            if (!IsActive()) return;
            if (DiscordIntegrationConfig.NotifyPlayerJoin?.Value != true) return;

            var url = DiscordIntegrationConfig.ResolveWebhookUrl(WebhookCategory.General);
            var embed = new DiscordEmbed()
                .SetTitle("\U0001F44B Player Joined")
                .SetColor(DiscordColors.Green)
                .AddField("Player", playerName, true)
                .AddField("Steam ID", steamId ?? "Unknown", true)
                .SetFooter(GetServerName());

            DiscordWebhookService.SendEmbed(url, embed, GetUsername(), GetAvatarUrl());
            LogVerbose($"Sent PlayerJoin webhook: {playerName}");
        }

        public static void OnPlayerLeave(string playerName, string steamId)
        {
            if (!IsActive()) return;
            if (DiscordIntegrationConfig.NotifyPlayerLeave?.Value != true) return;

            var url = DiscordIntegrationConfig.ResolveWebhookUrl(WebhookCategory.General);
            var embed = new DiscordEmbed()
                .SetTitle("\U0001F44B Player Left")
                .SetColor(DiscordColors.Red)
                .AddField("Player", playerName, true)
                .AddField("Steam ID", steamId ?? "Unknown", true)
                .SetFooter(GetServerName());

            DiscordWebhookService.SendEmbed(url, embed, GetUsername(), GetAvatarUrl());
            LogVerbose($"Sent PlayerLeave webhook: {playerName}");
        }

        public static void OnPlayerDeath(string playerName, string platformId = null)
        {
            if (!IsActive()) return;
            if (DiscordIntegrationConfig.NotifyPlayerDeath?.Value != true) return;

            // The client-shipped death screenshot already posted a richer embed for this death.
            if (DeathScreenshots.TryConsumeRecentPost(platformId)) return;

            var info = DeathScreenshots.TryGetDeathInfo(platformId);
            var url = DiscordIntegrationConfig.ResolveWebhookUrl(DiscordIntegrationConfig.ShotCategoryFor(playerName));
            var embed = new DiscordEmbed()
                .SetTitle("\U0001F480 Player Died")
                .SetDescription(DescribeDeath(playerName, info))
                .SetColor(DiscordColors.Red)
                .SetFooter(GetServerName());
            if (info != null && info.Damage > 0f)
                embed.AddField("\U0001F4A5 Last Hit", $"{info.Damage:0.#} damage", true);

            DiscordWebhookService.SendEmbed(url, embed, GetUsername(), GetAvatarUrl());
            LogVerbose($"Sent PlayerDeath webhook: {playerName}");
        }

        /// <summary>
        /// Death post with the dying player's client-captured screenshot attached. Posted the moment the
        /// image finishes arriving (at death), rather than at respawn like the fallback path.
        /// </summary>
        public static void OnPlayerDeathScreenshot(string playerName, string platformId, byte[] jpgBytes)
        {
            if (!IsActive()) return;
            if (DiscordIntegrationConfig.NotifyPlayerDeath?.Value != true) return;
            if (jpgBytes == null || jpgBytes.Length == 0) return;

            var info = DeathScreenshots.TryGetDeathInfo(platformId);
            var url = DiscordIntegrationConfig.ResolveWebhookUrl(DiscordIntegrationConfig.ShotCategoryFor(playerName));
            var embed = new ClientLogRelay.Webhook.MinimalWebhookPoster.Embed()
                .SetTitle("\U0001F480 Player Died")
                .SetDescription(DescribeDeath(playerName, info))
                .SetColor(DiscordColors.Red)
                .SetImage("attachment://death.jpg")
                .SetFooter(GetServerName());
            if (info != null && info.Damage > 0f)
                embed.AddField("\U0001F4A5 Last Hit", $"{info.Damage:0.#} damage", true);

            var files = new List<ClientLogRelay.Webhook.MinimalWebhookPoster.Attachment>
            {
                new ClientLogRelay.Webhook.MinimalWebhookPoster.Attachment("death.jpg", jpgBytes, "image/jpeg")
            };
            ClientLogRelay.Webhook.MinimalWebhookPoster.Post(url, embed, files, GetUsername(), GetAvatarUrl());
            LogVerbose($"Sent PlayerDeath webhook with screenshot ({jpgBytes.Length / 1024}KB): {playerName}");
        }

        /// <summary>Achievement completed (client-reported via the FUC bridge + ShotPipeline; RPGMaker-driven).</summary>
        public static void OnPlayerAchievement(string playerName, string platformId, string achievementName,
            string achievementDescription, byte[] jpgBytes)
        {
            if (!IsActive()) return;
            if (DiscordIntegrationConfig.AchievementScreenshots?.Value != true) return;
            if (string.IsNullOrEmpty(achievementName)) return;

            var url = DiscordIntegrationConfig.ResolveWebhookUrl(DiscordIntegrationConfig.ShotCategoryFor(playerName));
            string desc = $"**{playerName}** unlocked **{achievementName}**!";
            if (!string.IsNullOrEmpty(achievementDescription)) desc += $"\n*{achievementDescription}*";

            var embed = new ClientLogRelay.Webhook.MinimalWebhookPoster.Embed()
                .SetTitle("\U0001F3C6 Achievement Unlocked")
                .SetDescription(desc)
                .SetColor(DiscordColors.Yellow)
                .SetFooter(GetServerName());

            List<ClientLogRelay.Webhook.MinimalWebhookPoster.Attachment> files = null;
            if (jpgBytes != null && jpgBytes.Length > 0)
            {
                embed.SetImage("attachment://achievement.jpg");
                files = new List<ClientLogRelay.Webhook.MinimalWebhookPoster.Attachment>
                {
                    new ClientLogRelay.Webhook.MinimalWebhookPoster.Attachment("achievement.jpg", jpgBytes, "image/jpeg")
                };
            }
            ClientLogRelay.Webhook.MinimalWebhookPoster.Post(url, embed, files, GetUsername(), GetAvatarUrl());
            LogVerbose($"Sent Achievement webhook: {playerName} - {achievementName}" +
                       (files != null ? $" (+shot {jpgBytes.Length / 1024}KB)" : " (no shot)"));
        }

        /// <summary>Skill level-up (client-reported via ShotPipeline; threshold/step filtered client-side).</summary>
        public static void OnPlayerSkillLevelUp(string playerName, string platformId, string skillName,
            int level, byte[] jpgBytes)
        {
            if (!IsActive()) return;
            if (DiscordIntegrationConfig.SkillLevelUpScreenshots?.Value != true) return;
            if (string.IsNullOrEmpty(skillName) || level <= 0) return;

            var url = DiscordIntegrationConfig.ResolveWebhookUrl(DiscordIntegrationConfig.ShotCategoryFor(playerName));
            var embed = new ClientLogRelay.Webhook.MinimalWebhookPoster.Embed()
                .SetTitle("\U0001F4C8 Skill Level Up")
                .SetDescription($"**{playerName}** reached **{skillName}** level **{level}**!")
                .SetColor(DiscordColors.Green)
                .SetFooter(GetServerName());

            List<ClientLogRelay.Webhook.MinimalWebhookPoster.Attachment> files = null;
            if (jpgBytes != null && jpgBytes.Length > 0)
            {
                embed.SetImage("attachment://levelup.jpg");
                files = new List<ClientLogRelay.Webhook.MinimalWebhookPoster.Attachment>
                {
                    new ClientLogRelay.Webhook.MinimalWebhookPoster.Attachment("levelup.jpg", jpgBytes, "image/jpeg")
                };
            }
            ClientLogRelay.Webhook.MinimalWebhookPoster.Post(url, embed, files, GetUsername(), GetAvatarUrl());
            LogVerbose($"Sent SkillLevelUp webhook: {playerName} - {skillName} {level}" +
                       (files != null ? $" (+shot {jpgBytes.Length / 1024}KB)" : " (no shot)"));
        }

        /// <summary>
        /// Operator-triggered screenshot. Image-only by nature - skipped when capture failed.
        ///
        /// Deliberately gates on NOTHING but IsActive(). Two features arrive here, and each already decided
        /// at the point it initiated: the EWP poke returns on EwpPokeScreenshots before the server ever sends
        /// the poke RPC, and a ProgressEvents bridge request returns on BridgeScreenshots before the client
        /// captures. A shot reaching this method is one the initiating side already asked for.
        ///
        /// It used to re-check EwpPokeScreenshots here, which read as a safety net and was actually a bug:
        /// dead for the poke path that had already returned, and simply the WRONG switch for a bridge
        /// request, so turning the EWP feature off silently killed an unrelated one.
        /// </summary>
        public static void OnPlayerTriggeredScreenshot(string playerName, string platformId, string reason, byte[] jpgBytes,
            WebhookCategory category = WebhookCategory.Screenshots)
        {
            if (!IsActive()) return;
            if (jpgBytes == null || jpgBytes.Length == 0)
            {
                LogVerbose($"Triggered screenshot from {playerName} had no image (capture failed) - skipped.");
                return;
            }

            var url = DiscordIntegrationConfig.ResolveWebhookUrl(
                category == WebhookCategory.Screenshots ? DiscordIntegrationConfig.ShotCategoryFor(playerName) : category);
            string desc = $"**{playerName}**";
            // Captions arrive underscore-joined (EWP poke parameters can't contain spaces) - render them back.
            if (!string.IsNullOrEmpty(reason)) desc += $" \u2014 {reason.Replace('_', ' ')}";

            var embed = new ClientLogRelay.Webhook.MinimalWebhookPoster.Embed()
                .SetTitle("\U0001F4F8 Screenshot")
                .SetDescription(desc)
                .SetColor(DiscordColors.Blue)
                .SetImage("attachment://snapshot.jpg")
                .SetFooter(GetServerName());

            var files = new List<ClientLogRelay.Webhook.MinimalWebhookPoster.Attachment>
            {
                new ClientLogRelay.Webhook.MinimalWebhookPoster.Attachment("snapshot.jpg", jpgBytes, "image/jpeg")
            };
            ClientLogRelay.Webhook.MinimalWebhookPoster.Post(url, embed, files, GetUsername(), GetAvatarUrl());
            LogVerbose($"Sent triggered screenshot ({jpgBytes.Length / 1024}KB): {playerName}" +
                       (string.IsNullOrEmpty(reason) ? "" : $" ({reason})"));
        }

        // One sentence per vanilla HitData.HitType, using the dying client's own last-hit report
        // (attacker names arrive pre-localized from the client).
        private static string DescribeDeath(string playerName, DeathScreenshots.DeathInfo info)
        {
            string p = $"**{playerName}**";
            if (info == null) return $"{p} has fallen.";

            bool hasAttacker = !string.IsNullOrEmpty(info.Attacker);
            string a = hasAttacker ? $"**{info.Attacker}**" : null;

            switch (info.HitType)
            {
                case HitData.HitType.EnemyHit:      return hasAttacker ? $"{p} was slain by {a}." : $"{p} was slain.";
                case HitData.HitType.PlayerHit:     return hasAttacker ? $"{p} was killed by {a} in PvP." : $"{p} was killed in PvP.";
                case HitData.HitType.Fall:          return $"{p} fell to their death.";
                case HitData.HitType.Drowning:      return $"{p} drowned.";
                case HitData.HitType.Burning:       return $"{p} burned to death.";
                case HitData.HitType.Freezing:      return $"{p} froze to death.";
                case HitData.HitType.Poisoned:      return $"{p} succumbed to poison.";
                case HitData.HitType.Water:         return $"{p} was claimed by the water.";
                case HitData.HitType.Smoke:         return $"{p} suffocated in smoke.";
                case HitData.HitType.EdgeOfWorld:   return $"{p} fell off the edge of the world.";
                case HitData.HitType.Impact:        return $"{p} died on impact.";
                case HitData.HitType.Cart:          return $"{p} was crushed by a cart.";
                case HitData.HitType.Tree:          return $"{p} was crushed by a falling tree.";
                case HitData.HitType.Self:          return $"{p} died by their own hand.";
                case HitData.HitType.Structural:    return $"{p} was crushed by a collapsing structure.";
                case HitData.HitType.Turret:        return hasAttacker ? $"{p} was shot down by {a}." : $"{p} was shot down by a turret.";
                case HitData.HitType.Boat:          return $"{p} was crushed by a boat.";
                case HitData.HitType.Stalagtite:    return $"{p} was impaled by a stalactite.";
                case HitData.HitType.Catapult:      return $"{p} was launched by a catapult.";
                case HitData.HitType.CinderFire:    return $"{p} burned in cinder.";
                case HitData.HitType.AshlandsOcean: return $"{p} was boiled in the Ashlands ocean.";
                default:                            return hasAttacker ? $"{p} was slain by {a}." : $"{p} has fallen.";
            }
        }

        // ============================================================
        // Chat Events
        // ============================================================

        // Differentiates the three Valheim chat types the integration relays so the
        // Discord post can mark which channel a line came from.  Ping is omitted on
        // purpose - those are map-marker pings, not chat.
        public enum ChatLineType
        {
            Say,
            Shout,
            Whisper,
        }

        // How long to delay a chat relay so the first-ever @mention of a session
        // can wait for the role-name fetch to land.  The fetch typically returns
        // in ~1s; cap at 5s so a misconfigured / unreachable Discord doesn't
        // hold up chat indefinitely (the message still goes out - just without
        // resolution, the same behaviour as before this guard was added).
        private const float MentionResolveWaitTimeoutSeconds = 5f;

        public static void OnPlayerChat(string playerName, string message, ChatLineType lineType)
        {
            if (!IsActive())
            {
                ChatSkipOnce("inactive", $"[Discord] OnPlayerChat skipped: IsActive() == false (Discord.Enabled is off in cfg). lineType={lineType}");
                return;
            }
            if (!IsChatTypeEnabled(lineType))
            {
                ChatSkipOnce("off:" + lineType, $"[Discord] OnPlayerChat skipped: {lineType} disabled in cfg (NotifyPlayer{lineType} == false). Flip it on under [Discord.Events] to relay this chat type.");
                return;
            }
            if (IgnoredByPrefix(message, out string prefix))
            {
                if (_loggedIgnoredPrefixes.Add(prefix))
                    Debug.Log($"[Discord] chat lines starting with '{prefix}' are not relayed ([Discord.Events] ChatRelayIgnorePrefixes; said once per session).");
                return;
            }

            // If the message has an @-mention and either the role cache or
            // the member-fetch attempt hasn't landed yet, park the relay on
            // a coroutine that waits for the fetch (with a short timeout) so
            // the first shout of a session resolves to a real Discord ping
            // instead of going out as literal text.  Without this, the
            // historical behaviour was: ResolveMentions kicks off the
            // fetch, returns no resolution, the message ships unresolved,
            // and the player has to send a second @ before the cache is
            // warm.  The member-fetch gate is "attempted once" (not "user
            // cache populated") so a guild that hasn't granted the Server
            // Members intent doesn't make every @-message burn the timeout.
            bool hasMention = !string.IsNullOrEmpty(message) && message.IndexOf('@') >= 0;
            bool mentionTargetsCold = !DiscordMentionResolver.HasRoles
                                   || !DiscordMentionResolver.HasAttemptedMembersFetch;
            if (hasMention && mentionTargetsCold)
            {
                var host = DiscordMentionResolver.CoroutineHostInstance();
                if (host != null)
                {
                    host.StartCoroutine(DiscordMentionResolver.WaitForRolesThen(
                        MentionResolveWaitTimeoutSeconds,
                        () => SendChatLineToDiscord(playerName, message, lineType)));
                    return;
                }
                // No host available (e.g. ZNet not up yet) - fall through to
                // the synchronous path so we at least relay the line.
            }

            SendChatLineToDiscord(playerName, message, lineType);
        }

        // Synchronous end of the chat-relay pipeline: resolve mentions against
        // the (now-warm) role cache, format the line, and fire the webhook.
        // Split out from OnPlayerChat so the role-fetch wait can re-enter via a
        // coroutine continuation without duplicating the send logic.
        private static void SendChatLineToDiscord(string playerName, string message, ChatLineType lineType)
        {
            // "@admin" -> "<@&ROLE_ID>" + collect the role IDs we resolved so the
            // webhook payload can force-allow them through Discord's
            // allowed_mentions guard (otherwise non-mentionable roles would
            // render as silent <@&...> text instead of pinging).
            // Security: cap the line and strip @everyone/@here/role tags before anything is resolved or posted;
            // role pings from players are not resolved (roles stay off in allowed_mentions for chat).
            message = FiresDiscordIntegration.ChatRelay.ChatSecurity.SanitizeText(message, FiresDiscordIntegration.ChatRelay.ChatSecurity.MaxTextLength);
            playerName = FiresDiscordIntegration.ChatRelay.ChatSecurity.SanitizeText(playerName, FiresDiscordIntegration.ChatRelay.ChatSecurity.MaxNameLength);
            if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(playerName)) return;
            var resolved = DiscordMentionResolver.ResolveMentions(message, resolveRoles: false);

            string body = FormatChatLineBody(playerName, resolved.Message, lineType);
            string url  = DiscordIntegrationConfig.ResolveWebhookUrl(WebhookCategory.Chat);
            if (string.IsNullOrEmpty(url))
            {
                Debug.Log($"[Discord] OnPlayerChat skipped: no webhook URL resolved for category Chat. Set [Discord.Webhooks] ChatWebhookUrl (or GeneralWebhookUrl as fallback).");
                return;
            }

            DiscordWebhookService.SendMessage(
                url, body,
                GetUsername(), GetAvatarUrl(),
                forceAllowedRoleIds: null,
                allowRoleMentions: false);

            FiresLogger.LogDebug($"[Discord] Relayed {lineType} from '{playerName}' to Discord ({resolved.ResolvedRoleIds.Count} role mention(s) resolved).");
        }

        // A config skip is news once per reason (a plain log shows why chat never reaches Discord); every later message is Debug.
        private static readonly HashSet<string> _loggedChatSkips = new HashSet<string>();

        private static void ChatSkipOnce(string key, string line)
        {
            if (_loggedChatSkips.Add(key)) Debug.Log(line + " (said once per session; later skips at Debug)");
            else FiresLogger.LogDebug(line);
        }

        // Backwards-compatible shim - kept so any external code calling the old
        // entry point still works.  Routes through the new unified path.
        public static void OnPlayerShout(string playerName, string message) =>
            OnPlayerChat(playerName, message, ChatLineType.Shout);

        /// <summary>
        /// Whether OnPlayerChat will post this chat type: Discord on, the type switched on and a webhook or bot channel resolved
        /// (the server only tells a shouter "relayed" when it was).
        /// </summary>
        internal static bool WillRelay(ChatLineType lineType, string message) =>
            IsActive() && IsChatTypeEnabled(lineType) && !IgnoredByPrefix(message, out _)
            && !string.IsNullOrEmpty(DiscordIntegrationConfig.ResolveWebhookUrl(WebhookCategory.Chat));

        // [Discord.Events] ChatRelayIgnorePrefixes: chat lines starting with one of these words are kept out of Discord, e.g.
        // commands typed in chat for a bot ("bot drill give Club"). A word prefix must be followed by a non-letter/digit or the end of the line, so
        // "bot" does not catch "bottle"; a prefix ending in punctuation ("!", "/") matches anything after it.
        private static string _ignorePrefixesRaw;
        private static string[] _ignorePrefixes = new string[0];
        private static readonly HashSet<string> _loggedIgnoredPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static bool IgnoredByPrefix(string message, out string prefix)
        {
            prefix = null;
            string raw = DiscordIntegrationConfig.ChatRelayIgnorePrefixes?.Value ?? "";
            if (!string.Equals(raw, _ignorePrefixesRaw, StringComparison.Ordinal))
            {
                _ignorePrefixesRaw = raw;
                _ignorePrefixes = raw.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            }
            if (string.IsNullOrEmpty(message) || _ignorePrefixes.Length == 0) return false;
            string text = message.TrimStart();
            foreach (string candidate in _ignorePrefixes)
            {
                if (!text.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)) continue;
                // A word prefix ends at anything that is not a letter or digit, so "bot" also covers the bot's own replies
                // ("bot: drill: gave FishingRod x1").
                bool word = char.IsLetterOrDigit(candidate[candidate.Length - 1]);
                if (word && text.Length > candidate.Length && char.IsLetterOrDigit(text[candidate.Length])) continue;
                prefix = candidate;
                return true;
            }
            return false;
        }

        private static bool IsChatTypeEnabled(ChatLineType lineType)
        {
            switch (lineType)
            {
                case ChatLineType.Shout:   return DiscordIntegrationConfig.NotifyPlayerShout?.Value   == true;
                case ChatLineType.Say:     return DiscordIntegrationConfig.NotifyPlayerSay?.Value     == true;
                case ChatLineType.Whisper: return DiscordIntegrationConfig.NotifyPlayerWhisper?.Value == true;
                default: return false;
            }
        }

        // Discord-side decoration of one chat line.  Shouts get the megaphone the
        // mod has always used; say lines use a chat-bubble; whispers italicise the
        // body and add a (whisper) tag so admins reading the channel can tell at
        // a glance whether it was meant to be private.
        private static string FormatChatLineBody(string playerName, string message, ChatLineType lineType)
        {
            switch (lineType)
            {
                case ChatLineType.Shout:
                    return $"\U0001F4E2 **{playerName}:** {message}";
                case ChatLineType.Whisper:
                    return $"\U0001F92B **{playerName}** *(whisper)*: *{message}*";
                case ChatLineType.Say:
                default:
                    return $"\U0001F4AC **{playerName}:** {message}";
            }
        }

        // ============================================================
        // World Events (Raids)
        // ============================================================

        /// <summary>
        /// The server finished with one random event post: the event's name, "start" or "stop", true when Discord took it,
        /// and the log detail (the HTTP code, or why it was skipped). Raised on the server only; tests listen for it.
        /// </summary>
        public static event Action<string, string, bool, string> RandomEventReported;

        // Called on the server from RandEventSystem.SetRandomEvent (DiscordIntegrationPatches), whatever the clients run.
        public static void OnRandomEventStart(string eventName, string startMessage, Vector3 position)
        {
            if (!RandomEventRoute(eventName, "start", out string url)) return;

            var embed = new DiscordEmbed()
                .SetTitle("\u2694\uFE0F Event Started")
                .SetColor(DiscordColors.Purple)
                .AddField("Event", eventName, true)
                .AddField("Message", LocalizeOrRaw(startMessage), true)
                .AddField("Location", $"{position.x:F0}, {position.y:F0}, {position.z:F0}", true)
                .SetFooter(GetServerName());

            DiscordWebhookService.SendEmbedWithResult(url, embed, GetUsername(), GetAvatarUrl(),
                code => ReportRandomEvent(eventName, "start", code));
        }

        public static void OnRandomEventStop(string eventName, string endMessage)
        {
            if (!RandomEventRoute(eventName, "stop", out string url)) return;

            var embed = new DiscordEmbed()
                .SetTitle("\U0001F6D1 Event Ended")
                .SetDescription(LocalizeOrRaw(endMessage))
                .SetColor(DiscordColors.Yellow)
                .AddField("Event", eventName, true)
                .SetFooter(GetServerName());

            DiscordWebhookService.SendEmbedWithResult(url, embed, GetUsername(), GetAvatarUrl(),
                code => ReportRandomEvent(eventName, "stop", code));
        }

        // Where a random event posts, or false with one Info line saying why it doesn't (raids are rare, so a line each is fine).
        private static bool RandomEventRoute(string eventName, string phase, out string url)
        {
            url = null;
            string skipped = null;
            if (DiscordIntegrationConfig.Enabled == null || !DiscordIntegrationConfig.Enabled.Value)
                skipped = "[Discord] Enabled = false";
            else if (DiscordIntegrationConfig.NotifyRandomEvent?.Value != true)
                skipped = "[Discord.Events] NotifyRandomEvent = false";
            else
            {
                url = DiscordIntegrationConfig.ResolveWebhookUrl(WebhookCategory.Events);
                if (string.IsNullOrEmpty(url))
                    skipped = "no webhook ([Discord.Webhooks] EventsWebhookUrl and GeneralWebhookUrl are empty)";
                else if (!DiscordWebhookService.IsTarget(url))
                    skipped = "[Discord.Webhooks] EventsWebhookUrl / GeneralWebhookUrl is not a Discord webhook URL";
            }
            if (skipped == null) return true;

            FiresLogger.LogInfo($"[Discord] event '{eventName}' {phase} skipped: {skipped}");
            RaiseRandomEventReported(eventName, phase, false, "skipped: " + skipped);
            return false;
        }

        private static void ReportRandomEvent(string eventName, string phase, long code)
        {
            bool posted = code >= 200 && code < 300;
            if (posted) FiresLogger.LogDebug($"[Discord] event '{eventName}' {phase} posted (HTTP {code})");
            else FiresLogger.LogWarning($"[Discord] event '{eventName}' {phase} failed: HTTP {code}");
            RaiseRandomEventReported(eventName, phase, posted, "HTTP " + code);
        }

        private static void RaiseRandomEventReported(string eventName, string phase, bool posted, string detail)
        {
            try { RandomEventReported?.Invoke(eventName, phase, posted, detail); }
            catch (Exception ex) { FiresLogger.LogWarning($"[Discord] a RandomEventReported listener threw {ex.GetType().Name}: {ex.Message}"); }
        }

        // Event messages are "$..." tokens; the server's English text when it has one, the token otherwise.
        private static string LocalizeOrRaw(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            try { return Localization.instance != null ? Localization.instance.Localize(text) : text; }
            catch { return text; }
        }

        public static void OnNewDay(int dayNumber)
        {
            if (!IsActive()) return;
            if (DiscordIntegrationConfig.NotifyNewDay?.Value != true) return;

            var url = DiscordIntegrationConfig.ResolveWebhookUrl(WebhookCategory.General);
            DiscordWebhookService.SendMessage(url,
                $"\U0001F305 **New day: {dayNumber}**",
                GetUsername(), GetAvatarUrl());
            LogVerbose($"Sent NewDay webhook: day {dayNumber}");
        }

        // ============================================================
        // Anti-Cheat Events (called from VAngardeCore)
        // ============================================================

        /// <summary>
        /// Called when VAngarde detects a violation (before enforcement decision).
        /// </summary>
        public static void OnAntiCheatViolation(string playerName, string platformId, string reason, int violationCount)
        {
            if (!IsActive()) return;
            if (DiscordIntegrationConfig.NotifyAntiCheatViolation?.Value != true) return;

            var url = DiscordIntegrationConfig.ResolveWebhookUrl(WebhookCategory.AntiCheat);
            var embed = new DiscordEmbed()
                .SetTitle("\u26A0\uFE0F Anti-Cheat Violation")
                .SetColor(DiscordColors.Orange)
                .AddField("Player", playerName ?? "Unknown", true)
                .AddField("Steam ID", platformId ?? "Unknown", true)
                .AddField("Violation #", violationCount.ToString(), true)
                .AddField("Reason", reason ?? "Unknown")
                .SetFooter($"VAngarde - {GetServerName()}");

            DiscordWebhookService.SendEmbed(url, embed, GetUsername(), GetAvatarUrl());
            LogVerbose($"Sent AntiCheatViolation webhook: {platformId} - {reason}");
        }

        /// <summary>
        /// Admin (or exempt player) tripped a behavioral detection while in monitor mode - logged, not kicked.
        /// </summary>
        public static void OnAdminCheatDetected(string playerName, string platformId, string reason,
            int detectionCount, IReadOnlyList<string> activeAdminCommands)
        {
            if (!IsActive()) return;
            if (DiscordIntegrationConfig.NotifyAdminCheatDetected?.Value != true) return;

            var url = DiscordIntegrationConfig.ResolveWebhookUrl(WebhookCategory.AntiCheat);
            var embed = new DiscordEmbed()
                .SetTitle("\U0001F9E0 Admin Cheat Detected (Not Kicked)")
                .SetColor(DiscordColors.Yellow)
                .AddField("Player", playerName ?? "Unknown", true)
                .AddField("Steam ID", platformId ?? "Unknown", true)
                .AddField("Detection #", detectionCount.ToString(), true)
                .AddField("Reason", reason ?? "Unknown")
                .AddField("Active Admin Commands", FormatAdminCommands(activeAdminCommands))
                .SetFooter($"VAngarde - {GetServerName()}");

            DiscordWebhookService.SendEmbed(url, embed, GetUsername(), GetAvatarUrl());
            LogVerbose($"Sent AdminCheatDetected webhook: {platformId} - {reason}");
        }

        /// <summary>
        /// An admin toggled a cheat/debug command (devcommands/debugmode/god/ghost/fly/nocost) on or off.
        /// </summary>
        public static void OnAdminCommandSnapshot(string playerName, string platformId,
            string changedCommand, bool enabled, IReadOnlyList<string> activeAdminCommands)
        {
            if (!IsActive()) return;
            if (DiscordIntegrationConfig.NotifyAdminCommandSnapshot?.Value != true) return;

            var url = DiscordIntegrationConfig.ResolveWebhookUrl(WebhookCategory.AntiCheat);

            // A "(login)" snapshot is the admin connecting with cheats already active - batch it into
            // ONE "loaded commands" report rather than a per-command toggle message.
            bool isLogin = changedCommand == "(login)";
            var embed = new DiscordEmbed()
                .SetTitle(isLogin ? "\U0001F4E2 Admin Logged In - Active Commands" : "\U0001F4E2 Admin Command Toggled")
                .SetColor(isLogin ? DiscordColors.Blue : (enabled ? DiscordColors.Orange : DiscordColors.Blue))
                .AddField("Player", playerName ?? "Unknown", true)
                .AddField("Steam ID", platformId ?? "Unknown", true);
            if (!isLogin)
                embed.AddField("Command", changedCommand ?? "?", true)
                     .AddField("State", enabled ? "ENABLED" : "DISABLED", true);
            embed.AddField(isLogin ? "Loaded Commands" : "All Active Commands", FormatAdminCommands(activeAdminCommands))
                 .SetFooter($"VAngarde - {GetServerName()}");

            DiscordWebhookService.SendEmbed(url, embed, GetUsername(), GetAvatarUrl());
            LogVerbose($"Sent AdminCommandSnapshot webhook: {platformId} - {changedCommand} {(enabled ? "ON" : "OFF")}");
        }

        private static string FormatAdminCommands(IReadOnlyList<string> commands)
            => (commands == null || commands.Count == 0) ? "None" : string.Join(", ", commands);

        /// <summary>
        /// A server-side VAngarde mod list changed (enforced/whitelist/server-only/blacklist). Posts a readout
        /// to the anti-cheat channel: what was added/removed/version-bumped, the FULL current list (so players
        /// can see what's allowed), and a one-line summary of all four list sizes.
        /// </summary>
        public static void OnServerListChanged(DiscordListChangeInfo info, Action onComplete = null)
        {
            if (!IsActive()) { onComplete?.Invoke(); return; }
            if (DiscordIntegrationConfig.NotifyServerListChanged?.Value != true) { onComplete?.Invoke(); return; }
            if (info == null) { onComplete?.Invoke(); return; }

            var url = DiscordIntegrationConfig.ResolveWebhookUrl(WebhookCategory.AntiCheat);
            var style = ListStyle(info.Kind);

            int added = info.Added?.Count ?? 0;
            int removed = info.Removed?.Count ?? 0;
            int changed = info.Changed?.Count ?? 0;

            var parts = new List<string>();
            if (added > 0) parts.Add($"\u2795 {added} added");
            if (removed > 0) parts.Add($"\u2796 {removed} removed");
            if (changed > 0) parts.Add($"\U0001F504 {changed} version-changed");
            string desc = parts.Count > 0 ? string.Join("  \u2022  ", parts) : "list reloaded";
            if (!string.IsNullOrEmpty(info.Trigger)) desc += $"\nTrigger: {info.Trigger}";

            // Posted via MinimalWebhookPoster (not DiscordWebhookService) because it returns the created
            // message's id/channel - required to register the message so a MODS reaction on it serves the
            // live full-list dump via the ReactionPoller.
            string botToken = Utilities.BotTokenFile.Token;
            bool reactive = !string.IsNullOrEmpty(botToken);

            var embed = new ClientLogRelay.Webhook.MinimalWebhookPoster.Embed()
                .SetTitle($"\U0001F6E1 {style.label} updated")
                .SetColor(style.color)
                .SetDescription(desc);

            AddListFields(embed, "\u2795 Added", info.Added);
            AddListFields(embed, "\u2796 Removed", info.Removed);
            AddListFields(embed, "\U0001F504 Version changed", info.Changed);
            AddListFields(embed, $"{style.emoji} {style.label} now ({info.Current?.Count ?? 0})", info.Current);

            embed.AddField("\U0001F4CB Lists",
                $"\u2705 Enforced {info.EnforcedCount}  \u2022  \U0001F7E2 Whitelist {info.WhitelistCount}  \u2022  " +
                $"\U0001F512 Admin-only {info.AdminOnlyCount}  \u2022  " +
                $"\U0001F5A5\uFE0F Server-only {info.ServerOnlyCount}  \u2022  \u26D4 Blacklist {info.BlacklistCount}", false);
            embed.SetFooter(reactive
                ? $"VAngarde - {GetServerName()}\n{ClientLogRelay.Consumers.DiscordWebhookConsumer.EMOJI_MODS} Full list"
                : $"VAngarde - {GetServerName()}");

            ClientLogRelay.Webhook.MinimalWebhookPoster.Post(url, embed, null, GetUsername(), GetAvatarUrl(),
                onPosted: (messageId, channelId) =>
                {
                    try
                    {
                        // Register the message + pre-react so anyone can click the MODS emoji to get
                        // the full four-list dump (served live by the ReactionPoller).
                        if (reactive && !string.IsNullOrEmpty(messageId) && !string.IsNullOrEmpty(channelId))
                        {
                            FiresCore.ClientLogRelay.Interactions.LogRequestRegistry.Register(
                                new FiresCore.ClientLogRelay.Interactions.LogRequestContext(
                                    messageId, "server", "Server mod lists", DateTime.UtcNow,
                                    ClientLogRelay.Interactions.ReactionPoller.ServerListConsumerId, channelId));
                            ClientLogRelay.Webhook.MinimalWebhookPoster.AddReaction(
                                botToken, channelId, messageId,
                                ClientLogRelay.Consumers.DiscordWebhookConsumer.EMOJI_MODS);
                        }
                    }
                    finally { onComplete?.Invoke(); }
                });
            LogVerbose($"Sent ServerListChanged webhook{(reactive ? " (reactive)" : "")}: {info.Kind} ({desc.Replace("\n", " ")})");
        }

        private static (string label, string emoji, int color) ListStyle(DiscordListKind kind)
        {
            switch (kind)
            {
                case DiscordListKind.Enforced:   return ("Enforced mod list (exact match)", "\u2705", DiscordColors.Blue);
                case DiscordListKind.Whitelist:  return ("Whitelist (allowed extras)", "\U0001F7E2", DiscordColors.Green);
                case DiscordListKind.ServerOnly: return ("Server-only mods", "\U0001F5A5\uFE0F", DiscordColors.Purple);
                case DiscordListKind.Blacklist:  return ("Blacklist (banned)", "\u26D4", DiscordColors.DarkRed);
                case DiscordListKind.AdminOnly:  return ("Admin-only mods", "\U0001F512", DiscordColors.Orange);
                default:                         return ("Server list", "\U0001F4CB", DiscordColors.Blue);
            }
        }

        // Pack display-ready lines into embed fields, honoring Discord's ~1024-char field cap by spilling into
        // "(cont.)" fields, and truncating with a "(N more)" note past a sane field budget.
        private static void AddListFields(ClientLogRelay.Webhook.MinimalWebhookPoster.Embed embed, string header,
            IReadOnlyList<string> items, int maxChars = 1000, int maxFields = 5)
        {
            if (items == null || items.Count == 0) return;

            var sb = new StringBuilder();
            int fields = 0, shown = 0;
            for (int i = 0; i < items.Count; i++)
            {
                string line = items[i] ?? string.Empty;
                if (sb.Length > 0 && sb.Length + line.Length + 1 > maxChars)
                {
                    embed.AddField(fields == 0 ? header : header + " (cont.)", sb.ToString(), false);
                    sb.Length = 0;
                    if (++fields >= maxFields)
                    {
                        int remaining = items.Count - shown;
                        if (remaining > 0) embed.AddField(header + " (cont.)", $"*\u2026and {remaining} more*", false);
                        return;
                    }
                }
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line);
                shown++;
            }
            if (sb.Length > 0)
                embed.AddField(fields == 0 ? header : header + " (cont.)", sb.ToString(), false);
        }

        /// <summary>
        /// Called when VAngarde kicks/disconnects a player.
        /// </summary>
        public static void OnAntiCheatKick(string playerName, string platformId, string reason,
            Action onComplete = null)
        {
            OnAntiCheatKick(playerName, platformId, reason, null, null, onComplete);
        }

        /// <summary>
        /// Called when VAngarde kicks/disconnects a player.
        /// Optionally includes the client's mod list for debugging.
        /// </summary>
        public static void OnAntiCheatKick(string playerName, string platformId, string reason,
            Dictionary<string, string> clientModList, byte[] clientBepInExLog,
            Action onComplete = null)
        {
            if (!IsActive()) { onComplete?.Invoke(); return; }
            if (DiscordIntegrationConfig.NotifyAntiCheatKick?.Value != true) { onComplete?.Invoke(); return; }

            var url = DiscordIntegrationConfig.ResolveWebhookUrl(WebhookCategory.AntiCheat);

            var embed = new DiscordEmbed()
                .SetTitle("\U0001F6AB Player Kicked - Anti-Cheat")
                .SetColor(DiscordColors.DarkRed)
                .AddField("Player", playerName ?? "Unknown", true)
                .AddField("Steam ID", platformId ?? "Unknown", true)
                .AddField("Reason", reason ?? "Unknown")
                .SetFooter($"VAngarde - {GetServerName()}");

            // Collect file attachments
            var files = new List<DiscordFileAttachment>();
            string safeId = (platformId ?? "unknown").Replace(":", "_").Replace("/", "_").Replace("\\", "_");

            // Attach mod comparison report (client + server + mismatch analysis)
            if (clientModList != null && clientModList.Count > 0 &&
                DiscordIntegrationConfig.SendModListOnKick?.Value == true)
            {
                // Get the server's mod list for cross-reference
                Dictionary<string, string> serverModList = null;
                try
                {
                    serverModList = null /* VAngarde absent in standalone build */;
                }
                catch { /* non-critical */ }

                string modListText = BuildModListText(playerName, platformId, clientModList, serverModList);
                files.Add(new DiscordFileAttachment(
                    $"modlist_{safeId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt",
                    modListText));

                embed.AddField("Client Mods", clientModList.Count.ToString(), true);
                if (serverModList != null)
                    embed.AddField("Server Mods", serverModList.Count.ToString(), true);
            }

            // Attach the CLIENT's BepInEx output log if enabled and available
            if (DiscordIntegrationConfig.SendBepInExLogOnKick?.Value == true)
            {
                byte[] logData = clientBepInExLog;
                var    logSource = AttachedLogSource.ClientFresh;

                // Primary fallback: cached log written to disk on the last challenge response
                if (logData == null || logData.Length == 0)
                {
                    byte[] cached = null /* VAngarde absent in standalone build */;
                    if (cached != null && cached.Length > 0)
                    {
                        logData   = cached;
                        logSource = AttachedLogSource.ClientCached;
                    }
                }

                // Last-resort fallback: server's own BepInEx log
                if (logData == null || logData.Length == 0)
                {
                    logData   = ReadBepInExLog();
                    logSource = AttachedLogSource.ServerFallback;
                }

                if (logData != null && logData.Length > 0)
                {
                    files.Add(new DiscordFileAttachment(
                        $"{LogSourceFilenamePrefix(logSource)}_log_{safeId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt",
                        logData));
                    embed.AddField(LogSourceFieldLabel(logSource), "Attached", true);
                }
            }

            // Send with files if we have any, otherwise just the embed.
            // The onComplete callback is invoked after the webhook upload finishes
            // so the caller (VAngardeCore) can defer the actual kick until Discord has the data.
            if (files.Count > 0)
            {
                DiscordWebhookService.SendEmbedWithFiles(url, embed, files, GetUsername(), GetAvatarUrl(), onComplete);
            }
            else
            {
                DiscordWebhookService.SendEmbed(url, embed, GetUsername(), GetAvatarUrl(), onComplete);
            }

            LogVerbose($"Sent AntiCheatKick webhook: {platformId} - {reason}" +
                       (files.Count > 0 ? $" (with {files.Count} file attachment(s))" : ""));
        }

        /// <summary>
        /// Build a human-readable mod comparison report for a kicked player.
        /// Includes client mods, server mods, and a cross-reference mismatch analysis.
        /// </summary>
        private static string BuildModListText(string playerName, string platformId,
            Dictionary<string, string> clientMods, Dictionary<string, string> serverMods)
        {
            var sb = new StringBuilder();
            sb.AppendLine("==========================================================================");
            sb.AppendLine("  VAngarde Anti-Cheat - Mod Comparison Report");
            sb.AppendLine("==========================================================================");
            sb.AppendLine();
            sb.AppendLine($"Player:      {playerName ?? "Unknown"}");
            sb.AppendLine($"Steam ID:    {platformId ?? "Unknown"}");
            sb.AppendLine($"Server:      {GetServerName()}");
            sb.AppendLine($"Timestamp:   {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"Client Mods: {clientMods.Count}");
            sb.AppendLine($"Server Mods: {serverMods?.Count ?? 0}");
            sb.AppendLine();

            // -- Mismatch Analysis ------------------------------------
            if (serverMods != null && serverMods.Count > 0)
            {
                var clientOnly = new List<KeyValuePair<string, string>>();
                var serverOnly = new List<KeyValuePair<string, string>>();
                var versionMismatch = new List<(string guid, string clientVer, string serverVer)>();
                var matching = new List<string>();

                // All unique GUIDs across both lists
                var allGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in clientMods) allGuids.Add(kv.Key);
                foreach (var kv in serverMods) allGuids.Add(kv.Key);

                foreach (var guid in allGuids.OrderBy(g => g, StringComparer.OrdinalIgnoreCase))
                {
                    bool inClient = clientMods.TryGetValue(guid, out string cVer);
                    bool inServer = serverMods.TryGetValue(guid, out string sVer);

                    if (inClient && inServer)
                    {
                        if (!string.Equals(cVer, sVer, StringComparison.OrdinalIgnoreCase))
                            versionMismatch.Add((guid, cVer, sVer));
                        else
                            matching.Add(guid);
                    }
                    else if (inClient && !inServer)
                    {
                        clientOnly.Add(new KeyValuePair<string, string>(guid, cVer));
                    }
                    else if (!inClient && inServer)
                    {
                        serverOnly.Add(new KeyValuePair<string, string>(guid, sVer));
                    }
                }

                sb.AppendLine("==========================================================================");
                sb.AppendLine("  MISMATCH ANALYSIS");
                sb.AppendLine("==========================================================================");
                sb.AppendLine();
                sb.AppendLine($"  Matching:           {matching.Count}");
                sb.AppendLine($"  Version Mismatch:   {versionMismatch.Count}");
                sb.AppendLine($"  Client-Only:        {clientOnly.Count}  (mods the server does NOT have)");
                sb.AppendLine($"  Server-Only:        {serverOnly.Count}  (mods the client is MISSING)");
                sb.AppendLine();

                if (versionMismatch.Count > 0)
                {
                    sb.AppendLine("  -- VERSION MISMATCHES ------------------------------------------");
                    sb.AppendLine($"  {"GUID",-44} {"Client",-16} {"Server",-16}");
                    sb.AppendLine($"  {new string('-', 44)} {new string('-', 16)} {new string('-', 16)}");
                    foreach (var (guid, cV, sV) in versionMismatch)
                    {
                        sb.AppendLine($"  {guid,-44} {cV,-16} {sV,-16}");
                    }
                    sb.AppendLine();
                }

                if (clientOnly.Count > 0)
                {
                    sb.AppendLine("  -- CLIENT-ONLY MODS (not on server) ---------------------------");
                    sb.AppendLine($"  {"GUID",-44} {"Version",-16}");
                    sb.AppendLine($"  {new string('-', 44)} {new string('-', 16)}");
                    foreach (var kv in clientOnly)
                    {
                        sb.AppendLine($"  {kv.Key,-44} {kv.Value,-16}");
                    }
                    sb.AppendLine();
                }

                if (serverOnly.Count > 0)
                {
                    sb.AppendLine("  -- SERVER-ONLY MODS (missing from client) ---------------------");
                    sb.AppendLine($"  {"GUID",-44} {"Version",-16}");
                    sb.AppendLine($"  {new string('-', 44)} {new string('-', 16)}");
                    foreach (var kv in serverOnly)
                    {
                        sb.AppendLine($"  {kv.Key,-44} {kv.Value,-16}");
                    }
                    sb.AppendLine();
                }

                if (versionMismatch.Count == 0 && clientOnly.Count == 0 && serverOnly.Count == 0)
                {
                    sb.AppendLine("  No mismatches found - client and server mod lists are identical.");
                    sb.AppendLine();
                }
            }
            else
            {
                sb.AppendLine("  [Server mod list unavailable - cross-reference skipped]");
                sb.AppendLine();
            }

            // -- Full Client Mod List ---------------------------------
            sb.AppendLine("==========================================================================");
            sb.AppendLine($"  CLIENT MOD LIST ({clientMods.Count} mods)");
            sb.AppendLine("==========================================================================");
            sb.AppendLine($"  {"GUID",-44} {"Version",-16}");
            sb.AppendLine($"  {new string('-', 44)} {new string('-', 16)}");

            foreach (var kvp in clientMods.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"  {kvp.Key,-44} {kvp.Value,-16}");
            }
            sb.AppendLine();

            // -- Full Server Mod List ---------------------------------
            if (serverMods != null && serverMods.Count > 0)
            {
                sb.AppendLine("==========================================================================");
                sb.AppendLine($"  SERVER MOD LIST ({serverMods.Count} mods)");
                sb.AppendLine("==========================================================================");
                sb.AppendLine($"  {"GUID",-44} {"Version",-16}");
                sb.AppendLine($"  {new string('-', 44)} {new string('-', 16)}");

                foreach (var kvp in serverMods.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"  {kvp.Key,-44} {kvp.Value,-16}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("==========================================================================");
            sb.AppendLine("  End of Report");
            sb.AppendLine("==========================================================================");

            return sb.ToString();
        }

        /// <summary>
        /// Post a player's cached BepInEx log to the anti-cheat Discord webhook on admin request.
        /// Called by 'vangarde log &lt;steamid&gt;' console command.
        /// </summary>
        public static void SendClientLogOnRequest(string playerName, string platformId, byte[] logBytes)
        {
            if (!IsActive()) return;
            if (logBytes == null || logBytes.Length == 0) return;

            var url = DiscordIntegrationConfig.ResolveWebhookUrl(WebhookCategory.AntiCheat);
            string safeId = (platformId ?? "unknown").Replace(":", "_").Replace("/", "_").Replace("\\", "_");

            var embed = new DiscordEmbed()
                .SetTitle("\U0001F4C4 Client Log - Admin Request")
                .SetColor(DiscordColors.Blue)
                .AddField("Player", playerName ?? "Unknown", true)
                .AddField("Steam ID", platformId ?? "Unknown", true)
                .AddField("Log", "Attached", true)
                .SetFooter($"VAngarde - {GetServerName()}");

            var files = new System.Collections.Generic.List<DiscordFileAttachment>
            {
                new DiscordFileAttachment(
                    $"client_log_{safeId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt",
                    logBytes)
            };

            DiscordWebhookService.SendEmbedWithFiles(url, embed, files, GetUsername(), GetAvatarUrl());
            LogVerbose($"Sent on-request client log for {platformId} to Discord ({logBytes.Length} bytes)");
        }

        /// <summary>
        /// Read the server's BepInEx output log file (LogOutput.log).
        /// Used as a fallback when the client's log wasn't received via the challenge response.
        /// Returns the raw bytes, or null if the file doesn't exist or can't be read.
        /// The file is read with FileShare.ReadWrite so it doesn't conflict with BepInEx writing to it.
        /// </summary>
        private static byte[] ReadBepInExLog()
        {
            try
            {
                // BepInEx writes to LogOutput.log in the BepInEx root
                string logPath = Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log");
                if (!File.Exists(logPath))
                {
                    LogVerbose($"BepInEx log not found at: {logPath}");
                    return null;
                }

                // Use FileShare.ReadWrite so we don't lock BepInEx's active log file
                using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long length = fs.Length;
                    if (length <= 0) return null;

                    byte[] data = new byte[length];
                    int offset = 0;
                    while (offset < data.Length)
                    {
                        int read = fs.Read(data, offset, data.Length - offset);
                        if (read <= 0) break;
                        offset += read;
                    }

                    if (offset < data.Length)
                    {
                        byte[] trimmed = new byte[offset];
                        Array.Copy(data, 0, trimmed, 0, offset);
                        return trimmed;
                    }
                    return data;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Discord] Failed to read BepInEx log: {ex.Message}");
                return null;
            }
        }

        // ============================================================
        // Utilities
        // ============================================================

        private static void LogVerbose(string msg)
        {
            if (DiscordIntegrationConfig.VerboseLogging != null &&
                DiscordIntegrationConfig.VerboseLogging.Value)
                Debug.Log($"[Discord] {msg}");
        }
    }
}
