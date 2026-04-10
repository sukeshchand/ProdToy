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

    /// <summary>Access to the system tray icon.</summary>
    NotifyIcon TrayIcon { get; }

    /// <summary>Current global font family name.</summary>
    string GlobalFont { get; }

    /// <summary>Fires when the global font changes.</summary>
    event Action<string>? GlobalFontChanged;

    /// <summary>Register a triple-Ctrl-tap callback. Returns a disposable handle.</summary>
    IDisposable? RegisterTripleCtrl(Action callback);
}
