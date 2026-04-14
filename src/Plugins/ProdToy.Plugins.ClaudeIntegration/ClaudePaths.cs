namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>
/// Paths used by the Claude integration plugin.
/// Host-owned state lives under the plugin's own data directory; Claude CLI
/// state lives in its fixed locations under the user profile.
/// </summary>
static class ClaudePaths
{
    private static readonly string _userProfile =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // Plugin-owned (under {pluginDataDir}/scripts/)
    public static string ScriptsDir { get; private set; } = "";
    public static string ClaudeStatusLineScript { get; private set; } = "";
    public static string StatusLineConfigFile { get; private set; } = "";

    // Claude paths (fixed locations)
    public static string ClaudeHooksDir { get; } = Path.Combine(_userProfile, ".claude", "hooks");
    public static string ClaudeSettingsFile { get; } = Path.Combine(_userProfile, ".claude", "settings.json");
    public static string ClaudeMdFile { get; } = Path.Combine(_userProfile, ".claude", "CLAUDE.md");

    /// <summary>
    /// Initialize with the plugin's data directory (from <c>IPluginContext.DataDirectory</c>).
    /// All plugin-owned script state lives under <c>{dataDirectory}/scripts/</c>.
    /// </summary>
    public static void Initialize(string pluginDataDirectory)
    {
        ScriptsDir = Path.Combine(pluginDataDirectory, "scripts");
        ClaudeStatusLineScript = Path.Combine(ScriptsDir, "context-bar.ps1");
        StatusLineConfigFile = Path.Combine(ScriptsDir, "status-line-config.json");
    }
}
