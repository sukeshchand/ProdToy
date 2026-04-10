using System.Drawing;
using System.Drawing.Drawing2D;

namespace ProdToy.Plugins.Screenshot;

/// <summary>
/// A compact popup panel for border settings: toggle, style, color, thickness.
/// Appears below the border button when clicked.
/// </summary>
class BorderPopup : Form
{
    private readonly EditorSession _session;

    private const int PopupWidth = 220;
    private const int PopupHeight = 210;
    private const int Pad = 10;

    public event Action? SettingsChanged;

    // Color palette
    private static readonly Color[] BorderColors =
    {
        Color.FromArgb(60, 60, 60), Color.Black, Color.White,
        Color.Red, Color.FromArgb(255, 100, 0), Color.DodgerBlue,
        Color.FromArgb(160, 32, 240), Color.FromArgb(128, 128, 128),
    };

    // Style options
    private static readonly (CanvasBorderStyle Style, string Label)[] Styles =
    {
        (CanvasBorderStyle.Solid, "Solid"),
        (CanvasBorderStyle.Dashed, "Dashed"),
        (CanvasBorderStyle.Dotted, "Dotted"),
        (CanvasBorderStyle.Double, "Double"),
        (CanvasBorderStyle.Shadow, "Shadow"),
    };

    // Thickness range
    private const float MinThickness = 1f;
    private const float MaxThickness = 12f;

    // Layout rects computed in OnPaint
    private Rectangle _toggleRect;
    private readonly Rectangle[] _styleRects = new Rectangle[5];
    private readonly Rectangle[] _colorRects = new Rectangle[8];
    private Rectangle _sliderTrackRect;
    private bool _isDraggingSlider;

    public BorderPopup(EditorSession session, Point screenLocation)
    {
        _session = session;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(PopupWidth, PopupHeight);
        BackColor = Color.FromArgb(35, 35, 40);

        // Position below the button, clamped to screen
        var screen = Screen.FromPoint(screenLocation).WorkingArea;
        int x = Math.Min(screenLocation.X, screen.Right - PopupWidth);
        int y = screenLocation.Y;
        if (y + PopupHeight > screen.Bottom) y = screenLocation.Y - PopupHeight - 40;
        Location = new Point(x, y);

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        Deactivate += (_, _) => Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // Background + border
        using (var bgBrush = new SolidBrush(BackColor))
            g.FillRectangle(bgBrush, ClientRectangle);
        using var borderPen = new Pen(Color.FromArgb(80, 100, 100, 120), 1f);
        g.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);

        int y = Pad;
        using var labelFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        using var valueFont = new Font("Segoe UI", 8.5f);
        using var labelBrush = new SolidBrush(Color.FromArgb(160, 180, 180, 200));
        var accentColor = Color.FromArgb(200, 80, 160, 255);

        // --- Toggle ---
        _toggleRect = new Rectangle(Pad, y, PopupWidth - Pad * 2, 22);
        string toggleText = _session.BorderEnabled ? "\u2611  Border On" : "\u2610  Border Off";
        var toggleColor = _session.BorderEnabled ? accentColor : Color.FromArgb(160, 180, 180, 200);
        using (var toggleBrush = new SolidBrush(toggleColor))
            g.DrawString(toggleText, valueFont, toggleBrush, _toggleRect.X, _toggleRect.Y + 2);
        y += 28;

        // --- Style ---
        g.DrawString("STYLE", labelFont, labelBrush, Pad, y);
        y += 18;
        int styleW = (PopupWidth - Pad * 2 - 4 * (Styles.Length - 1)) / Styles.Length;
        for (int i = 0; i < Styles.Length; i++)
        {
            _styleRects[i] = new Rectangle(Pad + i * (styleW + 4), y, styleW, 22);
            bool active = _session.BorderEnabled && _session.BorderStyle == Styles[i].Style;
            var bg = active ? Color.FromArgb(60, 80, 160, 255) : Color.FromArgb(30, 255, 255, 255);
            using var bgB = new SolidBrush(bg);
            using var path = RoundedRect(_styleRects[i], 3);
            g.FillPath(bgB, path);
            if (active)
            {
                using var activePen = new Pen(Color.FromArgb(100, 80, 160, 255), 1f);
                g.DrawPath(activePen, path);
            }
            var txtColor = active ? accentColor : Color.FromArgb(140, 200, 200, 210);
            using var txtBrush = new SolidBrush(txtColor);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(Styles[i].Label, new Font("Segoe UI", 7.5f), txtBrush, _styleRects[i], sf);
        }
        y += 30;

