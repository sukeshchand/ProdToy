
<#
.SYNOPSIS
    Publishes ProdToy with manifest-driven local deployment.

.DESCRIPTION
    Builds the host + plugins, packages each as a zip, writes a metadata.json
    that lists the host version + every plugin with its own version & notes,
    then optionally deploys the zips + manifest to a local/UNC update path.

    Local deploy tree:
        $DeployPath\
            metadata.json
            ProdToy.zip
            plugins\
                ProdToy.Plugin.Alarm.zip
                ProdToy.Plugin.Screenshot.zip
                ProdToy.Plugin.ClaudeIntegration.zip

.PARAMETER DeployPath
    Local or UNC path to deploy the manifest + zips to. If omitted, only builds
    to the local release\ folder.

.PARAMETER ReleaseNotes
    Top-level (host) release notes. Plugins inherit this when their per-plugin
    release-notes.txt file is missing or empty.

.PARAMETER SkipVersionBump
    If set, skips incrementing the host patch version.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -DeployPath "I:\ProdToy\_beta" -ReleaseNotes "Zip-based deploy"
#>
param(
    [string]$DeployPath = "",
    [string]$ReleaseNotes = "Bug fixes and improvements.",
    [switch]$SkipVersionBump
)

$ErrorActionPreference = "Stop"

$versionFile      = "src\ProdToy.Win\Core\AppVersion.cs"
$setupVersionFile = "src\ProdToy.Setup\Common\AppVersion.cs"
$projectDir       = "src\ProdToy.Win"
$setupProjectDir  = "src\ProdToy.Setup"
$releaseDir       = "release"

# --- Read current version ---
$content = Get-Content $versionFile -Raw
if ($content -match 'Current\s*=\s*"(\d+\.\d+\.\d+)"') {
    $currentVersion = $Matches[1]
} else {
    Write-Error "Could not parse version from $versionFile"
    exit 1
}

Write-Host "Current version: $currentVersion" -ForegroundColor Cyan

# --- Bump host version ---
if (-not $SkipVersionBump) {
    $parts = $currentVersion.Split('.')
    $parts[2] = [int]$parts[2] + 1
    $newVersion = $parts -join '.'

    $content = $content -replace "Current\s*=\s*""$currentVersion""", "Current = ""$newVersion"""
    Set-Content $versionFile $content -NoNewline
    Write-Host "Bumped host to: $newVersion" -ForegroundColor Green
} else {
    $newVersion = $currentVersion
    Write-Host "Skipping host version bump, staying at: $newVersion" -ForegroundColor Yellow
}

# --- Sync Setup project's AppVersion.cs to match host ---
if (Test-Path $setupVersionFile) {
    $setupContent = Get-Content $setupVersionFile -Raw
    $setupContent = $setupContent -replace 'Current\s*=\s*"\d+\.\d+\.\d+"', "Current = ""$newVersion"""
    Set-Content $setupVersionFile $setupContent -NoNewline
    Write-Host "Synced ProdToy.Setup AppVersion to: $newVersion" -ForegroundColor Green
}

# --- Build host ---
if (Test-Path "$releaseDir\ProdToy.exe") {
    Remove-Item "$releaseDir\ProdToy.exe" -Force
}
if (Test-Path "$releaseDir\ProdToy.zip") {
    Remove-Item "$releaseDir\ProdToy.zip" -Force
}

Write-Host "Publishing host..." -ForegroundColor Cyan
dotnet publish -c Release $projectDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed"
    exit 1
}

# --- Plugin definitions ---
# Each entry: Id (matches [Plugin("Id", ...)]), DisplayName (for metadata), source dir,
# and the .cs file we parse to find the version literal.
$pluginProjects = @(
    @{
        Id          = "ProdToy.Plugin.Alarm"
        DisplayName = "Alarms"
        Dir         = "src\Plugins\ProdToy.Plugins.Alarm"
        SourceFile  = "src\Plugins\ProdToy.Plugins.Alarm\AlarmPlugin.cs"
    },
    @{
        Id          = "ProdToy.Plugin.Screenshot"
        DisplayName = "Screenshot"
        Dir         = "src\Plugins\ProdToy.Plugins.Screenshot"
        SourceFile  = "src\Plugins\ProdToy.Plugins.Screenshot\ScreenshotPlugin.cs"
    },
    @{
        Id          = "ProdToy.Plugin.ClaudeIntegration"
        DisplayName = "Claude Integration"
        Dir         = "src\Plugins\ProdToy.Plugins.ClaudeIntegration"
        SourceFile  = "src\Plugins\ProdToy.Plugins.ClaudeIntegration\ClaudeIntegrationPlugin.cs"
    },
    @{
        Id          = "ProdToy.Plugin.ShortCutManager"
        DisplayName = "Shortcuts"
        Dir         = "src\Plugins\ProdToy.Plugins.ShortCutManager"
        SourceFile  = "src\Plugins\ProdToy.Plugins.ShortCutManager\ShortCutManagerPlugin.cs"
    }
)

