using ProdToy.Sdk;

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
        // First tick deferred to 20s to let host startup settle (tray icon, plugin
        // loads, WebView2 prewarm in the Claude plugin, etc.). Firing at 5s used
        // to paint the popup while the UI thread was still busy, producing the
        // "Alarm (Not Responding)" window seen right after an update.
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30));
        PluginLog.Info("AlarmScheduler started");
    }

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        lock (_lock) { _firedKeys.Clear(); }
        PluginLog.Info("AlarmScheduler stopped");
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
                        if (alarm.SkipNextOccurrence)
                        {
                            AlarmStore.UpdateAlarm(alarm with { SkipNextOccurrence = false });
                            AlarmStore.AddHistoryEntry(new AlarmHistoryEntry
                            {
                                AlarmId = alarm.Id,
                                AlarmTitle = alarm.Title,
                                EventType = AlarmHistoryEventType.Skipped,
                                Detail = $"Skipped scheduled fire at {now:HH:mm:ss}",
                            });
                            continue;
                        }

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
                            PluginLog.Error($"Alarm trigger failed for '{alarm.Title}' ({alarm.Id})", ex);
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
            PluginLog.Error("AlarmScheduler tick error", ex);
        }
    }

    private static bool ShouldFire(AlarmEntry alarm, DateTime now)
    {
        if (alarm.PausedUntil is DateTime pu && pu > now) return false;

        if (alarm.StartDate != null && DateTime.TryParse(alarm.StartDate, out var sd) && now.Date < sd.Date)
            return false;

        if (alarm.ExceptionDates is { Length: > 0 } exDates)
        {
            foreach (var s in exDates)
                if (DateTime.TryParse(s, out var ex) && ex.Date == now.Date) return false;
        }

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
