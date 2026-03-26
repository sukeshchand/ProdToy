using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace DevToy;

/// <summary>
/// Collapsible right-side panel showing the last 10 saved screenshots.
/// Click to select (highlight only).
/// </summary>
class RecentImagesPanel : Panel
{
    private readonly PopupTheme _theme;
    private readonly Panel _scrollContent;
    private readonly RoundedButton _toggleBtn;
    private readonly Label _titleLabel;

    private const int PanelWidth = 170;
    private const int ThumbHeight = 75;
    private const int ItemPad = 5;
    private const int MaxItems = 10;

    private bool _collapsed;
    private string? _selectedFilePath;
    private Panel? _selectedItemPanel;
    private string? _editingEditId;

    public event Action<string?>? SelectionChanged;
    public event Action<string>? OpenRequested;

    public string? SelectedFilePath => _selectedFilePath;
    public bool IsCollapsed => _collapsed;

    /// <summary>Set the EditId of the currently editing session to highlight it in the list.</summary>
    public void SetEditingId(string? editId)
    {
        _editingEditId = editId;
        Reload();
    }

    public RecentImagesPanel(PopupTheme theme)
    {
        _theme = theme;
        Width = PanelWidth;
        BackColor = theme.BgHeader;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

        _toggleBtn = new RoundedButton
        {
            Text = "\u25B6", Font = new Font("Segoe UI", 9f),
            Size = new Size(24, 24), Location = new Point(3, 4),
            FlatStyle = FlatStyle.Flat, BackColor = theme.PrimaryDim,
            ForeColor = theme.TextSecondary, Cursor = Cursors.Hand,
        };
        _toggleBtn.FlatAppearance.BorderSize = 0;
        _toggleBtn.FlatAppearance.MouseOverBackColor = theme.Primary;
        _toggleBtn.Click += (_, _) => ToggleCollapse();
        Controls.Add(_toggleBtn);

        _titleLabel = new Label
        {
            Text = "Recent", Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
            ForeColor = theme.TextSecondary, AutoSize = true,
            Location = new Point(30, 7), BackColor = Color.Transparent,
        };
        Controls.Add(_titleLabel);

        _scrollContent = new Panel
        {
            Location = new Point(0, 32),
            Size = new Size(PanelWidth, Height - 32),
            AutoScroll = true, BackColor = theme.BgHeader,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom | AnchorStyles.Right,
        };
        Controls.Add(_scrollContent);

        Controls.Add(new Panel { Width = 1, Dock = DockStyle.Left, BackColor = theme.Border });

        LoadImages();
    }

    public void ToggleCollapse()
    {
        _collapsed = !_collapsed;
        if (_collapsed)
        {
            Width = 28;
            _toggleBtn.Text = "\u25C0";
            _scrollContent.Visible = false;
            _titleLabel.Visible = false;
        }
        else
        {
            Width = PanelWidth;
            _toggleBtn.Text = "\u25B6";
            _scrollContent.Visible = true;
            _titleLabel.Visible = true;
        }
    }

    public void Reload()
    {
        _selectedFilePath = null;
        _selectedItemPanel = null;
        _scrollContent.Controls.Clear();
        LoadImages();
    }

    private void LoadImages()
    {
        string[] files;
        try
        {
            string dir = AppPaths.ScreenshotsDir;
            if (!Directory.Exists(dir)) return;
            files = Directory.GetFiles(dir, "*.png")
                .Concat(Directory.GetFiles(dir, "*.jpg"))
                .Concat(Directory.GetFiles(dir, "*.bmp"))
                .OrderByDescending(File.GetLastWriteTime)
                .Take(MaxItems)
                .ToArray();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to list screenshots: {ex.Message}");
            return;
        }

        int y = 2;
        int innerW = PanelWidth - 16;

        foreach (var filePath in files)
        {
            // Check if this file matches the currently editing session
            bool isEditing = _editingEditId != null &&
                Path.GetFileNameWithoutExtension(filePath)
                    .Equals(_editingEditId, StringComparison.OrdinalIgnoreCase);

            var item = CreateItem(filePath, y, innerW, isEditing);
            _scrollContent.Controls.Add(item);
            y += item.Height + ItemPad;
        }
    }

