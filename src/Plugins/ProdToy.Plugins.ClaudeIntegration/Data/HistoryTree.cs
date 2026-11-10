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

    /// <summary>A folder the CLI was opened in (the Folders branch's opened-folder level), not an inner working folder.</summary>
    public bool IsOpenedFolder { get; init; }

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
        ? $"{Item!.Preview} {Item.Cwd} {Item.ProjectRoot} {Item.SessionId}"
        : Label;
}

/// <summary>How the Folders branch groups its items.</summary>
enum FolderView
{
    /// <summary>date &gt; opened folder &gt; inner folder &gt; items (the inner level is skipped when it is the opened folder).</summary>
    Opened,

    /// <summary>date &gt; working folder &gt; items: the folder each notification came from.</summary>
    Working,

    /// <summary>opened folder &gt; date &gt; inner folder &gt; items: one opened folder across days.</summary>
    FolderFirst,
}

static class FolderViews
{
    public const string Opened = "opened";
    public const string Working = "working";
    public const string FolderFirst = "folderFirst";

    public static FolderView Parse(string? setting) => setting switch
    {
        Working => FolderView.Working,
        FolderFirst => FolderView.FolderFirst,
        _ => FolderView.Opened,
    };

    public static string ToSetting(FolderView view) => view switch
    {
        FolderView.Working => Working,
        FolderView.FolderFirst => FolderFirst,
        _ => Opened,
    };

    public static string Title(FolderView view) => view switch
    {
        FolderView.Working => "Working folder",
        FolderView.FolderFirst => "Folder first",
        _ => "Opened folder",
    };
}

