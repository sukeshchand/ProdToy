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

        # Extract session context
        if ($payload.session_id) { $sessionId = $payload.session_id }
        if ($payload.cwd)        { $cwd = $payload.cwd }

        if ($payload.hook_event_name -eq "UserPromptSubmit") {
            # Save question to history via ProdToy and exit
            if ($payload.prompt) {
                $qFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "prodtoy_question.txt")
                [System.IO.File]::WriteAllText($qFile, $payload.prompt, [System.Text.Encoding]::UTF8)
                $qArgs = @("--save-question", "`"$qFile`"")
                if ($sessionId) { $qArgs += "--session-id", $sessionId }
                if ($cwd)       { $qArgs += "--cwd", "`"$cwd`"" }
                Start-Process -FilePath $exePath -ArgumentList $qArgs -WindowStyle Hidden
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

$msgFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "prodtoy_msg.txt")
[System.IO.File]::WriteAllText($msgFile, $message, [System.Text.Encoding]::UTF8)

$argList = @("--title", "`"$title`"", "--message-file", "`"$msgFile`"", "--type", $type)
if ($sessionId) { $argList += "--session-id", $sessionId }
if ($cwd)       { $argList += "--cwd", "`"$cwd`"" }
Start-Process -FilePath $exePath -ArgumentList $argList -WindowStyle Hidden
