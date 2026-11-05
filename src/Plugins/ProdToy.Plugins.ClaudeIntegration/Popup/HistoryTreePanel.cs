using System.Drawing;
using System.Drawing.Drawing2D;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>
/// The left panel of the notification window: History heading, find box and the tree. Only the rows
/// that are open are ever laid out, so a long history costs nothing; open state lives here and
/// survives every close, because the window is only hidden.
/// </summary>
sealed class HistoryTreePanel : Panel
{
    private readonly Label _heading;
    private readonly Label _total;
    private readonly Label _findIcon;
    private readonly TextBox _find;
    private readonly HistoryRows _rows;
    private readonly System.Windows.Forms.Timer _findTimer = new() { Interval = 150 };

    /// <summary>A node was picked: a click, or Enter on the keyboard.</summary>
    public event Action<HistoryNode>? NodePicked;

    public HistoryTreePanel(PluginTheme theme)
    {
        Dock = DockStyle.Left;

        _heading = new Label
        {
            Text = "HISTORY",
            Font = new Font("Segoe UI Semibold", 9f),
            AutoSize = true,
            Location = new Point(14, 12),
        };
        _total = new Label { Font = new Font("Segoe UI", 8.5f), AutoSize = true, Top = 13, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _findIcon = new Label
        {
            Text = "",
            Font = new Font("Segoe MDL2 Assets", 9f),
            AutoSize = true,
            Location = new Point(12, 43),
        };
        _find = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9f),
            PlaceholderText = "Find in history",
            Location = new Point(32, 39),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        _rows = new HistoryRows { Location = new Point(1, 70), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
        _rows.NodePicked += node => NodePicked?.Invoke(node);

        _findTimer.Tick += (_, _) =>
        {
            _findTimer.Stop();
            _rows.Filter = _find.Text.Trim();
        };
        _find.TextChanged += (_, _) =>
        {
            _findTimer.Stop();
            _findTimer.Start();
        };
        _find.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Down) return;
            e.Handled = true;
            _rows.Focus();
        };

