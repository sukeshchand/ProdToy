using System.Drawing;
using System.Drawing.Drawing2D;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Flicker-free owner-drawn <see cref="TabControl"/>. Uses full <c>UserPaint</c>
/// (like the host's ThemedTabControl) rather than <c>DrawMode.OwnerDrawFixed</c> +
/// <c>DrawItem</c> — the latter is painted by the OS outside .NET's double-buffer,
/// so it flickers no matter what styles you set. The whole strip is painted in
/// one buffered <see cref="OnPaint"/>; each tab is rendered via <see cref="PaintTab"/>.
/// </summary>
sealed class OwnerDrawTabControl : TabControl
{
    private readonly PluginTheme _theme;

    /// <summary>Per-tab painter: (graphics, tabIndex, tabBounds, isSelected).</summary>
    public Action<Graphics, int, Rectangle, bool>? PaintTab;

    public OwnerDrawTabControl(PluginTheme theme)
    {
        _theme = theme;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { /* painted in OnPaint */ }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var bg = new SolidBrush(_theme.BgDark))
            g.FillRectangle(bg, ClientRectangle);

        for (int i = 0; i < TabCount; i++)
        {
            Rectangle r;
            try { r = GetTabRect(i); } catch { continue; }
            PaintTab?.Invoke(g, i, r, SelectedIndex == i);
        }
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        Invalidate();
    }
}

/// <summary>Double-buffered <see cref="FlowLayoutPanel"/> for the shortcut-row
/// list, so row repaints don't flash the panel background.</summary>
sealed class BufferedFlowPanel : FlowLayoutPanel
{
    public BufferedFlowPanel() => DoubleBuffered = true;
}

/// <summary>
/// Tabbed console viewer for the Consolidated Launcher — one log pane per
/// shortcut, plus a "Launcher" tab at position 0 for the launcher's own
/// messages (lifecycle banners, errors). Stdout/stderr from each captured
/// child process is appended live into the matching tab.
/// </summary>
/// <remarks>
/// Ported from NordPilot.DeveloperTools' LogTabsControl, re-themed with
/// <see cref="PluginTheme"/>. Design notes:
///  - Owner-drawn tab headers paint a per-shortcut colour swatch on the strip.
///  - <see cref="RichTextBox"/> per tab so stderr renders in the theme error
///    colour while stdout uses the primary text colour. Appended per-line.
///  - Ring buffer: each pane caps at ~5000 lines, dropping the oldest ~500 in
///    one shot when exceeded.
///  - Right-click a tab for Restart / Clear / Copy-all.
/// </remarks>
sealed class ConsolidatedLogTabs : UserControl
{
    public const string LauncherTabKey = "__launcher__";
    private const int LineCap = 5000;
    private const int LineTrimChunk = 500;

    private readonly PluginTheme _theme;
    private readonly Color _stdoutColor;
    private readonly Color _stderrColor;

    private readonly OwnerDrawTabControl _tabs;
    private readonly Dictionary<string, TabPage> _tabsByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RichTextBox> _textBoxByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _lineCountByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TabPage, Color> _swatchByPage = new();
    private readonly Dictionary<TabPage, string> _keyByPage = new();
    private readonly ContextMenuStrip _tabContextMenu;
    private TabPage? _contextTab;

    /// <summary>Fired when the user picks Restart from a tab's right-click menu.
    /// Argument is the tab key (shortcut id). The launcher tab hides this entry.</summary>
    public event Action<string>? RestartRequested;

    public ConsolidatedLogTabs(PluginTheme theme)
    {
        _theme = theme;
        _stdoutColor = theme.TextPrimary;
        _stderrColor = theme.ErrorColor;
        BackColor = theme.BgDark;

        _tabs = new OwnerDrawTabControl(theme)
        {
            Dock = DockStyle.Fill,
            Appearance = TabAppearance.Normal,
            ItemSize = new Size(120, 24),
            SizeMode = TabSizeMode.Fixed,
        };
        _tabs.PaintTab = DrawTabHeader;
        _tabs.MouseUp += OnTabsMouseUp;
        Controls.Add(_tabs);

        _tabContextMenu = BuildContextMenu();

        AddOrGetTab(LauncherTabKey, "Launcher", theme.TextSecondary, canRestart: false);
    }

    /// <summary>Currently selected tab's key, or null if no tab is selected.</summary>
    public string? SelectedKey
        => _tabs.SelectedTab is { } t && _keyByPage.TryGetValue(t, out var k) ? k : null;

