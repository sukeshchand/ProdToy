using System.Diagnostics;

namespace ProdToy.Plugins.Alarm;

static class AlarmScheduler
{
    private static System.Threading.Timer? _timer;
    private static readonly HashSet<string> _firedKeys = new();
    private static readonly object _lock = new();
    private static bool _missedCheckDone;
    private static int _tickCount;
    private static Func<int> _getMissedGraceMinutes = () => 5;

    public static event Action<AlarmEntry>? AlarmTriggered;

    public static void Start(Func<int> getMissedGraceMinutes)
    {
        Stop();
        _getMissedGraceMinutes = getMissedGraceMinutes;
        _missedCheckDone = false;
        _tickCount = 0;
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        Debug.WriteLine("AlarmScheduler started");
    }

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        lock (_lock) { _firedKeys.Clear(); }
        Debug.WriteLine("AlarmScheduler stopped");
    }

    public static void Refresh()
    {
        AlarmStore.InvalidateAlarms();
    }

    public static void TestTrigger(AlarmEntry alarm)
    {
        AlarmTriggered?.Invoke(alarm);
    }

    private static void Tick()
    {
        try
        {
            var now = DateTime.Now;
            var alarms = AlarmStore.LoadAlarms();

            if (alarms.Count == 0)
            {
                if (!_missedCheckDone) _missedCheckDone = true;
                return;
            }

            bool hasActive = false;
            foreach (var alarm in alarms)
            {
                if (alarm.Status == AlarmStatus.Active) { hasActive = true; break; }
            }

            if (!hasActive)
            {
                if (!_missedCheckDone) _missedCheckDone = true;
                return;
            }

            foreach (var alarm in alarms)
            {
                if (alarm.Status != AlarmStatus.Active) continue;

                if (ShouldFire(alarm, now))
                {
                    var key = $"{alarm.Id}|{now:yyyy-MM-dd HH:mm}";
                    bool isNew;
                    lock (_lock) { isNew = _firedKeys.Add(key); }

                    if (isNew)
                    {
                        try
                        {
                            AlarmStore.AddHistoryEntry(new AlarmHistoryEntry
                            {
                                AlarmId = alarm.Id,
                                AlarmTitle = alarm.Title,
                                EventType = AlarmHistoryEventType.Triggered,
                                Detail = $"Scheduled: {alarm.Schedule.TimeOfDay}, Actual: {now:HH:mm:ss}",
                            });

                            AlarmStore.RecordTrigger(alarm.Id);
                            AlarmTriggered?.Invoke(alarm);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Alarm trigger failed for {alarm.Id}: {ex.Message}");
                            AlarmStore.AddHistoryEntry(new AlarmHistoryEntry
                            {
                                AlarmId = alarm.Id,
                                AlarmTitle = alarm.Title,
                                EventType = AlarmHistoryEventType.TriggerFailed,
                                Detail = ex.Message,
                            });
                        }
                    }
                }
            }

            CheckMissedAlarms(now, alarms);

            _tickCount++;
            if (_tickCount % 10 == 0)
                PruneFiredKeys(now);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AlarmScheduler tick error: {ex.Message}");
        }
    }

    private static bool ShouldFire(AlarmEntry alarm, DateTime now)
    {
        var time = alarm.Schedule.GetTimeOfDay();

        if (alarm.Schedule.Type == AlarmScheduleType.Interval)
        {
            if (alarm.Schedule.IntervalMinutes is int mins and > 0)
            {
                if (alarm.LastTriggeredAt is DateTime last)
                    return (now - last).TotalMinutes >= mins;
                return true;
            }
            return false;
        }

        var nowTime = now.TimeOfDay;
        bool timeMatch = nowTime.Hours == time.Hours && nowTime.Minutes == time.Minutes;
        if (!timeMatch) return false;

        if (alarm.EndDate != null && DateTime.TryParse(alarm.EndDate, out var end) && now.Date > end.Date)
            return false;

        return alarm.Schedule.Type switch
        {
            AlarmScheduleType.Once => alarm.Schedule.OneTimeDate != null
                && DateTime.TryParse(alarm.Schedule.OneTimeDate, out var d)
                && d.Date == now.Date,
            AlarmScheduleType.Daily => true,
            AlarmScheduleType.Weekdays => now.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday,
            AlarmScheduleType.Weekend => now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
            AlarmScheduleType.Weekly => alarm.Schedule.CustomDays is { Length: > 0 } days && now.DayOfWeek == days[0],
            AlarmScheduleType.Monthly => alarm.Schedule.DayOfMonth is int dom && now.Day == dom,
            AlarmScheduleType.Custom => alarm.Schedule.CustomDays is { Length: > 0 } days && days.Contains(now.DayOfWeek),
            _ => false,
        };
    }

    private static void CheckMissedAlarms(DateTime now, List<AlarmEntry> alarms)
    {
        if (_missedCheckDone) return;
        _missedCheckDone = true;

        int graceMinutes = _getMissedGraceMinutes();
        if (graceMinutes <= 0) return;

        foreach (var alarm in alarms)
        {
            if (alarm.Status != AlarmStatus.Active) continue;
            if (alarm.Schedule.Type == AlarmScheduleType.Interval) continue;

            var time = alarm.Schedule.GetTimeOfDay();
            var scheduledToday = now.Date + time;
            if (scheduledToday < now && (now - scheduledToday).TotalMinutes <= graceMinutes)
            {
                var key = $"{alarm.Id}|{scheduledToday:yyyy-MM-dd HH:mm}";
                bool isNew;
                lock (_lock) { isNew = _firedKeys.Add(key); }

                if (isNew && (alarm.LastTriggeredAt == null || alarm.LastTriggeredAt.Value.Date < now.Date
                    || alarm.LastTriggeredAt.Value.TimeOfDay < time.Subtract(TimeSpan.FromMinutes(1))))
                {
                    AlarmStore.AddHistoryEntry(new AlarmHistoryEntry
                    {
                        AlarmId = alarm.Id,
                        AlarmTitle = alarm.Title,
                        EventType = AlarmHistoryEventType.RestartRecovered,
                        Detail = $"Missed at {scheduledToday:HH:mm}, recovered within {graceMinutes}min grace period",
                    });

                    AlarmStore.RecordTrigger(alarm.Id);
                    AlarmTriggered?.Invoke(alarm);
                }
            }
        }
    }

    private static void PruneFiredKeys(DateTime now)
    {
        lock (_lock)
        {
            if (_firedKeys.Count == 0) return;
            _firedKeys.RemoveWhere(k =>
            {
                var sep = k.LastIndexOf('|');
                return sep > 0 && DateTime.TryParse(k[(sep + 1)..], out var dt) && (now - dt).TotalHours > 24;
            });
        }
    }
}