        Controls.AddRange(new Control[] { _heading, _total, _findIcon, _find, _rows });
        ApplyTheme(theme);
        Resize += (_, _) => LayoutInside();
    }

    public void ApplyTheme(PluginTheme theme)
    {
        BackColor = theme.BgDark;
        _heading.ForeColor = theme.TextSecondary;
        _total.ForeColor = theme.TextSecondary;
        _findIcon.ForeColor = theme.TextSecondary;
        _find.BackColor = theme.BgHeader;
        _find.ForeColor = theme.TextPrimary;
        _rows.Theme = theme;
    }

    public void SetTree(HistoryTree tree)
    {
        _rows.Tree = tree;
        RefreshRows();
    }

    /// <summary>The tree gained items, or the scope or the item changed.</summary>
    public void RefreshRows()
    {
        var root = _rows.Tree?.Root;
        _total.Text = _rows.Tree is { IsReady: true } ? $"{root!.Count} items" : "";
        LayoutInside();
        _rows.Rebuild();
    }

    public void SetState(HistoryNode? scope, string? itemKey)
    {
        _rows.Scope = scope;
        _rows.SelectedItem = itemKey;
        RefreshRows();
    }

    /// <summary>Opens the path down to a node and scrolls it into view.</summary>
    public void Reveal(HistoryNode node) => _rows.Reveal(node);

    private void LayoutInside()
    {
        _total.Left = Math.Max(0, ClientSize.Width - _total.PreferredWidth - 14);
        _find.Width = Math.Max(60, ClientSize.Width - 32 - 12);
        _rows.Size = new Size(Math.Max(0, ClientSize.Width - 2), Math.Max(0, ClientSize.Height - 71));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_rows.Theme is { } theme)
        {
            using var pen = new Pen(theme.Border);
            e.Graphics.DrawLine(pen, ClientSize.Width - 1, 0, ClientSize.Width - 1, ClientSize.Height);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _findTimer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>The drawn rows of the tree, with its keyboard and a thin scroll bar in the theme.</summary>
sealed class HistoryRows : Control
{
    private const int RowHeight = 26;
    private const int Indent = 16;
    private const int BarWidth = 8;

    private readonly HashSet<string> _open = new(StringComparer.Ordinal) { HistoryTree.RootKey, HistoryTree.FoldersKey, HistoryTree.SessionsKey };
    private readonly List<(HistoryNode Node, int Depth)> _visible = new();
    private readonly Font _font = new("Segoe UI", 9f);
    private readonly Font _bold = new("Segoe UI Semibold", 9f);
    private readonly Font _time = new("Consolas", 8f);
    private readonly Font _glyph = new("Segoe MDL2 Assets", 9f);

    private int _top;
    private int _focusRow = -1;
    private int _hoverRow = -1;
    private int _dragOffset = -1;
    private string _filter = "";
    private HashSet<HistoryNode>? _matches;

    public HistoryTree? Tree { get; set; }
    public PluginTheme? Theme { get; set; }
    public HistoryNode? Scope { get; set; }
    public string? SelectedItem { get; set; }

    public event Action<HistoryNode>? NodePicked;

    public string Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            _top = 0;
            _focusRow = -1;
            Rebuild();
        }
    }

    public HistoryRows()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
    }

    private int ContentHeight => _visible.Count * RowHeight;
    private int MaxTop => Math.Max(0, ContentHeight - ClientSize.Height);

    public void Rebuild()
    {
        _visible.Clear();
        if (Tree is { IsReady: true })
        {
            _matches = _filter.Length > 0 ? FindMatches(Tree.Root) : null;
            Flatten(Tree.Root, 0);
        }

        _top = Math.Clamp(_top, 0, MaxTop);
        Invalidate();
    }

    public void Reveal(HistoryNode node)
    {
        foreach (var n in node.SelfAndAncestors().Skip(node.IsItem ? 1 : 0)) _open.Add(n.Key);
        Rebuild();

        int row = _visible.FindIndex(v => ReferenceEquals(v.Node, node));
        if (row < 0) return;
        _focusRow = row;
        ScrollTo(row);
    }

    private HashSet<HistoryNode> FindMatches(HistoryNode root)
    {
        var hits = new HashSet<HistoryNode>();
        void Walk(HistoryNode n)
        {
            if (n.SearchText.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                foreach (var up in n.SelfAndAncestors()) hits.Add(up);
            foreach (var c in n.Children) Walk(c);
        }

        Walk(root);
        return hits;
    }

    private void Flatten(HistoryNode node, int depth)
    {
        if (_matches != null && !_matches.Contains(node)) return;

        _visible.Add((node, depth));
        if (_matches == null && !_open.Contains(node.Key)) return;
        foreach (var child in node.Children) Flatten(child, depth + 1);
    }

    private void ScrollTo(int row)
    {
        int y = row * RowHeight;
        if (y < _top) _top = y;
        else if (y + RowHeight > _top + ClientSize.Height) _top = y + RowHeight - ClientSize.Height;
        _top = Math.Clamp(_top, 0, MaxTop);
        Invalidate();
    }

    private void Toggle(HistoryNode node)
    {
        if (node.Children.Count == 0) return;
        if (!_open.Remove(node.Key)) _open.Add(node.Key);
        Rebuild();
    }

    private (int Y, int Height) Thumb()
    {
        int h = ClientSize.Height;
        int thumb = Math.Max(24, h * h / Math.Max(1, ContentHeight));
        int room = Math.Max(1, h - thumb);
        return (MaxTop == 0 ? 0 : _top * room / MaxTop, thumb);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _top = Math.Clamp(_top - Math.Sign(e.Delta) * RowHeight * 3, 0, MaxTop);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (MaxTop > 0 && e.X >= ClientSize.Width - BarWidth - 2)
        {
            var (y, height) = Thumb();
            _dragOffset = e.Y >= y && e.Y < y + height ? e.Y - y : height / 2;
            return;
        }

        int row = (e.Y + _top) / RowHeight;
        if (row < 0 || row >= _visible.Count) return;
        _focusRow = row;

        var node = _visible[row].Node;
        if (e.Button == MouseButtons.Left)
        {
            Toggle(node);
            NodePicked?.Invoke(node);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragOffset >= 0 && e.Button == MouseButtons.Left)
        {
            var (_, height) = Thumb();
            int room = Math.Max(1, ClientSize.Height - height);
            _top = Math.Clamp((e.Y - _dragOffset) * MaxTop / room, 0, MaxTop);
            Invalidate();
            return;
        }

        int row = (e.Y + _top) / RowHeight;
        row = row < _visible.Count ? row : -1;
        if (row != _hoverRow)
        {
            _hoverRow = row;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e) => _dragOffset = -1;

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoverRow = -1;
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Enter
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_visible.Count == 0) return;

        if (e.KeyCode is Keys.Up or Keys.Down)
        {
            _focusRow = Math.Clamp(_focusRow + (e.KeyCode == Keys.Down ? 1 : -1), 0, _visible.Count - 1);
            ScrollTo(_focusRow);
            e.Handled = true;
            return;
        }

        if (_focusRow < 0 || _focusRow >= _visible.Count) return;
        var node = _visible[_focusRow].Node;

        if (e.KeyCode == Keys.Right)
        {
            if (_open.Add(node.Key)) Rebuild();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Left)
        {
            if (_open.Remove(node.Key)) Rebuild();
            else if (node.Parent != null) Reveal(node.Parent);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Enter)
        {
            NodePicked?.Invoke(node);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Theme is not { } theme) return;

        var g = e.Graphics;
        g.Clear(theme.BgDark);

        if (Tree is not { IsReady: true })
        {
            TextRenderer.DrawText(g, "Loading history…", _font, new Point(14, 8), theme.TextSecondary);
            return;
        }

        if (_visible.Count == 0)
        {
            TextRenderer.DrawText(g, _filter.Length > 0 ? "Nothing found" : "No history yet", _font, new Point(14, 8), theme.TextSecondary);
            return;
        }

        int width = ClientSize.Width - (MaxTop > 0 ? BarWidth + 2 : 0);
        int first = _top / RowHeight;
        int last = Math.Min(_visible.Count - 1, (_top + ClientSize.Height) / RowHeight);
        for (int i = first; i <= last; i++)
            DrawRow(g, theme, i, new Rectangle(4, i * RowHeight - _top, width - 8, RowHeight));

        if (MaxTop > 0)
        {
            var (y, height) = Thumb();
            using var brush = new SolidBrush(theme.Border);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillRectangle(brush, ClientSize.Width - BarWidth, y, BarWidth - 3, height);
        }
    }

    private void DrawRow(Graphics g, PluginTheme theme, int row, Rectangle r)
    {
        var (node, depth) = _visible[row];
        bool selected = node.IsItem && HistoryTree.ItemKey(node.Item!) == SelectedItem;
        bool scoped = ReferenceEquals(node, Scope);

        Color back = selected ? theme.Primary : row == _hoverRow ? theme.PrimaryDim : theme.BgDark;
        using (var brush = new SolidBrush(back)) g.FillRectangle(brush, r);
        if (row == _focusRow && Focused)
        {
            using var pen = new Pen(theme.Border);
            g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        Color text = selected ? Color.White : scoped ? theme.PrimaryLight : theme.TextPrimary;
        Color quiet = selected ? Color.White : theme.TextSecondary;
        int x = r.X + 4 + depth * Indent;

        if (node.Children.Count > 0)
        {
            bool open = _matches != null || _open.Contains(node.Key);
            TextRenderer.DrawText(g, open ? "" : "", _glyph, new Point(x, r.Y + 6), quiet, TextFormatFlags.NoPadding);
        }

        x += 16;

        if (GlyphOf(node) is { } glyph)
        {
            TextRenderer.DrawText(g, glyph.Text, _glyph, new Point(x, r.Y + 6), selected ? Color.White : glyph.Color(theme), TextFormatFlags.NoPadding);
            x += 19;
        }

        if (node.IsItem)
        {
            string time = node.Item!.Timestamp.ToString("HH:mm");
            TextRenderer.DrawText(g, time, _time, new Point(x, r.Y + 7), quiet, TextFormatFlags.NoPadding);
            x += 40;
        }

        int right = r.Right - 6;
        if (!node.IsItem)
        {
            string count = node.Count.ToString();
            int w = TextRenderer.MeasureText(count, _time, Size.Empty, TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, count, _time, new Point(right - w, r.Y + 7), quiet, TextFormatFlags.NoPadding);
            right -= w + 8;
        }

        var font = scoped || node.Kind is HistoryNodeKind.Root or HistoryNodeKind.Branch ? _bold : _font;
        var box = new Rectangle(x, r.Y, Math.Max(0, right - x), r.Height);
        TextRenderer.DrawText(g, node.Label, font, box, text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    private readonly record struct Glyph(string Text, Func<PluginTheme, Color> Color);

    private static Glyph? GlyphOf(HistoryNode node) => node.Kind switch
    {
        HistoryNodeKind.Date => new Glyph("", t => t.TextSecondary),
        HistoryNodeKind.Branch when node.Key == HistoryTree.SessionsKey => new Glyph("", t => t.PrimaryLight),
        HistoryNodeKind.Branch => new Glyph("", t => t.Primary),
        HistoryNodeKind.Group when node.Key.StartsWith(HistoryTree.SessionsKey, StringComparison.Ordinal) => new Glyph("", t => t.PrimaryLight),
        HistoryNodeKind.Group => new Glyph("", t => t.Primary),
        _ => null,
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
            _bold.Dispose();
            _time.Dispose();
            _glyph.Dispose();
        }

        base.Dispose(disposing);
    }
}
