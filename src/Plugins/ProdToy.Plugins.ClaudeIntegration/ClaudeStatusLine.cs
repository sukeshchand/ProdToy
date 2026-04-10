using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProdToy.Plugins.ClaudeIntegration;

static class ClaudeStatusLine
{
    public static bool IsEnabled()
    {
        try
        {
            if (!File.Exists(ClaudePaths.ClaudeSettingsFile)) return false;
            string json = File.ReadAllText(ClaudePaths.ClaudeSettingsFile);
            var root = JsonNode.Parse(json);
            return root?["statusLine"] is JsonObject;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to check status line: {ex.Message}");
            return false;
        }
    }

    public static void Enable()
    {
        try
        {
            Directory.CreateDirectory(ClaudePaths.ScriptsDir);
            ExtractScript();

            string settingsPath = ClaudePaths.ClaudeSettingsFile;
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

            var statusLine = new JsonObject
            {
                ["type"] = "command",
                ["command"] = $"powershell -NoProfile -ExecutionPolicy Bypass -Command \"& '{ClaudePaths.ClaudeStatusLineScript}'\""
            };
            root["statusLine"] = statusLine;

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(settingsPath, root.ToJsonString(options), Encoding.UTF8);
            WriteConfig(null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to enable status line: {ex.Message}");
            throw;
        }
    }

    public static void Disable()
    {
        try
        {
            string settingsPath = ClaudePaths.ClaudeSettingsFile;
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

            if (File.Exists(ClaudePaths.ClaudeStatusLineScript))
                File.Delete(ClaudePaths.ClaudeStatusLineScript);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to disable status line: {ex.Message}");
            throw;
        }
    }

    public static void WriteConfig(ClaudePluginSettings? settings)
    {
        try
        {
            Directory.CreateDirectory(ClaudePaths.ScriptsDir);
            ExtractScript();

            settings ??= new ClaudePluginSettings();
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

            string configPath = Path.Combine(ClaudePaths.ScriptsDir, "status-line-config.json");
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
        using var stream = assembly.GetManifestResourceStream("ProdToy.Plugins.ClaudeIntegration.Scripts.context-bar.ps1");
        if (stream == null)
            throw new InvalidOperationException("Embedded context-bar.ps1 resource not found.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        string content = reader.ReadToEnd();
        File.WriteAllText(ClaudePaths.ClaudeStatusLineScript, content, Encoding.UTF8);
    }
}
