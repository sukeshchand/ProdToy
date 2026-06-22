using System.Drawing;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

class ShortcutEditForm : Form
{
    private readonly PluginTheme _theme;
    private readonly Shortcut? _existing;
    private readonly string _folderPath;

    // Layout target for the AddSection/AddLabel/MakeTextBox/MakeCombo helpers.
    // Set to the TabPage currently being populated so the same helpers can
    // build any tab. _pageW is that page's usable content width.
    private Control _host = null!;
    private int _pageW;

    private readonly TextBox _nameBox;
    private readonly TextBox _dirBox;
    private readonly TextBox _argsBox;
    private readonly ComboBox _shellCombo;
    private readonly TextBox _setupStepsBox;
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
    private readonly ToggleSwitch _explorerMenuToggle;
    private readonly ToggleSwitch _desktopShortcutToggle;
    private readonly ToggleSwitch _keepRunningToggle;
    private readonly TextBox _desktopShortcutNameBox;
    // True until the user manually edits the desktop name. While true, the
    // box is auto-populated from "<dir-leaf> <profile-display>" whenever the
    // working directory or profile changes — typing into the box flips this
    // off so we don't trample a custom name.
    private bool _desktopNameAutoSync = true;
    private readonly TextBox _statusUrlBox;
    private readonly NumericUpDown _statusTimeoutBox;
    private readonly ToggleSwitch _autoLoginToggle;
    private readonly TextBox _homeUrlBox;
    private readonly TextBox _loginUrlBox;
    private readonly TextBox _loginUsernameBox;
    private readonly TextBox _loginPasswordBox;
    private readonly TextBox _loggedInSelectorBox;
    private readonly Label _loginCaption;
    private readonly Label _homeUrlLabel;
    private readonly Label _loginUrlLabel;
    private readonly Label _loggedInSelectorLabel;
    private readonly Label _loginUsernameLabel;
    private readonly Label _loginPasswordLabel;
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
        Icon = IconHelper.CreateAppIcon(theme.Primary);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(851, 820);
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;

        int pad = 20;
        int labelW = 170;
        int inputX = pad + labelW;

