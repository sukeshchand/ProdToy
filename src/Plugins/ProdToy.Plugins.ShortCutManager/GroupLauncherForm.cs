using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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
    private readonly List<GroupRow> _rows = new();

    private string GroupPrefix => $"ProdToyShortCuts_{_groupId}_";
    private string BatchSuffix => $"ProdToyShortCuts_{_groupId}_{_session.BatchId}";

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

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 86,
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

        _launchAllBtn = MakeButton("▶ Launch All", theme.Primary, Color.White);
        _launchAllBtn.Size = new Size(120, 30);
        _launchAllBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _launchAllBtn.Click += (_, _) => LaunchAll();
        header.Controls.Add(_launchAllBtn);

        _stopAllBtn = MakeButton("■ Stop All", theme.ErrorBg, theme.ErrorColor);
        _stopAllBtn.Size = new Size(120, 30);
        _stopAllBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _stopAllBtn.Click += (_, _) => StopAll();
        header.Controls.Add(_stopAllBtn);

        _statusLabel = new Label
        {
            Text = "Ready.",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextSecondary,
            AutoSize = false,
            Height = 18,
            Location = new Point(pad, 62),
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        header.Controls.Add(_statusLabel);

        void LayoutHeader()
        {
            int w = header.ClientSize.Width;
            _launchAllBtn.Location = new Point(w - pad - 250, 10);
            _stopAllBtn.Location = new Point(w - pad - 120, 10);
            _statusLabel.Size = new Size(w - pad * 2, 18);
        }
        header.Resize += (_, _) => LayoutHeader();
        LayoutHeader();

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
        // Order matters: Fill control added first, then docked-Top is layered on top.
        _list.BringToFront();
        header.BringToFront();

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
    public void StopAllExternal() => StopAll();

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

    private async void LaunchOne(int index1Based)
    {
        if (_session.BatchId == 0) _session.BatchId = Random.Shared.Next(1000, 10000);

        var row = _rows[index1Based - 1];
        var s = _shortcuts[index1Based - 1];
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

    private void StopAll()
    {
        int n = CloseGroupWindows();
        _statusLabel.Text = n == 0
            ? "No windows found for this group."
            : $"Closed {n} window(s).";
        foreach (var row in _rows)
        {
            if (row.State == GroupRow.RowState.Running || row.State == GroupRow.RowState.Launching)
                row.SetState(GroupRow.RowState.Stopped, "Stopped");
        }
    }

    private void StopOne(int index1Based)
    {
        if (_session.BatchId == 0) return;
        var s = _shortcuts[index1Based - 1];
        string title = BuildOverrideTitle(s);
        int closed = 0;
        foreach (var w in WindowFinder.FindByTitleContains(title))
        {
            WindowFinder.CloseWindow(w.Handle);
            closed++;
        }
        var row = _rows[index1Based - 1];
        row.SetState(GroupRow.RowState.Stopped, closed > 0 ? "Stopped" : "No window found");
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
/// One shortcut row inside <see cref="GroupLauncherPanel"/>. Shows the name,
/// a colored status dot, the current state text, and per-row Launch / Stop
/// buttons. State transitions are recorded with a timestamp so the parent
/// can fail-out launches whose windows never appear.
/// </summary>
class GroupRow : Panel
{
    public enum RowState { Pending, Launching, Running, Stopped, Failed }

    private const int RowHeight = 56;

    private readonly Shortcut _shortcut;
    private readonly PluginTheme _theme;
    private readonly RoundedButton _launchBtn;
    private readonly RoundedButton _stopBtn;
    private RowState _state = RowState.Pending;
    private string _stateLabel = "Pending";
    private DateTime _stateChangedAt = DateTime.UtcNow;

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
        Margin = new Padding(0, 3, 0, 3);
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
    }

    public void SetState(RowState state, string label)
    {
        if (_state == state && _stateLabel == label) return;
        if (_state != state) _stateChangedAt = DateTime.UtcNow;
        _state = state;
        _stateLabel = label;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_launchBtn == null || _stopBtn == null) return;
        _stopBtn.Location = new Point(Width - _stopBtn.Width - 10, 14);
        _launchBtn.Location = new Point(_stopBtn.Left - _launchBtn.Width - 6, 14);
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

        int dotX = 14;
        int dotY = Height / 2 - 6;
        Color dotColor = StateColor(_state);
        using (var dotBrush = new SolidBrush(dotColor))
            g.FillEllipse(dotBrush, dotX, dotY, 12, 12);

        int textLeft = dotX + 22;
        int textRight = _launchBtn.Left - 10;
        if (textRight < textLeft + 80) textRight = Width - 100;

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
    }

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