    private Panel CreateItem(string filePath, int y, int innerW, bool isEditing)
    {
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        if (fileName.Length > 20) fileName = fileName[..17] + "...";

        int extraH = isEditing ? 16 : 0;
        var panel = new Panel
        {
            Location = new Point(4, y),
            Size = new Size(innerW, ThumbHeight + 16 + extraH),
            BackColor = isEditing
                ? Color.FromArgb(20, _theme.Primary.R, _theme.Primary.G, _theme.Primary.B)
                : _theme.BgDark,
            Cursor = Cursors.Hand,
        };

        // Editing accent bar + label
        if (isEditing)
        {
            panel.Controls.Add(new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(3, panel.Height),
                BackColor = _theme.Primary,
            });
            panel.Controls.Add(new Label
            {
                Text = "\u25CF Editing",
                Font = new Font("Segoe UI", 7f, FontStyle.Bold),
                ForeColor = _theme.Primary,
                AutoSize = true,
                Location = new Point(8, 2),
                BackColor = Color.Transparent,
            });
        }

        int thumbTop = isEditing ? 16 : 2;
        var thumb = new PictureBox
        {
            Location = new Point(isEditing ? 6 : 2, thumbTop),
            Size = new Size(innerW - (isEditing ? 8 : 4), ThumbHeight),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(35, 35, 35),
        };
        try
        {
            using var stream = File.OpenRead(filePath);
            using var img = Image.FromStream(stream);
            var bmp = new Bitmap(innerW - 4, ThumbHeight);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(Color.FromArgb(35, 35, 35));
                float scale = Math.Min((float)bmp.Width / img.Width, (float)bmp.Height / img.Height);
                int w = (int)(img.Width * scale), h = (int)(img.Height * scale);
                g.DrawImage(img, (bmp.Width - w) / 2, (bmp.Height - h) / 2, w, h);
            }
            thumb.Image = bmp;
        }
        catch (Exception ex) { Debug.WriteLine($"Thumb load failed: {ex.Message}"); }
        panel.Controls.Add(thumb);

        var label = new Label
        {
            Text = fileName, Font = new Font("Segoe UI", 7f),
            ForeColor = isEditing ? _theme.TextPrimary : _theme.TextSecondary,
            AutoSize = false, Size = new Size(innerW - 4, 14),
            Location = new Point(isEditing ? 6 : 2, thumbTop + ThumbHeight + 2),
            BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft,
        };
        panel.Controls.Add(label);

        var defaultBg = isEditing
            ? Color.FromArgb(20, _theme.Primary.R, _theme.Primary.G, _theme.Primary.B)
            : _theme.BgDark;

        var path = filePath;
        void OnClick(object? s, EventArgs e) => SelectItem(path, panel, defaultBg);
        void OnDblClick(object? s, EventArgs e) => OpenRequested?.Invoke(path);
        panel.Click += OnClick;
        thumb.Click += OnClick;
        label.Click += OnClick;
        panel.DoubleClick += OnDblClick;
        thumb.DoubleClick += OnDblClick;
        label.DoubleClick += OnDblClick;

        panel.MouseEnter += (_, _) => { if (_selectedItemPanel != panel) panel.BackColor = _theme.PrimaryDim; };
        panel.MouseLeave += (_, _) => { if (_selectedItemPanel != panel) panel.BackColor = defaultBg; };
        thumb.MouseEnter += (_, _) => { if (_selectedItemPanel != panel) panel.BackColor = _theme.PrimaryDim; };
        thumb.MouseLeave += (_, _) => { if (_selectedItemPanel != panel) panel.BackColor = defaultBg; };

        return panel;
    }

    private Color _selectedDefaultBg;

    private void SelectItem(string filePath, Panel panel, Color defaultBg)
    {
        if (_selectedFilePath == filePath)
        {
            _selectedItemPanel!.BackColor = _selectedDefaultBg;
            _selectedFilePath = null;
            _selectedItemPanel = null;
            SelectionChanged?.Invoke(null);
            return;
        }

        if (_selectedItemPanel != null)
            _selectedItemPanel.BackColor = _selectedDefaultBg;

        _selectedFilePath = filePath;
        _selectedItemPanel = panel;
        _selectedDefaultBg = defaultBg;
        panel.BackColor = Color.FromArgb(50, _theme.Primary.R, _theme.Primary.G, _theme.Primary.B);
        SelectionChanged?.Invoke(filePath);
    }
}
