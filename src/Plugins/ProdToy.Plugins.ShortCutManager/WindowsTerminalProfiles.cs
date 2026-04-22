using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Reads (and optionally writes) the user's Windows Terminal <c>settings.json</c>.
/// Falls back to a curated list of common shells when settings.json can't be found.
/// </summary>
static class WindowsTerminalProfiles
{
    private static readonly string[] FallbackProfileNames =
    {
        "Command Prompt",
        "PowerShell",
        "Windows PowerShell",
        "Developer Command Prompt",
        "Developer PowerShell",
        "Git Bash",
        "Azure Cloud Shell",
        "Ubuntu",
        "Ubuntu-22.04",
        "Debian",
        "Alpine",
        "Kali Linux",
        "openSUSE",
        "Cmder",
        "MSYS2",
    };

    private static readonly string[] FallbackSchemes =
    {
        "Campbell",
        "Campbell Powershell",
        "One Half Dark",
        "One Half Light",
        "Solarized Dark",
        "Solarized Light",
        "Tango Dark",
        "Tango Light",
        "Vintage",
    };

    /// <summary>
    /// Returns a de-duplicated, ordered list of profile names. Auto-detected
    /// profiles come first, followed by curated common names that weren't
    /// detected. The empty string is always last (= "use WT's default profile").
    /// </summary>
    public static IReadOnlyList<string> Discover()
    {
        var detected = TryRead(out var _, out var _);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var name in detected)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (seen.Add(name)) result.Add(name);
        }
        foreach (var name in FallbackProfileNames)
            if (seen.Add(name)) result.Add(name);
        result.Add("");
        return result;
    }

    /// <summary>Returns the color-scheme names declared in settings.json + common fallbacks.</summary>
    public static IReadOnlyList<string> DiscoverSchemes()
    {
        var schemes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in CandidatePaths())
        {
            if (!File.Exists(path)) continue;
            try
            {
                var node = ParseRoot(path);
                if (node?["schemes"] is JsonArray arr)
                {
                    foreach (var item in arr)
                    {
                        var name = item?["name"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                            schemes.Add(name);
                    }
                    if (schemes.Count > 0) break;
                }
            }
            catch (Exception ex) { Debug.WriteLine($"DiscoverSchemes: {path}: {ex.Message}"); }
        }
        foreach (var f in FallbackSchemes)
            if (seen.Add(f)) schemes.Add(f);
        return schemes;
    }

    /// <summary>
    /// Returns path to the settings.json file we'll write to, or null if WT
    /// isn't installed.
    /// </summary>
    public static string? FindSettingsPath()
    {
        foreach (var path in CandidatePaths())
            if (File.Exists(path)) return path;
        return null;
    }

    /// <summary>
    /// Appends a new profile entry to settings.json. Preserves existing JSON
    /// (except comments, which are lost — a known System.Text.Json limitation).
    /// Throws on failure.
    /// </summary>
    public static void AppendProfile(WtProfileDraft draft)
    {
        var (path, node, list) = LoadForWrite();
        foreach (var item in list)
        {
            var existingName = item?["name"]?.GetValue<string>();
            if (string.Equals(existingName, draft.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"A profile named \"{draft.Name}\" already exists. Choose a different name or edit the existing one in Windows Terminal.");
        }
        list.Add(draft.ToJson());
        Persist(path, node);
    }

    /// <summary>
    /// Reads a profile by name into a <see cref="WtProfileDraft"/>. Returns null
    /// if settings.json doesn't exist or the profile name isn't found.
    /// </summary>
    public static WtProfileDraft? ReadProfile(string name)
    {
        var path = FindSettingsPath();
        if (path == null) return null;
        var node = ParseRoot(path);
        var list = FindProfilesList(node);
        if (list == null) return null;
        foreach (var item in list)
        {
            if (item is not JsonObject obj) continue;
            var n = obj["name"]?.GetValue<string>();
            if (!string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) continue;

            return new WtProfileDraft
            {
                Name = n ?? "",
                Commandline = obj["commandline"]?.GetValue<string>() ?? "",
                ColorScheme = obj["colorScheme"]?.GetValue<string>() ?? "",
                FontFace = obj["font"]?["face"]?.GetValue<string>() ?? "",
                FontSize = obj["font"]?["size"] is JsonValue fsv && fsv.TryGetValue<int>(out var fs) ? fs : null,
                Icon = obj["icon"]?.GetValue<string>() ?? "",
                OpacityPercent = obj["opacity"] is JsonValue opv && opv.TryGetValue<int>(out var op) ? op : null,
                CursorShape = obj["cursorShape"]?.GetValue<string>() ?? "",
                StartingDirectory = obj["startingDirectory"]?.GetValue<string>() ?? "",
            };
        }
        return null;
    }

    /// <summary>
    /// Updates an existing profile (matched by <paramref name="oldName"/>) with
    /// the fields in <paramref name="draft"/>. Preserves the profile's guid so
    /// Windows Terminal doesn't see it as a new profile. Allows rename.
    /// </summary>
    public static void UpdateProfile(string oldName, WtProfileDraft draft)
    {
        var (path, node, list) = LoadForWrite();
        int idx = -1;
        JsonObject? oldObj = null;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is JsonObject o
                && string.Equals(o["name"]?.GetValue<string>(), oldName, StringComparison.OrdinalIgnoreCase))
            {
                idx = i; oldObj = o; break;
            }
        }
        if (idx < 0 || oldObj == null)
            throw new InvalidOperationException($"Profile \"{oldName}\" not found in settings.json.");

        // Name collision guard (ignore self)
        if (!string.Equals(oldName, draft.Name, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var item in list)
            {
                var n = item?["name"]?.GetValue<string>();
                if (string.Equals(n, draft.Name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"A profile named \"{draft.Name}\" already exists.");
            }
        }

        var next = draft.ToJson();
        // Preserve the original guid — Windows Terminal uses guid for identity.
        if (oldObj["guid"] is JsonValue g && g.TryGetValue<string>(out var guid))
            next["guid"] = guid;

        list[idx] = next;
        Persist(path, node);
    }

    public static void DeleteProfile(string name)
    {
        var (path, node, list) = LoadForWrite();
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is JsonObject o
                && string.Equals(o["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))
            {
                list.RemoveAt(i);
                Persist(path, node);
                return;
            }
        }
        throw new InvalidOperationException($"Profile \"{name}\" not found in settings.json.");
    }

    private static (string path, JsonNode root, JsonArray list) LoadForWrite()
    {
        var path = FindSettingsPath()
            ?? throw new InvalidOperationException("Windows Terminal settings.json not found.");
        var node = ParseRoot(path)
            ?? throw new InvalidOperationException("Couldn't parse Windows Terminal settings.json.");

        var list = FindProfilesList(node);
        if (list == null)
        {
            var po = new JsonObject { ["list"] = new JsonArray() };
            node["profiles"] = po;
            list = (JsonArray)po["list"]!;
        }
        return (path, node, list);
    }

    private static JsonArray? FindProfilesList(JsonNode? node)
    {
        if (node == null) return null;
        if (node["profiles"] is JsonObject po && po["list"] is JsonArray arr) return arr;
        if (node["profiles"] is JsonArray legacy) return legacy;
        return null;
    }

    private static void Persist(string path, JsonNode node)
    {
        var opts = new JsonSerializerOptions { WriteIndented = true };
        var json = node.ToJsonString(opts);
        var tmp = path + ".prodtoy.tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    private static JsonNode? ParseRoot(string path)
    {
        var docOpts = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        };
        var nodeOpts = new JsonNodeOptions { PropertyNameCaseInsensitive = false };
        var json = File.ReadAllText(path);
        return JsonNode.Parse(json, nodeOpts, docOpts);
    }

    private static List<string> TryRead(out string? settingsPath, out JsonNode? rootNode)
    {
        var result = new List<string>();
        settingsPath = null;
        rootNode = null;
        foreach (var path in CandidatePaths())
        {
            if (!File.Exists(path)) continue;
            try
            {
                var node = ParseRoot(path);
                var profilesNode = node?["profiles"];
                JsonArray? arr = null;
                if (profilesNode is JsonArray asArray) arr = asArray;
                else if (profilesNode?["list"] is JsonArray list) arr = list;
                if (arr == null) continue;
                foreach (var item in arr)
                {
                    var name = item?["name"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(name)) result.Add(name);
                }
                if (result.Count > 0) { settingsPath = path; rootNode = node; return result; }
            }
            catch (Exception ex) { Debug.WriteLine($"WindowsTerminalProfiles read {path}: {ex.Message}"); }
        }
        return result;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var packagesDir = Path.Combine(localApp, "Packages");
        if (Directory.Exists(packagesDir))
        {
            IEnumerable<string> pkgDirs = Array.Empty<string>();
            try { pkgDirs = Directory.EnumerateDirectories(packagesDir, "Microsoft.WindowsTerminal*"); }
            catch (Exception ex) { Debug.WriteLine($"enum packages: {ex.Message}"); }
            foreach (var dir in pkgDirs)
                yield return Path.Combine(dir, "LocalState", "settings.json");
        }

        yield return Path.Combine(localApp, "Microsoft", "Windows Terminal", "settings.json");
    }
}

