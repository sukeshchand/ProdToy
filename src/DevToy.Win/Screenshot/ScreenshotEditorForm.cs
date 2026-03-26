using System.Diagnostics;
using System.Drawing;

namespace DevToy;

class ScreenshotEditorForm : Form
{
    private readonly EditorSession _session;
    private readonly ScreenshotCanvas _canvas;
    private readonly CanvasContainer _canvasContainer;
    private readonly ScreenshotToolbar _toolbar;
    private readonly RecentImagesPanel _recentPanel;
    private readonly PopupTheme _theme;

    public event Action<string>? ImageSaved;
    public event Action? ImageCopied;

    public ScreenshotEditorForm(Bitmap capturedImage)
    {
        _session = new EditorSession(capturedImage);
        SaveToTemp(_session, capturedImage);
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
            string path = ScreenshotExporter.SaveToFile(_session);
            CleanupTemp();
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
                CleanupTemp();
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
            ScreenshotExporter.CopyToClipboard(_session);
            CleanupTemp();
            ImageCopied?.Invoke();
            Close();
        }
        catch (Exception ex) { Debug.WriteLine($"Copy failed: {ex.Message}"); }
    }

    private void DoCopyPath()
    {
        try
        {
            _canvas.CommitTextEdit();
            string path = ScreenshotExporter.SaveToFile(_session);
            CleanupTemp();
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

    /// <summary>Save the captured image into a temp folder with preview + base image.</summary>
    private static void SaveToTemp(EditorSession session, Bitmap capturedImage)
    {
        try
        {
            string id = DateTime.Now.ToString("yyyyMMdd_HHmmss_") + Guid.NewGuid().ToString("N")[..6];
            session.TempId = id;
            string dir = Path.Combine(AppPaths.ScreenshotsTempDir, id);
            Directory.CreateDirectory(dir);

            // Base image (original capture)
            capturedImage.Save(Path.Combine(dir, "base.png"), System.Drawing.Imaging.ImageFormat.Png);

            // Preview (same as base initially)
            capturedImage.Save(Path.Combine(dir, "preview.jpg"), System.Drawing.Imaging.ImageFormat.Jpeg);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SaveToTemp failed: {ex.Message}");
        }
    }

    /// <summary>Delete the temp folder for the current session.</summary>
    private void CleanupTemp()
    {
        if (string.IsNullOrEmpty(_session.TempId)) return;
        try
        {
            string dir = Path.Combine(AppPaths.ScreenshotsTempDir, _session.TempId);
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CleanupTemp failed: {ex.Message}");
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _session.OriginalImage.Dispose();
        base.OnFormClosed(e);
    }
}
