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
        DataDirectory = Path.Combine(pluginDir, "data");
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
