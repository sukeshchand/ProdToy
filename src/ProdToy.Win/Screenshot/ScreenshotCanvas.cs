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
    private HandlePosition _resizeHandle;
    private float _totalMoveDx, _totalMoveDy;

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
                StartShapeDraw(pt);
                break;
            case AnnotationTool.Text:
                HandleTextClick(pt);
                break;
            case AnnotationTool.Eraser:
                StartErase(pt);
                break;
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (_session == null || e.Button != MouseButtons.Left) { base.OnMouseDoubleClick(e); return; }
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

        if (_isSelectingRegion)
        {
            _regionEnd = pt;
            Invalidate();
            return;
        }

        if (_isErasing && _eraserTarget != null)
        {
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
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_session == null || e.Button != MouseButtons.Left) return;

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

        if (_isMoving && _session.SelectedObject != null)
        {
            // Record as undoable move
            if (Math.Abs(_totalMoveDx) > 0.5f || Math.Abs(_totalMoveDy) > 0.5f)
            {
                // Undo the live move, then execute via undo system
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
        Cursor = _session?.CurrentTool switch
        {
            AnnotationTool.Select => Cursors.Default,
            AnnotationTool.Text => Cursors.IBeam,
            _ => Cursors.Cross,
        };
    }
}