        // Header + folder readout (live on the form, above the tab strip).
        var header = new Label
        {
            Text = isEdit ? "Edit Shortcut" : "New Shortcut",
            Font = new Font("Segoe UI Semibold", 14f, FontStyle.Bold),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, 12),
            BackColor = Color.Transparent,
        };
        Controls.Add(header);

        var folderReadout = new Label
        {
            Text = string.IsNullOrEmpty(_folderPath) ? "📁 (no folder)" : $"📁 {_folderPath}",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(pad + 180, 18),
            BackColor = Color.Transparent,
        };
        Controls.Add(folderReadout);

        // Tab scaffold. Each page scrolls on its own if its content runs long,
        // so the dialog stays a fixed, reasonable height regardless of how many
        // fields a single tab holds.
        const int tabMargin = 12;
        const int tabTop = 44;
        const int buttonArea = 64;
        var tabs = new TabControl
        {
            Location = new Point(tabMargin, tabTop),
            Size = new Size(ClientSize.Width - tabMargin * 2, ClientSize.Height - tabTop - buttonArea),
            BackColor = theme.BgDark,
            DrawMode = TabDrawMode.OwnerDrawFixed,
            SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(165, 28),
        };

        TabPage NewPage(string title) => new TabPage(title)
        {
            BackColor = theme.BgDark,
            ForeColor = theme.TextPrimary,
            AutoScroll = true,
            Padding = new Padding(0),
        };
        var pageGeneral = NewPage("General");
        var pageCmd = NewPage("Command && Terminal");
        var pageIntegration = NewPage("Integration");
        var pageMonitoring = NewPage("Monitoring");
        tabs.TabPages.AddRange(new[] { pageGeneral, pageCmd, pageIntegration, pageMonitoring });

        // Owner-draw the tab headers so they match the dark theme instead of
        // the default light system tab strip.
        tabs.DrawItem += (sender, e) =>
        {
            var tc = (TabControl)sender!;
            if (e.Index < 0 || e.Index >= tc.TabPages.Count) return;
            bool selected = e.Index == tc.SelectedIndex;
            Color bg = selected ? theme.Primary : theme.BgHeader;
            Color fg = selected ? Color.White : theme.TextSecondary;
            using var brush = new SolidBrush(bg);
            e.Graphics.FillRectangle(brush, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tc.TabPages[e.Index].Text,
                new Font("Segoe UI Semibold", 9f), e.Bounds, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        Controls.Add(tabs);

        // All pages share the same size, so one width drives every input.
        // Subtract a little for the vertical scrollbar a long tab may show.
        _pageW = tabs.Width - 8;
        int inputW = _pageW - inputX - pad - 18;
        const int topPad = 12;

        var initialProfile = LaunchProfiles.GetOrDefault(existing?.Profile);

        // ---------------------------------------------------------------- General
        _host = pageGeneral;
        int y = topPad;

        y = AddSection("PROFILE", y);
        AddLabel("Launch profile", pad, y);
        _launchProfileCombo = MakeCombo(inputX, y, 240);
        foreach (var p in LaunchProfiles.All)
            _launchProfileCombo.Items.Add(p.DisplayName);
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

            // Claude-only: pre-populate the args textbox with `/rename <folder>`
            // so the tab shows the folder name after `claude` has overwritten it
            // with its own title on startup. Other profiles get no default.
            int profileIdx = _launchProfileCombo.SelectedIndex;
            bool isClaude = profileIdx >= 0
                && profileIdx < LaunchProfiles.All.Length
                && LaunchProfiles.All[profileIdx].Id.Equals("claude", StringComparison.OrdinalIgnoreCase);
            if (isClaude && string.IsNullOrWhiteSpace(_argsBox.Text))
                _argsBox.Text = $"\"/rename {leaf}\"";
        };
        _host.Controls.Add(browseBtn);
        y += 34;

        AddLabel("Name", pad, y);
        _nameBox = MakeTextBox(inputX, y, inputW);
        _nameBox.Text = existing?.Name ?? "";
        y += 34;

        y = AddSection("NOTES", y);
        _notesBox = MakeTextBox(pad, y, _pageW - pad * 2, multiline: true, height: 90);
        _notesBox.Text = existing?.Notes ?? "";
        y += 98;

        // ------------------------------------------------- Command & Terminal
        _host = pageCmd;
        y = topPad;

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
            Size = new Size(inputW, 32),
            Location = new Point(inputX, y),
            BackColor = Color.Transparent,
        };
        _host.Controls.Add(_argsHintLabel);
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
        _host.Controls.Add(chipPanel);
        RebuildArgsChips(chipPanel, initialProfile, theme);
        // Keep the chip highlight/tick in sync when the args are edited directly.
        _argsBox.TextChanged += (_, _) => RefreshArgsChipStates(chipPanel);
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
            RefreshForUrlKind();
        };

        // "Open URL" profile: keep only the URL field on the Command & Terminal
        // tab (the args box, relabeled) and disable the working directory. The
        // Integration + Monitoring tabs stay — auto-login, Status URL, desktop
        // shortcut, etc. all apply to a URL shortcut too.
        void RefreshForUrlKind()
        {
            bool isUrl = CurrentProfileIsUrl();
            foreach (Control c in pageCmd.Controls)
                c.Visible = !isUrl || c == _argsLabel || c == _argsBox || c == _argsHintLabel;
            if (isUrl)
            {
                _argsLabel.Text = "URL";
                _argsHintLabel.Text = "The URL to open in the in-app preview — e.g. https://localhost:5001";
            }
            _dirBox.Enabled = !isUrl;   // working directory is irrelevant for URL
        }

        // Shell — chooses cmd vs PowerShell for the setup steps + command.
        // Drives both the launcher and the setup-step syntax/chips below.
        AddLabel("Shell", pad, y);
        _shellCombo = MakeCombo(inputX, y, 200);
        _shellCombo.Items.Add("Command Prompt (cmd)");
        _shellCombo.Items.Add("PowerShell");
        _shellCombo.SelectedIndex = existing?.Shell == LaunchShell.PowerShell ? 1 : 0;
        y += 34;

        // Optional setup steps — shell statements run before the command in the
        // same session. Typical use: set env vars the command reads. One
        // statement per line. Syntax follows the Shell selection above.
        AddLabel("Setup steps", pad, y);
        _setupStepsBox = MakeTextBox(inputX, y, inputW, multiline: true, height: 54);
        _setupStepsBox.Text = existing?.SetupSteps ?? "";
        _setupStepsBox.ScrollBars = ScrollBars.Vertical;
        _setupStepsBox.AcceptsReturn = true;
        _setupStepsBox.Font = new Font("Consolas", 9.5f);
        y += 60;

        // Click-to-insert snippet chips — rebuilt when the Shell changes so
        // they show set VAR=value (cmd) or $env:VAR = "value" (PowerShell).
        var setupChipPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = false,
            Size = new Size(inputW, 96),
            Location = new Point(inputX, y),
            BackColor = Color.Transparent,
            Padding = new Padding(0),
            Margin = new Padding(0),
        };
        _host.Controls.Add(setupChipPanel);
        y += 100;

        var setupHint = new Label
        {
            Text = SetupHintFor(_shellCombo.SelectedIndex),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = false,
            Size = new Size(inputW, 32),
            Location = new Point(inputX, y),
            BackColor = Color.Transparent,
        };
        _host.Controls.Add(setupHint);
        y += 36;

        RebuildSetupChips(setupChipPanel, CurrentShell(), theme);
        _shellCombo.SelectedIndexChanged += (_, _) =>
        {
            setupHint.Text = SetupHintFor(_shellCombo.SelectedIndex);
            RebuildSetupChips(setupChipPanel, CurrentShell(), theme);
        };

        y = AddSection("TERMINAL", y);

        AddLabel("Launcher", pad, y);
        _launcherCombo = MakeCombo(inputX, y, 200);
        _launcherCombo.Items.Add("Windows Terminal");
        _launcherCombo.Items.Add("Standalone window");
        _launcherCombo.SelectedIndex = existing?.LauncherMode == LauncherMode.CmdWindow ? 1 : 0;
        y += 34;

        // Only meaningful when launcher = Windows Terminal. Disabled for the
        // standalone window since it can't attach to an existing WT window.
        AddLabel("Open in", pad, y);
        _windowTargetCombo = MakeCombo(inputX, y, 240);
        _windowTargetCombo.Items.Add("New window");
        _windowTargetCombo.Items.Add("Existing window (new tab)");
        _windowTargetCombo.SelectedIndex = existing?.WtWindowTarget == WtWindowTarget.NewWindow ? 0 : 1;

        // Tab group name — routes the tab into a specific named WT window via
        // `-w <name>`. Only relevant when "Existing window" is selected.
        int tabGroupX = inputX + 248;
        int tabGroupW = Math.Max(120, _pageW - tabGroupX - pad);
        _tabGroupBox = new TextBox
        {
            Font = new Font("Segoe UI", 10f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Size = new Size(tabGroupW, 26),
            Location = new Point(tabGroupX, y),
            PlaceholderText = "Tab group (optional)",
            Text = existing?.WtWindowName ?? FirstWordFromPath(existing?.WorkingDirectory ?? defaultFolder),
        };
        _host.Controls.Add(_tabGroupBox);

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
            if (_desktopNameAutoSync)
            {
                _desktopShortcutNameBox.Text = ComputeDefaultDesktopName(_dirBox.Text, _launchProfileCombo.SelectedIndex);
            }
        };

        // Profile change should also refresh the auto-suggested desktop name
        // (only while the user hasn't customized it).
        _launchProfileCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_desktopNameAutoSync)
                _desktopShortcutNameBox.Text = ComputeDefaultDesktopName(_dirBox.Text, _launchProfileCombo.SelectedIndex);
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
            Text = "Leave empty to keep the default terminal title. Names the WT tab, or the standalone window's title bar.",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = false,
            Size = new Size(inputW, 28),
            Location = new Point(inputX, y),
            BackColor = Color.Transparent,
        };
        _host.Controls.Add(titleHint);
        y += 30;

        // Optional keystrokes fired with SendKeys after the launch completes.
        // Useful for apps that rewrite the tab title on startup. The SendKeys
        // textbox and the wait-before-sending numeric share one row.
        AddLabel("Send keys after launch", pad, y);
        const int delayBoxWidth = 100;
        const int delayLabelGap = 8;

        var delayCaption = new Label
        {
            Text = "Wait before sending (ms)",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            BackColor = Color.Transparent,
            Location = new Point(0, y + 4),
        };
        _host.Controls.Add(delayCaption);
        int delayCaptionWidth = delayCaption.PreferredWidth;
        int delayCaptionX = inputX + inputW - delayBoxWidth - delayLabelGap - delayCaptionWidth;
        delayCaption.Left = delayCaptionX;

        int sendKeysWidth = delayCaptionX - inputX - delayLabelGap;
        if (sendKeysWidth < 120) sendKeysWidth = 120;
        _sendKeysBox = MakeTextBox(inputX, y, sendKeysWidth);
        _sendKeysBox.Text = existing?.PostLaunchSendKeys ?? "";

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
            Size = new Size(delayBoxWidth, 26),
            Location = new Point(inputX + inputW - delayBoxWidth, y),
        };
        _host.Controls.Add(_sendKeysDelayBox);
        y += 30;

        var sendKeysHint = new Label
        {
            Text = "Leave empty to skip. SendKeys syntax: {ENTER}, {TAB}, ^+p = Ctrl+Shift+P, etc.",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = false,
            Size = new Size(inputW, 28),
            Location = new Point(inputX, y),
            BackColor = Color.Transparent,
        };
        _host.Controls.Add(sendKeysHint);
        y += 30;

        AddLabel("WT appearance", pad, y);
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
        _host.Controls.Add(_profileCombo);

        var addProfileBtn = MakeProfileActionBtn(theme, "+", inputX + 328, y);
        addProfileBtn.Click += (_, _) => OpenCreateProfile();
        _host.Controls.Add(addProfileBtn);

        var editProfileBtn = MakeProfileActionBtn(theme, "✎", inputX + 328 + 36, y);
        editProfileBtn.Click += (_, _) => OpenEditProfile();
        _host.Controls.Add(editProfileBtn);
        _editProfileBtn = editProfileBtn;

        var delProfileBtn = MakeProfileActionBtn(theme, "🗑", inputX + 328 + 72, y);
        delProfileBtn.Font = new Font("Segoe UI Emoji", 11f, FontStyle.Regular);
        delProfileBtn.ForeColor = theme.ErrorColor;
        delProfileBtn.Click += (_, _) => DeleteSelectedProfile();
        _host.Controls.Add(delProfileBtn);
        _delProfileBtn = delProfileBtn;

        // Edit/Delete are hidden entirely for stock profiles — they only appear
        // when the selected name is a custom profile we created (tracked in
        // OwnedWtProfilesStore).
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
            Text = "Windows Terminal only — sets just the tab's look (colors, font, icon). The shell is the 'Shell' setting above. + create / ✎ edit / 🗑 delete.",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = false,
            Size = new Size(inputW, 32),
            Location = new Point(inputX, y),
            BackColor = Color.Transparent,
        };
        _host.Controls.Add(profileHint);
        y += 32;

        // The WT appearance profile only applies to the Windows Terminal
        // launcher; gray it out (and its +/✎/🗑 buttons) for the standalone
        // window so it's clear it has no effect there.
        void RefreshProfileEnabled()
        {
            bool wt = _launcherCombo.SelectedIndex == 0;
            _profileCombo.Enabled = wt;
            addProfileBtn.Enabled = wt;
            _editProfileBtn.Enabled = wt;
            _delProfileBtn.Enabled = wt;
            profileHint.ForeColor = wt ? theme.TextSecondary : theme.Border;
        }
        _launcherCombo.SelectedIndexChanged += (_, _) => RefreshProfileEnabled();
        RefreshProfileEnabled();

        // ------------------------------------------------------------ Integration
        _host = pageIntegration;
        y = topPad;

        y = AddSection("INTEGRATION", y);

        var keepRunningCaption = new Label
        {
            Text = "Keep running on Stop All",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, y + 4),
            BackColor = Color.Transparent,
        };
        _host.Controls.Add(keepRunningCaption);
        _keepRunningToggle = new ToggleSwitch(theme)
        {
            Checked = existing?.ExcludeFromStopAll ?? false,
            Location = new Point(inputX, y + 2),
        };
        _host.Controls.Add(_keepRunningToggle);
        var keepRunningHint = new Label
        {
            Text = "Consolidated Launcher's “Stop All” skips this (for always-on support tools). The row’s ■ still stops it.",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = false,
            Size = new Size(inputW, 30),
            Location = new Point(inputX + _keepRunningToggle.Width + 12, y + 4),
            BackColor = Color.Transparent,
        };
        _host.Controls.Add(keepRunningHint);
        y += 42;

        var adminCaption = new Label
        {
            Text = "Require administrator",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, y + 4),
            BackColor = Color.Transparent,
        };
        _host.Controls.Add(adminCaption);
        _adminToggle = new ToggleSwitch(theme)
        {
            Checked = existing?.RequireAdmin ?? false,
            Location = new Point(inputX, y + 2),
        };
        _host.Controls.Add(_adminToggle);
        var adminHint = new Label
        {
            Text = "Elevate via UAC prompt on launch",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(inputX + _adminToggle.Width + 12, y + 6),
            BackColor = Color.Transparent,
        };
        _host.Controls.Add(adminHint);
        y += 38;

        var explorerCaption = new Label
        {
            Text = "Show in Explorer right-click",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, y + 4),
            BackColor = Color.Transparent,
        };
        _host.Controls.Add(explorerCaption);
        _explorerMenuToggle = new ToggleSwitch(theme)
        {
            Checked = existing?.ShowInExplorerContextMenu ?? false,
            Location = new Point(inputX, y + 2),
        };
        _host.Controls.Add(_explorerMenuToggle);
        var explorerHint = new Label
        {
            Text = "Adds a “Run …” menu item when right-clicking inside this working directory",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(inputX + _explorerMenuToggle.Width + 12, y + 6),
            BackColor = Color.Transparent,
        };
        _host.Controls.Add(explorerHint);
        y += 38;

        var desktopCaption = new Label
        {
            Text = "Add shortcut to desktop",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, y + 4),
            BackColor = Color.Transparent,
        };
        _host.Controls.Add(desktopCaption);
        _desktopShortcutToggle = new ToggleSwitch(theme)
        {
            Checked = existing?.AddToDesktop ?? false,
            Location = new Point(inputX, y + 2),
        };
        _host.Controls.Add(_desktopShortcutToggle);
        var desktopHint = new Label
        {
            Text = "Creates a desktop .lnk that triggers the same launch action",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(inputX + _desktopShortcutToggle.Width + 12, y + 6),
            BackColor = Color.Transparent,
        };
        _host.Controls.Add(desktopHint);
        y += 36;

        AddLabel("Desktop name", pad, y);
        _desktopShortcutNameBox = MakeTextBox(inputX, y, inputW);
        if (existing != null && !string.IsNullOrEmpty(existing.DesktopShortcutName))
        {
            _desktopShortcutNameBox.Text = existing.DesktopShortcutName;
            _desktopNameAutoSync = false;
        }
        else
        {
            _desktopShortcutNameBox.Text = ComputeDefaultDesktopName(
                existing?.WorkingDirectory ?? "",
                _launchProfileCombo.SelectedIndex);
        }
        _desktopShortcutNameBox.TextChanged += (_, _) =>
        {
            string suggested = ComputeDefaultDesktopName(_dirBox.Text, _launchProfileCombo.SelectedIndex);
            if (!string.Equals(_desktopShortcutNameBox.Text, suggested, StringComparison.Ordinal))
                _desktopNameAutoSync = false;
        };
        y += 34;

        // ------------------------------------------------------------- Monitoring
        _host = pageMonitoring;
        y = topPad;

        y = AddSection("MONITORING", y);

        // Home URL — the page opened in the preview when the app is running, and
        // the page Auto login navigates to. Independent of the Auto login toggle.
        _homeUrlLabel = AddLabel("Home URL", pad, y);
        _homeUrlBox = MakeTextBox(inputX, y, inputW);
        _homeUrlBox.Text = existing?.HomeUrl ?? "";
        y += 28;
        var homeUrlHint = new Label
        {
            Text = "Opened in the preview when the app is running (and the page Auto login signs into). Blank = use the Status URL.",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = false,
            Size = new Size(inputW, 30),
            Location = new Point(inputX, y),
            BackColor = Color.Transparent,
        };
        _host.Controls.Add(homeUrlHint);
        y += 36;

        AddLabel("Status URL", pad, y);
        _statusUrlBox = MakeTextBox(inputX, y, inputW);
        _statusUrlBox.Text = existing?.StatusUrl ?? "";
        y += 30;

        var statusUrlHint = new Label
        {
            Text = "Optional. Polled every 3s for a Healthy / Unreachable badge — used only for the status check, not opened. e.g. http://localhost:5000.",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = false,
            Size = new Size(inputW, 32),
            Location = new Point(inputX, y),
            BackColor = Color.Transparent,
        };
        _host.Controls.Add(statusUrlHint);
        y += 38;

        // Probe timeout — how long each health check waits before marking the
        // row DOWN. Default 5s. Used by the Group Launcher's URL poller.
        AddLabel("Probe timeout (s)", pad, y);
        _statusTimeoutBox = new NumericUpDown
        {
            Font = new Font("Segoe UI", 10f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Minimum = 1,
            Maximum = 120,
            Increment = 1,
            Value = Math.Clamp(existing?.StatusTimeoutSeconds ?? 5, 1, 120),
            Size = new Size(100, 26),
            Location = new Point(inputX, y),
        };
        _host.Controls.Add(_statusTimeoutBox);
        y += 30;

        var timeoutHint = new Label
        {
            Text = "How long each probe waits for a response before showing DOWN. Raise it for slow HTTPS dev servers.",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = false,
            Size = new Size(inputW, 28),
            Location = new Point(inputX, y),
            BackColor = Color.Transparent,
        };
        _host.Controls.Add(timeoutHint);
        y += 32;

        _loginCaption = new Label
        {
            Text = "Auto login",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, y + 4),
            BackColor = Color.Transparent,
        };
        _host.Controls.Add(_loginCaption);
        _autoLoginToggle = new ToggleSwitch(theme)
        {
            Checked = existing?.AutoLoginEnabled ?? false,
            Location = new Point(inputX, y + 2),
        };
        _host.Controls.Add(_autoLoginToggle);
        var loginHint = new Label
        {
            Text = "On launch: open Home URL with cached cookies; if redirected to Login URL, sign in and return.",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(inputX + _autoLoginToggle.Width + 12, y + 6),
            BackColor = Color.Transparent,
        };
        _host.Controls.Add(loginHint);
        y += 36;

        _loginUrlLabel = AddLabel("Login URL", pad, y);
        _loginUrlBox = MakeTextBox(inputX, y, inputW);
        _loginUrlBox.Text = existing?.LoginUrl ?? "";
        y += 28;
        var loginUrlHint = new Label
        {
            Text = "The sign-in page. Blank = detect the login form on whatever page loads after Home URL.",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = false,
            Size = new Size(inputW, 30),
            Location = new Point(inputX, y),
            BackColor = Color.Transparent,
        };
        _host.Controls.Add(loginUrlHint);
        y += 34;

        // Logged-in selector — Playwright locator that's only visible when
        // the user is signed in. Needed for storefronts where the home URL
        // renders for anonymous users too (the URL + visible-password
        // heuristic can't tell the difference there).
        _loggedInSelectorLabel = AddLabel("Logged-in selector", pad, y);
        _loggedInSelectorBox = MakeTextBox(inputX, y, inputW);
        _loggedInSelectorBox.Text = existing?.LoggedInSelector ?? "";
        _loggedInSelectorBox.Font = new Font("Cascadia Mono", 9.5f);
        y += 28;
        var loggedInSelectorHint = new Label
        {
            Text = "Playwright locator visible only when signed in. Examples: text=Mina sidor, a[href*='/logout'], [data-testid='user-menu']. Empty = fall back to URL + password-field heuristic.",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = false,
            Size = new Size(inputW, 44),
            Location = new Point(inputX, y),
            BackColor = Color.Transparent,
        };
        _host.Controls.Add(loggedInSelectorHint);
        y += 48;

        _loginUsernameLabel = AddLabel("Username", pad, y);
        _loginUsernameBox = MakeTextBox(inputX, y, inputW);
        _loginUsernameBox.Text = existing?.LoginUsername ?? "";
        y += 32;

        _loginPasswordLabel = AddLabel("Password", pad, y);
        _loginPasswordBox = MakeTextBox(inputX, y, inputW);
        _loginPasswordBox.UseSystemPasswordChar = true;
        // DPAPI-decrypt once for display; encryption happens again on Save.
        _loginPasswordBox.Text = CredentialProtector.Decrypt(existing?.LoginPasswordEncrypted ?? "");
        y += 32;

        var passwordHint = new Label
        {
            Text = "Stored encrypted via Windows DPAPI (same user + machine only).",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(inputX, y),
            BackColor = Color.Transparent,
        };
        _host.Controls.Add(passwordHint);
        y += 24;

        // Home URL is independent of Auto login (it's the preview/landing page),
        // so it stays enabled. The toggle only gates the sign-in fields.
        void RefreshAutoLoginEnabled()
        {
            bool on = _autoLoginToggle.Checked;
            _loginUrlLabel.Enabled = on;
            _loginUrlBox.Enabled = on;
            _loggedInSelectorLabel.Enabled = on;
            _loggedInSelectorBox.Enabled = on;
            _loginUsernameLabel.Enabled = on;
            _loginUsernameBox.Enabled = on;
            _loginPasswordLabel.Enabled = on;
            _loginPasswordBox.Enabled = on;
            _loginUrlBox.BackColor = on ? _theme.BgHeader : _theme.BgDark;
            _loggedInSelectorBox.BackColor = on ? _theme.BgHeader : _theme.BgDark;
            _loginUsernameBox.BackColor = on ? _theme.BgHeader : _theme.BgDark;
            _loginPasswordBox.BackColor = on ? _theme.BgHeader : _theme.BgDark;
        }
        _autoLoginToggle.CheckedChanged += (_, _) => RefreshAutoLoginEnabled();
        RefreshAutoLoginEnabled();

        // Apply URL-kind field/tab visibility once everything is built.
        RefreshForUrlKind();

        // ------------------------------------------------------- Form-level chrome
        int btnRowY = ClientSize.Height - 46;
        _validationLabel = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.ErrorColor,
            AutoSize = false,
            Size = new Size(ClientSize.Width - pad * 2, 18),
            Location = new Point(pad, btnRowY - 22),
            BackColor = Color.Transparent,
        };
        Controls.Add(_validationLabel);

        var saveBtn = new RoundedButton
        {
            Text = isEdit ? "Save" : "Add Shortcut",
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            Size = new Size(140, 36),
            Location = new Point(ClientSize.Width - pad - 140, btnRowY),
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
            Location = new Point(ClientSize.Width - pad - 140 - 10 - 90, btnRowY),
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
                Location = new Point(pad, btnRowY),
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
    }

    /// <summary>True when the currently-selected launch profile is the URL kind.</summary>
    private bool CurrentProfileIsUrl()
    {
        int i = _launchProfileCombo.SelectedIndex;
        return i >= 0 && i < LaunchProfiles.All.Length
            && LaunchProfiles.All[i].Kind == LaunchKind.Url;
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
        _host.Controls.Add(hdr);
        var rule = new Panel
        {
            BackColor = _theme.Border,
            Location = new Point(20, y + 22),
            Size = new Size(_pageW - 40, 1),
        };
        _host.Controls.Add(rule);
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
        _host.Controls.Add(lbl);
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
        _host.Controls.Add(tb);
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
        _host.Controls.Add(cb);
        return cb;
    }

    private void TrySave()
    {
        if (string.IsNullOrWhiteSpace(_nameBox.Text))
        {
            _validationLabel.Text = "Name is required.";
            return;
        }
        if (CurrentProfileIsUrl())
        {
            var url = _argsBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                _validationLabel.Text = "URL is required.";
                return;
            }
            if (!(Uri.TryCreate(url, UriKind.Absolute, out var u)
                  && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)))
            {
                _validationLabel.Text = "Enter a valid http(s) URL (e.g. https://localhost:5001).";
                return;
            }
        }
        else
        {
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
        }
        if (_desktopShortcutToggle.Checked
            && string.IsNullOrWhiteSpace(_desktopShortcutNameBox.Text))
        {
            _validationLabel.Text = "Desktop name is required when 'Add shortcut to desktop' is on (Integration tab).";
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
            SetupSteps = _setupStepsBox.Text,
            Shell = _shellCombo.SelectedIndex == 1 ? LaunchShell.PowerShell : LaunchShell.Cmd,
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
            ExcludeFromStopAll = _keepRunningToggle.Checked,
            ShowInExplorerContextMenu = _explorerMenuToggle.Checked,
            AddToDesktop = _desktopShortcutToggle.Checked,
            DesktopShortcutName = _desktopShortcutNameBox.Text.Trim(),
            StatusUrl = _statusUrlBox.Text.Trim(),
            StatusTimeoutSeconds = (int)_statusTimeoutBox.Value,
            AutoLoginEnabled = _autoLoginToggle.Checked,
            HomeUrl = _homeUrlBox.Text.Trim(),
            LoginUrl = _loginUrlBox.Text.Trim(),
            LoggedInSelector = _loggedInSelectorBox.Text.Trim(),
            LoginUsername = _loginUsernameBox.Text.Trim(),
            LoginPasswordEncrypted = _autoLoginToggle.Checked
                ? CredentialProtector.Encrypt(_loginPasswordBox.Text)
                : "",
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

    /// <summary>Default desktop-shortcut filename: directory leaf name +
    /// profile display name (e.g. <c>"Lindex Claude CLI"</c>). Either part
    /// missing → that part is omitted; both missing → empty string.</summary>
    private static string ComputeDefaultDesktopName(string? workingDirectory, int profileIndex)
    {
        string leaf = string.IsNullOrWhiteSpace(workingDirectory)
            ? ""
            : Path.GetFileName(workingDirectory.TrimEnd('/', '\\')) ?? "";

        string profileName = "";
        if (profileIndex >= 0 && profileIndex < LaunchProfiles.All.Length)
            profileName = LaunchProfiles.All[profileIndex].DisplayName ?? "";

        if (string.IsNullOrWhiteSpace(leaf)) return profileName.Trim();
        if (string.IsNullOrWhiteSpace(profileName)) return leaf.Trim();
        return $"{leaf.Trim()} {profileName.Trim()}";
    }

    private void RebuildArgsChips(FlowLayoutPanel panel, LaunchProfile profile, PluginTheme theme)
    {
        foreach (Control c in panel.Controls) c.Dispose();
        panel.Controls.Clear();

        foreach (var token in profile.SuggestedTokens)
        {
            var chip = new RoundedButton
            {
                Text = token,
                Tag = token,
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
                // Toggle: add the token if missing, remove it if already present.
                _argsBox.Text = ToggleArgsToken(_argsBox.Text, captured);
                _argsBox.Focus();
                _argsBox.SelectionStart = _argsBox.Text.Length;
                RefreshArgsChipStates(panel);
            };
            panel.Controls.Add(chip);
        }
        RefreshArgsChipStates(panel);
    }

    /// <summary>Highlight (with a ✓) the suggestion chips whose token is already in
    /// the args, so the user can see at a glance what's applied.</summary>
    private void RefreshArgsChipStates(FlowLayoutPanel panel)
    {
        foreach (Control c in panel.Controls)
        {
            if (c is not RoundedButton chip || chip.Tag is not string token) continue;
            bool active = ArgsHasToken(_argsBox.Text, token);
            chip.Text = active ? "✓ " + token : token;
            chip.BackColor = active ? _theme.Primary : _theme.PrimaryDim;
            chip.ForeColor = active ? Color.White : _theme.TextPrimary;
        }
    }

    private static bool ArgsHasToken(string? args, string token)
    {
        string norm = " " + string.Join(" ", (args ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)) + " ";
        return norm.Contains(" " + token.Trim() + " ");
    }

    /// <summary>Add the token if absent, remove its (possibly multi-word) run if present.</summary>
    private static string ToggleArgsToken(string? args, string token)
    {
        string norm = string.Join(" ", (args ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        string t = token.Trim();
        string padded = " " + norm + " ";
        if (padded.Contains(" " + t + " "))
            return string.Join(" ", padded.Replace(" " + t + " ", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return norm.Length == 0 ? t : norm + " " + t;
    }

    private LaunchShell CurrentShell() =>
        _shellCombo.SelectedIndex == 1 ? LaunchShell.PowerShell : LaunchShell.Cmd;

    private static string SetupHintFor(int shellIndex) => shellIndex == 1
        ? "Optional. One PowerShell statement per line, run before the command. e.g. $env:NEXT_LOG_LEVEL = \"error\""
        : "Optional. One cmd statement per line, run before the command. e.g. set NEXT_LOG_LEVEL=error";

    /// <summary>Rebuilds the click-to-insert snippet chips for the current
    /// shell. Each chip appends its snippet as a new line in the setup-steps
    /// box (one statement per line).</summary>
    private void RebuildSetupChips(FlowLayoutPanel panel, LaunchShell shell, PluginTheme theme)
    {
        foreach (Control c in panel.Controls) c.Dispose();
        panel.Controls.Clear();

        foreach (var snippet in SetupStepChips.For(shell))
        {
            var chip = new RoundedButton
            {
                Text = snippet,
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
            string captured = snippet;
            chip.Click += (_, _) =>
            {
                var cur = _setupStepsBox.Text ?? "";
                _setupStepsBox.Text = string.IsNullOrWhiteSpace(cur)
                    ? captured
                    : cur.TrimEnd('\r', '\n') + Environment.NewLine + captured;
                _setupStepsBox.Focus();
                _setupStepsBox.SelectionStart = _setupStepsBox.Text.Length;
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
