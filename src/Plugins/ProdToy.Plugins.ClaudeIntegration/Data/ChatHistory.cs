using System.Diagnostics;
using System.Text.Json;

namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>
/// A single chat row: user prompt + assistant response + metadata.
/// Written to {pluginDataDir}/history/{yyyyMMdd}.json. Matches the shape
/// the host's legacy ResponseHistory used so dual-write is a straight copy.
/// </summary>
record HistoryEntry(
    string Title,
    string Message,
    string Question,
    string Type,
    DateTime Timestamp,
    string SessionId = "",
    string Cwd = "",
    DateTime QuestionTimestamp = default);

/// <summary>
/// Lightweight index entry cached in memory (no message/question text).
/// </summary>
record HistoryIndex(
    string Title,
    string Type,
    DateTime Timestamp,
    string DayFile,
    int Index,
    string SessionId = "",
    string Cwd = "");

/// <summary>
/// Claude chat history owned by the ClaudeIntegration plugin.
///
/// Storage: {pluginDataDir}/history/{yyyyMMdd}.json. One file per local day,
/// JSON array of HistoryEntry.
///
/// Thread-safety: all writers take a single lock. Reads of the cached index
/// also lock so a write can invalidate the cache without racing a reader.
///
/// This class replaces the host-side `ResponseHistory` static class. It's
/// instance-scoped because the data directory is only known at runtime when
/// the plugin initializes.
/// </summary>
sealed class ChatHistory
{
    private readonly string _historyDir;
    private readonly Func<bool> _isEnabledGetter;
    private readonly object _lock = new();
    private List<HistoryIndex>? _cachedIndex;

    public ChatHistory(string pluginDataDirectory, Func<bool> isEnabledGetter)
    {
        _historyDir = Path.Combine(pluginDataDirectory, "history");
        _isEnabledGetter = isEnabledGetter;
    }

    public bool IsEnabled => _isEnabledGetter();

    private string GetDayFile(DateTime date)
        => Path.Combine(_historyDir, $"{date:yyyyMMdd}.json");

