using ProdToy.Sdk;

namespace ProdToy;

/// <summary>
/// Implements IPluginHost, wrapping existing host infrastructure for plugin consumption.
/// </summary>
sealed class PluginHostImpl : IPluginHost
{
    private readonly NotifyIcon _trayIcon;
    private readonly PopupForm _popupForm;
    private readonly PipeRouter _pipeRouter;
    private readonly List<IPluginPopup> _registeredPopups = new();

    public PluginHostImpl(NotifyIcon trayIcon, PopupForm popupForm)
    {
        _trayIcon = trayIcon;
        _popupForm = popupForm;
        _pipeRouter = new PipeRouter(InvokeOnUI);
    }

    public PluginTheme CurrentTheme => ToPluginTheme(Themes.LoadSaved());

    public event Action<PluginTheme>? ThemeChanged;
    public event Action<string>? GlobalFontChanged;

    public string AppRootPath => AppPaths.Root;
    public string AppVersion => ProdToy.AppVersion.Current;
    public string GlobalFont => AppSettings.Load().GlobalFont;
    public NotifyIcon TrayIcon => _trayIcon;

    internal PipeRouter PipeRouter => _pipeRouter;
    internal IReadOnlyList<IPluginPopup> RegisteredPopups => _registeredPopups;

    public void ShowBalloonNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        try
        {
            _trayIcon.ShowBalloonTip(3000, title, message, icon);
        }
        catch (Exception ex)
        {
            Log.Warn($"ShowBalloonNotification failed: {ex.Message}");
        }
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

    public IDisposable RegisterPipeHandler(string command, PipeCommandHandler handler)
        => _pipeRouter.Register(command, handler);

    public IDisposable RegisterPopup(IPluginPopup popup)
    {
        lock (_registeredPopups) _registeredPopups.Add(popup);
        return new PopupRegistration(this, popup);
    }

    public string GetWebView2UserDataFolder(string subDirName)
    {
        if (string.IsNullOrWhiteSpace(subDirName))
            throw new ArgumentException("subDirName must not be empty", nameof(subDirName));
        // Sibling of the host's own "ProdToy_<pid>" folder so two WebView2
        // environments in the same process can't collide on the lock file.
        return Path.Combine(Path.GetTempPath(), $"ProdToy_{Environment.ProcessId}_{subDirName}");
    }

    public IDisposable? RegisterTripleCtrl(Action callback)
    {
        var detector = new TripleCtrlDetector();
        detector.TripleTapped += callback;
        return detector;
    }

    private static PluginTheme ToPluginTheme(PopupTheme t) => new(
        t.Name, t.BgDark, t.BgHeader, t.Primary, t.PrimaryLight, t.PrimaryDim,
        t.TextPrimary, t.TextSecondary, t.Border, t.SuccessColor, t.ErrorColor,
        t.SuccessBg, t.ErrorBg);

    private sealed class HotkeyRegistrationImpl : IHotkeyRegistration
    {
        private readonly GlobalHotkey _hotkey;

        public HotkeyRegistrationImpl(GlobalHotkey hotkey) => _hotkey = hotkey;

        public void Unregister() => _hotkey.Unregister();

        public void Dispose() => _hotkey.Dispose();
    }

    private sealed class PopupRegistration : IDisposable
    {
        private readonly PluginHostImpl _owner;
        private readonly IPluginPopup _popup;
        private bool _disposed;

        public PopupRegistration(PluginHostImpl owner, IPluginPopup popup)
        {
            _owner = owner;
            _popup = popup;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_owner._registeredPopups) _owner._registeredPopups.Remove(_popup);
        }
    }
}
