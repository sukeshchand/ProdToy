using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>
/// Install/uninstall the status-line integration across one or more Claude
/// installations. Install writes the <c>statusLine</c> entry in each
/// <c>settings.json</c> pointing at the plugin-owned <c>context-bar.ps1</c>.
///
/// The script itself reads <c>ClaudePluginSettings.SlEnabled</c> at runtime
/// and renders empty if the user has toggled it off. That means enabling or
/// disabling the status line at runtime does NOT require touching Claude's
/// settings.json — only toggling the plugin setting.
/// </summary>
static class ClaudeStatusLine
{
    // Matches context-bar.ps1 and context-bar-v{n}.ps1.
    private static readonly Regex ScriptNameRegex = new(
        @"^context-bar(?:-v(\d+))?\.ps1$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Extract context-bar.ps1 to the plugin's scripts dir and write the
    /// <c>statusLine</c> entry into every install's <c>settings.json</c>.
    /// Also writes the initial status-line-config.json from the current settings.
    /// </summary>
    public static void Install(
        IEnumerable<ClaudeInstall> installs,
        ClaudePluginSettings settings,
        string pluginSettingsPath)
    {
        try
        {
            Directory.CreateDirectory(ClaudePaths.ScriptsDir);
            ExtractScript(pluginSettingsPath, ClaudePaths.ClaudeStatusLineScript);
            WriteConfig(settings);

            string command = BuildCommand(ClaudePaths.ClaudeStatusLineScript);
            foreach (var install in installs)
                WriteStatusLineEntry(install.SettingsFile, command);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to install status line: {ex.Message}");
        }
    }

    /// <summary>
    /// Force Claude CLI to invalidate its cached status-line script by:
    ///   1. Writing a new <c>context-bar-v{n+1}.ps1</c> (fresh extraction from
    ///      the embedded template with current substitutions).
    ///   2. Updating every install's <c>settings.json</c> <c>statusLine.command</c>
    ///      to point at the new filename.
    ///   3. Deleting older <c>context-bar*.ps1</c> files we own, best-effort.
    /// Claude sees a changed <c>command</c> string and re-runs the script on
    /// the next render tick — that's how toggles take effect immediately.
    /// </summary>
    public static void BumpScriptVersion(
        IEnumerable<ClaudeInstall> installs,
        string pluginSettingsPath)
    {
        try
        {
            Directory.CreateDirectory(ClaudePaths.ScriptsDir);

            int nextVersion = FindHighestVersion() + 1;
            string newFileName = $"context-bar-v{nextVersion}.ps1";
            string newPath = Path.Combine(ClaudePaths.ScriptsDir, newFileName);

            ExtractScript(pluginSettingsPath, newPath);

            string command = BuildCommand(newPath);
            var installsList = installs.ToList();
            foreach (var install in installsList)
                WriteStatusLineEntry(install.SettingsFile, command);

            DeleteOldScripts(except: newPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to bump status line script: {ex.Message}");
        }
    }

    /// <summary>
    /// Remove the <c>statusLine</c> entry from every install's <c>settings.json</c>.
    /// Leaves the PS1 script and config file in place under the plugin data dir.
    /// </summary>
    public static void Uninstall(IEnumerable<ClaudeInstall> installs)
    {
        foreach (var install in installs)
        {
            try
            {
                if (!File.Exists(install.SettingsFile)) continue;
                string json = File.ReadAllText(install.SettingsFile);
                var root = JsonNode.Parse(json);
                if (root is JsonObject obj && obj.ContainsKey("statusLine"))
                {
                    // Only remove if the entry is ours (points at a context-bar*.ps1).
                    string? cmd = obj["statusLine"]?["command"]?.GetValue<string>();
                    if (cmd != null && IsOurCommand(cmd))
                    {
                        obj.Remove("statusLine");
                        var options = new JsonSerializerOptions { WriteIndented = true };
                        File.WriteAllText(install.SettingsFile, root!.ToJsonString(options), Encoding.UTF8);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to uninstall status line from {install.SettingsFile}: {ex.Message}");
            }
        }
    }

    private static string BuildCommand(string scriptPath) =>
        $"powershell -NoProfile -ExecutionPolicy Bypass -Command \"& '{scriptPath}'\"";

    private static bool IsOurCommand(string command)
    {
        // Match either "context-bar.ps1" or "context-bar-v{n}.ps1" inside the
        // command string. Prefix check is enough — we don't need a full regex.
        return command.Contains("context-bar", StringComparison.OrdinalIgnoreCase)
            && command.Contains(".ps1", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindHighestVersion()
    {
        int highest = 0;
        if (!Directory.Exists(ClaudePaths.ScriptsDir)) return highest;

        foreach (var file in Directory.EnumerateFiles(ClaudePaths.ScriptsDir, "context-bar*.ps1"))
        {
            var m = ScriptNameRegex.Match(Path.GetFileName(file));
            if (!m.Success) continue;
            if (m.Groups[1].Success && int.TryParse(m.Groups[1].Value, out int v))
            {
                if (v > highest) highest = v;
            }
        }
        return highest;
    }

    private static void DeleteOldScripts(string except)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(ClaudePaths.ScriptsDir, "context-bar*.ps1"))
            {
                if (string.Equals(file, except, StringComparison.OrdinalIgnoreCase)) continue;
                if (!ScriptNameRegex.IsMatch(Path.GetFileName(file))) continue;
                try { File.Delete(file); }
                catch (Exception ex) { Debug.WriteLine($"Could not delete old script {file}: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DeleteOldScripts failed: {ex.Message}");
        }
    }

    private static void WriteStatusLineEntry(string settingsPath, string command)
    {
        try
        {
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
                ["command"] = command,
            };
            root["statusLine"] = statusLine;

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(settingsPath, root.ToJsonString(options), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write statusLine to {settingsPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Write the runtime config file that context-bar.ps1 reads on every render.
    /// Called by <see cref="Install"/> and whenever the user toggles a status-line
    /// item in the settings panel.
    /// </summary>
    public static void WriteConfig(ClaudePluginSettings settings)
    {
        try
        {
            Directory.CreateDirectory(ClaudePaths.ScriptsDir);

            var config = new JsonObject
            {
                ["style"] = settings.SlStyle,
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

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(ClaudePaths.StatusLineConfigFile, config.ToJsonString(options), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write status line config: {ex.Message}");
        }
    }

    private static void ExtractScript(string pluginSettingsPath, string destPath)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("ProdToy.Plugins.ClaudeIntegration.Scripts.context-bar.ps1");
        if (stream == null)
            throw new InvalidOperationException("Embedded context-bar.ps1 resource not found.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        string template = reader.ReadToEnd();

        string content = template
            .Replace("{{SETTINGS_PATH}}", pluginSettingsPath)
            .Replace("{{PIPE_NAME}}", "ProdToy_Pipe");

        File.WriteAllText(destPath, content, Encoding.UTF8);
    }
}
