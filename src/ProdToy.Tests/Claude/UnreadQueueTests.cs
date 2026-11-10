using ProdToy.Plugins.ClaudeIntegration;
using Xunit;

namespace ProdToy.Tests.Claude;

public class UnreadQueueTests
{
    private static UnreadQueue NewQueue()
        => new(ReadStore.Load(Path.Combine(TestItems.TempDir(), "read-marks.json"), TestItems.Day));

    private static readonly Dictionary<string, DateTime> Times = new()
    {
        ["d#1"] = TestItems.Day.AddMinutes(1),
        ["d#2"] = TestItems.Day.AddMinutes(2),
        ["d#3"] = TestItems.Day.AddMinutes(3),
    };

    private static DateTime? TimeOf(string key) => Times.TryGetValue(key, out var t) ? t : null;

    [Fact]
    public void ANewItemWhileReadingANewOneIsQueued()
    {
        var queue = NewQueue();
        queue.Arrive("d#1", popupAllowed: true, windowShown: false, shownKey: null);

        var action = queue.Arrive("d#2", popupAllowed: true, windowShown: true, shownKey: "d#1");

        Assert.Equal(ArrivalAction.Queue, action);
        Assert.True(queue.IsNew("d#1"));
        Assert.True(queue.IsNew("d#2"));
    }

    [Fact]
    public void ANewItemWhileAReadOneIsOnScreenIsShown()
    {
        var queue = NewQueue();
        queue.Arrive("d#1", true, false, null);
        queue.Ok("d#1", TimeOf);

        Assert.Equal(ArrivalAction.Show, queue.Arrive("d#2", true, windowShown: true, shownKey: "d#1"));
    }

    [Fact]
    public void AHiddenWindowPopsUpOnTheNewItemAndTheOlderOnesStayNew()
    {
        var queue = NewQueue();
        queue.Arrive("d#1", true, false, null);
        queue.Arrive("d#2", true, true, "d#1");

        var action = queue.Arrive("d#3", true, windowShown: false, shownKey: "d#1");

        Assert.Equal(ArrivalAction.Show, action);
        Assert.Equal(new[] { "d#3", "d#2", "d#1" }, queue.NewestFirst(TimeOf));
    }

    [Fact]
    public void SnoozedOrSuppressedIsSavedAsNewWithoutAPopUp()
    {
        var queue = NewQueue();

        Assert.Equal(ArrivalAction.Silent, queue.Arrive("d#1", popupAllowed: false, windowShown: false, shownKey: null));
        Assert.Equal(ArrivalAction.Silent, queue.Arrive("d#2", popupAllowed: false, windowShown: true, shownKey: "d#1"));
        Assert.True(queue.IsNew("d#1"));
        Assert.True(queue.IsNew("d#2"));
    }

    [Fact]
    public void NothingSavedIsShownAsBefore()
    {
        var queue = NewQueue();
        queue.Arrive("d#1", true, false, null);

        Assert.Equal(ArrivalAction.Show, queue.Arrive(null, true, true, "d#1"));
        Assert.Equal(ArrivalAction.Show, queue.Arrive(null, true, false, "d#1"));
        Assert.Equal(ArrivalAction.Silent, queue.Arrive(null, false, true, "d#1"));
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void OkIsTheOnlyPathToRead()
    {
        var queue = NewQueue();
        queue.Arrive("d#1", true, false, null);
        queue.Arrive("d#2", true, true, "d#1");

        // Paging, a chip or the tree only change what is on screen: arrivals and decisions mark nothing Read.
        UnreadQueue.Decide(true, true, shownIsNew: true);
        Assert.True(queue.IsNew("d#1"));

        string? next = queue.Ok("d#1", TimeOf);

        Assert.False(queue.IsNew("d#1"));
        Assert.Equal("d#2", next);
        Assert.True(queue.IsNew("d#2"));
    }

    [Fact]
    public void OkOnTheLastNewItemClosesTheWindow()
    {
        var queue = NewQueue();
        queue.Arrive("d#1", true, false, null);

        Assert.Null(queue.Ok("d#1", TimeOf));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void OkShowsTheNewestNewItemNext()
    {
        var queue = NewQueue();
        queue.Arrive("d#1", true, false, null);
        queue.Arrive("d#3", true, true, "d#1");
        queue.Arrive("d#2", true, true, "d#1");

        Assert.Equal("d#3", queue.Ok("d#1", TimeOf));
    }

    [Fact]
    public void MarkAllReadClearsEveryChip()
    {
        var queue = NewQueue();
        queue.Arrive("d#1", true, false, null);
        queue.Arrive("d#2", true, true, "d#1");

        queue.MarkAllRead();

        Assert.Empty(queue.NewestFirst(TimeOf));
    }

    [Fact]
    public void KeysTheHistoryDoesNotKnowGetNoChip()
    {
        var queue = NewQueue();
        queue.Arrive("gone#7", true, false, null);
        queue.Arrive("d#1", true, false, null);

        Assert.Equal(new[] { "d#1" }, queue.NewestFirst(TimeOf));
    }
}
