using System.Text;
using System.Text.RegularExpressions;

namespace ProdToy.Plugins.Alarm;

/// <summary>
/// The alarm body files on disk. A markdown or html alarm owns the folder
/// <c>&lt;data&gt;/alarms/&lt;id&gt;/</c>: <c>body.md</c> or <c>body.html</c> plus the files it
/// references (images, css) beside it. A text alarm has no folder - its body is the Message.
/// No WinForms state: the store, the MCP tools, the edit form and the tests all use this.
/// </summary>
sealed class AlarmBodyFiles
{
    public const string FolderName = "alarms";
    public const string HistoryFolderName = "alarm-history-bodies";
    public const long HistoryCopyLimitBytes = 2L * 1024 * 1024;
    public const long ImportLimitBytes = 50L * 1024 * 1024;

    private static readonly Regex RxSafeId = new(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".svn", ".vs", "node_modules", "bin", "obj" };
    // src="x", href="x", url(x), ![alt](x)
    private static readonly Regex RxReference = new(
        @"(?:src|href)\s*=\s*[""']([^""'#?]+)|url\(\s*[""']?([^""')#?]+)|!\[[^\]]*\]\(([^)\s#?]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public AlarmBodyFiles(string dataDirectory)
    {
        DataDirectory = dataDirectory;
    }

    public string DataDirectory { get; }

    public string AlarmsRoot => Path.Combine(DataDirectory, FolderName);

    public static bool IsSafeId(string id) => RxSafeId.IsMatch(id);

    public string FolderFor(string id)
    {
        if (!IsSafeId(id)) throw new ArgumentException($"Not a valid alarm id: '{id}'.");
        return Path.Combine(AlarmsRoot, id);
    }

    public bool HasFolder(string id) => IsSafeId(id) && Directory.Exists(FolderFor(id));

    public static string FileNameFor(AlarmBodyKind kind) => kind switch
    {
        AlarmBodyKind.Markdown => "body.md",
        AlarmBodyKind.Html => "body.html",
        _ => throw new ArgumentException("A text alarm has no body file."),
    };

    /// <summary>markdown for .md / .markdown, html for .html / .htm, text for .txt; null otherwise.</summary>
    public static AlarmBodyKind? KindFromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".md" or ".markdown" => AlarmBodyKind.Markdown,
        ".html" or ".htm" => AlarmBodyKind.Html,
        ".txt" => AlarmBodyKind.Text,
        _ => null,
    };

    public static bool TryParseKind(string? value, out AlarmBodyKind kind)
    {
        kind = AlarmBodyKind.Text;
        if (string.IsNullOrWhiteSpace(value)) return false;
        switch (value.Trim().ToLowerInvariant())
        {
            case "text": case "txt": case "plain": kind = AlarmBodyKind.Text; return true;
            case "markdown": case "md": kind = AlarmBodyKind.Markdown; return true;
            case "html": case "htm": kind = AlarmBodyKind.Html; return true;
            default: return false;
        }
    }

    /// <summary>The body file's full path, or null for a text alarm / a missing file name.</summary>
    public string? BodyFileFor(AlarmEntry alarm)
    {
        if (alarm.BodyKind == AlarmBodyKind.Text || string.IsNullOrEmpty(alarm.BodyPath)) return null;
        if (!IsSafeId(alarm.Id)) return null;
        string folder = FolderFor(alarm.Id);
        string full = Path.GetFullPath(Path.Combine(folder, alarm.BodyPath));
        return IsInside(folder, full) ? full : null;
    }

    /// <summary>The body as text: the Message for a text alarm, the file's content otherwise
    /// (null when the file is missing).</summary>
    public string? ReadBody(AlarmEntry alarm)
    {
        if (alarm.BodyKind == AlarmBodyKind.Text) return alarm.Message;
        var file = BodyFileFor(alarm);
        return file != null && File.Exists(file) ? File.ReadAllText(file) : null;
    }

