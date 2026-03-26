namespace DevToy;

/// <summary>
/// Centralized path definitions for the application.
/// All paths are rooted under %USERPROFILE%\.dev-toy\.
/// </summary>
static class AppPaths
{
    /// <summary>Root: %USERPROFILE%\.dev-toy\</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dev-toy");

    /// <summary>Executable install location: Root\DevToy.exe</summary>
    public static string ExePath { get; } = Path.Combine(Root, "DevToy.exe");

    /// <summary>Settings file: Root\settings.json</summary>
    public static string SettingsFile { get; } = Path.Combine(Root, "settings.json");

    /// <summary>History root: Root\history\</summary>
    public static string HistoryDir { get; } = Path.Combine(Root, "history");

    /// <summary>Claude chat history: Root\history\claude\chats\</summary>
    public static string ClaudeChatHistoryDir { get; } = Path.Combine(Root, "history", "claude", "chats");

    /// <summary>Screenshots directory: Root\screenshots\</summary>
    public static string ScreenshotsDir { get; } = Path.Combine(Root, "screenshots");

    /// <summary>Temp captures: Root\screenshots\temp\</summary>
    public static string ScreenshotsTempDir { get; } = Path.Combine(Root, "screenshots", "temp");

    /// <summary>Scripts directory: Root\scripts\</summary>
    public static string ScriptsDir { get; } = Path.Combine(Root, "scripts");

    /// <summary>Claude status line script: Root\scripts\context-bar.ps1</summary>
    public static string ClaudeStatusLineScript { get; } = Path.Combine(Root, "scripts", "context-bar.ps1");

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
