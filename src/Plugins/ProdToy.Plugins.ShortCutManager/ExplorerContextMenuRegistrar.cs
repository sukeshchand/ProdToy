using System.Diagnostics;
using Microsoft.Win32;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Registers a single Windows Explorer "Directory background" verb under
/// <c>HKCU\Software\Classes\Directory\Background\shell\ProdToy.Shortcuts</c>
/// when at least one shortcut opts in via
/// <see cref="Shortcut.ShowInExplorerContextMenu"/>. The verb appears in
/// every folder's right-click menu (under "Show more options" on Win 11)
/// because legacy <c>AppliesTo</c> filtering does not work reliably for
/// the Directory\Background context. Filtering is instead done at click
/// time: the host receives <c>%V</c> (the current folder) and decides
/// which shortcut(s) to launch — none → tray balloon, one → launch,
/// many → small picker.
/// </summary>
static class ExplorerContextMenuRegistrar
{
    private const string ShellRoot = @"Software\Classes\Directory\Background\shell";
    private const string VerbKey = "ProdToy.Shortcuts";

    // Older builds wrote one verb per shortcut under "ProdToy.Shortcut.<id>";
    // we still sweep them on Register/Unregister so upgrades are clean.
    private const string LegacyKeyPrefix = "ProdToy.Shortcut.";

    /// <summary>If any shortcut in <paramref name="shortcuts"/> has
    /// <see cref="Shortcut.ShowInExplorerContextMenu"/> on, register the
    /// single ProdToy verb. Otherwise remove it. Idempotent — also clears
    /// any per-shortcut legacy entries from earlier builds.</summary>
    public static void RegisterAll(IEnumerable<Shortcut> shortcuts, string hostExePath)
    {
        try
        {
            UnregisterAll();

            bool anyOptIn = shortcuts.Any(s => s.ShowInExplorerContextMenu
                                            && !string.IsNullOrWhiteSpace(s.WorkingDirectory));
            if (!anyOptIn) return;

            using var shell = Registry.CurrentUser.CreateSubKey(ShellRoot, writable: true);
            if (shell == null) return;

            using var verb = shell.CreateSubKey(VerbKey, writable: true);
            if (verb == null) return;

            // (Default) and MUIVerb both set so different Win builds pick up
            // the display string regardless of which one they prefer.
            const string display = "ProdToy shortcuts";
            verb.SetValue("", display, RegistryValueKind.String);
            verb.SetValue("MUIVerb", display, RegistryValueKind.String);
            verb.SetValue("Icon", $"\"{hostExePath}\",0", RegistryValueKind.String);

            using var cmd = verb.CreateSubKey("command", writable: true);
            // %V is the current folder background's path; Windows expands it
            // before launching. The host's --cm-cwd helper turns the value
            // into the JSON payload {"cwd":"..."} the plugin handler expects
            // (path backslashes can't be embedded in a JSON literal directly).
            cmd?.SetValue("",
                $"\"{hostExePath}\" --command shortcuts.context-launch --cm-cwd \"%V\"",
                RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            PluginLog.Error("ExplorerContextMenuRegistrar.RegisterAll failed", ex);
        }
    }

    /// <summary>Remove the ProdToy verb and any legacy per-shortcut entries
    /// from earlier builds. Safe to call repeatedly.</summary>
    public static void UnregisterAll()
    {
        try
        {
            using var shell = Registry.CurrentUser.OpenSubKey(ShellRoot, writable: true);
            if (shell == null) return;

            try { shell.DeleteSubKeyTree(VerbKey, throwOnMissingSubKey: false); }
            catch (Exception ex) { PluginLog.Warn($"Failed to remove {VerbKey}: {ex.Message}"); }

            foreach (var name in shell.GetSubKeyNames())
            {
                if (name.StartsWith(LegacyKeyPrefix, StringComparison.Ordinal))
                {
                    try { shell.DeleteSubKeyTree(name, throwOnMissingSubKey: false); }
                    catch (Exception ex) { PluginLog.Warn($"Failed to remove {name}: {ex.Message}"); }
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error("ExplorerContextMenuRegistrar.UnregisterAll failed", ex);
        }
    }

    /// <summary>Best-effort host exe lookup. Plugins run in-process so this
    /// is the running ProdToy.exe. Falls back to AppContext.BaseDirectory
    /// if MainModule is unavailable (single-file publish edge case).</summary>
    public static string ResolveHostExePath()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
        }
        catch { }
        try
        {
            var path = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
        }
        catch { }
        return Path.Combine(AppContext.BaseDirectory, "ProdToy.exe");
    }

    /// <summary>Trim trailing separators and resolve the path to its full
    /// form so comparison against Explorer's <c>%V</c> argument is stable.
    /// Drive roots stay as <c>C:\</c>.</summary>
    public static string NormalizeFolderPath(string path)
    {
        try
        {
            string full = Path.GetFullPath(path.Trim());
            if (full.Length == 3 && full[1] == ':' && full[2] == Path.DirectorySeparatorChar)
                return full;
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }
}
