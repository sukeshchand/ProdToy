using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace DevToy;

/// <summary>
/// Collapsible right-side panel. Shows unsaved temp captures at top,
/// then last 10 saved screenshots below. Click to select (highlight only).
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
    private const int MaxSaved = 10;

    private bool _collapsed;
    private string? _selectedId;
    private Panel? _selectedItemPanel;

    /// <summary>Fires with the file/temp path of the selected item, or null on deselect.</summary>
    public event Action<string?>? SelectionChanged;

    public string? SelectedId => _selectedId;
    public bool IsCollapsed => _collapsed;

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

        LoadAll();
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
        _selectedId = null;
        _selectedItemPanel = null;
        _scrollContent.Controls.Clear();
        LoadAll();
    }

    private void LoadAll()
    {
        int y = 2;
        int innerW = PanelWidth - 16;

        // --- Unsaved (temp) items ---
        var tempItems = GetTempItems();
        if (tempItems.Length > 0)
        {
            var unsavedLabel = new Label
            {
                Text = "UNSAVED", Font = new Font("Segoe UI Semibold", 7f, FontStyle.Bold),
                ForeColor = _theme.ErrorColor, AutoSize = true,
                Location = new Point(6, y), BackColor = Color.Transparent,
            };
            _scrollContent.Controls.Add(unsavedLabel);
            y += 15;

            foreach (var (id, previewPath, timestamp) in tempItems)
            {
                string label = timestamp.ToString("HH:mm:ss");
                var item = CreateItem(id, previewPath, label, y, innerW, isUnsaved: true);
                _scrollContent.Controls.Add(item);
                y += item.Height + ItemPad;
            }

            // Separator
            _scrollContent.Controls.Add(new Panel
            {
                Location = new Point(6, y + 2),
                Size = new Size(PanelWidth - 28, 1),
                BackColor = _theme.Border,
            });
            y += 8;
        }

        // --- Saved items ---
        var savedFiles = GetSavedFiles();
        if (savedFiles.Length > 0)
        {
            var savedLabel = new Label
            {
                Text = "SAVED", Font = new Font("Segoe UI Semibold", 7f, FontStyle.Bold),
                ForeColor = _theme.TextSecondary, AutoSize = true,
                Location = new Point(6, y), BackColor = Color.Transparent,
            };
            _scrollContent.Controls.Add(savedLabel);
            y += 15;

            foreach (var filePath in savedFiles)
            {
                string name = Path.GetFileNameWithoutExtension(filePath);
                if (name.Length > 20) name = name[..17] + "...";
                var item = CreateItem(filePath, filePath, name, y, innerW, isUnsaved: false);
                _scrollContent.Controls.Add(item);
                y += item.Height + ItemPad;
            }
        }
    }

    private Panel CreateItem(string id, string imagePath, string label, int y, int innerW, bool isUnsaved)
    {
        var panel = new Panel
        {
            Location = new Point(4, y),
            Size = new Size(innerW, ThumbHeight + 16),
            BackColor = _theme.BgDark,
            Cursor = Cursors.Hand,
        };

        // Unsaved accent bar
        if (isUnsaved)
        {
            panel.Controls.Add(new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(3, panel.Height),
                BackColor = _theme.ErrorColor,
            });
        }

        // Thumbnail
        var thumb = new PictureBox
        {
            Location = new Point(isUnsaved ? 5 : 2, 2),
            Size = new Size(innerW - (isUnsaved ? 7 : 4), ThumbHeight),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(35, 35, 35),
        };
        try
        {
            using var stream = File.OpenRead(imagePath);
            using var img = Image.FromStream(stream);
            var bmp = new Bitmap(thumb.Width, ThumbHeight);
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

        // Label
        var nameLabel = new Label
        {
            Text = label, Font = new Font("Segoe UI", 7f),
            ForeColor = isUnsaved ? _theme.ErrorColor : _theme.TextSecondary,
            AutoSize = false, Size = new Size(innerW - 4, 14),
            Location = new Point(isUnsaved ? 5 : 2, ThumbHeight + 2),
            BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft,
        };
        panel.Controls.Add(nameLabel);

        // Click = select
        var itemId = id;
        void OnClick(object? s, EventArgs e) => SelectItem(itemId, panel);
        panel.Click += OnClick;
        thumb.Click += OnClick;
        nameLabel.Click += OnClick;

        // Hover
        panel.MouseEnter += (_, _) => { if (_selectedItemPanel != panel) panel.BackColor = _theme.PrimaryDim; };
        panel.MouseLeave += (_, _) => { if (_selectedItemPanel != panel) panel.BackColor = _theme.BgDark; };
        thumb.MouseEnter += (_, _) => { if (_selectedItemPanel != panel) panel.BackColor = _theme.PrimaryDim; };
        thumb.MouseLeave += (_, _) => { if (_selectedItemPanel != panel) panel.BackColor = _theme.BgDark; };

        return panel;
    }

    private void SelectItem(string id, Panel panel)
    {
        if (_selectedId == id)
        {
            _selectedItemPanel!.BackColor = _theme.BgDark;
            _selectedId = null;
            _selectedItemPanel = null;
            SelectionChanged?.Invoke(null);
            return;
        }

        if (_selectedItemPanel != null)
            _selectedItemPanel.BackColor = _theme.BgDark;

        _selectedId = id;
        _selectedItemPanel = panel;
        panel.BackColor = Color.FromArgb(35, _theme.Primary.R, _theme.Primary.G, _theme.Primary.B);
        SelectionChanged?.Invoke(id);
    }

    // --- Data sources ---

    private static (string Id, string PreviewPath, DateTime Timestamp)[] GetTempItems()
    {
        try
        {
            string tempDir = AppPaths.ScreenshotsTempDir;
            if (!Directory.Exists(tempDir)) return Array.Empty<(string, string, DateTime)>();

            return Directory.GetDirectories(tempDir)
                .Select(dir =>
                {
                    string id = Path.GetFileName(dir);
                    string preview = Path.Combine(dir, "preview.jpg");
                    if (!File.Exists(preview))
                        preview = Path.Combine(dir, "base.png");
                    if (!File.Exists(preview)) return default;
                    return (Id: id, PreviewPath: preview, Timestamp: File.GetLastWriteTime(preview));
                })
                .Where(x => x.Id != null)
                .OrderByDescending(x => x.Timestamp)
                .ToArray();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GetTempItems failed: {ex.Message}");
            return Array.Empty<(string, string, DateTime)>();
        }
    }

    private static string[] GetSavedFiles()
    {
        try
        {
            string dir = AppPaths.ScreenshotsDir;
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            return Directory.GetFiles(dir, "*.png")
                .Concat(Directory.GetFiles(dir, "*.jpg"))
                .Concat(Directory.GetFiles(dir, "*.bmp"))
                .OrderByDescending(File.GetLastWriteTime)
                .Take(MaxSaved)
                .ToArray();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GetSavedFiles failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }
}
