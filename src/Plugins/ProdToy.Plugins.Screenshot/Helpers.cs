using System.Drawing;
using System.Drawing.Drawing2D;
using ProdToy.Sdk;

namespace ProdToy.Plugins.Screenshot;

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
/// Icon helper to create themed app icons for screenshot forms.
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

/// <summary>
/// Centralized paths for the screenshot plugin. Initialized by ScreenshotPlugin.
/// </summary>
static class ScreenshotPaths
{
    public static string ScreenshotsDir { get; private set; } = "";
    public static string ScreenshotsEditsDir { get; private set; } = "";

    /// <summary>
    /// Per-installation environment id, read from ~/.prod-toy/launchSettings.json.
    /// Stamped into new screenshot filenames so a synced screenshots folder never
    /// has two machines colliding on the same timestamp. "" when not configured.
    /// </summary>
    public static string EnvId { get; private set; } = "";

    public static void Initialize(string dataDirectory)
    {
        ScreenshotsDir = Path.Combine(dataDirectory, "screenshots");
        ScreenshotsEditsDir = Path.Combine(dataDirectory, "screenshots", "_edits");
        Directory.CreateDirectory(ScreenshotsDir);
        Directory.CreateDirectory(ScreenshotsEditsDir);
        EnvId = ReadEnvId();
    }

    /// <summary>Build a new screenshot base name (without extension): "screenshot_{envId}_{yyyy-MM-dd_HHmmss}".
    /// Falls back to the legacy "screenshot_{timestamp}" when envId is unavailable.</summary>
    public static string NewScreenshotBaseName()
    {
        string ts = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        return string.IsNullOrEmpty(EnvId)
            ? $"screenshot_{ts}"
            : $"screenshot_{EnvId}_{ts}";
    }

    private static string ReadEnvId()
    {
        try
        {
            string launchSettingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".prod-toy", "launchSettings.json");
            if (!File.Exists(launchSettingsPath)) return "";
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(launchSettingsPath));
            var id = root?["envId"]?.GetValue<string>();
            return !string.IsNullOrWhiteSpace(id) ? id : "";
        }
        catch { return ""; }
    }
}
