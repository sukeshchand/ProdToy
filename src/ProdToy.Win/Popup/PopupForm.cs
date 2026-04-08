using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ProdToy;

class PopupForm : Form
{
    private PopupTheme _theme;

    private readonly Label _animLabel;
    private readonly Panel _headerPanel;
    private readonly Panel _infoPanel;
    private readonly Label _iconLabel;
    private readonly Label _titleLabel;
    private readonly Label _subtitleLabel;
    private readonly Label _versionLabel;
    private readonly Panel _headerLine;
    private readonly WebView2 _messageWebView;
    private readonly Panel _footerPanel;
    private readonly Panel _webViewContainer;
    private readonly Panel _separator;
    private readonly RoundedButton _okButton;
    private readonly RoundedButton _prevButton;
    private readonly RoundedButton _nextButton;
    private readonly Label _navLabel;
    private readonly System.Windows.Forms.Timer _typeTimer;
    private readonly System.Windows.Forms.Timer _sparkleTimer;
    private readonly List<Sparkle> _sparkles = new();
    private readonly Random _rng = new();

    private string _funnyText = "";
    private int _charIndex;
    private bool _showQuotes = AppSettings.Load().ShowQuotes;
    private Color _accentColor;
    private Color _iconBadgeBg;
    private bool _webViewReady;
    private string? _webView2UserDataFolder;
    private string? _pendingHtml;
    private bool _forceExit;
    private string _lastMessage = "";
    private string _lastType = NotificationType.Info;
    private DateTime _snoozeUntil = DateTime.MinValue;
    private readonly CheckBox _snoozeCheckBox;

    private int _historyIndex = -1; // -1 = showing live/current message
    private bool _viewingHistory;

    private readonly RoundedButton _filterButton;
    private FilterMode _filterMode = FilterMode.Session;
    private string _filterValue = "";
    private string _currentSessionId = "";
    private List<HistoryIndex>? _filteredIndex;
    private DateTime _selectedDate = DateTime.Today;

    private readonly Label _copyMdLink;
    private readonly Label _copyPreviewLink;
    private readonly Label _copyHtmlLink;
    private readonly Label _updateAvailableLabel;
    private readonly Label _updateButton;

    private const int HeaderHeight = 58;
    private const int InfoBarHeight = 56;
    private const int FooterHeight = 105;

    public PopupTheme CurrentTheme => _theme;
    public bool IsSnoozed => DateTime.Now < _snoozeUntil;
    public DateTime SnoozeUntil => _snoozeUntil;

    public event Action? SnoozeChanged;
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

        // --- Header (top, docked) ---
        _headerPanel = new Panel
        {
            BackColor = theme.BgHeader,
            Dock = DockStyle.Top,
            Height = HeaderHeight,
        };

