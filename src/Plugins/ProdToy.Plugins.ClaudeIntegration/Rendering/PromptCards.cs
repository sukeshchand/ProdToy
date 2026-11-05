using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ProdToy.Plugins.ClaudeIntegration;

enum PromptKind { Typed, Task, Session, Agent, Scheduled, Notice }

/// <summary>
/// The prompt hook fires for everything that enters a session, so the "question" of an entry may be a
/// background task's notice or another session's message rather than something a person typed. The kind
/// is read from how the text starts; the stored text is never changed, so old entries format too.
/// Plain string handling on purpose: these blocks need not be valid XML and a message can hold anything.
/// A broken block is shown as plain text.
/// </summary>
static class PromptCards
{
    /// <summary>The scheme of an output file link; the window opens it itself, never the browser.</summary>
    public const string OpenFileScheme = "prodtoy-open:";

    private const string SystemNoticeStart = "[SYSTEM NOTIFICATION";
    private static readonly Regex Attribute = new(@"([a-zA-Z-]+)=""([^""]*)""", RegexOptions.Compiled);
    private static readonly string[] OpenableEndings = [".output", ".txt", ".log"];

    private sealed record Block(PromptKind Kind, string Head, string Inner, string Raw);

    /// <summary>The tree row's short line; null for text a person typed.</summary>
    public static string? ShortLine(string? prompt)
    {
        var blocks = Blocks(prompt, onlyFirst: true);
        if (blocks.Count == 0) return null;

        var b = blocks[0];
        return b.Kind switch
        {
            PromptKind.Task => Field(b.Inner, "summary") is { Length: > 0 } s ? s : $"Background task · {Status(b)}",
            PromptKind.Session or PromptKind.Agent => $"From {Sender(b)}: {b.Inner.Trim()}",
            PromptKind.Scheduled => $"Scheduled: {Attr(b, "name")}",
            _ => "System notice",
        };
    }

    /// <summary>The cards of a system prompt as HTML, every value encoded; null for text a person typed.</summary>
    public static string? ToHtml(string? prompt, string timeBadgeHtml)
    {
        var blocks = Blocks(prompt, onlyFirst: false);
        if (blocks.Count == 0) return null;

        var html = new StringBuilder("<div class=\"prompt-cards\">");
        html.Append($"<div class=\"pc-by\">Woken by: {E(WokenBy(blocks[0]))}{timeBadgeHtml}</div>");
        foreach (var block in blocks) AppendCard(html, block);

        string raw = prompt!.Trim();
        int tail = EndOfBlocks(raw, blocks);
        if (tail < raw.Length && raw[tail..].Trim() is { Length: > 0 } rest)
            html.Append($"<div class=\"pc-text\">{E(rest)}</div>");

        html.Append($"<details class=\"pc-raw\"><summary>Show raw</summary><pre>{E(raw)}</pre></details></div>");
        return html.ToString();
    }

