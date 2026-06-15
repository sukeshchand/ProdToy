using System.Drawing;
using System.Drawing.Drawing2D;

namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>
/// Loads the shared red ProdToy brand icon (embedded as <c>ProdToy.AppIcon.png</c>)
/// for this plugin's windows, so they show the app icon instead of the default
/// WinForms window icon. Mirrors the Alarm / Screenshot / ShortCutManager plugins.
/// </summary>
static class IconHelper
{
    /// <summary>The fixed red brand icon, loaded from the embedded PNG. The colour
    /// argument is ignored — the icon is always red, independent of theme — and is
    /// kept only for call-site symmetry with the host's CreateAppIcon.</summary>
    public static Icon CreateAppIcon(Color primary)
    {
        try
        {
            using var s = typeof(IconHelper).Assembly.GetManifestResourceStream("ProdToy.AppIcon.png");
            if (s != null)
            {
                using var img = new Bitmap(s);
                return Icon.FromHandle(img.GetHicon());
            }
        }
        catch { /* fall back below */ }

        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Color.FromArgb(0xE5, 0x48, 0x4D));
        g.FillEllipse(brush, 2, 2, 28, 28);
        return Icon.FromHandle(bmp.GetHicon());
    }
}
