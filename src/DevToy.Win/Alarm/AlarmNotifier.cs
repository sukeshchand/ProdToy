using System.Diagnostics;
using System.Media;

namespace DevToy;

static class AlarmNotifier
{
    private static Form? _marshalForm;
    private static NotifyIcon? _trayIcon;

    // Track snooze timers per alarm to prevent leaks and duplicates
    private static readonly Dictionary<string, System.Threading.Timer> _snoozeTimers = new();
    private static readonly object _snoozeLock = new();

    public static void Initialize(Form marshalForm, NotifyIcon trayIcon)
    {
        _marshalForm = marshalForm;
        _trayIcon = trayIcon;
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
        if (_marshalForm == null || _marshalForm.IsDisposed) return;

        try
        {
            _marshalForm.Invoke(() => ShowAlarm(alarm));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AlarmNotifier dispatch failed: {ex.Message}");
        }
    }

    private static void ShowAlarm(AlarmEntry alarm)
    {
        try
        {
            // Play sound first
            if (alarm.SoundEnabled && AppSettings.Load().AlarmSoundEnabled)
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
                    Debug.WriteLine($"Sound play failed: {ex.Message}");
                }
            }

            // Show popup
            if (alarm.Notification is AlarmNotificationMode.Popup or AlarmNotificationMode.Both)
            {
                var ringForm = new AlarmRingForm(alarm, Themes.LoadSaved());
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
                if (_trayIcon != null)
                {
                    string message = alarm.Message.Length > 200 ? alarm.Message[..197] + "..." : alarm.Message;
                    if (string.IsNullOrEmpty(message)) message = alarm.GetScheduleDescription();
                    _trayIcon.ShowBalloonTip(5000, alarm.Title, message, ToolTipIcon.Info);

                    AlarmStore.AddHistoryEntry(new AlarmHistoryEntry
                    {
                        AlarmId = alarm.Id,
                        AlarmTitle = alarm.Title,
                        EventType = AlarmHistoryEventType.NotificationShown,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AlarmNotifier.ShowAlarm failed: {ex.Message}");
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
            // Cancel any existing snooze for this alarm
            if (_snoozeTimers.TryGetValue(alarm.Id, out var existing))
            {
                existing.Dispose();
                _snoozeTimers.Remove(alarm.Id);
            }

            var timer = new System.Threading.Timer(_ =>
            {
                // Remove from tracking
                lock (_snoozeLock) { _snoozeTimers.Remove(alarm.Id); }

                if (_marshalForm != null && !_marshalForm.IsDisposed)
                {
                    try { _marshalForm.Invoke(() => ShowAlarm(alarm)); }
                    catch (Exception ex) { Debug.WriteLine($"Snooze re-trigger failed: {ex.Message}"); }
                }
            }, null, TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);

            _snoozeTimers[alarm.Id] = timer;
        }
    }
}
