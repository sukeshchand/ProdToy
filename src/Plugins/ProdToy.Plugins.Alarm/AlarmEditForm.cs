using System.Drawing;
using ProdToy.Sdk;

namespace ProdToy.Plugins.Alarm;

class AlarmEditForm : Form
{
    private readonly PluginTheme _theme;
    private readonly AlarmEntry? _existing;

    // General
    private readonly TextBox _titleBox;
    private readonly TextBox _messageBox;
    private readonly TextBox _categoryBox;
    private readonly ComboBox _priorityCombo;
    private readonly ToggleSwitch _enabledToggle;

    // Schedule
    private readonly ComboBox _scheduleCombo;
    private readonly DateTimePicker _timePicker;
    private readonly DateTimePicker _datePicker;
    private readonly NumericUpDown _dayOfMonthPicker;
    private readonly NumericUpDown _intervalPicker;
    private readonly Label _dateLabel, _timeLabel, _domLabel, _intervalLabel, _daysLabel;
    private readonly Panel _customDaysPanel;
    private readonly CheckBox[] _dayCheckboxes;
    private readonly CheckBox _startDateCheck;
    private readonly DateTimePicker _startDatePicker;
    private readonly CheckBox _endDateCheck;
    private readonly DateTimePicker _endDatePicker;

    // Notification
    private readonly ComboBox _notifCombo;
    private readonly NumericUpDown _snoozePicker;
    private readonly CheckBox _soundCheck;
    private readonly CheckBox _fireAndForgetCheck;

    // Diagnostics (existing alarm only)
    private readonly Label? _diagNextFire;
    private readonly Label? _diagLastFired;
    private readonly Label? _diagTriggerCount;
    private readonly Label? _diagLastError;

    private readonly Label _validationLabel;

    public AlarmEntry? Result { get; private set; }
    public bool DeleteRequested { get; private set; }

    public AlarmEditForm(PluginTheme theme, AlarmEntry? existing = null)
    {
        _theme = theme;
        _existing = existing;
        bool isEdit = existing != null;

        Text = isEdit ? "Edit Alarm" : "New Alarm";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(560, isEdit ? 780 : 720);
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 9.5f);
        Icon = IconHelper.CreateAppIcon(theme.Primary);
        AutoScaleMode = AutoScaleMode.Dpi;

        int pad = 20;
        int y = pad;
        int labelW = 120;
        int inputX = pad + labelW;
        int inputW = ClientSize.Width - inputX - pad;

        // --- Header: title + enabled toggle ---
        var header = new Label
        {
            Text = isEdit ? "Edit Alarm" : "New Alarm",
            Font = new Font("Segoe UI Semibold", 14f, FontStyle.Bold),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        Controls.Add(header);

        var toggleCaption = new Label
        {
            Text = "Enabled",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(ClientSize.Width - pad - 46 - 70, y + 8),
            BackColor = Color.Transparent,
        };
        Controls.Add(toggleCaption);

        _enabledToggle = new ToggleSwitch(theme)
        {
            Location = new Point(ClientSize.Width - pad - 46, y + 6),
            Checked = existing == null || existing.Status == AlarmStatus.Active,
        };
        Controls.Add(_enabledToggle);
        y += 40;

        // --- General section ---
        y = AddSection("GENERAL", y);
        AddLabel("Title", pad, y);
        _titleBox = MakeTextBox(inputX, y, inputW);
        _titleBox.Text = existing?.Title ?? "";
        y += 34;

        AddLabel("Message", pad, y);
        _messageBox = MakeTextBox(inputX, y, inputW, multiline: true, height: 52);
        _messageBox.Text = existing?.Message ?? "";
        y += 60;

        AddLabel("Category", pad, y);
        _categoryBox = MakeTextBox(inputX, y, 180);
        _categoryBox.Text = existing?.Category ?? "";
        AddLabel("Priority", inputX + 200, y);
        _priorityCombo = MakeCombo(inputX + 200 + 60, y, 110);
        foreach (var p in Enum.GetValues<AlarmPriority>()) _priorityCombo.Items.Add(p);
        _priorityCombo.SelectedItem = existing?.Priority ?? AlarmPriority.Normal;
        y += 38;

        // --- Schedule section ---
        y = AddSection("SCHEDULE", y);
        AddLabel("Type", pad, y);
        _scheduleCombo = MakeCombo(inputX, y, 200);
        foreach (var t in Enum.GetValues<AlarmScheduleType>()) _scheduleCombo.Items.Add(t);
        _scheduleCombo.SelectedItem = existing?.Schedule.Type ?? AlarmScheduleType.Once;
        y += 34;

        _timeLabel = AddLabel("Time", pad, y);
        _timePicker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Time,
            ShowUpDown = true,
            Font = new Font("Segoe UI", 10f),
            Size = new Size(100, 26),
            Location = new Point(inputX, y),
        };
        if (existing != null && TimeSpan.TryParse(existing.Schedule.TimeOfDay, out var ts))
            _timePicker.Value = DateTime.Today + ts;
        Controls.Add(_timePicker);
        y += 34;

