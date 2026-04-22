using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

class ShortcutsForm : Form
{
    /// <summary>Sentinel Tag value for the hard-coded "recycle bin" node.</summary>
    private const string RecycleBinTag = "__recycle__";

    private readonly PluginTheme _theme;
    private readonly FlowLayoutPanel _listPanel;
    private readonly TreeView _folderTree;
    private readonly RoundedButton _newShortcutBtn;
    private readonly FolderSlotRow _recycleBinRow;
    private string? _expandedId;

    /// <summary>
    /// Currently selected node's tag. "" = hard-coded "main" root,
    /// RecycleBinTag = recycle bin, anything else = a normalized user folder
    /// path.
    /// </summary>
    private string _selectedFolder = "";

    public ShortcutsForm(PluginTheme theme)
    {
        _theme = theme;

        Text = "ProdToy — Shortcuts";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1200, 820);
        MinimumSize = new Size(840, 480);
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 10f);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;

        int pad = 18;

        // Toolbar
        var toolbar = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(ClientSize.Width, 56),
            BackColor = theme.BgDark,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(toolbar);

        var titleLabel = new Label
        {
            Text = "Shortcuts",
            Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, 14),
            BackColor = Color.Transparent,
        };
        toolbar.Controls.Add(titleLabel);

        _newShortcutBtn = MakeButton("+ New Shortcut", theme.Primary, Color.White);
        _newShortcutBtn.Size = new Size(140, 30);
        _newShortcutBtn.Location = new Point(pad + 220, 14);
        _newShortcutBtn.Click += (_, _) => NewShortcut();
        toolbar.Controls.Add(_newShortcutBtn);

        var hintLabel = new Label
        {
            Text = ShortcutLauncher.TryFindWindowsTerminal(out _)
                ? "Launches in Windows Terminal"
                : "Windows Terminal not found — plain cmd window will be used",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(pad + 380, 22),
            BackColor = Color.Transparent,
        };
        toolbar.Controls.Add(hintLabel);

        // Content: split container — folder tree (left) + list (right)
        int contentTop = 68;
        var split = new SplitContainer
        {
            Location = new Point(pad, contentTop),
            Size = new Size(ClientSize.Width - pad * 2, ClientSize.Height - contentTop - pad),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            Orientation = Orientation.Vertical,
            SplitterWidth = 4,
            BackColor = theme.Border,
            Panel1MinSize = 200,
            Panel2MinSize = 360,
        };
        split.SplitterDistance = 280;
        Controls.Add(split);

        // --- Left: folder tree + small toolbar ---
        // WinForms docks in REVERSE z-order: last-added Dock=Top ends up
        // topmost. Add order:
        //   1. tree         (Fill — placed after Dock edges)
        //   2. folderToolbar (Dock=Top — topmost)
        //   3. recycleBinRow (Dock=Bottom — very bottom)
        split.Panel1.BackColor = theme.BgDark;

        _folderTree = new TreeView
        {
            Dock = DockStyle.Fill,
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9.5f),
            ShowLines = false,
            ShowPlusMinus = true,
            // ShowRootLines must be true to get +/- glyphs on root-level
            // nodes. ShowLines = false still keeps connector lines hidden.
            ShowRootLines = true,
            HideSelection = false,
            FullRowSelect = true,
            Indent = 16,
            ItemHeight = 26,
        };
        _folderTree.AfterSelect += (_, e) =>
        {
            if (e.Node == null) return;
            _selectedFolder = (e.Node.Tag as string) ?? "";
            SyncSlotSelection();
            UpdateNewShortcutButtonState();
            RefreshList();
        };
        _folderTree.NodeMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                _folderTree.SelectedNode = e.Node;
                ShowTreeContextMenu(e.Node, e.Location);
            }
        };
        split.Panel1.Controls.Add(_folderTree);

        var folderToolbar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 34,
            BackColor = theme.BgDark,
        };
        split.Panel1.Controls.Add(folderToolbar);

        var folderLabel = new Label
        {
            Text = "FOLDERS",
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
            ForeColor = theme.Primary,
            AutoSize = true,
            Location = new Point(4, 10),
            BackColor = Color.Transparent,
        };
        folderToolbar.Controls.Add(folderLabel);

        var newFolderBtn = new RoundedButton
        {
            Text = "+ Folder",
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
            Size = new Size(82, 24),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextPrimary,
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        newFolderBtn.Location = new Point(folderToolbar.ClientSize.Width - 86, 4);
        newFolderBtn.FlatAppearance.BorderSize = 0;
        newFolderBtn.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        // "+ Folder" creates under the current selection (or top-level when
        // the hardcoded root "Shortcuts" is selected, since its Tag is "").
        newFolderBtn.Click += (_, _) => CreateFolder(parent: _selectedFolder);
        folderToolbar.Controls.Add(newFolderBtn);

        // recycleBinRow — pinned to the very bottom.
        _recycleBinRow = new FolderSlotRow(theme, "🗑 recycle bin") { Dock = DockStyle.Bottom };
        _recycleBinRow.Clicked += () => SelectSlot(RecycleBinTag);
        _recycleBinRow.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var menu = new ContextMenuStrip { BackColor = _theme.BgHeader, ForeColor = _theme.TextPrimary };
            var emptyItem = new ToolStripMenuItem("Empty recycle bin…")
            {
                Enabled = ShortcutsRecycleBin.Count > 0,
            };
            emptyItem.Click += (_, _) => EmptyRecycleBin();
            menu.Items.Add(emptyItem);
            menu.Show(_recycleBinRow, e.Location);
        };
        split.Panel1.Controls.Add(_recycleBinRow);

        // --- Right: shortcut list ---
        _listPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = theme.BgDark,
            Padding = new Padding(0, 4, 0, 4),
        };
        _listPanel.ClientSizeChanged += (_, _) => ResizeRows();
        split.Panel2.Controls.Add(_listPanel);

        KeyDown += OnKey;

        RebuildTree();
        UpdateNewShortcutButtonState();
        RefreshList();
    }

    /// <summary>Composite child painting to eliminate flicker.</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x02000000; // WS_EX_COMPOSITED
            return cp;
        }
    }

    private bool IsRootSelected => string.IsNullOrEmpty(_selectedFolder);
    private bool IsRecycleBinSelected => _selectedFolder == RecycleBinTag;
    private bool IsCreatableSelection => !IsRootSelected && !IsRecycleBinSelected;

    private void UpdateNewShortcutButtonState()
    {
        _newShortcutBtn.Enabled = IsCreatableSelection;
        _newShortcutBtn.BackColor = IsCreatableSelection ? _theme.Primary : _theme.PrimaryDim;
        _newShortcutBtn.ForeColor = IsCreatableSelection ? Color.White : _theme.TextSecondary;
    }

    /// <summary>
    /// Selects either the hard-coded "main" slot ("" tag) or the "recycle bin"
    /// slot (RecycleBinTag). Clears the TreeView's own selection so the slot
    /// rows own the highlight exclusively.
    /// </summary>
    private void SelectSlot(string slotTag)
    {
        _selectedFolder = slotTag;
        _folderTree.SelectedNode = null;
        SyncSlotSelection();
        UpdateNewShortcutButtonState();
        RefreshList();
    }

    private void SyncSlotSelection()
    {
        _recycleBinRow.Selected = IsRecycleBinSelected;
    }

    /// <summary>
    /// Returns to the "Shortcuts" root selection — used by Esc. The hard-coded
    /// root node's Tag is "" so AfterSelect resets _selectedFolder for us.
    /// </summary>
    private void ClearTreeSelection()
    {
        if (_folderTree.Nodes.Count > 0)
        {
            _folderTree.SelectedNode = _folderTree.Nodes[0];
        }
        else
        {
            _selectedFolder = "";
            SyncSlotSelection();
            UpdateNewShortcutButtonState();
            RefreshList();
        }
    }

    private void RefreshList()
    {
        _listPanel.SuspendLayout();
        _listPanel.Controls.Clear();

        var all = ShortcutStore.Load();

        if (IsRecycleBinSelected)
        {
            var recycled = ShortcutsRecycleBin.Load()
                .OrderByDescending(e => e.DeletedAt)
                .ToList();

            if (recycled.Count == 0)
            {
                _listPanel.Controls.Add(MakeEmptyLabel("Recycle bin is empty."));
            }
            else
            {
                foreach (var entry in recycled)
                {
                    var row = new RecycledEntryRow(entry, _theme);
                    row.RestoreRequested += () => RestoreRecycledEntry(entry.Id);
                    row.PurgeRequested += () => PurgeRecycledEntry(entry.Id);
                    _listPanel.Controls.Add(row);
                }
            }

            ResizeRows();
            _listPanel.ResumeLayout();
            return;
        }

        var filtered = all.Where(s => string.Equals(
            ShortcutFolders.Normalize(s.FolderPath),
            _selectedFolder,
            StringComparison.OrdinalIgnoreCase)).ToList();

        var ordered = filtered
            .OrderByDescending(s => s.LastLaunchedAt ?? DateTime.MinValue)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ordered.Count == 0)
        {
            string emptyText = IsRootSelected
                ? "Select a folder on the left, or create one with \"+ Folder\".\n\nShortcuts live inside folders — pick a folder to add a new shortcut."
                : $"No shortcuts in \"{_selectedFolder}\" yet.\nClick \"+ New Shortcut\" above to add one.";
            _listPanel.Controls.Add(MakeEmptyLabel(emptyText));
        }
        else
        {
            foreach (var s in ordered)
            {
                var row = new ShortcutRow(s, _theme) { Expanded = s.Id == _expandedId };
                row.RowClicked += id => ToggleExpand(id);
                row.RowDoubleClicked += id => Edit(id);
                row.LaunchRequested += id => Launch(id);
                row.ContextActionRequested += (id, anchor) => ShowRowMenu(id, anchor);
                _listPanel.Controls.Add(row);
            }
        }

        ResizeRows();
        _listPanel.ResumeLayout();

        if (_expandedId != null && !all.Any(s => s.Id == _expandedId))
            _expandedId = null;
    }

    private Label MakeEmptyLabel(string text) => new()
    {
        Text = text,
        Font = new Font("Segoe UI", 10.5f),
        ForeColor = _theme.TextSecondary,
        AutoSize = false,
        Size = new Size(_listPanel.ClientSize.Width - 24, 90),
        TextAlign = ContentAlignment.MiddleCenter,
        BackColor = Color.Transparent,
        Margin = new Padding(0, 40, 0, 0),
    };

    // ───── Folder tree ─────

    private void RebuildTree()
    {
        var all = ShortcutStore.Load();
        var allFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in all)
        {
            var p = ShortcutFolders.Normalize(s.FolderPath);
            if (string.IsNullOrEmpty(p)) continue;
            allFolders.Add(p);
            for (var parent = ShortcutFolders.ParentOf(p);
                 !string.IsNullOrEmpty(parent);
                 parent = ShortcutFolders.ParentOf(parent))
            {
                allFolders.Add(parent);
            }
        }
        foreach (var f in ShortcutFolders.Load())
            allFolders.Add(ShortcutFolders.Normalize(f));

        _folderTree.BeginUpdate();
        try
        {
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectExpanded(_folderTree.Nodes, expanded);

            _folderTree.Nodes.Clear();

            // Hard-coded "Shortcuts" root pinned at the top of the tree. All
            // user-created folders become children of it, so there's always a
            // way to return to a "no folder" state (click root) even when the
            // tree is densely filled. Tag = "" matches _selectedFolder's root
            // state and the empty-selection branches in RefreshList.
            int totalCount = all.Count;
            var rootNode = new TreeNode($"Shortcuts  ({totalCount})") { Tag = "" };
            _folderTree.Nodes.Add(rootNode);

            foreach (var path in allFolders.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var parts = path.Split('/');
                TreeNodeCollection parent = rootNode.Nodes;
                string cumulative = "";
                for (int i = 0; i < parts.Length; i++)
                {
                    cumulative = i == 0 ? parts[i] : cumulative + "/" + parts[i];
                    var existing = FindChild(parent, cumulative);
                    if (existing == null)
                    {
                        existing = new TreeNode(FormatNodeText(parts[i], cumulative, all)) { Tag = cumulative };
                        parent.Add(existing);
                    }
                    parent = existing.Nodes;
                }
            }

            rootNode.Expand();
            foreach (var key in expanded)
            {
                var node = FindNodeByPath(_folderTree.Nodes, key);
                node?.Expand();
            }

            int recycledCount = ShortcutsRecycleBin.Count;
            _recycleBinRow.Suffix = recycledCount > 0 ? $"  ({recycledCount})" : "";

            // Selection mapping:
            //   recycle bin → no TreeView selection, row shows state
            //   root ("")   → TreeView selection = rootNode
            //   user folder → TreeView selection = that node (or root if stale)
            if (IsRecycleBinSelected)
            {
                _folderTree.SelectedNode = null;
            }
            else if (IsRootSelected)
            {
                _folderTree.SelectedNode = rootNode;
            }
            else
            {
                var sel = FindNodeByPath(_folderTree.Nodes, _selectedFolder);
                if (sel != null)
                {
                    _folderTree.SelectedNode = sel;
                }
                else
                {
                    _selectedFolder = "";
                    _folderTree.SelectedNode = rootNode;
                }
            }
            SyncSlotSelection();
        }
        finally { _folderTree.EndUpdate(); }
    }

    private static void CollectExpanded(TreeNodeCollection nodes, HashSet<string> acc)
    {
        foreach (TreeNode n in nodes)
        {
            if (n.IsExpanded && n.Tag is string tag && !string.IsNullOrEmpty(tag))
                acc.Add(tag);
            CollectExpanded(n.Nodes, acc);
        }
    }

    private static TreeNode? FindChild(TreeNodeCollection nodes, string path)
    {
        foreach (TreeNode n in nodes)
            if (string.Equals(n.Tag as string, path, StringComparison.OrdinalIgnoreCase))
                return n;
        return null;
    }

    private static TreeNode? FindNodeByPath(TreeNodeCollection nodes, string path)
    {
        foreach (TreeNode n in nodes)
        {
            if (string.Equals(n.Tag as string, path, StringComparison.OrdinalIgnoreCase))
                return n;
            var inner = FindNodeByPath(n.Nodes, path);
            if (inner != null) return inner;
        }
        return null;
    }

    /// <summary>Builds the node label (with folder glyph) and a recursive count of shortcuts.</summary>
    private static string FormatNodeText(string leafName, string fullPath, List<Shortcut> all)
    {
        int count = all.Count(s => ShortcutFolders.IsSelfOrDescendant(
            ShortcutFolders.Normalize(s.FolderPath), fullPath));
        return count > 0 ? $"📁 {leafName}  ({count})" : $"📁 {leafName}";
    }

    private void ShowTreeContextMenu(TreeNode? node, Point location)
    {
        if (node == null) return;
        var path = (node.Tag as string) ?? "";
        if (string.IsNullOrEmpty(path) || path == RecycleBinTag) return;

        var menu = new ContextMenuStrip { BackColor = _theme.BgHeader, ForeColor = _theme.TextPrimary };
        menu.Items.Add("New subfolder…", null, (_, _) => CreateFolder(parent: path));
        menu.Items.Add("Rename folder…", null, (_, _) => RenameFolder(path));
        menu.Items.Add("Delete folder…", null, (_, _) => DeleteFolder(path));
        menu.Show(_folderTree, location);
    }

    private void CreateFolder(string parent)
    {
        // If the caller passed the recycle-bin sentinel (e.g. + Folder was
        // clicked while the recycle bin was selected), fall back to root.
        if (parent == RecycleBinTag) parent = "";

        var name = TextInputDialog.Prompt(this, _theme,
            "New folder",
            string.IsNullOrEmpty(parent)
                ? "Folder name"
                : $"Folder name (under \"{parent}\")");
        if (name == null) return;

        var cleaned = name.Replace('/', '_').Replace('\\', '_').Trim();
        if (string.IsNullOrEmpty(cleaned)) return;

        var full = string.IsNullOrEmpty(parent) ? cleaned : parent + "/" + cleaned;
        ShortcutFolders.Add(full);
        _selectedFolder = full;
        RebuildTree();
        UpdateNewShortcutButtonState();
        RefreshList();
    }

    private void RenameFolder(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var parent = ShortcutFolders.ParentOf(path) ?? "";
        var leaf = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        var newLeaf = TextInputDialog.Prompt(this, _theme, "Rename folder", "New name", leaf);
        if (newLeaf == null) return;
        newLeaf = newLeaf.Replace('/', '_').Replace('\\', '_').Trim();
        if (string.IsNullOrEmpty(newLeaf) || string.Equals(newLeaf, leaf, StringComparison.OrdinalIgnoreCase)) return;
        var newPath = string.IsNullOrEmpty(parent) ? newLeaf : parent + "/" + newLeaf;

        var all = ShortcutStore.Load();
        foreach (var s in all.ToList())
        {
            var normalized = ShortcutFolders.Normalize(s.FolderPath);
            if (ShortcutFolders.IsSelfOrDescendant(normalized, path))
            {
                var nextPath = ShortcutFolders.RewritePrefix(normalized, path, newPath);
                ShortcutStore.Update(s with { FolderPath = nextPath });
            }
        }
        ShortcutFolders.RenamePath(path, newPath);
        _selectedFolder = newPath;
        RebuildTree();
        UpdateNewShortcutButtonState();
        RefreshList();
    }

    private void DeleteFolder(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var all = ShortcutStore.Load();

        var shortcutsInside = all.Where(s => ShortcutFolders.IsSelfOrDescendant(
            ShortcutFolders.Normalize(s.FolderPath), path)).ToList();

        // Union every folder path under (or equal to) the anchor, from both
        // shortcuts and the persisted folder store. We preserve them all so
        // restoring rebuilds the exact hierarchy, including empty subfolders.
        var allFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in all)
        {
            var p = ShortcutFolders.Normalize(s.FolderPath);
            if (!string.IsNullOrEmpty(p)) allFolders.Add(p);
        }
        foreach (var f in ShortcutFolders.Load())
            allFolders.Add(ShortcutFolders.Normalize(f));
        allFolders.Add(path);
        var subfolders = allFolders
            .Where(p => ShortcutFolders.IsSelfOrDescendant(p, path))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string msg;
        int subCount = Math.Max(0, subfolders.Count - 1);
        if (shortcutsInside.Count == 0 && subCount == 0)
        {
            msg = $"Move folder \"{path}\" to recycle bin?";
        }
        else
        {
            var parts = new List<string>();
            if (subCount > 0) parts.Add($"{subCount} subfolder(s)");
            if (shortcutsInside.Count > 0) parts.Add($"{shortcutsInside.Count} shortcut(s)");
            msg = $"Move \"{path}\" to recycle bin?\n\nThis includes {string.Join(" and ", parts)}.\nYou can restore it from the recycle bin later.";
        }

        var res = MessageBox.Show(this, msg, "Move to recycle bin",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (res != DialogResult.Yes) return;

        ShortcutsRecycleBin.Add(new RecycleBinEntry
        {
            FolderPath = path,
            Subfolders = subfolders,
            Shortcuts = shortcutsInside,
        });

        foreach (var s in shortcutsInside)
            ShortcutStore.Delete(s.Id);
        ShortcutFolders.RemoveRecursive(path);

        _selectedFolder = ShortcutFolders.ParentOf(path) ?? "";
        RebuildTree();
        UpdateNewShortcutButtonState();
        RefreshList();
    }

    private void RestoreRecycledEntry(string id)
    {
        var entry = ShortcutsRecycleBin.Get(id);
        if (entry == null) return;

        foreach (var f in entry.Subfolders)
            ShortcutFolders.Add(f);
        if (!string.IsNullOrEmpty(entry.FolderPath))
            ShortcutFolders.Add(entry.FolderPath);
        foreach (var s in entry.Shortcuts)
            ShortcutStore.Add(s);

        ShortcutsRecycleBin.Remove(id);

        _selectedFolder = entry.FolderPath;
        RebuildTree();
        UpdateNewShortcutButtonState();
        RefreshList();
    }

    private void PurgeRecycledEntry(string id)
    {
        var entry = ShortcutsRecycleBin.Get(id);
        if (entry == null) return;
        var res = MessageBox.Show(this,
            $"Permanently delete \"{entry.FolderPath}\" and its contents?\n\nThis cannot be undone.",
            "Delete permanently",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (res != DialogResult.Yes) return;
        ShortcutsRecycleBin.Remove(id);
        RebuildTree();
        RefreshList();
    }

    private void EmptyRecycleBin()
    {
        int n = ShortcutsRecycleBin.Count;
        if (n == 0) return;
        var res = MessageBox.Show(this,
            $"Permanently delete all {n} item(s) from the recycle bin?\n\nThis cannot be undone.",
            "Empty recycle bin",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (res != DialogResult.Yes) return;
        ShortcutsRecycleBin.Clear();
        RebuildTree();
        RefreshList();
    }

    private void ResizeRows()
    {
        int w = _listPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 6;
        if (w < 120) w = 120;
        foreach (Control c in _listPanel.Controls)
        {
            if (c is ShortcutRow or RecycledEntryRow or Label)
                c.Width = w;
        }
    }

    private void ToggleExpand(string id)
    {
        _expandedId = _expandedId == id ? null : id;
        foreach (Control c in _listPanel.Controls)
            if (c is ShortcutRow r) r.Expanded = r.ShortcutId == _expandedId;
    }

    private void NewShortcut()
    {
        if (!IsCreatableSelection) return;
        using var dlg = new ShortcutEditForm(_theme, defaultFolder: _selectedFolder);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
        {
            ShortcutStore.Add(dlg.Result);
            _expandedId = dlg.Result.Id;
            RebuildTree();
            RefreshList();
        }
    }

    private void Edit(string id)
    {
        var cur = ShortcutStore.Get(id);
        if (cur == null) return;
        using var dlg = new ShortcutEditForm(_theme, cur);
        var dr = dlg.ShowDialog(this);
        if (dr != DialogResult.OK) return;
        if (dlg.DeleteRequested)
        {
            ShortcutStore.Delete(id);
            if (_expandedId == id) _expandedId = null;
        }
        else if (dlg.Result != null)
        {
            ShortcutStore.Update(dlg.Result);
        }
        RebuildTree();
        RefreshList();
    }

    private void Duplicate(string id)
    {
        var src = ShortcutStore.Get(id);
        if (src == null) return;
        var copy = src with
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = src.Name + " (copy)",
            CreatedAt = DateTime.Now,
            UpdatedAt = null,
            LastLaunchedAt = null,
            LaunchCount = 0,
        };
        ShortcutStore.Add(copy);
        _expandedId = copy.Id;
        RebuildTree();
        RefreshList();
    }

    private void Delete(string id)
    {
        var res = MessageBox.Show(this, "Delete this shortcut?", "Confirm Delete",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (res != DialogResult.Yes) return;
        ShortcutStore.Delete(id);
        if (_expandedId == id) _expandedId = null;
        RebuildTree();
        RefreshList();
    }

    private void MoveShortcutToFolder(string id, string targetFolder)
    {
        var cur = ShortcutStore.Get(id);
        if (cur == null) return;
        targetFolder = ShortcutFolders.Normalize(targetFolder);
        ShortcutStore.Update(cur with { FolderPath = targetFolder });
        if (!string.IsNullOrEmpty(targetFolder))
            ShortcutFolders.Add(targetFolder);
        RebuildTree();
        RefreshList();
    }

    private void Launch(string id)
    {
        var cur = ShortcutStore.Get(id);
        if (cur == null) return;
        var result = ShortcutLauncher.Launch(cur);
        if (!result.Ok)
        {
            MessageBox.Show(this, result.ErrorMessage ?? "Launch failed.", "Launch failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            var updated = ShortcutStore.Get(id);
            if (updated != null)
            {
                foreach (Control c in _listPanel.Controls)
                    if (c is ShortcutRow r && r.ShortcutId == id) r.UpdateEntry(updated);
            }
        }
    }

    private void OpenFolder(string id)
    {
        var cur = ShortcutStore.Get(id);
        if (cur == null || !Directory.Exists(cur.WorkingDirectory)) return;
        try { Process.Start(new ProcessStartInfo(cur.WorkingDirectory) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open folder failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowRowMenu(string id, Control anchor)
    {
        var cur = ShortcutStore.Get(id);
        if (cur == null) return;
        var menu = new ContextMenuStrip { BackColor = _theme.BgHeader, ForeColor = _theme.TextPrimary };
        menu.Items.Add("Launch", null, (_, _) => Launch(id));
        menu.Items.Add("Edit…", null, (_, _) => Edit(id));
        menu.Items.Add("Duplicate", null, (_, _) => Duplicate(id));
        menu.Items.Add(new ToolStripSeparator());

        // Move-to-folder submenu — lists every known non-root folder so users
        // can re-home a shortcut without editing.
        var moveMenu = new ToolStripMenuItem("Move to folder");
        var knownFolders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in ShortcutStore.Load())
        {
            var p = ShortcutFolders.Normalize(s.FolderPath);
            if (!string.IsNullOrEmpty(p)) knownFolders.Add(p);
        }
        foreach (var f in ShortcutFolders.Load())
        {
            var p = ShortcutFolders.Normalize(f);
            if (!string.IsNullOrEmpty(p)) knownFolders.Add(p);
        }

        if (knownFolders.Count == 0)
        {
            var none = new ToolStripMenuItem("(no folders yet — create one first)") { Enabled = false };
            moveMenu.DropDownItems.Add(none);
        }
        else
        {
            foreach (var folder in knownFolders)
            {
                string f = folder;
                var item = new ToolStripMenuItem(f) { Checked = string.Equals(cur.FolderPath, f, StringComparison.OrdinalIgnoreCase) };
                item.Click += (_, _) => MoveShortcutToFolder(id, f);
                moveMenu.DropDownItems.Add(item);
            }
        }
        menu.Items.Add(moveMenu);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open folder in Explorer", null, (_, _) => OpenFolder(id));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Delete", null, (_, _) => Delete(id));
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.N && IsCreatableSelection) { NewShortcut(); e.Handled = true; return; }
        if (e.KeyCode == Keys.Escape && ActiveControl is not TextBox)
        {
            if (_expandedId != null)
            {
                _expandedId = null;
                foreach (Control c in _listPanel.Controls)
                    if (c is ShortcutRow r) r.Expanded = false;
                e.Handled = true;
                return;
            }
            if (_folderTree.SelectedNode != null)
            {
                ClearTreeSelection();
                e.Handled = true;
            }
        }
    }

    private RoundedButton MakeButton(string text, Color bg, Color fg)
    {
        var b = new RoundedButton
        {
            Text = text,
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            Size = new Size(110, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = bg,
            ForeColor = fg,
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = _theme.PrimaryLight;
        return b;
    }
}

/// <summary>
/// One shortcut row in the list.
/// Collapsed: name, working dir, args, [Launch] and [⋯] buttons on the right.
/// Expanded: + notes, last launched, launch count.
/// </summary>
class ShortcutRow : Panel
{
    private const int CollapsedHeight = 72;
    private const int ExpandedHeight = 168;

    private Shortcut _shortcut;
    private readonly PluginTheme _theme;
    private readonly RoundedButton _launchBtn;
    private readonly Button _menuBtn;
    private bool _expanded;
    private bool _hover;

    public string ShortcutId => _shortcut.Id;

    public event Action<string>? RowClicked;
    public event Action<string>? RowDoubleClicked;
    public event Action<string>? LaunchRequested;
    public event Action<string, Control>? ContextActionRequested;

    public bool Expanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            Height = _expanded ? ExpandedHeight : CollapsedHeight;
            Invalidate();
        }
    }

    public void UpdateEntry(Shortcut s)
    {
        _shortcut = s;
        Invalidate();
    }

    public ShortcutRow(Shortcut s, PluginTheme theme)
    {
        _shortcut = s;
        _theme = theme;
        Margin = new Padding(6, 3, 6, 3);
        BackColor = theme.BgDark;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _launchBtn = new RoundedButton
        {
            Text = "▶ Launch",
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
            Size = new Size(100, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.Primary,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        _launchBtn.FlatAppearance.BorderSize = 0;
        _launchBtn.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        _launchBtn.Click += (_, _) => LaunchRequested?.Invoke(_shortcut.Id);
        Controls.Add(_launchBtn);

        _menuBtn = new Button
        {
            Text = "⋯",
            Font = new Font("Segoe UI Semibold", 14f, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat,
            ForeColor = theme.TextSecondary,
            BackColor = theme.BgDark,
            Size = new Size(30, 32),
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        _menuBtn.FlatAppearance.BorderSize = 0;
        _menuBtn.FlatAppearance.MouseOverBackColor = theme.BgHeader;
        _menuBtn.Click += (_, _) => ContextActionRequested?.Invoke(_shortcut.Id, _menuBtn);
        Controls.Add(_menuBtn);

        Height = CollapsedHeight;

        Click += (_, _) => RowClicked?.Invoke(_shortcut.Id);
        DoubleClick += (_, _) => RowDoubleClicked?.Invoke(_shortcut.Id);
        MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        MouseLeave += (_, _) => { _hover = false; Invalidate(); };
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_launchBtn == null || _menuBtn == null) return;
        _menuBtn.Location = new Point(Width - _menuBtn.Width - 10, 20);
        _launchBtn.Location = new Point(Width - _menuBtn.Width - 10 - _launchBtn.Width - 6, 20);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_launchBtn == null || _menuBtn == null) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedRect(rect, 8);
        Color bg = _expanded ? _theme.BgHeader : (_hover ? _theme.BgHeader : _theme.BgDark);
        using (var bgBrush = new SolidBrush(bg)) g.FillPath(bgBrush, path);
        if (_expanded)
        {
            using var pen = new Pen(_theme.Primary, 1.5f);
            g.DrawPath(pen, path);
        }

        int textLeft = 20;
        int textRight = _launchBtn.Left - 14;
        if (textRight < textLeft + 60) textRight = Width - 40;

        string name = string.IsNullOrWhiteSpace(_shortcut.Name) ? "(untitled)" : _shortcut.Name;
        using (var titleFont = new Font("Segoe UI Semibold", 11f, FontStyle.Bold))
        using (var tbrush = new SolidBrush(_theme.TextPrimary))
        using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        {
            g.DrawString(name, titleFont, tbrush, new RectangleF(textLeft, 10, textRight - textLeft, 24), sf);
        }

        if (_shortcut.RequireAdmin)
        {
            using var tf = new Font("Segoe UI Semibold", 8f, FontStyle.Bold);
            using var nameFont = new Font("Segoe UI Semibold", 11f, FontStyle.Bold);
            var nameSize = g.MeasureString(name, nameFont);
            const string tagText = "ADMIN";
            var tagSize = g.MeasureString(tagText, tf);
            int tagW = (int)tagSize.Width + 14;
            int tagH = 18;
            int tagX = textLeft + (int)nameSize.Width + 10;
            int tagY = 15;
            if (tagX + tagW < textRight)
            {
                var tagColor = Color.FromArgb(0xE6, 0xA5, 0x3A);
                using var tagPath = RoundedRect(new Rectangle(tagX, tagY, tagW, tagH), 3);
                using var fillBrush = new SolidBrush(tagColor);
                g.FillPath(fillBrush, tagPath);
                using var textBrush = new SolidBrush(Color.White);
                using var sfCenter = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(tagText, tf, textBrush, new RectangleF(tagX, tagY, tagW, tagH), sfCenter);
            }
        }

        using (var subFont = new Font("Segoe UI", 9f))
        using (var subBrush = new SolidBrush(_theme.TextSecondary))
        using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisPath, FormatFlags = StringFormatFlags.NoWrap })
        {
            g.DrawString(_shortcut.WorkingDirectory ?? "",
                subFont, subBrush, new RectangleF(textLeft, 36, textRight - textLeft, 18), sf);
        }

        string launcher;
        if (_shortcut.LauncherMode == LauncherMode.WindowsTerminal)
        {
            string baseLabel = string.IsNullOrEmpty(_shortcut.WtProfile) ? "wt" : $"wt · {_shortcut.WtProfile}";
            launcher = _shortcut.WtWindowTarget == WtWindowTarget.ExistingWindow
                ? $"{baseLabel} · tab"
                : baseLabel;
        }
        else
        {
            launcher = "cmd";
        }
        var profile = LaunchProfiles.GetOrDefault(_shortcut.Profile);
        string cmd = string.IsNullOrEmpty(profile.Command) ? "custom" : profile.Command;
        string thirdLine = $"{cmd} {_shortcut.Args}".Trim();
        thirdLine = $"{launcher}  •  {thirdLine}";
        using (var smFont = new Font("Segoe UI", 8.5f))
        using (var smBrush = new SolidBrush(_theme.TextSecondary))
        using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        {
            g.DrawString(thirdLine, smFont, smBrush, new RectangleF(textLeft, 52, textRight - textLeft, 18), sf);
        }

        if (_expanded)
            DrawExpanded(g, textLeft, Width - textLeft - 20);
    }

    private void DrawExpanded(Graphics g, int left, int availableWidth)
    {
        int y = CollapsedHeight + 2;
        using var rule = new Pen(_theme.Border, 1);
        g.DrawLine(rule, left, y, left + availableWidth, y);
        y += 8;

        using var keyFont = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold);
        using var valFont = new Font("Segoe UI", 9.25f);
        using var keyBrush = new SolidBrush(_theme.TextSecondary);
        using var valBrush = new SolidBrush(_theme.TextPrimary);

        void Row(string key, string val, ref int yy)
        {
            if (string.IsNullOrWhiteSpace(val)) return;
            g.DrawString(key.ToUpperInvariant(), keyFont, keyBrush, new PointF(left, yy));
            using var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(val, valFont, valBrush, new RectangleF(left + 110, yy - 2, availableWidth - 110, 20), sf);
            yy += 20;
        }

        if (!string.IsNullOrWhiteSpace(_shortcut.Notes))
            Row("Notes", _shortcut.Notes, ref y);

        Row("Last launched",
            _shortcut.LastLaunchedAt?.ToString("ddd, MMM d yyyy  HH:mm") ?? "—", ref y);
        Row("Launch count", _shortcut.LaunchCount.ToString(), ref y);
        Row("Created", _shortcut.CreatedAt.ToString("ddd, MMM d yyyy"), ref y);
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Min(radius, Math.Min(rect.Width, rect.Height)) * 2;
        if (d <= 0) { path.AddRectangle(rect); return path; }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>
/// Fixed "slot" row used for the hard-coded "main" and "recycle bin" entries
/// in the Shortcuts folder panel. Lives outside the TreeView so it
/// renders reliably; paints its own themed background + label and raises
/// <see cref="Clicked"/> when pressed.
/// </summary>
class FolderSlotRow : Panel
{
    private const int ChevronRegionWidth = 20;

    private readonly PluginTheme _theme;
    private readonly string _baseLabel;
    private bool _selected;
    private bool _hover;
    private string _suffix = "";
    private bool _showChevron;
    private bool _chevronExpanded = true;

    public event Action? Clicked;

    /// <summary>Raised when the user clicks the chevron region (left edge).</summary>
    public event Action? ChevronClicked;

    public bool Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; Invalidate(); }
    }

    /// <summary>Appended to the base label — e.g. "  (5)" to show a count.</summary>
    public string Suffix
    {
        get => _suffix;
        set { if (_suffix == value) return; _suffix = value ?? ""; Invalidate(); }
    }

    /// <summary>Whether to draw an expand/collapse chevron on the left edge.</summary>
    public bool ShowChevron
    {
        get => _showChevron;
        set { if (_showChevron == value) return; _showChevron = value; Invalidate(); }
    }

    /// <summary>True = chevron points down (▾, "expanded"); false = right (▸, "collapsed").</summary>
    public bool ChevronExpanded
    {
        get => _chevronExpanded;
        set { if (_chevronExpanded == value) return; _chevronExpanded = value; Invalidate(); }
    }

    public FolderSlotRow(PluginTheme theme, string label)
    {
        _theme = theme;
        _baseLabel = label;
        Height = 30;
        Cursor = Cursors.Hand;
        BackColor = theme.BgHeader;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        MouseLeave += (_, _) => { _hover = false; Invalidate(); };
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;

        if (_showChevron && e.X < ChevronRegionWidth)
            ChevronClicked?.Invoke();
        else
            Clicked?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var bg = _selected ? _theme.Primary
               : _hover ? _theme.BgDark
               : _theme.BgHeader;
        using (var brush = new SolidBrush(bg))
            g.FillRectangle(brush, ClientRectangle);

        var fg = _selected ? Color.White : _theme.TextPrimary;
        int textLeft = 10;

        if (_showChevron)
        {
            using var chevFont = new Font("Segoe UI", 10f, FontStyle.Bold);
            using var chevBrush = new SolidBrush(fg);
            string chev = _chevronExpanded ? "▾" : "▸";
            using var sfChev = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                Alignment = StringAlignment.Center,
            };
            g.DrawString(chev, chevFont, chevBrush,
                new RectangleF(0, 0, ChevronRegionWidth, Height), sfChev);
            textLeft = ChevronRegionWidth + 2;
        }

        // Use TextRenderer (GDI) instead of Graphics.DrawString (GDI+) so the
        // emoji in "🗑 recycle bin" falls back to Segoe UI Emoji when the
        // label font doesn't carry that glyph. Without this, users whose
        // Segoe UI Semibold lacks 🗑 see a tofu box.
        using var font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
        var bounds = new Rectangle(textLeft, 0, Width - textLeft - 10, Height);
        TextRenderer.DrawText(g, _baseLabel + _suffix, font, bounds, fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter
            | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
            | TextFormatFlags.NoPadding);
    }
}

