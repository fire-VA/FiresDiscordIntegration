using FiresCore.Logging;

namespace FiresDiscordIntegration
{
    // FiresDiscordIntegration's tagged logger. Thin wrapper over the shared FiresCore FiresLog
    // engine (referenced from the FiresUnifiedCore DLL).
    public static class FiresLogger
    {
        private static readonly FiresLog Log = new FiresLog(
            "FiresDiscordIntegration",
            () => ConfigManager.Instance?.configVerboseLogging?.Value ?? false);

        public static bool VerboseEnabled => Log.VerboseEnabled;

        public static void LogInfo(string message) => Log.Info(message);

        public static void LogVerbose(string message) => Log.Verbose(message);

        // Per-action detail, kept out of Info so the log stays quiet: prints only at Core's [Logging] Log Level = Debug.
        public static void LogDebug(string message) => Log.Debug(message);

        public static void LogWarning(string message) => Log.Warning(message);

        public static void LogError(string message) => Log.Error(message);
    }
}
