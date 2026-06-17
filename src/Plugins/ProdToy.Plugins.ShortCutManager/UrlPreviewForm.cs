using System.Drawing;
using System.Windows.Forms;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Standalone in-app preview window for an "Open URL" shortcut — hosts the same
/// <see cref="ConsolidatedBrowserPane"/> (WebView2 + address bar) the Consolidated
/// Launcher uses. One window per shortcut id (reused/focused on relaunch).
/// </summary>
sealed class UrlPreviewForm : Form
{
    private static readonly Dictionary<string, UrlPreviewForm> s_open =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Open (or focus + re-navigate, if already open) the preview for a shortcut.</summary>
    public static void OpenOrFocus(PluginTheme theme, string key, string title, string url)
    {
        if (s_open.TryGetValue(key, out var existing) && !existing.IsDisposed)
        {
            if (existing.WindowState == FormWindowState.Minimized)
                existing.WindowState = FormWindowState.Normal;
            existing.BringToFront();
            existing.Activate();
            _ = existing._pane.NavigateAsync(string.IsNullOrWhiteSpace(url) ? "about:blank" : url);
            return;
        }

        var form = new UrlPreviewForm(theme, title, url);
        s_open[key] = form;
        form.FormClosed += (_, _) =>
        {
            if (s_open.TryGetValue(key, out var cur) && ReferenceEquals(cur, form))
                s_open.Remove(key);
        };
        form.Show();
    }

    private readonly ConsolidatedBrowserPane _pane;
    private readonly string _url;

    private UrlPreviewForm(PluginTheme theme, string title, string url)
    {
        _url = string.IsNullOrWhiteSpace(url) ? "about:blank" : url;

        Text = string.IsNullOrWhiteSpace(title) ? "Preview" : $"{title} — Preview";
        try { Icon = IconHelper.CreateAppIcon(theme.Primary); } catch { /* keep default */ }
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1100, 800);
        MinimumSize = new Size(520, 360);
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        ShowInTaskbar = true;

        _pane = new ConsolidatedBrowserPane(theme) { Dock = DockStyle.Fill };
        Controls.Add(_pane);

        // Navigate once the handle exists so the WebView2 initializes cleanly.
        Shown += async (_, _) => { try { await _pane.NavigateAsync(_url); } catch { /* tab disposed */ } };
    }
}
