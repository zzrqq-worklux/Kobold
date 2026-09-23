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
            # The exe stays locked for a moment after the kill; copying into a
            # lock throws IOException, which used to leave a half-updated install.
            $proc.WaitForExit(5000) | Out-Null
        }
    } catch { }
}

# 3. Clean-copy the build output into the deploy folder
function Copy-WithRetry {
    param([string]$Source, [string]$Destination, [int]$Attempts = 5)

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            Copy-Item -LiteralPath $Source -Destination $Destination -Force
            return
        } catch {
            if ($attempt -eq $Attempts) { throw }
            Write-Host "Copy retry $attempt for $(Split-Path $Source -Leaf)..."
            Start-Sleep -Milliseconds 500
        }
    }
}

New-Item -ItemType Directory -Force -Path $target | Out-Null
Get-ChildItem -LiteralPath (Join-Path $repoRoot 'bin\Release\net48') -File | ForEach-Object {
    Copy-WithRetry -Source $_.FullName -Destination $target
}

# 4. Point the startup entry at the deployed exe
$exePath = Join-Path $target 'Kobold.exe'
Set-ItemProperty -Path $runKey -Name 'Kobold' -Value ('"' + $exePath + '"')

Write-Host ''
Write-Host "Deployed: $exePath"
Write-Host "Run key 'Kobold' -> $exePath"