        _dateLabel = AddLabel("Date", pad, y);
        _datePicker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Short,
            Font = new Font("Segoe UI", 10f),
            Size = new Size(140, 26),
            Location = new Point(inputX, y),
        };
        if (existing?.Schedule.OneTimeDate != null
            && DateTime.TryParse(existing.Schedule.OneTimeDate, out var od))
            _datePicker.Value = od;
        else
            _datePicker.Value = DateTime.Today;
        Controls.Add(_datePicker);
        y += 34;

        _domLabel = AddLabel("Day of month", pad, y);
        _dayOfMonthPicker = new NumericUpDown
        {
            Minimum = 1, Maximum = 31,
            Value = Math.Clamp(existing?.Schedule.DayOfMonth ?? 1, 1, 31),
            Font = new Font("Segoe UI", 10f),
            BackColor = theme.BgHeader, ForeColor = theme.TextPrimary,
            Size = new Size(80, 26),
            Location = new Point(inputX, y),
        };
        Controls.Add(_dayOfMonthPicker);
        y += 34;

        _intervalLabel = AddLabel("Every (min)", pad, y);
        _intervalPicker = new NumericUpDown
        {
            Minimum = 1, Maximum = 1440,
            Value = Math.Clamp(existing?.Schedule.IntervalMinutes ?? 30, 1, 1440),
            Font = new Font("Segoe UI", 10f),
            BackColor = theme.BgHeader, ForeColor = theme.TextPrimary,
            Size = new Size(80, 26),
            Location = new Point(inputX, y),
        };
        Controls.Add(_intervalPicker);
        y += 34;

        _daysLabel = AddLabel("Days", pad, y);
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
                Font = new Font("Segoe UI", 9f),
                ForeColor = theme.TextPrimary,
                BackColor = Color.Transparent,
                Checked = existingDays.Contains(day),
                AutoSize = true,
                Location = new Point(i * 46, 4),
                Cursor = Cursors.Hand,
            };
            _customDaysPanel.Controls.Add(_dayCheckboxes[i]);
        }
        Controls.Add(_customDaysPanel);
        y += 34;

        _startDateCheck = new CheckBox
        {
            Text = "Start date",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(pad, y + 4),
            Cursor = Cursors.Hand,
        };
        Controls.Add(_startDateCheck);
        _startDatePicker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Short,
            Font = new Font("Segoe UI", 10f),
            Size = new Size(140, 26),
            Location = new Point(inputX, y),
            Enabled = false,
        };
        if (existing?.StartDate != null && DateTime.TryParse(existing.StartDate, out var sd))
        { _startDateCheck.Checked = true; _startDatePicker.Value = sd; _startDatePicker.Enabled = true; }
        else { _startDatePicker.Value = DateTime.Today; }
        Controls.Add(_startDatePicker);
        _startDateCheck.CheckedChanged += (_, _) => _startDatePicker.Enabled = _startDateCheck.Checked;
        y += 34;

        _endDateCheck = new CheckBox
        {
            Text = "End date",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(pad, y + 4),
            Cursor = Cursors.Hand,
        };
        Controls.Add(_endDateCheck);
        _endDatePicker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Short,
            Font = new Font("Segoe UI", 10f),
            Size = new Size(140, 26),
            Location = new Point(inputX, y),
            Enabled = false,
        };
        if (existing?.EndDate != null && DateTime.TryParse(existing.EndDate, out var ed))
        { _endDateCheck.Checked = true; _endDatePicker.Value = ed; _endDatePicker.Enabled = true; }
        else { _endDatePicker.Value = DateTime.Today.AddMonths(1); }
        Controls.Add(_endDatePicker);
        _endDateCheck.CheckedChanged += (_, _) => _endDatePicker.Enabled = _endDateCheck.Checked;
        y += 38;

        // --- Notification ---
        y = AddSection("NOTIFICATION", y);
        AddLabel("Mode", pad, y);
        _notifCombo = MakeCombo(inputX, y, 160);
        foreach (var m in Enum.GetValues<AlarmNotificationMode>()) _notifCombo.Items.Add(m);
        _notifCombo.SelectedItem = existing?.Notification ?? AlarmNotificationMode.Both;

        AddLabel("Snooze (min)", inputX + 180, y);
        _snoozePicker = new NumericUpDown
        {
            Minimum = 1, Maximum = 60,
            Value = Math.Clamp(existing?.SnoozeMinutes ?? 5, 1, 60),
            Font = new Font("Segoe UI", 10f),
            BackColor = theme.BgHeader, ForeColor = theme.TextPrimary,
            Size = new Size(60, 26),
            Location = new Point(inputX + 180 + 90, y),
        };
        Controls.Add(_snoozePicker);
        y += 34;

        _soundCheck = new CheckBox
        {
            Text = "Play sound",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = existing?.SoundEnabled ?? true,
            AutoSize = true,
            Location = new Point(inputX, y),
            Cursor = Cursors.Hand,
        };
        Controls.Add(_soundCheck);

        _fireAndForgetCheck = new CheckBox
        {
            Text = "Fire and forget (one-shot)",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = existing?.FireAndForget ?? false,
            AutoSize = true,
            Location = new Point(inputX + 120, y),
            Cursor = Cursors.Hand,
        };
        Controls.Add(_fireAndForgetCheck);
        y += 32;

        // --- Diagnostics (edit mode only) ---
        if (isEdit)
        {
            y = AddSection("DIAGNOSTICS", y);
            _diagNextFire = AddDiagRow("Next fire",
                existing!.GetNextTrigger()?.ToString("ddd, MMM d yyyy  HH:mm") ?? "—", ref y);
            _diagLastFired = AddDiagRow("Last fired",
                existing.LastTriggeredAt?.ToString("ddd, MMM d yyyy  HH:mm") ?? "—", ref y);
            _diagTriggerCount = AddDiagRow("Trigger count",
                existing.TriggerCount.ToString(), ref y);
            _diagLastError = AddDiagRow("Last error",
                string.IsNullOrEmpty(existing.LastError) ? "—" : existing.LastError, ref y);
            _diagLastError.ForeColor = theme.ErrorColor;
            y += 6;
        }

        // --- Validation + buttons ---
        _validationLabel = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 9f),
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
            Location = new Point(ClientSize.Width - pad - 130, y),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.Primary,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
        };
        saveButton.FlatAppearance.BorderSize = 0;
        saveButton.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        saveButton.Click += (_, _) => TrySave();
        Controls.Add(saveButton);

        var cancelButton = new RoundedButton
        {
            Text = "Cancel",
            Font = new Font("Segoe UI", 9.5f),
            Size = new Size(90, 36),
            Location = new Point(ClientSize.Width - pad - 130 - 10 - 90, y),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextSecondary,
            Cursor = Cursors.Hand,
        };
        cancelButton.FlatAppearance.BorderSize = 0;
        cancelButton.FlatAppearance.MouseOverBackColor = theme.Primary;
        cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(cancelButton);

        if (isEdit)
        {
            var deleteButton = new RoundedButton
            {
                Text = "Delete",
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                Size = new Size(90, 36),
                Location = new Point(pad, y),
                FlatStyle = FlatStyle.Flat,
                BackColor = theme.ErrorBg,
                ForeColor = theme.ErrorColor,
                Cursor = Cursors.Hand,
            };
            deleteButton.FlatAppearance.BorderSize = 0;
            deleteButton.FlatAppearance.MouseOverBackColor = theme.ErrorColor;
            deleteButton.Click += (_, _) => TryDelete();
            Controls.Add(deleteButton);
        }

        ClientSize = new Size(ClientSize.Width, y + 36 + pad);

        _scheduleCombo.SelectedIndexChanged += (_, _) => UpdateFieldVisibility();
        UpdateFieldVisibility();
    }

    private int AddSection(string text, int y)
    {
        var hdr = new Label
        {
            Text = text,
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
            ForeColor = _theme.Primary,
            AutoSize = true,
            Location = new Point(20, y),
            BackColor = Color.Transparent,
        };
        Controls.Add(hdr);
        var rule = new Panel
        {
            BackColor = _theme.Border,
            Location = new Point(20, y + 22),
            Size = new Size(ClientSize.Width - 40, 1),
        };
        Controls.Add(rule);
        return y + 32;
    }

    private Label AddLabel(string text, int x, int y)
    {
        var lbl = new Label
        {
            Text = text,
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = _theme.TextSecondary,
            AutoSize = true,
            Location = new Point(x, y + 4),
            BackColor = Color.Transparent,
        };
        Controls.Add(lbl);
        return lbl;
    }

    private TextBox MakeTextBox(int x, int y, int w, bool multiline = false, int height = 26)
    {
        var tb = new TextBox
        {
            Font = new Font("Segoe UI", 10f),
            BackColor = _theme.BgHeader,
            ForeColor = _theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Multiline = multiline,
            Size = new Size(w, height),
            Location = new Point(x, y),
        };
        Controls.Add(tb);
        return tb;
    }

    private ComboBox MakeCombo(int x, int y, int w)
    {
        var cb = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Segoe UI", 10f),
            BackColor = _theme.BgHeader,
            ForeColor = _theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(w, 26),
            Location = new Point(x, y),
        };
        Controls.Add(cb);
        return cb;
    }

    private Label AddDiagRow(string key, string value, ref int y)
    {
        AddLabel(key, 20, y);
        var val = new Label
        {
            Text = value,
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = _theme.TextPrimary,
            AutoSize = true,
            Location = new Point(140, y + 4),
            BackColor = Color.Transparent,
            MaximumSize = new Size(ClientSize.Width - 160, 0),
        };
        Controls.Add(val);
        y += 26;
        return val;
    }

    private void UpdateFieldVisibility()
    {
        var type = (AlarmScheduleType)(_scheduleCombo.SelectedItem ?? AlarmScheduleType.Once);
        _dateLabel.Visible = _datePicker.Visible = type == AlarmScheduleType.Once;
        _domLabel.Visible = _dayOfMonthPicker.Visible = type == AlarmScheduleType.Monthly;
        _intervalLabel.Visible = _intervalPicker.Visible = type == AlarmScheduleType.Interval;
        _daysLabel.Visible = _customDaysPanel.Visible = type is AlarmScheduleType.Weekly or AlarmScheduleType.Custom;
        _timeLabel.Visible = _timePicker.Visible = type != AlarmScheduleType.Interval;
    }

    private void TrySave()
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

        var status = _enabledToggle.Checked
            ? AlarmStatus.Active
            : AlarmStatus.Disabled;

        // Preserve non-UI status values (Completed/Expired) unless the user re-enables a finished alarm.
        if (_existing != null
            && _existing.Status is AlarmStatus.Completed or AlarmStatus.Expired
            && !_enabledToggle.Checked)
        {
            status = _existing.Status;
        }

        Result = new AlarmEntry
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N"),
            Title = _titleBox.Text.Trim(),
            Message = _messageBox.Text.Trim(),
            Schedule = schedule,
            Status = status,
            Notification = (AlarmNotificationMode)(_notifCombo.SelectedItem ?? AlarmNotificationMode.Both),
            SnoozeMinutes = (int)_snoozePicker.Value,
            SoundEnabled = _soundCheck.Checked,
            FireAndForget = _fireAndForgetCheck.Checked,
            Category = string.IsNullOrWhiteSpace(_categoryBox.Text) ? null : _categoryBox.Text.Trim(),
            Priority = (AlarmPriority)(_priorityCombo.SelectedItem ?? AlarmPriority.Normal),
            StartDate = _startDateCheck.Checked ? _startDatePicker.Value.ToString("yyyy-MM-dd") : null,
            EndDate = _endDateCheck.Checked ? _endDatePicker.Value.ToString("yyyy-MM-dd") : null,
            CreatedAt = _existing?.CreatedAt ?? DateTime.Now,
            UpdatedAt = _existing != null ? DateTime.Now : null,
            LastTriggeredAt = _existing?.LastTriggeredAt,
            TriggerCount = _existing?.TriggerCount ?? 0,
            LastError = _existing?.LastError,
            PausedUntil = _existing?.PausedUntil,
            SnoozedUntil = _existing?.SnoozedUntil,
            SkipNextOccurrence = _existing?.SkipNextOccurrence ?? false,
            ExceptionDates = _existing?.ExceptionDates,
            Actions = _existing?.Actions,
            Note = _existing?.Note ?? "",
        };

        DialogResult = DialogResult.OK;
        Close();
    }

    private void TryDelete()
    {
        if (_existing == null) return;
        var res = MessageBox.Show(this, "Delete this alarm?", "Confirm Delete",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (res != DialogResult.Yes) return;
        DeleteRequested = true;
        DialogResult = DialogResult.OK;
        Close();
    }
}
