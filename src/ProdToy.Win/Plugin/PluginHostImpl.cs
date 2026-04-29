using ProdToy.Sdk;

namespace ProdToy;

/// <summary>
/// Implements IPluginHost, wrapping existing host infrastructure for plugin consumption.
/// </summary>
sealed class PluginHostImpl : IPluginHost
{
    private readonly NotifyIcon _trayIcon;
    private readonly PopupForm _popupForm;
    // The dashboard form is created once at startup and never disposed for
    // the app's lifetime, so it's a reliable always-alive UI marshaling
    // target. Its window handle is forced-created by the constructor so
    // BeginInvoke works even when the form has never been shown.
    private readonly DashboardForm _dashboardForm;
    private readonly PipeRouter _pipeRouter;
    private readonly List<IPluginPopup> _registeredPopups = new();

    // Strong references to popup forms enqueued via QueuePopup. Without
    // this set, the GC can collect the form between Show() returning and
    // the first WM_PAINT, leaving an un-painted shell that vanishes.
    private readonly HashSet<Form> _liveQueuedPopups = new();
    private readonly object _liveQueuedPopupsLock = new();

    public PluginHostImpl(NotifyIcon trayIcon, PopupForm popupForm, DashboardForm dashboardForm)
    {
        _trayIcon = trayIcon;
        _popupForm = popupForm;
        _dashboardForm = dashboardForm;
        // Force the dashboard's HWND to materialize even though the form
        // is hidden — required so BeginInvoke posts succeed before the
        // user has ever opened the dashboard.
        _ = _dashboardForm.Handle;
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
        // Dashboard form's handle is force-created in the ctor, so it's a
        // reliable always-alive marshaling target. The popup form's handle
        // doesn't exist until first display, which used to break this path.
        if (_dashboardForm.InvokeRequired)
            _dashboardForm.Invoke(action);
        else
            action();
    }

    public void BeginInvokeOnUI(Action action)
    {
        // Always go through BeginInvoke so the action runs on a fresh
        // message-pump iteration, never inline on the current call stack.
        _dashboardForm.BeginInvoke(action);
    }

    public void QueuePopup(Func<Form> factory)
    {
        // The factory + Show happens on the dashboard's UI thread, on a
        // fresh BeginInvoke message — that decouples it from the caller's
        // call stack (whether that's a threadpool timer or a context-menu
        // click handler running inside a nested modal pump). The host
        // owns the strong reference until FormClosed so GC can't reclaim
        // the form mid-paint.
        try
        {
            _dashboardForm.BeginInvoke(new Action(() =>
            {
                Form? form = null;
                try
                {
                    form = factory();
                    if (form == null)
                    {
                        Log.Warn("QueuePopup: factory returned null");
                        return;
                    }
                    lock (_liveQueuedPopupsLock) { _liveQueuedPopups.Add(form); }
                    var captured = form;
                    captured.FormClosed += (_, _) =>
                    {
                        lock (_liveQueuedPopupsLock) { _liveQueuedPopups.Remove(captured); }
                    };
                    captured.Show();
                }
                catch (Exception ex)
                {
                    Log.Error("QueuePopup factory/show failed", ex);
                    if (form != null)
                    {
                        lock (_liveQueuedPopupsLock) { _liveQueuedPopups.Remove(form); }
                        try { form.Dispose(); } catch { }
                    }
                }
            }));
        }
        catch (Exception ex)
        {
            Log.Error("QueuePopup BeginInvoke failed", ex);
        }
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
