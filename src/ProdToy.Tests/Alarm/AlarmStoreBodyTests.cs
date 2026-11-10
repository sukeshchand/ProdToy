using System.Text.Json;
using ProdToy.Plugins.Alarm;
using Xunit;

namespace ProdToy.Tests.Alarm;

[Collection(AlarmStoreCollection.Name)]
public class AlarmStoreBodyTests : AlarmStoreTestBase
{
    [Fact]
    public void OldAlarmsFile_LoadsUnchanged_AsTextWithNoFolder()
    {
        // An alarms.json written before bodies existed: no bodyKind / bodyPath.
        File.WriteAllText(Path.Combine(DataDir, "alarms.json"), """
            [
              {
                "id": "abc123",
                "title": "Stand-up",
                "message": "Daily stand-up in 5 minutes",
                "note": "",
                "schedule": { "type": 2, "timeOfDay": "09:55", "oneTimeDate": null, "dayOfMonth": null, "intervalMinutes": null, "customDays": null },
                "status": 0,
                "notification": 2,
                "snoozeMinutes": 5,
                "soundEnabled": true,
                "fireAndForget": false,
                "createdAt": "2026-01-01T09:00:00",
                "triggerCount": 3
              }
            ]
            """);
        AlarmStore.Invalidate();

        var alarm = Assert.Single(AlarmStore.LoadAlarms());
        Assert.Equal("Stand-up", alarm.Title);
        Assert.Equal("Daily stand-up in 5 minutes", alarm.Message);
        Assert.Equal(AlarmScheduleType.Weekdays, alarm.Schedule.Type);
        Assert.Equal(3, alarm.TriggerCount);
        Assert.Equal(AlarmBodyKind.Text, alarm.BodyKind);
        Assert.Null(alarm.BodyPath);
        Assert.False(AlarmStore.Bodies.HasFolder(alarm.Id));
        Assert.Null(AlarmStore.Bodies.BodyFileFor(alarm));
        Assert.Equal("Daily stand-up in 5 minutes", AlarmStore.Bodies.ReadBody(alarm));
    }

    [Fact]
    public void BodyFields_RoundTripThroughTheFile()
    {
        var alarm = AlarmStore.Bodies.WriteBody(new AlarmEntry { Title = "rich" }, AlarmBodyKind.Html, "<p>x</p>");
        AlarmStore.AddAlarm(alarm);
        AlarmStore.Invalidate();

        var loaded = Assert.Single(AlarmStore.LoadAlarms());
        Assert.Equal(AlarmBodyKind.Html, loaded.BodyKind);
        Assert.Equal("body.html", loaded.BodyPath);
    }

    [Fact]
    public void DeleteAlarm_RemovesItsFolder()
    {
        var alarm = AlarmStore.Bodies.WriteBody(new AlarmEntry { Title = "rich" }, AlarmBodyKind.Markdown, "# x");
        AlarmStore.AddAlarm(alarm);
        Assert.True(AlarmStore.Bodies.HasFolder(alarm.Id));

        AlarmStore.DeleteAlarm(alarm.Id);

        Assert.False(AlarmStore.Bodies.HasFolder(alarm.Id));
        Assert.Empty(AlarmStore.LoadAlarms());
    }

    [Fact]
    public void FiringEntry_KeepsTheKindAndACopyOfTheBody()
    {
        var alarm = AlarmStore.Bodies.WriteBody(new AlarmEntry { Title = "rich" }, AlarmBodyKind.Markdown, "# fired");
        var entry = AlarmStore.FiringEntry(alarm, AlarmHistoryEventType.Triggered, "now");

        Assert.Equal(AlarmBodyKind.Markdown, entry.BodyKind);
        Assert.NotNull(entry.BodyCopy);
        Assert.Equal("# fired", File.ReadAllText(Path.Combine(DataDir, entry.BodyCopy!)));

        var text = AlarmStore.FiringEntry(new AlarmEntry { Title = "plain" }, AlarmHistoryEventType.Triggered, "now");
        Assert.Equal(AlarmBodyKind.Text, text.BodyKind);
        Assert.Null(text.BodyCopy);
    }

    [Fact]
    public void HistoryRows_WithoutBody_DoNotWriteTheNewFields()
    {
        AlarmStore.AddHistoryEntry(new AlarmHistoryEntry { AlarmId = "a", AlarmTitle = "a", EventType = AlarmHistoryEventType.Dismissed });
        AlarmStore.FlushHistoryNow();
        string json = File.ReadAllText(Path.Combine(DataDir, "alarm-history.json"));
        Assert.DoesNotContain("bodyKind", json);
        Assert.DoesNotContain("bodyCopy", json);
    }

    [Fact]
    public void TrimmedHistoryRows_DeleteTheirBodyCopies()
    {
        AlarmStore.Initialize(DataDir, () => 1);
        var alarm = AlarmStore.Bodies.WriteBody(new AlarmEntry { Title = "rich" }, AlarmBodyKind.Markdown, "# one");
        var first = AlarmStore.FiringEntry(alarm, AlarmHistoryEventType.Triggered, "1");
        Thread.Sleep(5);
        var second = AlarmStore.FiringEntry(alarm, AlarmHistoryEventType.Triggered, "2");
        AlarmStore.AddHistoryEntry(first);
        AlarmStore.AddHistoryEntry(second);
        AlarmStore.FlushHistoryNow();

        Assert.False(File.Exists(Path.Combine(DataDir, first.BodyCopy!)));
        Assert.True(File.Exists(Path.Combine(DataDir, second.BodyCopy!)));
        var history = JsonSerializer.Deserialize<List<AlarmHistoryEntry>>(File.ReadAllText(Path.Combine(DataDir, "alarm-history.json")))!;
        Assert.Equal("2", Assert.Single(history).Detail);
    }
}
