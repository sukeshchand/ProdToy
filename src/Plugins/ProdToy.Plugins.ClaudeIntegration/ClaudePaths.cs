using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>
/// Paths used by the Claude integration plugin.
///
/// All plugin-owned script files live under the plugin's own data directory.
/// The plugin never writes into ~/.claude/hooks/ — it writes the scripts
/// inside data/plugins/ProdToy.Plugin.ClaudeIntegration/scripts/ and registers
/// the absolute path from there into each discovered Claude settings.json.
///
/// Status-line scripts are qualified with a sanitized machine id so that
/// multiple machines pointing at the same synced data folder each maintain
/// their own versioned script file without colliding.
/// </summary>
static class ClaudePaths
{
    private static readonly string _userProfile =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // Plugin-owned (under {pluginDataDir}/scripts/)
    public static string ScriptsDir { get; private set; } = "";
    public static string StatusLineConfigFile { get; private set; } = "";
    public static string ShowProdToyScript { get; private set; } = "";

    // Default Claude CLI locations. Step 4 adds multi-install discovery that
    // supersedes these — they remain as the fallback/primary install.
    public static string ClaudeHooksDir { get; } = Path.Combine(_userProfile, ".claude", "hooks");
    public static string ClaudeSettingsFile { get; } = Path.Combine(_userProfile, ".claude", "settings.json");
    public static string ClaudeMdFile { get; } = Path.Combine(_userProfile, ".claude", "CLAUDE.md");

    /// <summary>
    /// Sanitized, stable identifier for the current machine. Used as a
    /// fallback when no envId is available.
    /// </summary>
    public static string MachineId { get; } = SanitizeMachineId(Environment.MachineName);

    private static string _envId = ReadEnvId();

    /// <summary>
    /// Unique environment identifier read from ~/.prod-toy/launchSettings.json.
    /// Written by the installer; stable across reinstalls on the same machine.
    /// Falls back to <see cref="MachineId"/> when not yet set.
    /// </summary>
    public static string EnvId => _envId;

    /// <summary>Override the cached env id (used by Doctor fix to apply migration in-session).</summary>
    internal static void SetEnvId(string id)
    {
        _envId = id;
        ClaudeStatusLine.ResetScriptNameRegex();
    }

    /// <summary>
    /// Initialize with the plugin's data directory (from <c>IPluginContext.DataDirectory</c>).
    /// </summary>
    public static void Initialize(string pluginDataDirectory)
    {
        ScriptsDir = Path.Combine(pluginDataDirectory, "scripts");
        StatusLineConfigFile = Path.Combine(ScriptsDir, "status-line-config.json");
        ShowProdToyScript = Path.Combine(ScriptsDir, "Show-ProdToy.ps1");
    }

    /// <summary>Build the versioned, env-qualified status-line script path.</summary>
    public static string StatusLineScriptPath(int version) =>
        Path.Combine(ScriptsDir, $"context-bar--{EnvId}-v{version}.ps1");

    private static string ReadEnvId()
    {
        try
        {
            string launchSettingsPath = Path.Combine(_userProfile, ".prod-toy", "launchSettings.json");
            if (!File.Exists(launchSettingsPath)) return MachineId;
            var root = JsonNode.Parse(File.ReadAllText(launchSettingsPath));
            var id = root?["envId"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id)) return id;
        }
        catch { }
        return MachineId;
    }

    private static string SanitizeMachineId(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "machine";
        var cleaned = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(cleaned) ? "machine" : cleaned;
    }
}