    /// <summary>
    /// Creates a tab for the given key if it doesn't exist. Subsequent calls with the
    /// same key are a no-op. <paramref name="canRestart"/> controls whether the right-click
    /// menu exposes Restart — the launcher tab passes false.
    /// </summary>
    public void AddOrGetTab(string key, string displayName, Color swatch, bool canRestart = true)
    {
        if (_tabsByKey.ContainsKey(key)) return;

        var page = new TabPage(displayName)
        {
            BackColor = _theme.BgDark,
            Tag = canRestart,   // read by the context menu without a parallel dictionary
        };

        var rtb = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = false,
            BackColor = _theme.BgDark,
            ForeColor = _stdoutColor,
            Font = new Font("Cascadia Mono", 9F),
            BorderStyle = BorderStyle.None,
            HideSelection = false,
            DetectUrls = false,
            ScrollBars = RichTextBoxScrollBars.Both,
        };

        page.Controls.Add(rtb);
        _tabs.TabPages.Add(page);

        _tabsByKey[key] = page;
        _textBoxByKey[key] = rtb;
        _lineCountByKey[key] = 0;
        _swatchByPage[page] = swatch;
        _keyByPage[page] = key;
    }

    /// <summary>
    /// Append a line to a tab. Must be called on the UI thread.
    /// <paramref name="isError"/> renders the line in the error colour.
    /// </summary>
    public void AppendLine(string key, string line, bool isError = false)
    {
        if (IsDisposed || Disposing) return;

        if (!_textBoxByKey.TryGetValue(key, out var rtb))
        {
            // Unknown key — fall back to the launcher tab so the line isn't lost.
            if (!_textBoxByKey.TryGetValue(LauncherTabKey, out rtb)) return;
            key = LauncherTabKey;
        }
        if (rtb.IsDisposed) return;

        if (_lineCountByKey[key] >= LineCap)
            TrimOldestChunk(rtb, key);

        try
        {
            AppendColoured(rtb, line + Environment.NewLine, isError ? _stderrColor : _stdoutColor);
            _lineCountByKey[key] += 1;
        }
        catch (ObjectDisposedException) { /* race with shutdown — fine */ }
        catch (InvalidOperationException) { /* handle destroyed mid-append — fine */ }
    }

    /// <summary>
    /// Append many lines to a tab in one shot — the high-volume path used by
    /// the UI flush timer draining buffered process output. Redraw is suspended
    /// during the bulk append and a single <c>ScrollToCaret</c> runs at the end,
    /// so a burst of thousands of lines costs one repaint instead of thousands.
    /// Must be called on the UI thread.
    /// </summary>
    public void AppendBatch(string key, IReadOnlyList<(string line, bool isError)> batch)
    {
        if (IsDisposed || Disposing || batch.Count == 0) return;

        if (!_textBoxByKey.TryGetValue(key, out var rtb))
        {
            if (!_textBoxByKey.TryGetValue(LauncherTabKey, out rtb)) return;
            key = LauncherTabKey;
        }
        if (rtb.IsDisposed) return;

        // Keep the buffer bounded — trim repeatedly if the batch is large.
        while (_lineCountByKey[key] + batch.Count > LineCap)
        {
            int before = _lineCountByKey[key];
            TrimOldestChunk(rtb, key);
            if (_lineCountByKey[key] >= before) break;   // nothing left to trim
        }

        try
        {
            // Coalesce consecutive same-stream lines into one AppendText so a burst
            // is one (or a few) repaints instead of one per line — and a single
            // ScrollToCaret at the very end. This is what keeps streaming smooth;
            // toggling WM_SETREDRAW + Invalidate per flush was itself the flicker.
            int i = 0;
            while (i < batch.Count)
            {
                bool isErr = batch[i].isError;
                var sb = new System.Text.StringBuilder();
                int j = i;
                while (j < batch.Count && batch[j].isError == isErr)
                {
                    sb.Append(batch[j].line).Append(Environment.NewLine);
                    j++;
                }
                rtb.SelectionStart = rtb.TextLength;
                rtb.SelectionLength = 0;
                rtb.SelectionColor = isErr ? _stderrColor : _stdoutColor;
                rtb.AppendText(sb.ToString());
                i = j;
            }
            rtb.SelectionColor = rtb.ForeColor;
            _lineCountByKey[key] += batch.Count;
            rtb.SelectionStart = rtb.TextLength;
            rtb.ScrollToCaret();
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>Switch the active tab to the named key.</summary>
    public void FocusTab(string key)
    {
        if (_tabsByKey.TryGetValue(key, out var page))
            _tabs.SelectedTab = page;
    }

    /// <summary>Clear a tab's buffer.</summary>
    public void ClearTab(string key)
    {
        if (_textBoxByKey.TryGetValue(key, out var rtb))
        {
            rtb.Clear();
            _lineCountByKey[key] = 0;
        }
    }

    // ---- owner draw ----------------------------------------------------------------

    private void DrawTabHeader(Graphics g, int index, Rectangle bounds, bool selected)
    {
        if (index < 0 || index >= _tabs.TabPages.Count) return;
        var page = _tabs.TabPages[index];

        var bg = selected ? _theme.BgHeader : _theme.BgDark;
        using (var brush = new SolidBrush(bg))
            g.FillRectangle(brush, bounds);

        // 8x8 swatch on the left, vertically centred.
        var swatchColor = _swatchByPage.TryGetValue(page, out var c) ? c : _theme.TextSecondary;
        const int swatchSize = 8;
        var swatchY = bounds.Y + (bounds.Height - swatchSize) / 2;
        using (var swatchBrush = new SolidBrush(swatchColor))
            g.FillEllipse(swatchBrush, bounds.X + 8, swatchY, swatchSize, swatchSize);

        var textColor = selected ? _theme.TextPrimary : _theme.TextSecondary;
        using var font = new Font(selected ? "Segoe UI Semibold" : "Segoe UI", 9F);
        var textRect = new Rectangle(
            bounds.X + 8 + swatchSize + 6,
            bounds.Y,
            bounds.Width - (8 + swatchSize + 6) - 4,
            bounds.Height);
        TextRenderer.DrawText(
            g, page.Text, font, textRect, textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (selected)
        {
            using var pen = new Pen(swatchColor, 2);
            g.DrawLine(pen, bounds.X, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
        }
    }

    // ---- context menu --------------------------------------------------------------

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip { BackColor = _theme.BgHeader, ForeColor = _theme.TextPrimary };
        var restartItem = new ToolStripMenuItem("Restart") { Name = "restart" };
        var clearItem = new ToolStripMenuItem("Clear") { Name = "clear" };
        var copyItem = new ToolStripMenuItem("Copy all") { Name = "copy" };

        restartItem.Click += (_, _) =>
        {
            if (_contextTab is { } page && _keyByPage.TryGetValue(page, out var key))
                RestartRequested?.Invoke(key);
        };
        clearItem.Click += (_, _) =>
        {
            if (_contextTab is { } page && _keyByPage.TryGetValue(page, out var key))
                ClearTab(key);
        };
        copyItem.Click += (_, _) =>
        {
            if (_contextTab is { } page && _keyByPage.TryGetValue(page, out var key)
                && _textBoxByKey.TryGetValue(key, out var rtb)
                && rtb.TextLength > 0)
            {
                try { Clipboard.SetText(rtb.Text); } catch { }
            }
        };

        menu.Items.Add(restartItem);
        menu.Items.Add(clearItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(copyItem);

        menu.Opening += (_, _) => restartItem.Visible = _contextTab?.Tag is true;
        return menu;
    }

    private void OnTabsMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        for (int i = 0; i < _tabs.TabPages.Count; i++)
        {
            if (_tabs.GetTabRect(i).Contains(e.Location))
            {
                _contextTab = _tabs.TabPages[i];
                _tabContextMenu.Show(_tabs, e.Location);
                return;
            }
        }
    }

    // ---- helpers -------------------------------------------------------------------

    private static void AppendColoured(RichTextBox rtb, string text, Color color)
    {
        rtb.SelectionStart = rtb.TextLength;
        rtb.SelectionLength = 0;
        rtb.SelectionColor = color;
        rtb.AppendText(text);
        rtb.SelectionColor = rtb.ForeColor;
        rtb.SelectionStart = rtb.TextLength;
        rtb.ScrollToCaret();
    }

    private void TrimOldestChunk(RichTextBox rtb, string key)
    {
        var text = rtb.Text;
        int idx = 0;
        int found = 0;
        while (found < LineTrimChunk && idx < text.Length)
        {
            var nl = text.IndexOf('\n', idx);
            if (nl < 0) break;
            idx = nl + 1;
            found++;
        }
        if (idx > 0)
        {
            rtb.Select(0, idx);
            rtb.SelectedText = string.Empty;
            _lineCountByKey[key] = Math.Max(0, _lineCountByKey[key] - found);
        }
    }

    /// <summary>Deterministic, readable colour for a shortcut from its id —
    /// used for the tab swatch so each shortcut keeps the same colour.</summary>
    public static Color StableColor(string seed)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        uint hash = offset;
        var s = seed ?? "";
        for (int i = 0; i < s.Length; i++) { hash ^= s[i]; hash *= prime; }
        // HSV with fixed saturation/value so colours stay vivid but readable.
        double h = hash % 360;
        return FromHsv(h, 0.55, 0.95);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        int hi = (int)(h / 60) % 6;
        double f = h / 60 - Math.Floor(h / 60);
        v *= 255;
        int vi = (int)v;
        int p = (int)(v * (1 - s));
        int q = (int)(v * (1 - f * s));
        int t = (int)(v * (1 - (1 - f) * s));
        return hi switch
        {
            0 => Color.FromArgb(vi, t, p),
            1 => Color.FromArgb(q, vi, p),
            2 => Color.FromArgb(p, vi, t),
            3 => Color.FromArgb(p, q, vi),
            4 => Color.FromArgb(t, p, vi),
            _ => Color.FromArgb(vi, p, q),
        };
    }
}
