namespace ProdToy;

/// <summary>
/// Centralized path definitions for the application.
/// All paths are rooted under %USERPROFILE%\.prod-toy\.
/// </summary>
static class AppPaths
{
    /// <summary>Root: %USERPROFILE%\.prod-toy\</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".prod-toy");

    /// <summary>Executable install location: Root\ProdToy.exe</summary>
    public static string ExePath { get; } = Path.Combine(Root, "ProdToy.exe");

    /// <summary>Installer exe location (used by Settings → Uninstall): Root\ProdToySetup.exe</summary>
    public static string SetupExePath { get; } = Path.Combine(Root, "ProdToySetup.exe");

    /// <summary>Top-level persistent data directory: Root\data\.
    /// Survives uninstall/reinstall. Host settings and plugin data both
    /// live here.</summary>
    public static string DataDir { get; } = Path.Combine(Root, "data");

    /// <summary>Host settings file: Root\data\settings.json</summary>
    public static string SettingsFile { get; } = Path.Combine(Root, "data", "settings.json");

    /// <summary>History root: Root\history\ (legacy; plugin data dirs preferred)</summary>
    public static string HistoryDir { get; } = Path.Combine(Root, "history");

    /// <summary>Screenshots directory: Root\screenshots\</summary>
    public static string ScreenshotsDir { get; } = Path.Combine(Root, "screenshots");

    /// <summary>Edit sessions: Root\screenshots\_edits\</summary>
    public static string ScreenshotsEditsDir { get; } = Path.Combine(Root, "screenshots", "_edits");

    /// <summary>Alarms data: Root\alarms\</summary>
    public static string AlarmsDir { get; } = Path.Combine(Root, "alarms");

    /// <summary>Plugins root directory: Root\plugins\</summary>
    public static string PluginsDir { get; } = Path.Combine(Root, "plugins");

    /// <summary>Plugin DLLs: Root\plugins\bin\</summary>
    public static string PluginsBinDir { get; } = Path.Combine(Root, "plugins", "bin");

    /// <summary>Plugin data (survives uninstall): Root\data\plugins\.
    /// Separate top-level data\ tree so plugins\ only ever contains the
    /// volatile bin\ directory (wiped on uninstall), and all persistent
    /// state lives under data\.</summary>
    public static string PluginsDataDir { get; } = Path.Combine(Root, "data", "plugins");

    /// <summary>Plugins state file: Root\data\plugins\plugins-state.json.
    /// Enable/disable choices survive an uninstall and reinstall.</summary>
    public static string PluginsStateFile { get; } = Path.Combine(Root, "data", "plugins", "plugins-state.json");

    /// <summary>Logs directory: Root\logs\</summary>
    public static string LogsDir { get; } = Path.Combine(Root, "logs");

    /// <summary>Temporary working dir for updates and other short-lived state: Root\tmp\</summary>
    public static string TmpDir { get; } = Path.Combine(Root, "tmp");
}
