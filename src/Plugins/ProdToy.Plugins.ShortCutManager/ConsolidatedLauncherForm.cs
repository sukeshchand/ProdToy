using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net.Http;
using System.Text;
using System.Threading;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Standalone "Consolidated Launcher" window for a folder of shortcuts. Unlike
/// the Group Launcher (which spawns separate Windows Terminal windows), this
/// runs every shortcut as a <b>captured child process</b> and shows everything
/// in one screen:
///   • Column 1 top    — one row per shortcut (status, pid/uptime, ▶/■, open ↗)
///                       and Launch All / Stop All.
///   • Column 1 bottom — a console tab per shortcut with live stdout/stderr.
///   • Column 2        — an embedded WebView2 preview per shortcut's Status URL.
///
/// Opened from the "Consolidated" tab in <see cref="ShortcutsForm"/>. One window
/// per folder (reused/focused on reopen via <see cref="OpenOrFocus"/>).
/// </summary>
sealed class ConsolidatedLauncherForm : Form
{
    // One window per folder path, process-wide. Lets the Consolidated tab focus
    // an already-open launcher instead of stacking duplicates.
    private static readonly Dictionary<string, ConsolidatedLauncherForm> s_open =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Open (or focus, if already open) the consolidated launcher for a folder.</summary>
    public static void OpenOrFocus(PluginTheme theme, string folderPath, List<Shortcut> shortcuts)
    {
        if (s_open.TryGetValue(folderPath, out var existing) && !existing.IsDisposed)
        {
            if (existing.WindowState == FormWindowState.Minimized)
                existing.WindowState = FormWindowState.Normal;
            existing.BringToFront();
            existing.Activate();
            return;
        }
        var form = new ConsolidatedLauncherForm(theme, folderPath, shortcuts);
        s_open[folderPath] = form;
        form.Show();
    }

    private readonly PluginTheme _theme;
    private readonly string _folderPath;
    private readonly List<Shortcut> _shortcuts;

    private readonly Dictionary<string, CapturedProcessRunner> _runners =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ConsolidatedRow> _rowsById =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ConsolidatedRow> _rows = new();
    // Shortcuts whose browser tab we've already auto-opened this session, so we
    // only auto-open once (the first time the Status URL goes healthy).
    private readonly HashSet<string> _autoOpened = new(StringComparer.OrdinalIgnoreCase);

    private readonly FlowLayoutPanel _list;
    private readonly Label _statusLabel;
    private readonly RoundedButton _launchAllBtn;
    private readonly CheckBox _seqBuildCheck;
    private readonly ConsolidatedLogTabs _logTabs;
    private readonly ConsolidatedBrowserTabs _browserTabs;

    // Sequential-build state: when on, Launch All compiles dotnet shortcuts one
    // at a time (so a shared project isn't built concurrently and file-locked)
    // before running them all. Persisted per folder via ConsolidatedSettings.
    private bool _sequentialBuild;
    private bool _sequentialActive;                       // a sequential launch is in progress
    private CancellationTokenSource? _buildCts;
    private readonly Dictionary<string, Process> _buildProcs = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly System.Windows.Forms.Timer _urlPollTimer;
    private readonly System.Windows.Forms.Timer _flushTimer;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    private bool _urlPollInFlight;

    // Captured process output is enqueued from background reader threads and
    // drained to the console tabs on a UI timer, so a chatty process can't
    // flood the UI message pump. Cap how many lines we apply per flush so a
    // huge backlog drains over several ticks instead of freezing in one.
    private readonly ConcurrentQueue<(string key, string line, bool isError)> _pending = new();
    private const int MaxLinesPerFlush = 1500;

