using System.Drawing;

namespace ProdToy;

class PluginCatalogForm : Form
{
    private readonly PopupTheme _theme;
    private readonly ListView _listView;
    private readonly Label _statusLabel;
    private readonly RoundedButton _refreshButton;

    /// <summary>Fired after any install/update/uninstall so the parent can refresh.</summary>
    public event Action? PluginsChanged;

    // Column indices
    private const int ColName = 0;
    private const int ColDescription = 1;
    private const int ColInstalled = 2;
    private const int ColAvailable = 3;
    private const int ColStatus = 4;
    private const int ColAction = 5;

    public PluginCatalogForm(PopupTheme theme)
    {
        _theme = theme;
        Text = "Plugin Store";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(820, 520);
        MinimumSize = new Size(650, 380);
        ShowInTaskbar = true;
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 10f);
        Icon = Themes.CreateAppIcon(theme.Primary);
        AutoScaleMode = AutoScaleMode.Dpi;

        int pad = 20;
        int y = pad;

        var titleLabel = new Label
        {
            Text = "Plugin Store",
            Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        Controls.Add(titleLabel);

        _refreshButton = new RoundedButton
        {
            Text = "Refresh",
            Font = new Font("Segoe UI", 9f),
            Size = new Size(80, 30),
            Location = new Point(ClientSize.Width - pad - 80, y + 4),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextSecondary,
            Cursor = Cursors.Hand,
        };
        _refreshButton.FlatAppearance.BorderSize = 0;
        _refreshButton.FlatAppearance.MouseOverBackColor = theme.Primary;
        _refreshButton.Click += async (_, _) => await LoadCatalog();
        Controls.Add(_refreshButton);

        y += 40;

        var accentLine = new Panel
        {
            BackColor = theme.Primary,
            Location = new Point(pad, y),
            Size = new Size(ClientSize.Width - pad * 2, 2),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(accentLine);
        y += 10;

        _listView = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Font = new Font("Segoe UI", 9.5f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(pad, y),
            Size = new Size(ClientSize.Width - pad * 2, ClientSize.Height - y - 50),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            OwnerDraw = true,
        };
        _listView.Columns.Add("Plugin", 140);
        _listView.Columns.Add("Description", 210);
        _listView.Columns.Add("Installed", 75);
        _listView.Columns.Add("Available", 75);
        _listView.Columns.Add("Status", 100);
        _listView.Columns.Add("Action", 100);

        _listView.DrawColumnHeader += OnDrawColumnHeader;
        _listView.DrawSubItem += OnDrawSubItem;
        _listView.MouseClick += OnListMouseClick;
        _listView.MouseMove += OnListMouseMove;

        Controls.Add(_listView);

        // Bottom status
        int bottomY = ClientSize.Height - 35;

        _statusLabel = new Label
        {
            Text = "Loading catalog...",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(pad, bottomY),
            Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
            BackColor = Color.Transparent,
        };
        Controls.Add(_statusLabel);

        Shown += async (_, _) => await LoadCatalog();
    }

    private async Task LoadCatalog()
    {
        _statusLabel.Text = "Fetching catalog...";
        _statusLabel.ForeColor = _theme.TextSecondary;
        _refreshButton.Enabled = false;

        var catalog = await PluginCatalog.FetchCatalogAsync();
        _listView.Items.Clear();
        var installed = PluginManager.Plugins;

        if (catalog.Count == 0)
        {
            _statusLabel.Text = "No plugins available or catalog unreachable.";
            _refreshButton.Enabled = true;
            return;
        }

        foreach (var entry in catalog)
        {
            var local = installed.FirstOrDefault(p =>
                p.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase));

            string installedVer = local?.Version ?? "";
            string status;
            Color statusColor;
            string action;
            Color actionColor;

            // Check if plugin was just uninstalled but tab is still pending removal
            bool pendingRemoval = Owner is SettingsForm sf && sf.IsPluginPendingRemoval(entry.Id);

            if (pendingRemoval)
            {
                status = "Pending removal";
                statusColor = _theme.TextSecondary;
                action = "Close Settings to complete";
                actionColor = _theme.TextSecondary;
            }
            else if (local == null)
            {
                status = "Not installed";
                statusColor = _theme.TextSecondary;
                action = "Install";
                actionColor = _theme.Primary;
            }
            else if (IsNewerVersion(entry.Version, local.Version))
            {
                status = "Update available";
                statusColor = _theme.Primary;
                action = "Update";
                actionColor = _theme.Primary;
            }
            else
            {
                status = "Installed";
                statusColor = _theme.SuccessColor;
                action = "Uninstall";
                actionColor = _theme.ErrorColor;
            }

            var item = new ListViewItem(entry.Name) { Tag = entry };
            item.SubItems.Add(entry.Description);
            item.SubItems.Add(installedVer);
            item.SubItems.Add(entry.Version);
            item.SubItems.Add(status);
            item.SubItems.Add(action);

            item.UseItemStyleForSubItems = false;
            item.SubItems[ColStatus].ForeColor = statusColor;
            item.SubItems[ColAction].ForeColor = actionColor;

            _listView.Items.Add(item);
        }

        _statusLabel.Text = $"{catalog.Count} plugin(s) in catalog";
        _refreshButton.Enabled = true;
    }

