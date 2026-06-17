using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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
    private readonly Dictionary<string, ConsolidatedBrowserPane> _panesByKey = new(StringComparer.OrdinalIgnoreCase);

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

        // Top strip with a "+ New tab" button so the user can open a blank tab and
        // browse any URL — not just shortcut Status/Home URLs.
        var topBar = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = theme.BgHeader };
        var newTabBtn = new Button
        {
            Text = "+ New tab",
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.BgDark,
            ForeColor = theme.TextPrimary,
            Font = new Font("Segoe UI", 8.5F),
            Size = new Size(86, 22),
            Location = new Point(6, 4),
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        newTabBtn.FlatAppearance.BorderColor = theme.Border;
        newTabBtn.Click += (_, _) => OpenAdHocTab();
        topBar.Controls.Add(newTabBtn);
        var newTabTip = new ToolTip();
        newTabTip.SetToolTip(newTabBtn, "Open a blank preview tab — type a URL in the address bar to browse.");

        // Fill controls first (low z), then the docked top bar so it reserves the top.
        Controls.Add(_tabs);
        Controls.Add(_emptyHint);
        Controls.Add(topBar);
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
        var pane = new ConsolidatedBrowserPane(_theme) { Dock = DockStyle.Fill };
        page.Controls.Add(pane);
        _tabs.TabPages.Add(page);
        _tabsByKey[key] = page;
        _keyByTab[page] = key;
        _panesByKey[key] = pane;

        if (!_tabs.Visible)
        {
            _tabs.Visible = true;
            _emptyHint.Visible = false;
        }

        _tabs.SelectedTab = page;
        // Empty URL → open the pane on a blank page so the user can type a URL
        // in the address bar (used when a shortcut has no Status URL configured).
        _ = pane.NavigateAsync(string.IsNullOrWhiteSpace(url) ? "about:blank" : url);
    }

    /// <summary>Close a shortcut's browser tab.</summary>
    public void CloseTab(string key)
    {
        if (!_tabsByKey.TryGetValue(key, out var page)) return;

        if (_panesByKey.TryGetValue(key, out var pane))
        {
            try { pane.Dispose(); } catch { }
            _panesByKey.Remove(key);
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
                if (_keyByTab.TryGetValue(page, out var key)) CloseTab(key);
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

    private readonly WebView2 _webView;
    private readonly Button _backBtn;
    private readonly Button _forwardBtn;
    private readonly Button _reloadBtn;
    private readonly TextBox _urlBox;
    private readonly LinkLabel _openExternalLink;
    private readonly Button _devToolsBtn;
    private readonly Label _statusLabel;

    private string? _currentUrl;
    private bool _ready;

    public ConsolidatedBrowserPane(PluginTheme theme)
    {
        BackColor = theme.BgDark;

        const int toolbarHeight = 32;

        _webView = new WebView2 { Dock = DockStyle.Fill };

        var toolbar = new Panel { Dock = DockStyle.Top, Height = toolbarHeight, BackColor = theme.BgHeader };

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

        toolbar.Resize += (_, _) =>
        {
            _openExternalLink.Location = new Point(toolbar.Width - _openExternalLink.PreferredWidth - 8, 8);
            _devToolsBtn.Location = new Point(_openExternalLink.Left - _devToolsBtn.Width - 10, 5);
            var urlRight = _devToolsBtn.Left - 8;
            _urlBox.Width = Math.Max(50, urlRight - _urlBox.Left);
        };

        toolbar.Controls.Add(_backBtn);
        toolbar.Controls.Add(_forwardBtn);
        toolbar.Controls.Add(_reloadBtn);
        toolbar.Controls.Add(_urlBox);
        toolbar.Controls.Add(_devToolsBtn);
        toolbar.Controls.Add(_openExternalLink);

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

        // Stack order: Fill child added first so the Top toolbar sits above.
        Controls.Add(_webView);
        Controls.Add(_statusLabel);
        Controls.Add(toolbar);

        UpdateNavButtonsEnabled(false, false);
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
            try { _webView.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