    /// <summary>Sets the body from inline content. Text puts the content in Message and removes
    /// the folder; markdown / html write the body file (replacing the other kind's file).</summary>
    public AlarmEntry WriteBody(AlarmEntry alarm, AlarmBodyKind kind, string content)
    {
        if (kind == AlarmBodyKind.Text)
        {
            DeleteFolder(alarm.Id);
            return alarm with { BodyKind = AlarmBodyKind.Text, BodyPath = null, Message = content };
        }

        string folder = FolderFor(alarm.Id);
        Directory.CreateDirectory(folder);
        RemoveBodyFiles(folder);
        string name = FileNameFor(kind);
        File.WriteAllText(Path.Combine(folder, name), content, new UTF8Encoding(false));
        return alarm with { BodyKind = kind, BodyPath = name };
    }

    /// <summary>Sets the body from a file: copies it as body.md / body.html, plus the folders
    /// beside it and the relative files it references. The kind comes from the extension when
    /// not given. A text kind reads the file into Message.</summary>
    public AlarmEntry ImportBodyFile(AlarmEntry alarm, AlarmBodyKind? kind, string sourceFile)
    {
        if (!Path.IsPathRooted(sourceFile)) throw new ArgumentException("'bodyFile' must be an absolute path.");
        string source = Path.GetFullPath(sourceFile);
        if (!File.Exists(source)) throw new FileNotFoundException($"Body file not found: {source}");

        var resolved = kind ?? KindFromExtension(source)
            ?? throw new ArgumentException("Cannot tell the body kind from the file's extension - pass 'bodyKind'.");
        string content = File.ReadAllText(source);
        if (resolved == AlarmBodyKind.Text) return WriteBody(alarm, AlarmBodyKind.Text, content);

        string sourceDir = Path.GetDirectoryName(source)!;
        string folder = FolderFor(alarm.Id);
        if (IsInside(folder, source))
        {
            // Re-importing a file that already lives in the alarm's folder: only rename it.
            return WriteBody(alarm, resolved, content);
        }

        long budget = ImportLimitBytes;
        var plan = new List<(string From, string To)>();

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            if (SkippedFolders.Contains(Path.GetFileName(dir))) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(sourceDir, file);
                if (rel.Split(Path.DirectorySeparatorChar).Any(SkippedFolders.Contains)) continue;
                plan.Add((file, rel));
            }
        }
        foreach (var rel in RelativeReferences(content))
        {
            string full = Path.GetFullPath(Path.Combine(sourceDir, rel));
            if (!IsInside(sourceDir, full) || !File.Exists(full)) continue;
            string relNorm = Path.GetRelativePath(sourceDir, full);
            if (!plan.Any(p => string.Equals(p.To, relNorm, StringComparison.OrdinalIgnoreCase)))
                plan.Add((full, relNorm));
        }

        foreach (var (from, _) in plan)
        {
            budget -= new FileInfo(from).Length;
            if (budget < 0)
                throw new InvalidOperationException(
                    $"The files beside the body add up to more than {ImportLimitBytes / (1024 * 1024)} MB - move the body into its own folder first.");
        }

        var result = WriteBody(alarm, resolved, content);
        foreach (var (from, rel) in plan)
        {
            string to = Path.Combine(folder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, overwrite: true);
        }
        return result;
    }

    /// <summary>Copies extra files or folders into the alarm's folder (by their names).</summary>
    public void CopyResources(string id, IEnumerable<string> paths)
    {
        string folder = FolderFor(id);
        Directory.CreateDirectory(folder);
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!Path.IsPathRooted(raw)) throw new ArgumentException($"Resource paths must be absolute: '{raw}'.");
            string path = Path.GetFullPath(raw);
            if (File.Exists(path))
            {
                File.Copy(path, Path.Combine(folder, Path.GetFileName(path)), overwrite: true);
            }
            else if (Directory.Exists(path))
            {
                string target = Path.Combine(folder, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)));
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    string to = Path.Combine(target, Path.GetRelativePath(path, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(file, to, overwrite: true);
                }
            }
            else throw new FileNotFoundException($"Resource not found: {path}");
        }
    }

    public void DeleteFolder(string id)
    {
        if (!IsSafeId(id)) return;
        string folder = FolderFor(id);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    /// <summary>Copies one alarm's folder to another id (used by Duplicate).</summary>
    public void CopyFolder(string fromId, string toId)
    {
        if (!HasFolder(fromId)) return;
        string from = FolderFor(fromId), to = FolderFor(toId);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    /// <summary>Copies the body file for a history row when it is 2 MB or less. Returns the copy's
    /// path relative to the data folder, or null (text alarm, missing or too big).</summary>
    public string? SnapshotForHistory(AlarmEntry alarm, DateTime at)
    {
        var file = BodyFileFor(alarm);
        if (file == null || !File.Exists(file)) return null;
        if (new FileInfo(file).Length > HistoryCopyLimitBytes) return null;
        string rel = Path.Combine(HistoryFolderName, alarm.Id, $"{at:yyyyMMdd-HHmmss-fff}{Path.GetExtension(file)}");
        string dest = Path.Combine(DataDirectory, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(file, dest, overwrite: true);
        return rel;
    }

    /// <summary>Deletes a history copy (when its row is trimmed). Only paths under the history folder.</summary>
    public void DeleteHistoryCopy(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return;
        string root = Path.Combine(DataDirectory, HistoryFolderName);
        string full = Path.GetFullPath(Path.Combine(DataDirectory, relativePath));
        if (IsInside(root, full) && File.Exists(full)) File.Delete(full);
    }

    /// <summary>Maps a url path of the body viewer to a file inside the alarm's folder; null when
    /// the path leaves the folder or the file does not exist.</summary>
    public static string? ResolveInFolder(string folder, string urlPath)
    {
        string rel = Uri.UnescapeDataString(urlPath).TrimStart('/', '\\');
        if (rel.Length == 0) return null;
        string root = Path.GetFullPath(folder);
        string full;
        try { full = Path.GetFullPath(Path.Combine(root, rel)); }
        catch (Exception) { return null; }
        return IsInside(root, full) && File.Exists(full) ? full : null;
    }

    /// <summary>Relative paths referenced by a markdown or html body (no scheme, no rooted path).</summary>
    public static IEnumerable<string> RelativeReferences(string content)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in RxReference.Matches(content))
        {
            string value = (m.Groups[1].Success ? m.Groups[1].Value
                : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).Trim();
            if (value.Length == 0 || value.Contains(':') || value.StartsWith("//") || Path.IsPathRooted(value)) continue;
            value = Uri.UnescapeDataString(value).Replace('/', Path.DirectorySeparatorChar);
            if (seen.Add(value)) yield return value;
        }
    }

    public static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".json" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".txt" or ".md" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    /// <summary>A one-line summary of a markdown body for the balloon / list when no Message is given.</summary>
    public static string SummaryOf(AlarmBodyKind kind, string content)
    {
        if (kind == AlarmBodyKind.Html)
        {
            var title = Regex.Match(content, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return title.Success ? System.Net.WebUtility.HtmlDecode(title.Groups[1].Value).Trim() : "";
        }
        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim().TrimStart('#', '-', '*', '>', ' ').Trim();
            if (line.Length == 0 || line.StartsWith("|") || line.StartsWith("```") || line.StartsWith("![")) continue;
            return line.Length > 120 ? line[..117] + "..." : line;
        }
        return "";
    }

    private static void RemoveBodyFiles(string folder)
    {
        foreach (var name in new[] { "body.md", "body.html" })
        {
            string p = Path.Combine(folder, name);
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private static bool IsInside(string folder, string path)
    {
        string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
