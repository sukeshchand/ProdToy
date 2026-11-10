using ProdToy.Plugins.Alarm;
using Xunit;

namespace ProdToy.Tests.Alarm;

[Collection(AlarmStoreCollection.Name)]
public class AlarmBodyFilesTests : AlarmStoreTestBase
{
    private AlarmBodyFiles Files => AlarmStore.Bodies;

    [Fact]
    public void WriteBody_Markdown_CreatesTheFolderAndFile()
    {
        var alarm = new AlarmEntry { Title = "t" };
        var result = Files.WriteBody(alarm, AlarmBodyKind.Markdown, "# hi");

        Assert.Equal(AlarmBodyKind.Markdown, result.BodyKind);
        Assert.Equal("body.md", result.BodyPath);
        string file = Path.Combine(DataDir, "alarms", alarm.Id, "body.md");
        Assert.Equal("# hi", File.ReadAllText(file));
        Assert.Equal(file, Files.BodyFileFor(result));
        Assert.Equal("# hi", Files.ReadBody(result));
    }

    [Fact]
    public void WriteBody_SwitchingKind_ReplacesTheBodyFile_AndText_RemovesTheFolder()
    {
        var alarm = new AlarmEntry { Title = "t", Message = "short" };
        var md = Files.WriteBody(alarm, AlarmBodyKind.Markdown, "# hi");
        File.WriteAllText(Path.Combine(Files.FolderFor(alarm.Id), "keep.png"), "x");

        var html = Files.WriteBody(md, AlarmBodyKind.Html, "<p>hi</p>");
        string folder = Files.FolderFor(alarm.Id);
        Assert.False(File.Exists(Path.Combine(folder, "body.md")));
        Assert.True(File.Exists(Path.Combine(folder, "body.html")));
        Assert.True(File.Exists(Path.Combine(folder, "keep.png")));

        var text = Files.WriteBody(html, AlarmBodyKind.Text, "plain body");
        Assert.Equal(AlarmBodyKind.Text, text.BodyKind);
        Assert.Null(text.BodyPath);
        Assert.Equal("plain body", text.Message);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void ImportBodyFile_CopiesSiblingFoldersAndReferencedFiles_SkipsTheRest()
    {
        string src = MakeSourceFolder("page.html", "<img src=\"images/chart.png\"><img src='logo.png'><a href=\"https://x\">x</a>");
        var alarm = new AlarmEntry { Title = "t" };

        var result = Files.ImportBodyFile(alarm, null, Path.Combine(src, "page.html"));

        string folder = Files.FolderFor(alarm.Id);
        Assert.Equal(AlarmBodyKind.Html, result.BodyKind);
        Assert.Equal("body.html", result.BodyPath);
        Assert.True(File.Exists(Path.Combine(folder, "body.html")));
        Assert.True(File.Exists(Path.Combine(folder, "images", "chart.png")));
        Assert.True(File.Exists(Path.Combine(folder, "logo.png")));
        Assert.False(File.Exists(Path.Combine(folder, "unrelated.txt")));
        Assert.False(Directory.Exists(Path.Combine(folder, "node_modules")));
    }

    [Fact]
    public void ImportBodyFile_NeedsAnAbsoluteExistingFile_AndAKnownKind()
    {
        var alarm = new AlarmEntry { Title = "t" };
        Assert.Throws<ArgumentException>(() => Files.ImportBodyFile(alarm, null, "relative.md"));
        Assert.Throws<FileNotFoundException>(() => Files.ImportBodyFile(alarm, null, Path.Combine(Root, "missing.md")));
        string odd = Path.Combine(Root, "body.xyz");
        File.WriteAllText(odd, "x");
        Assert.Throws<ArgumentException>(() => Files.ImportBodyFile(alarm, null, odd));
        Assert.Equal(AlarmBodyKind.Markdown, Files.ImportBodyFile(alarm, AlarmBodyKind.Markdown, odd).BodyKind);
    }

    [Fact]
    public void CopyResources_CopiesFilesAndFolders()
    {
        string src = MakeSourceFolder("a.md", "x");
        var alarm = Files.WriteBody(new AlarmEntry(), AlarmBodyKind.Markdown, "x");
        Files.CopyResources(alarm.Id, new[] { Path.Combine(src, "logo.png"), Path.Combine(src, "images") });
        string folder = Files.FolderFor(alarm.Id);
        Assert.True(File.Exists(Path.Combine(folder, "logo.png")));
        Assert.True(File.Exists(Path.Combine(folder, "images", "chart.png")));
    }

    [Fact]
    public void ResolveInFolder_NeverLeavesTheFolder()
    {
        var alarm = Files.WriteBody(new AlarmEntry(), AlarmBodyKind.Html, "<p/>");
        string folder = Files.FolderFor(alarm.Id);
        File.WriteAllText(Path.Combine(DataDir, "alarms.json"), "[]");

        Assert.Equal(Path.Combine(folder, "body.html"), AlarmBodyFiles.ResolveInFolder(folder, "/body.html"));
        Assert.Null(AlarmBodyFiles.ResolveInFolder(folder, "/../../alarms.json"));
        Assert.Null(AlarmBodyFiles.ResolveInFolder(folder, "/%2e%2e/%2e%2e/alarms.json"));
        Assert.Null(AlarmBodyFiles.ResolveInFolder(folder, "/missing.png"));
        Assert.Null(AlarmBodyFiles.ResolveInFolder(folder, "/"));
    }

    [Fact]
    public void FolderFor_RejectsUnsafeIds()
    {
        Assert.Throws<ArgumentException>(() => Files.FolderFor("..\\x"));
        Assert.Throws<ArgumentException>(() => Files.FolderFor(""));
        Assert.False(Files.HasFolder("../x"));
    }

    [Theory]
    [InlineData("a.md", "Markdown")]
    [InlineData("a.MARKDOWN", "Markdown")]
    [InlineData("a.htm", "Html")]
    [InlineData("a.txt", "Text")]
    [InlineData("a.png", null)]
    public void KindFromExtension(string name, string? kind) =>
        Assert.Equal(kind, AlarmBodyFiles.KindFromExtension(name)?.ToString());

    [Fact]
    public void SummaryOf_TakesTheFirstTextLine_OrTheHtmlTitle()
    {
        Assert.Equal("Deploy check", AlarmBodyFiles.SummaryOf(AlarmBodyKind.Markdown, "\n# Deploy check\n| a |"));
        Assert.Equal("Page & more", AlarmBodyFiles.SummaryOf(AlarmBodyKind.Html, "<title>Page &amp; more</title>"));
    }

    [Fact]
    public void SnapshotForHistory_CopiesSmallBodies_SkipsBigOnes()
    {
        var alarm = Files.WriteBody(new AlarmEntry(), AlarmBodyKind.Markdown, "# small");
        string? copy = Files.SnapshotForHistory(alarm, new DateTime(2026, 1, 2, 3, 4, 5));
        Assert.NotNull(copy);
        Assert.Equal("# small", File.ReadAllText(Path.Combine(DataDir, copy!)));

        File.WriteAllBytes(Files.BodyFileFor(alarm)!, new byte[AlarmBodyFiles.HistoryCopyLimitBytes + 1]);
        Assert.Null(Files.SnapshotForHistory(alarm, DateTime.Now));
        Assert.Null(Files.SnapshotForHistory(new AlarmEntry { Message = "text" }, DateTime.Now));
    }
}
