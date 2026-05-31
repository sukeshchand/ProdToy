using System.Text.Json;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Per-folder preferences for the Consolidated Launcher. Today that's just the
/// "sequential build before start" toggle, persisted so each folder remembers
/// its choice across sessions. Stored in consolidated-settings.json in the
/// scoped (per-envId) data dir, keyed by normalized folder path.
/// </summary>
static class ConsolidatedSettings
{
    private static string _file = "";
    private static readonly object _lock = new();
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };
    private static Dictionary<string, FolderPrefs>? _cache;

    public sealed class FolderPrefs
    {
        public bool SequentialBuild { get; set; }
    }

    public static void Initialize(string dataDirectory)
        => _file = Path.Combine(dataDirectory, "consolidated-settings.json");

    public static bool GetSequentialBuild(string folderPath)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _cache!.TryGetValue(Key(folderPath), out var p) && p.SequentialBuild;
        }
    }

    public static void SetSequentialBuild(string folderPath, bool value)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var key = Key(folderPath);
            if (!_cache!.TryGetValue(key, out var p)) { p = new FolderPrefs(); _cache[key] = p; }
            if (p.SequentialBuild == value) return;
            p.SequentialBuild = value;
            Save();
        }
    }

    private static string Key(string folderPath) => ShortcutFolders.Normalize(folderPath);

    private static void EnsureLoaded()
    {
        if (_cache != null) return;
        try
        {
            if (File.Exists(_file))
                _cache = JsonSerializer.Deserialize<Dictionary<string, FolderPrefs>>(File.ReadAllText(_file))
                         ?? new();
            else
                _cache = new();
        }
        catch (Exception ex)
        {
            PluginLog.Error("ConsolidatedSettings: load failed", ex);
            _cache = new();
        }
        // Keys are folder paths compared case-insensitively (System.Text.Json
        // deserializes into an ordinal dictionary, so rebuild with the right comparer).
        if (!ReferenceEquals(_cache.Comparer, StringComparer.OrdinalIgnoreCase))
            _cache = new Dictionary<string, FolderPrefs>(_cache, StringComparer.OrdinalIgnoreCase);
    }

    private static void Save()
    {
        try { File.WriteAllText(_file, JsonSerializer.Serialize(_cache, _opts)); }
        catch (Exception ex) { PluginLog.Error("ConsolidatedSettings: save failed", ex); }
    }
}
