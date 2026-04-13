using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ProdToy.Setup;

class UninstallForm : Form
{
    private static readonly PopupTheme _theme = Themes.Default;

    private readonly RoundedButton _uninstallButton;
    private readonly RoundedButton _cancelButton;
    private readonly Label _statusLabel;

    private string? _cleanupBatPath;
    private bool _uninstalled;

    public UninstallForm()
    {
        string displayedVersion = AppRegistry.GetInstalledVersion() ?? AppVersion.Current;

        Text = "ProdToy - Uninstall";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = _theme.BgDark;
        Icon = SystemIcons.Warning;

        // --- Header ---
        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 90,
            BackColor = _theme.BgHeader,
        };

        var titleLabel = new Label
        {
            Text = "Uninstall ProdToy",
            Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold),
            ForeColor = _theme.TextPrimary,
            AutoSize = true,
            Location = new Point(28, 14),
            BackColor = Color.Transparent,
        };

        var descLabel = new Label
        {
            Text = "This will remove ProdToy and clean up Claude Code hook entries.",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = _theme.TextSecondary,
            AutoSize = false,
            Size = new Size(430, 36),
            Location = new Point(28, 48),
            BackColor = Color.Transparent,
        };

        var accentLine = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 2,
            BackColor = _theme.ErrorColor,
        };

        headerPanel.Controls.AddRange(new Control[] { titleLabel, descLabel, accentLine });

        int y = 106;

        var versionLabel = new Label
        {
            Text = $"Version:  {displayedVersion}",
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(96, 165, 250),
            AutoSize = true,
            Location = new Point(28, y),
            BackColor = Color.Transparent,
        };
        y += 24;

        var locationLabel = new Label
        {
            Text = $"Location:  {AppPaths.Root}",
            Font = new Font("Segoe UI", 9f),
            ForeColor = _theme.TextSecondary,
            AutoSize = true,
            Location = new Point(28, y),
            BackColor = Color.Transparent,
        };
        y += 28;

        var noteLabel = new Label
        {
            Text = "Your response history, settings, and plugin data will be kept.",
            Font = new Font("Segoe UI", 9f),
            ForeColor = _theme.TextSecondary,
            AutoSize = true,
            Location = new Point(28, y),
            BackColor = Color.Transparent,
        };
        y += 28;

        _statusLabel = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 9f),
            ForeColor = _theme.TextSecondary,
            AutoSize = false,
            Size = new Size(430, 36),
            Location = new Point(28, y),
            BackColor = Color.Transparent,
        };

        int buttonY = y + 44;
        int formWidth = 480;

        var buttonSep = new Panel
        {
            Size = new Size(formWidth, 1),
            Location = new Point(0, buttonY),
            BackColor = Color.FromArgb(40, 255, 255, 255),
        };

        _cancelButton = new RoundedButton
        {
            Text = "Cancel",
            Font = new Font("Segoe UI", 10f),
            Size = new Size(110, 38),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(40, 255, 255, 255),
            ForeColor = _theme.TextSecondary,
            Cursor = Cursors.Hand,
        };
        _cancelButton.Location = new Point(formWidth - _cancelButton.Width - 28, buttonY + 12);
        _cancelButton.FlatAppearance.BorderSize = 0;
        _cancelButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 255, 255, 255);
        _cancelButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(30, 255, 255, 255);
        _cancelButton.Click += (_, _) => Close();

        _uninstallButton = new RoundedButton
        {
            Text = "Uninstall",
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            Size = new Size(120, 38),
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.ErrorColor,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
        };
        _uninstallButton.Location = new Point(_cancelButton.Left - _uninstallButton.Width - 10, buttonY + 12);
        _uninstallButton.FlatAppearance.BorderSize = 0;
        _uninstallButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 50, 50);
        _uninstallButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(180, 40, 40);
        _uninstallButton.Click += OnUninstallClick;

        ClientSize = new Size(formWidth, buttonY + 62);

        Controls.AddRange(new Control[]
        {
            headerPanel, versionLabel, locationLabel, noteLabel,
            _statusLabel, buttonSep, _uninstallButton, _cancelButton
        });

        Shown += (_, _) =>
        {
            ShowWindow(Handle, SW_RESTORE);
            SetForegroundWindow(Handle);
            BringToFront();
            Activate();
        };
    }

    private async void OnUninstallClick(object? sender, EventArgs e)
    {
        _uninstallButton.Enabled = false;
        _uninstallButton.Text = "Uninstalling...";
        _cancelButton.Enabled = false;
        _statusLabel.Text = "Closing running instances...";
        _statusLabel.ForeColor = _theme.TextSecondary;

        try
        {
            var result = await Task.Run(() =>
            {
                int currentPid = Environment.ProcessId;
                foreach (var proc in Process.GetProcessesByName("ProdToy"))
                {
                    if (proc.Id == currentPid) continue;
                    try
                    {
                        proc.Kill();
                        proc.WaitForExit(3000);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Could not kill ProdToy PID {proc.Id}: {ex.Message}");
                    }
                }

                return Uninstaller.Run(out _cleanupBatPath);
            });

            if (result.Success)
            {
                _uninstallButton.Text = "Uninstalled";
                _statusLabel.Text = "ProdToy has been uninstalled.";
                _statusLabel.ForeColor = _theme.SuccessColor;
                _cancelButton.Enabled = true;
                _cancelButton.Text = "Close";
                _uninstalled = true;
            }
            else
            {
                _statusLabel.Text = $"Failed: {result.Message}";
                _statusLabel.ForeColor = _theme.ErrorColor;
                _uninstallButton.Text = "Retry";
                _uninstallButton.BackColor = _theme.ErrorColor;
                _uninstallButton.Enabled = true;
                _cancelButton.Enabled = true;
            }
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Error: {ex.Message}";
            _statusLabel.ForeColor = _theme.ErrorColor;
            _uninstallButton.Text = "Retry";
            _uninstallButton.Enabled = true;
            _cancelButton.Enabled = true;
        }
    }

    private void FinishAndExit()
    {
        if (_cleanupBatPath != null)
            Uninstaller.LaunchCleanupScript(_cleanupBatPath);
        Environment.Exit(0);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        if (_uninstalled)
            FinishAndExit();
    }

    private const int SW_RESTORE = 9;
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
