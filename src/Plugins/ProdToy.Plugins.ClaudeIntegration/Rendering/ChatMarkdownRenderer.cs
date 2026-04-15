using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>
/// Markdown → HTML renderer for the Claude chat popup. Plugin-owned copy of
/// the host's MarkdownRenderer, with the chat-specific .user-block /
/// .claude-block / .timestamp CSS still included here (the host's copy drops
/// those rules in Phase 5).
/// </summary>
static class ChatMarkdownRenderer
{
    private static readonly Regex RxTableRow = new(@"^\|.+\|$", RegexOptions.Compiled);
    private static readonly Regex RxTableSep = new(@"^\|[\s\-:|]+\|$", RegexOptions.Compiled);
    private static readonly Regex RxListClose = new(@"^\s*[-*]\s", RegexOptions.Compiled);
    private static readonly Regex RxOlClose = new(@"^\s*\d+\.\s", RegexOptions.Compiled);
    private static readonly Regex RxHrule = new(@"^[-*_]{3,}$", RegexOptions.Compiled);
    private static readonly Regex RxUl = new(@"^\s*[-*]\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex RxOl = new(@"^\s*\d+\.\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex RxHeading = new(@"^(#{1,3})\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex RxBold1 = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex RxBold2 = new(@"__(.+?)__", RegexOptions.Compiled);
    private static readonly Regex RxItalic1 = new(@"(?<!\w)\*(?!\s)(.+?)(?<!\s)\*(?!\w)", RegexOptions.Compiled);
    private static readonly Regex RxItalic2 = new(@"(?<!\w)_(?!\s)(.+?)(?<!\s)_(?!\w)", RegexOptions.Compiled);
    private static readonly Regex RxInlineCode = new(@"`(.+?)`", RegexOptions.Compiled);

    public static string ToHtml(string markdown, string accentColorHex, string textColorHex = "#a0afd2", string headingColorHex = "#ebf0ff", string bgColorHex = "transparent", string codeBgHex = "rgba(12, 16, 26, 0.8)", string? themePrimaryHex = null)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        bool inList = false;
        bool inCodeBlock = false;
        bool inTable = false;
        bool tableHeaderDone = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine;

            if (line.TrimStart().StartsWith("```"))
            {
                if (inCodeBlock)
                {
                    sb.AppendLine("</code></pre>");
                    inCodeBlock = false;
                }
                else
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inTable) { sb.AppendLine("</tbody></table>"); inTable = false; tableHeaderDone = false; }
                    sb.AppendLine("<pre><code>");
                    inCodeBlock = true;
                }
                continue;
            }

            if (inCodeBlock)
            {
                sb.AppendLine(WebUtility.HtmlEncode(line));
                continue;
            }

            bool isTableRow = RxTableRow.IsMatch(line.Trim());
            bool isSeparator = isTableRow && RxTableSep.IsMatch(line.Trim());

            if (isTableRow)
            {
                if (inList) { sb.AppendLine("</ul>"); inList = false; }

                if (!inTable)
                {
                    inTable = true;
                    tableHeaderDone = false;
                    sb.AppendLine("<table><thead><tr>");
                    foreach (var cell in ParseTableCells(line))
                        sb.Append($"<th>{InlineMarkdown(cell)}</th>");
                    sb.AppendLine("</tr></thead>");
                    continue;
                }

                if (isSeparator)
                {
                    if (!tableHeaderDone)
                    {
                        sb.AppendLine("<tbody>");
                        tableHeaderDone = true;
                    }
                    continue;
                }

                if (!tableHeaderDone)
                {
                    sb.AppendLine("<tbody>");
                    tableHeaderDone = true;
                }
                sb.Append("<tr>");
                foreach (var cell in ParseTableCells(line))
                    sb.Append($"<td>{InlineMarkdown(cell)}</td>");
                sb.AppendLine("</tr>");
                continue;
            }

            if (inTable)
            {
                sb.AppendLine("</tbody></table>");
                inTable = false;
                tableHeaderDone = false;
            }

            if (inList && !RxListClose.IsMatch(line) && !RxOlClose.IsMatch(line) && line.Trim().Length > 0)
            {
                sb.AppendLine("</ul>");
                inList = false;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                if (inList) { sb.AppendLine("</ul>"); inList = false; }
                sb.AppendLine("<br/>");
                continue;
            }

            if (RxHrule.IsMatch(line.Trim()))
            {
                if (inList) { sb.AppendLine("</ul>"); inList = false; }
                sb.AppendLine("<hr/>");
                continue;
            }

            var ulMatch = RxUl.Match(line);
            if (ulMatch.Success)
            {
                if (!inList) { sb.AppendLine("<ul>"); inList = true; }
                sb.AppendLine($"<li>{InlineMarkdown(ulMatch.Groups[1].Value)}</li>");
                continue;
            }

            var olMatch = RxOl.Match(line);
            if (olMatch.Success)
            {
                if (!inList) { sb.AppendLine("<ul>"); inList = true; }
                sb.AppendLine($"<li>{InlineMarkdown(olMatch.Groups[1].Value)}</li>");
                continue;
            }

            var headingMatch = RxHeading.Match(line);
            if (headingMatch.Success)
            {
                int level = headingMatch.Groups[1].Value.Length;
                sb.AppendLine($"<h{level}>{InlineMarkdown(headingMatch.Groups[2].Value)}</h{level}>");
                continue;
            }

