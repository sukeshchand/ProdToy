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

enum AlarmPriority
{
    Low,
    Normal,
    High
}

/// <summary>
/// Computed, UI-only display status. Not persisted; derived from AlarmEntry state
/// (Status, PausedUntil, SnoozedUntil, LastError, last trigger vs schedule).
/// </summary>
enum AlarmDisplayStatus
{
    Active,
    Disabled,
    Paused,
    Snoozed,
    Missed,
    Error,
    Completed,
    Expired,
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
    RestartRecovered,
    Paused,
    Resumed,
    Skipped,
}

/// <summary>
/// Phase-3 scaffold: an alarm may trigger zero or more additional actions beyond the
/// built-in popup/sound/windows notification. Only Type is required — everything else
/// is interpreted per-Type by an action dispatcher (not all types are implemented yet).
/// </summary>
record AlarmAction
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("target")]
    public string? Target { get; init; }

    [JsonPropertyName("args")]
    public string? Args { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;
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

    // --- v2 additions (all optional / back-compat) ---

    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("priority")]
    public AlarmPriority Priority { get; init; } = AlarmPriority.Normal;

    [JsonPropertyName("pausedUntil")]
    public DateTime? PausedUntil { get; init; }

    [JsonPropertyName("snoozedUntil")]
    public DateTime? SnoozedUntil { get; init; }

    [JsonPropertyName("skipNextOccurrence")]
    public bool SkipNextOccurrence { get; init; }

    [JsonPropertyName("startDate")]
    public string? StartDate { get; init; }

    [JsonPropertyName("exceptionDates")]
    public string[]? ExceptionDates { get; init; }

    [JsonPropertyName("actions")]
    public AlarmAction[]? Actions { get; init; }

    public DateTime? GetNextTrigger()
    {
        if (Status != AlarmStatus.Active) return null;
        var now = DateTime.Now;
        var time = Schedule.GetTimeOfDay();
        var from = PausedUntil is DateTime pu && pu > now ? pu : now;

        DateTime? next = Schedule.Type switch
        {
            AlarmScheduleType.Once => Schedule.GetOneTimeDateTime() is DateTime dt && dt > now ? dt : null,
            AlarmScheduleType.Daily => NextDaily(from, time),
            AlarmScheduleType.Weekdays => GetNextDayMatch(from, time, d => d.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday),
            AlarmScheduleType.Weekend => GetNextDayMatch(from, time, d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday),
            AlarmScheduleType.Weekly when Schedule.CustomDays is { Length: > 0 } days
                => GetNextDayMatch(from, time, d => d.DayOfWeek == days[0]),
            AlarmScheduleType.Monthly when Schedule.DayOfMonth is int dom => NextMonthly(from, time, dom),
            AlarmScheduleType.Interval when Schedule.IntervalMinutes is int mins and > 0
                => LastTriggeredAt is DateTime last ? last.AddMinutes(mins) : from.AddMinutes(1),
            AlarmScheduleType.Custom when Schedule.CustomDays is { Length: > 0 } days
                => GetNextDayMatch(from, time, d => days.Contains(d.DayOfWeek)),
            _ => null,
        };

        if (next == null) return null;
        if (EndDate != null && DateTime.TryParse(EndDate, out var end) && next.Value.Date > end.Date)
            return null;
        if (ExceptionDates is { Length: > 0 } exDates)
        {
            int guard = 0;
            while (guard++ < 32 && next is DateTime nv && IsExceptionDate(nv, exDates))
                next = AdvanceAfter(nv, time);
        }
        return next;
    }

    public AlarmDisplayStatus GetDisplayStatus()
    {
        if (Status == AlarmStatus.Disabled) return AlarmDisplayStatus.Disabled;
        if (Status == AlarmStatus.Completed) return AlarmDisplayStatus.Completed;
        if (Status == AlarmStatus.Expired) return AlarmDisplayStatus.Expired;

        var now = DateTime.Now;
        if (PausedUntil is DateTime pu && pu > now) return AlarmDisplayStatus.Paused;
        if (SnoozedUntil is DateTime su && su > now) return AlarmDisplayStatus.Snoozed;
        if (!string.IsNullOrEmpty(LastError)) return AlarmDisplayStatus.Error;

        // Missed: scheduled window passed recently but we never triggered on/after it.
        if (Schedule.Type != AlarmScheduleType.Interval)
        {
            var time = Schedule.GetTimeOfDay();
            var scheduledToday = now.Date + time;
            if (scheduledToday < now && (now - scheduledToday).TotalMinutes <= 60)
            {
                bool didFireToday = LastTriggeredAt is DateTime lt
                    && lt.Date == now.Date
                    && lt.TimeOfDay >= time.Subtract(TimeSpan.FromMinutes(1));
                if (!didFireToday && DayMatchesSchedule(now))
                    return AlarmDisplayStatus.Missed;
            }
        }

        return AlarmDisplayStatus.Active;
    }

    private bool DayMatchesSchedule(DateTime when) => Schedule.Type switch
    {
        AlarmScheduleType.Once => Schedule.OneTimeDate != null
            && DateTime.TryParse(Schedule.OneTimeDate, out var d) && d.Date == when.Date,
        AlarmScheduleType.Daily => true,
        AlarmScheduleType.Weekdays => when.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday,
        AlarmScheduleType.Weekend => when.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
        AlarmScheduleType.Weekly => Schedule.CustomDays is { Length: > 0 } days && when.DayOfWeek == days[0],
        AlarmScheduleType.Monthly => Schedule.DayOfMonth is int dom && when.Day == dom,
        AlarmScheduleType.Custom => Schedule.CustomDays is { Length: > 0 } days && days.Contains(when.DayOfWeek),
        _ => false,
    };

    private static DateTime NextDaily(DateTime from, TimeSpan time)
    {
        var today = from.Date + time;
        return today > from ? today : today.AddDays(1);
    }

    private static DateTime NextMonthly(DateTime from, TimeSpan time, int dom)
    {
        var thisMonth = new DateTime(from.Year, from.Month,
            Math.Min(dom, DateTime.DaysInMonth(from.Year, from.Month))) + time;
        if (thisMonth > from) return thisMonth;
        var nm = from.AddMonths(1);
        return new DateTime(nm.Year, nm.Month,
            Math.Min(dom, DateTime.DaysInMonth(nm.Year, nm.Month))) + time;
    }

    private static DateTime? GetNextDayMatch(DateTime from, TimeSpan time, Func<DateTime, bool> match)
    {
        for (int i = 0; i < 8; i++)
        {
            var candidate = from.Date.AddDays(i) + time;
            if (candidate > from && match(candidate))
                return candidate;
        }
        return null;
    }

    private static bool IsExceptionDate(DateTime when, string[] exDates)
    {
        foreach (var s in exDates)
        {
            if (DateTime.TryParse(s, out var ex) && ex.Date == when.Date) return true;
        }
        return false;
    }

    private DateTime? AdvanceAfter(DateTime when, TimeSpan time)
    {
        return Schedule.Type switch
        {
            AlarmScheduleType.Daily => when.AddDays(1),
            AlarmScheduleType.Weekdays => GetNextDayMatch(when.Date.AddDays(1).AddSeconds(-1), time,
                d => d.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday),
            AlarmScheduleType.Weekend => GetNextDayMatch(when.Date.AddDays(1).AddSeconds(-1), time,
                d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday),
            AlarmScheduleType.Weekly when Schedule.CustomDays is { Length: > 0 } days
                => GetNextDayMatch(when.Date.AddDays(1).AddSeconds(-1), time, d => d.DayOfWeek == days[0]),
            AlarmScheduleType.Custom when Schedule.CustomDays is { Length: > 0 } days
                => GetNextDayMatch(when.Date.AddDays(1).AddSeconds(-1), time, d => days.Contains(d.DayOfWeek)),
            AlarmScheduleType.Monthly when Schedule.DayOfMonth is int dom
                => NextMonthly(when.Date.AddDays(1).AddSeconds(-1), time, dom),
            _ => null,
        };
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
