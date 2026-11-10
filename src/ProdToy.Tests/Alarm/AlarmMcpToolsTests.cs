using System.Text.Json;
using System.Text.Json.Nodes;
using ProdToy.Plugins.Alarm;
using ProdToy.Sdk;
using Xunit;

namespace ProdToy.Tests.Alarm;

[Collection(AlarmStoreCollection.Name)]
public class AlarmMcpToolsTests : AlarmStoreTestBase
{
    private static PipeCommand Cmd(object payload) => new("mcp", JsonSerializer.Serialize(payload));

    private static JsonObject Json(string response) => JsonNode.Parse(response)!.AsObject();

    private static void AssertOk(string response) =>
        Assert.True(Json(response)["ok"]!.GetValue<bool>(), response);

    private static string AssertFails(string response)
    {
        var o = Json(response);
        Assert.False(o["ok"]!.GetValue<bool>(), response);
        return o["message"]!.GetValue<string>();
    }

    private static AlarmEntry Only() => Assert.Single(AlarmStore.LoadAlarms());

    [Fact]
    public void Create_WithMarkdownBody_WritesTheFolder_AndSummarisesTheMessage()
    {
        var r = AlarmMcpTools.Create(Cmd(new
        {
            title = "Report",
            time = "08:30",
            schedule = "daily",
            body = "# Weekly numbers\n\n| a | b |\n|---|---|\n| 1 | 2 |",
        }), alarmsEnabled: true);
        AssertOk(r);

        var a = Only();
        Assert.Equal(AlarmBodyKind.Markdown, a.BodyKind);
        Assert.Equal("body.md", a.BodyPath);
        Assert.Equal("Weekly numbers", a.Message);
        Assert.Equal(Path.Combine(DataDir, "alarms", a.Id), Json(r)["folder"]!.GetValue<string>());
        Assert.Contains("| 1 | 2 |", File.ReadAllText(Path.Combine(DataDir, "alarms", a.Id, "body.md")));
    }

    [Fact]
    public void Create_WithoutBody_IsAPlainTextAlarm_WithNoFolder()
    {
        AssertOk(AlarmMcpTools.Create(Cmd(new { title = "Tea", inMinutes = 5, message = "Tea is ready" }), true));
        var a = Only();
        Assert.Equal(AlarmBodyKind.Text, a.BodyKind);
        Assert.Equal("Tea is ready", a.Message);
        Assert.False(Directory.Exists(Path.Combine(DataDir, "alarms")));
    }

    [Fact]
    public void Create_WithBodyFile_CopiesTheFileAndItsResources()
    {
        string src = MakeSourceFolder("report.html", "<title>Numbers</title><img src=\"images/chart.png\"><img src=\"logo.png\">");
        string extra = Path.Combine(Root, "extra.css");
        File.WriteAllText(extra, "body{}");

        AssertOk(AlarmMcpTools.Create(Cmd(new
        {
            title = "Html report",
            inMinutes = 10,
            bodyFile = Path.Combine(src, "report.html"),
            resources = new[] { extra },
        }), true));

        var a = Only();
        string folder = Path.Combine(DataDir, "alarms", a.Id);
        Assert.Equal(AlarmBodyKind.Html, a.BodyKind);
        Assert.Equal("Numbers", a.Message);
        Assert.True(File.Exists(Path.Combine(folder, "body.html")));
        Assert.True(File.Exists(Path.Combine(folder, "images", "chart.png")));
        Assert.True(File.Exists(Path.Combine(folder, "logo.png")));
        Assert.True(File.Exists(Path.Combine(folder, "extra.css")));
    }

    [Fact]
    public void Create_BodyAndBodyFileTogether_Fails_AndLeavesNoFolder()
    {
        string msg = AssertFails(AlarmMcpTools.Create(Cmd(new { title = "x", inMinutes = 1, body = "a", bodyFile = "c:\\x.md" }), true));
        Assert.Contains("either", msg);
        Assert.Empty(AlarmStore.LoadAlarms());
        Assert.False(Directory.Exists(Path.Combine(DataDir, "alarms")) && Directory.EnumerateDirectories(Path.Combine(DataDir, "alarms")).Any());
    }

