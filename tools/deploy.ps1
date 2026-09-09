# Deploys the Release build to a stable location (%LocalAppData%\Programs\Kobold)
# and points the HKCU Run key at it - never at the development directory.
# Usage: powershell -File tools\deploy.ps1
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$target = Join-Path $env:LOCALAPPDATA 'Programs\Kobold'
$runKey = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'

# 1. Build Release
Write-Host 'Building Release...'
dotnet build (Join-Path $repoRoot 'Kobold.csproj') -c Release --nologo | Out-Host

# 2. Stop a Kobold instance running FROM the deploy folder (file lock)
$running = Get-Process -Name Kobold -ErrorAction SilentlyContinue
foreach ($proc in $running) {
    try {
        if ($proc.Path -and $proc.Path.StartsWith($target, [System.StringComparison]::OrdinalIgnoreCase)) {
            Write-Host "Stopping running Kobold from deploy folder (PID $($proc.Id))..."
            Stop-Process -Id $proc.Id -Force
        }
    } catch { }
}

# 3. Clean-copy the build output into the deploy folder
New-Item -ItemType Directory -Force -Path $target | Out-Null
Get-ChildItem -LiteralPath (Join-Path $repoRoot 'bin\Release\net48') -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $target -Force
}

# 4. Point the startup entry at the deployed exe
$exePath = Join-Path $target 'Kobold.exe'
Set-ItemProperty -Path $runKey -Name 'Kobold' -Value ('"' + $exePath + '"')

Write-Host ''
Write-Host "Deployed: $exePath"
Write-Host "Run key 'Kobold' -> $exePath"
