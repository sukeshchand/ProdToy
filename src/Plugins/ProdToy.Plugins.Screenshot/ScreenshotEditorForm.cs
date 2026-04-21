using ProdToy.Sdk;
using System.Diagnostics;
using System.Drawing;

namespace ProdToy.Plugins.Screenshot;

class ScreenshotEditorForm : Form
{
    private EditorSession _session;
    private ScreenshotCanvas _canvas;
    private CanvasContainer _canvasContainer;
    private readonly ScreenshotToolbar _toolbar;
    private readonly RecentImagesPanel _recentPanel;
    private readonly PluginTheme _theme;
    private System.Windows.Forms.Timer _autoSaveTimer;

    public event Action<string>? ImageSaved;
    public event Action? ImageCopied;

    /// <summary>Opens an existing screenshot file, restoring edit history if available.</summary>
    public ScreenshotEditorForm(string filePath) : this(
        LoadExisting(filePath, Path.GetFileNameWithoutExtension(filePath)),
        Path.GetFileNameWithoutExtension(filePath)) { }

    private static Bitmap LoadExisting(string filePath, string editId)
    {
        // Prefer base.png (original) from _edits folder if it exists
        string basePath = Path.Combine(ScreenshotPaths.ScreenshotsEditsDir, editId, "base.png");
        string loadFrom = File.Exists(basePath) ? basePath : filePath;

        using var stream = File.OpenRead(loadFrom);
        using var bmp = new Bitmap(stream);
        var image = new Bitmap(bmp.Width, bmp.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(image);
        g.DrawImage(bmp, 0, 0);
        return image;
    }

    public ScreenshotEditorForm(Bitmap capturedImage, string? existingEditId = null)
    {
        _session = new EditorSession(capturedImage);
        if (existingEditId != null)
        {
            _session.EditId = existingEditId;
            // Create _edits folder if missing
            string editDir = _session.EditDir;
            if (!Directory.Exists(editDir))
            {
                Directory.CreateDirectory(editDir);
                capturedImage.Save(Path.Combine(editDir, "base.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            else
            {
                // Restore annotations and settings from existing state
                SessionSerializer.Restore(_session);
            }
        }
        else
        {
            InitEditFolder(_session, capturedImage);
        }
        _theme = ScreenshotPlugin.GetTheme();

        Text = "ProdToy \u2014 Screenshot Editor";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        ShowInTaskbar = true;
        TopMost = true;
        KeyPreview = true;

        // Come to front on open, then allow going behind other windows
        Shown += (_, _) => { TopMost = false; };
        MinimumSize = new Size(500, 350);
        Icon = IconHelper.CreateAppIcon(_theme.Primary);
        BackColor = _theme.BgDark;
        AutoScaleMode = AutoScaleMode.Dpi;

        int toolbarAreaHeight = 60;
        int pad = 8;
        int extraPad = 80; // extra breathing room around the captured image
        int recentPanelWidth = 170; // right-side recent images panel
        int imgW = capturedImage.Width;
        int imgH = capturedImage.Height;
        int clientW = Math.Max(imgW + extraPad * 2 + recentPanelWidth, 600);
        int clientH = imgH + toolbarAreaHeight + extraPad + pad;
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        clientW = Math.Min(clientW, screen.Width - 40);
        clientH = Math.Min(clientH, screen.Height - 40);
        ClientSize = new Size(clientW, clientH);
        StartPosition = FormStartPosition.CenterScreen;

        // Toolbar
        _toolbar = new ScreenshotToolbar { Session = _session };
        _toolbar.Location = new Point(Math.Max(pad, (ClientSize.Width - _toolbar.Width) / 2), pad);
        Controls.Add(_toolbar);

        // Canvas
        int canvasTop = toolbarAreaHeight;
        _canvas = new ScreenshotCanvas { Size = new Size(imgW, imgH), Session = _session };

        // Recent images panel (right side, collapsible)
        _recentPanel = new RecentImagesPanel(_theme)
        {
            Location = new Point(ClientSize.Width - 170, canvasTop),
            Size = new Size(170, ClientSize.Height - canvasTop),
            Anchor = AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom,
        };
        _recentPanel.OpenRequested += OpenFromList;
        _recentPanel.SetEditingId(_session.EditId);
        Controls.Add(_recentPanel);

        // Canvas container (left of recent panel)
        _canvasContainer = new CanvasContainer(_canvas)
        {
            Location = new Point(0, canvasTop),
            Size = new Size(ClientSize.Width - _recentPanel.Width, ClientSize.Height - canvasTop),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            BackColor = _theme.BgDark,
        };
        Controls.Add(_canvasContainer);

        WireToolbarEvents();

        Resize += (_, _) =>
        {
            _toolbar.Location = new Point(Math.Max(8, (ClientSize.Width - _toolbar.Width) / 2), _toolbar.Location.Y);
            _canvasContainer.Width = ClientSize.Width - _recentPanel.Width;
        };

        _recentPanel.Resize += (_, _) =>
        {
            _canvasContainer.Width = ClientSize.Width - _recentPanel.Width;
            _canvasContainer.CenterCanvas();
        };

        // When canvas resizes due to dropped image, sync container
        _canvas.CanvasResizeRequested += _ => _canvasContainer.SyncCanvasSize();
        _canvas.ToolAutoSwitched += () => _toolbar.Invalidate();

        // Toolbar zoom label reads live zoom; toolbar refreshes on every zoom change.
        // Recentering is done by whatever triggered the zoom (wheel scrolls to anchor cursor,
        // toolbar buttons recenter explicitly), so we only repaint the toolbar here.
        _toolbar.ZoomProvider = () => _canvas.Zoom;
        _canvas.ZoomChanged += () => _toolbar.Invalidate();

        // Auto-save on every undo/redo action and canvas change
        _autoSaveTimer = new System.Windows.Forms.Timer { Interval = 300 };
        _autoSaveTimer.Tick += (_, _) => { _autoSaveTimer.Stop(); AutoSaveNow(); };
        _session.UndoRedo.StateChanged += ScheduleAutoSave;
        _canvas.CanvasChanged += ScheduleAutoSave;
    }

    private void WireToolbarEvents()
    {
        _toolbar.QuickCopyRequested += DoCopy;
        _toolbar.CopyPathRequested += DoCopyPath;
        _toolbar.CopyPathTextRequested += DoCopyPathText;

        _toolbar.ToolSelected += tool =>
        {
            _canvas.CommitTextEdit();
            if (_session.CurrentTool == AnnotationTool.Crop && tool != AnnotationTool.Crop)
                _canvas.CancelCrop();
            _session.CurrentTool = tool;
            if (tool != AnnotationTool.Select) _session.DeselectAll();
            _canvas.UpdateToolCursor();
            _canvas.Invalidate();
            _toolbar.Invalidate();
        };

        _toolbar.UndoRequested += () => { _session.UndoRedo.Undo(); _canvasContainer.SyncCanvasSize(); InvalidateAll(); };
        _toolbar.RedoRequested += () => { _session.UndoRedo.Redo(); _canvasContainer.SyncCanvasSize(); InvalidateAll(); };
        _toolbar.DeleteRequested += () => { _session.DeleteSelected(); _canvas.Invalidate(); };
        _toolbar.BringForwardRequested += () => { _session.BringForward(); _canvas.Invalidate(); };
        _toolbar.SendBackwardRequested += () => { _session.SendBackward(); _canvas.Invalidate(); };

        _toolbar.ColorPickerRequested += () =>
        {
            var pt = _toolbar.PointToScreen(new Point(_toolbar.Width / 2 - 170, _toolbar.Height));
            var picker = new ColorPickerPopup(_session.CurrentColor, pt);
            picker.ColorSelected += c =>
            {
                _session.CurrentColor = c;
                if (_session.SelectedObject != null)
                {
                    var obj = _session.SelectedObject;
                    var old = obj.StrokeColor;
                    _session.UndoRedo.Execute(new ModifyPropertyAction<Color>("Change color", v => obj.StrokeColor = v, old, c));
                }
                _canvas.Invalidate(); _toolbar.Invalidate();
            };
            picker.Show(this);
        };

        _toolbar.BgColorPickerRequested += () =>
        {
            var pt = _toolbar.PointToScreen(new Point(_toolbar.Width / 2 - 170, _toolbar.Height));
            var picker = new ColorPickerPopup(_session.CanvasBackgroundColor, pt);
            picker.ColorSelected += c => { _session.CanvasBackgroundColor = c; _canvas.Invalidate(); _toolbar.Invalidate(); };
            picker.Show(this);
        };

        _toolbar.ColorChanged += c =>
        {
            _session.CurrentColor = c;
            if (_session.SelectedObject != null)
            {
                var obj = _session.SelectedObject;
                var old = obj.StrokeColor;
                _session.UndoRedo.Execute(new ModifyPropertyAction<Color>("Change color", v => obj.StrokeColor = v, old, c));
                _canvas.Invalidate();
            }
        };

        _toolbar.ThicknessChanged += t =>
        {
            _session.CurrentThickness = t;
            if (_session.SelectedObject != null)
            {
                var obj = _session.SelectedObject;
                var old = obj.Thickness;
                _session.UndoRedo.Execute(new ModifyPropertyAction<float>("Change thickness", v => obj.Thickness = v, old, t));
                _canvas.Invalidate();
            }
        };

        _toolbar.FontSizeChanged += s =>
        {
            _session.CurrentFontSize = s;
            if (_session.SelectedObject is TextObject txt)
            {
                var old = txt.FontSize;
                _session.UndoRedo.Execute(new ModifyPropertyAction<float>("Change font size", v => txt.FontSize = v, old, s));
                _canvas.Invalidate();
            }
        };

        _toolbar.BorderToggled += () =>
        {
            _session.BorderEnabled = !_session.BorderEnabled;
            _canvas.Invalidate(); _toolbar.Invalidate();
        };

        _toolbar.BorderPopupRequested += () =>
        {
            var pt = _toolbar.PointToScreen(new Point(_toolbar.Width / 2 - 110, _toolbar.Height));
            var popup = new BorderPopup(_session, pt);
            popup.SettingsChanged += () => { _canvas.Invalidate(); _toolbar.Invalidate(); };
            popup.Show(this);
        };

        _toolbar.SaveAsRequested += DoSaveAs;
        _toolbar.CancelRequested += () => Close();

        _toolbar.ZoomInRequested += () => { _canvas.ZoomInStep(); _canvasContainer.CenterCanvas(); };
        _toolbar.ZoomOutRequested += () => { _canvas.ZoomOutStep(); _canvasContainer.CenterCanvas(); };
        _toolbar.ZoomResetRequested += () => { _canvas.ResetZoom(); _canvasContainer.CenterCanvas(); };
        _toolbar.ZoomFitRequested += () => { _canvas.FitToContainer(_canvasContainer.ClientSize); _canvasContainer.CenterCanvas(); };
        _toolbar.CompareRequested += DoCompare;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Control && e.Shift && e.KeyCode == Keys.S) { DoSaveAs(); e.Handled = true; return; }
        if (e.Control && e.Shift && e.KeyCode == Keys.C) { DoCopyPath(); e.Handled = true; return; }
        if (e.Control && e.Shift && e.KeyCode == Keys.P) { DoCopyPathText(); e.Handled = true; return; }
        if (e.Control && e.Shift && e.KeyCode == Keys.K) { DoCompare(); e.Handled = true; return; }
        if (e.Control && e.KeyCode == Keys.C) { DoCopy(); e.Handled = true; return; }
        if (e.Control && e.KeyCode == Keys.Z) { _session.UndoRedo.Undo(); _canvasContainer.SyncCanvasSize(); _canvas.Invalidate(); e.Handled = true; return; }
        if (e.Control && e.KeyCode == Keys.Y) { _session.UndoRedo.Redo(); _canvasContainer.SyncCanvasSize(); _canvas.Invalidate(); e.Handled = true; return; }
        // Zoom shortcuts (work with both =/+ on top row and numpad +)
        if (e.Control && (e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add)) { _canvas.ZoomInStep(); _canvasContainer.CenterCanvas(); e.Handled = true; return; }
        if (e.Control && (e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract)) { _canvas.ZoomOutStep(); _canvasContainer.CenterCanvas(); e.Handled = true; return; }
        if (e.Control && e.KeyCode == Keys.D0) { _canvas.ResetZoom(); _canvasContainer.CenterCanvas(); e.Handled = true; return; }
        if (e.Control && e.KeyCode == Keys.D9) { _canvas.FitToContainer(_canvasContainer.ClientSize); _canvasContainer.CenterCanvas(); e.Handled = true; return; }
        // Skip Delete/Escape and single-key shortcuts while editing text
        bool isEditingText = _session.Annotations.Any(a => a is TextObject { IsEditing: true });

        if (e.KeyCode == Keys.Delete && !isEditingText)
        {
            if (_canvas.HasSelectedRegion)
                _canvas.DeleteSelectedRegion();
            else
                _session.DeleteSelected();
            _canvas.Invalidate();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.Enter && _session.CurrentTool == AnnotationTool.Crop)
        {
            _canvas.ApplyCrop();
            _canvasContainer.SyncCanvasSize();
            e.Handled = true; return;
        }
        if (e.KeyCode == Keys.Escape)
        {
            if (_session.CurrentTool == AnnotationTool.Crop) { _canvas.CancelCrop(); _canvas.Invalidate(); e.Handled = true; return; }
            if (isEditingText) { _canvas.CommitTextEdit(); _canvas.Invalidate(); e.Handled = true; return; }
            if (_session.SelectedObject != null) { _session.DeselectAll(); _canvas.Invalidate(); e.Handled = true; return; }
            if (_canvas.HasSelectedRegion) { _canvas.ClearSelectedRegion(); e.Handled = true; return; }
            Close(); e.Handled = true; return;
        }

        if (!e.Control && !e.Alt && !isEditingText)
        {
            AnnotationTool? tool = e.KeyCode switch
            {
                Keys.V => AnnotationTool.Select, Keys.P => AnnotationTool.Pen,
                Keys.M => AnnotationTool.Marker, Keys.L => AnnotationTool.Line,
                Keys.A => AnnotationTool.Arrow, Keys.R => AnnotationTool.Rectangle,
                Keys.E => AnnotationTool.Ellipse, Keys.T => AnnotationTool.Text,
                Keys.K => AnnotationTool.MaskBox,
                Keys.X => AnnotationTool.Eraser,
                Keys.C => AnnotationTool.Crop,
                _ => null,
            };
            if (e.KeyCode == Keys.B)
            {
                _session.BorderEnabled = !_session.BorderEnabled;
                _canvas.Invalidate(); _toolbar.Invalidate();
                e.Handled = true; return;
            }
            if (tool != null)
            {
                _canvas.CommitTextEdit();
                _session.CurrentTool = tool.Value;
                if (tool != AnnotationTool.Select) _session.DeselectAll();
                _canvas.UpdateToolCursor(); _canvas.Invalidate(); _toolbar.Invalidate();
                e.Handled = true; return;
            }
        }
        base.OnKeyDown(e);
    }

    private void ScheduleAutoSave()
    {
        if (_autoSaveTimer == null) return;
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void AutoSaveNow()
    {
        try
        {
            _autoSaveTimer?.Stop();
            SessionSerializer.Save(_session);
            ScreenshotExporter.SaveToFile(_session, GetLinkedSavePath());
            SavePreview();
        }
        catch (Exception ex) { Debug.WriteLine($"AutoSave failed: {ex.Message}"); }
    }

    private void DoSaveAs()
    {
        try
        {
            _canvas.CommitTextEdit();
            using var dlg = new SaveFileDialog
            {
                Filter = "PNG Image|*.png|JPEG Image|*.jpg|Bitmap|*.bmp",
                DefaultExt = "png",
                FileName = $"screenshot_{DateTime.Now:yyyy-MM-dd_HHmmss}",
                InitialDirectory = ScreenshotPaths.ScreenshotsDir,
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                ScreenshotExporter.SaveToFile(_session, dlg.FileName);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Save As failed: {ex.Message}");
            MessageBox.Show(this, $"Save failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void DoCopy()
    {
        try
        {
            _canvas.CommitTextEdit();
            ScreenshotExporter.CopyToClipboard(_session);
            WindowState = FormWindowState.Minimized;
        }
        catch (Exception ex) { Debug.WriteLine($"Copy failed: {ex.Message}"); }
    }

    private void DoCopyPath()
    {
        try
        {
            _canvas.CommitTextEdit();
            AutoSaveNow();
            string path = GetLinkedSavePath();
            var fileList = new System.Collections.Specialized.StringCollection();
            fileList.Add(path);
            Clipboard.SetFileDropList(fileList);
            WindowState = FormWindowState.Minimized;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Copy Path failed: {ex.Message}");
            MessageBox.Show(this, $"Copy Path failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void DoCopyPathText()
    {
        try
        {
            _canvas.CommitTextEdit();
            AutoSaveNow();
            Clipboard.SetText(GetLinkedSavePath());
            WindowState = FormWindowState.Minimized;
        }
        catch (Exception ex) { Debug.WriteLine($"Copy Path Text failed: {ex.Message}"); }
    }

    /// <summary>
    /// Decides which two screenshot files to compare, based on items the user
    /// right-clicked to mark in <see cref="RecentImagesPanel"/>. Returns the
    /// paths newest-first (left = older, right = newer) for the fallback and
    /// single-marked cases; for the two-marked case the marking order is used
    /// (first marked = left, second marked = right).
    /// </summary>
    private bool PickComparePaths(out string leftPath, out string rightPath)
    {
        leftPath = rightPath = "";
        var marked = _recentPanel.CompareMarked;
        var visible = _recentPanel.VisibleFilePaths;

        if (marked.Count == 2)
        {
            leftPath = marked[0];
            rightPath = marked[1];
            return true;
        }

        if (marked.Count == 1)
        {
            string target = marked[0];
            int idx = visible.ToList().IndexOf(target);
            // visible is newest-first: prev-in-time = idx+1 (older), next-in-time = idx-1 (newer).
            string? older = idx >= 0 && idx + 1 < visible.Count ? visible[idx + 1] : null;
            string? newer = idx >= 1 ? visible[idx - 1] : null;
            if (older != null) { leftPath = older; rightPath = target; return true; }
            if (newer != null) { leftPath = target; rightPath = newer; return true; }

            MessageBox.Show(this,
                "Only one screenshot is available, so there is nothing to compare it with. " +
                "Capture another screenshot, or mark a second file as a compare item, and try again.",
                "Compare", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        // No marks — fall back to the most recent two screenshots.
        if (visible.Count < 2)
        {
            MessageBox.Show(this,
                visible.Count == 0
                    ? "There are no screenshots to compare yet. Capture two screenshots first."
                    : "Only one screenshot is available. Capture another one, or mark two files via right-click → Set as compare item.",
                "Compare", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        leftPath = visible[1];
        rightPath = visible[0];
        return true;
    }

    private void DoCompare()
    {
        try
        {
            if (!PickComparePaths(out string leftPath, out string rightPath))
                return;

            // Load both images
            Bitmap imgRight, imgLeft;
            using (var s1 = File.OpenRead(rightPath))
            using (var b1 = new Bitmap(s1))
            {
                imgRight = new Bitmap(b1.Width, b1.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(imgRight);
                g.DrawImage(b1, 0, 0);
            }
            using (var s2 = File.OpenRead(leftPath))
            using (var b2 = new Bitmap(s2))
            {
                imgLeft = new Bitmap(b2.Width, b2.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(imgLeft);
                g.DrawImage(b2, 0, 0);
            }

            // Layout: padding + imgLeft + gap + imgRight + padding
            int padding = 30;
            int gap = 20;
            int canvasW = padding + imgLeft.Width + gap + imgRight.Width + padding;
            int canvasH = padding + Math.Max(imgLeft.Height, imgRight.Height) + padding;
            float maxH = Math.Max(imgLeft.Height, imgRight.Height);

            // Create a blank canvas (the base image — just the background)
            var baseBmp = new Bitmap(canvasW, canvasH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(baseBmp))
                g.Clear(Color.FromArgb(240, 240, 240));

            // Save current session before switching
            _canvas.CommitTextEdit();
            AutoSaveNow();
            _session.OriginalImage.Dispose();

            // Create new session with blank base
            _session = new EditorSession(baseBmp);
            InitEditFolder(_session, baseBmp);

            // Save both layer images into the _edits folder so they persist
            string editDir = _session.EditDir;
            Directory.CreateDirectory(editDir);

            string leftLayerPath = Path.Combine(editDir, $"layer_compare_left.png");
            imgLeft.Save(leftLayerPath, System.Drawing.Imaging.ImageFormat.Png);

            string rightLayerPath = Path.Combine(editDir, $"layer_compare_right.png");
            imgRight.Save(rightLayerPath, System.Drawing.Imaging.ImageFormat.Png);

            // Add both as ImageObject annotation layers with SourcePath set
            float leftY = padding + (maxH - imgLeft.Height) / 2f;
            var leftObj = new ImageObject
            {
                Image = imgLeft,
                Position = new PointF(padding, leftY),
                DisplaySize = new SizeF(imgLeft.Width, imgLeft.Height),
                SourcePath = leftLayerPath,
            };
            _session.AddAnnotation(leftObj);

            float rightX = padding + imgLeft.Width + gap;
            float rightY = padding + (maxH - imgRight.Height) / 2f;
            var rightObj = new ImageObject
            {
                Image = imgRight,
                Position = new PointF(rightX, rightY),
                DisplaySize = new SizeF(imgRight.Width, imgRight.Height),
                SourcePath = rightLayerPath,
            };
            _session.AddAnnotation(rightObj);

            Text = $"ProdToy \u2014 Compare";

            // Replace canvas in same window
            _canvas = new ScreenshotCanvas
            {
                Size = new Size(_session.CanvasSize.Width, _session.CanvasSize.Height),
                Session = _session,
            };
            _canvasContainer.SetCanvas(_canvas);

            // Re-wire events
            _canvas.CanvasResizeRequested += _ => _canvasContainer.SyncCanvasSize();
            _canvas.ToolAutoSwitched += () => _toolbar.Invalidate();
            _toolbar.ZoomProvider = () => _canvas.Zoom;
            _canvas.ZoomChanged += () => _toolbar.Invalidate();
            _session.UndoRedo.StateChanged += ScheduleAutoSave;
            _canvas.CanvasChanged += ScheduleAutoSave;

            _toolbar.Session = _session;
            _toolbar.Invalidate();
            _recentPanel.SetEditingId(_session.EditId);
            ResizeFormToFitCanvas();

            // Immediately save so state.json, preview, and layer files are all written
            AutoSaveNow();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Compare failed: {ex.Message}");
            MessageBox.Show(this, $"Compare failed: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Create the _edits folder for this capture and save the base image.</summary>
    private static void InitEditFolder(EditorSession session, Bitmap capturedImage)
    {
        try
        {
            string editId = $"screenshot_{DateTime.Now:yyyy-MM-dd_HHmmss}";
            session.EditId = editId;
            string dir = session.EditDir;
            Directory.CreateDirectory(dir);
            capturedImage.Save(Path.Combine(dir, "base.png"), System.Drawing.Imaging.ImageFormat.Png);

            // Also save to screenshots/ so it appears in the recent list immediately
            Directory.CreateDirectory(ScreenshotPaths.ScreenshotsDir);
            capturedImage.Save(Path.Combine(ScreenshotPaths.ScreenshotsDir, editId + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"InitEditFolder failed: {ex.Message}");
        }
    }

    /// <summary>Get the save path in screenshots/ linked to the _edits folder name.</summary>
    private string GetLinkedSavePath()
    {
        Directory.CreateDirectory(ScreenshotPaths.ScreenshotsDir);
        return Path.Combine(ScreenshotPaths.ScreenshotsDir, _session.EditId + ".png");
    }

    /// <summary>Load an existing file into the editor, reusing the form.</summary>
    public void LoadFile(string filePath)
    {
        string fileEditId = Path.GetFileNameWithoutExtension(filePath);
        if (fileEditId.Equals(_session.EditId, StringComparison.OrdinalIgnoreCase))
        {
            BringToForeground();
            return;
        }
        LoadSession(fileEditId, filePath);
        BringToForeground();
    }

    /// <summary>Load a new capture into the editor, reusing the form.</summary>
    public void LoadCapture(Bitmap capturedImage)
    {
        var tempSession = new EditorSession(capturedImage);
        InitEditFolder(tempSession, capturedImage);
        LoadSession(tempSession.EditId, GetLinkedSavePathFor(tempSession.EditId));
        BringToForeground();
    }

    public void BringToForeground()
    {
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Show();
        Activate();
        BringToFront();
    }

    private static string GetLinkedSavePathFor(string editId)
    {
        return Path.Combine(ScreenshotPaths.ScreenshotsDir, editId + ".png");
    }

    private void OpenFromList(string filePath)
    {
        string fileEditId = Path.GetFileNameWithoutExtension(filePath);
        if (fileEditId.Equals(_session.EditId, StringComparison.OrdinalIgnoreCase)) return;
        LoadSession(fileEditId, filePath);
    }

    private void LoadSession(string fileEditId, string filePath)
    {
        try
        {
            // Save current session
            _canvas.CommitTextEdit();
            AutoSaveNow();
            _session.OriginalImage.Dispose();

            // Load the selected image from its _edits/base.png if available, otherwise from the file
            string editDir = Path.Combine(ScreenshotPaths.ScreenshotsEditsDir, fileEditId);
            string basePath = Path.Combine(editDir, "base.png");
            string loadFrom = File.Exists(basePath) ? basePath : filePath;

            Bitmap newImage;
            using (var stream = File.OpenRead(loadFrom))
            using (var bmp = new Bitmap(stream))
            {
                newImage = new Bitmap(bmp.Width, bmp.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(newImage);
                g.DrawImage(bmp, 0, 0);
            }

            // Create _edits folder if it doesn't exist
            if (!Directory.Exists(editDir))
            {
                Directory.CreateDirectory(editDir);
                newImage.Save(Path.Combine(editDir, "base.png"), System.Drawing.Imaging.ImageFormat.Png);
            }

            // Create new session
            _session = new EditorSession(newImage) { EditId = fileEditId };

            // If _edits folder has state.json, restore annotations and settings
            SessionSerializer.Restore(_session);

            // Replace canvas via container (handles remove/add/center)
            _canvas = new ScreenshotCanvas
            {
                Size = new Size(_session.CanvasSize.Width, _session.CanvasSize.Height),
                Session = _session,
            };
            _canvasContainer.SetCanvas(_canvas);

            // Wire auto-save for new session
            _session.UndoRedo.StateChanged += ScheduleAutoSave;
            _canvas.CanvasChanged += ScheduleAutoSave;
            _canvas.ToolAutoSwitched += () => _toolbar.Invalidate();

            _toolbar.Session = _session;
            _toolbar.Invalidate();
            _recentPanel.SetEditingId(_session.EditId);
            ResizeFormToFitCanvas();

            Text = $"ProdToy \u2014 {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OpenFromList failed: {ex.Message}");
            MessageBox.Show(this, $"Failed to open: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ResizeFormToFitCanvas()
    {
        int imgW = _session.CanvasSize.Width;
        int imgH = _session.CanvasSize.Height;
        int extraPad = 80;
        int recentPanelWidth = 170;
        int toolbarAreaHeight = 60;
        int pad = 8;

        int clientW = Math.Max(imgW + extraPad * 2 + recentPanelWidth, 600);
        int clientH = imgH + toolbarAreaHeight + extraPad + pad;

        var screen = Screen.FromControl(this).WorkingArea;
        clientW = Math.Min(clientW, screen.Width - 40);
        clientH = Math.Min(clientH, screen.Height - 40);

        ClientSize = new Size(clientW, clientH);

        // Re-center on screen
        Location = new Point(
            screen.X + (screen.Width - Width) / 2,
            screen.Y + (screen.Height - Height) / 2);

        _canvasContainer.SyncCanvasSize();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try
        {
            _canvas.CommitTextEdit();
            AutoSaveNow();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Auto-save on close failed: {ex.Message}");
        }

        // Hide instead of dispose — reuse the form next time
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _autoSaveTimer?.Dispose();
        base.OnFormClosing(e);
    }

    private void SavePreview()
    {
        try
        {
            string dir = _session.EditDir;
            if (!Directory.Exists(dir)) return;
            using var preview = ScreenshotExporter.Flatten(_session);
            preview.Save(Path.Combine(dir, "preview.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SavePreview failed: {ex.Message}");
        }
    }

    private void InvalidateAll()
    {
        SuspendLayout();
        _canvas.Invalidate();
        _toolbar.Invalidate();
        ResumeLayout();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Dispose all ImageObject bitmaps to prevent memory leaks
        foreach (var obj in _session.Annotations)
        {
            if (obj is ImageObject imgObj)
                imgObj.Dispose();
        }
        _session.OriginalImage.Dispose();
        FontPool.Clear();
        base.OnFormClosed(e);
    }
}
