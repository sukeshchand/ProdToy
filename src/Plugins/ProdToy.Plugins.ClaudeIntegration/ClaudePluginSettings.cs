using System.Text.Json.Serialization;

namespace ProdToy.Plugins.ClaudeIntegration;

record ClaudePluginSettings
{
    // Hook toggles
    [JsonPropertyName("hookStopEnabled")] public bool HookStopEnabled { get; init; } = true;
    [JsonPropertyName("hookNotificationEnabled")] public bool HookNotificationEnabled { get; init; } = false;
    [JsonPropertyName("hookUserPromptEnabled")] public bool HookUserPromptEnabled { get; init; } = true;

    // Status line — enabled by default
    [JsonPropertyName("slEnabled")] public bool SlEnabled { get; init; } = true;

    // Status line item visibility
    [JsonPropertyName("slShowModel")] public bool SlShowModel { get; init; } = true;
    [JsonPropertyName("slShowDir")] public bool SlShowDir { get; init; } = true;
    [JsonPropertyName("slShowBranch")] public bool SlShowBranch { get; init; } = true;
    [JsonPropertyName("slShowPrompts")] public bool SlShowPrompts { get; init; } = false;
    [JsonPropertyName("slShowContext")] public bool SlShowContext { get; init; } = true;
    [JsonPropertyName("slShowDuration")] public bool SlShowDuration { get; init; } = false;
    [JsonPropertyName("slShowMode")] public bool SlShowMode { get; init; } = true;
    [JsonPropertyName("slShowVersion")] public bool SlShowVersion { get; init; } = true;
    [JsonPropertyName("slShowEditStats")] public bool SlShowEditStats { get; init; } = false;

    // Auto-title
    [JsonPropertyName("autoTitleToFolder")] public bool AutoTitleToFolder { get; init; } = false;
}
