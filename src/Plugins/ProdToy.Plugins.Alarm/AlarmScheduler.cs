using Microsoft.Win32;
using ProdToy.Sdk;

namespace ProdToy.Plugins.Alarm;

static class AlarmScheduler
{
    private static System.Threading.Timer? _timer;
    private static readonly HashSet<string> _firedKeys = new();
    private static readonly object _lock = new();
    private static int _tickCount;
    private static Func<int> _getMissedGraceMinutes = () => 5;
    private static PowerModeChangedEventHandler? _powerHandler;

    public static event Action<AlarmEntry>? AlarmTriggered;

    public static void Start(Func<int> getMissedGraceMinutes)
    {
        Stop();
        _getMissedGraceMinutes = getMissedGraceMinutes;
        _tickCount = 0;
        // 1-second tick. Cheap (a few microseconds per tick scanning a
        // list of alarms in memory) and gives sub-second fire latency
        // for every alarm. The minute-precision time match plus
        // _firedKeys dedupe ensures each alarm fires once per scheduled
        // minute despite the high tick rate.
        _timer = new System.Threading.Timer(_ => Tick(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        // Subscribe to alarm-set changes so add/edit/delete triggers an
        // immediate tick. Without this, an alarm saved for 30 seconds in
        // the future could miss its first scheduled fire if we happened
        // to be between ticks at the moment.
        AlarmStore.Changed += OnAlarmsChanged;

        // Re-check immediately when the machine resumes from sleep — without
        // this, an alarm scheduled while suspended only gets recovered if the
        // resume happens within the missed-grace window of the next 30s tick.
        _powerHandler = (_, e) =>
        {
            if (e.Mode == PowerModes.Resume)
            {
                PluginLog.Info("AlarmScheduler: power resume — running immediate tick");
                Tick();
            }
        };
        try { SystemEvents.PowerModeChanged += _powerHandler; }
        catch (Exception ex) { PluginLog.Warn($"PowerModeChanged subscription failed: {ex.Message}"); }

        PluginLog.Info("AlarmScheduler started (1s ticks, missed-grace=" + getMissedGraceMinutes() + "min)");
    }

    /// <summary>AlarmStore.Changed handler — runs an immediate tick so a
    /// freshly-saved alarm whose schedule is right now (or moments away)
    /// fires without waiting for the next periodic tick.</summary>
    private static void OnAlarmsChanged()
    {
        try { Tick(); }
        catch (Exception ex) { PluginLog.Error("Immediate tick after alarm change failed", ex); }
    }

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        if (_powerHandler != null)
        {
            try { SystemEvents.PowerModeChanged -= _powerHandler; }
            catch { }
            _powerHandler = null;
        }
        try { AlarmStore.Changed -= OnAlarmsChanged; } catch { }
        lock (_lock) { _firedKeys.Clear(); }
        PluginLog.Info("AlarmScheduler stopped");
    }

    public static void Refresh()
    {
        AlarmStore.InvalidateAlarms();
    }

    public static void TestTrigger(AlarmEntry alarm)
    {
        try
        {
            AlarmTriggered?.Invoke(alarm);
        }
        catch (Exception ex)
        {
            PluginLog.Error("AlarmScheduler.TestTrigger raised", ex);
        }
    }

    private static void Tick()
    {
        try
        {
            var now = DateTime.Now;
            var alarms = AlarmStore.LoadAlarms();

            if (alarms.Count == 0)
            {
                _tickCount++;
                return;
            }

            int activeCount = 0;
            foreach (var a in alarms)
                if (a.Status == AlarmStatus.Active) activeCount++;

            if (activeCount == 0)
            {
                _tickCount++;
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
                            PluginLog.Info($"AlarmScheduler firing '{alarm.Title}' (scheduled {alarm.Schedule.TimeOfDay}, now {now:HH:mm:ss})");
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
            // Prune the fire-key set every 60 ticks (~1 min).
            if (_tickCount % 60 == 0)
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

        // Persistent dedupe across process restarts: if the alarm has
        // already triggered within the current minute (or within the last
        // 30 seconds for an interval alarm) skip it. Without this, a
        // process restart inside the firing minute can produce a second
        // fire because the in-memory _firedKeys set was cleared.
        if (alarm.LastTriggeredAt is DateTime lastFire)
        {
            if (alarm.Schedule.Type == AlarmScheduleType.Interval)
            {
                if ((now - lastFire).TotalSeconds < 30) return false;
            }
            else if (lastFire.Year == now.Year && lastFire.Month == now.Month
                  && lastFire.Day == now.Day
                  && lastFire.Hour == now.Hour
                  && lastFire.Minute == now.Minute)
            {
                return false;
            }
        }

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

    /// <summary>Look for active alarms whose scheduled time today fell
    /// inside the missed-grace window (default 5 min) and that haven't
    /// already triggered. Run on every tick so a sleep/resume that crosses
    /// the alarm time still fires the alarm on resume — previously this
    /// only ran once per process and missed those.</summary>
    private static int CheckMissedAlarms(DateTime now, List<AlarmEntry> alarms)
    {
        int graceMinutes = _getMissedGraceMinutes();
        if (graceMinutes <= 0) return 0;
        int recovered = 0;

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
                    PluginLog.Info($"AlarmScheduler recovered missed '{alarm.Title}' (scheduled {scheduledToday:HH:mm}, now {now:HH:mm:ss})");
                    AlarmStore.AddHistoryEntry(new AlarmHistoryEntry
                    {
                        AlarmId = alarm.Id,
                        AlarmTitle = alarm.Title,
                        EventType = AlarmHistoryEventType.RestartRecovered,
                        Detail = $"Missed at {scheduledToday:HH:mm}, recovered within {graceMinutes}min grace period",
                    });

                    AlarmStore.RecordTrigger(alarm.Id);
                    AlarmTriggered?.Invoke(alarm);
                    recovered++;
                }
            }
        }
        return recovered;
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
