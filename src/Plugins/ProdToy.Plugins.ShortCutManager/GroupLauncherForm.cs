using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net.Http;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Embedded "Group Launcher" panel — lives as the second tab of the right
/// pane in <see cref="ShortcutsForm"/>. Lists every shortcut in the
/// selected folder and lets the user start/stop them as a group while
/// status is tracked per-row by cmd.exe PID liveness (so a backgrounded
/// tab still shows Running).
///
/// Title scheme applied to launched windows:
///   <c>{shortcut.WindowTitle} ProdToyShortCuts_{groupId}_{batchId}</c>
///     - groupId: stable 4-digit FNV-1a hash of the folder path
///     - batchId: 4-digit random per Launch All run
///
/// Per-folder state (batch id + per-shortcut PID) lives in
/// <see cref="GroupLauncherSessions"/> so it survives folder navigation.
/// </summary>
class GroupLauncherPanel : UserControl
{
    private readonly PluginTheme _theme;
    private readonly string _folderPath;
    private readonly List<Shortcut> _shortcuts;
    private readonly int _groupId;
    private readonly GroupSession _session;
    private readonly FlowLayoutPanel _list;
    private readonly Label _statusLabel;
    private readonly RoundedButton _launchAllBtn;
    private readonly RoundedButton _stopAllBtn;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly System.Windows.Forms.Timer _urlPollTimer;
    private readonly List<GroupRow> _rows = new();

    /// <summary>One HttpClient per panel, reused across all rows' URL probes.
    /// Timeout is left infinite — each probe enforces the shortcut's own
    /// <see cref="Shortcut.StatusTimeoutSeconds"/> via a CancellationToken so
    /// the cap is per-target. The handler also trusts self-signed certs for
    /// loopback hosts so a local dev server's cert never shows a false DOWN.</summary>
    private readonly HttpClient _httpClient = CreateProbeClient();
    private bool _urlPollInFlight;

