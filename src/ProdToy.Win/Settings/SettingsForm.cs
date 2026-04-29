using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Win32;
using ProdToy.Sdk;

namespace ProdToy;

class SettingsForm : Form
{
    private PopupTheme _currentTheme;
    private readonly Panel _themePreview;
    private readonly Label _themeNameLabel;
    private readonly ComboBox _themeCombo;
    private readonly ThemedTabControl _tabControl;
    private readonly Label _versionLabel;

    private readonly Label _titleLabel;
    private readonly Panel _accentLine;

    // Fixed tab references for live rebuild
    private TabPage _pluginsPage = null!;
    private TabPage _aboutPage = null!;
    private readonly int _tp = 16;
    private int _tabInner;
    private readonly int _contentWidth = 852;

    public event Action<PopupTheme>? ThemeChanged;
    public event Action<string>? GlobalFontChanged;

    public SettingsForm(PopupTheme currentTheme)
    {
        _currentTheme = currentTheme;

        Text = "ProdToy Settings";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(900, 880);
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = currentTheme.BgDark;
        ForeColor = currentTheme.TextPrimary;
        Font = new Font("Segoe UI", 10f);
        Icon = Themes.CreateAppIcon(currentTheme.Primary);

        int leftMargin = 24;
        int contentWidth = _contentWidth;

        // --- Title ---
        int y = 16;
        _titleLabel = new Label
        {
            Text = "Settings",
            Font = new Font("Segoe UI Semibold", 18f, FontStyle.Bold),
            ForeColor = currentTheme.TextPrimary,
            AutoSize = true,
            Location = new Point(leftMargin, y),
            BackColor = Color.Transparent,
        };
        Controls.Add(_titleLabel);
        y += 44;

        // --- Accent line ---
        _accentLine = new Panel
        {
            BackColor = currentTheme.Primary,
            Location = new Point(leftMargin, y),
            Size = new Size(contentWidth, 2),
        };
        Controls.Add(_accentLine);
        y += 8;

        // --- TabControl (owner-drawn) ---
        // Initial tab width — will be recalculated when plugin tabs are added
        int tabCount = 2; // General, Plugins (About + plugin tabs added later)
        int tabWidth = (contentWidth - 4) / tabCount;
        _tabControl = new ThemedTabControl(currentTheme)
        {
            Location = new Point(leftMargin, y),
            Size = new Size(contentWidth, 760),
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
            SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(tabWidth, 32),
            Padding = new Point(0, 0),
            Multiline = true,
        };
        Controls.Add(_tabControl);

        int tp = _tp;
        int tabInner = _contentWidth - tp * 2 - 2;
        _tabInner = tabInner;

        // =============================================
        // TAB 0: General
        // =============================================
        var generalPage = CreateTabPage("General", currentTheme);
        _tabControl.TabPages.Add(generalPage);

        int gy = tp;

        var fontSectionLabel = CreateSectionLabel("FONT", tp, gy);
        generalPage.Controls.Add(fontSectionLabel);
        gy += 28;

        var fontLabel = new Label
        {
            Text = "Global font:",
            Font = new Font("Segoe UI", 9f),
            ForeColor = currentTheme.TextPrimary,
            AutoSize = true,
            Location = new Point(tp, gy + 3),
            BackColor = Color.Transparent,
        };
        generalPage.Controls.Add(fontLabel);

        var currentFont = AppSettings.Load().GlobalFont;
        var fontCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Segoe UI", 9.5f),
            BackColor = currentTheme.BgHeader,
            ForeColor = currentTheme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(250, 26),
            Location = new Point(tp + 100, gy),
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 24,
        };

        // Populate with installed font families
        using var installedFonts = new System.Drawing.Text.InstalledFontCollection();
        var families = installedFonts.Families;
        int selectedFontIdx = 0;
        for (int fi = 0; fi < families.Length; fi++)
        {
            fontCombo.Items.Add(families[fi].Name);
            if (families[fi].Name.Equals(currentFont, StringComparison.OrdinalIgnoreCase))
                selectedFontIdx = fi;
        }

        // Owner-draw to preview each font
        fontCombo.DrawItem += (_, de) =>
        {
            if (de.Index < 0) return;
            string name = fontCombo.Items[de.Index].ToString()!;
            bool selected = (de.State & DrawItemState.Selected) != 0;
            var bg = selected ? currentTheme.Primary : currentTheme.BgHeader;
            var fg = selected ? Color.White : currentTheme.TextPrimary;

            using var bgBrush = new SolidBrush(bg);
            de.Graphics.FillRectangle(bgBrush, de.Bounds);

            try
            {
                using var previewFont = new Font(name, 10f);
                using var textBrush = new SolidBrush(fg);
                using var sf = new StringFormat { LineAlignment = StringAlignment.Center };
                de.Graphics.DrawString(name, previewFont, textBrush, de.Bounds, sf);
            }
            catch
            {
                using var fallback = new Font("Segoe UI", 10f);
                using var textBrush = new SolidBrush(fg);
                using var sf = new StringFormat { LineAlignment = StringAlignment.Center };
                de.Graphics.DrawString(name, fallback, textBrush, de.Bounds, sf);
            }
        };

        fontCombo.SelectedIndex = selectedFontIdx;
        generalPage.Controls.Add(fontCombo);
        gy += 34;

        // Preview label
        var fontPreviewLabel = new Label
        {
            Text = "The quick brown fox jumps over the lazy dog",
            Font = new Font(currentFont, 11f),
            ForeColor = currentTheme.TextPrimary,
            AutoSize = true,
            MaximumSize = new Size(tabInner, 40),
            Location = new Point(tp, gy),
            BackColor = Color.Transparent,
        };
        generalPage.Controls.Add(fontPreviewLabel);
        gy += 34;

        // Apply button
        var fontApplyButton = new RoundedButton
        {
            Text = "Apply",
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            Size = new Size(90, 30),
            Location = new Point(tp, gy),
            FlatStyle = FlatStyle.Flat,
            BackColor = currentTheme.Primary,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
        };
        fontApplyButton.FlatAppearance.BorderSize = 0;
        fontApplyButton.FlatAppearance.MouseOverBackColor = currentTheme.PrimaryLight;
        fontApplyButton.Click += (_, _) =>
        {
            string selectedFont = fontCombo.SelectedItem?.ToString() ?? "Segoe UI";
            var settings = AppSettings.Load();
            AppSettings.Save(settings with { GlobalFont = selectedFont });

            // Update preview
            try { fontPreviewLabel.Font = new Font(selectedFont, 11f); } catch { }

            // Apply to this form
            ApplyGlobalFont(selectedFont);

            GlobalFontChanged?.Invoke(selectedFont);
        };
        generalPage.Controls.Add(fontApplyButton);

