using System.Text.RegularExpressions;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Reads the "Options:" block of <c>claude --help</c>: each option line starts with
/// two spaces and a dash; its description follows on the same line after a gap and /
/// or on deeper-indented lines below. Gives token, aliases, value kind (&lt;value&gt; =
/// required, [value] = optional), the choices listed as <c>(choices: "a", "b")</c>
/// and the description as one line.
/// </summary>
static class ClaudeHelpParser
{
    // Options that only make sense on their own, never in a shortcut.
    private static readonly HashSet<string> _skip = new(StringComparer.Ordinal) { "--help", "--version" };

    private static readonly Regex _gap = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex _quoted = new("\"([^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex _version = new(@"\d+(\.\d+)+", RegexOptions.Compiled);

    public static List<CliOption> Parse(string? help)
    {
        var result = new List<CliOption>();
        if (string.IsNullOrWhiteSpace(help)) return result;

        var lines = help.Replace("\r\n", "\n").Split('\n');
        int start = Array.FindIndex(lines, l => l.TrimEnd() == "Options:");
        if (start < 0) return result;

        string? head = null;
        var desc = new List<string>();

        void Flush()
        {
            if (head == null) return;
            var opt = BuildOption(head, string.Join(" ", desc));
            if (opt != null && !opt.Names.Any(_skip.Contains)) result.Add(opt);
            head = null;
            desc.Clear();
        }

        for (int i = start + 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            if (line.Length == 0) continue;
            if (!char.IsWhiteSpace(line[0])) break;          // next section, e.g. "Commands:"

            int indent = line.Length - line.TrimStart().Length;
            string body = line.Trim();
            if (indent <= 2 && body.StartsWith('-'))
            {
                Flush();
                var m = _gap.Match(body);
                if (m.Success)
                {
                    head = body[..m.Index];
                    desc.Add(body[(m.Index + m.Length)..]);
                }
                else head = body;
            }
            else if (head != null)
            {
                desc.Add(body);
            }
        }
        Flush();
        return result;
    }

    private static CliOption? BuildOption(string head, string description)
    {
        int v = head.IndexOfAny(new[] { '<', '[' });
        string namesPart = v >= 0 ? head[..v] : head;
        string valuePart = v >= 0 ? head[v..].Trim() : "";

        var names = namesPart.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n.StartsWith('-')).ToList();
        if (names.Count == 0) return null;

        // The canonical token is the last long name (--allowed-tools over --allowedTools,
        // --continue over -c).
        string token = names.LastOrDefault(n => n.StartsWith("--", StringComparison.Ordinal)) ?? names[^1];
        var aliases = names.Where(n => n != token).ToArray();

        var kind = valuePart.StartsWith('<') ? CliValueKind.Required
                 : valuePart.StartsWith('[') ? CliValueKind.Optional
                 : CliValueKind.None;

        return new CliOption(token, description.Trim(), kind, ParseChoices(description))
        {
            Aliases = aliases,
            Variadic = valuePart.Contains("...", StringComparison.Ordinal),
            ValueHint = valuePart,
        };
    }

    /// <summary>The quoted values of a <c>(choices: "a", "b", default: "a")</c> note.</summary>
    internal static string[]? ParseChoices(string description)
    {
        int at = description.IndexOf("(choices:", StringComparison.Ordinal);
        if (at < 0) return null;
        int end = description.IndexOf(')', at);
        string inner = end > at ? description[at..end] : description[at..];
        foreach (var stop in new[] { ", default:", ", preset:" })
        {
            int s = inner.IndexOf(stop, StringComparison.Ordinal);
            if (s >= 0) inner = inner[..s];
        }
        var list = _quoted.Matches(inner).Select(m => m.Groups[1].Value).Where(c => c.Length > 0).ToArray();
        return list.Length > 0 ? list : null;
    }

    /// <summary>The version number in <c>claude --version</c> output ("2.1.3 (Claude Code)").</summary>
    public static string? ParseVersion(string? versionOutput)
    {
        if (string.IsNullOrWhiteSpace(versionOutput)) return null;
        var m = _version.Match(versionOutput);
        return m.Success ? m.Value : null;
    }
}
