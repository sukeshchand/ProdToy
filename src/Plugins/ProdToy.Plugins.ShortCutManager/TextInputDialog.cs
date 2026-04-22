using System.Drawing;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Tiny modal prompt for a single-line text input, themed to match the host.
/// Used for "New folder" and "Rename folder" in the shortcuts form.
/// </summary>
class TextInputDialog : Form
{
    private readonly TextBox _input;
    public string Value => _input.Text.Trim();

    public TextInputDialog(PluginTheme theme, string title, string prompt, string initial = "")
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(420, 180);
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;

        int pad = 18;

        var promptLabel = new Label
        {
            Text = prompt,
            Font = new Font("Segoe UI", 10f),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, pad),
            BackColor = Color.Transparent,
        };
        Controls.Add(promptLabel);

        _input = new TextBox
        {
            Font = new Font("Segoe UI", 10f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Size = new Size(ClientSize.Width - pad * 2, 26),
            Location = new Point(pad, pad + 26),
            Text = initial,
        };
        _input.SelectAll();
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Commit(); }
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
        };
        Controls.Add(_input);

        var ok = new RoundedButton
        {
            Text = "OK",
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            Size = new Size(90, 32),
            Location = new Point(ClientSize.Width - pad - 90, pad + 70),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.Primary,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
        };
        ok.FlatAppearance.BorderSize = 0;
        ok.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        ok.Click += (_, _) => Commit();
        Controls.Add(ok);

        var cancel = new RoundedButton
        {
            Text = "Cancel",
            Font = new Font("Segoe UI", 9.5f),
            Size = new Size(80, 32),
            Location = new Point(ClientSize.Width - pad - 90 - 10 - 80, pad + 70),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextSecondary,
            Cursor = Cursors.Hand,
        };
        cancel.FlatAppearance.BorderSize = 0;
        cancel.FlatAppearance.MouseOverBackColor = theme.Primary;
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(cancel);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void Commit()
    {
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>Convenience helper: prompts and returns the trimmed value, or null if cancelled / empty.</summary>
    public static string? Prompt(IWin32Window owner, PluginTheme theme, string title, string prompt, string initial = "")
    {
        using var dlg = new TextInputDialog(theme, title, prompt, initial);
        if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
        var v = dlg.Value;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
