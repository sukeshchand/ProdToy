using System.Drawing;
using System.Drawing.Drawing2D;

namespace ProdToy.Plugins.Screenshot;

class ScreenshotToolbar : Control
{
    // Layout constants
    private const int BtnSize = 32;
    private const int BtnPad = 2;
    private const int SepWidth = 8;
    private const int BarPad = 6;

    private readonly List<ToolbarItem> _items = new();
    private int _hoverIndex = -1;
    private EditorSession? _session;

    public event Action? QuickCopyRequested;
    public event Action? CopyPathRequested;
    public event Action? CopyPathTextRequested;
    public event Action<AnnotationTool>? ToolSelected;
    public event Action? UndoRequested;
    public event Action? RedoRequested;
    public event Action? DeleteRequested;
    public event Action? BringForwardRequested;
    public event Action? SendBackwardRequested;
    public event Action? SaveRequested;
    public event Action? SaveAsRequested;
    public event Action? CopyRequested;
    public event Action? CancelRequested;
    public event Action<Color>? ColorChanged;
    public event Action? ColorPickerRequested;
    public event Action? BgColorPickerRequested;
    public event Action<float>? ThicknessChanged;
    public event Action<float>? FontSizeChanged;
    public event Action? BorderToggled;
    public event Action? BorderPopupRequested;
    public event Action? ZoomInRequested;
    public event Action? ZoomOutRequested;
    public event Action? ZoomResetRequested;
    public event Action? ZoomFitRequested;
    public event Action? CompareRequested;

    /// <summary>Provides the current zoom value (1.0 = 100%) for the toolbar label.</summary>
    public Func<float>? ZoomProvider { get; set; }

    public ScreenshotToolbar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.FromArgb(40, 40, 40);
        Height = BtnSize + BarPad * 2;