        // Update preview on selection change
        fontCombo.SelectedIndexChanged += (_, _) =>
        {
            string name = fontCombo.SelectedItem?.ToString() ?? "Segoe UI";
            try { fontPreviewLabel.Font = new Font(name, 11f); } catch { }
        };

        gy += 38;

        // --- STARTUP section ---
        var startupSectionLabel = CreateSectionLabel("STARTUP", tp, gy);
        generalPage.Controls.Add(startupSectionLabel);
        gy += 28;

        var startWithWindowsCheck = new CheckBox
        {
            Text = "Start ProdToy when Windows starts",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = currentTheme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = AppSettings.Load().StartWithWindows,
            AutoSize = true,
            Location = new Point(tp, gy),
            Cursor = Cursors.Hand,
        };
        startWithWindowsCheck.CheckedChanged += (_, _) =>
        {
            bool enabled = startWithWindowsCheck.Checked;
            AppSettings.Save(AppSettings.Load() with { StartWithWindows = enabled });
            SetStartWithWindows(enabled);
        };
        generalPage.Controls.Add(startWithWindowsCheck);
        gy += 28;

        var startupNote = new Label
        {
            Text = "ProdToy will start minimized to the system tray.",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = currentTheme.TextSecondary,
            AutoSize = true,
            Location = new Point(tp + 18, gy),
            BackColor = Color.Transparent,
        };
        generalPage.Controls.Add(startupNote);
        gy += 28;

        // --- THEME section (merged from Appearance tab) ---
        generalPage.Controls.Add(CreateSeparator(tp, gy, tabInner));
        gy += 14;

        var themeSectionLabel = CreateSectionLabel("THEME", tp, gy);
        generalPage.Controls.Add(themeSectionLabel);
        gy += 28;

