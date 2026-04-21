using System.Drawing;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ClaudeIntegration;

class ClaudeShortcutEditForm : Form
{
    private readonly PluginTheme _theme;
    private readonly ClaudeShortcut? _existing;

    private readonly TextBox _nameBox;
    private readonly TextBox _dirBox;
    private readonly TextBox _argsBox;
    private readonly ComboBox _profileCombo;
    private readonly ComboBox _launcherCombo;
    private readonly ComboBox _folderCombo;
    private readonly ToggleSwitch _adminToggle;
    private readonly TextBox _notesBox;
    private readonly Label _validationLabel;
    private readonly RoundedButton _editProfileBtn;
    private readonly RoundedButton _delProfileBtn;

    public ClaudeShortcut? Result { get; private set; }
    public bool DeleteRequested { get; private set; }

    public ClaudeShortcutEditForm(PluginTheme theme, ClaudeShortcut? existing = null, string? defaultFolder = null)
    {
        _theme = theme;
        _existing = existing;
        bool isEdit = existing != null;

        Text = isEdit ? "Edit Shortcut" : "New Shortcut";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(720, 720);
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;

        int pad = 20;
        int y = pad;
        int labelW = 170;
        int inputX = pad + labelW;
        int inputW = ClientSize.Width - inputX - pad;

        // Header
        var header = new Label
        {
            Text = isEdit ? "Edit Shortcut" : "New Shortcut",
            Font = new Font("Segoe UI Semibold", 14f, FontStyle.Bold),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        Controls.Add(header);
        y += 40;

        y = AddSection("PROJECT", y);

        AddLabel("Name", pad, y);
        _nameBox = MakeTextBox(inputX, y, inputW);
        _nameBox.Text = existing?.Name ?? "";
        y += 34;

        AddLabel("Folder", pad, y);
        _folderCombo = new ComboBox
        {
            Font = new Font("Segoe UI", 10f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(inputW, 26),
            Location = new Point(inputX, y),
            DropDownStyle = ComboBoxStyle.DropDown,
        };
        var knownFolders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in ClaudeShortcutStore.Load())
        {
            var p = ClaudeShortcutFolders.Normalize(s.FolderPath);
            if (!string.IsNullOrEmpty(p)) knownFolders.Add(p);
        }
        foreach (var f in ClaudeShortcutFolders.Load())
            knownFolders.Add(ClaudeShortcutFolders.Normalize(f));
        _folderCombo.Items.Add(""); // root
        foreach (var f in knownFolders) _folderCombo.Items.Add(f);
        _folderCombo.Text = existing != null
            ? ClaudeShortcutFolders.Normalize(existing.FolderPath)
            : (defaultFolder ?? "");
        Controls.Add(_folderCombo);
        y += 34;

        AddLabel("Working directory", pad, y);
        _dirBox = MakeTextBox(inputX, y, inputW - 90);
        _dirBox.Text = existing?.WorkingDirectory ?? "";
        var browseBtn = new RoundedButton
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
        browseBtn.FlatAppearance.BorderSize = 0;
        browseBtn.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        browseBtn.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Select project folder",
                UseDescriptionForTitle = true,
                InitialDirectory = Directory.Exists(_dirBox.Text) ? _dirBox.Text : "",
            };
            if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
                _dirBox.Text = dlg.SelectedPath;
        };
        Controls.Add(browseBtn);
        y += 34;

        y = AddSection("CLAUDE", y);

        AddLabel("Claude args", pad, y);
        _argsBox = MakeTextBox(inputX, y, inputW);
        _argsBox.Text = existing?.ClaudeArgs ?? "--dangerously-skip-permissions --continue";
        y += 34;

        var argsHint = new Label
        {
            Text = "Appended to `claude` — e.g. --continue, --model, -p, --dangerously-skip-permissions",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(inputX, y),
            BackColor = Color.Transparent,
        };
        Controls.Add(argsHint);
        y += 22;

        y = AddSection("TERMINAL", y);

        AddLabel("Launcher", pad, y);
        _launcherCombo = MakeCombo(inputX, y, 200);
        _launcherCombo.Items.Add("Windows Terminal");
        _launcherCombo.Items.Add("Plain cmd window");
        _launcherCombo.SelectedIndex = existing?.LauncherMode == ClaudeLauncherMode.CmdWindow ? 1 : 0;
        y += 34;

