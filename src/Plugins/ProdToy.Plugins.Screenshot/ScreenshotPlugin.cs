using ProdToy.Sdk;

namespace ProdToy.Plugins.Screenshot;

internal enum HotkeyApplyStatus
{
    Registered,
    Disabled,
    NoHotkey,
    Failed,
}

[Plugin("ProdToy.Plugin.Screenshot", "Screenshot", "1.0.356",
    Description = "Screen capture and annotation editor",
    Author = "ProdToy",
    MenuPriority = 100)]
public partial class ScreenshotPlugin : IPlugin, IDoctor
{
    private IPluginContext _context = null!;
    private IHotkeyRegistration? _hotkeyReg;
    private IDisposable? _tripleCtrlReg;
    private ScreenshotEditorForm? _editorForm;

    // Static accessor for theme (used by ScreenshotEditorForm internally)
    private static IPluginHost? _host;
    internal static PluginTheme GetTheme() => _host?.CurrentTheme ?? new PluginTheme(
        "Default", default, default, default, default, default, default, default, default, default, default, default, default);

    public void Install(IPluginContext context)
    {
        // No external-system state to install.
    }

    public void Uninstall(IPluginContext context)
    {
        // No external-system state to remove.
    }

    public void Initialize(IPluginContext context)
    {
        _context = context;
        _host = context.Host;
        PluginLog.Bootstrap(context);
        ScreenshotPaths.Initialize(context.DataDirectory);
    }

    public void Start() => ApplyHotkeyBindings();

    public void Stop()
    {
        TryDispose(ref _hotkeyReg);
        TryDispose(ref _tripleCtrlReg);
        _context.Host.InvokeOnUI(() =>
        {
            if (_editorForm != null && !_editorForm.IsDisposed)
            {
                _editorForm.Dispose();
                _editorForm = null;
            }
        });
    }

    /// <summary>
    /// Reload settings and (re-)register global hotkey + triple-Ctrl. Safe to
    /// call repeatedly — always disposes any existing registrations first, so
    /// settings changes take effect without restarting the host. Returns a
    /// status describing the hotkey registration outcome so the settings UI
    /// can surface conflicts/errors to the user.
    /// </summary>
    internal HotkeyApplyStatus ApplyHotkeyBindings()
    {
        TryDispose(ref _hotkeyReg);
        TryDispose(ref _tripleCtrlReg);

        var settings = _context.LoadSettings<ScreenshotPluginSettings>();

        if (settings.TripleCtrlEnabled)
        {
            try
            {
                _tripleCtrlReg = _context.Host.RegisterTripleCtrl(() =>
                    _context.Host.InvokeOnUI(EditLastScreenshot));
            }
            catch (Exception ex)
            {
                _context.LogError("Failed to register triple-Ctrl detector", ex);
            }
        }

        if (!settings.ScreenshotEnabled)
            return HotkeyApplyStatus.Disabled;

        if (string.IsNullOrEmpty(settings.ScreenshotHotkey))
            return HotkeyApplyStatus.NoHotkey;

        try
        {
            _hotkeyReg = _context.Host.RegisterHotkey(settings.ScreenshotHotkey, () =>
                _context.Host.InvokeOnUI(TakeScreenshot));
        }
        catch (Exception ex)
        {
            _context.LogError($"Hotkey '{settings.ScreenshotHotkey}' registration threw", ex);
            return HotkeyApplyStatus.Failed;
        }

        if (_hotkeyReg == null)
        {
            _context.LogError(
                $"Failed to register hotkey '{settings.ScreenshotHotkey}' — already in use or invalid");
            return HotkeyApplyStatus.Failed;
        }

        return HotkeyApplyStatus.Registered;
    }

    private static void TryDispose<T>(ref T? reg) where T : class, IDisposable
    {
        try { reg?.Dispose(); }
        catch (Exception ex) { PluginLog.Warn($"Screenshot dispose failed: {ex.Message}"); }
        reg = null;
    }

    public void Dispose() { }

    public IReadOnlyList<MenuContribution> GetMenuItems() =>
    [
        new("Take Screenshot", TakeScreenshot, Priority: 100, Icon: "\uD83D\uDCF7"),
        new("Edit Last Screenshot", EditLastScreenshot, Priority: 101, Icon: "\u270F\uFE0F"),
    ];

    public IReadOnlyList<MenuContribution> GetDashboardItems() =>
    [
        new("Take Screenshot", TakeScreenshot, Priority: 100, Icon: "\uD83D\uDCF7"),
        new("Edit Last Screenshot", EditLastScreenshot, Priority: 101, Icon: "\u270F\uFE0F"),
    ];

    public SettingsPageContribution? GetSettingsPage() =>
        new("Screenshot", () => BuildSettingsPanel(), TabOrder: 100);

