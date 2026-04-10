using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ProdToy.Sdk;

namespace ProdToy.Plugins.Alarm;

/// <summary>
/// Local copy of RoundedButton (host control not available to plugins).
/// </summary>
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

/// <summary>
/// Minimal P/Invoke for alarm popup window.
/// </summary>
static partial class AlarmNativeMethods
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);
}

/// <summary>
/// Icon helper to create themed app icons for alarm forms.
/// </summary>
static class IconHelper
{
    public static Icon CreateAppIcon(Color primary)
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(primary);
        g.FillEllipse(brush, 2, 2, 28, 28);
        using var pen = new Pen(Color.White, 2f);
        g.DrawLine(pen, 16, 8, 16, 16);
        g.DrawLine(pen, 16, 16, 22, 16);
        return Icon.FromHandle(bmp.GetHicon());
    }
}
