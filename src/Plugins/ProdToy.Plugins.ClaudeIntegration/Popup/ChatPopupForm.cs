using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>
/// Plugin-owned Claude chat popup. Owns everything Claude-specific that
/// used to live on the host's PopupForm: funny-quote header with sparkles,
/// snooze checkbox, notification-mode dispatch (popup vs. tray balloon),
/// chat history navigation, and filter dialog.
///
/// Lifecycle: created lazily in the notify handler, kept alive for the
/// life of the plugin. Show/Hide reuses the window so WebView2 state
/// survives across notifications.
/// </summary>
sealed class ChatPopupForm : Form, IPluginPopup
{
    private readonly IPluginHost _host;
    private readonly ChatHistory _history;
    private readonly IPluginContext _context;
    private PluginTheme _theme;

    // Header (funny quote + sparkles) — visible when ShowQuotes is on.
    private readonly Panel _headerPanel;
    private readonly Panel _headerLine;
    private readonly Label _animLabel;
    private readonly System.Windows.Forms.Timer _typeTimer;
    private readonly System.Windows.Forms.Timer _sparkleTimer;
    private readonly List<Sparkle> _sparkles = new();
    private readonly Random _rng = new();
    private string _funnyText = "";
    private int _charIndex;
    private bool _showQuotes;

    // Info bar: icon + title + subtitle + nav buttons.
    private readonly Panel _infoPanel;
    private readonly Label _titleLabel;
    private readonly Label _subtitleLabel;
    private readonly Label _iconLabel;
    private readonly RoundedButton _prevButton;
    private readonly RoundedButton _nextButton;
    private readonly Label _navLabel;
    private readonly RoundedButton _filterButton;

    // Footer: OK + snooze checkbox.
    private readonly Panel _footerPanel;
    private readonly Panel _separator;
    private readonly RoundedButton _okButton;
    private readonly CheckBox _snoozeCheckBox;
    private readonly Label _copyMdLink;
    private readonly Label _copyPreviewLink;
    private readonly Label _copyHtmlLink;

    // Content area.
    private readonly Panel _webViewContainer;
    private readonly WebView2 _webView;

    private bool _webViewReady;
    private bool _webViewFailed;
    private bool _webViewInitStarted;
    private string? _pendingHtml;

    /// <summary>
    /// Fired from <see cref="InitializeWebView2"/>'s catch block when WebView2
    /// init permanently fails (e.g. COM apartment race that somehow survives
    /// v1.0.324's CreationProperties fix). The plugin listens and disposes +
    /// reconstructs this popup on the next notification, so a one-time flaky
    /// startup doesn't brick the chat popup for the rest of the session.
    /// </summary>
    public event Action? WebViewInitFailed;

    public bool IsWebViewFailed => _webViewFailed;
    private string _lastMessage = "";
    private string _lastType = "info";
    private Color _accentColor;
    private Color _iconBadgeBg;

    // History navigation state.
    private int _historyIndex = -1;   // -1 = live/current message
    private bool _viewingHistory;
    private string _currentSessionId = "";
    private FilterMode _filterMode = FilterMode.Session;
    private string _filterValue = "";
    private List<HistoryIndex>? _filteredIndex;
    private DateTime _selectedDate = DateTime.Today;

    private const int HeaderHeight = 58;
    private const int InfoBarHeight = 72;
    private const int FooterHeight = 112;

    public ChatPopupForm(IPluginContext context, ChatHistory history)
    {
        _context = context;
        _host = context.Host;
        _history = history;
        _theme = _host.CurrentTheme;
        _accentColor = _theme.Primary;
        _iconBadgeBg = _theme.PrimaryDim;
        _showQuotes = context.LoadSettings<ClaudePluginSettings>().ShowQuotes;

        Text = "ProdToy — Claude";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        MinimumSize = new Size(480, 360);
        BackColor = _theme.BgDark;
        ForeColor = _theme.TextPrimary;
        Font = new Font("Segoe UI", 10f);

        // --- Funny-quote header (topmost) ---
        _headerPanel = new Panel
        {
            BackColor = _theme.BgHeader,
            Dock = DockStyle.Top,
            Height = HeaderHeight,
            Padding = new Padding(0, 10, 0, 10),
        };
        _animLabel = new Label
        {
            Text = "",
            Font = new Font("Cascadia Code", 11f, FontStyle.Italic),
            ForeColor = _theme.PrimaryLight,
            BackColor = Color.Transparent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 0, 10, 0),
        };
        _headerPanel.Controls.Add(_animLabel);

