using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

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

    public event Action<PopupTheme>? ThemeChanged;
    public event Action<bool>? HistoryEnabledChanged;
    public event Action<bool>? SnoozeChanged;
    public event Action<bool>? ShowQuotesChanged;
    public event Action<string>? ScreenshotHotkeyChanged;
    public event Action<string>? GlobalFontChanged;
    public event Action<bool>? ScreenshotEnabledChanged;
    public event Action<bool>? TripleCtrlChanged;

    public SettingsForm(PopupTheme currentTheme, DateTime snoozeUntil)
    {
        _currentTheme = currentTheme;

        Text = "ProdToy Settings";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(800, 700);
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = currentTheme.BgDark;
        ForeColor = currentTheme.TextPrimary;
        Font = new Font("Segoe UI", 10f);
        Icon = Themes.CreateAppIcon(currentTheme.Primary);

        int leftMargin = 24;
        int contentWidth = 752;

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
        int tabCount = 6;
        int tabWidth = (contentWidth - 4) / tabCount;
        _tabControl = new ThemedTabControl(currentTheme)
        {
            Location = new Point(leftMargin, y),
            Size = new Size(contentWidth, 580),
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
            SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(tabWidth, 32),
            Padding = new Point(0, 0),
        };
        Controls.Add(_tabControl);

        int tp = 16; // inner padding for controls inside tab pages
        int tabInner = contentWidth - tp * 2 - 2;

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

        // =============================================
        // TAB 1: Screen Capture
        // =============================================
        var capturePage = CreateTabPage("Capture", currentTheme);
        _tabControl.TabPages.Add(capturePage);

        int sc = tp;

        var captureSettings = AppSettings.Load();
        bool captureEnabled = captureSettings.ScreenshotEnabled;

        var enableCaptureLabel = CreateSectionLabel("SCREEN CAPTURE", tp, sc);
        capturePage.Controls.Add(enableCaptureLabel);
        sc += 26;

        var captureHotkeyControls = new List<Control>();

        var enableCaptureCheck = new CheckBox
        {
            Text = "Enable screen capture",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = currentTheme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = captureEnabled,
            AutoSize = true,
            Location = new Point(tp, sc),
            Cursor = Cursors.Hand,
        };
        capturePage.Controls.Add(enableCaptureCheck);
        sc += 32;

        capturePage.Controls.Add(CreateSeparator(tp, sc, tabInner));
        sc += 14;

        var captureShortcutLabel = CreateSectionLabel("SHORTCUT KEY", tp, sc);
        capturePage.Controls.Add(captureShortcutLabel);
        captureHotkeyControls.Add(captureShortcutLabel);
        sc += 26;

        var captureShortcutHint = new Label
        {
            Text = "Global hotkey to start screen capture:",
            Font = new Font("Segoe UI", 9f),
            ForeColor = currentTheme.TextPrimary,
            AutoSize = true,
            Location = new Point(tp, sc),
            BackColor = Color.Transparent,
        };
        capturePage.Controls.Add(captureShortcutHint);
        captureHotkeyControls.Add(captureShortcutHint);
        sc += 24;

        var currentHotkey = captureSettings.ScreenshotHotkey;
        var hotkeyBox = new TextBox
        {
            Text = currentHotkey,
            Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
            ForeColor = currentTheme.Primary,
            BackColor = currentTheme.BgHeader,
            BorderStyle = BorderStyle.FixedSingle,
            Size = new Size(260, 30),
            Location = new Point(tp, sc),
            ReadOnly = true,
            TextAlign = HorizontalAlignment.Center,
            Cursor = Cursors.Hand,
        };

        bool hotkeyRecording = false;
        var hotkeyRecordButton = new RoundedButton
        {
            Text = "Change",
            Font = new Font("Segoe UI", 8.5f),
            Size = new Size(80, 30),
            Location = new Point(tp + 268, sc),
            FlatStyle = FlatStyle.Flat,
            BackColor = currentTheme.PrimaryDim,
            ForeColor = currentTheme.TextSecondary,
            Cursor = Cursors.Hand,
        };
        hotkeyRecordButton.FlatAppearance.BorderSize = 0;
        hotkeyRecordButton.FlatAppearance.MouseOverBackColor = currentTheme.Primary;

        var hotkeyClearButton = new RoundedButton
        {
            Text = "Clear",
            Font = new Font("Segoe UI", 8.5f),
            Size = new Size(60, 30),
            Location = new Point(tp + 356, sc),
            FlatStyle = FlatStyle.Flat,
            BackColor = currentTheme.PrimaryDim,
            ForeColor = currentTheme.TextSecondary,
            Cursor = Cursors.Hand,
        };
        hotkeyClearButton.FlatAppearance.BorderSize = 0;
        hotkeyClearButton.FlatAppearance.MouseOverBackColor = currentTheme.Primary;

        var hotkeyStatusLabel = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = currentTheme.TextSecondary,
            AutoSize = true,
            Location = new Point(tp, sc + 38),
            BackColor = Color.Transparent,
        };

        hotkeyRecordButton.Click += (_, _) =>
        {
            if (!hotkeyRecording)
            {
                hotkeyRecording = true;
                hotkeyBox.Text = "Press a key combination...";
                hotkeyBox.ForeColor = currentTheme.TextSecondary;
                hotkeyRecordButton.Text = "Cancel";
                hotkeyStatusLabel.Text = "Press modifier(s) + key, e.g. Ctrl+Shift+S";
            }
            else
            {
                hotkeyRecording = false;
                hotkeyBox.Text = AppSettings.Load().ScreenshotHotkey;
                hotkeyBox.ForeColor = currentTheme.Primary;
                hotkeyRecordButton.Text = "Change";
                hotkeyStatusLabel.Text = "";
            }
        };

        hotkeyBox.KeyDown += (_, e) =>
        {
            if (!hotkeyRecording) return;
            e.SuppressKeyPress = true;

            // Ignore modifier-only presses
            if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LMenu
                or Keys.RMenu or Keys.LControlKey or Keys.RControlKey or Keys.LShiftKey
                or Keys.RShiftKey or Keys.LWin or Keys.RWin)
                return;

            // Require at least one modifier
            if (!e.Control && !e.Shift && !e.Alt)
            {
                hotkeyStatusLabel.ForeColor = currentTheme.ErrorColor;
                hotkeyStatusLabel.Text = "At least one modifier (Ctrl, Shift, Alt) is required";
                return;
            }

            var parts = new List<string>();
            if (e.Control) parts.Add("Ctrl");
            if (e.Alt) parts.Add("Alt");
            if (e.Shift) parts.Add("Shift");
            parts.Add(e.KeyCode.ToString());

            string hotkey = string.Join("+", parts);
            hotkeyBox.Text = hotkey;
            hotkeyBox.ForeColor = currentTheme.Primary;
            hotkeyRecording = false;
            hotkeyRecordButton.Text = "Change";

            var settings = AppSettings.Load();
            AppSettings.Save(settings with { ScreenshotHotkey = hotkey });
            hotkeyStatusLabel.ForeColor = currentTheme.SuccessColor;
            hotkeyStatusLabel.Text = "Hotkey saved — active now";
            ScreenshotHotkeyChanged?.Invoke(hotkey);
        };

        hotkeyClearButton.Click += (_, _) =>
        {
            hotkeyRecording = false;
            hotkeyBox.Text = "(none)";
            hotkeyBox.ForeColor = currentTheme.TextSecondary;
            hotkeyRecordButton.Text = "Change";

            var settings = AppSettings.Load();
            AppSettings.Save(settings with { ScreenshotHotkey = "" });
            hotkeyStatusLabel.ForeColor = currentTheme.TextSecondary;
            hotkeyStatusLabel.Text = "Hotkey cleared";
            ScreenshotHotkeyChanged?.Invoke("");
        };

        if (string.IsNullOrEmpty(currentHotkey))
        {
            hotkeyBox.Text = "(none)";
            hotkeyBox.ForeColor = currentTheme.TextSecondary;
        }

        capturePage.Controls.Add(hotkeyBox);
        capturePage.Controls.Add(hotkeyRecordButton);
        capturePage.Controls.Add(hotkeyClearButton);
        capturePage.Controls.Add(hotkeyStatusLabel);
        captureHotkeyControls.AddRange(new Control[] { hotkeyBox, hotkeyRecordButton, hotkeyClearButton, hotkeyStatusLabel });

        // Set initial enabled state for hotkey controls
        foreach (var ctrl in captureHotkeyControls)
            ctrl.Enabled = captureEnabled;

        enableCaptureCheck.CheckedChanged += (_, _) =>
        {
            bool enabled = enableCaptureCheck.Checked;
            foreach (var ctrl in captureHotkeyControls)
                ctrl.Enabled = enabled;

            var s = AppSettings.Load();
            AppSettings.Save(s with { ScreenshotEnabled = enabled });
            ScreenshotEnabledChanged?.Invoke(enabled);
        };

        sc += 14;
        capturePage.Controls.Add(CreateSeparator(tp, sc, tabInner));
        sc += 14;

        var tripleCtrlLabel = CreateSectionLabel("QUICK OPEN", tp, sc);
        capturePage.Controls.Add(tripleCtrlLabel);
        sc += 26;

        var tripleCtrlCheck = new CheckBox
        {
            Text = "Triple Ctrl tap to open last screenshot editor",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = currentTheme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = captureSettings.TripleCtrlEnabled,
            AutoSize = true,
            Location = new Point(tp, sc),
            Cursor = Cursors.Hand,
        };
        tripleCtrlCheck.CheckedChanged += (_, _) =>
        {
            var settings = AppSettings.Load();
            AppSettings.Save(settings with { TripleCtrlEnabled = tripleCtrlCheck.Checked });
            TripleCtrlChanged?.Invoke(tripleCtrlCheck.Checked);
        };
        capturePage.Controls.Add(tripleCtrlCheck);

        // =============================================
        // TAB 2: Appearance
        // =============================================
        var appearancePage = CreateTabPage("Appearance", currentTheme);
        _tabControl.TabPages.Add(appearancePage);

        int ay = tp;

        var themeSectionLabel = CreateSectionLabel("THEME", tp, ay);
        appearancePage.Controls.Add(themeSectionLabel);
        ay += 28;

        _themeCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Segoe UI", 10f),
            BackColor = currentTheme.BgHeader,
            ForeColor = currentTheme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(tabInner, 28),
            Location = new Point(tp, ay),
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
        appearancePage.Controls.Add(_themeCombo);
        ay += 40;

        _themeNameLabel = new Label
        {
            Text = currentTheme.Name,
            Font = new Font("Segoe UI", 10f),
            ForeColor = currentTheme.Primary,
            AutoSize = true,
            Location = new Point(tp, ay),
            BackColor = Color.Transparent,
        };
        appearancePage.Controls.Add(_themeNameLabel);

        _themePreview = new Panel
        {
            BackColor = currentTheme.Primary,
            Location = new Point(tp, ay + 24),
            Size = new Size(tabInner, 4),
        };
        appearancePage.Controls.Add(_themePreview);

        // =============================================
        // TAB 3: Claude CLI
        // =============================================
        var claudePage = CreateTabPage("Claude CLI", currentTheme);
        _tabControl.TabPages.Add(claudePage);

        int cy = tp;

        // --- Conversations main heading ---
        var conversationsLabel = CreateSectionLabel("CONVERSATIONS", tp, cy);
        claudePage.Controls.Add(conversationsLabel);
        cy += 28;

        // --- Notifications sub-group ---
        var notifLabel = CreateSubSectionLabel("Notifications", tp, cy, currentTheme);
        claudePage.Controls.Add(notifLabel);
        cy += 22;

        bool notifEnabled = AppSettings.Load().NotificationsEnabled;
        var notifEnabledCheck = new CheckBox
        {
            Text = "Enable notifications",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = currentTheme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = notifEnabled,
            AutoSize = true,
            Location = new Point(tp + 8, cy),
            Cursor = Cursors.Hand,
        };
        claudePage.Controls.Add(notifEnabledCheck);
        cy += 28;

        var notifSubControls = new List<Control>();

        var notifModeLabel = new Label
        {
            Text = "Notification type:",
            Font = new Font("Segoe UI", 9f),
            ForeColor = currentTheme.TextPrimary,
            AutoSize = true,
            Enabled = notifEnabled,
            Location = new Point(tp + 8, cy + 3),
            BackColor = Color.Transparent,
        };
        claudePage.Controls.Add(notifModeLabel);
        notifSubControls.Add(notifModeLabel);

        var notifModes = new[] { "Popup", "Windows", "Popup + Windows" };
        var currentMode = AppSettings.Load().NotificationMode;
        var notifModeCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Segoe UI", 9f),
            BackColor = currentTheme.BgHeader,
            ForeColor = currentTheme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(140, 24),
            Enabled = notifEnabled,
            Location = new Point(tp + 130, cy),
        };
        foreach (var mode in notifModes)
            notifModeCombo.Items.Add(mode);
        notifModeCombo.SelectedItem = notifModes.Contains(currentMode) ? currentMode : "Popup";
        notifModeCombo.SelectedIndexChanged += (_, _) =>
        {
            var s = AppSettings.Load();
            AppSettings.Save(s with { NotificationMode = notifModeCombo.SelectedItem?.ToString() ?? "Popup" });
        };
        claudePage.Controls.Add(notifModeCombo);
        notifSubControls.Add(notifModeCombo);
        cy += 30;

        var quotesCheck = new CheckBox
        {
            Text = "Show quotes in popup header",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = currentTheme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = AppSettings.Load().ShowQuotes,
            Enabled = notifEnabled,
            AutoSize = true,
            Location = new Point(tp + 8, cy),
            Cursor = Cursors.Hand,
        };
        quotesCheck.CheckedChanged += (_, _) =>
        {
            var settings = AppSettings.Load();
            AppSettings.Save(settings with { ShowQuotes = quotesCheck.Checked });
            ShowQuotesChanged?.Invoke(quotesCheck.Checked);
        };
        claudePage.Controls.Add(quotesCheck);
        notifSubControls.Add(quotesCheck);
        cy += 28;

        bool isSnoozed = DateTime.Now < snoozeUntil;
        var snoozeCheck = new CheckBox
        {
            Text = isSnoozed
                ? $"Snoozed ({Math.Max(1, (int)(snoozeUntil - DateTime.Now).TotalMinutes)} min left)"
                : "Snooze notifications (30 min)",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = currentTheme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = isSnoozed,
            Enabled = notifEnabled,
            AutoSize = true,
            Location = new Point(tp + 8, cy),
            Cursor = Cursors.Hand,
        };

        var _snoozeUntil = snoozeUntil;
        var snoozeTimer = new System.Windows.Forms.Timer { Interval = 15000 };
        snoozeTimer.Tick += (_, _) =>
        {
            if (DateTime.Now >= _snoozeUntil && snoozeCheck.Checked)
            {
                snoozeCheck.Checked = false;
                snoozeCheck.Text = "Snooze notifications (30 min)";
                SnoozeChanged?.Invoke(false);
                snoozeTimer.Stop();
            }
            else if (snoozeCheck.Checked && DateTime.Now < _snoozeUntil)
            {
                int mins = Math.Max(1, (int)(_snoozeUntil - DateTime.Now).TotalMinutes);
                snoozeCheck.Text = $"Snoozed ({mins} min left)";
            }
        };
        if (isSnoozed) snoozeTimer.Start();

        snoozeCheck.CheckedChanged += (_, _) =>
        {
            if (snoozeCheck.Checked)
            {
                _snoozeUntil = DateTime.Now.AddMinutes(30);
                snoozeCheck.Text = "Snoozed (30 min left)";
                snoozeTimer.Start();
            }
            else
            {
                _snoozeUntil = DateTime.MinValue;
                snoozeCheck.Text = "Snooze notifications (30 min)";
                snoozeTimer.Stop();
            }
            SnoozeChanged?.Invoke(snoozeCheck.Checked);
        };

        FormClosed += (_, _) => { snoozeTimer.Stop(); snoozeTimer.Dispose(); };

        claudePage.Controls.Add(snoozeCheck);
        notifSubControls.Add(snoozeCheck);

        notifEnabledCheck.CheckedChanged += (_, _) =>
        {
            var s = AppSettings.Load();
            AppSettings.Save(s with { NotificationsEnabled = notifEnabledCheck.Checked });
            foreach (var ctrl in notifSubControls)
                ctrl.Enabled = notifEnabledCheck.Checked;
        };
        cy += 28;

        // --- Chats sub-group ---
        cy += 4;
        var chatsLabel = CreateSubSectionLabel("Chats", tp, cy, currentTheme);
        claudePage.Controls.Add(chatsLabel);
        cy += 22;

        var historyCheck = new CheckBox
        {
            Text = "Save chat history",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = currentTheme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = ResponseHistory.IsEnabled,
            AutoSize = true,
            Location = new Point(tp + 8, cy),
            Cursor = Cursors.Hand,
        };
        historyCheck.CheckedChanged += (_, _) =>
        {
            ResponseHistory.IsEnabled = historyCheck.Checked;
            HistoryEnabledChanged?.Invoke(historyCheck.Checked);
        };
        claudePage.Controls.Add(historyCheck);
        cy += 30;

        // --- Hooks sub-group ---
        cy += 4;
        var hooksLabel = CreateSubSectionLabel("Hooks", tp, cy, currentTheme);
        claudePage.Controls.Add(hooksLabel);
        cy += 22;

        var hookSettings = AppSettings.Load();

        var hookStopCheck = new CheckBox
        {
            Text = "On Stop — notify when Claude finishes a response",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = currentTheme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = hookSettings.HookStopEnabled,
            AutoSize = true,
            Location = new Point(tp + 8, cy),
            Cursor = Cursors.Hand,
        };
        hookStopCheck.CheckedChanged += (_, _) =>
        {
            var s = AppSettings.Load();
            AppSettings.Save(s with { HookStopEnabled = hookStopCheck.Checked });
            UpdateClaudeHook("Stop", null, hookStopCheck.Checked);
        };
        claudePage.Controls.Add(hookStopCheck);
        cy += 24;

        var hookNotifCheck = new CheckBox
        {
            Text = "On Notification — notify on permission/idle/question prompts",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = currentTheme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = hookSettings.HookNotificationEnabled,
            AutoSize = true,
            Location = new Point(tp + 8, cy),
            Cursor = Cursors.Hand,
        };
        hookNotifCheck.CheckedChanged += (_, _) =>
        {
            var s = AppSettings.Load();
            AppSettings.Save(s with { HookNotificationEnabled = hookNotifCheck.Checked });
            UpdateClaudeHook("Notification", "permission_prompt|idle_prompt|elicitation_dialog", hookNotifCheck.Checked);
        };
        claudePage.Controls.Add(hookNotifCheck);
        cy += 24;

        var hookPromptCheck = new CheckBox
        {
            Text = "On User Prompt — save question when you send a message",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = currentTheme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = hookSettings.HookUserPromptEnabled,
            AutoSize = true,
            Location = new Point(tp + 8, cy),
            Cursor = Cursors.Hand,
        };
        hookPromptCheck.CheckedChanged += (_, _) =>
        {
            var s = AppSettings.Load();
            AppSettings.Save(s with { HookUserPromptEnabled = hookPromptCheck.Checked });
            UpdateClaudeHook("UserPromptSubmit", null, hookPromptCheck.Checked);
        };
        claudePage.Controls.Add(hookPromptCheck);
        cy += 30;

        // --- Status Line group ---
        claudePage.Controls.Add(CreateSeparator(tp, cy, tabInner));
        cy += 10;

        var statusLineLabel = CreateSectionLabel("STATUS LINE", tp, cy);
        claudePage.Controls.Add(statusLineLabel);
        cy += 26;

        var slCheckboxes = new List<CheckBox>();

        var statusLineCheck = new CheckBox
        {
            Text = "Enable Claude Code status line",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = currentTheme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = ClaudeStatusLine.IsEnabled(),
            AutoSize = true,
            Location = new Point(tp, cy),
            Cursor = Cursors.Hand,
        };

        var statusLineHint = new Label
        {
            Text = "Shows model, branch, context usage, edit stats in Claude CLI",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = currentTheme.TextSecondary,
            AutoSize = true,
            Location = new Point(tp + 2, cy + 22),
            BackColor = Color.Transparent,
        };

        var statusLineStatus = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = currentTheme.SuccessColor,
            AutoSize = true,
            Location = new Point(tp + 2, cy + 40),
            BackColor = Color.Transparent,
        };

        statusLineCheck.CheckedChanged += (_, _) =>
        {
            try
            {
                if (statusLineCheck.Checked)
                {
                    ClaudeStatusLine.Enable();
                    statusLineStatus.ForeColor = currentTheme.SuccessColor;
                    statusLineStatus.Text = "Enabled — restart Claude Code to apply";
                }
                else
                {
                    ClaudeStatusLine.Disable();
                    statusLineStatus.ForeColor = currentTheme.TextSecondary;
                    statusLineStatus.Text = "Disabled — restart Claude Code to apply";
                }
                // Enable/disable sub-checkboxes
                foreach (var slCb in slCheckboxes)
                    slCb.Enabled = statusLineCheck.Checked;
            }
            catch (Exception ex)
            {
                statusLineStatus.ForeColor = currentTheme.ErrorColor;
                statusLineStatus.Text = $"Error: {ex.Message}";
            }
        };

        claudePage.Controls.Add(statusLineCheck);
        claudePage.Controls.Add(statusLineHint);
        claudePage.Controls.Add(statusLineStatus);
        cy += 62;

        // Status line item checkboxes
        var slItems = new (string Label, string Setting, bool Default)[]
        {
            ("Model", "SlShowModel", true),
            ("Directory", "SlShowDir", true),
            ("Branch", "SlShowBranch", true),
            ("Prompts", "SlShowPrompts", true),
            ("Context %", "SlShowContext", true),
            ("Duration", "SlShowDuration", true),
            ("Mode", "SlShowMode", true),
            ("Version", "SlShowVersion", true),
            ("Edit Stats", "SlShowEditStats", true),
        };

        var slSettings = AppSettings.Load();
        int colWidth = tabInner / 3;
        for (int i = 0; i < slItems.Length; i++)
        {
            int col = i % 3;
            int row = i / 3;
            var item = slItems[i];

            // Read current value via reflection
            var prop = typeof(AppSettingsData).GetProperty(item.Setting);
            bool isChecked = prop != null ? (bool)prop.GetValue(slSettings)! : item.Default;

            bool slEnabled = statusLineCheck.Checked;
            var cb = new CheckBox
            {
                Text = item.Label,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = currentTheme.TextPrimary,
                BackColor = Color.Transparent,
                Checked = isChecked,
                Enabled = slEnabled,
                AutoSize = true,
                Location = new Point(tp + col * colWidth, cy + row * 22),
                Cursor = Cursors.Hand,
            };
            slCheckboxes.Add(cb);
            string settingName = item.Setting;
            cb.CheckedChanged += (_, _) =>
            {
                var s = AppSettings.Load();
                // Use reflection to set the property via with expression workaround
                s = settingName switch
                {
                    "SlShowModel" => s with { SlShowModel = cb.Checked },
                    "SlShowDir" => s with { SlShowDir = cb.Checked },
                    "SlShowBranch" => s with { SlShowBranch = cb.Checked },
                    "SlShowPrompts" => s with { SlShowPrompts = cb.Checked },
                    "SlShowContext" => s with { SlShowContext = cb.Checked },
                    "SlShowDuration" => s with { SlShowDuration = cb.Checked },
                    "SlShowMode" => s with { SlShowMode = cb.Checked },
                    "SlShowVersion" => s with { SlShowVersion = cb.Checked },
                    "SlShowEditStats" => s with { SlShowEditStats = cb.Checked },
                    _ => s,
                };
                AppSettings.Save(s);
                ClaudeStatusLine.WriteConfig();
            };
            claudePage.Controls.Add(cb);
        }
        cy += (slItems.Length / 3 + 1) * 22 + 4;

        // =============================================
        // TAB 4: Advanced
        // =============================================
        var advancedPage = CreateTabPage("Advanced", currentTheme);
        _tabControl.TabPages.Add(advancedPage);
        int uy = tp;

        var uninstallSectionLabel = CreateSectionLabel("UNINSTALL", tp, uy);
        advancedPage.Controls.Add(uninstallSectionLabel);
        uy += 28;

        var uninstallDesc = new Label
        {
            Text = "Remove ProdToy from the tools folder and clean up hook\nentries from Claude Code settings. Your response history\nand app settings will be preserved.",
            Font = new Font("Segoe UI", 9f),
            ForeColor = currentTheme.TextSecondary,
            AutoSize = true,
            Location = new Point(tp, uy),
            BackColor = Color.Transparent,
        };
        advancedPage.Controls.Add(uninstallDesc);
        uy += 60;

        var uninstallButton = new RoundedButton
        {
            Text = "Uninstall ProdToy",
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            Size = new Size(200, 34),
            Location = new Point(tp, uy),
            FlatStyle = FlatStyle.Flat,
            BackColor = currentTheme.ErrorColor,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
        };
        uninstallButton.FlatAppearance.BorderSize = 0;
        uninstallButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(
            Math.Min(255, currentTheme.ErrorColor.R + 30),
            Math.Min(255, currentTheme.ErrorColor.G + 10),
            Math.Min(255, currentTheme.ErrorColor.B + 10));
        uninstallButton.Click += (_, _) =>
        {
            using var uninstallForm = new UninstallForm();
            uninstallForm.ShowDialog(this);
        };
        advancedPage.Controls.Add(uninstallButton);

        // =============================================
        // TAB 5: About
        // =============================================
        var aboutPage = CreateTabPage("About", currentTheme);
        _tabControl.TabPages.Add(aboutPage);
        int ab = tp;

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
        aboutPage.Controls.Add(appIconPanel);

        var aboutNameLabel = new Label
        {
            Text = "ProdToy",
            Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold),
            ForeColor = currentTheme.TextPrimary,
            AutoSize = true,
            Location = new Point(tp + 58, ab),
            BackColor = Color.Transparent,
        };
        aboutPage.Controls.Add(aboutNameLabel);

        var aboutVersionLabel = new Label
        {
            Text = $"Version {AppVersion.Current}",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = currentTheme.TextSecondary,
            AutoSize = true,
            Location = new Point(tp + 60, ab + 28),
            BackColor = Color.Transparent,
        };
        aboutPage.Controls.Add(aboutVersionLabel);
        ab += 58;

        var aboutDescLabel = new Label
        {
            Text = "Developer utility toolkit for Windows.\nNotifications, screen capture, and productivity tools for Claude Code.",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = currentTheme.TextSecondary,
            AutoSize = true,
            MaximumSize = new Size(tabInner, 0),
            Location = new Point(tp, ab),
            BackColor = Color.Transparent,
        };
        aboutPage.Controls.Add(aboutDescLabel);
        ab += aboutDescLabel.PreferredHeight + 14;

        // --- Separator ---
        aboutPage.Controls.Add(CreateSeparator(tp, ab, tabInner));
        ab += 14;

        // --- Repository Section ---
        var repoSectionLabel = CreateSectionLabel("REPOSITORY", tp, ab);
        aboutPage.Controls.Add(repoSectionLabel);
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
        aboutPage.Controls.Add(githubIconPanel);

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
        aboutPage.Controls.Add(repoLink);
        ab += 30;

        // Info rows: Author, License, Runtime
        var infoItems = new (string label, string value)[]
        {
            ("Author", "Sukesh Chand"),
            ("License", "MIT"),
            ("Runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription),
            ("Platform", $"Windows {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}"),
        };
        foreach (var (label, value) in infoItems)
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
            aboutPage.Controls.Add(rowLabel);

            var rowValue = new Label
            {
                Text = value,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = currentTheme.TextPrimary,
                AutoSize = true,
                Location = new Point(tp + 84, ab),
                BackColor = Color.Transparent,
            };
            aboutPage.Controls.Add(rowValue);
            ab += 22;
        }
        ab += 10;

        // --- Separator ---
        aboutPage.Controls.Add(CreateSeparator(tp, ab, tabInner));
        ab += 14;

        // --- Updates Section ---
        var updateSectionLabel = CreateSectionLabel("UPDATES", tp, ab);
        aboutPage.Controls.Add(updateSectionLabel);
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
        aboutPage.Controls.Add(updatePathLabel);
        ab += 22;

        var updatePathBox = new TextBox
        {
            Text = AppSettings.Load().UpdateLocation,
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = currentTheme.TextPrimary,
            BackColor = currentTheme.BgHeader,
            BorderStyle = BorderStyle.FixedSingle,
            Size = new Size(tabInner - 40, 26),
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
        aboutPage.Controls.Add(updatePathBox);

        var savePathButton = new RoundedButton
        {
            Text = "Save",
            Font = new Font("Segoe UI", 8.5f),
            Size = new Size(34, 26),
            Location = new Point(tp + tabInner - 34, ab),
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
        aboutPage.Controls.Add(savePathButton);
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
        updateLinkLabel.Click += (_, _) =>
        {
            updateLinkLabel.Text = "Updating...";
            updateLinkLabel.Enabled = false;
            var result = Updater.Apply();
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
        aboutPage.Controls.Add(checkNowButton);
        aboutPage.Controls.Add(checkResultLabel);
        aboutPage.Controls.Add(updateLinkLabel);

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

        // Apply saved global font on open
        var savedGlobalFont = AppSettings.Load().GlobalFont;
        if (!string.IsNullOrEmpty(savedGlobalFont) && savedGlobalFont != "Segoe UI")
            ApplyGlobalFont(savedGlobalFont);
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

    /// <summary>
    /// Adds or removes the ProdToy hook from a Claude Code hook event in settings.json.
    /// </summary>
    internal static void UpdateClaudeHook(string eventName, string? matcher, bool enabled)
    {
        try
        {
            string path = AppPaths.ClaudeSettingsFile;
            if (!File.Exists(path)) return;

            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            if (root == null) return;

            var hooksNode = root["hooks"]?.AsObject();
            if (hooksNode == null) return;

            if (hooksNode[eventName] is not System.Text.Json.Nodes.JsonArray eventArray) return;

            if (!enabled)
            {
                // Remove ProdToy hook entries from this event
                for (int i = eventArray.Count - 1; i >= 0; i--)
                {
                    if (eventArray[i] is not System.Text.Json.Nodes.JsonObject ruleSet) continue;
                    if (ruleSet["hooks"] is not System.Text.Json.Nodes.JsonArray hooksArray) continue;

                    for (int j = hooksArray.Count - 1; j >= 0; j--)
                    {
                        if (IsProdToyHookCommand(hooksArray[j]?["command"]?.GetValue<string>()))
                            hooksArray.RemoveAt(j);
                    }

                    // Remove the rule set if no hooks remain
                    if (hooksArray.Count == 0)
                        eventArray.RemoveAt(i);
                }

                // Remove the event entirely if empty
                if (eventArray.Count == 0)
                    hooksNode.Remove(eventName);
            }
            else
            {
                // Re-add ProdToy hook if not already present
                bool exists = false;
                foreach (var ruleSet in eventArray)
                {
                    if (ruleSet?["hooks"] is System.Text.Json.Nodes.JsonArray hooksArray)
                    {
                        foreach (var hook in hooksArray)
                        {
                            if (IsProdToyHookCommand(hook?["command"]?.GetValue<string>()))
                            { exists = true; break; }
                        }
                    }
                    if (exists) break;
                }

                if (!exists)
                {
                    string hookCmd = $"powershell.exe -ExecutionPolicy Bypass -File \"{Path.Combine(AppPaths.ClaudeHooksDir, "Show-ProdToy.ps1")}\"";
                    var hookEntry = new System.Text.Json.Nodes.JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = hookCmd,
                    };

                    // Find a matching rule set or create one
                    bool added = false;
                    foreach (var ruleSet in eventArray)
                    {
                        if (ruleSet is not System.Text.Json.Nodes.JsonObject ruleObj) continue;
                        string? existingMatcher = ruleObj["matcher"]?.GetValue<string>();
                        if (existingMatcher == matcher)
                        {
                            var hooksArray = ruleObj["hooks"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray();
                            hooksArray.Add(System.Text.Json.Nodes.JsonNode.Parse(hookEntry.ToJsonString()));
                            ruleObj["hooks"] = hooksArray;
                            added = true;
                            break;
                        }
                    }

                    if (!added)
                    {
                        var newRuleSet = new System.Text.Json.Nodes.JsonObject();
                        if (matcher != null) newRuleSet["matcher"] = matcher;
                        newRuleSet["hooks"] = new System.Text.Json.Nodes.JsonArray
                        {
                            System.Text.Json.Nodes.JsonNode.Parse(hookEntry.ToJsonString())
                        };
                        eventArray.Add(newRuleSet);
                    }
                }
            }

            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, root.ToJsonString(options), System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"UpdateClaudeHook failed: {ex.Message}");
        }
    }

    private static bool IsProdToyHookCommand(string? command)
    {
        if (string.IsNullOrEmpty(command)) return false;
        return command.Contains("Show-ProdToy") || command.Contains("Show-DevToy");
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
            Debug.WriteLine($"SetStartWithWindows failed: {ex.Message}");
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