    private ConsolidatedLauncherForm(PluginTheme theme, string folderPath, List<Shortcut> shortcuts)
    {
        _theme = theme;
        _folderPath = folderPath;
        _shortcuts = shortcuts;

        Text = $"Consolidated Launcher — {folderPath}";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1320, 840);
        MinimumSize = new Size(900, 520);
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 10f);
        AutoScaleMode = AutoScaleMode.Dpi;
        // Buffer the form's own painting. NOTE: deliberately NOT WS_EX_COMPOSITED —
        // that re-composites the entire window on every child invalidation, which
        // made the whole form flicker under the 10×/sec log updates.
        DoubleBuffered = true;

        // Outer split: left column (rows + console) | right column (browser preview).
        // NOTE: Panel1MinSize / Panel2MinSize are deliberately NOT set here.
        // A freshly created SplitContainer has tiny default bounds and a
        // SplitterDistance of 50, so assigning large min sizes in the
        // constructor throws ("SplitterDistance must be between …"). They're
        // applied in the Shown handler below, after the panels have real sizes.
        var outerSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 5,
            BackColor = theme.Border,
        };
        Controls.Add(outerSplit);

        // Inner split inside the left column: rows + toolbar (top) / console (bottom).
        var innerSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 5,
            BackColor = theme.Border,
        };
        outerSplit.Panel1.Controls.Add(innerSplit);

        // ----- top of left column: header + rows -----
        innerSplit.Panel1.BackColor = theme.BgDark;

        _list = new BufferedFlowPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = theme.BgDark,
            Padding = new Padding(10, 4, 10, 6),
        };
        innerSplit.Panel1.Controls.Add(_list);

        var header = new Panel { Dock = DockStyle.Top, Height = 86, BackColor = theme.BgDark };
        innerSplit.Panel1.Controls.Add(header);

        const int btnW = 104, btnH = 30, btnGap = 8, pad = 10;
        var stopAllBtn = MakeButton("■ Stop All", theme.ErrorBg, theme.ErrorColor);
        stopAllBtn.Size = new Size(btnW, btnH);
        stopAllBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        stopAllBtn.Location = new Point(header.ClientSize.Width - pad - btnW, 8);
        stopAllBtn.Click += (_, _) => StopAll();
        header.Controls.Add(stopAllBtn);

        _launchAllBtn = MakeButton("▶ Launch All", theme.Primary, Color.White);
        _launchAllBtn.Size = new Size(btnW, btnH);
        _launchAllBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _launchAllBtn.Location = new Point(stopAllBtn.Left - btnGap - btnW, 8);
        _launchAllBtn.Click += (_, _) => LaunchAll();
        header.Controls.Add(_launchAllBtn);

        var title = new Label
        {
            Text = $"📁 {folderPath}",
            Font = new Font("Segoe UI Semibold", 12f, FontStyle.Bold),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, 8),
            BackColor = Color.Transparent,
        };
        header.Controls.Add(title);

        _statusLabel = new Label
        {
            Text = $"{shortcuts.Count} shortcut(s) · Ready.",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextSecondary,
            AutoSize = false,
            Height = 18,
            Location = new Point(pad, 34),
            Size = new Size(header.ClientSize.Width - pad * 2, 18),
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        header.Controls.Add(_statusLabel);

        // Sequential-build toggle (persisted per folder). When on, Launch All
        // compiles dotnet shortcuts one-by-one before running them, so a shared
        // project isn't built by several apps at once and file-locked.
        _sequentialBuild = ConsolidatedSettings.GetSequentialBuild(folderPath);
        _seqBuildCheck = new CheckBox
        {
            Text = "Sequential build (dotnet) before start",
            Checked = _sequentialBuild,
            AutoSize = true,
            FlatStyle = FlatStyle.Standard,
            ForeColor = theme.TextPrimary,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 9f),
            Location = new Point(pad, 58),
        };
        var seqTip = new ToolTip();
        seqTip.SetToolTip(_seqBuildCheck,
            "Compile each dotnet shortcut sequentially (dotnet build) before running them all.\n" +
            "Avoids file locks when several apps share a project. Non-dotnet shortcuts start normally.");
        _seqBuildCheck.CheckedChanged += (_, _) =>
        {
            _sequentialBuild = _seqBuildCheck.Checked;
            ConsolidatedSettings.SetSequentialBuild(_folderPath, _sequentialBuild);
        };
        header.Controls.Add(_seqBuildCheck);

        // ----- bottom of left column: console tabs -----
        _logTabs = new ConsolidatedLogTabs(theme) { Dock = DockStyle.Fill };
        _logTabs.RestartRequested += id => RestartOne(id);
        innerSplit.Panel2.Controls.Add(_logTabs);

        // ----- right column: browser preview tabs -----
        _browserTabs = new ConsolidatedBrowserTabs(theme) { Dock = DockStyle.Fill };
        outerSplit.Panel2.Controls.Add(_browserTabs);

        // Rows + console tabs, one per shortcut.
        for (int i = 0; i < shortcuts.Count; i++)
        {
            var s = shortcuts[i];
            var swatch = ConsolidatedLogTabs.StableColor(s.Id);
            _logTabs.AddOrGetTab(s.Id, ShortName(s), swatch);

            var row = new ConsolidatedRow(s, theme, i + 1, swatch);
            row.LaunchRequested += () => LaunchOne(s.Id);
            row.StopRequested += () => StopOne(s.Id);
            row.OpenUrlRequested += () => OpenBrowser(s);
            row.Selected += () => _logTabs.FocusTab(s.Id);
            _rows.Add(row);
            _rowsById[s.Id] = row;
            _list.Controls.Add(row);
        }
        _list.ClientSizeChanged += (_, _) => ResizeRows();
        ResizeRows();

        // Set splitter positions + min sizes once the panels have real sizes
        // (see the note at the SplitContainer creation). Order matters:
        // SplitterDistance must be assigned BEFORE the larger min sizes, and
        // each assignment must keep SplitterDistance within the current bounds.
        Shown += (_, _) =>
        {
            try
            {
                int oW = outerSplit.Width;
                if (oW > 700)
                {
                    int oDist = Math.Clamp((int)(oW * 0.46), 360, oW - 300);
                    outerSplit.SplitterDistance = oDist;
                    outerSplit.Panel1MinSize = 340;
                    outerSplit.Panel2MinSize = 260;
                }

                int iH = innerSplit.Height;
                if (iH > 280)
                {
                    int iDist = Math.Clamp((int)(iH * 0.52), 140, iH - 130);
                    innerSplit.SplitterDistance = iDist;
                    innerSplit.Panel1MinSize = 120;
                    innerSplit.Panel2MinSize = 100;
                }
            }
            catch { /* tiny window — leave defaults */ }
        };

        _logTabs.AppendLine(ConsolidatedLogTabs.LauncherTabKey,
            $"[{DateTime.Now:HH:mm:ss}] Consolidated Launcher ready for \"{folderPath}\" ({shortcuts.Count} shortcut(s)).");

        _pollTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _pollTimer.Tick += (_, _) => RefreshStatus();
        _pollTimer.Start();

        _urlPollTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _urlPollTimer.Tick += async (_, _) => await ProbeUrlsAsync();
        _urlPollTimer.Start();

        // Drains buffered process output to the console tabs ~10×/sec.
        _flushTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _flushTimer.Tick += (_, _) => FlushPending();
        _flushTimer.Start();
    }

    /// <summary>Drain up to <see cref="MaxLinesPerFlush"/> buffered output lines
    /// and apply them to the console tabs, grouped by shortcut so each tab takes
    /// a single batched append. Runs on the UI thread off the flush timer.</summary>
    private void FlushPending()
    {
        if (IsDisposed || _pending.IsEmpty) return;

        Dictionary<string, List<(string, bool)>>? byKey = null;
        int processed = 0;
        while (processed < MaxLinesPerFlush && _pending.TryDequeue(out var item))
        {
            byKey ??= new Dictionary<string, List<(string, bool)>>(StringComparer.OrdinalIgnoreCase);
            if (!byKey.TryGetValue(item.key, out var list))
                byKey[item.key] = list = new List<(string, bool)>();
            list.Add((item.line, item.isError));
            processed++;
        }
        if (byKey is null) return;
        foreach (var kv in byKey)
            _logTabs.AppendBatch(kv.Key, kv.Value);
    }

    private static string ShortName(Shortcut s) =>
        string.IsNullOrWhiteSpace(s.Name) ? "(untitled)" : s.Name.Trim();

    // ─────────────────────────── launch / stop ───────────────────────────

    private async void LaunchAll()
    {
        if (_sequentialActive) return;   // a sequential launch is already running

        if (!_sequentialBuild)
        {
            foreach (var s in _shortcuts) LaunchOne(s.Id, focus: false);
            _statusLabel.Text = $"{_shortcuts.Count} shortcut(s) · launching…";
            return;
        }

        await LaunchAllSequentialAsync();
    }

    /// <summary>
    /// Sequential-build Launch All: compile each dotnet shortcut one at a time
    /// (so a shared project isn't built concurrently and file-locked), then run
    /// them all. Non-dotnet shortcuts are launched immediately and untouched.
    /// </summary>
    private async Task LaunchAllSequentialAsync()
    {
        _sequentialActive = true;
        _launchAllBtn.Enabled = false;
        _buildCts?.Dispose();
        _buildCts = new CancellationTokenSource();
        var ct = _buildCts.Token;

        try
        {
            // Partition: dotnet 'run' shortcuts we can pre-build vs everything else.
            var dotnetApps = new List<(Shortcut s, string buildCmd)>();
            var others = new List<Shortcut>();
            foreach (var s in _shortcuts)
            {
                if (TryGetDotnetBuildCommand(s, out var buildCmd)) dotnetApps.Add((s, buildCmd));
                else others.Add(s);
            }

            // Launch non-dotnet shortcuts immediately — they don't share the lock.
            foreach (var s in others) LaunchOne(s.Id, focus: false);

            if (dotnetApps.Count == 0)
            {
                _statusLabel.Text = "No dotnet shortcuts to pre-build.";
                return;
            }

            foreach (var (s, _) in dotnetApps)
            {
                if (!(_runners.TryGetValue(s.Id, out var r) && r.IsRunning))
                    _rowsById[s.Id].SetState(ConsolidatedRow.RowState.Building, "Queued for build…", null);
            }

            _logTabs.AppendLine(ConsolidatedLogTabs.LauncherTabKey,
                $"[{DateTime.Now:HH:mm:ss}] Sequential build: {dotnetApps.Count} dotnet shortcut(s)…");

            int n = dotnetApps.Count, i = 0, built = 0, failed = 0, skipped = 0;
            var ranAfterBuild = new List<Shortcut>();
            foreach (var (s, buildCmd) in dotnetApps)
            {
                i++;
                if (ct.IsCancellationRequested || IsDisposed)
                {
                    _logTabs.AppendLine(ConsolidatedLogTabs.LauncherTabKey,
                        $"[{DateTime.Now:HH:mm:ss}] Sequential build cancelled.");
                    return;
                }

                var row = _rowsById[s.Id];
                if (_runners.TryGetValue(s.Id, out var rn) && rn.IsRunning)
                {
                    skipped++;
                    _logTabs.AppendLine(ConsolidatedLogTabs.LauncherTabKey,
                        $"[{DateTime.Now:HH:mm:ss}] [{i}/{n}] {ShortName(s)} — already running, skipped.");
                    continue;
                }

                row.SetState(ConsolidatedRow.RowState.Building, $"Building ({i}/{n})…", null);
                _statusLabel.Text = $"Building {i}/{n}: {ShortName(s)}…";
                EmitBanner(s.Id, $"[{i}/{n}] build · {buildCmd}");
                _logTabs.AppendLine(ConsolidatedLogTabs.LauncherTabKey,
                    $"[{DateTime.Now:HH:mm:ss}] [{i}/{n}] Building {ShortName(s)} — {buildCmd}");

                // --no-restore makes a no-op rebuild noticeably faster (skips the
                // implicit NuGet restore). If packages aren't restored yet it fails,
                // so retry once with a full restore.
                bool ok = await RunBuildAsync(s.Id, buildCmd + " --no-restore", s.WorkingDirectory, ct);
                if (!ok && !ct.IsCancellationRequested)
                {
                    EmitBanner(s.Id, $"[{i}/{n}] retrying with restore…");
                    ok = await RunBuildAsync(s.Id, buildCmd, s.WorkingDirectory, ct);
                }

                if (ct.IsCancellationRequested) { return; }
                if (ok)
                {
                    built++;
                    EmitBanner(s.Id, $"[{i}/{n}] build succeeded");
                    ranAfterBuild.Add(s);
                    // Clear the stale "Building (i/n)…" so only the app currently
                    // compiling shows that — the rest read "Built ✓ — waiting".
                    row.SetState(ConsolidatedRow.RowState.Building, "Built ✓ — waiting to start…", null);
                }
                else
                {
                    failed++;
                    EmitBanner(s.Id, $"[{i}/{n}] build FAILED — not started", isError: true);
                    row.SetState(ConsolidatedRow.RowState.Failed, "Build failed", null);
                    _logTabs.AppendLine(ConsolidatedLogTabs.LauncherTabKey,
                        $"[{DateTime.Now:HH:mm:ss}] [{i}/{n}] {ShortName(s)} build FAILED — skipping run.", isError: true);
                }
            }

            _logTabs.AppendLine(ConsolidatedLogTabs.LauncherTabKey,
                $"[{DateTime.Now:HH:mm:ss}] Builds done: {built} ok, {failed} failed, {skipped} skipped. Starting {ranAfterBuild.Count} app(s)…");

            // Run all successfully-built apps at once (dotnet run won't recompile).
            foreach (var s in ranAfterBuild)
            {
                if (ct.IsCancellationRequested || IsDisposed) return;
                LaunchOne(s.Id, focus: false);
            }
        }
        finally
        {
            _sequentialActive = false;
            if (!IsDisposed) _launchAllBtn.Enabled = true;
        }
    }

    /// <summary>
    /// Run one <c>dotnet build</c> as a captured child process, streaming output
    /// into the shortcut's console tab, and await its exit. Returns true on a
    /// zero exit code. The process is tracked in <see cref="_buildProcs"/> so
    /// Stop All / form close can kill an in-progress build.
    /// </summary>
    private async Task<bool> RunBuildAsync(string id, string buildCmd, string workingDir, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Process proc;
        try
        {
            proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {buildCmd}",
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                },
                EnableRaisingEvents = true,
            };
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) _pending.Enqueue((id, CapturedProcessRunner.StripAnsi(e.Data), false)); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) _pending.Enqueue((id, CapturedProcessRunner.StripAnsi(e.Data), true)); };
            proc.Exited += (_, _) => { try { tcs.TrySetResult(proc.ExitCode); } catch { tcs.TrySetResult(-1); } };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _pending.Enqueue((id, $"✖ build failed to start: {ex.Message}", true));
            return false;
        }

        _buildProcs[id] = proc;
        // Kill the build tree if Stop All / close cancels mid-build.
        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        });

        int code;
        try { code = await tcs.Task; }
        catch { code = -1; }
        finally
        {
            _buildProcs.Remove(id);
            try { proc.Dispose(); } catch { }
        }
        return code == 0 && !ct.IsCancellationRequested;
    }

    /// <summary>
    /// If <paramref name="s"/> resolves to a <c>dotnet … run …</c> command, derive
    /// the matching <c>dotnet build …</c> line (preserving project/config/framework,
    /// dropping run-only flags and app args). Returns false for non-dotnet, non-run,
    /// or compound (&amp;&amp;/||/|) commands — those are launched without a pre-build.
    /// </summary>
    private static bool TryGetDotnetBuildCommand(Shortcut s, out string buildCommand)
    {
        buildCommand = "";
        string run = ShortcutLauncher.BuildProfileCmdline(s).Trim();
        if (run.Length == 0) return false;
        // Compound shell commands are too varied to transform safely — skip.
        if (run.Contains("&&") || run.Contains("||") || run.Contains('|') || run.Contains('&')) return false;

        var tokens = Tokenize(run);
        if (tokens.Count == 0) return false;
        if (!Unquote(tokens[0]).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return false;

        int runIdx = tokens.FindIndex(1, t => Unquote(t).Equals("run", StringComparison.OrdinalIgnoreCase));
        if (runIdx < 0) return false;   // not a 'dotnet run' (e.g. dotnet build/test/watch) — skip

        var outArgs = new List<string> { "dotnet", "build" };
        for (int i = 1; i < tokens.Count; i++)
        {
            if (i == runIdx) continue;          // drop the 'run' verb
            var t = tokens[i];
            if (t == "--") break;                // stop at app args
            var lower = Unquote(t).ToLowerInvariant();
            switch (lower)
            {
                case "--project":
                    if (i + 1 < tokens.Count) outArgs.Add(tokens[++i]);  // build takes project as positional
                    break;
                case "-c":
                case "--configuration":
                case "-f":
                case "--framework":
                case "-r":
                case "--runtime":
                    outArgs.Add(t);
                    if (i + 1 < tokens.Count) outArgs.Add(tokens[++i]);
                    break;
                case "--no-restore":
                    outArgs.Add(t);
                    break;
                default:
                    if (lower.StartsWith("--project="))
                        outArgs.Add(t.Substring(t.IndexOf('=') + 1));
                    else if (lower.StartsWith("--configuration=") || lower.StartsWith("--framework=")
                             || lower.StartsWith("--runtime=") || lower.StartsWith("-c="))
                        outArgs.Add(t);
                    // else: run-only flag (--no-build, --launch-profile, --urls, …) — dropped.
                    break;
            }
        }
        buildCommand = string.Join(" ", outArgs);
        return true;
    }

    /// <summary>Split a command line on spaces, keeping double-quoted spans intact
    /// (quotes preserved so the rejoined string stays valid).</summary>
    private static List<string> Tokenize(string s)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        foreach (char c in s)
        {
            if (c == '"') { inQuotes = !inQuotes; sb.Append(c); }
            else if (c == ' ' && !inQuotes) { if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); } }
            else sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    private static string Unquote(string t) => t.Length >= 2 && t[0] == '"' && t[^1] == '"' ? t[1..^1] : t;

    private void LaunchOne(string id, bool focus = true)
    {
        var s = _shortcuts.FirstOrDefault(x => x.Id == id);
        if (s is null) return;
        var row = _rowsById[id];

        // Already running — focus its console tab instead of double-launching.
        if (_runners.TryGetValue(id, out var existing) && existing.IsRunning)
        {
            if (focus) _logTabs.FocusTab(id);
            return;
        }

        if (string.IsNullOrWhiteSpace(s.WorkingDirectory) || !Directory.Exists(s.WorkingDirectory))
        {
            row.SetState(ConsolidatedRow.RowState.Failed, "Working directory missing", null);
            _logTabs.AppendLine(id, $"[{DateTime.Now:HH:mm:ss}] ✖ Working directory missing or empty: \"{s.WorkingDirectory}\"", isError: true);
            return;
        }

        // Admin shortcuts can't be stream-captured (UAC + redirected stdio is
        // unsupported), so fall back to the normal launcher's external window.
        if (s.RequireAdmin)
        {
            _logTabs.AppendLine(id, $"[{DateTime.Now:HH:mm:ss}] ⚠ This shortcut requires admin — launching in an external window (output can't be captured here).", isError: true);
            var ext = ShortcutLauncher.Launch(s);
            row.SetState(ext.Ok ? ConsolidatedRow.RowState.Running : ConsolidatedRow.RowState.Failed,
                ext.Ok ? "Running (external, admin)" : (ext.ErrorMessage ?? "Launch failed"), null);
            return;
        }

        // Dispose any prior (exited) runner for this id before starting fresh.
        DisposeRunner(id);

        string command = ShortcutLauncher.BuildProfileCmdline(s);
        var runner = new CapturedProcessRunner(id, command, s.WorkingDirectory, RunOnUi);
        // LineReceived fires on a background reader thread — just enqueue (thread
        // safe); the flush timer applies it to the UI in batches.
        runner.LineReceived += (line, isErr) => _pending.Enqueue((id, line, isErr));
        runner.Exited += code => OnRunnerExited(id, code);

        EmitBanner(id, $"starting · {command}");
        try
        {
            runner.Start();
        }
        catch (Exception ex)
        {
            row.SetState(ConsolidatedRow.RowState.Failed, "Start failed", null);
            _logTabs.AppendLine(id, $"[{DateTime.Now:HH:mm:ss}] ✖ Failed to start: {ex.Message}", isError: true);
            try { runner.Dispose(); } catch { }
            return;
        }

        _runners[id] = runner;
        row.SetState(ConsolidatedRow.RowState.Launching, "Launching…", runner.Pid);
        if (focus) _logTabs.FocusTab(id);

        // Reuse the existing per-shortcut visible auto-login (separate Edge).
        ShortcutStore.RecordLaunch(id);
        AutoLoginRunner.RunInBackground(s);
    }

    private void StopAll()
    {
        // Cancel an in-progress sequential build and kill any running build.
        _buildCts?.Cancel();
        foreach (var p in _buildProcs.Values.ToList())
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }

        int stopped = 0;
        foreach (var s in _shortcuts)
            if (StopOne(s.Id, silent: true)) stopped++;
        _statusLabel.Text = stopped == 0 ? "Nothing to stop." : $"Stopped {stopped} process(es).";
    }

    private bool StopOne(string id) => StopOne(id, silent: false);

    private bool StopOne(string id, bool silent)
    {
        if (!_runners.TryGetValue(id, out var runner)) return false;
        bool wasRunning = runner.IsRunning;
        try { runner.Stop(); } catch { }
        DisposeRunner(id);
        var row = _rowsById[id];
        row.SetState(ConsolidatedRow.RowState.Stopped, "Stopped", null);
        if (wasRunning) EmitBanner(id, "stopped");
        if (!silent)
            _statusLabel.Text = wasRunning ? "Stopped 1 process." : "Nothing to stop.";
        return wasRunning;
    }

    private void RestartOne(string id)
    {
        StopOne(id, silent: true);
        LaunchOne(id);
    }

    private void OnRunnerExited(string id, int code)
    {
        if (IsDisposed) return;
        var row = _rowsById.GetValueOrDefault(id);
        EmitBanner(id, code == 0 ? "exited (code 0)" : $"exited (code {code})", isError: code != 0);
        row?.SetState(
            code == 0 ? ConsolidatedRow.RowState.Stopped : ConsolidatedRow.RowState.Exited,
            code == 0 ? "Exited (0)" : $"Exited (code {code})",
            null);
    }

    private void DisposeRunner(string id)
    {
        if (_runners.TryGetValue(id, out var r))
        {
            try { r.Dispose(); } catch { }
            _runners.Remove(id);
        }
    }

    // ─────────────────────────── status / probing ───────────────────────────

    private void RefreshStatus()
    {
        int running = 0, exited = 0;
        foreach (var s in _shortcuts)
        {
            var row = _rowsById[s.Id];
            if (_runners.TryGetValue(s.Id, out var runner) && runner.IsRunning)
            {
                row.SetState(ConsolidatedRow.RowState.Running, "Running", runner.Pid, runner.StartedAt);
                running++;
            }
            else if (row.State == ConsolidatedRow.RowState.Exited) exited++;
            row.TickHighlight();   // clear the post-change flash once it elapses
        }

        var parts = new List<string> { $"{_shortcuts.Count} shortcut(s)" };
        if (running > 0) parts.Add($"{running} running");
        if (exited > 0) parts.Add($"{exited} exited");
        _statusLabel.Text = string.Join(" · ", parts);
    }

    private async Task ProbeUrlsAsync()
    {
        if (_urlPollInFlight || IsDisposed) return;
        _urlPollInFlight = true;
        try
        {
            var tasks = new List<Task>();
            foreach (var s in _shortcuts)
            {
                var row = _rowsById[s.Id];
                var url = s.StatusUrl?.Trim() ?? "";
                if (string.IsNullOrEmpty(url))
                {
                    row.SetUrlStatus(ConsolidatedRow.UrlState.NotConfigured, "");
                    continue;
                }
                tasks.Add(ProbeOneAsync(s, row, url));
            }
            if (tasks.Count > 0) await Task.WhenAll(tasks);
        }
        finally { _urlPollInFlight = false; }
    }

    private async Task ProbeOneAsync(Shortcut s, ConsolidatedRow row, string url)
    {
        try
        {
            using var resp = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            int code = (int)resp.StatusCode;
            if (code is >= 200 and < 400)
            {
                row.SetUrlStatus(ConsolidatedRow.UrlState.Healthy, $"HTTP {code}");
                MaybeAutoOpenBrowser(s);
            }
            else
            {
                row.SetUrlStatus(ConsolidatedRow.UrlState.ServerError, $"HTTP {code}");
            }
        }
        catch (TaskCanceledException) { row.SetUrlStatus(ConsolidatedRow.UrlState.Unreachable, "Timeout"); }
        catch (HttpRequestException ex) { row.SetUrlStatus(ConsolidatedRow.UrlState.Unreachable, ex.InnerException?.Message ?? "Unreachable"); }
        catch (Exception ex) { row.SetUrlStatus(ConsolidatedRow.UrlState.Unreachable, ex.Message); }
    }

    /// <summary>Auto-open the preview tab the first time a shortcut's Status URL
    /// goes healthy — only while its process is running, and only once.</summary>
    private void MaybeAutoOpenBrowser(Shortcut s)
    {
        if (_autoOpened.Contains(s.Id)) return;
        if (!(_runners.TryGetValue(s.Id, out var r) && r.IsRunning)) return;
        if (string.IsNullOrWhiteSpace(s.StatusUrl)) return;
        _autoOpened.Add(s.Id);
        try { _browserTabs.OpenOrFocus(s.Id, ShortName(s), s.StatusUrl.Trim()); }
        catch (Exception ex)
        {
            PluginLog.Error($"Consolidated auto-preview failed for '{s.Name}'", ex);
            _logTabs.AppendLine(s.Id, $"[{DateTime.Now:HH:mm:ss}] ✖ Auto-preview failed: {ex.Message}", isError: true);
        }
    }

    private void OpenBrowser(Shortcut s)
    {
        string url = s.StatusUrl?.Trim() ?? "";
        _autoOpened.Add(s.Id);
        try
        {
            _browserTabs.OpenOrFocus(s.Id, ShortName(s), url);
            if (string.IsNullOrEmpty(url))
                _logTabs.AppendLine(s.Id, $"[{DateTime.Now:HH:mm:ss}] No Status URL set — opened a blank preview. Type a URL in the address bar, or add a Status URL to the shortcut to enable auto-preview.");
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Consolidated preview failed to open for '{s.Name}'", ex);
            _logTabs.AppendLine(s.Id, $"[{DateTime.Now:HH:mm:ss}] ✖ Preview failed to open: {ex.Message}", isError: true);
            _logTabs.FocusTab(s.Id);
        }
    }

    // ─────────────────────────── helpers ───────────────────────────

    /// <summary>Marshal an action onto the UI thread for the captured runner.</summary>
    private void RunOnUi(Action action)
    {
        if (IsDisposed) return;
        try
        {
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void EmitBanner(string id, string message, bool isError = false)
    {
        string bar = new string('─', 12);
        _logTabs.AppendLine(id, $"{bar} [{DateTime.Now:HH:mm:ss}] {message} {bar}", isError);
    }

    private void ResizeRows()
    {
        int w = _list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 10;
        if (w < 200) w = 200;
        foreach (Control c in _list.Controls)
            if (c is ConsolidatedRow r) r.Width = w;
    }

    private RoundedButton MakeButton(string text, Color bg, Color fg)
    {
        var b = new RoundedButton
        {
            Text = text,
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat,
            BackColor = bg,
            ForeColor = fg,
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = _theme.PrimaryLight;
        return b;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        int live = _runners.Values.Count(r => r.IsRunning);
        if (live > 0)
        {
            var res = MessageBox.Show(this,
                $"{live} process(es) are still running.\n\n" +
                "Yes — stop them all and close.\n" +
                "No — leave them running and close (they'll be killed when ProdToy exits).\n" +
                "Cancel — keep this window open.",
                "Close Consolidated Launcher",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
            if (res == DialogResult.Cancel) { e.Cancel = true; return; }
            if (res == DialogResult.Yes)
                foreach (var id in _runners.Keys.ToList()) StopOne(id, silent: true);
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _pollTimer.Stop(); _pollTimer.Dispose();
        _urlPollTimer.Stop(); _urlPollTimer.Dispose();
        _flushTimer.Stop(); _flushTimer.Dispose();
        try { _buildCts?.Cancel(); } catch { }
        foreach (var p in _buildProcs.Values.ToList())
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { p.Dispose(); } catch { }
        }
        _buildProcs.Clear();
        try { _buildCts?.Dispose(); } catch { }
        foreach (var r in _runners.Values) { try { r.Dispose(); } catch { } }
        _runners.Clear();
        try { _httpClient.Dispose(); } catch { }
        if (s_open.TryGetValue(_folderPath, out var f) && ReferenceEquals(f, this))
            s_open.Remove(_folderPath);
        base.OnFormClosed(e);
    }
}

/// <summary>
/// One compact shortcut row in the Consolidated Launcher's left column.
/// Owner-drawn: a state dot, "N. name", a state/pid/uptime line, a Status-URL
/// health badge, and ▶/■ + "open ↗" controls on the right. A thin left bar in
/// the shortcut's stable colour correlates the row with its console tab.
/// Clicking the row body focuses that shortcut's console tab.
/// </summary>
sealed class ConsolidatedRow : Panel
{
    public enum RowState { Stopped, Building, Launching, Running, Exited, Failed }
    public enum UrlState { NotConfigured, Healthy, ServerError, Unreachable }

    private const int RowHeight = 64;

    private readonly Shortcut _shortcut;
    private readonly PluginTheme _theme;
    private readonly int _index;
    private readonly Color _swatch;
    private readonly RoundedButton _launchBtn;
    private readonly RoundedButton _stopBtn;
    private readonly LinkLabel _openLink;

    private RowState _state = RowState.Stopped;
    private string _stateLabel = "Stopped";
    private int? _pid;
    private DateTime? _startedAt;
    private UrlState _urlState = UrlState.NotConfigured;
    private string _urlDetail = "";
    private DateTime _highlightUntil;   // brief accent border after a state change

    public RowState State => _state;

    public event Action? LaunchRequested;
    public event Action? StopRequested;
    public event Action? OpenUrlRequested;
    public event Action? Selected;

    public ConsolidatedRow(Shortcut s, PluginTheme theme, int index, Color swatch)
    {
        _shortcut = s;
        _theme = theme;
        _index = index;
        _swatch = swatch;
        Margin = new Padding(0, 3, 0, 3);
        Height = RowHeight;
        BackColor = theme.BgDark;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _launchBtn = MakeIconButton("▶", theme.Primary, Color.White, theme.PrimaryLight);
        _launchBtn.Click += (_, _) => LaunchRequested?.Invoke();
        Controls.Add(_launchBtn);

        _stopBtn = MakeIconButton("■", theme.ErrorBg, theme.ErrorColor, theme.ErrorColor);
        _stopBtn.Click += (_, _) => StopRequested?.Invoke();
        Controls.Add(_stopBtn);

        _openLink = new LinkLabel
        {
            Text = "open ↗",
            AutoSize = true,
            LinkColor = theme.Primary,
            ActiveLinkColor = theme.PrimaryLight,
            LinkBehavior = LinkBehavior.HoverUnderline,
            Font = new Font("Segoe UI", 8.5f),
            BackColor = Color.Transparent,
        };
        _openLink.LinkClicked += (_, _) => OpenUrlRequested?.Invoke();
        Controls.Add(_openLink);
        var openTip = new ToolTip();
        openTip.SetToolTip(_openLink, string.IsNullOrWhiteSpace(s.StatusUrl)
            ? "Open a preview pane — no Status URL set, so type one in the address bar (or add a Status URL to the shortcut)"
            : $"Preview {s.StatusUrl.Trim()} in the side pane");

        Click += (_, _) => Selected?.Invoke();
    }

    public void SetState(RowState state, string label, int? pid, DateTime? startedAt = null)
    {
        bool changed = _state != state || _stateLabel != label || _pid != pid;
        _state = state;
        _stateLabel = label;
        _pid = pid;
        if (startedAt.HasValue) _startedAt = startedAt;
        if (state != RowState.Running) _startedAt = startedAt;   // clear uptime when not running
        // Flash an accent border for a few seconds whenever the state changes so
        // the user notices "something happened here". Cleared by TickHighlight.
        if (changed) _highlightUntil = DateTime.UtcNow.AddSeconds(3);
        // Repaint every tick while running so the uptime advances.
        if (changed || state == RowState.Running) Invalidate();
    }

    /// <summary>Clear the post-change highlight once it has elapsed (called on
    /// the launcher's status timer). Returns the row to its normal style.</summary>
    public void TickHighlight()
    {
        if (_highlightUntil != default && DateTime.UtcNow >= _highlightUntil)
        {
            _highlightUntil = default;
            Invalidate();
        }
    }

    public void SetUrlStatus(UrlState state, string detail)
    {
        if (_urlState == state && _urlDetail == (detail ?? "")) return;
        _urlState = state;
        _urlDetail = detail ?? "";
        Invalidate();
    }

    private RoundedButton MakeIconButton(string text, Color bg, Color fg, Color hover)
    {
        var b = new RoundedButton
        {
            Text = text,
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            Size = new Size(34, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = bg,
            ForeColor = fg,
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = hover;
        return b;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_launchBtn == null || _stopBtn == null || _openLink == null) return;
        int btnTop = (RowHeight - _stopBtn.Height) / 2;
        _stopBtn.Location = new Point(Width - _stopBtn.Width - 12, btnTop);
        _launchBtn.Location = new Point(_stopBtn.Left - _launchBtn.Width - 6, btnTop);
        _openLink.Location = new Point(_launchBtn.Left - _openLink.PreferredWidth - 10,
            btnTop + (_stopBtn.Height - _openLink.PreferredHeight) / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_launchBtn == null || _stopBtn == null) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedRect(rect, 8);
        using (var bg = new SolidBrush(_theme.BgHeader)) g.FillPath(bg, path);

        // Just-changed flash: a 2px accent border in the state colour for ~3s.
        if (_highlightUntil != default && DateTime.UtcNow < _highlightUntil)
        {
            using var hp = new Pen(StateColor(_state), 2f);
            g.DrawPath(hp, path);
        }

        // Stable-colour correlation bar on the left edge.
        using (var barBrush = new SolidBrush(_swatch))
            g.FillRectangle(barBrush, 0, 6, 4, Height - 12);

        // State dot.
        int dotX = 14, dotY = 12;
        using (var dotBrush = new SolidBrush(StateColor(_state)))
            g.FillEllipse(dotBrush, dotX, dotY, 11, 11);

        int textLeft = dotX + 20;
        int textRight = (_openLink.Enabled ? _openLink.Left : _launchBtn.Left) - 10;
        if (textRight < textLeft + 80) textRight = Width - 120;

        string name = string.IsNullOrWhiteSpace(_shortcut.Name) ? "(untitled)" : _shortcut.Name;
        using (var titleFont = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold))
        using (var tbrush = new SolidBrush(_theme.TextPrimary))
        using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString($"{_index}. {name}", titleFont, tbrush, new RectangleF(textLeft, 8, textRight - textLeft, 22), sf);

        // Second line: state + pid + uptime, then URL badge.
        string detail = _stateLabel;
        if (_pid is int pid) detail += $" · pid {pid}";
        if (_state == RowState.Running && _startedAt is DateTime st)
            detail += $" · {FormatUptime(DateTime.Now - st)}";

        using (var subFont = new Font("Segoe UI", 8.5f))
        using (var subBrush = new SolidBrush(_theme.TextSecondary))
        using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(detail, subFont, subBrush, new RectangleF(textLeft, 34, textRight - textLeft, 18), sf);

        if (!string.IsNullOrWhiteSpace(_shortcut.StatusUrl))
            DrawUrlBadge(g, textLeft, textRight);
    }

    private void DrawUrlBadge(Graphics g, int textLeft, int textRight)
    {
        // Measure the detail line to place the badge after it.
        string label = UrlBadgeLabel(_urlState);
        Color color = UrlColor(_urlState);
        using var badgeFont = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold);
        var sz = g.MeasureString(label, badgeFont);
        int badgeW = (int)sz.Width + 12, badgeH = 15;
        int badgeX = textRight - badgeW;
        int badgeY = 35;
        if (badgeX < textLeft + 120) return;   // not enough room
        using var badgePath = RoundedRect(new Rectangle(badgeX, badgeY, badgeW, badgeH), 3);
        using (var fill = new SolidBrush(color)) g.FillPath(fill, badgePath);
        using (var tb = new SolidBrush(Color.White))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(label, badgeFont, tb, new RectangleF(badgeX, badgeY, badgeW, badgeH), sf);
    }

    private static string FormatUptime(TimeSpan t)
    {
        if (t.TotalSeconds < 0) t = TimeSpan.Zero;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }

    private static string UrlBadgeLabel(UrlState s) => s switch
    {
        UrlState.Healthy => "HEALTHY",
        UrlState.ServerError => "5xx",
        UrlState.Unreachable => "DOWN",
        _ => "—",
    };

    private Color UrlColor(UrlState s) => s switch
    {
        UrlState.Healthy => _theme.SuccessColor,
        UrlState.ServerError => Color.FromArgb(0xE6, 0xA5, 0x3A),
        UrlState.Unreachable => _theme.ErrorColor,
        _ => _theme.TextSecondary,
    };

    private Color StateColor(RowState s) => s switch
    {
        RowState.Building => Color.FromArgb(0x5B, 0x9B, 0xD5),   // blue — compiling
        RowState.Launching => Color.FromArgb(0xE6, 0xA5, 0x3A),
        RowState.Running => _theme.SuccessColor,
        RowState.Failed => _theme.ErrorColor,
        RowState.Exited => Color.FromArgb(0xE6, 0xA5, 0x3A),
        _ => _theme.TextSecondary,
    };

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Min(radius, Math.Min(rect.Width, rect.Height)) * 2;
        if (d <= 0) { path.AddRectangle(rect); return path; }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
