using System.Text.Json;
using ProdToy.Plugins.ClaudeIntegration;
using Xunit;

namespace ProdToy.Tests.Claude;

public class OpenedFolderTreeTests
{
    private const string Root = @"D:\work\App";
    private const string Day = "folders|20261110";
    private static readonly string RootKey = HistoryTree.FolderKey(Root);

    /// <summary>One session opened in App that moved into three inner folders, plus a plain session.</summary>
    private static List<HistoryIndex> Sample() => new()
    {
        TestItems.Item(0, cwd: Root, session: "s1"),
        TestItems.Item(1, cwd: Root + @"\src\Api", session: "s1"),
        TestItems.Item(2, cwd: Root + @"\src\Web", session: "s1"),
        TestItems.Item(3, cwd: Root + @"\tools", session: "s1"),
        TestItems.Item(4, cwd: Root + @"\src\Api", session: "s1"),
        TestItems.Item(5, cwd: @"D:\work\Other", session: "s2"),
    };

    private static HistoryTree Loaded(FolderView view = FolderView.Opened, IEnumerable<HistoryIndex>? items = null)
    {
        var tree = new HistoryTree();
        tree.SetFolderView(view);
        tree.Load(items ?? Sample());
        return tree;
    }

    [Fact]
    public void OpenedViewGroupsInnerFoldersUnderTheOpenedFolder()
    {
        var tree = Loaded();

        var opened = tree.Find($"{Day}|{RootKey}")!;
        Assert.True(opened.IsOpenedFolder);
        Assert.Equal("App", opened.Label);
        Assert.Equal(5, opened.Count);   // counts roll up to the opened folder

        var inner = opened.Children.Where(c => !c.IsItem).Select(c => c.Label).OrderBy(l => l).ToList();
        Assert.Equal(new[] { @"src\Api", @"src\Web", "tools" }, inner);
        Assert.Equal(2, tree.Find($"{Day}|{RootKey}|{HistoryTree.FolderKey(Root + @"\src\Api")}")!.Count);
    }

    [Fact]
    public void TheInnerLevelIsSkippedWhenItIsTheOpenedFolder()
    {
        var tree = Loaded();

        var opened = tree.Find($"{Day}|{RootKey}")!;
        Assert.Single(opened.Children, c => c.IsItem);   // the item made in App itself sits right under App

        var plain = tree.Find($"{Day}|{HistoryTree.FolderKey(@"D:\work\Other")}")!;
        Assert.All(plain.Children, c => Assert.True(c.IsItem));
    }

    [Fact]
    public void WorkingViewIsTheFlatFolderList()
    {
        var tree = Loaded(FolderView.Working);

        var day = tree.Find(Day)!;
        Assert.Equal(5, day.Children.Count);   // App, src\Api, src\Web, tools, Other
        Assert.All(day.Children, c => Assert.False(c.IsOpenedFolder));
        Assert.Equal("Api", tree.Find($"{Day}|{HistoryTree.FolderKey(Root + @"\src\Api")}")!.Label);
    }

    [Fact]
    public void FolderFirstPagesOneOpenedFolderAcrossDays()
    {
        var items = Sample();
        items.Add(TestItems.Item(0, cwd: Root + @"\tools", session: "s1", day: TestItems.Day.AddDays(1)));
        var tree = Loaded(FolderView.FolderFirst, items);

        var opened = tree.Find($"folders|{RootKey}")!;
        Assert.True(opened.IsOpenedFolder);
        Assert.Equal(6, opened.Count);
        Assert.Equal(2, opened.Children.Count);   // two days
        Assert.Equal("App · all days", tree.ScopeLabel(opened));
    }

    [Fact]
    public void ToggleRegroupsWithoutLosingItems()
    {
        var tree = Loaded();
        tree.SetFolderView(FolderView.Working);

        Assert.Equal(6, tree.Folders.Count);
        Assert.Equal(6, tree.Items(tree.Root).Count);
        Assert.Null(tree.Find($"{Day}|{RootKey}|{HistoryTree.FolderKey(Root + @"\tools")}"));

        tree.SetFolderView(FolderView.Opened);
        Assert.Equal(5, tree.Find($"{Day}|{RootKey}")!.Count);
    }

    [Fact]
    public void OldRowsTakeTheirSessionsEarliestFolder()
    {
        var tree = Loaded();

        Assert.Equal(Root, tree.OpenedFolderOf(TestItems.Key(TestItems.Item(3, cwd: Root + @"\tools", session: "s1"))));
        Assert.Equal(@"D:\work\Other", tree.OpenedFolderOf(TestItems.Key(TestItems.Item(5))));
    }

