using System.Drawing;
using ProdToy.Sdk;

namespace ProdToy.Plugins.Alarm;

class AlarmEditForm : Form
{
    private readonly TextBox _titleBox;
    private readonly TextBox _messageBox;
    private readonly ComboBox _scheduleCombo;
    private readonly DateTimePicker _timePicker;
    private readonly DateTimePicker _datePicker;
    private readonly NumericUpDown _dayOfMonthPicker;
    private readonly NumericUpDown _intervalPicker;
    private readonly Label _intervalLabel;
    private readonly Panel _customDaysPanel;
    private readonly CheckBox[] _dayCheckboxes;
    private readonly ComboBox _notifCombo;
    private readonly NumericUpDown _snoozePicker;
    private readonly CheckBox _soundCheck;
    private readonly CheckBox _fireAndForgetCheck;
    private readonly Label _validationLabel;

    public AlarmEntry? Result { get; private set; }

    public AlarmEditForm(PluginTheme theme, AlarmEntry? existing = null)
    {
        bool isEdit = existing != null;
        Text = isEdit ? "Edit Alarm" : "New Alarm";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(480, 580);
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 9.5f);
        Icon = IconHelper.CreateAppIcon(theme.Primary);
        AutoScaleMode = AutoScaleMode.Dpi;

        int pad = 20;
        int y = pad;
        int labelW = 110;
        int inputX = pad + labelW;
        int inputW = ClientSize.Width - inputX - pad;

        AddLabel("Title:", pad, y, theme);
        _titleBox = new TextBox
        {
            Text = existing?.Title ?? "",
            Font = new Font("Segoe UI", 10f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Size = new Size(inputW, 26),
            Location = new Point(inputX, y),
        };
        Controls.Add(_titleBox);
        y += 34;

        AddLabel("Message:", pad, y, theme);
        _messageBox = new TextBox
        {
            Text = existing?.Message ?? "",
            Font = new Font("Segoe UI", 9.5f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Multiline = true,
            Size = new Size(inputW, 52),
            Location = new Point(inputX, y),
        };
        Controls.Add(_messageBox);
        y += 60;

        AddLabel("Schedule:", pad, y, theme);
        _scheduleCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Segoe UI", 9.5f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(inputW, 26),
            Location = new Point(inputX, y),
        };
        foreach (var t in Enum.GetValues<AlarmScheduleType>())
            _scheduleCombo.Items.Add(t);
        _scheduleCombo.SelectedItem = existing?.Schedule.Type ?? AlarmScheduleType.Once;
        Controls.Add(_scheduleCombo);
        y += 34;

        AddLabel("Time:", pad, y, theme);
        _timePicker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Time,
            ShowUpDown = true,
            Font = new Font("Segoe UI", 10f),
            Size = new Size(inputW, 26),
            Location = new Point(inputX, y),
        };
        if (existing != null && TimeSpan.TryParse(existing.Schedule.TimeOfDay, out var ts))
            _timePicker.Value = DateTime.Today + ts;
        Controls.Add(_timePicker);
        y += 34;

