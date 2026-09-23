# Development helper: builds the Release and deploys it to the same place
# tools\install.ps1 uses (%LocalAppData%\Programs\Kobold), so the startup entry
# never points at the development directory. End users run tools\install.cmd.
#
# Usage: powershell -File tools\deploy.ps1 [-NoLaunch]
param(
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent

Write-Host 'Building Release...'
dotnet build (Join-Path $repoRoot 'Kobold.csproj') -c Release --nologo | Out-Host

& (Join-Path $PSScriptRoot 'install.ps1') -Source (Join-Path $repoRoot 'bin\Release\net48') -NoLaunch:$NoLaunch
exit $LASTEXITCODE
