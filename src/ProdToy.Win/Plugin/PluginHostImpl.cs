using ProdToy.Sdk;

namespace ProdToy;

/// <summary>
/// Implements IPluginHost, wrapping existing host infrastructure for plugin consumption.
/// </summary>
sealed class PluginHostImpl : IPluginHost
{
    private readonly NotifyIcon _trayIcon;
    private readonly PopupForm _popupForm;

    public PluginHostImpl(NotifyIcon trayIcon, PopupForm popupForm)
    {
        _trayIcon = trayIcon;
        _popupForm = popupForm;
    }

    public PluginTheme CurrentTheme => ToPluginTheme(Themes.LoadSaved());

    public event Action<PluginTheme>? ThemeChanged;
    public event Action<string>? GlobalFontChanged;

    public string AppRootPath => AppPaths.Root;
    public string AppVersion => ProdToy.AppVersion.Current;
    public string GlobalFont => AppSettings.Load().GlobalFont;
    public NotifyIcon TrayIcon => _trayIcon;

    public void ShowBalloonNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _trayIcon.ShowBalloonTip(3000, title, message, icon);
    }

    public IHotkeyRegistration? RegisterHotkey(string hotkeyString, Action callback)
    {
        var hotkey = new GlobalHotkey();
        hotkey.HotkeyPressed += callback;
        if (hotkey.Register(hotkeyString))
            return new HotkeyRegistrationImpl(hotkey);

        hotkey.Dispose();
        return null;
    }

    public void InvokeOnUI(Action action)
    {
        if (_popupForm.InvokeRequired)
            _popupForm.Invoke(action);
        else
            action();
    }

    internal void RaiseThemeChanged(PopupTheme theme)
    {
        ThemeChanged?.Invoke(ToPluginTheme(theme));
    }

    internal void RaiseGlobalFontChanged(string fontFamily)
    {
        GlobalFontChanged?.Invoke(fontFamily);
    }

    public void ShowNotificationPopup() => _popupForm.BringToForeground();

    public void NotifyShowQuotesChanged(bool show) => _popupForm.SetShowQuotes(show);

    public void NotifyHistoryEnabledChanged(bool enabled) => _popupForm.UpdateHistoryNav();

    public void Snooze() => _popupForm.Snooze();

    public void Unsnooze() => _popupForm.Unsnooze();

    public bool IsSnoozed => _popupForm.IsSnoozed;

    public DateTime SnoozeUntil => _popupForm.SnoozeUntil;

    public void EnsureHookScript() => Updater.EnsureHookScript(Application.ExecutablePath);

    public IDisposable? RegisterTripleCtrl(Action callback)
    {
        var detector = new TripleCtrlDetector();
        detector.TripleTapped += callback;
        return detector;
    }

    private static PluginTheme ToPluginTheme(PopupTheme t) => new(
        t.Name, t.BgDark, t.BgHeader, t.Primary, t.PrimaryLight, t.PrimaryDim,
        t.TextPrimary, t.TextSecondary, t.Border, t.SuccessColor, t.ErrorColor);

    private sealed class HotkeyRegistrationImpl : IHotkeyRegistration
    {
        private readonly GlobalHotkey _hotkey;

        public HotkeyRegistrationImpl(GlobalHotkey hotkey) => _hotkey = hotkey;

        public void Unregister() => _hotkey.Unregister();

        public void Dispose() => _hotkey.Dispose();
    }
}