    private static HttpClient CreateProbeClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
            {
                if (errors == System.Net.Security.SslPolicyErrors.None) return true;
                // Only bypass validation for localhost / loopback — never for
                // real remote URLs, so we don't silently weaken security.
                return IsLoopbackHost(request.RequestUri?.Host ?? "");
            },
        };
        return new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host == "127.0.0.1"
        || host == "::1"
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    // Title prefix stamped onto every launched window so the title scan
    // (StopAll fallback) can find and close them. Kept short — long prefixes
    // crowd the WT tab title and the user's own WindowTitle gets pushed off.
    private string GroupPrefix => $"PTS_{_groupId}_";
    private string BatchSuffix => $"PTS_{_groupId}_{_session.BatchId}";

    /// <summary>
    /// Title we ask the launcher to apply to the shortcut's window. We preserve
    /// whatever the shortcut author configured as <see cref="Shortcut.WindowTitle"/>
    /// and append the batch suffix so a title-scan can find it later. The user
    /// keeps full control over tab/window grouping via the shortcut's own
    /// "Open in" + tab-group settings — the launcher does not override them.
    /// </summary>
    private string BuildOverrideTitle(Shortcut s) =>
        string.IsNullOrWhiteSpace(s.WindowTitle)
            ? BatchSuffix
            : $"{s.WindowTitle.Trim()} {BatchSuffix}";

    public GroupLauncherPanel(PluginTheme theme, string folderPath, List<Shortcut> shortcuts)
    {
        _theme = theme;
        _folderPath = folderPath;
        _shortcuts = shortcuts;
        _groupId = ComputeGroupId(folderPath);
        _session = GroupLauncherSessions.GetOrCreate(folderPath);

        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 10f);

        int pad = 12;

        // WinForms docks in reverse z-order: the Fill child must be added FIRST
        // (it gets the lower z) so that the later-added docked-Top header
        // reserves space above it instead of being painted under it.
        _list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = theme.BgDark,
            Padding = new Padding(pad, 4, pad, 4),
        };
        Controls.Add(_list);

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 92,
            BackColor = theme.BgDark,
        };
        Controls.Add(header);

        var title = new Label
        {
            Text = $"📁 {folderPath}",
            Font = new Font("Segoe UI Semibold", 12f, FontStyle.Bold),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, 10),
            BackColor = Color.Transparent,
        };
        header.Controls.Add(title);

        var subtitle = new Label
        {
            Text = $"Group id {_groupId:D4} · {shortcuts.Count} shortcut(s)",
            Font = new Font("Segoe UI", 9f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(pad, 36),
            BackColor = Color.Transparent,
        };
        header.Controls.Add(subtitle);

        // Right-anchored buttons. We set the initial Location based on the
        // header's current width and rely on Anchor=Top|Right to track parent
        // resizes; no manual Resize handler needed.
        const int btnW = 110, btnH = 30, btnGap = 8;
        _stopAllBtn = MakeButton("■ Stop All", theme.ErrorBg, theme.ErrorColor);
        _stopAllBtn.Size = new Size(btnW, btnH);
        _stopAllBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _stopAllBtn.Location = new Point(header.ClientSize.Width - pad - btnW, 10);
        _stopAllBtn.Click += (_, _) => _ = StopAllAsync();
        header.Controls.Add(_stopAllBtn);

        _launchAllBtn = MakeButton("▶ Launch All", theme.Primary, Color.White);
        _launchAllBtn.Size = new Size(btnW, btnH);
        _launchAllBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _launchAllBtn.Location = new Point(_stopAllBtn.Left - btnGap - btnW, 10);
        _launchAllBtn.Click += (_, _) => LaunchAll();
        header.Controls.Add(_launchAllBtn);

        _statusLabel = new Label
        {
            Text = "Ready.",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextSecondary,
            AutoSize = false,
            Height = 20,
            Location = new Point(pad, 64),
            Size = new Size(header.ClientSize.Width - pad * 2, 20),
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        header.Controls.Add(_statusLabel);

        for (int i = 0; i < shortcuts.Count; i++)
        {
            int index1Based = i + 1;
            var s = shortcuts[i];
            var row = new GroupRow(s, theme);
            row.LaunchRequested += () => LaunchOne(index1Based);
            row.StopRequested += () => StopOne(index1Based);
            row.Index = index1Based;

            // Restore PID from the per-folder session so a backgrounded batch
            // keeps reporting Running when the user comes back to this tab.
            if (_session.PidByShortcutId.TryGetValue(s.Id, out int savedPid))
                row.LaunchedCmdPid = savedPid;

            _rows.Add(row);
            _list.Controls.Add(row);
        }
        _list.ClientSizeChanged += (_, _) => ResizeRows();
        ResizeRows();

        _pollTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _pollTimer.Tick += (_, _) => RefreshStatus();
        _pollTimer.Start();
        RefreshStatus();

        // URL probing runs on a separate, slower timer (3s) so HTTP calls
        // don't pile up while the 1s PID poll keeps the row indicator fresh.
        _urlPollTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _urlPollTimer.Tick += async (_, _) => await ProbeUrlsAsync();
        _urlPollTimer.Start();
        _ = ProbeUrlsAsync();
    }

    /// <summary>Hits every row's StatusUrl (when set) and updates that row's
    /// <see cref="GroupRow.UrlStatus"/> with the result. Single in-flight
    /// guard ensures slow probes don't queue up across ticks.</summary>
    private async Task ProbeUrlsAsync()
    {
        if (_urlPollInFlight || IsDisposed) return;
        _urlPollInFlight = true;
        try
        {
            var tasks = new List<Task>();
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                var url = _shortcuts[i].StatusUrl?.Trim() ?? "";
                if (string.IsNullOrEmpty(url))
                {
                    row.SetUrlStatus(GroupRow.UrlState.NotConfigured, "");
                    continue;
                }
                // Only probe a service that's actually up. Hitting the URL while
                // it's Stopped/Pending/Failed/Launching is pointless and (for an
                // SSR route) needlessly loads the dev server. Clear the badge so
                // it doesn't show a stale Healthy/DOWN from a previous run.
                if (row.State != GroupRow.RowState.Running)
                {
                    row.SetUrlStatus(GroupRow.UrlState.NotConfigured, "");
                    continue;
                }
                tasks.Add(ProbeOneAsync(row, url, _shortcuts[i].StatusTimeoutSeconds));
            }
            if (tasks.Count > 0) await Task.WhenAll(tasks);
        }
        finally { _urlPollInFlight = false; }
    }

    private async Task ProbeOneAsync(GroupRow row, string url, int timeoutSeconds)
    {
        // Per-target cap via a linked token (the shared client's own Timeout is
        // infinite). Clamp to a sane range so a bad saved value can't hang.
        using var cts = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 120)));
        try
        {
            // Probe with HEAD — no body transferred, and it's the conventional
            // "is it up?" request. Some servers/routes reject HEAD (405/501);
            // fall back to GET once in that case so the badge stays accurate.
            using var resp = await SendProbeAsync(HttpMethod.Head, url, cts.Token);
            int code = (int)resp.StatusCode;
            if (code is 405 or 501)
            {
                using var getResp = await SendProbeAsync(HttpMethod.Get, url, cts.Token);
                code = (int)getResp.StatusCode;
            }

            if (code is >= 200 and < 400)
                row.SetUrlStatus(GroupRow.UrlState.Healthy, $"HTTP {code}");
            else
                row.SetUrlStatus(GroupRow.UrlState.ServerError, $"HTTP {code}");
        }
        catch (OperationCanceledException)
        {
            row.SetUrlStatus(GroupRow.UrlState.Unreachable, "Timeout");
        }
        catch (HttpRequestException ex)
        {
            row.SetUrlStatus(GroupRow.UrlState.Unreachable, ex.InnerException?.Message ?? "Unreachable");
        }
        catch (Exception ex)
        {
            row.SetUrlStatus(GroupRow.UrlState.Unreachable, ex.Message);
        }
    }

    /// <summary>Sends a single probe request (headers only) with the given
    /// method. Caller owns the returned response (dispose it).</summary>
    private Task<HttpResponseMessage> SendProbeAsync(HttpMethod method, string url, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, url);
        return _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    // wt -w <name> joins a named window, but only if that window has been
    // registered with WT. Back-to-back launches before the first window has
    // registered race and end up with two top-level windows instead of one
    // grouped window. We poll for the spawned title to appear before firing
    // the next launch (with a hard timeout so we never hang).
    private const int PostLaunchPollMs = 100;
    private const int PostLaunchTimeoutMs = 4000;
    private const int PostLaunchGraceMs = 250;
    private const int CloseSettleMs = 600;

    /// <summary>Stop all launched windows in this folder's group. Used by
    /// <see cref="ShortcutsForm"/>'s close prompt to gracefully end the
    /// session from the parent form.</summary>
    public void StopAllExternal() => _ = StopAllAsync();

    private async void LaunchAll()
    {
        int closedCount = CloseGroupWindows();
        _session.BatchId = Random.Shared.Next(1000, 10000);
        _statusLabel.Text = $"Launching batch {_session.BatchId:D4}…";

        foreach (var row in _rows)
        {
            row.LaunchedCmdPid = 0;
            row.SetState(GroupRow.RowState.Launching, "Launching…");
        }
        _session.PidByShortcutId.Clear();

        if (closedCount > 0) await Task.Delay(CloseSettleMs);

        for (int i = 0; i < _shortcuts.Count; i++)
        {
            if (IsDisposed) return;
            var s = _shortcuts[i];
            if (ShortcutLauncher.IsUrl(s)) { OpenUrlRow(_rows[i], s); continue; }
            string expectedTitle = BuildOverrideTitle(s);
            var beforePids = GetCmdPidSnapshot();

            var result = ShortcutLauncher.Launch(s, expectedTitle, forceNewWindow: false);
            if (!result.Ok)
            {
                _rows[i].SetState(GroupRow.RowState.Failed, result.ErrorMessage ?? "Launch failed");
                continue;
            }

            await WaitForWindowAsync(expectedTitle, PostLaunchTimeoutMs);
            await Task.Delay(PostLaunchGraceMs);

            int newPid = PickNewCmdPid(beforePids);
            if (newPid > 0)
            {
                _rows[i].LaunchedCmdPid = newPid;
                _session.PidByShortcutId[s.Id] = newPid;
            }
        }
    }

    /// <summary>"Launch" a URL shortcut from the Group Launcher: open it in the
    /// in-app preview (no terminal/window/pid). Used by Launch All and per-row launch.</summary>
    private void OpenUrlRow(GroupRow row, Shortcut s)
    {
        var url = (s.Args ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            row.SetState(GroupRow.RowState.Failed, "No URL set");
            return;
        }
        row.LaunchedCmdPid = 0;
        try
        {
            UrlPreviewForm.OpenOrFocus(_theme, s.Id,
                string.IsNullOrWhiteSpace(s.Name) ? "Preview" : s.Name, url);
        }
        catch (Exception ex)
        {
            row.SetState(GroupRow.RowState.Failed, ex.Message);
            return;
        }
        row.SetState(GroupRow.RowState.Running, "Opened in preview ↗");
        ShortcutStore.RecordLaunch(s.Id);
        AutoLoginRunner.RunInBackground(s);   // no-op unless enabled + HomeUrl set
    }

    private async void LaunchOne(int index1Based)
    {
        if (_session.BatchId == 0) _session.BatchId = Random.Shared.Next(1000, 10000);

        var row = _rows[index1Based - 1];
        var s = _shortcuts[index1Based - 1];
        if (ShortcutLauncher.IsUrl(s)) { OpenUrlRow(row, s); return; }
        string overrideTitle = BuildOverrideTitle(s);

        foreach (var w in WindowFinder.FindByTitleContains(overrideTitle))
            WindowFinder.CloseWindow(w.Handle);

        row.LaunchedCmdPid = 0;
        _session.PidByShortcutId.Remove(s.Id);
        row.SetState(GroupRow.RowState.Launching, "Launching…");

        var beforePids = GetCmdPidSnapshot();

        var result = ShortcutLauncher.Launch(s, overrideTitle, forceNewWindow: false);
        if (!result.Ok)
        {
            row.SetState(GroupRow.RowState.Failed, result.ErrorMessage ?? "Launch failed");
            return;
        }

        await WaitForWindowAsync(overrideTitle, PostLaunchTimeoutMs);
        await Task.Delay(PostLaunchGraceMs);
        if (IsDisposed) return;
        int newPid = PickNewCmdPid(beforePids);
        if (newPid > 0)
        {
            row.LaunchedCmdPid = newPid;
            _session.PidByShortcutId[s.Id] = newPid;
        }
    }

    // Stop All: how long to wait between the PID-kill phase and the
    // straggler-check phase. Long enough for WT to react to a killed cmd
    // and tear down its tab, short enough that the user isn't left staring.
    private const int StopVerifyDelayMs = 4000;

    /// <summary>Two-phase Stop All:
    ///   1. Kill each row's tracked PID tree. WT tears down the tab when its
    ///      pty dies — no "close all tabs?" prompt, no orphaned processes.
    ///   2. Wait briefly for WT to settle, then for any tab still carrying our
    ///      group prefix, foreground its WT window and send Ctrl+Shift+W per
    ///      matching tab (via <see cref="TerminalTabCloser"/>). Per-tab close
    ///      never prompts and never touches unrelated tabs in the same WT
    ///      window. The keystroke loop sleeps between sends, so we run it on
    ///      a background thread to keep the panel responsive.
    /// We deliberately do NOT kill the WT process tree — when shortcuts open
    /// in an existing WT window via <c>wt -w 0</c>, that WT also hosts
    /// unrelated tabs (e.g. a Claude CLI session in a different group).</summary>
    private async Task StopAllAsync()
    {
        int killed = 0;
        foreach (var row in _rows)
        {
            var s = _shortcuts[row.Index - 1];
            if (KillPidTree(row.LaunchedCmdPid))
            {
                killed++;
                _session.PidByShortcutId.Remove(s.Id);
                row.LaunchedCmdPid = 0;
            }
        }

        _statusLabel.Text = killed > 0
            ? $"Stopped {killed} tracked process(es). Verifying…"
            : "No tracked PIDs. Verifying…";

        await Task.Delay(StopVerifyDelayMs);
        if (IsDisposed) return;

        var distinctWtWindows = WindowFinder.FindByTitleContains(GroupPrefix)
            .Select(w => w.Handle)
            .Distinct()
            .ToList();
        string prefix = GroupPrefix;
        int closedFallback = await Task.Run(() =>
        {
            int total = 0;
            foreach (var hwnd in distinctWtWindows)
                total += TerminalTabCloser.CloseTabsContaining(hwnd, prefix);
            return total;
        });
        if (IsDisposed) return;

        foreach (var row in _rows)
        {
            if (row.State == GroupRow.RowState.Running ||
                row.State == GroupRow.RowState.Launching)
                row.SetState(GroupRow.RowState.Stopped, "Stopped");
        }

        _statusLabel.Text = (killed + closedFallback) == 0
            ? "Nothing to stop — no tracked PIDs and no group windows found."
            : $"Stopped {killed} tracked"
              + (closedFallback > 0 ? $", closed {closedFallback} ghost tab(s)" : "") + ".";
    }

    private void StopOne(int index1Based)
    {
        var row = _rows[index1Based - 1];
        var s = _shortcuts[index1Based - 1];

        if (KillPidTree(row.LaunchedCmdPid))
        {
            _session.PidByShortcutId.Remove(s.Id);
            row.LaunchedCmdPid = 0;
            row.SetState(GroupRow.RowState.Stopped, "Stopped");
            return;
        }

        // No PID — fall back to title-scan window close. Note this routes
        // through WT's "close all tabs?" prompt when the row's tab shares a
        // WT window with siblings, and any child processes spawned by the
        // cmd (e.g. dotnet) may orphan because WT terminates cmd directly
        // rather than walking its process tree.
        int closed = 0;
        if (_session.BatchId != 0)
        {
            string title = BuildOverrideTitle(s);
            foreach (var w in WindowFinder.FindByTitleContains(title))
            {
                WindowFinder.CloseWindow(w.Handle);
                closed++;
            }
        }

        row.SetState(GroupRow.RowState.Stopped,
            closed > 0 ? "Stopped (window closed)" : "Nothing to stop");
    }

    /// <summary>Terminate the process at <paramref name="pid"/> together with
    /// every descendant. Returns true if a live process was actually killed.
    /// Killing the tree is important here — cmd.exe spawns dotnet/npm/etc
    /// and we want the user's command to go too, not just the shell.</summary>
    private static bool KillPidTree(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            p.Kill(entireProcessTree: true);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Close every visible window whose title carries this folder's
    /// group prefix — covers orphans from older sessions/batches.</summary>
    private int CloseGroupWindows()
    {
        var hits = WindowFinder.FindByTitleContains(GroupPrefix);
        foreach (var w in hits) WindowFinder.CloseWindow(w.Handle);
        return hits.Count;
    }

    private void RefreshStatus()
    {
        if (_session.BatchId == 0)
        {
            int orphans = WindowFinder.FindByTitleContains(GroupPrefix).Count;
            _statusLabel.Text = orphans > 0
                ? $"{orphans} orphan window(s) from a previous run — use Stop All to clean up."
                : "Ready.";
            return;
        }

        int running = 0, failed = 0, stopped = 0;
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            bool alive = row.LaunchedCmdPid > 0 && IsPidAlive(row.LaunchedCmdPid);

            if (alive)
            {
                row.SetState(GroupRow.RowState.Running, "Running");
                running++;
            }
            else if (row.State == GroupRow.RowState.Launching)
            {
                if (row.SecondsSinceStateChange > 8)
                {
                    row.SetState(GroupRow.RowState.Failed, "Process never started");
                    failed++;
                }
            }
            else if (row.State == GroupRow.RowState.Running)
            {
                row.SetState(GroupRow.RowState.Stopped, "Stopped");
                stopped++;
            }
            else if (row.State == GroupRow.RowState.Failed) failed++;
            else if (row.State == GroupRow.RowState.Stopped) stopped++;
        }

        var parts = new List<string> { $"batch {_session.BatchId:D4}" };
        if (running > 0) parts.Add($"{running} running");
        if (stopped > 0) parts.Add($"{stopped} stopped");
        if (failed > 0) parts.Add($"{failed} failed");
        _statusLabel.Text = string.Join(" · ", parts);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pollTimer.Stop();
            _pollTimer.Dispose();
            _urlPollTimer.Stop();
            _urlPollTimer.Dispose();
            _httpClient.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ResizeRows()
    {
        int w = _list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 12;
        if (w < 200) w = 200;
        foreach (Control c in _list.Controls)
            if (c is GroupRow gr) gr.Width = w;
    }

    private static async Task WaitForWindowAsync(string needle, int timeoutMs)
    {
        if (string.IsNullOrEmpty(needle)) return;
        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            if (WindowFinder.AnyWindowTitleContains(needle)) return;
            await Task.Delay(PostLaunchPollMs);
        }
    }

    private static HashSet<int> GetCmdPidSnapshot()
    {
        try
        {
            return Process.GetProcessesByName("cmd").Select(p =>
            {
                try { return p.Id; }
                catch { return 0; }
                finally { p.Dispose(); }
            }).Where(id => id > 0).ToHashSet();
        }
        catch { return new HashSet<int>(); }
    }

    private static int PickNewCmdPid(HashSet<int> before)
    {
        try
        {
            (int Pid, DateTime Start)? best = null;
            foreach (var p in Process.GetProcessesByName("cmd"))
            {
                try
                {
                    if (before.Contains(p.Id)) continue;
                    DateTime start;
                    try { start = p.StartTime; }
                    catch { continue; }
                    if (best == null || start > best.Value.Start)
                        best = (p.Id, start);
                }
                finally { p.Dispose(); }
            }
            return best?.Pid ?? 0;
        }
        catch { return 0; }
    }

    private static bool IsPidAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    /// <summary>4-digit stable hash of a folder path. FNV-1a → mod 10000 so
    /// the same folder always yields the same id across sessions.</summary>
    private static int ComputeGroupId(string folderPath)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        uint hash = offset;
        var s = folderPath ?? "";
        for (int i = 0; i < s.Length; i++)
        {
            hash ^= s[i];
            hash *= prime;
        }
        return (int)(hash % 10000);
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
}

