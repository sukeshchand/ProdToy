using ProdToy.Plugins.ClaudeIntegration;
using Xunit;

namespace ProdToy.Tests.Claude;

public class PagerPositionTests
{
    [Theory]
    [InlineData(0, 5, 5)]   // the newest is the last position
    [InlineData(4, 5, 1)]   // the oldest is 1
    [InlineData(2, 5, 3)]
    [InlineData(-1, 5, 0)]
    public void OldestIsOneNewestIsN(int index, int count, int shown)
        => Assert.Equal(shown, PagerPosition.Shown(index, count));

    [Fact]
    public void OlderAndNewerStopAtTheEnds()
    {
        Assert.Equal(4, PagerPosition.Older(4, 5));
        Assert.Equal(0, PagerPosition.Newer(0, 5));
        Assert.False(PagerPosition.CanOlder(4, 5));
        Assert.False(PagerPosition.CanNewer(0, 5));
        Assert.Equal(4, PagerPosition.Oldest(5));
        Assert.Equal(0, PagerPosition.Newest(5));
    }

    [Fact]
    public void TheNumberOfTheItemBeingReadStaysWhenANewItemArrivesInTheScope()
    {
        var tree = new HistoryTree();
        var items = Enumerable.Range(0, 3).Select(i => TestItems.Item(i)).ToList();
        tree.Load(items);
        var scope = tree.Find($"{HistoryTree.FoldersKey}|20261110")!;
        string reading = TestItems.Key(items[2]);

        Assert.Equal("3 / 3", PagerPosition.Label(tree.Items(scope), reading));

        tree.Add(TestItems.Item(3));

        Assert.Equal("3 / 4", PagerPosition.Label(tree.Items(scope), reading));
        Assert.Equal("4 / 4", PagerPosition.Label(tree.Items(scope), TestItems.Key(TestItems.Item(3))));
    }
}
