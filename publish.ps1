
<#
.SYNOPSIS
    Publishes DevToy and optionally deploys to a network update location.

.PARAMETER DeployPath
    Network path to copy the exe and metadata.json to (e.g. \\server\share\DevToy).
    If omitted, only builds to the local release/ folder.

.PARAMETER ReleaseNotes
    Release notes for this version. Defaults to "Bug fixes and improvements."

.PARAMETER SkipVersionBump
    If set, skips incrementing the version number.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -DeployPath "\\server\share\DevToy" -ReleaseNotes "Added auto-update feature"
#>
param(
    [string]$DeployPath = "",
    [string]$ReleaseNotes = "Bug fixes and improvements.",
    [switch]$SkipVersionBump
)

$ErrorActionPreference = "Stop"

$versionFile = "src\DevToy.Win\Core\AppVersion.cs"
$projectDir  = "src\DevToy.Win"
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
if (Test-Path "$releaseDir\DevToy.exe") {
    Remove-Item "$releaseDir\DevToy.exe" -Force
}

Write-Host "Publishing..." -ForegroundColor Cyan
dotnet publish -c Release $projectDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed"
    exit 1
}

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
    Copy-Item "$releaseDir\DevToy.exe" $DeployPath -Force
    Copy-Item $metadataPath $DeployPath -Force
    Write-Host "Deployed v$newVersion to $DeployPath" -ForegroundColor Green
} elseif ($DeployPath) {
    Write-Warning "Deploy path '$DeployPath' does not exist. Skipping deploy."
}

Write-Host ""
Write-Host "Published v$newVersion to $releaseDir\" -ForegroundColor Green
Write-Host "  DevToy.exe  $('{0:N0}' -f (Get-Item "$releaseDir\DevToy.exe").Length) bytes" -ForegroundColor Gray
Write-Host "  metadata.json" -ForegroundColor Gray
