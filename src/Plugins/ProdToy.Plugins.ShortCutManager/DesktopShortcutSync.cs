using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Maintains a Windows .lnk on the user's Desktop for every Shortcut whose
/// <see cref="Shortcut.AddToDesktop"/> flag is on. Keeps a sidecar JSON
/// (<c>desktop-shortcuts.json</c>) mapping shortcut id → last-known .lnk
/// filename so we can find and clean up our own files on rename / toggle-off
/// / delete without affecting unrelated .lnks the user owns.
///
/// .lnk files are created via the WScript.Shell COM object (no extra
/// dependency). Each .lnk targets the running ProdToy.exe with
/// <c>--command shortcuts.launch --payload {"id":"&lt;id&gt;"}</c>, which
/// the plugin's id-based pipe handler dispatches to <see cref="ShortcutLauncher"/>.
/// </summary>
static class DesktopShortcutSync
{
    private static string _sidecarFile = "";
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public static void Initialize(string dataDirectory)
    {
        _sidecarFile = Path.Combine(dataDirectory, "desktop-shortcuts.json");
    }

    /// <summary>Reconcile .lnk files on the desktop with the current
    /// shortcut set. Creates lnks for any opted-in shortcut, removes
    /// lnks we previously made for shortcuts that were turned off /
    /// renamed / deleted. Idempotent.</summary>
    public static void Sync(IEnumerable<Shortcut> shortcuts, string hostExePath)
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!Directory.Exists(desktop)) return;

            var existing = LoadSidecar();
            var newMap = new Dictionary<string, string>();

            foreach (var s in shortcuts)
            {
                if (!s.AddToDesktop) continue;
                // Use the user-chosen desktop name if set, otherwise the
                // shortcut's own Name. Empty after both → skip with a warn
                // so we don't accidentally create a file called ".lnk".
                string baseName = !string.IsNullOrWhiteSpace(s.DesktopShortcutName)
                    ? s.DesktopShortcutName
                    : s.Name;
                if (string.IsNullOrWhiteSpace(baseName))
                {
                    PluginLog.Warn($"DesktopShortcutSync: skipping {s.Id} — no desktop name available");
                    continue;
                }
                string fileName = MakeLnkFileName(baseName);
                string lnkPath = Path.Combine(desktop, fileName);

                // If we previously created a different file for this id
                // (rename), clean the old one up before writing the new.
                if (existing.TryGetValue(s.Id, out var prev)
                    && !string.Equals(prev, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    SafeDelete(Path.Combine(desktop, prev));
                }

                try
                {
                    CreateLnk(lnkPath, hostExePath, s);
                    newMap[s.Id] = fileName;
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"DesktopShortcutSync: failed to write {lnkPath}", ex);
                }
            }

            // Remove .lnks for shortcuts we tracked previously but that
            // are no longer opted in (or were deleted).
            foreach (var (id, file) in existing)
            {
                if (!newMap.ContainsKey(id))
                    SafeDelete(Path.Combine(desktop, file));
            }

            SaveSidecar(newMap);
        }
        catch (Exception ex)
        {
            PluginLog.Error("DesktopShortcutSync.Sync failed", ex);
        }
    }

    /// <summary>Delete every .lnk we previously tracked. Safe to call on
    /// uninstall — only touches files we created.</summary>
    public static void Cleanup()
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var existing = LoadSidecar();
            foreach (var (_, file) in existing)
                SafeDelete(Path.Combine(desktop, file));
            SaveSidecar(new Dictionary<string, string>());
        }
        catch (Exception ex)
        {
            PluginLog.Error("DesktopShortcutSync.Cleanup failed", ex);
        }
    }

    /// <summary>Build a filesystem-safe .lnk filename from the shortcut's
    /// display name. Empty or all-whitespace names fall back to a generic
    /// "ProdToy shortcut".</summary>
    private static string MakeLnkFileName(string name)
    {
        string trimmed = string.IsNullOrWhiteSpace(name) ? "ProdToy shortcut" : name.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(trimmed.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return clean + ".lnk";
    }

    private static void CreateLnk(string lnkPath, string hostExePath, Shortcut s)
    {
        // Late-bound COM via reflection so we don't take a hard dependency
        // on Microsoft.CSharp / the IWshRuntimeLibrary interop assembly.
        Type? wshType = Type.GetTypeFromProgID("WScript.Shell");
        if (wshType == null) throw new InvalidOperationException("WScript.Shell COM is not available");

        object? shell = Activator.CreateInstance(wshType);
        if (shell == null) throw new InvalidOperationException("Could not create WScript.Shell instance");

        object? lnk = null;
        try
        {
            lnk = wshType.InvokeMember("CreateShortcut",
                BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            if (lnk == null) throw new InvalidOperationException("CreateShortcut returned null");

            Type lnkType = lnk.GetType();
            string args = $"--command shortcuts.launch --payload \"{{\\\"id\\\":\\\"{s.Id}\\\"}}\"";

            SetProp(lnkType, lnk, "TargetPath", hostExePath);
            SetProp(lnkType, lnk, "Arguments", args);
            SetProp(lnkType, lnk, "WorkingDirectory",
                string.IsNullOrWhiteSpace(s.WorkingDirectory)
                    ? Path.GetDirectoryName(hostExePath) ?? ""
                    : s.WorkingDirectory);
            SetProp(lnkType, lnk, "IconLocation", $"{hostExePath},0");
            SetProp(lnkType, lnk, "Description", $"ProdToy shortcut: {s.Name}");

            lnkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
        }
        finally
        {
            if (lnk != null) Marshal.ReleaseComObject(lnk);
            Marshal.ReleaseComObject(shell);
        }
    }

    private static void SetProp(Type type, object instance, string name, string value)
    {
        type.InvokeMember(name, BindingFlags.SetProperty, null, instance, new object[] { value });
    }

    private static Dictionary<string, string> LoadSidecar()
    {
        try
        {
            if (!File.Exists(_sidecarFile)) return new();
            var json = File.ReadAllText(_sidecarFile);
            var entries = JsonSerializer.Deserialize<List<SidecarEntry>>(json) ?? new();
            return entries
                .Where(e => !string.IsNullOrEmpty(e.Id) && !string.IsNullOrEmpty(e.Lnk))
                .ToDictionary(e => e.Id!, e => e.Lnk!, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"DesktopShortcutSync: sidecar load failed — {ex.Message}");
            return new();
        }
    }

    private static void SaveSidecar(Dictionary<string, string> map)
    {
        try
        {
            var entries = map.Select(kv => new SidecarEntry { Id = kv.Key, Lnk = kv.Value }).ToList();
            var dir = Path.GetDirectoryName(_sidecarFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_sidecarFile, JsonSerializer.Serialize(entries, _opts));
        }
        catch (Exception ex)
        {
            PluginLog.Error("DesktopShortcutSync: sidecar save failed", ex);
        }
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { PluginLog.Warn($"DesktopShortcutSync: could not delete {path} — {ex.Message}"); }
    }

    private sealed class SidecarEntry
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("lnk")] public string? Lnk { get; set; }
    }
}
