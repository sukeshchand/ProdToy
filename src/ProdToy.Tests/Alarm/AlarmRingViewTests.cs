using System.Drawing;
using ProdToy.Plugins.Alarm;
using Xunit;

namespace ProdToy.Tests.Alarm;

[Collection(AlarmStoreCollection.Name)]
public class AlarmRingViewTests : AlarmStoreTestBase
{
    [Fact]
    public void Prepare_Text_ShowsTheMessage()
    {
        var c = AlarmBodyView.Prepare(AlarmStore.Bodies, new AlarmEntry { Message = "hello" }, DarkTheme);
        Assert.Equal(AlarmBodyKind.Text, c.Kind);
        Assert.Equal("hello", c.Text);
        Assert.Null(c.Folder);
    }

    [Fact]
    public void Prepare_Markdown_RendersFromTheFile_WithTheFolderForImages()
    {
        var alarm = AlarmStore.Bodies.WriteBody(new AlarmEntry(), AlarmBodyKind.Markdown, "![p](images/p.png)");
        var c = AlarmBodyView.Prepare(AlarmStore.Bodies, alarm, DarkTheme);
        Assert.Equal(AlarmBodyKind.Markdown, c.Kind);
        Assert.Contains("<img src=\"images/p.png\"", c.Html);
        Assert.Equal(AlarmStore.Bodies.FolderFor(alarm.Id), c.Folder);
        Assert.EndsWith(AlarmBodyView.MarkdownPage, AlarmBodyView.StartUrl(c));
    }

    [Fact]
    public void Prepare_Html_IsServedFromItsFile()
    {
        var alarm = AlarmStore.Bodies.WriteBody(new AlarmEntry(), AlarmBodyKind.Html, "<p>x</p>");
        var c = AlarmBodyView.Prepare(AlarmStore.Bodies, alarm, DarkTheme);
        Assert.Equal("body.html", c.EntryFile);
        Assert.Equal(AlarmBodyView.BaseUrl + "body.html", AlarmBodyView.StartUrl(c));
    }

    [Fact]
    public void Prepare_UnsavedContent_FromTheEditForm_WinsOverTheFile()
    {
        var alarm = AlarmStore.Bodies.WriteBody(new AlarmEntry(), AlarmBodyKind.Html, "<p>saved</p>");
        var c = AlarmBodyView.Prepare(AlarmStore.Bodies, alarm, DarkTheme, AlarmBodyKind.Html, "<p>draft</p>");
        Assert.Null(c.EntryFile);
        Assert.Equal("<p>draft</p>", c.Html);
    }

    [Fact]
    public void Prepare_MissingFile_FallsBackToText()
    {
        var alarm = new AlarmEntry { Message = "summary", BodyKind = AlarmBodyKind.Markdown, BodyPath = "body.md" };
        var c = AlarmBodyView.Prepare(AlarmStore.Bodies, alarm, DarkTheme);
        Assert.Equal(AlarmBodyKind.Text, c.Kind);
        Assert.StartsWith("summary", c.Text);
        Assert.Contains("missing", c.Text);
    }

    [Theory]
    [InlineData(1, "1st")]
    [InlineData(2, "2nd")]
    [InlineData(3, "3rd")]
    [InlineData(4, "4th")]
    [InlineData(11, "11th")]
    [InlineData(12, "12th")]
    [InlineData(13, "13th")]
    [InlineData(21, "21st")]
    [InlineData(112, "112th")]
    public void Ordinal(int n, string expected) => Assert.Equal(expected, AlarmRingText.Ordinal(n));

    [Fact]
    public void ScheduleLine_CountsTheFiringAboutToHappen()
    {
        var alarm = new AlarmEntry { Schedule = new AlarmSchedule { Type = AlarmScheduleType.Daily, TimeOfDay = "02:00" }, TriggerCount = 2 };
        Assert.Equal("Every day · 02:00 · 3rd firing", AlarmRingText.ScheduleLine(alarm, preview: false));
        Assert.EndsWith("preview", AlarmRingText.ScheduleLine(alarm, preview: true));
    }

    [Fact]
    public void Placement_RoundTripsPerScreen()
    {
        var p = new RingWindowPlacement(DataDir);
        var screenA = new Rectangle(0, 0, 1920, 1080);
        var screenB = new Rectangle(1920, 0, 2560, 1440);
        p.Save(screenA, new Rectangle(100, 100, 600, 500));
        p.Save(screenB, new Rectangle(2000, 50, 900, 700));

        var again = new RingWindowPlacement(DataDir);
        Assert.Equal(new Rectangle(100, 100, 600, 500), again.Load(screenA));
        Assert.Equal(new Rectangle(2000, 50, 900, 700), again.Load(screenB));
        Assert.Null(again.Load(new Rectangle(0, 0, 800, 600)));
    }

    [Fact]
    public void Fit_ClampsToTheWorkArea_AndTheMinimumSize()
    {
        var work = new Rectangle(0, 0, 1920, 1040);
        Assert.Equal(new Rectangle((1920 - 560) / 2, (1040 - 460) / 2, 560, 460), RingWindowPlacement.Fit(null, work));
        Assert.Equal(new Rectangle(10, 10, 420, 320), RingWindowPlacement.Fit(new Rectangle(10, 10, 100, 100), work));
        Assert.Equal(new Rectangle(1920 - 800, 1040 - 600, 800, 600), RingWindowPlacement.Fit(new Rectangle(5000, 5000, 800, 600), work));
        Assert.Equal(new Rectangle(0, 0, 1920, 1040), RingWindowPlacement.Fit(new Rectangle(-50, -50, 4000, 3000), work));
    }
}