/// <summary>
/// One shortcut row inside <see cref="GroupLauncherPanel"/>. Top strip shows
/// the name + Running/Stopped dot + URL health badge + per-row Launch/Stop.
/// A read-only log box fills the bottom for future stdout capture; for now
/// it carries status messages such as last URL probe result and PID changes.
/// </summary>
class GroupRow : Panel
{
    public enum RowState { Pending, Launching, Running, Stopped, Failed }
    public enum UrlState { NotConfigured, Healthy, ServerError, Unreachable }

    private const int RowHeight = 150;
    private const int TopStripHeight = 50;
    private const int InfoLineHeight = 22;

    private readonly Shortcut _shortcut;
    private readonly PluginTheme _theme;
    private readonly RoundedButton _launchBtn;
    private readonly RoundedButton _stopBtn;
    private readonly RichTextBox _logBox;
    private RowState _state = RowState.Pending;
    private string _stateLabel = "Pending";
    private DateTime _stateChangedAt = DateTime.UtcNow;
    private UrlState _urlState = UrlState.NotConfigured;
    private string _urlDetail = "";

    public int Index { get; set; }
    public RowState State => _state;
    public double SecondsSinceStateChange => (DateTime.UtcNow - _stateChangedAt).TotalSeconds;

    /// <summary>The cmd.exe PID identified at launch time for this row's tab,
    /// or 0 if this row hasn't been launched (or the spawn couldn't be
    /// matched). Status polling reads this to decide alive vs stopped.</summary>
    public int LaunchedCmdPid { get; set; }

