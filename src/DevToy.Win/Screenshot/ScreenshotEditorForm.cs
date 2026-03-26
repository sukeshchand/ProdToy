using System.Diagnostics;
using System.Drawing;

namespace DevToy;

class ScreenshotEditorForm : Form
{
    private EditorSession _session;
    private ScreenshotCanvas _canvas;
    private CanvasContainer _canvasContainer;
    private readonly ScreenshotToolbar _toolbar;
    private readonly RecentImagesPanel _recentPanel;
    private readonly PopupTheme _theme;

    public event Action<string>? ImageSaved;
    public event Action? ImageCopied;

    public ScreenshotEditorForm(Bitmap capturedImage)
    {
        _session = new EditorSession(capturedImage);
        InitEditFolder(_session, capturedImage);
        _theme = Themes.LoadSaved();

        Text = "DevToy \u2014 Screenshot Editor";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        ShowInTaskbar = true;
        TopMost = true;
        KeyPreview = true;
        MinimumSize = new Size(500, 350);
        Icon = Themes.CreateAppIcon(_theme.Primary);
        BackColor = _theme.BgDark;
        AutoScaleMode = AutoScaleMode.Dpi;

        int toolbarAreaHeight = 60;
        int pad = 8;
        int imgW = capturedImage.Width;
        int imgH = capturedImage.Height;
        int clientW = Math.Max(imgW + pad * 2, 600);
        int clientH = imgH + toolbarAreaHeight + pad * 2;
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

        // Auto-save state to _edits folder on every undo/redo action
        _session.UndoRedo.StateChanged += () => SessionSerializer.Save(_session);
    }

    private void WireToolbarEvents()
    {
        _toolbar.QuickCopyRequested += DoCopy;
        _toolbar.CopyPathRequested += DoCopyPath;

        _toolbar.ToolSelected += tool =>
        {
            _canvas.CommitTextEdit();
            _session.CurrentTool = tool;
            if (tool != AnnotationTool.Select) _session.DeselectAll();
            _canvas.UpdateToolCursor();
            _canvas.Invalidate();
            _toolbar.Invalidate();
        };

        _toolbar.UndoRequested += () => { _session.UndoRedo.Undo(); _canvas.Invalidate(); _toolbar.Invalidate(); };
        _toolbar.RedoRequested += () => { _session.UndoRedo.Redo(); _canvas.Invalidate(); _toolbar.Invalidate(); };
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
            var pt = _toolbar.PointToScreen(new Point(_toolbar.Width / 2 - 110, _toolbar.Height));
            var popup = new BorderPopup(_session, pt);
            popup.SettingsChanged += () => { _canvas.Invalidate(); _toolbar.Invalidate(); };
            popup.Show(this);
        };

