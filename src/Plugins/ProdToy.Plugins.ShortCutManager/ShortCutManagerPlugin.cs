using System.Drawing;
using System.Text.Json;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

[Plugin("ProdToy.Plugin.ShortCutManager", "Shortcuts", "1.0.415",
    Description = "Folder-organized launcher for project shortcuts — Claude CLI, npm, dotnet, custom commands",
    Author = "ProdToy",
    MenuPriority = 250)]
public partial class ShortCutManagerPlugin : IPlugin, IDoctor
{
    private IPluginContext _context = null!;
    private ShortcutsForm? _shortcutsForm;
    private IDisposable? _contextLaunchPipeHandler;
    private IDisposable? _idLaunchPipeHandler;
    private Action? _shortcutsChangedHandler;

    public void Install(IPluginContext context)
    {
        // No external-system state to install.
    }

    public void Uninstall(IPluginContext context)
    {
        // Clean up any shell-integration surfaces we wrote so an uninstall
        // doesn't leave dangling registry entries or desktop .lnks pointing
        // at a now-deleted ProdToy.exe.
        try { ExplorerContextMenuRegistrar.UnregisterAll(); } catch { }
        try
        {
            DesktopShortcutSync.Initialize(context.DataDirectory);
            DesktopShortcutSync.Cleanup();
        }
        catch { }
    }

    public void Initialize(IPluginContext context)
    {
        _context = context;
        PluginLog.Bootstrap(context);

        // If shortcut data was previously saved under the ClaudeIntegration
        // plugin (before shortcuts were extracted into this standalone
        // plugin), carry it across on first run so users don't lose their
        // folders, recycled entries, or WT profiles.
        TryMigrateFromClaudeIntegration(context);

        ShortcutStore.Initialize(context.DataDirectory);
        ShortcutFolders.Initialize(context.DataDirectory);
        ShortcutsRecycleBin.Initialize(context.DataDirectory);
        OwnedWtProfilesStore.Initialize(context.DataDirectory);
        OwnedWtSchemesStore.Initialize(context.DataDirectory);
        DesktopShortcutSync.Initialize(context.DataDirectory);

        // Two pipe handlers:
        //   shortcuts.context-launch — Explorer right-click invocation;
        //     payload {"cwd":"..."} routes to the matching shortcut(s).
        //   shortcuts.launch — desktop .lnk invocation;
        //     payload {"id":"..."} launches that specific shortcut.
        _contextLaunchPipeHandler = context.Host.RegisterPipeHandler("shortcuts.context-launch", OnContextLaunchPipeCommand);
        _idLaunchPipeHandler = context.Host.RegisterPipeHandler("shortcuts.launch", OnIdLaunchPipeCommand);

        // Refresh registry entries whenever shortcuts change so the menu
        // stays in sync with renames, working-directory edits, etc.
        _shortcutsChangedHandler = RefreshContextMenu;
        ShortcutStore.Changed += _shortcutsChangedHandler;
    }

    public void Start()
    {
        _context.Log("Shortcuts plugin started");
        // Apply the saved feature toggle on startup so the registry reflects
        // current shortcuts (newly installed plugin, or shortcuts modified
        // while the host was off).
        RefreshContextMenu();
    }

    public void Stop()
    {
        if (_shortcutsChangedHandler != null)
        {
            ShortcutStore.Changed -= _shortcutsChangedHandler;
            _shortcutsChangedHandler = null;
        }
        _contextLaunchPipeHandler?.Dispose();
        _contextLaunchPipeHandler = null;
        _idLaunchPipeHandler?.Dispose();
        _idLaunchPipeHandler = null;

        _context.Host.InvokeOnUI(() =>
        {
            try { _shortcutsForm?.Close(); } catch { }
            _shortcutsForm = null;
        });
    }

    public void Dispose() { }

    public IReadOnlyList<MenuContribution> GetMenuItems() =>
    [
        new("Shortcuts…", ShowShortcutsForm, Priority: 250, Icon: "🚀"),
    ];

