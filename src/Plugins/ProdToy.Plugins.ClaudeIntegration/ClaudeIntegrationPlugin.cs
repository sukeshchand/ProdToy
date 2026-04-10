using System.Drawing;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ClaudeIntegration;

[Plugin("ProdToy.Plugin.ClaudeIntegration", "Claude Integration", "1.0.244",
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

        // Sync Claude hooks with plugin settings on startup
        ClaudeHookManager.UpdateClaudeHook("Stop", null, settings.HookStopEnabled);
        ClaudeHookManager.UpdateClaudeHook("Notification",
            "permission_prompt|idle_prompt|elicitation_dialog", settings.HookNotificationEnabled);
        ClaudeHookManager.UpdateClaudeHook("UserPromptSubmit", null, settings.HookUserPromptEnabled);

        // Cleanup legacy hooks
        ClaudeHookManager.CleanupOldHook();

        // Sync auto-title if enabled
        if (settings.AutoTitleToFolder)
            ClaudeHookManager.SetAutoTitleHook(true);
    }

    public void Stop() { }

    public void Dispose() { }

    public IReadOnlyList<MenuContribution> GetMenuItems() => [];

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