    private Control BuildSettingsPanel()
    {
        var theme = _context.Host.CurrentTheme;
        var settings = _context.LoadSettings<ScreenshotPluginSettings>();

        var panel = new Panel
        {
            AutoScroll = true,
            Dock = DockStyle.Fill,
            BackColor = theme.BgDark,
        };

        int pad = 16;
        int y = pad;

        // --- SCREEN CAPTURE section ---
        var captureLabel = new Label
        {
            Text = "SCREEN CAPTURE",
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            ForeColor = theme.Primary,
            AutoSize = true,
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        panel.Controls.Add(captureLabel);
        y += 26;

        var enableCheck = new CheckBox
        {
            Text = "Enable screen capture",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = settings.ScreenshotEnabled,
            AutoSize = true,
            Location = new Point(pad + 8, y),
            Cursor = Cursors.Hand,
        };
        enableCheck.CheckedChanged += (_, _) =>
        {
            var s = _context.LoadSettings<ScreenshotPluginSettings>();
            _context.SaveSettings(s with { ScreenshotEnabled = enableCheck.Checked });
            ApplyHotkeyBindings();
        };
        panel.Controls.Add(enableCheck);
        y += 30;

        // --- SHORTCUT KEY section ---
        var hotkeyLabel = new Label
        {
            Text = "SHORTCUT KEY",
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            ForeColor = theme.Primary,
            AutoSize = true,
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        panel.Controls.Add(hotkeyLabel);
        y += 26;

        var hotkeyBox = new TextBox
        {
            Text = string.IsNullOrEmpty(settings.ScreenshotHotkey) ? "(none)" : settings.ScreenshotHotkey,
            Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
            ForeColor = theme.Primary,
            BackColor = theme.BgHeader,
            BorderStyle = BorderStyle.FixedSingle,
            Size = new Size(220, 30),
            Location = new Point(pad + 8, y),
            ReadOnly = true,
            TextAlign = HorizontalAlignment.Center,
            Cursor = Cursors.Hand,
        };

        bool recording = false;

        var changeBtn = new Button
        {
            Text = "Change",
            Font = new Font("Segoe UI", 8.5f),
            Size = new Size(70, 30),
            Location = new Point(pad + 236, y),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextSecondary,
            Cursor = Cursors.Hand,
        };
        changeBtn.FlatAppearance.BorderSize = 0;

        var clearBtn = new Button
        {
            Text = "Clear",
            Font = new Font("Segoe UI", 8.5f),
            Size = new Size(55, 30),
            Location = new Point(pad + 312, y),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextSecondary,
            Cursor = Cursors.Hand,
        };
        clearBtn.FlatAppearance.BorderSize = 0;

        var hotkeyStatus = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 8f),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(pad + 8, y + 36),
            BackColor = Color.Transparent,
        };

        changeBtn.Click += (_, _) =>
        {
            if (!recording)
            {
                recording = true;
                hotkeyBox.Text = "Press a key combination...";
                hotkeyBox.ForeColor = theme.TextSecondary;
                changeBtn.Text = "Cancel";
                hotkeyStatus.Text = "Press modifier(s) + key, e.g. Ctrl+Shift+S";
            }
            else
            {
                recording = false;
                var s = _context.LoadSettings<ScreenshotPluginSettings>();
                hotkeyBox.Text = string.IsNullOrEmpty(s.ScreenshotHotkey) ? "(none)" : s.ScreenshotHotkey;
                hotkeyBox.ForeColor = theme.Primary;
                changeBtn.Text = "Change";
                hotkeyStatus.Text = "";
            }
        };

        // PrintScreen does NOT generate WM_KEYDOWN on Windows, so we handle
        // both KeyDown (normal keys) and KeyUp (catches PrintScreen and any
        // other key that only fires on release).
        void CaptureHotkey(KeyEventArgs e)
        {
            if (!recording) return;
            e.SuppressKeyPress = true;

            if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LMenu
                or Keys.RMenu or Keys.LControlKey or Keys.RControlKey or Keys.LShiftKey
                or Keys.RShiftKey or Keys.LWin or Keys.RWin or Keys.None)
                return;

            var parts = new List<string>();
            if (e.Control) parts.Add("Ctrl");
            if (e.Alt) parts.Add("Alt");
            if (e.Shift) parts.Add("Shift");
            parts.Add(e.KeyCode.ToString());

            string hotkey = string.Join("+", parts);
            hotkeyBox.Text = hotkey;
            hotkeyBox.ForeColor = theme.Primary;
            recording = false;
            changeBtn.Text = "Change";

            var s = _context.LoadSettings<ScreenshotPluginSettings>();
            _context.SaveSettings(s with { ScreenshotHotkey = hotkey });

            var status = ApplyHotkeyBindings();
            if (status == HotkeyApplyStatus.Registered)
            {
                hotkeyStatus.ForeColor = theme.SuccessColor;
                hotkeyStatus.Text = $"Registered: {hotkey}";
            }
            else if (status == HotkeyApplyStatus.Disabled)
            {
                hotkeyStatus.ForeColor = theme.TextSecondary;
                hotkeyStatus.Text = "Saved — enable screen capture to activate";
            }
            else if (hotkey.Equals("PrintScreen", StringComparison.OrdinalIgnoreCase))
            {
                hotkeyStatus.ForeColor = theme.ErrorColor;
                hotkeyStatus.Text = "PrintScreen is owned by Windows. Disable Settings → Accessibility → Keyboard → 'Use Print screen to open Snipping Tool' and retry.";
            }
            else
            {
                hotkeyStatus.ForeColor = theme.ErrorColor;
                hotkeyStatus.Text = $"'{hotkey}' is already in use or invalid";
            }
        }

