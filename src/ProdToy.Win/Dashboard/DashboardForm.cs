using System.Drawing;
using System.Drawing.Drawing2D;
using ProdToy.Sdk;

namespace ProdToy;

class DashboardForm : Form
{
    private PopupTheme _theme;
    private readonly Panel _contentPanel;
    private const int Pad = 24;
    private const int TileWidth = 220;
    private const int TileHeight = 64;
    private const int TileGap = 8;
    private const int TilesPerRow = 2;
    private const int FormWidth = Pad * 2 + TileWidth * TilesPerRow + TileGap;

    public event Action? ShowSettingsRequested;

    public DashboardForm(PopupTheme theme)
    {
        _theme = theme;

        Text = "ProdToy";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 10f);
        Icon = Themes.CreateAppIcon(theme.Primary);
        AutoScaleMode = AutoScaleMode.Dpi;

        _contentPanel = new Panel
        {
            Location = new Point(0, 0),
            BackColor = Color.Transparent,
            AutoSize = false,
        };
        Controls.Add(_contentPanel);

        BuildTiles();

        PluginManager.PluginsChanged += OnPluginsChanged;
        FormClosed += (_, _) => PluginManager.PluginsChanged -= OnPluginsChanged;
    }

    private void OnPluginsChanged()
    {
        if (InvokeRequired) { Invoke(OnPluginsChanged); return; }
        BuildTiles();
    }

    public void ApplyTheme(PopupTheme theme)
    {
        _theme = theme;
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        var oldIcon = Icon;
        Icon = Themes.CreateAppIcon(theme.Primary);
        oldIcon?.Dispose();
        BuildTiles();
    }

    public void BuildTiles()
    {
        _contentPanel.Controls.Clear();
        int y = Pad;

        // --- Title ---
        var titleLabel = new Label
        {
            Text = "ProdToy",
            Font = new Font("Segoe UI Semibold", 20f, FontStyle.Bold),
            ForeColor = _theme.TextPrimary,
            AutoSize = true,
            Location = new Point(Pad, y),
            BackColor = Color.Transparent,
        };
        _contentPanel.Controls.Add(titleLabel);

        y += 42;

        var accentLine = new Panel
        {
            BackColor = _theme.Primary,
            Location = new Point(Pad, y),
            Size = new Size(FormWidth - Pad * 2, 2),
        };
        _contentPanel.Controls.Add(accentLine);
        y += 14;

        // --- Host section ---
        y = AddSectionLabel("PRODTOY", y);
        y = AddTile("Settings", "App, themes, and plugins", "\u2699\uFE0F",
            _theme.PrimaryDim, () => ShowSettingsRequested?.Invoke(), 0, y);
        y = FinishRow(1, y);

        // --- Plugin sections ---
        var dashboardGroups = PluginManager.GetAllDashboardItems();
        foreach (var (plugin, items) in dashboardGroups)
        {
            y = AddSectionLabel(plugin.Name.ToUpperInvariant(), y);
            int col = 0;
            foreach (var item in items)
            {
                y = AddTile(item.Text, "", item.Icon, _theme.PrimaryDim, item.OnClick, col, y);
                col++;
                if (col >= TilesPerRow)
                {
                    col = 0;
                    y += TileHeight + TileGap;
                }
            }
            if (col > 0)
                y += TileHeight + TileGap;
        }

        // --- Separator + Close to Tray button ---
        y += 4;
        var separator = new Panel
        {
            BackColor = _theme.Border,
            Location = new Point(Pad, y),
            Size = new Size(FormWidth - Pad * 2, 1),
        };
        _contentPanel.Controls.Add(separator);
        y += 12;

        int btnWidth = 140;
        var closeBtn = new LinkLabel
        {
            Text = "Close to Tray",
            Font = new Font("Segoe UI", 9f),
            LinkColor = _theme.TextSecondary,
            ActiveLinkColor = _theme.Primary,
            VisitedLinkColor = _theme.TextSecondary,
            AutoSize = true,
            Location = new Point((FormWidth - btnWidth) / 2, y),
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
        };
        // Center after measuring
        closeBtn.CreateControl();
        closeBtn.Location = new Point((FormWidth - closeBtn.PreferredWidth) / 2, y);
        closeBtn.LinkClicked += (_, _) => Hide();
        _contentPanel.Controls.Add(closeBtn);
        y += 24;

        // Version label bottom right
        y += 4;
        var versionLabel = new Label
        {
            Text = $"v{AppVersion.Current}",
            Font = new Font("Segoe UI", 7.5f),
            ForeColor = Color.FromArgb(80, _theme.TextSecondary),
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        versionLabel.Location = new Point(FormWidth - Pad - versionLabel.PreferredWidth, y);
        _contentPanel.Controls.Add(versionLabel);
        y += 16;

        // Resize form to fit content
        int totalHeight = y;
        _contentPanel.Size = new Size(FormWidth, totalHeight);
        ClientSize = new Size(FormWidth, totalHeight);
    }

    private int AddSectionLabel(string text, int y)
    {
        var label = new Label
        {
            Text = text,
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
            ForeColor = _theme.Primary,
            AutoSize = true,
            Location = new Point(Pad, y),
            BackColor = Color.Transparent,
        };
        _contentPanel.Controls.Add(label);
        return y + 22;
    }

    private int AddTile(string title, string subtitle, string iconChar, Color tileColor,
        Action onClick, int col, int y)
    {
        int x = Pad + col * (TileWidth + TileGap);

        var tile = new Panel
        {
            Location = new Point(x, y),
            Size = new Size(TileWidth, TileHeight),
            BackColor = Color.FromArgb(30, tileColor),
            Cursor = Cursors.Hand,
        };

        int iconAreaWidth = 42;
        int textX = iconAreaWidth + 2;

        tile.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = GetRoundedRect(new Rectangle(0, 0, tile.Width - 1, tile.Height - 1), 10);
            using var brush = new SolidBrush(tile.BackColor);
            e.Graphics.Clear(_theme.BgDark);
            e.Graphics.FillPath(brush, path);
            using var borderPen = new Pen(Color.FromArgb(40, tileColor), 1f);
            e.Graphics.DrawPath(borderPen, path);

            using var iconFont = new Font("Segoe UI", 15f);
            using var iconBrush = new SolidBrush(tileColor);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            e.Graphics.DrawString(iconChar, iconFont, iconBrush,
                new RectangleF(2, 0, iconAreaWidth, tile.Height), sf);
        };

        var titleLabel = new Label
        {
            Text = title,
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
            ForeColor = _theme.TextPrimary,
            AutoSize = true,
            MaximumSize = new Size(TileWidth - textX - 8, 0),
            Location = new Point(textX, string.IsNullOrEmpty(subtitle) ? 20 : 10),
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
        };
        tile.Controls.Add(titleLabel);

        if (!string.IsNullOrEmpty(subtitle))
        {
            var subLabel = new Label
            {
                Text = subtitle,
                Font = new Font("Segoe UI", 7.5f),
                ForeColor = _theme.TextSecondary,
                AutoSize = true,
                MaximumSize = new Size(TileWidth - textX - 8, 0),
                Location = new Point(textX, 32),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
            };
            tile.Controls.Add(subLabel);
            subLabel.Click += (_, _) => onClick();
        }

        // Hover effect
        var normalBg = tile.BackColor;
        var hoverBg = Color.FromArgb(50, tileColor);
        Action<Control> attachHover = null!;
        attachHover = (c) =>
        {
            c.MouseEnter += (_, _) => { tile.BackColor = hoverBg; tile.Invalidate(); };
            c.MouseLeave += (_, _) => { tile.BackColor = normalBg; tile.Invalidate(); };
            c.Click += (_, _) => onClick();
            foreach (Control child in c.Controls) attachHover(child);
        };
        attachHover(tile);

        _contentPanel.Controls.Add(tile);
        return y; // don't advance y — caller handles row logic
    }

    private int FinishRow(int itemsInRow, int y)
    {
        if (itemsInRow > 0)
            return y + TileHeight + TileGap;
        return y;
    }

    private static GraphicsPath GetRoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            base.OnFormClosing(e);
        }
    }

    public void BringToForeground()
    {
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        TopMost = true;
        Activate();
        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += (_, _) => { TopMost = false; timer.Stop(); timer.Dispose(); };
        timer.Start();
    }
}
