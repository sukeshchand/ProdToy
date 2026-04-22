using System.Drawing;
using System.Drawing.Imaging;
using ProdToy.Sdk;

namespace ProdToy.Plugins.Screenshot;

static class ScreenshotExporter
{
    /// <summary>Flatten original image + all annotations into a final bitmap.</summary>
    public static Bitmap Flatten(EditorSession session)
    {
        var canvasSize = session.CanvasSize;
        var imgOffset = session.ImageOffset;
        var result = new Bitmap(canvasSize.Width, canvasSize.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);

        // Background color for canvas (including expanded areas)
        g.Clear(session.CanvasBackgroundColor);

        // Draw original image at its offset
        g.DrawImage(session.OriginalImage, imgOffset.X, imgOffset.Y);

        // Render border if enabled
        if (session.BorderEnabled)
            RenderBorder(g, canvasSize, session);

        // Render annotations in z-order (list order = z-order)
        foreach (var obj in session.Annotations)
        {
            obj.IsSelected = false; // don't render handles in export
            obj.Render(g);
        }

        return result;
    }

    /// <summary>Save flattened image to a file.</summary>
    public static string SaveToFile(EditorSession session, string? filePath = null)
    {
        filePath ??= GenerateFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        using var flattened = Flatten(session);
        var format = Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ImageFormat.Jpeg,
            ".bmp" => ImageFormat.Bmp,
            _ => ImageFormat.Png,
        };
        flattened.Save(filePath, format);
        return filePath;
    }

    /// <summary>Copy flattened image to clipboard.</summary>
    public static void CopyToClipboard(EditorSession session)
    {
        try
        {
            using var flattened = Flatten(session);
            Clipboard.SetImage(flattened);
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"Clipboard copy failed: {ex.Message}");
        }
    }

    private static void RenderBorder(Graphics g, Size canvasSize, EditorSession session)
    {
        float t = session.BorderThickness;
        float half = t / 2;
        using var pen = new Pen(session.BorderColor, t);
        var rect = new RectangleF(half, half, canvasSize.Width - t, canvasSize.Height - t);

        switch (session.BorderStyle)
        {
            case CanvasBorderStyle.Solid:
                g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                break;
            case CanvasBorderStyle.Dashed:
                pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                pen.DashPattern = new float[] { 8f, 4f };
                g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                break;
            case CanvasBorderStyle.Dotted:
                pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dot;
                g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                break;
            case CanvasBorderStyle.Double:
                pen.Width = Math.Max(1f, t / 3);
                float gap = t / 2;
                g.DrawRectangle(pen, half, half, canvasSize.Width - t, canvasSize.Height - t);
                g.DrawRectangle(pen, half + gap, half + gap, canvasSize.Width - t - gap * 2, canvasSize.Height - t - gap * 2);
                break;
            case CanvasBorderStyle.Shadow:
                g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                float sh = Math.Max(2f, t);
                using (var shadowBrush = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
                {
                    g.FillRectangle(shadowBrush, rect.Right, rect.Y + sh, sh, rect.Height);
                    g.FillRectangle(shadowBrush, rect.X + sh, rect.Bottom, rect.Width, sh);
                }
                break;
        }
    }

    public static string GenerateFilePath()
    {
        string dir = ScreenshotPaths.ScreenshotsDir;
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        return Path.Combine(dir, $"screenshot_{timestamp}.png");
    }
}
