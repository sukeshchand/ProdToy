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

    public static void HandleAlarmTriggered(AlarmEntry alarm)
    {
        if (_host == null) return;

        // Fire-and-forget on the thread pool so the scheduler's timer thread is
        // never blocked waiting on the UI thread. If the UI thread is busy (e.g.
        // WebView2 prewarm still running right after an app update), the Invoke
        // call queues but the scheduler proceeds to its next tick immediately.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                _host.InvokeOnUI(() => ShowAlarm(alarm));
            }
            catch (Exception ex)
            {
                PluginLog.Error("AlarmNotifier dispatch failed", ex);
            }
        });
    }

    private static void ShowAlarm(AlarmEntry alarm)
    {
        if (_host == null) return;

        try
        {
            // Play sound first
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
                catch (Exception ex)
                {
                    PluginLog.Warn($"Alarm sound play failed: {ex.Message}");
                }
            }

            // Show popup
            if (alarm.Notification is AlarmNotificationMode.Popup or AlarmNotificationMode.Both)
            {
                var ringForm = new AlarmRingForm(alarm, _host.CurrentTheme, _host.GlobalFont);
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
                ringForm.Show();

                AlarmStore.AddHistoryEntry(new AlarmHistoryEntry
                {
                    AlarmId = alarm.Id,
                    AlarmTitle = alarm.Title,
                    EventType = AlarmHistoryEventType.PopupShown,
                });
            }

            // Show Windows notification
            if (alarm.Notification is AlarmNotificationMode.Windows or AlarmNotificationMode.Both)
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
        }
        catch (Exception ex)
        {
            PluginLog.Error($"ShowAlarm failed for '{alarm.Title}'", ex);
            AlarmStore.AddHistoryEntry(new AlarmHistoryEntry
            {
                AlarmId = alarm.Id,
                AlarmTitle = alarm.Title,
                EventType = AlarmHistoryEventType.TriggerFailed,
                Detail = ex.Message,
            });
        }
    }

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
                    try { _host.InvokeOnUI(() => ShowAlarm(alarm)); }
                    catch (Exception ex) { PluginLog.Error("Snooze re-trigger failed", ex); }
                }
            }, null, TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);

            _snoozeTimers[alarm.Id] = timer;
        }
    }
}
