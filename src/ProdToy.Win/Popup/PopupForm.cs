using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ProdToy;

/// <summary>
/// Generic welcome / update-banner popup owned by the host. Phase 8 stripped
/// all Claude-specific concerns (funny quotes, sparkles, snooze checkbox,
/// notification-mode dispatch) out — those live in the Claude plugin's
/// ChatPopupForm now. What remains:
///   - Welcome screen on a no-args launch ("No notifications yet...").
///   - Update banner (host-level update checker notifies here).
///   - UI-thread anchor for PluginHostImpl.InvokeOnUI.
///   - Copy-to-clipboard links for any markdown text shown.
/// </summary>
class PopupForm : Form
{
    private PopupTheme _theme;

    private readonly Panel _infoPanel;
    private readonly Label _iconLabel;
    private readonly Label _titleLabel;
    private readonly Label _subtitleLabel;
    private readonly Label _versionLabel;
    private readonly WebView2 _messageWebView;
    private readonly Panel _footerPanel;
    private readonly Panel _webViewContainer;
    private readonly Panel _separator;
    private readonly RoundedButton _okButton;

    private Color _accentColor;
    private Color _iconBadgeBg;
    private bool _webViewReady;
    private string? _webView2UserDataFolder;
    private string? _pendingHtml;
    private bool _forceExit;
    private string _lastMessage = "";
    private string _lastType = NotificationType.Info;

    private readonly Label _updateAvailableLabel;
    private readonly Label _updateButton;

    private const int InfoBarHeight = 56;
    private const int FooterHeight = 105;

    public PopupTheme CurrentTheme => _theme;

    public event Action? ExitRequested;

    public PopupForm(PopupTheme theme)
    {
        _theme = theme;
        _accentColor = theme.Primary;
        _iconBadgeBg = theme.PrimaryDim;

        Text = "ProdToy";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = false;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        MinimumSize = new Size(400, 300);
        Icon = Themes.CreateAppIcon(theme.Primary);

        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 10f);

