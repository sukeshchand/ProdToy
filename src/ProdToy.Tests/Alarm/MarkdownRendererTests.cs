using ProdToy.Plugins.Alarm;
using ProdToy.Sdk.Rendering;
using Xunit;

namespace ProdToy.Tests.Alarm;

public class MarkdownRendererTests
{
    private const string Sample = """
        # Deploy check
        Some **bold**, *italic*, __strong__, _em_ and `code`.

        | Step | State |
        |------|-------|
        | build | ok |
        | test | **red** |

        - one
        - two
        1. first
        2. second

        ---
        ```
        <script>alert(1)</script>
        ```
        ![chart](images/chart.png) and [docs](https://example.com)
        """;

    private static string Norm(string s) => s.Replace("\r\n", "\n");

    [Fact]
    public void ClaudeWrapper_IsByteForByteTheOldRenderer()
    {
        foreach (var md in new[] { Sample, "", "plain line", "```\nunclosed" })
        {
            string legacy = LegacyChatMarkdownRenderer.ToHtml(md, "#3884f4", "#a0afd2", "#ebf0ff", "#101018", "rgba(0,0,0,0.06)", "#ff8800");
            string now = ProdToy.Plugins.ClaudeIntegration.ChatMarkdownRenderer.ToHtml(md, "#3884f4", "#a0afd2", "#ebf0ff", "#101018", "rgba(0,0,0,0.06)", "#ff8800");
            Assert.Equal(Norm(legacy), Norm(now));
        }
        Assert.Equal(Norm(LegacyChatMarkdownRenderer.ToHtml(Sample, "#123456")),
                     Norm(ProdToy.Plugins.ClaudeIntegration.ChatMarkdownRenderer.ToHtml(Sample, "#123456")));
    }

    [Fact]
    public void DefaultOptions_LeaveImagesAndLinksAsText()
    {
        string html = MarkdownRenderer.ToHtml(Sample, "#3884f4");
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<a href", html);
        Assert.Contains("![chart](images/chart.png)", html);
    }

    [Fact]
    public void Images_And_Links_RenderWhenSwitchedOn()
    {
        string html = MarkdownRenderer.ToHtml(Sample, "#3884f4",
            options: new MarkdownRenderOptions { Images = true, Links = true });
        Assert.Contains("<img src=\"images/chart.png\" alt=\"chart\"/>", html);
        Assert.Contains("<a href=\"https://example.com\">docs</a>", html);
        Assert.Contains("max-width: 100%", html);
        Assert.Contains("<table><thead><tr>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Theory]
    [InlineData("javascript:alert(1)", true, false)]
    [InlineData("JavaScript:alert(1)", false, false)]
    [InlineData("file:///c:/x.png", true, false)]
    [InlineData("https://x/y.png", true, true)]
    [InlineData("images/a b.png", true, true)]
    [InlineData("data:image/png;base64,AAA", true, true)]
    [InlineData("data:text/html,x", true, false)]
    [InlineData("mailto:a@b", false, true)]
    public void IsSafeUrl(string url, bool image, bool expected) =>
        Assert.Equal(expected, MarkdownRenderer.IsSafeUrl(url, image));

    [Fact]
    public void UnsafeImage_StaysText()
    {
        string body = MarkdownRenderer.ToHtmlBody("![x](javascript:alert(1))", new MarkdownRenderOptions { Images = true });
        Assert.DoesNotContain("<img", body);
    }

    [Fact]
    public void AlarmMarkdown_GetsThePopupCss_AndRendersTableAndImage()
    {
        string html = AlarmBodyView.RenderMarkdown("| a | b |\n|---|---|\n| 1 | 2 |\n\n![pic](pic.png)", AlarmStoreTestBase.DarkTheme);
        Assert.Contains("<td>1</td>", html);
        Assert.Contains("<img src=\"pic.png\"", html);
        Assert.Contains("pre code {", html);
        Assert.Contains("background: #1E1E24", html);
    }
}
