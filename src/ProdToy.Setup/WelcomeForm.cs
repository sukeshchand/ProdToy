using System.Drawing;
using System.Runtime.InteropServices;

namespace ProdToy.Setup;

class WelcomeForm : Form
{
    private static readonly PopupTheme _theme = Themes.Default;

    public WelcomeForm(bool isUpdate)
    {
        string version = Installer.ReadBundledVersion();
        string heading = isUpdate
            ? $"ProdToy updated to version {version}"
            : "ProdToy installed successfully";

        Text = isUpdate ? "ProdToy - Updated" : "ProdToy - Welcome";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = _theme.BgDark;
        Icon = SystemIcons.Information;

        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 60,
            BackColor = _theme.BgHeader,
        };

        var titleLabel = new Label
        {
            Text = heading,
            Font = new Font("Segoe UI Semibold", 14f, FontStyle.Bold),
            ForeColor = _theme.SuccessColor,
            AutoSize = true,
            Location = new Point(28, 16),
            BackColor = Color.Transparent,
        };

        var accentLine = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 2,
            BackColor = _theme.Primary,
        };

        headerPanel.Controls.AddRange(new Control[] { titleLabel, accentLine });

        var features = new[]
        {
            "Rich popup notifications for Claude Code task completions, errors, and questions",
            "Response history with session filtering and date navigation",
            "Screenshot editor with annotations, crop, mask, rotation, and zoom",
            "Global hotkey for instant screen capture",
            "Alarm scheduling with popup and Windows notifications",
            "Auto-update from configured update location",
            "System tray integration with quick access to all features",
            "Configurable Claude Code hooks (Stop, Notification, UserPromptSubmit)",
            "Custom status line for Claude CLI",
        };

        int y = 76;
        var bulletColor = Color.FromArgb(96, 165, 250);
        var textColor = _theme.TextSecondary;

        foreach (var feature in features)
        {
            var bullet = new Label
            {
                Text = "\u2022",
                Font = new Font("Segoe UI", 10f),
                ForeColor = bulletColor,
                AutoSize = true,
                Location = new Point(28, y),
                BackColor = Color.Transparent,
            };

            var featureLabel = new Label
            {
                Text = feature,
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = textColor,
                AutoSize = false,
                Size = new Size(400, 20),
                Location = new Point(46, y + 1),
                BackColor = Color.Transparent,
            };

            Controls.AddRange(new Control[] { bullet, featureLabel });
            y += 22;
        }

        y += 12;

        var okButton = new RoundedButton
        {
            Text = "OK",
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            Size = new Size(100, 38),
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.Primary,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
        };
        okButton.Location = new Point(480 - okButton.Width - 28, y);
        okButton.FlatAppearance.BorderSize = 0;
        okButton.FlatAppearance.MouseOverBackColor = _theme.PrimaryLight;
        okButton.FlatAppearance.MouseDownBackColor = _theme.PrimaryDim;
        okButton.Click += (_, _) => Close();

        ClientSize = new Size(480, y + okButton.Height + 18);

        Controls.AddRange(new Control[] { headerPanel, okButton });

        Shown += (_, _) =>
        {
            ShowWindow(Handle, SW_RESTORE);
            SetForegroundWindow(Handle);
            BringToFront();
            Activate();
        };

        AcceptButton = okButton;
    }

    private const int SW_RESTORE = 9;
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
