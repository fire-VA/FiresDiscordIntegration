using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using UnityEngine;

namespace FiresDiscordIntegration.Utilities
{
    /// <summary>
    /// On mod load, drops the bundled <c>valheim_server_wrapper.bat</c> into
    /// <c>BepInEx/config/FiresDiscordIntegration/</c> if it isn't already there, so
    /// server operators can find it next to the mod's other on-disk files and just
    /// copy it to their dedicated-server install root.
    ///
    /// The bat ships as an <c>&lt;EmbeddedResource&gt;</c> in the DLL (manifest stream
    /// name ends with <c>valheim_server_wrapper.bat</c>). The extractor never
    /// overwrites an existing file on disk — once an operator edits SERVER_NAME /
    /// SERVER_PORT / etc. their copy stays untouched on subsequent loads.
    ///
    /// Idempotent and cheap (one File.Exists + a single stream copy on first run).
    /// Safe to call from any context — failures are non-fatal and just log.
    /// </summary>
    internal static class WrapperBatExtractor
    {
        private const string ResourceSuffix = "valheim_server_wrapper.bat";
        private const string TargetDirName  = "FiresDiscordIntegration";
        private const string TargetFileName = "valheim_server_wrapper.bat";

        public static void ExtractIfMissing()
        {
            try
            {
                string targetDir  = Path.Combine(Paths.ConfigPath, TargetDirName);
                string targetPath = Path.Combine(targetDir, TargetFileName);

                if (File.Exists(targetPath))
                {
                    // Don't clobber operator edits. Log at verbose only.
                    if (ConfigManager.Instance?.configVerboseLogging?.Value == true)
                    {
                        Debug.Log($"[{FiresDiscordIntegrationPlugin.PluginName}] Wrapper bat already present at {targetPath} — skipping extract.");
                    }
                    return;
                }

                if (!Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                Assembly asm = Assembly.GetExecutingAssembly();
                string resourceName = asm.GetManifestResourceNames()
                                         .FirstOrDefault(n => n.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase));
                if (resourceName == null)
                {
                    Debug.LogWarning($"[{FiresDiscordIntegrationPlugin.PluginName}] Wrapper bat embedded resource not found. Available resources: " +
                                     string.Join(", ", asm.GetManifestResourceNames()));
                    return;
                }

                using (Stream src = asm.GetManifestResourceStream(resourceName))
                using (FileStream dst = File.Create(targetPath))
                {
                    if (src == null)
                    {
                        Debug.LogWarning($"[{FiresDiscordIntegrationPlugin.PluginName}] Could not open embedded resource '{resourceName}' for read.");
                        return;
                    }
                    src.CopyTo(dst);
                }

                Debug.Log($"[{FiresDiscordIntegrationPlugin.PluginName}] Extracted wrapper bat TEMPLATE → {targetPath}. " +
                          $"This is a TEMPLATE — open it in a text editor and fill in SERVER_NAME, SERVER_PORT, " +
                          $"SERVER_WORLD, SERVER_PASSWORD (min 5 chars), and SERVER_PUBLIC (1/0) to match your " +
                          $"server. Then copy the edited bat to your Valheim dedicated server install root (next " +
                          $"to valheim_server.exe) and run it instead of valheim_server.exe directly to get " +
                          $"auto-restart + Discord control-panel integration. Subsequent mod updates will NEVER " +
                          $"overwrite this file once you've edited it.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{FiresDiscordIntegrationPlugin.PluginName}] WrapperBatExtractor failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
