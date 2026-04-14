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

    // Chat history (plugin-owned after Phase 3)
    [JsonPropertyName("historyEnabled")] public bool HistoryEnabled { get; init; } = true;

    // One-shot flag: true once the plugin has copied legacy host day files
    // from ~/.prod-toy/history/claude/chats/ into its own data dir.
    [JsonPropertyName("historyMigratedFromHost")] public bool HistoryMigratedFromHost { get; init; } = false;

    // Notification prefs (Phase 8 — migrated from host AppSettings). These are
    // Claude-specific: only the ChatPopupForm reads them.
    [JsonPropertyName("notificationsEnabled")] public bool NotificationsEnabled { get; init; } = true;
    // "Popup" | "Windows" | "Popup + Windows"
    [JsonPropertyName("notificationMode")] public string NotificationMode { get; init; } = "Popup";
    [JsonPropertyName("showQuotes")] public bool ShowQuotes { get; init; } = true;

    // Snooze end-time. Default == MinValue means "not snoozed". Persisted so
    // a 30-minute snooze survives an app restart. (The host used to keep
    // this in-memory; we deliberately upgrade to persistent here.)
    [JsonPropertyName("snoozeUntil")] public DateTime SnoozeUntil { get; init; } = DateTime.MinValue;
}
