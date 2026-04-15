using ProdToy.Sdk;

namespace ProdToy.Plugins.Alarm;

[Plugin("ProdToy.Plugin.Alarm", "Alarms", "1.0.288",
    Description = "Schedule recurring alarms with popup and sound notifications",
    Author = "ProdToy",
    MenuPriority = 200)]
public class AlarmPlugin : IPlugin
{
    private IPluginContext _context = null!;
    private AlarmForm? _alarmForm;
    private AlarmHistoryForm? _alarmHistoryForm;

    public void Install(IPluginContext context)
    {
        // No external-system state to install.
    }

    public void Uninstall(IPluginContext context)
    {
        // No external-system state to remove.
    }

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
            _alarmHistoryForm?.Close();
            _alarmHistoryForm = null;
        });
    }

    public void Dispose() { }

    public IReadOnlyList<MenuContribution> GetMenuItems() =>
    [
        new("Alarms...", ShowAlarmForm, Priority: 200, Icon: "\u23F0"),
        new("Alarm History", ShowAlarmHistory, Priority: 201, Icon: "\uD83D\uDCCB"),
    ];

    public IReadOnlyList<MenuContribution> GetDashboardItems() =>
    [
        new("Alarms", ShowAlarmForm, Priority: 200, Icon: "\u23F0"),
        new("Alarm History", ShowAlarmHistory, Priority: 201, Icon: "\uD83D\uDCCB"),
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

    private void ShowAlarmHistory()
    {
        if (_alarmHistoryForm != null && !_alarmHistoryForm.IsDisposed)
        {
            _alarmHistoryForm.BringToFront();
            _alarmHistoryForm.Activate();
            return;
        }

        var theme = _context.Host.CurrentTheme;
        _alarmHistoryForm = new AlarmHistoryForm(theme, null);
        _alarmHistoryForm.FormClosed += (_, _) => _alarmHistoryForm = null;
        _alarmHistoryForm.Show();
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
