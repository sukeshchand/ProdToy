using System.Text.Json.Serialization;

namespace ProdToy.Plugins.Screenshot;

record ScreenshotPluginSettings
{
    [JsonPropertyName("screenshotEnabled")]
    public bool ScreenshotEnabled { get; init; } = true;

    [JsonPropertyName("screenshotHotkey")]
    public string ScreenshotHotkey { get; init; } = "Ctrl+Q";

    [JsonPropertyName("tripleCtrlEnabled")]
    public bool TripleCtrlEnabled { get; init; } = true;

    [JsonPropertyName("screenshotLastColor")]
    public string ScreenshotLastColor { get; init; } = "Red";

    [JsonPropertyName("screenshotLastThickness")]
    public float ScreenshotLastThickness { get; init; } = 2f;

    [JsonPropertyName("screenshotMaxUndo")]
    public int ScreenshotMaxUndo { get; init; } = 30;
}
