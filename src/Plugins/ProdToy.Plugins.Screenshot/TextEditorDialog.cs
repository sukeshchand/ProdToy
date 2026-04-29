using System.Drawing;
using ProdToy.Sdk;

namespace ProdToy.Plugins.Screenshot;

/// <summary>
/// Modal popup that edits a single text annotation. Hosts a real multi-line
/// TextBox so all native editing — selection, arrow navigation, copy/paste,
/// Ctrl+A, etc. — works without us reimplementing it. The toolbar above the
/// text area lets the user change font family, size, bold/italic/underline,
/// and color; those settings apply uniformly to the whole TextObject (not
/// per-character). On OK the caller pulls the final values via the Result
/// property; on Cancel nothing is committed.
/// </summary>
class TextEditorDialog : Form
{
    private readonly PluginTheme _theme;
    private readonly TextBox _textBox;
    private readonly ComboBox _fontCombo;
    private readonly NumericUpDown _sizeNum;
    private readonly Button _boldBtn;
    private readonly Button _italicBtn;
    private readonly Button _underlineBtn;
    private readonly Button _colorBtn;

    public TextEditorResult Result { get; private set; }

    /// <summary>Fires on every edit (text, font, size, B/I/U, color). The
    /// canvas subscribes so the underlying TextObject can be mutated live;
    /// on Cancel the canvas restores from its pre-edit snapshot.</summary>
    public event Action<TextEditorResult>? PreviewChanged;

    public TextEditorDialog(PluginTheme theme, TextEditorResult initial)
    {
        _theme = theme;
        Result = initial;

        Text = "Edit Text";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        ClientSize = new Size(560, 420);
        MinimumSize = new Size(420, 280);
        KeyPreview = true;

        int pad = 10;
        int row = pad;

        // --- Row 1: font family + size ---
        var fontLabel = new Label
        {
            Text = "Font:",
            AutoSize = true,
            ForeColor = theme.TextPrimary,
            BackColor = Color.Transparent,
            Location = new Point(pad, row + 6),
        };
        Controls.Add(fontLabel);

        _fontCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            Location = new Point(pad + 44, row + 2),
            Width = 190,
        };
        // Populate with installed font families. Cap to a reasonable list to
        // keep the dropdown usable; uses InstalledFontCollection so missing
        // fonts on weird Windows installs don't crash.
        try
        {
            using var coll = new System.Drawing.Text.InstalledFontCollection();
            foreach (var fam in coll.Families)
                _fontCombo.Items.Add(fam.Name);
        }
        catch { _fontCombo.Items.Add("Segoe UI"); }
        if (_fontCombo.Items.Contains(initial.FontFamily))
            _fontCombo.SelectedItem = initial.FontFamily;
        else if (_fontCombo.Items.Count > 0)
            _fontCombo.SelectedIndex = 0;
        Controls.Add(_fontCombo);

        var sizeLabel = new Label
        {
            Text = "Size:",
            AutoSize = true,
            ForeColor = theme.TextPrimary,
            BackColor = Color.Transparent,
            Location = new Point(pad + 244, row + 6),
        };
        Controls.Add(sizeLabel);

        _sizeNum = new NumericUpDown
        {
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Minimum = 8,
            Maximum = 200,
            Value = (decimal)Math.Clamp(initial.FontSize, 8f, 200f),
            Location = new Point(pad + 282, row + 2),
            Width = 60,
        };
        Controls.Add(_sizeNum);

        // --- Row 1 cont.: B / I / U / Color ---
        _boldBtn = MakeToggle("B", initial.Bold, new Point(pad + 352, row), 32, FontStyle.Bold);
        _italicBtn = MakeToggle("I", initial.Italic, new Point(pad + 388, row), 32, FontStyle.Italic);
        _underlineBtn = MakeToggle("U", initial.Underline, new Point(pad + 424, row), 32, FontStyle.Underline);

        _colorBtn = new Button
        {
            Text = "Color",
            FlatStyle = FlatStyle.Flat,
            BackColor = initial.Color,
            ForeColor = ContrastText(initial.Color),
            Location = new Point(pad + 460, row),
            Size = new Size(80, 26),
            UseVisualStyleBackColor = false,
        };
        _colorBtn.FlatAppearance.BorderColor = theme.Border;
        _colorBtn.Click += (_, _) =>
        {
            var screenPos = PointToScreen(new Point(_colorBtn.Left, _colorBtn.Bottom));
            var picker = new ColorPickerPopup(_colorBtn.BackColor, screenPos);
            picker.ColorSelected += c =>
            {
                _colorBtn.BackColor = c;
                _colorBtn.ForeColor = ContrastText(c);
                UpdatePreview();
            };
            picker.Show(this);
        };
        Controls.Add(_colorBtn);

        row += 32;

        // Layout below the toolbar: the editor is the *only* big control,
        // and itself acts as the live preview — its Font, ForeColor, etc.
        // mirror exactly what will land on the canvas, so a separate preview
        // panel was redundant. Cancel button stays pinned at the bottom.
        int cancelHeight = 28;
        int bottomMargin = pad + cancelHeight + pad;
        int editorHeight = ClientSize.Height - row - bottomMargin;

