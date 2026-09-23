# Installs Kobold for the current user: copies the runtime files to
# %LocalAppData%\Programs\Kobold, adds a Start Menu shortcut and registers an
# uninstall entry - no admin rights needed. Running it again upgrades in place.
#
# Double-click tools\install.cmd, or run:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\install.ps1
param(
    [string]$Source = (Split-Path $PSScriptRoot -Parent),
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Kobold'),
    [string]$ShortcutDir = [Environment]::GetFolderPath('Programs'),
    [string]$DesktopDir = [Environment]::GetFolderPath('Desktop'),
    [string]$UninstallKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Kobold',
    [string]$Version = '',
    [string]$ProcessName = 'Kobold',
    [switch]$DesktopShortcut,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'installer-common.ps1')

$publisher = 'zzrqq-worklux'

Write-Step "Installing Kobold to $InstallDir"

# An installed copy that is still running holds a lock on the files we are
# about to replace - stop it first, then copy.
$stopped = Stop-KoboldProcess -ProcessName $ProcessName -InstallDir $InstallDir
if ($stopped -gt 0) { Write-Ok "stopped $stopped running copy" }

try {
    Install-KoboldFiles -Source $Source -InstallDir $InstallDir -ScriptsDir $PSScriptRoot
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host "source: $Source"
    Write-Host "tools:  $PSScriptRoot"
    exit 1
}
Write-Ok 'runtime files and uninstaller copied'

$exePath = Join-Path $InstallDir 'Kobold.exe'
New-KoboldShortcut -ShortcutPath (Join-Path $ShortcutDir 'Kobold.lnk') -TargetPath $exePath -WorkingDirectory $InstallDir
Write-Ok 'Start Menu shortcut created'
if ($DesktopShortcut) {
    New-KoboldShortcut -ShortcutPath (Join-Path $DesktopDir 'Kobold.lnk') -TargetPath $exePath -WorkingDirectory $InstallDir
    Write-Ok 'desktop shortcut created'
}

$installedVersion = if ($Version) { $Version } else { Get-KoboldSourceVersion -ExePath $exePath }
$sizeKb = [int][math]::Ceiling((Get-ChildItem -LiteralPath $InstallDir -File | Measure-Object -Property Length -Sum).Sum / 1KB)

# Per-user uninstall entry: shows up in Settings > Apps, needs no admin rights.
New-Item -Path $UninstallKeyPath -Force | Out-Null
$entry = Get-KoboldUninstallEntry -InstallDir $InstallDir -Version $installedVersion -SizeKb $sizeKb -Publisher $publisher
foreach ($name in $entry.Keys) {
    New-ItemProperty -Path $UninstallKeyPath -Name $name -Value $entry[$name].Value -PropertyType $entry[$name].Type -Force | Out-Null
}
Write-Ok "uninstall entry registered (v$installedVersion)"

# The app owns the "start with Windows" Run key (it rewrites it on every
# launch), so the installer deliberately leaves that key alone.

if (-not $NoLaunch) {
    Start-Process -FilePath $exePath -WorkingDirectory $InstallDir
}

Write-Host ''
Write-Host "Kobold $installedVersion installed. Launch it from the Start Menu or $exePath"
exit 0