        _headerLine = new Panel
        {
            BackColor = _theme.Primary,
            Dock = DockStyle.Top,
            Height = 2,
        };

        // --- Info bar ---
        _infoPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = InfoBarHeight,
            BackColor = _theme.BgDark,
        };

        _iconLabel = new Label
        {
            Text = "\u2726",
            Font = new Font("Segoe UI", 20f, FontStyle.Bold),
            ForeColor = _theme.Primary,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleCenter,
            Size = new Size(48, 48),
            Location = new Point(16, 4),
        };

        _titleLabel = new Label
        {
            Text = "Claude",
            Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold),
            ForeColor = _theme.TextPrimary,
            AutoSize = true,
            Location = new Point(72, 6),
            BackColor = Color.Transparent,
        };

        _subtitleLabel = new Label
        {
            Text = "Session",
            Font = new Font("Segoe UI", 9f),
            ForeColor = _theme.TextSecondary,
            AutoSize = true,
            Location = new Point(72, 30),
            BackColor = Color.Transparent,
        };

        _prevButton = new RoundedButton
        {
            Text = "\u25C0",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Size = new Size(32, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.PrimaryDim,
            ForeColor = _theme.TextSecondary,
            Cursor = Cursors.Hand,
            Visible = false,
        };
        _prevButton.FlatAppearance.BorderSize = 0;
        _prevButton.FlatAppearance.MouseOverBackColor = _theme.Primary;
        _prevButton.Click += (_, _) => NavigateHistory(-1);

        _nextButton = new RoundedButton
        {
            Text = "\u25B6",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Size = new Size(32, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.PrimaryDim,
            ForeColor = _theme.TextSecondary,
            Cursor = Cursors.Hand,
            Visible = false,
        };
        _nextButton.FlatAppearance.BorderSize = 0;
        _nextButton.FlatAppearance.MouseOverBackColor = _theme.Primary;
        _nextButton.Click += (_, _) => NavigateHistory(+1);

        _navLabel = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 8f),
            ForeColor = _theme.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Visible = false,
        };

        _filterButton = new RoundedButton
        {
            Text = "\uE14C",
            Font = new Font("Segoe MDL2 Assets", 10f),
            Size = new Size(28, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.PrimaryDim,
            ForeColor = _theme.TextSecondary,
            Cursor = Cursors.Hand,
            Visible = false,
        };
        _filterButton.FlatAppearance.BorderSize = 0;
        _filterButton.FlatAppearance.MouseOverBackColor = _theme.Primary;
        _filterButton.Click += (_, _) => ShowFilterDialog();

        _infoPanel.Controls.AddRange(new Control[]
        {
            _iconLabel, _titleLabel, _subtitleLabel,
            _prevButton, _navLabel, _nextButton, _filterButton,
        });

        // --- Footer ---
        _footerPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = FooterHeight,
            BackColor = _theme.BgDark,
        };

        _separator = new Panel
        {
            BackColor = _theme.Border,
            Height = 1,
            Dock = DockStyle.Top,
        };

        _okButton = new RoundedButton
        {
            Text = "OK",
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            Size = new Size(130, 36),
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.Primary,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Top,
        };
        _okButton.FlatAppearance.BorderSize = 0;
        _okButton.FlatAppearance.MouseOverBackColor = _theme.PrimaryLight;
        _okButton.FlatAppearance.MouseDownBackColor = _theme.PrimaryDim;
        _okButton.Click += (_, _) => OnOkClick();

        _snoozeCheckBox = new CheckBox
        {
            Text = "Snooze for 30 minutes",
            Font = new Font("Segoe UI", 9f),
            ForeColor = _theme.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Cursor = Cursors.Hand,
        };

        // Copy-to-clipboard links for the chat response text.
        _copyMdLink = CreateCopyLink("Copy Markdown");
        _copyMdLink.Click += (_, _) => CopyAs("markdown");
        _copyPreviewLink = CreateCopyLink("Copy Preview");
        _copyPreviewLink.Click += (_, _) => CopyAs("preview");
        _copyHtmlLink = CreateCopyLink("Copy HTML");
        _copyHtmlLink.Click += (_, _) => CopyAs("html");

        _footerPanel.Controls.Add(_separator);
        _footerPanel.Controls.Add(_copyMdLink);
        _footerPanel.Controls.Add(_copyPreviewLink);
        _footerPanel.Controls.Add(_copyHtmlLink);
        _footerPanel.Controls.Add(_okButton);
        _footerPanel.Controls.Add(_snoozeCheckBox);

        // --- Content area ---
        // Set the user data folder via CreationProperties BEFORE the control's
        // HWND is created. This makes WebView2 do its own STA bookkeeping
        // during handle creation and avoids the RPC_E_CHANGED_MODE race we'd
        // hit if we called CoreWebView2Environment.CreateAsync ourselves from
        // a thread whose COM apartment state was already decided by something
        // else (a prior plugin, a Control.Invoke callback chain, etc).
        string userDataFolder = _host.GetWebView2UserDataFolder("claude-chat");
        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = _theme.BgDark,
            CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = userDataFolder,
            },
        };

        _webViewContainer = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 8, 16, 16),
            BackColor = _theme.BgDark,
        };
        _webViewContainer.Controls.Add(_webView);

        ClientSize = new Size(800, 520);
        AcceptButton = _okButton;

        // WinForms docks the last-added control first. Fill goes in last visually
        // but first in the Controls collection.
        Controls.Add(_webViewContainer);
        Controls.Add(_footerPanel);
        Controls.Add(_infoPanel);
        Controls.Add(_headerLine);
        Controls.Add(_headerPanel);

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
        Resize += (_, _) => PositionControls();
        // Defer WebView2 init until the form handle exists. Calling
        // EnsureCoreWebView2Async from the constructor races the form's HWND
        // creation and can deadlock the UI thread, which presents as
        // "(Not Responding)" with white placeholder controls.
        Load += (_, _) => InitializeWebView2();

        _host.ThemeChanged += OnThemeChanged;
    }

    public bool IsVisible => Visible;

    // IPluginPopup plumbing: currently only used for registration; the plugin
    // drives display via the richer ShowPopup(title, message, type, ...) overload.
    void IPluginPopup.Show() => ShowPopup(_lastMessage.Length > 0 ? "Claude" : "ProdToy", _lastMessage, _lastType);
    public new void Hide() => base.Hide();
    void IPluginPopup.BringToFront() { if (Visible) base.BringToFront(); }

    // ---------- Snooze ----------

    public bool IsSnoozed
    {
        get
        {
            var until = _context.LoadSettings<ClaudePluginSettings>().SnoozeUntil;
            return until > DateTime.Now;
        }
    }

    public DateTime SnoozeUntil => _context.LoadSettings<ClaudePluginSettings>().SnoozeUntil;

    private void SetSnooze(DateTime until)
    {
        var s = _context.LoadSettings<ClaudePluginSettings>();
        _context.SaveSettings(s with { SnoozeUntil = until });
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

    // ---------- Show pipeline ----------

    /// <summary>Show a Claude notification. Returns without displaying if
    /// notifications are disabled, snoozed, or the user chose tray-balloon mode.
    /// The Claude plugin's handler calls this directly; there is no generic
    /// notification facility any more.</summary>
    public void ShowPopup(string title, string message, string type, string sessionId = "", string cwd = "")
    {
        _lastMessage = message;
        _lastType = type;
        _viewingHistory = false;
        _historyIndex = -1;
        _filteredIndex = null;
        _selectedDate = DateTime.Today;

        var settings = _context.LoadSettings<ClaudePluginSettings>();
        if (!settings.NotificationsEnabled) return;
        if (IsSnoozed) return;

        string mode = settings.NotificationMode;
        bool wantPopup = mode is "Popup" or "Popup + Windows";
        bool wantBalloon = mode is "Windows" or "Popup + Windows";

        if (wantBalloon)
            ShowTrayBalloon(title, message, type);

        if (!wantPopup) return;

        // Look up the question from plugin history for this session.
        _history.Invalidate();
        string question = "";
        DateTime questionTime = default;
        if (!string.IsNullOrEmpty(sessionId))
        {
            var sessionEntries = _history.FilterBySession(DateTime.Today, sessionId);
            if (sessionEntries.Count > 0)
            {
                var latest = _history.LoadEntry(sessionEntries[^1]);
                question = latest?.Question ?? "";
                questionTime = (latest?.QuestionTimestamp ?? default) == default
                    ? (latest?.Timestamp ?? default)
                    : latest!.QuestionTimestamp;
                if (string.IsNullOrEmpty(cwd)) cwd = latest?.Cwd ?? "";
            }
        }
        else
        {
            var latest = _history.GetLatest();
            question = latest?.Question ?? "";
            questionTime = (latest?.QuestionTimestamp ?? default) == default
                ? (latest?.Timestamp ?? default)
                : latest!.QuestionTimestamp;
            if (string.IsNullOrEmpty(sessionId)) sessionId = latest?.SessionId ?? "";
            if (string.IsNullOrEmpty(cwd)) cwd = latest?.Cwd ?? "";
        }

        if (!string.IsNullOrEmpty(sessionId))
        {
            _currentSessionId = sessionId;
            _filterMode = FilterMode.Session;
            _filterValue = sessionId;
            _filteredIndex = _history.FilterBySession(_selectedDate, sessionId);
        }
        else
        {
            _filterMode = FilterMode.None;
            _filterValue = "";
        }

        DisplayMessage(title, message, type, question, sessionId, cwd, questionTime, DateTime.Now);

        // Fresh funny quote + typewriter animation.
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
        BringToForeground();
    }

    private void ShowTrayBalloon(string title, string message, string type)
    {
        var icon = type switch
        {
            "error" => ToolTipIcon.Error,
            "success" => ToolTipIcon.Info,
            "pending" => ToolTipIcon.Warning,
            _ => ToolTipIcon.Info,
        };
        string truncated = message.Length > 200 ? message[..197] + "..." : message;
        truncated = truncated.Replace("`", "").Replace("*", "").Replace("#", "");
        _host.ShowBalloonNotification(title, truncated, icon);
    }

    public void BringToForeground()
    {
        if (!Visible) Show();
        WindowState = FormWindowState.Normal;
        TopMost = true;
        Activate();
        BringToFront();
        var releaseTimer = new System.Windows.Forms.Timer { Interval = 500 };
        releaseTimer.Tick += (_, _) =>
        {
            TopMost = false;
            releaseTimer.Stop();
            releaseTimer.Dispose();
        };
        releaseTimer.Start();
    }

    /// <summary>
    /// Force WebView2 environment creation on the current (UI) thread without
    /// showing the form. Called from <see cref="ClaudeIntegrationPlugin.Start"/>
    /// so the environment is built on the clean post-Application.Run call stack
    /// rather than deferred until the first pipe notification, which arrives
    /// nested inside a cross-thread Control.Invoke and reliably trips WebView2's
    /// RPC_E_CHANGED_MODE COM-apartment race on first init.
    /// </summary>
    public void Prewarm()
    {
        if (_webViewReady || _webViewFailed || _webViewInitStarted) return;
        // Touching Handle forces HWND creation for the form and recursively for
        // child controls (including the WebView2), which is what WebView2 needs
        // before EnsureCoreWebView2Async can proceed. The form stays invisible.
        _ = Handle;
        InitializeWebView2();
    }

    private async void InitializeWebView2()
    {
        if (_webViewInitStarted || _webViewReady || _webViewFailed) return;
        _webViewInitStarted = true;
        try
        {
            // With CreationProperties.UserDataFolder set on the WebView2
            // control in the constructor, we can just call the parameterless
            // EnsureCoreWebView2Async — the control creates its environment
            // internally, handling COM/STA bookkeeping correctly. This avoids
            // the "Cannot change thread mode after it is set" (RPC_E_CHANGED_MODE)
            // race that the manual CoreWebView2Environment.CreateAsync path hit.
            await _webView.EnsureCoreWebView2Async(null).ConfigureAwait(true);

            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
            _webViewReady = true;

            if (_pendingHtml != null)
            {
                _webView.NavigateToString(_pendingHtml);
                _pendingHtml = null;
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error("ChatPopupForm WebView2 init failed", ex);
            try { _context.LogError("ChatPopupForm WebView2 init failed", ex); } catch { }
            _webViewFailed = true;
            _webViewInitStarted = false;
            try { WebViewInitFailed?.Invoke(); } catch { }
        }
    }

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// Humanized relative time like "just now", "5m ago", "2h 30m ago",
    /// "3 days ago", "2 weeks ago". Used in the subtitle under the session
    /// info so the user sees at a glance how stale the notification is.
    /// </summary>
    private static string FormatRelative(DateTime ts)
    {
        if (ts == default) return "";
        var delta = DateTime.Now - ts;
        if (delta.TotalSeconds < 0) return "just now"; // clock skew guard
        if (delta.TotalSeconds < 45) return "just now";
        if (delta.TotalMinutes < 1.5) return "1 min ago";
        if (delta.TotalMinutes < 60)
            return $"{(int)Math.Round(delta.TotalMinutes)} min ago";
        if (delta.TotalHours < 24)
        {
            int h = (int)delta.TotalHours;
            int m = (int)(delta.TotalMinutes - h * 60);
            return m > 0 ? $"{h}h {m}m ago" : $"{h}h ago";
        }
        if (delta.TotalDays < 7)
        {
            int d = (int)delta.TotalDays;
            return d == 1 ? "1 day ago" : $"{d} days ago";
        }
        if (delta.TotalDays < 30)
        {
            int w = (int)(delta.TotalDays / 7);
            return w == 1 ? "1 week ago" : $"{w} weeks ago";
        }
        if (delta.TotalDays < 365)
        {
            int mo = (int)(delta.TotalDays / 30);
            return mo == 1 ? "1 month ago" : $"{mo} months ago";
        }
        int y = (int)(delta.TotalDays / 365);
        return y == 1 ? "1 year ago" : $"{y} years ago";
    }

    private static string FormatTimeBadge(DateTime ts)
    {
        if (ts == default) return "";
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        // Today → time only; any other day → "MMM d, h:mm tt" (e.g. "Apr 14, 3:42 PM").
        // Keeps today's popup compact while making history-navigation entries
        // unambiguous when you're browsing older days.
        string display = ts.Date == DateTime.Today
            ? ts.ToString("h:mm tt", culture)
            : ts.ToString("MMM d, h:mm tt", culture);
        string tooltip = ts.ToString("yyyy-MM-dd HH:mm:ss");
        return $" <span class=\"timestamp\" title=\"{tooltip}\">\u00B7 {display}</span>";
    }

    private string RenderHtml(string message)
    {
        bool isLight = _theme.BgDark.GetBrightness() > 0.5f;
        return ChatMarkdownRenderer.ToHtml(
            message,
            accentColorHex: ToHex(_accentColor),
            textColorHex: ToHex(isLight ? _theme.TextPrimary : _theme.TextSecondary),
            headingColorHex: ToHex(_theme.TextPrimary),
            bgColorHex: ToHex(_theme.BgDark),
            codeBgHex: isLight ? "rgba(0,0,0,0.06)" : "rgba(12, 16, 26, 0.8)",
            themePrimaryHex: ToHex(_theme.Primary));
    }

    private static string GetFirstName()
    {
        try
        {
            var userDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var userName = Path.GetFileName(userDir) ?? "";
            var first = userName.Split('.', ' ', '_', '-')[0];
            if (first.Length > 0)
                return char.ToUpper(first[0]) + first[1..];
            return userName;
        }
        catch { return "User"; }
    }

    private void DisplayMessage(string title, string message, string type,
        string question = "", string sessionId = "", string cwd = "",
        DateTime questionTime = default, DateTime responseTime = default)
    {
        _lastMessage = message;
        _lastType = type;
        ApplyTypeColors(type);

        // Heading shows the working folder when we have one (e.g. "DevToy")
        // instead of the generic hook title ("ProdToy - Done"). Falls back to
        // the title if there's no cwd.
        string folder = string.IsNullOrEmpty(cwd) ? "" : Path.GetFileName(cwd.TrimEnd('/', '\\'));
        string heading = string.IsNullOrEmpty(folder) ? title : folder;
        Text = heading;
        _titleLabel.Text = heading;

        // Subtitle: two lines — session info on top, date + relative time
        // underneath. Two lines via an embedded newline in a single Label;
        // AutoSize measures the tallest line so the label grows naturally.
        DateTime subtitleTime = responseTime != default ? responseTime
            : questionTime != default ? questionTime
            : DateTime.Now;
        string dateText = subtitleTime.ToString("dddd, MMM d, yyyy", System.Globalization.CultureInfo.CurrentCulture);
        string relative = FormatRelative(subtitleTime);

        string firstLine = string.IsNullOrEmpty(sessionId)
            ? "Claude notification"
            : $"Session {(sessionId.Length > 8 ? sessionId[..8] : sessionId)}";
        string secondLine = $"{dateText} \u00B7 {relative}";
        _subtitleLabel.Text = firstLine + "\n" + secondLine;

        string renderedMessage = RenderHtml(message);
        string firstName = GetFirstName();
        string questionTimeHtml = FormatTimeBadge(questionTime);
        string responseTimeHtml = FormatTimeBadge(responseTime);
        string bodyPrefix;
        if (!string.IsNullOrWhiteSpace(question))
        {
            var qLines = question.Split('\n');
            string shortQ = qLines.Length > 3 ? string.Join("\n", qLines.Take(3)) + "..." : question;
            string escapedQ = System.Net.WebUtility.HtmlEncode(shortQ).Replace("\n", "<br/>");
            bodyPrefix = $"<div class=\"user-block\"><div class=\"label\">{firstName}:{questionTimeHtml}</div><div class=\"text\">{escapedQ}</div></div><div class=\"claude-block\"><div class=\"claude-label\">Response:{responseTimeHtml}</div><div class=\"claude-content\">";
        }
        else
        {
            bodyPrefix = $"<div class=\"claude-block\"><div class=\"claude-label\">Response:{responseTimeHtml}</div><div class=\"claude-content\">";
        }

        string htmlContent = renderedMessage
            .Replace("<body>", $"<body>{bodyPrefix}")
            .Replace("</body>", "</div></div></body>");

        _snoozeCheckBox.Checked = IsSnoozed;

        // Smart resize: estimate content height from line count and adjust.
        // Use try-catch because Screen.FromControl can fail when the form
        // hasn't been shown yet (no screen context assigned).
        try
        {
            var lineCount = message.Split('\n').Length;
            int wrappedLines = lineCount;
            foreach (var line in message.Split('\n'))
            {
                if (line.Length > 80)
                    wrappedLines += (line.Length / 80);
            }
            int estimatedContentHeight = Math.Max(180, wrappedLines * 28 + 60);
            var screen = Visible ? Screen.FromControl(this) : (Screen.PrimaryScreen ?? Screen.AllScreens[0]);
            var workingArea = screen.WorkingArea;
            int maxHeight = (int)(workingArea.Height * 0.9);
            int newClientW = Math.Max(ClientSize.Width, 600);
            int newClientH = Math.Min(maxHeight, HeaderHeight + InfoBarHeight + estimatedContentHeight + FooterHeight);
            ClientSize = new Size(newClientW, newClientH);

            // Keep window within screen bounds
            int curLeft = Left, curTop = Top;
            if (curLeft < workingArea.Left || curTop < workingArea.Top ||
                curLeft + Width > workingArea.Right || curTop + Height > workingArea.Bottom)
            {
                Location = new Point(
                    Math.Clamp(curLeft, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - Width)),
                    Math.Clamp(curTop, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - Height)));
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"ChatPopup smart resize failed (non-fatal): {ex.Message}");
        }

        if (_webViewReady)
            _webView.NavigateToString(htmlContent);
        else
            _pendingHtml = htmlContent;

        UpdateHistoryNav();
        PositionControls();
    }

    private void ApplyTypeColors(string type)
    {
        var (accent, icon, badge) = type switch
        {
            "success" => (_theme.SuccessColor, "\u2713", _theme.SuccessBg),
            "error" => (_theme.ErrorColor, "\u2717", _theme.ErrorBg),
            _ => (_theme.Primary, "\u2726", _theme.PrimaryDim),
        };
        _accentColor = accent;
        _iconBadgeBg = badge;
        _iconLabel.Text = icon;
        _iconLabel.ForeColor = accent;
        _headerLine.BackColor = accent;
    }

    // ---------- Copy-to-clipboard ----------

    private Label CreateCopyLink(string text)
    {
        var link = new Label
        {
            Text = text,
            Font = new Font("Segoe UI", 8f),
            ForeColor = _theme.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Cursor = Cursors.Hand,
        };
        link.MouseEnter += (_, _) =>
        {
            link.ForeColor = _theme.Primary;
            link.Font = new Font(link.Font, FontStyle.Underline);
        };
        link.MouseLeave += (_, _) =>
        {
            link.ForeColor = _theme.TextSecondary;
            link.Font = new Font(link.Font, FontStyle.Regular);
        };
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
                _ => _lastMessage,
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
            PluginLog.Warn($"ChatPopup CopyAs({format}) failed: {ex.Message}");
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

        string headerTemplate = "Version:0.9\r\nStartHTML:0000000000\r\nEndHTML:0000000000\r\nStartFragment:0000000000\r\nEndFragment:0000000000\r\n";
        int headerLen = headerTemplate.Length;
        int startHtmlIdx = headerLen;
        int startFragIdx = headerLen + htmlPrefix.Length;
        int endFragIdx = startFragIdx + htmlBody.Length;
        int endHtmlIdx = endFragIdx + htmlSuffix.Length;

        return $"Version:0.9\r\nStartHTML:{startHtmlIdx:D10}\r\nEndHTML:{endHtmlIdx:D10}\r\nStartFragment:{startFragIdx:D10}\r\nEndFragment:{endFragIdx:D10}\r\n"
            + htmlPrefix + htmlBody + htmlSuffix;
    }

    private List<HistoryIndex> GetActiveIndex()
        => _filteredIndex ?? _history.LoadTodayIndex();

    private void UpdateHistoryNav()
    {
        var index = GetActiveIndex();
        bool hasMultiple = index.Count > 1;
        _prevButton.Visible = hasMultiple;
        _nextButton.Visible = hasMultiple;
        _navLabel.Visible = hasMultiple;
        _filterButton.Visible = index.Count > 0;

        if (hasMultiple)
        {
            int displayIdx = _viewingHistory ? _historyIndex + 1 : index.Count;
            _navLabel.Text = $"{displayIdx} / {index.Count}";
            _prevButton.Enabled = _historyIndex != 0;
            _nextButton.Enabled = _viewingHistory && _historyIndex < index.Count - 1;
        }

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
    }

    private void NavigateHistory(int direction)
    {
        _history.Invalidate();
        if (_filterMode != FilterMode.None)
        {
            _filteredIndex = _filterMode == FilterMode.Cwd
                ? _history.FilterByCwd(_selectedDate, _filterValue)
                : _history.FilterBySession(_selectedDate, _filterValue);
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

        var entry = _history.LoadEntry(index[_historyIndex]);
        if (entry != null)
        {
            var qts = entry.QuestionTimestamp == default ? entry.Timestamp : entry.QuestionTimestamp;
            DisplayMessage(entry.Title, entry.Message, entry.Type, entry.Question, entry.SessionId, entry.Cwd, qts, entry.Timestamp);
        }
    }

    private void ShowFilterDialog()
    {
        using var dlg = new FilterDialog(_theme, _history, _filterMode, _filterValue, _selectedDate);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _filterMode = dlg.SelectedMode;
            _filterValue = dlg.SelectedValue;
            _selectedDate = dlg.SelectedDate;
            _filteredIndex = null;

            if (_filterMode != FilterMode.None)
            {
                _history.Invalidate();
                _filteredIndex = _filterMode == FilterMode.Cwd
                    ? _history.FilterByCwd(_selectedDate, _filterValue)
                    : _history.FilterBySession(_selectedDate, _filterValue);

                if (_filteredIndex.Count > 0)
                {
                    _viewingHistory = true;
                    _historyIndex = _filteredIndex.Count - 1;
                    var entry = _history.LoadEntry(_filteredIndex[_historyIndex]);
                    if (entry != null)
                    {
                        var qts = entry.QuestionTimestamp == default ? entry.Timestamp : entry.QuestionTimestamp;
                        DisplayMessage(entry.Title, entry.Message, entry.Type, entry.Question, entry.SessionId, entry.Cwd, qts, entry.Timestamp);
                    }
                }
                else
                {
                    _viewingHistory = false;
                    _historyIndex = -1;
                    UpdateHistoryNav();
                    PositionControls();
                }
            }
            else
            {
                _viewingHistory = false;
                _historyIndex = -1;
                _selectedDate = DateTime.Today;
                UpdateHistoryNav();
                PositionControls();
            }
        }
    }

    // ---------- Layout ----------

    private void PositionControls()
    {
        if (_footerPanel == null || _okButton == null) return;

        int footerW = _footerPanel.ClientSize.Width;

        // Row 1: Copy links, centered above the OK button.
        int linkSpacing = 16;
        int totalLinkWidth = _copyMdLink.PreferredWidth + _copyPreviewLink.PreferredWidth + _copyHtmlLink.PreferredWidth + linkSpacing * 2;
        int linkStartX = (footerW - totalLinkWidth) / 2;
        int linkY = 8;
        _copyMdLink.Location = new Point(linkStartX, linkY);
        _copyPreviewLink.Location = new Point(_copyMdLink.Right + linkSpacing, linkY);
        _copyHtmlLink.Location = new Point(_copyPreviewLink.Right + linkSpacing, linkY);

        // Row 2: OK button, centered.
        _okButton.Location = new Point((footerW - _okButton.Width) / 2, linkY + _copyMdLink.Height + 6);

        // Row 3: Snooze checkbox, centered below OK.
        _snoozeCheckBox.Location = new Point((footerW - _snoozeCheckBox.PreferredSize.Width) / 2, _okButton.Bottom + 6);

        int ipw = _infoPanel.ClientSize.Width;
        if (_prevButton.Visible)
        {
            int navY = (InfoBarHeight - _prevButton.Height) / 2;
            int navX = ipw - 12;

            if (_filterButton.Visible)
            {
                navX -= _filterButton.Width;
                _filterButton.Location = new Point(navX, navY);
                navX -= 6;
            }

            navX -= _nextButton.Width;
            _nextButton.Location = new Point(navX, navY);
            navX -= (8 + _navLabel.PreferredWidth);
            _navLabel.Location = new Point(navX, navY + (_prevButton.Height - _navLabel.Height) / 2);
            navX -= (8 + _prevButton.Width);
            _prevButton.Location = new Point(navX, navY);
        }
        else if (_filterButton.Visible)
        {
            int navY = (InfoBarHeight - _filterButton.Height) / 2;
            _filterButton.Location = new Point(ipw - 12 - _filterButton.Width, navY);
        }
    }

    // ---------- Ok button + snooze ----------

    private void OnOkClick()
    {
        if (_snoozeCheckBox.Checked)
        {
            SetSnooze(DateTime.Now.AddMinutes(30));
        }
        else if (IsSnoozed)
        {
            SetSnooze(DateTime.MinValue);
        }
        Hide();
    }

    // ---------- Funny quote animation ----------

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
        var labelBounds = RectangleToClient(_animLabel.RectangleToScreen(_animLabel.ClientRectangle));

        // Derive 4 sparkle colors from the theme primary/primary-light so we
        // don't need to widen PluginTheme.
        Color[] palette = {
            _theme.PrimaryLight,
            _theme.Primary,
            Lighten(_theme.Primary, 0.3f),
            Lighten(_theme.PrimaryLight, 0.2f),
        };

        for (int i = 0; i < 3; i++)
        {
            _sparkles.Add(new Sparkle
            {
                X = labelBounds.X + _rng.Next(Math.Max(1, labelBounds.Width)),
                Y = labelBounds.Y + _rng.Next(Math.Max(1, labelBounds.Height)),
                VX = (_rng.NextDouble() - 0.5) * 8,
                VY = -_rng.NextDouble() * 4 - 1,
                Life = 1.0,
                Size = 3 + _rng.Next(5),
                Color = palette[_rng.Next(palette.Length)],
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

    private static Color Lighten(Color c, float amount)
    {
        int r = (int)Math.Min(255, c.R + 255 * amount);
        int g = (int)Math.Min(255, c.G + 255 * amount);
        int b = (int)Math.Min(255, c.B + 255 * amount);
        return Color.FromArgb(r, g, b);
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

        foreach (var s in _sparkles)
        {
            int alpha = Math.Clamp((int)(s.Life * 255), 0, 255);
            using var brush = new SolidBrush(Color.FromArgb(alpha, s.Color));
            g.FillEllipse(brush, (float)s.X, (float)s.Y, s.Size, s.Size);
        }
    }

    // ---------- Theme ----------

    private void OnThemeChanged(PluginTheme theme)
    {
        _theme = theme;
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        _headerPanel.BackColor = theme.BgHeader;
        _animLabel.ForeColor = theme.PrimaryLight;
        _headerLine.BackColor = theme.Primary;
        _infoPanel.BackColor = theme.BgDark;
        _titleLabel.ForeColor = theme.TextPrimary;
        _subtitleLabel.ForeColor = theme.TextSecondary;
        _separator.BackColor = theme.Border;
        _footerPanel.BackColor = theme.BgDark;
        _webViewContainer.BackColor = theme.BgDark;
        _webView.DefaultBackgroundColor = theme.BgDark;

        _okButton.BackColor = theme.Primary;
        _okButton.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        _okButton.FlatAppearance.MouseDownBackColor = theme.PrimaryDim;

        _snoozeCheckBox.ForeColor = theme.TextSecondary;

        foreach (var copyLink in new[] { _copyMdLink, _copyPreviewLink, _copyHtmlLink })
            copyLink.ForeColor = theme.TextSecondary;

        _prevButton.BackColor = theme.PrimaryDim;
        _prevButton.ForeColor = theme.TextSecondary;
        _prevButton.FlatAppearance.MouseOverBackColor = theme.Primary;
        _nextButton.BackColor = theme.PrimaryDim;
        _nextButton.ForeColor = theme.TextSecondary;
        _nextButton.FlatAppearance.MouseOverBackColor = theme.Primary;
        _navLabel.ForeColor = theme.TextSecondary;

        ApplyTypeColors(_lastType);
        if (_webViewReady && !string.IsNullOrEmpty(_lastMessage))
            _webView.NavigateToString(RenderHtml(_lastMessage));
        UpdateHistoryNav();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Hide instead of close so WebView2 state survives across notifications.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _host.ThemeChanged -= OnThemeChanged; } catch { }
            try { _typeTimer?.Dispose(); } catch { }
            try { _sparkleTimer?.Dispose(); } catch { }
            try { _webView?.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
