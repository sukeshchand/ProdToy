using System.Globalization;

namespace ProdToy.Plugins.ClaudeIntegration;

enum HistoryNodeKind { Root, Branch, Date, Group, Item, Tag, Pin }

/// <summary>One node of the history tree. Children are kept latest first.</summary>
sealed class HistoryNode
{
    public required string Key { get; init; }
    public required HistoryNodeKind Kind { get; init; }
    public required string Label { get; init; }
    public HistoryNode? Parent { get; init; }
    public List<HistoryNode> Children { get; } = new();
    public HistoryIndex? Item { get; init; }

    /// <summary>The day of a date node and of the groups under it; the Today chip reads it at paint.</summary>
    public DateOnly? Day { get; init; }

    /// <summary>A group's folder or session, as the tree knows it.</summary>
    public string GroupId { get; init; } = "";

    /// <summary>A pin's target: a node key.</summary>
    public string? Target { get; init; }

    /// <summary>Items under this node (an item in both branches counts once at the root).</summary>
    public int Count { get; set; }

    /// <summary>The newest item under this node; orders its siblings.</summary>
    public DateTime Newest { get; set; }

    public bool IsItem => Kind == HistoryNodeKind.Item;

    public IEnumerable<HistoryNode> SelfAndAncestors()
    {
        for (var n = this; n != null; n = n.Parent) yield return n;
    }

    /// <summary>The text the find box searches.</summary>
    public string SearchText => Kind == HistoryNodeKind.Item
        ? $"{Item!.Preview} {Item.Cwd} {Item.SessionId}"
        : Label;
}

/// <summary>
/// The history as a tree: History > Folders | Sessions > date > folder or session > items, latest
/// first at every level. Loaded once off the UI thread and kept in memory; a new notification goes
/// in with <see cref="Add"/>, so nothing is ever rebuilt. Every member takes the lock, so a
/// notification arriving during the first load just waits in <see cref="_waiting"/>.
/// </summary>
sealed class HistoryTree
{
    public const string RootKey = "root";
    public const string FoldersKey = "folders";
    public const string SessionsKey = "sessions";
    public const string TagsKey = "tags";
    public const string PinnedKey = "pinned";

    private const string NoFolder = "(no folder)";
    private const string NoSession = "(no session)";

    private readonly object _lock = new();
    private readonly Dictionary<string, HistoryNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<HistoryNode>> _itemNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<HistoryIndex>> _subtree = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _folderItems = new(StringComparer.Ordinal);
    private readonly List<HistoryIndex> _waiting = new();
    private bool _ready;

    public HistoryNode Root { get; }
    public HistoryNode Folders { get; }
    public HistoryNode Sessions { get; }
    public HistoryNode Tags { get; }

    /// <summary>Drawn above the root, so the root's count and scope know nothing of it.</summary>
    public HistoryNode Pinned { get; }

    /// <summary>Set before <see cref="Load"/>.</summary>
    public TagStore? TagStore { get; set; }

    /// <summary>Set before <see cref="Load"/>.</summary>
    public PinStore? PinStore { get; set; }

    public HistoryTree()
    {
        Root = Register(new HistoryNode { Key = RootKey, Kind = HistoryNodeKind.Root, Label = "History" });
        Folders = NewChild(Root, FoldersKey, HistoryNodeKind.Branch, "Folders");
        Sessions = NewChild(Root, SessionsKey, HistoryNodeKind.Branch, "Sessions");
        Tags = NewChild(Root, TagsKey, HistoryNodeKind.Branch, "Tags");
        Pinned = Register(new HistoryNode { Key = PinnedKey, Kind = HistoryNodeKind.Branch, Label = "Pinned" });
    }

    public bool IsReady { get { lock (_lock) return _ready; } }

    /// <summary>A stable id of a saved entry: its day file and its place in it.</summary>
    public static string ItemKey(HistoryIndex item) => $"{Path.GetFileName(item.DayFile)}#{item.Index}";

