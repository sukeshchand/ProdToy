namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>
/// Paths used by the Claude integration plugin.
///
/// All plugin-owned script files live under the plugin's own data directory.
/// The plugin never writes into ~/.claude/hooks/ — it writes the scripts
/// inside data/plugins/ProdToy.Plugin.ClaudeIntegration/scripts/ and registers
/// the absolute path from there into each discovered Claude settings.json.
/// </summary>
static class ClaudePaths
{
    private static readonly string _userProfile =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // Plugin-owned (under {pluginDataDir}/scripts/)
    public static string ScriptsDir { get; private set; } = "";
    public static string ClaudeStatusLineScript { get; private set; } = "";
    public static string StatusLineConfigFile { get; private set; } = "";
    public static string ShowProdToyScript { get; private set; } = "";

    // Default Claude CLI locations. Step 4 adds multi-install discovery that
    // supersedes these — they remain as the fallback/primary install.
    public static string ClaudeHooksDir { get; } = Path.Combine(_userProfile, ".claude", "hooks");
    public static string ClaudeSettingsFile { get; } = Path.Combine(_userProfile, ".claude", "settings.json");
    public static string ClaudeMdFile { get; } = Path.Combine(_userProfile, ".claude", "CLAUDE.md");

    /// <summary>
    /// Initialize with the plugin's data directory (from <c>IPluginContext.DataDirectory</c>).
    /// </summary>
    public static void Initialize(string pluginDataDirectory)
    {
        ScriptsDir = Path.Combine(pluginDataDirectory, "scripts");
        ClaudeStatusLineScript = Path.Combine(ScriptsDir, "context-bar.ps1");
        StatusLineConfigFile = Path.Combine(ScriptsDir, "status-line-config.json");
        ShowProdToyScript = Path.Combine(ScriptsDir, "Show-ProdToy.ps1");
    }
}
