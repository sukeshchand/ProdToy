using ProdToy.Plugins.ClaudeIntegration;
using Xunit;

namespace ProdToy.Tests.Claude;

public class ProjectRootResolverTests
{
    [Fact]
    public void TheTranscriptsFirstCwdIsTheOpenedFolder()
    {
        string dir = Path.Combine(TestItems.TempDir(), "D--work-App");
        Directory.CreateDirectory(dir);
        string transcript = Path.Combine(dir, "session.jsonl");
        File.WriteAllLines(transcript, new[]
        {
            "{\"type\":\"summary\",\"summary\":\"x\"}",
            "{\"type\":\"user\",\"cwd\":\"D:\\\\work\\\\App\",\"sessionId\":\"s1\"}",
            "{\"type\":\"user\",\"cwd\":\"D:\\\\work\\\\App\\\\src\\\\Api\",\"sessionId\":\"s1\"}",
        });

        Assert.Equal(@"D:\work\App", ProjectRootResolver.Resolve(transcript));
    }

    [Fact]
    public void WithoutATranscriptTheKeyIsMatchedAgainstRealFolders()
    {
        string baseDir = TestItems.TempDir();
        string app = Path.Combine(baseDir, "My.App");
        Directory.CreateDirectory(Path.Combine(app, "src"));
        Directory.CreateDirectory(Path.Combine(baseDir, "My"));   // a shorter name that must not win
        string key = ProjectRootResolver.Encode(app);

        Assert.Equal(app, ProjectRootResolver.Decode(key), ignoreCase: true);
        Assert.Equal(app, ProjectRootResolver.Resolve(Path.Combine(baseDir, "projects", key, "gone.jsonl")), ignoreCase: true);
    }

    [Fact]
    public void AKeyNoFolderMatchesStaysTheLabel()
    {
        const string key = "Q--no-such-folder-anywhere";

        Assert.Null(ProjectRootResolver.Decode(key));
        Assert.Equal(key, ProjectRootResolver.FromProjectKey(key));
        Assert.Equal("not-a-drive-key", ProjectRootResolver.FromProjectKey("not-a-drive-key"));
    }

    [Fact]
    public void NoTranscriptMeansNoFolder()
    {
        Assert.Equal("", ProjectRootResolver.Resolve(null));
        Assert.Equal("", ProjectRootResolver.Resolve("  "));
    }

    [Fact]
    public void TheKeyIsThePathWithEveryOtherCharacterADash()
        => Assert.Equal("C--Users-x-Repo-My-App", ProjectRootResolver.Encode(@"C:\Users\x\Repo\My.App"));
}
