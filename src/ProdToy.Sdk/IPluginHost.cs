namespace ProdToy.Sdk;

/// <summary>
/// Host services available to every plugin.
/// </summary>
public interface IPluginHost
{
    /// <summary>Current theme colors. Plugins should use this for consistent UI.</summary>
    PluginTheme CurrentTheme { get; }

    /// <summary>Fires when the user changes the theme.</summary>
    event Action<PluginTheme>? ThemeChanged;

    /// <summary>Show a Windows balloon notification from the tray icon.</summary>
    void ShowBalloonNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info);

    /// <summary>Register a global hotkey. Returns a handle to unregister later, or null on failure.</summary>
    IHotkeyRegistration? RegisterHotkey(string hotkeyString, Action callback);

    /// <summary>Application root path: ~/.prod-toy/</summary>
    string AppRootPath { get; }

    /// <summary>Application version string.</summary>
    string AppVersion { get; }

    /// <summary>Invoke an action on the UI thread. Required for UI work from background threads.</summary>
    void InvokeOnUI(Action action);

    /// <summary>Asynchronously post an action to the UI thread (BeginInvoke-style)
    /// so it runs on the next message-pump iteration. Use this instead of
    /// <see cref="InvokeOnUI"/> when the caller is *already* on the UI thread
    /// but inside a nested message loop (e.g. a context-menu click handler)
    /// and needs the action to land in the outer top-level pump — typical
    /// for popup forms that misbehave when created inside a nested pump.</summary>
    void BeginInvokeOnUI(Action action);

    /// <summary>Enqueue a popup form for the host to create + Show on the
    /// always-alive dashboard's UI thread. The factory runs on that thread,
    /// so it may safely touch WinForms. The host:
    ///   • holds a strong reference to the returned form until it closes
    ///     (so the form is not GC'd mid-paint),
    ///   • calls <see cref="Form.Show"/> for you,
    ///   • posts via the dashboard's BeginInvoke so the call is decoupled
    ///     from any nested message pump on the caller's stack.
    /// Use this for any popup invoked from a non-UI thread (alarm timer,
    /// scheduled triggers) or from inside a nested pump (context menus).</summary>
    void QueuePopup(Func<Form> factory);

    /// <summary>Access to the system tray icon.</summary>
    NotifyIcon TrayIcon { get; }

    /// <summary>Current global font family name.</summary>
    string GlobalFont { get; }

    /// <summary>Fires when the global font changes.</summary>
    event Action<string>? GlobalFontChanged;

    /// <summary>Register a triple-Ctrl-tap callback. Returns a disposable handle.</summary>
    IDisposable? RegisterTripleCtrl(Action callback);

    /// <summary>Register a handler for pipe commands routed to this plugin. The host
    /// inspects the "command" field of incoming pipe payloads and dispatches to the
    /// matching handler on the UI thread. Returns a disposable that unregisters on dispose.</summary>
    IDisposable RegisterPipeHandler(string command, PipeCommandHandler handler);

    /// <summary>Register a plugin-owned popup window so the host can route
    /// lifecycle events (theme change, exit) into it. Returns a disposable
    /// that unregisters on dispose.</summary>
    IDisposable RegisterPopup(IPluginPopup popup);

    /// <summary>Resolved path the plugin can use as a WebView2 user-data folder.
    /// Each plugin gets a sibling folder under the host's shared temp root so
    /// multiple WebView2 controls in the same process don't collide.</summary>
    string GetWebView2UserDataFolder(string subDirName);
}
