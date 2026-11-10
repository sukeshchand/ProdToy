using System.Text.Json;
using System.Text.RegularExpressions;
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
    string MachineName = "",
    string ProjectRoot = "");

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
    string MachineName = "",
    string Preview = "",
    string ProjectRoot = "");

/// <summary>
/// The bare "Claude is waiting" ping. Never saved any more; older ones stay in the day files but out of
/// the index, so the tree and the pager never see them. The messages are short, so the pattern only runs
/// on short text: the index reads every entry of the history.
/// </summary>
static class IdlePing
{
    private const int LongestPing = 200;
    private static Regex? _rule;
    private static string _pattern = "";

    public static bool Matches(string? message, string? pattern)
    {
        if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(pattern) || message.Length > LongestPing) return false;
        try
        {
            var rule = _rule;
            if (rule == null || _pattern != pattern)
            {
                rule = new Regex(pattern, RegexOptions.Compiled);
                _rule = rule;
                _pattern = pattern;
            }

            return rule.IsMatch(message);
        }
        catch (ArgumentException)
        {
            return false;   // a typo in the setting filters nothing
        }
    }
}

/// <summary>What <see cref="ChatHistory.SaveResponse"/> stored, and where.</summary>
record SavedResponse(HistoryIndex Index, HistoryEntry Entry);

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
    private readonly Func<string> _pingPatternGetter;
    private readonly string _envId;
    private readonly string _machineName;
    private readonly object _lock = new();
    private List<HistoryIndex>? _cachedIndex;
    private readonly Dictionary<string, DateTime> _seenFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How much of a question a tree row keeps.</summary>
    private const int PreviewLength = 80;

    public ChatHistory(string pluginDataDirectory, Func<bool> isEnabledGetter, Func<string> pingPatternGetter, string envId, string machineName)
    {
        _historyDir = Path.Combine(pluginDataDirectory, "history");
        _isEnabledGetter = isEnabledGetter;
        _pingPatternGetter = pingPatternGetter;
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

    public void SaveQuestion(string question, string sessionId = "", string cwd = "", string projectRoot = "")
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(question)) return;

        lock (_lock)
        {
            var entries = LoadDayEntries(DateTime.Now);
            var now = DateTime.Now;
            entries.Add(new HistoryEntry(
                "ProdToy", "", question.Trim(), NotificationType.Pending,
                now, sessionId, cwd, now, _envId, _machineName, projectRoot));
            WriteDay(DateTime.Now, entries);
            PatchCache(ToIndex(entries[^1], entries.Count - 1));
        }
    }

    /// <summary>
    /// A response answers the session's pending question, except a Notification: a permission prompt is
    /// its own row and must not take the question from the answer that follows it.
    /// </summary>
    public SavedResponse? SaveResponse(string title, string message, string type, string sessionId = "", string cwd = "", string hookEvent = "", string projectRoot = "")
    {
        if (!IsEnabled) return null;

        lock (_lock)
        {
            var entries = LoadDayEntries(DateTime.Now);

            bool takesQuestion = hookEvent != "Notification";
            int pendingIdx = -1;
            if (takesQuestion && !string.IsNullOrEmpty(sessionId))
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
            else if (takesQuestion && entries.Count > 0 && entries[^1].Type == NotificationType.Pending)
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
                    ProjectRoot = projectRoot.Length > 0 ? projectRoot : pending.ProjectRoot ?? "",
                    // EnvId / MachineName are preserved from the pending entry via `with`.
                };
            }
            else
            {
                entries.Add(new HistoryEntry(
                    title, message, "", type, DateTime.Now, sessionId, cwd, default, _envId, _machineName, projectRoot));
                pendingIdx = entries.Count - 1;
            }

            WriteDay(DateTime.Now, entries);
            var index = ToIndex(entries[pendingIdx], pendingIdx);
            PatchCache(index);
            return new SavedResponse(index, entries[pendingIdx]);
        }
    }

    private HistoryIndex ToIndex(HistoryEntry e, int position) => new(
        e.Title, e.Type, e.Timestamp, GetDayFile(e.Timestamp), position, e.SessionId, e.Cwd, _envId, _machineName, PreviewOf(e), e.ProjectRoot ?? "");

    /// <summary>A row's one line: the first words of the question, else of the reply, else the title.</summary>
    private static string PreviewOf(HistoryEntry e)
    {
        string text = PromptCards.ShortLine(e.Question) is { } card ? card
            : !string.IsNullOrWhiteSpace(e.Question) ? e.Question
            : !string.IsNullOrWhiteSpace(e.Message) ? e.Message
            : e.Title;
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > PreviewLength ? line[..PreviewLength] : line;
    }

    /// <summary>Keeps the cached index right after a write, so a write never costs a read of every day file.</summary>
    private void PatchCache(HistoryIndex item)
    {
        if (_cachedIndex == null) return;

        int at = _cachedIndex.FindIndex(i => i.Index == item.Index
            && string.Equals(i.DayFile, item.DayFile, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) _cachedIndex.RemoveAt(at);

        int place = _cachedIndex.FindLastIndex(i => i.Timestamp <= item.Timestamp) + 1;
        _cachedIndex.Insert(place, item);
    }

    public HistoryEntry? GetLatest()
    {
        var index = SnapshotIndex();
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

    /// <summary>Substring search over title/message/question, newest first,
    /// across up to <paramref name="maxDays"/> most recent day files. Used by
    /// the MCP history_search tool.</summary>
    public List<HistoryEntry> Search(string query, int limit, int maxDays = 60)
    {
        var results = new List<HistoryEntry>();
        foreach (var date in GetAvailableDates().OrderByDescending(d => d).Take(maxDays))
        {
            List<HistoryEntry> entries;
            lock (_lock) entries = LoadDayEntries(date);
            foreach (var e in entries.OrderByDescending(e => e.Timestamp))
            {
                if (e.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || e.Message.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || e.Question.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(e);
                    if (results.Count >= limit) return results;
                }
            }
        }
        return results;
    }

    /// <summary>A copy of the whole index, safe to read while a write goes on.</summary>
    public List<HistoryIndex> SnapshotIndex()
    {
        lock (_lock) return LoadIndex().ToList();
    }

    /// <summary>
    /// The entries of every day file written since the last look: a second machine's synced files.
    /// One directory listing, and only changed files are read.
    /// </summary>
    public List<HistoryIndex> LoadChangedFiles()
    {
        var found = new List<HistoryIndex>();
        try
        {
            if (!Directory.Exists(_historyDir)) return found;

            lock (_lock)
            {
                LoadIndex();
                foreach (var file in Directory.GetFiles(_historyDir, "*.json"))
                {
                    var written = File.GetLastWriteTimeUtc(file);
                    if (_seenFiles.TryGetValue(file, out var seen) && seen == written) continue;

                    _seenFiles[file] = written;
                    AppendIndexForFile(file, found);
                }

                foreach (var item in found) PatchCache(item);
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"Failed to look for changed history files: {ex.Message}");
        }

        return found;
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
                    _seenFiles[file] = File.GetLastWriteTimeUtc(file);
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

    private void AppendIndexForFile(string file, List<HistoryIndex> target)
    {
        try
        {
            string pingPattern = _pingPatternGetter();
            string json = File.ReadAllText(file);
            var entries = JsonSerializer.Deserialize<List<HistoryEntry>>(json);
            if (entries == null) return;

            string fileEnvId = ParseEnvIdFromFileName(file);
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.Type != NotificationType.Pending && IdlePing.Matches(e.Message, pingPattern)) continue;

                // Prefer the entry's own EnvId/MachineName (stamped at write time),
                // fall back to the filename-derived id for legacy entries.
                string envId = !string.IsNullOrEmpty(e.EnvId) ? e.EnvId : fileEnvId;
                target.Add(new HistoryIndex(
                    e.Title, e.Type, e.Timestamp, file, i, e.SessionId, e.Cwd,
                    envId, e.MachineName, PreviewOf(e), e.ProjectRoot ?? ""));
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
        }
        catch (Exception ex)
        {
            _cachedIndex = null;
            PluginLog.Error("Failed to write history", ex);
        }
    }

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
}
