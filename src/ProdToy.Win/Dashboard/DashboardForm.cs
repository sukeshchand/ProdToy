using System.Drawing;
using System.Drawing.Drawing2D;
using ProdToy.Sdk;

namespace ProdToy;

class DashboardForm : Form
{
    private PopupTheme _theme;
    private readonly Panel _contentPanel;

    private const int Pad = 28;
    private const int TileWidth = 260;
    private const int TileHeight = 88;
    private const int TileGap = 12;
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

        // --- Header: app name + version ---
        var titleLabel = new Label
        {
            Text = "ProdToy",
            Font = new Font("Segoe UI Semibold", 22f, FontStyle.Bold),
            ForeColor = _theme.TextPrimary,
            AutoSize = true,
            Location = new Point(Pad, y),
            BackColor = Color.Transparent,
        };
        _contentPanel.Controls.Add(titleLabel);

        var versionLabel = new Label
        {
            Text = $"v{AppVersion.Current}",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.FromArgb(100, _theme.TextSecondary),
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        versionLabel.Location = new Point(FormWidth - Pad - versionLabel.PreferredWidth, y + 12);
        _contentPanel.Controls.Add(versionLabel);

        y += 46;

        var accentLine = new Panel
        {
            BackColor = _theme.Primary,
            Location = new Point(Pad, y),
            Size = new Size(FormWidth - Pad * 2, 2),
        };
        _contentPanel.Controls.Add(accentLine);
        y += 20;

        // --- General section ---
        y = AddSectionHeader("General", y);
        int col = 0;
        y = AddTile("settings", "Settings", "Themes, plugins & preferences",
            _theme.Primary, () => ShowSettingsRequested?.Invoke(), col++, y);
        y = FinishRow(col, y);

        // --- Plugin sections ---
        var dashboardGroups = PluginManager.GetAllDashboardItems();
        foreach (var (plugin, items) in dashboardGroups)
        {
            y = AddSectionHeader(plugin.Name, y);
            col = 0;
            foreach (var item in items)
            {
                string iconKey = GetIconKey(item.Text);
                string subtitle = GetSubtitle(item.Text);
                y = AddTile(iconKey, item.Text, subtitle,
                    _theme.Primary, item.OnClick, col, y);
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

        // --- Footer ---
        y += 4;
        var separator = new Panel
        {
            BackColor = Color.FromArgb(40, _theme.Border),
            Location = new Point(Pad, y),
            Size = new Size(FormWidth - Pad * 2, 1),
        };
        _contentPanel.Controls.Add(separator);
        y += 16;

        var closeBtn = new Label
        {
            Text = "Close to Tray",
            Font = new Font("Segoe UI", 9f),
            ForeColor = _theme.TextSecondary,
            AutoSize = true,
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
        };
        closeBtn.CreateControl();
        closeBtn.Location = new Point((FormWidth - closeBtn.PreferredWidth) / 2, y);
        closeBtn.Click += (_, _) => Hide();
        closeBtn.MouseEnter += (_, _) => closeBtn.ForeColor = _theme.Primary;
        closeBtn.MouseLeave += (_, _) => closeBtn.ForeColor = _theme.TextSecondary;
        _contentPanel.Controls.Add(closeBtn);
        y += 28;

        int totalHeight = y;
        _contentPanel.Size = new Size(FormWidth, totalHeight);
        ClientSize = new Size(FormWidth, totalHeight);
    }

    private static string GetIconKey(string tileText) => tileText switch
    {
        "Take Screenshot" => "screenshot",
        "Edit Last Screenshot" => "edit",
        "Alarms" => "alarm",
        "Alarm History" => "history",
        "Last Notification" => "notification",
        _ => "default",
    };

    private static string GetSubtitle(string tileText) => tileText switch
    {
        "Take Screenshot" => "Capture a region  \u2022  Ctrl+Q",
        "Edit Last Screenshot" => "Re-open editor  \u2022  Triple Ctrl",
        "Alarms" => "Set timers and reminders",
        "Alarm History" => "View past alarms",
        "Last Notification" => "Show recent Claude message",
        _ => "",
    };

    private int AddSectionHeader(string text, int y)
    {
        var bar = new Panel
        {
            BackColor = _theme.Primary,
            Location = new Point(Pad, y + 4),
            Size = new Size(3, 14),
        };
        _contentPanel.Controls.Add(bar);

        var label = new Label
        {
            Text = text.ToUpperInvariant(),
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
            ForeColor = _theme.TextSecondary,
            AutoSize = true,
            Location = new Point(Pad + 10, y + 2),
            BackColor = Color.Transparent,
        };
        _contentPanel.Controls.Add(label);
        return y + 28;
    }

    private int AddTile(string iconKey, string title, string subtitle, Color accentColor,
        Action onClick, int col, int y)
    {
        int x = Pad + col * (TileWidth + TileGap);
        int iconSize = 44;
        int iconPad = 20;
        int textX = iconPad + iconSize + 14;

        var tile = new Panel
        {
            Location = new Point(x, y),
            Size = new Size(TileWidth, TileHeight),
            Cursor = Cursors.Hand,
        };

        var normalBg = Color.FromArgb(18, accentColor);
        var hoverBg = Color.FromArgb(38, accentColor);
        var borderColor = Color.FromArgb(30, accentColor);
        var hoverBorderColor = Color.FromArgb(60, accentColor);
        bool isHover = false;

        tile.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Tile background
            using var bgPath = GetRoundedRect(new Rectangle(0, 0, tile.Width - 1, tile.Height - 1), 12);
            using var bgBrush = new SolidBrush(isHover ? hoverBg : normalBg);
            g.Clear(_theme.BgDark);
            g.FillPath(bgBrush, bgPath);
            using var bPen = new Pen(isHover ? hoverBorderColor : borderColor, 1f);
            g.DrawPath(bPen, bgPath);

            // Icon circle
            int cx = iconPad;
            int cy = (tile.Height - iconSize) / 2;
            using var circlePath = new GraphicsPath();
            circlePath.AddEllipse(cx, cy, iconSize, iconSize);
            using var circleBrush = new SolidBrush(Color.FromArgb(isHover ? 50 : 35, accentColor));
            g.FillPath(circleBrush, circlePath);
            using var circleRing = new Pen(Color.FromArgb(isHover ? 80 : 50, accentColor), 1f);
            g.DrawPath(circleRing, circlePath);

            // Draw vector icon
            var iconColor = isHover ? _theme.PrimaryLight : accentColor;
            DrawIcon(g, iconKey, new RectangleF(cx + 8, cy + 8, iconSize - 16, iconSize - 16), iconColor);

            // Title
            using var titleFont = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
            using var titleBrush = new SolidBrush(_theme.TextPrimary);
            int titleY = string.IsNullOrEmpty(subtitle) ? (tile.Height - 18) / 2 : 20;
            g.DrawString(title, titleFont, titleBrush, textX, titleY);

            // Subtitle
            if (!string.IsNullOrEmpty(subtitle))
            {
                using var subFont = new Font("Segoe UI", 7.8f);
                using var subBrush = new SolidBrush(Color.FromArgb(isHover ? 200 : 160,
                    _theme.TextSecondary));
                g.DrawString(subtitle, subFont, subBrush, textX, titleY + 22);
            }
        };

        Action<Control> attachEvents = null!;
        attachEvents = (c) =>
        {
            c.MouseEnter += (_, _) => { isHover = true; tile.Invalidate(); };
            c.MouseLeave += (_, _) => { isHover = false; tile.Invalidate(); };
            c.Click += (_, _) => onClick();
            foreach (Control child in c.Controls) attachEvents(child);
        };
        attachEvents(tile);

        _contentPanel.Controls.Add(tile);
        return y;
    }

    // ---------------------------------------------------------------
    //  Vector icon drawing — all GDI+, no emoji, no image assets
    // ---------------------------------------------------------------

    private static void DrawIcon(Graphics g, string key, RectangleF r, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.8f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var brush = new SolidBrush(color);

        float x = r.X, y = r.Y, w = r.Width, h = r.Height;

        switch (key)
        {
            case "screenshot":
                DrawScreenshotIcon(g, pen, brush, x, y, w, h);
                break;
            case "edit":
                DrawEditIcon(g, pen, brush, x, y, w, h);
                break;
            case "settings":
                DrawSettingsIcon(g, pen, brush, x, y, w, h);
                break;
            case "alarm":
                DrawAlarmIcon(g, pen, brush, x, y, w, h);
                break;
            case "history":
                DrawHistoryIcon(g, pen, brush, x, y, w, h);
                break;
            case "notification":
                DrawNotificationIcon(g, pen, brush, x, y, w, h);
                break;
            default:
                DrawDefaultIcon(g, pen, brush, x, y, w, h);
                break;
        }
    }

    /// <summary>
    /// Screenshot icon: crop-corner brackets (top-left, top-right, bottom-left)
    /// with a small camera in the bottom-right quadrant.
    /// </summary>
    private static void DrawScreenshotIcon(Graphics g, Pen pen, SolidBrush brush,
        float x, float y, float w, float h)
    {
        float t = pen.Width;
        float corner = w * 0.38f; // bracket arm length

        // Top-left bracket
        g.DrawLine(pen, x, y + corner, x, y);
        g.DrawLine(pen, x, y, x + corner, y);

        // Top-right bracket
        g.DrawLine(pen, x + w - corner, y, x + w, y);
        g.DrawLine(pen, x + w, y, x + w, y + corner);

        // Bottom-left bracket
        g.DrawLine(pen, x, y + h - corner, x, y + h);
        g.DrawLine(pen, x, y + h, x + corner, y + h);

        // Camera body in bottom-right area
        float camW = w * 0.50f;
        float camH = h * 0.38f;
        float camX = x + w - camW;
        float camY = y + h - camH;
        float camR = 2.5f;

        // Camera bump (viewfinder)
        float bumpW = camW * 0.35f;
        float bumpH = camH * 0.28f;
        float bumpX = camX + (camW - bumpW) / 2;
        g.FillRectangle(brush, bumpX, camY - bumpH + 1, bumpW, bumpH);

        // Camera body rounded rect
        using var camPath = GetRoundedRect(
            Rectangle.Round(new RectangleF(camX, camY, camW, camH)), (int)camR);
        g.FillPath(brush, camPath);

        // Lens circle (cut out)
        float lensD = camH * 0.52f;
        float lensX = camX + (camW - lensD) / 2;
        float lensY = camY + (camH - lensD) / 2;
        using var bgBrush = new SolidBrush(Color.FromArgb(60, 0, 0, 0));
        g.FillEllipse(bgBrush, lensX, lensY, lensD, lensD);

        // Inner dot
        float dotD = lensD * 0.4f;
        g.FillEllipse(brush, lensX + (lensD - dotD) / 2, lensY + (lensD - dotD) / 2, dotD, dotD);
    }

    /// <summary>
    /// Edit icon: crop-corner brackets (like screenshot) with scissors inside.
    /// </summary>
    private static void DrawEditIcon(Graphics g, Pen pen, SolidBrush brush,
        float x, float y, float w, float h)
    {
        float corner = w * 0.38f;

        // Top-left bracket
        g.DrawLine(pen, x, y + corner, x, y);
        g.DrawLine(pen, x, y, x + corner, y);

        // Top-right bracket
        g.DrawLine(pen, x + w - corner, y, x + w, y);
        g.DrawLine(pen, x + w, y, x + w, y + corner);

        // Bottom-left bracket
        g.DrawLine(pen, x, y + h - corner, x, y + h);
        g.DrawLine(pen, x, y + h, x + corner, y + h);

        // Scissors in center-bottom area
        float cx = x + w * 0.55f;
        float cy = y + h * 0.58f;
        float ringR = w * 0.13f;  // finger-ring radius
        float bladeLen = w * 0.32f;

        // Left ring (bottom-left)
        g.DrawEllipse(pen, cx - ringR * 2.2f, cy + bladeLen * 0.4f, ringR * 2, ringR * 2);
        // Right ring (bottom-right)
        g.DrawEllipse(pen, cx + ringR * 0.2f, cy + bladeLen * 0.4f, ringR * 2, ringR * 2);

        // Left blade: from left ring up-right to pivot, then extends up-right
        using var bladePen = new Pen(pen.Color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        // Blades cross at pivot point
        float pivotX = cx;
        float pivotY = cy + bladeLen * 0.2f;

        // Left blade: ring-top → pivot → tip upper-right
        g.DrawLine(bladePen, cx - ringR * 1.2f, cy + bladeLen * 0.4f, pivotX, pivotY);
        g.DrawLine(bladePen, pivotX, pivotY, cx + bladeLen * 0.6f, cy - bladeLen * 0.5f);

        // Right blade: ring-top → pivot → tip upper-left
        g.DrawLine(bladePen, cx + ringR * 1.2f, cy + bladeLen * 0.4f, pivotX, pivotY);
        g.DrawLine(bladePen, pivotX, pivotY, cx - bladeLen * 0.6f, cy - bladeLen * 0.5f);

        // Pivot dot
        float dotR = 1.2f;
        g.FillEllipse(brush, pivotX - dotR, pivotY - dotR, dotR * 2, dotR * 2);
    }

    /// <summary>
    /// Settings icon: gear with 6 teeth.
    /// </summary>
    private static void DrawSettingsIcon(Graphics g, Pen pen, SolidBrush brush,
        float x, float y, float w, float h)
    {
        float cx = x + w / 2;
        float cy = y + h / 2;
        float outerR = w / 2 - 0.5f;
        float innerR = outerR * 0.62f;
        float holeR = outerR * 0.30f;
        int teeth = 6;

        using var gearPath = new GraphicsPath();
        int steps = teeth * 2;
        var pts = new PointF[steps];
        for (int i = 0; i < steps; i++)
        {
            float angle = (float)(i * Math.PI / teeth - Math.PI / 2);
            float r = (i % 2 == 0) ? outerR : innerR;
            pts[i] = new PointF(cx + r * (float)Math.Cos(angle), cy + r * (float)Math.Sin(angle));
        }
        gearPath.AddPolygon(pts);
        g.FillPath(brush, gearPath);

        // Center hole
        using var holeBrush = new SolidBrush(Color.FromArgb(80, 0, 0, 0));
        g.FillEllipse(holeBrush, cx - holeR, cy - holeR, holeR * 2, holeR * 2);
    }

    /// <summary>
    /// Alarm icon: classic bell shape.
    /// </summary>
    private static void DrawAlarmIcon(Graphics g, Pen pen, SolidBrush brush,
        float x, float y, float w, float h)
    {
        float cx = x + w / 2;

        // Bell body
        using var bellPath = new GraphicsPath();
        float bellTop = y + h * 0.12f;
        float bellBot = y + h * 0.75f;
        float bellW = w * 0.7f;

        // Dome
        bellPath.AddArc(cx - bellW / 2, bellTop, bellW, (bellBot - bellTop) * 1.1f, 180, 180);
        // Sides down to brim
        float brimW = w * 0.85f;
        bellPath.AddLine(cx + bellW / 2, bellBot - (bellBot - bellTop) * 0.1f, cx + brimW / 2, bellBot);
        bellPath.AddLine(cx + brimW / 2, bellBot, cx - brimW / 2, bellBot);
        bellPath.AddLine(cx - brimW / 2, bellBot, cx - bellW / 2, bellBot - (bellBot - bellTop) * 0.1f);
        bellPath.CloseFigure();
        g.FillPath(brush, bellPath);

        // Clapper
        float clapD = w * 0.16f;
        g.FillEllipse(brush, cx - clapD / 2, bellBot + 1, clapD, clapD);

        // Top knob
        float knobD = w * 0.14f;
        g.FillEllipse(brush, cx - knobD / 2, y, knobD, knobD);
    }

    /// <summary>
    /// History icon: clock face with an arrow.
    /// </summary>
    private static void DrawHistoryIcon(Graphics g, Pen pen, SolidBrush brush,
        float x, float y, float w, float h)
    {
        float cx = x + w / 2;
        float cy = y + h / 2;
        float r = w / 2 - 1;

        // Circle
        g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);

        // Clock hands
        // Hour hand (short, pointing ~10 o'clock)
        float hourAngle = (float)(-Math.PI / 2 + Math.PI * 2 * 10 / 12);
        float hourLen = r * 0.45f;
        g.DrawLine(pen, cx, cy,
            cx + hourLen * (float)Math.Cos(hourAngle),
            cy + hourLen * (float)Math.Sin(hourAngle));

        // Minute hand (long, pointing ~2 o'clock)
        float minAngle = (float)(-Math.PI / 2 + Math.PI * 2 * 2 / 12);
        float minLen = r * 0.7f;
        g.DrawLine(pen, cx, cy,
            cx + minLen * (float)Math.Cos(minAngle),
            cy + minLen * (float)Math.Sin(minAngle));

        // Center dot
        float dotR = 1.5f;
        g.FillEllipse(brush, cx - dotR, cy - dotR, dotR * 2, dotR * 2);
    }

    /// <summary>
    /// Notification icon: chat bubble with lines.
    /// </summary>
    private static void DrawNotificationIcon(Graphics g, Pen pen, SolidBrush brush,
        float x, float y, float w, float h)
    {
        // Rounded bubble
        float bubH = h * 0.72f;
        float radius = 4f;
        using var bubPath = GetRoundedRect(
            Rectangle.Round(new RectangleF(x, y, w, bubH)), (int)radius);
        g.FillPath(brush, bubPath);

        // Tail triangle
        float tailX = x + w * 0.25f;
        float tailY = y + bubH - 1;
        var tail = new PointF[]
        {
            new(tailX, tailY),
            new(tailX + w * 0.12f, tailY),
            new(tailX - w * 0.04f, tailY + h * 0.22f),
        };
        g.FillPolygon(brush, tail);

        // Message lines (darker, cut into the bubble)
        using var lineBrush = new SolidBrush(Color.FromArgb(80, 0, 0, 0));
        float lineH = 1.8f;
        float lineY1 = y + bubH * 0.30f;
        float lineY2 = y + bubH * 0.55f;
        float lineX = x + w * 0.18f;
        g.FillRectangle(lineBrush, lineX, lineY1, w * 0.64f, lineH);
        g.FillRectangle(lineBrush, lineX, lineY2, w * 0.44f, lineH);
    }

    /// <summary>Fallback: simple filled circle with a dot.</summary>
    private static void DrawDefaultIcon(Graphics g, Pen pen, SolidBrush brush,
        float x, float y, float w, float h)
    {
        float cx = x + w / 2;
        float cy = y + h / 2;
        float r = w / 2 - 1;
        g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
        float dotR = 2.5f;
        g.FillEllipse(brush, cx - dotR, cy - dotR, dotR * 2, dotR * 2);
    }

    // ---------------------------------------------------------------

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
