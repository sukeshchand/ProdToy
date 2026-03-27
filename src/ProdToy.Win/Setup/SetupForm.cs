using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ProdToy;

class SetupForm : Form
{
    private static readonly PopupTheme _theme = Themes.Default;

    private string? _webView2UserDataFolder;

    private readonly string _userProfile;
    private readonly string _toolsDir;
    private readonly string _installExePath;
    private readonly string _hooksDir;
    private readonly string _settingsPath;
    private readonly string _currentExePath;
    private readonly string _ps1Content;
    private readonly string _hookCommand;

    private readonly RoundedButton _installButton;

    public SetupForm()
    {
        Text = "ProdToy - Setup Instructions";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = _theme.BgDark;
        ClientSize = new Size(860, 740);
        Icon = SystemIcons.Information;

        _userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _toolsDir = AppPaths.Root;
        _installExePath = AppPaths.ExePath;
        _hooksDir = AppPaths.ClaudeHooksDir;
        _settingsPath = AppPaths.ClaudeSettingsFile;
        _currentExePath = Application.ExecutablePath;

        string hooksDirJsonEscaped = _hooksDir.Replace("\\", "\\\\");
        _hookCommand = $"powershell.exe -ExecutionPolicy Bypass -File \\\"{hooksDirJsonEscaped}\\\\Show-ProdToy.ps1\\\"";

        _ps1Content = BuildPs1Content(_installExePath);

        // --- Top panel with install button ---
        var topPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 130,
            BackColor = _theme.BgHeader,
            Padding = new Padding(0),
        };

        var titleLabel = new Label
        {
            Text = "ProdToy Setup",
            Font = new Font("Segoe UI Semibold", 18f, FontStyle.Bold),
            ForeColor = _theme.TextPrimary,
            AutoSize = true,
            Location = new Point(28, 16),
            BackColor = Color.Transparent,
        };

        var descLabel = new Label
        {
            Text = "Install the popup, hook script, and configure ProdToy — all in one click.",
            Font = new Font("Segoe UI", 10f),
            ForeColor = _theme.TextSecondary,
            AutoSize = true,
            Location = new Point(28, 46),
            BackColor = Color.Transparent,
        };

        _installButton = new RoundedButton
        {
            Text = "\u26A1  Install Automatically",
            Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
            Size = new Size(240, 44),
            Location = new Point(28, 76),
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.Primary,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
        };
        _installButton.FlatAppearance.BorderSize = 0;
        _installButton.FlatAppearance.MouseOverBackColor = _theme.PrimaryLight;
        _installButton.FlatAppearance.MouseDownBackColor = _theme.PrimaryDim;
        _installButton.Click += OnInstallClick;

