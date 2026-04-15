using System.Drawing;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ClaudeIntegration;

enum FilterMode { None, Cwd, Session }

/// <summary>
/// Plugin-owned Filter History dialog. Moved from the host; rewritten to
/// take the plugin's <see cref="ChatHistory"/> instance rather than the
/// host's static <c>ResponseHistory</c>, and <see cref="PluginTheme"/>
/// rather than the host's internal theme record.
/// </summary>
class FilterDialog : Form
{
    private readonly PluginTheme _theme;
    private readonly ChatHistory _history;
    private readonly RadioButton _rbCwd;
    private readonly RadioButton _rbSession;
    private readonly ListBox _listBox;
    private readonly RoundedButton _okButton;
    private readonly RoundedButton _clearButton;
    private readonly DateTimePicker _datePicker;
    private readonly RoundedButton _prevDayButton;
    private readonly RoundedButton _nextDayButton;

    private List<string> _cwdValues = new();
    private List<(string SessionId, string Cwd)> _sessionValues = new();
    private DateTime _selectedDate;
    private List<DateTime> _availableDates = new();

    public FilterMode SelectedMode { get; private set; } = FilterMode.None;
    public string SelectedValue { get; private set; } = "";
    public DateTime SelectedDate { get; private set; }

    public FilterDialog(PluginTheme theme, ChatHistory history, FilterMode currentMode, string currentValue, DateTime currentDate)
    {
        _theme = theme;
        _history = history;
        _selectedDate = currentDate.Date;
        SelectedDate = _selectedDate;
        Text = "Filter History";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        ClientSize = new Size(380, 420);
        Font = new Font("Segoe UI", 10f);

        var dateLabel = new Label
        {
            Text = "Date:",
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(16, 14),
            BackColor = Color.Transparent,
        };

        _prevDayButton = new RoundedButton
        {
            Text = "\u25C0",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Size = new Size(30, 28),
            Location = new Point(16, 38),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextSecondary,
            Cursor = Cursors.Hand,
        };
        _prevDayButton.FlatAppearance.BorderSize = 0;
        _prevDayButton.FlatAppearance.MouseOverBackColor = theme.Primary;
        _prevDayButton.Click += (_, _) => NavigateDate(-1);

        _datePicker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "ddd, dd MMM yyyy",
            Font = new Font("Segoe UI", 10f),
            Location = new Point(52, 38),
            Size = new Size(234, 28),
            Value = _selectedDate,
            MaxDate = DateTime.Today,
            CalendarForeColor = theme.TextPrimary,
            CalendarMonthBackground = theme.BgHeader,
        };
        _datePicker.ValueChanged += (_, _) => OnDateChanged(_datePicker.Value.Date);

        _nextDayButton = new RoundedButton
        {
            Text = "\u25B6",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Size = new Size(30, 28),
            Location = new Point(292, 38),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextSecondary,
            Cursor = Cursors.Hand,
        };
        _nextDayButton.FlatAppearance.BorderSize = 0;
        _nextDayButton.FlatAppearance.MouseOverBackColor = theme.Primary;
        _nextDayButton.Click += (_, _) => NavigateDate(+1);

        var groupLabel = new Label
        {
            Text = "Group by:",
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(16, 78),
            BackColor = Color.Transparent,
        };

        _rbCwd = new RadioButton
        {
            Text = "Working Folder",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(16, 102),
            Checked = currentMode == FilterMode.Cwd,
        };
        _rbCwd.CheckedChanged += (_, _) => { if (_rbCwd.Checked) PopulateList(FilterMode.Cwd); };

        _rbSession = new RadioButton
        {
            Text = "Session",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(180, 102),
            Checked = currentMode == FilterMode.Session,
        };
        _rbSession.CheckedChanged += (_, _) => { if (_rbSession.Checked) PopulateList(FilterMode.Session); };

        var listLabel = new Label
        {
            Text = "Select:",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(16, 130),
            BackColor = Color.Transparent,
        };

