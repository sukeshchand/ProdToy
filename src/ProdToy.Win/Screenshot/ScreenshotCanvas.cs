using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace ProdToy;

class ScreenshotCanvas : Control
{
    private EditorSession? _session;

    // Drawing state
    private bool _isDrawing;
    private AnnotationObject? _drawingObject;
    private PointF _lastDragPos;

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

    // Crop state
    private bool _isCropDragging;   // Phase 1: user is dragging to define the crop area
    private bool _isCropAdjusting;  // Phase 2: crop area defined, user can adjust handles
    private RectangleF _cropRect;
    private PointF _cropDragStart;
    private HandlePosition _cropHandle;
    private bool _isDraggingCropHandle;

    public event Action? CanvasChanged;
    public event Action? SelectionChanged;

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
            var clientPt = PointToClient(new Point(e.X, e.Y));
            float dropX = Math.Max(0, clientPt.X);
            float dropY = Math.Max(0, clientPt.Y);

            // Check if canvas needs to expand to fit the dropped image
            float neededW = dropX + img.Width;
            float neededH = dropY + img.Height;
            if (neededW > _session.CanvasSize.Width || neededH > _session.CanvasSize.Height)
            {
                var newSize = new Size(
                    Math.Max(_session.CanvasSize.Width, (int)neededW),
                    Math.Max(_session.CanvasSize.Height, (int)neededH));
                _session.CanvasSize = newSize;
                Size = new Size(newSize.Width, newSize.Height);
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
            System.Diagnostics.Debug.WriteLine($"Drop failed: {ex.Message}");
        }
    }

    public EditorSession? Session
    {
        get => _session;
        set
        {
            _session = value;
            Invalidate();
        }
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

        // Fill with canvas background color
        using (var bgBrush = new SolidBrush(_session.CanvasBackgroundColor))
            g.FillRectangle(bgBrush, ClientRectangle);

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

        // Draw crop overlay
        if (_isCropDragging || _isCropAdjusting)
        {
            // Normalize rect during drag (start/end may be inverted)
            var cr = _isCropDragging
                ? new RectangleF(
                    Math.Min(_cropDragStart.X, _cropRect.Right),
                    Math.Min(_cropDragStart.Y, _cropRect.Bottom),
                    Math.Abs(_cropRect.Width), Math.Abs(_cropRect.Height))
                : _cropRect;
            if (cr.Width >= 2 && cr.Height >= 2)
            {
            // Dim area outside crop
            using var dimBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
            g.FillRectangle(dimBrush, 0, 0, ClientSize.Width, cr.Y);
            g.FillRectangle(dimBrush, 0, cr.Bottom, ClientSize.Width, ClientSize.Height - cr.Bottom);
            g.FillRectangle(dimBrush, 0, cr.Y, cr.X, cr.Height);
            g.FillRectangle(dimBrush, cr.Right, cr.Y, ClientSize.Width - cr.Right, cr.Height);

            // Crop border — red
            var cropColor = Color.FromArgb(220, 220, 50, 50);
            using var cropPen = new Pen(cropColor, 2f);
            g.DrawRectangle(cropPen, cr.X, cr.Y, cr.Width, cr.Height);

            // Rule of thirds grid
            using var gridPen = new Pen(Color.FromArgb(50, 220, 50, 50), 0.5f);
            for (int i = 1; i <= 2; i++)
            {
                float gx = cr.X + cr.Width * i / 3f;
                float gy = cr.Y + cr.Height * i / 3f;
                g.DrawLine(gridPen, gx, cr.Y, gx, cr.Bottom);
                g.DrawLine(gridPen, cr.X, gy, cr.Right, gy);
            }

            // 8 small box handles — red filled with white border
            float hs = 8f, hh = hs / 2;
            float mx = cr.X + cr.Width / 2, my = cr.Y + cr.Height / 2;
            using var handleFill = new SolidBrush(cropColor);
            using var handleBorder = new Pen(Color.White, 1f);
            var handles = new[] {
                new RectangleF(cr.Left - hh, cr.Top - hh, hs, hs),
                new RectangleF(mx - hh, cr.Top - hh, hs, hs),
                new RectangleF(cr.Right - hh, cr.Top - hh, hs, hs),
                new RectangleF(cr.Left - hh, my - hh, hs, hs),
                new RectangleF(cr.Right - hh, my - hh, hs, hs),
                new RectangleF(cr.Left - hh, cr.Bottom - hh, hs, hs),
                new RectangleF(mx - hh, cr.Bottom - hh, hs, hs),
                new RectangleF(cr.Right - hh, cr.Bottom - hh, hs, hs),
            };
            foreach (var h in handles)
            {
                g.FillRectangle(handleFill, h);
                g.DrawRectangle(handleBorder, h.X, h.Y, h.Width, h.Height);
            }

            // Size label
            using var labelFont = new Font("Segoe UI", 8f);
            string sizeText = $"{(int)cr.Width} x {(int)cr.Height}";
            var textSize = g.MeasureString(sizeText, labelFont);
            float lx = cr.Right - textSize.Width - 4;
            float ly = cr.Bottom + 4;
            using var labelBg = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
            g.FillRectangle(labelBg, lx - 2, ly, textSize.Width + 4, textSize.Height);
            using var labelBrush = new SolidBrush(Color.White);
            g.DrawString(sizeText, labelFont, labelBrush, lx, ly);

            // Hint text (only in adjust phase)
            if (_isCropAdjusting)
            {
                string hint = "Double-click or Enter to crop \u2022 Esc to cancel";
                var hintSize = g.MeasureString(hint, labelFont);
                float hx = cr.X + (cr.Width - hintSize.Width) / 2;
                float hy = cr.Y - hintSize.Height - 6;
                if (hy < 0) hy = cr.Bottom + 4 + textSize.Height + 4;
                g.FillRectangle(labelBg, hx - 2, hy, hintSize.Width + 4, hintSize.Height);
                g.DrawString(hint, labelFont, labelBrush, hx, hy);
            }
            } // end cr.Width >= 2
        }

        // Draw region selection rectangle
        if (_isSelectingRegion || _selectedRegion.HasValue)
        {
            var rect = _selectedRegion ?? GetRegionRect();
            if (rect.Width > 2 && rect.Height > 2)
            {
                // Semi-transparent overlay outside selection
                using var dimBrush = new SolidBrush(Color.FromArgb(60, 0, 0, 0));
                g.FillRectangle(dimBrush, 0, 0, ClientSize.Width, rect.Y);
                g.FillRectangle(dimBrush, 0, rect.Bottom, ClientSize.Width, ClientSize.Height - rect.Bottom);
                g.FillRectangle(dimBrush, 0, rect.Y, rect.X, rect.Height);
                g.FillRectangle(dimBrush, rect.Right, rect.Y, ClientSize.Width - rect.Right, rect.Height);

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
        var rect = new RectangleF(half, half, ClientSize.Width - t, ClientSize.Height - t);

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
                g.DrawRectangle(pen, half, half, ClientSize.Width - t, ClientSize.Height - t);
                g.DrawRectangle(pen, half + gap, half + gap, ClientSize.Width - t - gap * 2, ClientSize.Height - t - gap * 2);
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

        // Right-click context menu
        if (e.Button == MouseButtons.Right)
        {
            ShowCanvasContextMenu(e.Location);
            return;
        }

        if (e.Button != MouseButtons.Left) return;
        Focus();
        var pt = new PointF(e.X, e.Y);

        // If editing text, commit on click outside
        if (_editingText != null)
        {
            if (!_editingText.HitTest(pt, 6f))
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
        var pt = new PointF(e.X, e.Y);

        // Double-click on a TextObject in Select mode → re-enter text editing
        if (_session.CurrentTool == AnnotationTool.Select)
        {
            for (int i = _session.Annotations.Count - 1; i >= 0; i--)
            {
                if (_session.Annotations[i] is TextObject txt && txt.HitTest(pt, 6f))
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
        var pt = new PointF(e.X, e.Y);

        if (_isCropDragging || _isDraggingCropHandle)
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
            // Normalize the rect and transition to adjust phase if big enough
            float x = Math.Min(_cropDragStart.X, _cropRect.Right);
            float y = Math.Min(_cropDragStart.Y, _cropRect.Bottom);
            float w = Math.Abs(_cropRect.Width);
            float h = Math.Abs(_cropRect.Height);
            if (w > 10 && h > 10)
            {
                _cropRect = new RectangleF(x, y, w, h);
                _isCropAdjusting = true;
            }
            Invalidate();
            return;
        }

        if (_isDraggingCropHandle)
        {
            _isDraggingCropHandle = false;
            _cropHandle = HandlePosition.None;
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

        // Snapshot for undo
        var beforeImage = _session.OriginalImage;
        var oldSize = _session.CanvasSize;
        var oldOffset = _session.ImageOffset;

        // Apply crop: replace original image, reset offset, update canvas size
        _session.OriginalImage = croppedImage;
        _session.CanvasSize = new Size(rw, rh);
        _session.ImageOffset = Point.Empty;

        // Shift all annotations by the crop offset
        foreach (var obj in _session.Annotations)
            obj.Move(-rx, -ry);

        Size = new Size(rw, rh);
        Invalidate();
        CanvasChanged?.Invoke();
    }

    // --- Crop tool methods ---

    private void HandleCropMouseDown(PointF pt)
    {
        if (_session == null) return;

        // Phase 2: adjusting — check handles or move
        if (_isCropAdjusting)
        {
            _cropHandle = HitTestCropHandle(pt);
            if (_cropHandle != HandlePosition.None)
            {
                _isDraggingCropHandle = true;
                _lastDragPos = pt;
                return;
            }
            if (_cropRect.Contains(pt))
            {
                _cropHandle = HandlePosition.None;
                _isDraggingCropHandle = true;
                _lastDragPos = pt;
                return;
            }
            // Click outside crop rect — restart drag selection
            _isCropAdjusting = false;
        }

        // Phase 1: start drag selection
        _cropDragStart = pt;
        _cropRect = new RectangleF(pt.X, pt.Y, 0, 0);
        _isCropDragging = true;
    }

    private void HandleCropMouseMove(PointF pt)
    {
        if (_isCropDragging)
        {
            // Update crop rect from drag start to current point
            _cropRect = new RectangleF(
                Math.Min(_cropDragStart.X, pt.X),
                Math.Min(_cropDragStart.Y, pt.Y),
                Math.Abs(pt.X - _cropDragStart.X),
                Math.Abs(pt.Y - _cropDragStart.Y));
            return;
        }

        // Phase 2: adjusting handles
        float dx = pt.X - _lastDragPos.X;
        float dy = pt.Y - _lastDragPos.Y;
        var cr = _cropRect;

        if (_cropHandle == HandlePosition.None)
        {
            cr.Offset(dx, dy);
        }
        else
        {
            switch (_cropHandle)
            {
                case HandlePosition.TopLeft:
                    cr = RectangleF.FromLTRB(cr.Left + dx, cr.Top + dy, cr.Right, cr.Bottom); break;
                case HandlePosition.TopCenter:
                    cr = RectangleF.FromLTRB(cr.Left, cr.Top + dy, cr.Right, cr.Bottom); break;
                case HandlePosition.TopRight:
                    cr = RectangleF.FromLTRB(cr.Left, cr.Top + dy, cr.Right + dx, cr.Bottom); break;
                case HandlePosition.MiddleLeft:
                    cr = RectangleF.FromLTRB(cr.Left + dx, cr.Top, cr.Right, cr.Bottom); break;
                case HandlePosition.MiddleRight:
                    cr = RectangleF.FromLTRB(cr.Left, cr.Top, cr.Right + dx, cr.Bottom); break;
                case HandlePosition.BottomLeft:
                    cr = RectangleF.FromLTRB(cr.Left + dx, cr.Top, cr.Right, cr.Bottom + dy); break;
                case HandlePosition.BottomCenter:
                    cr = RectangleF.FromLTRB(cr.Left, cr.Top, cr.Right, cr.Bottom + dy); break;
                case HandlePosition.BottomRight:
                    cr = RectangleF.FromLTRB(cr.Left, cr.Top, cr.Right + dx, cr.Bottom + dy); break;
            }
        }

        if (cr.Width >= 10 && cr.Height >= 10)
            _cropRect = cr;
        _lastDragPos = pt;
    }

    public void ApplyCrop()
    {
        if (!_isCropAdjusting || _session == null) return;
        _isCropAdjusting = false;
        _isCropDragging = false;
        CropCanvasTo(_cropRect);
    }

    public void CancelCrop()
    {
        _isCropDragging = false;
        _isCropAdjusting = false;
        _isDraggingCropHandle = false;
        Invalidate();
    }

    private HandlePosition HitTestCropHandle(PointF pt)
    {
        var cr = _cropRect;
        float hs = 8f, tol = 6f;
        float mx = cr.X + cr.Width / 2, my = cr.Y + cr.Height / 2;
        var positions = new[] {
            (HandlePosition.TopLeft, cr.Left, cr.Top),
            (HandlePosition.TopCenter, mx, cr.Top),
            (HandlePosition.TopRight, cr.Right, cr.Top),
            (HandlePosition.MiddleLeft, cr.Left, my),
            (HandlePosition.MiddleRight, cr.Right, my),
            (HandlePosition.BottomLeft, cr.Left, cr.Bottom),
            (HandlePosition.BottomCenter, mx, cr.Bottom),
            (HandlePosition.BottomRight, cr.Right, cr.Bottom),
        };
        foreach (var (handle, hx, hy) in positions)
        {
            if (Math.Abs(pt.X - hx) < hs + tol && Math.Abs(pt.Y - hy) < hs + tol)
                return handle;
        }
        return HandlePosition.None;
    }



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

        // Check if clicking an existing text object
        for (int i = _session.Annotations.Count - 1; i >= 0; i--)
        {
            if (_session.Annotations[i] is TextObject existing && existing.HitTest(pt, 6f))
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
        if (_session?.SelectedObject != null)
        {
            var handle = _session.SelectedObject.HitTestHandle(pt);
            Cursor = handle switch
            {
                HandlePosition.TopLeft or HandlePosition.BottomRight => Cursors.SizeNWSE,
                HandlePosition.TopRight or HandlePosition.BottomLeft => Cursors.SizeNESW,
                HandlePosition.TopCenter or HandlePosition.BottomCenter => Cursors.SizeNS,
                HandlePosition.MiddleLeft or HandlePosition.MiddleRight => Cursors.SizeWE,
                _ => _session.SelectedObject.HitTest(pt, 6f) ? Cursors.SizeAll : Cursors.Default,
            };
        }
        else
        {
            // Check if hovering over any object
            bool overObj = false;
            for (int i = _session!.Annotations.Count - 1; i >= 0; i--)
            {
                if (_session.Annotations[i].HitTest(pt, 6f))
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
