using System.Text.Json;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Persists folder paths so they survive restart even when no shortcut lives
/// under them. Live folders are also derived from shortcuts at read time; this
/// store plugs the empty-folder hole.
///
/// Paths are slash-separated, normalized (trimmed, no leading/trailing slashes,
/// no double slashes). Compared case-insensitively but stored with user-chosen
/// casing preserved for display.
/// </summary>
static class ShortcutFolders
{
    private static string _file = "";
    private static readonly object _lock = new();
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };
    private static List<string>? _cache;

    public static void Initialize(string dataDirectory)
    {
        _file = Path.Combine(dataDirectory, "shortcut-folders.json");
    }

    public static IReadOnlyList<string> Load()
    {
        lock (_lock)
        {
            if (_cache != null) return new List<string>(_cache);
            try
            {
                if (File.Exists(_file))
                {
                    var json = File.ReadAllText(_file);
                    _cache = JsonSerializer.Deserialize<List<string>>(json) ?? new();
                }
                else _cache = new();
            }
            catch (Exception ex)
            {
                PluginLog.Error("ShortcutFolders: load failed", ex);
                _cache = new();
            }
            return new List<string>(_cache);
        }
    }

    public static void Add(string path)
    {
        path = Normalize(path);
        if (string.IsNullOrEmpty(path)) return;
        lock (_lock)
        {
            var list = LoadInternal();
            if (!list.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(path);
                Save(list);
            }
        }
    }

    /// <summary>Removes the exact path only. Does not remove descendants.</summary>
    public static void Remove(string path)
    {
        path = Normalize(path);
        if (string.IsNullOrEmpty(path)) return;
        lock (_lock)
        {
            var list = LoadInternal();
            int removed = list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) Save(list);
        }
    }

    /// <summary>Removes the path and every descendant (path + "/...").</summary>
    public static void RemoveRecursive(string path)
    {
        path = Normalize(path);
        if (string.IsNullOrEmpty(path)) return;
        lock (_lock)
        {
            var list = LoadInternal();
            int removed = list.RemoveAll(p => IsSelfOrDescendant(p, path));
            if (removed > 0) Save(list);
        }
    }

    /// <summary>
    /// Renames <paramref name="oldPath"/> and all of its descendants to sit
    /// under <paramref name="newPath"/>. If oldPath = "Work" and newPath = "Job"
    /// then "Work/Backend" becomes "Job/Backend".
    /// </summary>
    public static void RenamePath(string oldPath, string newPath)
    {
        oldPath = Normalize(oldPath);
        newPath = Normalize(newPath);
        if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath)) return;
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return;

        lock (_lock)
        {
            var list = LoadInternal();
            bool changed = false;
            for (int i = 0; i < list.Count; i++)
            {
                if (IsSelfOrDescendant(list[i], oldPath))
                {
                    list[i] = RewritePrefix(list[i], oldPath, newPath);
                    changed = true;
                }
            }
            var deduped = list
                .Where(p => !string.IsNullOrEmpty(p))
                .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            if (deduped.Count != list.Count) changed = true;
            if (changed) Save(deduped);
        }
    }

    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("/", parts.Select(p => p.Trim()).Where(p => p.Length > 0));
    }

    public static bool IsSelfOrDescendant(string path, string folder)
    {
        if (string.IsNullOrEmpty(folder)) return !string.IsNullOrEmpty(path);
        if (string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)) return true;
        return path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static string RewritePrefix(string path, string oldPrefix, string newPrefix)
    {
        if (string.Equals(path, oldPrefix, StringComparison.OrdinalIgnoreCase))
            return newPrefix;
        if (path.StartsWith(oldPrefix + "/", StringComparison.OrdinalIgnoreCase))
            return newPrefix + path.Substring(oldPrefix.Length);
        return path;
    }

    public static string? ParentOf(string path)
    {
        path = Normalize(path);
        if (string.IsNullOrEmpty(path)) return null;
        int idx = path.LastIndexOf('/');
        return idx < 0 ? "" : path[..idx];
    }

    private static List<string> LoadInternal()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(_file))
            {
                var json = File.ReadAllText(_file);
                _cache = JsonSerializer.Deserialize<List<string>>(json) ?? new();
            }
            else _cache = new();
        }
        catch { _cache = new(); }
        return _cache;
    }

    private static void Save(List<string> list)
    {
        _cache = list;
        try
        {
            var dir = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_file, JsonSerializer.Serialize(list, _opts));
        }
        catch (Exception ex)
        {
            PluginLog.Error("ShortcutFolders: save failed", ex);
        }
    }
}
