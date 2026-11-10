namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>
/// Markdown to HTML for the Claude chat popup. The renderer itself lives in the SDK
/// (<see cref="ProdToy.Sdk.Rendering.MarkdownRenderer"/>); this wrapper adds the chat-only
/// css (.user-block, .claude-block, prompt cards, .timestamp) and keeps the popup's call
/// site unchanged.
/// </summary>
static class ChatMarkdownRenderer
{
    public static string ToHtml(string markdown, string accentColorHex, string textColorHex = "#a0afd2", string headingColorHex = "#ebf0ff", string bgColorHex = "transparent", string codeBgHex = "rgba(12, 16, 26, 0.8)", string? themePrimaryHex = null)
    {
        string primary = themePrimaryHex ?? accentColorHex;
        return ProdToy.Sdk.Rendering.MarkdownRenderer.ToHtml(
            markdown, accentColorHex, textColorHex, headingColorHex, bgColorHex, codeBgHex, primary,
            new ProdToy.Sdk.Rendering.MarkdownRenderOptions { ExtraCss = ChatCss(textColorHex, headingColorHex, primary) });
    }

    private static string ChatCss(string textColorHex, string headingColorHex, string themePrimaryHex) => $@"    .user-block {{
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
    .prompt-cards {{ margin: 0 0 10px 0; }}
    .pc-by {{
        color: {themePrimaryHex};
        font-weight: 700;
        font-size: 11px;
        text-transform: uppercase;
        letter-spacing: 1px;
        margin-bottom: 6px;
        opacity: 0.85;
    }}
    .pc-card {{
        background: {themePrimaryHex}10;
        border: 1px solid {themePrimaryHex}25;
        border-left: 4px solid {themePrimaryHex}55;
        padding: 8px 12px;
        margin: 0 0 6px 0;
        border-radius: 0 8px 8px 0;
    }}
    .pc-head {{ color: {headingColorHex}; font-weight: 600; font-size: 13px; }}
    .pc-mark {{ display: inline-block; width: 16px; font-weight: 700; }}
    .pc-ok {{ color: #4caf50; }}
    .pc-bad {{ color: #ef5350; }}
    .pc-quiet {{ color: {textColorHex}; }}
    .pc-text {{ color: {headingColorHex}; font-size: 13px; line-height: 1.45; margin-top: 4px; white-space: pre-wrap; word-break: break-word; }}
    .pc-meta {{ color: {textColorHex}; font-size: 11px; margin-top: 4px; opacity: 0.75; word-break: break-all; }}
    .pc-meta a {{ color: {themePrimaryHex}; }}
    .pc-raw {{ margin-top: 4px; font-size: 11px; color: {textColorHex}; }}
    .pc-raw summary {{ cursor: pointer; opacity: 0.75; }}
    .pc-raw pre {{ white-space: pre-wrap; word-break: break-word; }}
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
";
}
