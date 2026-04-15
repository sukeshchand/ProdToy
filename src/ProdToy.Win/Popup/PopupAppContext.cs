using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ProdToy;

class PopupAppContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly PopupForm _popupForm;
    private readonly DashboardForm _dashboardForm;
    private readonly CancellationTokenSource _cts = new();
    private Icon _appIcon;
    private SettingsForm? _settingsForm;
    private PluginHostImpl? _pluginHost;

    /// <summary>Envelope-mode constructor: no initial popup shown. The host
    /// boots normally, starts plugins, then dispatches the envelope through
    /// the pipe router on the UI thread. Used when `--command` is the first
    /// invocation of the day and there's no running host yet.</summary>
    public PopupAppContext(string envelopeCommand, string? envelopePayloadJson)
        : this("ProdToy", "", NotificationType.Info, startHidden: true)
    {
        if (string.IsNullOrEmpty(envelopeCommand)) return;
        var json = JsonSerializer.Serialize(new { command = envelopeCommand, payload = envelopePayloadJson });
        _popupForm.BeginInvoke(() => _pluginHost?.PipeRouter.TryDispatch(json));
    }

    public PopupAppContext(string initialTitle, string initialMessage, string initialType, bool startHidden = false)
    {
        var theme = Themes.LoadSaved();
        _appIcon = Themes.CreateAppIcon(theme.Primary);

        // Hook script is managed by the Claude Integration plugin's Start()/Stop()
        // No host-level hook management — plugins own their external integrations

        // Keep Windows "Apps & Features" DisplayVersion in sync after auto-updates.
        // Register/Unregister are ProdToySetup.exe's job.
        AppRegistry.SyncDisplayVersion();

        _popupForm = new PopupForm(theme);
        if (!startHidden)
            _popupForm.ShowPopup(initialTitle, initialMessage, initialType);

        // Create dashboard
        _dashboardForm = new DashboardForm(theme);
        _dashboardForm.ShowSettingsRequested += () => ShowSettingsForm();

        var trayMenu = BuildTrayMenu();

        _trayIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "ProdToy",
            Visible = true,
            ContextMenuStrip = trayMenu,
        };
        _trayIcon.DoubleClick += (_, _) => _dashboardForm.BringToForeground();

        // Initialize plugin system
        _pluginHost = new PluginHostImpl(_trayIcon, _popupForm);
        PluginManager.Initialize(_pluginHost);

        // Rebuild tray menu and dashboard when plugins change
        PluginManager.PluginsChanged += () =>
        {
            if (_popupForm.IsHandleCreated)
                _popupForm.Invoke(() =>
                {
                    _trayIcon.ContextMenuStrip = BuildTrayMenu();
                    _dashboardForm.BuildTiles();
                });
            else
            {
                _trayIcon.ContextMenuStrip = BuildTrayMenu();
                _dashboardForm.BuildTiles();
            }
        };

        Task.Run(() => PipeServerLoop(_cts.Token));

        // Exit when popup requests it (e.g. after update)
        _popupForm.ExitRequested += () => ExitApp();

        // Start update checker
        UpdateChecker.UpdateAvailable += metadata =>
        {
            _popupForm.ShowUpdateAvailable(metadata);
        };
        UpdateChecker.Start();

        // Start all loaded plugins
        PluginManager.StartAll();

        // Rebuild menus and dashboard now that plugins are loaded and started
        _trayIcon.ContextMenuStrip = BuildTrayMenu();
        _dashboardForm.BuildTiles();
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Dashboard", null, (_, _) => _dashboardForm.BringToForeground());

        // Plugin-contributed menu items grouped by plugin
        var groups = PluginManager.GetGroupedMenuItems();
        if (groups.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
            foreach (var (plugin, items) in groups)
            {
                if (items.Count == 1)
                {
                    // Single item — show directly with plugin name prefix
                    var item = items[0];
                    var mi = new ToolStripMenuItem($"{plugin.Name}: {item.Text}");
                    mi.Click += (_, _) => item.OnClick();
                    menu.Items.Add(mi);
                }
                else
                {
                    // Multiple items — create submenu under plugin name
                    var submenu = new ToolStripMenuItem(plugin.Name);
                    foreach (var item in items)
                    {
                        var mi = new ToolStripMenuItem(item.Text);
                        mi.Click += (_, _) => item.OnClick();
                        submenu.DropDownItems.Add(mi);
                    }
                    menu.Items.Add(submenu);
                }
            }
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings...", null, (_, _) => ShowSettingsForm());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        return menu;
    }

    private void ShowSettingsForm()
    {
        // Bring existing settings form to front if already open
        if (_settingsForm != null && !_settingsForm.IsDisposed)
        {
            _settingsForm.BringToFront();
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_popupForm.CurrentTheme);

        _settingsForm.ThemeChanged += theme =>
        {
            // Update tray icon
            _appIcon.Dispose();
            _appIcon = Themes.CreateAppIcon(theme.Primary);
            _trayIcon.Icon = _appIcon;

            // Apply to host forms
            _popupForm.ApplyTheme(theme);
            _dashboardForm.ApplyTheme(theme);

            // Broadcast to plugins (ChatPopupForm and any other IPluginPopup
            // instances subscribe to IPluginHost.ThemeChanged).
            _pluginHost?.RaiseThemeChanged(theme);
        };

        _settingsForm.GlobalFontChanged += fontFamily =>
        {
            _popupForm.ApplyGlobalFont(fontFamily);
        };

        _settingsForm.FormClosed += (_, _) => _settingsForm = null;

        _settingsForm.Show();
    }

    private static void ApplyFontToForm(Form form, string fontFamily)
    {
        try
        {
            form.SuspendLayout();
            var oldFont = form.Font;
            form.Font = new Font(fontFamily, form.Font.Size, form.Font.Style);
            if (oldFont != form.Font) oldFont.Dispose();
            ApplyFontRecursive(form, fontFamily);
            form.ResumeLayout();
            form.Invalidate(true);
        }
        catch { }
    }

    private static void ApplyFontRecursive(Control control, string fontFamily)
    {
        foreach (Control child in control.Controls)
        {
            try
            {
                var oldFont = child.Font;
                child.Font = new Font(fontFamily, child.Font.Size, child.Font.Style);
                // Only dispose if it's a different object (not inherited from parent)
                if (oldFont != child.Font && !oldFont.Equals(child.Parent?.Font))
                    oldFont.Dispose();
            }
            catch { }
            ApplyFontRecursive(child, fontFamily);
        }
    }

    private void ExitApp()
    {
        _cts.Cancel();
        PluginManager.StopAll();
        UpdateChecker.Stop();
        _settingsForm?.Close();
        _dashboardForm.Close();
        _dashboardForm.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _appIcon.Dispose();
        _popupForm.ForceExit();
        ExitThread();
    }

    private async Task PipeServerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    Program.PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                // Read with size limit to prevent unbounded memory allocation
                const int MaxMessageSize = 10 * 1024 * 1024; // 10MB
                using var reader = new StreamReader(server, Encoding.UTF8);
                var buffer = new char[MaxMessageSize];
                int charsRead = await reader.ReadAsync(buffer, 0, buffer.Length);
                var json = new string(buffer, 0, charsRead);

                if (!string.IsNullOrEmpty(json))
                {
                    // Phase 5: pipe is routed-only. Envelope shape is
                    // { "command": "...", "payload": "..." }; the host
                    // dispatches via PipeRouter. Plugins own the semantics
                    // of each command name they register.
                    _pluginHost?.PipeRouter.TryDispatch(json);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Debug.WriteLine($"Pipe server error: {ex.Message}");
                await Task.Delay(100, ct);
            }
        }
    }

}
