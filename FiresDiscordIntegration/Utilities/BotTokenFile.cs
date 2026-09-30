using System;
using System.IO;
using UnityEngine;

namespace FiresDiscordIntegration.Utilities
{
    /// <summary>
    /// Reads the Discord bot token from a file that lives at the Valheim install
    /// root — explicitly OUTSIDE <c>BepInEx/config/</c> so it cannot get picked
    /// up by Thunderstore Mod Manager / r2modman profile exports that bundle
    /// every <c>*.cfg</c> under <c>BepInEx/config/</c>.  A bot token is a much
    /// stronger credential than a webhook URL (it can read every message in
    /// every channel the bot can see, and read guild member info); accidentally
    /// shipping it inside a shared modpack would be a real leak.
    ///
    /// The file may live at EITHER the game/server install root
    /// (<c>{GameRoot}/FiresDiscordIntegration_BotToken.cfg</c>) OR the BepInEx root
    /// (<c>{BepInExRoot}/FiresDiscordIntegration_BotToken.cfg</c>) — both are outside
    /// <c>BepInEx/config/</c>, so neither gets swept into a profile export.  Game root is the
    /// primary (where the empty template is created if neither exists); BepInEx root is accepted
    /// as an alternative.  It's a tiny key/value text file the wrapper bat's inline PowerShell can
    /// read with the same regex as the cfg parse, so the bat needs no separate parser.
    /// </summary>
    public static class BotTokenFile
    {
        private const string TokenFileName = "FiresDiscordIntegration_BotToken.cfg";
        private const string TokenKey      = "BotToken";

        /// <summary>Environment variable checked before any file - the safest source on a rented host.</summary>
        public const string TokenEnvironmentVariable = "FIRES_DISCORD_BOT_TOKEN";

        private const string ServerProcessMarker = "server";

        // valheim_server.exe (or a renamed server exe such as server2.exe), the same test FGN uses first.
        private static bool IsDedicatedServerProcess =>
            BepInEx.Paths.ProcessName.IndexOf(ServerProcessMarker, StringComparison.OrdinalIgnoreCase) >= 0;

        private const string EmptyTemplateContents =
            "# FiresDiscordIntegration bot token\n" +
            "#\n" +
            "# This file deliberately lives OUTSIDE BepInEx/config/ so it can't get\n" +
            "# included in a modpack / profile export by Thunderstore Mod Manager or\n" +
            "# r2modman.  DO NOT share this file.  DO NOT commit it to a public repo.\n" +
            "#\n" +
            "# Get your token from https://discord.com/developers/applications:\n" +
            "#   1. Open your bot application\n" +
            "#   2. Sidebar -> Bot -> Reset Token\n" +
            "#   3. Copy the long string\n" +
            "#   4. Paste it after the '=' below, save the file, restart your server\n" +
            "#\n" +
            "# Make sure the bot's Server Members intent is enabled (same page as the\n" +
            "# token reset button) — that's what lets the mod read each Discord user's\n" +
            "# role color so chat messages get their author colour in-game.\n" +
            "\n" +
            TokenKey + " = \n";

        private static string _cachedToken;
        private static bool   _loaded;

        /// <summary>
        /// The bot token read from disk.  Returns empty string if the file is
        /// missing or has an unset/blank token.  Cached after the first read.
        /// </summary>
        public static string Token
        {
            get
            {
                if (!_loaded) LoadFromDisk();
                return _cachedToken ?? string.Empty;
            }
        }

        /// <summary>The primary path (game/server install root) — where the empty template is created.</summary>
        public static string FilePath => Path.Combine(BepInEx.Paths.GameRootPath, TokenFileName);

        // Accepted locations, in priority order: an operator-chosen path (Discord.BotListener/BotTokenPath, for
        // rented hosts that only expose certain folders), then the game root (primary, where the template is
        // created), then the BepInEx root. The configured path is REJECTED when it resolves inside
        // BepInEx/config or BepInEx/plugins, because those are exactly what r2modman / Thunderstore sweep into
        // profile and modpack exports - the whole reason this file is not a config entry - and when it resolves
        // outside the Valheim install and the BepInEx folder, because a Thunderstore mod keeps its files there.
        private static string[] CandidatePaths
        {
            get
            {
                string configured = ConfiguredPath();
                var paths = new System.Collections.Generic.List<string>(3);
                if (!string.IsNullOrEmpty(configured)) paths.Add(configured);
                paths.Add(Path.Combine(BepInEx.Paths.GameRootPath, TokenFileName));
                paths.Add(Path.Combine(BepInEx.Paths.BepInExRootPath, TokenFileName));
                return paths.ToArray();
            }
        }

