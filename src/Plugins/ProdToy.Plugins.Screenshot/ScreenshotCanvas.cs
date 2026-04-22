using System.Drawing;
using System.Drawing.Drawing2D;
using ProdToy.Sdk;

namespace ProdToy.Plugins.Screenshot;

class ScreenshotCanvas : Control
{
    private EditorSession? _session;

    // Drawing state
    private bool _isDrawing;
    private AnnotationObject? _drawingObject;
    private PointF _lastDragPos;
    private bool _hasDrawnOnce;

    // Selection/move state
    private bool _isMoving;
    private bool _isResizing;
    private bool _isRotating;
    private HandlePosition _resizeHandle;
    private float _totalMoveDx, _totalMoveDy;
    private float _totalRotation;

    // Text editing
    private TextObject? _editingText;

    // Region selection (for convert to layer)
    private bool _isSelectingRegion;
    private PointF _regionStart;
    private PointF _regionEnd;
    private RectangleF? _selectedRegion;

    // Eraser state
    private bool _isErasing;
    private Bitmap? _eraserTarget;
    private PointF _eraserTargetOffset;
    private SizeF _eraserTargetScale;
    private readonly List<PointF> _eraserPoints = new();
    private PointF _lastEraserPoint;
    private Bitmap? _eraserBeforeSnapshot;
    private PointF _eraserCursorPos;
    private bool _eraserCursorVisible;

    // Crop state (perspective: 4 independent corners)
    private bool _isCropDragging;   // Phase 1: user is dragging to define initial area
    private bool _isCropAdjusting;  // Phase 2: user adjusts corners
    private PointF _cropDragStart;
    private PointF[] _cropCorners = new PointF[4]; // TL, TR, BR, BL
    private int _dragCornerIndex = -1; // -1=none, 0-3=corner, 4-7=midpoint, 8=move all

    // Zoom state — view transform only, never persisted, never undoable.
    // Mouse events arrive in display pixels; we divide by _zoom to get logical
    // coordinates that match what OnPaint draws under ScaleTransform(_zoom).
    private float _zoom = 1.0f;
    private int _logicalWidth;
    private int _logicalHeight;
    public const float MinZoom = 0.1f;
    public const float MaxZoom = 8.0f;

    public float Zoom => _zoom;
    public Size LogicalSize => new(_logicalWidth, _logicalHeight);

    /// <summary>
    /// When true, the canvas draws resize handles at its edges. Set by
    /// <see cref="CanvasContainer"/> based on zoom level and crop state.
    /// </summary>
    public bool ShowResizeHandles { get; set; } = true;

    public event Action? ZoomChanged;

    public event Action? CanvasChanged;
    public event Action? SelectionChanged;
    public event Action? ToolAutoSwitched;

    /// <summary>Fires when canvas needs to be resized (e.g. to fit a dropped image).</summary>
    public event Action<Size>? CanvasResizeRequested;

    public ScreenshotCanvas()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        BackColor = Color.FromArgb(30, 30, 30);
        Cursor = Cursors.Cross;
        AllowDrop = true;

