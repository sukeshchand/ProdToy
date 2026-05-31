using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Wraps one child process (e.g. <c>cmd /c dotnet run</c>) launched by the
/// Consolidated Launcher. Stdout/stderr are redirected line-by-line so the
/// owner can stream them into the in-form console tab. This is the captured
/// alternative to <see cref="ShortcutLauncher"/>'s "spawn a Windows Terminal
/// window" path.
/// </summary>
/// <remarks>
/// Ported from NordPilot.DeveloperTools' AppProcessRunner. Design notes:
///  - A single <c>cmd.exe /c</c> wrapper runs any shell command (dotnet, npm,
///    node, custom) with a consistent quoting story and one root process to kill.
///  - <c>EnableRaisingEvents = true</c> plus the data-received events keep the UI
///    thread in charge of appending: events are pumped via the caller-supplied
///    <c>uiInvoke</c> so consumers never have to BeginInvoke themselves.
///  - ANSI escape codes are stripped — the WinForms log control is plain text,
///    and dotnet watch / tailwind emit lots of colour codes.
///  - Stop closes the Job Object first (reliable tree kill), then falls back to
///    <see cref="Process.Kill(bool)"/>.
/// </remarks>
sealed partial class CapturedProcessRunner : IDisposable
{
    private readonly string _shellCommand;
    private readonly string _workingDir;
    private readonly Action<Action> _uiInvoke;
    private Process? _proc;
    private ProcessJobObject? _job;
    private bool _disposed;

    public string Key { get; }
    public bool IsRunning => _proc is { HasExited: false };
    public int? Pid => _proc?.Id;
    public int? ExitCode => _proc is { HasExited: true } p ? p.ExitCode : null;
    public DateTime? StartedAt { get; private set; }

    /// <summary>Fires for each line of stdout/stderr, on a <b>background reader
    /// thread</b> (NOT the UI thread). The consumer must marshal to the UI
    /// thread itself — typically by enqueuing into a buffer that a UI timer
    /// drains in batches, so a chatty process can't flood the message pump.
    /// <paramref name="isError"/> distinguishes stderr from stdout.</summary>
    public event Action<string, bool>? LineReceived;

    /// <summary>Fires once when the child process exits (UI thread). Argument is the exit code.</summary>
    public event Action<int>? Exited;

    public CapturedProcessRunner(string key, string shellCommand, string workingDir, Action<Action> uiInvoke)
    {
        Key = key;
        _shellCommand = shellCommand;
        _workingDir = workingDir;
        _uiInvoke = uiInvoke;
    }

    public void Start()
    {
        if (_proc is not null) throw new InvalidOperationException("Already started.");

        // /c = run the command and exit (vs /k which keeps cmd open). We want cmd to exit
        // when the child finishes so the runner gets a clean ExitCode.
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {_shellCommand}",
            WorkingDirectory = _workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // Force UTF-8 so non-ASCII (emoji, accented chars) doesn't get mojibake'd.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.OutputDataReceived += (_, e) => RaiseLine(e.Data, isError: false);
        _proc.ErrorDataReceived  += (_, e) => RaiseLine(e.Data, isError: true);
        _proc.Exited += (_, _) =>
        {
            var code = _proc?.ExitCode ?? -1;
            _uiInvoke(() => Exited?.Invoke(code));
        };

        // Create the Job Object BEFORE Start so we can assign the child as soon as it
        // exists. Every descendant (cmd -> dotnet -> the app, or cmd -> npm -> node)
        // inherits job membership automatically, so closing the job handle in Stop()
        // kills the whole tree — including grandchildren that Process.Kill misses on
        // Windows because they re-parent themselves out of cmd.exe's ancestry.
        _job = new ProcessJobObject();

        _proc.Start();
        StartedAt = DateTime.Now;
        try
        {
            _job.AssignProcess(_proc);
        }
        catch
        {
            // Job assignment can fail on some Windows configurations (rare on Win10+).
            // Fall back to Process.Kill(entireProcessTree: true) in Stop().
            try { _job.Dispose(); } catch { }
            _job = null;
        }
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
    }

    private void RaiseLine(string? raw, bool isError)
    {
        if (raw is null) return;       // EOF marker from the pipe; not a real line
        if (_disposed) return;          // host is tearing down — drop buffered late lines
        var cleaned = StripAnsi(raw);
        // Snapshot the handler so a concurrent Dispose() that nulls subscribers
        // can't race us between the null-check and the call.
        var handler = LineReceived;
        if (handler is null) return;
        // Fire directly on the reader thread — NO per-line UI marshaling. The
        // consumer buffers and flushes on a UI timer, so a process emitting
        // thousands of lines/sec can't saturate the UI message pump.
        try { handler(cleaned, isError); }
        catch { /* consumer's enqueue should never throw; swallow to be safe */ }
    }

    /// <summary>
    /// Stops the child process tree. Primary mechanism is the Job Object — closing the
    /// job handle terminates every process in the job, regardless of parent-PID
    /// re-parenting. Falls back to <see cref="Process.Kill(bool)"/> if the job wasn't
    /// created (rare). Idempotent.
    /// </summary>
    public void Stop()
    {
        try { _job?.Dispose(); } catch { }
        _job = null;

        if (_proc is null || _proc.HasExited) return;
        try
        {
            _proc.Kill(entireProcessTree: true);
        }
        catch { /* race with natural exit — fine */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Drop event handlers first so any late stdout/stderr line still being drained
        // from the pipe doesn't reach the form after it has been disposed.
        LineReceived = null;
        Exited = null;
        try { Stop(); } catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
    }

    // ---- ANSI escape stripping ----
    // dotnet watch, tailwind, npm and friends emit ANSI CSI sequences for colour + cursor.
    // The log control is plain text, so we strip them.
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)")]
    private static partial Regex AnsiRegex();
    /// <summary>Strip ANSI escape sequences. Shared with the sequential-build path.</summary>
    internal static string StripAnsi(string s) => string.IsNullOrEmpty(s) ? s : AnsiRegex().Replace(s, string.Empty);
}
