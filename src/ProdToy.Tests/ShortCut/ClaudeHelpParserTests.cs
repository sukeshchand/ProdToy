using ProdToy.Plugins.ShortCutManager;
using Xunit;

namespace ProdToy.Tests.ShortCut;

public class ClaudeHelpParserTests
{
    private static readonly List<CliOption> Parsed = ClaudeHelpParser.Parse(CapturedClaudeHelp.Text);

    private static CliOption Get(string token) => Assert.Single(Parsed, o => o.Token == token);

    [Fact]
    public void ReadsEveryOptionOfTheBlockButHelpAndVersion()
    {
        Assert.True(Parsed.Count > 60, $"only {Parsed.Count} options");
        Assert.DoesNotContain(Parsed, o => o.Matches("--help") || o.Matches("-h"));
        Assert.DoesNotContain(Parsed, o => o.Matches("--version"));
        // The Commands block is not read as options.
        Assert.DoesNotContain(Parsed, o => o.Token.Contains("attach"));
        Assert.Equal("--worktree", Parsed[^1].Token);
    }

    [Fact]
    public void ShortAndLongNames_TokenIsTheLongOne()
    {
        var cont = Get("--continue");
        Assert.Equal(new[] { "-c" }, cont.Aliases);
        Assert.Equal(CliValueKind.None, cont.ValueKind);
        Assert.Equal("Continue the most recent conversation in the current directory", cont.Description);

        var print = Get("--print");
        Assert.Contains("-p", print.Aliases);
    }

    [Fact]
    public void CamelCaseAlias_OnAHeadLineWithoutDescription()
    {
        var allowed = Get("--allowed-tools");
        Assert.Equal(new[] { "--allowedTools" }, allowed.Aliases);
        Assert.Equal(CliValueKind.Required, allowed.ValueKind);
        Assert.True(allowed.Variadic);
        Assert.StartsWith("Comma or space-separated list of tool names to allow", allowed.Description);
    }

    [Fact]
    public void ValueKinds_RequiredOptionalNone()
    {
        Assert.Equal(CliValueKind.Required, Get("--model").ValueKind);
        Assert.Equal("<model>", Get("--model").ValueHint);
        Assert.Equal(CliValueKind.Optional, Get("--resume").ValueKind);
        Assert.Equal(CliValueKind.Optional, Get("--debug").ValueKind);
        Assert.Equal(CliValueKind.None, Get("--verbose").ValueKind);
        Assert.False(Get("--model").Variadic);
        Assert.True(Get("--add-dir").Variadic);
    }

    [Fact]
    public void Choices_FromTheChoicesNote_WithoutDefaultOrPreset()
    {
        Assert.Equal(new[] { "acceptEdits", "auto", "bypassPermissions", "manual", "dontAsk", "plan" },
            Get("--permission-mode").Choices);
        Assert.Equal(new[] { "text", "json", "stream-json" }, Get("--output-format").Choices);
        Assert.Equal(new[] { "host", "none" }, Get("--permission-prompts").Choices);
        Assert.Equal(new[] { "true", "false", "1", "0", "yes", "no", "on", "off" }, Get("--prompt-suggestions").Choices);
        Assert.Null(Get("--model").Choices);
    }

    [Fact]
    public void MultiLineDescription_IsJoinedIntoOneLine()
    {
        var bare = Get("--bare");
        Assert.StartsWith("Minimal mode: skip hooks", bare.Description);
        Assert.Contains("--append-system-prompt[-file], --add-dir", bare.Description);
        Assert.DoesNotContain("\n", bare.Description);
        Assert.DoesNotContain("  ", bare.Description);
    }

    [Fact]
    public void Version_IsTheNumber()
    {
        Assert.Equal("2.1.322", ClaudeHelpParser.ParseVersion(CapturedClaudeHelp.Version));
        Assert.Null(ClaudeHelpParser.ParseVersion(""));
        Assert.Null(ClaudeHelpParser.ParseVersion(null));
    }

    [Fact]
    public void NoOptionsBlock_GivesNothing()
    {
        Assert.Empty(ClaudeHelpParser.Parse("Usage: claude\n\nCommands:\n  doctor  Check"));
        Assert.Empty(ClaudeHelpParser.Parse(null));
    }

    [Fact]
    public void Wrap_KeepsLinesNearEightyCharacters()
    {
        string wrapped = ArgsEditor.Wrap(Get("--restricted").Description);
        var lines = wrapped.Split(Environment.NewLine);
        Assert.True(lines.Length > 3);
        Assert.All(lines, l => Assert.True(l.Length <= 80, l));
        Assert.Equal("", ArgsEditor.Wrap(""));
    }
}
