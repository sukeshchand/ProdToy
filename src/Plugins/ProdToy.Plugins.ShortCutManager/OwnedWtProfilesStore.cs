using System.Diagnostics;
using System.Text.Json;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Tracks which Windows Terminal profile names were created by ProdToy's
/// + New Profile flow, so we can offer Edit/Delete only for profiles we own
/// (rather than letting users accidentally destroy their hand-crafted ones).
///
/// Persisted to <c>owned-wt-profiles.json</c> in the plugin data directory.
/// Structure: a JSON array of profile names (case-preserved, compared case-insensitive).
/// </summary>
static class OwnedWtProfilesStore
{
    private static string _file = "";
    private static readonly object _lock = new();
    private static HashSet<string>? _cache;

    public static void Initialize(string dataDirectory)
    {
        _file = Path.Combine(dataDirectory, "owned-wt-profiles.json");
    }

    public static bool IsOwned(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return Load().Contains(name);
    }

    public static HashSet<string> Load()
    {
        lock (_lock)
        {
            if (_cache != null) return new HashSet<string>(_cache, StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(_file))
                {
                    var json = File.ReadAllText(_file);
                    var list = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
                    _cache = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    _cache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OwnedWtProfilesStore: load failed: {ex.Message}");
                _cache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            return new HashSet<string>(_cache, StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void Add(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (_lock)
        {
            var set = LoadInternal();
            if (set.Add(name)) Save(set);
        }
    }

    public static void Remove(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (_lock)
        {
            var set = LoadInternal();
            if (set.Remove(name)) Save(set);
        }
    }

    public static void Rename(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return;
        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)) return;
        lock (_lock)
        {
            var set = LoadInternal();
            bool changed = false;
            if (set.Remove(oldName)) changed = true;
            if (set.Add(newName)) changed = true;
            if (changed) Save(set);
        }
    }

    private static HashSet<string> LoadInternal()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(_file))
            {
                var json = File.ReadAllText(_file);
                var list = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
                _cache = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
            }
            else _cache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch { _cache = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        return _cache;
    }

    private static void Save(HashSet<string> set)
    {
        try
        {
            var dir = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(set.ToList(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_file, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OwnedWtProfilesStore: save failed: {ex.Message}");
        }
    }
}