        _themeCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Segoe UI", 10f),
            BackColor = currentTheme.BgHeader,
            ForeColor = currentTheme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(tabInner, 28),
            Location = new Point(tp, gy),
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 30,
        };

        int selectedIndex = 0;
        for (int i = 0; i < Themes.All.Length; i++)
        {
            _themeCombo.Items.Add(Themes.All[i]);
            if (Themes.All[i].Name == currentTheme.Name)
                selectedIndex = i;
        }

        _themeCombo.DrawItem += OnThemeComboDrawItem;
        _themeCombo.SelectedIndexChanged += OnThemeComboChanged;
        generalPage.Controls.Add(_themeCombo);
        gy += 40;

        _themeNameLabel = new Label
        {
            Text = currentTheme.Name,
            Font = new Font("Segoe UI", 10f),
            ForeColor = currentTheme.Primary,
            AutoSize = true,
            Location = new Point(tp, gy),
            BackColor = Color.Transparent,
        };
        generalPage.Controls.Add(_themeNameLabel);

        _themePreview = new Panel
        {
            BackColor = currentTheme.Primary,
            Location = new Point(tp, gy + 24),
            Size = new Size(tabInner, 4),
        };
        generalPage.Controls.Add(_themePreview);

        // =============================================
        // TAB: Plugins (fixed)
        // =============================================
        _pluginsPage = CreateTabPage("Plugins", currentTheme);
        _tabControl.TabPages.Add(_pluginsPage);
        int py = tp;

        var pluginsSectionLabel = CreateSectionLabel("INSTALLED PLUGINS", tp, py);
        _pluginsPage.Controls.Add(pluginsSectionLabel);

        var browseCatalogButton = new RoundedButton
        {
            Text = "Plugin Store",
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            Size = new Size(130, 28),
            Location = new Point(tabInner - 130 + tp, py),
            FlatStyle = FlatStyle.Flat,
            BackColor = currentTheme.Primary,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
        };
        browseCatalogButton.FlatAppearance.BorderSize = 0;
        browseCatalogButton.FlatAppearance.MouseOverBackColor = currentTheme.PrimaryLight;
        PluginCatalogForm? openCatalog = null;
        browseCatalogButton.Click += (_, _) =>
        {
            if (openCatalog != null && !openCatalog.IsDisposed)
            {
                openCatalog.BringToFront();
                openCatalog.Activate();
                return;
            }
            openCatalog = new PluginCatalogForm(currentTheme);
            openCatalog.FormClosed += (_, _) => openCatalog = null;
            openCatalog.Show(this);
        };
        _pluginsPage.Controls.Add(browseCatalogButton);

        // =============================================
        // TAB: Data Sync (fixed)
        // =============================================
        var dataSyncPage = CreateTabPage("Data Sync", currentTheme);
        _tabControl.TabPages.Add(dataSyncPage);
        BuildDataSyncPage(dataSyncPage, currentTheme, tp, tabInner);

        // =============================================
        // TAB: About (fixed, always last)
        // =============================================
        _aboutPage = CreateTabPage("About", currentTheme);
        _aboutPage.AutoScroll = true;
        // Kill the horizontal scrollbar permanently. WinForms' AutoScroll
        // recomputes scrollbar visibility on every layout pass, so a one-time
        // HorizontalScroll.Visible = false gets clobbered. Re-asserting on
        // Layout is the only robust way to keep it hidden.
        _aboutPage.Layout += (_, _) =>
        {
            _aboutPage.HorizontalScroll.Maximum = 0;
            _aboutPage.HorizontalScroll.Visible = false;
            _aboutPage.AutoScroll = true;
        };
        int ab = tp;

        // Reserve space for the vertical scrollbar — without this, any
        // control sized to the full tabInner width overflows the tab page's
        // client area as soon as VScroll appears, which in turn re-triggers
        // the horizontal scrollbar. Using a smaller width for every full-width
        // control on this page guarantees no child ever extends past the
        // scrollbar's left edge.
        int aboutInner = tabInner - 22;

        // --- App icon + name + version ---
        var appIconPanel = new Panel
        {
            Size = new Size(48, 48),
            Location = new Point(tp, ab),
            BackColor = Color.Transparent,
        };
        appIconPanel.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var bgBrush = new SolidBrush(currentTheme.PrimaryDim);
            e.Graphics.FillEllipse(bgBrush, 0, 0, 47, 47);
            using var textBrush = new SolidBrush(currentTheme.Primary);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var iconFont = new Font("Segoe UI Semibold", 18f, FontStyle.Bold);
            e.Graphics.DrawString("P", iconFont, textBrush, new RectangleF(0, 0, 48, 48), sf);
        };
        _aboutPage.Controls.Add(appIconPanel);

        var aboutNameLabel = new Label
        {
            Text = "ProdToy",
            Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold),
            ForeColor = currentTheme.TextPrimary,
            AutoSize = true,
            Location = new Point(tp + 58, ab),
            BackColor = Color.Transparent,
        };
        _aboutPage.Controls.Add(aboutNameLabel);

        var aboutVersionLabel = new Label
        {
            Text = $"Version {AppVersion.Current}",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = currentTheme.TextSecondary,
            AutoSize = true,
            Location = new Point(tp + 60, ab + 28),
            BackColor = Color.Transparent,
        };
        _aboutPage.Controls.Add(aboutVersionLabel);
        ab += 58;

        var aboutDescLabel = new Label
        {
            Text = "Developer utility toolkit for Windows.\nNotifications, screen capture, and productivity tools for Claude Code.",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = currentTheme.TextSecondary,
            AutoSize = true,
            MaximumSize = new Size(aboutInner, 0),
            Location = new Point(tp, ab),
            BackColor = Color.Transparent,
        };
        _aboutPage.Controls.Add(aboutDescLabel);
        ab += aboutDescLabel.PreferredHeight + 14;

        // --- Separator ---
        _aboutPage.Controls.Add(CreateSeparator(tp, ab, aboutInner));
        ab += 14;

        // --- Repository Section ---
        var repoSectionLabel = CreateSectionLabel("REPOSITORY", tp, ab);
        _aboutPage.Controls.Add(repoSectionLabel);
        ab += 26;

        // GitHub icon (drawn) + repo link
        var githubIconPanel = new Panel
        {
            Size = new Size(22, 22),
            Location = new Point(tp, ab + 1),
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
        };
        githubIconPanel.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // Draw GitHub octocat-style circle icon
            using var circleBrush = new SolidBrush(currentTheme.TextSecondary);
            e.Graphics.FillEllipse(circleBrush, 1, 1, 20, 20);
            using var innerBrush = new SolidBrush(currentTheme.BgDark);
            // Inner cutout for the "cat" silhouette effect
            e.Graphics.FillEllipse(innerBrush, 4, 4, 14, 14);
            using var fgBrush = new SolidBrush(currentTheme.TextSecondary);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var ghFont = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
            e.Graphics.DrawString("\u2B24", ghFont, fgBrush, new RectangleF(0, 0, 22, 22), sf);
        };
        githubIconPanel.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/sukeshchand/ProdToy") { UseShellExecute = true }); } catch { }
        };
        _aboutPage.Controls.Add(githubIconPanel);

        var repoLink = new Label
        {
            Text = "github.com/sukeshchand/ProdToy",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Underline),
            ForeColor = currentTheme.Primary,
            AutoSize = true,
            Location = new Point(tp + 28, ab + 2),
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
        };
        repoLink.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/sukeshchand/ProdToy") { UseShellExecute = true }); } catch { }
        };
        repoLink.MouseEnter += (_, _) => repoLink.ForeColor = currentTheme.PrimaryLight;
        repoLink.MouseLeave += (_, _) => repoLink.ForeColor = currentTheme.Primary;
        _aboutPage.Controls.Add(repoLink);
        ab += 30;

        // Info rows: Author, License, Runtime
        var infoItems = new (string label, string value)[]
        {
            ("Author", "SUKESH CHANDH RAJU"),
            ("", "sukesh.chand@gmail.com"),
            ("License", "MIT"),
            ("Runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription),
            ("Platform", $"Windows {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}"),
        };
        foreach (var (label, value) in infoItems)
        {
            if (!string.IsNullOrEmpty(label))
            {
                var rowLabel = new Label
                {
                    Text = label,
                    Font = new Font("Segoe UI", 8.5f),
                    ForeColor = currentTheme.TextSecondary,
                    Size = new Size(80, 20),
                    Location = new Point(tp, ab),
                    BackColor = Color.Transparent,
                };
                _aboutPage.Controls.Add(rowLabel);
            }

            var rowValue = new Label
            {
                Text = value,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = currentTheme.TextPrimary,
                AutoSize = true,
                Location = new Point(string.IsNullOrEmpty(label) ? tp : tp + 84, ab),
                BackColor = Color.Transparent,
            };
            _aboutPage.Controls.Add(rowValue);
            ab += 22;
        }
        ab += 10;

        // --- Separator ---
        _aboutPage.Controls.Add(CreateSeparator(tp, ab, aboutInner));
        ab += 14;

        // --- Updates Section ---
        var updateSectionLabel = CreateSectionLabel("UPDATES", tp, ab);
        _aboutPage.Controls.Add(updateSectionLabel);
        ab += 26;

        var updatePathLabel = new Label
        {
            Text = "Update location (URL or network path):",
            Font = new Font("Segoe UI", 9f),
            ForeColor = currentTheme.TextPrimary,
            AutoSize = true,
            Location = new Point(tp, ab),
            BackColor = Color.Transparent,
        };
        _aboutPage.Controls.Add(updatePathLabel);
        ab += 22;

        var updatePathBox = new TextBox
        {
            Text = AppSettings.Load().UpdateLocation,
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = currentTheme.TextPrimary,
            BackColor = currentTheme.BgHeader,
            BorderStyle = BorderStyle.FixedSingle,
            Size = new Size(aboutInner - 40, 26),
            Location = new Point(tp, ab),
        };
        updatePathBox.LostFocus += (_, _) =>
        {
            var loc = string.IsNullOrWhiteSpace(updatePathBox.Text)
                ? AppSettingsData.DefaultUpdateLocation
                : updatePathBox.Text.Trim();
            updatePathBox.Text = loc;
            var settings = AppSettings.Load();
            AppSettings.Save(settings with { UpdateLocation = loc });
        };
        _aboutPage.Controls.Add(updatePathBox);

        var savePathButton = new RoundedButton
        {
            Text = "Save",
            Font = new Font("Segoe UI", 8.5f),
            Size = new Size(34, 26),
            Location = new Point(tp + aboutInner - 34, ab),
            FlatStyle = FlatStyle.Flat,
            BackColor = currentTheme.PrimaryDim,
            ForeColor = currentTheme.TextSecondary,
            Cursor = Cursors.Hand,
        };
        savePathButton.FlatAppearance.BorderSize = 0;
        savePathButton.FlatAppearance.MouseOverBackColor = currentTheme.Primary;
        savePathButton.Click += (_, _) =>
        {
            var loc = string.IsNullOrWhiteSpace(updatePathBox.Text)
                ? AppSettingsData.DefaultUpdateLocation
                : updatePathBox.Text.Trim();
            updatePathBox.Text = loc;
            var settings = AppSettings.Load();
            AppSettings.Save(settings with { UpdateLocation = loc });
            savePathButton.Text = "\u2713";
            var resetTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            resetTimer.Tick += (_, _) => { savePathButton.Text = "Save"; resetTimer.Stop(); resetTimer.Dispose(); };
            resetTimer.Start();
        };
        _aboutPage.Controls.Add(savePathButton);
        ab += 34;

        var checkNowButton = new RoundedButton
        {
            Text = "Check for Updates",
            Font = new Font("Segoe UI", 8.5f),
            Size = new Size(140, 28),
            Location = new Point(tp, ab),
            FlatStyle = FlatStyle.Flat,
            BackColor = currentTheme.PrimaryDim,
            ForeColor = currentTheme.TextSecondary,
            Cursor = Cursors.Hand,
        };
        checkNowButton.FlatAppearance.BorderSize = 0;
        checkNowButton.FlatAppearance.MouseOverBackColor = currentTheme.Primary;

        var checkResultLabel = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = currentTheme.TextSecondary,
            AutoSize = true,
            Location = new Point(tp + 150, ab + 5),
            BackColor = Color.Transparent,
        };

        var updateLinkLabel = new Label
        {
            Text = "Install Update",
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Underline | FontStyle.Bold),
            ForeColor = currentTheme.Primary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Cursor = Cursors.Hand,
            Visible = false,
        };
        updateLinkLabel.Click += async (_, _) =>
        {
            updateLinkLabel.Text = "Updating...";
            updateLinkLabel.Enabled = false;
            var result = await Task.Run(Updater.Apply);
            if (result.Success)
            {
                Application.Exit();
            }
            else
            {
                MessageBox.Show(this, result.Message, "Update Failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                updateLinkLabel.Text = "Install Update";
                updateLinkLabel.Enabled = true;
            }
        };

        checkNowButton.Click += (_, _) =>
        {
            var settings = AppSettings.Load();
            AppSettings.Save(settings with { UpdateLocation = updatePathBox.Text.Trim() });

            checkNowButton.Enabled = false;
            checkNowButton.Text = "Checking...";
            checkResultLabel.Text = "";
            updateLinkLabel.Visible = false;

            UpdateChecker.CheckNow();
            var meta = UpdateChecker.LatestMetadata;
            if (meta != null)
            {
                checkResultLabel.ForeColor = currentTheme.SuccessColor;
                checkResultLabel.Text = $"v{meta.Version} available";
                updateLinkLabel.Visible = true;
                updateLinkLabel.Location = new Point(
                    checkResultLabel.Left + checkResultLabel.PreferredWidth + 10,
                    checkResultLabel.Top);
            }
            else
            {
                checkResultLabel.ForeColor = currentTheme.TextSecondary;
                checkResultLabel.Text = "You are up to date.";
            }

            checkNowButton.Enabled = true;
            checkNowButton.Text = "Check for Updates";
        };
        _aboutPage.Controls.Add(checkNowButton);
        _aboutPage.Controls.Add(checkResultLabel);
        _aboutPage.Controls.Add(updateLinkLabel);
        ab += 40;

        // --- INSTALLATION section ---
        _aboutPage.Controls.Add(CreateSeparator(tp, ab, aboutInner));
        ab += 14;

        var installSectionLabel = CreateSectionLabel("INSTALLATION", tp, ab);
        _aboutPage.Controls.Add(installSectionLabel);
        ab += 28;

        var installPathCaption = new Label
        {
            Text = "Installed at",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = currentTheme.TextSecondary,
            AutoSize = true,
            Location = new Point(tp, ab),
            BackColor = Color.Transparent,
        };
        _aboutPage.Controls.Add(installPathCaption);
        ab += 18;

        var installPathValue = new Label
        {
            Text = AppPaths.Root,
            Font = new Font("Consolas", 9f),
            ForeColor = currentTheme.TextPrimary,
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Size = new Size(aboutInner, 20),
            Location = new Point(tp, ab),
            BackColor = Color.Transparent,
        };
        _aboutPage.Controls.Add(installPathValue);
        ab += 24;

        var openFolderButton = new RoundedButton
        {
            Text = "Open Folder",
            Font = new Font("Segoe UI", 8.5f),
            Size = new Size(120, 28),
            Location = new Point(tp, ab),
            FlatStyle = FlatStyle.Flat,
            BackColor = currentTheme.PrimaryDim,
            ForeColor = currentTheme.TextSecondary,
            Cursor = Cursors.Hand,
        };
        openFolderButton.FlatAppearance.BorderSize = 0;
        openFolderButton.FlatAppearance.MouseOverBackColor = currentTheme.Primary;
        openFolderButton.Click += (_, _) =>
        {
            try
            {
                if (!Directory.Exists(AppPaths.Root))
                {
                    MessageBox.Show(this,
                        $"Install folder does not exist:\n{AppPaths.Root}",
                        "Open Folder", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = AppPaths.Root,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Could not open folder:\n{ex.Message}",
                    "Open Folder", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        _aboutPage.Controls.Add(openFolderButton);
        ab += 36;

        var logsSizeLabel = new Label
        {
            Text = $"Logs folder:  {FormatDirectorySize(AppPaths.LogsDir)}",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = currentTheme.TextSecondary,
            AutoSize = true,
            Location = new Point(tp, ab),
            BackColor = Color.Transparent,
        };
        _aboutPage.Controls.Add(logsSizeLabel);
        ab += 20;

        var dataSizeLabel = new Label
        {
            Text = $"Data folder:  {FormatDirectorySize(AppPaths.DataDir)}",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = currentTheme.TextSecondary,
            AutoSize = true,
            Location = new Point(tp, ab),
            BackColor = Color.Transparent,
        };
        _aboutPage.Controls.Add(dataSizeLabel);
        ab += 24;

        // --- DOCTOR section ---
        _aboutPage.Controls.Add(CreateSeparator(tp, ab, aboutInner));
        ab += 14;

        var doctorSectionLabel = CreateSectionLabel("DOCTOR", tp, ab);
        _aboutPage.Controls.Add(doctorSectionLabel);
        ab += 28;

        var doctorHint = new Label
        {
            Text = "Check installation files, config, and every plugin for problems. You'll see a list of issues and can choose which to fix.",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = currentTheme.TextSecondary,
            AutoSize = false,
            MaximumSize = new Size(aboutInner, 0),
            Size = new Size(aboutInner, 32),
            Location = new Point(tp, ab),
            BackColor = Color.Transparent,
        };
        _aboutPage.Controls.Add(doctorHint);
        ab += 38;

        var runDoctorBtn = new RoundedButton
        {
            Text = "Run Diagnostics",
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            Size = new Size(160, 32),
            Location = new Point(tp, ab),
            FlatStyle = FlatStyle.Flat,
            BackColor = currentTheme.Primary,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
        };
        runDoctorBtn.FlatAppearance.BorderSize = 0;
        runDoctorBtn.FlatAppearance.MouseOverBackColor = currentTheme.PrimaryLight;
        runDoctorBtn.Click += (_, _) =>
        {
            runDoctorBtn.Enabled = false;
            try
            {
                using var dlg = new DoctorForm(_currentTheme);
                dlg.ShowDialog(this);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Doctor crashed: {ex.Message}", "Doctor",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                runDoctorBtn.Enabled = true;
            }
        };
        _aboutPage.Controls.Add(runDoctorBtn);
        ab += 46;

        // --- UNINSTALL section (merged from Advanced tab) ---
        _aboutPage.Controls.Add(CreateSeparator(tp, ab, aboutInner));
        ab += 14;

        var uninstallSectionLabel = CreateSectionLabel("UNINSTALL", tp, ab);
        _aboutPage.Controls.Add(uninstallSectionLabel);
        ab += 28;

        var uninstallLink = new LinkLabel
        {
            Text = "Uninstall ProdToy",
            Font = new Font("Segoe UI", 8f),
            LinkColor = currentTheme.ErrorColor,
            ActiveLinkColor = currentTheme.ErrorColor,
            VisitedLinkColor = currentTheme.ErrorColor,
            AutoSize = true,
            Location = new Point(tp, ab),
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
        };
        uninstallLink.LinkClicked += (_, _) =>
        {
            // Uninstall lives in ProdToySetup.exe now. If it's next to the host
            // exe in the install dir, launch it with --uninstall. Fall back to a
            // helpful message if Setup isn't where we expect it.
            string setupExe = AppPaths.SetupExePath;
            if (File.Exists(setupExe))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = setupExe,
                        Arguments = "--uninstall",
                        UseShellExecute = true,
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this,
                        $"Could not launch installer:\n{ex.Message}",
                        "Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            else
            {
                MessageBox.Show(this,
                    $"ProdToySetup.exe was not found at:\n{setupExe}\n\n" +
                    "Run the installer you originally used to install ProdToy, " +
                    "then click Uninstall from it.",
                    "Uninstall",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
        _aboutPage.Controls.Add(uninstallLink);

        // --- Bottom version label on form ---
        _versionLabel = new Label
        {
            Text = $"ProdToy v{AppVersion.Current}",
            Font = new Font("Segoe UI", 8f),
            ForeColor = Color.FromArgb(60, 75, 105),
            AutoSize = true,
            Location = new Point(leftMargin, ClientSize.Height - 30),
            BackColor = Color.Transparent,
        };
        Controls.Add(_versionLabel);

        // Set initial selection
        _themeCombo.SelectedIndex = selectedIndex;

        // Build initial plugin list and dynamic settings tabs
        RebuildPluginList(_pluginsPage, currentTheme, tp, tabInner);

        var pluginSettingsPages = PluginManager.GetAllSettingsPages();
        foreach (var (pluginInfo, settingsPage) in pluginSettingsPages)
        {
            var pluginTabPage = CreateTabPage(settingsPage.TabTitle, currentTheme);
            pluginTabPage.Tag = $"plugin-tab:{pluginInfo.Id}";
            try
            {
                var content = settingsPage.CreateContent();
                content.Dock = DockStyle.Fill;
                pluginTabPage.Controls.Add(content);
            }
            catch (Exception ex)
            {
                pluginTabPage.Controls.Add(new Label
                {
                    Text = $"Failed to load: {ex.Message}",
                    Font = new Font("Segoe UI", 9f),
                    ForeColor = currentTheme.ErrorColor,
                    AutoSize = true, Location = new Point(tp, tp),
                    BackColor = Color.Transparent,
                });
            }
            _tabControl.TabPages.Add(pluginTabPage);
        }

        // About tab always last
        _tabControl.TabPages.Add(_aboutPage);

        // Recalculate tab widths. Reserve a buffer for the tab control's built-in
        // right-edge padding — without it 5+ tabs trigger the scroll arrows even
        // though the sum of tab widths fits the content width on paper.
        {
            int tc = _tabControl.TabPages.Count;
            int tw = (_contentWidth - 8) / Math.Max(1, tc) - 2;
            _tabControl.ItemSize = new Size(tw, 32);
        }

        // Live rebuild when plugins change
        PluginManager.PluginsChanged += OnPluginsChanged;
        FormClosed += (_, _) => PluginManager.PluginsChanged -= OnPluginsChanged;

        // Apply saved global font on open
        var savedGlobalFont = AppSettings.Load().GlobalFont;
        if (!string.IsNullOrEmpty(savedGlobalFont) && savedGlobalFont != "Segoe UI")
            ApplyGlobalFont(savedGlobalFont);
    }

    public void OnPluginsChanged()
    {
        if (InvokeRequired)
        {
            Invoke(OnPluginsChanged);
            return;
        }

        // Refresh the installed plugins list
        RebuildPluginList(_pluginsPage, _currentTheme, _tp, _tabInner);

        // Mark tabs for removed plugins — overlay with "uninstalled" message
        var activePluginIds = new HashSet<string>(
            PluginManager.GetAllSettingsPages().Select(p => p.Item1.Id),
            StringComparer.OrdinalIgnoreCase);

        foreach (TabPage page in _tabControl.TabPages)
        {
            if (page.Tag is not string tag) continue;
            if (!tag.StartsWith("plugin-tab:")) continue;

            string pluginId = tag["plugin-tab:".Length..];
            if (!activePluginIds.Contains(pluginId))
            {
                // Plugin was uninstalled — mask the tab content, keep ID for tracking
                page.Tag = $"plugin-tab-removed:{pluginId}";
                MaskTabContent(page);
            }
        }

        // Add tabs for newly installed/re-enabled plugins that don't have an active tab
        var existingActiveTabIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TabPage page in _tabControl.TabPages)
        {
            if (page.Tag is string t && t.StartsWith("plugin-tab:") && !t.StartsWith("plugin-tab-removed:"))
                existingActiveTabIds.Add(t["plugin-tab:".Length..]);
        }

        var pluginSettingsPages = PluginManager.GetAllSettingsPages();
        foreach (var (pluginInfo, settingsPage) in pluginSettingsPages)
        {
            if (existingActiveTabIds.Contains(pluginInfo.Id)) continue;

            // Remove any stale "removed" tab for this plugin before adding fresh one
            for (int i = _tabControl.TabPages.Count - 1; i >= 0; i--)
            {
                if (_tabControl.TabPages[i].Tag is string rt &&
                    rt.Equals($"plugin-tab-removed:{pluginInfo.Id}", StringComparison.OrdinalIgnoreCase))
                {
                    _tabControl.TabPages.RemoveAt(i);
                    break;
                }
            }

            // Add fresh tab before About
            var pluginTabPage = CreateTabPage(settingsPage.TabTitle, _currentTheme);
            pluginTabPage.Tag = $"plugin-tab:{pluginInfo.Id}";
            try
            {
                var content = settingsPage.CreateContent();
                content.Dock = DockStyle.Fill;
                pluginTabPage.Controls.Add(content);
            }
            catch (Exception ex)
            {
                pluginTabPage.Controls.Add(new Label
                {
                    Text = $"Failed to load: {ex.Message}",
                    Font = new Font("Segoe UI", 9f),
                    ForeColor = _currentTheme.ErrorColor,
                    AutoSize = true, Location = new Point(_tp, _tp),
                    BackColor = Color.Transparent,
                });
            }

            // Insert before About tab
            int aboutIdx = _tabControl.TabPages.IndexOf(_aboutPage);
            if (aboutIdx >= 0)
                _tabControl.TabPages.Insert(aboutIdx, pluginTabPage);
            else
                _tabControl.TabPages.Add(pluginTabPage);

            // Recalculate tab widths (same buffer as initial calc)
            int tabCount = _tabControl.TabPages.Count;
            int tabWidth = (_contentWidth - 8) / Math.Max(1, tabCount) - 2;
            _tabControl.ItemSize = new Size(tabWidth, 32);
        }
    }

    private void MaskTabContent(TabPage page)
    {
        // Hide all existing controls and show an overlay message
        foreach (Control c in page.Controls)
            c.Visible = false;

        var overlay = new Label
        {
            Text = "This plugin has been uninstalled.\n\nClose the Settings window to remove this tab.",
            Font = new Font("Segoe UI", 11f),
            ForeColor = _currentTheme.TextSecondary,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
            BackColor = _currentTheme.BgDark,
            Tag = "uninstall-overlay",
        };
        page.Controls.Add(overlay);
        overlay.BringToFront();
    }

    /// <summary>
    /// Returns true if a plugin has been uninstalled but its settings tab is still showing
    /// (pending removal on form close/reopen).
    /// </summary>
    public bool IsPluginPendingRemoval(string pluginId)
    {
        foreach (TabPage page in _tabControl.TabPages)
        {
            if (page.Tag is string tag &&
                tag.StartsWith("plugin-tab-removed:") &&
                tag["plugin-tab-removed:".Length..].Equals(pluginId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void RebuildPluginList(TabPage pluginsPage, PopupTheme theme, int tp, int tabInner)
    {
        // Remove old dynamic plugin controls (tagged with "plugin-item")
        // Collect first, then dispose — Dispose() removes from parent automatically
        var toRemove = new List<Control>();
        foreach (Control c in pluginsPage.Controls)
        {
            if (c.Tag is string tag && tag == "plugin-item")
                toRemove.Add(c);
        }
        foreach (var c in toRemove)
            c.Dispose();

        int py = tp + 34; // below section label + browse button

        var pluginsList = PluginManager.Plugins;
        if (pluginsList.Count == 0)
        {
            var noPluginsLabel = new Label
            {
                Text = "No plugins installed.\n\nPlace plugin DLLs in:\n" + AppPaths.PluginsDir,
                Font = new Font("Segoe UI", 9f),
                ForeColor = theme.TextSecondary,
                AutoSize = true,
                MaximumSize = new Size(tabInner, 0),
                Location = new Point(tp, py),
                BackColor = Color.Transparent,
                Tag = "plugin-item",
            };
            pluginsPage.Controls.Add(noPluginsLabel);
        }
        else
        {
            foreach (var plugin in pluginsList)
            {
                var pluginPanel = new Panel
                {
                    Location = new Point(tp, py),
                    Size = new Size(tabInner, 52),
                    BackColor = Color.FromArgb(20, theme.Primary),
                    Tag = "plugin-item",
                };

                var nameLabel = new Label
                {
                    Text = $"{plugin.Name} v{plugin.Version}",
                    Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                    ForeColor = theme.TextPrimary,
                    AutoSize = true,
                    Location = new Point(8, 4),
                    BackColor = Color.Transparent,
                };
                pluginPanel.Controls.Add(nameLabel);

                var descLabel = new Label
                {
                    Text = string.IsNullOrEmpty(plugin.Description) ? plugin.Id : plugin.Description,
                    Font = new Font("Segoe UI", 8f),
                    ForeColor = theme.TextSecondary,
                    AutoSize = true,
                    Location = new Point(8, 24),
                    BackColor = Color.Transparent,
                };
                pluginPanel.Controls.Add(descLabel);

                if (plugin.LoadError != null)
                {
                    var errorLabel = new Label
                    {
                        Text = plugin.LoadError,
                        Font = new Font("Segoe UI", 8f),
                        ForeColor = theme.ErrorColor,
                        AutoSize = true,
                        Location = new Point(8, 36),
                        BackColor = Color.Transparent,
                    };
                    pluginPanel.Controls.Add(errorLabel);
                    pluginPanel.Size = new Size(tabInner, 56);
                }

                pluginsPage.Controls.Add(pluginPanel);
                py += pluginPanel.Height + 6;
            }
        }
    }

    /// <summary>
    /// Build the Data Sync tab: redirect the user data folder to a synced
    /// location (OneDrive, Dropbox, Syncthing, etc.) so settings, history,
    /// and plugin data follow the user across machines. The override is
    /// persisted to Root\data-location.json and takes effect on next launch.
    /// </summary>
    private void BuildDataSyncPage(TabPage page, PopupTheme theme, int pad, int innerWidth)
    {
        int y = pad;

        page.Controls.Add(CreateSectionLabel("DATA FOLDER", pad, y));
        y += 28;

        var desc = new Label
        {
            Text = "Redirect the user data folder (settings, history, plugin data) to a\n" +
                   "synced location — for example a folder inside OneDrive, Dropbox, or\n" +
                   "Syncthing — to share state across multiple machines. Plugin binaries\n" +
                   "stay in the install folder.",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            MaximumSize = new Size(innerWidth, 0),
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        page.Controls.Add(desc);
        y += desc.PreferredSize.Height + 16;

        var currentLabelCaption = new Label
        {
            Text = "Current folder:",
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        page.Controls.Add(currentLabelCaption);
        y += 22;

        var currentPathLabel = new Label
        {
            Text = AppPaths.DataDir,
            Font = new Font("Consolas", 9f),
            ForeColor = AppPaths.IsDataDirRedirected ? theme.Primary : theme.TextPrimary,
            AutoSize = true,
            MaximumSize = new Size(innerWidth, 0),
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        page.Controls.Add(currentPathLabel);
        y += currentPathLabel.PreferredSize.Height + 4;

        var defaultNoteLabel = new Label
        {
            Text = AppPaths.IsDataDirRedirected
                ? $"(Default would be: {AppPaths.DefaultDataDir})"
                : "(This is the default location.)",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            MaximumSize = new Size(innerWidth, 0),
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        page.Controls.Add(defaultNoteLabel);
        y += defaultNoteLabel.PreferredSize.Height + 18;

        var browseBtn = new RoundedButton
        {
            Text = "Choose folder...",
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            Size = new Size(150, 30),
            Location = new Point(pad, y),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.Primary,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
        };
        browseBtn.FlatAppearance.BorderSize = 0;
        browseBtn.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        page.Controls.Add(browseBtn);

        var resetBtn = new RoundedButton
        {
            Text = "Reset to default",
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            Size = new Size(150, 30),
            Location = new Point(pad + 160, y),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            Cursor = Cursors.Hand,
            Enabled = AppPaths.IsDataDirRedirected,
        };
        resetBtn.FlatAppearance.BorderSize = 0;
        page.Controls.Add(resetBtn);
        y += 40;

        var statusLabel = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.SuccessColor,
            AutoSize = true,
            MaximumSize = new Size(innerWidth, 0),
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        page.Controls.Add(statusLabel);

        browseBtn.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Choose a folder for ProdToy data (settings, history, plugin data).",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                SelectedPath = AppPaths.DataDir,
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            string chosen = dlg.SelectedPath;
            // Guard against the user picking the install root itself, which
            // would conflate user data with volatile install-local state.
            if (string.Equals(
                    Path.TrimEndingDirectorySeparator(chosen),
                    Path.TrimEndingDirectorySeparator(AppPaths.Root),
                    StringComparison.OrdinalIgnoreCase))
            {
                statusLabel.ForeColor = theme.ErrorColor;
                statusLabel.Text = "Pick a subfolder — the install root itself is not allowed.";
                return;
            }

            // No-op if the user picked the folder we're already using.
            if (string.Equals(
                    Path.TrimEndingDirectorySeparator(chosen),
                    Path.TrimEndingDirectorySeparator(AppPaths.DataDir),
                    StringComparison.OrdinalIgnoreCase))
            {
                statusLabel.ForeColor = theme.TextSecondary;
                statusLabel.Text = "That is already the current data folder.";
                return;
            }

            MigrateAndRestart(theme, statusLabel, AppPaths.DataDir, chosen);
        };

        resetBtn.Click += (_, _) =>
        {
            if (!AppPaths.IsDataDirRedirected) return;
            MigrateAndRestart(theme, statusLabel, AppPaths.DataDir, AppPaths.DefaultDataDir, resetToDefault: true);
        };
    }

    /// <summary>
    /// Confirm with the user, copy the data folder into the new destination
    /// with a progress dialog, persist the override, and restart the app so
    /// every path consumer (settings, plugin data dirs, etc.) picks up the
    /// new root. The restart is the enforcement mechanism — <see cref="AppPaths"/>
    /// resolves paths once at startup into static fields.
    /// </summary>
    private void MigrateAndRestart(
        PopupTheme theme,
        Label statusLabel,
        string source,
        string dest,
        bool resetToDefault = false)
    {
        string title = resetToDefault ? "Reset to default data folder?" : "Move data folder?";
        string message =
            $"Do you want to copy the files into the new directory?\n\n" +
            $"From:\n  {source}\nTo:\n  {dest}\n\n" +
            "Yes — copy existing files (overwriting any at the destination), then restart.\n" +
            "No — switch to the new folder without copying, then restart.\n" +
            "Cancel — keep the current data folder.";
        var confirm = MessageBox.Show(this, message, title,
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button3);
        if (confirm == DialogResult.Cancel) return;

        if (confirm == DialogResult.Yes)
        {
            using var dlg = new DataMigrationDialog(theme, source, dest);
            dlg.ShowDialog(this);
            if (!dlg.Success)
            {
                statusLabel.ForeColor = theme.ErrorColor;
                statusLabel.Text = $"Copy failed: {dlg.Error ?? "unknown error"}. Data folder unchanged.";
                return;
            }
        }

        AppPaths.SetDataDir(resetToDefault ? null : dest);

        statusLabel.ForeColor = theme.SuccessColor;
        statusLabel.Text = confirm == DialogResult.Yes
            ? "Copy complete. Restarting ProdToy..."
            : "Data folder switched. Restarting ProdToy...";
        Application.DoEvents();

        // Application.Restart() is cleanest here — it spawns a new process and
        // exits this one, which lets static AppPaths fields re-resolve against
        // the freshly-written data-location.json.
        Application.Restart();
        Environment.Exit(0);
    }

    private static TabPage CreateTabPage(string text, PopupTheme theme)
    {
        return new TabPage(text)
        {
            BackColor = theme.BgDark,
            ForeColor = theme.TextPrimary,
        };
    }

    private void OnThemeComboDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var theme = (PopupTheme)_themeCombo.Items[e.Index];

        bool selected = (e.State & DrawItemState.Selected) != 0;
        var bgColor = selected ? _currentTheme.Primary : _currentTheme.BgHeader;
        var txtColor = selected ? Color.White : _currentTheme.TextPrimary;

        using (var bgBrush = new SolidBrush(bgColor))
            e.Graphics.FillRectangle(bgBrush, e.Bounds);

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int circleSize = 18;
        int circleY = e.Bounds.Y + (e.Bounds.Height - circleSize) / 2;
        var circleRect = new Rectangle(e.Bounds.X + 8, circleY, circleSize, circleSize);

        bool isLight = theme.BgDark.GetBrightness() > 0.5f;
        var fillColor = isLight ? theme.BgDark : theme.Primary;
        using (var brush = new SolidBrush(fillColor))
            g.FillEllipse(brush, circleRect);

        if (isLight)
        {
            using var borderPen = new Pen(theme.Border, 1f);
            g.DrawEllipse(borderPen, circleRect);
        }

        using var textBrush = new SolidBrush(txtColor);
        var textRect = new Rectangle(e.Bounds.X + 34, e.Bounds.Y, e.Bounds.Width - 34, e.Bounds.Height);
        using var sf = new StringFormat { LineAlignment = StringAlignment.Center };
        g.DrawString(theme.Name, e.Font ?? Font, textBrush, textRect, sf);
    }

    private void OnThemeComboChanged(object? sender, EventArgs e)
    {
        if (_themeCombo.SelectedItem is not PopupTheme theme) return;

        _currentTheme = theme;

        _themeNameLabel.Text = theme.Name;
        _themeNameLabel.ForeColor = theme.Primary;
        _themePreview.BackColor = theme.Primary;

        ApplyThemeToForm(theme);

        Themes.Save(theme);
        ThemeChanged?.Invoke(theme);
    }

    private void ApplyGlobalFont(string fontFamily)
    {
        SuspendLayout();
        try
        {
            var newFont = new Font(fontFamily, 10f);
            Font = newFont;
            foreach (Control c in Controls)
                ApplyFontRecursive(c, fontFamily);
        }
        catch { }
        ResumeLayout();
        Invalidate(true);
    }

    private static void ApplyFontRecursive(Control control, string fontFamily)
    {
        try
        {
            control.Font = new Font(fontFamily, control.Font.Size, control.Font.Style);
        }
        catch { }
        foreach (Control child in control.Controls)
            ApplyFontRecursive(child, fontFamily);
    }

    private void ApplyThemeToForm(PopupTheme theme)
    {
        SuspendLayout();

        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Icon = Themes.CreateAppIcon(theme.Primary);

        _titleLabel.ForeColor = theme.TextPrimary;
        _accentLine.BackColor = theme.Primary;

        _tabControl.Theme = theme;
        _tabControl.Invalidate();

        foreach (TabPage page in _tabControl.TabPages)
        {
            page.BackColor = theme.BgDark;
            page.ForeColor = theme.TextPrimary;
            RecolorControls(page.Controls, theme);
        }

        _themeCombo.BackColor = theme.BgHeader;
        _themeCombo.ForeColor = theme.TextPrimary;
        _themeCombo.Invalidate();

        ResumeLayout();
        Invalidate(true);
    }

    private static void RecolorControls(Control.ControlCollection controls, PopupTheme theme)
    {
        foreach (Control c in controls)
        {
            switch (c)
            {
                case Label lbl when lbl.Font.Bold && lbl.Font.Size < 10:
                    lbl.ForeColor = theme.TextSecondary;
                    break;
                case Label lbl:
                    if (lbl.ForeColor != theme.Primary)
                        lbl.ForeColor = lbl.Font.Size <= 8.5f ? theme.TextSecondary : theme.TextPrimary;
                    break;
                case CheckBox cb:
                    cb.ForeColor = theme.TextPrimary;
                    break;
                case TextBox tb:
                    tb.BackColor = theme.BgHeader;
                    tb.ForeColor = theme.TextPrimary;
                    break;
                case RoundedButton btn:
                    if (btn.ForeColor != Color.White)
                    {
                        btn.BackColor = theme.PrimaryDim;
                        btn.ForeColor = theme.TextSecondary;
                        btn.FlatAppearance.MouseOverBackColor = theme.Primary;
                    }
                    break;
                case Panel panel when panel.Height == 1:
                    panel.BackColor = theme.Border;
                    break;
            }
        }
    }

    private static Label CreateSubSectionLabel(string text, int x, int y, PopupTheme theme)
    {
        return new Label
        {
            Text = text,
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(x + 4, y),
            BackColor = Color.Transparent,
        };
    }

    private Label CreateSectionLabel(string text, int x, int y)
    {
        // Add letter spacing for better readability
        string spaced = string.Join(" ", text.ToCharArray());
        return new Label
        {
            Text = spaced,
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
            ForeColor = _currentTheme.Primary,
            AutoSize = true,
            Location = new Point(x, y),
            BackColor = Color.Transparent,
        };
    }

    private Panel CreateSeparator(int x, int y, int width)
    {
        return new Panel
        {
            BackColor = _currentTheme.Border,
            Location = new Point(x, y),
            Size = new Size(width, 1),
        };
    }

    private static string FormatDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return "not present";
        long bytes = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { bytes += new FileInfo(f).Length; }
                catch { /* file may have vanished between enumeration and stat — skip */ }
            }
        }
        catch { return "unavailable"; }

        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }

    private static void SetStartWithWindows(bool enabled)
    {
        const string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "ProdToy";

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            if (key == null) return;

            if (enabled)
                key.SetValue(valueName, $"\"{AppPaths.ExePath}\"");
            else
                key.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Warn($"SetStartWithWindows failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Owner-drawn TabControl that renders tabs with theme colors.
/// </summary>
class ThemedTabControl : TabControl
{
    public PopupTheme Theme { get; set; }

    public ThemedTabControl(PopupTheme theme)
    {
        Theme = theme;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.DoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var bgBrush = new SolidBrush(Theme.BgDark))
            g.FillRectangle(bgBrush, ClientRectangle);

        if (TabCount == 0) return;

        var pageRect = GetPageBounds();
        using (var borderPen = new Pen(Theme.Border, 1f))
            g.DrawRectangle(borderPen, pageRect);

        for (int i = 0; i < TabCount; i++)
        {
            var tabRect = GetTabRect(i);
            bool isActive = SelectedIndex == i;

            if (isActive)
            {
                using (var activeBrush = new SolidBrush(Theme.BgDark))
                    g.FillRectangle(activeBrush, tabRect);

                using (var accentPen = new Pen(Theme.Primary, 2.5f))
                    g.DrawLine(accentPen, tabRect.Left + 2, tabRect.Top + 1, tabRect.Right - 2, tabRect.Top + 1);

                using var borderPen = new Pen(Theme.Border, 1f);
                g.DrawLine(borderPen, tabRect.Left, tabRect.Top, tabRect.Left, tabRect.Bottom);
                g.DrawLine(borderPen, tabRect.Right, tabRect.Top, tabRect.Right, tabRect.Bottom);
                g.DrawLine(borderPen, tabRect.Left, tabRect.Top, tabRect.Right, tabRect.Top);

                using (var erasePen = new Pen(Theme.BgDark, 2f))
                    g.DrawLine(erasePen, tabRect.Left + 1, tabRect.Bottom, tabRect.Right - 1, tabRect.Bottom);
            }
            else
            {
                using (var inactiveBrush = new SolidBrush(Theme.BgHeader))
                    g.FillRectangle(inactiveBrush, tabRect.X, tabRect.Y + 2, tabRect.Width, tabRect.Height - 2);

                using var borderPen = new Pen(Theme.Border, 1f);
                g.DrawLine(borderPen, tabRect.Left, tabRect.Bottom, tabRect.Right, tabRect.Bottom);
            }

            var textColor = isActive ? Theme.TextPrimary : Theme.TextSecondary;
            using var textBrush = new SolidBrush(textColor);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(TabPages[i].Text, Font, textBrush, tabRect, sf);
        }
    }

    private Rectangle GetPageBounds()
    {
        if (TabCount == 0) return ClientRectangle;
        var firstTab = GetTabRect(0);
        int tabStripHeight = firstTab.Bottom;
        return new Rectangle(0, tabStripHeight, Width - 1, Height - tabStripHeight - 1);
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) { }
}