/// <summary>
/// The history as a tree: History > Folders | Sessions > date > folder or session > items, latest
/// first at every level. The Folders branch groups by the folder the CLI was opened in, then the inner
/// working folder (see <see cref="FolderView"/>). Loaded once off the UI thread and kept in memory; a new notification goes
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
    private readonly Dictionary<string, string> _sessionRoots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _roots = new(StringComparer.Ordinal);
    private readonly List<HistoryIndex> _all = new();
    private readonly List<HistoryIndex> _waiting = new();
    private FolderView _view = FolderView.Opened;
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

    /// <summary>Which items are still New; the rows paint a dot for them. Set before <see cref="Load"/>.</summary>
    public ReadStore? ReadStore { get; set; }

    public FolderView FolderView { get { lock (_lock) return _view; } }

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

    /// <summary>The saved key's node, else its nearest ancestor that exists (opened folder, then day, then the root).</summary>
    public HistoryNode FindOrAncestor(string? key)
    {
        lock (_lock)
        {
            for (string? k = key; !string.IsNullOrEmpty(k);)
            {
                if (_nodes.TryGetValue(k, out var found)) return found;
                int cut = k.LastIndexOf('|');
                k = cut < 0 ? null : k[..cut];
            }

            return Root;
        }
    }

    /// <summary>An item by its key; null when the tree does not hold it.</summary>
    public HistoryIndex? FindItem(string? itemKey)
    {
        if (string.IsNullOrEmpty(itemKey)) return null;
        lock (_lock) return _itemNodes.TryGetValue(itemKey, out var places) ? places[0].Item : null;
    }

    /// <summary>Every item key the tree holds.</summary>
    public IReadOnlyList<string> ItemKeys()
    {
        lock (_lock) return _itemNodes.Keys.ToList();
    }

    /// <summary>The folder the CLI was opened in for this item: saved with it, or derived from its session.</summary>
    public string OpenedFolderOf(string? itemKey)
    {
        if (string.IsNullOrEmpty(itemKey)) return "";
        lock (_lock) return _roots.GetValueOrDefault(itemKey) ?? "";
    }

    /// <summary>Regroups the Folders branch. Before the first load it only takes note.</summary>
    public void SetFolderView(FolderView view)
    {
        lock (_lock)
        {
            if (view == _view) return;
            _view = view;
            if (!_ready) return;

            foreach (var child in Folders.Children) Unregister(child);
            Folders.Children.Clear();
            Folders.Count = 0;
            Folders.Newest = default;
            foreach (var item in _all)
            {
                string key = ItemKey(item);
                _itemNodes[key][0] = Place(Folders, item, key, FolderSteps(item, key));
            }

            _subtree.Clear();
        }
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
    /// A new notification's scope: the current one when it holds the item (the reading position stays), else the
    /// item's own group - its opened folder in the Opened-folder and Folder-first views, its working folder in the
    /// Working-folder view, its session when the current scope is in the Sessions branch.
    /// </summary>
    public HistoryNode ScopeFor(string itemKey, HistoryNode? current)
    {
        if (current != null && Contains(current, itemKey)) return current;

        var node = ItemNode(itemKey, current);
        if (node == null) return current ?? Root;

        lock (_lock)
        {
            if (_view != FolderView.Working && BranchOf(node) == Folders
                && node.SelfAndAncestors().FirstOrDefault(n => n.IsOpenedFolder) is { } opened)
                return opened;
        }

        return node.Parent ?? current ?? Root;
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
            return store.ForItem(itemKey)
                .Concat(store.ForFolder(FolderKey(places[0].Item!.Cwd)))
                .Concat(store.ForFolder(FolderKey(_roots.GetValueOrDefault(itemKey) ?? "")))
                .DistinctBy(t => t.Id).ToList();
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

    /// <summary>
    /// What the chip beside the pager says. Under Folders it reads down the path: "day › folder", "day › folder › inner";
    /// a top-level day or an opened folder across days says which branch it pages.
    /// </summary>
    public string ScopeLabel(HistoryNode node)
    {
        if (node.Kind is HistoryNodeKind.Date or HistoryNodeKind.Group && node.Parent != null && BranchOf(node) == Folders)
        {
            if (node.Parent == Folders)
                return node.Kind == HistoryNodeKind.Date ? $"{Folders.Label} · {node.Label}" : $"{node.Label} · all days";

            var path = node.SelfAndAncestors().TakeWhile(n => n.Kind != HistoryNodeKind.Branch).Reverse().Select(n => n.Label);
            return string.Join(" \u203A ", path);
        }

        return node.Kind switch
        {
            HistoryNodeKind.Root => $"All · {Root.Count}",
            HistoryNodeKind.Branch => $"{node.Label} · all",
            HistoryNodeKind.Date => $"{node.Parent!.Label} · {node.Label}",
            HistoryNodeKind.Group => $"{node.Label} · {node.Parent!.Label}",
            HistoryNodeKind.Tag => $"Tag · {node.Label}",
            _ => node.Label,
        };
    }

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

        _roots[key] = RootOf(item);
        _all.Add(item);
        var places = new List<HistoryNode>
        {
            Place(Folders, item, key, FolderSteps(item, key)),
            Place(Sessions, item, key, new[] { DateStep(item), new Step(item.SessionId, HistoryNodeKind.Group, SessionLabel(item.SessionId), item.SessionId, Dated: true) }),
        };
        _itemNodes[key] = places;
        Root.Count++;
        if (item.Timestamp > Root.Newest) Root.Newest = item.Timestamp;
        _subtree.Clear();

        bool tagged = false;
        foreach (string folder in new[] { FolderKey(item.Cwd), FolderKey(_roots[key]) }.Distinct())
        {
            if (!_folderItems.TryGetValue(folder, out var inFolder)) _folderItems[folder] = inFolder = new List<string>();
            inFolder.Add(key);
            tagged |= TagStore?.FolderHasTags(folder) == true;
        }

        if (_ready && tagged) SyncTags();
        return true;
    }

    /// <summary>
    /// The folder the CLI was opened in: saved with the item, else the earliest working folder of its session (a session
    /// starts there before it moves anywhere; the items come in time order), else the item's own working folder.
    /// </summary>
    private string RootOf(HistoryIndex item)
    {
        string saved = item.ProjectRoot ?? "";
        if (item.SessionId.Length == 0) return saved.Length > 0 ? saved : item.Cwd;

        if (saved.Length > 0)
        {
            _sessionRoots.TryAdd(item.SessionId, saved);
            return saved;
        }

        if (_sessionRoots.TryGetValue(item.SessionId, out var known)) return known;
        _sessionRoots[item.SessionId] = item.Cwd;
        return item.Cwd;
    }

    /// <summary>One level on an item's path in a branch.</summary>
    private readonly record struct Step(string KeyPart, HistoryNodeKind Kind, string Label, string GroupId = "", bool Opened = false, bool Dated = false);

    private static Step DateStep(HistoryIndex item) => new(
        item.Timestamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture), HistoryNodeKind.Date,
        item.Timestamp.ToString("ddd dd MMM yyyy", CultureInfo.CurrentCulture), Dated: true);

    /// <summary>The item's path under Folders in the current view.</summary>
    private IReadOnlyList<Step> FolderSteps(HistoryIndex item, string itemKey)
    {
        string cwdKey = FolderKey(item.Cwd);
        if (_view == FolderView.Working)
            return new[] { DateStep(item), new Step(cwdKey, HistoryNodeKind.Group, FolderLabel(item.Cwd), cwdKey, Dated: true) };

        string root = _roots.GetValueOrDefault(itemKey) ?? item.Cwd;
        string rootKey = FolderKey(root);
        bool inner = cwdKey != rootKey;
        var opened = new Step(rootKey, HistoryNodeKind.Group, FolderLabel(root), rootKey, Opened: true, Dated: _view == FolderView.Opened);
        var innerStep = new Step(cwdKey, HistoryNodeKind.Group, InnerLabel(root, item.Cwd), cwdKey, Dated: true);

        var steps = _view == FolderView.FolderFirst
            ? new List<Step> { opened, DateStep(item) }
            : new List<Step> { DateStep(item), opened };
        if (inner) steps.Add(innerStep);
        return steps;
    }

    private HistoryNode Place(HistoryNode branch, HistoryIndex item, string itemKey, IEnumerable<Step> steps)
    {
        var onDay = DateOnly.FromDateTime(item.Timestamp);
        var parent = branch;
        foreach (var step in steps)
            parent = GetOrAdd(parent, $"{parent.Key}|{step.KeyPart}", step.Kind, step.Label, step.Dated ? onDay : null, step.GroupId, step.Opened);

        var node = NewChild(parent, $"{parent.Key}|{itemKey}", HistoryNodeKind.Item, item.Preview, item);
        node.Newest = item.Timestamp;
        node.Count = 1;

        Reposition(node);
        for (var n = parent; n != null && n != Root; n = n.Parent)
        {
            n.Count++;
            if (item.Timestamp > n.Newest) n.Newest = item.Timestamp;
            Reposition(n);
        }

        return node;
    }

    private HistoryNode GetOrAdd(HistoryNode parent, string key, HistoryNodeKind kind, string label, DateOnly? day = null, string groupId = "", bool opened = false)
        => _nodes.TryGetValue(key, out var found) ? found : NewChild(parent, key, kind, label, day: day, groupId: groupId, opened: opened);

    private HistoryNode NewChild(HistoryNode parent, string key, HistoryNodeKind kind, string label, HistoryIndex? item = null, DateOnly? day = null, string groupId = "", bool opened = false)
    {
        var node = Register(new HistoryNode { Key = key, Kind = kind, Label = label, Parent = parent, Item = item, Day = day, GroupId = groupId, IsOpenedFolder = opened });
        parent.Children.Add(node);
        return node;
    }

    private HistoryNode Register(HistoryNode node)
    {
        _nodes[node.Key] = node;
        return node;
    }

    private void Unregister(HistoryNode node)
    {
        _nodes.Remove(node.Key);
        foreach (var child in node.Children) Unregister(child);
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

    public static string FolderLabel(string cwd)
    {
        string name = Path.GetFileName(cwd.Trim().TrimEnd('/', '\\'));
        return name.Length > 0 ? name : NoFolder;
    }

    /// <summary>An inner folder by its path below the opened folder ("src\App"), else by its own name.</summary>
    public static string InnerLabel(string root, string cwd)
    {
        string r = root.Trim().TrimEnd('/', '\\');
        string c = cwd.Trim().TrimEnd('/', '\\');
        if (r.Length > 0 && c.Length > r.Length + 1 && c.StartsWith(r, StringComparison.OrdinalIgnoreCase) && c[r.Length] is '/' or '\\')
            return c[(r.Length + 1)..];
        return FolderLabel(cwd);
    }

    private static string SessionLabel(string sessionId)
        => sessionId.Length == 0 ? NoSession : $"Session {(sessionId.Length > 8 ? sessionId[..8] : sessionId)}";
}
