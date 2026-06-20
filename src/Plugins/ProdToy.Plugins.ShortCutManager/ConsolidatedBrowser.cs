using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Right-column pane of the Consolidated Launcher: a TabControl holding one
/// <see cref="ConsolidatedBrowserPane"/> per shortcut the user opens (via the
/// "open ↗" link on a row, or auto-opened when a shortcut's StatusUrl goes
/// live). Tabs are created lazily and close with an × on the header.
/// </summary>
/// <remarks>Ported from NordPilot.DeveloperTools' BrowserTabsControl, re-themed
/// and decoupled from the Playwright session store (ProdToy's auto-login runs
/// its own visible Edge instance).</remarks>
sealed class ConsolidatedBrowserTabs : UserControl
{
    private readonly PluginTheme _theme;
    private readonly OwnerDrawTabControl _tabs;
    private readonly Label _emptyHint;
    private readonly Dictionary<string, TabPage> _tabsByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TabPage, string> _keyByTab = new();
    private readonly Dictionary<string, SplitBrowserView> _viewsByKey = new(StringComparer.OrdinalIgnoreCase);

    public ConsolidatedBrowserTabs(PluginTheme theme)
    {
        _theme = theme;
        BackColor = theme.BgDark;

        _tabs = new OwnerDrawTabControl(theme)
        {
            Dock = DockStyle.Fill,
            Appearance = TabAppearance.Normal,
            ItemSize = new Size(150, 24),
            SizeMode = TabSizeMode.Fixed,
            Visible = false,    // hidden until the first browser tab is opened
        };
        _tabs.PaintTab = DrawTabHeader;
        _tabs.MouseDown += OnTabsMouseDown;

        _emptyHint = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Preview pane\n\nClick \"+ New tab\" above to type any URL,\nor \"open ↗\" on a shortcut to load it here.",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = theme.TextSecondary,
            Font = new Font("Segoe UI", 11F),
            BackColor = theme.BgDark,
        };

