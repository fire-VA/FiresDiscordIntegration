using System;
using HarmonyLib;
using UnityEngine;

namespace FiresDiscordIntegration.Discord
{
    /// <summary>
    /// Client-side triggers for the progress posts: vanilla skill level-ups (patched directly — vanilla
    /// shows its own top-left toast, which the capture delay lets land in frame) and achievements (via the
    /// FUC <c>ProgressEvents</c> bridge, raised by FiresRPGmaker's completion watcher; when RPGMaker isn't
    /// installed nothing ever raises, so achievements are skipped naturally). Both ship through
    /// <see cref="ShotPipeline"/> and post server-side to the Progress webhook.
    ///
    /// Skill spam control: only levels at/above <c>SkillLevelUpMinLevel</c> that land on a
    /// <c>SkillLevelUpStep</c> boundary are announced (defaults 10/5 → 10, 15, 20, ...; step 1 = every level).
    /// </summary>
    [HarmonyPatch]
    internal static class ProgressScreenshots
    {
        private const float SkillCaptureDelaySeconds = 0.4f;
        private const float AchievementCaptureDelaySeconds = 0.9f;
        private const float BridgeCaptureDelaySeconds = 0.2f;
        private const float BridgeCooldownSeconds = 5f;

        private static bool _subscribed;
        private static float _nextBridgeCaptureAllowedAt;

        // ── Achievement bridge subscription (client) ──────────────────────────────────────────────────
        [HarmonyPatch(typeof(ZNet), "Awake")]
        [HarmonyPostfix]
        private static void ZNet_Awake_Postfix(ZNet __instance)
        {
            try
            {
                if (__instance == null || __instance.IsServer() || _subscribed) return;
                _subscribed = true;
                FiresCore.Bridge.ProgressEvents.AchievementCompleted += OnAchievementCompleted;
                FiresCore.Bridge.ProgressEvents.ScreenshotRequested += OnScreenshotRequested;
            }
            catch (Exception ex) { Debug.LogWarning($"[Discord] Achievement subscribe failed: {ex.Message}"); }
        }

        // Bridge-requested captures come from automated producers (FiresDebugginTools' census timer), so the
        // cooldown is the same guard the EWP poke path needs: a producer on a short loop must not be able to
        // turn one webhook into a flood.
        private static void OnScreenshotRequested(string reason)
        {
            try
            {
                if (ZNet.instance == null || ZNet.instance.IsServer()) return;
                if (DiscordIntegrationConfig.BridgeScreenshots?.Value != true) return;
                if (Time.realtimeSinceStartup < _nextBridgeCaptureAllowedAt) return;
                _nextBridgeCaptureAllowedAt = Time.realtimeSinceStartup + BridgeCooldownSeconds;

                ShotPipeline.CaptureAndSend(ShotPipeline.ShotKind.Bridge,
                    string.IsNullOrEmpty(reason) ? "Requested" : reason, string.Empty, 0f, BridgeCaptureDelaySeconds);
            }
            catch (Exception ex) { Debug.LogWarning($"[Discord] Bridge screenshot capture failed: {ex.Message}"); }
        }

        private static void OnAchievementCompleted(string name, string description)
        {
            try
            {
                if (ZNet.instance == null || ZNet.instance.IsServer()) return;
                if (DiscordIntegrationConfig.AchievementScreenshots?.Value != true) return;
                if (string.IsNullOrEmpty(name)) return;

                ShotPipeline.CaptureAndSend(ShotPipeline.ShotKind.Achievement,
                    name, description ?? string.Empty, 0f, AchievementCaptureDelaySeconds);
            }
            catch (Exception ex) { Debug.LogWarning($"[Discord] Achievement capture failed: {ex.Message}"); }
        }

