using System.Drawing;
using System.Drawing.Drawing2D;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>The highlight colour of New items: amber on a dark theme, the theme's dim primary on a light one.</summary>
static class NewMark
{
    public static bool IsLight(PluginTheme theme) => theme.BgDark.GetBrightness() > 0.5f;

    public static Color Back(PluginTheme theme) => IsLight(theme) ? theme.PrimaryDim : Color.FromArgb(232, 160, 32);

    public static Color Fore(PluginTheme theme) => IsLight(theme) ? theme.TextPrimary : Color.FromArgb(28, 22, 8);

    /// <summary>The dot on a tree row and the outline of the chip on screen.</summary>
    public static Color Dot(PluginTheme theme) => IsLight(theme) ? theme.Primary : Color.FromArgb(240, 176, 48);
}

/// <summary>One New chip: the item, its folder, its time and its title for the tooltip.</summary>
readonly record struct UnreadChip(string Key, string Folder, DateTime Time, string Title);

/// <summary>
/// The New chips in the left strip under the tree button, newest on top: one per item not yet confirmed with OK. A click shows
/// the item (it stays New); a right-click offers Mark read and Mark all read. The chip of the item on screen is outlined.
/// When the chips do not fit, the last slot says how many more there are.
/// </summary>
sealed class UnreadChipStrip : Control
{
    private const int ChipHeight = 36;
    private const int Gap = 4;

    private readonly ToolTip _tip = new() { InitialDelay = 300, ReshowDelay = 100 };
    private readonly Font _folderFont = new("Segoe UI Semibold", 7f);
    private readonly Font _timeFont = new("Segoe UI", 7f);
    private IReadOnlyList<UnreadChip> _chips = Array.Empty<UnreadChip>();
    private int _hover = -1;
    private int _tipFor = -2;

    public UnreadChipStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    public PluginTheme? Theme { get; set; }

    /// <summary>The item on screen: its chip is outlined.</summary>
    public string? Current { get; private set; }

    /// <summary>A chip was clicked: show its item.</summary>
    public event Action<string>? ChipClicked;

    /// <summary>Mark read from a chip's menu.</summary>
    public event Action<string>? MarkRead;

    /// <summary>Mark all read from a chip's menu.</summary>
    public event Action? MarkAllRead;

    public int Count => _chips.Count;

    /// <summary>How many chips fit; the rest are summed up in the last slot.</summary>
    public int Fitting => Math.Max(0, (ClientSize.Height + Gap) / (ChipHeight + Gap));

    /// <summary>More chips than fit.</summary>
    public bool Overflowing => _chips.Count > Fitting;

    public void SetChips(IReadOnlyList<UnreadChip> chips, string? current)
    {
        _chips = chips;
        Current = current;
        _tipFor = -2;
        Invalidate();
    }

    public void SetCurrent(string? current)
    {
        if (current == Current) return;
        Current = current;
        Invalidate();
    }

    /// <summary>The slot under a point; the overflow slot answers -1.</summary>
    private int ChipAt(Point p)
    {
        int slot = p.Y / (ChipHeight + Gap);
        if (slot < 0 || slot >= _chips.Count || p.Y % (ChipHeight + Gap) >= ChipHeight) return -1;
        if (Overflowing && slot >= Fitting - 1) return -1;
        return slot;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int at = ChipAt(e.Location);
        if (at != _hover)
        {
            _hover = at;
            Invalidate();
        }

        if (at != _tipFor)
        {
            _tipFor = at;
            _tip.SetToolTip(this, at >= 0 ? TipOf(_chips[at]) : Overflowing ? $"{_chips.Count - Fitting + 1} more New - open the tree to see them" : "");
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        int at = ChipAt(e.Location);
        if (at < 0) return;

        string key = _chips[at].Key;
        if (e.Button == MouseButtons.Left)
        {
            ChipClicked?.Invoke(key);
            return;
        }

        if (e.Button != MouseButtons.Right) return;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Mark read", null, (_, _) => MarkRead?.Invoke(key));
        menu.Items.Add("Mark all read", null, (_, _) => MarkAllRead?.Invoke());
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(this, e.Location);
    }

    private static string TipOf(UnreadChip chip) => $"New · {chip.Folder} · {chip.Time:HH:mm}\n{chip.Title}";

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Theme is not { } theme) return;

        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? theme.BgHeader);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int fit = Fitting;
        int shown = Overflowing ? Math.Max(0, fit - 1) : _chips.Count;
        for (int i = 0; i < shown; i++)
        {
            var chip = _chips[i];
            var box = new Rectangle(1, i * (ChipHeight + Gap), ClientSize.Width - 2, ChipHeight);
            var back = NewMark.Back(theme);
            if (i == _hover) back = ControlPaint.Light(back, 0.2f);
            using (var path = Rounded(box, 6))
            using (var brush = new SolidBrush(back))
                g.FillPath(brush, path);

            if (chip.Key == Current)
            {
                using var path = Rounded(Rectangle.Inflate(box, -1, -1), 6);
                using var pen = new Pen(theme.TextPrimary, 2f);
                g.DrawPath(pen, path);
            }

            var fore = NewMark.Fore(theme);
            var top = new Rectangle(box.X + 2, box.Y + 3, box.Width - 4, 15);
            var bottom = new Rectangle(box.X + 2, box.Y + 18, box.Width - 4, 14);
            const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, chip.Folder, _folderFont, top, fore, flags);
            TextRenderer.DrawText(g, chip.Time.ToString("HH:mm"), _timeFont, bottom, fore, flags);
        }

        if (Overflowing && fit > 0)
        {
            var box = new Rectangle(1, (fit - 1) * (ChipHeight + Gap), ClientSize.Width - 2, ChipHeight);
            using (var path = Rounded(box, 6))
            using (var pen = new Pen(NewMark.Dot(theme), 1.5f))
                g.DrawPath(pen, path);
            TextRenderer.DrawText(g, $"+{_chips.Count - fit + 1}", _folderFont, box, NewMark.Dot(theme),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
            _folderFont.Dispose();
            _timeFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
