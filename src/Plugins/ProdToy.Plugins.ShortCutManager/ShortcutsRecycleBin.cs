using System.Text.Json;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// One folder deletion captured for restore. FolderPath is the "anchor" the
/// user deleted; Subfolders holds every path that was under (or equal to) it
/// at the time, and Shortcuts holds every shortcut that lived in any of those
/// paths. All shortcut <see cref="Shortcut.FolderPath"/> values are
/// preserved as-is so a restore puts them back where they came from.
/// </summary>
sealed record RecycleBinEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime DeletedAt { get; init; } = DateTime.Now;
    public string FolderPath { get; init; } = "";
    public List<string> Subfolders { get; init; } = new();
    public List<Shortcut> Shortcuts { get; init; } = new();
}

/// <summary>
/// Persistence for soft-deleted folders. JSON file in the plugin data dir.
/// </summary>
static class ShortcutsRecycleBin
{
    private static string _file = "";
    private static readonly object _lock = new();
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };
    private static List<RecycleBinEntry>? _cache;

    public static void Initialize(string dataDirectory)
    {
        _file = Path.Combine(dataDirectory, "shortcut-recycled.json");
    }

    public static List<RecycleBinEntry> Load()
    {
        lock (_lock)
        {
            if (_cache != null) return new List<RecycleBinEntry>(_cache);
            try
            {
                if (File.Exists(_file))
                {
                    var json = File.ReadAllText(_file);
                    _cache = JsonSerializer.Deserialize<List<RecycleBinEntry>>(json) ?? new();
                }
                else _cache = new();
            }
            catch (Exception ex)
            {
                PluginLog.Error("RecycleBin: load failed", ex);
                _cache = new();
            }
            return new List<RecycleBinEntry>(_cache);
        }
    }

    public static void Save(List<RecycleBinEntry> entries)
    {
        string json;
        lock (_lock)
        {
            _cache = new List<RecycleBinEntry>(entries);
            json = JsonSerializer.Serialize(_cache, _opts);
        }
        try
        {
            var dir = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_file, json);
        }
        catch (Exception ex)
        {
            PluginLog.Error("RecycleBin: save failed", ex);
        }
    }

    public static RecycleBinEntry? Get(string id) =>
        Load().FirstOrDefault(e => e.Id == id);

    public static void Add(RecycleBinEntry entry)
    {
        var all = Load();
        all.Add(entry);
        Save(all);
    }

    public static void Remove(string id)
    {
        var all = Load();
        if (all.RemoveAll(e => e.Id == id) > 0) Save(all);
    }

    public static void Clear()
    {
        Save(new List<RecycleBinEntry>());
    }

    public static int Count => Load().Count;
}
