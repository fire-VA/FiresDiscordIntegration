using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresDiscordIntegration.Discord
{
    /// <summary>
    /// Client-side trigger for Valheim's OWN achievements, alongside the FiresRPGmaker ones in
    /// <see cref="ProgressScreenshots"/>. Both ship through <see cref="ShotPipeline"/> to the progress webhook.
    ///
    /// Two things about vanilla's system shape this. Achievements.Initialize re-fires every already-earned
    /// achievement at boot to re-push it to Steam, which would post a screenshot storm at every login - those
    /// calls pass showPopup=false, and only a genuine in-game unlock passes true, so that flag is the filter.
    /// And vanilla records no achievement progress at all once the session counts as cheated, which includes
    /// Game.isModded (Jotunn and others set it), so on a modded server nothing unlocks and nothing posts unless
    /// the server opts in below.
    /// </summary>
    [HarmonyPatch]
    internal static class VanillaAchievements
    {
        private const float CaptureDelaySeconds = 0.9f;

        private static readonly HashSet<string> Announced = new HashSet<string>(StringComparer.Ordinal);

        [HarmonyPatch(typeof(Achievements), nameof(Achievements.AchievementEvent))]
        [HarmonyPostfix]
        private static void Achievements_AchievementEvent_Postfix(Achievement ach, bool showPopup)
        {
            try
            {
                if (!showPopup || ach == null) return;
                if (ZNet.instance == null || ZNet.instance.IsServer()) return;
                if (Player.m_localPlayer == null) return;
                if (DiscordIntegrationConfig.VanillaAchievementScreenshots?.Value != true) return;

                string key = !string.IsNullOrEmpty(ach.m_id) ? ach.m_id : ach.m_name;
                if (string.IsNullOrEmpty(key) || !Announced.Add(key)) return;

                ShotPipeline.CaptureAndSend(ShotPipeline.ShotKind.Achievement,
                    Localize(ach.m_name), Localize(ach.m_description), 0f, CaptureDelaySeconds);
            }
            catch (Exception ex) { Debug.LogWarning($"[Discord] Vanilla achievement capture failed: {ex.Message}"); }
        }

        /// <summary>
        /// Opt-in: let Valheim record achievement progress on a modded profile. An explicit cheated=true from the
        /// caller still wins, so a cheated item or world keeps its own achievements off. Unlocks reach the player's
        /// real Steam profile, which is why this is off unless the server turns it on.
        /// </summary>
        [HarmonyPatch(typeof(Achievements), nameof(Achievements.CanGetAchievements))]
        [HarmonyPostfix]
        private static void Achievements_CanGetAchievements_Postfix(bool cheated, ref bool __result)
        {
            if (__result || cheated) return;
            if (DiscordIntegrationConfig.AllowAchievementsOnModdedProfile?.Value == true)
                __result = true;
        }

        private static string Localize(string token)
        {
            if (string.IsNullOrEmpty(token)) return string.Empty;
            try { return Localization.instance != null ? Localization.instance.Localize(token) : token; }
            catch { return token; }
        }
    }
}
