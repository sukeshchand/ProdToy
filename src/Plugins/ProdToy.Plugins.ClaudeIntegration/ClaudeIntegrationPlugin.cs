using System.Drawing;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ClaudeIntegration;

[Plugin("ProdToy.Plugin.ClaudeIntegration", "Claude Integration", "1.0.288",
    Description = "Claude Code hooks, status line, and auto-title integration",
    Author = "ProdToy",
    MenuPriority = 300)]
public class ClaudeIntegrationPlugin : IPlugin
{
    private IPluginContext _context = null!;
    private IDisposable? _notifyHandlerReg;
    private IDisposable? _saveQuestionHandlerReg;
    private IDisposable? _popupReg;
    private ChatHistory _chatHistory = null!;
    private ChatPopupForm? _chatPopup;

    public void Initialize(IPluginContext context)
    {
        _context = context;
        ClaudePaths.Initialize(context.DataDirectory);

        // Phase 3: chat history lives in the plugin's data dir. Migrate
        // legacy host-owned day files once, then use the plugin-owned store
        // from here on. Legacy originals are left in place so the host's
        // still-running ResponseHistory can keep reading them during the
        // parallel-run window (Phases 3–4).
        _chatHistory = new ChatHistory(
            context.DataDirectory,
            () => context.LoadSettings<ClaudePluginSettings>().HistoryEnabled);

        try
        {
            var settings = context.LoadSettings<ClaudePluginSettings>();
            if (!settings.HistoryMigratedFromHost)
            {
                string legacyDir = Path.Combine(context.Host.AppRootPath, "history", "claude", "chats");
                int copied = _chatHistory.MigrateFromLegacy(legacyDir);
                context.SaveSettings(settings with { HistoryMigratedFromHost = true });
                context.Log($"Chat history migrated from host: {copied} day file(s) copied from {legacyDir}");
            }
        }
        catch (Exception ex)
        {
            context.LogError("Chat history legacy migration failed", ex);
        }

        // Phase 2+3: routed pipe handlers. claude.notify → write to plugin
        // history, then show via generic notifications. claude.save-question
        // → plugin-owned SaveQuestion.
        _notifyHandlerReg = context.Host.RegisterPipeHandler("claude.notify", OnNotifyCommand);
        _saveQuestionHandlerReg = context.Host.RegisterPipeHandler("claude.save-question", OnSaveQuestionCommand);
    }

    private void OnNotifyCommand(PipeCommand cmd)
    {
        try
        {
            if (string.IsNullOrEmpty(cmd.PayloadJson)) return;
            var payload = System.Text.Json.JsonSerializer.Deserialize<NotifyPayload>(cmd.PayloadJson);
            if (payload == null) return;

            string title = payload.title ?? "ProdToy";
            string message = payload.message ?? "";
            string type = payload.type ?? "info";
            string sessionId = payload.sessionId ?? "";
            string cwd = payload.cwd ?? "";

            // Phase 3: write the response into plugin-owned history.
            _chatHistory.SaveResponse(title, message, type, sessionId, cwd);

            // Phase 8: snooze, notifications-enabled, and notification-mode
            // gates now live entirely inside ChatPopupForm.ShowPopup(), which
            // reads ClaudePluginSettings directly. No host-side facility.
            EnsureChatPopup();
            _chatPopup?.ShowPopup(title, message, type, sessionId, cwd);
        }
        catch (Exception ex)
        {
            _context.LogError("claude.notify handler failed", ex);
        }
    }

    private void EnsureChatPopup()
    {
        if (_chatPopup != null) return;
        _chatPopup = new ChatPopupForm(_context, _chatHistory);
        _popupReg = _context.Host.RegisterPopup(_chatPopup);
    }