    [Fact]
    public void Create_MapsTheOptionFields()
    {
        AssertOk(AlarmMcpTools.Create(Cmd(new
        {
            title = "Opts", inMinutes = 3, note = "private", notification = "balloon", sound = false,
            fireAndForget = true, priority = "high", snoozeMinutes = 15, category = "work",
        }), true));
        var a = Only();
        Assert.Equal("private", a.Note);
        Assert.Equal(AlarmNotificationMode.Windows, a.Notification);
        Assert.False(a.SoundEnabled);
        Assert.True(a.FireAndForget);
        Assert.Equal(AlarmPriority.High, a.Priority);
        Assert.Equal(15, a.SnoozeMinutes);
        Assert.Equal("work", a.Category);
    }

    [Fact]
    public void Update_DaysOnly_ChangesTheDays_AndNothingElse()
    {
        AssertOk(AlarmMcpTools.Create(Cmd(new
        {
            title = "Gym", time = "18:00", schedule = "custom", days = new[] { "Monday" },
            body = "# Gym", priority = "high", note = "n",
        }), true));
        var before = Only();

        AssertOk(AlarmMcpTools.Update(Cmd(new { name = "Gym", days = new[] { "Tuesday", "Thursday" } })));

        var after = Only();
        Assert.Equal(new[] { DayOfWeek.Tuesday, DayOfWeek.Thursday }, after.Schedule.CustomDays);
        Assert.Equal(AlarmScheduleType.Custom, after.Schedule.Type);
        Assert.Equal("18:00", after.Schedule.TimeOfDay);
        Assert.Equal(before.Title, after.Title);
        Assert.Equal(before.Message, after.Message);
        Assert.Equal(before.Priority, after.Priority);
        Assert.Equal(before.Note, after.Note);
        Assert.Equal(before.BodyKind, after.BodyKind);
        Assert.Equal("# Gym", AlarmStore.Bodies.ReadBody(after));
    }

    [Fact]
    public void Update_Body_ReplacesTheFile_AndKeepsTheSchedule()
    {
        AssertOk(AlarmMcpTools.Create(Cmd(new { title = "R", time = "07:00", schedule = "weekdays", body = "# old" }), true));
        AssertOk(AlarmMcpTools.Update(Cmd(new { name = "R", body = "# new" })));

        var a = Only();
        Assert.Equal("# new", AlarmStore.Bodies.ReadBody(a));
        Assert.Equal(AlarmScheduleType.Weekdays, a.Schedule.Type);
        Assert.Equal("07:00", a.Schedule.TimeOfDay);

        AssertOk(AlarmMcpTools.Update(Cmd(new { name = "R", bodyKind = "html", body = "<p>new</p>" })));
        a = Only();
        Assert.Equal(AlarmBodyKind.Html, a.BodyKind);
        Assert.False(File.Exists(Path.Combine(DataDir, "alarms", a.Id, "body.md")));

        AssertOk(AlarmMcpTools.Update(Cmd(new { name = "R", bodyKind = "text" })));
        a = Only();
        Assert.Equal(AlarmBodyKind.Text, a.BodyKind);
        Assert.False(Directory.Exists(Path.Combine(DataDir, "alarms", a.Id)));
    }

