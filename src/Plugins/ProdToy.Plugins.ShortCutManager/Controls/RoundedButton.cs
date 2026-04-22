using System.Drawing;
using System.Drawing.Drawing2D;

namespace ProdToy.Plugins.ShortCutManager;

// Plugin-local copy of the host's RoundedButton (see CLAUDE.md: plugins
// include their own copies of shared controls since they can't reference
// host internals).
class RoundedButton : Button
{
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width, Height);
        using var path = GetRoundedRect(rect, 8);
        using var brush = new SolidBrush(BackColor);
        g.Clear(Parent?.BackColor ?? Color.Transparent);
        g.FillPath(brush, path);

        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var textBrush = new SolidBrush(ForeColor);
        g.DrawString(Text, Font, textBrush, new RectangleF(0, 0, Width, Height), sf);
    }

    private static GraphicsPath GetRoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
