using System.Text.Json.Serialization;

namespace ProdToy.Plugins.Alarm;

record AlarmPluginSettings
{
    [JsonPropertyName("alarmsEnabled")]
    public bool AlarmsEnabled { get; init; } = true;

    [JsonPropertyName("alarmDefaultNotification")]
    public string AlarmDefaultNotification { get; init; } = "Both";

    [JsonPropertyName("alarmDefaultSnoozeMinutes")]
    public int AlarmDefaultSnoozeMinutes { get; init; } = 5;

    [JsonPropertyName("alarmSoundEnabled")]
    public bool AlarmSoundEnabled { get; init; } = true;

    [JsonPropertyName("alarmHistoryMaxEntries")]
    public int AlarmHistoryMaxEntries { get; init; } = 500;

    [JsonPropertyName("alarmMissedGraceMinutes")]
    public int AlarmMissedGraceMinutes { get; init; } = 5;
}
