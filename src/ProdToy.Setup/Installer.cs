using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace ProdToy.Setup;

/// <summary>
/// Performs the actual install/repair/update work. Extracts bundled zips next
/// to the running installer into the user's .prod-toy directory, writes the
/// Claude hook script, merges Claude settings, and registers the app in
/// Windows "Apps &amp; Features".
/// </summary>
static class Installer
{
    public record InstallResult(bool Success, string Message);

    /// <summary>
    /// Locate bundled artifacts. They live next to the running installer exe
    /// (the publish output places ProdToySetup.exe alongside ProdToy.zip and
    /// plugins/*.zip and metadata.json).
    /// </summary>
    public static string BundleDir => Path.GetDirectoryName(Application.ExecutablePath)!;

    public static string HostZipPath => Path.Combine(BundleDir, "ProdToy.zip");

    public static string PluginsBundleDir => Path.Combine(BundleDir, "plugins");

    public static string MetadataPath => Path.Combine(BundleDir, "metadata.json");

    /// <summary>
    /// Returns the version of the bundled host (from metadata.json if present,
    /// otherwise falls back to the installer's own AppVersion.Current).
    /// </summary>
    public static string ReadBundledVersion()
    {
        try
        {
            if (File.Exists(MetadataPath))
            {
                var json = JsonNode.Parse(File.ReadAllText(MetadataPath));
                var v = json?["version"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ReadBundledVersion failed: {ex.Message}");
        }
        return AppVersion.Current;
    }

    public static InstallResult Run()
    {
        var log = new StringBuilder();
        try
        {
            // Step 1: Kill any running ProdToy instances (except this installer).
            int currentPid = Environment.ProcessId;
            foreach (var proc in Process.GetProcessesByName("ProdToy"))
            {
                if (proc.Id == currentPid) continue;
                try
                {
                    proc.Kill();
                    proc.WaitForExit(3000);
                    log.AppendLine($"Closed running ProdToy (PID {proc.Id}).");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Could not kill ProdToy PID {proc.Id}: {ex.Message}");
                }
            }

            // Step 2: Ensure install dir exists.
            Directory.CreateDirectory(AppPaths.Root);

            // Step 3: Extract ProdToy.zip → Root\ProdToy.exe
            if (!File.Exists(HostZipPath))
                return new InstallResult(false, $"ProdToy.zip not found next to installer at {HostZipPath}.");

            ExtractZipFlat(HostZipPath, AppPaths.Root);
            log.AppendLine($"Installed host exe to {AppPaths.ExePath}");

            // Step 4: Extract each plugin zip → Root\plugins\bin\{PluginId}\
            int pluginCount = 0;
            if (Directory.Exists(PluginsBundleDir))
            {
                Directory.CreateDirectory(AppPaths.PluginsBinDir);
                foreach (var zipPath in Directory.GetFiles(PluginsBundleDir, "*.zip"))
                {
                    string pluginId = Path.GetFileNameWithoutExtension(zipPath);
                    string destDir = Path.Combine(AppPaths.PluginsBinDir, pluginId);
                    Directory.CreateDirectory(destDir);
                    ExtractZipFlat(zipPath, destDir);
                    pluginCount++;
                }
                log.AppendLine($"Installed {pluginCount} plugin package(s).");
            }
            else
            {
                log.AppendLine("No bundled plugins directory found (skipping plugin install).");
            }

            // Step 5: Copy the installer exe itself to install dir so Windows
            //         Add/Remove Programs can find it for uninstall.
            try
            {
                string runningSetup = Application.ExecutablePath;
                if (!string.Equals(runningSetup, AppPaths.SetupExePath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(runningSetup, AppPaths.SetupExePath, overwrite: true);
                    log.AppendLine($"Copied installer to {AppPaths.SetupExePath}");
                }
            }
            catch (Exception ex)
            {
                log.AppendLine($"Warning: could not copy installer: {ex.Message}");
            }

            // Step 6: Write the hook script from embedded resource.
            Directory.CreateDirectory(AppPaths.ClaudeHooksDir);
            string ps1Path = Path.Combine(AppPaths.ClaudeHooksDir, "Show-ProdToy.ps1");
            string ps1Content = LoadEmbeddedHookScript(AppPaths.ExePath);
            File.WriteAllText(ps1Path, ps1Content, Encoding.UTF8);
            log.AppendLine($"Hook script written to {ps1Path}");

            // Step 7: Back up and merge Claude settings.json.
            if (File.Exists(AppPaths.ClaudeSettingsFile))
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string backupPath = Path.Combine(
                    Path.GetDirectoryName(AppPaths.ClaudeSettingsFile)!,
                    $"settings.backup_{timestamp}.json");
                try { File.Copy(AppPaths.ClaudeSettingsFile, backupPath, overwrite: false); }
                catch { /* backup is best-effort */ }
            }
            MergeHooksIntoSettings();
            log.AppendLine($"Configured Claude hooks in {AppPaths.ClaudeSettingsFile}");

            // Step 8: Register in Windows Apps & Features with the bundled version.
            try
            {
                string bundledVersion = ReadBundledVersion();
                AppRegistry.Register(bundledVersion);
                log.AppendLine($"Registered v{bundledVersion} in Apps & Features.");
            }
            catch (Exception ex)
            {
                log.AppendLine($"Warning: could not register in Apps & Features: {ex.Message}");
            }

            return new InstallResult(true, log.ToString());
        }
        catch (Exception ex)
        {
            log.AppendLine($"Error: {ex.Message}");
            return new InstallResult(false, log.ToString());
        }
    }

    /// <summary>
    /// Extract a zip into destDir. Assumes flat zip layout (entries at the root).
    /// Overwrites existing files.
    /// </summary>
    private static void ExtractZipFlat(string zipPath, string destDir)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // skip directories
            string destPath = Path.Combine(destDir, entry.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }

    private static string LoadEmbeddedHookScript(string installExePath)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("ProdToy.Setup.Scripts.Show-ProdToy.ps1")
            ?? throw new InvalidOperationException("Embedded hook script resource not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string template = reader.ReadToEnd();
        return template.Replace("{{EXE_PATH}}", installExePath);
    }

    private static void MergeHooksIntoSettings()
    {
        string settingsPath = AppPaths.ClaudeSettingsFile;
        JsonNode root;
        if (File.Exists(settingsPath))
        {
            string existing = File.ReadAllText(settingsPath);
            root = JsonNode.Parse(existing) ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        var hooksNode = root["hooks"]?.AsObject() ?? new JsonObject();

        string hookCommand =
            $"powershell.exe -ExecutionPolicy Bypass -File \"{AppPaths.ClaudeHooksDir}\\Show-ProdToy.ps1\"";

        var popupHookEntry = new JsonObject
        {
            ["type"] = "command",
            ["command"] = hookCommand,
        };

        MergeHookEvent(hooksNode, "UserPromptSubmit", null, popupHookEntry);
        MergeHookEvent(hooksNode, "Stop", null, popupHookEntry);
        MergeHookEvent(hooksNode, "Notification",
            "permission_prompt|idle_prompt|elicitation_dialog", popupHookEntry);

        root["hooks"] = hooksNode;

        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(settingsPath, root.ToJsonString(options), Encoding.UTF8);
    }

    private static void MergeHookEvent(JsonObject hooksNode, string eventName, string? matcher, JsonObject newHookEntry)
    {
        if (hooksNode[eventName] is JsonArray existingArray)
        {
            // Skip if any existing entry already references Show-ProdToy.
            foreach (var ruleSet in existingArray)
            {
                if (ruleSet?["hooks"] is JsonArray hooksArray)
                {
                    foreach (var hook in hooksArray)
                    {
                        if (hook?["command"]?.GetValue<string>()?.Contains("Show-ProdToy") == true)
                            return;
                    }
                }
            }

            // Otherwise, try to add to a rule set with matching matcher.
            foreach (var ruleSet in existingArray)
            {
                if (ruleSet is not JsonObject ruleObj) continue;
                string? existingMatcher = ruleObj["matcher"]?.GetValue<string>();
                if (existingMatcher == matcher)
                {
                    var hooksArray = ruleObj["hooks"]?.AsArray() ?? new JsonArray();
                    hooksArray.Add(JsonNode.Parse(newHookEntry.ToJsonString()));
                    ruleObj["hooks"] = hooksArray;
                    return;
                }
            }

            // Fall through: create a new rule set.
            var newRuleSet = new JsonObject();
            if (matcher != null) newRuleSet["matcher"] = matcher;
            newRuleSet["hooks"] = new JsonArray { JsonNode.Parse(newHookEntry.ToJsonString()) };
            existingArray.Add(newRuleSet);
        }
        else
        {
            var newRuleSet = new JsonObject();
            if (matcher != null) newRuleSet["matcher"] = matcher;
            newRuleSet["hooks"] = new JsonArray { JsonNode.Parse(newHookEntry.ToJsonString()) };
            hooksNode[eventName] = new JsonArray { newRuleSet };
        }
    }
}
