using System.Drawing;
using System.Text.Json;
using ProdToy.Sdk;
using ProdToy.Sdk.Rendering;

namespace ProdToy.Plugins.Alarm;

/// <summary>What the ring window shows in its body area.</summary>
/// <param name="Kind">The body kind.</param>
/// <param name="Text">Text kind: the text to show (also the fallback when a file is missing).</param>
/// <param name="Html">Markdown kind: the rendered page.</param>
/// <param name="Folder">The alarm's folder, when it has one (resources load from it).</param>
/// <param name="EntryFile">Html kind: the body file's name inside the folder.</param>
sealed record AlarmBodyContent(AlarmBodyKind Kind, string Text, string? Html, string? Folder, string? EntryFile);

/// <summary>
/// Turns an alarm (or an unsaved body from the edit form) into <see cref="AlarmBodyContent"/>.
/// The ring window serves the result from a private https host so relative images resolve.
/// </summary>
static class AlarmBodyView
{
    public const string VirtualHost = "alarm-body.invalid";
    public const string MarkdownPage = "__alarm_view.html";

    public static string BaseUrl => $"https://{VirtualHost}/";

    public static AlarmBodyContent Prepare(AlarmBodyFiles files, AlarmEntry alarm, PluginTheme theme,
        AlarmBodyKind? kindOverride = null, string? contentOverride = null)
    {
        var kind = kindOverride ?? alarm.BodyKind;
        string? folder = AlarmBodyFiles.IsSafeId(alarm.Id) && files.HasFolder(alarm.Id) ? files.FolderFor(alarm.Id) : null;

        if (kind == AlarmBodyKind.Text)
            return new AlarmBodyContent(AlarmBodyKind.Text, contentOverride ?? alarm.Message, null, folder, null);

        string? content = contentOverride ?? (alarm.BodyKind == kind ? files.ReadBody(alarm) : null);
        if (content == null)
        {
            string missing = $"The body file is missing ({AlarmBodyFiles.FileNameFor(kind)}).";
            string text = string.IsNullOrWhiteSpace(alarm.Message) ? missing : alarm.Message + Environment.NewLine + Environment.NewLine + missing;
            return new AlarmBodyContent(AlarmBodyKind.Text, text, null, folder, null);
        }

        if (kind == AlarmBodyKind.Markdown)
            return new AlarmBodyContent(kind, content, RenderMarkdown(content, theme), folder, null);

        // Html: a saved body is served from its file; an unsaved one from memory as the page.
        bool fromFile = contentOverride == null && folder != null && alarm.BodyPath != null;
        return fromFile
            ? new AlarmBodyContent(kind, content, null, folder, alarm.BodyPath)
            : new AlarmBodyContent(kind, content, content, folder, null);
    }

    /// <summary>The url the web view opens for a markdown / html body.</summary>
    public static string StartUrl(AlarmBodyContent content) =>
        content.EntryFile != null ? BaseUrl + Uri.EscapeDataString(content.EntryFile) : BaseUrl + MarkdownPage;

    /// <summary>Markdown to a page with the same css as the Claude popup, images and links on.</summary>
    public static string RenderMarkdown(string markdown, PluginTheme theme)
    {
        bool isLight = theme.BgDark.GetBrightness() > 0.5f;
        return MarkdownRenderer.ToHtml(
            markdown,
            accentColorHex: ToHex(theme.Primary),
            textColorHex: ToHex(isLight ? theme.TextPrimary : theme.TextSecondary),
            headingColorHex: ToHex(theme.TextPrimary),
            bgColorHex: ToHex(theme.BgDark),
            codeBgHex: isLight ? "rgba(0,0,0,0.06)" : "rgba(12, 16, 26, 0.8)",
            themePrimaryHex: ToHex(theme.Primary),
            options: new MarkdownRenderOptions { Images = true, Links = true });
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}

/// <summary>Header text for the ring window.</summary>
static class AlarmRingText
{
    /// <summary>"Every day · 09:00 · 3rd firing" (the firing about to be counted), or "... · preview".</summary>
    public static string ScheduleLine(AlarmEntry alarm, bool preview)
    {
        var parts = new List<string> { alarm.GetScheduleDescription() };
        if (alarm.Schedule.Type != AlarmScheduleType.Interval) parts.Add(alarm.Schedule.TimeOfDay);
        parts.Add(preview ? "preview" : $"{Ordinal(alarm.TriggerCount + 1)} firing");
        return string.Join(" · ", parts);
    }

    public static string Ordinal(int n)
    {
        if (n <= 0) return n.ToString();
        int lastTwo = n % 100;
        string suffix = lastTwo is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return n + suffix;
    }

    public static Color PriorityColor(AlarmPriority priority, PluginTheme theme) => priority switch
    {
        AlarmPriority.High => theme.ErrorColor,
        AlarmPriority.Low => theme.TextSecondary,
        _ => theme.Primary,
    };
}

/// <summary>
/// Remembers the ring window's size and position per screen (keyed by the screen's bounds) in
/// <c>ring-window.json</c>, and fits a saved rectangle back onto the screen's working area.
/// </summary>
sealed class RingWindowPlacement
{
    public static readonly Size MinimumSize = new(420, 320);
    public static readonly Size DefaultSize = new(560, 460);

    private readonly string _file;

    public RingWindowPlacement(string dataDirectory)
    {
        _file = Path.Combine(dataDirectory, "ring-window.json");
    }

    public static string ScreenKey(Rectangle screenBounds) =>
        $"{screenBounds.X},{screenBounds.Y},{screenBounds.Width}x{screenBounds.Height}";

    public Rectangle? Load(Rectangle screenBounds)
    {
        try
        {
            var all = ReadAll();
            return all.TryGetValue(ScreenKey(screenBounds), out var r) && r.Length == 4
                ? new Rectangle(r[0], r[1], r[2], r[3])
                : null;
        }
        catch (Exception) { return null; }
    }

    public void Save(Rectangle screenBounds, Rectangle window)
    {
        try
        {
            var all = ReadAll();
            all[ScreenKey(screenBounds)] = new[] { window.X, window.Y, window.Width, window.Height };
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(all));
        }
        catch (Exception ex) { PluginLog.Warn($"Could not save the alarm window placement: {ex.Message}"); }
    }

    /// <summary>The saved rectangle clamped into the work area and to the minimum size, or the default
    /// size centred in the work area when nothing was saved.</summary>
    public static Rectangle Fit(Rectangle? saved, Rectangle workArea)
    {
        var size = saved?.Size ?? DefaultSize;
        int w = Math.Clamp(size.Width, MinimumSize.Width, Math.Max(MinimumSize.Width, workArea.Width));
        int h = Math.Clamp(size.Height, MinimumSize.Height, Math.Max(MinimumSize.Height, workArea.Height));
        int x, y;
        if (saved is Rectangle s)
        {
            x = Math.Clamp(s.X, workArea.Left, Math.Max(workArea.Left, workArea.Right - w));
            y = Math.Clamp(s.Y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - h));
        }
        else
        {
            x = workArea.Left + (workArea.Width - w) / 2;
            y = workArea.Top + (workArea.Height - h) / 2;
        }
        return new Rectangle(x, y, w, h);
    }

    private Dictionary<string, int[]> ReadAll()
    {
        if (!File.Exists(_file)) return new();
        return JsonSerializer.Deserialize<Dictionary<string, int[]>>(File.ReadAllText(_file)) ?? new();
    }
}
