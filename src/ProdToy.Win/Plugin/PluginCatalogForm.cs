using System.Drawing;

namespace ProdToy;

class PluginCatalogForm : Form
{
    private readonly PopupTheme _theme;
    private readonly ListView _listView;
    private readonly Label _statusLabel;
    private readonly RoundedButton _actionButton;
    private readonly RoundedButton _refreshButton;

    public PluginCatalogForm(PopupTheme theme)
    {
        _theme = theme;
        Text = "Plugin Catalog";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(750, 500);
        MinimumSize = new Size(550, 350);
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
            Text = "Plugin Catalog",
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
            Size = new Size(ClientSize.Width - pad * 2, ClientSize.Height - y - 80),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        };
        _listView.Columns.Add("Plugin", 180);
        _listView.Columns.Add("Description", 220);
        _listView.Columns.Add("Installed", 80);
        _listView.Columns.Add("Available", 80);
        _listView.Columns.Add("Status", 100);
        _listView.SelectedIndexChanged += (_, _) => UpdateActionButton();
        Controls.Add(_listView);

        // Bottom bar
        int bottomY = ClientSize.Height - 50;

        _statusLabel = new Label
        {
            Text = "Loading catalog...",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(pad, bottomY + 8),
            Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
            BackColor = Color.Transparent,
        };
        Controls.Add(_statusLabel);

        _actionButton = new RoundedButton
        {
            Text = "Install",
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
            Size = new Size(120, 34),
            Location = new Point(ClientSize.Width - pad - 120, bottomY + 2),
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.Primary,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            Enabled = false,
        };
        _actionButton.FlatAppearance.BorderSize = 0;
        _actionButton.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        _actionButton.Click += async (_, _) => await PerformAction();
        Controls.Add(_actionButton);

        // Load on show
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

            if (local == null)
            {
                status = "Not installed";
                statusColor = _theme.TextSecondary;
            }
            else if (IsNewerVersion(entry.Version, local.Version))
            {
                status = "Update available";
                statusColor = _theme.Primary;
            }
            else
            {
                status = "Up to date";
                statusColor = _theme.SuccessColor;
            }

            var item = new ListViewItem(entry.Name)
            {
                Tag = entry,
            };
            item.SubItems.Add(entry.Description);
            item.SubItems.Add(installedVer);
            item.SubItems.Add(entry.Version);
            item.SubItems.Add(status);
            item.UseItemStyleForSubItems = false;
            item.SubItems[4].ForeColor = statusColor;

            _listView.Items.Add(item);
        }

        _statusLabel.Text = $"{catalog.Count} plugin(s) in catalog";
        _refreshButton.Enabled = true;
    }

    private void UpdateActionButton()
    {
        if (_listView.SelectedItems.Count == 0)
        {
            _actionButton.Enabled = false;
            _actionButton.Text = "Install";
            return;
        }

        var entry = (CatalogEntry)_listView.SelectedItems[0].Tag;
        var local = PluginManager.Plugins.FirstOrDefault(p =>
            p.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase));

        if (local == null)
        {
            _actionButton.Text = "Install";
            _actionButton.BackColor = _theme.Primary;
        }
        else if (IsNewerVersion(entry.Version, local.Version))
        {
            _actionButton.Text = "Update";
            _actionButton.BackColor = _theme.Primary;
        }
        else
        {
            _actionButton.Text = "Reinstall";
            _actionButton.BackColor = _theme.PrimaryDim;
        }

        _actionButton.Enabled = true;
    }

    private async Task PerformAction()
    {
        if (_listView.SelectedItems.Count == 0) return;

        var entry = (CatalogEntry)_listView.SelectedItems[0].Tag;
        var local = PluginManager.Plugins.FirstOrDefault(p =>
            p.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase));

        _actionButton.Enabled = false;
        _statusLabel.ForeColor = _theme.TextSecondary;

        if (local != null && IsNewerVersion(entry.Version, local.Version))
        {
            // Update
            _statusLabel.Text = $"Updating {entry.Name}...";
            var progress = new Progress<int>(p => _statusLabel.Text = $"Updating {entry.Name}... {p}%");
            var (success, message) = await PluginCatalog.UpdatePluginAsync(entry.Id, progress);
            _statusLabel.Text = message;
            _statusLabel.ForeColor = success ? _theme.SuccessColor : _theme.ErrorColor;
        }
        else
        {
            // Install or reinstall
            _statusLabel.Text = $"Installing {entry.Name}...";
            var progress = new Progress<int>(p => _statusLabel.Text = $"Installing {entry.Name}... {p}%");
            var (success, message) = await PluginCatalog.InstallFromCatalogAsync(entry, progress);
            _statusLabel.Text = message;
            _statusLabel.ForeColor = success ? _theme.SuccessColor : _theme.ErrorColor;
        }

        // Refresh the list
        await LoadCatalog();
        _actionButton.Enabled = true;
    }

    private static bool IsNewerVersion(string catalogVersion, string installedVersion)
    {
        if (Version.TryParse(catalogVersion, out var cv) && Version.TryParse(installedVersion, out var iv))
            return cv > iv;
        return string.Compare(catalogVersion, installedVersion, StringComparison.Ordinal) > 0;
    }
}
