using System.Diagnostics;
using System.Text.Json;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Persistence for Shortcut records. Stored in its own file rather than
/// the main plugin settings.json to keep concerns clean (settings vs. user-
/// managed project shortcuts). Cached in memory; writes are atomic full-file.
/// </summary>
static class ShortcutStore
{
    private static string _file = "";
    private static readonly object _lock = new();
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };
    private static List<Shortcut>? _cache;

    public static void Initialize(string dataDirectory)
    {
        _file = Path.Combine(dataDirectory, "shortcuts.json");
    }

    public static List<Shortcut> Load()
    {
        lock (_lock)
        {
            if (_cache != null) return new List<Shortcut>(_cache);
            try
            {
                if (File.Exists(_file))
                {
                    var json = File.ReadAllText(_file);
                    _cache = JsonSerializer.Deserialize<List<Shortcut>>(json) ?? new();
                }
                else
                {
                    _cache = new();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ShortcutStore: load failed: {ex.Message}");
                _cache = new();
            }
            return new List<Shortcut>(_cache);
        }
    }

    public static void Save(List<Shortcut> shortcuts)
    {
        string json;
        lock (_lock)
        {
            _cache = new List<Shortcut>(shortcuts);
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
            Debug.WriteLine($"ShortcutStore: save failed: {ex.Message}");
        }
    }

    public static Shortcut? Get(string id) =>
        Load().FirstOrDefault(s => s.Id == id);

    public static void Add(Shortcut s)
    {
        var all = Load();
        all.Add(s);
        Save(all);
    }

    public static void Update(Shortcut s)
    {
        var all = Load();
        int idx = all.FindIndex(x => x.Id == s.Id);
        if (idx >= 0) all[idx] = s with { UpdatedAt = DateTime.Now };
        else all.Add(s);
        Save(all);
    }

    public static void Delete(string id)
    {
        var all = Load();
        int removed = all.RemoveAll(x => x.Id == id);
        if (removed > 0) Save(all);
    }

    public static void RecordLaunch(string id)
    {
        var all = Load();
        int idx = all.FindIndex(x => x.Id == id);
        if (idx < 0) return;
        all[idx] = all[idx] with
        {
            LastLaunchedAt = DateTime.Now,
            LaunchCount = all[idx].LaunchCount + 1,
        };
        Save(all);
    }
}
