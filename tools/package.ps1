# Packages the Release build into Releases\Kobold-v<version>-win-x64.zip
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools\package.ps1 [-Version 1.0.0]
param(
    [string]$Version = '1.0.0'
)
$ErrorActionPreference = 'Stop'

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
foreach ($file in 'Kobold.exe', 'Kobold.exe.config', 'Hardcodet.NotifyIcon.Wpf.dll', 'Newtonsoft.Json.dll') {
    Copy-Item -LiteralPath (Join-Path $outDir $file) -Destination $stage -Force
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $stage -Force

if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Remove-Item -LiteralPath $stage -Recurse -Force

Write-Host ''
Write-Host "Packaged: $zip"