    /// <summary>The first load, with the history's whole index. Run it once, off the UI thread.</summary>
    public void Load(IEnumerable<HistoryIndex> items)
    {
        lock (_lock)
        {
            foreach (var item in items.OrderBy(i => i.Timestamp)) AddItem(item);
            foreach (var item in _waiting) AddItem(item);
            _waiting.Clear();
            _ready = true;
            SyncTags();
            SyncPins();
        }
    }

    /// <summary>Adds one saved entry in place. A pending question or a known entry changes nothing.</summary>
    public bool Add(HistoryIndex item)
    {
        lock (_lock)
        {
            if (!_ready)
            {
                _waiting.Add(item);
                return false;
            }

            return AddItem(item);
        }
    }

    public HistoryNode? Find(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        lock (_lock) return _nodes.GetValueOrDefault(key);
    }

    /// <summary>The items of a node's whole subtree, latest first.</summary>
    public IReadOnlyList<HistoryIndex> Items(HistoryNode node)
    {
        lock (_lock)
        {
            if (_subtree.TryGetValue(node.Key, out var cached)) return cached;

            var list = new List<HistoryIndex>();
            Collect(node.Kind == HistoryNodeKind.Root ? Folders : node, list);
            if (ReferenceEquals(node, Tags)) list = list.DistinctBy(ItemKey).ToList();
            list.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
            _subtree[node.Key] = list;
            return list;
        }
    }

    public bool Contains(HistoryNode scope, string itemKey)
    {
        lock (_lock)
        {
            return _itemNodes.TryGetValue(itemKey, out var places)
                && places.Any(p => p.SelfAndAncestors().Contains(scope));
        }
    }

    /// <summary>The item's own node, in the scope's branch when it has one.</summary>
    public HistoryNode? ItemNode(string itemKey, HistoryNode? scope)
    {
        lock (_lock)
        {
            if (!_itemNodes.TryGetValue(itemKey, out var places)) return null;
            var branch = scope == null ? Folders : BranchOf(scope);
            return places.FirstOrDefault(p => p.SelfAndAncestors().Contains(branch)) ?? places[0];
        }
    }

    /// <summary>
    /// A new notification's scope: the current one when it holds the item, else the item's own group
    /// (its date > its folder, or its session when the current scope is in the Sessions branch).
    /// </summary>
    public HistoryNode ScopeFor(string itemKey, HistoryNode? current)
    {
        if (current != null && Contains(current, itemKey)) return current;
        return ItemNode(itemKey, current)?.Parent ?? current ?? Root;
    }

    /// <summary>A tag goes on an item or a folder, never on a session.</summary>
    public bool CanTag(HistoryNode node)
        => node.IsItem || (node.Kind == HistoryNodeKind.Group && BranchOf(node) == Folders);

    /// <summary>The tags on this node itself: an item's, or a folder's.</summary>
    public IReadOnlyList<TreeTag> TagsOf(HistoryNode node)
    {
        if (TagStore is not { } store || !CanTag(node)) return Array.Empty<TreeTag>();
        return node.IsItem ? store.ForItem(ItemKey(node.Item!)) : store.ForFolder(node.GroupId);
    }

    /// <summary>The tags an item shows in the right pane: its own and its folder's.</summary>
    public IReadOnlyList<TreeTag> TagsOfItem(string itemKey)
    {
        if (TagStore is not { } store) return Array.Empty<TreeTag>();
        lock (_lock)
        {
            if (!_itemNodes.TryGetValue(itemKey, out var places)) return Array.Empty<TreeTag>();
            return store.ForItem(itemKey).Concat(store.ForFolder(FolderKey(places[0].Item!.Cwd))).DistinctBy(t => t.Id).ToList();
        }
    }

    public string PinLabel(HistoryNode node) => node.Kind switch
    {
        HistoryNodeKind.Date => $"{node.Parent!.Label} · {node.Label}",
        HistoryNodeKind.Group => $"{node.Label} · {node.Parent!.Label}",
        _ => node.Label,
    };