        var accentLine = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 2,
            BackColor = _theme.Primary,
        };

        topPanel.Controls.AddRange(new Control[] { titleLabel, descLabel, _installButton, accentLine });

        // --- WebView for manual instructions below ---
        var webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = _theme.BgDark,
        };

        // Add controls (order matters for Dock: Fill added first, Top overlays)
        Controls.Add(webView);
        Controls.Add(topPanel);

        string htmlContent = BuildSetupHtml(_currentExePath, _installExePath, _toolsDir, _hooksDir, _settingsPath, _ps1Content, _hookCommand);

        Shown += async (_, _) =>
        {
            NativeMethods.ShowWindow(Handle, NativeMethods.SW_RESTORE);
            NativeMethods.SetForegroundWindow(Handle);
            BringToFront();
            Activate();

            try
            {
                _webView2UserDataFolder = Path.Combine(Path.GetTempPath(), "ProdToy_Setup_" + Guid.NewGuid().ToString("N"));
                var env = await CoreWebView2Environment.CreateAsync(null, _webView2UserDataFolder);
                await webView.EnsureCoreWebView2Async(env);
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                webView.NavigateToString(htmlContent);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetupForm WebView2 init failed: {ex.Message}");
                // WebView2 not available — hide silently, button still works
                webView.Visible = false;
            }
        };

        FormClosed += (_, _) =>
        {
            try
            {
                if (_webView2UserDataFolder != null && Directory.Exists(_webView2UserDataFolder))
                    Directory.Delete(_webView2UserDataFolder, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetupForm WebView2 cleanup failed: {ex.Message}");
            }
        };
    }

    private async void OnInstallClick(object? sender, EventArgs e)
    {
        _installButton.Enabled = false;
        _installButton.Text = "Installing...";
        _installButton.BackColor = _theme.PrimaryDim;

        try
        {
            var result = await Task.Run(() => RunAutoInstall());

            if (result.Success)
            {
                _installButton.Text = "\u2713  Installed Successfully";
                _installButton.BackColor = _theme.SuccessColor;

                MessageBox.Show(this,
                    result.Message,
                    "Installation Complete",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else
            {
                _installButton.Text = "Installation Failed";
                _installButton.BackColor = _theme.ErrorColor;

                MessageBox.Show(this,
                    result.Message,
                    "Installation Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                // Re-enable for retry
                _installButton.Enabled = true;
                _installButton.Text = "\u26A1  Retry Installation";
                _installButton.BackColor = _theme.Primary;
            }
        }
        catch (Exception ex)
        {
            _installButton.Text = "Installation Failed";
            _installButton.BackColor = _theme.ErrorColor;

            MessageBox.Show(this,
                $"Error: {ex.Message}",
                "Installation Failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            _installButton.Enabled = true;
            _installButton.Text = "\u26A1  Retry Installation";
            _installButton.BackColor = _theme.Primary;
        }
    }

    private record InstallResult(bool Success, string Message);

    private InstallResult RunAutoInstall()
    {
        var log = new StringBuilder();
        string? backupPath = null;

        try
        {
            // Step 1: Copy exe to ~/.dev-toy/
            Directory.CreateDirectory(_toolsDir);
            File.Copy(_currentExePath, _installExePath, overwrite: true);
            log.AppendLine($"Installed to {_installExePath}");

            // Step 2: Apply defaults from defaultSettings.json (if present next to installer)
            ApplyDefaultSettings(log);

            // Step 3: Create hooks directory and write PS1 script
            Directory.CreateDirectory(_hooksDir);
            string ps1Path = Path.Combine(_hooksDir, "Show-ProdToy.ps1");
            File.WriteAllText(ps1Path, _ps1Content, Encoding.UTF8);
            log.AppendLine($"Hook script created at {ps1Path}");

            // Step 4: Backup and merge settings.json
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

            log.AppendLine();
            log.Append("Please restart all running Claude Code instances for hooks to take effect.");

            return new InstallResult(true, log.ToString());
        }
        catch (Exception ex)
        {
            log.AppendLine($"Error: {ex.Message}");
            if (backupPath != null)
                log.AppendLine($"Original settings backed up at {backupPath}");
            return new InstallResult(false, log.ToString());
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
                    ? defaults.UpdateLocation
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

    private static string BuildPs1Content(string installExePath)
    {
        return $@"[Console]::InputEncoding = [System.Text.Encoding]::UTF8
$inputJson = [Console]::In.ReadToEnd()

$title     = ""ProdToy""
$message   = ""Task finished.""
$type      = ""success""
$sessionId = """"
$cwd       = """"

$exePath = ""{installExePath}""

if ($inputJson) {{
    try {{
        $payload = $inputJson | ConvertFrom-Json

        # Extract session context
        if ($payload.session_id) {{ $sessionId = $payload.session_id }}
        if ($payload.cwd)        {{ $cwd = $payload.cwd }}

        if ($payload.hook_event_name -eq ""UserPromptSubmit"") {{
            # Save question to history via ProdToy and exit
            if ($payload.prompt) {{
                $qFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), ""prodtoy_question.txt"")
                [System.IO.File]::WriteAllText($qFile, $payload.prompt, [System.Text.Encoding]::UTF8)
                $qArgs = @(""--save-question"", ""`""$qFile`"""")
                if ($sessionId) {{ $qArgs += ""--session-id"", $sessionId }}
                if ($cwd)       {{ $qArgs += ""--cwd"", ""`""$cwd`"""" }}
                Start-Process -FilePath $exePath -ArgumentList $qArgs -WindowStyle Hidden
            }}
            exit 0
        }}
        elseif ($payload.hook_event_name -eq ""Notification"") {{
            if ($payload.title)   {{ $title = $payload.title }}
            if ($payload.message) {{ $message = $payload.message }}
            $type = ""info""
        }}
        elseif ($payload.hook_event_name -eq ""Stop"") {{
            $title = ""ProdToy - Done""
            if ($payload.last_assistant_message) {{
                $message = $payload.last_assistant_message
            }} else {{
                $message = ""Task finished.""
            }}
            $type = ""success""
        }}
    }}
    catch {{ }}
}}

$msgFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), ""prodtoy_msg.txt"")
[System.IO.File]::WriteAllText($msgFile, $message, [System.Text.Encoding]::UTF8)

$argList = @(""--title"", ""`""$title`"""", ""--message-file"", ""`""$msgFile`"""", ""--type"", $type)
if ($sessionId) {{ $argList += ""--session-id"", $sessionId }}
if ($cwd)       {{ $argList += ""--cwd"", ""`""$cwd`"""" }}
Start-Process -FilePath $exePath -ArgumentList $argList -WindowStyle Hidden";
    }

    private static string BuildSetupHtml(
        string currentExePath, string installExePath, string toolsDir,
        string hooksDir, string settingsPath, string ps1Content, string hookCommand)
    {
        string hooksDirJsonEscaped = hooksDir.Replace("\\", "\\\\");
        string settingsJson = @"{
  ""hooks"": {
    ""Stop"": [
      {
        ""hooks"": [
          {
            ""type"": ""command"",
            ""command"": ""powershell.exe -ExecutionPolicy Bypass -File \""HOOKS_DIR_PLACEHOLDER\\Show-ProdToy.ps1\""""
          }
        ]
      }
    ],
    ""Notification"": [
      {
        ""matcher"": ""permission_prompt|idle_prompt|elicitation_dialog"",
        ""hooks"": [
          {
            ""type"": ""command"",
            ""command"": ""powershell.exe -ExecutionPolicy Bypass -File \""HOOKS_DIR_PLACEHOLDER\\Show-ProdToy.ps1\""""
          }
        ]
      }
    ]
  }
}";
        settingsJson = settingsJson.Replace("HOOKS_DIR_PLACEHOLDER", hooksDirJsonEscaped);

        string copyCmd = $@"New-Item -ItemType Directory -Force -Path ""{toolsDir}""
Copy-Item -Path ""{currentExePath}"" -Destination ""{installExePath}"" -Force";

        string ps1Html = WebUtility.HtmlEncode(ps1Content);
        string settingsHtml = WebUtility.HtmlEncode(settingsJson);
        string copyCmdHtml = WebUtility.HtmlEncode(copyCmd);

        return $@"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8""/>
<style>
    * {{ margin: 0; padding: 0; box-sizing: border-box; }}
    body {{
        font-family: 'Segoe UI', -apple-system, sans-serif;
        font-size: 15px;
        color: #c0cde8;
        background: #0f141e;
        padding: 28px 40px;
        line-height: 1.7;
    }}
    h2 {{
        font-size: 18px;
        color: #ebf0ff;
        margin: 24px 0 12px 0;
        font-weight: 600;
        display: flex;
        align-items: center;
        gap: 10px;
    }}
    h2 .step {{
        display: inline-flex;
        align-items: center;
        justify-content: center;
        width: 28px;
        height: 28px;
        border-radius: 50%;
        background: linear-gradient(135deg, #3884f4, #2563eb);
        color: #fff;
        font-size: 13px;
        font-weight: 700;
        flex-shrink: 0;
    }}
    p {{ margin: 8px 0; }}
    .section-title {{
        font-size: 16px;
        color: #8094c0;
        margin-bottom: 8px;
        font-weight: 600;
        text-transform: uppercase;
        letter-spacing: 1px;
    }}
    .path {{
        color: #60a5fa;
        font-family: 'Cascadia Code', 'Consolas', monospace;
        font-size: 13px;
        background: rgba(56, 132, 244, 0.08);
        padding: 2px 8px;
        border-radius: 4px;
        border: 1px solid rgba(56, 132, 244, 0.15);
        word-break: break-all;
    }}
    .code-block {{
        position: relative;
        background: #0a0f1a;
        border: 1px solid #1e3755;
        border-radius: 8px;
        margin: 12px 0 16px 0;
        overflow: hidden;
    }}
    .code-header {{
        display: flex;
        justify-content: space-between;
        align-items: center;
        padding: 8px 14px;
        background: #111827;
        border-bottom: 1px solid #1e3755;
        font-size: 12px;
        color: #6888b8;
    }}
    .code-header .lang {{
        font-family: 'Cascadia Code', 'Consolas', monospace;
        text-transform: uppercase;
        letter-spacing: 0.5px;
    }}
    .copy-btn {{
        background: rgba(56, 132, 244, 0.15);
        color: #60a5fa;
        border: 1px solid rgba(56, 132, 244, 0.3);
        padding: 4px 14px;
        border-radius: 4px;
        cursor: pointer;
        font-size: 12px;
        font-family: 'Segoe UI', sans-serif;
        font-weight: 600;
        transition: all 0.2s;
    }}
    .copy-btn:hover {{
        background: rgba(56, 132, 244, 0.25);
        border-color: rgba(56, 132, 244, 0.5);
    }}
    .copy-btn.copied {{
        background: rgba(52, 211, 153, 0.15);
        color: #34d399;
        border-color: rgba(52, 211, 153, 0.3);
    }}
    pre {{
        padding: 14px 18px;
        overflow-x: auto;
        font-family: 'Cascadia Code', 'Consolas', monospace;
        font-size: 13px;
        line-height: 1.6;
        color: #a0b8d8;
        tab-size: 4;
    }}
    .info-box {{
        background: rgba(56, 132, 244, 0.06);
        border: 1px solid rgba(56, 132, 244, 0.2);
        border-radius: 8px;
        padding: 14px 18px;
        margin: 12px 0;
        font-size: 14px;
    }}
    .info-box .label {{
        color: #60a5fa;
        font-weight: 600;
        margin-right: 6px;
    }}
    .warn-box {{
        background: rgba(251, 191, 36, 0.06);
        border: 1px solid rgba(251, 191, 36, 0.2);
        border-radius: 8px;
        padding: 14px 18px;
        margin: 12px 0;
        font-size: 14px;
    }}
    .warn-box .label {{
        color: #fbbf24;
        font-weight: 600;
        margin-right: 6px;
    }}
    .divider {{
        border: none;
        border-top: 1px solid #1e3755;
        margin: 24px 0;
    }}
    .usage-grid {{
        display: grid;
        grid-template-columns: 1fr 1fr 1fr;
        gap: 12px;
        margin: 12px 0;
    }}
    .usage-card {{
        background: #111827;
        border: 1px solid #1e3755;
        border-radius: 8px;
        padding: 14px;
    }}
    .usage-card .flag {{
        color: #60a5fa;
        font-family: 'Cascadia Code', 'Consolas', monospace;
        font-size: 14px;
        font-weight: 600;
        margin-bottom: 4px;
    }}
    .usage-card .desc {{
        color: #6888b8;
        font-size: 13px;
    }}
    .usage-card .vals {{
        color: #a0b8d8;
        font-family: 'Cascadia Code', 'Consolas', monospace;
        font-size: 12px;
        margin-top: 4px;
    }}
    ::-webkit-scrollbar {{ width: 8px; height: 8px; }}
    ::-webkit-scrollbar-track {{ background: transparent; }}
    ::-webkit-scrollbar-thumb {{ background: rgba(56, 132, 244, 0.3); border-radius: 4px; }}
    ::-webkit-scrollbar-thumb:hover {{ background: rgba(56, 132, 244, 0.5); }}
</style>
</head>
<body>

<p class=""section-title"">Manual Setup</p>
<p style=""color: #6888b8;"">If you prefer to set things up manually, follow these steps:</p>

<h2><span class=""step"">1</span> Install the executable</h2>
<p>Copy ProdToy to <span class=""path"">{WebUtility.HtmlEncode(toolsDir)}</span>:</p>
<div class=""code-block"">
    <div class=""code-header"">
        <span class=""lang"">PowerShell</span>
        <button class=""copy-btn"" onclick=""copyBlock(this)"">Copy</button>
    </div>
    <pre>{copyCmdHtml}</pre>
</div>
<p>The exe will be at: <span class=""path"">{WebUtility.HtmlEncode(installExePath)}</span></p>

<hr class=""divider""/>

<h2><span class=""step"">2</span> Create the PowerShell hook script</h2>
<p>Save this file as: <span class=""path"">{WebUtility.HtmlEncode(hooksDir)}\\Show-ProdToy.ps1</span></p>
<div class=""code-block"">
    <div class=""code-header"">
        <span class=""lang"">PowerShell</span>
        <button class=""copy-btn"" onclick=""copyBlock(this)"">Copy</button>
    </div>
    <pre>{ps1Html}</pre>
</div>

<hr class=""divider""/>

<h2><span class=""step"">3</span> Configure ProdToy hooks</h2>
<p>Add this to your settings file: <span class=""path"">{WebUtility.HtmlEncode(settingsPath)}</span></p>
<div class=""info-box"">
    <span class=""label"">Tip:</span> If you already have hooks configured, merge the <code>Stop</code> and <code>Notification</code> entries into your existing <code>hooks</code> object.
</div>
<div class=""code-block"">
    <div class=""code-header"">
        <span class=""lang"">JSON</span>
        <button class=""copy-btn"" onclick=""copyBlock(this)"">Copy</button>
    </div>
    <pre>{settingsHtml}</pre>
</div>

<hr class=""divider""/>

<h2><span class=""step"">4</span> Test it</h2>
<p>Run the exe with arguments to verify it works:</p>
<div class=""code-block"">
    <div class=""code-header"">
        <span class=""lang"">PowerShell</span>
        <button class=""copy-btn"" onclick=""copyBlock(this)"">Copy</button>
    </div>
    <pre>& ""{WebUtility.HtmlEncode(installExePath)}"" --title ""Test"" --message ""Hello from ProdToy!"" --type success</pre>
</div>

<hr class=""divider""/>

<h2>Command-line usage</h2>
<div class=""usage-grid"">
    <div class=""usage-card"">
        <div class=""flag"">--title, -t</div>
        <div class=""desc"">Window title text</div>
        <div class=""vals"">Default: &quot;ProdToy&quot;</div>
    </div>
    <div class=""usage-card"">
        <div class=""flag"">--message, -m</div>
        <div class=""desc"">Body content (supports Markdown)</div>
        <div class=""vals"">Default: &quot;Task completed.&quot;</div>
    </div>
    <div class=""usage-card"">
        <div class=""flag"">--type</div>
        <div class=""desc"">Visual theme of the popup</div>
        <div class=""vals"">info | success | error</div>
    </div>
</div>

<hr class=""divider""/>

<h2>Hook events</h2>
<div class=""info-box"">
    <span class=""label"">Stop:</span> Fires when Claude finishes a response. The popup shows the last assistant message.
</div>
<div class=""info-box"">
    <span class=""label"">Notification:</span> Fires when Claude needs attention (permission prompts, idle prompts). Shows the notification title and message.
</div>

<div class=""warn-box"">
    <span class=""label"">Note:</span> Requires <a href=""https://developer.microsoft.com/en-us/microsoft-edge/webview2/"" style=""color:#fbbf24"">Microsoft Edge WebView2 Runtime</a> (pre-installed on Windows 10/11).
</div>

<script>
function copyBlock(btn) {{
    const pre = btn.closest('.code-block').querySelector('pre');
    const text = pre.textContent;
    navigator.clipboard.writeText(text).then(() => {{
        btn.textContent = 'Copied!';
        btn.classList.add('copied');
        setTimeout(() => {{
            btn.textContent = 'Copy';
            btn.classList.remove('copied');
        }}, 2000);
    }});
}}
</script>

</body>
</html>";
    }
}
