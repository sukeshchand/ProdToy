using Xunit;

namespace ProdToy.Tests;

public class SmokeTests
{
    [Fact]
    public void ThePluginAssembliesLoad()
    {
        Assert.NotNull(typeof(ProdToy.Plugins.ClaudeIntegration.ClaudePaths).Assembly);
    }
}
