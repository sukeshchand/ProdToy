using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ClaudeIntegration;

class ClaudeShortcutsForm : Form
{
    private readonly PluginTheme _theme;
    private readonly FlowLayoutPanel _listPanel;
    private readonly TreeView _folderTree;
    private string? _expandedId;
    /// <summary>Currently selected folder path ("" = root = "All").</summary>
    private string _selectedFolder = "";

    public ClaudeShortcutsForm(PluginTheme theme)
    {
        _theme = theme;

        Text = "ProdToy — Claude Shortcuts";
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
            Text = "Claude Shortcuts",
            Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, 14),
            BackColor = Color.Transparent,
        };
        toolbar.Controls.Add(titleLabel);

        var newBtn = MakeButton("+ New Shortcut", theme.Primary, Color.White);
        newBtn.Size = new Size(140, 30);
        newBtn.Location = new Point(pad + 220, 14);
        newBtn.Click += (_, _) => NewShortcut();
        toolbar.Controls.Add(newBtn);

        var hintLabel = new Label
        {
            Text = ClaudeShortcutLauncher.TryFindWindowsTerminal(out _)
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
            Panel1MinSize = 180,
            Panel2MinSize = 360,
        };
        split.SplitterDistance = 260;
        Controls.Add(split);

        // --- Left: folder tree + small toolbar ---
        split.Panel1.BackColor = theme.BgDark;

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
            Location = new Point(0, 4),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextPrimary,
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        newFolderBtn.Location = new Point(folderToolbar.ClientSize.Width - 86, 4);
        newFolderBtn.FlatAppearance.BorderSize = 0;
        newFolderBtn.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        newFolderBtn.Click += (_, _) => CreateFolder(parent: _selectedFolder);
        folderToolbar.Controls.Add(newFolderBtn);

        _folderTree = new TreeView
        {
            Dock = DockStyle.Fill,
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9.5f),
            ShowLines = false,
            ShowPlusMinus = false, // we draw our own chevron
            ShowRootLines = false,
            HideSelection = false,
            FullRowSelect = true,
            Indent = 16,
            ItemHeight = 26,
            DrawMode = TreeViewDrawMode.OwnerDrawAll,
        };
        _folderTree.DrawNode += OnDrawTreeNode;
        // Single-click on a node with children toggles expand when clicked on
        // the chevron area — keeps the UX close to normal tree behavior now
        // that we've turned off the built-in +/- indicator.
        _folderTree.NodeMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && e.Node != null && e.Node.Nodes.Count > 0)
            {
                int chevronLeft = (e.Node.Level + 1) * _folderTree.Indent - 4;
                int chevronRight = chevronLeft + 18;
                if (e.X >= chevronLeft && e.X <= chevronRight)
                {
                    e.Node.Toggle();
                }
            }
        };
        _folderTree.AfterSelect += (_, e) =>
        {
            _selectedFolder = (e.Node?.Tag as string) ?? "";
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

    private void RefreshList()
    {
        _listPanel.SuspendLayout();
        _listPanel.Controls.Clear();

        var all = ClaudeShortcutStore.Load();
        // Filter by selected folder: root (empty) shows everything;
        // non-empty shows the folder itself + descendants.
        var filtered = string.IsNullOrEmpty(_selectedFolder)
            ? all
            : all.Where(s => ClaudeShortcutFolders.IsSelfOrDescendant(
                ClaudeShortcutFolders.Normalize(s.FolderPath), _selectedFolder)).ToList();

        var ordered = filtered
            .OrderByDescending(s => s.LastLaunchedAt ?? DateTime.MinValue)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ordered.Count == 0)
        {
            var emptyText = string.IsNullOrEmpty(_selectedFolder)
                ? "No shortcuts yet. Click \"+ New Shortcut\" to add your first project."
                : $"No shortcuts in \"{_selectedFolder}\".";
            var empty = new Label
            {
                Text = emptyText,
                Font = new Font("Segoe UI", 10.5f),
                ForeColor = _theme.TextSecondary,
                AutoSize = false,
                Size = new Size(_listPanel.ClientSize.Width - 24, 60),
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 30, 0, 0),
            };
            _listPanel.Controls.Add(empty);
        }
        else
        {
            foreach (var s in ordered)
            {
                var row = new ClaudeShortcutRow(s, _theme) { Expanded = s.Id == _expandedId };
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

    // ───── Folder tree ─────

    private void RebuildTree()
    {
        var all = ClaudeShortcutStore.Load();
        var shortcutCountByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var allFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Union of live folder paths (from shortcuts) + persisted empty folders.
        foreach (var s in all)
        {
            var p = ClaudeShortcutFolders.Normalize(s.FolderPath);
            if (!string.IsNullOrEmpty(p))
            {
                allFolders.Add(p);
                // Ensure ancestors are represented too so "Work/Backend" creates "Work".
                for (var parent = ClaudeShortcutFolders.ParentOf(p); !string.IsNullOrEmpty(parent);
                     parent = ClaudeShortcutFolders.ParentOf(parent))
                {
                    allFolders.Add(parent);
                }
            }
            if (!string.IsNullOrEmpty(p))
                shortcutCountByPath[p] = shortcutCountByPath.GetValueOrDefault(p) + 1;
        }
        foreach (var f in ClaudeShortcutFolders.Load())
            allFolders.Add(ClaudeShortcutFolders.Normalize(f));

        _folderTree.BeginUpdate();
        try
        {
            // Preserve expansion state so the tree doesn't jarringly collapse on refresh.
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectExpanded(_folderTree.Nodes, expanded);

            _folderTree.Nodes.Clear();

            int totalCount = all.Count;
            var root = new TreeNode($"All  ({totalCount})") { Tag = "" };
            _folderTree.Nodes.Add(root);

            // Sorted alphabetical for stability.
            foreach (var path in allFolders.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var parts = path.Split('/');
                TreeNodeCollection parent = root.Nodes;
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

            root.Expand();
            foreach (var key in expanded)
            {
                var node = FindNodeByPath(_folderTree.Nodes, key);
                node?.Expand();
            }

            // Re-select the previously selected folder if it still exists; else root.
            var sel = FindNodeByPath(_folderTree.Nodes, _selectedFolder) ?? root;
            _folderTree.SelectedNode = sel;
            _selectedFolder = (sel.Tag as string) ?? "";
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

    /// <summary>Builds the node label with the count of shortcuts under this path (recursive).</summary>
    private static string FormatNodeText(string leafName, string fullPath, List<ClaudeShortcut> all)
    {
        int count = all.Count(s => ClaudeShortcutFolders.IsSelfOrDescendant(
            ClaudeShortcutFolders.Normalize(s.FolderPath), fullPath));
        return count > 0 ? $"{leafName}  ({count})" : leafName;
    }

    /// <summary>
    /// Owner-draws the entire row. Using OwnerDrawAll (vs. OwnerDrawText) means
    /// the TreeView does NOT paint its default selection background first, so
    /// we don't get the light-gray bleed-through on selected nodes.
    /// </summary>
    private void OnDrawTreeNode(object? sender, DrawTreeNodeEventArgs e)
    {
        if (e.Node == null) return;

        var g = e.Graphics;
        var bounds = e.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        bool selected = _folderTree.SelectedNode == e.Node;
        bool isRoot = e.Node.Level == 0;

        // Fill the full row width (not just e.Bounds, which can be text-only).
        var rowRect = new Rectangle(0, bounds.Y, _folderTree.ClientSize.Width, bounds.Height);
        var bg = selected ? _theme.Primary : _theme.BgHeader;
        using (var bgBrush = new SolidBrush(bg))
            g.FillRectangle(bgBrush, rowRect);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        int left = (e.Node.Level + 1) * _folderTree.Indent - 12;
        if (left < 4) left = 4;

        var glyphColor = selected ? Color.White : _theme.TextSecondary;
        var textColor = selected ? Color.White : _theme.TextPrimary;

        // Chevron (only if node has children).
        if (e.Node.Nodes.Count > 0)
        {
            using var chevronFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            using var chevronBrush = new SolidBrush(glyphColor);
            string chev = e.Node.IsExpanded ? "⌄" : "›";
            var sf = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Center };
            g.DrawString(chev, chevronFont, chevronBrush, new RectangleF(left, bounds.Y, 14, bounds.Height), sf);
        }
        int x = left + 16;

        // Folder / root glyph.
        string iconText = isRoot ? "★" : (e.Node.IsExpanded && e.Node.Nodes.Count > 0 ? "📂" : "📁");
        using (var iconFont = new Font("Segoe UI Emoji", 10f))
        using (var iconBrush = new SolidBrush(textColor))
        {
            var sfIcon = new StringFormat { LineAlignment = StringAlignment.Center };
            g.DrawString(iconText, iconFont, iconBrush, new RectangleF(x, bounds.Y, 20, bounds.Height), sfIcon);
        }
        x += 22;

        // Node text (fills remaining row width).
        using var font = new Font(_folderTree.Font, isRoot ? FontStyle.Bold : FontStyle.Regular);
        using var textBrush = new SolidBrush(textColor);
        var textRect = new RectangleF(x, bounds.Y, rowRect.Right - x - 4, bounds.Height);
        using var sfText = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
        };
        g.DrawString(e.Node.Text, font, textBrush, textRect, sfText);
    }

    private void ShowTreeContextMenu(TreeNode? node, Point location)
    {
        if (node == null) return;
        var path = (node.Tag as string) ?? "";
        var menu = new ContextMenuStrip { BackColor = _theme.BgHeader, ForeColor = _theme.TextPrimary };
        menu.Items.Add("New subfolder…", null, (_, _) => CreateFolder(parent: path));
        if (!string.IsNullOrEmpty(path))
        {
            menu.Items.Add("Rename folder…", null, (_, _) => RenameFolder(path));
            menu.Items.Add("Delete folder…", null, (_, _) => DeleteFolder(path));
        }
        menu.Show(_folderTree, location);
    }

    private void CreateFolder(string parent)
    {
        var name = TextInputDialog.Prompt(this, _theme,
            "New folder",
            string.IsNullOrEmpty(parent)
                ? "Folder name"
                : $"Folder name (under \"{parent}\")");
        if (name == null) return;

        // Only accept a leaf name — no slashes — to keep the UX predictable.
        var cleaned = name.Replace('/', '_').Replace('\\', '_').Trim();
        if (string.IsNullOrEmpty(cleaned)) return;

        var full = string.IsNullOrEmpty(parent) ? cleaned : parent + "/" + cleaned;
        ClaudeShortcutFolders.Add(full);
        _selectedFolder = full;
        RebuildTree();
        RefreshList();
    }

    private void RenameFolder(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var parent = ClaudeShortcutFolders.ParentOf(path) ?? "";
        var leaf = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        var newLeaf = TextInputDialog.Prompt(this, _theme, "Rename folder", "New name", leaf);
        if (newLeaf == null) return;
        newLeaf = newLeaf.Replace('/', '_').Replace('\\', '_').Trim();
        if (string.IsNullOrEmpty(newLeaf) || string.Equals(newLeaf, leaf, StringComparison.OrdinalIgnoreCase)) return;
        var newPath = string.IsNullOrEmpty(parent) ? newLeaf : parent + "/" + newLeaf;

        // Rewrite all matching shortcuts.
        var all = ClaudeShortcutStore.Load();
        bool changed = false;
        foreach (var s in all.ToList())
        {
            var normalized = ClaudeShortcutFolders.Normalize(s.FolderPath);
            if (ClaudeShortcutFolders.IsSelfOrDescendant(normalized, path))
            {
                var nextPath = ClaudeShortcutFolders.RewritePrefix(normalized, path, newPath);
                ClaudeShortcutStore.Update(s with { FolderPath = nextPath });
                changed = true;
            }
        }
        ClaudeShortcutFolders.RenamePath(path, newPath);
        _ = changed;
        _selectedFolder = newPath;
        RebuildTree();
        RefreshList();
    }

    private void DeleteFolder(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var all = ClaudeShortcutStore.Load();
        var under = all.Where(s => ClaudeShortcutFolders.IsSelfOrDescendant(
            ClaudeShortcutFolders.Normalize(s.FolderPath), path)).ToList();

        if (under.Count == 0)
        {
            var res = MessageBox.Show(this,
                $"Delete folder \"{path}\"?",
                "Confirm Delete",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (res != DialogResult.Yes) return;
            ClaudeShortcutFolders.RemoveRecursive(path);
        }
        else
        {
            var res = MessageBox.Show(this,
                $"\"{path}\" contains {under.Count} shortcut(s).\n\n"
                + "Move them up to the parent folder and delete \"" + path + "\"?\n"
                + "(No will cancel and leave everything in place.)",
                "Confirm Delete",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (res != DialogResult.Yes) return;
            var parent = ClaudeShortcutFolders.ParentOf(path) ?? "";
            foreach (var s in under)
            {
                var normalized = ClaudeShortcutFolders.Normalize(s.FolderPath);
                // Only shortcuts whose path == this folder move up; deeper descendants
                // follow the prefix-rewrite to stay under the parent.
                string target;
                if (string.Equals(normalized, path, StringComparison.OrdinalIgnoreCase))
                    target = parent;
                else
                    target = ClaudeShortcutFolders.RewritePrefix(normalized, path, parent);
                ClaudeShortcutStore.Update(s with { FolderPath = target });
            }
            ClaudeShortcutFolders.RemoveRecursive(path);
        }

        _selectedFolder = ClaudeShortcutFolders.ParentOf(path) ?? "";
        RebuildTree();
        RefreshList();
    }

    private void ResizeRows()
    {
        int w = _listPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 6;
        if (w < 120) w = 120;
        foreach (Control c in _listPanel.Controls)
            if (c is ClaudeShortcutRow r) r.Width = w;
    }

    private void ToggleExpand(string id)
    {
        _expandedId = _expandedId == id ? null : id;
        foreach (Control c in _listPanel.Controls)
            if (c is ClaudeShortcutRow r) r.Expanded = r.ShortcutId == _expandedId;
    }

    private void NewShortcut()
    {
        using var dlg = new ClaudeShortcutEditForm(_theme, defaultFolder: _selectedFolder);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
        {
            ClaudeShortcutStore.Add(dlg.Result);
            _expandedId = dlg.Result.Id;
            RebuildTree();
            RefreshList();
        }
    }

    private void Edit(string id)
    {
        var cur = ClaudeShortcutStore.Get(id);
        if (cur == null) return;
        using var dlg = new ClaudeShortcutEditForm(_theme, cur);
        var dr = dlg.ShowDialog(this);
        if (dr != DialogResult.OK) return;
        if (dlg.DeleteRequested)
        {
            ClaudeShortcutStore.Delete(id);
            if (_expandedId == id) _expandedId = null;
        }
        else if (dlg.Result != null)
        {
            ClaudeShortcutStore.Update(dlg.Result);
        }
        RebuildTree();
        RefreshList();
    }

    private void Duplicate(string id)
    {
        var src = ClaudeShortcutStore.Get(id);
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
        ClaudeShortcutStore.Add(copy);
        _expandedId = copy.Id;
        RebuildTree();
        RefreshList();
    }

    private void Delete(string id)
    {
        var res = MessageBox.Show(this, "Delete this shortcut?", "Confirm Delete",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (res != DialogResult.Yes) return;
        ClaudeShortcutStore.Delete(id);
        if (_expandedId == id) _expandedId = null;
        RebuildTree();
        RefreshList();
    }

    private void MoveShortcutToFolder(string id, string targetFolder)
    {
        var cur = ClaudeShortcutStore.Get(id);
        if (cur == null) return;
        targetFolder = ClaudeShortcutFolders.Normalize(targetFolder);
        ClaudeShortcutStore.Update(cur with { FolderPath = targetFolder });
        if (!string.IsNullOrEmpty(targetFolder))
            ClaudeShortcutFolders.Add(targetFolder);
        RebuildTree();
        RefreshList();
    }

    private void Launch(string id)
    {
        var cur = ClaudeShortcutStore.Get(id);
        if (cur == null) return;
        var result = ClaudeShortcutLauncher.Launch(cur);
        if (!result.Ok)
        {
            MessageBox.Show(this, result.ErrorMessage ?? "Launch failed.", "Launch failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            // Re-read the updated entry and push to the row so launch-count updates
            // without a full list rebuild.
            var updated = ClaudeShortcutStore.Get(id);
            if (updated != null)
            {
                foreach (Control c in _listPanel.Controls)
                    if (c is ClaudeShortcutRow r && r.ShortcutId == id) r.UpdateEntry(updated);
            }
        }
    }

    private void OpenFolder(string id)
    {
        var cur = ClaudeShortcutStore.Get(id);
        if (cur == null || !Directory.Exists(cur.WorkingDirectory)) return;
        try { Process.Start(new ProcessStartInfo(cur.WorkingDirectory) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open folder failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowRowMenu(string id, Control anchor)
    {
        var cur = ClaudeShortcutStore.Get(id);
        if (cur == null) return;
        var menu = new ContextMenuStrip { BackColor = _theme.BgHeader, ForeColor = _theme.TextPrimary };
        menu.Items.Add("Launch", null, (_, _) => Launch(id));
        menu.Items.Add("Edit…", null, (_, _) => Edit(id));
        menu.Items.Add("Duplicate", null, (_, _) => Duplicate(id));
        menu.Items.Add(new ToolStripSeparator());

        // Move-to-folder submenu lists every known folder (derived + persisted).
        var moveMenu = new ToolStripMenuItem("Move to folder");
        var knownFolders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in ClaudeShortcutStore.Load())
        {
            var p = ClaudeShortcutFolders.Normalize(s.FolderPath);
            if (!string.IsNullOrEmpty(p)) knownFolders.Add(p);
        }
        foreach (var f in ClaudeShortcutFolders.Load())
            knownFolders.Add(ClaudeShortcutFolders.Normalize(f));

        moveMenu.DropDownItems.Add("(Root)", null, (_, _) => MoveShortcutToFolder(id, ""));
        if (knownFolders.Count > 0)
            moveMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var folder in knownFolders)
        {
            string f = folder;
            var item = new ToolStripMenuItem(f) { Checked = string.Equals(cur.FolderPath, f, StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) => MoveShortcutToFolder(id, f);
            moveMenu.DropDownItems.Add(item);
        }
        moveMenu.DropDownItems.Add(new ToolStripSeparator());
        moveMenu.DropDownItems.Add("New folder…", null, (_, _) =>
        {
            var name = TextInputDialog.Prompt(this, _theme, "New folder",
                "Folder path (e.g. \"Work/Backend\")");
            if (string.IsNullOrWhiteSpace(name)) return;
            MoveShortcutToFolder(id, name);
        });
        menu.Items.Add(moveMenu);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open folder in Explorer", null, (_, _) => OpenFolder(id));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Delete", null, (_, _) => Delete(id));
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.N) { NewShortcut(); e.Handled = true; return; }
        if (e.KeyCode == Keys.Escape && _expandedId != null && ActiveControl is not TextBox)
        {
            _expandedId = null;
            foreach (Control c in _listPanel.Controls)
                if (c is ClaudeShortcutRow r) r.Expanded = false;
            e.Handled = true;
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
class ClaudeShortcutRow : Panel
{
    private const int CollapsedHeight = 72;
    private const int ExpandedHeight = 168;

    private ClaudeShortcut _shortcut;
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

    public void UpdateEntry(ClaudeShortcut s)
    {
        _shortcut = s;
        Invalidate();
    }

    public ClaudeShortcutRow(ClaudeShortcut s, PluginTheme theme)
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

        // Admin tag — solid fill + white uppercase text, drawn to the right of
        // the name. Rendered as a flat tag (minimal corner rounding) rather than
        // a full pill so it reads as a label, not a bubble.
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

        // Working directory (second line)
        using (var subFont = new Font("Segoe UI", 9f))
        using (var subBrush = new SolidBrush(_theme.TextSecondary))
        using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisPath, FormatFlags = StringFormatFlags.NoWrap })
        {
            g.DrawString(_shortcut.WorkingDirectory ?? "",
                subFont, subBrush, new RectangleF(textLeft, 36, textRight - textLeft, 18), sf);
        }

        // Third line: args + launcher mode
        string launcher = _shortcut.LauncherMode == ClaudeLauncherMode.WindowsTerminal
            ? (string.IsNullOrEmpty(_shortcut.WtProfile) ? "wt" : $"wt · {_shortcut.WtProfile}")
            : "cmd";
        string thirdLine = $"claude {_shortcut.ClaudeArgs}".Trim();
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
