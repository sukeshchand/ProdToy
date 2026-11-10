using ProdToy.Sdk;

namespace ProdToy.Plugins.Alarm;

/// <summary>Builds ring windows with the host's theme, font and web view folder - for a firing
/// (through the notifier) and for a preview (MCP alarm_preview, the edit form's Preview button).</summary>
static class AlarmRingLauncher
{
    private static IPluginHost? _host;

    public static void Initialize(IPluginHost host) => _host = host;

    public static AlarmRingForm Create(AlarmEntry alarm, bool preview,
        AlarmBodyKind? kindOverride = null, string? contentOverride = null)
    {
        var theme = _host?.CurrentTheme ?? FallbackTheme;
        AlarmBodyContent content;
        try
        {
            content = AlarmBodyView.Prepare(AlarmStore.Bodies, alarm, theme, kindOverride, contentOverride);
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"Alarm body could not be prepared for '{alarm.Title}': {ex.Message}");
            content = new AlarmBodyContent(AlarmBodyKind.Text, alarm.Message, null, null, null);
        }

        string? webViewFolder = null;
        try { webViewFolder = _host?.GetWebView2UserDataFolder("alarm-ring"); }
        catch (Exception ex) { PluginLog.Warn($"No web view folder for the alarm window: {ex.Message}"); }

        var placement = string.IsNullOrEmpty(AlarmStore.DataDirectory) ? null : new RingWindowPlacement(AlarmStore.DataDirectory);
        return new AlarmRingForm(alarm, theme, _host?.GlobalFont ?? "", content, webViewFolder, placement, preview);
    }

    /// <summary>Opens the ring window for an alarm without firing it: no sound, no history row.</summary>
    public static void ShowPreview(AlarmEntry alarm, AlarmBodyKind? kindOverride = null, string? contentOverride = null, IWin32Window? owner = null)
    {
        var form = Create(alarm, preview: true, kindOverride, contentOverride);
        if (owner != null) form.Show(owner);
        else form.Show();
        form.Activate();
    }

    private static PluginTheme FallbackTheme => new(
        Name: "fallback",
        BgDark: System.Drawing.Color.FromArgb(0x1e, 0x1e, 0x24),
        BgHeader: System.Drawing.Color.FromArgb(0x2a, 0x2a, 0x32),
        Primary: System.Drawing.Color.FromArgb(0x5f, 0xc7, 0x6e),
        PrimaryLight: System.Drawing.Color.FromArgb(0x7f, 0xd7, 0x8e),
        PrimaryDim: System.Drawing.Color.FromArgb(0x3a, 0x5a, 0x4a),
        TextPrimary: System.Drawing.Color.FromArgb(0xe6, 0xe6, 0xee),
        TextSecondary: System.Drawing.Color.FromArgb(0x9a, 0x9a, 0xa8),
        Border: System.Drawing.Color.FromArgb(0x3a, 0x3a, 0x44),
        SuccessColor: System.Drawing.Color.FromArgb(0x5f, 0xc7, 0x6e),
        ErrorColor: System.Drawing.Color.FromArgb(0xe7, 0x48, 0x56),
        SuccessBg: System.Drawing.Color.FromArgb(0x24, 0x3a, 0x2a),
        ErrorBg: System.Drawing.Color.FromArgb(0x3a, 0x24, 0x24));
}
