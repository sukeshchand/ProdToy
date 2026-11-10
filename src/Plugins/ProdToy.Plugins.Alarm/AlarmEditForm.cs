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

    // Body tab
    private const int TabInset = 24;
    private readonly int _layoutW;
    private readonly string _alarmId;
    private Control _target;
    private readonly TabControl _tabs;
    private ComboBox _bodyKindCombo = null!;
    private TextBox _bodyEditor = null!;
    private Label _bodyInfo = null!;
    private RoundedButton _openFolderButton = null!;
    private string? _pendingImport;
    private string _loadedBody = "";

    public AlarmEntry? Result { get; private set; }
    public bool DeleteRequested { get; private set; }

    public AlarmEditForm(PluginTheme theme, AlarmEntry? existing = null)
    {
        _theme = theme;
        _existing = existing;
        _target = this;
        _alarmId = existing?.Id ?? Guid.NewGuid().ToString("N");
        bool isEdit = existing != null;

        Text = isEdit ? "Edit Alarm" : "New Alarm";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(590, isEdit ? 820 : 760);
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 9.5f);
        Icon = IconHelper.CreateAppIcon(theme.Primary);
        AutoScaleMode = AutoScaleMode.Dpi;

        int pad = 20;
        int y = pad;
        int labelW = 120;
        int inputX = pad + labelW;
        _layoutW = ClientSize.Width - TabInset;
        int inputW = _layoutW - inputX - pad;

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
        Add(header);

        var toggleCaption = new Label
        {
            Text = "Enabled",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(ClientSize.Width - pad - 46 - 70, y + 8),
            BackColor = Color.Transparent,
        };
        Add(toggleCaption);

        _enabledToggle = new ToggleSwitch(theme)
        {
            Location = new Point(ClientSize.Width - pad - 46, y + 6),
            Checked = existing == null || existing.Status == AlarmStatus.Active,
        };
        Add(_enabledToggle);
        y += 40;

        // --- Tabs: General (everything below) and Body ---
        _tabs = new TabControl
        {
            Location = new Point(8, y),
            Width = ClientSize.Width - 16,
            Height = 400,
            SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(110, 28),
            DrawMode = TabDrawMode.OwnerDrawFixed,
            Font = new Font("Segoe UI", 9.5f),
        };
        _tabs.DrawItem += DrawTab;
        var generalPage = new TabPage("General") { BackColor = theme.BgDark, ForeColor = theme.TextPrimary };
        var bodyPage = new TabPage("Body") { BackColor = theme.BgDark, ForeColor = theme.TextPrimary };
        _tabs.TabPages.Add(generalPage);
        _tabs.TabPages.Add(bodyPage);
        Controls.Add(_tabs);
        int tabsTop = y;
        _target = generalPage;
        y = 12;

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
        Add(_timePicker);
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
        Add(_datePicker);
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
        Add(_dayOfMonthPicker);
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
        Add(_intervalPicker);
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
        Add(_customDaysPanel);
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
        Add(_startDateCheck);
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
        Add(_startDatePicker);
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
        Add(_endDateCheck);
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
        Add(_endDatePicker);
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
        Add(_snoozePicker);
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
        Add(_soundCheck);

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
        Add(_fireAndForgetCheck);
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

        // --- Body tab, then back to the form for the buttons ---
        int pageH = Math.Max(y, 420);
        _tabs.Height = pageH + _tabs.ItemSize.Height + 10;
        _target = bodyPage;
        BuildBodyPage(pageH);
        _target = this;
        y = tabsTop + _tabs.Height + 8;

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
        Add(_validationLabel);
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
        Add(saveButton);

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
        Add(cancelButton);

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
            Add(deleteButton);
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
        Add(hdr);
        var rule = new Panel
        {
            BackColor = _theme.Border,
            Location = new Point(20, y + 22),
            Size = new Size(_layoutW - 40, 1),
        };
        Add(rule);
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
        Add(lbl);
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
        Add(tb);
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
        Add(cb);
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
            MaximumSize = new Size(_layoutW - 160, 0),
        };
        Add(val);
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

        var result = new AlarmEntry
        {
            Id = _alarmId,
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
            BodyKind = _existing?.BodyKind ?? AlarmBodyKind.Text,
            BodyPath = _existing?.BodyPath,
        };

        try
        {
            result = ApplyBodyEdits(result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _tabs.SelectedIndex = 1;
            _validationLabel.Text = ex.Message;
            return;
        }

        Result = result;
        DialogResult = DialogResult.OK;
        Close();
    }

    // ─────────────────────────── Body tab ───────────────────────────

    private void Add(Control c) => _target.Controls.Add(c);

    private void DrawTab(object? sender, DrawItemEventArgs e)
    {
        bool selected = e.Index == _tabs.SelectedIndex;
        using var back = new SolidBrush(selected ? _theme.BgDark : _theme.BgHeader);
        e.Graphics.FillRectangle(back, e.Bounds);
        TextRenderer.DrawText(e.Graphics, _tabs.TabPages[e.Index].Text, _tabs.Font, e.Bounds,
            selected ? _theme.Primary : _theme.TextSecondary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void BuildBodyPage(int pageH)
    {
        int pad = 20;
        int y = 12;
        int w = _layoutW - pad * 2;

        AddLabel("Kind", pad, y);
        _bodyKindCombo = MakeCombo(pad + 60, y, 140);
        _bodyKindCombo.Items.AddRange(new object[] { "Text", "Markdown", "Html" });
        _bodyKindCombo.SelectedIndex = (int)(_existing?.BodyKind ?? AlarmBodyKind.Text);

        _bodyInfo = new Label
        {
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = _theme.TextSecondary,
            AutoSize = false,
            AutoEllipsis = true,
            Location = new Point(pad + 215, y + 5),
            Size = new Size(w - 215, 20),
            BackColor = Color.Transparent,
        };
        Add(_bodyInfo);
        y += 36;

        int buttonsY = pageH - 44;
        _bodyEditor = new TextBox
        {
            Multiline = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Cascadia Code", 9.5f),
            BackColor = _theme.BgHeader,
            ForeColor = _theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(pad, y),
            Size = new Size(w, buttonsY - y - 10),
        };
        if (_existing != null && _existing.BodyKind != AlarmBodyKind.Text)
        {
            try { _loadedBody = AlarmStore.Bodies.ReadBody(_existing) ?? ""; }
            catch (Exception ex) { _loadedBody = ""; PluginLog.Warn($"Could not read the alarm body: {ex.Message}"); }
        }
        _bodyEditor.Text = _loadedBody.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        _loadedBody = _bodyEditor.Text;
        _bodyEditor.TextChanged += (_, _) => { if (_bodyEditor.Focused) _pendingImport = null; UpdateBodyInfo(); };
        Add(_bodyEditor);

        var chooseButton = MakeBodyButton("Choose file...", pad, buttonsY, 120);
        chooseButton.Click += (_, _) => ChooseBodyFile();
        _openFolderButton = MakeBodyButton("Open folder", pad + 130, buttonsY, 110);
        _openFolderButton.Click += (_, _) =>
        {
            if (AlarmStore.Bodies.HasFolder(_alarmId)) AlarmRingForm.OpenFolder(AlarmStore.Bodies.FolderFor(_alarmId));
        };
        var previewButton = MakeBodyButton("Preview", pad + w - 100, buttonsY, 100);
        previewButton.Click += (_, _) => ShowBodyPreview();

        _bodyKindCombo.SelectedIndexChanged += (_, _) => UpdateBodyInfo();
        UpdateBodyInfo();
    }

    private RoundedButton MakeBodyButton(string text, int x, int y, int width)
    {
        var b = new RoundedButton
        {
            Text = text,
            Font = new Font("Segoe UI", 9.5f),
            Size = new Size(width, 34),
            Location = new Point(x, y),
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.PrimaryDim,
            ForeColor = _theme.TextPrimary,
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = _theme.Primary;
        Add(b);
        return b;
    }

    private AlarmBodyKind SelectedBodyKind => (AlarmBodyKind)Math.Max(0, _bodyKindCombo.SelectedIndex);

    private void UpdateBodyInfo()
    {
        var kind = SelectedBodyKind;
        _bodyEditor.Enabled = kind != AlarmBodyKind.Text;
        _openFolderButton.Enabled = AlarmStore.Bodies.HasFolder(_alarmId);
        _bodyInfo.Text = kind == AlarmBodyKind.Text
            ? "A text alarm shows its Message (General tab)."
            : _pendingImport != null
                ? $"Copied in on save: {Path.GetFileName(_pendingImport)} and the folders beside it"
                : $"Saved as alarms/{_alarmId[..Math.Min(8, _alarmId.Length)]}.../{AlarmBodyFiles.FileNameFor(kind)}";
    }

    private void ChooseBodyFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Choose the alarm body",
            Filter = "Markdown or HTML (*.md;*.markdown;*.html;*.htm)|*.md;*.markdown;*.html;*.htm|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var kind = AlarmBodyFiles.KindFromExtension(dlg.FileName);
            if (kind is AlarmBodyKind.Markdown or AlarmBodyKind.Html) _bodyKindCombo.SelectedIndex = (int)kind.Value;
            else if (SelectedBodyKind == AlarmBodyKind.Text) _bodyKindCombo.SelectedIndex = (int)AlarmBodyKind.Html;
            _bodyEditor.Text = File.ReadAllText(dlg.FileName).Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            _pendingImport = dlg.FileName;
            UpdateBodyInfo();
        }
        catch (Exception ex)
        {
            _validationLabel.Text = $"Could not read the file: {ex.Message}";
        }
    }

    private void ShowBodyPreview()
    {
        var kind = SelectedBodyKind;
        var alarm = (_existing ?? new AlarmEntry { Id = _alarmId }) with
        {
            Title = string.IsNullOrWhiteSpace(_titleBox.Text) ? "(no title)" : _titleBox.Text.Trim(),
            Message = _messageBox.Text.Trim(),
            Priority = (AlarmPriority)(_priorityCombo.SelectedItem ?? AlarmPriority.Normal),
        };
        string? content = kind == AlarmBodyKind.Text ? alarm.Message : _bodyEditor.Text;
        try { AlarmRingLauncher.ShowPreview(alarm, kind, content, this); }
        catch (Exception ex) { _validationLabel.Text = $"Preview failed: {ex.Message}"; }
    }

    /// <summary>Writes the body tab's content into the alarm's folder on save.</summary>
    private AlarmEntry ApplyBodyEdits(AlarmEntry result)
    {
        var files = AlarmStore.Bodies;
        var kind = SelectedBodyKind;
        var before = _existing?.BodyKind ?? AlarmBodyKind.Text;

        if (kind == AlarmBodyKind.Text)
            return before == AlarmBodyKind.Text ? result : files.WriteBody(result, AlarmBodyKind.Text, result.Message);

        if (_pendingImport != null)
            result = files.ImportBodyFile(result, kind, _pendingImport);
        else if (kind != before || _bodyEditor.Text != _loadedBody)
        {
            if (string.IsNullOrWhiteSpace(_bodyEditor.Text))
                throw new InvalidOperationException("The body is empty - write it or choose a file.");
            result = files.WriteBody(result, kind, _bodyEditor.Text);
        }

        if (string.IsNullOrWhiteSpace(result.Message))
            result = result with { Message = AlarmBodyFiles.SummaryOf(kind, _bodyEditor.Text) };
        return result;
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
