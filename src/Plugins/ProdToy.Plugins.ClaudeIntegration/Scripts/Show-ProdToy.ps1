# ProdToy Claude Code hook script.
#
# Reads the event JSON from stdin, checks the plugin's runtime settings, and
# dispatches a notify envelope to the running ProdToy host via the named pipe.
#
# Gate order (any fail → exit 0 without doing anything):
#   1. Plugin settings file exists and is readable.
#   2. HostRunning is true AND the named pipe is responding.
#   3. NotificationsEnabled is true.
#   4. The per-event hook flag (HookStopEnabled, HookNotificationEnabled,
#      HookUserPromptEnabled) is true.

[Console]::InputEncoding = [System.Text.Encoding]::UTF8
$inputJson = [Console]::In.ReadToEnd()

$exePath      = "{{EXE_PATH}}"
$settingsPath = "{{SETTINGS_PATH}}"
$pipeName     = "{{PIPE_NAME}}"

# --- Read plugin settings ---
$settings = $null
if (Test-Path -LiteralPath $settingsPath) {
    try { $settings = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json } catch { }
}
if ($null -eq $settings) { exit 0 }

function Get-SettingBool($obj, $name, $default) {
    if ($null -eq $obj) { return $default }
    $prop = $obj.PSObject.Properties[$name]
    if ($null -eq $prop) { return $default }
    return [bool]$prop.Value
}

$notificationsEnabled    = Get-SettingBool $settings "notificationsEnabled" $true
$hookStopEnabled         = Get-SettingBool $settings "hookStopEnabled" $true
$hookNotificationEnabled = Get-SettingBool $settings "hookNotificationEnabled" $false
$hookUserPromptEnabled   = Get-SettingBool $settings "hookUserPromptEnabled" $true
$hostRunningFlag         = Get-SettingBool $settings "hostRunning" $false

if (-not $notificationsEnabled) { exit 0 }

# --- Host-running check: flag + pipe probe ---
function Test-HostRunning() {
    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::Out)
        try {
            $pipe.Connect(150)
            return $pipe.IsConnected
        } finally {
            $pipe.Dispose()
        }
    } catch {
        return $false
    }
}

if (-not $hostRunningFlag) {
    # Flag says off → trust it (fast path).
    exit 0
}
if (-not (Test-HostRunning)) {
    # Flag says on but pipe is dead (crash or not-yet-reachable). Bail quietly.
    exit 0
}

# --- Parse the Claude event ---
$title     = "ProdToy"
$message   = "Task finished."
$type      = "success"
$sessionId = ""
$cwd       = ""
$transcriptPath = ""
$eventName = ""
$notificationType = ""

if ($inputJson) {
    try {
        $payload = $inputJson | ConvertFrom-Json
        $eventName = [string]$payload.hook_event_name

        if ($payload.session_id) { $sessionId = $payload.session_id }
        if ($payload.cwd)        { $cwd = $payload.cwd }
        # The transcript sits under the CLI's folder for where it was opened; ProdToy groups by that folder.
        if ($payload.transcript_path) { $transcriptPath = [string]$payload.transcript_path }

        if ($eventName -eq "UserPromptSubmit") {
            if (-not $hookUserPromptEnabled) { exit 0 }

            if ($payload.prompt) {
                # Write only the inner payload. Program.cs's SendEnvelopeToPipe
                # wraps it in the {command, payload} envelope. Pre-wrapping here
                # would produce a doubly-wrapped envelope that PipeRouter strips
                # once, leaving OnSaveQuestionCommand with the outer shape
                # ({command, payload}) instead of the {question, sessionId, cwd}
                # shape it deserializes — so SaveQuestion would never fire.
                $qPayload = @{
                    question  = [string]$payload.prompt
                    sessionId = $sessionId
                    cwd       = $cwd
                    transcriptPath = $transcriptPath
                } | ConvertTo-Json -Compress

                $qFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "prodtoy_save_question.json")
                [System.IO.File]::WriteAllText($qFile, $qPayload, [System.Text.Encoding]::UTF8)
                Start-Process -FilePath $exePath -ArgumentList @("--command", "claude.save-question", "--payload-file", "`"$qFile`"") -WindowStyle Hidden
            }
            exit 0
        }
        elseif ($eventName -eq "Notification") {
            if (-not $hookNotificationEnabled) { exit 0 }
            if ($payload.title)   { $title = $payload.title }
            if ($payload.message) { $message = $payload.message }
            if ($payload.notification_type) { $notificationType = [string]$payload.notification_type }
            $type = "info"
        }
        elseif ($eventName -eq "Stop") {
            if (-not $hookStopEnabled) { exit 0 }
            $title = "ProdToy - Done"
            if ($payload.last_assistant_message) {
                $message = $payload.last_assistant_message
            } else {
                $message = "Task finished."
            }
            $type = "success"
        }
    }
    catch { }
}

# --- Send notify envelope ---
$notifyPayload = @{
    title     = [string]$title
    message   = [string]$message
    type      = [string]$type
    sessionId = $sessionId
    cwd       = $cwd
    hookEvent = [string]$eventName
    notificationType = $notificationType
    transcriptPath = $transcriptPath
} | ConvertTo-Json -Compress

$nFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "prodtoy_notify.json")
[System.IO.File]::WriteAllText($nFile, $notifyPayload, [System.Text.Encoding]::UTF8)
Start-Process -FilePath $exePath -ArgumentList @("--command", "claude.notify", "--payload-file", "`"$nFile`"") -WindowStyle Hidden
