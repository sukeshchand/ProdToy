using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevToy;

record AppSettingsData
{
    [JsonPropertyName("theme")]
    public string Theme { get; init; } = "Amber";

    [JsonPropertyName("historyEnabled")]
    public bool HistoryEnabled { get; init; } = true;

    [JsonPropertyName("showQuotes")]
    public bool ShowQuotes { get; init; } = true;

    [JsonPropertyName("updateLocation")]
    public string UpdateLocation { get; init; } = "";

    [JsonPropertyName("autoTitleToFolder")]
    public bool AutoTitleToFolder { get; init; } = false;
}

static class AppSettings
{
    private static readonly string _dataDir = AppPaths.Root;

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
            Directory.CreateDirectory(_dataDir);
            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save settings: {ex.Message}");
        }
    }

    public static string DataDir => _dataDir;
}
