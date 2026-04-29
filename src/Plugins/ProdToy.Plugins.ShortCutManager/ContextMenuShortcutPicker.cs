using System.Drawing;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Minimal modal picker shown when the Explorer context-menu invocation
/// matches multiple shortcuts for the same working directory. The user
/// double-clicks (or selects + Enter) one to launch; Cancel/Esc dismisses.
/// </summary>
class ContextMenuShortcutPicker : Form
{
    public Shortcut? Selected { get; private set; }

    public ContextMenuShortcutPicker(IReadOnlyList<Shortcut> matches, PluginTheme theme, string folderPath)
    {
        Text = "ProdToy shortcuts";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        ClientSize = new Size(420, 280);
        MinimumSize = new Size(360, 220);
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        KeyPreview = true;

        int pad = 12;

        var heading = new Label
        {
            Text = "Multiple shortcuts match this folder. Pick one:",
            AutoSize = true,
            ForeColor = theme.TextPrimary,
            BackColor = Color.Transparent,
            Location = new Point(pad, pad),
        };
        Controls.Add(heading);

        var folderLabel = new Label
        {
            Text = folderPath,
            AutoEllipsis = true,
            ForeColor = theme.TextPrimary,
            BackColor = Color.Transparent,
            Font = new Font("Consolas", 9f),
            Location = new Point(pad, pad + 22),
            Size = new Size(ClientSize.Width - pad * 2, 18),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(folderLabel);

        var list = new ListBox
        {
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            IntegralHeight = false,
            Location = new Point(pad, pad + 50),
            Size = new Size(ClientSize.Width - pad * 2, ClientSize.Height - pad * 3 - 50 - 36),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        foreach (var s in matches)
            list.Items.Add(string.IsNullOrWhiteSpace(s.Name) ? "(unnamed)" : s.Name);
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        list.DoubleClick += (_, _) => CommitAndClose(list, matches);
        Controls.Add(list);

        var launchBtn = new Button
        {
            Text = "Launch",
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.Primary,
            ForeColor = Color.White,
            UseVisualStyleBackColor = false,
            Size = new Size(100, 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            DialogResult = DialogResult.OK,
        };
        launchBtn.FlatAppearance.BorderColor = theme.Primary;
        launchBtn.Location = new Point(
            ClientSize.Width - pad - launchBtn.Width,
            ClientSize.Height - pad - launchBtn.Height);
        launchBtn.Click += (_, _) => CommitAndClose(list, matches);
        Controls.Add(launchBtn);

        var cancelBtn = new Button
        {
            Text = "Cancel",
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            UseVisualStyleBackColor = false,
            Size = new Size(100, 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            DialogResult = DialogResult.Cancel,
        };
        cancelBtn.FlatAppearance.BorderColor = theme.Border;
        cancelBtn.Location = new Point(
            launchBtn.Left - pad - cancelBtn.Width,
            launchBtn.Top);
        Controls.Add(cancelBtn);

        AcceptButton = launchBtn;
        CancelButton = cancelBtn;
    }

    private void CommitAndClose(ListBox list, IReadOnlyList<Shortcut> matches)
    {
        if (list.SelectedIndex >= 0 && list.SelectedIndex < matches.Count)
        {
            Selected = matches[list.SelectedIndex];
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
