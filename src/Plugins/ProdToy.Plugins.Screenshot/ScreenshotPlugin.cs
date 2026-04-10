using ProdToy.Sdk;

namespace ProdToy.Plugins.Screenshot;

[Plugin("ProdToy.Plugin.Screenshot", "Screenshot", "1.0.244",
    Description = "Screen capture and annotation editor",
    Author = "ProdToy",
    MenuPriority = 100)]
public class ScreenshotPlugin : IPlugin
{
    private IPluginContext _context = null!;
    private IHotkeyRegistration? _hotkeyReg;
    private IDisposable? _tripleCtrlReg;
    private ScreenshotEditorForm? _editorForm;

    // Static accessor for theme (used by ScreenshotEditorForm internally)
    private static IPluginHost? _host;
    internal static PluginTheme GetTheme() => _host?.CurrentTheme ?? new PluginTheme(
        "Default", default, default, default, default, default, default, default, default, default, default);

    public void Initialize(IPluginContext context)
    {
        _context = context;
        _host = context.Host;
        ScreenshotPaths.Initialize(context.DataDirectory);
    }

    public void Start()
    {
        var settings = _context.LoadSettings<ScreenshotPluginSettings>();
        if (!settings.ScreenshotEnabled) return;

        // Register global hotkey
        if (!string.IsNullOrEmpty(settings.ScreenshotHotkey))
        {
            _hotkeyReg = _context.Host.RegisterHotkey(settings.ScreenshotHotkey, () =>
                _context.Host.InvokeOnUI(TakeScreenshot));
        }

        // Register triple-Ctrl
        if (settings.TripleCtrlEnabled)
        {
            _tripleCtrlReg = _context.Host.RegisterTripleCtrl(() =>
                _context.Host.InvokeOnUI(EditLastScreenshot));
        }
    }

    public void Stop()
    {
        _hotkeyReg?.Dispose();
        _hotkeyReg = null;
        _tripleCtrlReg?.Dispose();
        _tripleCtrlReg = null;
        _context.Host.InvokeOnUI(() =>
        {
            if (_editorForm != null && !_editorForm.IsDisposed)
            {
                _editorForm.Dispose();
                _editorForm = null;
            }
        });
    }

    public void Dispose() { }

    public IReadOnlyList<MenuContribution> GetMenuItems()
    {
        var settings = _context.LoadSettings<ScreenshotPluginSettings>();
        bool enabled = settings.ScreenshotEnabled;
        return
        [
            new("Take Screenshot", TakeScreenshot, Priority: 100, Visible: enabled),
            new("Edit Last Screenshot", EditLastScreenshot, Priority: 101, Visible: enabled),
        ];
    }

    public SettingsPageContribution? GetSettingsPage() => null;

    private void TakeScreenshot()
    {
        var overlay = new ScreenshotOverlay();
        overlay.RegionCaptured += bitmap => EnsureEditor(bitmap);
        overlay.Show();
    }

    private void EditLastScreenshot()
    {
        try
        {
            string dir = ScreenshotPaths.ScreenshotsDir;
            if (!Directory.Exists(dir)) return;

            var lastFile = Directory.GetFiles(dir, "*.png")
                .Concat(Directory.GetFiles(dir, "*.jpg"))
                .Concat(Directory.GetFiles(dir, "*.bmp"))
                .OrderByDescending(File.GetCreationTime)
                .FirstOrDefault();

            if (lastFile == null) return;
            EnsureEditor(lastFile);
        }
        catch (Exception ex)
        {
            _context.LogError("EditLastScreenshot failed", ex);
        }
    }

    private void EnsureEditor(Bitmap capturedImage)
    {
        if (_editorForm == null || _editorForm.IsDisposed)
        {
            _editorForm = new ScreenshotEditorForm(capturedImage);
            _editorForm.BringToForeground();
        }
        else
        {
            _editorForm.LoadCapture(capturedImage);
        }
    }

    private void EnsureEditor(string filePath)
    {
        if (_editorForm == null || _editorForm.IsDisposed)
        {
            _editorForm = new ScreenshotEditorForm(filePath);
            _editorForm.BringToForeground();
        }
        else
        {
            _editorForm.LoadFile(filePath);
        }
    }
}
