using System.Diagnostics;

namespace ProdToy;

/// <summary>
/// Lightweight file logger. Writes one file per day to Root\logs\prod-toy-yyyyMMdd.log,
/// keeps 30 days, drops anything below Info. Thread-safe via a single lock.
/// </summary>
static class Log
{
    private const int RetentionDays = 30;
    private static readonly object _gate = new();
    private static DateTime _cachedDate = DateTime.MinValue;
    private static string _cachedPath = "";

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex != null ? $"{message}: {ex}" : message);

    /// <summary>
    /// Tagged write used by plugin contexts so their lines land in the same daily file
    /// but are attributable to the plugin id.
    /// </summary>
    public static void Tagged(string level, string tag, string message)
        => Write(level.PadRight(5), $"[{tag}] {message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                var now = DateTimeOffset.Now;
                if (now.Date != _cachedDate)
                {
                    Directory.CreateDirectory(AppPaths.LogsDir);
                    _cachedDate = now.Date;
                    _cachedPath = Path.Combine(
                        AppPaths.LogsDir, $"prod-toy-{now:yyyyMMdd}.log");
                    PruneOld(now.Date);
                }

                string line = $"{now:yyyy-MM-ddTHH:mm:ss.fffzzz} [{level}] {message}";
                File.AppendAllText(_cachedPath, line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Log write failed: {ex.Message}");
        }
    }

    private static void PruneOld(DateTime today)
    {
        try
        {
            var cutoff = today.AddDays(-RetentionDays);
            foreach (var file in Directory.GetFiles(AppPaths.LogsDir, "prod-toy-*.log"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Length != "prod-toy-yyyyMMdd".Length) continue;
                string datePart = name.Substring("prod-toy-".Length);
                if (!DateTime.TryParseExact(datePart, "yyyyMMdd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var fileDate))
                    continue;
                if (fileDate < cutoff)
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Log prune failed: {ex.Message}");
        }
    }
}
