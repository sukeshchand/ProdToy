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

    // Status line item visibility
    [JsonPropertyName("slShowModel")] public bool SlShowModel { get; init; } = true;
    [JsonPropertyName("slShowDir")] public bool SlShowDir { get; init; } = true;
    [JsonPropertyName("slShowBranch")] public bool SlShowBranch { get; init; } = true;
    [JsonPropertyName("slShowPrompts")] public bool SlShowPrompts { get; init; } = true;
    [JsonPropertyName("slShowContext")] public bool SlShowContext { get; init; } = true;
    [JsonPropertyName("slShowDuration")] public bool SlShowDuration { get; init; } = true;
    [JsonPropertyName("slShowMode")] public bool SlShowMode { get; init; } = true;
    [JsonPropertyName("slShowVersion")] public bool SlShowVersion { get; init; } = true;
    [JsonPropertyName("slShowEditStats")] public bool SlShowEditStats { get; init; } = true;

    [JsonPropertyName("screenshotLastColor")]
    public string ScreenshotLastColor { get; init; } = "Red";

    [JsonPropertyName("screenshotLastThickness")]
    public float ScreenshotLastThickness { get; init; } = 2f;

    [JsonPropertyName("screenshotMaxUndo")]
    public int ScreenshotMaxUndo { get; init; } = 30;
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
