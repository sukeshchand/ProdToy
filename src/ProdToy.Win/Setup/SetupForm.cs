using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProdToy;

class SetupForm : Form
{
    private static readonly PopupTheme _theme = Themes.Default;

    private readonly string _toolsDir;
    private readonly string _installExePath;
    private readonly string _hooksDir;
    private readonly string _settingsPath;
    private readonly string _currentExePath;
    private readonly string _ps1Content;
    private readonly string _hookCommand;

    private readonly bool _repairMode;
    private readonly RoundedButton _installButton;
    private readonly RoundedButton _cancelButton;
    private readonly Label _statusLabel;

    public SetupForm(bool repair = false)
    {
        _repairMode = repair;

        _toolsDir = AppPaths.Root;
        _installExePath = AppPaths.ExePath;
        _hooksDir = AppPaths.ClaudeHooksDir;
        _settingsPath = AppPaths.ClaudeSettingsFile;
        _currentExePath = Application.ExecutablePath;

        string hooksDirJsonEscaped = _hooksDir.Replace("\\", "\\\\");
        _hookCommand = $"powershell.exe -ExecutionPolicy Bypass -File \\\"{hooksDirJsonEscaped}\\\\Show-ProdToy.ps1\\\"";

        _ps1Content = LoadEmbeddedHookScript(_installExePath);

        // --- Determine mode: install / update / repair / downgrade ---
        string? installedVersion = repair ? AppRegistry.GetInstalledVersion() : null;
        string setupVersion = AppVersion.Current;
        bool isUpgrade = false;
        bool isDowngrade = false;

        if (installedVersion != null)
        {
            try
            {
                var installed = new Version(installedVersion);
                var setup = new Version(setupVersion);
                isUpgrade = setup > installed;
                isDowngrade = setup < installed;
            }
            catch { }
        }

        // Mode-specific text
        string formTitle, heading, description, buttonText;
        if (!repair)
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
            description = $"A newer version is available. Update from {installedVersion} to {setupVersion}.";
            buttonText = "Update";
        }
        else if (isDowngrade)
        {
            formTitle = "ProdToy - Downgrade";
            heading = "Downgrade ProdToy";
            description = $"This will downgrade from {installedVersion} to {setupVersion}.";
            buttonText = "Downgrade";
        }
        else
        {
            formTitle = "ProdToy - Repair Installation";
            heading = "Repair ProdToy";
            description = "Reinstall ProdToy to repair files and hooks.";
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

        // --- Version info (styled with highlight) ---
        var versionColor = Color.FromArgb(96, 165, 250); // bright blue for version numbers
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
            Text = installedVersion != null ? $"New version:  {setupVersion}" : $"Version:  {setupVersion}",
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
            ForeColor = versionColor,
            AutoSize = true,
            Location = new Point(28, infoY),
            BackColor = Color.Transparent,
        };
        infoY += 24;

        // --- Install location label ---
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

