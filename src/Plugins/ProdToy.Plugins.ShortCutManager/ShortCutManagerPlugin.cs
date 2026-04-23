using System.Drawing;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

[Plugin("ProdToy.Plugin.ShortCutManager", "Shortcuts", "1.0.410",
    Description = "Folder-organized launcher for project shortcuts — Claude CLI, npm, dotnet, custom commands",
    Author = "ProdToy",
    MenuPriority = 250)]
public partial class ShortCutManagerPlugin : IPlugin, IDoctor
{
    private IPluginContext _context = null!;
    private ShortcutsForm? _shortcutsForm;

    public void Install(IPluginContext context)
    {
        // No external-system state to install.
    }

    public void Uninstall(IPluginContext context)
    {
        // No external-system state to remove.
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
    }

    public void Start()
    {
        _context.Log("Shortcuts plugin started");
    }

    public void Stop()
    {
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
