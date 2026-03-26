using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevToy;

static class ClaudeStatusLine
{
    /// <summary>
    /// Returns true if the statusLine entry exists in Claude's settings.json.
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            if (!File.Exists(AppPaths.ClaudeSettingsFile)) return false;

            string json = File.ReadAllText(AppPaths.ClaudeSettingsFile);
            var root = JsonNode.Parse(json);
            return root?["statusLine"] is JsonObject;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to check status line: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Enables the status line: copies the script to the scripts dir and adds the entry to Claude settings.
    /// </summary>
    public static void Enable()
    {
        try
        {
            // Step 1: Extract embedded script to disk
            Directory.CreateDirectory(AppPaths.ScriptsDir);
            ExtractScript();

            // Step 2: Add statusLine entry to Claude settings.json
            string settingsPath = AppPaths.ClaudeSettingsFile;
            JsonNode root;
            if (File.Exists(settingsPath))
            {
                string existing = File.ReadAllText(settingsPath);
                root = JsonNode.Parse(existing) ?? new JsonObject();
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                root = new JsonObject();
            }

            string scriptPath = AppPaths.ClaudeStatusLineScript.Replace("\\", "\\\\");
            var statusLine = new JsonObject
            {
                ["type"] = "command",
                ["command"] = $"powershell -NoProfile -ExecutionPolicy Bypass -Command \"& '{AppPaths.ClaudeStatusLineScript}'\""
            };

            root["statusLine"] = statusLine;

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(settingsPath, root.ToJsonString(options), Encoding.UTF8);

            // Write config file for script item visibility
            WriteConfig();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to enable status line: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Disables the status line: removes the entry from Claude settings and deletes the script.
    /// </summary>
    public static void Disable()
    {
        try
        {
            // Step 1: Remove statusLine entry from Claude settings.json
            string settingsPath = AppPaths.ClaudeSettingsFile;
            if (File.Exists(settingsPath))
            {
                string json = File.ReadAllText(settingsPath);
                var root = JsonNode.Parse(json);
                if (root is JsonObject obj && obj.ContainsKey("statusLine"))
                {
                    obj.Remove("statusLine");
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    File.WriteAllText(settingsPath, root.ToJsonString(options), Encoding.UTF8);
                }
            }

            // Step 2: Delete the script file
            if (File.Exists(AppPaths.ClaudeStatusLineScript))
                File.Delete(AppPaths.ClaudeStatusLineScript);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to disable status line: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Writes status-line-config.json next to the script with item visibility flags.
    /// </summary>
    public static void WriteConfig()
    {
        try
        {
            var settings = AppSettings.Load();
            var config = new JsonObject
            {
                ["model"] = settings.SlShowModel,
                ["dir"] = settings.SlShowDir,
                ["branch"] = settings.SlShowBranch,
                ["prompts"] = settings.SlShowPrompts,
                ["context"] = settings.SlShowContext,
                ["duration"] = settings.SlShowDuration,
                ["mode"] = settings.SlShowMode,
                ["version"] = settings.SlShowVersion,
                ["editStats"] = settings.SlShowEditStats,
            };

            Directory.CreateDirectory(AppPaths.ScriptsDir);
            string configPath = Path.Combine(AppPaths.ScriptsDir, "status-line-config.json");
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(configPath, config.ToJsonString(options), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write status line config: {ex.Message}");
        }
    }

    private static void ExtractScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("DevToy.Scripts.context-bar.ps1");
        if (stream == null)
            throw new InvalidOperationException("Embedded script resource not found.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        string content = reader.ReadToEnd();
        File.WriteAllText(AppPaths.ClaudeStatusLineScript, content, Encoding.UTF8);
    }
}
