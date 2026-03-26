using System.Drawing;
using System.Drawing.Drawing2D;

namespace DevToy;

/// <summary>
/// Hosts the ScreenshotCanvas as a fixed-size child, centers it, and provides
/// resize handles on the canvas edges. During drag only a preview frame is shown;
/// the actual resize is applied on mouse release.
/// </summary>
class CanvasContainer : Panel
{
    private ScreenshotCanvas _canvas;
    private const int HandleSize = 8;
    private const int HandleHitZone = 10;

    // Resize state
    private bool _isResizingCanvas;
    private HandlePosition _resizeHandle;
    private Point _resizeStart;
    private Rectangle _canvasBoundsAtStart;
    private Rectangle _previewRect;

    public CanvasContainer(ScreenshotCanvas canvas)
    {
        _canvas = canvas;

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        BackColor = Color.FromArgb(30, 30, 30);
        AutoScroll = false;

        Controls.Add(_canvas);
        CenterCanvas();
    }

    /// <summary>Replace the canvas with a new one.</summary>
    public void SetCanvas(ScreenshotCanvas newCanvas)
    {
        Controls.Remove(_canvas);
        _canvas = newCanvas;
        Controls.Add(_canvas);
        CenterCanvas();
    }

    /// <summary>Sync the canvas control size from the session's CanvasSize and recenter.</summary>
    public void SyncCanvasSize()
    {
        if (_canvas.Session != null)
        {
            _canvas.Size = new Size(_canvas.Session.CanvasSize.Width, _canvas.Session.CanvasSize.Height);
        }
        CenterCanvas();
    }

    /// <summary>Recenter the canvas within this container.</summary>
    public void CenterCanvas()
    {
        int x = Math.Max(0, (ClientSize.Width - _canvas.Width) / 2);
        int y = Math.Max(0, (ClientSize.Height - _canvas.Height) / 2);
        _canvas.Location = new Point(x, y);
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        CenterCanvas();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Background
        using (var bgBrush = new SolidBrush(BackColor))
            g.FillRectangle(bgBrush, ClientRectangle);

        // Border around the canvas
        var cr = _canvas.Bounds;
        using var borderPen = new Pen(Color.FromArgb(100, 128, 128, 128), 1f);
        g.DrawRectangle(borderPen, cr.X - 1, cr.Y - 1, cr.Width + 1, cr.Height + 1);

        // Draw resize handles (on the actual canvas, not the preview)
        if (!_isResizingCanvas)
        {
            DrawResizeHandles(g, cr);
        }

        // Draw preview frame during drag
        if (_isResizingCanvas)
        {
            using var previewPen = new Pen(Color.FromArgb(180, 80, 160, 255), 1.5f);
            previewPen.DashStyle = DashStyle.Dash;
            previewPen.DashPattern = new float[] { 6f, 4f };
            g.DrawRectangle(previewPen, _previewRect);

            // Size label on preview
            string sizeText = $"{_previewRect.Width} x {_previewRect.Height}";
            using var font = new Font("Segoe UI", 9f);
            var textSize = g.MeasureString(sizeText, font);
            float lx = _previewRect.Right - textSize.Width - 4;
            float ly = _previewRect.Bottom + 4;
            using var labelBg = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
            g.FillRectangle(labelBg, lx - 4, ly - 1, textSize.Width + 8, textSize.Height + 2);
            using var labelBrush = new SolidBrush(Color.FromArgb(200, 80, 160, 255));
            g.DrawString(sizeText, font, labelBrush, lx, ly);

            // Also draw handles on the preview rect
            DrawResizeHandles(g, _previewRect);
        }
    }

    private void DrawResizeHandles(Graphics g, Rectangle rect)
    {
        using var brush = new SolidBrush(Color.FromArgb(200, 80, 160, 255));
        var handles = GetHandleRects(rect);
        foreach (var r in handles)
            g.FillRectangle(brush, r);
    }

    private static RectangleF[] GetHandleRects(Rectangle cr)
    {
        int hs = HandleSize;
        int hh = hs / 2;
        float cx = cr.X + cr.Width / 2f;
        float cy = cr.Y + cr.Height / 2f;

        return new RectangleF[]
        {
            new(cr.Left - hh, cr.Top - hh, hs, hs),
            new(cx - hh, cr.Top - hh, hs, hs),
            new(cr.Right - hh, cr.Top - hh, hs, hs),
            new(cr.Left - hh, cy - hh, hs, hs),
            new(cr.Right - hh, cy - hh, hs, hs),
            new(cr.Left - hh, cr.Bottom - hh, hs, hs),
            new(cx - hh, cr.Bottom - hh, hs, hs),
            new(cr.Right - hh, cr.Bottom - hh, hs, hs),
        };
    }

