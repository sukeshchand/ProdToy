using System.Drawing;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ClaudeIntegration;

[Plugin("ProdToy.Plugin.ClaudeIntegration", "Claude Integration", "1.0.262",
    Description = "Claude Code hooks, status line, and auto-title integration",
    Author = "ProdToy",
    MenuPriority = 300)]
public class ClaudeIntegrationPlugin : IPlugin
{
    private IPluginContext _context = null!;

    public void Initialize(IPluginContext context)
    {
        _context = context;
        ClaudePaths.Initialize(context.Host.AppRootPath);
    }

    public void Start()
    {
        var settings = _context.LoadSettings<ClaudePluginSettings>();

        // Ensure hook script exists on disk (critical after updates or fresh install)
        EnsureHookScript();

        // Verify and sync all Claude hooks with plugin settings
        ClaudeHookManager.UpdateClaudeHook("Stop", null, settings.HookStopEnabled);
        ClaudeHookManager.UpdateClaudeHook("Notification",
            "permission_prompt|idle_prompt|elicitation_dialog", settings.HookNotificationEnabled);
        ClaudeHookManager.UpdateClaudeHook("UserPromptSubmit", null, settings.HookUserPromptEnabled);

        // Verify and sync status line
        if (ClaudeStatusLine.IsEnabled())
            ClaudeStatusLine.WriteConfig(settings);

        // Sync auto-title if enabled
        if (settings.AutoTitleToFolder)
            ClaudeHookManager.SetAutoTitleHook(true);

        // Cleanup legacy hooks
        ClaudeHookManager.CleanupOldHook();

        _context.Log("Claude integration started — hooks, script, and status line verified");
    }

    public void Stop()
    {
        // Remove ALL ProdToy hooks and integrations from Claude
        try
        {
            // Remove hook entries from settings.json
            ClaudeHookManager.UpdateClaudeHook("Stop", null, false);
            ClaudeHookManager.UpdateClaudeHook("Notification",
                "permission_prompt|idle_prompt|elicitation_dialog", false);
            ClaudeHookManager.UpdateClaudeHook("UserPromptSubmit", null, false);

            // Remove auto-title instruction from CLAUDE.md
            ClaudeHookManager.SetAutoTitleHook(false);

            // Disable and remove status line
            if (ClaudeStatusLine.IsEnabled())
                ClaudeStatusLine.Disable();

            // Delete the hook script file so Claude Code can't invoke ProdToy
            DeleteHookScript();

            _context.Log("Claude integration stopped — all hooks, script, and status line removed");
        }
        catch (Exception ex)
        {
            _context.LogError("Failed to clean up Claude hooks on stop", ex);
        }
    }

    private void EnsureHookScript()
    {
        try
        {
            // The hook script needs the host exe path
            string exePath = Path.Combine(_context.Host.AppRootPath, "ProdToy.exe");
            if (!File.Exists(exePath))
                exePath = Application.ExecutablePath;

            // Delegate to host's Updater which has the script template
            // For now, just verify the file exists
            string scriptPath = Path.Combine(ClaudePaths.ClaudeHooksDir, "Show-ProdToy.ps1");
            if (!File.Exists(scriptPath))
                _context.Log("Hook script missing — will be created by host on next startup");
        }
        catch (Exception ex)
        {
            _context.LogError("Failed to ensure hook script", ex);
        }
    }

    private static void DeleteHookScript()
    {
        try
        {
            string scriptPath = Path.Combine(ClaudePaths.ClaudeHooksDir, "Show-ProdToy.ps1");
            if (File.Exists(scriptPath))
                File.Delete(scriptPath);
        }
        catch { }
    }

    public void Dispose() { }

    public IReadOnlyList<MenuContribution> GetMenuItems() =>
    [
        new("Show Last Notification", () => _context.Host.ShowNotificationPopup(), Priority: 50),
    ];

    public IReadOnlyList<MenuContribution> GetDashboardItems() =>
    [
        new("Last Notification", () => _context.Host.ShowNotificationPopup(), Priority: 50),
    ];

    public SettingsPageContribution? GetSettingsPage() =>
        new("Claude CLI", () => BuildSettingsPanel(), TabOrder: 200);

