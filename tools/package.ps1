# Packages the Release build into Releases\Kobold-v<version>-win-x64.zip
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools\package.ps1 [-Version 1.2.0]
# Version defaults to the <Version> in Kobold.csproj - one source of truth.
param(
    [string]$Version
)
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'installer-common.ps1')

$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $Version) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'Kobold.csproj')
    $Version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
    if (-not $Version) { throw 'No <Version> in Kobold.csproj; pass -Version explicitly.' }
}
$outDir = Join-Path $repoRoot 'bin\Release\net48'
$stage = Join-Path $env:TEMP "Kobold-package-$Version"
$zip = Join-Path $repoRoot "Releases\Kobold-v$Version-win-x64.zip"

Write-Host 'Building Release...'
dotnet build (Join-Path $repoRoot 'Kobold.csproj') -c Release --nologo | Out-Host

if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path $zip) | Out-Null

# Runtime files only - no .pdb, no obj/bin leftovers. The current layout is
# flat, but a relative path would still land in its subdirectory.
foreach ($file in $KoboldRuntimeFiles) {
    $dest = Join-Path $stage $file
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
    Copy-Item -LiteralPath (Join-Path $outDir $file) -Destination $dest -Force
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $stage -Force
foreach ($file in 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
    Copy-Item -LiteralPath (Join-Path $repoRoot $file) -Destination $stage -Force
}

# The README embeds docs/media and links the reference docs and the skill;
# bundle just those, not the development scratch folders under docs\.
$docs = Join-Path $stage 'docs'
New-Item -ItemType Directory -Force -Path $docs | Out-Null
foreach ($file in 'FEATURES.md', 'FEATURES.zh.md', 'DEVELOPMENT.md', 'DEVELOPMENT.zh.md') {
    Copy-Item -LiteralPath (Join-Path $repoRoot "docs\$file") -Destination $docs -Force
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\media') -Destination $docs -Recurse -Force
$skill = Join-Path $stage 'skill\kobold-config'
New-Item -ItemType Directory -Force -Path $skill | Out-Null
Copy-Item -Path (Join-Path $repoRoot 'skill\kobold-config\*') -Destination $skill -Recurse -Force

# The installer travels in tools\: in the extracted zip the user double-clicks
# tools\install.cmd, and install.ps1 carries the uninstaller from there.
$tools = Join-Path $stage 'tools'
New-Item -ItemType Directory -Force -Path $tools | Out-Null
foreach ($file in @('install.cmd', 'install.ps1') + $KoboldUninstallerFiles + $KoboldToolFiles) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $tools -Force
}

if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Remove-Item -LiteralPath $stage -Recurse -Force

Write-Host ''
Write-Host "Packaged: $zip"
