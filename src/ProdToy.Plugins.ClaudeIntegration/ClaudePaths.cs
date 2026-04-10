namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>
/// Paths used by the Claude integration plugin.
/// These reference both ProdToy's data directory and Claude's config directories.
/// </summary>
static class ClaudePaths
{
    private static readonly string _userProfile =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // ProdToy paths (set from plugin data dir)
    public static string ScriptsDir { get; private set; } = "";
    public static string ClaudeStatusLineScript { get; private set; } = "";

    // Claude paths (fixed locations)
    public static string ClaudeHooksDir { get; } = Path.Combine(_userProfile, ".claude", "hooks");
    public static string ClaudeSettingsFile { get; } = Path.Combine(_userProfile, ".claude", "settings.json");
    public static string ClaudeMdFile { get; } = Path.Combine(_userProfile, ".claude", "CLAUDE.md");

    public static void Initialize(string appRootPath)
    {
        ScriptsDir = Path.Combine(appRootPath, "scripts");
        ClaudeStatusLineScript = Path.Combine(ScriptsDir, "context-bar.ps1");
    }
}
