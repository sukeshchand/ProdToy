using System.Text.Json;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Persistence for Shortcut records. Stored as two files in the data
/// directory: <c>shortcuts.json</c> (shared across all machines pointing at
/// the same data dir) and an optional <c>shortcuts.{envId}.json</c> overlay
/// holding entries this machine owns. <see cref="Load"/> returns the union;
/// writes partition the input list by <see cref="Shortcut.EnvId"/> and
/// rewrite each file independently.
/// </summary>
static class ShortcutStore
{
    private static string _sharedFile = "";
    private static string _envFile = "";
    private static string _envId = "";
    private static readonly object _lock = new();
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };
    private static List<Shortcut>? _sharedCache;
    private static List<Shortcut>? _envCache;

    /// <summary>
    /// Raised after Add/Update/Delete (and bulk Save) — i.e. anything that
    /// could change the *set* of shortcuts. Deliberately NOT raised by
    /// <see cref="RecordLaunch"/>, which only mutates launch counters and
    /// would otherwise refresh shell-extension state on every launch.
    /// </summary>
    public static event Action? Changed;

    /// <summary>Current machine's environment id (from launchSettings.json),
    /// or empty when no machine identity is configured. Used by the edit
    /// form's "this machine only" toggle to stamp the shortcut.</summary>
    public static string CurrentEnvId => _envId;

    public static void Initialize(string dataDirectory)
    {
        _envId = ReadEnvId();
        _sharedFile = Path.Combine(dataDirectory, "shortcuts.json");
        _envFile = string.IsNullOrEmpty(_envId)
            ? ""
            : Path.Combine(dataDirectory, $"shortcuts.{_envId}.json");
    }

    public static List<Shortcut> Load()
    {
        lock (_lock)
        {
            _sharedCache ??= ReadFromDisk(_sharedFile);
            _envCache ??= string.IsNullOrEmpty(_envFile)
                ? new List<Shortcut>()
                : ReadFromDisk(_envFile);

            // Both caches stamp shortcuts with the appropriate EnvId on read
            // so the in-memory model is uniform; saves use that stamp to
            // route the entry back to the correct file.
            var merged = new List<Shortcut>(_sharedCache.Count + _envCache.Count);
            merged.AddRange(_sharedCache);
            merged.AddRange(_envCache);
            return merged;
        }
    }

    public static void Save(List<Shortcut> shortcuts) => SaveCore(shortcuts, raiseChanged: true);

    private static void SaveCore(List<Shortcut> shortcuts, bool raiseChanged)
    {
        List<Shortcut> shared, env;
        lock (_lock)
        {
            shared = shortcuts.Where(s => string.IsNullOrEmpty(s.EnvId)).ToList();
            env    = shortcuts.Where(s => !string.IsNullOrEmpty(s.EnvId)).ToList();

            // Defensive: any shortcut whose EnvId names some *other* machine
            // shouldn't be rewritten by this one. Drop those from the env
            // payload and leave their original file alone.
            if (!string.IsNullOrEmpty(_envId))
                env = env.Where(s => string.Equals(s.EnvId, _envId, StringComparison.OrdinalIgnoreCase)).ToList();

            _sharedCache = new List<Shortcut>(shared);
            _envCache = new List<Shortcut>(env);
        }

        WriteToDisk(_sharedFile, shared);
        if (!string.IsNullOrEmpty(_envFile))
            WriteToDisk(_envFile, env);

        if (raiseChanged)
        {
            try { Changed?.Invoke(); }
            catch (Exception ex) { PluginLog.Warn($"ShortcutStore.Changed handler threw: {ex.Message}"); }
        }
    }

    private static List<Shortcut> ReadFromDisk(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<Shortcut>>(json) ?? new();
        }
        catch (Exception ex)
        {
            PluginLog.Error($"ShortcutStore: load failed ({path})", ex);
            return new();
        }
    }

    private static void WriteToDisk(string path, List<Shortcut> shortcuts)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(shortcuts, _opts);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            PluginLog.Error($"ShortcutStore: save failed ({path})", ex);
        }
    }

    public static Shortcut? Get(string id) =>
        Load().FirstOrDefault(s => s.Id == id);

    public static void Add(Shortcut s)
    {
        // Every shortcut created on this machine is per-machine: if no
        // EnvId was supplied (the edit form never sets one) we stamp the
        // current machine's id so the entry lands in this machine's
        // overlay file and not the shared one. Skips when no envId is
        // configured — the entry falls back to the shared file.
        if (string.IsNullOrEmpty(s.EnvId) && !string.IsNullOrEmpty(_envId))
            s = s with { EnvId = _envId };

        var all = Load();
        all.Add(s);
        Save(all);
    }

    public static void Update(Shortcut s)
    {
        var all = Load();
        int idx = all.FindIndex(x => x.Id == s.Id);
        if (idx >= 0) all[idx] = s with { UpdatedAt = DateTime.Now };
        else all.Add(s);
        Save(all);
    }

    public static void Delete(string id)
    {
        var all = Load();
        int removed = all.RemoveAll(x => x.Id == id);
        if (removed > 0) Save(all);
    }

    public static void RecordLaunch(string id)
    {
        var all = Load();
        int idx = all.FindIndex(x => x.Id == id);
        if (idx < 0) return;
        all[idx] = all[idx] with
        {
            LastLaunchedAt = DateTime.Now,
            LaunchCount = all[idx].LaunchCount + 1,
        };
        // Suppress Changed: launch-count bumps don't alter the shortcut set
        // and shouldn't trigger shell-extension refreshes.
        SaveCore(all, raiseChanged: false);
    }

    /// <summary>Per-installation environment id from
    /// <c>~/.prod-toy/launchSettings.json</c>. Same source the screenshot
    /// plugin uses; empty when not configured.</summary>
    private static string ReadEnvId()
    {
        try
        {
            string launchSettingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".prod-toy", "launchSettings.json");
            if (!File.Exists(launchSettingsPath)) return "";
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(launchSettingsPath));
            var id = root?["envId"]?.GetValue<string>();
            return !string.IsNullOrWhiteSpace(id) ? id : "";
        }
        catch { return ""; }
    }
}