        hotkeyBox.KeyDown += (_, e) => CaptureHotkey(e);
        hotkeyBox.KeyUp += (_, e) => CaptureHotkey(e);

        clearBtn.Click += (_, _) =>
        {
            recording = false;
            hotkeyBox.Text = "(none)";
            hotkeyBox.ForeColor = theme.TextSecondary;
            changeBtn.Text = "Change";

            var s = _context.LoadSettings<ScreenshotPluginSettings>();
            _context.SaveSettings(s with { ScreenshotHotkey = "" });
            ApplyHotkeyBindings();
            hotkeyStatus.ForeColor = theme.TextSecondary;
            hotkeyStatus.Text = "Hotkey cleared";
        };

        panel.Controls.Add(hotkeyBox);
        panel.Controls.Add(changeBtn);
        panel.Controls.Add(clearBtn);
        panel.Controls.Add(hotkeyStatus);
        y += 58;

        var hotkeyNote = new Label
        {
            Text = "Hotkey applies immediately. Use PrintScreen, F13+, or any Ctrl/Alt/Shift combo.",
            Font = new Font("Segoe UI", 8f),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(pad + 8, y),
            BackColor = Color.Transparent,
        };
        panel.Controls.Add(hotkeyNote);
        y += 24;

        // --- QUICK OPEN section ---
        var tripleCtrlLabel = new Label
        {
            Text = "QUICK OPEN",
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            ForeColor = theme.Primary,
            AutoSize = true,
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        panel.Controls.Add(tripleCtrlLabel);
        y += 26;

        var tripleCtrlCheck = new CheckBox
        {
            Text = "Triple Ctrl tap to open last screenshot editor",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = settings.TripleCtrlEnabled,
            AutoSize = true,
            Location = new Point(pad + 8, y),
            Cursor = Cursors.Hand,
        };
        tripleCtrlCheck.CheckedChanged += (_, _) =>
        {
            var s = _context.LoadSettings<ScreenshotPluginSettings>();
            _context.SaveSettings(s with { TripleCtrlEnabled = tripleCtrlCheck.Checked });
            ApplyHotkeyBindings();
        };
        panel.Controls.Add(tripleCtrlCheck);

        return panel;
    }

    private void TakeScreenshot()
    {
        var overlay = new ScreenshotOverlay();
        overlay.RegionCaptured += bitmap => EnsureEditor(bitmap);
        overlay.Show();
    }

    private void EditLastScreenshot()
    {
        try
        {
            string dir = ScreenshotPaths.ScreenshotsDir;
            if (!Directory.Exists(dir)) return;

            var lastFile = Directory.GetFiles(dir, "*.png")
                .Concat(Directory.GetFiles(dir, "*.jpg"))
                .Concat(Directory.GetFiles(dir, "*.bmp"))
                .OrderByDescending(File.GetCreationTime)
                .FirstOrDefault();

            if (lastFile == null) return;
            EnsureEditor(lastFile);
        }
        catch (Exception ex)
        {
            _context.LogError("EditLastScreenshot failed", ex);
        }
    }

    private void EnsureEditor(Bitmap capturedImage)
    {
        if (_editorForm == null || _editorForm.IsDisposed)
        {
            _editorForm = new ScreenshotEditorForm(capturedImage);
            _editorForm.BringToForeground();
        }
        else
        {
            _editorForm.LoadCapture(capturedImage);
        }
    }

    private void EnsureEditor(string filePath)
    {
        if (_editorForm == null || _editorForm.IsDisposed)
        {
            _editorForm = new ScreenshotEditorForm(filePath);
            _editorForm.BringToForeground();
        }
        else
        {
            _editorForm.LoadFile(filePath);
        }
    }
}
