using System.Text.Json.Serialization;

namespace ProdToy.Plugins.Alarm;

enum AlarmScheduleType
{
    Once,
    Daily,
    Weekdays,
    Weekend,
    Weekly,
    Monthly,
    Interval,
    Custom
}

enum AlarmStatus
{
    Active,
    Disabled,
    Expired,
    Completed
}

enum AlarmNotificationMode
{
    Popup,
    Windows,
    Both
}

enum AlarmHistoryEventType
{
    Created,
    Edited,
    Triggered,
    PopupShown,
    NotificationShown,
    SoundPlayed,
    Dismissed,
    Snoozed,
    Missed,
    Completed,
    AutoDisabled,
    Deleted,
    TriggerFailed,
    RestartRecovered
}

record AlarmSchedule
{
    [JsonPropertyName("type")]
    public AlarmScheduleType Type { get; init; } = AlarmScheduleType.Once;

    [JsonPropertyName("timeOfDay")]
    public string TimeOfDay { get; init; } = "09:00";

    [JsonPropertyName("oneTimeDate")]
    public string? OneTimeDate { get; init; }

    [JsonPropertyName("dayOfMonth")]
    public int? DayOfMonth { get; init; }

    [JsonPropertyName("intervalMinutes")]
    public int? IntervalMinutes { get; init; }

    [JsonPropertyName("customDays")]
    public DayOfWeek[]? CustomDays { get; init; }

    public TimeSpan GetTimeOfDay()
    {
        if (TimeSpan.TryParse(TimeOfDay, out var ts)) return ts;
        return TimeSpan.FromHours(9);
    }

    public DateTime? GetOneTimeDateTime()
    {
        if (OneTimeDate == null) return null;
        if (DateTime.TryParse(OneTimeDate, out var dt))
            return dt.Date + GetTimeOfDay();
        return null;
    }
}

record AlarmEntry
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("note")]
    public string Note { get; init; } = "";

    [JsonPropertyName("schedule")]
    public AlarmSchedule Schedule { get; init; } = new();

    [JsonPropertyName("status")]
    public AlarmStatus Status { get; init; } = AlarmStatus.Active;

    [JsonPropertyName("notification")]
    public AlarmNotificationMode Notification { get; init; } = AlarmNotificationMode.Both;

    [JsonPropertyName("snoozeMinutes")]
    public int SnoozeMinutes { get; init; } = 5;

    [JsonPropertyName("soundEnabled")]
    public bool SoundEnabled { get; init; } = true;

    [JsonPropertyName("fireAndForget")]
    public bool FireAndForget { get; init; } = false;

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; init; }

    [JsonPropertyName("lastTriggeredAt")]
    public DateTime? LastTriggeredAt { get; init; }

    [JsonPropertyName("triggerCount")]
    public int TriggerCount { get; init; } = 0;

    [JsonPropertyName("lastError")]
    public string? LastError { get; init; }

    [JsonPropertyName("endDate")]
    public string? EndDate { get; init; }

    public DateTime? GetNextTrigger()
    {
        if (Status != AlarmStatus.Active) return null;
        var now = DateTime.Now;
        var time = Schedule.GetTimeOfDay();

        switch (Schedule.Type)
        {
            case AlarmScheduleType.Once:
                var dt = Schedule.GetOneTimeDateTime();
                return dt > now ? dt : null;

            case AlarmScheduleType.Daily:
                var today = now.Date + time;
                return today > now ? today : today.AddDays(1);

            case AlarmScheduleType.Weekdays:
                return GetNextDayMatch(now, time, d => d.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday);

            case AlarmScheduleType.Weekend:
                return GetNextDayMatch(now, time, d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);

            case AlarmScheduleType.Weekly:
                if (Schedule.CustomDays is { Length: > 0 })
                    return GetNextDayMatch(now, time, d => d.DayOfWeek == Schedule.CustomDays[0]);
                return null;

            case AlarmScheduleType.Monthly:
                if (Schedule.DayOfMonth is int dom)
                {
                    var thisMonth = new DateTime(now.Year, now.Month, Math.Min(dom, DateTime.DaysInMonth(now.Year, now.Month))) + time;
                    if (thisMonth > now) return thisMonth;
                    var next = now.AddMonths(1);
                    return new DateTime(next.Year, next.Month, Math.Min(dom, DateTime.DaysInMonth(next.Year, next.Month))) + time;
                }
                return null;

            case AlarmScheduleType.Interval:
                if (Schedule.IntervalMinutes is int mins and > 0)
                {
                    if (LastTriggeredAt is DateTime last)
                        return last.AddMinutes(mins);
                    return now.AddMinutes(1);
                }
                return null;

            case AlarmScheduleType.Custom:
                if (Schedule.CustomDays is { Length: > 0 } days)
                    return GetNextDayMatch(now, time, d => days.Contains(d.DayOfWeek));
                return null;

            default:
                return null;
        }
    }

    private static DateTime? GetNextDayMatch(DateTime now, TimeSpan time, Func<DateTime, bool> match)
    {
        for (int i = 0; i < 8; i++)
        {
            var candidate = now.Date.AddDays(i) + time;
            if (candidate > now && match(candidate))
                return candidate;
        }
        return null;
    }

    public string GetScheduleDescription()
    {
        return Schedule.Type switch
        {
            AlarmScheduleType.Once => Schedule.OneTimeDate != null ? $"Once on {Schedule.OneTimeDate}" : "Once",
            AlarmScheduleType.Daily => "Every day",
            AlarmScheduleType.Weekdays => "Weekdays (Mon-Fri)",
            AlarmScheduleType.Weekend => "Weekends (Sat-Sun)",
            AlarmScheduleType.Weekly when Schedule.CustomDays is { Length: > 0 } => $"Every {Schedule.CustomDays[0]}",
            AlarmScheduleType.Monthly when Schedule.DayOfMonth is int d => $"Monthly on day {d}",
            AlarmScheduleType.Interval when Schedule.IntervalMinutes is int m => $"Every {m} min",
            AlarmScheduleType.Custom when Schedule.CustomDays is { Length: > 0 } => string.Join(", ", Schedule.CustomDays.Select(d => d.ToString()[..3])),
            _ => Schedule.Type.ToString(),
        };
    }
}

record AlarmHistoryEntry
{
    [JsonPropertyName("alarmId")]
    public string AlarmId { get; init; } = "";

    [JsonPropertyName("alarmTitle")]
    public string AlarmTitle { get; init; } = "";

    [JsonPropertyName("eventType")]
    public AlarmHistoryEventType EventType { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; } = DateTime.Now;

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}
