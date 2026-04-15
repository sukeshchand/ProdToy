using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProdToy.Setup;

static class Uninstaller
{
    public record UninstallResult(bool Success, string Message);

    /// <summary>
    /// Removes hook script, settings entries, registry entries. Writes a cleanup
    /// batch script that the caller must launch before exiting — the batch waits
    /// for this process to terminate, then deletes the exe files.
    /// Plugin DATA under data/plugins/ is preserved.
    /// </summary>
    public static UninstallResult Run(out string? cleanupBatPath)
    {
        cleanupBatPath = null;
        var log = new StringBuilder();
        string toolsDir = AppPaths.Root;
        string hooksDir = AppPaths.ClaudeHooksDir;
        string settingsPath = AppPaths.ClaudeSettingsFile;
        string runningExe = Application.ExecutablePath;
        string toolsExe = AppPaths.ExePath;
        string toolsSetupExe = AppPaths.SetupExePath;

        try
        {
            // Step 1: Remove hook script
            string ps1Path = Path.Combine(hooksDir, "Show-ProdToy.ps1");
            if (File.Exists(ps1Path))
            {
                try { File.Delete(ps1Path); log.AppendLine($"Removed hook script: {ps1Path}"); }
                catch (Exception ex) { log.AppendLine($"Warning: could not remove hook script: {ex.Message}"); }
            }
            else
            {
                log.AppendLine("Hook script not found (already removed).");
            }

            // Step 2: Remove ProdToy hook entries from Claude settings.json
            if (File.Exists(settingsPath))
            {
                RemoveHooksFromSettings(settingsPath);
                log.AppendLine($"Removed hook entries from: {settingsPath}");
            }
            else
            {
                log.AppendLine("Claude settings file not found (no hooks to remove).");
            }

            // Step 3: Unregister from Windows Apps & Features
            try
            {
                AppRegistry.Unregister();
                log.AppendLine("Removed from Windows Apps & Features.");
            }
            catch (Exception ex)
            {
                log.AppendLine($"Warning: could not remove Apps & Features entry: {ex.Message}");
            }

            // Step 3b: Remove "Start with Windows" registry entry
            try
            {
                using var runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
                runKey?.DeleteValue("ProdToy", throwOnMissingValue: false);
                log.AppendLine("Removed startup registry entry.");
            }
            catch (Exception ex)
            {
                log.AppendLine($"Warning: could not remove startup entry: {ex.Message}");
            }

            // Step 3c: Remove desktop and Start Menu shortcuts (if present).
            TryDeleteShortcut(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "ProdToy.lnk"),
                "desktop shortcut",
                log);
            TryDeleteShortcut(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "Windows", "Start Menu", "Programs", "ProdToy.lnk"),
                "Start Menu shortcut",
                log);

            // Step 4: Build cleanup batch that deletes exe files after this process exits.
            var batLines = new StringBuilder();
            batLines.AppendLine("@echo off");
            batLines.AppendLine($"taskkill /f /pid {Environment.ProcessId} >nul 2>&1");
            batLines.AppendLine("timeout /t 2 /nobreak >nul");

            // Collect unique paths to delete: both the running installer and the
            // installed copies of the host and installer in .prod-toy.
            var exePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(runningExe))    exePaths.Add(runningExe);
            if (File.Exists(toolsExe))      exePaths.Add(toolsExe);
            if (File.Exists(toolsSetupExe)) exePaths.Add(toolsSetupExe);

            var dirsToClean = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var exe in exePaths)
            {
                batLines.AppendLine($"del /f /q \"{exe}\" >nul 2>&1");
                log.AppendLine($"Scheduled removal of: {exe}");
                dirsToClean.Add(Path.GetDirectoryName(exe)!);
            }

            // Remove the entire plugins/ directory (DLLs only — plugin data
            // lives under data/plugins/ and survives uninstall).
            string pluginsDir = Path.Combine(AppPaths.Root, "plugins");
            batLines.AppendLine($"rmdir /s /q \"{pluginsDir}\" >nul 2>&1");
            log.AppendLine($"Scheduled removal of: {pluginsDir}");

            // Try to remove each exe's containing dir if empty (rmdir without /s).
            foreach (var dir in dirsToClean)
            {
                batLines.AppendLine($"rmdir /q \"{dir}\" >nul 2>&1");
            }

            batLines.AppendLine("del /f /q \"%~f0\" >nul 2>&1");

            cleanupBatPath = Path.Combine(Path.GetTempPath(), "prodtoy_uninstall.cmd");
            File.WriteAllText(cleanupBatPath, batLines.ToString(), Encoding.ASCII);

            log.AppendLine();
            log.Append("ProdToy has been uninstalled. Your settings, history, screenshots, and plugin data are preserved.");

            return new UninstallResult(true, log.ToString());
        }
        catch (Exception ex)
        {
            log.AppendLine($"Error: {ex.Message}");
            return new UninstallResult(false, log.ToString());
        }
    }

    public static void LaunchCleanupScript(string batPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{batPath}\"",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }

    private static void TryDeleteShortcut(string path, string label, StringBuilder log)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                log.AppendLine($"Removed {label}: {path}");
            }
        }
        catch (Exception ex)
        {
            log.AppendLine($"Warning: could not remove {label}: {ex.Message}");
        }
    }

    private static void RemoveHooksFromSettings(string settingsPath)
    {
        string json = File.ReadAllText(settingsPath);
        var root = JsonNode.Parse(json);
        if (root?["hooks"] is not JsonObject hooksNode) return;

        var eventNames = hooksNode.Select(kv => kv.Key).ToList();
        foreach (var eventName in eventNames)
        {
            if (hooksNode[eventName] is not JsonArray eventArray) continue;

            for (int i = eventArray.Count - 1; i >= 0; i--)
            {
                if (eventArray[i]?["hooks"] is not JsonArray hooksArray) continue;

                for (int j = hooksArray.Count - 1; j >= 0; j--)
                {
                    string? command = hooksArray[j]?["command"]?.GetValue<string>();
                    if (command != null && command.Contains("Show-ProdToy"))
                    {
                        hooksArray.RemoveAt(j);
                    }
                }

                if (hooksArray.Count == 0)
                {
                    eventArray.RemoveAt(i);
                }
            }

            if (eventArray.Count == 0)
            {
                hooksNode.Remove(eventName);
            }
        }

        if (hooksNode.Count == 0)
        {
            root.AsObject().Remove("hooks");
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(settingsPath, root.ToJsonString(options), Encoding.UTF8);
    }
}