    public IReadOnlyList<MenuContribution> GetDashboardItems() =>
    [
        new("Shortcuts", ShowShortcutsForm, Priority: 250, Icon: "🚀"),
    ];

    public SettingsPageContribution? GetSettingsPage() => null;

    // Per-shortcut explorer integration: each shortcut has its own
    // ShowInExplorerContextMenu flag set in the edit form. There's no
    // global toggle — ExplorerContextMenuRegistrar.RegisterAll simply
    // skips shortcuts where the flag is off, so the registry mirrors
    // per-shortcut intent automatically.

    /// <summary>Pipe handler for the Explorer context-menu invocation.
    /// Payload shape: <c>{"cwd":"&lt;current-folder&gt;"}</c>. Filters
    /// shortcuts to those that opted in AND whose
    /// <see cref="Shortcut.WorkingDirectory"/> matches the folder. Zero
    /// matches → tray balloon; one → launch; many → modal picker.</summary>
    private void OnContextLaunchPipeCommand(PipeCommand cmd)
    {
        try
        {
            string? cwd = null;
            if (!string.IsNullOrEmpty(cmd.PayloadJson))
            {
                using var doc = JsonDocument.Parse(cmd.PayloadJson);
                if (doc.RootElement.TryGetProperty("cwd", out var cwdEl)
                    && cwdEl.ValueKind == JsonValueKind.String)
                {
                    cwd = cwdEl.GetString();
                }
            }
            if (string.IsNullOrWhiteSpace(cwd))
            {
                _context.Log("shortcuts.context-launch: payload missing 'cwd'");
                return;
            }

            string normalizedCwd = ExplorerContextMenuRegistrar.NormalizeFolderPath(cwd);
            var matches = ShortcutStore.Load()
                .Where(s => s.ShowInExplorerContextMenu)
                .Where(s => !string.IsNullOrWhiteSpace(s.WorkingDirectory))
                .Where(s => string.Equals(
                    ExplorerContextMenuRegistrar.NormalizeFolderPath(s.WorkingDirectory),
                    normalizedCwd,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                ShowNoMatchBalloon(normalizedCwd);
                return;
            }

            if (matches.Count == 1)
            {
                var result = ShortcutLauncher.Launch(matches[0]);
                if (!result.Ok)
                    _context.Log($"shortcuts.context-launch: '{matches[0].Name}' failed — {result.ErrorMessage}");
                return;
            }

            // Multiple matches — show picker on UI thread (handler is
            // already on UI thread per RegisterPipeHandler contract).
            using var picker = new ContextMenuShortcutPicker(matches, _context.Host.CurrentTheme, normalizedCwd);
            if (picker.ShowDialog() == DialogResult.OK && picker.Selected != null)
            {
                var result = ShortcutLauncher.Launch(picker.Selected);
                if (!result.Ok)
                    _context.Log($"shortcuts.context-launch: '{picker.Selected.Name}' failed — {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            _context.LogError("shortcuts.context-launch handler crashed", ex);
        }
    }

    private void ShowNoMatchBalloon(string folder)
    {
        try
        {
            _context.Host.TrayIcon.ShowBalloonTip(
                3000,
                "ProdToy",
                $"No shortcut configured for:\n{folder}",
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _context.LogError("Failed to show no-match balloon", ex);
        }
    }

    /// <summary>Re-syncs both shell-integration surfaces — the Explorer
    /// background-context-menu verb and the per-shortcut desktop .lnks —
    /// with the current shortcut set. Each sync only touches entries our
    /// own flags own, so this is safe to call after every Add/Update/Delete.</summary>
    private void RefreshContextMenu()
    {
        var shortcuts = ShortcutStore.Load();
        var hostExe = ExplorerContextMenuRegistrar.ResolveHostExePath();
        try
        {
            ExplorerContextMenuRegistrar.RegisterAll(shortcuts, hostExe);
        }
        catch (Exception ex)
        {
            _context.LogError("Refresh Explorer context menu failed", ex);
        }
        try
        {
            DesktopShortcutSync.Sync(shortcuts, hostExe);
        }
        catch (Exception ex)
        {
            _context.LogError("Refresh desktop shortcuts failed", ex);
        }
    }

    /// <summary>Pipe handler for desktop-.lnk invocations. Payload shape
    /// <c>{"id":"&lt;shortcut-id&gt;"}</c>. Looks up the shortcut and
    /// launches it; logs and returns quietly if the id is unknown so a
    /// stale .lnk doesn't pop a dialog.</summary>
    private void OnIdLaunchPipeCommand(PipeCommand cmd)
    {
        try
        {
            if (string.IsNullOrEmpty(cmd.PayloadJson))
            {
                _context.Log("shortcuts.launch: empty payload");
                return;
            }
            string? id = null;
            using (var doc = JsonDocument.Parse(cmd.PayloadJson))
            {
                if (doc.RootElement.TryGetProperty("id", out var idEl)
                    && idEl.ValueKind == JsonValueKind.String)
                {
                    id = idEl.GetString();
                }
            }
            if (string.IsNullOrEmpty(id))
            {
                _context.Log("shortcuts.launch: payload missing 'id'");
                return;
            }

            var shortcut = ShortcutStore.Get(id);
            if (shortcut == null)
            {
                _context.Log($"shortcuts.launch: unknown id '{id}' (stale .lnk?)");
                return;
            }

            var result = ShortcutLauncher.Launch(shortcut);
            if (!result.Ok)
                _context.Log($"shortcuts.launch: '{shortcut.Name}' failed — {result.ErrorMessage}");
        }
        catch (Exception ex)
        {
            _context.LogError("shortcuts.launch handler crashed", ex);
        }
    }

    private void ShowShortcutsForm()
    {
        _context.Host.InvokeOnUI(() =>
        {
            if (_shortcutsForm != null && !_shortcutsForm.IsDisposed)
            {
                _shortcutsForm.BringToFront();
                _shortcutsForm.Activate();
                return;
            }
            var theme = _context.Host.CurrentTheme;
            _shortcutsForm = new ShortcutsForm(theme);
            _shortcutsForm.FormClosed += (_, _) => _shortcutsForm = null;
            _shortcutsForm.Show();
        });
    }

    /// <summary>
    /// One-time migration: copy shortcuts.json, shortcut-folders.json,
    /// shortcut-recycled.json and owned-wt-profiles.json from the old
    /// ClaudeIntegration plugin data dir into this plugin's data dir, but
    /// only if this dir doesn't already have those files (i.e. first run).
    /// Quiet on any filesystem error — the feature still works with a fresh
    /// empty state, migration is only a convenience.
    /// </summary>
    private void TryMigrateFromClaudeIntegration(IPluginContext context)
    {
        try
        {
            string targetDir = context.DataDirectory;
            // DataDirectory is ...\plugins\data\ProdToy.Plugin.ShortCutManager;
            // go up one level and sideways to the old plugin's data dir.
            string? parent = Path.GetDirectoryName(targetDir);
            if (string.IsNullOrEmpty(parent)) return;
            string oldDir = Path.Combine(parent, "ProdToy.Plugin.ClaudeIntegration");
            if (!Directory.Exists(oldDir)) return;

            Directory.CreateDirectory(targetDir);

            string[] files =
            {
                "shortcuts.json",
                "shortcut-folders.json",
                "shortcut-recycled.json",
                "owned-wt-profiles.json",
            };
            int migrated = 0;
            foreach (var name in files)
            {
                var src = Path.Combine(oldDir, name);
                var dst = Path.Combine(targetDir, name);
                if (File.Exists(src) && !File.Exists(dst))
                {
                    File.Copy(src, dst);
                    migrated++;
                }
            }
            if (migrated > 0)
                context.Log($"Shortcuts: migrated {migrated} file(s) from ClaudeIntegration data dir");
        }
        catch (Exception ex)
        {
            context.LogError("Shortcuts: migration from ClaudeIntegration failed (non-fatal)", ex);
        }
    }
}
