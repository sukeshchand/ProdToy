using System.Drawing;
using System.Globalization;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Modal form for creating or editing a Windows Terminal color scheme — the
/// 20 named colors WT's "schemes" array entries support, plus a name. Each color
/// row is a hex textbox + click-to-pick swatch (WinForms ColorDialog).
/// </summary>
class WtSchemeEditForm : Form
{
    private readonly PluginTheme _theme;
    private readonly string? _originalName;

    private readonly TextBox _nameBox;
    private readonly Label _validationLabel;

    // Each color gets: row label, hex textbox, swatch button
    private readonly Dictionary<string, TextBox> _colorBoxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Panel> _colorSwatches = new(StringComparer.OrdinalIgnoreCase);

    public string? ResultSchemeName { get; private set; }
    public bool Deleted { get; private set; }

    // Display order: the 4 special roles on top, then the 8 basic + 8 bright ANSI colors.
    private static readonly (string Key, string Label)[] ColorFields =
    {
        ("foreground",          "Foreground"),
        ("background",          "Background"),
        ("cursorColor",         "Cursor"),
        ("selectionBackground", "Selection BG"),
        ("black",               "Black"),
        ("red",                 "Red"),
        ("green",               "Green"),
        ("yellow",              "Yellow"),
        ("blue",                "Blue"),
        ("purple",              "Purple"),
        ("cyan",                "Cyan"),
        ("white",               "White"),
        ("brightBlack",         "Bright Black"),
        ("brightRed",           "Bright Red"),
        ("brightGreen",         "Bright Green"),
        ("brightYellow",        "Bright Yellow"),
        ("brightBlue",          "Bright Blue"),
        ("brightPurple",        "Bright Purple"),
        ("brightCyan",          "Bright Cyan"),
        ("brightWhite",         "Bright White"),
    };

