using System.Drawing;
using ProdToy.Plugins.Alarm;
using ProdToy.Sdk;
using Xunit;

namespace ProdToy.Tests.Alarm;

/// <summary>AlarmStore is static: every test that touches it runs in this one collection, one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AlarmStoreCollection
{
    public const string Name = "AlarmStore";
}

/// <summary>A fresh data folder per test, with AlarmStore pointed at it.</summary>
public abstract class AlarmStoreTestBase : IDisposable
{
    protected readonly string Root;
    protected readonly string DataDir;

    protected AlarmStoreTestBase()
    {
        Root = Path.Combine(Path.GetTempPath(), "prodtoy-alarm-tests", Guid.NewGuid().ToString("N"));
        DataDir = Path.Combine(Root, "data");
        Directory.CreateDirectory(DataDir);
        AlarmStore.Initialize(DataDir, () => 500);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { }
    }

    internal static PluginTheme DarkTheme => new(
        "test",
        Color.FromArgb(0x1e, 0x1e, 0x24), Color.FromArgb(0x2a, 0x2a, 0x32),
        Color.FromArgb(0x5f, 0xc7, 0x6e), Color.FromArgb(0x7f, 0xd7, 0x8e), Color.FromArgb(0x3a, 0x5a, 0x4a),
        Color.FromArgb(0xe6, 0xe6, 0xee), Color.FromArgb(0x9a, 0x9a, 0xa8), Color.FromArgb(0x3a, 0x3a, 0x44),
        Color.FromArgb(0x5f, 0xc7, 0x6e), Color.FromArgb(0xe7, 0x48, 0x56),
        Color.FromArgb(0x24, 0x3a, 0x2a), Color.FromArgb(0x3a, 0x24, 0x24));

    /// <summary>A source folder outside the data folder: body file + images/ folder + a sibling image.</summary>
    protected string MakeSourceFolder(string bodyName, string bodyContent)
    {
        string src = Path.Combine(Root, "src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(src, "images"));
        File.WriteAllBytes(Path.Combine(src, "images", "chart.png"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(src, "logo.png"), new byte[] { 4, 5 });
        File.WriteAllText(Path.Combine(src, "unrelated.txt"), "not copied");
        Directory.CreateDirectory(Path.Combine(src, "node_modules"));
        File.WriteAllText(Path.Combine(src, "node_modules", "skip.js"), "skipped");
        File.WriteAllText(Path.Combine(src, bodyName), bodyContent);
        return src;
    }
}
