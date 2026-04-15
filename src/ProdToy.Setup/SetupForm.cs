using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ProdToy.Setup;

/// <summary>
/// Installer / repair / update wizard. Decides mode from the Windows registry
/// plus the bundled-vs-installed version comparison, then delegates the actual
/// work to Installer.Run().
/// </summary>
class SetupForm : Form
{
    private static readonly PopupTheme _theme = Themes.Default;

    private readonly RoundedButton _installButton;
    private readonly RoundedButton _cancelButton;
    private readonly Label _statusLabel;
    private readonly TextBox _logBox;
    private readonly CheckBox? _desktopShortcutCheck;
    private readonly CheckBox? _startMenuShortcutCheck;
    private readonly bool _repairMode;

    public SetupForm()
    {
        string bundledVersion = Installer.ReadBundledVersion();
        string? installedVersion = AppRegistry.GetInstalledVersion();
        _repairMode = installedVersion != null;

        bool isUpgrade = false;
        bool isDowngrade = false;
        if (installedVersion != null)
        {
            try
            {
                var installed = new Version(installedVersion);
                var bundled = new Version(bundledVersion);
                isUpgrade = bundled > installed;
                isDowngrade = bundled < installed;
            }
            catch { }
        }

        string formTitle, heading, description, buttonText;
        if (!_repairMode)
        {
            formTitle = "ProdToy Setup";
            heading = "Install ProdToy";
            description = $"Install ProdToy to {AppPaths.Root} and configure Claude Code hooks.";
            buttonText = "Install";
        }
        else if (isUpgrade)
        {
            formTitle = "ProdToy - Update";
            heading = "Update ProdToy";
            description = $"A newer version is available. Update from {installedVersion} to {bundledVersion}.";
            buttonText = "Update";
        }
        else if (isDowngrade)
        {
            formTitle = "ProdToy - Downgrade";
            heading = "Downgrade ProdToy";
            description = $"This will downgrade from {installedVersion} to {bundledVersion}.";
            buttonText = "Downgrade";
        }
        else
        {
            formTitle = "ProdToy - Repair Installation";
            heading = "Repair ProdToy";
            description = "Reinstall ProdToy to repair files and Claude hooks.";
            buttonText = "Repair";
        }

        Text = formTitle;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = _theme.BgDark;
        Icon = SystemIcons.Information;

        // --- Header panel ---
        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 90,
            BackColor = _theme.BgHeader,
        };

