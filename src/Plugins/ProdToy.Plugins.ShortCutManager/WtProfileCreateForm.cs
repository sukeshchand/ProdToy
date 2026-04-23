using System.Drawing;
using System.Drawing.Text;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Modal form for creating a new — or editing an existing — Windows Terminal
/// profile. Only non-empty fields get serialized into settings.json, so the
/// resulting profile stays minimal and defers to WT defaults for anything the
/// user left blank.
/// </summary>
class WtProfileCreateForm : Form
{
    private readonly PluginTheme _theme;
    private readonly string? _originalName;

    private readonly TextBox _nameBox;
    private readonly ComboBox _shellCombo;
    private readonly ComboBox _schemeCombo;
    private readonly ComboBox _fontFaceCombo;
    private readonly NumericUpDown _fontSize;
    private readonly TextBox _iconBox;
    private readonly TrackBar _opacity;
    private readonly Label _opacityVal;
    private readonly ComboBox _cursorCombo;
    private readonly TextBox _startingDirBox;
    private readonly Label _validationLabel;
    private readonly RoundedButton _editSchemeBtn = null!;
    private readonly RoundedButton _delSchemeBtn = null!;

    /// <summary>Name of the profile that was created or edited (null if cancelled).</summary>
    public string? ResultProfileName { get; private set; }
    /// <summary>True if the user clicked Delete (and confirmed) instead of Save.</summary>
    public bool Deleted { get; private set; }