        _listBox = new ListBox
        {
            Location = new Point(16, 152),
            Size = new Size(348, 190),
            Font = new Font("Cascadia Code", 9f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            IntegralHeight = false,
        };
        _listBox.DoubleClick += (_, _) => AcceptSelection();

        _okButton = new RoundedButton
        {
            Text = "OK",
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            Size = new Size(100, 36),
            Location = new Point(160, 360),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.Primary,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
        };
        _okButton.FlatAppearance.BorderSize = 0;
        _okButton.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        _okButton.Click += (_, _) => AcceptSelection();

        _clearButton = new RoundedButton
        {
            Text = "Clear Filter",
            Font = new Font("Segoe UI", 9.5f),
            Size = new Size(100, 36),
            Location = new Point(268, 360),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextSecondary,
            Cursor = Cursors.Hand,
        };
        _clearButton.FlatAppearance.BorderSize = 0;
        _clearButton.FlatAppearance.MouseOverBackColor = theme.Primary;
        _clearButton.Click += (_, _) =>
        {
            SelectedMode = FilterMode.None;
            SelectedValue = "";
            SelectedDate = DateTime.Today;
            DialogResult = DialogResult.OK;
            Close();
        };

        Controls.AddRange(new Control[]
        {
            dateLabel, _prevDayButton, _datePicker, _nextDayButton,
            groupLabel, _rbCwd, _rbSession, listLabel, _listBox, _okButton, _clearButton
        });

        _availableDates = _history.GetAvailableDates();
        SetMinDate();
        LoadDataForDate();
        UpdateDayNavButtons();

        if (currentMode == FilterMode.Cwd)
            PopulateList(FilterMode.Cwd, currentValue);
        else if (currentMode == FilterMode.Session)
            PopulateList(FilterMode.Session, currentValue);
        else if (_cwdValues.Count > 0)
        {
            _rbCwd.Checked = true;
            PopulateList(FilterMode.Cwd);
        }
        else if (_sessionValues.Count > 0)
        {
            _rbSession.Checked = true;
            PopulateList(FilterMode.Session);
        }
    }

    private void SetMinDate()
    {
        if (_availableDates.Count > 0)
            _datePicker.MinDate = _availableDates[0];
        else
            _datePicker.MinDate = DateTime.Today;
    }

    private void LoadDataForDate()
    {
        _history.Invalidate();
        _cwdValues = _history.GetDistinctCwd(_selectedDate);
        _sessionValues = _history.GetDistinctSessions(_selectedDate);
    }

    private void OnDateChanged(DateTime newDate)
    {
        if (newDate.Date == _selectedDate) return;
        _selectedDate = newDate.Date;
        SelectedDate = _selectedDate;
        LoadDataForDate();
        UpdateDayNavButtons();

        if (_rbCwd.Checked)
            PopulateList(FilterMode.Cwd);
        else if (_rbSession.Checked)
            PopulateList(FilterMode.Session);
    }

    private void NavigateDate(int direction)
    {
        var newDate = _selectedDate.AddDays(direction);
        if (newDate > DateTime.Today) return;
        if (_availableDates.Count > 0 && newDate < _availableDates[0]) return;
        _datePicker.Value = newDate;
    }

    private void UpdateDayNavButtons()
    {
        _prevDayButton.Enabled = _availableDates.Count > 0 && _selectedDate > _availableDates[0];
        _nextDayButton.Enabled = _selectedDate < DateTime.Today;
    }

    private void PopulateList(FilterMode mode, string? selectValue = null)
    {
        _listBox.Items.Clear();

        if (mode == FilterMode.Cwd)
        {
            foreach (var cwd in _cwdValues)
            {
                string folder = Path.GetFileName(cwd.TrimEnd('/', '\\'));
                if (string.IsNullOrEmpty(folder)) folder = cwd;
                int count = _history.FilterByCwd(_selectedDate, cwd).Count;
                _listBox.Items.Add(new FilterItem(folder, cwd, count));
            }
        }
        else if (mode == FilterMode.Session)
        {
            foreach (var (sessionId, cwd) in _sessionValues)
            {
                string shortId = sessionId.Length > 8 ? sessionId[..8] : sessionId;
                string folder = string.IsNullOrEmpty(cwd) ? "" : Path.GetFileName(cwd.TrimEnd('/', '\\'));
                string display = string.IsNullOrEmpty(folder) ? shortId : $"{shortId} ({folder})";
                int count = _history.FilterBySession(_selectedDate, sessionId).Count;
                _listBox.Items.Add(new FilterItem(display, sessionId, count));
            }
        }

        if (selectValue != null)
        {
            for (int i = 0; i < _listBox.Items.Count; i++)
            {
                if (_listBox.Items[i] is FilterItem fi && fi.Value == selectValue)
                {
                    _listBox.SelectedIndex = i;
                    break;
                }
            }
        }

        if (_listBox.SelectedIndex < 0 && _listBox.Items.Count > 0)
            _listBox.SelectedIndex = 0;
    }

    private void AcceptSelection()
    {
        if (_listBox.SelectedItem is FilterItem fi)
        {
            SelectedMode = _rbCwd.Checked ? FilterMode.Cwd : FilterMode.Session;
            SelectedValue = fi.Value;
            SelectedDate = _selectedDate;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    private record FilterItem(string Display, string Value, int Count)
    {
        public override string ToString() => $"{Display}  ({Count})";
    }
}
