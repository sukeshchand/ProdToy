# ProdToy Claude Code status-line script.
#
# Runtime gates (evaluated in order; any fail → empty output, Claude shows
# nothing for the status line):
#   1. Plugin settings.json is readable.
#   2. SlEnabled flag is true.
#   3. HostRunning flag is true AND the named pipe is responding (crash probe).
# If all three pass, render the status line from the event JSON on stdin and
# the per-item visibility settings in status-line-config.json.

$settingsPath = "{{SETTINGS_PATH}}"
$pipeName     = "{{PIPE_NAME}}"

# --- Plugin settings gate ---
$pluginSettings = $null
if (Test-Path -LiteralPath $settingsPath) {
    try { $pluginSettings = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json } catch { }
}
if ($null -eq $pluginSettings) { exit 0 }

function Get-SettingBool($obj, $name, $default) {
    if ($null -eq $obj) { return $default }
    $prop = $obj.PSObject.Properties[$name]
    if ($null -eq $prop) { return $default }
    return [bool]$prop.Value
}

$slEnabled       = Get-SettingBool $pluginSettings "slEnabled" $true
$hostRunningFlag = Get-SettingBool $pluginSettings "hostRunning" $false

if (-not $slEnabled) { exit 0 }

# --- Host-running check (crash-safe): flag says on + pipe is reachable. ---
if (-not $hostRunningFlag) { exit 0 }
try {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::Out)
    try {
        $pipe.Connect(150)
        if (-not $pipe.IsConnected) { exit 0 }
    } finally {
        $pipe.Dispose()
    }
} catch {
    exit 0
}

$ESC = [char]27
$C_LABEL  = "$ESC[38;5;141m"   # soft purple — label keys
$C_VALUE  = "$ESC[38;5;78m"    # medium green — values
$C_SEP    = "$ESC[38;5;239m"   # dark gray — separators
$C_GREEN  = "$ESC[38;5;78m"    # green — default mode
$C_YELLOW = "$ESC[38;5;220m"   # yellow — acceptEdits mode / dev branches
$C_RED    = "$ESC[38;5;203m"   # red — bypassPermissions mode / main|master branches
$C_NONE   = "$ESC[38;5;109m"   # muted teal — no branch
$C_WHITE  = "$ESC[38;5;231m"  # bright white — upgrade notice
$C_RESET  = "$ESC[0m"

# Read config for item visibility
$cfg = @{
    model = $true; dir = $true; branch = $true
    prompts = $true; context = $true; duration = $true
    mode = $true; version = $true; editStats = $true
}
$cfgPath = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "status-line-config.json"
if (Test-Path $cfgPath) {
    try {
        $cfgData = Get-Content $cfgPath -Raw | ConvertFrom-Json
        if ($null -ne $cfgData.model) { $cfg.model = [bool]$cfgData.model }
        if ($null -ne $cfgData.dir) { $cfg.dir = [bool]$cfgData.dir }
        if ($null -ne $cfgData.branch) { $cfg.branch = [bool]$cfgData.branch }
        if ($null -ne $cfgData.prompts) { $cfg.prompts = [bool]$cfgData.prompts }
        if ($null -ne $cfgData.context) { $cfg.context = [bool]$cfgData.context }
        if ($null -ne $cfgData.duration) { $cfg.duration = [bool]$cfgData.duration }
        if ($null -ne $cfgData.mode) { $cfg.mode = [bool]$cfgData.mode }
        if ($null -ne $cfgData.version) { $cfg.version = [bool]$cfgData.version }
        if ($null -ne $cfgData.editStats) { $cfg.editStats = [bool]$cfgData.editStats }
    } catch {}
}

try {
    $jsonData = [Console]::In.ReadToEnd() | ConvertFrom-Json
} catch {
    Write-Host "status line error"
    exit
}

$model = if ($jsonData.model.display_name) { $jsonData.model.display_name } else { "?" }
$cwd = $jsonData.cwd
$dir = if ($cwd) { Split-Path -Leaf $cwd } else { "?" }

# Git branch
$branch = ""
$dirty = 0
if ($cfg.branch -and $cwd -and (Test-Path $cwd)) {
    Push-Location $cwd
    try {
        $branch = git branch --show-current 2>$null
        if ($branch) {
            $dirty = (git --no-optional-locks status --porcelain 2>$null | Measure-Object).Count
        }
    } catch {}
    Pop-Location
}

# Transcript pass: prompt count, permission mode, edit tracking
$prompts = 0
$permMode = "default"
$lastUserLineIdx = -1
$editEvents = [System.Collections.Generic.List[hashtable]]::new()
$transcript = $jsonData.transcript_path
$lineIdx = 0