        _toolbar.SaveRequested += DoSave;
        _toolbar.SaveAsRequested += DoSaveAs;
        _toolbar.CopyRequested += DoCopy;
        _toolbar.CancelRequested += () => Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Control && e.Shift && e.KeyCode == Keys.S) { DoSaveAs(); e.Handled = true; return; }
        if (e.Control && e.Shift && e.KeyCode == Keys.C) { DoCopyPath(); e.Handled = true; return; }
        if (e.Control && e.KeyCode == Keys.S) { DoSave(); e.Handled = true; return; }
        if (e.Control && e.KeyCode == Keys.C) { DoCopy(); e.Handled = true; return; }
        if (e.Control && e.KeyCode == Keys.Z) { _session.UndoRedo.Undo(); _canvas.Invalidate(); e.Handled = true; return; }
        if (e.Control && e.KeyCode == Keys.Y) { _session.UndoRedo.Redo(); _canvas.Invalidate(); e.Handled = true; return; }
        if (e.KeyCode == Keys.Delete) { _session.DeleteSelected(); _canvas.Invalidate(); e.Handled = true; return; }
        if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; return; }

        if (!e.Control && !e.Alt)
        {
            AnnotationTool? tool = e.KeyCode switch
            {
                Keys.V => AnnotationTool.Select, Keys.P => AnnotationTool.Pen,
                Keys.M => AnnotationTool.Marker, Keys.L => AnnotationTool.Line,
                Keys.A => AnnotationTool.Arrow, Keys.R => AnnotationTool.Rectangle,
                Keys.E => AnnotationTool.Ellipse, _ => null,
            };
            if (e.KeyCode == Keys.T && _session.Annotations.All(a => a is not TextObject { IsEditing: true }))
                tool = AnnotationTool.Text;
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

    private void DoSave()
    {
        try
        {
            _canvas.CommitTextEdit();
            string path = GetLinkedSavePath();
            ScreenshotExporter.SaveToFile(_session, path);
            ImageSaved?.Invoke(path);
            Close();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Save failed: {ex.Message}");
            MessageBox.Show(this, $"Save failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
                InitialDirectory = AppPaths.ScreenshotsDir,
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                ScreenshotExporter.SaveToFile(_session, dlg.FileName);
    
                ImageSaved?.Invoke(dlg.FileName);
                Close();
            }
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
            string path = GetLinkedSavePath();
            ScreenshotExporter.SaveToFile(_session, path);
            ScreenshotExporter.CopyToClipboard(_session);
            ImageSaved?.Invoke(path);
            Close();
        }
        catch (Exception ex) { Debug.WriteLine($"Copy failed: {ex.Message}"); }
    }

    private void DoCopyPath()
    {
        try
        {
            _canvas.CommitTextEdit();
            string path = GetLinkedSavePath();
            ScreenshotExporter.SaveToFile(_session, path);
            var fileList = new System.Collections.Specialized.StringCollection();
            fileList.Add(path);
            Clipboard.SetFileDropList(fileList);
            ImageSaved?.Invoke(path);
            Close();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Copy Path failed: {ex.Message}");
            MessageBox.Show(this, $"Copy Path failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            Directory.CreateDirectory(AppPaths.ScreenshotsDir);
            capturedImage.Save(Path.Combine(AppPaths.ScreenshotsDir, editId + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"InitEditFolder failed: {ex.Message}");
        }
    }

    /// <summary>Get the save path in screenshots/ linked to the _edits folder name.</summary>
    private string GetLinkedSavePath()
    {
        Directory.CreateDirectory(AppPaths.ScreenshotsDir);
        return Path.Combine(AppPaths.ScreenshotsDir, _session.EditId + ".png");
    }

    private void OpenFromList(string filePath)
    {
        // Don't reload the same image
        string fileEditId = Path.GetFileNameWithoutExtension(filePath);
        if (fileEditId.Equals(_session.EditId, StringComparison.OrdinalIgnoreCase)) return;

        if (MessageBox.Show(this,
            "Save current edits and open the selected image?",
            "Open Image",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1) != DialogResult.Yes)
            return;

        try
        {
            // Save current session state + final image
            _canvas.CommitTextEdit();
            ScreenshotExporter.SaveToFile(_session, GetLinkedSavePath());
            SessionSerializer.Save(_session);
            SavePreview();
            _session.OriginalImage.Dispose();

            // Load the selected image from its _edits/base.png if available, otherwise from the file
            string editDir = Path.Combine(AppPaths.ScreenshotsEditsDir, fileEditId);
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

            // Create new session
            _session = new EditorSession(newImage) { EditId = fileEditId };

            // If _edits folder has state.json, restore annotations and settings
            if (Directory.Exists(editDir))
                SessionSerializer.Restore(_session);

            // Replace canvas
            _canvasContainer.Controls.Remove(_canvas);
            _canvas = new ScreenshotCanvas
            {
                Size = new Size(_session.CanvasSize.Width, _session.CanvasSize.Height),
                Session = _session,
            };
            _canvasContainer.Controls.Add(_canvas);
            _canvasContainer.CenterCanvas();

            // Auto-save on undo/redo for new session
            _session.UndoRedo.StateChanged += () => SessionSerializer.Save(_session);

            _toolbar.Session = _session;
            _toolbar.Invalidate();
            _recentPanel.SetEditingId(_session.EditId);

            Text = $"DevToy \u2014 {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OpenFromList failed: {ex.Message}");
            MessageBox.Show(this, $"Failed to open: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try
        {
            _canvas.CommitTextEdit();

            // Save final image to screenshots/
            string path = GetLinkedSavePath();
            ScreenshotExporter.SaveToFile(_session, path);

            // Save state + preview to _edits folder
            SessionSerializer.Save(_session);
            SavePreview();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Auto-save on close failed: {ex.Message}");
        }
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

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _session.OriginalImage.Dispose();
        base.OnFormClosed(e);
    }
}