        AddLabel("WT profile", pad, y);
        _profileCombo = new ComboBox
        {
            Font = new Font("Segoe UI", 10f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(320, 26),
            Location = new Point(inputX, y),
            DropDownStyle = ComboBoxStyle.DropDown,
        };
        foreach (var p in WindowsTerminalProfiles.Discover())
            _profileCombo.Items.Add(p);
        _profileCombo.Text = existing?.WtProfile ?? "Command Prompt";
        Controls.Add(_profileCombo);

        var addProfileBtn = MakeProfileActionBtn(theme, "+", inputX + 328, y);
        addProfileBtn.Click += (_, _) => OpenCreateProfile();
        Controls.Add(addProfileBtn);

        var editProfileBtn = MakeProfileActionBtn(theme, "✎", inputX + 328 + 36, y);
        editProfileBtn.Click += (_, _) => OpenEditProfile();
        Controls.Add(editProfileBtn);
        _editProfileBtn = editProfileBtn;

        var delProfileBtn = MakeProfileActionBtn(theme, "🗑", inputX + 328 + 72, y);
        delProfileBtn.ForeColor = theme.ErrorColor;
        delProfileBtn.Click += (_, _) => DeleteSelectedProfile();
        Controls.Add(delProfileBtn);
        _delProfileBtn = delProfileBtn;

        // Edit/Delete are hidden entirely for stock profiles — they only appear
        // when the selected name is a custom profile we created (tracked in
        // OwnedWtProfilesStore). This keeps the control row tidy and prevents
        // accidental clicks on disabled buttons.
        void RefreshProfileButtons()
        {
            bool owned = OwnedWtProfilesStore.IsOwned(_profileCombo.Text?.Trim() ?? "");
            _editProfileBtn.Visible = owned;
            _delProfileBtn.Visible = owned;
        }
        _profileCombo.TextChanged += (_, _) => RefreshProfileButtons();
        _profileCombo.SelectedIndexChanged += (_, _) => RefreshProfileButtons();
        RefreshProfileButtons();
        y += 30;

        var profileHint = new Label
        {
            Text = "Pick an existing profile, type a custom name, or click + to create / ✎ edit / 🗑 delete",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(inputX, y),
            BackColor = Color.Transparent,
        };
        Controls.Add(profileHint);
        y += 22;

        // Require admin toggle
        var adminCaption = new Label
        {
            Text = "Require administrator",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, y + 4),
            BackColor = Color.Transparent,
        };
        Controls.Add(adminCaption);
        _adminToggle = new ToggleSwitch(theme)
        {
            Checked = existing?.RequireAdmin ?? false,
            Location = new Point(inputX, y + 2),
        };
        Controls.Add(_adminToggle);
        var adminHint = new Label
        {
            Text = "Elevate via UAC prompt on launch",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(inputX + _adminToggle.Width + 12, y + 6),
            BackColor = Color.Transparent,
        };
        Controls.Add(adminHint);
        y += 38;

        y = AddSection("NOTES", y);

        _notesBox = MakeTextBox(pad, y, ClientSize.Width - pad * 2, multiline: true, height: 60);
        _notesBox.Text = existing?.Notes ?? "";
        y += 68;

        _validationLabel = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.ErrorColor,
            AutoSize = true,
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        Controls.Add(_validationLabel);
        y += 22;

