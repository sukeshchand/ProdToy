using System.Text.Json;
using ProdToy.Sdk;

namespace ProdToy.Plugins.Alarm;

static class AlarmStore
{
    private static string _alarmsFile = "";
    private static string _historyFile = "";
    private static string _dataDir = "";
    private static Func<int> _getMaxHistoryEntries = () => 500;
    private static readonly object _alarmLock = new();
    private static readonly object _historyLock = new();
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    private static List<AlarmEntry>? _cachedAlarms;
    private static List<AlarmHistoryEntry>? _cachedHistory;

    // History write batching: buffer entries in memory, flush periodically
    private static readonly List<AlarmHistoryEntry> _historyBuffer = new();
    private static System.Threading.Timer? _historyFlushTimer;

    public static void Initialize(string dataDirectory, Func<int> getMaxHistoryEntries)
    {
        _dataDir = dataDirectory;
        _alarmsFile = Path.Combine(dataDirectory, "alarms.json");
        _historyFile = Path.Combine(dataDirectory, "alarm-history.json");
        _getMaxHistoryEntries = getMaxHistoryEntries;
    }

    public static void StartHistoryFlush()
    {
        _historyFlushTimer = new System.Threading.Timer(_ => FlushHistory(), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
    }

    public static void StopHistoryFlush()
    {
        _historyFlushTimer?.Dispose();
        _historyFlushTimer = null;
        FlushHistory(); // Final flush on shutdown
    }

    // --- Alarms ---

    public static List<AlarmEntry> LoadAlarms()
    {
        lock (_alarmLock)
        {
            if (_cachedAlarms != null) return _cachedAlarms;
            try
            {
                if (File.Exists(_alarmsFile))
                {
                    var json = File.ReadAllText(_alarmsFile);
                    _cachedAlarms = JsonSerializer.Deserialize<List<AlarmEntry>>(json) ?? new();
                    return _cachedAlarms;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("Failed to load alarms", ex);
            }
            _cachedAlarms = new();
            return _cachedAlarms;
        }
    }

    public static void SaveAlarms(List<AlarmEntry> alarms)
    {
        string json;
        lock (_alarmLock)
        {
            _cachedAlarms = alarms;
            json = JsonSerializer.Serialize(alarms, _jsonOpts);
        }
        try
        {
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(_alarmsFile, json);
        }
        catch (Exception ex)
        {
            PluginLog.Error("Failed to save alarms", ex);
        }
    }

    public static AlarmEntry? GetAlarm(string id)
    {
        return LoadAlarms().FirstOrDefault(a => a.Id == id);
    }

    public static void AddAlarm(AlarmEntry alarm)
    {
        var alarms = new List<AlarmEntry>(LoadAlarms()) { alarm };
        SaveAlarms(alarms);
        AddHistoryEntry(new AlarmHistoryEntry
        {
            AlarmId = alarm.Id,
            AlarmTitle = alarm.Title,
            EventType = AlarmHistoryEventType.Created,
        });
    }

    public static void UpdateAlarm(AlarmEntry alarm)
    {
        var alarms = new List<AlarmEntry>(LoadAlarms());
        int idx = alarms.FindIndex(a => a.Id == alarm.Id);
        if (idx >= 0)
            alarms[idx] = alarm with { UpdatedAt = DateTime.Now };
        else
            alarms.Add(alarm);
        SaveAlarms(alarms);
    }

    public static void DeleteAlarm(string id)
    {
        var alarms = new List<AlarmEntry>(LoadAlarms());
        var alarm = alarms.FirstOrDefault(a => a.Id == id);
        if (alarm != null)
        {
            alarms.Remove(alarm);
            SaveAlarms(alarms);
            AddHistoryEntry(new AlarmHistoryEntry
            {
                AlarmId = id,
                AlarmTitle = alarm.Title,
                EventType = AlarmHistoryEventType.Deleted,
            });
        }
    }

    public static void SetStatus(string id, AlarmStatus status)
    {
        var alarms = new List<AlarmEntry>(LoadAlarms());
        int idx = alarms.FindIndex(a => a.Id == id);
        if (idx >= 0)
        {
            alarms[idx] = alarms[idx] with { Status = status, UpdatedAt = DateTime.Now };
            SaveAlarms(alarms);
        }
    }

    public static void RecordTrigger(string id)
    {
        var alarms = new List<AlarmEntry>(LoadAlarms());
        int idx = alarms.FindIndex(a => a.Id == id);
        if (idx >= 0)
        {
            var alarm = alarms[idx];
            alarms[idx] = alarm with
            {
                LastTriggeredAt = DateTime.Now,
                TriggerCount = alarm.TriggerCount + 1,
                UpdatedAt = DateTime.Now,
            };

            if (alarm.Schedule.Type == AlarmScheduleType.Once)
                alarms[idx] = alarms[idx] with { Status = AlarmStatus.Completed };

            if (alarm.FireAndForget)
            {
                alarms[idx] = alarms[idx] with { Status = AlarmStatus.Completed };
                AddHistoryEntry(new AlarmHistoryEntry
                {
                    AlarmId = id,
                    AlarmTitle = alarm.Title,
                    EventType = AlarmHistoryEventType.Completed,
                    Detail = "Fire-and-forget alarm completed",
                });
            }

            SaveAlarms(alarms);
        }
    }

    // --- History (batched writes) ---

    public static List<AlarmHistoryEntry> LoadHistory()
    {
        lock (_historyLock)
        {
            if (_cachedHistory != null)
            {
                if (_historyBuffer.Count > 0)
                    return _cachedHistory.Concat(_historyBuffer).ToList();
                return _cachedHistory;
            }
            try
            {
                if (File.Exists(_historyFile))
                {
                    var json = File.ReadAllText(_historyFile);
                    _cachedHistory = JsonSerializer.Deserialize<List<AlarmHistoryEntry>>(json) ?? new();
                }
                else
                {
                    _cachedHistory = new();
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("Failed to load alarm history", ex);
                _cachedHistory = new();
            }
            if (_historyBuffer.Count > 0)
                return _cachedHistory.Concat(_historyBuffer).ToList();
            return _cachedHistory;
        }
    }

    public static List<AlarmHistoryEntry> LoadHistory(string alarmId)
    {
        return LoadHistory().Where(h => h.AlarmId == alarmId).ToList();
    }

    public static void AddHistoryEntry(AlarmHistoryEntry entry)
    {
        lock (_historyLock)
        {
            _historyBuffer.Add(entry);
        }
    }

    private static void FlushHistory()
    {
        List<AlarmHistoryEntry> toFlush;
        lock (_historyLock)
        {
            if (_historyBuffer.Count == 0) return;
            toFlush = new List<AlarmHistoryEntry>(_historyBuffer);
            _historyBuffer.Clear();
        }

        try
        {
            List<AlarmHistoryEntry> history;
            lock (_historyLock)
            {
                if (_cachedHistory != null)
                    history = new List<AlarmHistoryEntry>(_cachedHistory);
                else if (File.Exists(_historyFile))
                {
                    var json = File.ReadAllText(_historyFile);
                    history = JsonSerializer.Deserialize<List<AlarmHistoryEntry>>(json) ?? new();
                }
                else
                {
                    history = new();
                }
            }

            history.AddRange(toFlush);

            int max = _getMaxHistoryEntries();
            if (history.Count > max)
                history = history.Skip(history.Count - max).ToList();

            var jsonOut = JsonSerializer.Serialize(history, _jsonOpts);
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(_historyFile, jsonOut);

            lock (_historyLock)
            {
                _cachedHistory = history;
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error("Failed to flush alarm history", ex);
            lock (_historyLock)
            {
                _historyBuffer.InsertRange(0, toFlush);
            }
        }
    }

    public static void InvalidateAlarms()
    {
        lock (_alarmLock) { _cachedAlarms = null; }
    }

    public static void Invalidate()
    {
        lock (_alarmLock) { _cachedAlarms = null; }
        lock (_historyLock) { _cachedHistory = null; }
    }
}