$needTranscript = $cfg.prompts -or $cfg.mode -or $cfg.editStats
if ($needTranscript -and $transcript -and (Test-Path $transcript)) {
    Get-Content $transcript | ForEach-Object {
        $line = $_

        # User messages (prompt count)
        if ($line -match '"type"\s*:\s*"user"' -and $line -notmatch '"type"\s*:\s*"tool_result"') {
            $prompts++
            $lastUserLineIdx = $lineIdx
        }

        # Permission mode
        if ($line -match '"permissionMode"') {
            $pm = [regex]::Match($line, '"permissionMode"\s*:\s*"([^"]+)"')
            if ($pm.Success) { $permMode = $pm.Groups[1].Value }
        }

        # Edit/Write tool calls
        if ($cfg.editStats -and ($line -match '"name"\s*:\s*"Edit"' -or $line -match '"name"\s*:\s*"Write"')) {
            try {
                $obj = $line | ConvertFrom-Json
                $content = $obj.message.content
                if ($null -eq $content) { $content = $obj.content }
                foreach ($item in $content) {
                    if ($item.type -eq "tool_use") {
                        if ($item.name -eq "Edit" -and $null -ne $item.input.old_string -and $null -ne $item.input.new_string) {
                            $oldLines = ($item.input.old_string -split "`n").Count
                            $newLines = ($item.input.new_string -split "`n").Count
                            $editEvents.Add(@{
                                LineIdx  = $lineIdx
                                Added    = [math]::Max(0, $newLines - $oldLines)
                                Deleted  = [math]::Max(0, $oldLines - $newLines)
                                Modified = [math]::Min($oldLines, $newLines)
                                FilePath = $item.input.file_path
                            })
                        } elseif ($item.name -eq "Write" -and $null -ne $item.input.content) {
                            $newLines = ($item.input.content -split "`n").Count
                            $editEvents.Add(@{
                                LineIdx  = $lineIdx
                                Added    = $newLines
                                Deleted  = 0
                                Modified = 0
                                FilePath = $item.input.file_path
                            })
                        }
                    }
                }
            } catch {}
        }

        $lineIdx++
    }
}

# Compute edit stats
$totalAdded = 0; $totalDeleted = 0; $totalModified = 0; $totalFilePaths = @()
$promptAdded = 0; $promptDeleted = 0; $promptModified = 0; $promptFilePaths = @()
if ($cfg.editStats) {
    foreach ($ev in $editEvents) {
        $absPath = if ([System.IO.Path]::IsPathRooted($ev.FilePath)) { $ev.FilePath } else { Join-Path $cwd $ev.FilePath }
        if (-not (Test-Path $absPath)) { continue }
        $totalAdded    += $ev.Added
        $totalDeleted  += $ev.Deleted
        $totalModified += $ev.Modified
        $totalFilePaths += $ev.FilePath
        if ($ev.LineIdx -gt $lastUserLineIdx) {
            $promptAdded    += $ev.Added
            $promptDeleted  += $ev.Deleted
            $promptModified += $ev.Modified
            $promptFilePaths += $ev.FilePath
        }
    }
}
$totalFileCount = @($totalFilePaths | Select-Object -Unique).Count
$promptFileCount = @($promptFilePaths | Select-Object -Unique).Count

# Context window usage
$ctx = ""
$ctxPct = $jsonData.context_window.used_percentage
if ($cfg.context -and $null -ne $ctxPct) {
    $ctx = "ctx ${ctxPct}%"
}
$ctxColor = if ($null -eq $ctxPct) { $C_VALUE }
            elseif ($ctxPct -lt 50)  { $C_VALUE  }
            elseif ($ctxPct -lt 75)  { $C_YELLOW }
            else                     { $C_RED    }

# Session duration
$duration = ""
if ($cfg.duration) {
    $dur_ms = if ($jsonData.cost.total_duration_ms) { [long]$jsonData.cost.total_duration_ms } else { 0 }
    if ($dur_ms -gt 0) {
        $mins = [math]::Floor($dur_ms / 60000)
        if ($mins -ge 60) {
            $h = [math]::Floor($mins / 60)
            $m = $mins % 60
            $duration = "${h}h${m}m"
        } else {
            $duration = "${mins}m"
        }
    }
}

# Edit stats formatting helper
function Format-EditStats($added, $deleted, $modified, $files) {
    $parts = [System.Collections.Generic.List[string]]::new()
    if ($added -gt 0)    { $parts.Add("${C_VALUE}${added} added") }
    if ($deleted -gt 0)  { $parts.Add("${C_RED}${deleted} deleted") }
    if ($modified -gt 0) { $parts.Add("${C_YELLOW}${modified} modified") }
    $fileWord = if ($files -eq 1) { "file" } else { "files" }
    $parts.Add("${C_NONE}${files} ${fileWord}")
    return $parts -join "${C_SEP}, "
}

