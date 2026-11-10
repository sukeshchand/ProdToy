using ProdToy.Plugins.ShortCutManager;
using Xunit;

namespace ProdToy.Tests.ShortCut;

/// <summary>The catalog keeps static state (the cache file and the merged list), so
/// these tests run in one class, one after another.</summary>
public sealed class ClaudeCliCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "prodtoy-cli-catalog-" + Guid.NewGuid().ToString("N"));

    public ClaudeCliCatalogTests()
    {
        Directory.CreateDirectory(_dir);
        ClaudeCliCatalog.Initialize(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string? FakeClaude(string args, string help) =>
        args == "--help" ? help : args == "--version" ? CapturedClaudeHelp.Version : null;

    [Fact]
    public void WithoutCache_TheBuiltInListIsUsed()
    {
        Assert.Null(ClaudeCliCatalog.CliVersion);
        Assert.Same(ClaudeCliCatalog.BuiltIn, ClaudeCliCatalog.Current);
    }

    [Fact]
    public void Refresh_WithoutClaude_KeepsTheBuiltInList()
    {
        var result = ClaudeCliCatalog.Refresh(_ => null);
        Assert.False(result.Ok);
        Assert.Null(ClaudeCliCatalog.CliVersion);
        Assert.Same(ClaudeCliCatalog.BuiltIn, ClaudeCliCatalog.Current);
        Assert.False(File.Exists(Path.Combine(_dir, ClaudeCliCatalog.CacheFileName)));
    }

    [Fact]
    public void Refresh_MergesCachesAndReloads()
    {
        var result = ClaudeCliCatalog.Refresh(a => FakeClaude(a, CapturedClaudeHelp.Text));
        Assert.True(result.Ok, result.Message);
        Assert.True(result.Added > 0);
        Assert.Equal("2.1.322", ClaudeCliCatalog.CliVersion);

        var current = ClaudeCliCatalog.Current;
        // A flag the built-in list lacks arrives with its text, in "Other".
        var tmux = Assert.Single(current, o => o.Token == "--tmux");
        Assert.Equal("Other", tmux.Group);
        Assert.StartsWith("Create a tmux session", tmux.Description);
        // Built-in entries keep their token, group, choices and exclusions.
        var model = Assert.Single(current, o => o.Token == "--model");
        Assert.Equal("Model", model.Group);
        Assert.Equal(new[] { "fable", "opus", "sonnet", "haiku" }, model.Choices);
        var print = Assert.Single(current, o => o.Token == "-p");
        Assert.Contains("--print", print.Aliases);
        Assert.Contains("--permission-mode", Assert.Single(current, o => o.Token == "--dangerously-skip-permissions").Excludes);
        Assert.Single(current, o => o.Token == "/rename <name>");
        // No option twice.
        Assert.Equal(current.Count, current.Select(o => o.Token).Distinct().Count());

        // The cache survives a restart.
        Assert.True(File.Exists(Path.Combine(_dir, ClaudeCliCatalog.CacheFileName)));
        ClaudeCliCatalog.Initialize(_dir);
        Assert.Equal("2.1.322", ClaudeCliCatalog.CliVersion);
        Assert.Contains(ClaudeCliCatalog.Current, o => o.Token == "--tmux");
    }

    [Fact]
    public void Refresh_AfterACliUpdate_AddsTheNewFlagWithItsText()
    {
        ClaudeCliCatalog.Refresh(a => FakeClaude(a, CapturedClaudeHelp.Text));
        string newer = CapturedClaudeHelp.Text.Replace("\r\n", "\n").Replace("Options:\n",
            "Options:\n  --shiny-new <thing>                   A brand new option for testing\n");
        Assert.Contains("--shiny-new", newer);
        var result = ClaudeCliCatalog.Refresh(a => FakeClaude(a, newer));
        Assert.Equal(1, result.Added);
        var shiny = Assert.Single(ClaudeCliCatalog.Current, o => o.Token == "--shiny-new");
        Assert.Equal("A brand new option for testing", shiny.Description);
        Assert.Equal(CliValueKind.Required, shiny.ValueKind);
    }

    [Fact]
    public void CorruptCache_FallsBackToBuiltIn()
    {
        File.WriteAllText(Path.Combine(_dir, ClaudeCliCatalog.CacheFileName), "{ not json");
        ClaudeCliCatalog.Initialize(_dir);
        Assert.Same(ClaudeCliCatalog.BuiltIn, ClaudeCliCatalog.Current);
    }

    [Fact]
    public void NewClaudeShortcut_DefaultsToAutoModeAndContinue()
    {
        var claude = LaunchProfiles.GetOrDefault("claude");
        Assert.Equal("--permission-mode auto --continue", claude.DefaultArgs);
        Assert.True(claude.HasOptionsMenu);
        Assert.Same(ClaudeCliCatalog.Chips, claude.SuggestedTokens);
    }

    [Fact]
    public void OtherProfiles_KeepTheirChips_WithEmptyDescriptions()
    {
        foreach (var id in new[] { "npm", "vite", "dotnet", "pwsh", "custom", "url", "vssln" })
        {
            var p = LaunchProfiles.GetOrDefault(id);
            Assert.Equal(id, p.Id);
            Assert.Equal("", p.DefaultArgs);
            Assert.False(p.HasOptionsMenu);
            Assert.All(p.SuggestedTokens, o => Assert.Equal("", o.Description));
        }
        Assert.Contains(LaunchProfiles.GetOrDefault("npm").SuggestedTokens, o => o.Token == "run dev");
    }

    [Fact]
    public void ExistingShortcut_KeepsItsArgs()
    {
        var json = "[{\"name\":\"x\",\"profile\":\"claude\",\"claudeArgs\":\"--dangerously-skip-permissions --continue\"}]";
        var list = System.Text.Json.JsonSerializer.Deserialize<List<ProdToy.Plugins.ShortCutManager.Shortcut>>(json)!;
        Assert.Equal("--dangerously-skip-permissions --continue", list[0].Args);
    }
}
