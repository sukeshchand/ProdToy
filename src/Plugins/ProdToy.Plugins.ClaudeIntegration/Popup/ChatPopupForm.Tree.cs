using System.Drawing;

namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>The tree's pins, tags and width, and the pager's First and Last.</summary>
sealed partial class ChatPopupForm
{
    private const int DefaultTreeWidth = 340;

    private int _dragStartX;
    private int _dragStartWidth;
    private bool _dragging;

    // ---------- the splitter ----------

    private int WidestTree => Math.Max(MinTreeWidth, Math.Min(ClientSize.Width / 2, ClientSize.Width - StripWidth - 480));

    private void WireSplitter()
    {
        _treeSplitter.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _dragStartX = Cursor.Position.X;
            _dragStartWidth = _treeWidth;
        };
        _treeSplitter.MouseMove += (_, _) =>
        {
            if (_dragging) SizeTree(_dragStartWidth + Cursor.Position.X - _dragStartX);
        };
        _treeSplitter.MouseUp += (_, _) =>
        {
            if (!_dragging) return;
            _dragging = false;
            RememberTreeWidth();
        };
        _treeSplitter.DoubleClick += (_, _) =>
        {
            SizeTree(DefaultTreeWidth);
            RememberTreeWidth();
        };
    }

    /// <summary>Moves the line between the tree and the right pane; the window keeps its size.</summary>
    private void SizeTree(int width)
    {
        _treeWidth = Math.Clamp(width, MinTreeWidth, Math.Max(MinTreeWidth, WidestTree));
        _treePanel.Width = _treeWidth;
        ApplyMinimumWidth();
    }

    private void RememberTreeWidth()
    {
        var s = _context.LoadSettings<ClaudePluginSettings>();
        _context.SaveSettings(s with { HistoryTreeWidth = _treeWidth });
    }

    // ---------- First and Last ----------

    private void JumpHistory(bool last)
    {
        var items = ScopeItems();
        if (items.Count == 0) return;

        // The list is latest first: Last = the newest (index 0), First = the oldest.
        LoadItem(items[last ? PagerPosition.Newest(items.Count) : PagerPosition.Oldest(items.Count)]);
        RememberScope();
        SyncTree();
    }

    // ---------- pins and tags ----------

    private void OnNodeMenu(HistoryNode node, Point screen)
    {
        var target = node.Kind == HistoryNodeKind.Pin ? _tree.Find(node.Target) : node;
        var menu = new ContextMenuStrip();

        // The Folders header chooses how its folders group.
        if (ReferenceEquals(node, _tree.Folders))
        {
            var group = new ToolStripMenuItem("Group folders by");
            foreach (var view in new[] { FolderView.Opened, FolderView.Working, FolderView.FolderFirst })
            {
                var choice = new ToolStripMenuItem(FolderViews.Title(view)) { Checked = _tree.FolderView == view };
                choice.Click += (_, _) => SetFolderView(view);
                group.DropDownItems.Add(choice);
            }

            menu.Items.Add(group);
        }

        // Page here: the node's whole subtree becomes the pager's list (a day, an opened folder with every inner folder, ...).
        if (node.Kind is HistoryNodeKind.Root or HistoryNodeKind.Branch or HistoryNodeKind.Date or HistoryNodeKind.Group or HistoryNodeKind.Tag
            && !ReferenceEquals(node, _tree.Pinned))
            menu.Items.Add("Page here", null, (_, _) => OnNodePicked(node));

        if (node.Kind == HistoryNodeKind.Pin)
            menu.Items.Add("Unpin", null, (_, _) => Unpin(node.Target!));
        else if (node.Kind is HistoryNodeKind.Date or HistoryNodeKind.Group or HistoryNodeKind.Item or HistoryNodeKind.Tag && _tree.PinStore is { } pins)
        {
            if (pins.Contains(node.Key)) menu.Items.Add("Unpin", null, (_, _) => Unpin(node.Key));
            else menu.Items.Add("Pin", null, (_, _) => Pin(node));
        }

        if (target != null && _tree.CanTag(target))
            menu.Items.Add("Tag...", null, (_, _) => EditTags(target));

        if (menu.Items.Count == 0)
        {
            menu.Dispose();
            return;
        }

        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(screen);
    }

    /// <summary>Regroups the Folders branch and remembers the choice; the scope falls back to its nearest ancestor that still exists.</summary>
    private void SetFolderView(FolderView view)
    {
        var s = _context.LoadSettings<ClaudePluginSettings>();
        _context.SaveSettings(s with { HistoryFolderView = FolderViews.ToSetting(view) });
        _tree.SetFolderView(view);
        if (_scope != null) _scope = _tree.FindOrAncestor(_scope.Key);
        RememberScope();
        _treePanel.RefreshRows();
        SyncTree();
        if (_scope != null && _itemKey != null && _tree.ItemNode(_itemKey, _scope) is { } node) _treePanel.Reveal(node);
    }

    private void Pin(HistoryNode node)
    {
        _tree.PinStore?.Add(node.Key, _tree.PinLabel(node));
        _tree.SyncPins();
        _treePanel.RefreshRows();
    }

    private void Unpin(string key)
    {
        _tree.PinStore?.Remove(key);
        _tree.SyncPins();
        _treePanel.RefreshRows();
    }

    private void EditTags(HistoryNode node)
    {
        if (_tree.TagStore is not { } store) return;

        var current = _tree.TagsOf(node).Select(t => t.Id);
        using var dialog = new TagDialog(_theme, store, current, $"Tags of {node.Label}");
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            if (node.IsItem) store.SetItem(HistoryTree.ItemKey(node.Item!), dialog.Chosen);
            else store.SetFolder(node.GroupId, dialog.Chosen);
        }

        // A tag may have gone even on Cancel: deleting one is done at once.
        _tree.SyncTags();
        if (_scope != null && _tree.Find(_scope.Key) == null) _scope = _tree.FindOrAncestor(_scope.Key);
        SyncTree();
    }

    /// <summary>The right pane's chips: the item's own tags and its folder's.</summary>
    private void ShowTagChips()
    {
        _tagChips.SetTags(_itemKey != null && _tree.IsReady ? _tree.TagsOfItem(_itemKey) : Array.Empty<TreeTag>());
        _tagChips.Location = new Point(Math.Max(0, _infoPanel.ClientSize.Width - 12 - _tagChips.Width), InfoBarHeight - 20);
    }
}
