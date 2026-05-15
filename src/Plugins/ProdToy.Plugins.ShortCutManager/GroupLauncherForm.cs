using System.Drawing;
using System.Drawing.Drawing2D;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Modeless companion window opened from <see cref="ShortcutsForm"/>'s
/// "Group Launcher" button. Lists every shortcut in a folder and lets the
/// user start/stop them as a group. Each launch stamps a unique title onto
/// the spawned window so we can find and close it later by scanning
/// top-level window titles.
///
/// Title scheme: <c>ProdToyShortCuts_{groupId}_{batchId}_{index}</c>
///   - groupId: stable 4-digit FNV-1a hash of the folder path
///   - batchId: 4-digit random number, regenerated per Launch-All run
///   - index:   1-based row position within this form
///
/// Stop-all scans for the prefix <c>ProdToyShortCuts_{groupId}_</c> so it
/// catches orphan windows from earlier sessions, not just the current batch.
/// </summary>
class GroupLauncherForm : Form
{
    private readonly PluginTheme _theme;
    private readonly string _folderPath;
    private readonly List<Shortcut> _shortcuts;
    private readonly int _groupId;
    private readonly FlowLayoutPanel _list;
    private readonly Label _statusLabel;
    private readonly RoundedButton _launchAllBtn;
    private readonly RoundedButton _stopAllBtn;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly List<GroupRow> _rows = new();

    private int _batchId;

    private string GroupPrefix => $"ProdToyShortCuts_{_groupId}_";
    private string BatchSuffix => $"ProdToyShortCuts_{_groupId}_{_batchId}";

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

    public GroupLauncherForm(PluginTheme theme, string folderPath, List<Shortcut> shortcuts)
    {
        _theme = theme;
        _folderPath = folderPath;
        _shortcuts = shortcuts;
        _groupId = ComputeGroupId(folderPath);

        Text = $"Group Launcher — {folderPath}";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(720, 600);
        MinimumSize = new Size(520, 360);
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 10f);
        AutoScaleMode = AutoScaleMode.Dpi;

        int pad = 18;

        var header = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(ClientSize.Width, 88),
            BackColor = theme.BgDark,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(header);