        // --- Info bar (icon + title + subtitle + version) ---
        _infoPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = InfoBarHeight,
            BackColor = theme.BgDark,
        };

        _iconLabel = new Label
        {
            Text = "\u2139",
            Font = new Font("Segoe UI", 20f, FontStyle.Bold),
            ForeColor = theme.Primary,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleCenter,
            Size = new Size(48, 48),
            Location = new Point(22, 4),
        };

        _titleLabel = new Label
        {
            Text = "ProdToy",
            Font = new Font("Segoe UI Semibold", 14f, FontStyle.Bold),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(82, 6),
            BackColor = Color.Transparent,
        };

        _subtitleLabel = new Label
        {
            Text = "Notification",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(82, 30),
            BackColor = Color.Transparent,
        };

        _versionLabel = new Label
        {
            Text = $"v{AppVersion.Current}",
            Font = new Font("Segoe UI", 8f),
            ForeColor = Color.FromArgb(60, 75, 105),
            AutoSize = true,
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };

        _infoPanel.Controls.AddRange(new Control[]
        {
            _iconLabel, _titleLabel, _subtitleLabel,
        });

        // --- Footer ---
        _footerPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = FooterHeight,
            BackColor = theme.BgDark,
        };

        _separator = new Panel
        {
            BackColor = theme.Border,
            Height = 1,
            Dock = DockStyle.Top,
        };

        _okButton = new RoundedButton
        {
            Text = "OK",
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            Size = new Size(130, 40),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.Primary,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Top,
        };
        _okButton.FlatAppearance.BorderSize = 0;
        _okButton.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        _okButton.FlatAppearance.MouseDownBackColor = theme.PrimaryDim;
        _okButton.Click += (_, _) => Hide();

        // --- Update notification (bottom-left of footer, hidden by default) ---
        _updateAvailableLabel = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = theme.SuccessColor,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(20, 88),
            Visible = false,
        };

        _updateButton = new Label
        {
            Text = "Update",
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Underline | FontStyle.Bold),
            ForeColor = theme.Primary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Cursor = Cursors.Hand,
            Visible = false,
        };
        _updateButton.Click += (_, _) => OnUpdateClick();

        _footerPanel.Controls.Add(_separator);
        _footerPanel.Controls.Add(_okButton);
        _footerPanel.Controls.Add(_updateAvailableLabel);
        _footerPanel.Controls.Add(_updateButton);
        _footerPanel.Controls.Add(_versionLabel);

        // --- WebView content area ---
        _messageWebView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = theme.BgDark,
        };

        _webViewContainer = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 8, 16, 24),
            BackColor = theme.BgDark,
        };
        _webViewContainer.Controls.Add(_messageWebView);

        ClientSize = new Size(800, 500);
        AcceptButton = _okButton;

        // WinForms docks last-added-first. Order matters:
        //   Top: infoPanel (below anything else added top)
        //   Bottom: footerPanel
        //   Fill: webViewContainer
        Controls.Add(_webViewContainer);
        Controls.Add(_footerPanel);
        Controls.Add(_infoPanel);

        Shown += (_, _) => PositionControls();

        InitializeWebView2();

        var savedFont = AppSettings.Load().GlobalFont;
        if (!string.IsNullOrEmpty(savedFont) && savedFont != "Segoe UI")
            ApplyGlobalFont(savedFont);
    }

    private void PositionControls()
    {
        if (_footerPanel == null || _okButton == null || _versionLabel == null) return;

        int footerW = _footerPanel.ClientSize.Width;

        _okButton.Location = new Point((footerW - _okButton.Width) / 2, 14);

        _versionLabel.Location = new Point(
            footerW - _versionLabel.Width - 12,
            _footerPanel.ClientSize.Height - _versionLabel.Height - 6);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        PositionControls();
        Invalidate();
    }

    private async void InitializeWebView2()
    {
        try
        {
            _webView2UserDataFolder = Path.Combine(Path.GetTempPath(), "ProdToy_" + Environment.ProcessId);
            var env = await CoreWebView2Environment.CreateAsync(null, _webView2UserDataFolder);
            await _messageWebView.EnsureCoreWebView2Async(env);
            _messageWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _messageWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _messageWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _messageWebView.CoreWebView2.Settings.IsZoomControlEnabled = false;
            _webViewReady = true;

            if (_pendingHtml != null)
            {
                _messageWebView.NavigateToString(_pendingHtml);
                _pendingHtml = null;
            }
        }
        catch (Exception ex)
        {
            Log.Error("PopupForm WebView2 init failed", ex);
        }
    }

    public void ApplyTheme(PopupTheme theme)
    {
        _theme = theme;

        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        var oldIcon = Icon;
        Icon = Themes.CreateAppIcon(theme.Primary);
        oldIcon?.Dispose();

        _infoPanel.BackColor = theme.BgDark;
        _titleLabel.ForeColor = theme.TextPrimary;
        _subtitleLabel.ForeColor = theme.TextSecondary;
        _separator.BackColor = theme.Border;
        _footerPanel.BackColor = theme.BgDark;
        _webViewContainer.BackColor = theme.BgDark;
        _messageWebView.DefaultBackgroundColor = theme.BgDark;

        _okButton.BackColor = theme.Primary;
        _okButton.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        _okButton.FlatAppearance.MouseDownBackColor = theme.PrimaryDim;

        _updateAvailableLabel.ForeColor = theme.SuccessColor;
        _updateButton.ForeColor = theme.Primary;

        ApplyTypeColors(_lastType);

        if (_webViewReady && !string.IsNullOrEmpty(_lastMessage))
        {
            _messageWebView.NavigateToString(RenderHtml(_lastMessage));
        }

        Invalidate();
    }

    public void ApplyGlobalFont(string fontFamily)
    {
        try
        {
            SuspendLayout();
            Font = new Font(fontFamily, Font.Size, Font.Style);
            ApplyFontRecursive(this, fontFamily);
            ResumeLayout();
            Invalidate(true);
        }
        catch { }
    }

    private static void ApplyFontRecursive(Control control, string fontFamily)
    {
        foreach (Control child in control.Controls)
        {
            try { child.Font = new Font(fontFamily, child.Font.Size, child.Font.Style); } catch { }
            ApplyFontRecursive(child, fontFamily);
        }
    }

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private string RenderHtml(string message)
    {
        bool isLight = _theme.BgDark.GetBrightness() > 0.5f;
        return MarkdownRenderer.ToHtml(
            message,
            accentColorHex: ToHex(_accentColor),
            textColorHex: ToHex(isLight ? _theme.TextPrimary : _theme.TextSecondary),
            headingColorHex: ToHex(_theme.TextPrimary),
            bgColorHex: ToHex(_theme.BgDark),
            codeBgHex: isLight ? "rgba(0,0,0,0.06)" : "rgba(12, 16, 26, 0.8)",
            themePrimaryHex: ToHex(_theme.Primary));
    }

    private void ApplyTypeColors(string type)
    {
        var (accentColor, iconText, iconBadgeBg, subtitle) = type switch
        {
            NotificationType.Success => (_theme.SuccessColor, "\u2713", _theme.SuccessBg, "Completed successfully"),
            NotificationType.Error => (_theme.ErrorColor, "\u2717", _theme.ErrorBg, "An error occurred"),
            _ => (_theme.Primary, "\u2139", _theme.PrimaryDim, "Notification"),
        };
        _accentColor = accentColor;
        _iconBadgeBg = iconBadgeBg;
        _iconLabel.Text = iconText;
        _iconLabel.ForeColor = accentColor;
        _subtitleLabel.Text = subtitle;
    }

    public void ShowPopup(string title, string message, string type)
    {
        _lastMessage = message;
        _lastType = type;

        DisplayMessage(title, message, type);

        Show();
        WindowState = FormWindowState.Normal;
        BringToTop();
        Invalidate();
    }

    private void BringToTop()
    {
        TopMost = true;
        NativeMethods.ShowWindow(Handle, NativeMethods.SW_RESTORE);
        NativeMethods.SetForegroundWindow(Handle);
        BringToFront();
        Activate();
        var releaseTimer = new System.Windows.Forms.Timer { Interval = 500 };
        releaseTimer.Tick += (_, _) =>
        {
            TopMost = false;
            releaseTimer.Stop();
            releaseTimer.Dispose();
        };
        releaseTimer.Start();
    }

    private void DisplayMessage(string title, string message, string type)
    {
        _lastMessage = message;
        ApplyTypeColors(type);
        Text = title;
        _titleLabel.Text = title;

        string htmlContent = RenderHtml(message);

        var lineCount = message.Split('\n').Length;
        int wrappedLines = lineCount;
        foreach (var line in message.Split('\n'))
        {
            if (line.Length > 80)
                wrappedLines += (line.Length / 80);
        }
        int estimatedContentHeight = Math.Max(180, wrappedLines * 28 + 60);
        var workingArea = Screen.FromControl(this).WorkingArea;
        int maxHeight = (int)(workingArea.Height * 0.9);
        int newClientW = Math.Max(ClientSize.Width, 600);
        int newClientH = Math.Min(maxHeight, InfoBarHeight + estimatedContentHeight + FooterHeight);
        ClientSize = new Size(newClientW, newClientH);

        int curLeft = Left, curTop = Top;
        if (curLeft < workingArea.Left || curTop < workingArea.Top ||
            curLeft + Width > workingArea.Right || curTop + Height > workingArea.Bottom)
        {
            Location = new Point(
                Math.Clamp(curLeft, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - Width)),
                Math.Clamp(curTop, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - Height)));
        }

        if (_webViewReady)
            _messageWebView.NavigateToString(htmlContent);
        else
            _pendingHtml = htmlContent;

        PositionControls();
    }

    public void BringToForeground()
    {
        Show();
        WindowState = FormWindowState.Normal;
        BringToTop();
    }

    public void ForceExit()
    {
        _forceExit = true;
        try
        {
            if (_webView2UserDataFolder != null && Directory.Exists(_webView2UserDataFolder))
                Directory.Delete(_webView2UserDataFolder, true);
        }
        catch (Exception ex)
        {
            Log.Warn($"PopupForm WebView2 cleanup failed: {ex.Message}");
        }
        Close();
    }

    public void ShowUpdateAvailable(UpdateMetadata metadata)
    {
        if (InvokeRequired)
        {
            Invoke(() => ShowUpdateAvailable(metadata));
            return;
        }

        _updateAvailableLabel.Text = $"v{metadata.Version} available";
        _updateAvailableLabel.Visible = true;
        _updateButton.Visible = true;
        _updateButton.Location = new Point(
            _updateAvailableLabel.Left + _updateAvailableLabel.PreferredWidth + 8,
            _updateAvailableLabel.Top);
    }

    private async void OnUpdateClick()
    {
        _updateButton.Text = "Updating...";
        _updateButton.Enabled = false;

        try
        {
            var result = await Task.Run(Updater.Apply);
            if (result.Success)
            {
                _forceExit = true;
                Environment.Exit(0);
            }
            else
            {
                MessageBox.Show(this, result.Message, "Update Failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                _updateButton.Text = "Update";
                _updateButton.Enabled = true;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Update Failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            _updateButton.Text = "Update";
            _updateButton.Enabled = true;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_forceExit && e.CloseReason == CloseReason.UserClosing)
        {
            // Minimize to tray instead of exiting
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Badge circle behind the icon label.
        var iconScreenPos = _iconLabel.Parent!.PointToScreen(_iconLabel.Location);
        var iconFormPos = PointToClient(iconScreenPos);
        int badgeX = iconFormPos.X, badgeY = iconFormPos.Y, badgeSize = 48;
        using (var badgeBrush = new SolidBrush(_iconBadgeBg))
            g.FillEllipse(badgeBrush, badgeX, badgeY, badgeSize, badgeSize);
        using (var badgePen = new Pen(Color.FromArgb(60, _accentColor), 1.5f))
            g.DrawEllipse(badgePen, badgeX, badgeY, badgeSize, badgeSize);
    }
}