        DragEnter += OnDragEnter;
        DragDrop += OnDragDropHandler;
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.Text) == true ||
            e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Copy;
        }
    }

    private void OnDragDropHandler(object? sender, DragEventArgs e)
    {
        if (_session == null) return;

        string? filePath = null;

        // From our panel (text = file path)
        if (e.Data?.GetDataPresent(DataFormats.Text) == true)
            filePath = e.Data.GetData(DataFormats.Text) as string;

        // From external file drop
        if (filePath == null && e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files?.Length > 0) filePath = files[0];
        }

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

        try
        {
            // Load the image
            Bitmap img;
            using (var stream = File.OpenRead(filePath))
            using (var bmp = new Bitmap(stream))
            {
                img = new Bitmap(bmp.Width, bmp.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(img);
                g.DrawImage(bmp, 0, 0);
            }

            // Calculate drop position in canvas coordinates
            // PointToClient returns display pixels; divide by zoom to get logical coords.
            var clientPt = PointToClient(new Point(e.X, e.Y));
            float dropX = Math.Max(0, clientPt.X / _zoom);
            float dropY = Math.Max(0, clientPt.Y / _zoom);

            // Check if canvas needs to expand to fit the dropped image
            float neededW = dropX + img.Width;
            float neededH = dropY + img.Height;
            if (neededW > _session.CanvasSize.Width || neededH > _session.CanvasSize.Height)
            {
                var newSize = new Size(
                    Math.Max(_session.CanvasSize.Width, (int)neededW),
                    Math.Max(_session.CanvasSize.Height, (int)neededH));
                _session.CanvasSize = newSize;
                SetLogicalSize(newSize.Width, newSize.Height);
                CanvasResizeRequested?.Invoke(newSize);
            }

            // Copy image into the _edits folder so it's preserved independently
            string localPath = filePath;
            if (!string.IsNullOrEmpty(_session.EditId))
            {
                string editsDir = _session.EditDir;
                Directory.CreateDirectory(editsDir);
                string localName = $"layer_{DateTime.Now:HHmmss}_{Path.GetFileName(filePath)}";
                localPath = Path.Combine(editsDir, localName);
                img.Save(localPath, System.Drawing.Imaging.ImageFormat.Png);
            }

            // Create the image annotation
            var imageObj = new ImageObject
            {
                Image = img,
                Position = new PointF(dropX, dropY),
                DisplaySize = new SizeF(img.Width, img.Height),
                SourcePath = localPath,
            };

            _session.AddAnnotation(imageObj);
            Invalidate();
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"ScreenshotCanvas drop failed: {ex.Message}");
        }
    }

    public EditorSession? Session
    {
        get => _session;
        set
        {
            _session = value;
            if (_session != null)
            {
                _logicalWidth = _session.CanvasSize.Width;
                _logicalHeight = _session.CanvasSize.Height;
                ApplyZoomToControlSize();
            }
            Invalidate();
        }
    }

    /// <summary>Set logical (image-space) canvas size. Display size becomes logical * zoom.</summary>
    public void SetLogicalSize(int width, int height)
    {
        _logicalWidth = width;
        _logicalHeight = height;
        ApplyZoomToControlSize();
        Invalidate();
    }

    private void ApplyZoomToControlSize()
    {
        int dispW = Math.Max(1, (int)Math.Round(_logicalWidth * _zoom));
        int dispH = Math.Max(1, (int)Math.Round(_logicalHeight * _zoom));
        if (Size.Width != dispW || Size.Height != dispH)
            Size = new Size(dispW, dispH);
    }

    /// <summary>Set zoom level. Clamps to [MinZoom, MaxZoom]. Does not raise event if unchanged.</summary>
    public void SetZoom(float newZoom)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 0.0001f) return;
        _zoom = newZoom;
        ApplyZoomToControlSize();
        Invalidate();
        ZoomChanged?.Invoke();
    }

    public void ResetZoom() => SetZoom(1.0f);

    /// <summary>Fit logical canvas inside the given container client size, with a small margin.</summary>
    public void FitToContainer(Size containerSize)
    {
        if (_logicalWidth <= 0 || _logicalHeight <= 0) return;
        const int margin = 24;
        float availW = Math.Max(1, containerSize.Width - margin * 2);
        float availH = Math.Max(1, containerSize.Height - margin * 2);
        float fit = Math.Min(availW / _logicalWidth, availH / _logicalHeight);
        SetZoom(fit);
    }

    private static readonly float[] ZoomSnaps =
        { 0.1f, 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f, 3f, 4f, 6f, 8f };

    public void ZoomInStep()
    {
        foreach (var s in ZoomSnaps)
            if (s > _zoom + 0.0001f) { SetZoom(s); return; }
    }

    public void ZoomOutStep()
    {
        for (int i = ZoomSnaps.Length - 1; i >= 0; i--)
            if (ZoomSnaps[i] < _zoom - 0.0001f) { SetZoom(ZoomSnaps[i]); return; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        if (_session == null)
        {
            using var checker = new HatchBrush(HatchStyle.LargeCheckerBoard,
                Color.FromArgb(50, 50, 50), Color.FromArgb(40, 40, 40));
            g.FillRectangle(checker, ClientRectangle);
            return;
        }

        // Apply zoom transform — everything below draws in LOGICAL coordinates.
        // Logical canvas size is _logicalWidth × _logicalHeight regardless of zoom.
        if (Math.Abs(_zoom - 1.0f) > 0.0001f)
            g.ScaleTransform(_zoom, _zoom);

        var logicalRect = new Rectangle(0, 0, _logicalWidth, _logicalHeight);

        // Fill with canvas background color
        using (var bgBrush = new SolidBrush(_session.CanvasBackgroundColor))
            g.FillRectangle(bgBrush, logicalRect);

        // Draw the original image at its offset (shifts when canvas expands left/top)
        var imgOff = _session.ImageOffset;
        g.DrawImage(_session.OriginalImage, imgOff.X, imgOff.Y);

        // Draw border if enabled
        if (_session.BorderEnabled)
            RenderBorder(g);

        // Draw all annotations in order (list order = z-order)
        foreach (var obj in _session.Annotations)
        {
            obj.Render(g);
            if (obj.IsSelected)
                obj.RenderSelectionHandles(g);
        }

        // Draw in-progress shape/line
        if (_isDrawing && _drawingObject != null)
        {
            _drawingObject.Render(g);
        }

        // Draw eraser cursor circle (dual-color for visibility on any background)
        if (_eraserCursorVisible && _session.CurrentTool == AnnotationTool.Eraser)
        {
            float r = _session.CurrentThickness * 2;
            float cx = _eraserCursorPos.X - r;
            float cy = _eraserCursorPos.Y - r;
            float d = r * 2;
            using var outerPen = new Pen(Color.Black, 1.5f);
            g.DrawEllipse(outerPen, cx, cy, d, d);
            using var innerPen = new Pen(Color.White, 1.5f);
            innerPen.DashStyle = DashStyle.Dash;
            innerPen.DashPattern = new float[] { 4f, 4f };
            g.DrawEllipse(innerPen, cx, cy, d, d);
        }

        // Draw perspective crop overlay
        if (_isCropDragging || _isCropAdjusting)
        {
            var cc = GetNormalizedCropCorners();

            // Dim area outside the quad using GraphicsPath clipping
            using var dimPath = new GraphicsPath();
            dimPath.AddRectangle(new RectangleF(0, 0, _logicalWidth, _logicalHeight));
            dimPath.AddPolygon(cc);
            using var dimBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
            g.FillPath(dimBrush, dimPath);

            // Quad border — black + white dashed double line (visible on all backgrounds)
            using var outerPen = new Pen(Color.Black, 4f);
            g.DrawPolygon(outerPen, cc);
            using var innerPen = new Pen(Color.White, 2.5f);
            innerPen.DashStyle = DashStyle.Dash;
            innerPen.DashPattern = new float[] { 5f, 5f };
            g.DrawPolygon(innerPen, cc);

            // Perspective grid (3x3) using bilinear interpolation
            using var gridPenBlack = new Pen(Color.FromArgb(80, 0, 0, 0), 1.5f);
            using var gridPenWhite = new Pen(Color.FromArgb(120, 255, 255, 255), 1f);
            gridPenWhite.DashStyle = DashStyle.Dash;
            gridPenWhite.DashPattern = new float[] { 4f, 4f };
            for (int i = 1; i <= 2; i++)
            {
                float t = i / 3f;
                var hl = Lerp(cc[0], cc[3], t);
                var hr = Lerp(cc[1], cc[2], t);
                g.DrawLine(gridPenBlack, hl, hr);
                g.DrawLine(gridPenWhite, hl, hr);
                var vt = Lerp(cc[0], cc[1], t);
                var vb = Lerp(cc[3], cc[2], t);
                g.DrawLine(gridPenBlack, vt, vb);
                g.DrawLine(gridPenWhite, vt, vb);
            }

            // 8 handles: 4 corners + 4 midpoints
            float hs = 8f, hh = hs / 2;
            using var handleFill = new SolidBrush(Color.Black);
            using var handleBorder = new Pen(Color.White, 1.5f);
            // Corners
            foreach (var c in cc)
            {
                g.FillRectangle(handleFill, c.X - hh, c.Y - hh, hs, hs);
                g.DrawRectangle(handleBorder, c.X - hh, c.Y - hh, hs, hs);
            }
            // Midpoints: top, right, bottom, left
            var midpoints = new[] {
                Lerp(cc[0], cc[1], 0.5f), // top mid
                Lerp(cc[1], cc[2], 0.5f), // right mid
                Lerp(cc[2], cc[3], 0.5f), // bottom mid
                Lerp(cc[3], cc[0], 0.5f), // left mid
            };
            foreach (var m in midpoints)
            {
                g.FillRectangle(handleFill, m.X - hh, m.Y - hh, hs, hs);
                g.DrawRectangle(handleBorder, m.X - hh, m.Y - hh, hs, hs);
            }

            // Size label (estimated output dimensions)
            float outW = Math.Max(Dist(cc[0], cc[1]), Dist(cc[3], cc[2]));
            float outH = Math.Max(Dist(cc[0], cc[3]), Dist(cc[1], cc[2]));
            using var labelFont = new Font("Segoe UI", 8f);
            string sizeText = $"{(int)outW} x {(int)outH}";
            var textSize = g.MeasureString(sizeText, labelFont);
            float lx = cc[2].X - textSize.Width;
            float ly = cc[2].Y + 4;
            using var labelBg = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
            g.FillRectangle(labelBg, lx - 2, ly, textSize.Width + 4, textSize.Height);
            using var labelBrush = new SolidBrush(Color.White);
            g.DrawString(sizeText, labelFont, labelBrush, lx, ly);

            // Hint text (only in adjust phase)
            if (_isCropAdjusting)
            {
                string hint = "Drag corners to adjust \u2022 Enter to crop \u2022 Esc to cancel";
                var hintSize = g.MeasureString(hint, labelFont);
                float hx = (cc[0].X + cc[1].X) / 2 - hintSize.Width / 2;
                float hy = Math.Min(cc[0].Y, cc[1].Y) - hintSize.Height - 6;
                if (hy < 0) hy = Math.Max(cc[2].Y, cc[3].Y) + 20;
                g.FillRectangle(labelBg, hx - 2, hy, hintSize.Width + 4, hintSize.Height);
                g.DrawString(hint, labelFont, labelBrush, hx, hy);
            }
        }

        // Draw region selection rectangle
        if (_isSelectingRegion || _selectedRegion.HasValue)
        {
            var rect = _selectedRegion ?? GetRegionRect();
            if (rect.Width > 2 && rect.Height > 2)
            {
                // Semi-transparent overlay outside selection
                using var dimBrush = new SolidBrush(Color.FromArgb(60, 0, 0, 0));
                g.FillRectangle(dimBrush, 0, 0, _logicalWidth, rect.Y);
                g.FillRectangle(dimBrush, 0, rect.Bottom, _logicalWidth, _logicalHeight - rect.Bottom);
                g.FillRectangle(dimBrush, 0, rect.Y, rect.X, rect.Height);
                g.FillRectangle(dimBrush, rect.Right, rect.Y, _logicalWidth - rect.Right, rect.Height);

                // Dashed selection border
                using var borderPen = new Pen(Color.FromArgb(200, 80, 160, 255), 1.5f);
                borderPen.DashStyle = DashStyle.Dash;
                borderPen.DashPattern = new float[] { 6f, 4f };
                g.DrawRectangle(borderPen, rect.X, rect.Y, rect.Width, rect.Height);

                // Size label
                using var font = new Font("Segoe UI", 8f);
                string sizeText = $"{(int)rect.Width} x {(int)rect.Height}";
                var textSize = g.MeasureString(sizeText, font);
                float lx = rect.Right - textSize.Width - 4;
                float ly = rect.Y - textSize.Height - 4;
                if (ly < 0) ly = rect.Bottom + 4;
                using var labelBg = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
                g.FillRectangle(labelBg, lx - 2, ly, textSize.Width + 4, textSize.Height);
                using var labelBrush = new SolidBrush(Color.FromArgb(200, 80, 160, 255));
                g.DrawString(sizeText, font, labelBrush, lx, ly);
            }
        }

        // --- Canvas resize handles (drawn last, in display coords) ---
        // Reset any zoom transform so handles are in pixel coords and sit
        // at the physical edges of this control — never occluded.
        if (ShowResizeHandles && !IsCropActive)
        {
            g.ResetTransform();
            int hs = 8, hh = hs / 2;
            float cxH = Width / 2f;
            float cyH = Height / 2f;
            var handleRects = new RectangleF[]
            {
                new(-hh, -hh, hs, hs),
                new(cxH - hh, -hh, hs, hs),
                new(Width - hh, -hh, hs, hs),
                new(-hh, cyH - hh, hs, hs),
                new(Width - hh, cyH - hh, hs, hs),
                new(-hh, Height - hh, hs, hs),
                new(cxH - hh, Height - hh, hs, hs),
                new(Width - hh, Height - hh, hs, hs),
            };
            using var hBrush = new SolidBrush(Color.FromArgb(200, 80, 160, 255));
            foreach (var hr in handleRects)
                g.FillRectangle(hBrush, hr);
        }
    }

    private RectangleF GetRegionRect()
    {
        float x = Math.Min(_regionStart.X, _regionEnd.X);
        float y = Math.Min(_regionStart.Y, _regionEnd.Y);
        float w = Math.Abs(_regionEnd.X - _regionStart.X);
        float h = Math.Abs(_regionEnd.Y - _regionStart.Y);
        return new RectangleF(x, y, w, h);
    }

    private void RenderBorder(Graphics g)
    {
        if (_session == null) return;
        float t = _session.BorderThickness;
        float half = t / 2;
        // Border draws inside the zoom transform → use logical dimensions.
        float lw = _logicalWidth;
        float lh = _logicalHeight;
        var rect = new RectangleF(half, half, lw - t, lh - t);

        using var pen = new Pen(_session.BorderColor, t);
        switch (_session.BorderStyle)
        {
            case CanvasBorderStyle.Solid:
                pen.DashStyle = DashStyle.Solid;
                g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                break;
            case CanvasBorderStyle.Dashed:
                pen.DashStyle = DashStyle.Dash;
                pen.DashPattern = new float[] { 8f, 4f };
                g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                break;
            case CanvasBorderStyle.Dotted:
                pen.DashStyle = DashStyle.Dot;
                g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                break;
            case CanvasBorderStyle.Double:
                pen.Width = Math.Max(1f, t / 3);
                float gap = t / 2;
                g.DrawRectangle(pen, half, half, lw - t, lh - t);
                g.DrawRectangle(pen, half + gap, half + gap, lw - t - gap * 2, lh - t - gap * 2);
                break;
            case CanvasBorderStyle.Shadow:
                pen.DashStyle = DashStyle.Solid;
                g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                // Shadow offset
                float sh = Math.Max(2f, t);
                using (var shadowBrush = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
                {
                    g.FillRectangle(shadowBrush, rect.Right, rect.Y + sh, sh, rect.Height);
                    g.FillRectangle(shadowBrush, rect.X + sh, rect.Bottom, rect.Width, sh);
                }
                break;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (_session == null) return;

        // Right-click context menu — Location stays in display pixels for popup positioning.
        if (e.Button == MouseButtons.Right)
        {
            ShowCanvasContextMenu(e.Location);
            return;
        }

        if (e.Button != MouseButtons.Left) return;
        Focus();
        // Convert display pixels → logical canvas coordinates so all hit-testing,
        // drawing, and selection work in the unscaled coordinate space.
        var pt = new PointF(e.X / _zoom, e.Y / _zoom);
        // Hit-test tolerance in logical pixels (keeps ~6 display-px feel at any zoom).
        float tol = 6f / _zoom;

        // If editing text, commit on click outside
        if (_editingText != null)
        {
            if (!_editingText.HitTest(pt, tol))
                CommitTextEdit();
        }

        switch (_session.CurrentTool)
        {
            case AnnotationTool.Select:
                HandleSelectMouseDown(pt);
                break;
            case AnnotationTool.Pen:
                StartPenDraw(pt, false);
                break;
            case AnnotationTool.Marker:
                StartPenDraw(pt, true);
                break;
            case AnnotationTool.Line:
            case AnnotationTool.Arrow:
            case AnnotationTool.Rectangle:
            case AnnotationTool.Ellipse:
            case AnnotationTool.MaskBox:
                StartShapeDraw(pt);
                break;
            case AnnotationTool.Text:
                HandleTextClick(pt);
                break;
            case AnnotationTool.Eraser:
                StartErase(pt);
                break;
            case AnnotationTool.Crop:
                HandleCropMouseDown(pt);
                break;
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (_session == null || e.Button != MouseButtons.Left) { base.OnMouseDoubleClick(e); return; }

        // Double-click applies crop
        if (_isCropAdjusting && _session.CurrentTool == AnnotationTool.Crop)
        {
            ApplyCrop();
            return;
        }
        var pt = new PointF(e.X / _zoom, e.Y / _zoom);
        float tol = 6f / _zoom;

        // Double-click on a TextObject in Select mode → re-enter text editing
        if (_session.CurrentTool == AnnotationTool.Select)
        {
            for (int i = _session.Annotations.Count - 1; i >= 0; i--)
            {
                if (_session.Annotations[i] is TextObject txt && txt.HitTest(pt, tol))
                {
                    _isMoving = false;
                    StartTextEdit(txt);
                    Invalidate();
                    return;
                }
            }
        }
        base.OnMouseDoubleClick(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_session == null) return;
        var pt = new PointF(e.X / _zoom, e.Y / _zoom);

        if (_isCropDragging || _dragCornerIndex >= 0)
        {
            HandleCropMouseMove(pt);
            Invalidate();
            return;
        }

        if (_isSelectingRegion)
        {
            _regionEnd = pt;
            Invalidate();
            return;
        }

        if (_isErasing && _eraserTarget != null)
        {
            _eraserCursorPos = pt;
            ApplyErasePoint(pt);
            Invalidate();
            return;
        }

        if (_isDrawing && _drawingObject != null)
        {
            switch (_drawingObject)
            {
                case PenStroke stroke:
                    stroke.Points.Add(pt);
                    break;
                case ShapeObject shape:
                    shape.End = pt;
                    break;
            }
            Invalidate();
            return;
        }

        if (_isMoving && _session.SelectedObject != null)
        {
            float dx = pt.X - _lastDragPos.X;
            float dy = pt.Y - _lastDragPos.Y;
            _session.SelectedObject.Move(dx, dy);
            _totalMoveDx += dx;
            _totalMoveDy += dy;
            _lastDragPos = pt;
            Invalidate();
            return;
        }

        if (_isRotating && _session.SelectedObject != null)
        {
            var center = _session.SelectedObject.GetCenter();
            float prevAngle = MathF.Atan2(_lastDragPos.Y - center.Y, _lastDragPos.X - center.X);
            float currAngle = MathF.Atan2(pt.Y - center.Y, pt.X - center.X);
            float delta = (currAngle - prevAngle) * 180f / MathF.PI;
            _session.SelectedObject.Rotation += delta;
            _totalRotation += delta;
            _lastDragPos = pt;
            Invalidate();
            return;
        }

        if (_isResizing && _session.SelectedObject != null)
        {
            float dx = pt.X - _lastDragPos.X;
            float dy = pt.Y - _lastDragPos.Y;
            _session.SelectedObject.Resize(_resizeHandle, dx, dy);
            _totalMoveDx += dx;
            _totalMoveDy += dy;
            _lastDragPos = pt;
            Invalidate();
            return;
        }

        // Update cursor for select tool
        if (_session.CurrentTool == AnnotationTool.Select)
        {
            UpdateCursor(pt);
        }

        // Update cursor for crop handles
        if (_session.CurrentTool == AnnotationTool.Crop && _isCropAdjusting)
        {
            int hit = HitTestCropCorner(pt);
            Cursor = hit switch
            {
                0 => Cursors.SizeNWSE,  // TL
                1 => Cursors.SizeNESW,  // TR
                2 => Cursors.SizeNWSE,  // BR
                3 => Cursors.SizeNESW,  // BL
                4 => Cursors.SizeNS,    // top mid
                5 => Cursors.SizeWE,    // right mid
                6 => Cursors.SizeNS,    // bottom mid
                7 => Cursors.SizeWE,    // left mid
                _ => PointInQuad(pt, _cropCorners) ? Cursors.SizeAll : Cursors.Cross,
            };
        }

        // Track eraser cursor position for visual feedback
        if (_session.CurrentTool == AnnotationTool.Eraser)
        {
            _eraserCursorPos = pt;
            _eraserCursorVisible = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_session == null || e.Button != MouseButtons.Left) return;

        if (_isCropDragging)
        {
            _isCropDragging = false;
            var cc = GetNormalizedCropCorners();
            float w = Dist(cc[0], cc[1]);
            float h = Dist(cc[0], cc[3]);
            if (w > 10 && h > 10)
            {
                _cropCorners = cc;
                _isCropAdjusting = true;
            }
            _dragCornerIndex = -1;
            Invalidate();
            return;
        }

        if (_dragCornerIndex >= 0)
        {
            _dragCornerIndex = -1;
            Invalidate();
            return;
        }

        if (_isErasing)
        {
            FinishErase();
            return;
        }

        if (_isSelectingRegion)
        {
            _isSelectingRegion = false;
            var rect = GetRegionRect();
            if (rect.Width > 5 && rect.Height > 5)
                _selectedRegion = rect;
            else
                _selectedRegion = null;
            Invalidate();
            return;
        }

        if (_isDrawing && _drawingObject != null)
        {
            FinishDraw();
            return;
        }

        if (_isRotating && _session.SelectedObject != null)
        {
            if (Math.Abs(_totalRotation) > 0.1f)
            {
                _session.SelectedObject.Rotation -= _totalRotation;
                _session.UndoRedo.Execute(new RotateObjectAction(_session.SelectedObject, _totalRotation));
            }
            _isRotating = false;
            Invalidate();
            CanvasChanged?.Invoke();
            return;
        }

        if (_isMoving && _session.SelectedObject != null)
        {
            // Record as undoable move
            if (Math.Abs(_totalMoveDx) > 0.5f || Math.Abs(_totalMoveDy) > 0.5f)
            {
                _session.SelectedObject.Move(-_totalMoveDx, -_totalMoveDy);
                _session.UndoRedo.Execute(new MoveObjectAction(_session.SelectedObject, _totalMoveDx, _totalMoveDy));
            }
            _isMoving = false;
            Invalidate();
            return;
        }

        if (_isResizing && _session.SelectedObject != null)
        {
            // Record as undoable resize
            if (Math.Abs(_totalMoveDx) > 0.5f || Math.Abs(_totalMoveDy) > 0.5f)
            {
                _session.SelectedObject.Resize(_resizeHandle, -_totalMoveDx, -_totalMoveDy);
                _session.UndoRedo.Execute(new ResizeObjectAction(_session.SelectedObject, _resizeHandle, _totalMoveDx, _totalMoveDy));
            }
            _isResizing = false;
            Invalidate();
            return;
        }
    }

    /// <summary>
    /// Ctrl+Wheel zooms toward the cursor: the logical point under the cursor
    /// stays under the cursor after the zoom change. Achieved by adjusting the
    /// container scroll offset by the delta of (logical_pt × new_zoom - logical_pt × old_zoom).
    /// </summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) != Keys.Control)
        {
            base.OnMouseWheel(e);
            return;
        }

        // logical point under cursor BEFORE zoom
        float logicalX = e.X / _zoom;
        float logicalY = e.Y / _zoom;

        float oldZoom = _zoom;
        float factor = e.Delta > 0 ? 1.2f : 1f / 1.2f;
        SetZoom(_zoom * factor);
        if (Math.Abs(_zoom - oldZoom) < 0.0001f) return;

        // After SetZoom, control size has been updated. Tell the container to
        // shift its scroll position so the same logical point lands under the cursor.
        // We expose the desired delta through an event-friendly hook on the container,
        // but the simplest path is: move our Location by (-deltaX, -deltaY) where
        // delta is how much the cursor's logical point now maps to in display px.
        if (Parent is CanvasContainer container)
        {
            float newDispX = logicalX * _zoom;
            float newDispY = logicalY * _zoom;
            container.AdjustScrollForZoom(this, e.X, e.Y, newDispX, newDispY);
        }
    }

    // --- Key handling for text editing ---
    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_editingText != null)
        {
            HandleTextKeyDown(e);
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (_editingText != null && !char.IsControl(e.KeyChar))
        {
            var old = _editingText.Text;
            _editingText.Text += e.KeyChar;
            e.Handled = true;
            Invalidate();
            return;
        }
        base.OnKeyPress(e);
    }

    // --- Private methods ---

    private void ShowCanvasContextMenu(Point location)
    {
        var menu = new ContextMenuStrip();

        if (_session?.SelectedObject != null)
        {
            var bringFwd = new ToolStripMenuItem("Bring Forward");
            bringFwd.Click += (_, _) => { _session.BringForward(); Invalidate(); };
            menu.Items.Add(bringFwd);

            var sendBack = new ToolStripMenuItem("Send Backward");
            sendBack.Click += (_, _) => { _session.SendBackward(); Invalidate(); };
            menu.Items.Add(sendBack);

            menu.Items.Add(new ToolStripSeparator());

            var deleteItem = new ToolStripMenuItem("Delete");
            deleteItem.Click += (_, _) => { _session.DeleteSelected(); Invalidate(); };
            menu.Items.Add(deleteItem);
        }
        else if (_selectedRegion.HasValue)
        {
            var deleteRegion = new ToolStripMenuItem("Delete Region");
            deleteRegion.Click += (_, _) => DeleteSelectedRegion();
            menu.Items.Add(deleteRegion);

            menu.Items.Add(new ToolStripSeparator());

            var cutItem = new ToolStripMenuItem("Cut into Layer");
            cutItem.Click += (_, _) => RegionToLayer(clearOriginal: true);
            menu.Items.Add(cutItem);

            var copyItem = new ToolStripMenuItem("Copy into Layer");
            copyItem.Click += (_, _) => RegionToLayer(clearOriginal: false);
            menu.Items.Add(copyItem);

            menu.Items.Add(new ToolStripSeparator());

            var clearItem = new ToolStripMenuItem("Clear Selection");
            clearItem.Click += (_, _) => { _selectedRegion = null; Invalidate(); };
            menu.Items.Add(clearItem);
        }
        else
        {
            var hint = new ToolStripMenuItem("Drag to select a region first") { Enabled = false };
            menu.Items.Add(hint);
        }

        menu.Show(this, location);
    }

    public bool HasSelectedRegion => _selectedRegion.HasValue;
    public bool IsCropActive => _isCropDragging || _isCropAdjusting;

    public void ClearSelectedRegion()
    {
        if (_selectedRegion == null) return;
        _selectedRegion = null;
        Invalidate();
    }

    public void DeleteSelectedRegion()
    {
        if (_session == null || !_selectedRegion.HasValue) return;

        var rect = _selectedRegion.Value;
        int x = (int)rect.X, y = (int)rect.Y, w = (int)rect.Width, h = (int)rect.Height;
        x = Math.Max(0, x);
        y = Math.Max(0, y);
        w = Math.Min(w, _session.CanvasSize.Width - x);
        h = Math.Min(h, _session.CanvasSize.Height - y);
        if (w <= 0 || h <= 0) return;

        // Snapshot for undo
        var beforeSnapshot = (Bitmap)_session.OriginalImage.Clone();

        // Fill the region on OriginalImage with background color
        var imgOff = _session.ImageOffset;
        int imgX = x - imgOff.X;
        int imgY = y - imgOff.Y;
        int imgW = Math.Min(w, _session.OriginalImage.Width - imgX);
        int imgH = Math.Min(h, _session.OriginalImage.Height - imgY);

        if (imgX >= 0 && imgY >= 0 && imgW > 0 && imgH > 0)
        {
            using (var g = Graphics.FromImage(_session.OriginalImage))
            {
                using var bgBrush = new SolidBrush(_session.CanvasBackgroundColor);
                g.FillRectangle(bgBrush, imgX, imgY, imgW, imgH);
            }

            var affectedRect = new System.Drawing.Rectangle(imgX, imgY, imgW, imgH);
            var action = new BitmapEraseAction(_session.OriginalImage, beforeSnapshot, affectedRect);
            action.CaptureAfterState();
            _session.UndoRedo.Execute(action);
        }
        else
        {
            beforeSnapshot.Dispose();
        }

        _selectedRegion = null;
        Invalidate();
        CanvasChanged?.Invoke();
    }

    public void CropCanvasToRegion()
    {
        if (_session == null || !_selectedRegion.HasValue) return;
        CropCanvasTo(_selectedRegion.Value);
        _selectedRegion = null;
    }

    private void CropCanvasTo(RectangleF rect)
    {
        if (_session == null) return;
        int rx = Math.Max(0, (int)rect.X);
        int ry = Math.Max(0, (int)rect.Y);
        int rw = Math.Min((int)rect.Width, _session.CanvasSize.Width - rx);
        int rh = Math.Min((int)rect.Height, _session.CanvasSize.Height - ry);
        if (rw <= 0 || rh <= 0) return;

        // Crop the OriginalImage to the intersection with the region
        var imgOff = _session.ImageOffset;
        int imgX = rx - imgOff.X;
        int imgY = ry - imgOff.Y;

        // Compute intersection of region with original image
        int srcX = Math.Max(0, imgX);
        int srcY = Math.Max(0, imgY);
        int srcR = Math.Min(_session.OriginalImage.Width, imgX + rw);
        int srcB = Math.Min(_session.OriginalImage.Height, imgY + rh);
        int srcW = Math.Max(0, srcR - srcX);
        int srcH = Math.Max(0, srcB - srcY);

        // Create cropped original image
        var croppedImage = new Bitmap(rw, rh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(croppedImage))
        {
            g.Clear(_session.CanvasBackgroundColor);
            if (srcW > 0 && srcH > 0)
            {
                int destX = srcX - imgX;
                int destY = srcY - imgY;
                g.DrawImage(_session.OriginalImage,
                    new System.Drawing.Rectangle(destX, destY, srcW, srcH),
                    new System.Drawing.Rectangle(srcX, srcY, srcW, srcH),
                    GraphicsUnit.Pixel);
            }
        }

        // Compute shifted annotations for the after-state (don't mutate session directly)
        var shiftedAnnotations = _session.Annotations.Select(a =>
        {
            var clone = a.Clone();
            clone.Move(-rx, -ry);
            return clone;
        }).ToList();

        var corners = new[] {
            new PointF(rx, ry), new PointF(rx + rw, ry),
            new PointF(rx + rw, ry + rh), new PointF(rx, ry + rh)
        };

        // Execute through undo system
        var action = new CropAction(_session, croppedImage, new Size(rw, rh),
            shiftedAnnotations, "rect", corners);
        _session.UndoRedo.Execute(action);

        SetLogicalSize(rw, rh);
        Invalidate();
        CanvasChanged?.Invoke();
    }

    // --- Crop tool methods ---

    private void HandleCropMouseDown(PointF pt)
    {
        if (_session == null) return;

        if (_isCropAdjusting)
        {
            // Check if clicking a corner handle
            int corner = HitTestCropCorner(pt);
            if (corner >= 0)
            {
                _dragCornerIndex = corner;
                _lastDragPos = pt;
                return;
            }
            // Check if inside the quad — move all corners
            if (PointInQuad(pt, _cropCorners))
            {
                _dragCornerIndex = 8; // move all
                _lastDragPos = pt;
                return;
            }
            // Click outside — restart
            _isCropAdjusting = false;
        }

        // Start drag selection
        _cropDragStart = pt;
        _cropCorners[0] = pt; _cropCorners[1] = pt;
        _cropCorners[2] = pt; _cropCorners[3] = pt;
        _isCropDragging = true;
    }

    private void HandleCropMouseMove(PointF pt)
    {
        if (_isCropDragging)
        {
            // Build rectangle from drag, stored as 4 corners
            float x1 = Math.Min(_cropDragStart.X, pt.X);
            float y1 = Math.Min(_cropDragStart.Y, pt.Y);
            float x2 = Math.Max(_cropDragStart.X, pt.X);
            float y2 = Math.Max(_cropDragStart.Y, pt.Y);
            _cropCorners[0] = new PointF(x1, y1); // TL
            _cropCorners[1] = new PointF(x2, y1); // TR
            _cropCorners[2] = new PointF(x2, y2); // BR
            _cropCorners[3] = new PointF(x1, y2); // BL
            return;
        }

        float dx = pt.X - _lastDragPos.X;
        float dy = pt.Y - _lastDragPos.Y;
        bool shift = (ModifierKeys & Keys.Shift) != 0;

        if (_dragCornerIndex >= 0 && _dragCornerIndex <= 3)
        {
            // Move single corner — Shift constrains to dominant axis
            if (shift) { if (Math.Abs(dx) > Math.Abs(dy)) dy = 0; else dx = 0; }
            _cropCorners[_dragCornerIndex] = new PointF(
                _cropCorners[_dragCornerIndex].X + dx,
                _cropCorners[_dragCornerIndex].Y + dy);
        }
        else if (_dragCornerIndex >= 4 && _dragCornerIndex <= 7)
        {
            // Move midpoint = move both adjacent corners (parallel edge shift)
            // 4=top(0,1), 5=right(1,2), 6=bottom(2,3), 7=left(3,0)
            // Shift: top/bottom → vertical only, left/right → horizontal only
            if (shift)
            {
                if (_dragCornerIndex == 4 || _dragCornerIndex == 6) dx = 0; // top/bottom: vertical only
                if (_dragCornerIndex == 5 || _dragCornerIndex == 7) dy = 0; // left/right: horizontal only
            }
            int c1 = _dragCornerIndex - 4;
            int c2 = (_dragCornerIndex - 3) % 4;
            _cropCorners[c1] = new PointF(_cropCorners[c1].X + dx, _cropCorners[c1].Y + dy);
            _cropCorners[c2] = new PointF(_cropCorners[c2].X + dx, _cropCorners[c2].Y + dy);
        }
        else if (_dragCornerIndex == 8)
        {
            // Move all corners
            for (int i = 0; i < 4; i++)
                _cropCorners[i] = new PointF(_cropCorners[i].X + dx, _cropCorners[i].Y + dy);
        }

        _lastDragPos = pt;
    }

    public void ApplyCrop()
    {
        if (!_isCropAdjusting || _session == null) return;
        _isCropAdjusting = false;
        _isCropDragging = false;
        _dragCornerIndex = -1;
        ApplyPerspectiveCrop();
    }

    public void CancelCrop()
    {
        _isCropDragging = false;
        _isCropAdjusting = false;
        _dragCornerIndex = -1;
        Invalidate();
    }

    private int HitTestCropCorner(PointF pt, float tolerance = 14f)
    {
        // 0-3: corners (TL, TR, BR, BL)
        for (int i = 0; i < 4; i++)
        {
            if (Math.Abs(pt.X - _cropCorners[i].X) < tolerance &&
                Math.Abs(pt.Y - _cropCorners[i].Y) < tolerance)
                return i;
        }
        // 4-7: midpoints (top, right, bottom, left)
        var midpoints = new[] {
            Lerp(_cropCorners[0], _cropCorners[1], 0.5f),
            Lerp(_cropCorners[1], _cropCorners[2], 0.5f),
            Lerp(_cropCorners[2], _cropCorners[3], 0.5f),
            Lerp(_cropCorners[3], _cropCorners[0], 0.5f),
        };
        for (int i = 0; i < 4; i++)
        {
            if (Math.Abs(pt.X - midpoints[i].X) < tolerance &&
                Math.Abs(pt.Y - midpoints[i].Y) < tolerance)
                return i + 4;
        }
        return -1;
    }

    private static bool PointInQuad(PointF p, PointF[] quad)
    {
        // Cross product sign test for convex quad
        bool SameSide(PointF a, PointF b, PointF c, PointF pt)
        {
            float cross1 = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            float cross2 = (b.X - a.X) * (pt.Y - a.Y) - (b.Y - a.Y) * (pt.X - a.X);
            return cross1 * cross2 >= 0;
        }
        // Check if point is inside triangle TL-TR-BR or TL-BR-BL
        bool InTri(PointF a, PointF b, PointF c, PointF pt)
        {
            return SameSide(a, b, c, pt) && SameSide(b, c, a, pt) && SameSide(c, a, b, pt);
        }
        return InTri(quad[0], quad[1], quad[2], p) || InTri(quad[0], quad[2], quad[3], p);
    }

    private PointF[] GetNormalizedCropCorners()
    {
        if (_isCropDragging)
        {
            float x1 = Math.Min(_cropDragStart.X, _cropCorners[2].X);
            float y1 = Math.Min(_cropDragStart.Y, _cropCorners[2].Y);
            float x2 = Math.Max(_cropDragStart.X, _cropCorners[2].X);
            float y2 = Math.Max(_cropDragStart.Y, _cropCorners[2].Y);
            return new[] { new PointF(x1, y1), new PointF(x2, y1), new PointF(x2, y2), new PointF(x1, y2) };
        }
        return (PointF[])_cropCorners.Clone();
    }

    private void ApplyPerspectiveCrop()
    {
        if (_session == null) return;

        // Compute output dimensions from the quad
        float outW = Math.Max(Dist(_cropCorners[0], _cropCorners[1]), Dist(_cropCorners[3], _cropCorners[2]));
        float outH = Math.Max(Dist(_cropCorners[0], _cropCorners[3]), Dist(_cropCorners[1], _cropCorners[2]));
        int w = Math.Max(1, (int)Math.Round(outW));
        int h = Math.Max(1, (int)Math.Round(outH));

        // Build source image: canvas bg + OriginalImage at offset (no annotations)
        var source = new Bitmap(_session.CanvasSize.Width, _session.CanvasSize.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(source))
        {
            using var bgBrush = new SolidBrush(_session.CanvasBackgroundColor);
            g.FillRectangle(bgBrush, 0, 0, source.Width, source.Height);
            g.DrawImage(_session.OriginalImage, _session.ImageOffset.X, _session.ImageOffset.Y);
        }

        // Perspective warp only the base image
        var result = PerspectiveWarp(source, _cropCorners, w, h);
        source.Dispose();

        // Compute bounding box of quad for annotation offset
        float minX = Math.Min(Math.Min(_cropCorners[0].X, _cropCorners[1].X),
                              Math.Min(_cropCorners[2].X, _cropCorners[3].X));
        float minY = Math.Min(Math.Min(_cropCorners[0].Y, _cropCorners[1].Y),
                              Math.Min(_cropCorners[2].Y, _cropCorners[3].Y));

        // Shift annotations by crop offset (preserve, don't bake)
        var shiftedAnnotations = _session.Annotations.Select(a =>
        {
            var clone = a.Clone();
            clone.Move(-minX, -minY);
            return clone;
        }).ToList();

        // Execute through undo system
        var action = new CropAction(_session, result, new Size(w, h),
            shiftedAnnotations, "perspective", (PointF[])_cropCorners.Clone());
        _session.UndoRedo.Execute(action);

        SetLogicalSize(w, h);
        Invalidate();
        CanvasChanged?.Invoke();
    }

    private static unsafe Bitmap PerspectiveWarp(Bitmap source, PointF[] srcQuad, int outW, int outH)
    {
        var result = new Bitmap(outW, outH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var srcData = source.LockBits(
            new System.Drawing.Rectangle(0, 0, source.Width, source.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var dstData = result.LockBits(
            new System.Drawing.Rectangle(0, 0, outW, outH),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        int srcStride = srcData.Stride;
        int dstStride = dstData.Stride;
        byte* srcPtr = (byte*)srcData.Scan0;
        byte* dstPtr = (byte*)dstData.Scan0;

        // TL=0, TR=1, BR=2, BL=3
        float tlx = srcQuad[0].X, tly = srcQuad[0].Y;
        float trx = srcQuad[1].X, try_ = srcQuad[1].Y;
        float brx = srcQuad[2].X, bry = srcQuad[2].Y;
        float blx = srcQuad[3].X, bly = srcQuad[3].Y;

        int srcW = source.Width, srcH = source.Height;
        int srcWm1 = srcW - 1, srcHm1 = srcH - 1;
        float invH = outH > 1 ? 1f / (outH - 1) : 0;
        float invW = outW > 1 ? 1f / (outW - 1) : 0;

        for (int dy = 0; dy < outH; dy++)
        {
            float v = dy * invH;
            float leftX = tlx + (blx - tlx) * v;
            float leftY = tly + (bly - tly) * v;
            float rightX = trx + (brx - trx) * v;
            float rightY = try_ + (bry - try_) * v;

            byte* dstRow = dstPtr + dy * dstStride;

            for (int dx = 0; dx < outW; dx++)
            {
                float u = dx * invW;
                float sx = leftX + (rightX - leftX) * u;
                float sy = leftY + (rightY - leftY) * u;

                // Bilinear interpolation: blend 4 surrounding pixels
                int x0 = (int)MathF.Floor(sx);
                int y0 = (int)MathF.Floor(sy);

                if (x0 < -1 || x0 >= srcW || y0 < -1 || y0 >= srcH)
                    continue;

                float fx = sx - x0;
                float fy = sy - y0;
                float fx1 = 1f - fx;
                float fy1 = 1f - fy;

                // Clamp to valid pixel range
                int x1 = Math.Min(x0 + 1, srcWm1);
                int y1 = Math.Min(y0 + 1, srcHm1);
                x0 = Math.Max(x0, 0);
                y0 = Math.Max(y0, 0);

                byte* p00 = srcPtr + y0 * srcStride + x0 * 4;
                byte* p10 = srcPtr + y0 * srcStride + x1 * 4;
                byte* p01 = srcPtr + y1 * srcStride + x0 * 4;
                byte* p11 = srcPtr + y1 * srcStride + x1 * 4;

                // Blend weights
                float w00 = fx1 * fy1;
                float w10 = fx * fy1;
                float w01 = fx1 * fy;
                float w11 = fx * fy;

                byte* dstPixel = dstRow + dx * 4;
                dstPixel[0] = (byte)(p00[0] * w00 + p10[0] * w10 + p01[0] * w01 + p11[0] * w11 + 0.5f); // B
                dstPixel[1] = (byte)(p00[1] * w00 + p10[1] * w10 + p01[1] * w01 + p11[1] * w11 + 0.5f); // G
                dstPixel[2] = (byte)(p00[2] * w00 + p10[2] * w10 + p01[2] * w01 + p11[2] * w11 + 0.5f); // R
                dstPixel[3] = (byte)(p00[3] * w00 + p10[3] * w10 + p01[3] * w01 + p11[3] * w11 + 0.5f); // A
            }
        }

        source.UnlockBits(srcData);
        result.UnlockBits(dstData);
        return result;
    }

    private static PointF Lerp(PointF a, PointF b, float t)
        => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    private static float Dist(PointF a, PointF b)
        => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));



    private void RegionToLayer(bool clearOriginal)
    {
        if (_session == null || !_selectedRegion.HasValue) return;

        var rect = _selectedRegion.Value;
        int x = (int)rect.X, y = (int)rect.Y, w = (int)rect.Width, h = (int)rect.Height;

        x = Math.Max(0, x);
        y = Math.Max(0, y);
        w = Math.Min(w, _session.CanvasSize.Width - x);
        h = Math.Min(h, _session.CanvasSize.Height - y);
        if (w <= 0 || h <= 0) return;

        // Render the region content: bg + base image in that area
        var regionBmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(regionBmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TranslateTransform(-x, -y);

            using (var bgBrush = new SolidBrush(_session.CanvasBackgroundColor))
                g.FillRectangle(bgBrush, 0, 0, _session.CanvasSize.Width, _session.CanvasSize.Height);

            var imgOff = _session.ImageOffset;
            g.DrawImage(_session.OriginalImage, imgOff.X, imgOff.Y);
        }

        // Cut mode: fill the original area with background color
        if (clearOriginal)
        {
            using (var g = Graphics.FromImage(_session.OriginalImage))
            {
                var imgOff = _session.ImageOffset;
                int imgX = x - imgOff.X;
                int imgY = y - imgOff.Y;
                int imgW = Math.Min(w, _session.OriginalImage.Width - imgX);
                int imgH = Math.Min(h, _session.OriginalImage.Height - imgY);

                if (imgX >= 0 && imgY >= 0 && imgW > 0 && imgH > 0)
                {
                    using var bgBrush = new SolidBrush(_session.CanvasBackgroundColor);
                    g.FillRectangle(bgBrush, imgX, imgY, imgW, imgH);
                }
            }
        }

        // Save to _edits folder
        string localPath = "";
        if (!string.IsNullOrEmpty(_session.EditId))
        {
            string dir = _session.EditDir;
            Directory.CreateDirectory(dir);

            if (clearOriginal)
                _session.OriginalImage.Save(Path.Combine(dir, "base.png"), System.Drawing.Imaging.ImageFormat.Png);

            localPath = Path.Combine(dir, $"layer_{DateTime.Now:HHmmss}_{x}_{y}.png");
            regionBmp.Save(localPath, System.Drawing.Imaging.ImageFormat.Png);
        }

        var imageObj = new ImageObject
        {
            Image = regionBmp,
            Position = new PointF(x, y),
            DisplaySize = new SizeF(w, h),
            SourcePath = localPath,
        };

        _session.AddAnnotation(imageObj);
        _selectedRegion = null;
        Invalidate();
        CanvasChanged?.Invoke();
    }

    private void HandleSelectMouseDown(PointF pt)
    {
        // Clear any existing region selection
        _selectedRegion = null;

        // Check if clicking a handle of selected object
        if (_session!.SelectedObject != null)
        {
            var handle = _session.SelectedObject.HitTestHandle(pt);
            if (handle == HandlePosition.Rotate)
            {
                _isRotating = true;
                _lastDragPos = pt;
                _totalRotation = 0;
                return;
            }
            if (handle != HandlePosition.None)
            {
                _isResizing = true;
                _resizeHandle = handle;
                _lastDragPos = pt;
                _totalMoveDx = 0;
                _totalMoveDy = 0;
                return;
            }
        }

        _session.SelectAt(pt);
        SelectionChanged?.Invoke();

        if (_session.SelectedObject != null)
        {
            _isMoving = true;
            _lastDragPos = pt;
            _totalMoveDx = 0;
            _totalMoveDy = 0;
        }
        else
        {
            // No object hit — start region selection
            _isSelectingRegion = true;
            _regionStart = pt;
            _regionEnd = pt;
        }

        Invalidate();
    }

    private void StartPenDraw(PointF pt, bool isMarker)
    {
        PenStroke stroke = isMarker
            ? new MarkerStroke { StrokeColor = _session!.CurrentColor }
            : new PenStroke { StrokeColor = _session!.CurrentColor, Thickness = _session.CurrentThickness };

        if (isMarker)
        {
            stroke.Thickness = Math.Max(_session.CurrentThickness * 4, 16f);
        }

        stroke.Points.Add(pt);
        _drawingObject = stroke;
        _isDrawing = true;
    }

    private void StartShapeDraw(PointF pt)
    {
        ShapeObject shape = _session!.CurrentTool switch
        {
            AnnotationTool.Line => new LineObject(),
            AnnotationTool.Arrow => new ArrowObject(),
            AnnotationTool.Rectangle => new RectangleObject(),
            AnnotationTool.Ellipse => new EllipseObject(),
            AnnotationTool.MaskBox => new MaskBoxObject(),
            _ => new LineObject(),
        };
        shape.Start = pt;
        shape.End = pt;
        shape.StrokeColor = _session.CurrentColor;
        shape.Thickness = _session.CurrentThickness;
        _drawingObject = shape;
        _isDrawing = true;
    }

    private void FinishDraw()
    {
        _isDrawing = false;
        if (_drawingObject == null || _session == null) return;

        // Validate minimum size
        bool valid = _drawingObject switch
        {
            PenStroke ps => ps.Points.Count >= 2,
            ShapeObject so => Math.Abs(so.End.X - so.Start.X) > 2 || Math.Abs(so.End.Y - so.Start.Y) > 2,
            _ => true,
        };

        if (valid)
        {
            _session.AddAnnotation(_drawingObject);
            CanvasChanged?.Invoke();

            // First draw after opening: switch to Select tool
            if (!_hasDrawnOnce)
            {
                _hasDrawnOnce = true;
                _session.CurrentTool = AnnotationTool.Select;
                _session.DeselectAll();
                UpdateToolCursor();
                ToolAutoSwitched?.Invoke();
            }
        }

        _drawingObject = null;
        Invalidate();
    }

    // --- Eraser methods ---

    private void StartErase(PointF canvasPt)
    {
        if (_session == null) return;

        _eraserTarget = null;
        _eraserPoints.Clear();

        // Hit-test annotations top-down for ImageObject
        for (int i = _session.Annotations.Count - 1; i >= 0; i--)
        {
            if (_session.Annotations[i] is ImageObject imgObj)
            {
                var bounds = new RectangleF(imgObj.Position, imgObj.DisplaySize);
                if (bounds.Contains(canvasPt))
                {
                    _eraserTarget = imgObj.Image;
                    _eraserTargetOffset = imgObj.Position;
                    _eraserTargetScale = new SizeF(
                        imgObj.Image.Width / imgObj.DisplaySize.Width,
                        imgObj.Image.Height / imgObj.DisplaySize.Height);
                    break;
                }
            }
        }

        // If no ImageObject hit, check OriginalImage bounds
        if (_eraserTarget == null)
        {
            var imgOff = _session.ImageOffset;
            var imgBounds = new RectangleF(imgOff.X, imgOff.Y,
                _session.OriginalImage.Width, _session.OriginalImage.Height);
            if (imgBounds.Contains(canvasPt))
            {
                _eraserTarget = _session.OriginalImage;
                _eraserTargetOffset = new PointF(imgOff.X, imgOff.Y);
                _eraserTargetScale = new SizeF(1f, 1f);
            }
        }

        if (_eraserTarget == null) return;

        _eraserBeforeSnapshot = (Bitmap)_eraserTarget.Clone();
        _isErasing = true;
        _eraserPoints.Add(canvasPt);
        _lastEraserPoint = canvasPt;
        EraseAtCanvasPoint(canvasPt);
        Invalidate();
    }

    private void ApplyErasePoint(PointF canvasPt)
    {
        _eraserPoints.Add(canvasPt);

        float eraserRadius = _session!.CurrentThickness * 2;
        float step = Math.Max(1f, eraserRadius / 3f);
        float dx = canvasPt.X - _lastEraserPoint.X;
        float dy = canvasPt.Y - _lastEraserPoint.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);

        if (length > step)
        {
            int steps = (int)(length / step);
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                EraseAtCanvasPoint(new PointF(
                    _lastEraserPoint.X + dx * t,
                    _lastEraserPoint.Y + dy * t));
            }
        }
        else
        {
            EraseAtCanvasPoint(canvasPt);
        }

        _lastEraserPoint = canvasPt;
    }

    private void EraseAtCanvasPoint(PointF canvasPt)
    {
        if (_eraserTarget == null || _session == null) return;

        float bmpX = (canvasPt.X - _eraserTargetOffset.X) * _eraserTargetScale.Width;
        float bmpY = (canvasPt.Y - _eraserTargetOffset.Y) * _eraserTargetScale.Height;
        float bmpRadius = _session.CurrentThickness * 2 * Math.Max(_eraserTargetScale.Width, _eraserTargetScale.Height);

        using var g = Graphics.FromImage(_eraserTarget);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Color.FromArgb(0, 0, 0, 0));
        g.FillEllipse(brush, bmpX - bmpRadius, bmpY - bmpRadius, bmpRadius * 2, bmpRadius * 2);
    }

    private void FinishErase()
    {
        _isErasing = false;
        if (_eraserTarget == null || _eraserPoints.Count == 0 || _session == null || _eraserBeforeSnapshot == null)
        {
            _eraserTarget = null;
            _eraserBeforeSnapshot?.Dispose();
            _eraserBeforeSnapshot = null;
            return;
        }

        // Compute bounding box in bitmap coordinates
        float eraserRadius = _session.CurrentThickness * 2;
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var pt in _eraserPoints)
        {
            float bmpX = (pt.X - _eraserTargetOffset.X) * _eraserTargetScale.Width;
            float bmpY = (pt.Y - _eraserTargetOffset.Y) * _eraserTargetScale.Height;
            float bmpR = eraserRadius * Math.Max(_eraserTargetScale.Width, _eraserTargetScale.Height);
            minX = Math.Min(minX, bmpX - bmpR);
            minY = Math.Min(minY, bmpY - bmpR);
            maxX = Math.Max(maxX, bmpX + bmpR);
            maxY = Math.Max(maxY, bmpY + bmpR);
        }

        int rx = Math.Max(0, (int)Math.Floor(minX) - 1);
        int ry = Math.Max(0, (int)Math.Floor(minY) - 1);
        int rr = Math.Min(_eraserTarget.Width, (int)Math.Ceiling(maxX) + 1);
        int rb = Math.Min(_eraserTarget.Height, (int)Math.Ceiling(maxY) + 1);
        var affectedRect = new System.Drawing.Rectangle(rx, ry, Math.Max(1, rr - rx), Math.Max(1, rb - ry));

        var action = new BitmapEraseAction(_eraserTarget, _eraserBeforeSnapshot, affectedRect);
        action.CaptureAfterState();
        _session.UndoRedo.Execute(action);

        _eraserBeforeSnapshot = null; // ownership transferred
        _eraserTarget = null;
        _eraserPoints.Clear();
        Invalidate();
        CanvasChanged?.Invoke();
    }

    private void HandleTextClick(PointF pt)
    {
        if (_session == null) return;

        float tol = 6f / _zoom;
        // Check if clicking an existing text object
        for (int i = _session.Annotations.Count - 1; i >= 0; i--)
        {
            if (_session.Annotations[i] is TextObject existing && existing.HitTest(pt, tol))
            {
                StartTextEdit(existing);
                return;
            }
        }

        // Create new text object
        var txt = new TextObject
        {
            Position = pt,
            StrokeColor = _session.CurrentColor,
            FontSize = _session.CurrentFontSize,
        };
        _session.AddAnnotation(txt);
        StartTextEdit(txt);
    }

    private void StartTextEdit(TextObject txt)
    {
        if (_editingText != null) CommitTextEdit();
        _editingText = txt;
        _editingText.IsEditing = true;
        _session!.SelectedObject = txt;
        txt.IsSelected = true;
        Cursor = Cursors.IBeam;
        Invalidate();
    }

    public void CommitTextEdit()
    {
        if (_editingText == null) return;

        // Remove empty text objects
        if (string.IsNullOrWhiteSpace(_editingText.Text))
        {
            _session?.Annotations.Remove(_editingText);
        }

        _editingText.IsEditing = false;
        _editingText = null;

        if (_session?.CurrentTool != AnnotationTool.Text)
            Cursor = Cursors.Cross;
        else
            Cursor = Cursors.IBeam;

        Invalidate();
        CanvasChanged?.Invoke();
    }

    private void HandleTextKeyDown(KeyEventArgs e)
    {
        if (_editingText == null) return;

        switch (e.KeyCode)
        {
            case Keys.Back:
                if (_editingText.Text.Length > 0)
                    _editingText.Text = _editingText.Text[..^1];
                e.Handled = true;
                break;
            case Keys.Enter:
                _editingText.Text += "\n";
                e.Handled = true;
                break;
            case Keys.Escape:
                CommitTextEdit();
                e.Handled = true;
                break;
        }
        Invalidate();
    }

    private void UpdateCursor(PointF pt)
    {
        float tol = 6f / _zoom;
        if (_session?.SelectedObject != null)
        {
            var handle = _session.SelectedObject.HitTestHandle(pt);
            Cursor = handle switch
            {
                HandlePosition.TopLeft or HandlePosition.BottomRight => Cursors.SizeNWSE,
                HandlePosition.TopRight or HandlePosition.BottomLeft => Cursors.SizeNESW,
                HandlePosition.TopCenter or HandlePosition.BottomCenter => Cursors.SizeNS,
                HandlePosition.MiddleLeft or HandlePosition.MiddleRight => Cursors.SizeWE,
                _ => _session.SelectedObject.HitTest(pt, tol) ? Cursors.SizeAll : Cursors.Default,
            };
        }
        else
        {
            // Check if hovering over any object
            bool overObj = false;
            for (int i = _session!.Annotations.Count - 1; i >= 0; i--)
            {
                if (_session.Annotations[i].HitTest(pt, tol))
                {
                    overObj = true;
                    break;
                }
            }
            Cursor = overObj ? Cursors.Hand : Cursors.Default;
        }
    }

    public void UpdateToolCursor()
    {
        if (_editingText != null) return;
        if (_session?.CurrentTool == AnnotationTool.Eraser)
        {
            Cursor = new Cursor(new MemoryStream(CreateBlankCursorData()));
            _eraserCursorVisible = ClientRectangle.Contains(PointToClient(Control.MousePosition));
            Invalidate();
            return;
        }
        _eraserCursorVisible = false;
        Cursor = _session?.CurrentTool switch
        {
            AnnotationTool.Select => Cursors.Default,
            AnnotationTool.Text => Cursors.IBeam,
            _ => Cursors.Cross,
        };
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (_session?.CurrentTool == AnnotationTool.Eraser)
        {
            _eraserCursorVisible = true;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_eraserCursorVisible)
        {
            _eraserCursorVisible = false;
            Invalidate();
        }
    }

    private static byte[] CreateBlankCursorData()
    {
        // Minimal .cur format: 1x1 transparent cursor
        using var bmp = new Bitmap(1, 1, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        bmp.SetPixel(0, 0, Color.FromArgb(0, 0, 0, 0));
        var ms = new MemoryStream();
        var icon = System.Drawing.Icon.FromHandle(bmp.GetHicon());
        icon.Save(ms);
        ms.Position = 0;
        // Patch the icon header to be a cursor (type 2 instead of 1), hotspot at 0,0
        var data = ms.ToArray();
        data[2] = 2; // idType = cursor
        return data;
    }
}
