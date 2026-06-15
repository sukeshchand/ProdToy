using System.Drawing;

namespace ProdToy.Setup;

/// <summary>
/// Loads the ProdToy brand icon (red teddy + gear) embedded in the setup exe,
/// so the installer windows match the app. Falls back to a system icon if the
/// resource is somehow missing.
/// </summary>
static class SetupIcon
{
    public static Icon App()
    {
        try
        {
            using var s = typeof(SetupIcon).Assembly.GetManifestResourceStream("ProdToy.AppIcon.png");
            if (s != null)
            {
                using var bmp = new Bitmap(s);
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
        catch { /* fall back */ }
        return SystemIcons.Application;
    }
}
