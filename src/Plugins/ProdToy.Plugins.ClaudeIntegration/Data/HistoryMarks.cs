using System.Text.Json;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>A tag: a name and its own colour (#rrggbb).</summary>
sealed record TreeTag(string Id, string Name, string Color);

/// <summary>What the person marked in the tree is kept beside the history, never inside it: the day files are not rewritten.</summary>
static class MarkFile
{
    public static T Read<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? new T();
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"Could not read {Path.GetFileName(path)}: {ex.Message}");
        }

        return new T();
    }

    public static void Write<T>(string path, T value)
    {
        try
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"Could not write {Path.GetFileName(path)}: {ex.Message}");
        }
    }
}

/// <summary>
/// The tags and what they are on. An item is known by <see cref="HistoryTree.ItemKey"/>, a folder by its
/// normalised working folder, so a tag on a folder covers every date of that folder, future ones too.
/// </summary>
sealed class TagStore
{
    public sealed class Saved
    {
        public List<TreeTag> Tags { get; set; } = new();
        public Dictionary<string, List<string>> Items { get; set; } = new();
        public Dictionary<string, List<string>> Folders { get; set; } = new();
    }

    private readonly string _path;
    private readonly Saved _data;
    private readonly object _lock = new();

    private TagStore(string path, Saved data)
    {
        _path = path;
        _data = data;
    }

    public static TagStore Load(string path) => new(path, MarkFile.Read<Saved>(path));

    public IReadOnlyList<TreeTag> All { get { lock (_lock) return _data.Tags.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList(); } }

    public IReadOnlyList<TreeTag> ForItem(string itemKey) => Resolve(_data.Items, itemKey);

    public IReadOnlyList<TreeTag> ForFolder(string folderKey) => Resolve(_data.Folders, folderKey);

    public bool FolderHasTags(string folderKey)
    {
        lock (_lock) return _data.Folders.ContainsKey(folderKey);
    }

    public IReadOnlyList<string> ItemsOf(string tagId) => Keys(_data.Items, tagId);

    public IReadOnlyList<string> FoldersOf(string tagId) => Keys(_data.Folders, tagId);

    public TreeTag Create(string name, string color)
    {
        lock (_lock)
        {
            var known = _data.Tags.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (known != null) return known;

            var tag = new TreeTag(Guid.NewGuid().ToString("N"), name, color);
            _data.Tags.Add(tag);
            Save();
            return tag;
        }
    }

    public void Delete(string tagId)
    {
        lock (_lock)
        {
            _data.Tags.RemoveAll(t => t.Id == tagId);
            foreach (var map in new[] { _data.Items, _data.Folders })
            {
                foreach (var key in map.Keys.ToList())
                {
                    map[key].Remove(tagId);
                    if (map[key].Count == 0) map.Remove(key);
                }
            }

            Save();
        }
    }

    public void SetItem(string itemKey, IEnumerable<string> tagIds) => Set(_data.Items, itemKey, tagIds);

    public void SetFolder(string folderKey, IEnumerable<string> tagIds) => Set(_data.Folders, folderKey, tagIds);

    private void Set(Dictionary<string, List<string>> map, string key, IEnumerable<string> tagIds)
    {
        lock (_lock)
        {
            var ids = tagIds.Distinct().ToList();
            if (ids.Count == 0) map.Remove(key);
            else map[key] = ids;
            Save();
        }
    }

    private IReadOnlyList<TreeTag> Resolve(Dictionary<string, List<string>> map, string key)
    {
        lock (_lock)
        {
            if (!map.TryGetValue(key, out var ids)) return Array.Empty<TreeTag>();
            return _data.Tags.Where(t => ids.Contains(t.Id)).ToList();
        }
    }

    private IReadOnlyList<string> Keys(Dictionary<string, List<string>> map, string tagId)
    {
        lock (_lock) return map.Where(kv => kv.Value.Contains(tagId)).Select(kv => kv.Key).ToList();
    }

    private void Save() => MarkFile.Write(_path, _data);
}

/// <summary>One pin: the node's key and the words it showed, kept for when its target is gone.</summary>
sealed record TreePin(string Key, string Label);

/// <summary>The pinned tree nodes, in the order they were pinned.</summary>
sealed class PinStore
{
    private readonly string _path;
    private readonly List<TreePin> _pins;
    private readonly object _lock = new();

    private PinStore(string path, List<TreePin> pins)
    {
        _path = path;
        _pins = pins;
    }

    public static PinStore Load(string path) => new(path, MarkFile.Read<List<TreePin>>(path));

    public IReadOnlyList<TreePin> All { get { lock (_lock) return _pins.ToList(); } }

    public bool Contains(string key)
    {
        lock (_lock) return _pins.Any(p => p.Key == key);
    }

    public void Add(string key, string label)
    {
        lock (_lock)
        {
            if (_pins.Any(p => p.Key == key)) return;
            _pins.Add(new TreePin(key, label));
            MarkFile.Write(_path, _pins);
        }
    }

    public void Remove(string key)
    {
        lock (_lock)
        {
            if (_pins.RemoveAll(p => p.Key == key) > 0) MarkFile.Write(_path, _pins);
        }
    }
}