    [Fact]
    public void ASavedOpenedFolderWins()
    {
        var items = new List<HistoryIndex>
        {
            TestItems.Item(0, cwd: Root + @"\src\Api", session: "s9", root: Root),
            TestItems.Item(1, cwd: Root + @"\src\Web", session: "s9"),
        };
        var tree = Loaded(items: items);

        Assert.Equal(Root, tree.OpenedFolderOf(TestItems.Key(items[0])));
        Assert.Equal(Root, tree.OpenedFolderOf(TestItems.Key(items[1])));
        Assert.Equal(2, tree.Find($"{Day}|{RootKey}")!.Count);
    }

    [Fact]
    public void ScopeLabelReadsDownThePath()
    {
        var tree = Loaded();
        var day = tree.Find(Day)!;
        var opened = tree.Find($"{Day}|{RootKey}")!;
        var inner = tree.Find($"{Day}|{RootKey}|{HistoryTree.FolderKey(Root + @"\tools")}")!;

        Assert.Equal($"Folders · {day.Label}", tree.ScopeLabel(day));
        Assert.Equal($"{day.Label} \u203A App", tree.ScopeLabel(opened));
        Assert.Equal($"{day.Label} \u203A App \u203A tools", tree.ScopeLabel(inner));
    }

    [Fact]
    public void ScopeForPrefersTheOpenedFolder()
    {
        var tree = Loaded();
        var newItem = TestItems.Item(9, cwd: Root + @"\src\Web", session: "s1");
        tree.Add(newItem);

        var scope = tree.ScopeFor(TestItems.Key(newItem), tree.Find($"{Day}|{HistoryTree.FolderKey(@"D:\work\Other")}"));

        Assert.Equal($"{Day}|{RootKey}", scope.Key);
    }

    [Fact]
    public void ScopeForKeepsAScopeThatHoldsTheItem()
    {
        var tree = Loaded();
        var current = tree.Find($"{Day}|{RootKey}|{HistoryTree.FolderKey(Root + @"\src\Web")}")!;
        var newItem = TestItems.Item(9, cwd: Root + @"\src\Web", session: "s1");
        tree.Add(newItem);

        Assert.Same(current, tree.ScopeFor(TestItems.Key(newItem), current));
    }

    [Fact]
    public void ScopeForInTheWorkingViewIsTheWorkingFolder()
    {
        var tree = Loaded(FolderView.Working);
        var newItem = TestItems.Item(9, cwd: Root + @"\src\Web", session: "s1");
        tree.Add(newItem);

        Assert.Equal($"{Day}|{HistoryTree.FolderKey(Root + @"\src\Web")}", tree.ScopeFor(TestItems.Key(newItem), null).Key);
    }

    [Fact]
    public void ASavedScopeGoneAfterAToggleFallsBackToTheNearestAncestor()
    {
        var tree = Loaded();
        string innerKey = $"{Day}|{RootKey}|{HistoryTree.FolderKey(Root + @"\tools")}";
        Assert.NotNull(tree.Find(innerKey));

        tree.SetFolderView(FolderView.Working);

        Assert.Equal($"{Day}|{RootKey}", tree.FindOrAncestor(innerKey).Key);   // App had items of its own
        Assert.Equal(Day, tree.FindOrAncestor($"{Day}|d:\\nowhere|x").Key);
        Assert.Same(tree.Root, tree.FindOrAncestor("folders-gone|x"));
        Assert.Same(tree.Root, tree.FindOrAncestor(""));
    }

    [Fact]
    public void TheViewIsSavedInTheSettings()
    {
        var settings = new ClaudePluginSettings();
        Assert.Equal(FolderView.Opened, FolderViews.Parse(settings.HistoryFolderView));

        string json = JsonSerializer.Serialize(settings with { HistoryFolderView = FolderViews.ToSetting(FolderView.Working) });
        var back = JsonSerializer.Deserialize<ClaudePluginSettings>(json)!;

        Assert.Contains("\"historyFolderView\":\"working\"", json);
        Assert.Equal(FolderView.Working, FolderViews.Parse(back.HistoryFolderView));
        Assert.Equal(FolderView.FolderFirst, FolderViews.Parse("folderFirst"));
        Assert.Equal(FolderView.Opened, FolderViews.Parse("nonsense"));
    }

    [Fact]
    public void ATagOnTheOpenedFolderCoversItsInnerFolders()
    {
        string dir = TestItems.TempDir();
        var tags = TagStore.Load(Path.Combine(dir, "tags.json"));
        var tag = tags.Create("work", "#ff8800");
        tags.SetFolder(RootKey, new[] { tag.Id });
        var tree = new HistoryTree { TagStore = tags };
        tree.Load(Sample());

        Assert.Contains(tree.TagsOfItem(TestItems.Key(TestItems.Item(3, cwd: Root + @"\tools"))), t => t.Id == tag.Id);
        Assert.Equal(5, tree.Find($"tags|{tag.Id}")!.Count);
    }
}
