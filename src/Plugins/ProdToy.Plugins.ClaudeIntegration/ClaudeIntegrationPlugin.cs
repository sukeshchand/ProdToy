using System.Drawing;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ClaudeIntegration;

[Plugin("ProdToy.Plugin.ClaudeIntegration", "Claude Integration", "1.0.357",
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
    private TelegramNotifier _telegram = null!;

    public void Install(IPluginContext context)
    {
        ClaudePaths.Initialize(context.DataDirectory);

        // 1. Scan the disk for Claude installations.
        var installs = ClaudeInstallDiscovery.Scan();

        // 2. Extract both PS1 scripts into the plugin data dir.
        string pluginSettingsPath = Path.Combine(context.DataDirectory, "settings.json");
        EnsureHookScriptFromResource(context);
        ClaudeStatusLine.Install(installs, context.LoadSettings<ClaudePluginSettings>(), pluginSettingsPath);

        // 3. Write hook entries into every discovered install's settings.json.
        //    Entries are always written unconditionally — the PS1 scripts decide
        //    at runtime whether to actually render anything based on plugin
        //    settings (SlEnabled, NotificationsEnabled, HostRunning).
        ClaudeHookManager.UpdateClaudeHook(installs, "Stop", null, enabled: true);
        ClaudeHookManager.UpdateClaudeHook(installs, "Notification",
            "permission_prompt|idle_prompt|elicitation_dialog", enabled: true);
        ClaudeHookManager.UpdateClaudeHook(installs, "UserPromptSubmit", null, enabled: true);

        // 4. Auto-title instruction in CLAUDE.md.
        var settings = context.LoadSettings<ClaudePluginSettings>();
        if (settings.AutoTitleToFolder)
            ClaudeHookManager.SetAutoTitleHook(installs, enabled: true);

        // 5. Remember the installs we registered into so Uninstall() can find them.
        context.SaveSettings(settings with
        {
            ClaudeConfigDirs = installs.Select(i => i.ConfigDir).ToList(),
        });

        context.Log($"Claude integration installed into {installs.Count} Claude install(s)");
    }

    public void Uninstall(IPluginContext context)
    {
        ClaudePaths.Initialize(context.DataDirectory);

        var settings = context.LoadSettings<ClaudePluginSettings>();
        var installs = settings.ClaudeConfigDirs
            .Where(Directory.Exists)
            .Select(d => new ClaudeInstall(d))
            .ToList();

        // Fallback: if the stored list is empty (shouldn't happen after a
        // proper Install(), but belt-and-suspenders), rescan now.
        if (installs.Count == 0)
            installs = ClaudeInstallDiscovery.Scan();

        try
        {
            ClaudeHookManager.RemoveAllProdToyHooks(installs);
            ClaudeHookManager.SetAutoTitleHook(installs, enabled: false);
            ClaudeStatusLine.Uninstall(installs);
        }
        catch (Exception ex)
        {
            context.LogError("Failed to remove Claude integration", ex);
        }

        context.Log($"Claude integration removed from {installs.Count} Claude install(s)");
    }

    public void Initialize(IPluginContext context)
    {
        _context = context;
        ClaudePaths.Initialize(context.DataDirectory);

        _chatHistory = new ChatHistory(
            context.DataDirectory,
            () => context.LoadSettings<ClaudePluginSettings>().HistoryEnabled);

        _telegram = new TelegramNotifier(context);

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
            string hookEvent = payload.hookEvent ?? "";

            // Phase 3: write the response into plugin-owned history.
            _chatHistory.SaveResponse(title, message, type, sessionId, cwd);

            // Phase 8: PipeRouter dispatches on the UI thread via
            // _popupForm.Invoke() — a synchronous SendMessage. WebView2's
            // CoreWebView2 rejects calls from that nested Invoke call stack
            // with "can only be accessed from the UI thread" even though it IS
            // the UI thread. Fix: BeginInvoke on the ChatPopupForm itself to
            // post ShowPopup onto a clean top-level message loop iteration.
            EnsureChatPopup();
            if (_chatPopup != null)
            {
                var popup = _chatPopup;
                popup.BeginInvoke(() =>
                {
                    popup.ShowPopup(title, message, type, sessionId, cwd);
                });
            }

            // Fan out to Telegram (fire-and-forget). TelegramNotifier handles
            // its own enable/gate/credential checks and never throws into the
            // UI thread — all errors land in plugins.log.
            _ = _telegram.SendAsync(title, message, hookEvent);
        }
        catch (Exception ex)
        {
            _context.LogError("claude.notify handler failed", ex);
        }
    }

    private void EnsureChatPopup()
    {
        // If the previous instance's WebView2 init failed permanently, tear
        // it down and rebuild. Prevents a single flaky startup (e.g. a transient
        // COM apartment race) from bricking notifications for the rest of the
        // session — next notification gets a fresh popup with a fresh WebView2.
        if (_chatPopup != null && _chatPopup.IsWebViewFailed)
        {
            _context.LogError("ChatPopupForm previous init failed — rebuilding");
            try { _popupReg?.Dispose(); } catch { }
            _popupReg = null;
            try { _chatPopup.Close(); } catch { }
            try { _chatPopup.Dispose(); } catch { }
            _chatPopup = null;
        }

        if (_chatPopup != null) return;

        _chatPopup = new ChatPopupForm(_context, _chatHistory);
        _chatPopup.WebViewInitFailed += () =>
        {
            // Nothing to do synchronously — the IsWebViewFailed flag is already
            // set. The next EnsureChatPopup call (on the next notification) will
            // see it and rebuild.
        };
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

    private sealed record NotifyPayload(string? title, string? message, string? type, string? sessionId, string? cwd, string? hookEvent);
    private sealed record SaveQuestionPayload(string? question, string? sessionId, string? cwd);

    public void Start()
    {
        // Start is per-run: we only touch in-process state here, not Claude
        // settings.json or hook scripts on disk (that's Install()'s job, done
        // once at plugin install time).
        //
        // BUT the PS1 hook script is shipped as an embedded resource and
        // evolves across plugin versions (e.g. v1.0.322 added the hookEvent
        // field to the envelope). A pre-upgrade PS1 left on disk will send
        // a stale envelope that silently drops the event routing. So on every
        // Start we unconditionally re-extract from the resource — cheap
        // (single file write), self-healing across upgrades, and no user
        // action required.
        try
        {
            EnsureHookScriptFromResource(_context);
        }
        catch (Exception ex)
        {
            _context.LogError("Start: ShowProdToy script extraction failed", ex);
        }

        // Mark host as running so the PS1 scripts know they can dispatch.
        SetHostRunning(true);

        // Force Claude to re-run the status-line script so the bar appears
        // immediately on host start (HostRunning flipped from false → true,
        // but Claude would otherwise keep showing its cached empty output).
        BumpStatusLineNow();

        // Pre-create the chat popup and kick off WebView2 initialization NOW,
        // while Start() is running on the clean UI-thread call stack from
        // PluginManager.StartAll() in the host ctor. Deferring popup creation
        // until the first pipe notification arrives via Control.Invoke
        // reliably trips WebView2's RPC_E_CHANGED_MODE COM-apartment race on
        // first init — matching the user-observed "works after opening from
        // dashboard first, fails if the first trigger is a hook" symptom.
        try
        {
            _context.Host.InvokeOnUI(() =>
            {
                EnsureChatPopup();
                _chatPopup?.Prewarm();
            });
        }
        catch (Exception ex)
        {
            _context.LogError("Chat popup prewarm failed", ex);
        }

        _context.Log("Claude integration started");
    }

    public void Stop()
    {
        // Mark host as not running so PS1 scripts short-circuit without
        // trying to talk to a dead pipe. Done first so a slow form dispose
        // below doesn't leave the flag stale if Claude fires a hook mid-Stop.
        SetHostRunning(false);

        // Force Claude to re-run the status-line script so the bar disappears
        // immediately on host stop (HostRunning flipped true → false, but
        // Claude would otherwise keep showing its cached populated output).
        // Must happen while the process is still alive enough to write JSON.
        BumpStatusLineNow();

        // Stop is per-run: only in-process teardown (pipe handlers, popup form).
        // Claude settings.json and hook scripts are left alone — they were put
        // there by Install() and are only removed by Uninstall().
        _notifyHandlerReg?.Dispose();
        _notifyHandlerReg = null;
        _saveQuestionHandlerReg?.Dispose();
        _saveQuestionHandlerReg = null;
        _popupReg?.Dispose();
        _popupReg = null;

        try { _chatPopup?.Close(); } catch { }
        try { _chatPopup?.Dispose(); } catch { }
        _chatPopup = null;

        _context.Log("Claude integration stopped");
    }

    private void SetHostRunning(bool running)
    {
        try
        {
            var s = _context.LoadSettings<ClaudePluginSettings>();
            _context.SaveSettings(s with { HostRunning = running });
        }
        catch (Exception ex)
        {
            _context.LogError($"Failed to write HostRunning={running}", ex);
        }
    }

    /// <summary>
    /// Extracts <c>Show-ProdToy.ps1</c> from the embedded resource into the
    /// plugin's scripts directory, substituting runtime placeholders with
    /// concrete paths so the PS1 script is fully self-contained.
    /// </summary>
    private static void EnsureHookScriptFromResource(IPluginContext context)
    {
        Directory.CreateDirectory(ClaudePaths.ScriptsDir);

        var assembly = typeof(ClaudeIntegrationPlugin).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            "ProdToy.Plugins.ClaudeIntegration.Scripts.Show-ProdToy.ps1");
        if (stream == null)
            throw new InvalidOperationException("Embedded Show-ProdToy.ps1 not found.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        string template = reader.ReadToEnd();

        string exePath = Path.Combine(context.Host.AppRootPath, "ProdToy.exe");
        string settingsPath = Path.Combine(context.DataDirectory, "settings.json");

        string content = template
            .Replace("{{EXE_PATH}}", exePath)
            .Replace("{{SETTINGS_PATH}}", settingsPath)
            .Replace("{{PIPE_NAME}}", "ProdToy_Pipe");

        File.WriteAllText(ClaudePaths.ShowProdToyScript, content, System.Text.Encoding.UTF8);
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

        // Local helper: thin horizontal separator between sections.
        int AddSeparator(int yPos)
        {
            var sep = new Panel
            {
                BackColor = theme.Border,
                Size = new Size(contentWidth - pad, 1),
                Location = new Point(pad, yPos + 6),
            };
            panel.Controls.Add(sep);
            return yPos + 18;
        }

        // --- CLAUDE INSTALLATIONS section ---
        var installsLabel = new Label
        {
            Text = "CLAUDE INSTALLATIONS",
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            ForeColor = theme.Primary,
            AutoSize = true,
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        panel.Controls.Add(installsLabel);
        y += 22;

        var installsScroll = new Panel
        {
            AutoScroll = true,
            Size = new Size(contentWidth - 120, 90),
            Location = new Point(pad + 8, y),
            BackColor = Color.Transparent,
        };
        var installsList = new Label
        {
            Text = BuildInstallsListText(settings.ClaudeConfigDirs),
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            MaximumSize = new Size(installsScroll.ClientSize.Width - 4, 0),
            Location = new Point(0, 0),
            BackColor = Color.Transparent,
        };
        installsScroll.Controls.Add(installsList);
        panel.Controls.Add(installsScroll);

        var rescanButton = new Button
        {
            Text = "Rescan",
            Font = new Font("Segoe UI", 8.5f),
            Size = new Size(90, 26),
            Location = new Point(contentWidth - 104, y),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextPrimary,
            Cursor = Cursors.Hand,
        };
        rescanButton.FlatAppearance.BorderSize = 0;
        rescanButton.Click += (_, _) =>
        {
            var found = ClaudeInstallDiscovery.Scan();
            var s = _context.LoadSettings<ClaudePluginSettings>();
            _context.SaveSettings(s with { ClaudeConfigDirs = found.Select(i => i.ConfigDir).ToList() });

            // Re-apply installation into any newly discovered installs so
            // their settings.json picks up the hook/statusLine entries.
            try { Install(_context); } catch (Exception ex) { _context.LogError("Rescan install failed", ex); }

            installsList.Text = BuildInstallsListText(found.Select(i => i.ConfigDir).ToList());
        };
        panel.Controls.Add(rescanButton);
        y += 98;

        y = AddSeparator(y);

        // --- HOOKS AND NOTIFICATIONS section ---
        var hooksNotifLabel = new Label
        {
            Text = "HOOKS AND NOTIFICATIONS",
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            ForeColor = theme.Primary,
            AutoSize = true,
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        panel.Controls.Add(hooksNotifLabel);
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
        y += 34;

        // Hook toggles just update plugin settings. Show-ProdToy.ps1 reads
        // these flags at runtime and short-circuits if the event is disabled.
        y = AddHookCheckbox(panel, theme, "On Stop — notify when Claude finishes a response",
            settings.HookStopEnabled, pad, y, (checked_) =>
            {
                var s = _context.LoadSettings<ClaudePluginSettings>();
                _context.SaveSettings(s with { HookStopEnabled = checked_ });
            });

        y = AddHookCheckbox(panel, theme, "On Notification — notify on permission/idle/question prompts",
            settings.HookNotificationEnabled, pad, y, (checked_) =>
            {
                var s = _context.LoadSettings<ClaudePluginSettings>();
                _context.SaveSettings(s with { HookNotificationEnabled = checked_ });
            });

        y = AddHookCheckbox(panel, theme, "On User Prompt — save question when you send a message",
            settings.HookUserPromptEnabled, pad, y, (checked_) =>
            {
                var s = _context.LoadSettings<ClaudePluginSettings>();
                _context.SaveSettings(s with { HookUserPromptEnabled = checked_ });
            });

        y += 10;

        y = AddSeparator(y);

        // --- TELEGRAM section ---
        // Outbound Telegram bot channel. Runs alongside popup/balloon — this
        // is orthogonal to NotificationMode, gated by its own TelegramEnabled
        // flag so a user can disable Telegram without losing the local popup.
        var telegramLabel = new Label
        {
            Text = "TELEGRAM",
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            ForeColor = theme.Primary,
            AutoSize = true,
            Location = new Point(pad, y),
            BackColor = Color.Transparent,
        };
        panel.Controls.Add(telegramLabel);
        y += 26;

        var tgSubControls = new List<Control>();

        var tgEnabledCheck = new CheckBox
        {
            Text = "Send notifications to Telegram",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = settings.TelegramEnabled,
            AutoSize = true,
            Location = new Point(pad + 8, y),
            Cursor = Cursors.Hand,
        };
        panel.Controls.Add(tgEnabledCheck);
        y += 30;

        int labelX = pad + 8;
        int fieldX = pad + 110;
        int fieldW = contentWidth - fieldX - pad;

        // Bot token (masked)
        var tokenLabel = new Label
        {
            Text = "Bot token:",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(labelX, y + 4),
            BackColor = Color.Transparent,
        };
        panel.Controls.Add(tokenLabel);
        tgSubControls.Add(tokenLabel);

        var tokenBox = new TextBox
        {
            Font = new Font("Segoe UI", 9f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            UseSystemPasswordChar = true,
            Size = new Size(fieldW, 24),
            Location = new Point(fieldX, y),
            Text = settings.TelegramBotToken,
        };
        tokenBox.TextChanged += (_, _) =>
        {
            var s = _context.LoadSettings<ClaudePluginSettings>();
            _context.SaveSettings(s with { TelegramBotToken = tokenBox.Text.Trim() });
        };
        panel.Controls.Add(tokenBox);
        tgSubControls.Add(tokenBox);
        y += 30;

        // Chat ID
        var chatLabel = new Label
        {
            Text = "Chat ID:",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(labelX, y + 4),
            BackColor = Color.Transparent,
        };
        panel.Controls.Add(chatLabel);
        tgSubControls.Add(chatLabel);

        var chatBox = new TextBox
        {
            Font = new Font("Segoe UI", 9f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Size = new Size(200, 24),
            Location = new Point(fieldX, y),
            Text = settings.TelegramChatId,
        };
        chatBox.TextChanged += (_, _) =>
        {
            var s = _context.LoadSettings<ClaudePluginSettings>();
            _context.SaveSettings(s with { TelegramChatId = chatBox.Text.Trim() });
        };
        panel.Controls.Add(chatBox);
        tgSubControls.Add(chatBox);
        y += 30;

        // Prefix
        var prefixLabel = new Label
        {
            Text = "Prefix:",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(labelX, y + 4),
            BackColor = Color.Transparent,
        };
        panel.Controls.Add(prefixLabel);
        tgSubControls.Add(prefixLabel);

        var prefixBox = new TextBox
        {
            Font = new Font("Segoe UI", 9f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Size = new Size(200, 24),
            Location = new Point(fieldX, y),
            Text = settings.TelegramPrefix,
        };
        prefixBox.TextChanged += (_, _) =>
        {
            var s = _context.LoadSettings<ClaudePluginSettings>();
            _context.SaveSettings(s with { TelegramPrefix = prefixBox.Text });
        };
        panel.Controls.Add(prefixBox);
        tgSubControls.Add(prefixBox);
        y += 30;

        // Max chars
        var maxLabel = new Label
        {
            Text = "Max chars:",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(labelX, y + 4),
            BackColor = Color.Transparent,
        };
        panel.Controls.Add(maxLabel);
        tgSubControls.Add(maxLabel);

        var maxBox = new NumericUpDown
        {
            Font = new Font("Segoe UI", 9f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Minimum = 50,
            Maximum = 4000,
            Increment = 50,
            Value = Math.Clamp(settings.TelegramMaxChars, 50, 4000),
            Size = new Size(80, 24),
            Location = new Point(fieldX, y),
        };
        maxBox.ValueChanged += (_, _) =>
        {
            var s = _context.LoadSettings<ClaudePluginSettings>();
            _context.SaveSettings(s with { TelegramMaxChars = (int)maxBox.Value });
        };
        panel.Controls.Add(maxBox);
        tgSubControls.Add(maxBox);
        y += 32;

        // Per-event toggles
        var tgOnStopCheck = new CheckBox
        {
            Text = "On Stop",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = settings.TelegramOnStop,
            AutoSize = true,
            Location = new Point(pad + 8, y),
            Cursor = Cursors.Hand,
        };
        tgOnStopCheck.CheckedChanged += (_, _) =>
        {
            var s = _context.LoadSettings<ClaudePluginSettings>();
            _context.SaveSettings(s with { TelegramOnStop = tgOnStopCheck.Checked });
        };
        panel.Controls.Add(tgOnStopCheck);
        tgSubControls.Add(tgOnStopCheck);

        var tgOnNotifCheck = new CheckBox
        {
            Text = "On Notification",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextPrimary,
            BackColor = Color.Transparent,
            Checked = settings.TelegramOnNotification,
            AutoSize = true,
            Location = new Point(pad + 120, y),
            Cursor = Cursors.Hand,
        };
        tgOnNotifCheck.CheckedChanged += (_, _) =>
        {
            var s = _context.LoadSettings<ClaudePluginSettings>();
            _context.SaveSettings(s with { TelegramOnNotification = tgOnNotifCheck.Checked });
        };
        panel.Controls.Add(tgOnNotifCheck);
        tgSubControls.Add(tgOnNotifCheck);
        y += 32;

        // Test button + status
        var testBtn = new Button
        {
            Text = "Send test message",
            Font = new Font("Segoe UI", 9f),
            Size = new Size(150, 28),
            Location = new Point(pad + 8, y),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextPrimary,
            Cursor = Cursors.Hand,
        };
        testBtn.FlatAppearance.BorderSize = 0;
        panel.Controls.Add(testBtn);
        tgSubControls.Add(testBtn);

        var testStatus = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(pad + 168, y + 7),
            BackColor = Color.Transparent,
        };
        panel.Controls.Add(testStatus);

        testBtn.Click += async (_, _) =>
        {
            testBtn.Enabled = false;
            testStatus.ForeColor = theme.TextSecondary;
            testStatus.Text = "Sending...";
            try
            {
                var (ok, detail) = await _telegram.TestSendAsync();
                testStatus.ForeColor = ok ? theme.SuccessColor : theme.ErrorColor;
                testStatus.Text = ok ? "Sent!" : $"Failed: {detail}";
            }
            catch (Exception ex)
            {
                testStatus.ForeColor = theme.ErrorColor;
                testStatus.Text = $"Error: {ex.Message}";
            }
            finally
            {
                testBtn.Enabled = true;
            }
        };

        // Sub-control enable state follows the master checkbox
        foreach (var ctrl in tgSubControls)
            ctrl.Enabled = settings.TelegramEnabled;

        tgEnabledCheck.CheckedChanged += (_, _) =>
        {
            var s = _context.LoadSettings<ClaudePluginSettings>();
            _context.SaveSettings(s with { TelegramEnabled = tgEnabledCheck.Checked });
            foreach (var ctrl in tgSubControls)
                ctrl.Enabled = tgEnabledCheck.Checked;
        };

        y += 36;

        y = AddSeparator(y);

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

        y = AddSeparator(y);

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

                // context-bar.ps1 reads SlEnabled on every render, so the
                // behavior change is already live. But Claude caches the
                // status-line script output between events, so we bump the
                // script filename to force it to re-run on the next tick.
                BumpStatusLineNow();

                slStatus.ForeColor = slEnableCheck.Checked ? theme.SuccessColor : theme.TextSecondary;
                slStatus.Text = slEnableCheck.Checked
                    ? "Enabled"
                    : "Disabled";

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

        // Style dropdown — selects the rendering preset used by context-bar.ps1.
        var styleLabel = new Label
        {
            Text = "Style",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(pad, y + 4),
            BackColor = Color.Transparent,
        };
        panel.Controls.Add(styleLabel);

        var styleOptions = new (string Value, string Display)[]
        {
            ("classic",     "Classic — multi-line, labeled"),
            ("minimal",     "Minimal — single line, no colors"),
            ("emoji",       "Emoji — icons + colors (default)"),
            ("powerline",   "Powerline — chevron-separated blocks"),
            ("ascii",       "ASCII — [Key=Value] brackets, no colors"),
            ("compact",     "Compact — single-letter labels"),
            ("progressbar", "Progress Bar — visual context meter"),
            ("verbose",     "Verbose — multi-line, dense"),
        };

        var styleCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Segoe UI", 9f),
            Location = new Point(pad + 60, y),
            Size = new Size(contentWidth - 80, 24),
            Enabled = slEnableCheck.Checked,
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.BgDark,
            ForeColor = theme.TextPrimary,
        };
        foreach (var (_, display) in styleOptions) styleCombo.Items.Add(display);
        int styleIdx = Array.FindIndex(styleOptions, o =>
            string.Equals(o.Value, settings.SlStyle, StringComparison.OrdinalIgnoreCase));
        styleCombo.SelectedIndex = styleIdx >= 0 ? styleIdx : 0;
        styleCombo.SelectedIndexChanged += (_, _) =>
        {
            if (styleCombo.SelectedIndex < 0) return;
            string value = styleOptions[styleCombo.SelectedIndex].Value;
            var s = _context.LoadSettings<ClaudePluginSettings>();
            _context.SaveSettings(s with { SlStyle = value });
            ClaudeStatusLine.WriteConfig(s with { SlStyle = value });
            BumpStatusLineNow();
        };
        panel.Controls.Add(styleCombo);
        // Keep style combo enabled-state in sync with the master enable toggle.
        slEnableCheck.CheckedChanged += (_, _) => styleCombo.Enabled = slEnableCheck.Checked;
        y += 32;

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

                // Force Claude to re-run the status-line script next tick.
                BumpStatusLineNow();
            };
            panel.Controls.Add(cb);
        }

        return panel;
    }

    /// <summary>
    /// Force every discovered Claude install to re-run the status-line script
    /// on the next render tick. Claude caches script output between events, so
    /// just changing plugin settings isn't visible immediately — bumping the
    /// script filename invalidates the cache.
    /// </summary>
    private void BumpStatusLineNow()
    {
        try
        {
            var s = _context.LoadSettings<ClaudePluginSettings>();
            var installs = s.ClaudeConfigDirs
                .Where(Directory.Exists)
                .Select(d => new ClaudeInstall(d))
                .ToList();
            if (installs.Count == 0) return;

            string pluginSettingsPath = Path.Combine(_context.DataDirectory, "settings.json");
            ClaudeStatusLine.BumpScriptVersion(installs, pluginSettingsPath);
        }
        catch (Exception ex)
        {
            _context.LogError("BumpStatusLineNow failed", ex);
        }
    }

    private static string BuildInstallsListText(List<string> dirs)
    {
        if (dirs == null || dirs.Count == 0)
            return "No Claude installations detected. Click Rescan to search.";
        return string.Join("\n", dirs.Select(d => "• " + d));
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
