[Console]::InputEncoding = [System.Text.Encoding]::UTF8
$inputJson = [Console]::In.ReadToEnd()

$title     = "ProdToy"
$message   = "Task finished."
$type      = "success"
$sessionId = ""
$cwd       = ""

$exePath = "{{EXE_PATH}}"

if ($inputJson) {
    try {
        $payload = $inputJson | ConvertFrom-Json

        if ($payload.session_id) { $sessionId = $payload.session_id }
        if ($payload.cwd)        { $cwd = $payload.cwd }

        if ($payload.hook_event_name -eq "UserPromptSubmit") {
            # Save question via the plugin's claude.save-question command.
            if ($payload.prompt) {
                $qPayload = @{
                    question  = [string]$payload.prompt
                    sessionId = $sessionId
                    cwd       = $cwd
                } | ConvertTo-Json -Compress

                $qEnvelope = @{
                    command = "claude.save-question"
                    payload = $qPayload
                } | ConvertTo-Json -Compress

                $qFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "prodtoy_save_question.json")
                [System.IO.File]::WriteAllText($qFile, $qEnvelope, [System.Text.Encoding]::UTF8)
                Start-Process -FilePath $exePath -ArgumentList @("--command", "claude.save-question", "--payload-file", "`"$qFile`"") -WindowStyle Hidden
            }
            exit 0
        }
        elseif ($payload.hook_event_name -eq "Notification") {
            if ($payload.title)   { $title = $payload.title }
            if ($payload.message) { $message = $payload.message }
            $type = "info"
        }
        elseif ($payload.hook_event_name -eq "Stop") {
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

# Send notify envelope via --payload-file to avoid command-line length limits.
$notifyPayload = @{
    title     = [string]$title
    message   = [string]$message
    type      = [string]$type
    sessionId = $sessionId
    cwd       = $cwd
} | ConvertTo-Json -Compress

$nFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "prodtoy_notify.json")
[System.IO.File]::WriteAllText($nFile, $notifyPayload, [System.Text.Encoding]::UTF8)
Start-Process -FilePath $exePath -ArgumentList @("--command", "claude.notify", "--payload-file", "`"$nFile`"") -WindowStyle Hidden