    /// <summary>Only a local file that exists with a known ending may be opened: the path comes from stored text.</summary>
    public static bool CanOpen(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        try
        {
            return Path.IsPathFullyQualified(path)
                && OpenableEndings.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
                && File.Exists(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void AppendCard(StringBuilder html, Block b)
    {
        switch (b.Kind)
        {
            case PromptKind.Task:
            {
                string status = Status(b);
                html.Append($"<div class=\"pc-card\"><div class=\"pc-head\"><span class=\"pc-mark {MarkClass(status)}\">{Mark(status)}</span>Background task · {E(status)}</div>");
                if (Field(b.Inner, "summary") is { Length: > 0 } summary) html.Append($"<div class=\"pc-text\">{E(summary)}</div>");
                if (Field(b.Inner, "result") is { Length: > 0 } result) html.Append($"<div class=\"pc-text\">{E(result)}</div>");
                if (Field(b.Inner, "note") is { Length: > 0 } note) html.Append($"<div class=\"pc-meta\">{E(note)}</div>");

                string ids = string.Join(" · ", new[] { Field(b.Inner, "task-id"), Field(b.Inner, "tool-use-id") }.Where(i => i.Length > 0));
                if (ids.Length > 0) html.Append($"<div class=\"pc-meta\">{E(ids)}</div>");

                if (Field(b.Inner, "output-file") is { Length: > 0 } file)
                {
                    html.Append(CanOpen(file)
                        ? $"<div class=\"pc-meta\"><a href=\"{OpenFileScheme}{Uri.EscapeDataString(file)}\">{E(Path.GetFileName(file))}</a></div>"
                        : $"<div class=\"pc-meta\">{E(file)}</div>");
                }

                html.Append("</div>");
                break;
            }

            case PromptKind.Session:
            case PromptKind.Agent:
            {
                string mode = Attr(b, "from-mode");
                string from = mode.Length > 0 ? $"{Sender(b)} · {mode}" : Sender(b);
                html.Append($"<div class=\"pc-card\"><div class=\"pc-head\">From {E(from)}</div><div class=\"pc-text\">{E(b.Inner.Trim())}</div></div>");
                break;
            }

            case PromptKind.Scheduled:
                html.Append($"<div class=\"pc-card\"><div class=\"pc-head\">Scheduled task · {E(Attr(b, "name"))}</div><div class=\"pc-text\">{E(b.Inner.Trim())}</div></div>");
                break;

            default:
                html.Append($"<div class=\"pc-card\"><div class=\"pc-head\">System notice</div><div class=\"pc-text\">{E(b.Inner.Trim())}</div></div>");
                break;
        }
    }

    private static string WokenBy(Block b) => b.Kind switch
    {
        PromptKind.Task => "background task",
        PromptKind.Session or PromptKind.Agent => Sender(b),
        PromptKind.Scheduled => "scheduled task",
        _ => "system notice",
    };

    private static string Sender(Block b)
        => Attr(b, "from-name") is { Length: > 0 } name ? name : Attr(b, "from") is { Length: > 0 } from && !from.Contains('\\') ? from : "another session";

    private static string Status(Block b) => Field(b.Inner, "status") is { Length: > 0 } s ? s : "finished";

    private static string Mark(string status) => status switch
    {
        "completed" => "✓",
        "failed" => "✗",
        "killed" or "stopped" => "■",
        _ => "•",
    };

    private static string MarkClass(string status) => status switch
    {
        "completed" => "pc-ok",
        "failed" => "pc-bad",
        _ => "pc-quiet",
    };

    private static string E(string text) => WebUtility.HtmlEncode(text);

    private static string Attr(Block b, string name)
    {
        foreach (Match m in Attribute.Matches(b.Head))
            if (m.Groups[1].Value == name) return m.Groups[2].Value;
        return "";
    }

    /// <summary>The first &lt;name&gt;...&lt;/name&gt; of a block, trimmed; "" when it is missing or not closed.</summary>
    private static string Field(string inner, string name)
    {
        string open = $"<{name}>";
        int start = inner.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return "";

        start += open.Length;
        int end = inner.IndexOf($"</{name}>", start, StringComparison.Ordinal);
        return end < 0 ? "" : inner[start..end].Trim();
    }

    private static List<Block> Blocks(string? prompt, bool onlyFirst)
    {
        var blocks = new List<Block>();
        if (string.IsNullOrWhiteSpace(prompt)) return blocks;

        string text = prompt.TrimStart();
        if (text.StartsWith(SystemNoticeStart, StringComparison.Ordinal))
        {
            blocks.Add(new Block(PromptKind.Notice, "", text, text));
            return blocks;
        }

        int at = 0;
        while (at < text.Length)
        {
            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
            if (ReadBlock(text, at) is not { } read) break;

            blocks.Add(read.Block);
            at = read.End;
            if (onlyFirst) break;
        }

        return blocks;
    }

    private static (Block Block, int End)? ReadBlock(string text, int at)
    {
        var kind = KindAt(text, at, out string tag);
        if (kind == PromptKind.Typed) return null;

        int headEnd = text.IndexOf('>', at);
        if (headEnd < 0) return null;

        string close = $"</{tag}>";
        int end = text.IndexOf(close, headEnd, StringComparison.Ordinal);
        if (end < 0) return null;

        int after = end + close.Length;
        return (new Block(kind, text[at..headEnd], text[(headEnd + 1)..end], text[at..after]), after);
    }

    private static PromptKind KindAt(string text, int at, out string tag)
    {
        foreach (var (name, kind) in new[]
        {
            ("task-notification", PromptKind.Task),
            ("cross-session-message", PromptKind.Session),
            ("agent-message", PromptKind.Agent),
            ("scheduled-task", PromptKind.Scheduled),
            ("system-reminder", PromptKind.Notice),
        })
        {
            if (string.CompareOrdinal(text, at, "<" + name, 0, name.Length + 1) != 0) continue;

            int next = at + name.Length + 1;
            if (next < text.Length && (text[next] == '>' || char.IsWhiteSpace(text[next])))
            {
                tag = name;
                return kind;
            }
        }

        tag = "";
        return PromptKind.Typed;
    }

    private static int EndOfBlocks(string raw, List<Block> blocks)
    {
        int at = 0;
        foreach (var block in blocks)
        {
            int found = raw.IndexOf(block.Raw, at, StringComparison.Ordinal);
            if (found < 0) return raw.Length;
            at = found + block.Raw.Length;
        }

        return at;
    }
}
