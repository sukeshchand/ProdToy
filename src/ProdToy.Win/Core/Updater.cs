using System.Diagnostics;
using System.Text;

namespace ProdToy;

static class Updater
{
    public record UpdateResult(bool Success, string Message);

    public static UpdateResult Apply()
    {
        try
        {
            var settings = AppSettings.Load();
            if (string.IsNullOrWhiteSpace(settings.UpdateLocation))
                return new UpdateResult(false, "No update location configured.");

            string sourceExe = Path.Combine(settings.UpdateLocation, "ProdToy.exe");
            if (!File.Exists(sourceExe))
                return new UpdateResult(false, $"Update file not found at {sourceExe}");

            string installDir = Path.GetDirectoryName(Application.ExecutablePath)!;
            string currentExe = Application.ExecutablePath;
            string currentExeName = Path.GetFileName(currentExe);
            string updateExe = Path.Combine(installDir, "ProdToy.update.exe");
            string scriptPath = Path.Combine(installDir, "_update.ps1");
            int currentPid = Environment.ProcessId;

            // Step 1: Copy new exe from network to local staging file
            File.Copy(sourceExe, updateExe, overwrite: true);

            // Step 2: Write the updater PowerShell script
            string ps1 = $@"
# ProdToy Auto-Updater
# Wait for the original process to exit, then swap the exe and relaunch.

$exePath   = '{currentExe.Replace("'", "''")}'
$exeName   = '{currentExeName.Replace("'", "''")}'
$updateExe = '{updateExe.Replace("'", "''")}'
$installDir = '{installDir.Replace("'", "''")}'
$targetPid  = {currentPid}
$scriptPath = '{scriptPath.Replace("'", "''")}'

# Phase 1: Wait for the process to exit (check every 2 sec, up to 10 sec)
$waited = 0
while ($waited -lt 10) {{
    $proc = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
    if (-not $proc) {{ break }}
    Start-Sleep -Seconds 2
    $waited += 2
}}

# Phase 2: If still running, try to kill it (up to 10 attempts)
for ($attempt = 1; $attempt -le 10; $attempt++) {{
    $proc = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
    if (-not $proc) {{ break }}
    try {{
        Stop-Process -Id $targetPid -Force -ErrorAction Stop
        Start-Sleep -Seconds 1
    }} catch {{
        Start-Sleep -Seconds 1
    }}
}}

# Phase 3: Swap the exe
Start-Sleep -Milliseconds 500
if (Test-Path $exePath) {{
    Remove-Item $exePath -Force -ErrorAction SilentlyContinue
}}
if (Test-Path $updateExe) {{
    Rename-Item $updateExe $exeName -Force
}}

# Phase 4: Relaunch
if (Test-Path $exePath) {{
    Start-Process -FilePath $exePath
}}

# Phase 5: Self-cleanup
Start-Sleep -Seconds 2
Remove-Item $scriptPath -Force -ErrorAction SilentlyContinue
";

            File.WriteAllText(scriptPath, ps1, Encoding.UTF8);

            // Step 3: Launch the PowerShell script and exit
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = installDir,
            });

            return new UpdateResult(true, "Update started. Application will restart.");
        }
        catch (Exception ex)
        {
            return new UpdateResult(false, $"Update failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Ensures the hook script on disk matches this exe's version.
    /// Called on startup so the new exe always writes the latest script.
    /// </summary>
    public static void EnsureHookScript(string exePath)
    {
        try
        {
            string hooksDir = AppPaths.ClaudeHooksDir;
            string ps1Path = Path.Combine(hooksDir, "Show-ProdToy.ps1");

            Directory.CreateDirectory(hooksDir);

            string ps1Content = $@"[Console]::InputEncoding = [System.Text.Encoding]::UTF8
$inputJson = [Console]::In.ReadToEnd()

$title     = ""ProdToy""
$message   = ""Task finished.""
$type      = ""success""
$sessionId = """"
$cwd       = """"

$exePath = ""{exePath}""

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

            File.WriteAllText(ps1Path, ps1Content, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to regenerate hook script: {ex.Message}");
        }
    }
}