/// <summary>
/// Data collected by <see cref="WtProfileCreateForm"/>. Only non-empty
/// fields are written to settings.json so the resulting profile stays minimal
/// and defers to WT's defaults for anything the user left blank.
/// </summary>
sealed record WtProfileDraft
{
    public string Name { get; init; } = "";
    public string Commandline { get; init; } = "";
    public string ColorScheme { get; init; } = "";
    public string FontFace { get; init; } = "";
    public int? FontSize { get; init; }
    public string Icon { get; init; } = "";
    public int? OpacityPercent { get; init; }
    public string CursorShape { get; init; } = "";
    public string StartingDirectory { get; init; } = "";

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["guid"] = "{" + Guid.NewGuid().ToString() + "}",
            ["name"] = Name,
            ["hidden"] = false,
        };
        if (!string.IsNullOrWhiteSpace(Commandline))   o["commandline"]   = Commandline;
        if (!string.IsNullOrWhiteSpace(ColorScheme))   o["colorScheme"]   = ColorScheme;
        if (!string.IsNullOrWhiteSpace(Icon))          o["icon"]          = Icon;
        if (!string.IsNullOrWhiteSpace(StartingDirectory)) o["startingDirectory"] = StartingDirectory;
        if (!string.IsNullOrWhiteSpace(CursorShape))   o["cursorShape"]   = CursorShape;
        if (OpacityPercent is int op && op is > 0 and <= 100)
            o["opacity"] = op;

        if (!string.IsNullOrWhiteSpace(FontFace) || FontSize is int)
        {
            var font = new JsonObject();
            if (!string.IsNullOrWhiteSpace(FontFace)) font["face"] = FontFace;
            if (FontSize is int fs) font["size"] = fs;
            o["font"] = font;
        }
        return o;
    }
}
