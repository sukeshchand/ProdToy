using System.Drawing;
using System.Drawing.Drawing2D;

namespace ProdToy.Plugins.Screenshot;

/// <summary>
/// Reusable color picker popup with:
/// - Saturation/brightness spectrum box
/// - Hue vertical bar
/// - Hex input
/// - RGB fields
/// - Preset color swatches
/// </summary>
class ColorPickerPopup : Form
{
    private Color _selectedColor;
    private float _hue, _sat, _bri;

    // Layout
    private const int PopupWidth = 340;
    private const int PopupHeight = 400;
    private const int Pad = 12;

    // Spectrum box
    private const int SpectrumSize = 180;
    private Rectangle _spectrumRect;
    private Bitmap? _spectrumBitmap;

    // Hue bar
    private const int HueBarWidth = 20;
    private Rectangle _hueBarRect;
    private Bitmap? _hueBarBitmap;

    // Input fields
    private TextBox _hexBox = null!;
    private TextBox _redBox = null!;
    private TextBox _greenBox = null!;
    private TextBox _blueBox = null!;
    private Panel _previewPanel = null!;
    private bool _updatingFields;

    // Dragging
    private bool _draggingSpectrum;
    private bool _draggingHue;

    // Presets
    private static readonly Color[] BasicColors =
    {
        Color.FromArgb(255, 0, 0), Color.FromArgb(255, 50, 50), Color.FromArgb(200, 80, 80),
        Color.FromArgb(255, 128, 0), Color.FromArgb(255, 165, 0), Color.FromArgb(255, 200, 100),
        Color.FromArgb(255, 255, 0), Color.FromArgb(200, 200, 0), Color.FromArgb(255, 255, 128),
        Color.FromArgb(0, 200, 0), Color.FromArgb(0, 255, 0), Color.FromArgb(128, 255, 128),
        Color.FromArgb(0, 200, 200), Color.FromArgb(0, 255, 255), Color.FromArgb(128, 255, 255),
        Color.FromArgb(0, 100, 255), Color.FromArgb(0, 0, 255), Color.FromArgb(100, 100, 255),
        Color.FromArgb(128, 0, 255), Color.FromArgb(160, 32, 240), Color.FromArgb(200, 128, 255),
        Color.FromArgb(255, 0, 255), Color.FromArgb(255, 0, 128), Color.FromArgb(255, 128, 200),
        Color.White, Color.FromArgb(220, 220, 220), Color.FromArgb(180, 180, 180),
        Color.FromArgb(128, 128, 128), Color.FromArgb(80, 80, 80), Color.FromArgb(40, 40, 40),
        Color.Black, Color.FromArgb(60, 60, 60), Color.Transparent,
    };
    private Rectangle[] _swatchRects = Array.Empty<Rectangle>();

    public event Action<Color>? ColorSelected;

    public Color SelectedColor
    {
        get => _selectedColor;
        set
        {
            _selectedColor = value;
            ColorToHSB(value, out _hue, out _sat, out _bri);
            RebuildSpectrumBitmap();
            UpdateFieldsFromColor();
            Invalidate();
        }
    }

    public ColorPickerPopup(Color initialColor, Point screenLocation)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(PopupWidth, PopupHeight);
        BackColor = Color.FromArgb(35, 35, 40);
        KeyPreview = true;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        // Clamp to screen
        var screen = Screen.FromPoint(screenLocation).WorkingArea;
        int x = Math.Min(screenLocation.X, screen.Right - PopupWidth);
        int y = screenLocation.Y;
        if (y + PopupHeight > screen.Bottom) y = screenLocation.Y - PopupHeight - 40;
        Location = new Point(Math.Max(screen.Left, x), Math.Max(screen.Top, y));

        // Layout rects
        _spectrumRect = new Rectangle(Pad, Pad, SpectrumSize, SpectrumSize);
        _hueBarRect = new Rectangle(Pad + SpectrumSize + 8, Pad, HueBarWidth, SpectrumSize);

        // Set initial color
        _selectedColor = initialColor;
        ColorToHSB(initialColor, out _hue, out _sat, out _bri);

        BuildHueBarBitmap();
        RebuildSpectrumBitmap();
        CreateInputControls();