        // --- Color ---
        g.DrawString("COLOR", labelFont, labelBrush, Pad, y);
        y += 18;
        int swatchSize = 20;
        int swatchGap = 4;
        for (int i = 0; i < BorderColors.Length; i++)
        {
            int col = i;
            _colorRects[i] = new Rectangle(Pad + col * (swatchSize + swatchGap), y, swatchSize, swatchSize);
            using var brush = new SolidBrush(BorderColors[i]);
            g.FillRectangle(brush, _colorRects[i]);
            if (_session.BorderColor.ToArgb() == BorderColors[i].ToArgb())
            {
                using var selPen = new Pen(accentColor, 1.5f);
                g.DrawRectangle(selPen, _colorRects[i].X - 1, _colorRects[i].Y - 1,
                    _colorRects[i].Width + 1, _colorRects[i].Height + 1);
            }
        }
        y += swatchSize + 10;

        // --- Thickness slider ---
        g.DrawString($"THICKNESS  ({_session.BorderThickness:0.#}px)", labelFont, labelBrush, Pad, y);
        y += 18;
        int trackLeft = Pad;
        int trackRight = PopupWidth - Pad;
        int trackWidth = trackRight - trackLeft;
        _sliderTrackRect = new Rectangle(trackLeft, y + 4, trackWidth, 6);

        // Track background
        using (var trackBrush = new SolidBrush(Color.FromArgb(50, 255, 255, 255)))
            g.FillRectangle(trackBrush, _sliderTrackRect);

        // Thumb position
        float ratio = (_session.BorderThickness - MinThickness) / (MaxThickness - MinThickness);
        int thumbX = trackLeft + (int)(ratio * trackWidth);
        using (var thumbBrush = new SolidBrush(accentColor))
            g.FillEllipse(thumbBrush, thumbX - 6, y, 12, 12);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var pt = e.Location;

        // Toggle
        if (_toggleRect.Contains(pt))
        {
            _session.BorderEnabled = !_session.BorderEnabled;
            SettingsChanged?.Invoke();
            Invalidate();
            return;
        }

        // Styles
        for (int i = 0; i < Styles.Length; i++)
        {
            if (_styleRects[i].Contains(pt))
            {
                _session.BorderStyle = Styles[i].Style;
                if (!_session.BorderEnabled) _session.BorderEnabled = true;
                SettingsChanged?.Invoke();
                Invalidate();
                return;
            }
        }

        // Colors
        for (int i = 0; i < BorderColors.Length; i++)
        {
            if (_colorRects[i].Contains(pt))
            {
                _session.BorderColor = BorderColors[i];
                if (!_session.BorderEnabled) _session.BorderEnabled = true;
                SettingsChanged?.Invoke();
                Invalidate();
                return;
            }
        }

        // Slider
        var sliderHit = _sliderTrackRect;
        sliderHit.Inflate(0, 8);
        if (sliderHit.Contains(pt))
        {
            _isDraggingSlider = true;
            UpdateSlider(pt.X);
            Capture = true;
            return;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_isDraggingSlider)
        {
            UpdateSlider(e.X);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_isDraggingSlider)
        {
            _isDraggingSlider = false;
            Capture = false;
        }
    }

    private void UpdateSlider(int mouseX)
    {
        float ratio = (float)(mouseX - _sliderTrackRect.Left) / _sliderTrackRect.Width;
        ratio = Math.Clamp(ratio, 0f, 1f);
        _session.BorderThickness = MinThickness + ratio * (MaxThickness - MinThickness);
        _session.BorderThickness = MathF.Round(_session.BorderThickness * 2) / 2; // snap to 0.5
        if (!_session.BorderEnabled) _session.BorderEnabled = true;
        SettingsChanged?.Invoke();
        Invalidate();
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Close();
    }
}
