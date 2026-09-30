using BepInEx.Configuration;

namespace FiresDiscordIntegration
{
    /// <summary>
    /// Singleton wrapper around BepInEx's <see cref="ConfigFile"/> for cross-module
    /// access. Module-specific configs (Discord, ClientLogRelay) are bound by each
    /// module's own static *Config class - this manager just holds the file handle
    /// and the verbose-logging flag everything else reads from.
    /// </summary>
    public sealed class ConfigManager
    {
        private static ConfigManager _instance;
        public static ConfigManager Instance => _instance ?? (_instance = new ConfigManager());

        public ConfigFile Config { get; private set; }

        /// <summary>
        /// Mod-wide verbose logging toggle. Individual modules may have their own
        /// finer-grained verbose flags as well (see DiscordIntegrationConfig.VerboseLogging).
        /// </summary>
        public ConfigEntry<bool> configVerboseLogging;

        public void Initialize(ConfigFile config)
        {
            Config = config;

            configVerboseLogging = config.Bind(
                "General",
                "VerboseLogging",
                false,
                "Emit additional debug logs for the Discord/log-relay subsystems. " +
                "Off by default \u2014 most operators only want this on for troubleshooting.");

            FiresCore.Config.ConfigHotReload.Watch(config);
        }

    }
}
