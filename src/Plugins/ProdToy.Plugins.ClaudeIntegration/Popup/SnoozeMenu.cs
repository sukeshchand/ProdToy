using System.Drawing;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>
/// The Snooze menu, the same in the popup's footer and on the settings page: 10 · 20 · 30 · 60 minutes, the last custom length
/// once one was used, More… and, while snoozed, "Snoozed until hh:mm - Wake now". It writes
/// <see cref="ClaudePluginSettings.SnoozeUntil"/> and nothing else.
/// </summary>
static class SnoozeMenu
{
    /// <param name="snoozed">Called after a length was picked (the popup closes then).</param>
    /// <param name="woke">Called after Wake now.</param>
    public static ContextMenuStrip Build(IPluginContext context, PluginTheme theme, IWin32Window? owner, Action snoozed, Action woke)
    {
        var settings = context.LoadSettings<ClaudePluginSettings>();
        var now = DateTime.Now;
        var menu = new ContextMenuStrip { ShowImageMargin = false };

        void Snooze(int minutes, bool custom)
        {
            var s = context.LoadSettings<ClaudePluginSettings>();
            s = s with { SnoozeUntil = SnoozeMath.Until(DateTime.Now, minutes) };
            if (custom) s = s with { SnoozeLastMinutes = SnoozeMath.Clamp(minutes) };
            context.SaveSettings(s);
            snoozed();
        }

        foreach (int minutes in SnoozeMath.Presets)
            menu.Items.Add(SnoozeMath.PresetText(minutes), null, (_, _) => Snooze(minutes, custom: false));

        if (SnoozeMath.LastText(settings.SnoozeLastMinutes) is { } last)
        {
            int minutes = settings.SnoozeLastMinutes;
            menu.Items.Add(last, null, (_, _) => Snooze(minutes, custom: true));
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("More…", null, (_, _) =>
        {
            using var dialog = new SnoozeDialog(theme, context.LoadSettings<ClaudePluginSettings>().SnoozeLastMinutes);
            if (dialog.ShowDialog(owner) == DialogResult.OK) Snooze(dialog.Minutes, custom: true);
        });

        if (SnoozeMath.WakeText(settings.SnoozeUntil, now) is { } wake)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(wake, null, (_, _) =>
            {
                var s = context.LoadSettings<ClaudePluginSettings>();
                context.SaveSettings(s with { SnoozeUntil = SnoozeMath.Awake });
                woke();
            });
        }

        menu.Closed += (_, _) =>
        {
            // Disposed after the click has run.
            var timer = new System.Windows.Forms.Timer { Interval = 1 };
            timer.Tick += (_, _) =>
            {
                timer.Dispose();
                menu.Dispose();
            };
            timer.Start();
        };
        return menu;
    }

    /// <summary>Opens the menu under a button.</summary>
    public static void ShowUnder(Control button, IPluginContext context, PluginTheme theme, Action snoozed, Action woke)
    {
        var menu = Build(context, theme, button.FindForm(), snoozed, woke);
        menu.Show(button, new Point(0, button.Height));
    }
}

/// <summary>More…: a number with minutes or hours, quick choices 2 h · 4 h · 8 h · 1 day; from 1 minute to 1 day.</summary>
sealed class SnoozeDialog : Form
{
    private readonly TextBox _number;
    private readonly ComboBox _unit;
    private readonly Label _error;

    public int Minutes { get; private set; }

    public SnoozeDialog(PluginTheme theme, int lastMinutes)
    {
        Text = "Snooze for…";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(340, 170);

        var label = new Label { Text = "Snooze notifications for", AutoSize = true, Location = new Point(16, 16), ForeColor = theme.TextSecondary };

        bool hours = lastMinutes >= 60 && lastMinutes % 60 == 0;
        _number = new TextBox
        {
            Text = lastMinutes >= SnoozeMath.MinMinutes ? (hours ? lastMinutes / 60 : lastMinutes).ToString() : "2",
            Location = new Point(16, 42),
            Width = 80,
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
        };
        _unit = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(104, 41),
            Width = 100,
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
        };
        _unit.Items.AddRange(new object[] { "minutes", "hours" });
        _unit.SelectedIndex = lastMinutes >= SnoozeMath.MinMinutes && !hours ? 0 : 1;

        _error = new Label { AutoSize = true, Location = new Point(16, 72), ForeColor = theme.ErrorColor, Text = "" };

        Controls.AddRange(new Control[] { label, _number, _unit, _error });

        int x = 16;
        foreach (int minutes in SnoozeMath.QuickChoices)
        {
            var chip = Flat(theme, SnoozeMath.Words(minutes), theme.PrimaryDim, theme.TextPrimary);
            chip.Location = new Point(x, 94);
            chip.Size = new Size(64, 26);
            chip.Click += (_, _) => Finish(minutes);
            Controls.Add(chip);
            x += 72;
        }

        var ok = Flat(theme, "OK", theme.Primary, Color.White);
        ok.Bounds = new Rectangle(ClientSize.Width - 16 - 80 - 8 - 80, 132, 80, 28);
        ok.Click += (_, _) => TryFinish();
        var cancel = Flat(theme, "Cancel", theme.BgHeader, theme.TextPrimary);
        cancel.Bounds = new Rectangle(ClientSize.Width - 16 - 80, 132, 80, 28);
        cancel.DialogResult = DialogResult.Cancel;
        Controls.Add(ok);
        Controls.Add(cancel);

        AcceptButton = ok;   // Enter = OK
        CancelButton = cancel;
        _number.TextChanged += (_, _) => _error.Text = "";
        _unit.SelectedIndexChanged += (_, _) => _error.Text = "";
    }

    private static RoundedButton Flat(PluginTheme theme, string text, Color back, Color fore)
    {
        var button = new RoundedButton
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = fore,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 9f),
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        return button;
    }

    private void TryFinish()
    {
        var unit = _unit.SelectedIndex == 1 ? SnoozeUnit.Hours : SnoozeUnit.Minutes;
        if (!SnoozeMath.TryParse(_number.Text, unit, out int minutes))
        {
            _error.Text = "From 1 minute up to 1 day (24 hours).";
            _number.Focus();
            _number.SelectAll();
            return;
        }

        Finish(minutes);
    }

    private void Finish(int minutes)
    {
        Minutes = minutes;
        DialogResult = DialogResult.OK;
        Close();
    }
}
