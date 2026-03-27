using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProdToy;

static class Uninstaller
{
    public record UninstallResult(bool Success, string Message);

    /// <summary>
    /// Removes hook script and settings entries immediately.
    /// Writes a cleanup batch script that deletes exe files after the process exits.
    /// The caller must launch the batch script and then exit the application.
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

        try
        {
            // Step 1: Remove hook script
            string ps1Path = Path.Combine(hooksDir, "Show-ProdToy.ps1");
            if (File.Exists(ps1Path))
            {
                File.Delete(ps1Path);
                log.AppendLine($"Removed hook script: {ps1Path}");
            }
            else
            {
                log.AppendLine("Hook script not found (already removed).");
            }

            // Step 2: Remove ProdToy hooks from settings.json
            if (File.Exists(settingsPath))
            {
                RemoveHooksFromSettings(settingsPath);
                log.AppendLine($"Removed hook entries from: {settingsPath}");
            }
            else
            {
                log.AppendLine("Settings file not found (no hooks to remove).");
            }

            // Step 3: Build a cleanup batch script that waits for this process to exit,
            // then deletes the exe(s) and their folders (if empty).
            var batLines = new StringBuilder();
            batLines.AppendLine("@echo off");
            // Wait for the running process to exit
            batLines.AppendLine($"taskkill /f /pid {Environment.ProcessId} >nul 2>&1");
            batLines.AppendLine("timeout /t 2 /nobreak >nul");

            // Collect unique paths to delete
            var exePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(runningExe)) exePaths.Add(runningExe);
            if (File.Exists(toolsExe)) exePaths.Add(toolsExe);

            var dirsToClean = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var exe in exePaths)
            {
                batLines.AppendLine($"del /f /q \"{exe}\" >nul 2>&1");
                log.AppendLine($"Scheduled removal of: {exe}");
                dirsToClean.Add(Path.GetDirectoryName(exe)!);
            }

            // Remove directories if empty after exe deletion
            foreach (var dir in dirsToClean)
            {
                batLines.AppendLine($"rmdir /q \"{dir}\" >nul 2>&1");
            }

            // Self-delete the batch script
            batLines.AppendLine("del /f /q \"%~f0\" >nul 2>&1");

            cleanupBatPath = Path.Combine(Path.GetTempPath(), "prodtoy_uninstall.cmd");
            File.WriteAllText(cleanupBatPath, batLines.ToString(), Encoding.ASCII);

            log.AppendLine();
            log.Append("ProdToy has been uninstalled. Please restart any running Claude Code instances.");

            return new UninstallResult(true, log.ToString());
        }
        catch (Exception ex)
        {
            log.AppendLine($"Error: {ex.Message}");
            return new UninstallResult(false, log.ToString());
        }
    }

    /// <summary>
    /// Launches the cleanup batch script (hidden window) so it can delete files after app exit.
    /// </summary>
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

    private static void RemoveHooksFromSettings(string settingsPath)
    {
        string json = File.ReadAllText(settingsPath);
        var root = JsonNode.Parse(json);
        if (root?["hooks"] is not JsonObject hooksNode) return;

        // Process each hook event (Stop, Notification, UserPromptSubmit, etc.)
        var eventNames = hooksNode.Select(kv => kv.Key).ToList();
        foreach (var eventName in eventNames)
        {
            if (hooksNode[eventName] is not JsonArray eventArray) continue;

            // Iterate rule sets in reverse so we can safely remove
            for (int i = eventArray.Count - 1; i >= 0; i--)
            {
                if (eventArray[i]?["hooks"] is not JsonArray hooksArray) continue;

                // Remove any hook entries that reference Show-ProdToy
                for (int j = hooksArray.Count - 1; j >= 0; j--)
                {
                    string? command = hooksArray[j]?["command"]?.GetValue<string>();
                    if (command != null && command.Contains("Show-ProdToy"))
                    {
                        hooksArray.RemoveAt(j);
                    }
                }

                // If the rule set has no hooks left, remove the entire rule set
                if (hooksArray.Count == 0)
                {
                    eventArray.RemoveAt(i);
                }
            }

            // If the event has no rule sets left, remove the event
            if (eventArray.Count == 0)
            {
                hooksNode.Remove(eventName);
            }
        }

        // If hooks object is empty, remove it entirely
        if (hooksNode.Count == 0)
        {
            root.AsObject().Remove("hooks");
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(settingsPath, root.ToJsonString(options), System.Text.Encoding.UTF8);
    }
}
