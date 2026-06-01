using System.Diagnostics;
using System.Text;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Per-tab append-only log file. The file is the source of truth for what the
/// shortcut emitted during this launcher-window session: every line streamed
/// into the Live RTB is also written here, so Clear (which empties only the
/// RTB) can be undone via Reload (re-reads this file).
///
/// Encoding is UTF-8. Each line is prefixed with a single character + space
/// so the stdout/stderr distinction round-trips through reload:
///   <c>O </c> → stdout
///   <c>E </c> → stderr
/// Lines from the source are preserved verbatim — newlines inside them aren't
/// expected (the consolidated launcher splits on newline before calling here).
///
/// One <see cref="LogFileStore"/> instance per tab. Constructed with a
/// non-existent target path; the constructor truncates it. Append buffers
/// via the <see cref="StreamWriter"/>'s default buffer; <see cref="Flush"/>
/// is called at the end of each <c>AppendBatch</c> so at worst one batch is
/// lost on a hard crash.
/// </summary>
sealed class LogFileStore : IDisposable
{
    public string FilePath { get; }
    private readonly StreamWriter _writer;
    private bool _disposed;

    public LogFileStore(string filePath)
    {
        FilePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        // Append:false → start fresh each session. Share read so EnumerateAll
        // (which opens its own reader while we still hold the writer) can read
        // the in-progress file.
        var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(false));
    }

    public void Append(string line, bool isError)
    {
        if (_disposed) return;
        try
        {
            _writer.Write(isError ? "E " : "O ");
            _writer.WriteLine(line);
        }
        catch (Exception ex) { Debug.WriteLine($"LogFileStore.Append: {ex.Message}"); }
    }

    public void Flush()
    {
        if (_disposed) return;
        try { _writer.Flush(); }
        catch (Exception ex) { Debug.WriteLine($"LogFileStore.Flush: {ex.Message}"); }
    }

    /// <summary>Stream every (line, isError) tuple written so far, in order.
    /// Flushes the writer first so partial-buffered lines are included.
    /// Safe to call while appends continue — the read snapshot is bounded by
    /// the byte length at open time of the reader's FileStream.</summary>
    public IEnumerable<(string line, bool isError)> EnumerateAll()
    {
        if (_disposed) yield break;
        Flush();

        FileStream? fs = null;
        StreamReader? sr = null;
        try
        {
            // FileShare.ReadWrite — our own writer still owns the file handle.
            fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            sr = new StreamReader(fs, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LogFileStore.EnumerateAll open: {ex.Message}");
            sr?.Dispose();
            fs?.Dispose();
            yield break;
        }

        try
        {
            string? raw;
            while ((raw = sr.ReadLine()) != null)
            {
                bool isErr;
                string body;
                if (raw.Length >= 2 && raw[1] == ' ' && (raw[0] == 'E' || raw[0] == 'O'))
                {
                    isErr = raw[0] == 'E';
                    body = raw.Substring(2);
                }
                else
                {
                    // Unprefixed (shouldn't happen, but be robust).
                    isErr = false;
                    body = raw;
                }
                yield return (body, isErr);
            }
        }
        finally
        {
            sr.Dispose();
            fs.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _writer.Flush(); } catch { }
        try { _writer.Dispose(); } catch { }
    }

    // ---- session-wide helpers --------------------------------------------------

    /// <summary>Where per-tab log files live. Wiped at the start of every host
    /// session (no cross-session persistence — Clear/Reload is session-scoped).</summary>
    public static string LogsDirectory =>
        Path.Combine(Path.GetTempPath(), "ProdToyShortcuts", "logs");

    /// <summary>Best-effort wipe of every <c>*.log</c> in <see cref="LogsDirectory"/>.
    /// Called from the plugin's <c>Start()</c> so each host launch begins with a
    /// clean slate. Failures are swallowed — a stale file never breaks a future
    /// session because each tab's file path is session-guid-scoped anyway.</summary>
    public static void WipeAll()
    {
        try
        {
            var dir = LogsDirectory;
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*.log"))
            {
                try { File.Delete(f); }
                catch (Exception ex) { Debug.WriteLine($"WipeAll delete {f}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Debug.WriteLine($"WipeAll: {ex.Message}"); }
    }
}