    [Fact]
    public void Update_ScheduleFields_AndValidation()
    {
        AssertOk(AlarmMcpTools.Create(Cmd(new { title = "S", time = "09:00", schedule = "daily" }), true));

        AssertOk(AlarmMcpTools.Update(Cmd(new { name = "S", schedule = "monthly", dayOfMonth = 12, startDate = "2026-01-01", endDate = "2027-01-01" })));
        var a = Only();
        Assert.Equal(AlarmScheduleType.Monthly, a.Schedule.Type);
        Assert.Equal(12, a.Schedule.DayOfMonth);
        Assert.Equal("2026-01-01", a.StartDate);
        Assert.Equal("2027-01-01", a.EndDate);

        AssertOk(AlarmMcpTools.Update(Cmd(new { name = "S", schedule = "interval", intervalMinutes = 45 })));
        a = Only();
        Assert.Equal(45, a.Schedule.IntervalMinutes);
        Assert.Null(a.Schedule.DayOfMonth);

        Assert.Contains("only applies", AssertFails(AlarmMcpTools.Update(Cmd(new { name = "S", days = new[] { "Monday" } }))));
        Assert.Contains("need 'days'", AssertFails(AlarmMcpTools.Update(Cmd(new { name = "S", schedule = "weekly" }))));
        AssertFails(AlarmMcpTools.Update(Cmd(new { name = "S", notification = "loud" })));
        AssertFails(AlarmMcpTools.Update(Cmd(new { name = "S", bodyKind = "markdown" })));
        AssertFails(AlarmMcpTools.Update(Cmd(new { name = "S", resources = new[] { "c:\\x.png" } })));
        Assert.Equal(45, Only().Schedule.IntervalMinutes);

        AssertOk(AlarmMcpTools.Update(Cmd(new { name = "S", sound = false, fireAndForget = true, notification = "popup", note = "hi", endDate = "" })));
        a = Only();
        Assert.False(a.SoundEnabled);
        Assert.True(a.FireAndForget);
        Assert.Equal(AlarmNotificationMode.Popup, a.Notification);
        Assert.Equal("hi", a.Note);
        Assert.Null(a.EndDate);
    }

    [Fact]
    public void Preview_ShowsTheAlarm_AndWritesNoHistory()
    {
        AssertOk(AlarmMcpTools.Create(Cmd(new { title = "P", inMinutes = 30, body = "# p" }), true));
        AlarmStore.FlushHistoryNow();
        int before = AlarmStore.LoadHistory().Count;

        AlarmEntry? shown = null;
        AssertOk(AlarmMcpTools.Preview(Cmd(new { name = "P" }), a => shown = a));
        AlarmStore.FlushHistoryNow();

        Assert.Equal("P", shown?.Title);
        Assert.Equal(before, AlarmStore.LoadHistory().Count);
        Assert.Equal(0, Only().TriggerCount);
    }

    [Fact]
    public void Get_ReturnsEveryField_TheBodyAndTheFolder()
    {
        AssertOk(AlarmMcpTools.Create(Cmd(new { title = "G", inMinutes = 30, body = "# hello\n![x](img.png)", note = "nn" }), true));
        var id = Only().Id;
        File.WriteAllText(Path.Combine(DataDir, "alarms", id, "img.png"), "x");

        var o = Json(AlarmMcpTools.Get(Cmd(new { name = id })))["alarm"]!.AsObject();
        Assert.Equal("markdown", o["bodyKind"]!.GetValue<string>());
        Assert.Equal("# hello\n![x](img.png)", o["body"]!.GetValue<string>());
        Assert.Equal(Path.Combine(DataDir, "alarms", id), o["folder"]!.GetValue<string>());
        Assert.Equal(Path.Combine(DataDir, "alarms", id, "body.md"), o["bodyFile"]!.GetValue<string>());
        Assert.Equal("nn", o["note"]!.GetValue<string>());
        Assert.Contains("img.png", o["files"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("once", o["schedule"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void List_ShowsTheBodyKind()
    {
        AssertOk(AlarmMcpTools.Create(Cmd(new { title = "A", inMinutes = 30, body = "<p/>", bodyKind = "html" }), true));
        var alarms = Json(AlarmMcpTools.List(Cmd(new { })))["alarms"]!.AsArray();
        Assert.Equal("html", alarms.Single()!["bodyKind"]!.GetValue<string>());
    }

    [Fact]
    public void Delete_RemovesTheFolder()
    {
        AssertOk(AlarmMcpTools.Create(Cmd(new { title = "D", inMinutes = 30, body = "# d" }), true));
        var id = Only().Id;
        AssertOk(AlarmMcpTools.Delete(Cmd(new { name = "D" })));
        Assert.False(Directory.Exists(Path.Combine(DataDir, "alarms", id)));
    }
}
