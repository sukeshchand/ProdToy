namespace ProdToy.Sdk;

/// <summary>
/// Lifecycle interface that every plugin must implement.
/// The host calls methods in order: Initialize → Start → (running) → Stop → Dispose.
/// </summary>
public interface IPlugin : IDisposable
{
    /// <summary>
    /// Called once after the plugin assembly is loaded.
    /// Wire up references only — do not show UI or start background work.
    /// </summary>
    void Initialize(IPluginContext context);

    /// <summary>
    /// Called after all plugins are initialized.
    /// Safe to start timers, register hotkeys, show forms.
    /// </summary>
    void Start();

    /// <summary>
    /// Called during shutdown or when the plugin is being disabled.
    /// Stop all background work, close forms, unregister hotkeys.
    /// </summary>
    void Stop();

    /// <summary>
    /// Menu items this plugin contributes to the tray context menu.
    /// Called after Initialize, before Start. May return an empty list.
    /// </summary>
    IReadOnlyList<MenuContribution> GetMenuItems();

    /// <summary>
    /// Settings page this plugin contributes to the Settings dialog.
    /// Return null if this plugin has no settings UI.
    /// </summary>
    SettingsPageContribution? GetSettingsPage();
}