        // Top strip: "+ New tab" (open a blank tab to browse any URL) plus Split /
        // Unsplit, which divide the current tab into up to 5 side-by-side panes.
        var topBar = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = theme.BgHeader };
        var tip = new ToolTip();
        int bx = 6;
        Button MakeBarButton(string text, int width, string tooltip, Action onClick)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = theme.BgDark,
                ForeColor = theme.TextPrimary,
                Font = new Font("Segoe UI", 8.5F),
                Size = new Size(width, 22),
                Location = new Point(bx, 4),
                Cursor = Cursors.Hand,
                TabStop = false,
            };
            b.FlatAppearance.BorderColor = theme.Border;
            b.Click += (_, _) => onClick();
            tip.SetToolTip(b, tooltip);
            topBar.Controls.Add(b);
            bx += width + 6;
            return b;
        }

        MakeBarButton("+ New tab", 86, "Open a blank preview tab — type a URL in the address bar to browse.", OpenAdHocTab);
        MakeBarButton("⊞ Split", 70, "Split the current tab into another pane (up to 5), each with its own address bar. Close a split with the ✕ on its toolbar.", SplitCurrent);

        // Fill controls first (low z), then the docked top bar so it reserves the top.
        Controls.Add(_tabs);
        Controls.Add(_emptyHint);
        Controls.Add(topBar);
    }

    private SplitBrowserView? CurrentView() =>
        _tabs.SelectedTab != null
        && _keyByTab.TryGetValue(_tabs.SelectedTab, out var key)
        && _viewsByKey.TryGetValue(key, out var v) ? v : null;

    /// <summary>Split the current tab into one more pane (opening a new tab first if
    /// none is open). Caps at <see cref="SplitBrowserView.MaxPanes"/>.</summary>
    private void SplitCurrent()
    {
        var view = CurrentView();
        if (view == null) { OpenAdHocTab(); view = CurrentView(); }
        view?.AddPane("about:blank");
    }

    private int _adhocCounter;

    /// <summary>Open a fresh blank preview tab the user can type any URL into.</summary>
    public void OpenAdHocTab()
    {
        int n = ++_adhocCounter;
        OpenOrFocus($"adhoc:{n}", $"New tab {n}", "");
    }

    /// <summary>Whether a tab for the given key already exists.</summary>
    public bool HasTab(string key) => _tabsByKey.ContainsKey(key);

    /// <summary>
    /// Open a tab for the given shortcut — creates the pane on first call and
    /// navigates to <paramref name="url"/>, just focuses the tab on later calls.
    /// </summary>
    public void OpenOrFocus(string key, string displayName, string url)
    {
        if (_tabsByKey.TryGetValue(key, out var existing))
        {
            _tabs.SelectedTab = existing;
            return;
        }

        var page = new TabPage(displayName) { BackColor = _theme.BgDark };
        // A SplitBrowserView starts as one pane (navigated to url) and can split
        // into up to 5 side-by-side panes via the toolbar.
        var view = new SplitBrowserView(_theme, url) { Dock = DockStyle.Fill };
        page.Controls.Add(view);
        _tabs.TabPages.Add(page);
        _tabsByKey[key] = page;
        _keyByTab[page] = key;
        _viewsByKey[key] = view;

        if (!_tabs.Visible)
        {
            _tabs.Visible = true;
            _emptyHint.Visible = false;
        }

        _tabs.SelectedTab = page;
    }

    /// <summary>Close a shortcut's browser tab.</summary>
    public void CloseTab(string key)
    {
        if (!_tabsByKey.TryGetValue(key, out var page)) return;

        if (_viewsByKey.TryGetValue(key, out var view))
        {
            try { view.Dispose(); } catch { }
            _viewsByKey.Remove(key);
        }
        _tabs.TabPages.Remove(page);
        _tabsByKey.Remove(key);
        _keyByTab.Remove(page);
        page.Dispose();

        if (_tabs.TabPages.Count == 0)
        {
            _tabs.Visible = false;
            _emptyHint.Visible = true;
        }
    }

    // ---- owner draw -----------------------------------------------------------------

    private void DrawTabHeader(Graphics g, int index, Rectangle bounds, bool selected)
    {
        if (index < 0 || index >= _tabs.TabPages.Count) return;
        var page = _tabs.TabPages[index];

        var bg = selected ? _theme.BgHeader : _theme.BgDark;
        using (var brush = new SolidBrush(bg))
            g.FillRectangle(brush, bounds);

        var textColor = selected ? _theme.TextPrimary : _theme.TextSecondary;
        using var font = new Font(selected ? "Segoe UI Semibold" : "Segoe UI", 9F);
        var textRect = new Rectangle(bounds.X + 10, bounds.Y, bounds.Width - 28, bounds.Height);
        TextRenderer.DrawText(
            g, page.Text, font, textRect, textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        // Small × glyph at the right edge.
        var closeRect = GetCloseRect(bounds);
        using (var pen = new Pen(textColor, 1.5f))
        {
            g.DrawLine(pen, closeRect.Left + 3, closeRect.Top + 3, closeRect.Right - 3, closeRect.Bottom - 3);
            g.DrawLine(pen, closeRect.Right - 3, closeRect.Top + 3, closeRect.Left + 3, closeRect.Bottom - 3);
        }

        if (selected)
        {
            using var pen = new Pen(_theme.Primary, 2);
            g.DrawLine(pen, bounds.X, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
        }
    }

    private static Rectangle GetCloseRect(Rectangle tabBounds)
    {
        const int size = 14;
        return new Rectangle(tabBounds.Right - size - 6, tabBounds.Y + (tabBounds.Height - size) / 2, size, size);
    }

    private void OnTabsMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        for (int i = 0; i < _tabs.TabPages.Count; i++)
        {
            var bounds = _tabs.GetTabRect(i);
            if (!bounds.Contains(e.Location)) continue;
            if (GetCloseRect(bounds).Contains(e.Location))
            {
                var page = _tabs.TabPages[i];
                if (_keyByTab.TryGetValue(page, out var key))
                {
                    var res = MessageBox.Show(FindForm() ?? (IWin32Window)this,
                        $"Close the preview tab “{page.Text}”?",
                        "Close tab", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button2);
                    if (res == DialogResult.Yes) CloseTab(key);
                }
                return;
            }
        }
    }
}