            sb.AppendLine($"<p>{InlineMarkdown(line)}</p>");
        }

        if (inList) sb.AppendLine("</ul>");
        if (inTable) sb.AppendLine("</tbody></table>");
        if (inCodeBlock) sb.AppendLine("</code></pre>");

        return WrapInHtmlDocument(sb.ToString(), accentColorHex, textColorHex, headingColorHex, bgColorHex, codeBgHex, themePrimaryHex ?? accentColorHex);
    }

    private static string InlineMarkdown(string text)
    {
        text = WebUtility.HtmlEncode(text);
        text = RxBold1.Replace(text, "<strong>$1</strong>");
        text = RxBold2.Replace(text, "<strong>$1</strong>");
        text = RxItalic1.Replace(text, "<em>$1</em>");
        text = RxItalic2.Replace(text, "<em>$1</em>");
        text = RxInlineCode.Replace(text, "<code class=\"inline\">$1</code>");
        return text;
    }

    private static string[] ParseTableCells(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("|")) trimmed = trimmed[1..];
        if (trimmed.EndsWith("|")) trimmed = trimmed[..^1];
        return trimmed.Split('|').Select(c => c.Trim()).ToArray();
    }

    private static string WrapInHtmlDocument(string body, string accentColorHex, string textColorHex, string headingColorHex, string bgColorHex, string codeBgHex, string themePrimaryHex)
    {
        return $@"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8""/>
<style>
    * {{ margin: 0; padding: 0; box-sizing: border-box; }}
    body {{
        font-family: 'Segoe UI', sans-serif;
        font-size: 15px;
        color: {textColorHex};
        background: {bgColorHex};
        padding: 4px 14px 20px 4px;
        line-height: 1.6;
        overflow-x: hidden;
        overflow-y: auto;
    }}
    p {{ margin: 3px 0; }}
    strong {{ color: {headingColorHex}; font-weight: 600; }}
    em {{ font-style: italic; opacity: 0.7; }}
    code.inline {{
        background: rgba(56, 132, 244, 0.12);
        color: {accentColorHex};
        padding: 2px 6px;
        border-radius: 4px;
        font-family: 'Cascadia Code', 'Consolas', monospace;
        font-size: 14px;
        border: 1px solid rgba(56, 132, 244, 0.2);
    }}
    pre {{
        background: {codeBgHex};
        border-left: 3px solid {accentColorHex};
        padding: 10px 14px;
        margin: 8px 0;
        border-radius: 6px;
        overflow-x: auto;
    }}
    pre code {{
        font-family: 'Cascadia Code', 'Consolas', monospace;
        font-size: 14px;
        color: {textColorHex};
        background: none;
        padding: 0;
        border: none;
    }}
    ul {{
        margin: 4px 0 4px 8px;
        padding-left: 18px;
    }}
    li {{ margin: 3px 0; }}
    li::marker {{ color: {accentColorHex}; }}
    h1, h2, h3 {{
        color: {headingColorHex};
        margin: 8px 0 4px 0;
    }}
    h1 {{ font-size: 18px; }}
    h2 {{ font-size: 16px; }}
    h3 {{ font-size: 15px; }}
    table {{
        border-collapse: collapse;
        width: 100%;
        margin: 8px 0;
        font-size: 13px;
    }}
    th, td {{
        border: 1px solid rgba(56, 132, 244, 0.2);
        padding: 6px 10px;
        text-align: left;
    }}
    th {{
        background: rgba(56, 132, 244, 0.1);
        color: {headingColorHex};
        font-weight: 600;
        font-size: 13px;
    }}
    tr:nth-child(even) {{ background: rgba(56, 132, 244, 0.04); }}
    td {{ color: {textColorHex}; }}
    hr {{
        border: none;
        border-top: 1px solid {accentColorHex}33;
        margin: 10px 0;
    }}
    .user-block {{
        background: {themePrimaryHex}10;
        border: 1px solid {themePrimaryHex}25;
        border-left: 4px solid {themePrimaryHex}77;
        padding: 12px 16px;
        margin: 0 0 10px 0;
        border-radius: 0 8px 8px 0;
    }}
    .user-block .label {{
        color: {themePrimaryHex};
        font-weight: 700;
        font-size: 11px;
        text-transform: uppercase;
        letter-spacing: 1px;
        margin-bottom: 6px;
        opacity: 0.85;
    }}
    .user-block .label::before {{ content: '\25B8 '; }}
    .user-block .text {{
        color: {headingColorHex};
        font-family: 'Cascadia Code', 'Consolas', monospace;
        font-size: 14px;
        line-height: 1.5;
        white-space: pre-wrap;
        word-break: break-word;
    }}
    .claude-block {{
        background: transparent;
        border-left: 3px solid {themePrimaryHex}33;
        border-radius: 0;
        padding: 8px 14px 8px 14px;
        margin: 4px 0 0 0;
    }}
    .claude-label {{
        color: {themePrimaryHex};
        font-weight: 700;
        font-size: 11px;
        text-transform: uppercase;
        letter-spacing: 1px;
        margin-bottom: 6px;
    }}
    .claude-label::before {{ content: '\2726 '; }}
    .claude-content {{ }}
    .timestamp {{
        display: inline-block;
        margin-left: 6px;
        font-weight: 400;
        font-size: 10px;
        text-transform: none;
        letter-spacing: 0;
        opacity: 0.7;
        color: {textColorHex};
    }}
    br {{ display: block; content: ''; margin: 4px 0; }}
    ::-webkit-scrollbar {{ width: 6px; }}
    ::-webkit-scrollbar-track {{ background: transparent; }}
    ::-webkit-scrollbar-thumb {{ background: rgba(56, 132, 244, 0.3); border-radius: 3px; }}
    ::-webkit-scrollbar-thumb:hover {{ background: rgba(56, 132, 244, 0.5); }}
</style>
</head>
<body>{body}</body>
</html>";
    }
}
