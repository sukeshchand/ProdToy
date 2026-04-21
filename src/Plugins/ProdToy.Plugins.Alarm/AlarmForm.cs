using System.Drawing;
using System.Drawing.Drawing2D;
using ProdToy.Sdk;

namespace ProdToy.Plugins.Alarm;

class AlarmForm : Form
{
    private readonly PluginTheme _theme;

    private readonly TextBox _searchBox;
    private readonly TextBox _quickAddBox;
    private readonly Panel _filterBar;
    private readonly List<FilterChip> _chips = new();
    private string _activeFilter = "all";

    private readonly Label _cardActiveVal;
    private readonly Label _cardTodayVal;
    private readonly Label _cardNextVal;
    private readonly Label _cardMissedVal;

    private readonly FlowLayoutPanel _listPanel;

    private string? _expandedId;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    public AlarmForm(PluginTheme theme)
    {
        _theme = theme;

        Text = "ProdToy — Alarms";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1200, 1040);
        MinimumSize = new Size(900, 560);
        ShowInTaskbar = true;
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 10f);
        Icon = IconHelper.CreateAppIcon(theme.Primary);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;

        int pad = 18;

        // ── Toolbar ──
        var toolbar = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(ClientSize.Width, 56),
            BackColor = theme.BgDark,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(toolbar);

        var titleLabel = new Label
        {
            Text = "Alarms",
            Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, 14),
            BackColor = Color.Transparent,
        };
        toolbar.Controls.Add(titleLabel);

        _searchBox = new TextBox
        {
            Font = new Font("Segoe UI", 10f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Size = new Size(260, 26),
            Location = new Point(pad + 120, 16),
            PlaceholderText = "Search alarms …",
        };
        _searchBox.TextChanged += (_, _) => RefreshList();
        toolbar.Controls.Add(_searchBox);

        var newBtn = MakeButton("+ New Alarm", theme.Primary, Color.White);
        newBtn.Size = new Size(120, 30);
        newBtn.Location = new Point(pad + 400, 14);
        newBtn.Click += (_, _) => NewAlarm();
        toolbar.Controls.Add(newBtn);

        int qx = pad + 530;
        var quicks = new[] { ("5m", 5), ("10m", 10), ("30m", 30), ("1h", 60) };
        foreach (var (lbl, mins) in quicks)
        {
            var qb = MakeButton(lbl, theme.PrimaryDim, theme.TextPrimary);
            qb.Size = new Size(44, 30);
            qb.Location = new Point(qx, 14);
            int m = mins;
            qb.Click += (_, _) => QuickAlarm(m);
            toolbar.Controls.Add(qb);
            qx += 48;
        }

        _quickAddBox = new TextBox
        {
            Font = new Font("Segoe UI", 10f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Size = new Size(280, 26),
            Location = new Point(qx + 8, 16),
            PlaceholderText = "\"in 20 min\", \"tomorrow 8am\" …",
        };
        _quickAddBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; TryQuickAdd(); }
        };
        toolbar.Controls.Add(_quickAddBox);

        var historyBtn = MakeButton("History", theme.PrimaryDim, theme.TextPrimary);
        historyBtn.Size = new Size(80, 30);
        historyBtn.Location = new Point(ClientSize.Width - 80 - pad, 14);
        historyBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        historyBtn.Click += (_, _) => ShowHistory(null);
        toolbar.Controls.Add(historyBtn);

        // ── Filter chips ──
        _filterBar = new Panel
        {
            Location = new Point(0, 56),
            Size = new Size(ClientSize.Width, 38),
            BackColor = theme.BgDark,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_filterBar);
        BuildFilterChips();

        // ── Summary cards ──
        var cardsPanel = new Panel
        {
            Location = new Point(0, 94),
            Size = new Size(ClientSize.Width, 70),
            BackColor = theme.BgDark,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(cardsPanel);

        _cardActiveVal = BuildCard(cardsPanel, pad, "ACTIVE", "0", theme.SuccessColor);
        _cardTodayVal = BuildCard(cardsPanel, pad + 230, "TODAY", "0", theme.Primary);
        _cardNextVal = BuildCard(cardsPanel, pad + 460, "NEXT ALARM", "—", theme.TextPrimary);
        _cardMissedVal = BuildCard(cardsPanel, pad + 760, "MISSED", "0", theme.ErrorColor);

        // ── Full-width list ──
        int contentTop = 164;
        _listPanel = new FlowLayoutPanel
        {
            Location = new Point(pad, contentTop),
            Size = new Size(ClientSize.Width - pad * 2, ClientSize.Height - contentTop - pad),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = theme.BgDark,
            Padding = new Padding(0, 4, 0, 4),
        };
        _listPanel.ClientSizeChanged += (_, _) => ResizeListRows();
        Controls.Add(_listPanel);

        KeyDown += OnFormKeyDown;

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _refreshTimer.Tick += (_, _) => SoftRefresh();
        _refreshTimer.Start();
        FormClosed += (_, _) => _refreshTimer.Stop();

        RefreshList();
    }

    /// <summary>
    /// Composite the whole form so child controls paint in one buffered frame.
    /// Eliminates the flashing we used to see during the 30s refresh.
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x02000000; // WS_EX_COMPOSITED
            return cp;
        }
    }

    /// <summary>
    /// Lightweight periodic refresh — just repaints rows so relative-time labels
    /// stay fresh ("in 12m" → "in 11m"), and updates the summary cards. No tear-down.
    /// </summary>
    private void SoftRefresh()
    {
        foreach (Control c in _listPanel.Controls)
            if (c is AlarmListRow r) r.Invalidate();
        UpdateSummary(AlarmStore.LoadAlarms(), DateTime.Now);
    }

    private void BuildFilterChips()
    {
        _filterBar.Controls.Clear();
        _chips.Clear();
        var labels = new[]
        {
            ("all", "All"),
            ("today", "Today"),
            ("active", "Active"),
            ("paused", "Paused"),
            ("missed", "Missed"),
        };
        int x = 18;
        foreach (var (key, text) in labels)
        {
            var chip = new FilterChip(text, _theme, key == _activeFilter) { Key = key };
            chip.Location = new Point(x, 4);
            string k = key;
            chip.Click += (_, _) =>
            {
                _activeFilter = k;
                foreach (var c in _chips) c.SetSelected(c.Key == _activeFilter);
                RefreshList();
            };
            _filterBar.Controls.Add(chip);
            _chips.Add(chip);
            x += chip.Width + 6;
        }
    }

    private Label BuildCard(Panel host, int x, string label, string initialVal, Color accent)
    {
        var card = new Panel
        {
            BackColor = _theme.BgHeader,
            Location = new Point(x, 0),
            Size = new Size(210, 60),
        };
        host.Controls.Add(card);

        var stripe = new Panel
        {
            BackColor = accent,
            Location = new Point(0, 0),
            Size = new Size(3, 60),
        };
        card.Controls.Add(stripe);

        var caption = new Label
        {
            Text = label,
            Font = new Font("Segoe UI Semibold", 8.25f, FontStyle.Bold),
            ForeColor = _theme.TextSecondary,
            AutoSize = true,
            Location = new Point(14, 8),
            BackColor = Color.Transparent,
        };
        card.Controls.Add(caption);

        var value = new Label
        {
            Text = initialVal,
            Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold),
            ForeColor = _theme.TextPrimary,
            AutoSize = true,
            Location = new Point(14, 26),
            BackColor = Color.Transparent,
        };
        card.Controls.Add(value);
        return value;
    }

    private void RefreshList()
    {
        _listPanel.SuspendLayout();
        _listPanel.Controls.Clear();

        var all = AlarmStore.LoadAlarms();
        var q = (_searchBox.Text ?? "").Trim().ToLowerInvariant();

        IEnumerable<AlarmEntry> filtered = all;
        if (!string.IsNullOrEmpty(q))
        {
            filtered = filtered.Where(a =>
                a.Title.ToLowerInvariant().Contains(q)
                || a.Message.ToLowerInvariant().Contains(q)
                || (a.Category ?? "").ToLowerInvariant().Contains(q));
        }

        var now = DateTime.Now;
        filtered = _activeFilter switch
        {
            "today" => filtered.Where(a => a.GetNextTrigger() is DateTime d && d.Date == now.Date),
            "active" => filtered.Where(a => a.Status == AlarmStatus.Active
                && a.GetDisplayStatus() is AlarmDisplayStatus.Active or AlarmDisplayStatus.Missed),
            "paused" => filtered.Where(a => a.GetDisplayStatus() == AlarmDisplayStatus.Paused),
            "missed" => filtered.Where(a => a.GetDisplayStatus() == AlarmDisplayStatus.Missed),
            _ => filtered,
        };

        var ordered = filtered
            .OrderBy(a => a.Status == AlarmStatus.Active ? 0 : 1)
            .ThenBy(a => a.GetNextTrigger() ?? DateTime.MaxValue)
            .ToList();

        foreach (var a in ordered)
        {
            var row = new AlarmListRow(a, _theme) { Expanded = a.Id == _expandedId };
            row.RowClicked += id => ToggleExpand(id);
            row.RowDoubleClicked += id => EditInModal(id);
            row.ContextActionRequested += (id, anchor) => ShowRowContextMenu(id, anchor);
            row.StatusToggled += (id, enabled) =>
            {
                AlarmStore.SetStatus(id, enabled ? AlarmStatus.Active : AlarmStatus.Disabled);
                AlarmScheduler.Refresh();
                // Surgical refresh: push the updated entry to just this row (no list rebuild, no flicker).
                var updated = AlarmStore.GetAlarm(id);
                if (updated != null) row.UpdateEntry(updated);
                UpdateSummary(AlarmStore.LoadAlarms(), DateTime.Now);
            };
            _listPanel.Controls.Add(row);
        }

        ResizeListRows();
        _listPanel.ResumeLayout();

        UpdateSummary(all, now);

        if (_expandedId != null && !all.Any(a => a.Id == _expandedId))
            _expandedId = null;
    }

    private void ResizeListRows()
    {
        int w = _listPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 6;
        if (w < 120) w = 120;
        foreach (Control c in _listPanel.Controls)
            if (c is AlarmListRow r) r.Width = w;
    }

    private void UpdateSummary(List<AlarmEntry> all, DateTime now)
    {
        int active = all.Count(a => a.GetDisplayStatus() == AlarmDisplayStatus.Active);
        int today = all.Count(a => a.GetNextTrigger() is DateTime d && d.Date == now.Date);
        int missed = all.Count(a => a.GetDisplayStatus() == AlarmDisplayStatus.Missed);

        var next = all
            .Select(a => a.GetNextTrigger())
            .Where(d => d is DateTime)
            .Cast<DateTime>()
            .OrderBy(d => d)
            .FirstOrDefault();

        _cardActiveVal.Text = active.ToString();
        _cardTodayVal.Text = today.ToString();
        _cardNextVal.Text = next == default ? "—" : AlarmListRow.RelativeFuture(next, now);
        _cardMissedVal.Text = missed.ToString();
    }

    private void ToggleExpand(string id)
    {
        _expandedId = _expandedId == id ? null : id;
        foreach (Control c in _listPanel.Controls)
        {
            if (c is AlarmListRow r)
                r.Expanded = r.AlarmId == _expandedId;
        }
    }

    private void ShowRowContextMenu(string id, Control anchor)
    {
        var alarm = AlarmStore.GetAlarm(id);
        if (alarm == null) return;

        var menu = new ContextMenuStrip { BackColor = _theme.BgHeader, ForeColor = _theme.TextPrimary };
        menu.Items.Add("Edit…", null, (_, _) => EditInModal(id));
        menu.Items.Add("Duplicate", null, (_, _) => DuplicateInline(id));
        menu.Items.Add(new ToolStripSeparator());

        // Quick enable/disable
        var enableItem = new ToolStripMenuItem(alarm.Status == AlarmStatus.Active ? "Disable" : "Enable");
        enableItem.Click += (_, _) =>
        {
            AlarmStore.SetStatus(id, alarm.Status == AlarmStatus.Active
                ? AlarmStatus.Disabled : AlarmStatus.Active);
            AlarmScheduler.Refresh();
            RefreshList();
        };
        menu.Items.Add(enableItem);

        if (alarm.Schedule.Type != AlarmScheduleType.Once && alarm.Schedule.Type != AlarmScheduleType.Interval)
        {
            var skip = new ToolStripMenuItem("Skip next occurrence")
            {
                Checked = alarm.SkipNextOccurrence,
                CheckOnClick = false,
            };
            skip.Click += (_, _) =>
            {
                var cur = AlarmStore.GetAlarm(id);
                if (cur == null) return;
                AlarmStore.UpdateAlarm(cur with { SkipNextOccurrence = !cur.SkipNextOccurrence });
                AlarmScheduler.Refresh();
                RefreshList();
            };
            menu.Items.Add(skip);
        }

        var pauseMenu = new ToolStripMenuItem("Pause for …");
        pauseMenu.DropDownItems.Add("30 minutes", null, (_, _) => ApplyPause(id, TimeSpan.FromMinutes(30)));
        pauseMenu.DropDownItems.Add("2 hours", null, (_, _) => ApplyPause(id, TimeSpan.FromHours(2)));
        pauseMenu.DropDownItems.Add("Until tomorrow 8am", null, (_, _) =>
            ApplyPauseUntil(id, DateTime.Today.AddDays(1).AddHours(8)));
        pauseMenu.DropDownItems.Add("1 week", null, (_, _) => ApplyPause(id, TimeSpan.FromDays(7)));
        menu.Items.Add(pauseMenu);

        if (alarm.PausedUntil is DateTime pu && pu > DateTime.Now)
        {
            menu.Items.Add("Resume", null, (_, _) =>
            {
                var cur = AlarmStore.GetAlarm(id);
                if (cur == null) return;
                AlarmStore.UpdateAlarm(cur with { PausedUntil = null });
                AlarmStore.AddHistoryEntry(new AlarmHistoryEntry
                {
                    AlarmId = id, AlarmTitle = cur.Title, EventType = AlarmHistoryEventType.Resumed,
                });
                AlarmScheduler.Refresh();
                RefreshList();
            });
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Test trigger", null, (_, _) => AlarmScheduler.TestTrigger(alarm));
        menu.Items.Add("View history", null, (_, _) => ShowHistory(id));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Delete", null, (_, _) => DeleteInline(id));
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    private void ApplyPause(string id, TimeSpan d) => ApplyPauseUntil(id, DateTime.Now + d);

    private void ApplyPauseUntil(string id, DateTime until)
    {
        var cur = AlarmStore.GetAlarm(id);
        if (cur == null) return;
        AlarmStore.UpdateAlarm(cur with { PausedUntil = until });
        AlarmStore.AddHistoryEntry(new AlarmHistoryEntry
        {
            AlarmId = id, AlarmTitle = cur.Title, EventType = AlarmHistoryEventType.Paused,
            Detail = $"Paused until {until:g}",
        });
        AlarmScheduler.Refresh();
        RefreshList();
    }

    private void NewAlarm()
    {
        var form = new AlarmEditForm(_theme);
        if (form.ShowDialog(this) == DialogResult.OK && form.Result != null)
        {
            AlarmStore.AddAlarm(form.Result);
            AlarmScheduler.Refresh();
            _expandedId = form.Result.Id;
            RefreshList();
        }
    }

    private void QuickAlarm(int minutes)
    {
        var when = DateTime.Now.AddMinutes(minutes);
        var alarm = new AlarmEntry
        {
            Title = $"Timer ({minutes} min)",
            Message = $"{minutes}-minute timer",
            Schedule = new AlarmSchedule
            {
                Type = AlarmScheduleType.Once,
                TimeOfDay = when.ToString("HH:mm"),
                OneTimeDate = when.ToString("yyyy-MM-dd"),
            },
            FireAndForget = true,
            Notification = AlarmNotificationMode.Both,
            SoundEnabled = true,
        };
        AlarmStore.AddAlarm(alarm);
        AlarmScheduler.Refresh();
        _expandedId = alarm.Id;
        RefreshList();
    }

    private void TryQuickAdd()
    {
        var text = (_quickAddBox.Text ?? "").Trim();
        if (text.Length == 0) return;
        var parsed = AlarmQuickParser.TryParse(text);
        if (parsed == null)
        {
            MessageBox.Show(this,
                "Couldn't parse that. Try: \"in 15 minutes\", \"tomorrow at 8am\", \"every weekday at 9\".",
                "Quick add", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        AlarmStore.AddAlarm(parsed);
        AlarmScheduler.Refresh();
        _expandedId = parsed.Id;
        _quickAddBox.Clear();
        RefreshList();
    }

    private void EditInModal(string id)
    {
        var alarm = AlarmStore.GetAlarm(id);
        if (alarm == null) return;
        var form = new AlarmEditForm(_theme, alarm);
        var dr = form.ShowDialog(this);
        if (dr == DialogResult.OK)
        {
            if (form.DeleteRequested)
            {
                AlarmStore.DeleteAlarm(alarm.Id);
                if (_expandedId == alarm.Id) _expandedId = null;
            }
            else if (form.Result != null)
            {
                AlarmStore.UpdateAlarm(form.Result);
                AlarmStore.AddHistoryEntry(new AlarmHistoryEntry
                {
                    AlarmId = form.Result.Id, AlarmTitle = form.Result.Title,
                    EventType = AlarmHistoryEventType.Edited,
                });
            }
            AlarmScheduler.Refresh();
            RefreshList();
        }
    }

    private void DeleteInline(string id)
    {
        var res = MessageBox.Show(this, "Delete this alarm?", "Confirm Delete",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (res != DialogResult.Yes) return;
        AlarmStore.DeleteAlarm(id);
        AlarmScheduler.Refresh();
        if (_expandedId == id) _expandedId = null;
        RefreshList();
    }

    private void DuplicateInline(string id)
    {
        var src = AlarmStore.GetAlarm(id);
        if (src == null) return;
        var copy = src with
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = src.Title + " (copy)",
            CreatedAt = DateTime.Now,
            UpdatedAt = null,
            LastTriggeredAt = null,
            TriggerCount = 0,
            Status = AlarmStatus.Active,
            PausedUntil = null, SnoozedUntil = null, SkipNextOccurrence = false, LastError = null,
        };
        AlarmStore.AddAlarm(copy);
        AlarmScheduler.Refresh();
        _expandedId = copy.Id;
        RefreshList();
    }

    private void ShowHistory(string? alarmId)
    {
        var form = new AlarmHistoryForm(_theme, alarmId);
        form.Show(this);
    }

    private void OnFormKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.N) { NewAlarm(); e.Handled = true; return; }
        if (e.Control && e.KeyCode == Keys.F) { _searchBox.Focus(); _searchBox.SelectAll(); e.Handled = true; return; }
        if (e.KeyCode == Keys.Delete && _expandedId != null && ActiveControl is not TextBox)
        {
            DeleteInline(_expandedId); e.Handled = true; return;
        }
        if (e.KeyCode == Keys.Escape && _expandedId != null && ActiveControl is not TextBox)
        {
            _expandedId = null;
            foreach (Control c in _listPanel.Controls)
                if (c is AlarmListRow r) r.Expanded = false;
            e.Handled = true;
        }
    }

    private RoundedButton MakeButton(string text, Color bg, Color fg)
    {
        var b = new RoundedButton
        {
            Text = text,
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            Size = new Size(110, 30),
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
/// Pill-shaped filter chip. Colors change when selected.
/// </summary>
class FilterChip : Control
{
    private readonly PluginTheme _theme;
    private bool _selected;
    public string Key { get; set; } = "";

    public FilterChip(string text, PluginTheme theme, bool selected)
    {
        _theme = theme;
        _selected = selected;
        Text = text;
        Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        using var g = CreateGraphics();
        var size = g.MeasureString(text, Font);
        Width = (int)size.Width + 28;
        Height = 28;
    }

    public void SetSelected(bool v)
    {
        if (_selected == v) return;
        _selected = v;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = new GraphicsPath();
        int r = Height;
        path.AddArc(rect.X, rect.Y, r, r, 90, 180);
        path.AddArc(rect.Right - r, rect.Y, r, r, 270, 180);
        path.CloseFigure();

        var bg = _selected ? _theme.Primary : _theme.BgHeader;
        var fg = _selected ? Color.White : _theme.TextSecondary;
        using var b = new SolidBrush(bg);
        g.FillPath(b, path);
        if (!_selected)
        {
            using var pen = new Pen(_theme.Border, 1);
            g.DrawPath(pen, path);
        }
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var tb = new SolidBrush(fg);
        g.DrawString(Text, Font, tb, new RectangleF(0, 0, Width, Height), sf);
    }
}

/// <summary>
/// Row in the alarm list. Click to expand and reveal view-only details.
/// Double-click to open the edit dialog. ⋯ button shows row actions.
/// </summary>
class AlarmListRow : Panel
{
    private const int CollapsedHeight = 62;
    private const int ExpandedHeight = 180;

    private AlarmEntry _alarm;
    private readonly PluginTheme _theme;
    private readonly Button _actionBtn;
    private readonly ToggleSwitch _enableToggle;
    private readonly Label _toggleCaption;
    private bool _expanded;
    private bool _hover;

    public string AlarmId => _alarm.Id;

    public event Action<string>? RowClicked;
    public event Action<string>? RowDoubleClicked;
    public event Action<string, Control>? ContextActionRequested;
    public event Action<string, bool>? StatusToggled;

    public void UpdateEntry(AlarmEntry alarm)
    {
        _alarm = alarm;
        Invalidate();
    }

    public bool Expanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            Height = _expanded ? ExpandedHeight : CollapsedHeight;
            _enableToggle.Visible = _expanded;
            _toggleCaption.Visible = _expanded;
            LayoutChildren();
            Invalidate();
        }
    }

    public AlarmListRow(AlarmEntry alarm, PluginTheme theme)
    {
        _alarm = alarm;
        _theme = theme;
        Margin = new Padding(6, 3, 6, 3);
        BackColor = theme.BgDark;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _actionBtn = new Button
        {
            Text = "⋯",
            Font = new Font("Segoe UI Semibold", 14f, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat,
            ForeColor = theme.TextSecondary,
            BackColor = theme.BgDark,
            Size = new Size(30, 26),
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        _actionBtn.FlatAppearance.BorderSize = 0;
        _actionBtn.FlatAppearance.MouseOverBackColor = theme.BgHeader;
        _actionBtn.Click += (_, _) => ContextActionRequested?.Invoke(_alarm.Id, _actionBtn);
        Controls.Add(_actionBtn);

        _toggleCaption = new Label
        {
            Text = alarm.Status == AlarmStatus.Active ? "Enabled" : "Disabled",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            BackColor = Color.Transparent,
            Visible = false,
        };
        Controls.Add(_toggleCaption);

        _enableToggle = new ToggleSwitch(theme)
        {
            Checked = alarm.Status == AlarmStatus.Active,
            Visible = false,
        };
        _enableToggle.CheckedChanged += (_, _) =>
        {
            _toggleCaption.Text = _enableToggle.Checked ? "Enabled" : "Disabled";
            StatusToggled?.Invoke(_alarm.Id, _enableToggle.Checked);
        };
        Controls.Add(_enableToggle);

        Height = CollapsedHeight;

        Click += (_, _) => RowClicked?.Invoke(_alarm.Id);
        DoubleClick += (_, _) => RowDoubleClicked?.Invoke(_alarm.Id);
        MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        MouseLeave += (_, _) => { _hover = false; Invalidate(); };
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutChildren();
    }

    private void LayoutChildren()
    {
        if (_actionBtn == null) return; // fires during base ctor
        _actionBtn.Location = new Point(Width - _actionBtn.Width - 10, 18);
        if (_enableToggle != null && _toggleCaption != null && _expanded)
        {
            int toggleY = Height - _enableToggle.Height - 14;
            int toggleX = Width - _enableToggle.Width - 20;
            _enableToggle.Location = new Point(toggleX, toggleY);
            _toggleCaption.Location = new Point(
                toggleX - _toggleCaption.Width - 8,
                toggleY + (_enableToggle.Height - _toggleCaption.Height) / 2);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_actionBtn == null) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedRect(rect, 8);
        Color bg = _expanded ? _theme.BgHeader : (_hover ? _theme.BgHeader : _theme.BgDark);
        using (var bgBrush = new SolidBrush(bg)) g.FillPath(bgBrush, path);
        if (_expanded)
        {
            using var pen = new Pen(_theme.Primary, 1.5f);
            g.DrawPath(pen, path);
        }

        var displayStatus = _alarm.GetDisplayStatus();
        var statusColor = AlarmStatusChip.ColorFor(displayStatus, _theme);
        string statusLabel = AlarmStatusChip.Label(displayStatus);

        // Minimal status indicator: small filled dot + label.
        // Attention-grabbing states (Missed/Error) get colored text; the rest stay muted
        // so the row title reads as the primary information.
        bool emphasize = displayStatus is AlarmDisplayStatus.Missed or AlarmDisplayStatus.Error;
        using var statusFont = new Font("Segoe UI", 9f);
        var labelSize = g.MeasureString(statusLabel, statusFont);
        int dotD = 8;
        int gap = 6;
        int labelW = (int)labelSize.Width;
        int statusBlockW = dotD + gap + labelW;
        int statusRight = Width - _actionBtn.Width - 14;
        int statusX = statusRight - statusBlockW;
        int statusCenterY = 30;

        using (var dotBrush = new SolidBrush(statusColor))
        {
            g.FillEllipse(dotBrush, statusX, statusCenterY - dotD / 2, dotD, dotD);
        }
        using (var labelBrush = new SolidBrush(emphasize ? statusColor : _theme.TextSecondary))
        using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
        {
            g.DrawString(statusLabel, statusFont, labelBrush,
                new RectangleF(statusX + dotD + gap, statusCenterY - 9, labelW + 4, 18), sf);
        }

        int textLeft = 20;
        int textRight = statusX - 12;
        if (textRight < textLeft + 60) textRight = Width - 40;

        // Title
        using (var titleFont = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold))
        using (var tbrush = new SolidBrush(_alarm.Status == AlarmStatus.Active ? _theme.TextPrimary : _theme.TextSecondary))
        {
            var title = string.IsNullOrWhiteSpace(_alarm.Title) ? "(untitled)" : _alarm.Title;
            var titleRect = new RectangleF(textLeft, 10, textRight - textLeft, 22);
            using var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(title, titleFont, tbrush, titleRect, sf);
        }

        // Subtitle
        var now = DateTime.Now;
        var next = _alarm.GetNextTrigger();
        string schedule = _alarm.GetScheduleDescription();
        string time = _alarm.Schedule.Type == AlarmScheduleType.Interval ? "" : _alarm.Schedule.TimeOfDay;
        string relative = next is DateTime nx ? RelativeFuture(nx, now)
            : _alarm.LastTriggeredAt is DateTime lt ? "last " + RelativePast(lt, now)
            : "";
        if (displayStatus == AlarmDisplayStatus.Paused && _alarm.PausedUntil is DateTime pu)
            relative = "paused · resumes " + RelativeFuture(pu, now);
        else if (displayStatus == AlarmDisplayStatus.Snoozed && _alarm.SnoozedUntil is DateTime su)
            relative = "snoozed · rings " + RelativeFuture(su, now);

        var subtitleParts = new[] { schedule, time, relative }.Where(s => !string.IsNullOrEmpty(s));
        var subtitle = string.Join("  •  ", subtitleParts);
        using (var sub = new Font("Segoe UI", 9f))
        using (var subBrush = new SolidBrush(_theme.TextSecondary))
        using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        {
            g.DrawString(subtitle, sub, subBrush, new RectangleF(textLeft, 34, textRight - textLeft, 20), sf);
        }

        if (_expanded)
            DrawExpandedDetails(g, textLeft, Width - textLeft - 20);
    }

    private void DrawExpandedDetails(Graphics g, int left, int availableWidth)
    {
        int y = CollapsedHeight + 2;
        using var rule = new Pen(_theme.Border, 1);
        g.DrawLine(rule, left, y, left + availableWidth, y);
        y += 8;

        using var keyFont = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold);
        using var valFont = new Font("Segoe UI", 9.25f);
        using var keyBrush = new SolidBrush(_theme.TextSecondary);
        using var valBrush = new SolidBrush(_theme.TextPrimary);

        void DrawRow(string key, string val, ref int yy, Color? valColor = null)
        {
            if (string.IsNullOrWhiteSpace(val)) return;
            g.DrawString(key.ToUpperInvariant(), keyFont, keyBrush, new PointF(left, yy));
            using var b = valColor is Color vc ? new SolidBrush(vc) : null;
            using var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(val, valFont, b ?? valBrush, new RectangleF(left + 110, yy - 2, availableWidth - 110, 20), sf);
            yy += 20;
        }

        if (!string.IsNullOrWhiteSpace(_alarm.Message))
            DrawRow("Message", _alarm.Message, ref y);

        var metaParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_alarm.Category)) metaParts.Add($"Category: {_alarm.Category}");
        metaParts.Add($"Priority: {_alarm.Priority}");
        DrawRow("Meta", string.Join("    ", metaParts), ref y);

        DrawRow("Next fire",
            _alarm.GetNextTrigger()?.ToString("ddd, MMM d yyyy  HH:mm") ?? "—", ref y);
        DrawRow("Last fired",
            _alarm.LastTriggeredAt?.ToString("ddd, MMM d yyyy  HH:mm") ?? "—", ref y);
        DrawRow("Triggered", $"{_alarm.TriggerCount} times", ref y);

        if (!string.IsNullOrEmpty(_alarm.LastError))
            DrawRow("Last error", _alarm.LastError!, ref y, _theme.ErrorColor);
    }

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

    public static string RelativeFuture(DateTime when, DateTime now)
    {
        var d = when - now;
        if (d.TotalSeconds <= 0) return "now";
        if (d.TotalMinutes < 1) return $"in {(int)d.TotalSeconds}s";
        if (d.TotalMinutes < 60) return $"in {(int)d.TotalMinutes}m";
        if (d.TotalHours < 24) return $"in {(int)d.TotalHours}h {(int)(d.TotalMinutes % 60)}m";
        if (d.TotalDays < 7) return $"in {(int)d.TotalDays}d";
        return when.ToString("MMM d HH:mm");
    }

    public static string RelativePast(DateTime when, DateTime now)
    {
        var d = now - when;
        if (d.TotalSeconds <= 30) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays}d ago";
        return when.ToString("MMM d");
    }
}