/// <summary>
/// One in-form browser tab: a <see cref="WebView2"/> with a slim toolbar
/// (Back / Forward / Reload / editable URL box / "Open externally").
/// </summary>
/// <remarks>
/// Ported from NordPilot.DeveloperTools' BrowserPane. The shared
/// CoreWebView2Environment is created lazily once per process under a
/// ProdToy-local user-data folder, with <c>--ignore-certificate-errors</c> so
/// localhost dev certs (https://localhost:5001 etc.) don't error out.
/// </remarks>
sealed class ConsolidatedBrowserPane : UserControl
{
    private static CoreWebView2Environment? s_sharedEnvironment;
    private static readonly SemaphoreSlim s_envInitLock = new(1, 1);

    private readonly Panel _toolbar;
    private readonly WebView2 _webView;
    private readonly Button _backBtn;
    private readonly Button _forwardBtn;
    private readonly Button _reloadBtn;
    private readonly TextBox _urlBox;
    private readonly LinkLabel _openExternalLink;
    private readonly Button _devToolsBtn;
    private readonly Button _closeBtn;
    private readonly Label _statusLabel;
    private readonly Panel _netBar;
    private readonly Label _netLabel;
    private readonly Panel _progressTrack;
    private readonly Panel _progressFill;
    private readonly Button _detailsBtn;
    private readonly PluginTheme _theme;

    private string? _currentUrl;
    private bool _ready;

    // Live network telemetry for the current navigation (via DevTools Protocol).
    private bool _cdpEnabled;
    private bool _loading;
    private int _reqStarted, _finished, _failed, _cachedCount, _mainStatus;
    private long _downloadedBytes;
    private double _navStartTs;
    private readonly HashSet<string> _cachedIds = new();
    private readonly Stopwatch _navWatch = new();
    // Per-request log for the current navigation (drives the Details window).
    private readonly List<NetworkResource> _resources = new();
    private readonly Dictionary<string, NetworkResource> _resById = new();
    private NetworkDetailsForm? _detailsForm;

    /// <summary>Raised when the pane's ✕ (close split) button is clicked.</summary>
    public event Action? CloseRequested;

