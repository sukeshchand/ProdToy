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

        LoadItem(items[last ? items.Count - 1 : 0]);
        RememberScope();
        SyncTree();
    }

    // ---------- pins and tags ----------

    private void OnNodeMenu(HistoryNode node, Point screen)
    {
        var target = node.Kind == HistoryNodeKind.Pin ? _tree.Find(node.Target) : node;
        var menu = new ContextMenuStrip();

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
        if (_scope != null && _tree.Find(_scope.Key) == null) _scope = _tree.Root;
        SyncTree();
    }

    /// <summary>The right pane's chips: the item's own tags and its folder's.</summary>
    private void ShowTagChips()
    {
        _tagChips.SetTags(_itemKey != null && _tree.IsReady ? _tree.TagsOfItem(_itemKey) : Array.Empty<TreeTag>());
        _tagChips.Location = new Point(Math.Max(0, _infoPanel.ClientSize.Width - 12 - _tagChips.Width), InfoBarHeight - 20);
    }
}