    // --- Owner-draw: render Action column as underlined link ---

    private void OnDrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        e.DrawDefault = true;
    }

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.ColumnIndex == ColAction)
        {
            // Draw action column — underlined link for actions, plain text for status labels
            e.DrawBackground();
            string text = e.SubItem?.Text ?? "";
            Color color = e.SubItem?.ForeColor ?? _theme.TextPrimary;
            bool isAction = text is "Install" or "Update" or "Uninstall";
            var fontStyle = isAction ? FontStyle.Underline : FontStyle.Italic;
            using var font = new Font(e.SubItem?.Font ?? _listView.Font, fontStyle);
            var bounds = e.Bounds;
            bounds.X += 4;
            TextRenderer.DrawText(e.Graphics!, text, font, bounds, color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }
        else
        {
            e.DrawDefault = true;
        }
    }

    private void OnListMouseMove(object? sender, MouseEventArgs e)
    {
        var hit = _listView.HitTest(e.Location);
        if (hit.SubItem != null && hit.Item != null)
        {
            int colIndex = hit.Item.SubItems.IndexOf(hit.SubItem);
            string actionText = hit.SubItem.Text;
            bool isClickable = colIndex == ColAction && actionText is "Install" or "Update" or "Uninstall";
            _listView.Cursor = isClickable ? Cursors.Hand : Cursors.Default;
        }
        else
        {
            _listView.Cursor = Cursors.Default;
        }
    }

    private async void OnListMouseClick(object? sender, MouseEventArgs e)
    {
        var hit = _listView.HitTest(e.Location);
        if (hit.SubItem == null || hit.Item == null) return;

        int colIndex = hit.Item.SubItems.IndexOf(hit.SubItem);
        if (colIndex != ColAction) return;

        var entry = (CatalogEntry)hit.Item.Tag;
        string action = hit.SubItem.Text;

        // Only handle actual actions, not status labels
        if (action is "Install" or "Update" or "Uninstall")
            await PerformAction(entry, action);
    }

    private async Task PerformAction(CatalogEntry entry, string action)
    {
        _refreshButton.Enabled = false;
        _statusLabel.ForeColor = _theme.TextSecondary;

        switch (action)
        {
            case "Install":
            {
                _statusLabel.Text = $"Installing {entry.Name}...";
                var (success, message) = await PluginCatalog.InstallPluginAsync(entry);
                _statusLabel.Text = message;
                _statusLabel.ForeColor = success ? _theme.SuccessColor : _theme.ErrorColor;
                break;
            }
            case "Update":
            {
                _statusLabel.Text = $"Updating {entry.Name}...";
                PluginManager.UninstallPlugin(entry.Id);
                var (success, message) = await PluginCatalog.InstallPluginAsync(entry);
                _statusLabel.Text = success ? $"Updated {entry.Name} to v{entry.Version}" : message;
                _statusLabel.ForeColor = success ? _theme.SuccessColor : _theme.ErrorColor;
                break;
            }
            case "Uninstall":
            {
                var confirm = MessageBox.Show(this,
                    $"Uninstall {entry.Name}?\n\nPlugin data will be deleted.",
                    "Confirm Uninstall",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);

                if (confirm != DialogResult.Yes)
                {
                    _refreshButton.Enabled = true;
                    return;
                }

                var (success, message) = PluginCatalog.UninstallPlugin(entry.Id, entry.Name);
                _statusLabel.Text = message;
                _statusLabel.ForeColor = success ? _theme.SuccessColor : _theme.ErrorColor;
                break;
            }
        }

        // Directly notify the parent SettingsForm to refresh
        if (Owner is SettingsForm settingsForm)
            settingsForm.OnPluginsChanged();

        await LoadCatalog();
    }

    private static bool IsNewerVersion(string catalogVersion, string installedVersion)
    {
        if (Version.TryParse(catalogVersion, out var cv) && Version.TryParse(installedVersion, out var iv))
            return cv > iv;
        return string.Compare(catalogVersion, installedVersion, StringComparison.Ordinal) > 0;
    }
}
