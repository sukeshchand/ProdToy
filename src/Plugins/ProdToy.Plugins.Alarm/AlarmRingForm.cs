using System.Diagnostics;
using System.Drawing;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ProdToy.Sdk;

namespace ProdToy.Plugins.Alarm;

/// <summary>
/// The window an alarm opens when it fires. Resizable (at least 420 x 320), remembers its size and
/// position per screen. Header: icon, title, priority strip, schedule line. Body: a web view for
/// markdown and html (served from the alarm's folder so relative images load), a scrolling text box
/// for text. Footer: snooze length + Snooze, Open folder, Dismiss. In preview mode nothing is
/// written to the history and there is no snooze.
/// </summary>
class AlarmRingForm : Form
{
    private readonly AlarmEntry _alarm;
    private readonly AlarmBodyContent _content;
    private readonly PluginTheme _theme;
    private readonly bool _preview;
    private readonly string? _webViewDataFolder;
    private readonly RingWindowPlacement? _placement;
    private readonly System.Windows.Forms.Timer _autoCloseTimer;
    private readonly Panel _bodyHost;
    private WebView2? _webView;
    private Rectangle _screenBounds;

    public event Action? Dismissed;
    public event Action<int>? Snoozed;

    public AlarmRingForm(AlarmEntry alarm, PluginTheme theme, string globalFont,
        AlarmBodyContent? content = null, string? webViewDataFolder = null,
        RingWindowPlacement? placement = null, bool preview = false)
    {
        _alarm = alarm;
        _preview = preview;
        _webViewDataFolder = webViewDataFolder;
        _placement = placement;

        // Defensive: if the host passed a null theme, fall back to hard-coded
        // dark colors so the form at least renders.
        if (theme == null)
        {
            PluginLog.Warn("AlarmRingForm: theme was null, falling back to defaults");
            theme = new PluginTheme(
                Name: "fallback",
                BgDark:        Color.FromArgb(0x1e, 0x1e, 0x24),
                BgHeader:      Color.FromArgb(0x2a, 0x2a, 0x32),
                Primary:       Color.FromArgb(0x5f, 0xc7, 0x6e),
                PrimaryLight:  Color.FromArgb(0x7f, 0xd7, 0x8e),
                PrimaryDim:    Color.FromArgb(0x3a, 0x5a, 0x4a),
                TextPrimary:   Color.FromArgb(0xe6, 0xe6, 0xee),
                TextSecondary: Color.FromArgb(0x9a, 0x9a, 0xa8),
                Border:        Color.FromArgb(0x3a, 0x3a, 0x44),
                SuccessColor:  Color.FromArgb(0x5f, 0xc7, 0x6e),
                ErrorColor:    Color.FromArgb(0xe7, 0x48, 0x56),
                SuccessBg:     Color.FromArgb(0x24, 0x3a, 0x2a),
                ErrorBg:       Color.FromArgb(0x3a, 0x24, 0x24));
        }
        _theme = theme;
        _content = content ?? new AlarmBodyContent(AlarmBodyKind.Text, alarm.Message, null, null, null);

        Text = preview ? $"Alarm preview - {alarm.Title}" : $"Alarm - {alarm.Title}";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = !preview;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = RingWindowPlacement.MinimumSize;
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 10f);
        try { Icon = IconHelper.CreateAppIcon(theme.Primary); }
        catch (Exception ex) { PluginLog.Warn($"AlarmRingForm: CreateAppIcon failed: {ex.Message}"); }

        PlaceOnScreen();

