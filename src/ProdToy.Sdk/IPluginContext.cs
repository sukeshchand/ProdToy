namespace ProdToy.Sdk;

/// <summary>
/// Per-plugin context providing access to host services and plugin-specific paths/settings.
/// </summary>
public interface IPluginContext
{
    /// <summary>Host services shared across all plugins.</summary>
    IPluginHost Host { get; }

    /// <summary>The plugin's own data directory: ~/.prod-toy/plugins/{PluginId}/data/</summary>
    string DataDirectory { get; }

    /// <summary>Load this plugin's settings as a typed record. Returns new T() if no settings file exists.</summary>
    T LoadSettings<T>() where T : class, new();

    /// <summary>Save this plugin's settings to its data directory.</summary>
    void SaveSettings<T>(T settings) where T : class;

    /// <summary>The plugin's metadata from its [Plugin] attribute.</summary>
    PluginAttribute Metadata { get; }

    /// <summary>Log an informational message to the host's daily log, tagged with this plugin's id.</summary>
    void Log(string message);

    /// <summary>Log a warning to the host's daily log, tagged with this plugin's id.</summary>
    void LogWarn(string message);

    /// <summary>Log an error to the host's daily log, tagged with this plugin's id.</summary>
    void LogError(string message, Exception? ex = null);
}