        AddLabel("Date:", pad, y, theme);
        _datePicker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Short,
            Font = new Font("Segoe UI", 10f),
            Size = new Size(inputW, 26),
            Location = new Point(inputX, y),
        };
        if (existing?.Schedule.OneTimeDate != null && DateTime.TryParse(existing.Schedule.OneTimeDate, out var d))
            _datePicker.Value = d;
        else
            _datePicker.Value = DateTime.Today;
        Controls.Add(_datePicker);
        y += 34;

        var domLabel = AddLabel("Day of month:", pad, y, theme);
        _dayOfMonthPicker = new NumericUpDown
        {
            Minimum = 1, Maximum = 31,
            Value = existing?.Schedule.DayOfMonth ?? 1,
            Font = new Font("Segoe UI", 10f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            Size = new Size(80, 26),
            Location = new Point(inputX, y),
        };
        Controls.Add(_dayOfMonthPicker);
        y += 34;

        _intervalLabel = AddLabel("Every (min):", pad, y, theme);
        _intervalPicker = new NumericUpDown
        {
            Minimum = 1, Maximum = 1440,
            Value = existing?.Schedule.IntervalMinutes ?? 30,
            Font = new Font("Segoe UI", 10f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            Size = new Size(80, 26),
            Location = new Point(inputX, y),
        };
        Controls.Add(_intervalPicker);
        y += 34;

        _customDaysPanel = new Panel
        {
            Location = new Point(inputX, y),
            Size = new Size(inputW, 28),
            BackColor = Color.Transparent,
        };
        var existingDays = existing?.Schedule.CustomDays ?? Array.Empty<DayOfWeek>();
        _dayCheckboxes = new CheckBox[7];
        var dayNames = new[] { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" };
        for (int i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)i;
            _dayCheckboxes[i] = new CheckBox
            {
                Text = dayNames[i],
                Font = new Font("Segoe UI", 8f),
                ForeColor = theme.TextPrimary,
                BackColor = Color.Transparent,
                Checked = existingDays.Contains(day),
                AutoSize = true,
                Location = new Point(i * 44, 0),
                Cursor = Cursors.Hand,
            };
            _customDaysPanel.Controls.Add(_dayCheckboxes[i]);
        }
        AddLabel("Days:", pad, y, theme);
        Controls.Add(_customDaysPanel);
        y += 34;

        AddLabel("Notification:", pad, y, theme);
        _notifCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Segoe UI", 9.5f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(inputW, 26),
            Location = new Point(inputX, y),
        };
        foreach (var m in Enum.GetValues<AlarmNotificationMode>())
            _notifCombo.Items.Add(m);
        _notifCombo.SelectedItem = existing?.Notification ?? AlarmNotificationMode.Both;
        Controls.Add(_notifCombo);
        y += 34;

        _snoozePicker = new NumericUpDown
        {
            Minimum = 1, Maximum = 60,
            Value = existing?.SnoozeMinutes ?? 5,
            Font = new Font("Segoe UI", 9f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            Size = new Size(50, 24),
            Location = new Point(inputX + 85, y),
        };
        AddLabel("Snooze (min):", pad, y + 2, theme);
        Controls.Add(_snoozePicker);

        _soundCheck = new CheckBox
        {
            Text = "Sound",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = existing?.SoundEnabled ?? true,
            AutoSize = true,
            Location = new Point(inputX + 150, y + 2),
            Cursor = Cursors.Hand,
        };
        Controls.Add(_soundCheck);

        _fireAndForgetCheck = new CheckBox
        {
            Text = "Fire && forget",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = existing?.FireAndForget ?? false,
            AutoSize = true,
            Location = new Point(inputX + 230, y + 2),
            Cursor = Cursors.Hand,
        };
        Controls.Add(_fireAndForgetCheck);
        y += 38;

        _validationLabel = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = theme.ErrorColor,
            AutoSize = true,
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        Controls.Add(_validationLabel);
        y += 22;

        var saveButton = new RoundedButton
        {
            Text = isEdit ? "Save" : "Add Alarm",
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            Size = new Size(130, 36),
            Location = new Point(ClientSize.Width - pad - 130 - 10 - 90, y),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.Primary,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
        };
        saveButton.FlatAppearance.BorderSize = 0;
        saveButton.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        saveButton.Click += (_, _) => TrySave(existing);
        Controls.Add(saveButton);

        var cancelButton = new RoundedButton
        {
            Text = "Cancel",
            Font = new Font("Segoe UI", 9.5f),
            Size = new Size(90, 36),
            Location = new Point(ClientSize.Width - pad - 90, y),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextSecondary,
            Cursor = Cursors.Hand,
        };
        cancelButton.FlatAppearance.BorderSize = 0;
        cancelButton.FlatAppearance.MouseOverBackColor = theme.Primary;
        cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(cancelButton);

        ClientSize = new Size(ClientSize.Width, y + 36 + pad);

        _scheduleCombo.SelectedIndexChanged += (_, _) => UpdateFieldVisibility();
        UpdateFieldVisibility();
    }

    private void UpdateFieldVisibility()
    {
        var type = (AlarmScheduleType)(_scheduleCombo.SelectedItem ?? AlarmScheduleType.Once);
        _datePicker.Visible = type == AlarmScheduleType.Once;
        _dayOfMonthPicker.Visible = type == AlarmScheduleType.Monthly;
        _intervalPicker.Visible = type == AlarmScheduleType.Interval;
        _intervalLabel.Visible = type == AlarmScheduleType.Interval;
        _customDaysPanel.Visible = type is AlarmScheduleType.Weekly or AlarmScheduleType.Custom;

        foreach (Control c in Controls)
        {
            if (c is Label lbl && lbl.Text == "Date:")
                lbl.Visible = type == AlarmScheduleType.Once;
            if (c is Label lbl2 && lbl2.Text == "Day of month:")
                lbl2.Visible = type == AlarmScheduleType.Monthly;
            if (c is Label lbl3 && lbl3.Text == "Days:")
                lbl3.Visible = type is AlarmScheduleType.Weekly or AlarmScheduleType.Custom;
        }

        _timePicker.Visible = type != AlarmScheduleType.Interval;
        foreach (Control c in Controls)
        {
            if (c is Label lbl && lbl.Text == "Time:")
                lbl.Visible = type != AlarmScheduleType.Interval;
        }
    }

    private void TrySave(AlarmEntry? existing)
    {
        if (string.IsNullOrWhiteSpace(_titleBox.Text))
        {
            _validationLabel.Text = "Title is required.";
            return;
        }

        var type = (AlarmScheduleType)(_scheduleCombo.SelectedItem ?? AlarmScheduleType.Once);

        if (type == AlarmScheduleType.Once)
        {
            var dt = _datePicker.Value.Date + _timePicker.Value.TimeOfDay;
            if (dt <= DateTime.Now)
            {
                _validationLabel.Text = "Date and time must be in the future.";
                return;
            }
        }

        if (type is AlarmScheduleType.Weekly or AlarmScheduleType.Custom)
        {
            if (!_dayCheckboxes.Any(c => c.Checked))
            {
                _validationLabel.Text = "Select at least one day.";
                return;
            }
        }

        var selectedDays = _dayCheckboxes
            .Select((cb, i) => (cb.Checked, Day: (DayOfWeek)i))
            .Where(x => x.Checked)
            .Select(x => x.Day)
            .ToArray();

        var schedule = new AlarmSchedule
        {
            Type = type,
            TimeOfDay = _timePicker.Value.ToString("HH:mm"),
            OneTimeDate = type == AlarmScheduleType.Once ? _datePicker.Value.ToString("yyyy-MM-dd") : null,
            DayOfMonth = type == AlarmScheduleType.Monthly ? (int)_dayOfMonthPicker.Value : null,
            IntervalMinutes = type == AlarmScheduleType.Interval ? (int)_intervalPicker.Value : null,
            CustomDays = type is AlarmScheduleType.Weekly or AlarmScheduleType.Custom ? selectedDays : null,
        };

        Result = new AlarmEntry
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
            Title = _titleBox.Text.Trim(),
            Message = _messageBox.Text.Trim(),
            Schedule = schedule,
            Status = existing?.Status ?? AlarmStatus.Active,
            Notification = (AlarmNotificationMode)(_notifCombo.SelectedItem ?? AlarmNotificationMode.Both),
            SnoozeMinutes = (int)_snoozePicker.Value,
            SoundEnabled = _soundCheck.Checked,
            FireAndForget = _fireAndForgetCheck.Checked,
            CreatedAt = existing?.CreatedAt ?? DateTime.Now,
            UpdatedAt = existing != null ? DateTime.Now : null,
            LastTriggeredAt = existing?.LastTriggeredAt,
            TriggerCount = existing?.TriggerCount ?? 0,
        };

        DialogResult = DialogResult.OK;
        Close();
    }

    private Label AddLabel(string text, int x, int y, PluginTheme theme)
    {
        var lbl = new Label
        {
            Text = text,
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(x, y + 3),
            BackColor = Color.Transparent,
        };
        Controls.Add(lbl);
        return lbl;
    }
}
