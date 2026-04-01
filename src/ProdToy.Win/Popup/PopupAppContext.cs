using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ProdToy;

class PopupAppContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly PopupForm _popupForm;
    private readonly CancellationTokenSource _cts = new();
    private Icon _appIcon;
    private SettingsForm? _settingsForm;
    private readonly GlobalHotkey _globalHotkey;
    private readonly TripleCtrlDetector _tripleCtrl;
    private readonly ToolStripItem _takeScreenshotItem;
    private readonly ToolStripItem _editScreenshotItem;
    private AlarmForm? _alarmForm;
    private ScreenshotEditorForm? _editorForm;

    public PopupAppContext(string initialTitle, string initialMessage, string initialType, string sessionId = "", string cwd = "")
    {
        var theme = Themes.LoadSaved();
        _appIcon = Themes.CreateAppIcon(theme.Primary);

        // Ensure hook script matches this exe version (critical after updates)
        Updater.EnsureHookScript(Application.ExecutablePath);

        _popupForm = new PopupForm(theme);
        _popupForm.ShowPopup(initialTitle, initialMessage, initialType, sessionId, cwd);

        var trayMenu = BuildTrayMenu();
        _takeScreenshotItem = trayMenu.Items[2];
        _editScreenshotItem = trayMenu.Items[3];

        _trayIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "ProdToy",
            Visible = true,
            ContextMenuStrip = trayMenu,
        };
        _trayIcon.DoubleClick += (_, _) => _popupForm.BringToForeground();

        Task.Run(() => PipeServerLoop(_cts.Token));

        // Exit when popup requests it (e.g. after update)
        _popupForm.ExitRequested += () => ExitApp();

        // Register global screenshot hotkey (only if capture is enabled)
        _globalHotkey = new GlobalHotkey();
        _globalHotkey.HotkeyPressed += () => _popupForm.Invoke(TakeScreenshot);
        var appSettings = AppSettings.Load();
        if (appSettings.ScreenshotEnabled && !string.IsNullOrEmpty(appSettings.ScreenshotHotkey))
            _globalHotkey.Register(appSettings.ScreenshotHotkey);

        // Triple Ctrl tap → edit last screenshot
        _tripleCtrl = new TripleCtrlDetector();
        _tripleCtrl.TripleTapped += () => _popupForm.Invoke(EditLastScreenshot);

        // Sync Claude hooks with ProdToy settings on startup
        SyncClaudeHooks(appSettings);

        // Start update checker
        UpdateChecker.UpdateAvailable += metadata =>
        {
            _popupForm.ShowUpdateAvailable(metadata);
        };
        UpdateChecker.Start();

        // Start alarm scheduler
        if (AppSettings.Load().AlarmsEnabled)
        {
            AlarmNotifier.Initialize(_popupForm, _trayIcon);
            AlarmScheduler.AlarmTriggered += AlarmNotifier.HandleAlarmTriggered;
            AlarmStore.StartHistoryFlush();
            AlarmScheduler.Start();
        }
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        bool captureEnabled = AppSettings.Load().ScreenshotEnabled;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show Last Notification", null, (_, _) => _popupForm.BringToForeground());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Take Screenshot", null, (_, _) => TakeScreenshot());
        menu.Items.Add("Edit Last Screenshot", null, (_, _) => EditLastScreenshot());
        menu.Items[2].Visible = captureEnabled;
        menu.Items[3].Visible = captureEnabled;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Alarms...", null, (_, _) => ShowAlarmForm());
        menu.Items.Add("Settings...", null, (_, _) => ShowSettingsForm());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        return menu;
    }

    private void EnsureEditor(Bitmap capturedImage)
    {
        if (_editorForm == null || _editorForm.IsDisposed)
        {
            _editorForm = new ScreenshotEditorForm(capturedImage);
            _editorForm.BringToForeground();
        }
        else
        {
            _editorForm.LoadCapture(capturedImage);
        }
    }

    private void EnsureEditor(string filePath)
    {
        if (_editorForm == null || _editorForm.IsDisposed)
        {
            _editorForm = new ScreenshotEditorForm(filePath);
            _editorForm.BringToForeground();
        }
        else
        {
            _editorForm.LoadFile(filePath);
        }
    }

    private void TakeScreenshot()
    {
        var overlay = new ScreenshotOverlay();
        overlay.RegionCaptured += bitmap => EnsureEditor(bitmap);
        overlay.Show();
    }

    private void EditLastScreenshot()
    {
        try
        {
            string dir = AppPaths.ScreenshotsDir;
            if (!Directory.Exists(dir)) return;

            var lastFile = Directory.GetFiles(dir, "*.png")
                .Concat(Directory.GetFiles(dir, "*.jpg"))
                .Concat(Directory.GetFiles(dir, "*.bmp"))
                .OrderByDescending(File.GetCreationTime)
                .FirstOrDefault();

            if (lastFile == null) return;

            EnsureEditor(lastFile);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"EditLastScreenshot failed: {ex.Message}");
        }
    }

    private void ShowSettingsForm()
    {
        // Bring existing settings form to front if already open
        if (_settingsForm != null && !_settingsForm.IsDisposed)
        {
            _settingsForm.BringToFront();
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_popupForm.CurrentTheme, _popupForm.SnoozeUntil);

        _settingsForm.ThemeChanged += theme =>
        {
            // Update tray icon
            _appIcon.Dispose();
            _appIcon = Themes.CreateAppIcon(theme.Primary);
            _trayIcon.Icon = _appIcon;

            // Apply to popup
            _popupForm.ApplyTheme(theme);
        };

        _settingsForm.HistoryEnabledChanged += _ =>
        {
            _popupForm.UpdateHistoryNav();
        };

        _settingsForm.ShowQuotesChanged += show =>
        {
            _popupForm.SetShowQuotes(show);
        };

        _settingsForm.SnoozeChanged += snoozed =>
        {
            if (snoozed)
                _popupForm.Snooze();
            else
                _popupForm.Unsnooze();

            UpdateTrayText();
        };

        _settingsForm.ScreenshotEnabledChanged += enabled =>
        {
            _takeScreenshotItem.Visible = enabled;
            _editScreenshotItem.Visible = enabled;
            _globalHotkey.Unregister();
            if (enabled)
            {
                var hk = AppSettings.Load().ScreenshotHotkey;
                if (!string.IsNullOrEmpty(hk))
                    _globalHotkey.Register(hk);
            }
        };

        _settingsForm.ScreenshotHotkeyChanged += hotkey =>
        {
            _globalHotkey.Unregister();
            if (AppSettings.Load().ScreenshotEnabled && !string.IsNullOrEmpty(hotkey))
                _globalHotkey.Register(hotkey);
        };

        _settingsForm.GlobalFontChanged += fontFamily =>
        {
            _popupForm.ApplyGlobalFont(fontFamily);
            if (_alarmForm != null && !_alarmForm.IsDisposed)
                ApplyFontToForm(_alarmForm, fontFamily);
        };

        _settingsForm.UninstallRequested += () => ExitApp();

        _popupForm.SnoozeChanged += UpdateTrayText;

        _settingsForm.FormClosed += (_, _) =>
        {
            _popupForm.SnoozeChanged -= UpdateTrayText;
            _settingsForm = null;
        };

        _settingsForm.Show();
    }

    private void UpdateTrayText()
    {
        if (_popupForm.IsSnoozed)
        {
            var remaining = _popupForm.SnoozeUntil - DateTime.Now;
            int mins = Math.Max(1, (int)remaining.TotalMinutes);
            _trayIcon.Text = $"ProdToy (snoozed {mins}m)";
        }
        else
        {
            _trayIcon.Text = "ProdToy";
        }
    }

    private static void ApplyFontToForm(Form form, string fontFamily)
    {
        try
        {
            form.SuspendLayout();
            var oldFont = form.Font;
            form.Font = new Font(fontFamily, form.Font.Size, form.Font.Style);
            if (oldFont != form.Font) oldFont.Dispose();
            ApplyFontRecursive(form, fontFamily);
            form.ResumeLayout();
            form.Invalidate(true);
        }
        catch { }
    }

    private static void ApplyFontRecursive(Control control, string fontFamily)
    {
        foreach (Control child in control.Controls)
        {
            try
            {
                var oldFont = child.Font;
                child.Font = new Font(fontFamily, child.Font.Size, child.Font.Style);
                // Only dispose if it's a different object (not inherited from parent)
                if (oldFont != child.Font && !oldFont.Equals(child.Parent?.Font))
                    oldFont.Dispose();
            }
            catch { }
            ApplyFontRecursive(child, fontFamily);
        }
    }

    private void ShowWindowsNotification(string title, string message, string type)
    {
        var icon = type switch
        {
            NotificationType.Error => ToolTipIcon.Error,
            NotificationType.Success => ToolTipIcon.Info,
            NotificationType.Pending => ToolTipIcon.Warning,
            _ => ToolTipIcon.Info,
        };

        // Truncate message for balloon notification
        string truncated = message.Length > 200 ? message[..197] + "..." : message;
        // Strip markdown formatting
        truncated = truncated.Replace("`", "").Replace("*", "").Replace("#", "");

        _trayIcon.ShowBalloonTip(5000, title, truncated, icon);
    }

    private void ShowAlarmForm()
    {
        if (_alarmForm != null && !_alarmForm.IsDisposed)
        {
            _alarmForm.BringToFront();
            _alarmForm.Activate();
            return;
        }

        _alarmForm = new AlarmForm(_popupForm.CurrentTheme);
        _alarmForm.FormClosed += (_, _) => _alarmForm = null;
        var savedFont = AppSettings.Load().GlobalFont;
        if (!string.IsNullOrEmpty(savedFont) && savedFont != "Segoe UI")
            ApplyFontToForm(_alarmForm, savedFont);
        _alarmForm.Show();
    }

    private static void SyncClaudeHooks(AppSettingsData settings)
    {
        try
        {
            SettingsForm.UpdateClaudeHook("Stop", null, settings.HookStopEnabled);
            SettingsForm.UpdateClaudeHook("Notification", "permission_prompt|idle_prompt|elicitation_dialog", settings.HookNotificationEnabled);
            SettingsForm.UpdateClaudeHook("UserPromptSubmit", null, settings.HookUserPromptEnabled);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SyncClaudeHooks failed: {ex.Message}");
        }
    }

    private void ExitApp()
    {
        _cts.Cancel();
        UpdateChecker.Stop();
        AlarmScheduler.Stop();
        AlarmStore.StopHistoryFlush();
        AlarmNotifier.Cleanup();
        _globalHotkey.Dispose();
        _tripleCtrl.Dispose();
        _alarmForm?.Close();
        _settingsForm?.Close();
        if (_editorForm != null && !_editorForm.IsDisposed)
        {
            _editorForm.Dispose();
            _editorForm = null;
        }
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _appIcon.Dispose();
        _popupForm.ForceExit();
        ExitThread();
    }

    private async Task PipeServerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    Program.PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                // Read with size limit to prevent unbounded memory allocation
                const int MaxMessageSize = 10 * 1024 * 1024; // 10MB
                using var reader = new StreamReader(server, Encoding.UTF8);
                var buffer = new char[MaxMessageSize];
                int charsRead = await reader.ReadAsync(buffer, 0, buffer.Length);
                var json = new string(buffer, 0, charsRead);

                if (!string.IsNullOrEmpty(json))
                {
                    PipeMessage? msg;
                    try { msg = JsonSerializer.Deserialize<PipeMessage>(json); }
                    catch { msg = null; }
                    if (msg != null && AppSettings.Load().NotificationsEnabled)
                    {
                        var mode = AppSettings.Load().NotificationMode;
                        var title = msg.title ?? "ProdToy";
                        var message = msg.message ?? "Task completed.";
                        var type = msg.type ?? NotificationType.Info;

                        if (mode is "Popup" or "Popup + Windows")
                        {
                            _popupForm.Invoke(() => _popupForm.ShowPopup(
                                title, message, type,
                                msg.sessionId ?? "",
                                msg.cwd ?? ""));
                        }

                        if (mode is "Windows" or "Popup + Windows")
                        {
                            _popupForm.Invoke(() => ShowWindowsNotification(title, message, type));
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Debug.WriteLine($"Pipe server error: {ex.Message}");
                await Task.Delay(100, ct);
            }
        }
    }

    private record PipeMessage(string? title, string? message, string? type, string? sessionId, string? cwd);
}
