using System.Drawing;
using System.Drawing.Drawing2D;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// iOS-style toggle switch. Click to flip. Fires <see cref="CheckedChanged"/>.
/// Plugin-local copy (plugins can't reference each other's controls).
/// </summary>
class ToggleSwitch : Control
{
    private bool _checked;
    private readonly PluginTheme _theme;

    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ToggleSwitch(PluginTheme theme)
    {
        _theme = theme;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Width = 46;
        Height = 24;
        Cursor = Cursors.Hand;
        Click += (_, _) => Checked = !Checked;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = new GraphicsPath();
        int r = Height;
        path.AddArc(rect.X, rect.Y, r, r, 90, 180);
        path.AddArc(rect.Right - r, rect.Y, r, r, 270, 180);
        path.CloseFigure();

        var bg = _checked ? _theme.Primary : _theme.BgHeader;
        using var bgBrush = new SolidBrush(bg);
        g.FillPath(bgBrush, path);
        using var border = new Pen(_checked ? _theme.Primary : _theme.Border, 1);
        g.DrawPath(border, path);

        int knob = Height - 6;
        int kx = _checked ? Width - knob - 3 : 3;
        using var knobBrush = new SolidBrush(Color.White);
        g.FillEllipse(knobBrush, kx, 3, knob, knob);
    }
}