    private Control BuildSettingsPanel()
    {
        var theme = _context.Host.CurrentTheme;
        var settings = _context.LoadSettings<ClaudePluginSettings>();
        var hostSettings = HostSettings.Load(_context.Host.AppRootPath);

        var panel = new Panel
        {
            AutoScroll = true,
            Dock = DockStyle.Fill,
            BackColor = theme.BgDark,
        };

        int pad = 16;
        int y = pad;
        int contentWidth = 700;

        // --- NOTIFICATIONS section ---
        var notifSectionLabel = new Label
        {
            Text = "NOTIFICATIONS",
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            ForeColor = theme.Primary,
            AutoSize = true,
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        panel.Controls.Add(notifSectionLabel);
        y += 26;

        bool notifEnabled = hostSettings.NotificationsEnabled;
        var notifSubControls = new List<Control>();

        var notifEnabledCheck = new CheckBox
        {
            Text = "Enable notifications",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = notifEnabled,
            AutoSize = true,
            Location = new Point(pad + 8, y),
            Cursor = Cursors.Hand,
        };
        panel.Controls.Add(notifEnabledCheck);
        y += 28;

        var notifModeLabel = new Label
        {
            Text = "Notification type:",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Enabled = notifEnabled,
            Location = new Point(pad + 8, y + 3),
            BackColor = Color.Transparent,
        };
        panel.Controls.Add(notifModeLabel);
        notifSubControls.Add(notifModeLabel);

        var notifModes = new[] { "Popup", "Windows", "Popup + Windows" };
        var notifModeCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Segoe UI", 9f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(140, 24),
            Enabled = notifEnabled,
            Location = new Point(pad + 130, y),
        };
        foreach (var mode in notifModes)
            notifModeCombo.Items.Add(mode);
        notifModeCombo.SelectedItem = notifModes.Contains(hostSettings.NotificationMode) ? hostSettings.NotificationMode : "Popup";
        notifModeCombo.SelectedIndexChanged += (_, _) =>
        {
            var hs = HostSettings.Load(_context.Host.AppRootPath);
            HostSettings.Save(_context.Host.AppRootPath, hs with { NotificationMode = notifModeCombo.SelectedItem?.ToString() ?? "Popup" });
        };
        panel.Controls.Add(notifModeCombo);
        notifSubControls.Add(notifModeCombo);
        y += 30;

        var quotesCheck = new CheckBox
        {
            Text = "Show quotes in popup header",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = hostSettings.ShowQuotes,
            Enabled = notifEnabled,
            AutoSize = true,
            Location = new Point(pad + 8, y),
            Cursor = Cursors.Hand,
        };
        quotesCheck.CheckedChanged += (_, _) =>
        {
            var hs = HostSettings.Load(_context.Host.AppRootPath);
            HostSettings.Save(_context.Host.AppRootPath, hs with { ShowQuotes = quotesCheck.Checked });
            _context.Host.NotifyShowQuotesChanged(quotesCheck.Checked);
        };
        panel.Controls.Add(quotesCheck);
        notifSubControls.Add(quotesCheck);
        y += 28;

        bool isSnoozed = _context.Host.IsSnoozed;
        var snoozeCheck = new CheckBox
        {
            Text = isSnoozed
                ? $"Snoozed ({Math.Max(1, (int)(_context.Host.SnoozeUntil - DateTime.Now).TotalMinutes)} min left)"
                : "Snooze notifications (30 min)",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = isSnoozed,
            Enabled = notifEnabled,
            AutoSize = true,
            Location = new Point(pad + 8, y),
            Cursor = Cursors.Hand,
        };
        snoozeCheck.CheckedChanged += (_, _) =>
        {
            if (snoozeCheck.Checked)
                _context.Host.Snooze();
            else
                _context.Host.Unsnooze();
        };
        panel.Controls.Add(snoozeCheck);
        notifSubControls.Add(snoozeCheck);

        notifEnabledCheck.CheckedChanged += (_, _) =>
        {
            var hs = HostSettings.Load(_context.Host.AppRootPath);
            HostSettings.Save(_context.Host.AppRootPath, hs with { NotificationsEnabled = notifEnabledCheck.Checked });
            foreach (var ctrl in notifSubControls)
                ctrl.Enabled = notifEnabledCheck.Checked;
        };
        y += 28;

        // --- CHATS section ---
        var historyCheck = new CheckBox
        {
            Text = "Save chat history",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = hostSettings.HistoryEnabled,
            AutoSize = true,
            Location = new Point(pad + 8, y),
            Cursor = Cursors.Hand,
        };
        historyCheck.CheckedChanged += (_, _) =>
        {
            var hs = HostSettings.Load(_context.Host.AppRootPath);
            HostSettings.Save(_context.Host.AppRootPath, hs with { HistoryEnabled = historyCheck.Checked });
            _context.Host.NotifyHistoryEnabledChanged(historyCheck.Checked);
        };
        panel.Controls.Add(historyCheck);
        y += 34;

        // --- HOOKS section ---
        var hooksLabel = new Label
        {
            Text = "HOOKS",
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            ForeColor = theme.Primary,
            AutoSize = true,
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        panel.Controls.Add(hooksLabel);
        y += 26;

        y = AddHookCheckbox(panel, theme, "On Stop — notify when Claude finishes a response",
            settings.HookStopEnabled, pad, y, (checked_) =>
            {
                var s = _context.LoadSettings<ClaudePluginSettings>();
                _context.SaveSettings(s with { HookStopEnabled = checked_ });
                ClaudeHookManager.UpdateClaudeHook("Stop", null, checked_);
            });

        y = AddHookCheckbox(panel, theme, "On Notification — notify on permission/idle/question prompts",
            settings.HookNotificationEnabled, pad, y, (checked_) =>
            {
                var s = _context.LoadSettings<ClaudePluginSettings>();
                _context.SaveSettings(s with { HookNotificationEnabled = checked_ });
                ClaudeHookManager.UpdateClaudeHook("Notification",
                    "permission_prompt|idle_prompt|elicitation_dialog", checked_);
            });

        y = AddHookCheckbox(panel, theme, "On User Prompt — save question when you send a message",
            settings.HookUserPromptEnabled, pad, y, (checked_) =>
            {
                var s = _context.LoadSettings<ClaudePluginSettings>();
                _context.SaveSettings(s with { HookUserPromptEnabled = checked_ });
                ClaudeHookManager.UpdateClaudeHook("UserPromptSubmit", null, checked_);
            });

        y += 10;

        // --- STATUS LINE section ---
        var slSectionLabel = new Label
        {
            Text = "STATUS LINE",
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            ForeColor = theme.Primary,
            AutoSize = true,
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        panel.Controls.Add(slSectionLabel);
        y += 26;

        var slCheckboxes = new List<CheckBox>();

        var slEnableCheck = new CheckBox
        {
            Text = "Enable Claude Code status line",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = ClaudeStatusLine.IsEnabled(),
            AutoSize = true,
            Location = new Point(pad, y),
            Cursor = Cursors.Hand,
        };

        var slStatus = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = theme.SuccessColor,
            AutoSize = true,
            Location = new Point(pad + 2, y + 22),
            BackColor = Color.Transparent,
        };

        slEnableCheck.CheckedChanged += (_, _) =>
        {
            try
            {
                if (slEnableCheck.Checked)
                {
                    ClaudeStatusLine.Enable();
                    slStatus.ForeColor = theme.SuccessColor;
                    slStatus.Text = "Enabled — restart Claude Code to apply";
                }
                else
                {
                    ClaudeStatusLine.Disable();
                    slStatus.ForeColor = theme.TextSecondary;
                    slStatus.Text = "Disabled — restart Claude Code to apply";
                }
                foreach (var cb in slCheckboxes)
                    cb.Enabled = slEnableCheck.Checked;
            }
            catch (Exception ex)
            {
                slStatus.ForeColor = theme.ErrorColor;
                slStatus.Text = $"Error: {ex.Message}";
            }
        };

        panel.Controls.Add(slEnableCheck);
        panel.Controls.Add(slStatus);
        y += 44;

        // Status line item toggles
        var slItems = new (string Label, string Prop)[]
        {
            ("Model", "SlShowModel"), ("Directory", "SlShowDir"), ("Branch", "SlShowBranch"),
            ("Prompts", "SlShowPrompts"), ("Context %", "SlShowContext"), ("Duration", "SlShowDuration"),
            ("Mode", "SlShowMode"), ("Version", "SlShowVersion"), ("Edit Stats", "SlShowEditStats"),
        };

        int colWidth = contentWidth / 3;
        for (int i = 0; i < slItems.Length; i++)
        {
            int col = i % 3;
            int row = i / 3;
            var item = slItems[i];

            var prop = typeof(ClaudePluginSettings).GetProperty(item.Prop);
            bool isChecked = prop != null ? (bool)prop.GetValue(settings)! : true;

            var cb = new CheckBox
            {
                Text = item.Label,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = theme.TextPrimary,
                BackColor = Color.Transparent,
                Checked = isChecked,
                Enabled = slEnableCheck.Checked,
                AutoSize = true,
                Location = new Point(pad + col * colWidth, y + row * 22),
                Cursor = Cursors.Hand,
            };
            slCheckboxes.Add(cb);
            string propName = item.Prop;
            cb.CheckedChanged += (_, _) =>
            {
                var s = _context.LoadSettings<ClaudePluginSettings>();
                s = propName switch
                {
                    "SlShowModel" => s with { SlShowModel = cb.Checked },
                    "SlShowDir" => s with { SlShowDir = cb.Checked },
                    "SlShowBranch" => s with { SlShowBranch = cb.Checked },
                    "SlShowPrompts" => s with { SlShowPrompts = cb.Checked },
                    "SlShowContext" => s with { SlShowContext = cb.Checked },
                    "SlShowDuration" => s with { SlShowDuration = cb.Checked },
                    "SlShowMode" => s with { SlShowMode = cb.Checked },
                    "SlShowVersion" => s with { SlShowVersion = cb.Checked },
                    "SlShowEditStats" => s with { SlShowEditStats = cb.Checked },
                    _ => s,
                };
                _context.SaveSettings(s);
                ClaudeStatusLine.WriteConfig(s);
            };
            panel.Controls.Add(cb);
        }

        return panel;
    }

    private static int AddHookCheckbox(Panel panel, PluginTheme theme, string text,
        bool isChecked, int pad, int y, Action<bool> onChanged)
    {
        var cb = new CheckBox
        {
            Text = text,
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = isChecked,
            AutoSize = true,
            Location = new Point(pad + 8, y),
            Cursor = Cursors.Hand,
        };
        cb.CheckedChanged += (_, _) => onChanged(cb.Checked);
        panel.Controls.Add(cb);
        return y + 24;
    }
}