        // ── Skill level-ups (client) ──────────────────────────────────────────────────────────────────
        // Natural gains route through Skills.RaiseSkill -> Player.OnSkillLevelup; the raiseskill console
        // command (CheatRaiseSkill) sets the level DIRECTLY and never calls OnSkillLevelup, so it gets its
        // own patch — otherwise admin testing (and admin-granted levels) never announce.
        [HarmonyPatch(typeof(Player), "OnSkillLevelup")]
        [HarmonyPostfix]
        private static void Player_OnSkillLevelup_Postfix(Player __instance, Skills.SkillType skill, float level)
        {
            try
            {
                if (__instance == null || __instance != Player.m_localPlayer) return;
                int lvl = (int)level;
                TryAnnounce(skill, lvl - 1, lvl);
            }
            catch (Exception ex) { Debug.LogWarning($"[Discord] Skill level-up capture failed: {ex.Message}"); }
        }

        private struct CheatSkillState
        {
            public bool Valid;
            public Skills.SkillType Type;
            public int OldLevel;
        }

        [HarmonyPatch(typeof(Skills), "CheatRaiseSkill")]
        [HarmonyPrefix]
        private static void Skills_CheatRaiseSkill_Prefix(Skills __instance, string name, out CheatSkillState __state)
        {
            __state = default;
            try
            {
                if (Player.m_localPlayer == null || __instance != Player.m_localPlayer.GetSkills()) return;
                if (string.IsNullOrEmpty(name) || string.Equals(name, "all", StringComparison.OrdinalIgnoreCase)) return;

                foreach (Skills.SkillType t in Enum.GetValues(typeof(Skills.SkillType)))
                {
                    if (t == Skills.SkillType.All || t == Skills.SkillType.None) continue;
                    if (!string.Equals(t.ToString(), name, StringComparison.OrdinalIgnoreCase)) continue;
                    __state = new CheatSkillState { Valid = true, Type = t, OldLevel = (int)__instance.GetSkillLevel(t) };
                    return;
                }
            }
            catch { }
        }

        [HarmonyPatch(typeof(Skills), "CheatRaiseSkill")]
        [HarmonyPostfix]
        private static void Skills_CheatRaiseSkill_Postfix(Skills __instance, bool showMessage, CheatSkillState __state)
        {
            try
            {
                if (!__state.Valid || !showMessage) return; // showMessage=false = the "all" recursion - skip the spam
                int newLevel = (int)__instance.GetSkillLevel(__state.Type);
                TryAnnounce(__state.Type, __state.OldLevel, newLevel);
            }
            catch (Exception ex) { Debug.LogWarning($"[Discord] Cheat skill capture failed: {ex.Message}"); }
        }

        // Milestone-crossing announce: fires when (oldLevel, newLevel] contains a step multiple at/above the
        // min level. Natural +1 gains reduce to the plain "level % step == 0" check; multi-level jumps
        // (raiseskill) announce once for the highest milestone crossed, captioned with the actual new level.
        private static void TryAnnounce(Skills.SkillType skill, int oldLevel, int newLevel)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;
            if (DiscordIntegrationConfig.SkillLevelUpScreenshots?.Value != true) return;
            if (newLevel <= oldLevel) return;

            int minLevel = DiscordIntegrationConfig.SkillLevelUpMinLevel?.Value ?? 10;
            int step = Math.Max(1, DiscordIntegrationConfig.SkillLevelUpStep?.Value ?? 5);
            int milestone = newLevel / step * step;
            if (milestone < minLevel || milestone <= oldLevel) return;

            ShotPipeline.CaptureAndSend(ShotPipeline.ShotKind.SkillLevelUp,
                SkillDisplayName(skill), string.Empty, newLevel, SkillCaptureDelaySeconds);
        }

        private static string SkillDisplayName(Skills.SkillType skill)
        {
            try
            {
                string localized = Localization.instance.Localize("$skill_" + skill.ToString().ToLower());
                // Missing tokens come back bracketed/unresolved (custom skills) — fall back to the enum name.
                if (!string.IsNullOrEmpty(localized) && localized.IndexOf('[') < 0 && localized.IndexOf("skill_", StringComparison.OrdinalIgnoreCase) < 0)
                    return localized;
            }
            catch { }
            return skill.ToString();
        }
    }
}