        BuildItems();
        Width = CalculateWidth();
    }

    public EditorSession? Session
    {
        get => _session;
        set { _session = value; Invalidate(); }
    }

    private void BuildItems()
    {
        _items.Clear();

        // Primary actions — most used, visually distinct
        _items.Add(new ToolbarPrimaryButton("quickcopy", "\uE8C8", "Image", "Copy Image (Ctrl+C)", () => QuickCopyRequested?.Invoke()));
        _items.Add(new ToolbarPrimaryButton("copypath", "\uE8C8", "File", "Copy File (Ctrl+Shift+C)", () => CopyPathRequested?.Invoke()));
        _items.Add(new ToolbarPrimaryButton("copypathtext", "\uE8C8", "Path", "Copy Path (Ctrl+Shift+P)", () => CopyPathTextRequested?.Invoke()));
        _items.Add(new ToolbarPrimaryButton("compare", "\uE8F1", "Compare", "Compare Last Two (Ctrl+Shift+K)", () => CompareRequested?.Invoke()));
        AddSeparator();

        // Tools
        AddButton("select", "\uE8B0", "Select (V)", () => ToolSelected?.Invoke(AnnotationTool.Select));
        AddButton("pen", "\uE70F", "Pen (P)", () => ToolSelected?.Invoke(AnnotationTool.Pen));
        AddButton("marker", "\uE7E6", "Marker (M)", () => ToolSelected?.Invoke(AnnotationTool.Marker));
        AddSeparator();
        AddButton("line", "\uF7AF", "Line (L)", () => ToolSelected?.Invoke(AnnotationTool.Line));
        AddButton("arrow", "\uE8AD", "Arrow (A)", () => ToolSelected?.Invoke(AnnotationTool.Arrow));
        AddButton("rect", "\uE739", "Rectangle (R)", () => ToolSelected?.Invoke(AnnotationTool.Rectangle));
        AddButton("ellipse", "\uEA3A", "Ellipse (E)", () => ToolSelected?.Invoke(AnnotationTool.Ellipse));
        AddButton("text", "\uE8D2", "Text (T)", () => ToolSelected?.Invoke(AnnotationTool.Text));
        AddButton("mask", "***", "Mask Box (K)", () => ToolSelected?.Invoke(AnnotationTool.MaskBox));
        AddButton("eraser", "\uE75C", "Eraser (X)", () => ToolSelected?.Invoke(AnnotationTool.Eraser));
        AddButton("crop", "\uE7A8", "Crop (C)", () => ToolSelected?.Invoke(AnnotationTool.Crop));
        AddSeparator();

        // Undo/Redo
        AddButton("undo", "\uE7A7", "Undo (Ctrl+Z)", () => UndoRequested?.Invoke());
        AddButton("redo", "\uE7A6", "Redo (Ctrl+Y)", () => RedoRequested?.Invoke());
        AddSeparator();

        // Layer controls
        AddButton("forward", "\uE74A", "Bring Forward", () => BringForwardRequested?.Invoke());
        AddButton("backward", "\uE74B", "Send Backward", () => SendBackwardRequested?.Invoke());
        AddButton("delete", "\uE74D", "Delete (Del)", () => DeleteRequested?.Invoke());
        AddSeparator();

        // Border (split: toggle + dropdown arrow for popup)
        _items.Add(new ToolbarBorderSplitButton("\uF573", "Border (B)",
            () => BorderToggled?.Invoke(),
            () => BorderPopupRequested?.Invoke()));
        AddSeparator();

        // Color: swatch showing current color, opens full picker
        _items.Add(new ToolbarColorSwatch("color", "Stroke Color", () => ColorPickerRequested?.Invoke(), session => session?.CurrentColor));
        _items.Add(new ToolbarColorSwatch("bgcolor", "Canvas BG Color", () => BgColorPickerRequested?.Invoke(), session => session?.CanvasBackgroundColor));
        AddSeparator();

        // Thickness
        AddThicknessSelector();
        AddSeparator();

        // Font size (only relevant for text tool)
        AddFontSizeSelector();
        AddSeparator();

        // Zoom group
        AddSeparator();
        AddButton("zoomout", "\uE71F", "Zoom Out (Ctrl+-)", () => ZoomOutRequested?.Invoke());
        _items.Add(new ToolbarZoomLabel(
            () => ZoomProvider?.Invoke() ?? 1f,
            () => ZoomResetRequested?.Invoke()));
        AddButton("zoomin", "\uE8A3", "Zoom In (Ctrl+=)", () => ZoomInRequested?.Invoke());
        AddButton("zoomfit", "\uE9A6", "Fit to Window (Ctrl+9)", () => ZoomFitRequested?.Invoke());
        AddSeparator();

        // Actions
        AddButton("saveas", "\uE792", "Save As (Ctrl+Shift+S)", () => SaveAsRequested?.Invoke());
        AddButton("cancel", "\uE711", "Close (Esc)", () => CancelRequested?.Invoke());

        Width = CalculateWidth();
    }

    private void AddButton(string id, string icon, string tooltip, Action onClick)
    {
        _items.Add(new ToolbarButton(id, icon, tooltip, onClick));
    }

    private void AddSeparator()
    {
        _items.Add(new ToolbarSeparator());
    }

    private void AddThicknessSelector()
    {
        _items.Add(new ToolbarThicknessSelector(thickness =>
        {
            ThicknessChanged?.Invoke(thickness);
            Invalidate();
        }));
    }

    private void AddFontSizeSelector()
    {
        _items.Add(new ToolbarFontSizeSelector(size =>
        {
            FontSizeChanged?.Invoke(size);
            Invalidate();
        }));
    }

    private int CalculateWidth()
    {
        int w = BarPad;
        foreach (var item in _items)
            w += item.GetWidth() + BtnPad;
        return w + BarPad;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // Background with rounded corners
        using var bgBrush = new SolidBrush(Color.FromArgb(230, 35, 35, 40));
        using var bgPath = RoundedRect(new RectangleF(0, 0, Width, Height), 8);
        g.FillPath(bgBrush, bgPath);

        // Border
        using var borderPen = new Pen(Color.FromArgb(80, 100, 100, 120), 1f);
        g.DrawPath(borderPen, bgPath);

        int x = BarPad;
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var rect = new Rectangle(x, BarPad, item.GetWidth(), BtnSize);
            item.Render(g, rect, i == _hoverIndex, _session);
            x += item.GetWidth() + BtnPad;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int idx = HitTestItem(e.Location);
        if (idx != _hoverIndex)
        {
            _hoverIndex = idx;
            Invalidate();

            // Tooltip
            if (idx >= 0)
            {
                string? tooltip = _items[idx] switch
                {
                    ToolbarButton btn => btn.Tooltip,
                    ToolbarPrimaryButton pb => pb.Tooltip,
                    ToolbarColorSwatch cs => cs.Tooltip,
                    ToolbarBorderSplitButton bs => bs.Tooltip,
                    _ => null,
                };
                if (tooltip != null)
                {
                    var tt = GetOrCreateTooltip();
                    tt.SetToolTip(this, tooltip);
                }
            }
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoverIndex = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        int idx = HitTestItem(e.Location);
        if (idx >= 0)
        {
            var rect = GetItemRect(idx);
            _items[idx].HandleClick(e.Location, rect, _session);
            Invalidate();
        }
    }

    private int HitTestItem(Point pt)
    {
        int x = BarPad;
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var rect = new Rectangle(x, BarPad, item.GetWidth(), BtnSize);
            if (rect.Contains(pt)) return i;
            x += item.GetWidth() + BtnPad;
        }
        return -1;
    }

    private Rectangle GetItemRect(int index)
    {
        int x = BarPad;
        for (int i = 0; i < _items.Count; i++)
        {
            var rect = new Rectangle(x, BarPad, _items[i].GetWidth(), BtnSize);
            if (i == index) return rect;
            x += _items[i].GetWidth() + BtnPad;
        }
        return Rectangle.Empty;
    }

    private ToolTip? _tooltip;
    private ToolTip GetOrCreateTooltip()
    {
        _tooltip ??= new ToolTip { InitialDelay = 400, ReshowDelay = 200 };
        return _tooltip;
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
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

    // --- Toolbar item types ---

    abstract class ToolbarItem
    {
        public abstract int GetWidth();
        public abstract void Render(Graphics g, Rectangle rect, bool hover, EditorSession? session);
        public virtual void HandleClick(Point pt, Rectangle rect, EditorSession? session) { }
    }

    class ToolbarSeparator : ToolbarItem
    {
        public override int GetWidth() => SepWidth;
        public override void Render(Graphics g, Rectangle rect, bool hover, EditorSession? session)
        {
            int cx = rect.X + rect.Width / 2;
            using var pen = new Pen(Color.FromArgb(60, 128, 128, 128), 1f);
            g.DrawLine(pen, cx, rect.Y + 6, cx, rect.Bottom - 6);
        }
    }

    class ToolbarButton : ToolbarItem
    {
        public string Id { get; }
        public string Icon { get; }
        public string Tooltip { get; }
        private readonly Action _onClick;

        private static readonly Dictionary<string, AnnotationTool> ToolMap = new()
        {
            ["select"] = AnnotationTool.Select,
            ["pen"] = AnnotationTool.Pen,
            ["marker"] = AnnotationTool.Marker,
            ["line"] = AnnotationTool.Line,
            ["arrow"] = AnnotationTool.Arrow,
            ["rect"] = AnnotationTool.Rectangle,
            ["ellipse"] = AnnotationTool.Ellipse,
            ["text"] = AnnotationTool.Text,
            ["mask"] = AnnotationTool.MaskBox,
            ["eraser"] = AnnotationTool.Eraser,
            ["crop"] = AnnotationTool.Crop,
        };

        public ToolbarButton(string id, string icon, string tooltip, Action onClick)
        {
            Id = id;
            Icon = icon;
            Tooltip = tooltip;
            _onClick = onClick;
        }

        public override int GetWidth() => BtnSize;

        public override void Render(Graphics g, Rectangle rect, bool hover, EditorSession? session)
        {
            bool isActive = session != null && ToolMap.TryGetValue(Id, out var tool) && session.CurrentTool == tool;
            if (Id == "border" && session != null) isActive = session.BorderEnabled;

            if (isActive)
            {
                using var activeBrush = new SolidBrush(Color.FromArgb(60, 80, 160, 255));
                using var activePath = RoundedRectF(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), 4);
                g.FillPath(activeBrush, activePath);
                using var activeBorder = new Pen(Color.FromArgb(120, 80, 160, 255), 1f);
                g.DrawPath(activeBorder, activePath);
            }
            else if (hover)
            {
                using var hoverBrush = new SolidBrush(Color.FromArgb(40, 255, 255, 255));
                using var hoverPath = RoundedRectF(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), 4);
                g.FillPath(hoverBrush, hoverPath);
            }

            // Special coloring
            Color textColor = Id switch
            {
                "cancel" => Color.FromArgb(200, 255, 80, 80),
                "delete" => Color.FromArgb(200, 255, 80, 80),
                "save" or "copy" => Color.FromArgb(200, 80, 200, 120),
                _ => Color.FromArgb(200, 220, 220, 230),
            };

            var font = Id == "mask"
                ? FontPool.Get("Segoe UI", 11f, FontStyle.Bold)
                : FontPool.Get("Segoe Fluent Icons", 12f);
            using var brush = new SolidBrush(textColor);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(Icon, font, brush, rect, sf);
        }

        public override void HandleClick(Point pt, Rectangle rect, EditorSession? session) => _onClick();

        private static GraphicsPath RoundedRectF(RectangleF r, float rad)
        {
            var path = new GraphicsPath();
            float d = rad * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>Split button: left side toggles border, right arrow opens popup.</summary>
    class ToolbarBorderSplitButton : ToolbarItem
    {
        private readonly string _icon;
        public string Tooltip { get; }
        private readonly Action _onToggle;
        private readonly Action _onPopup;
        private const int TotalWidth = 44; // icon area + arrow area
        private const int ArrowWidth = 14;

        public ToolbarBorderSplitButton(string icon, string tooltip, Action onToggle, Action onPopup)
        {
            _icon = icon;
            Tooltip = tooltip;
            _onToggle = onToggle;
            _onPopup = onPopup;
        }

        public override int GetWidth() => TotalWidth;

        public override void Render(Graphics g, Rectangle rect, bool hover, EditorSession? session)
        {
            bool isActive = session?.BorderEnabled == true;
            int iconW = rect.Width - ArrowWidth;
            var iconRect = new Rectangle(rect.X, rect.Y, iconW, rect.Height);
            var arrowRect = new Rectangle(rect.X + iconW, rect.Y, ArrowWidth, rect.Height);

            // Icon area background
            if (isActive)
            {
                using var activeBrush = new SolidBrush(Color.FromArgb(60, 80, 160, 255));
                using var activePath = RoundedRectF(new RectangleF(iconRect.X, iconRect.Y, iconRect.Width, iconRect.Height), 4);
                g.FillPath(activeBrush, activePath);
                using var activeBorder = new Pen(Color.FromArgb(120, 80, 160, 255), 1f);
                g.DrawPath(activeBorder, activePath);
            }
            else if (hover)
            {
                using var hoverBrush = new SolidBrush(Color.FromArgb(40, 255, 255, 255));
                using var hoverPath = RoundedRectF(new RectangleF(iconRect.X, iconRect.Y, iconRect.Width, iconRect.Height), 4);
                g.FillPath(hoverBrush, hoverPath);
            }

            // Arrow area background (always subtle hover)
            if (hover)
            {
                using var arrowBg = new SolidBrush(Color.FromArgb(30, 255, 255, 255));
                using var arrowPath = RoundedRectF(new RectangleF(arrowRect.X, arrowRect.Y, arrowRect.Width, arrowRect.Height), 4);
                g.FillPath(arrowBg, arrowPath);
            }

            // Icon
            var iconColor = Color.FromArgb(200, 220, 220, 230);
            var font = FontPool.Get("Segoe Fluent Icons", 12f);
            using var brush = new SolidBrush(iconColor);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(_icon, font, brush, iconRect, sf);

            // Down arrow
            var arrowFont = FontPool.Get("Segoe Fluent Icons", 7f);
            g.DrawString("\uE70D", arrowFont, brush, arrowRect, sf);
        }

        public override void HandleClick(Point pt, Rectangle rect, EditorSession? session)
        {
            int iconW = rect.Width - ArrowWidth;
            if (pt.X >= rect.X + iconW)
                _onPopup();
            else
                _onToggle();
        }

        private static GraphicsPath RoundedRectF(RectangleF r, float rad)
        {
            var path = new GraphicsPath();
            float d = rad * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>Primary action button — wider, filled accent background, icon + label.</summary>
    class ToolbarPrimaryButton : ToolbarItem
    {
        private readonly string _id;
        private readonly string _icon;
        private readonly string _label;
        public string Tooltip { get; }
        private readonly Action _onClick;
        private const int Width = 58;

        public ToolbarPrimaryButton(string id, string icon, string label, string tooltip, Action onClick)
        {
            _id = id;
            _icon = icon;
            _label = label;
            Tooltip = tooltip;
            _onClick = onClick;
        }

        public override int GetWidth() => Width;

        public override void Render(Graphics g, Rectangle rect, bool hover, EditorSession? session)
        {
            // Filled accent background
            var bgColor = hover
                ? Color.FromArgb(200, 60, 140, 230)
                : Color.FromArgb(180, 45, 120, 210);
            using var bgPath = RoundedRectF(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), 5);
            using var bgBrush = new SolidBrush(bgColor);
            g.FillPath(bgBrush, bgPath);

            // Border glow
            using var borderPen = new Pen(Color.FromArgb(hover ? 140 : 80, 80, 160, 255), 1f);
            g.DrawPath(borderPen, bgPath);

            // Icon + label stacked
            var iconFont = FontPool.Get("Segoe Fluent Icons", 12f);
            var labelFont = FontPool.Get("Segoe UI", 7f, FontStyle.Bold);
            using var textBrush = new SolidBrush(Color.White);

            // Icon centered in top portion
            var iconRect = new RectangleF(rect.X, rect.Y - 1, rect.Width, rect.Height * 0.62f);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(_icon, iconFont, textBrush, iconRect, sf);

            // Label at bottom
            var labelRect = new RectangleF(rect.X, rect.Y + rect.Height * 0.55f, rect.Width, rect.Height * 0.45f);
            g.DrawString(_label, labelFont, textBrush, labelRect, sf);
        }

        public override void HandleClick(Point pt, Rectangle rect, EditorSession? session) => _onClick();

        private static GraphicsPath RoundedRectF(RectangleF r, float rad)
        {
            var path = new GraphicsPath();
            float d = rad * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>A small colored swatch that shows the current color and opens a picker on click.</summary>
    class ToolbarColorSwatch : ToolbarItem
    {
        private readonly string _id;
        public string Tooltip { get; }
        private readonly Action _onClick;
        private readonly Func<EditorSession?, Color?> _getColor;
        private const int SwatchW = 28;

        public ToolbarColorSwatch(string id, string tooltip, Action onClick, Func<EditorSession?, Color?> getColor)
        {
            _id = id;
            Tooltip = tooltip;
            _onClick = onClick;
            _getColor = getColor;
        }

        public override int GetWidth() => SwatchW;

        public override void Render(Graphics g, Rectangle rect, bool hover, EditorSession? session)
        {
            var color = _getColor(session) ?? Color.Red;
            int pad = 4;
            var swatchRect = new Rectangle(rect.X + pad, rect.Y + pad, rect.Width - pad * 2, rect.Height - pad * 2);

            // Fill with checkerboard first (for light colors visibility)
            using (var checker = new System.Drawing.Drawing2D.HatchBrush(
                System.Drawing.Drawing2D.HatchStyle.SmallCheckerBoard,
                Color.FromArgb(60, 60, 60), Color.FromArgb(40, 40, 40)))
                g.FillRectangle(checker, swatchRect);

            // Color fill
            using (var brush = new SolidBrush(color))
                g.FillRectangle(brush, swatchRect);

            // Border
            var borderColor = hover ? Color.FromArgb(180, 80, 160, 255) : Color.FromArgb(100, 160, 160, 170);
            using var pen = new Pen(borderColor, hover ? 1.5f : 1f);
            g.DrawRectangle(pen, swatchRect);

            // Small label
            var font = FontPool.Get("Segoe UI", 6f);
            using var labelBrush = new SolidBrush(Color.FromArgb(140, 200, 200, 210));
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            string label = _id == "bgcolor" ? "BG" : "";
            if (label.Length > 0)
            {
                // Draw label background
                var labelRect = new RectangleF(swatchRect.X, swatchRect.Bottom - 9, swatchRect.Width, 9);
                using var labelBg = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
                g.FillRectangle(labelBg, labelRect);
                using var labelTextBrush = new SolidBrush(Color.FromArgb(200, 220, 220, 230));
                g.DrawString(label, font, labelTextBrush, labelRect, sf);
            }
        }

        public override void HandleClick(Point pt, Rectangle rect, EditorSession? session) => _onClick();
    }

    class ToolbarThicknessSelector : ToolbarItem
    {
        private readonly float[] _sizes = { 1f, 2f, 3f, 5f, 8f };
        private readonly Action<float> _onPick;

        public ToolbarThicknessSelector(Action<float> onPick) => _onPick = onPick;

        public override int GetWidth() => _sizes.Length * 14 + 4;

        public override void Render(Graphics g, Rectangle rect, bool hover, EditorSession? session)
        {
            int x = rect.X + 2;
            float currentThickness = session?.CurrentThickness ?? 2f;
            for (int i = 0; i < _sizes.Length; i++)
            {
                int cx = x + i * 14 + 7;
                int cy = rect.Y + rect.Height / 2;
                int r = Math.Max(2, (int)(_sizes[i] * 1.2f));
                bool active = Math.Abs(_sizes[i] - currentThickness) < 0.1f;

                using var brush = new SolidBrush(active ? Color.FromArgb(200, 80, 160, 255) : Color.FromArgb(160, 200, 200, 210));
                g.FillEllipse(brush, cx - r, cy - r, r * 2, r * 2);
            }
        }

        public override void HandleClick(Point pt, Rectangle rect, EditorSession? session)
        {
            int x = rect.X + 2;
            for (int i = 0; i < _sizes.Length; i++)
            {
                var hitRect = new Rectangle(x + i * 14, rect.Y, 14, rect.Height);
                if (hitRect.Contains(pt))
                {
                    _onPick(_sizes[i]);
                    return;
                }
            }
        }
    }

    class ToolbarFontSizeSelector : ToolbarItem
    {
        private readonly float[] _sizes = { 12f, 16f, 24f, 36f };
        private readonly Action<float> _onPick;

        public ToolbarFontSizeSelector(Action<float> onPick) => _onPick = onPick;

        public override int GetWidth() => _sizes.Length * 20 + 4;

        public override void Render(Graphics g, Rectangle rect, bool hover, EditorSession? session)
        {
            int x = rect.X + 2;
            float currentSize = session?.CurrentFontSize ?? 16f;
            for (int i = 0; i < _sizes.Length; i++)
            {
                bool active = Math.Abs(_sizes[i] - currentSize) < 0.1f;
                var itemRect = new Rectangle(x + i * 20, rect.Y, 20, rect.Height);
                var textColor = active ? Color.FromArgb(200, 80, 160, 255) : Color.FromArgb(140, 200, 200, 210);
                float fs = 6f + _sizes[i] / 8f;
                var font = FontPool.Get("Segoe UI", Math.Min(fs, 11f), FontStyle.Bold);
                using var brush = new SolidBrush(textColor);
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("A", font, brush, itemRect, sf);
            }
        }

        public override void HandleClick(Point pt, Rectangle rect, EditorSession? session)
        {
            int x = rect.X + 2;
            for (int i = 0; i < _sizes.Length; i++)
            {
                var hitRect = new Rectangle(x + i * 20, rect.Y, 20, rect.Height);
                if (hitRect.Contains(pt))
                {
                    _onPick(_sizes[i]);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Read-only zoom percentage label. Click resets to 100%.
    /// Reads live zoom via a getter callback so the toolbar repaints reflect changes.
    /// </summary>
    class ToolbarZoomLabel : ToolbarItem
    {
        private readonly Func<float> _zoomGetter;
        private readonly Action _onClickReset;

        public ToolbarZoomLabel(Func<float> zoomGetter, Action onClickReset)
        {
            _zoomGetter = zoomGetter;
            _onClickReset = onClickReset;
        }

        public override int GetWidth() => 44;

        public override void Render(Graphics g, Rectangle rect, bool hover, EditorSession? session)
        {
            float z = _zoomGetter();
            int pct = (int)Math.Round(z * 100);
            string label = pct + "%";

            if (hover)
            {
                using var hb = new SolidBrush(Color.FromArgb(40, 255, 255, 255));
                g.FillRectangle(hb, rect);
            }

            var font = FontPool.Get("Segoe UI", 9f, FontStyle.Bold);
            using var brush = new SolidBrush(Color.FromArgb(220, 220, 220, 230));
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(label, font, brush, rect, sf);
        }

        public override void HandleClick(Point pt, Rectangle rect, EditorSession? session)
        {
            _onClickReset();
        }
    }
}
