
<#
.SYNOPSIS
    Publishes ProdToy and optionally deploys to a network update location.

.PARAMETER DeployPath
    Network path to copy the exe and metadata.json to (e.g. \\server\share\ProdToy).
    If omitted, only builds to the local release/ folder.

.PARAMETER ReleaseNotes
    Release notes for this version. Defaults to "Bug fixes and improvements."

.PARAMETER SkipVersionBump
    If set, skips incrementing the version number.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -DeployPath "\\server\share\ProdToy" -ReleaseNotes "Added auto-update feature"
#>
param(
    [string]$DeployPath = "",
    [string]$ReleaseNotes = "Bug fixes and improvements.",
    [switch]$SkipVersionBump
)

$ErrorActionPreference = "Stop"

$versionFile = "src\ProdToy.Win\Core\AppVersion.cs"
$projectDir  = "src\ProdToy.Win"
$releaseDir  = "release"

# --- Read current version ---
$content = Get-Content $versionFile -Raw
if ($content -match 'Current\s*=\s*"(\d+\.\d+\.\d+)"') {
    $currentVersion = $Matches[1]
} else {
    Write-Error "Could not parse version from $versionFile"
    exit 1
}

Write-Host "Current version: $currentVersion" -ForegroundColor Cyan

# --- Bump version ---
if (-not $SkipVersionBump) {
    $parts = $currentVersion.Split('.')
    $parts[2] = [int]$parts[2] + 1
    $newVersion = $parts -join '.'

    $content = $content -replace "Current\s*=\s*""$currentVersion""", "Current = ""$newVersion"""
    Set-Content $versionFile $content -NoNewline
    Write-Host "Bumped to: $newVersion" -ForegroundColor Green
} else {
    $newVersion = $currentVersion
    Write-Host "Skipping version bump, staying at: $newVersion" -ForegroundColor Yellow
}

# --- Clean and publish ---
if (Test-Path "$releaseDir\ProdToy.exe") {
    Remove-Item "$releaseDir\ProdToy.exe" -Force
}

Write-Host "Publishing..." -ForegroundColor Cyan
dotnet publish -c Release $projectDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed"
    exit 1
}

# --- Build and package plugins ---
$pluginProjects = @(
    @{ Name = "ProdToy.Plugin.Alarm";              Dir = "src\Plugins\ProdToy.Plugins.Alarm" },
    @{ Name = "ProdToy.Plugin.Screenshot";         Dir = "src\Plugins\ProdToy.Plugins.Screenshot" },
    @{ Name = "ProdToy.Plugin.ClaudeIntegration";  Dir = "src\Plugins\ProdToy.Plugins.ClaudeIntegration" }
)

$pluginsReleaseDir = Join-Path $releaseDir "plugins"
if (-not (Test-Path $pluginsReleaseDir)) { New-Item -ItemType Directory -Path $pluginsReleaseDir | Out-Null }

foreach ($plugin in $pluginProjects) {
    Write-Host "Building plugin: $($plugin.Name)..." -ForegroundColor Cyan
    dotnet build -c Release $plugin.Dir
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Plugin $($plugin.Name) build failed — skipping"
        continue
    }

    $pluginOutDir = Join-Path $pluginsReleaseDir $plugin.Name
    if (-not (Test-Path $pluginOutDir)) { New-Item -ItemType Directory -Path $pluginOutDir | Out-Null }

    # Copy DLL and deps.json. The DLL name is the project directory leaf
    # (e.g. src\Plugins\ProdToy.Plugins.Screenshot → ProdToy.Plugins.Screenshot.dll).
    $buildOut = Join-Path $plugin.Dir "bin\Release\net8.0-windows"
    $dllName = Split-Path -Leaf $plugin.Dir
    $dllSrc = Join-Path $buildOut "$dllName.dll"
    $depsSrc = Join-Path $buildOut "$dllName.deps.json"
    if (-not (Test-Path $dllSrc)) {
        Write-Warning "Plugin DLL not found: $dllSrc — skipping copy"
        continue
    }
    Copy-Item $dllSrc $pluginOutDir -Force
    if (Test-Path $depsSrc) { Copy-Item $depsSrc $pluginOutDir -Force }

    # Create zip for catalog distribution
    $zipPath = Join-Path $pluginsReleaseDir "$($plugin.Name).zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path "$pluginOutDir\*" -DestinationPath $zipPath
    Write-Host "  Packaged $($plugin.Name).zip" -ForegroundColor Gray
}

Write-Host ""

# --- Generate metadata.json in release folder ---
$metadata = @{
    version      = $newVersion
    releaseNotes = $ReleaseNotes
    publishedAt  = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
} | ConvertTo-Json -Depth 1

$metadataPath = Join-Path $releaseDir "metadata.json"
Set-Content $metadataPath $metadata -Encoding UTF8
Write-Host "Created $metadataPath" -ForegroundColor Green

# --- Deploy to network path ---
if ($DeployPath -and (Test-Path $DeployPath)) {
    Write-Host "Deploying to $DeployPath ..." -ForegroundColor Cyan
    Copy-Item "$releaseDir\ProdToy.exe" $DeployPath -Force
    Copy-Item $metadataPath $DeployPath -Force

    # Deploy plugin directories (for bundled install/update) and zips (for catalog)
    $deployPluginsDir = Join-Path $DeployPath "plugins"
    if (-not (Test-Path $deployPluginsDir)) { New-Item -ItemType Directory -Path $deployPluginsDir | Out-Null }

    # Copy expanded plugin directories (used by Updater and SetupForm)
    foreach ($plugin in $pluginProjects) {
        $pluginSrcDir = Join-Path $pluginsReleaseDir $plugin.Name
        if (Test-Path $pluginSrcDir) {
            $destPluginDir = Join-Path $deployPluginsDir $plugin.Name
            if (-not (Test-Path $destPluginDir)) { New-Item -ItemType Directory -Path $destPluginDir | Out-Null }
            Copy-Item "$pluginSrcDir\*" $destPluginDir -Force -Recurse
            Write-Host "  Deployed plugin dir: $($plugin.Name)/" -ForegroundColor Gray
        }
    }

    # Copy plugin zips (for catalog distribution)
    $pluginZips = Get-ChildItem "$pluginsReleaseDir\*.zip" -ErrorAction SilentlyContinue
    foreach ($zip in $pluginZips) {
        Copy-Item $zip.FullName $deployPluginsDir -Force
    }

    Write-Host "Deployed v$newVersion to $DeployPath" -ForegroundColor Green
} elseif ($DeployPath) {
    Write-Warning "Deploy path '$DeployPath' does not exist. Skipping deploy."
}

Write-Host ""
Write-Host "Published v$newVersion to $releaseDir\" -ForegroundColor Green
$exeSize = '{0:N0}' -f (Get-Item (Join-Path $releaseDir 'ProdToy.exe')).Length
Write-Host "  ProdToy.exe  $exeSize bytes" -ForegroundColor Gray
Write-Host "  metadata.json" -ForegroundColor Gray
$pluginZipCount = (Get-ChildItem "$pluginsReleaseDir\*.zip" -ErrorAction SilentlyContinue).Count
if ($pluginZipCount -gt 0) {
    Write-Host "  plugins/     $pluginZipCount plugin package(s)" -ForegroundColor Gray
}