$pluginsReleaseDir = Join-Path $releaseDir "plugins"
if (Test-Path $pluginsReleaseDir) {
    # Wipe loose dirs from old layout (we now ship only zips).
    Remove-Item "$pluginsReleaseDir\*" -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $pluginsReleaseDir -Force | Out-Null

# --- Build, parse version, package each plugin ---
$pluginManifestEntries = @()
$publishedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

foreach ($plugin in $pluginProjects) {
    Write-Host "Building plugin: $($plugin.Id)..." -ForegroundColor Cyan
    dotnet build -c Release $plugin.Dir
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Plugin $($plugin.Id) build failed"
        exit 1
    }

    # Parse plugin version from its [Plugin("...", "...", "X.Y.Z", ...)] attribute.
    $sourceContent = Get-Content $plugin.SourceFile -Raw
    if ($sourceContent -match '\[Plugin\(\s*"[^"]+"\s*,\s*"[^"]+"\s*,\s*"(\d+\.\d+\.\d+)"') {
        $pluginVersion = $Matches[1]
    } else {
        Write-Error "Could not parse [Plugin(...)] version from $($plugin.SourceFile)"
        exit 1
    }

    # Per-plugin release notes (plain text next to .csproj). Falls back to host notes.
    $notesFile = Join-Path $plugin.Dir "release-notes.txt"
    if (Test-Path $notesFile) {
        $pluginNotes = (Get-Content $notesFile -Raw -ErrorAction SilentlyContinue).Trim()
    } else {
        $pluginNotes = ""
    }
    if ([string]::IsNullOrWhiteSpace($pluginNotes)) {
        $pluginNotes = $ReleaseNotes
    }

    # Locate built artifacts. The DLL name = project directory leaf.
    $buildOut = Join-Path $plugin.Dir "bin\Release\net8.0-windows"
    $dllName  = Split-Path -Leaf $plugin.Dir
    $dllSrc   = Join-Path $buildOut "$dllName.dll"
    $depsSrc  = Join-Path $buildOut "$dllName.deps.json"
    if (-not (Test-Path $dllSrc)) {
        Write-Error "Plugin DLL not found: $dllSrc"
        exit 1
    }

    # Build the zip in a staging dir so the contents land at the zip root (no subdir).
    $stagingDir = Join-Path $pluginsReleaseDir "_stage_$($plugin.Id)"
    if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
    New-Item -ItemType Directory -Path $stagingDir | Out-Null
    Copy-Item $dllSrc $stagingDir -Force
    if (Test-Path $depsSrc) { Copy-Item $depsSrc $stagingDir -Force }

    $zipPath = Join-Path $pluginsReleaseDir "$($plugin.Id).zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path "$stagingDir\*" -DestinationPath $zipPath
    Remove-Item $stagingDir -Recurse -Force
    $pluginSha = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "  Packaged $($plugin.Id).zip (v$pluginVersion, sha256=$($pluginSha.Substring(0,12))...)" -ForegroundColor Gray

    # Use ordered hashtable to preserve property order in the resulting JSON.
    $pluginManifestEntries += [ordered]@{
        id           = $plugin.Id
        name         = $plugin.DisplayName
        version      = $pluginVersion
        releaseNotes = $pluginNotes
        publishedAt  = $publishedAt
        zip          = "plugins/$($plugin.Id).zip"
        sha256       = $pluginSha
    }
}