/// <summary>
/// Row representing a single recycle-bin entry in the right-hand list.
/// Shows the recycled folder path, a summary of contents and the deleted
/// timestamp, with "Restore" and permanent-delete buttons.
/// </summary>
class RecycledEntryRow : Panel
{
    private const int RowHeight = 72;

    private readonly RecycleBinEntry _entry;
    private readonly PluginTheme _theme;
    private readonly RoundedButton _restoreBtn;
    private readonly Button _purgeBtn;

    public event Action? RestoreRequested;
    public event Action? PurgeRequested;

    public RecycledEntryRow(RecycleBinEntry entry, PluginTheme theme)
    {
        _entry = entry;
        _theme = theme;
        Height = RowHeight;
        Margin = new Padding(6, 3, 6, 3);
        BackColor = theme.BgDark;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _restoreBtn = new RoundedButton
        {
            Text = "↶ Restore",
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
            Size = new Size(100, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.Primary,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        _restoreBtn.FlatAppearance.BorderSize = 0;
        _restoreBtn.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        _restoreBtn.Click += (_, _) => RestoreRequested?.Invoke();
        Controls.Add(_restoreBtn);

        _purgeBtn = new Button
        {
            Text = "🗑",
            Font = new Font("Segoe UI Emoji", 11f, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat,
            ForeColor = theme.ErrorColor,
            BackColor = theme.BgDark,
            Size = new Size(32, 32),
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        _purgeBtn.FlatAppearance.BorderSize = 0;
        _purgeBtn.FlatAppearance.MouseOverBackColor = theme.BgHeader;
        _purgeBtn.Click += (_, _) => PurgeRequested?.Invoke();
        Controls.Add(_purgeBtn);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_restoreBtn == null || _purgeBtn == null) return;
        _purgeBtn.Location = new Point(Width - _purgeBtn.Width - 10, 20);
        _restoreBtn.Location = new Point(Width - _purgeBtn.Width - 10 - _restoreBtn.Width - 6, 20);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_restoreBtn == null || _purgeBtn == null) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedRectPath(rect, 8);
        using (var bg = new SolidBrush(_theme.BgHeader))
            g.FillPath(bg, path);

        int textLeft = 20;
        int textRight = _restoreBtn.Left - 14;
        if (textRight < textLeft + 60) textRight = Width - 40;

        // TextRenderer (GDI) for the title so the 📁 emoji falls back to
        // Segoe UI Emoji when the label font doesn't have it (prevents tofu).
        using var titleFont = new Font("Segoe UI Semibold", 11f, FontStyle.Bold);
        TextRenderer.DrawText(g, $"📁 {_entry.FolderPath}", titleFont,
            new Rectangle(textLeft, 10, textRight - textLeft, 24),
            _theme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.Top
            | TextFormatFlags.SingleLine | TextFormatFlags.PathEllipsis
            | TextFormatFlags.NoPadding);

        int subCount = Math.Max(0, _entry.Subfolders.Count - 1);
        var parts = new List<string>();
        if (subCount > 0) parts.Add($"{subCount} subfolder" + (subCount == 1 ? "" : "s"));
        parts.Add($"{_entry.Shortcuts.Count} shortcut" + (_entry.Shortcuts.Count == 1 ? "" : "s"));
        string summary = string.Join("  ·  ", parts);

        using var subFont = new Font("Segoe UI", 9f);
        using var subBrush = new SolidBrush(_theme.TextSecondary);
        using var sfChar = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(summary, subFont, subBrush,
            new RectangleF(textLeft, 36, textRight - textLeft, 18), sfChar);

        string when = $"deleted {_entry.DeletedAt:ddd, MMM d yyyy  HH:mm}";
        using var whenFont = new Font("Segoe UI", 8.5f);
        g.DrawString(when, whenFont, subBrush,
            new RectangleF(textLeft, 52, textRight - textLeft, 18), sfChar);
    }

    private static GraphicsPath RoundedRectPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Min(radius, Math.Min(rect.Width, rect.Height)) * 2;
        if (d <= 0) { path.AddRectangle(rect); return path; }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