    public void SaveQuestion(string question, string sessionId = "", string cwd = "")
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(question)) return;

        lock (_lock)
        {
            var entries = LoadDayEntries(DateTime.Now);
            var now = DateTime.Now;
            entries.Add(new HistoryEntry(
                "ProdToy", "", question.Trim(), NotificationType.Pending,
                now, sessionId, cwd, now));
            WriteDay(DateTime.Now, entries);
        }
    }

    public void SaveResponse(string title, string message, string type, string sessionId = "", string cwd = "")
    {
        if (!IsEnabled) return;

        lock (_lock)
        {
            var entries = LoadDayEntries(DateTime.Now);

            int pendingIdx = -1;
            if (!string.IsNullOrEmpty(sessionId))
            {
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    if (entries[i].Type == NotificationType.Pending
                        && string.Equals(entries[i].SessionId, sessionId, StringComparison.Ordinal))
                    {
                        pendingIdx = i;
                        break;
                    }
                }
            }
            else if (entries.Count > 0 && entries[^1].Type == NotificationType.Pending)
            {
                pendingIdx = entries.Count - 1;
            }

            if (pendingIdx >= 0)
            {
                var pending = entries[pendingIdx];
                var qts = pending.QuestionTimestamp == default ? pending.Timestamp : pending.QuestionTimestamp;
                entries[pendingIdx] = pending with
                {
                    Title = title,
                    Message = message,
                    Type = type,
                    Timestamp = DateTime.Now,
                    SessionId = sessionId,
                    Cwd = cwd,
                    QuestionTimestamp = qts,
                };
            }
            else
            {
                entries.Add(new HistoryEntry(title, message, "", type, DateTime.Now, sessionId, cwd, default));
            }

            WriteDay(DateTime.Now, entries);
        }
    }

    public HistoryEntry? GetLatest()
    {
        var index = LoadIndex();
        if (index.Count == 0) return null;
        return LoadEntry(index[^1]);
    }

    public HistoryEntry? LoadEntry(HistoryIndex idx)
    {
        try
        {
            if (!File.Exists(idx.DayFile)) return null;
            string json = File.ReadAllText(idx.DayFile);
            var entries = JsonSerializer.Deserialize<List<HistoryEntry>>(json);
            if (entries != null && idx.Index < entries.Count)
                return entries[idx.Index];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load history entry: {ex.Message}");
        }
        return null;
    }

    public List<HistoryIndex> LoadIndex()
    {
        lock (_lock)
        {
            if (_cachedIndex != null) return _cachedIndex;

            var index = new List<HistoryIndex>();
            try
            {
                if (!Directory.Exists(_historyDir)) return index;

                foreach (var file in Directory.GetFiles(_historyDir, "*.json").OrderBy(f => f))
                {
                    try
                    {
                        string json = File.ReadAllText(file);
                        var entries = JsonSerializer.Deserialize<List<HistoryEntry>>(json);
                        if (entries == null) continue;

                        for (int i = 0; i < entries.Count; i++)
                        {
                            var e = entries[i];
                            index.Add(new HistoryIndex(e.Title, e.Type, e.Timestamp, file, i, e.SessionId, e.Cwd));
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to read history file {file}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load history index: {ex.Message}");
            }

            _cachedIndex = index;
            return index;
        }
    }

    private List<HistoryEntry> LoadDayEntries(DateTime date)
    {
        try
        {
            var file = GetDayFile(date);
            if (File.Exists(file))
            {
                string json = File.ReadAllText(file);
                return JsonSerializer.Deserialize<List<HistoryEntry>>(json) ?? new List<HistoryEntry>();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load day entries: {ex.Message}");
        }

        return new List<HistoryEntry>();
    }

    private void WriteDay(DateTime date, List<HistoryEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(_historyDir);
            string json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GetDayFile(date), json);
            _cachedIndex = null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write history: {ex.Message}");
        }
    }

    public List<HistoryIndex> LoadTodayIndex() => LoadDayIndex(DateTime.Now);

    public List<HistoryIndex> LoadDayIndex(DateTime date)
    {
        var dayFile = GetDayFile(date);
        var index = new List<HistoryIndex>();
        try
        {
            if (!File.Exists(dayFile)) return index;
            string json = File.ReadAllText(dayFile);
            var entries = JsonSerializer.Deserialize<List<HistoryEntry>>(json);
            if (entries == null) return index;

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                index.Add(new HistoryIndex(e.Title, e.Type, e.Timestamp, dayFile, i, e.SessionId, e.Cwd));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load day index for {date:yyyyMMdd}: {ex.Message}");
        }
        return index.OrderBy(i => i.Timestamp).ToList();
    }

    public List<string> GetTodayDistinctCwd() => GetDistinctCwd(DateTime.Now);

    public List<string> GetDistinctCwd(DateTime date)
        => LoadDayIndex(date).Where(i => !string.IsNullOrEmpty(i.Cwd))
            .Select(i => i.Cwd).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public List<(string SessionId, string Cwd)> GetTodayDistinctSessions()
        => GetDistinctSessions(DateTime.Now);

    public List<(string SessionId, string Cwd)> GetDistinctSessions(DateTime date)
        => LoadDayIndex(date).Where(i => !string.IsNullOrEmpty(i.SessionId))
            .GroupBy(i => i.SessionId)
            .Select(g => (g.Key, g.First().Cwd))
            .ToList();

    public List<HistoryIndex> FilterTodayByCwd(string cwd) => FilterByCwd(DateTime.Now, cwd);

    public List<HistoryIndex> FilterByCwd(DateTime date, string cwd)
        => LoadDayIndex(date).Where(i => string.Equals(i.Cwd, cwd, StringComparison.OrdinalIgnoreCase)).ToList();

    public List<HistoryIndex> FilterTodayBySession(string sessionId)
        => FilterBySession(DateTime.Now, sessionId);

    public List<HistoryIndex> FilterBySession(DateTime date, string sessionId)
        => LoadDayIndex(date).Where(i => string.Equals(i.SessionId, sessionId, StringComparison.Ordinal)).ToList();

    public List<DateTime> GetAvailableDates()
    {
        var dates = new List<DateTime>();
        try
        {
            if (!Directory.Exists(_historyDir)) return dates;
            foreach (var file in Directory.GetFiles(_historyDir, "*.json").OrderBy(f => f))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (DateTime.TryParseExact(name, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date))
                    dates.Add(date);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to get available dates: {ex.Message}");
        }
        return dates;
    }

    public void Invalidate() { lock (_lock) { _cachedIndex = null; } }
}