    private static readonly HandlePosition[] HandlePositions =
    {
        HandlePosition.TopLeft, HandlePosition.TopCenter, HandlePosition.TopRight,
        HandlePosition.MiddleLeft, HandlePosition.MiddleRight,
        HandlePosition.BottomLeft, HandlePosition.BottomCenter, HandlePosition.BottomRight,
    };

    private HandlePosition HitTestHandle(Point pt)
    {
        var handles = GetHandleRects(_canvas.Bounds);
        for (int i = 0; i < handles.Length; i++)
        {
            var r = handles[i];
            r.Inflate(HandleHitZone / 2f, HandleHitZone / 2f);
            if (r.Contains(pt))
                return HandlePositions[i];
        }
        return HandlePosition.None;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        var handle = HitTestHandle(e.Location);
        if (handle != HandlePosition.None)
        {
            _isResizingCanvas = true;
            _resizeHandle = handle;
            _resizeStart = e.Location;
            _canvasBoundsAtStart = _canvas.Bounds;
            _previewRect = _canvas.Bounds;
            Capture = true;
            Invalidate();
            return;
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_isResizingCanvas)
        {
            int dx = e.X - _resizeStart.X;
            int dy = e.Y - _resizeStart.Y;

            // Compute the preview rect by adjusting the appropriate edges
            var r = _canvasBoundsAtStart;
            int left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;

            switch (_resizeHandle)
            {
                case HandlePosition.TopLeft:     left += dx; top += dy; break;
                case HandlePosition.TopCenter:   top += dy; break;
                case HandlePosition.TopRight:    right += dx; top += dy; break;
                case HandlePosition.MiddleLeft:  left += dx; break;
                case HandlePosition.MiddleRight: right += dx; break;
                case HandlePosition.BottomLeft:  left += dx; bottom += dy; break;
                case HandlePosition.BottomCenter: bottom += dy; break;
                case HandlePosition.BottomRight: right += dx; bottom += dy; break;
            }

            // Enforce minimum size
            if (right - left < 50) { if (left != _canvasBoundsAtStart.Left) left = right - 50; else right = left + 50; }
            if (bottom - top < 50) { if (top != _canvasBoundsAtStart.Top) top = bottom - 50; else bottom = top + 50; }

            _previewRect = Rectangle.FromLTRB(left, top, right, bottom);
            Invalidate();
            return;
        }

        // Update cursor based on handle hover
        var hit = HitTestHandle(e.Location);
        Cursor = hit switch
        {
            HandlePosition.TopLeft or HandlePosition.BottomRight => Cursors.SizeNWSE,
            HandlePosition.TopRight or HandlePosition.BottomLeft => Cursors.SizeNESW,
            HandlePosition.TopCenter or HandlePosition.BottomCenter => Cursors.SizeNS,
            HandlePosition.MiddleLeft or HandlePosition.MiddleRight => Cursors.SizeWE,
            _ => Cursors.Default,
        };

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_isResizingCanvas)
        {
            _isResizingCanvas = false;
            Capture = false;

            var oldBounds = _canvasBoundsAtStart;
            var newBounds = _previewRect;

            int leftDelta = oldBounds.Left - newBounds.Left;
            int topDelta = oldBounds.Top - newBounds.Top;
            int newW = newBounds.Width;
            int newH = newBounds.Height;

            if (_canvas.Session != null)
            {
                var session = _canvas.Session;
                var oldSize = session.CanvasSize;
                var oldOffset = session.ImageOffset;
                var newSize = new Size(newW, newH);
                var newOffset = new Point(oldOffset.X + leftDelta, oldOffset.Y + topDelta);

                // Execute through undo system
                var action = new CanvasResizeAction(session, oldSize, newSize, oldOffset, newOffset, leftDelta, topDelta);
                session.UndoRedo.Execute(action);
            }

            _canvas.Size = new Size(newW, newH);
            CenterCanvas();
            return;
        }

        base.OnMouseUp(e);
    }
}