    /// <summary>
    /// Brings the Tags branch in line with the store. A tag's items are real nodes beside the Folders and
    /// Sessions ones, so the pager, <see cref="Contains"/> and <see cref="ItemNode"/> work on them as on any
    /// branch; only what changed is touched.
    /// </summary>
    public void SyncTags()
    {
        if (TagStore is not { } store) return;
        lock (_lock)
        {
            if (!_ready) return;

            var tags = store.All;
            foreach (var gone in Tags.Children.Where(c => tags.All(t => TagKey(t.Id) != c.Key)).ToList())
            {
                foreach (var child in gone.Children.ToList()) RemoveTagItem(child);
                Tags.Children.Remove(gone);
                _nodes.Remove(gone.Key);
            }

            foreach (var tag in tags)
            {
                var node = GetOrAdd(Tags, TagKey(tag.Id), HistoryNodeKind.Tag, tag.Name);
                var wanted = new HashSet<string>(store.ItemsOf(tag.Id).Where(_itemNodes.ContainsKey), StringComparer.Ordinal);
                foreach (var folder in store.FoldersOf(tag.Id))
                    if (_folderItems.TryGetValue(folder, out var items)) wanted.UnionWith(items);

                foreach (var child in node.Children.Where(c => !wanted.Contains(ItemKey(c.Item!))).ToList()) RemoveTagItem(child);

                var present = node.Children.Select(c => ItemKey(c.Item!)).ToHashSet(StringComparer.Ordinal);
                foreach (var key in wanted.Where(k => !present.Contains(k)))
                {
                    var item = _itemNodes[key][0].Item!;
                    var child = NewChild(node, $"{node.Key}|{key}", HistoryNodeKind.Item, item.Preview, item);
                    child.Newest = item.Timestamp;
                    child.Count = 1;
                    _itemNodes[key].Add(child);
                }

                node.Children.Sort((a, b) => b.Newest.CompareTo(a.Newest));
                node.Count = node.Children.Count;
                node.Newest = node.Children.Count > 0 ? node.Children[0].Newest : default;
            }

            Tags.Children.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.CurrentCultureIgnoreCase));
            Tags.Count = Tags.Children.SelectMany(t => t.Children).Select(c => ItemKey(c.Item!)).Distinct().Count();
            _subtree.Clear();
        }
    }

    /// <summary>Brings the Pinned branch in line with the store. A pin whose target is gone stays, as a disabled row.</summary>
    public void SyncPins()
    {
        if (PinStore is not { } store) return;
        lock (_lock)
        {
            foreach (var old in Pinned.Children) _nodes.Remove(old.Key);
            Pinned.Children.Clear();
            foreach (var pin in store.All)
            {
                var node = Register(new HistoryNode { Key = $"{PinnedKey}|{pin.Key}", Kind = HistoryNodeKind.Pin, Label = pin.Label, Parent = Pinned, Target = pin.Key });
                Pinned.Children.Add(node);
            }

            Pinned.Count = Pinned.Children.Count;
        }
    }

    /// <summary>What the chip beside the pager says.</summary>
    public string ScopeLabel(HistoryNode node) => node.Kind switch
    {
        HistoryNodeKind.Root => $"All · {Root.Count}",
        HistoryNodeKind.Branch => $"{node.Label} · all",
        HistoryNodeKind.Date => $"{node.Parent!.Label} · {node.Label}",
        HistoryNodeKind.Group => $"{node.Label} · {node.Parent!.Label}",
        HistoryNodeKind.Tag => $"Tag · {node.Label}",
        _ => node.Label,
    };

    private static string TagKey(string tagId) => $"{TagsKey}|{tagId}";

    private void RemoveTagItem(HistoryNode child)
    {
        child.Parent!.Children.Remove(child);
        _nodes.Remove(child.Key);
        if (_itemNodes.TryGetValue(ItemKey(child.Item!), out var places)) places.Remove(child);
    }

    private HistoryNode BranchOf(HistoryNode node)
        => node.SelfAndAncestors().FirstOrDefault(n => n.Kind == HistoryNodeKind.Branch) ?? Folders;

    private bool AddItem(HistoryIndex item)
    {
        if (item.Type == NotificationType.Pending) return false;

        string key = ItemKey(item);
        if (_itemNodes.ContainsKey(key)) return false;

        var places = new List<HistoryNode>
        {
            Place(Folders, item, key, FolderKey(item.Cwd), FolderLabel(item.Cwd)),
            Place(Sessions, item, key, item.SessionId, SessionLabel(item.SessionId)),
        };
        _itemNodes[key] = places;
        Root.Count++;
        if (item.Timestamp > Root.Newest) Root.Newest = item.Timestamp;
        _subtree.Clear();

        string folder = FolderKey(item.Cwd);
        if (!_folderItems.TryGetValue(folder, out var inFolder)) _folderItems[folder] = inFolder = new List<string>();
        inFolder.Add(key);
        if (_ready && TagStore?.FolderHasTags(folder) == true) SyncTags();
        return true;
    }

    private HistoryNode Place(HistoryNode branch, HistoryIndex item, string itemKey, string groupId, string groupLabel)
    {
        string day = item.Timestamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var onDay = DateOnly.FromDateTime(item.Timestamp);
        var date = GetOrAdd(branch, $"{branch.Key}|{day}", HistoryNodeKind.Date,
            item.Timestamp.ToString("ddd dd MMM yyyy", CultureInfo.CurrentCulture), onDay);
        var group = GetOrAdd(date, $"{date.Key}|{groupId}", HistoryNodeKind.Group, groupLabel, onDay, groupId);
        var node = NewChild(group, $"{group.Key}|{itemKey}", HistoryNodeKind.Item, item.Preview, item);
        node.Newest = item.Timestamp;
        node.Count = 1;

        Reposition(node);
        for (var n = group; n != null && n != Root; n = n.Parent)
        {
            n.Count++;
            if (item.Timestamp > n.Newest) n.Newest = item.Timestamp;
            Reposition(n);
        }

        return node;
    }

    private HistoryNode GetOrAdd(HistoryNode parent, string key, HistoryNodeKind kind, string label, DateOnly? day = null, string groupId = "")
        => _nodes.TryGetValue(key, out var found) ? found : NewChild(parent, key, kind, label, day: day, groupId: groupId);

    private HistoryNode NewChild(HistoryNode parent, string key, HistoryNodeKind kind, string label, HistoryIndex? item = null, DateOnly? day = null, string groupId = "")
    {
        var node = Register(new HistoryNode { Key = key, Kind = kind, Label = label, Parent = parent, Item = item, Day = day, GroupId = groupId });
        parent.Children.Add(node);
        return node;
    }

    private HistoryNode Register(HistoryNode node)
    {
        _nodes[node.Key] = node;
        return node;
    }

    /// <summary>Keeps a node in order among its siblings: latest first; the branches stay put.</summary>
    private static void Reposition(HistoryNode node)
    {
        if (node.Parent == null || node.Kind == HistoryNodeKind.Branch) return;

        var siblings = node.Parent.Children;
        siblings.Remove(node);
        int at = siblings.FindIndex(s => s.Newest <= node.Newest);
        siblings.Insert(at < 0 ? siblings.Count : at, node);
    }

    private static void Collect(HistoryNode node, List<HistoryIndex> into)
    {
        if (node.Item != null) into.Add(node.Item);
        foreach (var child in node.Children) Collect(child, into);
    }

    public static string FolderKey(string cwd)
        => cwd.Trim().TrimEnd('/', '\\').ToLowerInvariant();

    private static string FolderLabel(string cwd)
    {
        string name = Path.GetFileName(cwd.Trim().TrimEnd('/', '\\'));
        return name.Length > 0 ? name : NoFolder;
    }

    private static string SessionLabel(string sessionId)
        => sessionId.Length == 0 ? NoSession : $"Session {(sessionId.Length > 8 ? sessionId[..8] : sessionId)}";
}