        // Buttons
        var saveBtn = new RoundedButton
        {
            Text = isEdit ? "Save" : "Add Shortcut",
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            Size = new Size(140, 36),
            Location = new Point(ClientSize.Width - pad - 140, y),
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
            Location = new Point(ClientSize.Width - pad - 140 - 10 - 90, y),
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

    private TextBox MakeTextBox(int x, int y, int w, bool multiline = false, int height = 26)
    {
        var tb = new TextBox
        {
            Font = new Font("Segoe UI", 10f),
            BackColor = _theme.BgHeader,
            ForeColor = _theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Multiline = multiline,
            Size = new Size(w, height),
            Location = new Point(x, y),
        };
        Controls.Add(tb);
        return tb;
    }

    private ComboBox MakeCombo(int x, int y, int w)
    {
        var cb = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
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

    private void TrySave()
    {
        if (string.IsNullOrWhiteSpace(_nameBox.Text))
        {
            _validationLabel.Text = "Name is required.";
            return;
        }
        var folderNormalized = ClaudeShortcutFolders.Normalize(_folderCombo.Text);
        if (string.IsNullOrEmpty(folderNormalized))
        {
            _validationLabel.Text = "Folder is required — pick an existing one or type a new path.";
            _folderCombo.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(_dirBox.Text))
        {
            _validationLabel.Text = "Working directory is required.";
            return;
        }
        if (!Directory.Exists(_dirBox.Text.Trim()))
        {
            _validationLabel.Text = "Working directory doesn't exist.";
            return;
        }

        var launcher = _launcherCombo.SelectedIndex == 1
            ? ClaudeLauncherMode.CmdWindow
            : ClaudeLauncherMode.WindowsTerminal;

        var normalizedFolder = ClaudeShortcutFolders.Normalize(_folderCombo.Text);

        Result = new ClaudeShortcut
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N"),
            Name = _nameBox.Text.Trim(),
            WorkingDirectory = _dirBox.Text.Trim(),
            ClaudeArgs = _argsBox.Text.Trim(),
            WtProfile = _profileCombo.Text.Trim(),
            LauncherMode = launcher,
            RequireAdmin = _adminToggle.Checked,
            Notes = _notesBox.Text,
            FolderPath = normalizedFolder,
            CreatedAt = _existing?.CreatedAt ?? DateTime.Now,
            UpdatedAt = _existing != null ? DateTime.Now : null,
            LastLaunchedAt = _existing?.LastLaunchedAt,
            LaunchCount = _existing?.LaunchCount ?? 0,
        };

        // Ensure the folder sticks even if the user types a new path.
        if (!string.IsNullOrEmpty(normalizedFolder))
            ClaudeShortcutFolders.Add(normalizedFolder);

        DialogResult = DialogResult.OK;
        Close();
    }

    private void OpenCreateProfile()
    {
        using var dlg = new ClaudeWtProfileCreateForm(_theme);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (string.IsNullOrWhiteSpace(dlg.ResultProfileName)) return;
        ReloadProfileDropdown(selectName: dlg.ResultProfileName);
    }

    private void OpenEditProfile()
    {
        var current = _profileCombo.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(current) || !OwnedWtProfilesStore.IsOwned(current)) return;

        var existing = WindowsTerminalProfiles.ReadProfile(current);
        if (existing == null)
        {
            MessageBox.Show(this,
                $"Couldn't find profile \"{current}\" in settings.json. It may have been deleted outside ProdToy.",
                "Edit profile", MessageBoxButtons.OK, MessageBoxIcon.Information);
            OwnedWtProfilesStore.Remove(current);
            ReloadProfileDropdown();
            return;
        }

        using var dlg = new ClaudeWtProfileCreateForm(_theme, existing);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        if (dlg.Deleted)
        {
            ReloadProfileDropdown(selectName: "");
        }
        else if (!string.IsNullOrWhiteSpace(dlg.ResultProfileName))
        {
            ReloadProfileDropdown(selectName: dlg.ResultProfileName);
        }
    }

    private void DeleteSelectedProfile()
    {
        var current = _profileCombo.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(current) || !OwnedWtProfilesStore.IsOwned(current)) return;

        var res = MessageBox.Show(this,
            $"Delete the Windows Terminal profile \"{current}\"?\n\nThis removes the entry from settings.json.",
            "Confirm Delete",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (res != DialogResult.Yes) return;

        try
        {
            WindowsTerminalProfiles.DeleteProfile(current);
            OwnedWtProfilesStore.Remove(current);
            ReloadProfileDropdown(selectName: "");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Delete failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ReloadProfileDropdown(string? selectName = null)
    {
        var prev = selectName ?? _profileCombo.Text;
        _profileCombo.Items.Clear();
        foreach (var p in WindowsTerminalProfiles.Discover())
            _profileCombo.Items.Add(p);
        _profileCombo.Text = prev ?? "";
    }

    private static RoundedButton MakeProfileActionBtn(PluginTheme theme, string glyph, int x, int y)
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

    private void TryDelete()
    {
        if (_existing == null) return;
        var res = MessageBox.Show(this, "Delete this shortcut?", "Confirm Delete",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (res != DialogResult.Yes) return;
        DeleteRequested = true;
        DialogResult = DialogResult.OK;
        Close();
    }
}
