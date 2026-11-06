<#
.SYNOPSIS
    Fails when a plugin's files changed since the last release but its version did not.

.DESCRIPTION
    The updater replaces an installed plugin only when the release lists a higher
    version, so a changed plugin with the same version never reaches anyone who
    already has that version. Compares each plugin folder with the given release tag.

.PARAMETER Base
    The release tag to compare with. Default: the highest v* tag.
#>
param([string]$Base)

$ErrorActionPreference = 'Stop'

if (-not $Base) { $Base = git tag --list 'v*' --sort=-v:refname | Select-Object -First 1 }
if (-not $Base) {
    Write-Host 'No earlier release: nothing to compare.'
    exit 0
}

$versionPattern = '\[Plugin\(\s*"[^"]+"\s*,\s*"[^"]+"\s*,\s*"(\d+\.\d+\.\d+)"'
$problems = @()

foreach ($dir in Get-ChildItem 'src/Plugins' -Directory) {
    $folder = "src/Plugins/$($dir.Name)"
    $source = Get-ChildItem $dir.FullName -Filter '*Plugin.cs' |
        Where-Object { (Get-Content $_.FullName -Raw) -match $versionPattern } |
        Select-Object -First 1
    if (-not $source) { continue }

    $path = "$folder/$($source.Name)"
    $old = git show "${Base}:$path" 2>$null | Out-String
    if ($LASTEXITCODE -ne 0) { $global:LASTEXITCODE = 0; continue }   # a plugin new in this release

    git diff --quiet $Base HEAD -- $folder
    $changed = $LASTEXITCODE -ne 0
    $global:LASTEXITCODE = 0
    if (-not $changed) { continue }

    $null = (Get-Content $source.FullName -Raw) -match $versionPattern
    $newVersion = $Matches[1]
    $null = $old -match $versionPattern
    if ($newVersion -eq $Matches[1]) {
        $problems += "$($dir.Name) changed since $Base but is still version $newVersion"
    }
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "ERROR: $_" -ForegroundColor Red }
    Write-Host 'Raise the version in the plugin''s [Plugin(...)] attribute, or updates will skip it.' -ForegroundColor Red
    exit 1
}

Write-Host "Plugin versions OK against $Base."
