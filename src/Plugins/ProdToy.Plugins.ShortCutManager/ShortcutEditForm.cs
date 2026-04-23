using System.Drawing;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

class ShortcutEditForm : Form
{
    private readonly PluginTheme _theme;
    private readonly Shortcut? _existing;
    private readonly string _folderPath;

    private readonly TextBox _nameBox;
    private readonly TextBox _dirBox;
    private readonly TextBox _argsBox;
    private readonly ComboBox _profileCombo;
    private readonly ComboBox _launchProfileCombo;
    private readonly Label _argsLabel;
    private readonly Label _argsHintLabel;
    private readonly ComboBox _launcherCombo;
    private readonly ComboBox _windowTargetCombo;
    private readonly TextBox _tabGroupBox;
    private readonly TextBox _titleBox;
    private readonly TextBox _sendKeysBox;
    private readonly NumericUpDown _sendKeysDelayBox;
    private readonly ToggleSwitch _adminToggle;
    private readonly TextBox _notesBox;
    private readonly Label _validationLabel;
    private readonly RoundedButton _editProfileBtn;
    private readonly RoundedButton _delProfileBtn;

    public Shortcut? Result { get; private set; }
    public bool DeleteRequested { get; private set; }

    public ShortcutEditForm(PluginTheme theme, Shortcut? existing = null, string? defaultFolder = null)
    {
        _theme = theme;
        _existing = existing;
        // On edit, preserve the shortcut's current folder. On create, take the
        // folder from the tree selection passed by the parent form (required —
        // the parent gates "+ New Shortcut" on a non-root selection).
        _folderPath = ShortcutFolders.Normalize(
            existing?.FolderPath ?? defaultFolder ?? "");
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

        // Folder readout — the shortcut's folder is chosen by tree selection,
        // not editable here. Show "📁 <path>" next to the header so the user
        // can confirm where it'll be saved.
        var folderReadout = new Label
        {
            Text = string.IsNullOrEmpty(_folderPath) ? "📁 (no folder)" : $"📁 {_folderPath}",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(pad + 180, y + 8),
            BackColor = Color.Transparent,
        };
        Controls.Add(folderReadout);
        y += 40;

        // Launch profile sits at the very top — selecting it swaps the
        // command's default args, label, and hint so the edit form works for
        // any CLI (npm, dotnet, vite, …), not just claude.
        y = AddSection("PROFILE", y);

        AddLabel("Launch profile", pad, y);
        _launchProfileCombo = MakeCombo(inputX, y, 240);
        foreach (var p in LaunchProfiles.All)
            _launchProfileCombo.Items.Add(p.DisplayName);
        var initialProfile = LaunchProfiles.GetOrDefault(existing?.Profile);
        _launchProfileCombo.SelectedIndex = Array.FindIndex(
            LaunchProfiles.All, p => p.Id == initialProfile.Id);
        if (_launchProfileCombo.SelectedIndex < 0) _launchProfileCombo.SelectedIndex = 0;
        y += 34;

        y = AddSection("PROJECT", y);

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
            if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath))
                return;
            _dirBox.Text = dlg.SelectedPath;

            string leaf = Path.GetFileName(dlg.SelectedPath.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(leaf)) return;

            // Auto-fill empty fields from the selected folder's leaf name so
            // the common "name/title after the project folder" case is one
            // click. Existing user edits are preserved.
            if (string.IsNullOrWhiteSpace(_nameBox.Text))
                _nameBox.Text = leaf;
            if (string.IsNullOrWhiteSpace(_titleBox.Text))
                _titleBox.Text = leaf;

            // Claude-only: pre-populate the post-launch rename keystrokes so
            // the tab shows the folder name after `claude` has overwritten it
            // with its own title on startup. Other profiles get no default.
            int profileIdx = _launchProfileCombo.SelectedIndex;
            bool isClaude = profileIdx >= 0
                && profileIdx < LaunchProfiles.All.Length
                && LaunchProfiles.All[profileIdx].Id.Equals("claude", StringComparison.OrdinalIgnoreCase);
            if (isClaude && string.IsNullOrWhiteSpace(_sendKeysBox.Text))
                _sendKeysBox.Text = $"/rename {leaf}{{ENTER}}";
        };
        Controls.Add(browseBtn);
        y += 34;

        AddLabel("Name", pad, y);
        _nameBox = MakeTextBox(inputX, y, inputW);
        _nameBox.Text = existing?.Name ?? "";
        y += 34;

        y = AddSection("COMMAND", y);

        _argsLabel = AddLabel($"{initialProfile.DisplayName} args", pad, y);
        _argsBox = MakeTextBox(inputX, y, inputW);
        _argsBox.Text = existing?.Args ?? initialProfile.DefaultArgs;
        y += 34;

        _argsHintLabel = new Label
        {
            Text = initialProfile.ArgsHint,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = false,
            // Reserve height for two wrapped lines; width matches the input.
            Size = new Size(inputW, 32),
            Location = new Point(inputX, y),
            BackColor = Color.Transparent,
        };
        Controls.Add(_argsHintLabel);
        y += 34;

        // Chip panel — clickable token buttons for common flags/subcommands.
        // Rebuilt on profile change. Clicking a chip appends its text to _argsBox.
        var chipPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = false,
            Size = new Size(inputW, 64),
            Location = new Point(inputX, y),
            BackColor = Color.Transparent,
            Padding = new Padding(0),
            Margin = new Padding(0),
        };
        Controls.Add(chipPanel);
        RebuildArgsChips(chipPanel, initialProfile, theme);
        y += 68;

        // When the profile changes, update the label + hint immediately. The
        // args textbox is only overwritten if it's empty or still holds the
        // previous profile's default (so we don't clobber the user's edits).
        string prevDefault = initialProfile.DefaultArgs;
        _launchProfileCombo.SelectedIndexChanged += (_, _) =>
        {
            int idx = _launchProfileCombo.SelectedIndex;
            if (idx < 0 || idx >= LaunchProfiles.All.Length) return;
            var p = LaunchProfiles.All[idx];
            _argsLabel.Text = $"{p.DisplayName} args";
            _argsHintLabel.Text = p.ArgsHint;
            string current = _argsBox.Text ?? "";
            if (string.IsNullOrWhiteSpace(current) || current == prevDefault)
                _argsBox.Text = p.DefaultArgs;
            prevDefault = p.DefaultArgs;
            RebuildArgsChips(chipPanel, p, theme);
        };

        y = AddSection("TERMINAL", y);

        AddLabel("Launcher", pad, y);
        _launcherCombo = MakeCombo(inputX, y, 200);
        _launcherCombo.Items.Add("Windows Terminal");
        _launcherCombo.Items.Add("Plain cmd window");
        _launcherCombo.SelectedIndex = existing?.LauncherMode == LauncherMode.CmdWindow ? 1 : 0;
        y += 34;

        // Only meaningful when launcher = Windows Terminal. Disabled for cmd
        // fallback since cmd.exe can't attach to an existing WT window.
        AddLabel("Open in", pad, y);
        _windowTargetCombo = MakeCombo(inputX, y, 240);
        _windowTargetCombo.Items.Add("New window");
        _windowTargetCombo.Items.Add("Existing window (new tab)");
        // New shortcuts default to "Existing window" (grouping into a shared WT
        // window is the more common case). Edits preserve the saved target.
        _windowTargetCombo.SelectedIndex = existing?.WtWindowTarget == WtWindowTarget.NewWindow ? 0 : 1;

        // Tab group name — routes the tab into a specific named WT window via
        // `-w <name>`. Only relevant when "Existing window" is selected.
        int tabGroupX = inputX + 248;
        int tabGroupW = Math.Max(120, ClientSize.Width - tabGroupX - pad);
        _tabGroupBox = new TextBox
        {
            Font = new Font("Segoe UI", 10f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Size = new Size(tabGroupW, 26),
            Location = new Point(tabGroupX, y),
            PlaceholderText = "Tab group (optional)",
            // Prefill with the first word of the directory's leaf folder name
            // (split on space/dash/underscore/dot) so related shortcuts cluster
            // into the same WT window by default. Users can still clear or edit.
            Text = existing?.WtWindowName ?? FirstWordFromPath(existing?.WorkingDirectory ?? defaultFolder),
        };
        Controls.Add(_tabGroupBox);

        // Keep tab group synced with directory leaf — but only while the user
        // hasn't diverged from our auto-fill. Once they edit the tab group to
        // a different value, we stop overwriting.
        string lastAutoTabGroup = _tabGroupBox.Text;
        _dirBox.TextChanged += (_, _) =>
        {
            if (_tabGroupBox.Text == lastAutoTabGroup)
            {
                var derived = FirstWordFromPath(_dirBox.Text);
                _tabGroupBox.Text = derived;
                lastAutoTabGroup = derived;
            }
        };

        void RefreshWindowTargetEnabled()
        {
            bool launcherIsWt = _launcherCombo.SelectedIndex == 0;
            _windowTargetCombo.Enabled = launcherIsWt;
            // Tab group only makes sense for "Existing window (new tab)".
            _tabGroupBox.Enabled = launcherIsWt && _windowTargetCombo.SelectedIndex == 1;
        }
        _launcherCombo.SelectedIndexChanged += (_, _) => RefreshWindowTargetEnabled();
        _windowTargetCombo.SelectedIndexChanged += (_, _) => RefreshWindowTargetEnabled();
        RefreshWindowTargetEnabled();
        y += 34;

        // Optional custom tab/window title. Empty = "leave it alone".
        AddLabel("Window title", pad, y);
        _titleBox = MakeTextBox(inputX, y, inputW);
        _titleBox.Text = existing?.WindowTitle ?? "";
        y += 30;

        var titleHint = new Label
        {
            Text = "Leave empty to keep the default terminal title. Used for wt --title or cmd title.",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(inputX, y),
            BackColor = Color.Transparent,
        };
        Controls.Add(titleHint);
        y += 26;

        // Optional keystrokes fired with SendKeys after the launch completes.
        // Useful for apps that rewrite the tab title on startup — you can bind
        // your preferred rename shortcut and replay it here.
        AddLabel("Send keys after launch", pad, y);
        _sendKeysBox = MakeTextBox(inputX, y, inputW);
        _sendKeysBox.Text = existing?.PostLaunchSendKeys ?? "";
        y += 30;

        var sendKeysHint = new Label
        {
            Text = "Leave empty to skip. SendKeys syntax: {ENTER}, {TAB}, ^+p = Ctrl+Shift+P, etc.",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(inputX, y),
            BackColor = Color.Transparent,
        };
        Controls.Add(sendKeysHint);
        y += 22;

        AddLabel("Delay (ms)", pad, y);
        _sendKeysDelayBox = new NumericUpDown
        {
            Font = new Font("Segoe UI", 10f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Minimum = 100,
            Maximum = 60_000,
            Increment = 500,
            Value = Math.Clamp(existing?.PostLaunchDelayMs ?? 3000, 100, 60_000),
            Size = new Size(100, 26),
            Location = new Point(inputX, y),
        };
        Controls.Add(_sendKeysDelayBox);
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
        delProfileBtn.Font = new Font("Segoe UI Emoji", 11f, FontStyle.Regular);
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
            ? LauncherMode.CmdWindow
            : LauncherMode.WindowsTerminal;

        Result = new Shortcut
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N"),
            Name = _nameBox.Text.Trim(),
            Profile = (_launchProfileCombo.SelectedIndex >= 0
                && _launchProfileCombo.SelectedIndex < LaunchProfiles.All.Length)
                ? LaunchProfiles.All[_launchProfileCombo.SelectedIndex].Id
                : LaunchProfiles.Default.Id,
            WorkingDirectory = _dirBox.Text.Trim(),
            Args = _argsBox.Text.Trim(),
            WtProfile = _profileCombo.Text.Trim(),
            LauncherMode = launcher,
            WtWindowTarget = _windowTargetCombo.SelectedIndex == 1
                ? WtWindowTarget.ExistingWindow
                : WtWindowTarget.NewWindow,
            WtWindowName = _tabGroupBox.Text.Trim(),
            WindowTitle = _titleBox.Text.Trim(),
            PostLaunchSendKeys = _sendKeysBox.Text,
            PostLaunchDelayMs = (int)_sendKeysDelayBox.Value,
            RequireAdmin = _adminToggle.Checked,
            Notes = _notesBox.Text,
            FolderPath = _folderPath,
            CreatedAt = _existing?.CreatedAt ?? DateTime.Now,
            UpdatedAt = _existing != null ? DateTime.Now : null,
            LastLaunchedAt = _existing?.LastLaunchedAt,
            LaunchCount = _existing?.LaunchCount ?? 0,
        };

        DialogResult = DialogResult.OK;
        Close();
    }

    private void OpenCreateProfile()
    {
        using var dlg = new WtProfileCreateForm(_theme);
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

        using var dlg = new WtProfileCreateForm(_theme, existing);
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

    private static string FirstWordFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var leaf = Path.GetFileName(path.TrimEnd('/', '\\'));
        if (string.IsNullOrWhiteSpace(leaf)) return "";
        var parts = leaf.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : leaf;
    }

    private void RebuildArgsChips(FlowLayoutPanel panel, LaunchProfile profile, PluginTheme theme)
    {
        // Dispose + clear any chips from a previous profile so we don't leak handles.
        foreach (Control c in panel.Controls) c.Dispose();
        panel.Controls.Clear();

        foreach (var token in profile.SuggestedTokens)
        {
            var chip = new RoundedButton
            {
                Text = token,
                Font = new Font("Consolas", 9f),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 6, 6),
                Padding = new Padding(8, 2, 8, 2),
                FlatStyle = FlatStyle.Flat,
                BackColor = theme.PrimaryDim,
                ForeColor = theme.TextPrimary,
                Cursor = Cursors.Hand,
            };
            chip.FlatAppearance.BorderSize = 0;
            chip.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
            string captured = token;
            chip.Click += (_, _) =>
            {
                var current = _argsBox.Text ?? "";
                // Append with a space separator when existing text is non-empty and
                // doesn't already end with whitespace. Avoid duplicate exact tokens.
                if (string.IsNullOrWhiteSpace(current))
                {
                    _argsBox.Text = captured;
                }
                else
                {
                    var existingTokens = current.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (Array.IndexOf(existingTokens, captured) >= 0) return;
                    _argsBox.Text = current.TrimEnd() + " " + captured;
                }
                _argsBox.Focus();
                _argsBox.SelectionStart = _argsBox.Text.Length;
            };
            panel.Controls.Add(chip);
        }
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
