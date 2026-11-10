namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>
/// Which history items are still New (not yet confirmed with OK). Only the unread keys are kept, so every item that is not
/// in the file counts as Read: on the first start nothing is New, and an item that left the history drops out on the next
/// <see cref="Prune"/>. An item is known by <see cref="HistoryTree.ItemKey"/>, as tags and pins are.
/// </summary>
sealed class ReadStore
{
    public sealed class Saved
    {
        /// <summary>When the read marks began: everything older counts as Read.</summary>
        public DateTime Since { get; set; }

        /// <summary>The New items, in the order they arrived.</summary>
        public List<string> Unread { get; set; } = new();
    }

    private readonly string _path;
    private readonly Saved _data;
    private readonly object _lock = new();

    private ReadStore(string path, Saved data)
    {
        _path = path;
        _data = data;
    }

    /// <summary>Raised after any change, on the thread that made it.</summary>
    public event Action? Changed;

    /// <summary>Loads the marks; a missing file starts them now, with nothing New.</summary>
    public static ReadStore Load(string path, DateTime now)
    {
        if (!File.Exists(path))
        {
            var fresh = new ReadStore(path, new Saved { Since = now });
            fresh.Save();
            return fresh;
        }

        var data = MarkFile.Read<Saved>(path);
        data.Unread = data.Unread.Where(k => !string.IsNullOrEmpty(k)).Distinct(StringComparer.Ordinal).ToList();
        if (data.Since == default) data.Since = now;
        return new ReadStore(path, data);
    }

    public DateTime Since { get { lock (_lock) return _data.Since; } }

    public int Count { get { lock (_lock) return _data.Unread.Count; } }

    /// <summary>The New items, oldest arrival first.</summary>
    public IReadOnlyList<string> Unread { get { lock (_lock) return _data.Unread.ToList(); } }

    public bool IsUnread(string? itemKey)
    {
        if (string.IsNullOrEmpty(itemKey)) return false;
        lock (_lock) return _data.Unread.Contains(itemKey, StringComparer.Ordinal);
    }

    public void MarkUnread(string itemKey)
    {
        if (string.IsNullOrEmpty(itemKey)) return;
        lock (_lock)
        {
            if (_data.Unread.Contains(itemKey, StringComparer.Ordinal)) return;
            _data.Unread.Add(itemKey);
            Save();
        }

        Changed?.Invoke();
    }

    public void MarkRead(string? itemKey)
    {
        if (string.IsNullOrEmpty(itemKey)) return;
        lock (_lock)
        {
            if (_data.Unread.RemoveAll(k => k == itemKey) == 0) return;
            Save();
        }

        Changed?.Invoke();
    }

    public void MarkAllRead()
    {
        lock (_lock)
        {
            if (_data.Unread.Count == 0) return;
            _data.Unread.Clear();
            Save();
        }

        Changed?.Invoke();
    }

    /// <summary>Drops every New key the history no longer holds. Answers how many went.</summary>
    public int Prune(IEnumerable<string> knownKeys)
    {
        var known = new HashSet<string>(knownKeys, StringComparer.Ordinal);
        int removed;
        lock (_lock)
        {
            removed = _data.Unread.RemoveAll(k => !known.Contains(k));
            if (removed > 0) Save();
        }

        if (removed > 0) Changed?.Invoke();
        return removed;
    }

    private void Save() => MarkFile.Write(_path, _data);
}