        // --- Downgrade warning ---
        Label? warningLabel = null;
        if (isDowngrade)
        {
            warningLabel = new Label
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

        // --- Status label (shown during/after install) ---
        _statusLabel = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 9f),
            ForeColor = _theme.TextSecondary,
            AutoSize = false,
            Size = new Size(430, 36),
            Location = new Point(28, infoY),
            BackColor = Color.Transparent,
        };

        // --- Buttons (absolute positioned at bottom) ---
        int buttonY = infoY + 44;
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

        // --- Set form size to fit all content ---
        ClientSize = new Size(formWidth, buttonY + 62);

        Controls.AddRange(new Control[] { headerPanel, setupVersionLabel, locationLabel, _statusLabel, buttonSep, _installButton, _cancelButton });

        Shown += (_, _) =>
        {
            NativeMethods.ShowWindow(Handle, NativeMethods.SW_RESTORE);
            NativeMethods.SetForegroundWindow(Handle);
            BringToFront();
            Activate();
        };
    }

    private async void OnInstallClick(object? sender, EventArgs e)
    {
        _installButton.Enabled = false;
        _installButton.Text = "Installing...";
        _installButton.BackColor = _theme.PrimaryDim;
        _cancelButton.Enabled = false;
        _statusLabel.Text = "Installing...";
        _statusLabel.ForeColor = _theme.TextSecondary;

        try
        {
            var result = await Task.Run(() => RunAutoInstall());

            if (result.Success)
            {
                _installButton.Text = "Installed";
                _installButton.BackColor = _theme.SuccessColor;
                _statusLabel.Text = "Installation complete. Restart Claude Code for hooks to take effect.";
                _statusLabel.ForeColor = _theme.SuccessColor;
                _cancelButton.Enabled = true;
                _cancelButton.Text = "Close";

                // Show welcome/update screen and launch the installed app quietly
                Hide();
                using (var welcome = new WelcomeForm(isUpdate: _repairMode))
                    welcome.ShowDialog();
                try
                {
                    // Write hidden-start marker so launched app skips the popup
                    // (use a different marker than _updated.marker to avoid double welcome)
                    File.WriteAllText(Path.Combine(_toolsDir, "_start_hidden.marker"), "");
                    Process.Start(_installExePath);
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

    private record InstallResult(bool Success, string Message);

    private InstallResult RunAutoInstall()
    {
        var log = new StringBuilder();
        string? backupPath = null;

        try
        {
            // Step 1: Kill any running ProdToy instances (except this one)
            int currentPid = Environment.ProcessId;
            foreach (var proc in Process.GetProcessesByName("ProdToy"))
            {
                if (proc.Id == currentPid) continue;
                try
                {
                    proc.Kill();
                    proc.WaitForExit(3000);
                    log.AppendLine($"Closed running ProdToy (PID {proc.Id}).");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Could not kill ProdToy PID {proc.Id}: {ex.Message}");
                }
            }

            // Step 2: Copy exe to install dir
            Directory.CreateDirectory(_toolsDir);
            File.Copy(_currentExePath, _installExePath, overwrite: true);
            log.AppendLine($"Installed to {_installExePath}");

            // Step 2b: Copy bundled plugins (if present next to installer exe)
            try
            {
                string sourceDir = Path.GetDirectoryName(_currentExePath)!;
                string sourcePluginsDir = Path.Combine(sourceDir, "plugins");
                if (Directory.Exists(sourcePluginsDir))
                {
                    string destPluginsDir = AppPaths.PluginsBinDir;
                    Directory.CreateDirectory(destPluginsDir);
                    int pluginCount = 0;
                    foreach (var pluginDir in Directory.GetDirectories(sourcePluginsDir))
                    {
                        string pluginName = Path.GetFileName(pluginDir);
                        string destDir = Path.Combine(destPluginsDir, pluginName);
                        Directory.CreateDirectory(destDir);
                        foreach (var file in Directory.GetFiles(pluginDir))
                            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
                        pluginCount++;
                    }
                    log.AppendLine($"Installed {pluginCount} bundled plugin(s)");
                }
            }
            catch (Exception ex)
            {
                log.AppendLine($"Plugin install warning: {ex.Message}");
            }

            // Step 3: Apply defaults from defaultSettings.json (if present next to installer)
            ApplyDefaultSettings(log);

            // Step 4: Create hooks directory and write PS1 script
            Directory.CreateDirectory(_hooksDir);
            string ps1Path = Path.Combine(_hooksDir, "Show-ProdToy.ps1");
            File.WriteAllText(ps1Path, _ps1Content, Encoding.UTF8);
            log.AppendLine($"Hook script created at {ps1Path}");

            // Step 5: Backup and merge settings.json
            if (File.Exists(_settingsPath))
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                backupPath = Path.Combine(
                    Path.GetDirectoryName(_settingsPath)!,
                    $"settings.backup_{timestamp}.json"
                );
                File.Copy(_settingsPath, backupPath, overwrite: false);
                log.AppendLine($"Settings backed up to {backupPath}");
            }

            MergeHooksIntoSettings();
            log.AppendLine($"Hooks configured in {_settingsPath}");

            // Step 6: Register in Windows "Apps & Features"
            try
            {
                AppRegistry.Register();
                log.AppendLine("Registered in Windows Apps & Features.");
            }
            catch (Exception ex)
            {
                log.AppendLine($"Note: Could not register in Apps & Features: {ex.Message}");
            }

            return new InstallResult(true, log.ToString());
        }
        catch (Exception ex)
        {
            log.AppendLine($"Error: {ex.Message}");
            if (backupPath != null)
                log.AppendLine($"Original settings backed up at {backupPath}");
            return new InstallResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Reads defaultSettings.json from the directory where the installer exe is located.
    /// If present, merges its values into the app settings as defaults.
    /// </summary>
    private static void ApplyDefaultSettings(StringBuilder log)
    {
        try
        {
            string sourceDir = Path.GetDirectoryName(Application.ExecutablePath)!;
            string defaultSettingsPath = Path.Combine(sourceDir, "defaultSettings.json");

            if (!File.Exists(defaultSettingsPath)) return;

            string json = File.ReadAllText(defaultSettingsPath);
            var defaults = JsonSerializer.Deserialize<AppSettingsData>(json);
            if (defaults == null) return;

            var current = AppSettings.Load();

            // Only apply defaults for values that are not already set by the user
            var merged = current with
            {
                UpdateLocation = string.IsNullOrWhiteSpace(current.UpdateLocation)
                    ? (string.IsNullOrWhiteSpace(defaults.UpdateLocation)
                        ? AppSettingsData.DefaultUpdateLocation
                        : defaults.UpdateLocation)
                    : current.UpdateLocation,
            };

            AppSettings.Save(merged);
            log.AppendLine($"Applied defaults from {defaultSettingsPath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to apply default settings: {ex.Message}");
        }
    }

    private void MergeHooksIntoSettings()
    {
        JsonNode root;
        if (File.Exists(_settingsPath))
        {
            string existing = File.ReadAllText(_settingsPath);
            root = JsonNode.Parse(existing) ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        var hooksNode = root["hooks"]?.AsObject() ?? new JsonObject();

        // Build the hook command entry
        var popupHookEntry = new JsonObject
        {
            ["type"] = "command",
            ["command"] = _hookCommand.Replace("\\\\", "\\").Replace("\\\"", "\"")
        };

        // Merge UserPromptSubmit hook (captures user question)
        MergeHookEvent(hooksNode, "UserPromptSubmit", null, popupHookEntry);

        // Merge Stop hook
        MergeHookEvent(hooksNode, "Stop", null, popupHookEntry);

        // Merge Notification hook
        MergeHookEvent(hooksNode, "Notification", "permission_prompt|idle_prompt|elicitation_dialog", popupHookEntry);

        root["hooks"] = hooksNode;

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(_settingsPath, root.ToJsonString(options), Encoding.UTF8);
    }

    private static void MergeHookEvent(JsonObject hooksNode, string eventName, string? matcher, JsonObject newHookEntry)
    {
        string commandValue = newHookEntry["command"]!.GetValue<string>();

        if (hooksNode[eventName] is JsonArray existingArray)
        {
            bool alreadyExists = false;
            foreach (var ruleSet in existingArray)
            {
                if (ruleSet?["hooks"] is JsonArray hooksArray)
                {
                    foreach (var hook in hooksArray)
                    {
                        if (hook?["command"]?.GetValue<string>()?.Contains("Show-ProdToy") == true)
                        {
                            alreadyExists = true;
                            break;
                        }
                    }
                }
                if (alreadyExists) break;
            }

            if (!alreadyExists)
            {
                bool added = false;
                foreach (var ruleSet in existingArray)
                {
                    if (ruleSet is not JsonObject ruleObj) continue;
                    string? existingMatcher = ruleObj["matcher"]?.GetValue<string>();
                    if (existingMatcher == matcher)
                    {
                        var hooksArray = ruleObj["hooks"]?.AsArray() ?? new JsonArray();
                        hooksArray.Add(JsonNode.Parse(newHookEntry.ToJsonString()));
                        ruleObj["hooks"] = hooksArray;
                        added = true;
                        break;
                    }
                }

                if (!added)
                {
                    var newRuleSet = new JsonObject();
                    if (matcher != null)
                        newRuleSet["matcher"] = matcher;
                    newRuleSet["hooks"] = new JsonArray { JsonNode.Parse(newHookEntry.ToJsonString()) };
                    existingArray.Add(newRuleSet);
                }
            }
        }
        else
        {
            var newRuleSet = new JsonObject();
            if (matcher != null)
                newRuleSet["matcher"] = matcher;
            newRuleSet["hooks"] = new JsonArray { JsonNode.Parse(newHookEntry.ToJsonString()) };
            hooksNode[eventName] = new JsonArray { newRuleSet };
        }
    }

    private static string LoadEmbeddedHookScript(string installExePath)
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("ProdToy.Scripts.Show-ProdToy.ps1")
            ?? throw new InvalidOperationException("Embedded hook script resource not found.");
        using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
        string template = reader.ReadToEnd();
        return template.Replace("{{EXE_PATH}}", installExePath);
    }
}
