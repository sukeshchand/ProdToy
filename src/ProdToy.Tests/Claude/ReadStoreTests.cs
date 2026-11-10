using ProdToy.Plugins.ClaudeIntegration;
using Xunit;

namespace ProdToy.Tests.Claude;

public class ReadStoreTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0);

    [Fact]
    public void FirstStartHasNothingNewAndStartsNow()
    {
        string path = Path.Combine(TestItems.TempDir(), "read-marks.json");

        var store = ReadStore.Load(path, Now);

        Assert.Equal(0, store.Count);
        Assert.Equal(Now, store.Since);
        Assert.False(store.IsUnread("20261109--env.json#3"));   // everything older counts as Read
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void UnreadMarksSurviveARestart()
    {
        string path = Path.Combine(TestItems.TempDir(), "read-marks.json");
        var store = ReadStore.Load(path, Now);
        store.MarkUnread("a#1");
        store.MarkUnread("a#2");
        store.MarkUnread("a#2");
        store.MarkRead("a#1");

        var again = ReadStore.Load(path, Now.AddHours(1));

        Assert.Equal(new[] { "a#2" }, again.Unread);
        Assert.True(again.IsUnread("a#2"));
        Assert.False(again.IsUnread("a#1"));
        Assert.Equal(Now, again.Since);
    }

    [Fact]
    public void PruneDropsKeysTheHistoryNoLongerHolds()
    {
        string path = Path.Combine(TestItems.TempDir(), "read-marks.json");
        var store = ReadStore.Load(path, Now);
        store.MarkUnread("old#1");
        store.MarkUnread("kept#2");

        int removed = store.Prune(new[] { "kept#2", "other#9" });

        Assert.Equal(1, removed);
        Assert.Equal(new[] { "kept#2" }, ReadStore.Load(path, Now).Unread);
    }

    [Fact]
    public void MarkAllReadEmptiesTheList()
    {
        var store = ReadStore.Load(Path.Combine(TestItems.TempDir(), "read-marks.json"), Now);
        store.MarkUnread("a#1");
        store.MarkUnread("a#2");
        int changes = 0;
        store.Changed += () => changes++;

        store.MarkAllRead();

        Assert.Equal(0, store.Count);
        Assert.Equal(1, changes);
    }
}