    public WtProfileCreateForm(PluginTheme theme, WtProfileDraft? existing = null)
    {
        _theme = theme;
        _originalName = existing?.Name;
        bool isEdit = existing != null;

        Text = isEdit ? "Edit Windows Terminal Profile" : "New Windows Terminal Profile";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(600, 660);
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;

        int pad = 20;
        int y = pad;
        int labelW = 130;
        int inputX = pad + labelW;
        int inputW = ClientSize.Width - inputX - pad;

        var header = new Label
        {
            Text = isEdit ? "Edit Windows Terminal Profile" : "New Windows Terminal Profile",
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
                ? "Updates your existing entry in Windows Terminal's settings.json."
                : "Writes a new entry into Windows Terminal's settings.json.\nYou can further customize it later in WT Settings.",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        Controls.Add(note);
        y += isEdit ? 26 : 40;

        y = AddSection("GENERAL", y);

        AddLabel("Name", pad, y);
        _nameBox = MakeTextBox(inputX, y, inputW);
        _nameBox.Text = existing?.Name ?? "";
        y += 34;

        AddLabel("Shell", pad, y);
        _shellCombo = MakeCombo(inputX, y, inputW, editable: true);
        foreach (var s in new[]
        {
            "",
            "cmd.exe",
            @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe",
            "pwsh.exe",
            @"%PROGRAMFILES%\Git\bin\bash.exe",
            "wsl.exe",
            "wsl.exe -d Ubuntu",
        }) _shellCombo.Items.Add(s);
        _shellCombo.Text = existing?.Commandline ?? "cmd.exe";
        y += 34;

        AddLabel("Starting folder", pad, y);
        _startingDirBox = MakeTextBox(inputX, y, inputW);
        _startingDirBox.Text = existing?.StartingDirectory ?? "";
        y += 34;

        y = AddSection("APPEARANCE", y);

        AddLabel("Color scheme", pad, y);
        _schemeCombo = MakeCombo(inputX, y, 240, editable: true);
        ReloadSchemeDropdown(selectName: existing?.ColorScheme ?? "Campbell");

        var newSchemeBtn = MakeSchemeActionBtn(theme, "+", inputX + 248, y);
        newSchemeBtn.Click += (_, _) => OpenCreateScheme();
        Controls.Add(newSchemeBtn);

        _editSchemeBtn = MakeSchemeActionBtn(theme, "✎", inputX + 248 + 36, y);
        _editSchemeBtn.Click += (_, _) => OpenEditScheme();
        Controls.Add(_editSchemeBtn);

        _delSchemeBtn = MakeSchemeActionBtn(theme, "🗑", inputX + 248 + 72, y);
        _delSchemeBtn.Font = new Font("Segoe UI Emoji", 11f, FontStyle.Regular);
        _delSchemeBtn.ForeColor = theme.ErrorColor;
        _delSchemeBtn.Click += (_, _) => DeleteSelectedScheme();
        Controls.Add(_delSchemeBtn);

        _schemeCombo.TextChanged         += (_, _) => RefreshSchemeButtons();
        _schemeCombo.SelectedIndexChanged += (_, _) => RefreshSchemeButtons();
        RefreshSchemeButtons();

        y += 34;

        AddLabel("Font face", pad, y);
        _fontFaceCombo = MakeCombo(inputX, y, 240, editable: true);
        PopulateFontFaces(showAll: false);
        _fontFaceCombo.Text = existing?.FontFace ?? "Cascadia Mono";

        AddLabel("Size", inputX + 256, y);
        _fontSize = new NumericUpDown
        {
            Minimum = 6, Maximum = 48,
            Value = existing?.FontSize is int fs ? Math.Clamp(fs, 6, 48) : 12,
            Font = new Font("Segoe UI", 10f),
            BackColor = theme.BgHeader, ForeColor = theme.TextPrimary,
            Size = new Size(60, 26),
            Location = new Point(inputX + 296, y),
        };
        Controls.Add(_fontSize);
        y += 30;

        var showAllFontsCheck = new CheckBox
        {
            Text = "Show all fonts (include non-monospace)",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(inputX, y),
        };
        showAllFontsCheck.CheckedChanged += (_, _) =>
        {
            var current = _fontFaceCombo.Text;
            PopulateFontFaces(showAll: showAllFontsCheck.Checked);
            _fontFaceCombo.Text = current;
        };
        Controls.Add(showAllFontsCheck);
        y += 28;

        var previewLabel = new Label
        {
            Text = "The quick brown fox jumps over the lazy dog — 0123 {} => != #!",
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Size = new Size(inputW, 56),
            Location = new Point(inputX, y),
        };
        Controls.Add(previewLabel);

        void UpdatePreview()
        {
            var face = string.IsNullOrWhiteSpace(_fontFaceCombo.Text) ? "Segoe UI" : _fontFaceCombo.Text.Trim();
            var size = (float)_fontSize.Value;
            var old = previewLabel.Font;
            try { previewLabel.Font = new Font(face, size, FontStyle.Regular, GraphicsUnit.Point); }
            catch { previewLabel.Font = new Font("Segoe UI", size); }
            old?.Dispose();
        }
        _fontFaceCombo.TextChanged += (_, _) => UpdatePreview();
        _fontFaceCombo.SelectedIndexChanged += (_, _) => UpdatePreview();
        _fontSize.ValueChanged += (_, _) => UpdatePreview();
        UpdatePreview();
        y += 64;

        AddLabel("Cursor", pad, y);
        _cursorCombo = MakeCombo(inputX, y, 160);
        foreach (var c in new[] { "bar", "underscore", "filledBox", "emptyBox", "vintage", "doubleUnderscore" })
            _cursorCombo.Items.Add(c);
        _cursorCombo.Text = string.IsNullOrWhiteSpace(existing?.CursorShape) ? "bar" : existing!.CursorShape;
        y += 34;

        AddLabel("Opacity", pad, y);
        _opacity = new TrackBar
        {
            Minimum = 20, Maximum = 100,
            Value = Math.Clamp(existing?.OpacityPercent ?? 100, 20, 100),
            TickFrequency = 10, TickStyle = TickStyle.BottomRight,
            Size = new Size(inputW - 60, 40),
            Location = new Point(inputX, y - 4),
            BackColor = theme.BgDark,
        };
        _opacity.ValueChanged += (_, _) => _opacityVal!.Text = $"{_opacity.Value}%";
        Controls.Add(_opacity);
        _opacityVal = new Label
        {
            Text = $"{_opacity.Value}%",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(inputX + inputW - 48, y + 6),
            BackColor = Color.Transparent,
        };
        Controls.Add(_opacityVal);
        y += 42;

        AddLabel("Icon", pad, y);
        _iconBox = MakeTextBox(inputX, y, inputW - 90);
        _iconBox.Text = existing?.Icon ?? "";
        var browseIcon = new RoundedButton
        {
            Text = "Browse…",
            Font = new Font("Segoe UI", 9f),
            Size = new Size(80, 26),
            Location = new Point(inputX + inputW - 84, y),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextPrimary,
            Cursor = Cursors.Hand,
        };
        browseIcon.FlatAppearance.BorderSize = 0;
        browseIcon.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        browseIcon.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Pick an icon",
                Filter = "Image files (*.png;*.ico;*.jpg;*.svg)|*.png;*.ico;*.jpg;*.svg|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _iconBox.Text = dlg.FileName;
        };
        Controls.Add(browseIcon);
        y += 34;

        var iconHint = new Label
        {
            Text = "Path to an image file, or an emoji (e.g. 🐚, ⚡)",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(inputX, y),
            BackColor = Color.Transparent,
        };
        Controls.Add(iconHint);
        y += 24;

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

    private void TrySave()
    {
        _validationLabel.Text = "";
        if (string.IsNullOrWhiteSpace(_nameBox.Text))
        {
            _validationLabel.Text = "Name is required.";
            return;
        }
        if (WindowsTerminalProfiles.FindSettingsPath() == null)
        {
            _validationLabel.Text = "Windows Terminal settings.json not found. Is WT installed?";
            return;
        }

        var draft = new WtProfileDraft
        {
            Name = _nameBox.Text.Trim(),
            Commandline = _shellCombo.Text.Trim(),
            ColorScheme = _schemeCombo.Text.Trim(),
            FontFace = _fontFaceCombo.Text.Trim(),
            FontSize = (int)_fontSize.Value,
            Icon = _iconBox.Text.Trim(),
            OpacityPercent = _opacity.Value,
            CursorShape = _cursorCombo.Text.Trim(),
            StartingDirectory = _startingDirBox.Text.Trim(),
        };

        try
        {
            if (_originalName != null)
            {
                WindowsTerminalProfiles.UpdateProfile(_originalName, draft);
                OwnedWtProfilesStore.Rename(_originalName, draft.Name);
            }
            else
            {
                WindowsTerminalProfiles.AppendProfile(draft);
                OwnedWtProfilesStore.Add(draft.Name);
            }
            ResultProfileName = draft.Name;
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
            $"Delete the Windows Terminal profile \"{_originalName}\"?\n\nThis removes the entry from settings.json.",
            "Confirm Delete",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (res != DialogResult.Yes) return;
        try
        {
            WindowsTerminalProfiles.DeleteProfile(_originalName);
            OwnedWtProfilesStore.Remove(_originalName);
            ResultProfileName = _originalName;
            Deleted = true;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _validationLabel.Text = $"Delete failed: {ex.Message}";
        }
    }

    private int AddSection(string text, int y)
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
            Size = new Size(ClientSize.Width - 40, 1),
        };
        Controls.Add(rule);
        return y + 32;
    }

