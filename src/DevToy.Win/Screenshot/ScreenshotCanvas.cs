using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace DevToy;

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
        if (_session == null || e.Button != MouseButtons.Left) return;
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
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_session == null) return;
        var pt = new PointF(e.X, e.Y);

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

    private void HandleSelectMouseDown(PointF pt)
    {
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