        var title = new Label
        {
            Text = $"📁 {folderPath}",
            Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, 12),
            BackColor = Color.Transparent,
        };
        header.Controls.Add(title);

        var subtitle = new Label
        {
            Text = $"Group id {_groupId:D4} · {shortcuts.Count} shortcut(s)",
            Font = new Font("Segoe UI", 9f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(pad, 40),
            BackColor = Color.Transparent,
        };
        header.Controls.Add(subtitle);

        _launchAllBtn = MakeButton("▶ Launch All", theme.Primary, Color.White);
        _launchAllBtn.Size = new Size(120, 32);
        _launchAllBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _launchAllBtn.Location = new Point(header.ClientSize.Width - pad - 260, 28);
        _launchAllBtn.Click += (_, _) => LaunchAll();
        header.Controls.Add(_launchAllBtn);

        _stopAllBtn = MakeButton("■ Stop All", theme.ErrorBg, theme.ErrorColor);
        _stopAllBtn.Size = new Size(120, 32);
        _stopAllBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _stopAllBtn.Location = new Point(header.ClientSize.Width - pad - 130, 28);
        _stopAllBtn.Click += (_, _) => StopAll();
        header.Controls.Add(_stopAllBtn);

        _statusLabel = new Label
        {
            Text = "Ready.",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextSecondary,
            AutoSize = false,
            Size = new Size(ClientSize.Width - pad * 2, 18),
            Location = new Point(pad, 64),
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        header.Controls.Add(_statusLabel);

        _list = new FlowLayoutPanel
        {
            Location = new Point(pad, 92),
            Size = new Size(ClientSize.Width - pad * 2, ClientSize.Height - 92 - pad),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = theme.BgDark,
            Padding = new Padding(0, 4, 0, 4),
        };
        Controls.Add(_list);

        for (int i = 0; i < shortcuts.Count; i++)
        {
            int index1Based = i + 1;
            var s = shortcuts[i];
            var row = new GroupRow(s, theme);
            row.LaunchRequested += () => LaunchOne(index1Based);
            row.StopRequested += () => StopOne(index1Based);
            row.Index = index1Based;
            _rows.Add(row);
            _list.Controls.Add(row);
        }
        _list.ClientSizeChanged += (_, _) => ResizeRows();
        ResizeRows();

        _pollTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _pollTimer.Tick += (_, _) => RefreshStatus();
        _pollTimer.Start();

        // Capture window state at open: any orphan windows from previous runs
        // become "Running" on the very first tick so the user can stop them.
        RefreshStatus();
    }

    /// <summary>Composite child painting to eliminate flicker on resize.</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x02000000; // WS_EX_COMPOSITED
            return cp;
        }
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

    private async void LaunchAll()
    {
        int closedCount = CloseGroupWindows();
        _batchId = Random.Shared.Next(1000, 10000);
        _statusLabel.Text = $"Launching batch {_batchId:D4}…";

        foreach (var row in _rows)
            row.SetState(GroupRow.RowState.Launching, "Launching…");

        // If we just retired windows of the same group, let WT drop those
        // names before we start reusing them — otherwise the new launch
        // sometimes still sees the old name.
        if (closedCount > 0) await Task.Delay(CloseSettleMs);

        for (int i = 0; i < _shortcuts.Count; i++)
        {
            var s = _shortcuts[i];
            string expectedTitle = BuildOverrideTitle(s);
            var result = ShortcutLauncher.Launch(s, expectedTitle, forceNewWindow: false);
            if (!result.Ok)
            {
                _rows[i].SetState(GroupRow.RowState.Failed, result.ErrorMessage ?? "Launch failed");
                continue;
            }

            // Wait until WT has actually painted a top-level window with our
            // title, then a short grace so its named-window registration
            // settles. Without this, the next `wt -w <name>` call races and
            // creates a second window instead of joining the first.
            await WaitForWindowAsync(expectedTitle, PostLaunchTimeoutMs);
            await Task.Delay(PostLaunchGraceMs);
        }
    }

    /// <summary>Poll for a visible top-level window whose title contains
    /// <paramref name="needle"/>. Returns when found or when the timeout
    /// elapses — never throws.</summary>
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

    private void StopAll()
    {
        int n = CloseGroupWindows();
        _statusLabel.Text = n == 0
            ? "No windows found for this group."
            : $"Closed {n} window(s).";
        // Mark every row that *was* running as stopped — the next status tick
        // will reconcile against the live window list.
        foreach (var row in _rows)
        {
            if (row.State == GroupRow.RowState.Running || row.State == GroupRow.RowState.Launching)
                row.SetState(GroupRow.RowState.Stopped, "Stopped");
        }
    }

    private void LaunchOne(int index1Based)
    {
        if (_batchId == 0) _batchId = Random.Shared.Next(1000, 10000);

        var row = _rows[index1Based - 1];
        var s = _shortcuts[index1Based - 1];
        string overrideTitle = BuildOverrideTitle(s);

        // Kill an existing instance of this row (if any) before relaunching so
        // the same title doesn't end up doubled.
        foreach (var w in WindowFinder.FindByTitleContains(overrideTitle))
            WindowFinder.CloseWindow(w.Handle);

        row.SetState(GroupRow.RowState.Launching, "Launching…");
        var result = ShortcutLauncher.Launch(s, overrideTitle, forceNewWindow: false);
        if (!result.Ok)
            row.SetState(GroupRow.RowState.Failed, result.ErrorMessage ?? "Launch failed");
    }

    private void StopOne(int index1Based)
    {
        if (_batchId == 0) return;
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
        if (_batchId == 0)
        {
            // No batch yet — but any orphan group windows should still show as
            // running so the user can spot them and Stop All.
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
            string title = BuildOverrideTitle(_shortcuts[i]);
            bool alive = WindowFinder.AnyWindowTitleContains(title);

            if (alive)
            {
                row.SetState(GroupRow.RowState.Running, "Running");
                running++;
            }
            else if (row.State == GroupRow.RowState.Launching)
            {
                // Give the OS a few seconds to bring the window up before
                // declaring failure. SecondsSinceStateChange is a coarse
                // approximation but good enough for this UI.
                if (row.SecondsSinceStateChange > 8)
                {
                    row.SetState(GroupRow.RowState.Failed, "Window never appeared");
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

        var parts = new List<string> { $"batch {_batchId:D4}" };
        if (running > 0) parts.Add($"{running} running");
        if (stopped > 0) parts.Add($"{stopped} stopped");
        if (failed > 0) parts.Add($"{failed} failed");
        _statusLabel.Text = string.Join(" · ", parts);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        int live = WindowFinder.FindByTitleContains(GroupPrefix).Count;
        if (live > 0)
        {
            var res = MessageBox.Show(this,
                $"{live} launched window(s) from this group are still open.\n\n" +
                "Yes — stop them all and close.\n" +
                "No — leave them running and close.\n" +
                "Cancel — keep the launcher open.",
                "Group Launcher",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button3);
            if (res == DialogResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            if (res == DialogResult.Yes) CloseGroupWindows();
        }
        _pollTimer.Stop();
        _pollTimer.Dispose();
        base.OnFormClosing(e);
    }

    private void ResizeRows()
    {
        int w = _list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 6;
        if (w < 200) w = 200;
        foreach (Control c in _list.Controls)
            if (c is GroupRow gr) gr.Width = w;
    }

    /// <summary>4-digit stable hash of a folder path. FNV-1a → mod 10000 so
    /// the same folder always yields the same id across sessions.</summary>
    private static int ComputeGroupId(string folderPath)
    {
        // FNV-1a 32-bit
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
/// One shortcut row inside <see cref="GroupLauncherForm"/>. Shows the name,
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