    private void OnSaveQuestionCommand(PipeCommand cmd)
    {
        try
        {
            if (string.IsNullOrEmpty(cmd.PayloadJson)) return;
            var payload = System.Text.Json.JsonSerializer.Deserialize<SaveQuestionPayload>(cmd.PayloadJson);
            if (payload == null) return;
            _chatHistory.SaveQuestion(
                payload.question ?? "",
                payload.sessionId ?? "",
                payload.cwd ?? "");
        }
        catch (Exception ex)
        {
            _context.LogError("claude.save-question handler failed", ex);
        }
    }

    private sealed record NotifyPayload(string? title, string? message, string? type, string? sessionId, string? cwd);
    private sealed record SaveQuestionPayload(string? question, string? sessionId, string? cwd);

    public void Start()
    {
        var settings = _context.LoadSettings<ClaudePluginSettings>();

        // Always re-apply everything — hooks/scripts may have been removed externally
        // Ensure hook script exists on disk
        EnsureHookScript();

        // Force re-apply all Claude hooks (writes entries even if they already exist)
        ClaudeHookManager.UpdateClaudeHook("Stop", null, settings.HookStopEnabled);
        ClaudeHookManager.UpdateClaudeHook("Notification",
            "permission_prompt|idle_prompt|elicitation_dialog", settings.HookNotificationEnabled);
        ClaudeHookManager.UpdateClaudeHook("UserPromptSubmit", null, settings.HookUserPromptEnabled);

        // Force re-apply status line (always re-enable + rewrite config if setting says enabled)
        if (settings.SlEnabled)
        {
            ClaudeStatusLine.Enable();
            ClaudeStatusLine.WriteConfig(settings);
        }
        else
        {
            if (ClaudeStatusLine.IsEnabled())
                ClaudeStatusLine.Disable();
        }

        // Force re-apply auto-title
        ClaudeHookManager.SetAutoTitleHook(settings.AutoTitleToFolder);

        // Cleanup legacy hooks
        ClaudeHookManager.CleanupOldHook();

        _context.Log("Claude integration started — hooks, script, and status line verified");
    }

