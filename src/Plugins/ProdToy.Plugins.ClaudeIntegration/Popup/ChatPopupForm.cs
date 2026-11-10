using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>
/// Plugin-owned Claude chat popup. Owns everything Claude-specific that
/// used to live on the host's PopupForm: funny-quote header with sparkles,
/// the footer (Snooze menu · OK · ⋯ menu), notification-mode dispatch (popup vs. tray balloon),
/// chat history navigation, and filter dialog.
///
/// Lifecycle: created lazily in the notify handler, kept alive for the
/// life of the plugin. Show/Hide reuses the window so WebView2 state
/// survives across notifications.
/// </summary>
sealed partial class ChatPopupForm : Form, IPluginPopup
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
    private readonly RoundedButton _firstButton;
    private readonly RoundedButton _lastButton;
    private readonly TagChipStrip _tagChips;
    private readonly Label _navLabel;
    private readonly Label _scopeChip;

    // Footer: one row - Snooze ▾ · OK · ⋯ (copy and open actions).
    private readonly Panel _footerPanel;
    private readonly Panel _separator;
    private readonly RoundedButton _okButton;
    private readonly RoundedButton _snoozeButton;
    private readonly RoundedButton _moreButton;
    private readonly ContextMenuStrip _moreMenu;
    private readonly System.Windows.Forms.Timer _snoozeTimer;

    // Content area.
    private readonly Panel _webViewContainer;
    private readonly WebView2 _webView;
    private readonly Label _loadingLabel;

    // History tree: strip + panel on the left, the rest in the right pane.
    private readonly Panel _strip;
    private readonly RoundedButton _treeButton;
    private readonly HistoryTreePanel _treePanel;
    private readonly Panel _treeSplitter;
    private readonly Panel _rightPane;
    private readonly System.Windows.Forms.Timer _loadingTimer;

    // New / Read: the queue's rules, the chips under the tree button and the count on it.
    private readonly UnreadQueue _unread;
    private readonly UnreadChipStrip _chips;
    private readonly Label _unreadBadge;

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

    // History navigation: the scope is a tree node, its whole subtree the list.
    private readonly HistoryTree _tree;
    private HistoryNode? _scope;
    private string? _itemKey;
    private int _loadVersion;
    private bool _treeOpen;
    private int _treeWidth;

    public const int MinTreeWidth = 220;
    public const int MaxTreeWidth = 1200;
    private const int StripWidth = 48;

    private const int HeaderHeight = 58;
    private const int InfoBarHeight = 72;
    private const int FooterHeight = 60;

    public ChatPopupForm(IPluginContext context, ChatHistory history, HistoryTree tree)
    {
        _context = context;
        _host = context.Host;
        _history = history;
        _tree = tree;
        _theme = _host.CurrentTheme;
        _accentColor = _theme.Primary;
        _iconBadgeBg = _theme.PrimaryDim;
        var startSettings = context.LoadSettings<ClaudePluginSettings>();
        _showQuotes = startSettings.ShowQuotes;
        _treeOpen = startSettings.HistoryTreeOpen;
        _treeWidth = Math.Clamp(startSettings.HistoryTreeWidth, MinTreeWidth, MaxTreeWidth);

        Text = "ProdToy — Claude";
        Icon = IconHelper.CreateAppIcon(_theme.Primary);
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
        _prevButton.Click += (_, _) => NavigateHistory(older: true);

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
        _nextButton.Click += (_, _) => NavigateHistory(older: false);

        _firstButton = NavEndButton("|\u25C0");
        _firstButton.Click += (_, _) => JumpHistory(last: false);
        _lastButton = NavEndButton("\u25B6|");
        _lastButton.Click += (_, _) => JumpHistory(last: true);
        _tagChips = new TagChipStrip { Height = 16, Visible = false };

        _navLabel = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 8f),
            ForeColor = _theme.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Visible = false,
        };

        _scopeChip = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5f),
            BackColor = _theme.PrimaryDim,
            ForeColor = _theme.TextPrimary,
            Padding = new Padding(8, 3, 8, 3),
            Visible = false,
        };

        _infoPanel.Controls.AddRange(new Control[]
        {
            _iconLabel, _titleLabel, _subtitleLabel,
            _firstButton, _prevButton, _navLabel, _nextButton, _lastButton, _scopeChip, _tagChips,
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

        // Snooze ▾: 10 · 20 · 30 · 60 minutes, More…, Wake now. Snoozing closes the window and marks nothing Read.
        _snoozeButton = FooterButton("Snooze \u25BE", new Size(120, 32));
        _snoozeButton.Click += (_, _) => SnoozeMenu.ShowUnder(_snoozeButton, _context, _theme,
            snoozed: () => { UpdateSnoozeButton(); Hide(); },
            woke: UpdateSnoozeButton);

        // ⋯: the copy and open actions for the response on screen.
        _moreButton = FooterButton("\u22EF", new Size(44, 32));
        _moreButton.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        _moreMenu = new ContextMenuStrip { ShowImageMargin = false };
        _moreMenu.Items.Add("Copy Markdown", null, (_, _) => CopyAs("markdown"));
        _moreMenu.Items.Add("Copy Preview", null, (_, _) => CopyAs("preview"));
        _moreMenu.Items.Add("Copy HTML", null, (_, _) => CopyAs("html"));
        _moreMenu.Items.Add(new ToolStripSeparator());
        _moreMenu.Items.Add("Open as HTML page", null, (_, _) => OpenAsHtmlPage());
        _moreButton.Click += (_, _) => _moreMenu.Show(_moreButton, new Point(_moreButton.Width - _moreMenu.PreferredSize.Width, _moreButton.Height));

        // The button counts the snooze down while the window is up.
        _snoozeTimer = new System.Windows.Forms.Timer { Interval = 20_000 };
        _snoozeTimer.Tick += (_, _) => { if (Visible) UpdateSnoozeButton(); };
        _snoozeTimer.Start();

        _footerPanel.Controls.Add(_separator);
        _footerPanel.Controls.Add(_snoozeButton);
        _footerPanel.Controls.Add(_okButton);
        _footerPanel.Controls.Add(_moreButton);

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

        _loadingLabel = new Label
        {
            Text = "Loading…",
            AutoSize = true,
            Font = new Font("Segoe UI", 10f),
            ForeColor = _theme.TextSecondary,
            BackColor = _theme.BgDark,
            Location = new Point(24, 16),
            Visible = false,
        };
        _webViewContainer.Controls.Add(_loadingLabel);
        _loadingLabel.BringToFront();
        _loadingTimer = new System.Windows.Forms.Timer { Interval = 120 };
        _loadingTimer.Tick += (_, _) =>
        {
            _loadingTimer.Stop();
            _loadingLabel.Visible = true;
        };

        // --- History tree: a strip with its button, then the panel ---
        _strip = new Panel { Dock = DockStyle.Left, Width = StripWidth, BackColor = _theme.BgHeader };
        _treeButton = new RoundedButton
        {
            Text = "\uE8B7",
            Font = new Font("Segoe MDL2 Assets", 12f),
            Size = new Size(36, 36),
            Location = new Point(6, 12),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        _treeButton.FlatAppearance.BorderSize = 0;
        _treeButton.Click += (_, _) => ToggleTree();
        _strip.Controls.Add(_treeButton);

        var readMarks = tree.ReadStore ?? ReadStore.Load(Path.Combine(context.DataDirectory, "read-marks.json"), DateTime.Now);
        tree.ReadStore ??= readMarks;
        _unread = new UnreadQueue(readMarks);
        _chips = new UnreadChipStrip { Theme = _theme, Location = new Point(2, 58), Width = StripWidth - 4, Height = 100 };
        _chips.ChipClicked += ShowItem;
        _chips.MarkRead += key => _unread.MarkRead(key);
        _chips.MarkAllRead += () => _unread.MarkAllRead();
        _unreadBadge = new Label
        {
            AutoSize = false,
            Size = new Size(22, 14),
            Location = new Point(StripWidth - 24, 5),
            Font = new Font("Segoe UI", 6.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = NewMark.Back(_theme),
            ForeColor = NewMark.Fore(_theme),
            Visible = false,
        };
        _strip.Controls.Add(_chips);
        _strip.Controls.Add(_unreadBadge);
        _unreadBadge.BringToFront();
        _strip.Resize += (_, _) =>
        {
            _chips.Height = Math.Max(0, _strip.ClientSize.Height - _chips.Top - 8);
            UpdateUnreadBadge();
        };
        readMarks.Changed += OnReadMarksChanged;

        _treePanel = new HistoryTreePanel(_theme) { Width = _treeWidth, Visible = _treeOpen };
        _treePanel.SetTree(_tree);
        _treePanel.NodePicked += OnNodePicked;
        _treePanel.NodeMenu += OnNodeMenu;

        _treeSplitter = new Panel { Dock = DockStyle.Left, Width = 5, Cursor = Cursors.SizeWE, BackColor = _theme.BgDark, Visible = _treeOpen };
        WireSplitter();

        _rightPane = new Panel { Dock = DockStyle.Fill, BackColor = _theme.BgDark };
        _rightPane.Controls.Add(_webViewContainer);
        _rightPane.Controls.Add(_footerPanel);
        _rightPane.Controls.Add(_infoPanel);

        ApplyMinimumWidth();
        ClientSize = new Size(800 + StripWidth + (_treeOpen ? _treeWidth : 0), 520);
        ApplyTreeButtonColors();
        AcceptButton = _okButton;

        // WinForms docks the last-added control first. Fill goes in last visually
        // but first in the Controls collection.
        Controls.Add(_rightPane);
        Controls.Add(_treeSplitter);
        Controls.Add(_treePanel);
        Controls.Add(_strip);
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
        // Shown fires once; a window hidden by OK and shown again lays out again too.
        VisibleChanged += (_, _) => { if (Visible) { UpdateSnoozeButton(); UpdateHistoryNav(); PositionControls(); } };
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
    void IPluginPopup.Show() => ShowPopup(_lastMessage.Length > 0 ? "Claude" : "ProdToy", _lastMessage, _lastType, bypassFilter: true);
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

    /// <summary>True when the user-configured suppress regex matches the
    /// message. Empty pattern → never matches. Invalid pattern → never matches
    /// (we don't want a typo in settings to silently suppress everything).</summary>
    private static bool MatchesSuppressRegex(string message, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || message == null) return false;
        try
        {
            return Regex.IsMatch(message, pattern);
        }
        catch (ArgumentException)
        {
            // Bad regex — treat as no filter rather than blocking everything.
            return false;
        }
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
    public void ShowPopup(string title, string message, string type, string sessionId = "", string cwd = "", string machineName = "", bool bypassFilter = false, SavedResponse? saved = null)
    {
        // The tree takes every saved item, also one the window stays closed for.
        string? newItem = null;
        if (saved != null)
        {
            _tree.Add(saved.Index);
            newItem = HistoryTree.ItemKey(saved.Index);
            _treePanel.RefreshRows();
        }

        // Suppress messages matching the user-configured regex (default
        // catches the bare "Claude is waiting for your input" pings) — but
        // only for live incoming notifications. When the user explicitly
        // clicks "Show Last Notification" we always honor the request, even
        // if the saved message would otherwise be filtered.
        var settings = _context.LoadSettings<ClaudePluginSettings>();
        bool allowed = settings.NotificationsEnabled && !IsSnoozed
            && (bypassFilter || !MatchesSuppressRegex(message, settings.SuppressMessageRegex));

        string mode = settings.NotificationMode;
        bool wantPopup = mode is "Popup" or "Popup + Windows";
        bool wantBalloon = mode is "Windows" or "Popup + Windows";

        if (allowed && wantBalloon)
            ShowTrayBalloon(title, message, type);

        // A saved item is New until OK. While the item on screen is New the window is left alone: only the chips,
        // the tree and the pager's total change. Snoozed or suppressed items stay New without a pop-up.
        var action = _unread.Arrive(newItem, allowed && wantPopup, Visible && WindowState != FormWindowState.Minimized, _itemKey);
        RefreshUnread();
        if (action == ArrivalAction.Silent) return;
        if (action == ArrivalAction.Queue)
        {
            UpdateHistoryNav();
            PositionControls();
            return;
        }

        _loadVersion++;
        HideLoading();
        _lastMessage = message;
        _lastType = type;

        // A new notification wins: the scope stays if it holds the item, else
        // the scope moves to the item's own group.
        if (newItem != null && _tree.IsReady)
        {
            EnsureScope();
            SetScope(_tree.ScopeFor(newItem, _scope), newItem);
            if (_tree.ItemNode(newItem, _scope) is { } node) _treePanel.Reveal(node);
        }
        else if (newItem != null)
        {
            _itemKey = newItem;   // the tree is still loading: OK must still mark the item on screen
            _chips.SetCurrent(newItem);
        }

        var entry = saved?.Entry;
        DateTime questionTime = entry == null ? default
            : entry.QuestionTimestamp == default ? entry.Timestamp : entry.QuestionTimestamp;
        string openedFolder = newItem != null ? _tree.OpenedFolderOf(newItem) : "";
        if (openedFolder.Length == 0) openedFolder = entry?.ProjectRoot ?? "";
        DisplayMessage(title, message, type, entry?.Question ?? "", sessionId, cwd, questionTime, DateTime.Now, machineName,
            projectRoot: openedFolder);

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

    /// <summary>A manual open (tray, hotkey): the remembered scope and the last viewed item. Nothing is rebuilt.</summary>
    public void ShowManual()
    {
        if (_tree.IsReady)
        {
            EnsureScope();
            if (_itemKey == null || !_tree.Contains(_scope!, _itemKey))
            {
                var settings = _context.LoadSettings<ClaudePluginSettings>();
                var items = _tree.Items(_scope!);
                var remembered = items.FirstOrDefault(i => HistoryTree.ItemKey(i) == settings.HistoryItemKey);
                var item = remembered ?? items.FirstOrDefault();
                if (item != null) LoadItem(item);
            }

            SyncTree();
            MergeChangedFiles();
        }

        Show();
        WindowState = WindowState == FormWindowState.Minimized ? FormWindowState.Normal : WindowState;
        BringToForeground();
    }

    /// <summary>The first build finished: show the rows, and the remembered scope when the window is already up.</summary>
    public void OnTreeReady()
    {
        EnsureScope();
        _treePanel.RefreshRows();
        SyncTree();
        RefreshUnread();
    }

    /// <summary>The scope once, from the settings; a saved key that is gone falls back to its nearest ancestor that exists.</summary>
    private void EnsureScope()
    {
        if (_scope != null) return;
        _scope = _tree.FindOrAncestor(_context.LoadSettings<ClaudePluginSettings>().HistoryScopeKey);
    }

    private void SetScope(HistoryNode scope, string itemKey)
    {
        _scope = scope;
        _itemKey = itemKey;
        RememberScope();
        SyncTree();
    }

    private void RememberScope()
    {
        var s = _context.LoadSettings<ClaudePluginSettings>();
        _context.SaveSettings(s with { HistoryScopeKey = _scope?.Key ?? "", HistoryItemKey = _itemKey ?? "" });
    }

    private void SyncTree()
    {
        _chips.SetCurrent(_itemKey);
        _treePanel.SetState(_scope, _itemKey);
        UpdateHistoryNav();
        PositionControls();
    }

    /// <summary>A second machine's items: read off the UI thread, added in place.</summary>
    private async void MergeChangedFiles()
    {
        try
        {
            var found = await Task.Run(_history.LoadChangedFiles);
            bool added = false;
            foreach (var item in found) added |= _tree.Add(item);
            if (added) SyncTree();
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"Could not merge changed history files: {ex.Message}");
        }
    }

    private void OnNodePicked(HistoryNode node)
    {
        if (ReferenceEquals(node, _tree.Pinned)) return;   // a list of pins, not a scope

        if (node.Kind == HistoryNodeKind.Pin)
        {
            // A pin behaves as its target; an item scopes to its own group. A gone target does nothing.
            if (_tree.Find(node.Target) is not { } target) return;
            if (target.IsItem) _scope = target.Parent;
            OnNodePicked(target);
            return;
        }

        if (node.IsItem)
        {
            _itemKey = HistoryTree.ItemKey(node.Item!);
            RememberScope();
            SyncTree();
            LoadItem(node.Item!);
            return;
        }

        _scope = node;
        var items = _tree.Items(node);
        bool newest = node.Kind is HistoryNodeKind.Group or HistoryNodeKind.Tag;
        bool stays = !newest && _itemKey != null && _tree.Contains(node, _itemKey);
        if (!stays && items.Count > 0)
        {
            _itemKey = HistoryTree.ItemKey(items[0]);
            LoadItem(items[0]);
        }

        RememberScope();
        SyncTree();
    }

    /// <summary>Loads one item off the UI thread; a newer load drops an older one.</summary>
    private async void LoadItem(HistoryIndex item)
    {
        int version = ++_loadVersion;
        _itemKey = HistoryTree.ItemKey(item);
        _chips.SetCurrent(_itemKey);
        _loadingTimer.Start();
        try
        {
            var (entry, html) = await Task.Run(() =>
            {
                var e = _history.LoadEntry(item);
                return (e, e == null ? "" : RenderHtml(e.Message));
            });
            if (version != _loadVersion) return;

            HideLoading();
            if (entry == null) return;

            var qts = entry.QuestionTimestamp == default ? entry.Timestamp : entry.QuestionTimestamp;
            string openedFolder = _tree.OpenedFolderOf(HistoryTree.ItemKey(item));
            DisplayMessage(entry.Title, entry.Message, entry.Type, entry.Question, entry.SessionId, entry.Cwd, qts,
                entry.Timestamp, entry.MachineName, html, openedFolder.Length > 0 ? openedFolder : entry.ProjectRoot ?? "");
        }
        catch (Exception ex)
        {
            if (version == _loadVersion) HideLoading();
            PluginLog.Warn($"Could not load a history item: {ex.Message}");
        }
    }

    private void HideLoading()
    {
        _loadingTimer.Stop();
        _loadingLabel.Visible = false;
    }

    private void ToggleTree()
    {
        SetTreeOpen(!_treeOpen);
        var s = _context.LoadSettings<ClaudePluginSettings>();
        _context.SaveSettings(s with { HistoryTreeOpen = _treeOpen });
    }

    public void SetTreeOpen(bool open)
    {
        if (open == _treeOpen) return;

        _treeOpen = open;
        _treePanel.Visible = open;
        _treeSplitter.Visible = open;
        ApplyTreeButtonColors();

        // The panel takes its room from outside, so the message keeps its width.
        int width = Width + (open ? _treeWidth : -_treeWidth);
        ApplyMinimumWidth();
        if (WindowState == FormWindowState.Normal) Width = width;
        SyncTree();
        UpdateUnreadBadge();
    }

    public void SetTreeWidth(int width)
    {
        int next = Math.Clamp(width, MinTreeWidth, MaxTreeWidth);
        if (_treeOpen && WindowState == FormWindowState.Normal) Width += next - _treeWidth;
        _treeWidth = next;
        _treePanel.Width = next;
        ApplyMinimumWidth();
    }

    private void ApplyMinimumWidth()
        => MinimumSize = new Size(480 + StripWidth + (_treeOpen ? _treeWidth : 0), 360);

    private void ApplyTreeButtonColors()
    {
        _treeButton.BackColor = _treeOpen ? _theme.Primary : _theme.PrimaryDim;
        _treeButton.ForeColor = _treeOpen ? Color.White : _theme.TextSecondary;
        _treeButton.FlatAppearance.MouseOverBackColor = _theme.Primary;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Alt | Keys.Left)) { NavigateHistory(older: true); return true; }
        if (keyData == (Keys.Alt | Keys.Right)) { NavigateHistory(older: false); return true; }
        if (keyData == Keys.Escape) { Hide(); return true; }   // closes; marks nothing Read
        return base.ProcessCmdKey(ref msg, keyData);
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
            _webView.CoreWebView2.NavigationStarting += OnWebNavigationStarting;
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

    private RoundedButton NavEndButton(string text)
    {
        var button = new RoundedButton
        {
            Text = text,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Size = new Size(36, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.PrimaryDim,
            ForeColor = _theme.TextSecondary,
            Cursor = Cursors.Hand,
            Visible = false,
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = _theme.Primary;
        return button;
    }

    /// <summary>An output file link: Notepad gets the path as an argument, nothing goes through the shell.</summary>
    private static void OnWebNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!e.Uri.StartsWith(PromptCards.OpenFileScheme, StringComparison.Ordinal)) return;

        e.Cancel = true;
        try
        {
            string path = Uri.UnescapeDataString(e.Uri[PromptCards.OpenFileScheme.Length..]);
            if (!PromptCards.CanOpen(path)) return;

            var start = new System.Diagnostics.ProcessStartInfo("notepad.exe") { UseShellExecute = false };
            start.ArgumentList.Add(path);
            System.Diagnostics.Process.Start(start);
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"Could not open the output file: {ex.Message}");
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
        DateTime questionTime = default, DateTime responseTime = default,
        string machineName = "", string? renderedHtml = null, string projectRoot = "")
    {
        _lastMessage = message;
        _lastType = type;
        ApplyTypeColors(type);

        // Heading shows the working folder when we have one (e.g. "DevToy")
        // instead of the generic hook title ("ProdToy - Done"). Falls back to
        // the title if there's no cwd.
        // When the session moved below the folder the CLI was opened in, it reads "opened › inner".
        string folder = string.IsNullOrEmpty(cwd) ? "" : Path.GetFileName(cwd.TrimEnd('/', '\\'));
        if (folder.Length > 0 && projectRoot.Length > 0 && HistoryTree.FolderKey(projectRoot) != HistoryTree.FolderKey(cwd))
            folder = $"{HistoryTree.FolderLabel(projectRoot)} \u203A {HistoryTree.InnerLabel(projectRoot, cwd)}";
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
        if (!string.IsNullOrEmpty(machineName))
            firstLine += $" · {machineName}";
        string secondLine = $"{dateText} \u00B7 {relative}";
        _subtitleLabel.Text = firstLine + "\n" + secondLine;

        string renderedMessage = renderedHtml ?? RenderHtml(message);
        string firstName = GetFirstName();
        string questionTimeHtml = FormatTimeBadge(questionTime);
        string responseTimeHtml = FormatTimeBadge(responseTime);
        string bodyPrefix;
        if (!string.IsNullOrWhiteSpace(question))
        {
            string asked;
            if (PromptCards.ToHtml(question, questionTimeHtml) is { } cards)
            {
                asked = cards;
            }
            else
            {
                var qLines = question.Split('\n');
                string shortQ = qLines.Length > 3 ? string.Join("\n", qLines.Take(3)) + "..." : question;
                string escapedQ = System.Net.WebUtility.HtmlEncode(shortQ).Replace("\n", "<br/>");
                asked = $"<div class=\"user-block\"><div class=\"label\">{firstName}:{questionTimeHtml}</div><div class=\"text\">{escapedQ}</div></div>";
            }

            bodyPrefix = $"{asked}<div class=\"claude-block\"><div class=\"claude-label\">Response:{responseTimeHtml}</div><div class=\"claude-content\">";
        }
        else
        {
            bodyPrefix = $"<div class=\"claude-block\"><div class=\"claude-label\">Response:{responseTimeHtml}</div><div class=\"claude-content\">";
        }

        string htmlContent = renderedMessage
            .Replace("<body>", $"<body>{bodyPrefix}")
            .Replace("</body>", "</div></div></body>");

        UpdateSnoozeButton();

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
            int leftWidth = StripWidth + (_treeOpen ? _treeWidth : 0);
            int newClientW = Math.Max(ClientSize.Width, 600 + leftWidth);

            // With the tree open the window keeps its height: a jumping list is hard to click.
            int newClientH = _treeOpen
                ? ClientSize.Height
                : Math.Min(maxHeight, HeaderHeight + InfoBarHeight + estimatedContentHeight + FooterHeight);
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

    private RoundedButton FooterButton(string text, Size size)
    {
        var button = new RoundedButton
        {
            Text = text,
            Font = new Font("Segoe UI", 9f),
            Size = size,
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.PrimaryDim,
            ForeColor = _theme.TextPrimary,
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = _theme.Primary;
        return button;
    }

    /// <summary>The Snooze button says how long is left while snoozed.</summary>
    private void UpdateSnoozeButton()
    {
        var until = SnoozeUntil;
        string text = SnoozeMath.ButtonText(until, DateTime.Now) + " \u25BE";
        if (_snoozeButton.Text == text) return;
        _snoozeButton.Text = text;
        int width = TextRenderer.MeasureText(text, _snoozeButton.Font).Width + 24;
        _snoozeButton.Width = Math.Max(120, width);
        PositionControls();
    }

    /// <summary>A short "done" or "failed" on the ⋯ button after one of its actions.</summary>
    private void FlashMoreButton(bool ok)
    {
        _moreButton.Text = ok ? "\u2713" : "!";
        _moreButton.ForeColor = ok ? _theme.SuccessColor : _theme.ErrorColor;
        var resetTimer = new System.Windows.Forms.Timer { Interval = ok ? 1500 : 2000 };
        resetTimer.Tick += (_, _) =>
        {
            _moreButton.Text = "\u22EF";
            _moreButton.ForeColor = _theme.TextPrimary;
            resetTimer.Stop();
            resetTimer.Dispose();
        };
        resetTimer.Start();
    }

    private void CopyAs(string format)
    {
        if (string.IsNullOrEmpty(_lastMessage)) return;

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

            FlashMoreButton(ok: true);
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"ChatPopup CopyAs({format}) failed: {ex.Message}");
            FlashMoreButton(ok: false);
        }
    }

    /// <summary>Render the current response to a full themed HTML document, save it
    /// to a temp file under %TEMP%\ProdToy, and open it in the default browser.</summary>
    private void OpenAsHtmlPage()
    {
        if (string.IsNullOrEmpty(_lastMessage)) return;
        try
        {
            string html = RenderHtml(_lastMessage);
            // Standalone-page only: wrap the content in a centered, fixed-width card
            // (padding + rounded border + shadow) sitting on the page background.
            // The popup preview / clipboard HTML are untouched.
            string bgPage = ToHex(_theme.BgDark);
            string bgCard = ToHex(_theme.BgHeader);
            string border = ToHex(_theme.Border);
            string overrideCss =
                "<style>html,body{background:" + bgPage + ";}" +
                "body{padding:28px 16px !important;}" +
                ".prodtoy-card{max-width:860px;margin:0 auto;padding:32px 36px 40px 36px;background:" + bgCard +
                ";border:1px solid " + border + ";border-radius:14px;box-shadow:0 10px 34px rgba(0,0,0,0.35);}</style>";
            html = html.Contains("</head>")
                ? html.Replace("</head>", overrideCss + "</head>")
                : overrideCss + html;
            // Wrap everything between the real <body> and </body> in the card div.
            int bodyOpen = html.IndexOf("<body>", StringComparison.OrdinalIgnoreCase);
            int bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (bodyOpen >= 0 && bodyClose > bodyOpen)
            {
                int contentStart = bodyOpen + "<body>".Length;
                html = html.Substring(0, contentStart)
                    + "<div class=\"prodtoy-card\">"
                    + html.Substring(contentStart, bodyClose - contentStart)
                    + "</div>"
                    + html.Substring(bodyClose);
            }
            string dir = Path.Combine(Path.GetTempPath(), "ProdToy");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"claude-{DateTime.Now:yyyyMMdd-HHmmss-fff}.html");
            File.WriteAllText(file, html, System.Text.Encoding.UTF8);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
            FlashMoreButton(ok: true);
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"ChatPopup OpenAsHtmlPage failed: {ex.Message}");
            FlashMoreButton(ok: false);
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

    private IReadOnlyList<HistoryIndex> ScopeItems()
        => _scope != null && _tree.IsReady ? _tree.Items(_scope) : Array.Empty<HistoryIndex>();

    private int CurrentPosition(IReadOnlyList<HistoryIndex> items)
    {
        if (_itemKey == null) return -1;
        for (int i = 0; i < items.Count; i++)
            if (HistoryTree.ItemKey(items[i]) == _itemKey) return i;
        return -1;
    }

    private void UpdateHistoryNav()
    {
        var items = ScopeItems();
        // What shows is decided here and kept: PositionControls goes by it, never by Control.Visible (false while hidden).
        _pagerShown = items.Count > 1;
        _pagerCount = items.Count;
        if (_scope != null) _scopeChip.Text = _tree.ScopeLabel(_scope);

        if (_pagerShown)
        {
            // The list is latest first; the pager runs oldest (1) to newest (n).
            int at = CurrentPosition(items);
            _navLabel.Text = $"{PagerPosition.Shown(at, items.Count)} / {items.Count}";
            _prevButton.Enabled = _firstButton.Enabled = PagerPosition.CanOlder(at, items.Count);
            _nextButton.Enabled = _lastButton.Enabled = PagerPosition.CanNewer(at, items.Count);
        }

        ShowTagChips();
    }

    private bool _pagerShown;
    private int _pagerCount;

    /// <summary>One step in the scope: Previous = one older, Next = one newer (the newest is the last position).</summary>
    private void NavigateHistory(bool older)
    {
        var items = ScopeItems();
        if (items.Count == 0) return;

        int now = CurrentPosition(items);
        int at = older ? PagerPosition.Older(now, items.Count) : PagerPosition.Newer(now, items.Count);
        LoadItem(items[at]);
        RememberScope();
        SyncTree();
    }

    private void PositionControls()
    {
        if (_footerPanel == null || _okButton == null || _snoozeButton == null || _moreButton == null) return;

        // The footer: one row, OK centred, Snooze on its left, ⋯ on its right (below the 1 px separator).
        var row = FooterLayout.Place(_footerPanel.ClientSize.Width, _footerPanel.ClientSize.Height - _separator.Height,
            _snoozeButton.Size, _okButton.Size, _moreButton.Size);
        _snoozeButton.Location = new Point(row.Snooze.X, row.Snooze.Y + _separator.Height);
        _okButton.Location = new Point(row.Ok.X, row.Ok.Y + _separator.Height);
        _moreButton.Location = new Point(row.More.X, row.More.Y + _separator.Height);

        // The info bar: one rule places the pager, the chip and the subtitle's stop, so no two controls overlap.
        var subtitle = _subtitleLabel.GetPreferredSize(Size.Empty);
        string widest = $"{_pagerCount} / {_pagerCount}";
        var counter = TextRenderer.MeasureText(widest, _navLabel.Font);
        bool hasChip = _scope != null && _tree.IsReady && _scopeChip.Text.Length > 0;
        var r = InfoBarLayout.Place(new InfoBarLayout.Input(
            _infoPanel.ClientSize.Width, InfoBarHeight,
            _subtitleLabel.Left + Math.Max(_titleLabel.PreferredWidth, subtitle.Width),
            _prevButton.Size, _firstButton.Size, new Size(counter.Width + 4, counter.Height),
            hasChip ? _scopeChip.PreferredSize : Size.Empty, _pagerShown));

        foreach (var c in new Control[] { _firstButton, _prevButton, _navLabel, _nextButton, _lastButton }) c.Visible = r.ShowPager;
        if (r.ShowPager)
        {
            _firstButton.Bounds = r.First;
            _prevButton.Bounds = r.Previous;
            _navLabel.Bounds = r.Counter;
            _nextButton.Bounds = r.Next;
            _lastButton.Bounds = r.Last;
        }

        _scopeChip.Visible = r.ShowChip;
        if (r.ShowChip)
        {
            _scopeChip.Location = r.Chip.Location;
            _scopeChip.SendToBack();   // a safety net only: the rule above keeps it clear of the buttons
        }

        // A long subtitle stops before the chip or the pager.
        _subtitleLabel.AutoSize = false;
        _subtitleLabel.AutoEllipsis = true;
        _subtitleLabel.Size = new Size(Math.Max(0, Math.Min(subtitle.Width, r.SubtitleRight - _subtitleLabel.Left)), subtitle.Height);
    }

    // ---------- Ok button + snooze ----------

    /// <summary>OK: the item on screen is Read. The newest New item shows next; with none left the window closes.</summary>
    private void OnOkClick()
    {
        string? next = _unread.Ok(_itemKey, TimeOf);
        if (next == null)
        {
            Hide();
            return;
        }

        ShowItem(next);
    }

    private DateTime? TimeOf(string itemKey) => _tree.FindItem(itemKey)?.Timestamp;

    /// <summary>Shows one item (a chip, or the next New one after OK): its scope as for a new notification, the tree revealed. It stays New.</summary>
    private void ShowItem(string itemKey)
    {
        if (!_tree.IsReady || _tree.FindItem(itemKey) is not { } item) return;

        EnsureScope();
        SetScope(_tree.ScopeFor(itemKey, _scope), itemKey);
        if (_tree.ItemNode(itemKey, _scope) is { } node) _treePanel.Reveal(node);
        LoadItem(item);
        if (!Visible) ShowManual();
    }

    // ---------- New chips ----------

    private void OnReadMarksChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            if (IsHandleCreated) BeginInvoke(RefreshUnread);
            return;
        }

        RefreshUnread();
    }

    /// <summary>The chips, the count on the tree button and the tree's New dots, from the read marks.</summary>
    private void RefreshUnread()
    {
        var chips = new List<UnreadChip>();
        if (_tree.IsReady)
        {
            foreach (var key in _unread.NewestFirst(TimeOf))
            {
                if (_tree.FindItem(key) is not { } item) continue;
                chips.Add(new UnreadChip(key, HistoryTree.FolderLabel(item.Cwd), item.Timestamp,
                    string.IsNullOrWhiteSpace(item.Preview) ? item.Title : item.Preview));
            }
        }

        _chips.SetChips(chips, _itemKey);
        UpdateUnreadBadge();
        _treePanel.Invalidate(true);
    }

    /// <summary>The count on the tree button: shown when the tree is closed or the chips do not all fit.</summary>
    private void UpdateUnreadBadge()
    {
        int n = _chips.Count;
        _unreadBadge.Text = n > 99 ? "99+" : n.ToString();
        _unreadBadge.Visible = n > 0 && (!_treeOpen || _chips.Overflowing);
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

        foreach (var button in new[] { _snoozeButton, _moreButton })
        {
            button.BackColor = theme.PrimaryDim;
            button.ForeColor = theme.TextPrimary;
            button.FlatAppearance.MouseOverBackColor = theme.Primary;
        }

        _prevButton.BackColor = theme.PrimaryDim;
        _prevButton.ForeColor = theme.TextSecondary;
        _prevButton.FlatAppearance.MouseOverBackColor = theme.Primary;
        _nextButton.BackColor = theme.PrimaryDim;
        _nextButton.ForeColor = theme.TextSecondary;
        _nextButton.FlatAppearance.MouseOverBackColor = theme.Primary;
        foreach (var end in new[] { _firstButton, _lastButton })
        {
            end.BackColor = theme.PrimaryDim;
            end.ForeColor = theme.TextSecondary;
            end.FlatAppearance.MouseOverBackColor = theme.Primary;
        }

        _treeSplitter.BackColor = theme.BgDark;
        _navLabel.ForeColor = theme.TextSecondary;

        _strip.BackColor = theme.BgHeader;
        _rightPane.BackColor = theme.BgDark;
        _loadingLabel.BackColor = theme.BgDark;
        _loadingLabel.ForeColor = theme.TextSecondary;
        _scopeChip.BackColor = theme.PrimaryDim;
        _scopeChip.ForeColor = theme.TextPrimary;
        _treePanel.ApplyTheme(theme);
        _treePanel.Invalidate(true);
        ApplyTreeButtonColors();
        _chips.Theme = theme;
        _chips.Invalidate();
        _unreadBadge.BackColor = NewMark.Back(theme);
        _unreadBadge.ForeColor = NewMark.Fore(theme);

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
            try { _unread.Store.Changed -= OnReadMarksChanged; } catch { }
            try { _typeTimer?.Dispose(); } catch { }
            try { _snoozeTimer?.Dispose(); } catch { }
            try { _moreMenu?.Dispose(); } catch { }
            try { _loadingTimer?.Dispose(); } catch { }
            try { _sparkleTimer?.Dispose(); } catch { }
            try { _webView?.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