    public WtSchemeEditForm(PluginTheme theme, WtSchemeDraft? existing = null)
    {
        _theme = theme;
        _originalName = existing?.Name;
        bool isEdit = existing != null;
        var src = existing ?? new WtSchemeDraft();

        Text = isEdit ? "Edit Color Scheme" : "New Color Scheme";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(760, 720);
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;

        int pad = 20;
        int y = pad;

        var header = new Label
        {
            Text = isEdit ? "Edit Color Scheme" : "New Color Scheme",
            Font = new Font("Segoe UI Semibold", 14f, FontStyle.Bold),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        Controls.Add(header);
        y += 40;

        var note = new Label
        {
            Text = isEdit
                ? "Updates an existing entry in Windows Terminal's settings.json \"schemes\" array."
                : "Writes a new entry into Windows Terminal's settings.json \"schemes\" array.\n"
                + "Click any swatch to pick a color, or edit the hex value directly.",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        Controls.Add(note);
        y += isEdit ? 26 : 40;

        AddSection("NAME", y, sepWidth: ClientSize.Width - pad * 2);
        y += 32;
        var nameLbl = new Label
        {
            Text = "Scheme name",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(pad, y + 4),
            BackColor = Color.Transparent,
        };
        Controls.Add(nameLbl);
        _nameBox = new TextBox
        {
            Font = new Font("Segoe UI", 10f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Size = new Size(280, 26),
            Location = new Point(pad + 100, y),
            Text = src.Name,
        };
        Controls.Add(_nameBox);
        y += 40;

        AddSection("COLORS", y, sepWidth: ClientSize.Width - pad * 2);
        y += 32;

        // Arrange in 2 columns of 10 rows.
        int colGap   = 24;
        int colWidth = (ClientSize.Width - pad * 2 - colGap) / 2;
        int rowH     = 34;
        int colStartY = y;

        for (int i = 0; i < ColorFields.Length; i++)
        {
            int col = i / 10;
            int row = i % 10;
            int x = pad + col * (colWidth + colGap);
            int ry = colStartY + row * rowH;
            var (key, label) = ColorFields[i];
            AddColorRow(key, label, GetCurrent(src, key), x, ry, colWidth);
        }
        y = colStartY + 10 * rowH + 6;

        _validationLabel = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.ErrorColor,
            AutoSize = true,
            MaximumSize = new Size(ClientSize.Width - pad * 2, 0),
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        Controls.Add(_validationLabel);
        y += 30;

        var saveBtn = new RoundedButton
        {
            Text = isEdit ? "Save" : "Create",
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            Size = new Size(120, 36),
            Location = new Point(ClientSize.Width - pad - 120, y),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.Primary,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
        };
        saveBtn.FlatAppearance.BorderSize = 0;
        saveBtn.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        saveBtn.Click += (_, _) => TrySave();
        Controls.Add(saveBtn);

        var cancelBtn = new RoundedButton
        {
            Text = "Cancel",
            Font = new Font("Segoe UI", 9.5f),
            Size = new Size(90, 36),
            Location = new Point(ClientSize.Width - pad - 120 - 10 - 90, y),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextSecondary,
            Cursor = Cursors.Hand,
        };
        cancelBtn.FlatAppearance.BorderSize = 0;
        cancelBtn.FlatAppearance.MouseOverBackColor = theme.Primary;
        cancelBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(cancelBtn);

        if (isEdit)
        {
            var deleteBtn = new RoundedButton
            {
                Text = "Delete",
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                Size = new Size(90, 36),
                Location = new Point(pad, y),
                FlatStyle = FlatStyle.Flat,
                BackColor = theme.ErrorBg,
                ForeColor = theme.ErrorColor,
                Cursor = Cursors.Hand,
            };
            deleteBtn.FlatAppearance.BorderSize = 0;
            deleteBtn.FlatAppearance.MouseOverBackColor = theme.ErrorColor;
            deleteBtn.Click += (_, _) => TryDelete();
            Controls.Add(deleteBtn);
        }

        ClientSize = new Size(ClientSize.Width, y + 36 + pad);
    }

    private void AddColorRow(string key, string label, string initialHex, int x, int y, int rowWidth)
    {
        var lbl = new Label
        {
            Text = label,
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = _theme.TextSecondary,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Size = new Size(110, 26),
            Location = new Point(x, y),
            BackColor = Color.Transparent,
        };
        Controls.Add(lbl);

        var hex = new TextBox
        {
            Font = new Font("Consolas", 10f),
            BackColor = _theme.BgHeader,
            ForeColor = _theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Size = new Size(90, 26),
            Location = new Point(x + 110, y),
            Text = initialHex,
            MaxLength = 7,
        };
        Controls.Add(hex);
        _colorBoxes[key] = hex;

        var swatch = new Panel
        {
            Size = new Size(rowWidth - 110 - 90 - 8, 26),
            Location = new Point(x + 110 + 90 + 4, y),
            BackColor = ParseHexOrDefault(initialHex, Color.Black),
            BorderStyle = BorderStyle.FixedSingle,
            Cursor = Cursors.Hand,
        };
        swatch.Click += (_, _) =>
        {
            using var dlg = new ColorDialog
            {
                Color = ParseHexOrDefault(hex.Text, Color.Black),
                FullOpen = true,
                AnyColor = true,
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                var c = dlg.Color;
                hex.Text = $"#{c.R:x2}{c.G:x2}{c.B:x2}";
            }
        };
        Controls.Add(swatch);
        _colorSwatches[key] = swatch;

        hex.TextChanged += (_, _) =>
        {
            if (TryParseHex(hex.Text, out var c))
                swatch.BackColor = c;
        };
    }

    private void TrySave()
    {
        _validationLabel.Text = "";
        var name = _nameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            _validationLabel.Text = "Scheme name is required.";
            return;
        }
        if (WindowsTerminalProfiles.FindSettingsPath() == null)
        {
            _validationLabel.Text = "Windows Terminal settings.json not found. Is WT installed?";
            return;
        }

        // Validate every hex value before writing anything.
        foreach (var (key, label) in ColorFields)
        {
            if (!TryParseHex(_colorBoxes[key].Text, out _))
            {
                _validationLabel.Text = $"\"{label}\" is not a valid #RRGGBB color.";
                return;
            }
        }

        string Get(string k) => NormalizeHex(_colorBoxes[k].Text);
        var draft = new WtSchemeDraft
        {
            Name = name,
            Foreground          = Get("foreground"),
            Background          = Get("background"),
            CursorColor         = Get("cursorColor"),
            SelectionBackground = Get("selectionBackground"),
            Black               = Get("black"),
            Red                 = Get("red"),
            Green               = Get("green"),
            Yellow              = Get("yellow"),
            Blue                = Get("blue"),
            Purple              = Get("purple"),
            Cyan                = Get("cyan"),
            White               = Get("white"),
            BrightBlack         = Get("brightBlack"),
            BrightRed           = Get("brightRed"),
            BrightGreen         = Get("brightGreen"),
            BrightYellow        = Get("brightYellow"),
            BrightBlue          = Get("brightBlue"),
            BrightPurple        = Get("brightPurple"),
            BrightCyan          = Get("brightCyan"),
            BrightWhite         = Get("brightWhite"),
        };

        try
        {
            if (_originalName != null)
            {
                WindowsTerminalProfiles.UpdateScheme(_originalName, draft);
                OwnedWtSchemesStore.Rename(_originalName, draft.Name);
            }
            else
            {
                WindowsTerminalProfiles.AppendScheme(draft);
                OwnedWtSchemesStore.Add(draft.Name);
            }
            ResultSchemeName = draft.Name;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _validationLabel.Text = $"Write failed: {ex.Message}";
        }
    }

    private void TryDelete()
    {
        if (_originalName == null) return;
        var res = MessageBox.Show(this,
            $"Delete the color scheme \"{_originalName}\"?\n\nThis removes the entry from settings.json.",
            "Confirm Delete",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (res != DialogResult.Yes) return;
        try
        {
            WindowsTerminalProfiles.DeleteScheme(_originalName);
            OwnedWtSchemesStore.Remove(_originalName);
            ResultSchemeName = _originalName;
            Deleted = true;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _validationLabel.Text = $"Delete failed: {ex.Message}";
        }
    }

    private void AddSection(string text, int y, int sepWidth)
    {
        var hdr = new Label
        {
            Text = text,
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
            ForeColor = _theme.Primary,
            AutoSize = true,
            Location = new Point(20, y),
            BackColor = Color.Transparent,
        };
        Controls.Add(hdr);
        var rule = new Panel
        {
            BackColor = _theme.Border,
            Location = new Point(20, y + 22),
            Size = new Size(sepWidth, 1),
        };
        Controls.Add(rule);
    }

    private static string GetCurrent(WtSchemeDraft s, string key) => key switch
    {
        "foreground"          => s.Foreground,
        "background"          => s.Background,
        "cursorColor"         => s.CursorColor,
        "selectionBackground" => s.SelectionBackground,
        "black"               => s.Black,
        "red"                 => s.Red,
        "green"               => s.Green,
        "yellow"              => s.Yellow,
        "blue"                => s.Blue,
        "purple"              => s.Purple,
        "cyan"                => s.Cyan,
        "white"               => s.White,
        "brightBlack"         => s.BrightBlack,
        "brightRed"           => s.BrightRed,
        "brightGreen"         => s.BrightGreen,
        "brightYellow"        => s.BrightYellow,
        "brightBlue"          => s.BrightBlue,
        "brightPurple"        => s.BrightPurple,
        "brightCyan"          => s.BrightCyan,
        "brightWhite"         => s.BrightWhite,
        _                     => "#000000",
    };

    private static bool TryParseHex(string? s, out Color c)
    {
        c = Color.Black;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.StartsWith("#")) s = s.Substring(1);
        if (s.Length == 3)
        {
            // Expand #rgb → #rrggbb
            s = string.Concat(s.Select(ch => new string(ch, 2)));
        }
        if (s.Length != 6) return false;
        if (!int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        c = Color.FromArgb((v >> 16) & 0xff, (v >> 8) & 0xff, v & 0xff);
        return true;
    }

    private static Color ParseHexOrDefault(string? s, Color fallback)
        => TryParseHex(s, out var c) ? c : fallback;

    private static string NormalizeHex(string s)
        => TryParseHex(s, out var c) ? $"#{c.R:x2}{c.G:x2}{c.B:x2}" : s.Trim();
}
