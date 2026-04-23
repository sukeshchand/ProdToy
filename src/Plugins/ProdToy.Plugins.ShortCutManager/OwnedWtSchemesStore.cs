using System.Text.Json;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Tracks which Windows Terminal color scheme names were created by ProdToy's
/// + New Scheme flow, so we can offer Edit/Delete only for schemes we own
/// (rather than letting users accidentally destroy built-in/hand-crafted ones).
///
/// Persisted to <c>owned-wt-schemes.json</c> in the plugin data directory.
/// </summary>
static class OwnedWtSchemesStore
{
    private static string _file = "";
    private static readonly object _lock = new();
    private static HashSet<string>? _cache;

    public static void Initialize(string dataDirectory)
    {
        _file = Path.Combine(dataDirectory, "owned-wt-schemes.json");
    }

    public static bool IsOwned(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return LoadInternal().Contains(name);
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
        catch (Exception ex)
        {
            PluginLog.Error("OwnedWtSchemesStore: load failed", ex);
            _cache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
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
            PluginLog.Error("OwnedWtSchemesStore: save failed", ex);
        }
    }
}
