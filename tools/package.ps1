# Packages the Release build into Releases\Kobold-v<version>-win-x64.zip
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools\package.ps1 [-Version 1.0.0]
param(
    [string]$Version = '1.0.0'
)
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'installer-common.ps1')

$repoRoot = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $repoRoot 'bin\Release\net48'
$stage = Join-Path $env:TEMP "Kobold-package-$Version"
$zip = Join-Path $repoRoot "Releases\Kobold-v$Version-win-x64.zip"

Write-Host 'Building Release...'
dotnet build (Join-Path $repoRoot 'Kobold.csproj') -c Release --nologo | Out-Host

if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path $zip) | Out-Null

# Runtime files only - no .pdb, no obj/bin leftovers
foreach ($file in $KoboldRuntimeFiles) {
    Copy-Item -LiteralPath (Join-Path $outDir $file) -Destination $stage -Force
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $stage -Force

# The installer travels in tools\: in the extracted zip the user double-clicks
# tools\install.cmd, and install.ps1 carries the uninstaller from there.
$tools = Join-Path $stage 'tools'
New-Item -ItemType Directory -Force -Path $tools | Out-Null
foreach ($file in @('install.cmd', 'install.ps1') + $KoboldUninstallerFiles) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $tools -Force
}

# Screenshots referenced by the bundled README
$shots = Join-Path $stage 'Screenshot'
New-Item -ItemType Directory -Force -Path $shots | Out-Null
foreach ($shot in 'island.png', 'panel-dark.png', 'context-menu.png', 'light-theme.png') {
    Copy-Item -LiteralPath (Join-Path $repoRoot "Screenshot\$shot") -Destination $shots -Force
}

if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Remove-Item -LiteralPath $stage -Recurse -Force

Write-Host ''
Write-Host "Packaged: $zip"
