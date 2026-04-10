using System.Drawing;
using ProdToy.Sdk;

namespace ProdToy.Plugins.Alarm;

class AlarmForm : Form
{
    private readonly PluginTheme _theme;
    private readonly ListView _listView;

    public AlarmForm(PluginTheme theme)
    {
        _theme = theme;

        Text = "ProdToy \u2014 Alarms";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(820, 540);
        MinimumSize = new Size(600, 400);
        ShowInTaskbar = true;
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 10f);
        Icon = IconHelper.CreateAppIcon(theme.Primary);
        AutoScaleMode = AutoScaleMode.Dpi;

        int pad = 20;
        int y = pad;

        var titleLabel = new Label
        {
            Text = "Alarms",
            Font = new Font("Segoe UI Semibold", 18f, FontStyle.Bold),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        Controls.Add(titleLabel);
        y += 44;

        var accentLine = new Panel
        {
            BackColor = theme.Primary,
            Location = new Point(pad, y),
            Size = new Size(ClientSize.Width - pad * 2, 2),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(accentLine);
        y += 10;

        var addButton = CreateToolButton("+ Add Alarm", theme.Primary, Color.White, theme);
        addButton.Location = new Point(pad, y);
        addButton.Click += (_, _) => AddAlarm();
        Controls.Add(addButton);

        var quickButtons = new[] { ("5 min", 5), ("10 min", 10), ("30 min", 30), ("1 hour", 60) };
        int qx = pad + addButton.Width + 10;
        foreach (var (label, mins) in quickButtons)
        {
            var qb = CreateToolButton(label, theme.PrimaryDim, theme.TextSecondary, theme);
            qb.Location = new Point(qx, y);
            qb.Size = new Size(60, 30);
            int m = mins;
            qb.Click += (_, _) => QuickAlarm(m);
            Controls.Add(qb);
            qx += 66;
        }

        var historyButton = CreateToolButton("History", theme.PrimaryDim, theme.TextSecondary, theme);
        historyButton.Location = new Point(qx + 10, y);
        historyButton.Click += (_, _) => ShowHistory(null);
        Controls.Add(historyButton);
        y += 40;

        _listView = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            MultiSelect = false,
            CheckBoxes = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Font = new Font("Segoe UI", 9.5f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(pad, y),
            Size = new Size(ClientSize.Width - pad * 2, ClientSize.Height - y - pad),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        };
        _listView.Columns.Add("Title", 180);
        _listView.Columns.Add("Schedule", 130);
        _listView.Columns.Add("Time", 60);
        _listView.Columns.Add("Next Fire", 140);
        _listView.Columns.Add("Last Fired", 140);
        _listView.Columns.Add("Status", 80);
        _listView.ItemChecked += OnItemChecked;
        _listView.DoubleClick += (_, _) => EditSelected();

        var ctx = new ContextMenuStrip();
        ctx.Items.Add("Edit", null, (_, _) => EditSelected());
        ctx.Items.Add("Delete", null, (_, _) => DeleteSelected());
        ctx.Items.Add(new ToolStripSeparator());
        ctx.Items.Add("Test Trigger", null, (_, _) => TestSelected());
        ctx.Items.Add("View History", null, (_, _) => ShowHistoryForSelected());
        ctx.Items.Add(new ToolStripSeparator());
        ctx.Items.Add("Duplicate", null, (_, _) => DuplicateSelected());
        _listView.ContextMenuStrip = ctx;

        Controls.Add(_listView);
        RefreshList();
    }

    private void RefreshList()
    {
        _listView.ItemChecked -= OnItemChecked;
        _listView.Items.Clear();

        var alarms = AlarmStore.LoadAlarms();
        foreach (var alarm in alarms.OrderBy(a => a.GetNextTrigger() ?? DateTime.MaxValue))
        {
            var item = new ListViewItem
            {
                Text = alarm.Title,
                Checked = alarm.Status == AlarmStatus.Active,
                Tag = alarm.Id,
                ForeColor = alarm.Status == AlarmStatus.Active ? _theme.TextPrimary : _theme.TextSecondary,
            };
            item.SubItems.Add(alarm.GetScheduleDescription());
            item.SubItems.Add(alarm.Schedule.TimeOfDay);
            item.SubItems.Add(alarm.GetNextTrigger()?.ToString("g") ?? "-");
            item.SubItems.Add(alarm.LastTriggeredAt?.ToString("g") ?? "-");
            item.SubItems.Add(alarm.Status.ToString());
            _listView.Items.Add(item);
        }

        _listView.ItemChecked += OnItemChecked;
    }

    private void OnItemChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (e.Item.Tag is not string id) return;
        var newStatus = e.Item.Checked ? AlarmStatus.Active : AlarmStatus.Disabled;
        AlarmStore.SetStatus(id, newStatus);
        AlarmScheduler.Refresh();

        e.Item.ForeColor = e.Item.Checked ? _theme.TextPrimary : _theme.TextSecondary;
        var alarm = AlarmStore.GetAlarm(id);
        if (alarm != null)
        {
            e.Item.SubItems[3].Text = alarm.GetNextTrigger()?.ToString("g") ?? "-";
            e.Item.SubItems[5].Text = alarm.Status.ToString();
        }
    }