        _textBox = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            AcceptsReturn = true,
            AcceptsTab = false,
            WordWrap = true,
            BackColor = theme.BgHeader,
            ForeColor = initial.Color,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(pad, row),
            Size = new Size(ClientSize.Width - pad * 2, editorHeight),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Text = initial.Text,
            Font = BuildFont(initial.FontFamily, initial.FontSize, initial.Bold, initial.Italic, initial.Underline),
        };
        _textBox.SelectionStart = _textBox.Text.Length;
        Controls.Add(_textBox);

        // --- OK / Cancel buttons (bottom of window) ---
        var okBtn = new Button
        {
            Text = "OK",
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.Primary,
            ForeColor = Color.White,
            Size = new Size(100, cancelHeight),
            UseVisualStyleBackColor = false,
            DialogResult = DialogResult.OK,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        okBtn.FlatAppearance.BorderColor = theme.Primary;
        // OK sits to the LEFT of Cancel (Cancel is bottom-right). Location is
        // finalized after Cancel is sized below so we can anchor against it.
        okBtn.Click += (_, _) => CommitResult();
        Controls.Add(okBtn);

        var cancelBtn = new Button
        {
            Text = "Cancel",
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            Size = new Size(100, cancelHeight),
            UseVisualStyleBackColor = false,
            DialogResult = DialogResult.Cancel,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        cancelBtn.FlatAppearance.BorderColor = theme.Border;
        cancelBtn.Location = new Point(
            ClientSize.Width - pad - cancelBtn.Width,
            ClientSize.Height - pad - cancelBtn.Height);
        Controls.Add(cancelBtn);

        // OK is placed left of Cancel.
        okBtn.Location = new Point(
            cancelBtn.Left - pad - okBtn.Width,
            cancelBtn.Top);

        // CancelButton makes Escape revert; AcceptButton intentionally not
        // set so Enter inside the multi-line textbox produces a newline
        // instead of dismissing the dialog.
        CancelButton = cancelBtn;

        // Live preview wiring
        _fontCombo.SelectedIndexChanged += (_, _) => UpdatePreview();
        _sizeNum.ValueChanged += (_, _) => UpdatePreview();
        _textBox.TextChanged += (_, _) => UpdatePreview();

        UpdatePreview();
    }

    private Button MakeToggle(string label, bool initialOn, Point loc, int width, FontStyle drawStyle)
    {
        var btn = new Button
        {
            Text = label,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(width, 26),
            Location = loc,
            UseVisualStyleBackColor = false,
            Tag = initialOn,
            Font = new Font("Segoe UI", 9f, drawStyle),
            BackColor = initialOn ? _theme.Primary : _theme.BgHeader,
            ForeColor = initialOn ? Color.White : _theme.TextPrimary,
        };
        btn.FlatAppearance.BorderColor = _theme.Border;
        btn.Click += (_, _) =>
        {
            bool on = !(bool)btn.Tag!;
            btn.Tag = on;
            btn.BackColor = on ? _theme.Primary : _theme.BgHeader;
            btn.ForeColor = on ? Color.White : _theme.TextPrimary;
            UpdatePreview();
        };
        Controls.Add(btn);
        return btn;
    }

    private static bool IsOn(Button b) => b.Tag is bool on && on;

    private void UpdatePreview()
    {
        try
        {
            string fam = _fontCombo.SelectedItem as string ?? "Segoe UI";
            float size = (float)_sizeNum.Value;
            bool bold = IsOn(_boldBtn);
            bool italic = IsOn(_italicBtn);
            bool underline = IsOn(_underlineBtn);

            // The textbox itself is the preview — its Font / ForeColor mirror
            // what will land on the canvas. We do cap the on-screen font at
            // 96pt so a 200-pt setting doesn't make typing impossible; the
            // underlying TextEditorResult still carries the real size.
            float editorSize = Math.Min(size, 96f);
            _textBox.Font = BuildFont(fam, editorSize, bold, italic, underline);
            _textBox.ForeColor = _colorBtn.BackColor;

            PreviewChanged?.Invoke(new TextEditorResult
            {
                Text = _textBox.Text,
                FontFamily = fam,
                FontSize = size,
                Bold = bold,
                Italic = italic,
                Underline = underline,
                Color = _colorBtn.BackColor,
            });
        }
        catch { /* invalid font/size combo — ignore until next edit */ }
    }

    private static Font BuildFont(string family, float size, bool bold, bool italic, bool underline)
    {
        var style = FontStyle.Regular;
        if (bold) style |= FontStyle.Bold;
        if (italic) style |= FontStyle.Italic;
        if (underline) style |= FontStyle.Underline;
        return new Font(string.IsNullOrEmpty(family) ? "Segoe UI" : family, size, style);
    }


    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Land focus inside the editor with all text pre-selected so the user
        // can immediately retype to replace, or just press an arrow to start
        // editing in place.
        _textBox.Focus();
        _textBox.SelectAll();
    }

    private void CommitResult()
    {
        Result = new TextEditorResult
        {
            Text = _textBox.Text,
            FontFamily = _fontCombo.SelectedItem as string ?? "Segoe UI",
            FontSize = (float)_sizeNum.Value,
            Bold = IsOn(_boldBtn),
            Italic = IsOn(_italicBtn),
            Underline = IsOn(_underlineBtn),
            Color = _colorBtn.BackColor,
        };
    }

    /// <summary>Pick black or white text so a button label stays readable
    /// against any background color the user picks.</summary>
    private static Color ContrastText(Color bg)
    {
        // Standard luminance formula; threshold 0.55 leans toward dark text.
        double lum = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
        return lum > 0.55 ? Color.Black : Color.White;
    }
}

struct TextEditorResult
{
    public string Text;
    public string FontFamily;
    public float FontSize;
    public bool Bold;
    public bool Italic;
    public bool Underline;
    public Color Color;
}
