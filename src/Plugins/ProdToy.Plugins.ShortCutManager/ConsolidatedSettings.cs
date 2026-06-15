using System.Text.Json;
using System.Text.Json.Nodes;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Persisted preferences for the Consolidated Launcher.
///
/// File shape (v2):
/// <code>
///   {
///     "version": 2,
///     "byFolder": { "&lt;folderPath&gt;": { "sequentialBuild": true }, ... },
///     "highlightRules": [ { "pattern": "ERR", "isRegex": false, "colorHex": "#FF6464", "enabled": true }, ... ]
///   }
/// </code>
/// v1 was a flat <c>Dictionary&lt;string, FolderPrefs&gt;</c>; loaded as-is and
/// rewrapped on first save. Highlight rules are global (shared across folders)
/// because rules like <c>ERR → red</c> are useful everywhere.
/// </summary>
static class ConsolidatedSettings
{
    private const int CurrentVersion = 2;

    private static string _file = "";
    private static readonly object _lock = new();
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    private static Dictionary<string, FolderPrefs>? _byFolder;
    private static List<HighlightRule>? _highlightRules;

    public sealed class FolderPrefs
    {
        public bool SequentialBuild { get; set; }
        /// <summary>Shortcut ids (within this folder) whose row has "clean bin/obj
        /// before run" enabled. Per-shortcut, dotnet-only.</summary>
        public List<string> CleanBinObjIds { get; set; } = new();
    }

    public static void Initialize(string dataDirectory)
        => _file = Path.Combine(dataDirectory, "consolidated-settings.json");

    public static bool GetSequentialBuild(string folderPath)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _byFolder!.TryGetValue(Key(folderPath), out var p) && p.SequentialBuild;
        }
    }

    public static void SetSequentialBuild(string folderPath, bool value)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var key = Key(folderPath);
            if (!_byFolder!.TryGetValue(key, out var p)) { p = new FolderPrefs(); _byFolder[key] = p; }
            if (p.SequentialBuild == value) return;
            p.SequentialBuild = value;
            Save();
        }
    }

    public static bool GetCleanBinObj(string folderPath, string shortcutId)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _byFolder!.TryGetValue(Key(folderPath), out var p)
                && p.CleanBinObjIds != null
                && p.CleanBinObjIds.Contains(shortcutId, StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void SetCleanBinObj(string folderPath, string shortcutId, bool value)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var key = Key(folderPath);
            if (!_byFolder!.TryGetValue(key, out var p)) { p = new FolderPrefs(); _byFolder[key] = p; }
            p.CleanBinObjIds ??= new();
            bool has = p.CleanBinObjIds.Contains(shortcutId, StringComparer.OrdinalIgnoreCase);
            if (value && !has) p.CleanBinObjIds.Add(shortcutId);
            else if (!value && has) p.CleanBinObjIds.RemoveAll(x => string.Equals(x, shortcutId, StringComparison.OrdinalIgnoreCase));
            else return;
            Save();
        }
    }

    /// <summary>Snapshot of the highlight rules — caller can mutate the returned
    /// list safely; it's a copy. To persist changes call <see cref="SetHighlightRules"/>.</summary>
    public static List<HighlightRule> GetHighlightRules()
    {
        lock (_lock)
        {
            EnsureLoaded();
            return new List<HighlightRule>(_highlightRules!);
        }
    }

    /// <summary>Replace the rule list and persist. Caller order is preserved
    /// (first-match-wins is rule consumer policy).</summary>
    public static void SetHighlightRules(IEnumerable<HighlightRule> rules)
    {
        lock (_lock)
        {
            EnsureLoaded();
            _highlightRules = rules?.Where(r => r != null).ToList() ?? new();
            Save();
        }
        HighlightRulesChanged?.Invoke();
    }

    /// <summary>Fires after <see cref="SetHighlightRules"/> persists. Log-tab
    /// controls subscribe so they re-compile their compiled-rule cache without
    /// having to poll the settings file.</summary>
    public static event Action? HighlightRulesChanged;

    private static string Key(string folderPath) => ShortcutFolders.Normalize(folderPath);

    private static void EnsureLoaded()
    {
        if (_byFolder != null && _highlightRules != null) return;

        try
        {
            if (!File.Exists(_file))
            {
                _byFolder = new(StringComparer.OrdinalIgnoreCase);
                _highlightRules = DefaultRules();
                return;
            }

            string text = File.ReadAllText(_file);
            var root = JsonNode.Parse(text);
            if (root is JsonObject obj && (obj["version"] is not null
                || obj["byFolder"] is JsonObject
                || obj["highlightRules"] is JsonArray))
            {
                // v2 shape.
                _byFolder = obj["byFolder"] is JsonObject folderObj
                    ? folderObj.Deserialize<Dictionary<string, FolderPrefs>>(_opts)
                      ?? new()
                    : new();
                _highlightRules = obj["highlightRules"] is JsonArray rulesArr
                    ? rulesArr.Deserialize<List<HighlightRule>>(_opts) ?? DefaultRules()
                    : DefaultRules();
            }
            else if (root is JsonObject legacy)
            {
                // v1: top-level map of folder → FolderPrefs.
                _byFolder = legacy.Deserialize<Dictionary<string, FolderPrefs>>(_opts) ?? new();
                _highlightRules = DefaultRules();
                PluginLog.Info("ConsolidatedSettings: migrated v1 → v2");
            }
            else
            {
                _byFolder = new();
                _highlightRules = DefaultRules();
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error("ConsolidatedSettings: load failed", ex);
            _byFolder = new();
            _highlightRules = DefaultRules();
        }

        // Folder keys compare case-insensitively (System.Text.Json gives us
        // ordinal by default, so re-wrap with the right comparer).
        if (!ReferenceEquals(_byFolder.Comparer, StringComparer.OrdinalIgnoreCase))
            _byFolder = new Dictionary<string, FolderPrefs>(_byFolder, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Seed rules so a brand-new install has visibly-useful highlights
    /// out of the box. Kept tiny — users will add their own.</summary>
    private static List<HighlightRule> DefaultRules() => new()
    {
        new HighlightRule { Pattern = "ERR", IsRegex = false, ColorHex = "#FF6464", Enabled = true },
        new HighlightRule { Pattern = @"\b(WRN|WARN)\b", IsRegex = true, ColorHex = "#F2C94C", Enabled = true },
    };

    private static void Save()
    {
        try
        {
            var doc = new JsonObject
            {
                ["version"] = CurrentVersion,
                ["byFolder"] = JsonSerializer.SerializeToNode(_byFolder, _opts),
                ["highlightRules"] = JsonSerializer.SerializeToNode(_highlightRules, _opts),
            };
            File.WriteAllText(_file, doc.ToJsonString(_opts));
        }
        catch (Exception ex)
        {
            PluginLog.Error("ConsolidatedSettings: save failed", ex);
        }
    }
}