        /// <summary>The operator's own token path, or empty when unset or pointing somewhere that gets exported.</summary>
        private static string ConfiguredPath()
        {
            string raw = null;
            try { raw = Discord.DiscordIntegrationConfig.BotTokenPath?.Value; } catch { }
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            string full;
            try { full = Path.GetFullPath(raw.Trim()); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresDiscordIntegration] BotTokenPath '{raw}' is not a usable path ({ex.Message}); using the default locations.");
                return string.Empty;
            }
            if (IsExportedLocation(full))
            {
                Debug.LogError(
                    $"[FiresDiscordIntegration] BotTokenPath '{full}' is inside BepInEx/config or BepInEx/plugins. " +
                    "Those folders are packaged into r2modman / Thunderstore profile and modpack exports, so a token " +
                    "there would be shared with anyone who receives the pack. IGNORING it and using the default " +
                    "locations. Put the file outside the profile, or set the " + TokenEnvironmentVariable + " environment variable.");
                return string.Empty;
            }
            if (!IsInsideGameFolders(full))
            {
                Debug.LogWarning(
                    $"[FiresDiscordIntegration] BotTokenPath '{full}' is outside the Valheim install and the BepInEx folder. " +
                    "IGNORING it and using the default locations. Put the file in the Valheim install (not in BepInEx/config " +
                    "or BepInEx/plugins), or set the " + TokenEnvironmentVariable + " environment variable.");
                return string.Empty;
            }
            return full;
        }

        private static bool IsExportedLocation(string fullPath)
        {
            return IsUnder(fullPath, BepInEx.Paths.ConfigPath) || IsUnder(fullPath, BepInEx.Paths.PluginPath);
        }

        /// <summary>
        /// True when a full path is inside the Valheim install or the BepInEx folder (a mod manager's profile keeps
        /// BepInEx outside the install). A Thunderstore mod keeps the files it reads and writes there, so every
        /// operator-chosen path in this mod is checked against it.
        /// </summary>
        internal static bool IsInsideGameFolders(string fullPath)
        {
            return IsUnder(fullPath, BepInEx.Paths.GameRootPath) || IsUnder(fullPath, BepInEx.Paths.BepInExRootPath);
        }

        private static bool IsUnder(string fullPath, string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            string root;
            try { root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; }
            catch { return false; }
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Force a re-read from disk on next <see cref="Token"/> access.  Useful
        /// for tests or if the operator edits the file while the server is
        /// running and wants the bot listener to pick up the new value without
        /// a full restart.
        /// </summary>
        public static void InvalidateCache()
        {
            _loaded = false;
            _cachedToken = null;
        }

        private static void LoadFromDisk()
        {
            _loaded = true;

            // The bot token belongs to the dedicated server only.  Reading / creating the
            // file on a player's client install would just spam an unactionable log line
            // and drop an empty template into their Valheim folder, and a client that runs
            // -batchmode -nographics (a headless test bot) must never pick up a server's
            // token, so the test is the server EXECUTABLE (valheim_server.exe), not batch
            // mode.  Listen servers (a host playing on valheim.exe) don't need it either —
            // they aren't running the server-manager panel or login-snapshot pipeline.
            if (!IsDedicatedServerProcess) return;

            // An environment variable never lands in a profile export and is what rented hosts expose, so it wins.
            try
            {
                string fromEnvironment = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
                if (!string.IsNullOrWhiteSpace(fromEnvironment))
                {
                    _cachedToken = fromEnvironment.Trim();
                    Debug.Log($"[FiresDiscordIntegration] Bot token loaded from the {TokenEnvironmentVariable} environment variable.");
                    return;
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[FiresDiscordIntegration] Could not read {TokenEnvironmentVariable}: {ex.Message}"); }

            try
            {
                // Read from whichever accepted location has the file (configured path, game root, BepInEx root).
                foreach (var path in CandidatePaths)
                {
                    if (!File.Exists(path)) continue;

                    foreach (var line in File.ReadAllLines(path))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        string trimmed = line.TrimStart();
                        if (trimmed.StartsWith("#")) continue;

                        int eq = trimmed.IndexOf('=');
                        if (eq <= 0) continue;

                        string key   = trimmed.Substring(0, eq).Trim();
                        string value = trimmed.Substring(eq + 1).Trim();
                        if (!string.Equals(key, TokenKey, StringComparison.OrdinalIgnoreCase)) continue;

                        _cachedToken = value;
                        if (!string.IsNullOrEmpty(value))
                            Debug.Log($"[FiresDiscordIntegration] Bot token loaded from {path}");
                        return;
                    }
                }

                // Neither location had the file — create an empty template at the primary (game root).
                string primary = CandidatePaths[0];
                try { File.WriteAllText(primary, EmptyTemplateContents); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FiresDiscordIntegration] Failed to create bot token template at '{primary}': {ex.Message}");
                    return;
                }
                Debug.Log(
                    $"[FiresDiscordIntegration] Bot token file not found — created empty template at: {primary}\n" +
                    $"(It may also be placed at the BepInEx root: {CandidatePaths[1]}.)\n" +
                    $"Open it, paste your Discord bot token after 'BotToken = ', save, and restart the server. " +
                    $"Until then the bot listener / status panel / reaction features stay disabled (webhook events still work).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresDiscordIntegration] Failed to read bot token file: {ex.Message}");
            }
        }

    }
}