        var titleLabel = new Label
        {
            Text = heading,
            Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold),
            ForeColor = _theme.TextPrimary,
            AutoSize = true,
            Location = new Point(28, 14),
            BackColor = Color.Transparent,
        };

        var descLabel = new Label
        {
            Text = description,
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
            BackColor = _theme.Primary,
        };
        headerPanel.Controls.AddRange(new Control[] { titleLabel, descLabel, accentLine });

        var versionColor = Color.FromArgb(96, 165, 250);
        int infoY = 106;

        if (installedVersion != null)
        {
            var installedLabel = new Label
            {
                Text = $"Installed version:  {installedVersion}",
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColor = versionColor,
                AutoSize = true,
                Location = new Point(28, infoY),
                BackColor = Color.Transparent,
            };
            Controls.Add(installedLabel);
            infoY += 24;
        }

        var setupVersionLabel = new Label
        {
            Text = installedVersion != null ? $"New version:  {bundledVersion}" : $"Version:  {bundledVersion}",
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
            ForeColor = versionColor,
            AutoSize = true,
            Location = new Point(28, infoY),
            BackColor = Color.Transparent,
        };
        infoY += 24;

        var locationLabel = new Label
        {
            Text = $"Location:  {AppPaths.Root}",
            Font = new Font("Segoe UI", 9f),
            ForeColor = _theme.TextSecondary,
            AutoSize = true,
            Location = new Point(28, infoY),
            BackColor = Color.Transparent,
        };
        infoY += 28;

        if (isDowngrade)
        {
            var warningLabel = new Label
            {
                Text = "Warning: This will install an older version than what is currently installed.",
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(251, 191, 36),
                AutoSize = true,
                Location = new Point(28, infoY),
                BackColor = Color.Transparent,
            };
            Controls.Add(warningLabel);
            infoY += 28;
        }

        _statusLabel = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 9f),
            ForeColor = _theme.TextSecondary,
            AutoSize = false,
            Size = new Size(484, 20),
            Location = new Point(28, infoY),
            BackColor = Color.Transparent,
        };
        infoY += 24;

        // Only offer the shortcut options on a fresh install. On repair/update
        // we leave any existing shortcuts alone.
        if (!_repairMode)
        {
            _desktopShortcutCheck = new CheckBox
            {
                Text = "Create a desktop shortcut",
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = _theme.TextPrimary,
                BackColor = Color.Transparent,
                AutoSize = true,
                Checked = true,
                Location = new Point(28, infoY),
                Cursor = Cursors.Hand,
            };
            Controls.Add(_desktopShortcutCheck);
            infoY += 26;

            _startMenuShortcutCheck = new CheckBox
            {
                Text = "Create a Start Menu shortcut",
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = _theme.TextPrimary,
                BackColor = Color.Transparent,
                AutoSize = true,
                Checked = true,
                Location = new Point(28, infoY),
                Cursor = Cursors.Hand,
            };
            Controls.Add(_startMenuShortcutCheck);
            infoY += 30;
        }

        int formWidth = 540;

        _logBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Font = new Font("Consolas", 8.5f),
            BackColor = _theme.BgHeader,
            ForeColor = _theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Size = new Size(formWidth - 56, 180),
            Location = new Point(28, infoY),
            Visible = false,
        };

        int buttonY = infoY + _logBox.Height + 14;

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

        _installButton = new RoundedButton
        {
            Text = buttonText,
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            Size = new Size(120, 38),
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.Primary,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
        };
        _installButton.Location = new Point(_cancelButton.Left - _installButton.Width - 10, buttonY + 12);
        _installButton.FlatAppearance.BorderSize = 0;
        _installButton.FlatAppearance.MouseOverBackColor = _theme.PrimaryLight;
        _installButton.FlatAppearance.MouseDownBackColor = _theme.PrimaryDim;
        _installButton.Click += OnInstallClick;

        ClientSize = new Size(formWidth, buttonY + 62);

        Controls.AddRange(new Control[] { headerPanel, setupVersionLabel, locationLabel, _statusLabel, _logBox, buttonSep, _installButton, _cancelButton });

        Shown += (_, _) =>
        {
            ShowWindow(Handle, SW_RESTORE);
            SetForegroundWindow(Handle);
            BringToFront();
            Activate();
        };
    }

    private void AppendLog(string line)
    {
        if (InvokeRequired) { BeginInvoke(new Action<string>(AppendLog), line); return; }
        _logBox.AppendText(line + Environment.NewLine);
    }

    private async void OnInstallClick(object? sender, EventArgs e)
    {
        _installButton.Enabled = false;
        _installButton.Text = "Working...";
        _installButton.BackColor = _theme.PrimaryDim;
        _cancelButton.Enabled = false;
        _statusLabel.Text = "Working — see log below...";
        _statusLabel.ForeColor = _theme.TextSecondary;
        _logBox.Visible = true;

        try
        {
            // Phase 1: ensure we have a bundle (offline sibling or GitHub download).
            string bundleDir = await BootstrapDownloader.EnsureBundleAsync(AppendLog);

            // Phase 2: run the actual installer against the resolved bundle.
            bool createDesktopShortcut = _desktopShortcutCheck?.Checked ?? false;
            bool createStartMenuShortcut = _startMenuShortcutCheck?.Checked ?? false;
            var result = await Task.Run(() => Installer.Run(
                bundleDir, AppendLog,
                createDesktopShortcut: createDesktopShortcut,
                createStartMenuShortcut: createStartMenuShortcut));

            if (result.Success)
            {
                _installButton.Text = "Done";
                _installButton.BackColor = _theme.SuccessColor;
                _statusLabel.Text = "Installation complete. Restart Claude Code for hooks to take effect.";
                _statusLabel.ForeColor = _theme.SuccessColor;
                _cancelButton.Enabled = true;
                _cancelButton.Text = "Close";

                Hide();
                using (var welcome = new WelcomeForm(isUpdate: _repairMode))
                    welcome.ShowDialog();

                try
                {
                    // Start-hidden marker tells the host to skip showing its popup.
                    File.WriteAllText(Path.Combine(AppPaths.Root, "_start_hidden.marker"), "");
                    Process.Start(AppPaths.ExePath);
                }
                catch { }
                Close();
                return;
            }
            else
            {
                _statusLabel.Text = $"Failed: {result.Message}";
                _statusLabel.ForeColor = _theme.ErrorColor;
                _installButton.Text = "Retry";
                _installButton.BackColor = _theme.Primary;
                _installButton.Enabled = true;
                _cancelButton.Enabled = true;
            }
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Error: {ex.Message}";
            _statusLabel.ForeColor = _theme.ErrorColor;
            _installButton.Text = "Retry";
            _installButton.BackColor = _theme.Primary;
            _installButton.Enabled = true;
            _cancelButton.Enabled = true;
        }
    }

    // --- Win32 window focus helpers (inlined to avoid a shared NativeMethods file) ---
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
