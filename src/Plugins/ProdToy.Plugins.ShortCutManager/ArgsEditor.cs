using System.Text;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Edits an args string option by option: toggle a switch, set / swap / remove a
/// valued option, and switch off the options that cannot be combined with the one
/// turned on. Quote-aware, so <c>"/rename my app"</c> stays one token. No UI state.
/// </summary>
static class ArgsEditor
{
    /// <summary>Splits on whitespace outside double quotes; quotes stay in the token.</summary>
    public static List<string> Tokenize(string? args)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(args)) return list;
        var sb = new StringBuilder();
        bool inQuote = false;
        foreach (char ch in args)
        {
            if (ch == '"') inQuote = !inQuote;
            if (!inQuote && char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(ch);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    public static string Join(IEnumerable<string> tokens) => string.Join(" ", tokens);

    /// <summary>A slash command passed as the prompt (<c>"/rename x"</c>) - never an option's value.</summary>
    private static bool IsDirective(string token) =>
        token.StartsWith('/') || token.StartsWith("\"/", StringComparison.Ordinal);

    private static bool LooksLikeOption(string token) => token.StartsWith('-') && token.Length > 1;

    private static string Unquote(string token) =>
        token.Length >= 2 && token[0] == '"' && token[^1] == '"' ? token[1..^1] : token;

    private static string QuoteIfNeeded(string value) =>
        value.Length > 0 && value.Any(char.IsWhiteSpace) && !value.Contains('"') ? "\"" + value + "\"" : value;

    /// <summary>Finds the option in the token list: start index and token count
    /// (the flag plus its value tokens). Returns (-1, 0) when absent.</summary>
    private static (int Index, int Count) Find(List<string> toks, CliOption option, int from = 0)
    {
        for (int i = from; i < toks.Count; i++)
        {
            string t = toks[i];
            int eq = t.IndexOf('=');
            if (eq > 0 && LooksLikeOption(t) && option.Matches(t[..eq]))
                return (i, 1);
            if (!option.Matches(t)) continue;

            int count = 1;
            if (option.ValueKind == CliValueKind.Required && i + 1 < toks.Count
                && !IsDirective(toks[i + 1]) && !LooksLikeOption(toks[i + 1]))
            {
                count = 2;
                if (option.Variadic)
                    while (i + count < toks.Count && !LooksLikeOption(toks[i + count]) && !IsDirective(toks[i + count]))
                        count++;
            }
            else if (option.ValueKind == CliValueKind.Optional)
            {
                while (i + count < toks.Count && !LooksLikeOption(toks[i + count]) && !IsDirective(toks[i + count]))
                {
                    count++;
                    if (!option.Variadic) break;
                }
            }
            return (i, count);
        }
        return (-1, 0);
    }

    public static bool Has(string? args, CliOption option) => Find(Tokenize(args), option).Index >= 0;

    /// <summary>The option's value ("" when present without one); null when absent.</summary>
    public static string? GetValue(string? args, CliOption option)
    {
        var toks = Tokenize(args);
        var (i, n) = Find(toks, option);
        if (i < 0) return null;
        int eq = toks[i].IndexOf('=');
        if (eq > 0 && LooksLikeOption(toks[i])) return Unquote(toks[i][(eq + 1)..]);
        return n <= 1 ? "" : string.Join(" ", toks.Skip(i + 1).Take(n - 1).Select(Unquote));
    }

    /// <summary>Removes every occurrence of the option together with its value.</summary>
    public static string Remove(string? args, CliOption option)
    {
        var toks = Tokenize(args);
        RemoveAll(toks, option);
        return Join(toks);
    }

    private static void RemoveAll(List<string> toks, CliOption option)
    {
        while (true)
        {
            var (i, n) = Find(toks, option);
            if (i < 0) return;
            toks.RemoveRange(i, n);
        }
    }

    private static void RemoveExcluded(List<string> toks, CliOption option, IReadOnlyList<CliOption>? catalog)
    {
        foreach (var name in option.Excludes)
        {
            var other = catalog?.FirstOrDefault(o => o.Matches(name)) ?? new CliOption(name);
            RemoveAll(toks, other);
        }
    }

    /// <summary>Sets the option's value: replaces it in place when present (so the
    /// args keep one <c>--model</c>), else adds it before any slash-command prompt.
    /// An empty value writes the bare flag.</summary>
    public static string SetValue(string? args, CliOption option, string? value, IReadOnlyList<CliOption>? catalog = null)
    {
        var toks = Tokenize(args);
        RemoveExcluded(toks, option, catalog);
        var add = new List<string> { option.Token };
        string v = (value ?? "").Trim();
        if (v.Length > 0) add.Add(option.Variadic ? v : QuoteIfNeeded(v));

        var (i, n) = Find(toks, option);
        if (i >= 0)
        {
            toks.RemoveRange(i, n);
            RemoveAll(toks, option);
            toks.InsertRange(i, add);
        }
        else
        {
            int at = toks.FindIndex(IsDirective);
            if (at < 0) toks.AddRange(add); else toks.InsertRange(at, add);
        }
        return Join(Tokenize(Join(toks)));
    }

    /// <summary>A switch: removes it when present, else adds it (switching off the
    /// options it excludes).</summary>
    public static string Toggle(string? args, CliOption option, IReadOnlyList<CliOption>? catalog = null) =>
        Has(args, option) ? Remove(args, option) : SetValue(args, option, null, catalog);

    /// <summary>A preset value (<c>--model opus</c>): removes the option when it already
    /// holds that value, otherwise sets / swaps to it.</summary>
    public static string ToggleValue(string? args, CliOption option, string value, IReadOnlyList<CliOption>? catalog = null)
    {
        string? current = GetValue(args, option);
        return current != null && string.Equals(current, value, StringComparison.OrdinalIgnoreCase)
            ? Remove(args, option)
            : SetValue(args, option, value, catalog);
    }

    /// <summary>Wraps text at word boundaries to lines of at most <paramref name="width"/>
    /// characters (a longer single word stays whole).</summary>
    public static string Wrap(string? text, int width = 80)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                lines.Add(line.ToString());
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) lines.Add(line.ToString());
        return string.Join(Environment.NewLine, lines);
    }
}
