using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ProdToy.Sdk;

namespace ProdToy.Plugins.Screenshot;

class ScreenshotOverlay : Form
{
    private Bitmap? _screenCapture;
    private Point _startPoint;
    private Point _currentPoint;
    private bool _isDragging;
    private bool _captured;

    /// <summary>Fires after a region is captured. The bitmap is the cropped screenshot (caller owns it).</summary>
    public event Action<Bitmap>? RegionCaptured;

    public ScreenshotOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        WindowState = FormWindowState.Normal;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        BackColor = Color.Black;
        Opacity = 1.0;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        // Capture the screen before showing — avoids capturing our own form
        CaptureFullScreen();
    }

    private void CaptureFullScreen()
    {
        // Capture all screens into one bitmap
        var totalBounds = SystemInformation.VirtualScreen;
        _screenCapture = new Bitmap(totalBounds.Width, totalBounds.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(_screenCapture);
        g.CopyFromScreen(totalBounds.Location, Point.Empty, totalBounds.Size, CopyPixelOperation.SourceCopy);

        // Position overlay to cover entire virtual screen (works on RDP + multi-monitor)
        Bounds = totalBounds;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _startPoint = e.Location;
            _currentPoint = e.Location;
            _isDragging = true;
        }
        else if (e.Button == MouseButtons.Right)
        {
            // Cancel
            Close();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_isDragging)
        {
            _currentPoint = e.Location;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!_isDragging || e.Button != MouseButtons.Left) return;
        _isDragging = false;
        _currentPoint = e.Location;

        var rect = GetSelectionRect();
        if (rect.Width < 5 || rect.Height < 5)
        {
            // Too small, ignore
            Close();
            return;
        }

        CaptureRegion(rect);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Close();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        if (_screenCapture != null)
        {
            // Draw the full screen capture as background
            g.DrawImage(_screenCapture, 0, 0);

            // Dark overlay on top
            using var dimBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
            g.FillRectangle(dimBrush, ClientRectangle);

            if (_isDragging || _captured)
            {
                var rect = GetSelectionRect();

                // Draw the selected region at full brightness (cut through the dim overlay)
                if (rect.Width > 0 && rect.Height > 0)
                {
                    g.SetClip(rect);
                    g.DrawImage(_screenCapture, 0, 0);
                    g.ResetClip();

                    // Selection border (dotted)
                    using var borderPen = new Pen(Color.FromArgb(200, 80, 160, 255), 1.5f);
                    borderPen.DashStyle = DashStyle.Dash;
                    borderPen.DashPattern = new float[] { 6f, 4f };
                    g.DrawRectangle(borderPen, rect);

                    // Corner handles
                    DrawCornerHandles(g, rect);

                    // Size label
                    DrawSizeLabel(g, rect);
                }
            }
        }

        // Instructions at top center
        if (!_isDragging)
        {
            string hint = "Click and drag to select a region. Right-click or Escape to cancel.";
            using var font = new Font("Segoe UI", 11f);
            var textSize = g.MeasureString(hint, font);
            float tx = (ClientSize.Width - textSize.Width) / 2;
            float ty = 20;

            using var bgBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
            var bgRect = new RectangleF(tx - 12, ty - 6, textSize.Width + 24, textSize.Height + 12);
            using var bgPath = RoundedRectPath(bgRect, 8);
            g.FillPath(bgBrush, bgPath);

            using var textBrush = new SolidBrush(Color.FromArgb(220, 255, 255, 255));
            g.DrawString(hint, font, textBrush, tx, ty);
        }
    }

    private void DrawCornerHandles(Graphics g, Rectangle rect)
    {
        int size = 6;
        using var brush = new SolidBrush(Color.FromArgb(220, 80, 160, 255));
        var corners = new[]
        {
            new Point(rect.Left, rect.Top),
            new Point(rect.Right, rect.Top),
            new Point(rect.Left, rect.Bottom),
            new Point(rect.Right, rect.Bottom),
        };
        foreach (var c in corners)
        {
            g.FillRectangle(brush, c.X - size / 2, c.Y - size / 2, size, size);
        }
    }

    private void DrawSizeLabel(Graphics g, Rectangle rect)
    {
        string sizeText = $"{rect.Width} x {rect.Height}";
        using var font = new Font("Segoe UI", 9f);
        var textSize = g.MeasureString(sizeText, font);

        float lx = rect.Left;
        float ly = rect.Bottom + 6;

        // Keep label on screen
        if (ly + textSize.Height + 8 > ClientSize.Height)
            ly = rect.Top - textSize.Height - 12;

        using var bgBrush = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
        var bgRect = new RectangleF(lx, ly, textSize.Width + 16, textSize.Height + 6);
        using var bgPath = RoundedRectPath(bgRect, 4);
        g.FillPath(bgBrush, bgPath);

        using var textBrush = new SolidBrush(Color.FromArgb(200, 80, 160, 255));
        g.DrawString(sizeText, font, textBrush, lx + 8, ly + 3);
    }

    private Rectangle GetSelectionRect()
    {
        int x = Math.Min(_startPoint.X, _currentPoint.X);
        int y = Math.Min(_startPoint.Y, _currentPoint.Y);
        int w = Math.Abs(_currentPoint.X - _startPoint.X);
        int h = Math.Abs(_currentPoint.Y - _startPoint.Y);
        return new Rectangle(x, y, w, h);
    }

    private void CaptureRegion(Rectangle rect)
    {
        if (_screenCapture == null) return;

        try
        {
            // Crop the region from the full screen capture
            var cropped = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(cropped))
            {
                g.DrawImage(_screenCapture, new Rectangle(0, 0, rect.Width, rect.Height),
                    rect, GraphicsUnit.Pixel);
            }

            _captured = true;
            RegionCaptured?.Invoke(cropped);
        }
        catch (Exception ex)
        {
            PluginLog.Error("Screenshot capture failed", ex);
        }

        Close();
    }

    private static GraphicsPath RoundedRectPath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _screenCapture?.Dispose();
        _screenCapture = null;
        base.OnFormClosed(e);
    }
}