    public event Action? LaunchRequested;
    public event Action? StopRequested;

    public GroupRow(Shortcut s, PluginTheme theme)
    {
        _shortcut = s;
        _theme = theme;
        Margin = new Padding(0, 4, 0, 4);
        Height = RowHeight;
        BackColor = theme.BgDark;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _launchBtn = new RoundedButton
        {
            Text = "▶",
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            Size = new Size(36, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.Primary,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        _launchBtn.FlatAppearance.BorderSize = 0;
        _launchBtn.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        _launchBtn.Click += (_, _) => LaunchRequested?.Invoke();
        Controls.Add(_launchBtn);

        _stopBtn = new RoundedButton
        {
            Text = "■",
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            Size = new Size(36, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.ErrorBg,
            ForeColor = theme.ErrorColor,
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        _stopBtn.FlatAppearance.BorderSize = 0;
        _stopBtn.FlatAppearance.MouseOverBackColor = theme.ErrorColor;
        _stopBtn.Click += (_, _) => StopRequested?.Invoke();
        Controls.Add(_stopBtn);

        // Read-only log surface. Empty for now (stdout capture is a future
        // feature requiring redirected Process.Start instead of `wt.exe`).
        // Reserved here so the user can see room for the data they're
        // planning to add.
        _logBox = new RichTextBox
        {
            Font = new Font("Cascadia Mono", 8.5f),
            BackColor = theme.BgDark,
            ForeColor = theme.TextSecondary,
            BorderStyle = BorderStyle.FixedSingle,
            ReadOnly = true,
            DetectUrls = false,
            Multiline = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            WordWrap = false,
        };
        Controls.Add(_logBox);
    }

    public void SetState(RowState state, string label)
    {
        if (_state == state && _stateLabel == label) return;
        if (_state != state)
        {
            _stateChangedAt = DateTime.UtcNow;
            AppendLog($"[{DateTime.Now:HH:mm:ss}] state → {state}" +
                (state == RowState.Running && LaunchedCmdPid > 0 ? $" (pid {LaunchedCmdPid})" : ""),
                StateColor(state));
        }
        _state = state;
        _stateLabel = label;
        Invalidate();
    }

    public void SetUrlStatus(UrlState state, string detail)
    {
        bool changed = _urlState != state;
        _urlState = state;
        _urlDetail = detail ?? "";
        if (changed && state != UrlState.NotConfigured)
            AppendLog($"[{DateTime.Now:HH:mm:ss}] url → {state} {(_urlDetail.Length > 0 ? "(" + _urlDetail + ")" : "")}",
                UrlColor(state));
        Invalidate();
    }

    private void AppendLog(string line, Color color)
    {
        if (_logBox.IsDisposed) return;
        try
        {
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.SelectionLength = 0;
            _logBox.SelectionColor = color;
            _logBox.AppendText(line + Environment.NewLine);
            _logBox.SelectionColor = _logBox.ForeColor;
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }
        catch { }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_launchBtn == null || _stopBtn == null || _logBox == null) return;

        int btnTop = (TopStripHeight - _stopBtn.Height) / 2 + 6;
        _stopBtn.Location = new Point(Width - _stopBtn.Width - 12, btnTop);
        _launchBtn.Location = new Point(_stopBtn.Left - _launchBtn.Width - 6, btnTop);

        int logTop = TopStripHeight + InfoLineHeight;
        int logLeft = 14;
        int logRight = Width - 14;
        int logBottom = Height - 12;
        _logBox.Location = new Point(logLeft, logTop);
        _logBox.Size = new Size(Math.Max(50, logRight - logLeft), Math.Max(20, logBottom - logTop));
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

        // PID/state dot
        int dotX = 14;
        int dotY = 18;
        using (var dotBrush = new SolidBrush(StateColor(_state)))
            g.FillEllipse(dotBrush, dotX, dotY, 12, 12);

        int textLeft = dotX + 22;
        int textRight = _launchBtn.Left - 10;
        if (textRight < textLeft + 80) textRight = Width - 100;

        // Row title (top strip)
        string name = string.IsNullOrWhiteSpace(_shortcut.Name) ? "(untitled)" : _shortcut.Name;
        using (var titleFont = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold))
        using (var tbrush = new SolidBrush(_theme.TextPrimary))
        using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        {
            g.DrawString($"{Index}. {name}", titleFont, tbrush,
                new RectangleF(textLeft, 8, textRight - textLeft, 22), sf);
        }

        using (var subFont = new Font("Segoe UI", 8.5f))
        using (var subBrush = new SolidBrush(_theme.TextSecondary))
        using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        {
            g.DrawString(_stateLabel, subFont, subBrush,
                new RectangleF(textLeft, 30, textRight - textLeft, 18), sf);
        }

        // Info line: PID + URL badge
        int infoY = TopStripHeight + 2;
        int infoLeft = textLeft;
        int infoRight = Width - 14;
        string pidText = LaunchedCmdPid > 0 ? $"PID {LaunchedCmdPid}" : "PID —";
        using (var labelFont = new Font("Segoe UI", 8.5f))
        using (var labelBrush = new SolidBrush(_theme.TextSecondary))
        {
            g.DrawString(pidText, labelFont, labelBrush, new PointF(infoLeft, infoY));
        }

        // URL badge
        if (!string.IsNullOrWhiteSpace(_shortcut.StatusUrl))
        {
            string label = UrlBadgeLabel(_urlState);
            Color urlColor = UrlColor(_urlState);
            using var badgeFont = new Font("Segoe UI Semibold", 8f, FontStyle.Bold);
            var badgeSize = g.MeasureString(label, badgeFont);
            int badgeW = (int)badgeSize.Width + 12;
            int badgeH = 16;
            int badgeX = infoLeft + 90;
            int badgeY = infoY;

            using var badgePath = RoundedRect(new Rectangle(badgeX, badgeY, badgeW, badgeH), 3);
            using (var fill = new SolidBrush(urlColor))
                g.FillPath(fill, badgePath);
            using (var textBrush = new SolidBrush(Color.White))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(label, badgeFont, textBrush, new RectangleF(badgeX, badgeY, badgeW, badgeH), sf);

            int urlTextX = badgeX + badgeW + 6;
            string urlLine = string.IsNullOrEmpty(_urlDetail)
                ? _shortcut.StatusUrl
                : $"{_shortcut.StatusUrl} · {_urlDetail}";
            using var urlFont = new Font("Segoe UI", 8.5f);
            using var urlBrush = new SolidBrush(_theme.TextSecondary);
            using var urlSf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(urlLine, urlFont, urlBrush,
                new RectangleF(urlTextX, infoY, Math.Max(20, infoRight - urlTextX), 16), urlSf);
        }
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
        RowState.Pending => _theme.TextSecondary,
        RowState.Launching => Color.FromArgb(0xE6, 0xA5, 0x3A),
        RowState.Running => _theme.SuccessColor,
        RowState.Stopped => _theme.TextSecondary,
        RowState.Failed => _theme.ErrorColor,
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
