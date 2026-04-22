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

    public PluginContextImpl(IPluginHost host, PluginAttribute metadata)
    {
        Host = host;
        Metadata = metadata;
        // Data stored outside plugin DLL folder so it survives uninstall/reinstall.
        // Path: ~/.prod-toy/data/plugins/{PluginId}/
        DataDirectory = Path.Combine(AppPaths.PluginsDataDir, metadata.Id);
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
        => WriteLog("INFO", message);

    public void LogWarn(string message)
        => WriteLog("WARN", message);

    public void LogError(string message, Exception? ex = null)
    {
        string detail = ex != null ? $"{message}: {ex}" : message;
        WriteLog("ERROR", detail);
    }

    private void WriteLog(string level, string message)
        => global::ProdToy.Log.Tagged(level, Metadata.Id, message);
}
