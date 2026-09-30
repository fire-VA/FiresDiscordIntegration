using System;
using System.Collections.Generic;
using FiresCore.Bridge;

namespace FiresDiscordIntegration.Discord
{
    /// <summary>
    /// Implements <see cref="IDiscordSink"/> (the cross-mod contract in FUC) by delegating to
    /// <see cref="DiscordIntegrationCore"/>. This is the seam producer mods (e.g. FiresVAngarde)
    /// call into via <see cref="DiscordSink"/>; when this mod isn't installed the static facade
    /// no-ops, so Discord stays an optional dependency on the producer side.
    /// </summary>
    internal sealed class DiscordSinkAdapter : IDiscordSink, IDiscordAntiCheatSink
    {
        public bool IsActive() => DiscordIntegrationCore.IsActive();

        public void OnServerListChanged(DiscordListChangeInfo info, Action onComplete = null)
            => DiscordIntegrationCore.OnServerListChanged(info, onComplete);

        public void OnAntiCheatViolation(string playerName, string platformId, string reason, int violationCount)
            => DiscordIntegrationCore.OnAntiCheatViolation(playerName, platformId, reason, violationCount);

        public void OnAntiCheatKick(string playerName, string platformId, string reason, Action onComplete = null)
            => DiscordIntegrationCore.OnAntiCheatKick(playerName, platformId, reason, onComplete);

        public void OnAntiCheatKick(string playerName, string platformId, string reason,
            Dictionary<string, string> clientModList, byte[] clientBepInExLog, Action onComplete = null)
            => DiscordIntegrationCore.OnAntiCheatKick(playerName, platformId, reason,
                clientModList, clientBepInExLog, onComplete);

        public void SendClientLogOnRequest(string playerName, string platformId, byte[] logBytes)
            => DiscordIntegrationCore.SendClientLogOnRequest(playerName, platformId, logBytes);

        public bool NotifyClientLoginArtifacts()
            => DiscordIntegrationConfig.NotifyClientLoginArtifacts?.Value ?? false;

        public void OnAdminCheatDetected(string playerName, string platformId, string reason,
            int detectionCount, IReadOnlyList<string> activeAdminCommands)
            => DiscordIntegrationCore.OnAdminCheatDetected(playerName, platformId, reason, detectionCount, activeAdminCommands);

        public void OnAdminCommandSnapshot(string playerName, string platformId,
            string changedCommand, bool enabled, IReadOnlyList<string> activeAdminCommands)
            => DiscordIntegrationCore.OnAdminCommandSnapshot(playerName, platformId, changedCommand, enabled, activeAdminCommands);
    }
}
