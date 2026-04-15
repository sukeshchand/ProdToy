namespace ProdToy.Sdk;

/// <summary>
/// Lifecycle interface that every plugin must implement.
///
/// <para>One-time lifecycle (install/uninstall):</para>
/// <list type="bullet">
///   <item><description><b>Install()</b> — called exactly once when the plugin is first installed
///     (by the plugin store or as part of a fresh host install that bundles this plugin).
///     This is the right place to write external-system state: hook-script files, settings
///     entries in third-party config files, registry keys, scheduled tasks, desktop shortcuts.</description></item>
///   <item><description><b>Uninstall()</b> — called exactly once when the plugin is being removed
///     via the plugin store. Must undo everything Install() did. After Uninstall(), the system
///     should be as if the plugin was never installed.</description></item>
/// </list>
///
/// <para>Per-run lifecycle (start/stop):</para>
/// <list type="bullet">
///   <item><description><b>Initialize(context)</b> — called once after the plugin assembly is
///     loaded. Wire up references only — do not show UI or start background work.</description></item>
///   <item><description><b>Start()</b> — called after all plugins are initialized, and again on
///     every host restart. Register hotkeys, start timers, open background watchers. Does NOT
///     touch external-system state (that's Install's job).</description></item>
///   <item><description><b>Stop()</b> — called during host shutdown. Unregister hotkeys, stop
///     timers, close forms. Does NOT remove external-system state.</description></item>
///   <item><description><b>Dispose()</b> — called after Stop() during host shutdown.</description></item>
/// </list>
/// </summary>
public interface IPlugin : IDisposable
{
    /// <summary>
    /// Called exactly once when the plugin is first installed on this machine.
    /// Write external-system state here (hook scripts, third-party config entries,
    /// registry keys). Idempotent — running twice should be safe, but the host
    /// guarantees it only runs once per install.
    /// </summary>
    void Install(IPluginContext context);

    /// <summary>
    /// Called exactly once when the plugin is being removed via the plugin store.
    /// Undo everything <see cref="Install"/> did. After Uninstall(), no trace of
    /// this plugin should remain in any external system.
    /// </summary>
    void Uninstall(IPluginContext context);

    /// <summary>
    /// Called once after the plugin assembly is loaded.
    /// Wire up references only — do not show UI or start background work.
    /// </summary>
    void Initialize(IPluginContext context);

    /// <summary>
    /// Called after all plugins are initialized, and on every host restart.
    /// Register hotkeys, start timers, open background watchers. Does NOT
    /// touch external-system state (that's Install's job).
    /// </summary>
    void Start();

    /// <summary>
    /// Called during host shutdown. Unregister hotkeys, stop timers, close
    /// forms. Does NOT remove external-system state (that's Uninstall's job).
    /// </summary>
    void Stop();

    /// <summary>
    /// Items for the tray right-click context menu. Quick actions.
    /// </summary>
    IReadOnlyList<MenuContribution> GetMenuItems();

    /// <summary>
    /// Items for the dashboard tile grid. Main plugin actions shown on the home screen.
    /// Return empty list if this plugin has no dashboard presence.
    /// </summary>
    IReadOnlyList<MenuContribution> GetDashboardItems();

    /// <summary>
    /// Settings page this plugin contributes to the Settings dialog.
    /// Return null if this plugin has no settings UI.
    /// </summary>
    SettingsPageContribution? GetSettingsPage();
}