        _animLabel = new Label
        {
            Text = "",
            Font = new Font("Cascadia Code", 11f, FontStyle.Italic),
            ForeColor = theme.PrimaryLight,
            BackColor = Color.Transparent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 0, 10, 0),
        };
        _headerPanel.Padding = new Padding(0, 10, 0, 10);
        _headerPanel.Controls.Add(_animLabel);

        _headerLine = new Panel
        {
            BackColor = theme.Primary,
            Dock = DockStyle.Top,
            Height = 2,
        };

        // --- Info bar (icon + title + subtitle + version) - fixed height below header ---
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
            Location = new Point(22, 4)
        };

        _titleLabel = new Label
        {
            Text = "ProdToy",
            Font = new Font("Segoe UI Semibold", 14f, FontStyle.Bold),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(82, 6),
            BackColor = Color.Transparent
        };

        _subtitleLabel = new Label
        {
            Text = "Notification",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(82, 30),
            BackColor = Color.Transparent
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

        // --- Nav buttons for history (in info bar, top center) ---
        _prevButton = new RoundedButton
        {
            Text = "\u25C0",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Size = new Size(32, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextSecondary,
            Cursor = Cursors.Hand,
            Visible = false,
        };
        _prevButton.FlatAppearance.BorderSize = 0;
        _prevButton.FlatAppearance.MouseOverBackColor = theme.Primary;
        _prevButton.Click += (_, _) => NavigateHistory(-1);

        _nextButton = new RoundedButton
        {
            Text = "\u25B6",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Size = new Size(32, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextSecondary,
            Cursor = Cursors.Hand,
            Visible = false,
        };
        _nextButton.FlatAppearance.BorderSize = 0;
        _nextButton.FlatAppearance.MouseOverBackColor = theme.Primary;
        _nextButton.Click += (_, _) => NavigateHistory(+1);

        _navLabel = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 8f),
            ForeColor = theme.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Visible = false,
        };

        _filterButton = new RoundedButton
        {
            Text = "\uE14C",  // Group list icon from Segoe MDL2 Assets
            Font = new Font("Segoe MDL2 Assets", 10f),
            Size = new Size(28, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextSecondary,
            Cursor = Cursors.Hand,
            Visible = false,
        };
        _filterButton.FlatAppearance.BorderSize = 0;
        _filterButton.FlatAppearance.MouseOverBackColor = theme.Primary;
        _filterButton.Click += (_, _) => ShowFilterDialog();

        _infoPanel.Controls.AddRange(new Control[]
        {
            _iconLabel, _titleLabel, _subtitleLabel,
            _prevButton, _navLabel, _nextButton, _filterButton
        });

        // --- Footer (bottom, docked) ---
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
        _okButton.Click += (_, _) => OnOkClick();

        // --- Copy format links ---
        _copyMdLink = CreateCopyLink("Copy Markdown", theme);
        _copyMdLink.Click += (_, _) => CopyAs("markdown");

        _copyPreviewLink = CreateCopyLink("Copy Preview", theme);
        _copyPreviewLink.Click += (_, _) => CopyAs("preview");

        _copyHtmlLink = CreateCopyLink("Copy HTML", theme);
        _copyHtmlLink.Click += (_, _) => CopyAs("html");

        _snoozeCheckBox = new CheckBox
        {
            Text = "Snooze for 30 minutes",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Cursor = Cursors.Hand,
            Location = new Point(20, 68),
        };

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
        _footerPanel.Controls.Add(_copyMdLink);
        _footerPanel.Controls.Add(_copyPreviewLink);
        _footerPanel.Controls.Add(_copyHtmlLink);
        _footerPanel.Controls.Add(_okButton);
        _footerPanel.Controls.Add(_snoozeCheckBox);
        _footerPanel.Controls.Add(_updateAvailableLabel);
        _footerPanel.Controls.Add(_updateButton);
        _footerPanel.Controls.Add(_versionLabel);

        // --- WebView (fills remaining space, with padding) ---
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

        // WinForms docks last-added first. We want:
        //   Top: _headerPanel, _headerLine, infoPanel
        //   Bottom: _footerPanel
        //   Fill: webViewContainer (with padding around WebView)
        // So add Fill first, then the docked panels in reverse visual order.
        Controls.Add(_webViewContainer);   // Fill - added first, docked last
        Controls.Add(_footerPanel);       // Bottom
        Controls.Add(_infoPanel);          // Top (below header line)
        Controls.Add(_headerLine);        // Top (below header)
        Controls.Add(_headerPanel);       // Top (first)

        if (!_showQuotes)
        {
            _headerPanel.Visible = false;
            _headerLine.Visible = false;
        }

        _typeTimer = new System.Windows.Forms.Timer { Interval = 60 };
        _typeTimer.Tick += TypeTimer_Tick;

        _sparkleTimer = new System.Windows.Forms.Timer { Interval = 40 };
        _sparkleTimer.Tick += SparkleTimer_Tick;

        Shown += (_, _) => { UpdateHistoryNav(); PositionControls(); };

        InitializeWebView2();

        // Apply saved global font
        var savedFont = AppSettings.Load().GlobalFont;
        if (!string.IsNullOrEmpty(savedFont) && savedFont != "Segoe UI")
            ApplyGlobalFont(savedFont);
    }

    private void PositionControls()
    {
        if (_footerPanel == null || _okButton == null || _versionLabel == null) return;

        int footerW = _footerPanel.ClientSize.Width;

        // Copy links row — centered above OK button
        int linkSpacing = 16;
        int totalLinkWidth = _copyMdLink.PreferredWidth + _copyPreviewLink.PreferredWidth + _copyHtmlLink.PreferredWidth + linkSpacing * 2;
        int linkStartX = (footerW - totalLinkWidth) / 2;
        int linkY = 8;
        _copyMdLink.Location = new Point(linkStartX, linkY);
        _copyPreviewLink.Location = new Point(_copyMdLink.Right + linkSpacing, linkY);
        _copyHtmlLink.Location = new Point(_copyPreviewLink.Right + linkSpacing, linkY);

        // Center OK button below copy links
        _okButton.Location = new Point((footerW - _okButton.Width) / 2, linkY + _copyMdLink.Height + 6);

        // Version label bottom-right of footer
        _versionLabel.Location = new Point(
            footerW - _versionLabel.Width - 12,
            _footerPanel.ClientSize.Height - _versionLabel.Height - 6);

        // Nav buttons right-aligned in info panel, session info below subtitle
        var infoPanel = _prevButton.Parent;
        if (infoPanel != null)
        {
            int ipw = infoPanel.ClientSize.Width;

            // Nav buttons and filter button right-aligned in info panel
            if (_prevButton.Visible)
            {
                int navY = (InfoBarHeight - _prevButton.Height) / 2;
                int navX = ipw - 12;

                // Filter button (rightmost)
                if (_filterButton.Visible)
                {
                    navX -= _filterButton.Width;
                    _filterButton.Location = new Point(navX, navY);
                    navX -= 6;
                }

                // Next button
                navX -= _nextButton.Width;
                _nextButton.Location = new Point(navX, navY);
                navX -= (8 + _navLabel.PreferredWidth);
                _navLabel.Location = new Point(navX, navY + (_prevButton.Height - _navLabel.Height) / 2);
                navX -= (8 + _prevButton.Width);
                _prevButton.Location = new Point(navX, navY);
            }
        }
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
            Debug.WriteLine($"WebView2 init failed: {ex.Message}");
        }
    }

    public void ApplyTheme(PopupTheme theme)
    {
        _theme = theme;

        // Update form colors
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        var oldIcon = Icon;
        Icon = Themes.CreateAppIcon(theme.Primary);
        oldIcon?.Dispose();

        _headerPanel.BackColor = theme.BgHeader;
        _animLabel.ForeColor = theme.PrimaryLight;
        _headerLine.BackColor = theme.Primary;
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

        foreach (var copyLink in new[] { _copyMdLink, _copyPreviewLink, _copyHtmlLink })
            copyLink.ForeColor = theme.TextSecondary;

        _snoozeCheckBox.ForeColor = theme.TextSecondary;

        _prevButton.BackColor = theme.PrimaryDim;
        _prevButton.ForeColor = theme.TextSecondary;
        _prevButton.FlatAppearance.MouseOverBackColor = theme.Primary;
        _nextButton.BackColor = theme.PrimaryDim;
        _nextButton.ForeColor = theme.TextSecondary;
        _nextButton.FlatAppearance.MouseOverBackColor = theme.Primary;
        _navLabel.ForeColor = theme.TextSecondary;

        _filterButton.FlatAppearance.MouseOverBackColor = theme.Primary;
        if (_filterMode != FilterMode.None)
        {
            _filterButton.BackColor = theme.Primary;
            _filterButton.ForeColor = Color.White;
        }
        else
        {
            _filterButton.BackColor = theme.PrimaryDim;
            _filterButton.ForeColor = theme.TextSecondary;
        }

        _updateAvailableLabel.ForeColor = theme.SuccessColor;
        _updateButton.ForeColor = theme.Primary;

        // Re-apply type-based colors
        ApplyTypeColors(_lastType);

        // Re-render WebView content with new theme colors
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

    private Label CreateCopyLink(string text, PopupTheme theme)
    {
        var link = new Label
        {
            Text = text,
            Font = new Font("Segoe UI", 8f),
            ForeColor = theme.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Cursor = Cursors.Hand,
        };
        link.MouseEnter += (_, _) => { link.ForeColor = _theme.Primary; link.Font = new Font(link.Font, FontStyle.Underline); };
        link.MouseLeave += (_, _) => { link.ForeColor = _theme.TextSecondary; link.Font = new Font(link.Font, FontStyle.Regular); };
        return link;
    }

    private void CopyAs(string format)
    {
        if (string.IsNullOrEmpty(_lastMessage)) return;

        Label link = format switch
        {
            "markdown" => _copyMdLink,
            "preview" => _copyPreviewLink,
            _ => _copyHtmlLink,
        };
        string originalText = link.Text;

        try
        {
            string textToCopy = format switch
            {
                "markdown" => _lastMessage,
                "html" => RenderHtml(_lastMessage),
                _ => _lastMessage, // preview handled below
            };

            if (format == "preview")
            {
                string previewHtml = RenderHtml(_lastMessage);
                var dataObj = new DataObject();
                dataObj.SetData(DataFormats.Html, CreateHtmlClipboardFormat(previewHtml));
                dataObj.SetData(DataFormats.UnicodeText, _lastMessage);
                Clipboard.SetDataObject(dataObj, copy: true);
            }
            else
            {
                Clipboard.SetDataObject(textToCopy, copy: true);
            }

            link.Text = "\u2713 Copied";
            link.ForeColor = _theme.SuccessColor;
            var resetTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            resetTimer.Tick += (_, _) =>
            {
                link.Text = originalText;
                link.ForeColor = _theme.TextSecondary;
                resetTimer.Stop();
                resetTimer.Dispose();
            };
            resetTimer.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CopyAs({format}) failed: {ex.Message}");
            link.Text = "Failed";
            link.ForeColor = _theme.ErrorColor;
            var resetTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            resetTimer.Tick += (_, _) =>
            {
                link.Text = originalText;
                link.ForeColor = _theme.TextSecondary;
                resetTimer.Stop();
                resetTimer.Dispose();
            };
            resetTimer.Start();
        }
    }

    private static string CreateHtmlClipboardFormat(string htmlBody)
    {
        string startFrag = "<!--StartFragment-->";
        string endFrag = "<!--EndFragment-->";
        string htmlPrefix = "<html><body>" + startFrag;
        string htmlSuffix = endFrag + "</body></html>";

        // The header has fixed-width placeholders
        string headerTemplate = "Version:0.9\r\nStartHTML:0000000000\r\nEndHTML:0000000000\r\nStartFragment:0000000000\r\nEndFragment:0000000000\r\n";
        int headerLen = headerTemplate.Length;
        int startHtmlIdx = headerLen;
        int startFragIdx = headerLen + htmlPrefix.Length;
        int endFragIdx = startFragIdx + htmlBody.Length;
        int endHtmlIdx = endFragIdx + htmlSuffix.Length;

        return $"Version:0.9\r\nStartHTML:{startHtmlIdx:D10}\r\nEndHTML:{endHtmlIdx:D10}\r\nStartFragment:{startFragIdx:D10}\r\nEndFragment:{endFragIdx:D10}\r\n"
            + htmlPrefix + htmlBody + htmlSuffix;
    }

    private void ApplyTypeColors(string type)
    {
        var (accentColor, iconText, iconBadgeBg, subtitle) = type switch
        {
            NotificationType.Success => (_theme.SuccessColor, "\u2713", _theme.SuccessBg, "Completed successfully"),
            NotificationType.Error => (_theme.ErrorColor, "\u2717", _theme.ErrorBg, "An error occurred"),
            _ => (_theme.Primary, "\u2139", _theme.PrimaryDim, "Notification")
        };
        _accentColor = accentColor;
        _iconBadgeBg = iconBadgeBg;
        _iconLabel.Text = iconText;
        _iconLabel.ForeColor = accentColor;
        _subtitleLabel.Text = subtitle;
        _headerLine.BackColor = accentColor;
    }

    public void Snooze()
    {
        _snoozeUntil = DateTime.Now.AddMinutes(30);
        _snoozeCheckBox.Checked = true;
        SnoozeChanged?.Invoke();
    }

    public void Unsnooze()
    {
        _snoozeUntil = DateTime.MinValue;
        _snoozeCheckBox.Checked = false;
        SnoozeChanged?.Invoke();
    }

    public void SetShowQuotes(bool show)
    {
        _showQuotes = show;
        _headerPanel.Visible = show;
        _headerLine.Visible = show;
        if (!show)
        {
            _typeTimer.Stop();
            _sparkleTimer.Stop();
            _sparkles.Clear();
            _animLabel.Text = "";
        }
    }

    private void OnOkClick()
    {
        if (_snoozeCheckBox.Checked)
        {
            _snoozeUntil = DateTime.Now.AddMinutes(30);
            SnoozeChanged?.Invoke();
        }
        else if (IsSnoozed)
        {
            // User unchecked → cancel snooze
            _snoozeUntil = DateTime.MinValue;
            SnoozeChanged?.Invoke();
        }
        Hide();
    }

    public void ShowPopup(string title, string message, string type, string sessionId = "", string cwd = "")
    {
        // Always store latest message even if snoozed
        _lastMessage = message;
        _lastType = type;

        // Reset to live view and current date
        _viewingHistory = false;
        _historyIndex = -1;
        _filteredIndex = null;
        _selectedDate = DateTime.Today;

        // If snoozed, don't show the popup
        if (IsSnoozed)
            return;

        // Read question from the latest history entry matching this session
        ResponseHistory.Invalidate();
        string question = "";
        if (!string.IsNullOrEmpty(sessionId))
        {
            var sessionEntries = ResponseHistory.FilterBySession(DateTime.Today, sessionId);
            if (sessionEntries.Count > 0)
            {
                var latestSessionEntry = ResponseHistory.LoadEntry(sessionEntries[^1]);
                question = latestSessionEntry?.Question ?? "";
                if (string.IsNullOrEmpty(cwd)) cwd = latestSessionEntry?.Cwd ?? "";
            }
        }
        else
        {
            var latest = ResponseHistory.GetLatest();
            question = latest?.Question ?? "";
            if (string.IsNullOrEmpty(sessionId)) sessionId = latest?.SessionId ?? "";
            if (string.IsNullOrEmpty(cwd)) cwd = latest?.Cwd ?? "";
        }

        // Default filter to current session (even if user had folder filter active)
        if (!string.IsNullOrEmpty(sessionId))
        {
            _currentSessionId = sessionId;
            _filterMode = FilterMode.Session;
            _filterValue = sessionId;
            _filteredIndex = ResponseHistory.FilterBySession(_selectedDate, sessionId);
        }
        else
        {
            _filterMode = FilterMode.None;
            _filterValue = "";
        }

        DisplayMessage(title, message, type, question, sessionId, cwd);

        // New funny quote + restart typewriter
        if (_showQuotes)
        {
            _funnyText = FunnyQuotes.Lines[_rng.Next(FunnyQuotes.Lines.Length)];
            _charIndex = 0;
            _animLabel.Text = "";
            _sparkles.Clear();
            _typeTimer.Start();
        }

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
        // Release TopMost after a brief moment so user can click other windows over it
        var releaseTimer = new System.Windows.Forms.Timer { Interval = 500 };
        releaseTimer.Tick += (_, _) =>
        {
            TopMost = false;
            releaseTimer.Stop();
            releaseTimer.Dispose();
        };
        releaseTimer.Start();
    }

    private static string GetFirstName()
    {
        try
        {
            // Extract username from user profile directory (e.g. C:\Users\sukesh.chand)
            var userDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var userName = Path.GetFileName(userDir) ?? "";
            // Split by common separators and take first part, then capitalize
            var first = userName.Split('.', ' ', '_', '-')[0];
            if (first.Length > 0)
                return char.ToUpper(first[0]) + first[1..];
            return userName;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GetFirstName failed: {ex.Message}");
            return "User";
        }
    }

    private void DisplayMessage(string title, string message, string type, string question = "", string sessionId = "", string cwd = "")
    {
        _lastMessage = message;
        ApplyTypeColors(type);
        Text = title;
        _titleLabel.Text = title;

        // Render the Claude response markdown to HTML first (before wrapping with raw HTML blocks)
        string renderedMessage = RenderHtml(message);

        // Build the final HTML by injecting pre-rendered user/Claude blocks into the document body
        string firstName = GetFirstName();
        string bodyPrefix;
        if (!string.IsNullOrWhiteSpace(question))
        {
            // Truncate long questions to first 3 lines
            var qLines = question.Split('\n');
            string shortQ = qLines.Length > 3
                ? string.Join("\n", qLines.Take(3)) + "..."
                : question;
            string escapedQ = System.Net.WebUtility.HtmlEncode(shortQ).Replace("\n", "<br/>");
            bodyPrefix = $"<div class=\"user-block\"><div class=\"label\">{firstName}:</div><div class=\"text\">{escapedQ}</div></div><div class=\"claude-block\"><div class=\"claude-label\">Response:</div><div class=\"claude-content\">";
        }
        else
        {
            bodyPrefix = "<div class=\"claude-block\"><div class=\"claude-label\">Response:</div><div class=\"claude-content\">";
        }

        // Inject the prefix before the rendered body content inside the <body> tag,
        // and close the claude-content + claude-block divs at the end
        string htmlContent = renderedMessage
            .Replace("<body>", $"<body>{bodyPrefix}")
            .Replace("</body>", "</div></div></body>");

        // Estimate a good initial window height based on content, capped at 90% of screen
        var lineCount = message.Split('\n').Length;
        int wrappedLines = lineCount;
        foreach (var line in message.Split('\n'))
        {
            if (line.Length > 80)
                wrappedLines += (line.Length / 80);
        }
        int prefixHeight = string.IsNullOrWhiteSpace(question) ? 40 : 100; // user-block + claude-label
        int estimatedContentHeight = Math.Max(180, wrappedLines * 28 + prefixHeight + 60);
        var workingArea = Screen.FromControl(this).WorkingArea;
        int maxHeight = (int)(workingArea.Height * 0.9);
        int newClientW = Math.Max(ClientSize.Width, 800);
        int headerTotal = _showQuotes ? HeaderHeight + 2 : 0;
        int newClientH = Math.Min(maxHeight, headerTotal + InfoBarHeight + estimatedContentHeight + FooterHeight);
        ClientSize = new Size(newClientW, newClientH);

        // Only reposition if any corner is outside the current screen
        int curLeft = Left, curTop = Top;
        if (curLeft < workingArea.Left || curTop < workingArea.Top ||
            curLeft + Width > workingArea.Right || curTop + Height > workingArea.Bottom)
        {
            Location = new Point(
                Math.Clamp(curLeft, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - Width)),
                Math.Clamp(curTop, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - Height)));
        }

        _snoozeCheckBox.Checked = IsSnoozed;

        if (_webViewReady)
            _messageWebView.NavigateToString(htmlContent);
        else
            _pendingHtml = htmlContent;

        UpdateHistoryNav();
        PositionControls();
    }

    private void NavigateHistory(int direction)
    {
        ResponseHistory.Invalidate();

        // Refresh filtered index if filter is active
        if (_filterMode != FilterMode.None)
        {
            _filteredIndex = _filterMode == FilterMode.Cwd
                ? ResponseHistory.FilterByCwd(_selectedDate, _filterValue)
                : ResponseHistory.FilterBySession(_selectedDate, _filterValue);
        }

        var index = GetActiveIndex();
        if (index.Count == 0) return;

        if (!_viewingHistory)
        {
            _historyIndex = index.Count - 1;
            _viewingHistory = true;
        }

        _historyIndex += direction;
        _historyIndex = Math.Clamp(_historyIndex, 0, index.Count - 1);

        var entry = ResponseHistory.LoadEntry(index[_historyIndex]);
        if (entry != null)
            DisplayMessage(entry.Title, entry.Message, entry.Type, entry.Question, entry.SessionId, entry.Cwd);
    }

    private void ShowFilterDialog()
    {
        using var dlg = new FilterDialog(_theme, _filterMode, _filterValue, _selectedDate);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _filterMode = dlg.SelectedMode;
            _filterValue = dlg.SelectedValue;
            _selectedDate = dlg.SelectedDate;
            _filteredIndex = null;

            if (_filterMode != FilterMode.None)
            {
                // Build filtered list and navigate to latest entry
                ResponseHistory.Invalidate();
                _filteredIndex = _filterMode == FilterMode.Cwd
                    ? ResponseHistory.FilterByCwd(_selectedDate, _filterValue)
                    : ResponseHistory.FilterBySession(_selectedDate, _filterValue);

                if (_filteredIndex.Count > 0)
                {
                    _viewingHistory = true;
                    _historyIndex = _filteredIndex.Count - 1;
                    var entry = ResponseHistory.LoadEntry(_filteredIndex[_historyIndex]);
                    if (entry != null)
                        DisplayMessage(entry.Title, entry.Message, entry.Type, entry.Question, entry.SessionId, entry.Cwd);
                }
                else
                {
                    // Filter returned no results — reset navigation state
                    _viewingHistory = false;
                    _historyIndex = -1;
                    UpdateHistoryNav();
                    PositionControls();
                }
            }
            else
            {
                // Clear filter — go back to full index and today's date
                _viewingHistory = false;
                _historyIndex = -1;
                _selectedDate = DateTime.Today;
                UpdateHistoryNav();
                PositionControls();
            }
        }
    }

    private List<HistoryIndex> GetActiveIndex()
    {
        if (_filterMode != FilterMode.None && _filteredIndex != null)
            return _filteredIndex;
        // When viewing a different date, show that day's entries; otherwise full index
        if (_selectedDate.Date != DateTime.Today)
            return ResponseHistory.LoadDayIndex(_selectedDate);
        return ResponseHistory.LoadIndex();
    }

    public void UpdateHistoryNav()
    {
        var index = GetActiveIndex();
        bool showNav = ResponseHistory.IsEnabled && index.Count > 0;

        _prevButton.Visible = showNav;
        _nextButton.Visible = showNav;
        _navLabel.Visible = showNav;
        _filterButton.Visible = ResponseHistory.IsEnabled && ResponseHistory.LoadIndex().Count > 0;

        // Update filter button appearance based on active filter
        if (_filterMode != FilterMode.None)
        {
            _filterButton.BackColor = _theme.Primary;
            _filterButton.ForeColor = Color.White;
        }
        else
        {
            _filterButton.BackColor = _theme.PrimaryDim;
            _filterButton.ForeColor = _theme.TextSecondary;
        }

        if (showNav)
        {
            string filterLabel = "";
            if (_filterMode == FilterMode.Cwd)
            {
                string folder = Path.GetFileName(_filterValue.TrimEnd('/', '\\'));
                filterLabel = $" [{folder}]";
            }
            else if (_filterMode == FilterMode.Session)
            {
                string shortId = _filterValue.Length > 8 ? _filterValue[..8] : _filterValue;
                // Find the cwd associated with this session
                string sessionCwd = "";
                if (_filteredIndex != null && _filteredIndex.Count > 0)
                {
                    var first = _filteredIndex.FirstOrDefault(i => !string.IsNullOrEmpty(i.Cwd));
                    if (first != null)
                        sessionCwd = Path.GetFileName(first.Cwd.TrimEnd('/', '\\'));
                }
                filterLabel = string.IsNullOrEmpty(sessionCwd)
                    ? $" [{shortId}]"
                    : $" [{shortId} - {sessionCwd}]";
            }

            // Show date prefix when viewing a day other than today
            string datePrefix = _selectedDate.Date != DateTime.Today
                ? $"{_selectedDate:dd MMM} \u2022 "
                : "";

            if (_viewingHistory)
            {
                _navLabel.Text = $"{datePrefix}{_historyIndex + 1} / {index.Count}{filterLabel}";
                _prevButton.Enabled = _historyIndex > 0;
                _nextButton.Enabled = _historyIndex < index.Count - 1;
            }
            else
            {
                _navLabel.Text = $"{datePrefix}Latest ({index.Count}){filterLabel}";
                _prevButton.Enabled = index.Count > 0;
                _nextButton.Enabled = false;
            }
        }

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
        _typeTimer.Stop();
        _sparkleTimer.Stop();
        try
        {
            if (_webView2UserDataFolder != null && Directory.Exists(_webView2UserDataFolder))
                Directory.Delete(_webView2UserDataFolder, true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WebView2 cleanup failed: {ex.Message}");
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

    private void OnUpdateClick()
    {
        _updateButton.Text = "Updating...";
        _updateButton.Enabled = false;

        try
        {
            var result = Updater.Apply();
            if (result.Success)
            {
                _forceExit = true;
                _typeTimer.Stop();
                _sparkleTimer.Stop();
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

        // Draw badge circle behind the icon label (translate from icon's parent to form coords)
        var iconScreenPos = _iconLabel.Parent!.PointToScreen(_iconLabel.Location);
        var iconFormPos = PointToClient(iconScreenPos);
        int badgeX = iconFormPos.X, badgeY = iconFormPos.Y, badgeSize = 48;
        using (var badgeBrush = new SolidBrush(_iconBadgeBg))
            g.FillEllipse(badgeBrush, badgeX, badgeY, badgeSize, badgeSize);
        using (var badgePen = new Pen(Color.FromArgb(60, _accentColor), 1.5f))
            g.DrawEllipse(badgePen, badgeX, badgeY, badgeSize, badgeSize);

        foreach (var s in _sparkles)
        {
            int alpha = Math.Clamp((int)(s.Life * 255), 0, 255);
            using var brush = new SolidBrush(Color.FromArgb(alpha, s.Color));
            g.FillEllipse(brush, (float)s.X, (float)s.Y, s.Size, s.Size);
        }
    }

    private void TypeTimer_Tick(object? sender, EventArgs e)
    {
        if (_charIndex < _funnyText.Length)
        {
            _charIndex++;
            _animLabel.Text = "\u201C" + _funnyText[.._charIndex] + "\u2588";
        }
        else
        {
            _animLabel.Text = "\u201C" + _funnyText + "\u201D";
            _typeTimer.Stop();
            _sparkleTimer.Start();
            var stopTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            stopTimer.Tick += (_, _) =>
            {
                _sparkleTimer.Stop();
                stopTimer.Stop();
                stopTimer.Dispose();
                Invalidate();
            };
            stopTimer.Start();
        }
    }

    private void SparkleTimer_Tick(object? sender, EventArgs e)
    {
        // Compute sparkle origin relative to form coordinates from _animLabel bounds
        var labelBounds = RectangleToClient(_animLabel.RectangleToScreen(_animLabel.ClientRectangle));

        for (int i = 0; i < 3; i++)
        {
            _sparkles.Add(new Sparkle
            {
                X = labelBounds.X + _rng.Next(labelBounds.Width),
                Y = labelBounds.Y + _rng.Next(labelBounds.Height),
                VX = (_rng.NextDouble() - 0.5) * 8,
                VY = -_rng.NextDouble() * 4 - 1,
                Life = 1.0,
                Size = 3 + _rng.Next(5),
                Color = _rng.Next(4) switch
                {
                    0 => _theme.Sparkle1,
                    1 => _theme.Sparkle2,
                    2 => _theme.Sparkle3,
                    _ => _theme.Sparkle4
                }
            });
        }

        for (int i = _sparkles.Count - 1; i >= 0; i--)
        {
            var s = _sparkles[i];
            s.X += s.VX;
            s.Y += s.VY;
            s.VY += 0.15;
            s.Life -= 0.04;
            if (s.Life <= 0)
                _sparkles.RemoveAt(i);
        }

        Invalidate(new Rectangle(0, 0, ClientSize.Width, HeaderHeight + 2));
    }
}
