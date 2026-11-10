using ProdToy.Plugins.ShortCutManager;
using Xunit;

namespace ProdToy.Tests.ShortCut;

public class ArgsEditorTests
{
    private static readonly IReadOnlyList<CliOption> Catalog = ClaudeCliCatalog.BuiltIn;
    private static CliOption O(string token) => Catalog.First(o => o.Matches(token));

    [Fact]
    public void Tokenize_KeepsQuotedTextTogether()
    {
        Assert.Equal(new[] { "--continue", "\"/rename my app\"" },
            ArgsEditor.Tokenize("  --continue   \"/rename my app\" "));
    }

    [Fact]
    public void Switch_TogglesOnAndOff()
    {
        string on = ArgsEditor.Toggle("--continue", O("--verbose"), Catalog);
        Assert.Equal("--continue --verbose", on);
        Assert.Equal("--continue", ArgsEditor.Toggle(on, O("--verbose"), Catalog));
    }

    [Fact]
    public void Switch_FoundByItsAlias()
    {
        Assert.True(ArgsEditor.Has("-c", O("--continue")));
        Assert.True(ArgsEditor.Has("--print", O("-p")));
        Assert.Equal("", ArgsEditor.Toggle("-c", O("--continue"), Catalog));
    }

    [Fact]
    public void ChoiceValue_SwapsInPlace_OneModelOnly()
    {
        string args = "--model opus --continue";
        string swapped = ArgsEditor.ToggleValue(args, O("--model"), "sonnet", Catalog);
        Assert.Equal("--model sonnet --continue", swapped);
        Assert.Single(ArgsEditor.Tokenize(swapped), t => t == "--model");
        Assert.Equal("sonnet", ArgsEditor.GetValue(swapped, O("--model")));
    }

    [Fact]
    public void ChoiceValue_SameValueAgain_RemovesTokenAndValue()
    {
        Assert.Equal("--continue", ArgsEditor.ToggleValue("--model opus --continue", O("--model"), "opus", Catalog));
    }

    [Fact]
    public void Value_EqualsForm_IsReadAndReplaced()
    {
        Assert.Equal("opus", ArgsEditor.GetValue("--model=opus", O("--model")));
        Assert.Equal("--model fable", ArgsEditor.SetValue("--model=opus", O("--model"), "fable", Catalog));
    }

    [Fact]
    public void FreeValue_WithSpaces_IsQuoted_AndRemovedWhole()
    {
        string args = ArgsEditor.SetValue("--continue", O("--append-system-prompt"), "be brief", Catalog);
        Assert.Equal("--continue --append-system-prompt \"be brief\"", args);
        Assert.Equal("be brief", ArgsEditor.GetValue(args, O("--append-system-prompt")));
        Assert.Equal("--continue", ArgsEditor.Remove(args, O("--append-system-prompt")));
    }

    [Fact]
    public void OptionalValue_PresentWithAndWithoutValue()
    {
        Assert.Equal("", ArgsEditor.GetValue("--resume --verbose", O("--resume")));
        Assert.Equal("abc", ArgsEditor.GetValue("--resume abc --verbose", O("--resume")));
        Assert.Null(ArgsEditor.GetValue("--verbose", O("--resume")));
        Assert.Equal("--verbose", ArgsEditor.Remove("--resume abc --verbose", O("--resume")));
    }

    [Fact]
    public void SlashCommandPrompt_IsNeverTakenAsAValue_AndNewOptionsGoBeforeIt()
    {
        string args = "--resume \"/rename App\"";
        Assert.Equal("", ArgsEditor.GetValue(args, O("--resume")));
        Assert.Equal("--resume --verbose \"/rename App\"", ArgsEditor.Toggle(args, O("--verbose"), Catalog));
    }

    [Fact]
    public void VariadicValue_TakesAllItems()
    {
        string args = ArgsEditor.SetValue("", O("--add-dir"), "../a ../b", Catalog);
        Assert.Equal("--add-dir ../a ../b", args);
        Assert.Equal("../a ../b", ArgsEditor.GetValue(args + " --verbose", O("--add-dir")));
        Assert.Equal("--verbose", ArgsEditor.Remove(args + " --verbose", O("--add-dir")));
    }

    [Fact]
    public void ExclusivePair_ChromeSwitchesOffNoChrome()
    {
        string args = ArgsEditor.Toggle("--no-chrome --continue", O("--chrome"), Catalog);
        Assert.Equal("--continue --chrome", args);
        Assert.Equal("--continue --no-chrome", ArgsEditor.Toggle(args, O("--no-chrome"), Catalog));
    }

    [Fact]
    public void ExclusivePair_PermissionModeAndSkipPermissions()
    {
        string auto = ArgsEditor.ToggleValue("--dangerously-skip-permissions --continue", O("--permission-mode"), "auto", Catalog);
        Assert.Equal("--continue --permission-mode auto", auto);
        Assert.Equal("--continue --dangerously-skip-permissions",
            ArgsEditor.Toggle(auto, O("--dangerously-skip-permissions"), Catalog));
    }

    [Fact]
    public void Chips_StartWithAutoModeAndCarryThreeModels()
    {
        var chips = ClaudeCliCatalog.Chips;
        Assert.Equal("--permission-mode auto", chips[0].Label);
        Assert.Equal(new[] { "--model fable", "--model opus", "--model sonnet" },
            chips.Where(c => c.Token == "--model").Select(c => c.Label));
        Assert.Contains(chips, c => c.Label == "--resume");
        Assert.Contains(chips, c => c.Label == "-p");
        Assert.DoesNotContain(chips, c => c.Token == "--dangerously-skip-permissions");
    }

    [Fact]
    public void BuiltInCatalog_HasEveryGroup_AndModelChoices()
    {
        foreach (var g in ClaudeCliCatalog.Groups)
            Assert.Contains(Catalog, o => o.Group == g);
        Assert.Equal(new[] { "fable", "opus", "sonnet", "haiku" }, O("--model").Choices);
        Assert.Equal(new[] { "low", "medium", "high", "xhigh", "max" }, O("--effort").Choices);
        Assert.All(Catalog, o => Assert.False(string.IsNullOrWhiteSpace(o.Description), o.Token));
    }
}
