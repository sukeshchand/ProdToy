using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// User-defined line-colouring rule for the Consolidated Launcher's log tabs.
/// Matches a single line of log output; the first matching rule (in list
/// order) wins. Persisted as JSON in consolidated-settings.json — kept as
/// hex strings so colours round-trip cleanly through System.Text.Json.
/// </summary>
public sealed record HighlightRule
{
    public string Pattern { get; init; } = "";

    /// <summary>When true, <see cref="Pattern"/> is treated as a regex; otherwise
    /// as a case-insensitive substring. Substring is the common case (e.g.
    /// "ERR"); regex is for compound patterns like <c>WRN|WARN</c>.</summary>
    public bool IsRegex { get; init; }

    /// <summary>Foreground colour applied to the whole line, as <c>#RRGGBB</c>.
    /// Background is not coloured — line backgrounds in a RichTextBox require
    /// per-character SelectionBackColor which doesn't survive line breaks
    /// well and would make selection look weird.</summary>
    public string ColorHex { get; init; } = "#FF6464";

    public bool Enabled { get; init; } = true;
}

/// <summary>Compiled form of a <see cref="HighlightRule"/> — used at log-append
/// time to match lines and pick a colour. Compiled once per rule set so the
/// hot path doesn't re-parse regex or re-decode the hex on every line.</summary>
sealed class CompiledHighlight
{
    public required Regex Pattern { get; init; }
    public required Color Color { get; init; }
    public bool Enabled { get; init; } = true;
}

static class LogHighlightCompiler
{
    /// <summary>Compile rules in user order (first-match-wins). Rules whose
    /// regex fails to parse are skipped — broken rules shouldn't take down
    /// the log tab. Disabled rules are kept in the array so callers can
    /// honour the order property if they ever want to surface that.</summary>
    public static CompiledHighlight[] Compile(IEnumerable<HighlightRule> rules)
    {
        if (rules == null) return Array.Empty<CompiledHighlight>();
        var list = new List<CompiledHighlight>();
        foreach (var r in rules)
        {
            if (r == null || string.IsNullOrEmpty(r.Pattern)) continue;
            Regex regex;
            try
            {
                string pattern = r.IsRegex ? r.Pattern : Regex.Escape(r.Pattern);
                regex = new Regex(pattern,
                    RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
            }
            catch { continue; }

            list.Add(new CompiledHighlight
            {
                Pattern = regex,
                Color = ParseHex(r.ColorHex),
                Enabled = r.Enabled,
            });
        }
        return list.ToArray();
    }

    /// <summary>First enabled rule whose pattern matches <paramref name="line"/>,
    /// or null if none match. Hot path — keep allocation-free.</summary>
    public static Color? FirstMatch(CompiledHighlight[] rules, string line)
    {
        for (int i = 0; i < rules.Length; i++)
        {
            var r = rules[i];
            if (!r.Enabled) continue;
            if (r.Pattern.IsMatch(line)) return r.Color;
        }
        return null;
    }

    /// <summary>Parse <c>#RRGGBB</c> (or <c>RRGGBB</c>); fall back to a visible
    /// red if the string is malformed so a broken rule still highlights
    /// instead of silently rendering in the default foreground.</summary>
    public static Color ParseHex(string? hex)
    {
        if (!string.IsNullOrEmpty(hex))
        {
            string h = hex.TrimStart('#');
            if (h.Length == 6
                && int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            {
                return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
            }
        }
        return Color.FromArgb(255, 0xFF, 0x64, 0x64);
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
