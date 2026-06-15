using System.Text;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Derives alternative ways to run the same command for the Consolidated
/// Launcher's "Run as ▾" switcher. Currently dotnet only — run / watch / Release.
/// Returns an empty list when there's no meaningful alternative (non-dotnet,
/// or a non-run/watch verb like build/test).
/// </summary>
static class LaunchVariants
{
    public static List<(string Label, string Command)> For(string baseCommand)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(baseCommand)) return list;

        var tokens = Tokenize(baseCommand.Trim());
        if (tokens.Count == 0) return list;
        if (!Eq(tokens[0], "dotnet")) return list;

        bool runOrWatch = tokens.Skip(1).Any(t => Eq(t, "run") || Eq(t, "watch"));
        if (!runOrWatch) return list;

        // Skip the leading run/watch verbs to isolate the option tail
        // (--project, -c, --launch-profile, -- appargs, …).
        int i = 1;
        while (i < tokens.Count && (Eq(tokens[i], "run") || Eq(tokens[i], "watch"))) i++;
        var tailTokens = tokens.Skip(i).ToList();
        string tail = string.Join(" ", tailTokens).Trim();
        bool hasConfig = tailTokens.Any(t =>
        {
            var u = Unquote(t).ToLowerInvariant();
            return u == "-c" || u == "--configuration" || u.StartsWith("-c=") || u.StartsWith("--configuration=");
        });

        list.Add(("dotnet run", Join("dotnet run", tail)));
        list.Add(("dotnet watch", Join("dotnet watch run", tail)));
        if (!hasConfig)
            list.Add(("dotnet run · Release", Join("dotnet run -c Release", tail)));
        return list;
    }

    private static string Join(string head, string tail) =>
        string.IsNullOrEmpty(tail) ? head : head + " " + tail;

    private static bool Eq(string token, string word) =>
        Unquote(token).Equals(word, StringComparison.OrdinalIgnoreCase);

    private static string Unquote(string t) =>
        t.Length >= 2 && t[0] == '"' && t[^1] == '"' ? t[1..^1] : t;

    private static List<string> Tokenize(string s)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        foreach (char c in s)
        {
            if (c == '"') { inQuotes = !inQuotes; sb.Append(c); }
            else if (c == ' ' && !inQuotes) { if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); } }
            else sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }
}