    public void Stop()
    {
        _notifyHandlerReg?.Dispose();
        _notifyHandlerReg = null;
        _saveQuestionHandlerReg?.Dispose();
        _saveQuestionHandlerReg = null;
        _popupReg?.Dispose();
        _popupReg = null;

        try { _chatPopup?.Close(); } catch { }
        try { _chatPopup?.Dispose(); } catch { }
        _chatPopup = null;

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
        // Phase 5: the plugin owns the PS1 template. Extract the embedded
        // resource and substitute {{EXE_PATH}} with the running host exe
        // path so the hook script can find the host.
        try
        {
            Directory.CreateDirectory(ClaudePaths.ClaudeHooksDir);
            string ps1Path = Path.Combine(ClaudePaths.ClaudeHooksDir, "Show-ProdToy.ps1");

            var assembly = typeof(ClaudeIntegrationPlugin).Assembly;
            using var stream = assembly.GetManifestResourceStream(
                "ProdToy.Plugins.ClaudeIntegration.Scripts.Show-ProdToy.ps1");
            if (stream == null)
                throw new InvalidOperationException("Embedded Show-ProdToy.ps1 not found.");
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            string template = reader.ReadToEnd();

            string exePath = Path.Combine(_context.Host.AppRootPath, "ProdToy.exe");
            string content = template.Replace("{{EXE_PATH}}", exePath);

            File.WriteAllText(ps1Path, content, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _context.LogError("Failed to write hook script", ex);
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
        new("Show Last Notification", ShowLastNotification, Priority: 50, Icon: "\uD83D\uDCE8"),
    ];

    public IReadOnlyList<MenuContribution> GetDashboardItems() =>
    [
        new("Last Notification", ShowLastNotification, Priority: 50, Icon: "\uD83D\uDCE8"),
    ];

    private void ShowLastNotification()
    {
        // Phase 8: the plugin owns its popup entirely. There's no generic
        // notification facility to fall back on — just bring the chat
        // popup forward (creating it lazily if needed) and show the last
        // entry from plugin-owned history.
        _context.Host.InvokeOnUI(() =>
        {
            EnsureChatPopup();
            var latest = _chatHistory.GetLatest();
            if (latest != null)
            {
                _chatPopup!.ShowPopup(latest.Title, latest.Message, latest.Type, latest.SessionId, latest.Cwd);
            }
            else
            {
                _chatPopup!.BringToForeground();
            }
        });
    }

    public SettingsPageContribution? GetSettingsPage() =>
        new("Claude CLI", () => BuildSettingsPanel(), TabOrder: 200);

    private Control BuildSettingsPanel()
    {
        var theme = _context.Host.CurrentTheme;
        var settings = _context.LoadSettings<ClaudePluginSettings>();

        var panel = new Panel
        {
            AutoScroll = true,
            Dock = DockStyle.Fill,
            BackColor = theme.BgDark,
        };

        int pad = 16;
        int y = pad;
        int contentWidth = 700;

        // --- NOTIFICATIONS section (Phase 8 — all plugin-owned) ---
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

        bool notifEnabled = settings.NotificationsEnabled;
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
        notifModeCombo.SelectedItem = notifModes.Contains(settings.NotificationMode) ? settings.NotificationMode : "Popup";
        notifModeCombo.SelectedIndexChanged += (_, _) =>
        {
            var s = _context.LoadSettings<ClaudePluginSettings>();
            _context.SaveSettings(s with { NotificationMode = notifModeCombo.SelectedItem?.ToString() ?? "Popup" });
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
            Checked = settings.ShowQuotes,
            Enabled = notifEnabled,
            AutoSize = true,
            Location = new Point(pad + 8, y),
            Cursor = Cursors.Hand,
        };
        quotesCheck.CheckedChanged += (_, _) =>
        {
            var s = _context.LoadSettings<ClaudePluginSettings>();
            _context.SaveSettings(s with { ShowQuotes = quotesCheck.Checked });
            _chatPopup?.SetShowQuotes(quotesCheck.Checked);
        };
        panel.Controls.Add(quotesCheck);
        notifSubControls.Add(quotesCheck);
        y += 28;

        bool isSnoozed = settings.SnoozeUntil > DateTime.Now;
        var snoozeCheck = new CheckBox
        {
            Text = isSnoozed
                ? $"Snoozed ({Math.Max(1, (int)(settings.SnoozeUntil - DateTime.Now).TotalMinutes)} min left)"
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
            var s = _context.LoadSettings<ClaudePluginSettings>();
            var until = snoozeCheck.Checked ? DateTime.Now.AddMinutes(30) : DateTime.MinValue;
            _context.SaveSettings(s with { SnoozeUntil = until });
        };
        panel.Controls.Add(snoozeCheck);
        notifSubControls.Add(snoozeCheck);

        notifEnabledCheck.CheckedChanged += (_, _) =>
        {
            var s = _context.LoadSettings<ClaudePluginSettings>();
            _context.SaveSettings(s with { NotificationsEnabled = notifEnabledCheck.Checked });
            foreach (var ctrl in notifSubControls)
                ctrl.Enabled = notifEnabledCheck.Checked;
        };
        y += 28;

        // --- CHATS section ---
        // Phase 5: HistoryEnabled is plugin-owned. ChatHistory reads this
        // flag as its gate; we no longer touch host settings.json for it.
        var historyCheck = new CheckBox
        {
            Text = "Save chat history",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = _context.LoadSettings<ClaudePluginSettings>().HistoryEnabled,
            AutoSize = true,
            Location = new Point(pad + 8, y),
            Cursor = Cursors.Hand,
        };
        historyCheck.CheckedChanged += (_, _) =>
        {
            var s = _context.LoadSettings<ClaudePluginSettings>();
            _context.SaveSettings(s with { HistoryEnabled = historyCheck.Checked });
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
            Checked = settings.SlEnabled,
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
                var s = _context.LoadSettings<ClaudePluginSettings>();
                _context.SaveSettings(s with { SlEnabled = slEnableCheck.Checked });

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