    private Label AddLabel(string text, int x, int y)
    {
        var lbl = new Label
        {
            Text = text,
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = _theme.TextSecondary,
            AutoSize = true,
            Location = new Point(x, y + 4),
            BackColor = Color.Transparent,
        };
        Controls.Add(lbl);
        return lbl;
    }

    private TextBox MakeTextBox(int x, int y, int w)
    {
        var tb = new TextBox
        {
            Font = new Font("Segoe UI", 10f),
            BackColor = _theme.BgHeader,
            ForeColor = _theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Size = new Size(w, 26),
            Location = new Point(x, y),
        };
        Controls.Add(tb);
        return tb;
    }

    private static RoundedButton MakeSchemeActionBtn(PluginTheme theme, string glyph, int x, int y)
    {
        var b = new RoundedButton
        {
            Text = glyph,
            Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
            Size = new Size(32, 26),
            Location = new Point(x, y),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextPrimary,
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        return b;
    }

    private void ReloadSchemeDropdown(string? selectName = null)
    {
        var current = selectName ?? _schemeCombo.Text;
        _schemeCombo.BeginUpdate();
        _schemeCombo.Items.Clear();
        foreach (var s in WindowsTerminalProfiles.DiscoverSchemes())
            _schemeCombo.Items.Add(s);
        _schemeCombo.EndUpdate();
        _schemeCombo.Text = current ?? "";
    }

    private void RefreshSchemeButtons()
    {
        var name = _schemeCombo.Text?.Trim() ?? "";
        var owned = !string.IsNullOrEmpty(name) && OwnedWtSchemesStore.IsOwned(name);
        _editSchemeBtn.Visible = owned;
        _delSchemeBtn.Visible = owned;
    }

    private void OpenCreateScheme()
    {
        using var dlg = new WtSchemeEditForm(_theme);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (string.IsNullOrWhiteSpace(dlg.ResultSchemeName)) return;
        ReloadSchemeDropdown(selectName: dlg.ResultSchemeName);
        RefreshSchemeButtons();
    }

    private void OpenEditScheme()
    {
        var current = _schemeCombo.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(current) || !OwnedWtSchemesStore.IsOwned(current)) return;

        var existing = WindowsTerminalProfiles.ReadScheme(current);
        if (existing == null)
        {
            MessageBox.Show(this,
                $"Couldn't find color scheme \"{current}\" in settings.json.",
                "Edit scheme", MessageBoxButtons.OK, MessageBoxIcon.Information);
            OwnedWtSchemesStore.Remove(current);
            ReloadSchemeDropdown();
            RefreshSchemeButtons();
            return;
        }

        using var dlg = new WtSchemeEditForm(_theme, existing);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        if (dlg.Deleted)
        {
            ReloadSchemeDropdown(selectName: "Campbell");
        }
        else if (!string.IsNullOrWhiteSpace(dlg.ResultSchemeName))
        {
            ReloadSchemeDropdown(selectName: dlg.ResultSchemeName);
        }
        RefreshSchemeButtons();
    }