        // ── header ──
        var header = new Panel { Dock = DockStyle.Top, Height = 74, BackColor = theme.BgHeader, Padding = new Padding(0) };
        var strip = new Panel { Dock = DockStyle.Left, Width = 6, BackColor = AlarmRingText.PriorityColor(alarm.Priority, theme) };
        var titleLabel = new Label
        {
            Text = $"⏰  {alarm.Title}",
            Font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold),
            ForeColor = theme.Primary,
            AutoEllipsis = true,
            Location = new Point(18, 8),
            Height = 34,
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
            BackColor = Color.Transparent,
        };
        var scheduleLabel = new Label
        {
            Text = AlarmRingText.ScheduleLine(alarm, preview) + (preview ? "" : $"  ·  {DateTime.Now:HH:mm:ss}"),
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextSecondary,
            AutoEllipsis = true,
            Location = new Point(20, 44),
            Height = 20,
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
            BackColor = Color.Transparent,
        };
        header.Controls.Add(titleLabel);
        header.Controls.Add(scheduleLabel);
        header.Controls.Add(strip);
        header.Resize += (_, _) =>
        {
            titleLabel.Width = Math.Max(50, header.ClientSize.Width - 30);
            scheduleLabel.Width = Math.Max(50, header.ClientSize.Width - 30);
        };

        // ── footer ──
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(12, 10, 12, 10),
            BackColor = theme.BgDark,
        };
        var dismissButton = MakeButton(preview ? "Close" : "Dismiss", theme.Primary, Color.White, theme.PrimaryLight, 110, bold: true);
        dismissButton.Click += (_, _) =>
        {
            if (!_preview) Dismissed?.Invoke();
            Close();
        };
        footer.Controls.Add(dismissButton);

        if (_content.Folder != null)
        {
            var openFolder = MakeButton("Open folder", theme.PrimaryDim, theme.TextPrimary, theme.Primary, 110);
            openFolder.Click += (_, _) => OpenFolder(_content.Folder);
            footer.Controls.Add(openFolder);
        }

        if (!preview)
        {
            var snoozeButton = MakeButton("Snooze", theme.PrimaryDim, theme.TextPrimary, theme.Primary, 90);
            var snoozeCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9f),
                BackColor = theme.BgHeader,
                ForeColor = theme.TextPrimary,
                FlatStyle = FlatStyle.Flat,
                Width = 90,
                Margin = new Padding(12, 6, 4, 0),
            };
            var lengths = new[] { 5, 10, 15, 30, 60 };
            foreach (var m in lengths) snoozeCombo.Items.Add(m < 60 ? $"{m} min" : "1 hour");
            int idx = Array.IndexOf(lengths, alarm.SnoozeMinutes);
            snoozeCombo.SelectedIndex = idx >= 0 ? idx : 0;
            snoozeButton.Click += (_, _) =>
            {
                int minutes = snoozeCombo.SelectedIndex >= 0 ? lengths[snoozeCombo.SelectedIndex] : 5;
                Snoozed?.Invoke(minutes);
                Close();
            };
            footer.Controls.Add(snoozeButton);
            footer.Controls.Add(snoozeCombo);
        }

        var rule = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = theme.Border };

        // ── body ──
        _bodyHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 10, 10, 6), BackColor = theme.BgDark };
        if (_content.Kind == AlarmBodyKind.Text) ShowTextBody(_content.Text);
        else CreateWebView();

        Controls.Add(_bodyHost);
        Controls.Add(rule);
        Controls.Add(footer);
        Controls.Add(header);

        _autoCloseTimer = new System.Windows.Forms.Timer { Interval = 300000 };
        _autoCloseTimer.Tick += (_, _) =>
        {
            _autoCloseTimer.Stop();
            if (!_preview)
            {
                AlarmStore.AddHistoryEntry(new AlarmHistoryEntry
                {
                    AlarmId = alarm.Id,
                    AlarmTitle = alarm.Title,
                    EventType = AlarmHistoryEventType.Missed,
                    Detail = "Auto-closed after 5 minutes without interaction",
                });
            }
            Close();
        };
        if (!preview) _autoCloseTimer.Start();

        Shown += (_, _) =>
        {
            if (!_preview) TopMost = true;
            Activate();
            try { AlarmNativeMethods.SetForegroundWindow(Handle); }
            catch (Exception ex) { PluginLog.Warn($"SetForegroundWindow failed: {ex.Message}"); }
            if (_webView != null) InitializeWebView();
        };

        FormClosing += (_, _) =>
        {
            if (WindowState == FormWindowState.Normal)
                _placement?.Save(_screenBounds, Bounds);
        };

        FormClosed += (_, _) =>
        {
            _autoCloseTimer.Stop();
            _autoCloseTimer.Dispose();
            try { _webView?.Dispose(); } catch { }
        };

        if (!string.IsNullOrEmpty(globalFont) && globalFont != "Segoe UI")
        {
            try
            {
                foreach (Control c in Controls)
                    ApplyFontRecursive(c, globalFont);
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"AlarmRingForm: global font '{globalFont}' apply failed: {ex.Message}");
            }
        }
    }

    private void PlaceOnScreen()
    {
        var screen = Screen.FromPoint(Cursor.Position) ?? Screen.PrimaryScreen!;
        _screenBounds = screen.Bounds;
        var saved = _placement?.Load(_screenBounds);
        Bounds = RingWindowPlacement.Fit(saved, screen.WorkingArea);
    }

    private RoundedButton MakeButton(string text, Color back, Color fore, Color hover, int width, bool bold = false)
    {
        var b = new RoundedButton
        {
            Text = text,
            Font = new Font("Segoe UI Semibold", bold ? 10f : 9f, FontStyle.Bold),
            Size = new Size(width, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = fore,
            Cursor = Cursors.Hand,
            Margin = new Padding(6, 0, 0, 0),
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = hover;
        return b;
    }

    private void ShowTextBody(string text)
    {
        _bodyHost.Controls.Clear();
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            BackColor = _theme.BgDark,
            ForeColor = _theme.TextPrimary,
            Font = new Font("Segoe UI", 11f),
            Text = (text ?? "").Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
            TabStop = false,
        };
        _bodyHost.Controls.Add(box);
        box.Select(0, 0);
    }

    private void CreateWebView()
    {
        try
        {
            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = _theme.BgDark,
            };
            if (_webViewDataFolder != null)
                _webView.CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = _webViewDataFolder };
            _bodyHost.Controls.Add(_webView);
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"AlarmRingForm: web view could not be created, showing the raw body: {ex.Message}");
            _webView = null;
            ShowTextBody(_content.Text);
        }
    }

    private async void InitializeWebView()
    {
        if (_webView == null) return;
        try
        {
            await _webView.EnsureCoreWebView2Async(null).ConfigureAwait(true);
            if (IsDisposed) return;
            var core = _webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.AddWebResourceRequestedFilter($"{AlarmBodyView.BaseUrl}*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenExternal(e.Uri);
            };
            core.Navigate(AlarmBodyView.StartUrl(_content));
        }
        catch (Exception ex)
        {
            PluginLog.Error("AlarmRingForm: web view init failed, showing the raw body", ex);
            try { _webView.Dispose(); } catch { }
            _webView = null;
            ShowTextBody(_content.Text);
        }
    }

    /// <summary>Serves the body page and the files of the alarm's folder; nothing outside it.</summary>
    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var env = _webView?.CoreWebView2?.Environment;
        if (env == null) return;
        try
        {
            var uri = new Uri(e.Request.Uri);
            string path = uri.AbsolutePath;
            const string headers = "Cache-Control: no-store";

            if (path == "/" + AlarmBodyView.MarkdownPage && _content.Html != null)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(_content.Html);
                e.Response = env.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK",
                    $"Content-Type: text/html; charset=utf-8\r\n{headers}");
                return;
            }

            string? file = _content.Folder != null ? AlarmBodyFiles.ResolveInFolder(_content.Folder, path) : null;
            if (file == null)
            {
                e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", headers);
                return;
            }
            var stream = new MemoryStream(File.ReadAllBytes(file));
            e.Response = env.CreateWebResourceResponse(stream, 200, "OK",
                $"Content-Type: {AlarmBodyFiles.ContentTypeFor(file)}\r\n{headers}");
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"AlarmRingForm: resource request failed: {ex.Message}");
            e.Response = env.CreateWebResourceResponse(null, 500, "Error", "");
        }
    }

    /// <summary>Links that leave the body open in the default browser.</summary>
    private static void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri.StartsWith(AlarmBodyView.BaseUrl, StringComparison.OrdinalIgnoreCase)) return;
        if (e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
        e.Cancel = true;
        OpenExternal(e.Uri);
    }

    private static void OpenExternal(string uri)
    {
        if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { PluginLog.Warn($"Could not open link: {ex.Message}"); }
    }

    internal static void OpenFolder(string folder)
    {
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(folder);
            Process.Start(start);
        }
        catch (Exception ex) { PluginLog.Warn($"Could not open the alarm folder: {ex.Message}"); }
    }

    private static void ApplyFontRecursive(Control control, string fontFamily)
    {
        try { control.Font = new Font(fontFamily, control.Font.Size, control.Font.Style); } catch { }
        foreach (Control child in control.Controls)
            ApplyFontRecursive(child, fontFamily);
    }
}