# --- Create the host zip (ProdToy.exe at root) ---
Write-Host "Packaging host zip..." -ForegroundColor Cyan
$hostZipPath = Join-Path $releaseDir "ProdToy.zip"
Compress-Archive -Path (Join-Path $releaseDir "ProdToy.exe") -DestinationPath $hostZipPath -Force
$hostSha = (Get-FileHash $hostZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "  ProdToy.zip sha256=$($hostSha.Substring(0,12))..." -ForegroundColor Gray

# --- Build the installer (ProdToySetup.exe) ---
Write-Host "Publishing installer..." -ForegroundColor Cyan
if (Test-Path "$releaseDir\ProdToySetup.exe") {
    Remove-Item "$releaseDir\ProdToySetup.exe" -Force
}
dotnet publish -c Release $setupProjectDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish (Setup) failed"
    exit 1
}
if (-not (Test-Path (Join-Path $releaseDir "ProdToySetup.exe"))) {
    Write-Error "ProdToySetup.exe was not produced by dotnet publish"
    exit 1
}

# --- Generate metadata.json ---
$metadataObject = [ordered]@{
    version       = $newVersion
    releaseNotes  = $ReleaseNotes
    publishedAt   = $publishedAt
    hostZip       = "ProdToy.zip"
    hostSha256    = $hostSha
    plugins       = $pluginManifestEntries
}
$metadataJson = $metadataObject | ConvertTo-Json -Depth 5
$metadataPath = Join-Path $releaseDir "metadata.json"
Set-Content $metadataPath $metadataJson -Encoding UTF8
Write-Host "Created $metadataPath" -ForegroundColor Green

# --- Deploy ---
if ($DeployPath -and (Test-Path $DeployPath)) {
    Write-Host "Deploying to $DeployPath ..." -ForegroundColor Cyan

    # Cleanup of any old-format files from previous publishes.
    $loosePathsToRemove = @(
        (Join-Path $DeployPath "ProdToy.exe")
    )
    foreach ($p in $loosePathsToRemove) {
        if (Test-Path $p) { Remove-Item $p -Force -ErrorAction SilentlyContinue }
    }
    $deployPluginsDir = Join-Path $DeployPath "plugins"
    if (Test-Path $deployPluginsDir) {
        # Wipe everything under the deploy plugins/ — we re-deploy only zips.
        Get-ChildItem $deployPluginsDir -Directory -ErrorAction SilentlyContinue |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        Get-ChildItem $deployPluginsDir -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -eq ".zip" } |
            Remove-Item -Force -ErrorAction SilentlyContinue
    } else {
        New-Item -ItemType Directory -Path $deployPluginsDir | Out-Null
    }

    Copy-Item $metadataPath $DeployPath -Force
    Copy-Item $hostZipPath  $DeployPath -Force

    $setupExePath = Join-Path $releaseDir "ProdToySetup.exe"
    if (Test-Path $setupExePath) {
        Copy-Item $setupExePath $DeployPath -Force
    }

    foreach ($zip in (Get-ChildItem "$pluginsReleaseDir\*.zip" -ErrorAction SilentlyContinue)) {
        Copy-Item $zip.FullName $deployPluginsDir -Force
    }

    Write-Host "Deployed v$newVersion to $DeployPath" -ForegroundColor Green
} elseif ($DeployPath) {
    Write-Warning "Deploy path '$DeployPath' does not exist. Skipping deploy."
}

# --- Summary ---
Write-Host ""
Write-Host "Published v$newVersion to $releaseDir\" -ForegroundColor Green
$hostZipSize = '{0:N0}' -f (Get-Item $hostZipPath).Length
Write-Host "  ProdToy.zip      $hostZipSize bytes" -ForegroundColor Gray
$setupExeLocalPath = Join-Path $releaseDir "ProdToySetup.exe"
if (Test-Path $setupExeLocalPath) {
    $setupExeSize = '{0:N0}' -f (Get-Item $setupExeLocalPath).Length
    Write-Host "  ProdToySetup.exe $setupExeSize bytes" -ForegroundColor Gray
}
Write-Host "  metadata.json" -ForegroundColor Gray
$pluginZipCount = (Get-ChildItem "$pluginsReleaseDir\*.zip" -ErrorAction SilentlyContinue).Count
if ($pluginZipCount -gt 0) {
    Write-Host "  plugins/         $pluginZipCount plugin package(s)" -ForegroundColor Gray
}
foreach ($entry in $pluginManifestEntries) {
    Write-Host "    $($entry.id) v$($entry.version)" -ForegroundColor DarkGray
}
