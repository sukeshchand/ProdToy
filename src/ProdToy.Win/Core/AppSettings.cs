using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProdToy;

record AppSettingsData
{
    [JsonPropertyName("theme")]
    public string Theme { get; init; } = "Warm Gray";

    [JsonPropertyName("globalFont")]
    public string GlobalFont { get; init; } = "Segoe UI";

    public const string DefaultUpdateLocation =
        "https://github.com/sukeshchand/ProdToy/releases/latest/download/metadata.json";

    [JsonPropertyName("updateLocation")]
    public string UpdateLocation { get; init; } = DefaultUpdateLocation;

    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; init; } = true;
}

static class AppSettings
{
    private static readonly string _settingsPath = AppPaths.SettingsFile;

    private static AppSettingsData? _cached;

    public static AppSettingsData Load()
    {
        if (_cached != null) return _cached;

        try
        {
            if (File.Exists(_settingsPath))
            {
                string json = File.ReadAllText(_settingsPath);
                _cached = JsonSerializer.Deserialize<AppSettingsData>(json) ?? new AppSettingsData();
                return _cached;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load settings: {ex.Message}");
        }

        _cached = new AppSettingsData();
        return _cached;
    }

    public static void Save(AppSettingsData settings)
    {
        _cached = settings;
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save settings: {ex.Message}");
        }
    }
}