    public ConsolidatedBrowserPane(PluginTheme theme)
    {
        _theme = theme;
        BackColor = theme.BgDark;

        const int toolbarHeight = 32;

        _webView = new WebView2 { Dock = DockStyle.Fill };

        _toolbar = new Panel { Dock = DockStyle.Top, Height = toolbarHeight, BackColor = theme.BgHeader };

        _backBtn = MakeNavButton("←", "Back", theme);
        _backBtn.Location = new Point(4, 4);
        _backBtn.Click += (_, _) => { if (_ready && _webView.CoreWebView2.CanGoBack) _webView.CoreWebView2.GoBack(); };

        _forwardBtn = MakeNavButton("→", "Forward", theme);
        _forwardBtn.Location = new Point(34, 4);
        _forwardBtn.Click += (_, _) => { if (_ready && _webView.CoreWebView2.CanGoForward) _webView.CoreWebView2.GoForward(); };

        _reloadBtn = MakeNavButton("⟳", "Reload", theme);
        _reloadBtn.Location = new Point(64, 4);
        _reloadBtn.Click += (_, _) => { if (_ready) _webView.CoreWebView2.Reload(); };

        _urlBox = new TextBox
        {
            Location = new Point(96, 6),
            Height = 22,
            Font = new Font("Segoe UI", 9F),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = theme.BgDark,
            ForeColor = theme.TextPrimary,
        };
        _urlBox.KeyDown += (_, ev) =>
        {
            if (ev.KeyCode != Keys.Enter) return;
            ev.Handled = true;
            ev.SuppressKeyPress = true;
            var typed = _urlBox.Text?.Trim();
            if (!string.IsNullOrEmpty(typed)) _ = NavigateAsync(typed);
        };

        _devToolsBtn = new Button
        {
            Text = "DevTools",
            Size = new Size(72, 22),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.BgDark,
            ForeColor = theme.TextPrimary,
            Font = new Font("Segoe UI", 8.5F),
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        _devToolsBtn.FlatAppearance.BorderColor = theme.Border;
        _devToolsBtn.Click += (_, _) => OpenDevTools();
        var devTip = new ToolTip();
        devTip.SetToolTip(_devToolsBtn, "Open browser DevTools for this page (separate window)");

        _openExternalLink = new LinkLabel
        {
            Text = "Open externally",
            AutoSize = true,
            LinkColor = theme.Primary,
            ActiveLinkColor = theme.PrimaryLight,
            LinkBehavior = LinkBehavior.HoverUnderline,
            Font = new Font("Segoe UI", 9F),
            BackColor = theme.BgHeader,
        };
        _openExternalLink.LinkClicked += (_, _) =>
        {
            var url = _currentUrl ?? _urlBox.Text;
            if (string.IsNullOrEmpty(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        };

        // ✕ Close-split button (hidden until this pane is one of several splits).
        _closeBtn = new Button
        {
            Text = "✕",
            Size = new Size(24, 22),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.BgHeader,
            ForeColor = theme.TextSecondary,
            Font = new Font("Segoe UI", 8.5F),
            Cursor = Cursors.Hand,
            TabStop = false,
            Visible = false,
        };
        _closeBtn.FlatAppearance.BorderSize = 0;
        _closeBtn.FlatAppearance.MouseOverBackColor = theme.ErrorBg;
        _closeBtn.Click += (_, _) =>
        {
            var res = MessageBox.Show(FindForm() ?? (IWin32Window)this,
                "Close this split pane?",
                "Close split", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (res == DialogResult.Yes) CloseRequested?.Invoke();
        };
        var closeTip = new ToolTip();
        closeTip.SetToolTip(_closeBtn, "Close this split");

        _toolbar.Resize += (_, _) => LayoutToolbar();

        _toolbar.Controls.Add(_backBtn);
        _toolbar.Controls.Add(_forwardBtn);
        _toolbar.Controls.Add(_reloadBtn);
        _toolbar.Controls.Add(_urlBox);
        _toolbar.Controls.Add(_devToolsBtn);
        _toolbar.Controls.Add(_openExternalLink);
        _toolbar.Controls.Add(_closeBtn);

        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Loading browser…",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = theme.TextSecondary,
            BackColor = theme.BgDark,
            Font = new Font("Segoe UI", 10F),
            Visible = true,
        };

        // Status bar (just under the address bar): live loading/network telemetry
        // — files, bytes, cache — plus a progress bar and a "Details" button.
        _netBar = new Panel { Dock = DockStyle.Top, Height = 24, BackColor = theme.BgHeader };
        _netLabel = new Label
        {
            Text = "",
            ForeColor = theme.TextSecondary,
            BackColor = theme.BgHeader,
            Font = new Font("Segoe UI", 8F),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };
        _progressTrack = new Panel { BackColor = theme.BgDark, Visible = false };
        _progressFill = new Panel { BackColor = theme.Primary, Location = new Point(0, 0) };
        _progressTrack.Controls.Add(_progressFill);
        _detailsBtn = new Button
        {
            Text = "Details ▸",
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.BgDark,
            ForeColor = theme.TextPrimary,
            Font = new Font("Segoe UI", 7.5F),
            Size = new Size(72, 18),
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        _detailsBtn.FlatAppearance.BorderColor = theme.Border;
        _detailsBtn.Click += (_, _) => OpenNetworkDetails();
        var detailsTip = new ToolTip();
        detailsTip.SetToolTip(_detailsBtn, "Show every request that loaded — status, type, size, time (updates live).");
        _netBar.Controls.Add(_netLabel);
        _netBar.Controls.Add(_progressTrack);
        _netBar.Controls.Add(_detailsBtn);
        _netBar.Resize += (_, _) => LayoutNetBar();

        // Stack order: Fill children first (lowest z), then the status bar (Top),
        // then the toolbar (Top) last so it docks at the very top, status bar below it.
        Controls.Add(_webView);
        Controls.Add(_statusLabel);
        Controls.Add(_netBar);
        Controls.Add(_toolbar);

        UpdateNavButtonsEnabled(false, false);
    }

    private void LayoutNetBar()
    {
        int h = _netBar.ClientSize.Height;
        _detailsBtn.Location = new Point(_netBar.ClientSize.Width - _detailsBtn.Width - 6, (h - _detailsBtn.Height) / 2);
        const int trackW = 110, trackH = 6;
        _progressTrack.Size = new Size(trackW, trackH);
        _progressTrack.Location = new Point(_detailsBtn.Left - trackW - 8, (h - trackH) / 2);
        _netLabel.Location = new Point(8, 0);
        _netLabel.Size = new Size(Math.Max(20, _progressTrack.Left - 14), h);
        UpdateProgressFill();
    }

    private void UpdateProgressFill()
    {
        _progressTrack.Visible = _loading;
        if (!_loading) return;
        double pct = _reqStarted > 0 ? Math.Min(1.0, _finished / (double)_reqStarted) : 0.05;
        int w = (int)(_progressTrack.ClientSize.Width * pct);
        _progressFill.Bounds = new Rectangle(0, 0, w, _progressTrack.ClientSize.Height);
    }

    /// <summary>Position the right-aligned toolbar controls, leaving room for the
    /// ✕ close button when it's shown.</summary>
    private void LayoutToolbar()
    {
        int right = _toolbar.Width - 6;
        if (_closeBtn.Visible)
        {
            _closeBtn.Location = new Point(right - _closeBtn.Width, 5);
            right = _closeBtn.Left - 6;
        }
        _openExternalLink.Location = new Point(right - _openExternalLink.PreferredWidth - 2, 8);
        _devToolsBtn.Location = new Point(_openExternalLink.Left - _devToolsBtn.Width - 10, 5);
        int urlRight = _devToolsBtn.Left - 8;
        _urlBox.Width = Math.Max(50, urlRight - _urlBox.Left);
    }

    /// <summary>Show/hide the ✕ close-split button (only meaningful when this pane
    /// is one of several splits).</summary>
    public void SetCloseButtonVisible(bool visible)
    {
        if (_closeBtn.Visible == visible) return;
        _closeBtn.Visible = visible;
        LayoutToolbar();
    }

    public string? CurrentUrl => _currentUrl;

    /// <summary>
    /// Navigate the page to a URL. Initialises the WebView2 on first call
    /// (one-time ~300ms cost, shown via the "Loading browser…" overlay).
    /// </summary>
    public async Task NavigateAsync(string url)
    {
        try
        {
            if (IsDisposed || Disposing) return;
            url = NormalizeUrl(url);

            if (!_ready)
            {
                await EnsureCoreWebView2Async();
                if (IsDisposed || _webView.IsDisposed || _webView.CoreWebView2 is null) return;

                _ready = true;
                _statusLabel.Visible = false;
                _webView.CoreWebView2.HistoryChanged += (_, _) =>
                    UpdateNavButtonsEnabled(_webView.CoreWebView2.CanGoBack, _webView.CoreWebView2.CanGoForward);
                _webView.CoreWebView2.SourceChanged += (_, _) =>
                {
                    _currentUrl = _webView.CoreWebView2.Source;
                    if (!_urlBox.Focused) _urlBox.Text = _currentUrl ?? string.Empty;
                };
                _webView.CoreWebView2.NavigationStarting += (_, e) =>
                {
                    if (e.IsRedirected) return;   // same navigation continuing — keep counters
                    ResetNetStats();
                    _loading = true;
                    _navWatch.Restart();
                    UpdateNetLabel();
                };
                _webView.CoreWebView2.NavigationCompleted += (_, e) =>
                {
                    _loading = false;
                    _navWatch.Stop();
                    try { if (_mainStatus == 0 && e.HttpStatusCode > 0) _mainStatus = e.HttpStatusCode; } catch { }
                    UpdateNetLabel();
                };
                await EnableNetworkTelemetryAsync();
            }

            if (IsDisposed || _webView.IsDisposed || _webView.CoreWebView2 is null) return;

            _urlBox.Text = url;
            _currentUrl = url;
            _webView.CoreWebView2.Navigate(url);
        }
        catch (ObjectDisposedException) { /* tab closed mid-navigation — fine */ }
        catch (InvalidOperationException) { /* WebView2 not ready / handle gone — fine */ }
        catch (Exception ex)
        {
            _statusLabel.Text = "Browser init failed: " + ex.Message;
            _statusLabel.Visible = true;
        }
    }

    /// <summary>Make a typed address navigable: pass through about:/already-scheme'd
    /// URLs, default localhost/loopback to http and everything else to https so the
    /// user can type "localhost:5000" or "example.com" without a scheme.</summary>
    private static string NormalizeUrl(string url)
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0) return "about:blank";
        if (u.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return u;
        if (u.Contains("://")) return u;   // already has a scheme
        bool local = u.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
                  || u.StartsWith("127.0.0.1")
                  || u.StartsWith("0.0.0.0")
                  || u.StartsWith("[::1]");
        return (local ? "http://" : "https://") + u;
    }

    // ─────────────────────── network telemetry (CDP) ───────────────────────

    /// <summary>Subscribe to DevTools-Protocol Network events and enable the Network
    /// domain so the status bar can show live request count, bytes downloaded, and
    /// how many resources were served from cache.</summary>
    private async Task EnableNetworkTelemetryAsync()
    {
        if (_cdpEnabled || _webView.CoreWebView2 is null) return;
        try
        {
            WireCdp("Network.requestWillBeSent", OnRequestWillBeSent);
            WireCdp("Network.responseReceived", OnResponseReceived);
            WireCdp("Network.loadingFinished", OnLoadingFinished);
            WireCdp("Network.loadingFailed", OnLoadingFailed);
            WireCdp("Network.requestServedFromCache", MarkCached);
            await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            _cdpEnabled = true;
        }
        catch (Exception ex) { PluginLog.Warn($"Browser network telemetry init failed: {ex.Message}"); }
    }

    private void WireCdp(string eventName, Action<JsonElement> handler)
    {
        var receiver = _webView.CoreWebView2!.GetDevToolsProtocolEventReceiver(eventName);
        receiver.DevToolsProtocolEventReceived += (_, e) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
                handler(doc.RootElement);
            }
            catch { /* malformed/partial event — ignore */ }
        };
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Ts(JsonElement p) =>
        p.TryGetProperty("timestamp", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private NetworkResource? Res(JsonElement p) =>
        Str(p, "requestId") is string id && _resById.TryGetValue(id, out var r) ? r : null;

    private void OnRequestWillBeSent(JsonElement p)
    {
        _reqStarted++;
        if (Str(p, "requestId") is not string id) return;
        if (_resById.TryGetValue(id, out var existing))
        {
            if (p.TryGetProperty("request", out var rq0)) existing.Url = Str(rq0, "url") ?? existing.Url;
            return;   // redirect re-uses the id — keep one row
        }
        double ts = Ts(p);
        if (_resources.Count == 0) _navStartTs = ts;
        var res = new NetworkResource { Id = id, StartTs = ts, State = NetState.Pending, Type = Str(p, "type") ?? "" };
        if (p.TryGetProperty("request", out var rq))
        {
            res.Url = Str(rq, "url") ?? "";
            res.Method = Str(rq, "method") ?? "";
        }
        _resById[id] = res;
        _resources.Add(res);
    }

    private void OnResponseReceived(JsonElement p)
    {
        if (!p.TryGetProperty("response", out var r)) return;
        bool cache = r.TryGetProperty("fromDiskCache", out var fc) && fc.ValueKind == JsonValueKind.True;
        if (cache) MarkCached(p);
        int status = r.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 0;
        if (_mainStatus == 0 && p.TryGetProperty("type", out var t0) && t0.GetString() == "Document" && status > 0)
            _mainStatus = status;
        if (Res(p) is NetworkResource res)
        {
            res.Status = status;
            res.Mime = Str(r, "mimeType") ?? res.Mime;
            res.Type = Str(p, "type") ?? res.Type;
            if (cache) res.FromCache = true;
        }
    }

    private void OnLoadingFinished(JsonElement p)
    {
        _finished++;
        long len = 0;
        if (p.TryGetProperty("encodedDataLength", out var el) && el.ValueKind == JsonValueKind.Number)
            len = (long)el.GetDouble();
        _downloadedBytes += len;
        if (Res(p) is NetworkResource res) { res.Bytes = len; res.EndTs = Ts(p); res.State = NetState.Done; }
        UpdateNetLabel();
    }

    private void OnLoadingFailed(JsonElement p)
    {
        _failed++;
        if (Res(p) is NetworkResource res) { res.EndTs = Ts(p); res.State = NetState.Failed; }
        UpdateNetLabel();
    }

    private void MarkCached(JsonElement p)
    {
        if (Str(p, "requestId") is string rid && _cachedIds.Add(rid))
        {
            _cachedCount++;
            if (_resById.TryGetValue(rid, out var res)) res.FromCache = true;
            UpdateNetLabel();
        }
    }

    private void ResetNetStats()
    {
        _reqStarted = _finished = _failed = _cachedCount = _mainStatus = 0;
        _downloadedBytes = 0;
        _navStartTs = 0;
        _cachedIds.Clear();
        _resources.Clear();
        _resById.Clear();
    }

    private void UpdateNetLabel()
    {
        if (_netLabel.IsDisposed) return;
        _netLabel.Text = SummaryText();
        UpdateProgressFill();
    }

    /// <summary>One-line summary of the current navigation's network activity.</summary>
    public string SummaryText()
    {
        string state = _loading ? "⟳ Loading…" : (_failed > 0 ? "⚠ Done" : "✓ Done");
        string status = _mainStatus > 0 ? $"  ·  HTTP {_mainStatus}" : "";
        string files = $"  ·  {_reqStarted} file{(_reqStarted == 1 ? "" : "s")}";
        string size = $"  ·  {FormatBytes(_downloadedBytes)} downloaded";
        string cache = _cachedCount > 0 ? $"  ·  {_cachedCount} from cache" : "";
        string fail = _failed > 0 ? $"  ·  {_failed} failed" : "";
        string time = (!_loading && _navWatch.ElapsedMilliseconds > 0) ? $"  ·  {_navWatch.Elapsed.TotalSeconds:0.0}s" : "";
        return $"{state}{status}{files}{size}{cache}{fail}{time}";
    }

    // ── public surface for the Details window (read on the UI thread) ──
    public IReadOnlyList<NetworkResource> NetworkSnapshot() => _resources.ToArray();
    public double NavStartTs => _navStartTs;
    public bool IsLoading => _loading;
    public TimeSpan NavElapsed => _navWatch.Elapsed;

    private void OpenNetworkDetails()
    {
        if (_detailsForm != null && !_detailsForm.IsDisposed)
        {
            if (_detailsForm.WindowState == FormWindowState.Minimized) _detailsForm.WindowState = FormWindowState.Normal;
            _detailsForm.BringToFront();
            _detailsForm.Activate();
            return;
        }
        _detailsForm = new NetworkDetailsForm(_theme, this);
        _detailsForm.Show();
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return kb.ToString("0.0") + " KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return mb.ToString("0.00") + " MB";
        return (mb / 1024.0).ToString("0.00") + " GB";
    }

    // ─────────────────────────── DevTools ───────────────────────────

    /// <summary>
    /// Open the browser DevTools for this page. WebView2 only exposes DevTools
    /// as a <b>separate window</b> — it can't be docked into the host (the
    /// remote-debugging "embed the frontend in a second WebView2" route doesn't
    /// work on Edge, which doesn't serve the DevTools frontend over HTTP), so
    /// this opens the standard DevTools window.
    /// </summary>
    private void OpenDevTools()
    {
        if (!_ready || _webView.CoreWebView2 is null) return;
        try { _webView.CoreWebView2.OpenDevToolsWindow(); } catch { }
    }

    private async Task EnsureCoreWebView2Async()
    {
        await s_envInitLock.WaitAsync();
        try
        {
            if (s_sharedEnvironment is null)
            {
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ProdToy", "ShortCutManager", "WebView2");
                Directory.CreateDirectory(userDataFolder);

                var options = new CoreWebView2EnvironmentOptions
                {
                    // Trust localhost dev certs without prompting.
                    AdditionalBrowserArguments = "--ignore-certificate-errors",
                };

                s_sharedEnvironment = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userDataFolder,
                    options: options);
            }
        }
        finally
        {
            s_envInitLock.Release();
        }

        await _webView.EnsureCoreWebView2Async(s_sharedEnvironment);
    }

    private void UpdateNavButtonsEnabled(bool canBack, bool canForward)
    {
        _backBtn.Enabled = canBack;
        _forwardBtn.Enabled = canForward;
    }

    private static Button MakeNavButton(string glyph, string tooltip, PluginTheme theme)
    {
        var b = new Button
        {
            Text = glyph,
            Size = new Size(26, 22),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.BgDark,
            ForeColor = theme.TextPrimary,
            Font = new Font("Segoe UI", 9F),
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        b.FlatAppearance.BorderColor = theme.Border;
        var tip = new ToolTip();
        tip.SetToolTip(b, tooltip);
        return b;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { if (_detailsForm is { IsDisposed: false }) _detailsForm.Close(); } catch { }
            try { _webView.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
