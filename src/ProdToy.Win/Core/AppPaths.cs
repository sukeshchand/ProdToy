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

    /// <summary>Settings file: Root\settings.json</summary>
    public static string SettingsFile { get; } = Path.Combine(Root, "settings.json");

    /// <summary>History root: Root\history\</summary>
    public static string HistoryDir { get; } = Path.Combine(Root, "history");

    /// <summary>Claude chat history: Root\history\claude\chats\</summary>
    public static string ClaudeChatHistoryDir { get; } = Path.Combine(Root, "history", "claude", "chats");

    /// <summary>Screenshots directory: Root\screenshots\</summary>
    public static string ScreenshotsDir { get; } = Path.Combine(Root, "screenshots");

    /// <summary>Edit sessions: Root\screenshots\_edits\</summary>
    public static string ScreenshotsEditsDir { get; } = Path.Combine(Root, "screenshots", "_edits");

    /// <summary>Scripts directory: Root\scripts\</summary>
    public static string ScriptsDir { get; } = Path.Combine(Root, "scripts");

    /// <summary>Claude status line script: Root\scripts\context-bar.ps1</summary>
    public static string ClaudeStatusLineScript { get; } = Path.Combine(Root, "scripts", "context-bar.ps1");

    /// <summary>Alarms data: Root\alarms\</summary>
    public static string AlarmsDir { get; } = Path.Combine(Root, "alarms");

    /// <summary>Plugins root directory: Root\plugins\</summary>
    public static string PluginsDir { get; } = Path.Combine(Root, "plugins");

    /// <summary>Plugin DLLs: Root\plugins\bin\</summary>
    public static string PluginsBinDir { get; } = Path.Combine(Root, "plugins", "bin");

    /// <summary>Plugin data (survives uninstall): Root\plugins\data\</summary>
    public static string PluginsDataDir { get; } = Path.Combine(Root, "plugins", "data");

    /// <summary>Plugins state file: Root\plugins\plugins-state.json</summary>
    public static string PluginsStateFile { get; } = Path.Combine(Root, "plugins", "plugins-state.json");

    /// <summary>Logs directory: Root\logs\</summary>
    public static string LogsDir { get; } = Path.Combine(Root, "logs");

    /// <summary>Claude hooks directory: %USERPROFILE%\.claude\hooks\</summary>
    public static string ClaudeHooksDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "hooks");

    /// <summary>Claude settings file: %USERPROFILE%\.claude\settings.json</summary>
    public static string ClaudeSettingsFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    /// <summary>Claude CLAUDE.md: %USERPROFILE%\.claude\CLAUDE.md</summary>
    public static string ClaudeMdFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "CLAUDE.md");
}
