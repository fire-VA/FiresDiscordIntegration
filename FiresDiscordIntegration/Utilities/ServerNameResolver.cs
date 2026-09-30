using System;
using UnityEngine;

namespace FiresDiscordIntegration.Utilities
{
    /// <summary>
    /// Returns the human-facing server name shown in Discord embeds, footer
    /// lines, and BepInEx logs.  Resolution order, with each fallback covering
    /// a stage of server startup:
    ///   1. <c>ZNet.m_ServerName</c> — populated from the <c>-name</c> launch
    ///      arg once <c>ZNet.Awake</c> runs.  Preferred because Valheim has
    ///      already split the operator name from the world filename.
    ///   2. Command-line <c>-name</c> parse — works before <c>ZNet</c> is
    ///      loaded (e.g. for the heartbeat's pre-ZNet log lines).
    ///   3. <c>ZNet.GetWorldName()</c> — last-resort label when the server
    ///      was launched without <c>-name</c>; matches Valheim's own list-server
    ///      display.
    ///   4. <see cref="UnknownNameFallback"/> if nothing else resolves.
    /// Cached after the first non-unknown hit since both sources are immutable
    /// across a session.
    /// </summary>
    public static class ServerNameResolver
    {
        private const string UnknownNameFallback = "Unknown";
        private const string NameLaunchArg       = "-name";

        private static string _cachedServerName;

        public static string GetOperatorServerName()
        {
            if (!string.IsNullOrEmpty(_cachedServerName))
                return _cachedServerName;

            string fromZNetField = TryReadZNetServerName();
            if (!string.IsNullOrEmpty(fromZNetField))
                return _cachedServerName = fromZNetField;

            string fromCommandLine = TryReadNameLaunchArg();
            if (!string.IsNullOrEmpty(fromCommandLine))
                return _cachedServerName = fromCommandLine;

            string fromWorldName = TryReadWorldName();
            if (!string.IsNullOrEmpty(fromWorldName))
                return _cachedServerName = fromWorldName;

            return UnknownNameFallback;
        }

        private static string TryReadZNetServerName()
        {
            try { return ZNet.m_ServerName; }
            catch { return null; }
        }

        private static string TryReadNameLaunchArg()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i].Equals(NameLaunchArg, StringComparison.OrdinalIgnoreCase))
                        return args[i + 1];
                }
            }
            catch { /* fall through to next source */ }
            return null;
        }

        private static string TryReadWorldName()
        {
            try { return ZNet.instance?.GetWorldName(); }
            catch { return null; }
        }
    }
}
