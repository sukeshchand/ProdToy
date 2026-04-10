using System.Diagnostics;
using System.Text.Json;
using ProdToy.Sdk;

namespace ProdToy;

/// <summary>
/// Per-plugin context providing host services, data directory, and settings I/O.
/// </summary>
sealed class PluginContextImpl : IPluginContext
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public IPluginHost Host { get; }
    public string DataDirectory { get; }
    public PluginAttribute Metadata { get; }

    public PluginContextImpl(IPluginHost host, PluginAttribute metadata, string pluginDir)
    {
        Host = host;
        Metadata = metadata;
        // Data stored outside plugin DLL folder so it survives uninstall/reinstall
        // Path: ~/.prod-toy/plugins/data/{PluginId}/
        DataDirectory = Path.Combine(AppPaths.PluginsDir, "data", metadata.Id);

        // Migrate data from old location ({pluginDir}/data/) if it exists
        MigrateOldData(pluginDir);
    }

    private void MigrateOldData(string pluginDir)
    {
        try
        {
            string oldDataDir = Path.Combine(pluginDir, "data");
            if (!Directory.Exists(oldDataDir)) return;
            if (Directory.GetFiles(oldDataDir).Length == 0 && Directory.GetDirectories(oldDataDir).Length == 0) return;

            // Only migrate if new location doesn't already have data
            if (Directory.Exists(DataDirectory) && Directory.GetFiles(DataDirectory).Length > 0) return;

            Directory.CreateDirectory(DataDirectory);
            foreach (var file in Directory.GetFiles(oldDataDir))
                File.Copy(file, Path.Combine(DataDirectory, Path.GetFileName(file)), overwrite: false);
            foreach (var dir in Directory.GetDirectories(oldDataDir))
                CopyDirectoryRecursive(dir, Path.Combine(DataDirectory, Path.GetFileName(dir)));

            // Remove old data dir after successful migration
            Directory.Delete(oldDataDir, recursive: true);
            Debug.WriteLine($"Migrated plugin data from {oldDataDir} to {DataDirectory}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Plugin data migration failed for {Metadata.Id}: {ex.Message}");
        }
    }

    private static void CopyDirectoryRecursive(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectoryRecursive(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    public T LoadSettings<T>() where T : class, new()
    {
        try
        {
            string path = Path.Combine(DataDirectory, "settings.json");
            if (!File.Exists(path))
                return new T();

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json) ?? new T();
        }
        catch (Exception ex)
        {
            LogError($"Failed to load settings", ex);
            return new T();
        }
    }

    public void SaveSettings<T>(T settings) where T : class
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            string path = Path.Combine(DataDirectory, "settings.json");
            string json = JsonSerializer.Serialize(settings, _jsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            LogError($"Failed to save settings", ex);
        }
    }

    public void Log(string message)
    {
        WriteLog("INFO", message);
    }

    public void LogError(string message, Exception? ex = null)
    {
        string detail = ex != null ? $"{message}: {ex.Message}" : message;
        WriteLog("ERROR", detail);
    }

    private void WriteLog(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            string logPath = Path.Combine(AppPaths.LogsDir, "plugins.log");
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] [{Metadata.Id}] {message}";
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Plugin log write failed: {ex.Message}");
        }
    }
}