    private void AddAlarm()
    {
        var form = new AlarmEditForm(_theme);
        if (form.ShowDialog(this) == DialogResult.OK && form.Result != null)
        {
            AlarmStore.AddAlarm(form.Result);
            AlarmScheduler.Refresh();
            RefreshList();
        }
    }

    private void QuickAlarm(int minutes)
    {
        var alarm = new AlarmEntry
        {
            Title = $"Timer ({minutes} min)",
            Message = $"{minutes}-minute timer",
            Schedule = new AlarmSchedule
            {
                Type = AlarmScheduleType.Once,
                TimeOfDay = DateTime.Now.AddMinutes(minutes).ToString("HH:mm"),
                OneTimeDate = DateTime.Now.AddMinutes(minutes).ToString("yyyy-MM-dd"),
            },
            FireAndForget = true,
            Notification = AlarmNotificationMode.Both,
            SoundEnabled = true,
        };
        AlarmStore.AddAlarm(alarm);
        AlarmScheduler.Refresh();
        RefreshList();
    }

    private void EditSelected()
    {
        if (_listView.SelectedItems.Count == 0) return;
        var id = _listView.SelectedItems[0].Tag as string;
        if (id == null) return;

        var alarm = AlarmStore.GetAlarm(id);
        if (alarm == null) return;

        var form = new AlarmEditForm(_theme, alarm);
        if (form.ShowDialog(this) == DialogResult.OK && form.Result != null)
        {
            AlarmStore.UpdateAlarm(form.Result);
            AlarmStore.AddHistoryEntry(new AlarmHistoryEntry
            {
                AlarmId = form.Result.Id,
                AlarmTitle = form.Result.Title,
                EventType = AlarmHistoryEventType.Edited,
            });
            AlarmScheduler.Refresh();
            RefreshList();
        }
    }

    private void DeleteSelected()
    {
        if (_listView.SelectedItems.Count == 0) return;
        var id = _listView.SelectedItems[0].Tag as string;
        if (id == null) return;

        var result = MessageBox.Show(this,
            "Delete this alarm?", "Confirm Delete",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);

        if (result == DialogResult.Yes)
        {
            AlarmStore.DeleteAlarm(id);
            AlarmScheduler.Refresh();
            RefreshList();
        }
    }

    private void TestSelected()
    {
        if (_listView.SelectedItems.Count == 0) return;
        var id = _listView.SelectedItems[0].Tag as string;
        if (id == null) return;

        var alarm = AlarmStore.GetAlarm(id);
        if (alarm != null)
            AlarmScheduler.TestTrigger(alarm);
    }

    private void ShowHistoryForSelected()
    {
        if (_listView.SelectedItems.Count == 0) return;
        var id = _listView.SelectedItems[0].Tag as string;
        ShowHistory(id);
    }

    private void DuplicateSelected()
    {
        if (_listView.SelectedItems.Count == 0) return;
        var id = _listView.SelectedItems[0].Tag as string;
        if (id == null) return;

        var alarm = AlarmStore.GetAlarm(id);
        if (alarm == null) return;

        var copy = alarm with
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = alarm.Title + " (copy)",
            CreatedAt = DateTime.Now,
            UpdatedAt = null,
            LastTriggeredAt = null,
            TriggerCount = 0,
            Status = AlarmStatus.Active,
        };
        AlarmStore.AddAlarm(copy);
        AlarmScheduler.Refresh();
        RefreshList();
    }

    private void ShowHistory(string? alarmId)
    {
        var form = new AlarmHistoryForm(_theme, alarmId);
        form.Show(this);
    }

    private RoundedButton CreateToolButton(string text, Color bg, Color fg, PluginTheme theme)
    {
        var btn = new RoundedButton
        {
            Text = text,
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            Size = new Size(110, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = bg,
            ForeColor = fg,
            Cursor = Cursors.Hand,
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        return btn;
    }
}
