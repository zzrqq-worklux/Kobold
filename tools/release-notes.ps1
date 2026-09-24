# Builds the GitHub Release body for one version straight from CHANGELOG.md,
# so the release notes can never drift from the changelog. Fails (exit 1) when
# the version section is missing or empty - a release must not go out without
# changelog entries.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\release-notes.ps1 -Version 1.2.0
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\release-notes.ps1  (version read from Kobold.csproj)
#
# -Version omitted  -> read the <Version> in Kobold.csproj (single source of truth)
# -OutFile omitted  -> write the notes to stdout
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Csproj,
    [string]$Changelog,
    [string]$OutFile
)
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $Csproj) { $Csproj = Join-Path $repoRoot 'Kobold.csproj' }
if (-not $Changelog) { $Changelog = Join-Path $repoRoot 'CHANGELOG.md' }

if (-not $Version) {
    [xml]$project = Get-Content -LiteralPath $Csproj
    $Version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
    if (-not $Version) {
        throw "release-notes: no <Version> found in $Csproj - pass -Version explicitly."
    }
}

if (-not (Test-Path -LiteralPath $Changelog)) {
    throw "release-notes: changelog not found: $Changelog"
}

# Windows PowerShell 5.1 reads BOM-less files as ANSI, which would mangle every
# Chinese line - read and write UTF-8 explicitly.
$lines = @(Get-Content -LiteralPath $Changelog -Encoding UTF8)
$escaped = [regex]::Escape($Version)
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match "^##\s+v$escaped(\s|$)") { $start = $i; break }
}
if ($start -lt 0) {
    throw "release-notes: CHANGELOG has no section '## v$Version'."
}

$end = $lines.Count
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^##\s') { $end = $i; break }
}

$body = @()
if ($end -gt $start + 1) { $body = @($lines[($start + 1)..($end - 1)]) }
$body = @($body | Where-Object { $_ -notmatch '^\s*---\s*$' })

# Trim blank edges by index; slicing a single-element array with a reversed
# range does not shrink it, which would spin forever on a blank-only section.
$first = 0
while ($first -lt $body.Count -and [string]::IsNullOrWhiteSpace($body[$first])) { $first++ }
$last = $body.Count - 1
while ($last -ge $first -and [string]::IsNullOrWhiteSpace($body[$last])) { $last-- }
if ($last -ge $first) { $body = @($body[$first..$last]) } else { $body = @() }

if ($body.Count -eq 0) {
    throw "release-notes: CHANGELOG section '## v$Version' is empty."
}

$notes = @"
$($body -join "`n")

## Downloads / 下载

- [Kobold-v$Version-win-x64.zip](https://github.com/zzrqq-worklux/Kobold/releases/download/v$Version/Kobold-v$Version-win-x64.zip)
"@

if ($OutFile) {
    # UTF-8 without BOM: GitHub reads the notes file as UTF-8, and a BOM would
    # show up as a stray character at the top of the release.
    [System.IO.File]::WriteAllText($OutFile, $notes, (New-Object System.Text.UTF8Encoding($false)))
} else {
    Write-Output $notes
}
