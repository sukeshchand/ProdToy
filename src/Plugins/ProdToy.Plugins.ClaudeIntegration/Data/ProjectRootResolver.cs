using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>
/// The folder the CLI was opened in, from a hook's transcript path. A hook's own cwd is the session's CURRENT folder, which
/// moves when the session changes directory; the transcript lives under the CLI's project folder for the folder it was
/// opened in (its name is that path with every character other than a letter or a digit turned into '-').
/// <list type="number">
/// <item>The transcript's first lines carry the cwd the session started in: that is the answer.</item>
/// <item>Else the project folder's name is matched against the directories that really exist.</item>
/// <item>Else the name itself is the label.</item>
/// </list>
/// </summary>
static class ProjectRootResolver
{
    /// <summary>How many transcript lines are read looking for the first cwd.</summary>
    private const int LinesToRead = 50;

    private static readonly ConcurrentDictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex NotLetterOrDigit = new("[^a-zA-Z0-9]", RegexOptions.Compiled);

    /// <summary>The opened folder for a transcript; "" when there is no transcript. A transcript's answer never changes, so it is kept.</summary>
    public static string Resolve(string? transcriptPath)
    {
        if (string.IsNullOrWhiteSpace(transcriptPath)) return "";
        if (Known.TryGetValue(transcriptPath, out var cached)) return cached;

        string found = FirstCwd(transcriptPath) is { Length: > 0 } cwd ? cwd : FromProjectKey(ProjectKeyOf(transcriptPath));
        if (found.Length > 0) Known[transcriptPath] = found;
        return found;
    }

    /// <summary>The project folder's name: the transcript's parent directory.</summary>
    public static string ProjectKeyOf(string transcriptPath)
        => Path.GetFileName(Path.GetDirectoryName(transcriptPath.Trim()) ?? "") ?? "";

    /// <summary>The first "cwd" in the transcript's first lines; "" when the file is missing or has none.</summary>
    public static string FirstCwd(string transcriptPath)
    {
        try
        {
            if (!File.Exists(transcriptPath)) return "";
            using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            for (int i = 0; i < LinesToRead && reader.ReadLine() is { } line; i++)
            {
                if (!line.Contains("\"cwd\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("cwd", out var cwd)
                        && cwd.ValueKind == JsonValueKind.String
                        && cwd.GetString() is { Length: > 0 } value)
                        return value;
                }
                catch (JsonException)
                {
                    // a torn line while the CLI writes: try the next
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return "";
    }

    /// <summary>The project key turned back into a real folder when one matches, else the key itself.</summary>
    public static string FromProjectKey(string key) => Decode(key) ?? key;

    /// <summary>
    /// Walks the disk from the drive down: at each level the child directory whose encoded name starts the rest of the key
    /// is taken (the longest first, so "My-App" beats "My"). Null when no real folder matches the whole key.
    /// </summary>
    public static string? Decode(string key)
    {
        // "C--Users-x" = "C:\Users\x": the drive letter, then ':' and '\' both turned into '-'.
        if (key.Length < 3 || !char.IsLetter(key[0]) || key[1] != '-' || key[2] != '-') return null;
        string drive = $"{char.ToUpperInvariant(key[0])}:\\";
        string rest = key[3..];
        return rest.Length == 0 ? (Directory.Exists(drive) ? drive : null) : Walk(drive, rest);
    }

    public static string Encode(string path) => NotLetterOrDigit.Replace(path, "-");

    private static string? Walk(string dir, string rest)
    {
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(dir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var child in children.OrderByDescending(c => Path.GetFileName(c).Length))
        {
            string encoded = Encode(Path.GetFileName(child));
            if (encoded.Length == 0 || !rest.StartsWith(encoded, StringComparison.OrdinalIgnoreCase)) continue;
            if (rest.Length == encoded.Length) return child;
            if (rest[encoded.Length] != '-') continue;
            if (Walk(child, rest[(encoded.Length + 1)..]) is { } deeper) return deeper;
        }

        return null;
    }
}