# Row 1: environment
$sep = "${C_SEP} | "
$row1Parts = [System.Collections.Generic.List[string]]::new()
if ($cfg.model) { $row1Parts.Add("${C_LABEL}Model: ${C_VALUE}${model}") }
if ($cfg.dir) { $row1Parts.Add("${C_LABEL}Dir: ${C_VALUE}${dir}") }
if ($cfg.branch) {
    $branchStr = if ($branch) { $branch + $(if ($dirty -gt 0) { " *${dirty}" } else { "" }) } else { "none" }
    $branchColor = if (-not $branch) {
        $C_NONE
    } elseif ($branch -eq "main" -or $branch -eq "master") {
        $C_RED
    } elseif ($branch.StartsWith("dev")) {
        $C_YELLOW
    } else {
        $C_VALUE
    }
    $row1Parts.Add("${C_LABEL}Branch: ${branchColor}${branchStr}")
}
$row1 = $row1Parts -join $sep

# Row 2: session stats + mode
$permDisplay = switch ($permMode) {
    "default"            { @{ label = "Default";    color = $C_GREEN  } }
    "acceptEdits"        { @{ label = "Auto-Edit";  color = $C_YELLOW } }
    "bypassPermissions"  { @{ label = "Bypass All"; color = $C_RED    } }
    default              { @{ label = $permMode;    color = $C_VALUE  } }
}

$row2Parts = [System.Collections.Generic.List[string]]::new()
if ($cfg.prompts) { $row2Parts.Add("${C_LABEL}Prompts: ${C_VALUE}${prompts}") }
if ($cfg.context -and $ctx) { $row2Parts.Add("${C_LABEL}Context: ${ctxColor}${ctxPct}%") }
if ($cfg.duration -and $duration) { $row2Parts.Add("${C_LABEL}Duration: ${C_VALUE}${duration}") }
if ($cfg.mode) { $row2Parts.Add("${C_LABEL}Mode: $($permDisplay.color)$($permDisplay.label)") }

# Version info
if ($cfg.version) {
    $versionCacheFile = Join-Path $env:TEMP "claude-version-cache.json"
    $runningVersion = if ($jsonData.version) { $jsonData.version } else { "" }
    $installedVersion = ""
    $latestVersion = ""

    $cacheValid = $false
    if (Test-Path $versionCacheFile) {
        try {
            $cache = Get-Content $versionCacheFile -Raw | ConvertFrom-Json
            $cacheAge = (Get-Date) - [datetime]$cache.timestamp
            if ($cacheAge.TotalMinutes -lt 10) {
                $installedVersion = $cache.installed
                $latestVersion    = $cache.latest
                $cacheValid = $true
            }
        } catch {}
    }

    if (-not $cacheValid) {
        try {
            $installedVersion = (claude --version 2>$null).Trim() -replace '\(Claude Code\)', '' -replace '\s+', ' ' | ForEach-Object { $_.Trim() }
        } catch {}
        try {
            $latestVersion = (npm view @anthropic-ai/claude-code@latest version 2>$null).Trim()
        } catch {}
        try {
            @{ timestamp = (Get-Date -Format o); installed = $installedVersion; latest = $latestVersion } |
                ConvertTo-Json | Set-Content $versionCacheFile -Encoding UTF8
        } catch {}
    }

    if ($runningVersion) {
        $verStr = "${C_LABEL}Running: ${C_VALUE}${runningVersion}"
        if ($installedVersion -and $installedVersion -ne $runningVersion) {
            $verStr += " ${C_WHITE}(new version v${installedVersion} available)"
        } elseif ($latestVersion -and $latestVersion -ne $runningVersion) {
            $verStr += " ${C_WHITE}(new version v${latestVersion} available)"
        }
        $row2Parts.Add($verStr)
    } elseif ($installedVersion) {
        $verStr = "${C_LABEL}Installed: ${C_VALUE}${installedVersion}"
        if ($latestVersion -and $latestVersion -ne $installedVersion) {
            $verStr += " ${C_WHITE}(new version v${latestVersion} available)"
        }
        $row2Parts.Add($verStr)
    }
}
$row2 = $row2Parts -join $sep

# Row 3: edit stats
$row3 = ""
if ($cfg.editStats) {
    if ($totalFileCount -gt 0) {
        $row3 += "${C_LABEL}Session: $(Format-EditStats $totalAdded $totalDeleted $totalModified $totalFileCount)"
    }
    if ($promptFileCount -gt 0) {
        if ($row3) { $row3 += $sep }
        $row3 += "${C_LABEL}Last: $(Format-EditStats $promptAdded $promptDeleted $promptModified $promptFileCount)"
    }
}

if ($row1) { Write-Host "${row1}${C_RESET}" }
if ($row2) { Write-Host "${row2}${C_RESET}" }
if ($row3) { Write-Host "${row3}${C_RESET}" }
