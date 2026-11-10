using ProdToy.Plugins.ClaudeIntegration;

namespace ProdToy.Tests.Claude;

/// <summary>History rows for the tests: one day file, items by position.</summary>
static class TestItems
{
    public static readonly DateTime Day = new(2026, 11, 10, 9, 0, 0);

    public static HistoryIndex Item(int index, string cwd = @"D:\work\App", string session = "s1", string root = "",
        int minute = -1, DateTime? day = null)
    {
        var at = (day ?? Day).AddMinutes(minute < 0 ? index : minute);
        return new HistoryIndex($"Title {index}", NotificationType.Success, at, $@"D:\h\{at:yyyyMMdd}--env.json", index,
            session, cwd, "env", "pc", $"Preview {index}", root);
    }

    public static string Key(HistoryIndex item) => HistoryTree.ItemKey(item);

    public static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "prodtoy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
