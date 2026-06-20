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

    // ── live process matching (Consolidated Launcher process-info feature) ──
    // Every 5s we find the OS process behind each shortcut — whether we started
    // it or someone else did — by Status-URL port first, working-directory
    // command-line match second. The matched root PID is remembered so Stop can
    // take down an externally-started process tree, and the active command per
    // shortcut is tracked so the "Run as ▾" variant switcher can relaunch it.
    private readonly System.Windows.Forms.Timer _procTimer;
    private bool _procPollInFlight;
    private readonly Dictionary<string, int> _matchedRoot = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime When, TimeSpan Cpu, int Pid)> _cpuState =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _activeCommand = new(StringComparer.OrdinalIgnoreCase);
    // Shortcut ids whose row has "clean bin/obj before run" enabled (dotnet only).
    private readonly HashSet<string> _cleanIds = new(StringComparer.OrdinalIgnoreCase);

    private sealed record ProcResult(
        string Id, bool Matched, int RootPid, int Pid, string Name,
        long MemoryBytes, DateTime StartUtc, TimeSpan TotalCpu, bool External);

    private ConsolidatedLauncherForm(PluginTheme theme, string folderPath, List<Shortcut> shortcuts)
    {
        _theme = theme;
        _folderPath = folderPath;
        _shortcuts = shortcuts;

        // Folder name first (so it's visible in the truncated taskbar title), and
        // use just the folder's leaf rather than the full path to keep it short.
        string folderLeaf = folderPath.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault() ?? folderPath;
        if (string.IsNullOrWhiteSpace(folderLeaf)) folderLeaf = "Shortcuts";
        Text = $"{folderLeaf} — Launcher";
        try { Icon = IconHelper.CreateAppIcon(theme.Primary); } catch { /* keep default */ }
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

        // Manual "refresh now" — re-probe each row's matched process (memory, CPU,
        // uptime) and running/health status without waiting for the 5s timer.
        var refreshBtn = MakeButton("↻ Refresh", theme.PrimaryDim, theme.TextPrimary);
        refreshBtn.Size = new Size(btnW, btnH);
        refreshBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        refreshBtn.Location = new Point(_launchAllBtn.Left - btnGap - btnW, 8);
        var refreshTip = new ToolTip();
        refreshTip.SetToolTip(refreshBtn, "Refresh process info now — memory, CPU, uptime and running status for every row.");
        refreshBtn.Click += (_, _) =>
        {
            _statusLabel.Text = "Refreshing process info…";
            RefreshStatus();
            StartProcPoll();
            _ = ProbeUrlsAsync();
        };
        header.Controls.Add(refreshBtn);

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
            row.RefreshRequested += () => RefreshOne(s.Id);
            row.VariantChosen += cmd => OnVariantChosen(s.Id, cmd);
            // Show the command up-front and offer alternative ways to run it
            // (dotnet run ↔ dotnet watch ↔ Release) via the "Run as ▾" switcher.
            string baseCmd = ShortcutLauncher.BuildProfileCmdline(s);
            _activeCommand[s.Id] = baseCmd;
            row.SetVariants(LaunchVariants.For(baseCmd));

            // dotnet-only "clean bin/obj before run" toggle, persisted per shortcut.
            // With clean on, the shown/run command drops --no-build/--no-restore
            // (clean wipes the build output, so those would fail).
            bool isDotnet = IsDotnetCommand(baseCmd);
            bool cleanInit = isDotnet && ConsolidatedSettings.GetCleanBinObj(folderPath, s.Id);
            if (cleanInit) _cleanIds.Add(s.Id);
            row.SetCommand(cleanInit ? StripNoBuildFlags(baseCmd) : baseCmd);
            row.SetCleanOption(isDotnet, cleanInit);
            row.SetKeepRunning(s.ExcludeFromStopAll);
            row.CleanToggled += on =>
            {
                if (on) _cleanIds.Add(s.Id); else _cleanIds.Remove(s.Id);
                ConsolidatedSettings.SetCleanBinObj(folderPath, s.Id, on);
                string cur = _activeCommand.TryGetValue(s.Id, out var ac2) && !string.IsNullOrWhiteSpace(ac2)
                    ? ac2 : baseCmd;
                row.SetCommand(on && IsDotnetCommand(cur) ? StripNoBuildFlags(cur) : cur);
            };

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
                    // Left column (rows + console) ≈ 30%; right preview takes the rest.
                    int oDist = Math.Clamp((int)(oW * 0.30), 300, oW - 360);
                    outerSplit.SplitterDistance = oDist;
                    outerSplit.Panel1MinSize = 300;
                    outerSplit.Panel2MinSize = 340;
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

            // First process probe right away so info appears without waiting 5s.
            StartProcPoll();
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

        // Finds + refreshes the matched OS process info for each row every 5s.
        _procTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _procTimer.Tick += (_, _) => StartProcPoll();
        _procTimer.Start();
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

                // Clean bin/obj before the build, if this row has it enabled, so a
                // stale/locked output dir doesn't poison the fresh compile.
                if (_cleanIds.Contains(s.Id))
                {
                    row.SetState(ConsolidatedRow.RowState.Building, $"Cleaning ({i}/{n})…", null);
                    EmitBanner(s.Id, $"[{i}/{n}] clean · removing bin/obj before build");
                    string runCmd = _activeCommand.TryGetValue(s.Id, out var rc) && !string.IsNullOrWhiteSpace(rc)
                        ? rc : ShortcutLauncher.BuildProfileCmdline(s);
                    try { await CleanBinObjAsync(s.Id, runCmd, s.WorkingDirectory); }
                    catch (Exception ex) { _pending.Enqueue((s.Id, $"✖ clean error: {ex.Message}", true)); }
                    if (ct.IsCancellationRequested) return;
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
            // skipClean: we already cleaned above, before the build.
            foreach (var s in ranAfterBuild)
            {
                if (ct.IsCancellationRequested || IsDisposed) return;
                LaunchOne(s.Id, focus: false, skipClean: true);
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

    private void LaunchOne(string id, bool focus = true, string? commandOverride = null, bool skipClean = false)
    {
        var s = _shortcuts.FirstOrDefault(x => x.Id == id);
        if (s is null) return;
        var row = _rowsById[id];

        // URL shortcut → open it in the preview pane; no captured process.
        if (ShortcutLauncher.IsUrl(s))
        {
            var url = (s.Args ?? "").Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                row.SetState(ConsolidatedRow.RowState.Failed, "No URL set", null);
                return;
            }
            _autoOpened.Add(id);
            try { _browserTabs.OpenOrFocus(id, ShortName(s), url); }
            catch (Exception ex)
            {
                _logTabs.AppendLine(id, $"[{DateTime.Now:HH:mm:ss}] ✖ Preview failed: {ex.Message}", isError: true);
            }
            row.SetState(ConsolidatedRow.RowState.Running, "Opened in preview ↗", null);
            LogLauncher($"↗ {ShortName(s)} — opened URL in preview: {url}");
            ShortcutStore.RecordLaunch(id);
            if (s.AutoLoginEnabled)
            {
                AutoLoginRunner.RunInBackground(s, msg =>
                {
                    if (!IsDisposed)
                        _pending.Enqueue((ConsolidatedLogTabs.LauncherTabKey,
                            $"[{DateTime.Now:HH:mm:ss}] {ShortName(s)} · auto-login: {msg}", false));
                });
            }
            return;
        }

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

        // Use the chosen "Run as" variant if one was picked, else the shortcut's
        // configured command. Remember it so the row + variant menu stay in sync.
        string command = commandOverride
            ?? (_activeCommand.TryGetValue(id, out var ac) && !string.IsNullOrWhiteSpace(ac)
                ? ac : ShortcutLauncher.BuildProfileCmdline(s));
        _activeCommand[id] = command;

        // Optional pre-run clean of bin/ + obj/ (dotnet rows with the toggle on).
        // Clean wipes the build/restore output, so a configured --no-build /
        // --no-restore would then fail — strip them so dotnet rebuilds. Runs on a
        // background thread with retry-on-lock, then starts the runner.
        bool cleanOn = !skipClean && _cleanIds.Contains(id) && IsDotnetCommand(command);
        string runCommand = cleanOn ? StripNoBuildFlags(command) : command;
        row.SetCommand(runCommand);

        if (cleanOn)
        {
            row.SetState(ConsolidatedRow.RowState.Building, "Cleaning bin/obj…", null);
            EmitBanner(id, "clean · removing bin/obj before run");
            string workDir = s.WorkingDirectory, cmd = runCommand;
            // Reflect each step of the clean on the row's status line.
            void OnCleanStatus(string status) => RunOnUi(() =>
            {
                if (!IsDisposed && _rowsById.TryGetValue(id, out var r0))
                    r0.SetState(ConsolidatedRow.RowState.Building, status, null);
            });
            _ = Task.Run(async () =>
            {
                try { await CleanBinObjAsync(id, cmd, workDir, OnCleanStatus); }
                catch (Exception ex) { _pending.Enqueue((id, $"✖ clean error: {ex.Message}", true)); }
                RunOnUi(() => { if (!IsDisposed) StartRunnerCore(id, cmd, focus); });
            });
            return;
        }

        StartRunnerCore(id, runCommand, focus);
    }

    /// <summary>Construct + start the captured runner for <paramref name="id"/> with
    /// <paramref name="command"/>, wire output/exit, and kick off auto-login. Split
    /// out of <see cref="LaunchOne"/> so an optional pre-run clean can run first on a
    /// background thread and then resume here on the UI thread.</summary>
    private void StartRunnerCore(string id, string command, bool focus)
    {
        var s = _shortcuts.FirstOrDefault(x => x.Id == id);
        if (s is null) return;
        var row = _rowsById[id];

        // A launch may have raced in while we were cleaning — don't double-start.
        if (_runners.TryGetValue(id, out var live) && live.IsRunning)
        {
            if (focus) _logTabs.FocusTab(id);
            return;
        }

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
        LogLauncher($"▶ Starting {ShortName(s)} (pid {runner.Pid}) — {command}");

        // Reuse the existing per-shortcut visible auto-login (separate Edge).
        // Route its progress into the launcher console (thread-safe enqueue, so
        // the user can see whether/why auto-login ran).
        ShortcutStore.RecordLaunch(id);
        if (s.AutoLoginEnabled)
        {
            AutoLoginRunner.RunInBackground(s, msg =>
            {
                if (!IsDisposed)
                    _pending.Enqueue((ConsolidatedLogTabs.LauncherTabKey,
                        $"[{DateTime.Now:HH:mm:ss}] {ShortName(s)} · auto-login: {msg}", false));
            });
        }
    }

    // ─────────────────────────── bin/obj clean ───────────────────────────

    private static bool IsDotnetCommand(string command)
    {
        var toks = Tokenize(command.Trim());
        if (toks.Count == 0) return false;
        string exe = Unquote(toks[0]);
        int slash = exe.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) exe = exe.Substring(slash + 1);
        if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = exe[..^4];
        return exe.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Remove <c>--no-build</c> / <c>--no-restore</c> from a command. Used
    /// when "clean bin/obj" is on: cleaning wipes the build + restore output, so
    /// those flags would make <c>dotnet run</c> fail ("cannot find …exe"). Stripping
    /// them lets dotnet rebuild/restore.</summary>
    private static string StripNoBuildFlags(string command)
    {
        var kept = new List<string>();
        foreach (var t in Tokenize(command))
        {
            var u = Unquote(t).ToLowerInvariant();
            if (u is "--no-build" or "--no-restore") continue;
            kept.Add(t);
        }
        return string.Join(" ", kept);
    }

    /// <summary>Delete bin/ and obj/ under the working directory (and the
    /// <c>--project</c> directory, if the command targets one) before running.
    /// Reports progress into the shortcut's console.</summary>
    private async Task CleanBinObjAsync(string id, string command, string workingDir, Action<string>? onStatus = null)
    {
        var baseDirs = ResolveCleanDirs(command, workingDir);
        int removed = 0, failed = 0, found = 0;
        foreach (var baseDir in baseDirs)
            foreach (var sub in new[] { "bin", "obj" })
            {
                string target = Path.Combine(baseDir, sub);
                if (!Directory.Exists(target)) continue;
                found++;
                onStatus?.Invoke($"Cleaning {sub}/ …");
                if (await DeleteDirWithRetryAsync(id, target, onStatus)) removed++; else failed++;
            }
        _pending.Enqueue((id,
            $"[{DateTime.Now:HH:mm:ss}] clean · done — {removed} folder(s) removed{(failed > 0 ? $", {failed} still locked" : "")}.",
            failed > 0));
        onStatus?.Invoke(
            failed > 0 ? $"Clean: {failed} locked — starting anyway…"
            : found == 0 ? "Nothing to clean — starting…"
            : "Cleaned ✓ — starting…");
    }

    /// <summary>Recursively delete <paramref name="dir"/>, retrying up to 10 times
    /// when a file is locked, with a progressive 1s → 2s → 3s back-off, then give up.</summary>
    private async Task<bool> DeleteDirWithRetryAsync(string id, string dir, Action<string>? onStatus = null)
    {
        string sub = Path.GetFileName(dir.TrimEnd('\\', '/'));
        const int maxAttempts = 10;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await Task.Run(() => Directory.Delete(dir, recursive: true));
                _pending.Enqueue((id, $"[{DateTime.Now:HH:mm:ss}] clean · removed {ShortPath(dir)}", false));
                return true;
            }
            catch (DirectoryNotFoundException) { return true; }   // already gone
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= maxAttempts)
                {
                    onStatus?.Invoke($"{sub}/ locked — gave up");
                    _pending.Enqueue((id, $"[{DateTime.Now:HH:mm:ss}] clean · gave up on {ShortPath(dir)} after {maxAttempts} tries — locked.", true));
                    return false;
                }
                int delaySec = Math.Min(attempt, 3);              // 1s, 2s, 3s, 3s…
                onStatus?.Invoke($"{sub}/ locked — retry {attempt}/{maxAttempts} in {delaySec}s…");
                _pending.Enqueue((id, $"[{DateTime.Now:HH:mm:ss}] clean · {ShortPath(dir)} locked — retry {attempt}/{maxAttempts} in {delaySec}s…", false));
                await Task.Delay(delaySec * 1000);
            }
            catch (Exception ex)
            {
                _pending.Enqueue((id, $"[{DateTime.Now:HH:mm:ss}] clean · error on {ShortPath(dir)}: {ex.Message}", true));
                return false;
            }
        }
        return false;
    }

    /// <summary>Directories to clean: the working directory plus any <c>--project</c>
    /// directory referenced by the command (deduped, absolute).</summary>
    private static List<string> ResolveCleanDirs(string command, string workingDir)
    {
        var result = new List<string>();
        void Add(string? d)
        {
            if (string.IsNullOrWhiteSpace(d)) return;
            try
            {
                string full = Path.GetFullPath(d);
                if (!result.Any(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase)))
                    result.Add(full);
            }
            catch { }
        }

        Add(workingDir);

        var toks = Tokenize(command);
        for (int i = 0; i < toks.Count; i++)
        {
            var u = Unquote(toks[i]);
            string? proj = null;
            if (u.Equals("--project", StringComparison.OrdinalIgnoreCase) && i + 1 < toks.Count)
                proj = Unquote(toks[i + 1]);
            else if (u.StartsWith("--project=", StringComparison.OrdinalIgnoreCase))
                proj = u.Substring("--project=".Length);
            if (string.IsNullOrWhiteSpace(proj)) continue;

            string resolved = Path.IsPathRooted(proj) ? proj : Path.Combine(workingDir, proj);
            string dir = File.Exists(resolved) || resolved.EndsWith("proj", StringComparison.OrdinalIgnoreCase)
                ? (Path.GetDirectoryName(resolved) ?? resolved)
                : resolved;
            Add(dir);
        }
        return result;
    }

    private static string ShortPath(string p)
    {
        try
        {
            var parts = p.TrimEnd('\\', '/').Split('\\', '/');
            return parts.Length >= 2 ? $"…\\{parts[^2]}\\{parts[^1]}" : p;
        }
        catch { return p; }
    }

    /// <summary>Append a timestamped line to the shared "Launcher" console tab —
    /// the consolidated activity log (build/start/exit/auto-login).</summary>
    private void LogLauncher(string message) =>
        _logTabs.AppendLine(ConsolidatedLogTabs.LauncherTabKey, $"[{DateTime.Now:HH:mm:ss}] {message}");

    private void StopAll()
    {
        // Cancel an in-progress sequential build and kill any running build.
        _buildCts?.Cancel();
        foreach (var p in _buildProcs.Values.ToList())
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }

        int stopped = 0, kept = 0;
        foreach (var s in _shortcuts)
        {
            // "Keep running on Stop All" shortcuts (always-on support tools) are
            // left alone here; the row's own ■ still stops them explicitly.
            if (s.ExcludeFromStopAll)
            {
                bool live = (_runners.TryGetValue(s.Id, out var r) && r.IsRunning)
                            || _matchedRoot.ContainsKey(s.Id);
                if (live) kept++;
                continue;
            }
            if (StopOne(s.Id, silent: true)) stopped++;
        }
        var msg = stopped == 0 ? "Nothing to stop." : $"Stopped {stopped} process(es).";
        if (kept > 0) msg += $" {kept} kept running.";
        _statusLabel.Text = msg;
    }

    private bool StopOne(string id) => StopOne(id, silent: false);

    private bool StopOne(string id, bool silent)
    {
        // URL shortcut → "stop" just closes its preview tab.
        var su = _shortcuts.FirstOrDefault(x => x.Id == id);
        if (su != null && ShortcutLauncher.IsUrl(su))
        {
            bool wasOpen = _browserTabs.HasTab(id);
            if (wasOpen) _browserTabs.CloseTab(id);
            _autoOpened.Remove(id);
            _rowsById[id].SetState(ConsolidatedRow.RowState.Stopped, "Stopped", null);
            if (!silent) _statusLabel.Text = wasOpen ? "Closed 1 preview." : "Nothing to stop.";
            return wasOpen;
        }

        bool stopped = false;

        if (_runners.TryGetValue(id, out var runner))
        {
            // We started it — stop our captured process tree.
            bool wasRunning = runner.IsRunning;
            try { runner.Stop(); } catch { }
            DisposeRunner(id);
            if (wasRunning) { stopped = true; EmitBanner(id, "stopped"); }
        }
        else if (_matchedRoot.TryGetValue(id, out var root) && root > 0)
        {
            // We didn't start it, but matched an externally-started process —
            // take down its whole tree (e.g. a dotnet watch supervisor + child).
            ProcessProbe.KillTree(root);
            _matchedRoot.Remove(id);
            stopped = true;
            EmitBanner(id, $"stopped external process (pid {root})");
        }

        _cpuState.Remove(id);
        var row = _rowsById[id];
        row.SetState(ConsolidatedRow.RowState.Stopped, "Stopped", null);
        row.ClearProcess();
        if (!silent)
            _statusLabel.Text = stopped ? "Stopped 1 process." : "Nothing to stop.";
        return stopped;
    }

    /// <summary>A "Run as ▾" variant was chosen on a row: stop whatever's running
    /// for that shortcut (ours or external) and relaunch under the new command,
    /// captured here. e.g. switch a running <c>dotnet run</c> to <c>dotnet watch</c>.</summary>
    private void OnVariantChosen(string id, string command)
    {
        var s = _shortcuts.FirstOrDefault(x => x.Id == id);
        string name = s != null ? ShortName(s) : id;
        LogLauncher($"↻ {name} — switching to: {command}");
        StopOne(id, silent: true);
        LaunchOne(id, focus: true, commandOverride: command);
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
        var s = _shortcuts.FirstOrDefault(x => x.Id == id);
        string name = s != null ? ShortName(s) : id;
        EmitBanner(id, code == 0 ? "exited (code 0)" : $"exited (code {code})", isError: code != 0);
        LogLauncher($"■ {name} exited (code {code}).");
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
                // No Status URL to gate on, but a Home URL is set — auto-open it
                // once the process is up (health-gated path handles the rest).
                if (string.IsNullOrWhiteSpace(s.StatusUrl) && !string.IsNullOrWhiteSpace(s.HomeUrl))
                    MaybeAutoOpenBrowser(s);
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

    /// <summary>The page to open in the preview pane: the Home URL if set, else the
    /// Status URL. (The Status URL is only used for health polling, not opened.)</summary>
    private static string PreviewUrl(Shortcut s)
    {
        var home = s.HomeUrl?.Trim();
        return !string.IsNullOrEmpty(home) ? home : (s.StatusUrl?.Trim() ?? "");
    }

    /// <summary>Auto-open the preview tab once, while the process is running:
    /// triggered when the Status URL goes healthy, or — when there's no Status URL
    /// to poll — as soon as the process is up (see <see cref="RefreshStatus"/>).
    /// Opens the <see cref="PreviewUrl"/> (Home URL preferred).</summary>
    private void MaybeAutoOpenBrowser(Shortcut s)
    {
        if (_autoOpened.Contains(s.Id)) return;
        if (!(_runners.TryGetValue(s.Id, out var r) && r.IsRunning)) return;
        string url = PreviewUrl(s);
        if (string.IsNullOrEmpty(url)) return;
        _autoOpened.Add(s.Id);
        try { _browserTabs.OpenOrFocus(s.Id, ShortName(s), url); }
        catch (Exception ex)
        {
            PluginLog.Error($"Consolidated auto-preview failed for '{s.Name}'", ex);
            _logTabs.AppendLine(s.Id, $"[{DateTime.Now:HH:mm:ss}] ✖ Auto-preview failed: {ex.Message}", isError: true);
        }
    }

    private void OpenBrowser(Shortcut s)
    {
        string url = PreviewUrl(s);
        _autoOpened.Add(s.Id);
        try
        {
            _browserTabs.OpenOrFocus(s.Id, ShortName(s), url);
            if (string.IsNullOrEmpty(url))
                _logTabs.AppendLine(s.Id, $"[{DateTime.Now:HH:mm:ss}] No Home/Status URL set — opened a blank preview. Type a URL in the address bar, or add a Home URL to the shortcut to enable auto-preview.");
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Consolidated preview failed to open for '{s.Name}'", ex);
            _logTabs.AppendLine(s.Id, $"[{DateTime.Now:HH:mm:ss}] ✖ Preview failed to open: {ex.Message}", isError: true);
            _logTabs.FocusTab(s.Id);
        }
    }

    // ─────────────────────── live process matching ───────────────────────

    /// <summary>Kick off a background sweep that matches each shortcut to a running
    /// OS process (port-first, working-directory fallback), reads its live stats,
    /// and marshals the results back to the rows. Re-entrancy guarded so a slow
    /// WMI snapshot can't stack up behind the 5s timer.</summary>
    private void StartProcPoll()
    {
        if (_procPollInFlight || IsDisposed) return;
        _procPollInFlight = true;

        // Snapshot the per-row inputs on the UI thread (so the background sweep
        // never touches _runners / shortcut state directly).
        var inputs = new List<(string Id, int Port, string WorkDir, bool HasRunner, int RunnerPid)>();
        foreach (var s in _shortcuts)
        {
            bool hasRunner = _runners.TryGetValue(s.Id, out var r) && r.IsRunning;
            int rpid = hasRunner && r!.Pid is int p ? p : -1;
            inputs.Add((s.Id, ParsePort(s.StatusUrl), s.WorkingDirectory ?? "", hasRunner, rpid));
        }

        Task.Run(() =>
        {
            List<ProcResult> results;
            try { results = ProbeProcesses(inputs); }
            catch (Exception ex) { PluginLog.Warn($"ProcessProbe poll failed: {ex.Message}"); results = new(); }
            RunOnUi(() =>
            {
                try { if (!IsDisposed) ApplyProcResults(results); }
                finally { _procPollInFlight = false; }
            });
        });
    }

    /// <summary>Re-probe a single shortcut's process on demand (the per-row ↻
    /// button): refresh its running state, matched-process stats, and — if it has
    /// a Status URL — its health badge, without touching the other rows.</summary>
    private void RefreshOne(string id)
    {
        var s = _shortcuts.FirstOrDefault(x => x.Id == id);
        if (s is null || !_rowsById.TryGetValue(id, out var row)) return;

        bool hasRunner = _runners.TryGetValue(id, out var r) && r.IsRunning;
        int rpid = hasRunner && r!.Pid is int p ? p : -1;
        if (hasRunner) row.SetState(ConsolidatedRow.RowState.Running, "Running", rpid, r!.StartedAt);

        var inputs = new List<(string Id, int Port, string WorkDir, bool HasRunner, int RunnerPid)>
        {
            (id, ParsePort(s.StatusUrl), s.WorkingDirectory ?? "", hasRunner, rpid),
        };
        Task.Run(() =>
        {
            List<ProcResult> results;
            try { results = ProbeProcesses(inputs); }
            catch (Exception ex) { PluginLog.Warn($"ProcessProbe row refresh failed: {ex.Message}"); results = new(); }
            RunOnUi(() => { if (!IsDisposed) ApplyProcResults(results); });
        });

        var url = s.StatusUrl?.Trim();
        if (!string.IsNullOrEmpty(url)) _ = ProbeOneAsync(s, row, url);
    }

    /// <summary>Background half of the sweep: one port table + one process-tree
    /// snapshot for the whole batch, a lazily-fetched WMI command-line map only if
    /// a port match misses, then per-shortcut matching + live info.</summary>
    private static List<ProcResult> ProbeProcesses(
        List<(string Id, int Port, string WorkDir, bool HasRunner, int RunnerPid)> inputs)
    {
        var results = new List<ProcResult>(inputs.Count);
        Dictionary<int, int>? ports = null;
        Dictionary<int, (int Parent, string Name)>? snap = null;
        Dictionary<int, string>? cmdLines = null;
        try { ports = ProcessProbe.GetListenerPids(); } catch { }
        try { snap = ProcessTree.SnapshotByPid(); } catch { }

        foreach (var inp in inputs)
        {
            int leafPid = 0;

            // 1) Port → owning listener PID (most reliable for servers).
            if (inp.Port > 0 && ports != null && ports.TryGetValue(inp.Port, out var pp)) leafPid = pp;

            // 2) Fallback: a process whose command line references the work dir.
            if (leafPid == 0 && inp.WorkDir.Length > 0)
            {
                cmdLines ??= TrySnapshotCommandLines();
                leafPid = MatchByWorkDir(cmdLines, inp.WorkDir);
            }

            // 3) Last resort: the process we started ourselves (cmd.exe wrapper).
            if (leafPid == 0 && inp.HasRunner && inp.RunnerPid > 0) leafPid = inp.RunnerPid;

            bool external = !inp.HasRunner;
            if (leafPid <= 0)
            {
                results.Add(new ProcResult(inp.Id, false, 0, 0, "", 0, default, default, external));
                continue;
            }

            int root = snap != null ? ProcessProbe.ResolveRoot(leafPid, snap) : leafPid;
            if (!ProcessProbe.TryGetInfo(leafPid, out var info)
                && !ProcessProbe.TryGetInfo(root, out info))
            {
                results.Add(new ProcResult(inp.Id, false, 0, 0, "", 0, default, default, external));
                continue;
            }
            results.Add(new ProcResult(inp.Id, true, root, info.Pid, info.Name,
                info.MemoryBytes, info.StartTimeUtc, info.TotalCpu, external));
        }
        return results;
    }

    private static Dictionary<int, string> TrySnapshotCommandLines()
    {
        try { return ProcessProbe.SnapshotCommandLines(); } catch { return new(); }
    }

    /// <summary>Find a process whose command line contains the working directory,
    /// preferring a dotnet/node host (the actual server) over an incidental hit.</summary>
    private static int MatchByWorkDir(Dictionary<int, string> cmdLines, string workDir)
    {
        string needle = workDir.TrimEnd('\\', '/');
        if (needle.Length < 3) return 0;
        int firstHit = 0;
        foreach (var kv in cmdLines)
        {
            if (kv.Value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (kv.Value.IndexOf("dotnet", StringComparison.OrdinalIgnoreCase) >= 0
                || kv.Value.IndexOf("node", StringComparison.OrdinalIgnoreCase) >= 0)
                return kv.Key;
            if (firstHit == 0) firstHit = kv.Key;
        }
        return firstHit;
    }

    /// <summary>Explicit port from a Status URL (localhost:5000) — null/blank, no
    /// scheme, or a scheme-default (80/443) port returns 0 so we don't match an
    /// unrelated server on the default port.</summary>
    private static int ParsePort(string? statusUrl)
    {
        if (string.IsNullOrWhiteSpace(statusUrl)) return 0;
        if (!Uri.TryCreate(statusUrl.Trim(), UriKind.Absolute, out var uri)) return 0;
        string host = uri.Host, authority = uri.Authority;
        // Authority is "host:port" only when a port was explicitly written.
        if (authority.Length > host.Length && authority[host.Length] == ':' && uri.Port > 0)
            return uri.Port;
        return 0;
    }

    /// <summary>Apply the sweep results to the rows (UI thread): compute CPU% from
    /// the per-shortcut delta, remember the root PID of externally-started matches
    /// (so Stop can kill them), and update the live process line.</summary>
    private void ApplyProcResults(List<ProcResult> results)
    {
        var nowUtc = DateTime.UtcNow;
        foreach (var r in results)
        {
            if (!_rowsById.TryGetValue(r.Id, out var row)) continue;
            bool ours = _runners.TryGetValue(r.Id, out var rn) && rn.IsRunning;

            if (!r.Matched)
            {
                bool wasExternal = _matchedRoot.Remove(r.Id);
                _cpuState.Remove(r.Id);
                row.ClearProcess();
                // A previously-shown external process is gone — reset its row.
                if (wasExternal && !ours && row.State == ConsolidatedRow.RowState.Running)
                    row.SetState(ConsolidatedRow.RowState.Stopped, "Stopped", null);
                continue;
            }

            double cpuPct = -1;
            if (_cpuState.TryGetValue(r.Id, out var prev) && prev.Pid == r.Pid)
            {
                double wallMs = (nowUtc - prev.When).TotalMilliseconds;
                double cpuMs = (r.TotalCpu - prev.Cpu).TotalMilliseconds;
                if (wallMs > 0)
                    cpuPct = Math.Clamp(cpuMs / (wallMs * Math.Max(1, Environment.ProcessorCount)) * 100.0, 0, 100);
            }
            _cpuState[r.Id] = (nowUtc, r.TotalCpu, r.Pid);

            if (r.External)
            {
                _matchedRoot[r.Id] = r.RootPid;
                // Externally started but genuinely running — reflect it on the row.
                if (!ours && row.State != ConsolidatedRow.RowState.Running)
                    row.SetState(ConsolidatedRow.RowState.Running, "Running", null);
            }
            else _matchedRoot.Remove(r.Id);

            string? uptime = null;
            if (r.StartUtc != default)
            {
                var up = nowUtc - r.StartUtc;
                if (up > TimeSpan.Zero) uptime = FormatUptime(up);
            }
            string? cpu = cpuPct >= 0 ? cpuPct.ToString("0.0") + "%" : null;
            row.SetProcess(string.IsNullOrEmpty(r.Name) ? "proc" : r.Name, r.Pid,
                FormatMem(r.MemoryBytes), uptime, cpu, r.External);
        }
    }

    private static string FormatMem(long bytes)
    {
        double mb = bytes / 1024.0 / 1024.0;
        return mb >= 1024 ? (mb / 1024.0).ToString("0.0") + " GB" : mb.ToString("0") + " MB";
    }

    private static string FormatUptime(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{(int)t.TotalSeconds}s";
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
        _procTimer.Stop(); _procTimer.Dispose();
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

    private const int RowHeight = 96;
    private static readonly Color Amber = Color.FromArgb(0xE6, 0xA5, 0x3A);

    private readonly Shortcut _shortcut;
    private readonly PluginTheme _theme;
    private readonly int _index;
    private readonly Color _swatch;
    private readonly RoundedButton _launchBtn;
    private readonly RoundedButton _stopBtn;
    private readonly LinkLabel _openLink;
    private readonly RoundedButton _variantsBtn;
    private readonly RoundedButton _refreshBtn;
    private readonly RoundedButton _cleanBtn;
    private readonly ContextMenuStrip _variantsMenu;

    private RowState _state = RowState.Stopped;
    private string _stateLabel = "Stopped";
    private DateTime? _startedAt;
    private UrlState _urlState = UrlState.NotConfigured;
    private string _urlDetail = "";
    private DateTime _highlightUntil;   // brief accent border after a state change
    private string _command = "";       // the command line that will run / is running
    // Live matched-process info, rendered as chips (#pid · mem · up · cpu).
    private bool _hasProc;
    private string? _procName;
    private int _procPid;
    private string? _procMem;
    private string? _procUptime;
    private string? _procCpu;
    private bool _procExternal;         // matched process wasn't started by us
    private List<(string Label, string Command)> _variants = new();
    private bool _cleanApplicable;      // dotnet command → show the Clean toggle button
    private bool _cleanOn;              // clean bin/obj before run
    private bool _keepRunning;          // excluded from Stop All (📌 in the title)

    // Faint, theme-adaptive chip fills (a tint of the foreground over the row).
    private static readonly Color AmberDim = Color.FromArgb(0x40, 0xE6, 0xA5, 0x3A);

    public RowState State => _state;

    public event Action? LaunchRequested;
    public event Action? StopRequested;
    public event Action? OpenUrlRequested;
    public event Action? Selected;
    public event Action? RefreshRequested;         // re-probe this row's process now
    public event Action<string>? VariantChosen;   // a "Run as" variant command was picked
    public event Action<bool>? CleanToggled;       // "clean bin/obj before run" toggled

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
        string previewUrl = !string.IsNullOrWhiteSpace(s.HomeUrl) ? s.HomeUrl.Trim()
            : (s.StatusUrl?.Trim() ?? "");
        var openTip = new ToolTip();
        openTip.SetToolTip(_openLink, string.IsNullOrEmpty(previewUrl)
            ? "Open a preview pane — no Home/Status URL set, so type one in the address bar (or add a Home URL to the shortcut)"
            : $"Preview {previewUrl} in the side pane");

        // "Run as ▾" variant switcher (hidden unless there are alternatives).
        _variantsMenu = new ContextMenuStrip { BackColor = theme.BgHeader, ForeColor = theme.TextPrimary };
        _variantsBtn = new RoundedButton
        {
            Text = "Run as ▾",
            Font = new Font("Segoe UI", 8f),
            Size = new Size(78, 22),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextPrimary,
            Cursor = Cursors.Hand,
            TabStop = false,
            Visible = false,
        };
        _variantsBtn.FlatAppearance.BorderSize = 0;
        _variantsBtn.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        _variantsBtn.Click += (_, _) => ShowVariantsMenu();
        Controls.Add(_variantsBtn);

        // Small per-row "refresh now" — re-probe this process's memory/CPU/status.
        _refreshBtn = new RoundedButton
        {
            Text = "↻",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Size = new Size(26, 22),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextPrimary,
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        _refreshBtn.FlatAppearance.BorderSize = 0;
        _refreshBtn.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        _refreshBtn.Click += (_, _) => RefreshRequested?.Invoke();
        Controls.Add(_refreshBtn);
        var refreshTip = new ToolTip();
        refreshTip.SetToolTip(_refreshBtn, "Refresh this process now — memory, CPU, uptime and status.");

        // "Clean bin/obj before run" toggle (dotnet rows only). Styled like the
        // other right-side buttons — not a flat chip — so it reads as a control,
        // and sits next to Run as ▾ / ↻ at the bottom-right.
        _cleanBtn = new RoundedButton
        {
            Text = "☐ Clean bin/obj",
            Font = new Font("Segoe UI", 8f),
            Size = new Size(114, 22),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextSecondary,
            Cursor = Cursors.Hand,
            TabStop = false,
            Visible = false,
        };
        _cleanBtn.FlatAppearance.BorderSize = 0;
        _cleanBtn.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        _cleanBtn.Click += (_, _) => { _cleanOn = !_cleanOn; ApplyCleanStyle(); CleanToggled?.Invoke(_cleanOn); };
        Controls.Add(_cleanBtn);
        var cleanTip = new ToolTip();
        cleanTip.SetToolTip(_cleanBtn, "Delete bin/ and obj/ before running this dotnet shortcut (retries if files are locked).");
    }

    /// <summary>Mark this row as excluded from Stop All (shows a 📌 in the title).</summary>
    public void SetKeepRunning(bool on)
    {
        if (_keepRunning == on) return;
        _keepRunning = on;
        Invalidate();
    }

    /// <summary>Show/hide the "Clean bin/obj" toggle (dotnet only) and set its state.</summary>
    public void SetCleanOption(bool applicable, bool enabled)
    {
        _cleanApplicable = applicable;
        _cleanOn = enabled && applicable;
        _cleanBtn.Visible = applicable;
        ApplyCleanStyle();
    }

    private void ApplyCleanStyle()
    {
        if (_cleanBtn == null) return;
        _cleanBtn.Text = (_cleanOn ? "☑ " : "☐ ") + "Clean bin/obj";
        _cleanBtn.BackColor = _cleanOn ? _theme.SuccessColor : _theme.PrimaryDim;
        _cleanBtn.ForeColor = _cleanOn ? Color.White : _theme.TextSecondary;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        Selected?.Invoke();
    }

    /// <summary>The command line shown on the row (what will run / is running).</summary>
    public void SetCommand(string command)
    {
        _command = command ?? "";
        Invalidate();
    }

    /// <summary>Populate the "Run as ▾" alternatives; hidden when there's ≤1.</summary>
    public void SetVariants(List<(string Label, string Command)> variants)
    {
        _variants = variants ?? new();
        _variantsBtn.Visible = _variants.Count > 1;
    }

    /// <summary>Set the live matched-process info shown as chips. Each metric is a
    /// pre-formatted string (or null to omit that chip).</summary>
    public void SetProcess(string? name, int pid, string? mem, string? uptime, string? cpu, bool external)
    {
        _hasProc = true;
        _procName = name;
        _procPid = pid;
        _procMem = mem;
        _procUptime = uptime;
        _procCpu = cpu;
        _procExternal = external;
        Invalidate();
    }

    /// <summary>Hide the process chips (no matching process for this shortcut).</summary>
    public void ClearProcess()
    {
        if (!_hasProc) return;
        _hasProc = false;
        _procName = _procMem = _procUptime = _procCpu = null;
        _procPid = 0;
        _procExternal = false;
        Invalidate();
    }

    private void ShowVariantsMenu()
    {
        _variantsMenu.Items.Clear();
        foreach (var (label, command) in _variants)
        {
            bool current = string.Equals(command.Trim(), _command.Trim(), StringComparison.OrdinalIgnoreCase);
            var item = new ToolStripMenuItem(label) { Checked = current, ForeColor = _theme.TextPrimary };
            string cmd = command;
            item.Click += (_, _) => { if (!current) VariantChosen?.Invoke(cmd); };
            _variantsMenu.Items.Add(item);
        }
        _variantsMenu.Show(_variantsBtn, new Point(0, _variantsBtn.Height));
    }

    public void SetState(RowState state, string label, int? pid, DateTime? startedAt = null)
    {
        bool changed = _state != state || _stateLabel != label;
        _state = state;
        _stateLabel = label;
        if (startedAt.HasValue) _startedAt = startedAt;
        if (state != RowState.Running) _startedAt = startedAt;
        if (changed) _highlightUntil = DateTime.UtcNow.AddSeconds(3);
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
        if (_launchBtn == null || _stopBtn == null || _openLink == null
            || _variantsBtn == null || _refreshBtn == null || _cleanBtn == null) return;
        int btnTop = 12;
        _stopBtn.Location = new Point(Width - _stopBtn.Width - 12, btnTop);
        _launchBtn.Location = new Point(_stopBtn.Left - _launchBtn.Width - 6, btnTop);
        _openLink.Location = new Point(_launchBtn.Left - _openLink.PreferredWidth - 10,
            btnTop + (_stopBtn.Height - _openLink.PreferredHeight) / 2);

        // Bottom-right cluster (right→left): [↻] [Run as ▾] [Clean bin/obj].
        int cy = RowHeight - _refreshBtn.Height - 12;
        _refreshBtn.Location = new Point(Width - _refreshBtn.Width - 12, cy);
        int next = _refreshBtn.Left;
        if (_variantsBtn.Visible)
        {
            _variantsBtn.Location = new Point(next - _variantsBtn.Width - 6, cy);
            next = _variantsBtn.Left;
        }
        if (_cleanBtn.Visible)
            _cleanBtn.Location = new Point(next - _cleanBtn.Width - 6, cy);
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

        if (_highlightUntil != default && DateTime.UtcNow < _highlightUntil)
        {
            using var hp = new Pen(StateColor(_state), 2f);
            g.DrawPath(hp, path);
        }

        using (var barBrush = new SolidBrush(_swatch))
            g.FillRectangle(barBrush, 0, 6, 4, Height - 12);

        int dotX = 14, dotY = 12;
        using (var dotBrush = new SolidBrush(StateColor(_state)))
            g.FillEllipse(dotBrush, dotX, dotY, 11, 11);

        int textLeft = dotX + 20;
        int textRight = (_openLink.Enabled ? _openLink.Left : _launchBtn.Left) - 10;
        if (textRight < textLeft + 80) textRight = Width - 120;
        int fullRight = Width - 16;   // command/process lines can run wider (no top buttons there)

        // Line 1: index + name (📌 prefix when excluded from Stop All).
        string name = string.IsNullOrWhiteSpace(_shortcut.Name) ? "(untitled)" : _shortcut.Name;
        string title = _keepRunning ? $"📌 {_index}. {name}" : $"{_index}. {name}";
        using (var titleFont = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold))
        using (var tbrush = new SolidBrush(_theme.TextPrimary))
        using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(title, titleFont, tbrush, new RectangleF(textLeft, 8, textRight - textLeft, 22), sf);

        // Chips/args stop before the bottom-right button cluster (refresh always
        // present; Run as ▾ and Clean bin/obj to its left when shown).
        int clusterLeft = _refreshBtn.Left;
        if (_variantsBtn.Visible) clusterLeft = Math.Min(clusterLeft, _variantsBtn.Left);
        if (_cleanBtn.Visible) clusterLeft = Math.Min(clusterLeft, _cleanBtn.Left);
        int rightLimit = clusterLeft - 10;

        // Line 2: status chip + URL badge.
        using (var statusFont = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold))
            DrawChip(g, textLeft, 31, _stateLabel, StateColor(_state), Color.White, statusFont, textRight);

        if (!string.IsNullOrWhiteSpace(_shortcut.StatusUrl))
            DrawUrlBadge(g, textLeft, textRight);

        // Line 3: the command — main executable as a coloured chip, args plain.
        if (!string.IsNullOrEmpty(_command))
        {
            var (exe, rest) = SplitCommand(_command);
            using var exeFont = new Font("Segoe UI Semibold", 8f, FontStyle.Bold);
            float cx = DrawChip(g, textLeft, 52, exe, CommandChipColor(exe), Color.White, exeFont, rightLimit);
            if (!string.IsNullOrEmpty(rest) && cx < rightLimit - 12)
                using (var cmdFont = new Font("Cascadia Mono", 8f))
                using (var cmdBrush = new SolidBrush(_theme.TextSecondary))
                using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center })
                    g.DrawString(rest, cmdFont, cmdBrush, new RectangleF(cx, 52, rightLimit - cx, 17), sf);
        }

        // Line 4: live process info as chips (#pid · mem · up · cpu [· external]).
        if (_hasProc)
        {
            using var chipFont = new Font("Segoe UI", 8f);
            Color metaText = _procExternal ? Amber : _theme.TextPrimary;
            Color metaBg = _procExternal ? AmberDim : ChipSurface();
            float px = textLeft;

            if (!string.IsNullOrEmpty(_procName))
            {
                using var nameFont = new Font("Segoe UI", 8f);
                using var nameBrush = new SolidBrush(_theme.TextSecondary);
                using var sf = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
                float nw = g.MeasureString(_procName, nameFont).Width;
                g.DrawString(_procName, nameFont, nameBrush, new RectangleF(px, 73, nw, 16), sf);
                px += nw + 6;
            }

            px = DrawChip(g, px, 73, "#" + _procPid, metaBg, metaText, chipFont, rightLimit);
            if (!string.IsNullOrEmpty(_procMem)) px = DrawChip(g, px, 73, _procMem, metaBg, metaText, chipFont, rightLimit);
            if (!string.IsNullOrEmpty(_procUptime)) px = DrawChip(g, px, 73, "up " + _procUptime, metaBg, metaText, chipFont, rightLimit);
            if (!string.IsNullOrEmpty(_procCpu)) px = DrawChip(g, px, 73, _procCpu + " cpu", metaBg, metaText, chipFont, rightLimit);
            if (_procExternal) DrawChip(g, px, 73, "external", AmberDim, Amber, chipFont, rightLimit);
        }
    }

    /// <summary>Draw a rounded "chip": a filled pill with centred text. Returns the
    /// x to start the next chip (chip right + a small gap), or the unchanged x if
    /// there wasn't room (so callers can stop laying out further chips).</summary>
    private float DrawChip(Graphics g, float x, float y, string text, Color bg, Color fg, Font font, float maxRight)
    {
        if (string.IsNullOrEmpty(text)) return x;
        float w = g.MeasureString(text, font).Width + 12;   // 6px padding each side
        const int h = 16;
        if (x + w > maxRight)
        {
            if (maxRight - x < 22) return x;                // no usable room — skip
            w = maxRight - x;                               // clip the last chip
        }
        using (var path = RoundedRect(new Rectangle((int)x, (int)y, (int)w, h), 4))
        using (var b = new SolidBrush(bg))
            g.FillPath(b, path);
        using (var tb = new SolidBrush(fg))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(text, font, tb, new RectangleF(x, y, w, h), sf);
        return x + w + 5;
    }

    /// <summary>A faint, theme-adaptive chip fill (a low-alpha tint of the text
    /// colour over the row) for the neutral metric chips.</summary>
    private Color ChipSurface() => Color.FromArgb(30, _theme.TextPrimary);

    /// <summary>Split a command into (executable label, remaining args). Handles a
    /// quoted first token and strips a path / ".exe" so the chip shows just the tool
    /// name (dotnet, npm, node…).</summary>
    private static (string Exe, string Args) SplitCommand(string command)
    {
        string cmd = command.Trim();
        string exe; int restStart;
        if (cmd.StartsWith("\""))
        {
            int q = cmd.IndexOf('"', 1);
            if (q > 0) { exe = cmd.Substring(1, q - 1); restStart = q + 1; }
            else { exe = cmd; restStart = cmd.Length; }
        }
        else
        {
            int sp = cmd.IndexOf(' ');
            if (sp < 0) { exe = cmd; restStart = cmd.Length; }
            else { exe = cmd.Substring(0, sp); restStart = sp; }
        }
        string rest = restStart < cmd.Length ? cmd.Substring(restStart).Trim() : "";
        string label = exe;
        if (label.IndexOfAny(new[] { '\\', '/' }) >= 0) label = Path.GetFileName(label);
        if (label.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) label = label[..^4];
        if (label.Length == 0) label = exe;
        return (label, rest);
    }

    /// <summary>Brand-ish colour for a tool chip so dotnet/npm/node are recognisable
    /// at a glance; a neutral slate for anything else.</summary>
    private Color CommandChipColor(string exe) => exe.ToLowerInvariant() switch
    {
        "dotnet" => Color.FromArgb(0x68, 0x2A, 0xCB),       // .NET purple
        "npm" or "npx" => Color.FromArgb(0xCB, 0x38, 0x37), // npm red
        "node" => Color.FromArgb(0x53, 0x9E, 0x43),         // node green
        "yarn" => Color.FromArgb(0x2C, 0x8E, 0xBB),
        "pnpm" => Color.FromArgb(0xF6, 0x9B, 0x0D),
        "python" or "py" => Color.FromArgb(0x37, 0x6A, 0xB0),
        "go" => Color.FromArgb(0x00, 0xAD, 0xD8),
        "cargo" or "rustc" => Color.FromArgb(0xC0, 0x6B, 0x2B),
        "docker" => Color.FromArgb(0x1D, 0x63, 0xED),
        "claude" => _theme.Primary,
        _ => Color.FromArgb(0x4B, 0x52, 0x5E),              // neutral slate
    };

    private void DrawUrlBadge(Graphics g, int textLeft, int textRight)
    {
        string label = UrlBadgeLabel(_urlState);
        Color color = UrlColor(_urlState);
        using var badgeFont = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold);
        var sz = g.MeasureString(label, badgeFont);
        int badgeW = (int)sz.Width + 12, badgeH = 15;
        int badgeX = textRight - badgeW;
        int badgeY = 33;
        if (badgeX < textLeft + 120) return;   // not enough room
        using var badgePath = RoundedRect(new Rectangle(badgeX, badgeY, badgeW, badgeH), 3);
        using (var fill = new SolidBrush(color)) g.FillPath(fill, badgePath);
        using (var tb = new SolidBrush(Color.White))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(label, badgeFont, tb, new RectangleF(badgeX, badgeY, badgeW, badgeH), sf);
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
