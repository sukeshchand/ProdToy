namespace ProdToy.Setup;

/// <summary>
/// Path constants used by the installer. Mirrors the host's AppPaths for the
/// paths the installer touches — kept as a local copy so the Setup project
/// stays independent of ProdToy.Win.
/// </summary>
static class AppPaths
{
    /// <summary>Root: %USERPROFILE%\.prod-toy\</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".prod-toy");

    /// <summary>Installed host exe: Root\ProdToy.exe</summary>
    public static string ExePath { get; } = Path.Combine(Root, "ProdToy.exe");

    /// <summary>Installed setup exe (used by Windows Add/Remove): Root\ProdToySetup.exe</summary>
    public static string SetupExePath { get; } = Path.Combine(Root, "ProdToySetup.exe");

    /// <summary>Plugin DLLs: Root\plugins\bin\</summary>
    public static string PluginsBinDir { get; } = Path.Combine(Root, "plugins", "bin");

    /// <summary>Default data directory: Root\data\</summary>
    public static string DataDir { get; } = Path.Combine(Root, "data");

    /// <summary>Plugin data (preserved on uninstall): Root\data\plugins\</summary>
    public static string PluginsDataDir { get; } = Path.Combine(Root, "data", "plugins");

    /// <summary>Launch settings (machine-local env id): Root\launchSettings.json</summary>
    public static string LaunchSettingsFile { get; } = Path.Combine(Root, "launchSettings.json");

    /// <summary>Claude hooks directory: %USERPROFILE%\.claude\hooks\</summary>
    public static string ClaudeHooksDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "hooks");

    /// <summary>Claude settings file: %USERPROFILE%\.claude\settings.json</summary>
    public static string ClaudeSettingsFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
}