        Deactivate += (_, _) => Close();
    }

    private void CreateInputControls()
    {
        int rightX = Pad + SpectrumSize + 8 + HueBarWidth + 12;
        int rightW = PopupWidth - rightX - Pad;
        int fieldY = Pad;

        // Preview
        _previewPanel = new Panel
        {
            Location = new Point(rightX, fieldY),
            Size = new Size(rightW, 30),
            BackColor = _selectedColor,
        };
        _previewPanel.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(80, 128, 128, 128), 1f);
            e.Graphics.DrawRectangle(pen, 0, 0, _previewPanel.Width - 1, _previewPanel.Height - 1);
        };
        Controls.Add(_previewPanel);
        fieldY += 38;

        // Hex
        var hexLabel = CreateLabel("#", rightX, fieldY + 2);
        Controls.Add(hexLabel);
        _hexBox = CreateTextBox(rightX + 16, fieldY, rightW - 16, _selectedColor.R.ToString("X2") + _selectedColor.G.ToString("X2") + _selectedColor.B.ToString("X2"));
        _hexBox.MaxLength = 6;
        _hexBox.TextChanged += OnHexChanged;
        Controls.Add(_hexBox);
        fieldY += 32;

        // RGB
        var rLabel = CreateLabel("R", rightX, fieldY + 2);
        Controls.Add(rLabel);
        _redBox = CreateTextBox(rightX + 16, fieldY, rightW - 16, _selectedColor.R.ToString());
        _redBox.TextChanged += OnRgbChanged;
        Controls.Add(_redBox);
        fieldY += 30;

        var gLabel = CreateLabel("G", rightX, fieldY + 2);
        Controls.Add(gLabel);
        _greenBox = CreateTextBox(rightX + 16, fieldY, rightW - 16, _selectedColor.G.ToString());
        _greenBox.TextChanged += OnRgbChanged;
        Controls.Add(_greenBox);
        fieldY += 30;

        var bLabel = CreateLabel("B", rightX, fieldY + 2);
        Controls.Add(bLabel);
        _blueBox = CreateTextBox(rightX + 16, fieldY, rightW - 16, _selectedColor.B.ToString());
        _blueBox.TextChanged += OnRgbChanged;
        Controls.Add(_blueBox);
        fieldY += 38;

        // OK / Cancel buttons
        var okBtn = new Button
        {
            Text = "OK",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Location = new Point(rightX, fieldY),
            Size = new Size(rightW / 2 - 2, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(45, 120, 210),
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
        };
        okBtn.FlatAppearance.BorderSize = 0;
        okBtn.Click += (_, _) =>
        {
            ColorSelected?.Invoke(_selectedColor);
            Close();
        };
        Controls.Add(okBtn);

        var cancelBtn = new Button
        {
            Text = "Cancel",
            Font = new Font("Segoe UI", 9f),
            Location = new Point(rightX + rightW / 2 + 2, fieldY),
            Size = new Size(rightW / 2 - 2, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(55, 55, 60),
            ForeColor = Color.FromArgb(180, 180, 190),
            Cursor = Cursors.Hand,
        };
        cancelBtn.FlatAppearance.BorderSize = 0;
        cancelBtn.Click += (_, _) => Close();
        Controls.Add(cancelBtn);
    }

    private Label CreateLabel(string text, int x, int y)
    {
        return new Label
        {
            Text = text,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(150, 180, 180, 200),
            AutoSize = true,
            Location = new Point(x, y),
            BackColor = Color.Transparent,
        };
    }

    private TextBox CreateTextBox(int x, int y, int w, string text)
    {
        return new TextBox
        {
            Text = text,
            Font = new Font("Segoe UI", 9f),
            Location = new Point(x, y),
            Size = new Size(w, 24),
            BackColor = Color.FromArgb(50, 50, 55),
            ForeColor = Color.FromArgb(220, 220, 230),
            BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle,
        };
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

        // Spectrum
        if (_spectrumBitmap != null)
            g.DrawImage(_spectrumBitmap, _spectrumRect);

        // Spectrum cursor
        int sx = _spectrumRect.X + (int)(_sat * _spectrumRect.Width);
        int sy = _spectrumRect.Y + (int)((1 - _bri) * _spectrumRect.Height);
        using var cursorPen = new Pen(Color.White, 1.5f);
        g.DrawEllipse(cursorPen, sx - 6, sy - 6, 12, 12);
        using var cursorPenInner = new Pen(Color.Black, 1f);
        g.DrawEllipse(cursorPenInner, sx - 5, sy - 5, 10, 10);

        // Hue bar
        if (_hueBarBitmap != null)
            g.DrawImage(_hueBarBitmap, _hueBarRect);

        // Hue cursor
        int hy = _hueBarRect.Y + (int)(_hue / 360f * _hueBarRect.Height);
        using var hueCursorPen = new Pen(Color.White, 2f);
        g.DrawRectangle(hueCursorPen, _hueBarRect.X - 2, hy - 3, _hueBarRect.Width + 3, 6);

        // Swatches
        int swatchY = _spectrumRect.Bottom + 12;
        using var swatchLabel = new Font("Segoe UI", 8f, FontStyle.Bold);
        using var labelBrush = new SolidBrush(Color.FromArgb(150, 180, 180, 200));
        g.DrawString("Basic colors", swatchLabel, labelBrush, Pad, swatchY);
        swatchY += 18;

        int swatchSize = 18;
        int swatchGap = 2;
        int cols = (PopupWidth - Pad * 2 + swatchGap) / (swatchSize + swatchGap);
        _swatchRects = new Rectangle[BasicColors.Length];

        for (int i = 0; i < BasicColors.Length; i++)
        {
            int col = i % cols;
            int row = i / cols;
            _swatchRects[i] = new Rectangle(
                Pad + col * (swatchSize + swatchGap),
                swatchY + row * (swatchSize + swatchGap),
                swatchSize, swatchSize);

            if (BasicColors[i] == Color.Transparent)
            {
                // Checkerboard for transparent
                using var checker = new HatchBrush(HatchStyle.SmallCheckerBoard,
                    Color.FromArgb(80, 80, 80), Color.FromArgb(50, 50, 50));
                g.FillRectangle(checker, _swatchRects[i]);
            }
            else
            {
                using var brush = new SolidBrush(BasicColors[i]);
                g.FillRectangle(brush, _swatchRects[i]);
            }

            if (BasicColors[i].ToArgb() == _selectedColor.ToArgb())
            {
                using var selPen = new Pen(Color.FromArgb(200, 80, 160, 255), 1.5f);
                g.DrawRectangle(selPen, _swatchRects[i].X - 1, _swatchRects[i].Y - 1,
                    _swatchRects[i].Width + 1, _swatchRects[i].Height + 1);
            }
        }
    }

    // --- Mouse handling ---

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        if (_spectrumRect.Contains(e.Location))
        {
            _draggingSpectrum = true;
            UpdateFromSpectrum(e.Location);
            Capture = true;
            return;
        }

        if (InflatedRect(_hueBarRect, 4).Contains(e.Location))
        {
            _draggingHue = true;
            UpdateFromHueBar(e.Y);
            Capture = true;
            return;
        }

        // Swatches
        for (int i = 0; i < _swatchRects.Length; i++)
        {
            if (_swatchRects[i].Contains(e.Location))
            {
                SetColor(BasicColors[i]);
                return;
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_draggingSpectrum)
            UpdateFromSpectrum(e.Location);
        else if (_draggingHue)
            UpdateFromHueBar(e.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _draggingSpectrum = false;
        _draggingHue = false;
        Capture = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Close();
        if (e.KeyCode == Keys.Enter)
        {
            ColorSelected?.Invoke(_selectedColor);
            Close();
        }
    }

    // --- Update methods ---

    private void UpdateFromSpectrum(Point pt)
    {
        _sat = Math.Clamp((float)(pt.X - _spectrumRect.X) / _spectrumRect.Width, 0, 1);
        _bri = Math.Clamp(1f - (float)(pt.Y - _spectrumRect.Y) / _spectrumRect.Height, 0, 1);
        _selectedColor = HSBToColor(_hue, _sat, _bri);
        UpdateFieldsFromColor();
        _previewPanel.BackColor = _selectedColor;
        Invalidate();
    }

    private void UpdateFromHueBar(int mouseY)
    {
        _hue = Math.Clamp((float)(mouseY - _hueBarRect.Y) / _hueBarRect.Height * 360f, 0, 359);
        RebuildSpectrumBitmap();
        _selectedColor = HSBToColor(_hue, _sat, _bri);
        UpdateFieldsFromColor();
        _previewPanel.BackColor = _selectedColor;
        Invalidate();
    }

    private void SetColor(Color c)
    {
        _selectedColor = c;
        ColorToHSB(c, out _hue, out _sat, out _bri);
        RebuildSpectrumBitmap();
        UpdateFieldsFromColor();
        _previewPanel.BackColor = c;
        Invalidate();
    }

    private void UpdateFieldsFromColor()
    {
        _updatingFields = true;
        _hexBox.Text = _selectedColor.R.ToString("X2") + _selectedColor.G.ToString("X2") + _selectedColor.B.ToString("X2");
        _redBox.Text = _selectedColor.R.ToString();
        _greenBox.Text = _selectedColor.G.ToString();
        _blueBox.Text = _selectedColor.B.ToString();
        _updatingFields = false;
    }

    private void OnHexChanged(object? sender, EventArgs e)
    {
        if (_updatingFields) return;
        string hex = _hexBox.Text.Trim().TrimStart('#');
        if (hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int val))
        {
            var c = Color.FromArgb((val >> 16) & 0xFF, (val >> 8) & 0xFF, val & 0xFF);
            SetColorFromInput(c);
        }
    }

    private void OnRgbChanged(object? sender, EventArgs e)
    {
        if (_updatingFields) return;
        if (int.TryParse(_redBox.Text, out int r) &&
            int.TryParse(_greenBox.Text, out int g) &&
            int.TryParse(_blueBox.Text, out int b))
        {
            r = Math.Clamp(r, 0, 255);
            g = Math.Clamp(g, 0, 255);
            b = Math.Clamp(b, 0, 255);
            SetColorFromInput(Color.FromArgb(r, g, b));
        }
    }

    private void SetColorFromInput(Color c)
    {
        _selectedColor = c;
        ColorToHSB(c, out _hue, out _sat, out _bri);
        RebuildSpectrumBitmap();
        _previewPanel.BackColor = c;
        _updatingFields = true;
        _hexBox.Text = c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        _redBox.Text = c.R.ToString();
        _greenBox.Text = c.G.ToString();
        _blueBox.Text = c.B.ToString();
        _updatingFields = false;
        Invalidate();
    }

    // --- Bitmap generation ---

    private void RebuildSpectrumBitmap()
    {
        _spectrumBitmap?.Dispose();
        _spectrumBitmap = new Bitmap(_spectrumRect.Width, _spectrumRect.Height);
        for (int y = 0; y < _spectrumBitmap.Height; y++)
        {
            float brightness = 1f - (float)y / _spectrumBitmap.Height;
            for (int x = 0; x < _spectrumBitmap.Width; x++)
            {
                float saturation = (float)x / _spectrumBitmap.Width;
                _spectrumBitmap.SetPixel(x, y, HSBToColor(_hue, saturation, brightness));
            }
        }
    }

    private void BuildHueBarBitmap()
    {
        _hueBarBitmap?.Dispose();
        _hueBarBitmap = new Bitmap(_hueBarRect.Width, _hueBarRect.Height);
        for (int y = 0; y < _hueBarBitmap.Height; y++)
        {
            float hue = (float)y / _hueBarBitmap.Height * 360f;
            var c = HSBToColor(hue, 1f, 1f);
            for (int x = 0; x < _hueBarBitmap.Width; x++)
                _hueBarBitmap.SetPixel(x, y, c);
        }
    }

    // --- HSB conversion ---

    private static Color HSBToColor(float hue, float sat, float bri)
    {
        int hi = (int)(hue / 60f) % 6;
        float f = hue / 60f - (int)(hue / 60f);
        float v = bri;
        float p = bri * (1 - sat);
        float q = bri * (1 - f * sat);
        float t = bri * (1 - (1 - f) * sat);

        float r, g, b;
        switch (hi)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }

        return Color.FromArgb(
            Math.Clamp((int)(r * 255), 0, 255),
            Math.Clamp((int)(g * 255), 0, 255),
            Math.Clamp((int)(b * 255), 0, 255));
    }

    private static void ColorToHSB(Color c, out float hue, out float sat, out float bri)
    {
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float delta = max - min;

        bri = max;
        sat = max < 0.001f ? 0 : delta / max;

        if (delta < 0.001f)
        {
            hue = 0;
        }
        else if (max == r)
        {
            hue = 60f * (((g - b) / delta) % 6);
        }
        else if (max == g)
        {
            hue = 60f * ((b - r) / delta + 2);
        }
        else
        {
            hue = 60f * ((r - g) / delta + 4);
        }

        if (hue < 0) hue += 360;
    }

    private static Rectangle InflatedRect(Rectangle r, int amount)
    {
        var result = r;
        result.Inflate(amount, amount);
        return result;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _spectrumBitmap?.Dispose();
        _hueBarBitmap?.Dispose();
        base.OnFormClosed(e);
    }
}
