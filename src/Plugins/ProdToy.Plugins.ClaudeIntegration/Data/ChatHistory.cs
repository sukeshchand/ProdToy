using System.Text.Json;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>
/// A single chat row: user prompt + assistant response + metadata.
/// Written to {pluginDataDir}/history/{yyyyMMdd}--{envId}.json. Each machine
/// writes to its own file so synced data folders don't race on concurrent writes.
/// </summary>
record HistoryEntry(
    string Title,
    string Message,
    string Question,
    string Type,
    DateTime Timestamp,
    string SessionId = "",
    string Cwd = "",
    DateTime QuestionTimestamp = default,
    string EnvId = "",
    string MachineName = "");

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
    string Cwd = "",
    string EnvId = "",
    string MachineName = "");

/// <summary>
/// Claude chat history owned by the ClaudeIntegration plugin.
///
/// Storage: {pluginDataDir}/history/{yyyyMMdd}--{envId}.json. One file per
/// local day PER MACHINE — so two machines sharing a synced data folder never
/// write to the same file. Reads merge every {yyyyMMdd}*.json file for the
/// date (plus the legacy no-envId {yyyyMMdd}.json for pre-migration entries).
///
/// Thread-safety: all writers take a single lock. Reads of the cached index
/// also lock so a write can invalidate the cache without racing a reader.
/// </summary>
sealed class ChatHistory
{
    private readonly string _historyDir;
    private readonly Func<bool> _isEnabledGetter;
    private readonly string _envId;
    private readonly string _machineName;
    private readonly object _lock = new();
    private List<HistoryIndex>? _cachedIndex;

    public ChatHistory(string pluginDataDirectory, Func<bool> isEnabledGetter, string envId, string machineName)
    {
        _historyDir = Path.Combine(pluginDataDirectory, "history");
        _isEnabledGetter = isEnabledGetter;
        _envId = envId ?? "";
        _machineName = machineName ?? "";
    }

    public bool IsEnabled => _isEnabledGetter();

    /// <summary>This machine's day file: {yyyyMMdd}--{envId}.json.</summary>
    private string GetDayFile(DateTime date)
        => Path.Combine(_historyDir, $"{date:yyyyMMdd}--{_envId}.json");

    /// <summary>All env-qualified files for the given date.</summary>
    private IEnumerable<string> EnumerateDayFiles(DateTime date)
    {
        if (!Directory.Exists(_historyDir)) yield break;
        string prefix = $"{date:yyyyMMdd}--";
        foreach (var f in Directory.EnumerateFiles(_historyDir, $"{prefix}*.json"))
        {
            if (Path.GetFileNameWithoutExtension(f).StartsWith(prefix, StringComparison.Ordinal))
                yield return f;
        }
    }

    /// <summary>Parse the envId suffix out of a filename. Returns "" if the name is malformed.</summary>
    private static string ParseEnvIdFromFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        int sep = name.IndexOf("--", StringComparison.Ordinal);
        return sep >= 0 ? name[(sep + 2)..] : "";
    }

    public void SaveQuestion(string question, string sessionId = "", string cwd = "")
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(question)) return;

        lock (_lock)
        {
            var entries = LoadDayEntries(DateTime.Now);
            var now = DateTime.Now;
            entries.Add(new HistoryEntry(
                "ProdToy", "", question.Trim(), NotificationType.Pending,
                now, sessionId, cwd, now, _envId, _machineName));
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
                    // EnvId / MachineName are preserved from the pending entry via `with`.
                };
            }
            else
            {
                entries.Add(new HistoryEntry(
                    title, message, "", type, DateTime.Now, sessionId, cwd, default, _envId, _machineName));
            }

            WriteDay(DateTime.Now, entries);
        }
    }

    public HistoryEntry? GetLatest()
    {
        var index = LoadIndex();
        for (int i = index.Count - 1; i >= 0; i--)
        {
            if (index[i].Type == NotificationType.Pending) continue;
            return LoadEntry(index[i]);
        }
        return null;
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
            PluginLog.Error("Failed to load history entry", ex);
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
                    AppendIndexForFile(file, index);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("Failed to load history index", ex);
            }

            // Sort globally by timestamp so merged entries from multiple machines
            // interleave correctly in navigation order.
            index.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            _cachedIndex = index;
            return index;
        }
    }

    private static void AppendIndexForFile(string file, List<HistoryIndex> target)
    {
        try
        {
            string json = File.ReadAllText(file);
            var entries = JsonSerializer.Deserialize<List<HistoryEntry>>(json);
            if (entries == null) return;

            string fileEnvId = ParseEnvIdFromFileName(file);
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                // Prefer the entry's own EnvId/MachineName (stamped at write time),
                // fall back to the filename-derived id for legacy entries.
                string envId = !string.IsNullOrEmpty(e.EnvId) ? e.EnvId : fileEnvId;
                target.Add(new HistoryIndex(
                    e.Title, e.Type, e.Timestamp, file, i, e.SessionId, e.Cwd,
                    envId, e.MachineName));
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"Failed to read history file {file}: {ex.Message}");
        }
    }

    /// <summary>
    /// Load entries for the given date — THIS MACHINE'S FILE ONLY. Used by
    /// SaveQuestion / SaveResponse so the pending-match logic operates on a
    /// single envelope per machine. Cross-machine merging happens in the
    /// index-building read path, not the write path.
    /// </summary>
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
            PluginLog.Error("Failed to load day entries", ex);
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
            PluginLog.Error("Failed to write history", ex);
        }
    }

    public List<HistoryIndex> LoadTodayIndex() => LoadDayIndex(DateTime.Now);

    public List<HistoryIndex> LoadDayIndex(DateTime date)
    {
        var index = new List<HistoryIndex>();
        foreach (var file in EnumerateDayFiles(date))
            AppendIndexForFile(file, index);
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
        var dates = new HashSet<DateTime>();
        try
        {
            if (!Directory.Exists(_historyDir)) return new List<DateTime>();
            foreach (var file in Directory.GetFiles(_historyDir, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                int sep = name.IndexOf("--", StringComparison.Ordinal);
                if (sep < 0) continue; // skip anything without the env suffix
                string datePart = name[..sep];

                if (DateTime.TryParseExact(datePart, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date))
                    dates.Add(date);
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error("Failed to get available dates", ex);
        }
        return dates.OrderBy(d => d).ToList();
    }

    public void Invalidate() { lock (_lock) { _cachedIndex = null; } }
}
