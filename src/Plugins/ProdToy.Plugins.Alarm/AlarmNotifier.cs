using System.Media;
using ProdToy.Sdk;

namespace ProdToy.Plugins.Alarm;

static class AlarmNotifier
{
    private static IPluginHost? _host;
    private static readonly Dictionary<string, System.Threading.Timer> _snoozeTimers = new();
    private static readonly object _snoozeLock = new();

    public static void Initialize(IPluginHost host)
    {
        _host = host;
        PluginLog.Info("AlarmNotifier.Initialize: host wired");
    }

    public static void Cleanup()
    {
        lock (_snoozeLock)
        {
            foreach (var timer in _snoozeTimers.Values)
                timer.Dispose();
            _snoozeTimers.Clear();
        }
    }

    /// <summary>Entry point invoked by AlarmScheduler.AlarmTriggered. Plays
    /// sound and balloon synchronously (both are thread-safe), then enqueues
    /// the popup form via the host's QueuePopup pipeline. The host owns
    /// strong-ref tracking and dashboard-thread marshaling so the popup
    /// can't be GC'd mid-paint and isn't subject to nested-pump issues.</summary>
    public static void HandleAlarmTriggered(AlarmEntry alarm)
    {
        PluginLog.Info($"AlarmNotifier.HandleAlarmTriggered '{alarm.Title}' (host={(_host == null ? "null" : "set")})");
        if (_host == null)
        {
            PluginLog.Error("AlarmNotifier: _host is null — popup pipeline cannot run.");
            return;
        }

        if (alarm.SoundEnabled)
        {
            try
            {
                SystemSounds.Exclamation.Play();
                AlarmStore.AddHistoryEntry(new AlarmHistoryEntry
                {
                    AlarmId = alarm.Id,
                    AlarmTitle = alarm.Title,
                    EventType = AlarmHistoryEventType.SoundPlayed,
                });
            }
            catch (Exception ex) { PluginLog.Warn($"Alarm sound play failed: {ex.Message}"); }
        }

        if (alarm.Notification is AlarmNotificationMode.Windows or AlarmNotificationMode.Both)
        {
            try
            {
                string message = alarm.Message.Length > 200 ? alarm.Message[..197] + "..." : alarm.Message;
                if (string.IsNullOrEmpty(message)) message = alarm.GetScheduleDescription();
                _host.ShowBalloonNotification(alarm.Title, message);
                AlarmStore.AddHistoryEntry(new AlarmHistoryEntry
                {
                    AlarmId = alarm.Id,
                    AlarmTitle = alarm.Title,
                    EventType = AlarmHistoryEventType.NotificationShown,
                });
            }
            catch (Exception ex) { PluginLog.Warn($"Alarm balloon failed: {ex.Message}"); }
        }

        if (alarm.Notification is AlarmNotificationMode.Popup or AlarmNotificationMode.Both)
        {
            PluginLog.Info($"AlarmNotifier: queueing popup for '{alarm.Title}'");
            try
            {
                _host.QueuePopup(() =>
                {
                    PluginLog.Info($"AlarmNotifier: factory building ring form for '{alarm.Title}'");
                    var ringForm = new AlarmRingForm(alarm, _host!.CurrentTheme, _host.GlobalFont);
                    ringForm.Dismissed += () =>
                    {
                        AlarmStore.AddHistoryEntry(new AlarmHistoryEntry
                        {
                            AlarmId = alarm.Id,
                            AlarmTitle = alarm.Title,
                            EventType = AlarmHistoryEventType.Dismissed,
                        });
                    };
                    ringForm.Snoozed += minutes =>
                    {
                        AlarmStore.AddHistoryEntry(new AlarmHistoryEntry
                        {
                            AlarmId = alarm.Id,
                            AlarmTitle = alarm.Title,
                            EventType = AlarmHistoryEventType.Snoozed,
                            Detail = $"Snoozed for {minutes} minutes",
                        });
                        ScheduleSnooze(alarm, minutes);
                    };
                    ringForm.Shown += (_, _) =>
                    {
                        PluginLog.Info($"AlarmNotifier: ring form Shown event for '{alarm.Title}'");
                        AlarmStore.AddHistoryEntry(new AlarmHistoryEntry
                        {
                            AlarmId = alarm.Id,
                            AlarmTitle = alarm.Title,
                            EventType = AlarmHistoryEventType.PopupShown,
                        });
                    };
                    ringForm.FormClosed += (_, _) =>
                        PluginLog.Info($"AlarmNotifier: ring form closed for '{alarm.Title}'");
                    return ringForm;
                });
            }
            catch (Exception ex)
            {
                PluginLog.Error($"AlarmNotifier.QueuePopup failed for '{alarm.Title}'", ex);
            }
        }
    }

    /// <summary>Schedule a snooze re-fire. After the given minutes, re-runs
    /// the full HandleAlarmTriggered path so the popup goes through the
    /// same QueuePopup pipeline as the first display.</summary>
    private static void ScheduleSnooze(AlarmEntry alarm, int minutes)
    {
        lock (_snoozeLock)
        {
            if (_snoozeTimers.TryGetValue(alarm.Id, out var existing))
            {
                existing.Dispose();
                _snoozeTimers.Remove(alarm.Id);
            }

            try
            {
                var current = AlarmStore.GetAlarm(alarm.Id);
                if (current != null)
                    AlarmStore.UpdateAlarm(current with { SnoozedUntil = DateTime.Now.AddMinutes(minutes) });
            }
            catch (Exception ex) { PluginLog.Warn($"Snooze persist failed: {ex.Message}"); }

            var timer = new System.Threading.Timer(_ =>
            {
                lock (_snoozeLock) { _snoozeTimers.Remove(alarm.Id); }

                try
                {
                    var current = AlarmStore.GetAlarm(alarm.Id);
                    if (current != null)
                        AlarmStore.UpdateAlarm(current with { SnoozedUntil = null });
                }
                catch (Exception ex) { PluginLog.Warn($"Snooze clear failed: {ex.Message}"); }

                if (_host != null)
                {
                    try { HandleAlarmTriggered(alarm); }
                    catch (Exception ex) { PluginLog.Error("Snooze re-trigger failed", ex); }
                }
            }, null, TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);

            _snoozeTimers[alarm.Id] = timer;
        }
    }
}
