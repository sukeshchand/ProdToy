using ProdToy.Sdk;

namespace ProdToy.Plugins.Alarm;

[Plugin("ProdToy.Alarm", "Alarms", "1.0.0",
    Description = "Schedule recurring alarms with popup and sound notifications",
    Author = "ProdToy",
    MenuPriority = 200)]
public class AlarmPlugin : IPlugin
{
    private IPluginContext _context = null!;
    private AlarmForm? _alarmForm;

    public void Initialize(IPluginContext context)
    {
        _context = context;

        // Initialize store with plugin data directory
        AlarmStore.Initialize(context.DataDirectory, () =>
        {
            var s = context.LoadSettings<AlarmPluginSettings>();
            return s.AlarmHistoryMaxEntries;
        });
    }

    public void Start()
    {
        var settings = _context.LoadSettings<AlarmPluginSettings>();
        if (!settings.AlarmsEnabled) return;

        AlarmNotifier.Initialize(_context.Host);
        AlarmScheduler.AlarmTriggered += AlarmNotifier.HandleAlarmTriggered;
        AlarmStore.StartHistoryFlush();
        AlarmScheduler.Start(() =>
        {
            var s = _context.LoadSettings<AlarmPluginSettings>();
            return s.AlarmMissedGraceMinutes;
        });
    }

    public void Stop()
    {
        AlarmScheduler.Stop();
        AlarmStore.StopHistoryFlush();
        AlarmNotifier.Cleanup();
        _context.Host.InvokeOnUI(() =>
        {
            _alarmForm?.Close();
            _alarmForm = null;
        });
    }

    public void Dispose() { }

    public IReadOnlyList<MenuContribution> GetMenuItems() =>
    [
        new("Alarms...", ShowAlarmForm, Priority: 200),
    ];

    public SettingsPageContribution? GetSettingsPage() => null;

    private void ShowAlarmForm()
    {
        if (_alarmForm != null && !_alarmForm.IsDisposed)
        {
            _alarmForm.BringToFront();
            _alarmForm.Activate();
            return;
        }

        var theme = _context.Host.CurrentTheme;
        _alarmForm = new AlarmForm(theme);
        _alarmForm.FormClosed += (_, _) => _alarmForm = null;
        var savedFont = _context.Host.GlobalFont;
        if (!string.IsNullOrEmpty(savedFont) && savedFont != "Segoe UI")
            ApplyFontToForm(_alarmForm, savedFont);
        _alarmForm.Show();
    }

    private static void ApplyFontToForm(Control form, string fontFamily)
    {
        try
        {
            var font = new Font(fontFamily, form.Font.Size, form.Font.Style);
            form.Font = font;
            foreach (Control c in form.Controls)
                ApplyFontRecursive(c, fontFamily);
        }
        catch { }
    }

    private static void ApplyFontRecursive(Control control, string fontFamily)
    {
        try
        {
            control.Font = new Font(fontFamily, control.Font.Size, control.Font.Style);
            foreach (Control c in control.Controls)
                ApplyFontRecursive(c, fontFamily);
        }
        catch { }
    }
}
