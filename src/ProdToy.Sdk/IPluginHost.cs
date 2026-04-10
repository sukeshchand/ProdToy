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

    /// <summary>Show the notification popup window (bring to foreground).</summary>
    void ShowNotificationPopup();

    /// <summary>Notify the host that show-quotes setting changed.</summary>
    void NotifyShowQuotesChanged(bool show);

    /// <summary>Notify the host that history-enabled setting changed.</summary>
    void NotifyHistoryEnabledChanged(bool enabled);

    /// <summary>Snooze notifications for 30 minutes.</summary>
    void Snooze();

    /// <summary>Cancel active snooze.</summary>
    void Unsnooze();

    /// <summary>Whether notifications are currently snoozed.</summary>
    bool IsSnoozed { get; }

    /// <summary>When the current snooze expires.</summary>
    DateTime SnoozeUntil { get; }

    /// <summary>Write/regenerate the Claude Code hook script to disk. Called by plugins that need it.</summary>
    void EnsureHookScript();
}