    private void DeleteSelectedScheme()
    {
        var current = _schemeCombo.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(current) || !OwnedWtSchemesStore.IsOwned(current)) return;

        var res = MessageBox.Show(this,
            $"Delete the color scheme \"{current}\"?\n\nThis removes the entry from settings.json.",
            "Confirm Delete",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (res != DialogResult.Yes) return;

        try
        {
            WindowsTerminalProfiles.DeleteScheme(current);
            OwnedWtSchemesStore.Remove(current);
            ReloadSchemeDropdown(selectName: "Campbell");
            RefreshSchemeButtons();
        }
        catch (Exception ex)
        {
            _validationLabel.Text = $"Delete failed: {ex.Message}";
        }
    }

    private static readonly string[] CuratedMonoFonts =
    {
        "Cascadia Mono", "Cascadia Code", "Consolas", "JetBrains Mono",
        "Fira Code", "Source Code Pro", "Courier New", "Lucida Console",
    };

    private static List<string>? _cachedAllFonts;
    private static List<string>? _cachedMonoFonts;

    private void PopulateFontFaces(bool showAll)
    {
        _fontFaceCombo.BeginUpdate();
        _fontFaceCombo.Items.Clear();
        foreach (var f in showAll ? GetAllFonts() : GetMonospaceFonts())
            _fontFaceCombo.Items.Add(f);
        _fontFaceCombo.EndUpdate();
    }

    private static List<string> GetAllFonts()
    {
        if (_cachedAllFonts != null) return _cachedAllFonts;
        using var col = new InstalledFontCollection();
        _cachedAllFonts = col.Families.Select(f => f.Name).OrderBy(n => n).ToList();
        return _cachedAllFonts;
    }

    private static List<string> GetMonospaceFonts()
    {
        if (_cachedMonoFonts != null) return _cachedMonoFonts;
        using var col = new InstalledFontCollection();
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        var detected = new List<string>();
        foreach (var fam in col.Families)
        {
            if (!fam.IsStyleAvailable(FontStyle.Regular)) continue;
            try
            {
                using var font = new Font(fam, 12f, FontStyle.Regular);
                var wi = g.MeasureString("iiiii", font).Width;
                var ww = g.MeasureString("WWWWW", font).Width;
                if (Math.Abs(wi - ww) < 0.5f) detected.Add(fam.Name);
            }
            catch { }
        }
        // Merge curated list first (so preferred fonts sort to the top even if not installed yet),
        // then alphabetised detected monospace fonts.
        var result = new List<string>();
        foreach (var f in CuratedMonoFonts)
            if (!result.Contains(f, StringComparer.OrdinalIgnoreCase)) result.Add(f);
        foreach (var f in detected.OrderBy(n => n))
            if (!result.Contains(f, StringComparer.OrdinalIgnoreCase)) result.Add(f);
        _cachedMonoFonts = result;
        return _cachedMonoFonts;
    }

    private ComboBox MakeCombo(int x, int y, int w, bool editable = false)
    {
        var cb = new ComboBox
        {
            DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList,
            Font = new Font("Segoe UI", 10f),
            BackColor = _theme.BgHeader,
            ForeColor = _theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(w, 26),
            Location = new Point(x, y),
        };
        Controls.Add(cb);
        return cb;
    }
}
