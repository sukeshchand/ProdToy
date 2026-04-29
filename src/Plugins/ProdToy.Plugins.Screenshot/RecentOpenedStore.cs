using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProdToy.Plugins.Screenshot;

/// <summary>
/// Persists the list of library files the user has explicitly opened via the
/// "Open…" button in the <see cref="RecentImagesPanel"/>. Entries are stored
/// as filenames relative to <see cref="ScreenshotPaths.ScreenshotsDir"/> so
/// the list survives data-folder moves.
///
/// Capped at 10 entries; display code further filters to "opened within the
/// last 10 days" so old marks fade out naturally.
/// </summary>
static class RecentOpenedStore
{
    private const int MaxEntries = 10;

    private static string _storePath = "";
    private static readonly List<RecentOpenedEntry> _entries = new();

    public static void Initialize(string dataDirectory)
    {
        _storePath = Path.Combine(dataDirectory, "recent_opened.json");
        _entries.Clear();
        _entries.AddRange(LoadFromDisk());
    }

    /// <summary>Record that <paramref name="filePath"/> was opened now. No-op
    /// if the path is outside <see cref="ScreenshotPaths.ScreenshotsDir"/> —
    /// imports happen before marking, so callers always pass library paths.</summary>
    public static void MarkOpened(string filePath)
    {
        string? fileName = ToRelativeName(filePath);
        if (fileName == null) return;

        _entries.RemoveAll(e => string.Equals(e.File, fileName, StringComparison.OrdinalIgnoreCase));
        _entries.Insert(0, new RecentOpenedEntry(fileName, DateTime.UtcNow));
        while (_entries.Count > MaxEntries) _entries.RemoveAt(_entries.Count - 1);

        SaveToDisk();
    }

    /// <summary>Full paths for entries opened within the last <paramref name="daysWindow"/>
    /// days, newest first, capped at <see cref="MaxEntries"/>. Missing files are skipped.</summary>
    public static IReadOnlyList<string> GetRecent(int daysWindow = 10)
    {
        var cutoff = DateTime.UtcNow.AddDays(-daysWindow);
        var result = new List<string>();
        foreach (var e in _entries.OrderByDescending(e => e.OpenedAt))
        {
            if (e.OpenedAt < cutoff) continue;
            string full = Path.Combine(ScreenshotPaths.ScreenshotsDir, e.File);
            if (!File.Exists(full)) continue;
            result.Add(full);
            if (result.Count >= MaxEntries) break;
        }
        return result;
    }

    private static string? ToRelativeName(string filePath)
    {
        try
        {
            string dir = Path.GetFullPath(ScreenshotPaths.ScreenshotsDir);
            string full = Path.GetFullPath(filePath);
            if (!full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return null;
            return Path.GetFileName(full);
        }
        catch { return null; }
    }

    private static List<RecentOpenedEntry> LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_storePath)) return new();
            var json = File.ReadAllText(_storePath);
            var loaded = JsonSerializer.Deserialize<List<RecentOpenedEntry>>(json);
            return loaded ?? new();
        }
        catch { return new(); }
    }

    private static void SaveToDisk()
    {
        try
        {
            if (string.IsNullOrEmpty(_storePath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storePath, json);
        }
        catch { }
    }
}

record RecentOpenedEntry(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("openedAt")] DateTime OpenedAt);
