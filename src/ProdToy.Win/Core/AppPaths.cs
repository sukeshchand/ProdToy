using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProdToy;

/// <summary>
/// Centralized path definitions for the application.
///
/// Everything is rooted under %USERPROFILE%\.prod-toy\. The one exception is
/// <see cref="DataDir"/>, which a user can redirect to any folder (e.g. a
/// OneDrive/Dropbox/Syncthing path) so their settings and plugin data sync
/// across machines. The override is persisted in a tiny bootstrap file at
/// Root\data-location.json — kept outside <c>data\</c> itself so it can be
/// read before we know where <c>data\</c> lives.
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

    /// <summary>Default data directory if no override is set: Root\data\.</summary>
    public static string DefaultDataDir { get; } = Path.Combine(Root, "data");

    /// <summary>Bootstrap file holding the <see cref="DataDir"/> override, if any.
    /// Lives at Root\data-location.json — outside <c>data\</c> so it stays put
    /// when the user redirects data somewhere else.</summary>
    public static string DataLocationFile { get; } = Path.Combine(Root, "data-location.json");

    /// <summary>Top-level persistent data directory. Defaults to <see cref="DefaultDataDir"/>;
    /// can be redirected via <see cref="SetDataDir"/> to enable cross-machine sync.
    /// Resolved once at startup — changes require a restart.</summary>
    public static string DataDir { get; } = ResolveDataDir();

    /// <summary>Host settings file: DataDir\settings.json</summary>
    public static string SettingsFile { get; } = Path.Combine(DataDir, "settings.json");

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

    /// <summary>Plugin DLLs: Root\plugins\bin\. Always under Root — binaries are
    /// install-local, not user data, and must not sync across machines.</summary>
    public static string PluginsBinDir { get; } = Path.Combine(Root, "plugins", "bin");

    /// <summary>Plugin data (survives uninstall): DataDir\plugins\.</summary>
    public static string PluginsDataDir { get; } = Path.Combine(DataDir, "plugins");

    /// <summary>Plugins state file: DataDir\plugins\plugins-state.json.
    /// Enable/disable choices survive an uninstall and reinstall.</summary>
    public static string PluginsStateFile { get; } = Path.Combine(DataDir, "plugins", "plugins-state.json");

    /// <summary>Logs directory: Root\logs\</summary>
    public static string LogsDir { get; } = Path.Combine(Root, "logs");

    /// <summary>Temporary working dir for updates and other short-lived state: Root\tmp\</summary>
    public static string TmpDir { get; } = Path.Combine(Root, "tmp");

    /// <summary>Machine-local launch settings (env id, etc.): Root\launchSettings.json</summary>
    public static string LaunchSettingsFile { get; } = Path.Combine(Root, "launchSettings.json");

    /// <summary>
    /// Unique identifier for this installation environment. Read from launchSettings.json
    /// (written by the installer). Empty string when not yet set.
    /// </summary>
    public static string EnvId { get; } = ReadEnvId();

    /// <summary>True when DataDir is currently redirected away from the default.</summary>
    public static bool IsDataDirRedirected =>
        !string.Equals(
            Path.TrimEndingDirectorySeparator(DataDir),
            Path.TrimEndingDirectorySeparator(DefaultDataDir),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Persist a new data directory for the next launch. Writes (or deletes,
    /// if <paramref name="path"/> is null/empty/the default) the bootstrap
    /// override file. Callers should prompt the user to restart — paths are
    /// cached in static fields and are not re-read after startup.
    /// </summary>
    public static void SetDataDir(string? path)
    {
        try
        {
            Directory.CreateDirectory(Root);
            if (string.IsNullOrWhiteSpace(path) ||
                string.Equals(
                    Path.TrimEndingDirectorySeparator(path),
                    Path.TrimEndingDirectorySeparator(DefaultDataDir),
                    StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(DataLocationFile)) File.Delete(DataLocationFile);
                return;
            }

            var json = JsonSerializer.Serialize(
                new DataLocationOverride { DataDir = path },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(DataLocationFile, json);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to write data-location override", ex);
        }
    }

    private static string ResolveDataDir()
    {
        try
        {
            if (!File.Exists(DataLocationFile)) return DefaultDataDir;
            var json = File.ReadAllText(DataLocationFile);
            var parsed = JsonSerializer.Deserialize<DataLocationOverride>(json);
            var overridePath = parsed?.DataDir;
            if (string.IsNullOrWhiteSpace(overridePath)) return DefaultDataDir;
            return Path.GetFullPath(overridePath);
        }
        catch
        {
            // Any failure to read the override file falls back to the default —
            // we must never block startup on a bad sync config.
            return DefaultDataDir;
        }
    }

    private static string ReadEnvId()
    {
        try
        {
            if (!File.Exists(LaunchSettingsFile)) return "";
            var root = JsonNode.Parse(File.ReadAllText(LaunchSettingsFile));
            var id = root?["envId"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id)) return id;
        }
        catch { }
        return "";
    }

    private sealed class DataLocationOverride
    {
        public string? DataDir { get; set; }
    }
}
