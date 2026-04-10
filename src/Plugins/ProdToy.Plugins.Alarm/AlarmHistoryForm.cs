using System.Drawing;
using ProdToy.Sdk;

namespace ProdToy.Plugins.Alarm;

class AlarmHistoryForm : Form
{
    public AlarmHistoryForm(PluginTheme theme, string? alarmId)
    {
        string title = alarmId != null
            ? $"Alarm History \u2014 {AlarmStore.GetAlarm(alarmId)?.Title ?? alarmId}"
            : "Alarm History \u2014 All";

        Text = title;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(700, 480);
        MinimumSize = new Size(500, 300);
        ShowInTaskbar = true;
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 9.5f);
        Icon = IconHelper.CreateAppIcon(theme.Primary);
        AutoScaleMode = AutoScaleMode.Dpi;

        int pad = 20;
        int y = pad;

        var titleLabel = new Label
        {
            Text = "Alarm History",
            Font = new Font("Segoe UI Semibold", 14f, FontStyle.Bold),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        Controls.Add(titleLabel);
        y += 36;

        var accentLine = new Panel
        {
            BackColor = theme.Primary,
            Location = new Point(pad, y),
            Size = new Size(ClientSize.Width - pad * 2, 2),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(accentLine);
        y += 10;

        var listView = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Font = new Font("Segoe UI", 9f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(pad, y),
            Size = new Size(ClientSize.Width - pad * 2, ClientSize.Height - y - pad),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        };
        listView.Columns.Add("Time", 150);
        listView.Columns.Add("Alarm", 160);
        listView.Columns.Add("Event", 120);
        listView.Columns.Add("Detail", 220);

        var entries = alarmId != null
            ? AlarmStore.LoadHistory(alarmId)
            : AlarmStore.LoadHistory();

        foreach (var entry in entries.OrderByDescending(e => e.Timestamp))
        {
            var item = new ListViewItem(entry.Timestamp.ToString("g"));
            item.SubItems.Add(entry.AlarmTitle);
            item.SubItems.Add(FormatEventType(entry.EventType));
            item.SubItems.Add(entry.Detail ?? "");

            item.ForeColor = entry.EventType switch
            {
                AlarmHistoryEventType.Triggered => theme.SuccessColor,
                AlarmHistoryEventType.TriggerFailed => theme.ErrorColor,
                AlarmHistoryEventType.Missed => theme.ErrorColor,
                AlarmHistoryEventType.Deleted => theme.TextSecondary,
                AlarmHistoryEventType.Snoozed => theme.Primary,
                _ => theme.TextPrimary,
            };

            listView.Items.Add(item);
        }

        Controls.Add(listView);

        var closeButton = new RoundedButton
        {
            Text = "Close",
            Font = new Font("Segoe UI", 9f),
            Size = new Size(80, 30),
            Location = new Point(ClientSize.Width - pad - 80, pad),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextSecondary,
            Cursor = Cursors.Hand,
        };
        closeButton.FlatAppearance.BorderSize = 0;
        closeButton.FlatAppearance.MouseOverBackColor = theme.Primary;
        closeButton.Click += (_, _) => Close();
        Controls.Add(closeButton);
    }

    private static string FormatEventType(AlarmHistoryEventType type)
    {
        return type switch
        {
            AlarmHistoryEventType.Created => "Created",
            AlarmHistoryEventType.Edited => "Edited",
            AlarmHistoryEventType.Triggered => "Triggered",
            AlarmHistoryEventType.PopupShown => "Popup Shown",
            AlarmHistoryEventType.NotificationShown => "Notification",
            AlarmHistoryEventType.SoundPlayed => "Sound Played",
            AlarmHistoryEventType.Dismissed => "Dismissed",
            AlarmHistoryEventType.Snoozed => "Snoozed",
            AlarmHistoryEventType.Missed => "Missed",
            AlarmHistoryEventType.Completed => "Completed",
            AlarmHistoryEventType.AutoDisabled => "Auto-Disabled",
            AlarmHistoryEventType.Deleted => "Deleted",
            AlarmHistoryEventType.TriggerFailed => "Failed",
            AlarmHistoryEventType.RestartRecovered => "Recovered",
            _ => type.ToString(),
        };
    }
}
