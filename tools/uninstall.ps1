# Removes the Kobold install for the current user: stops the app, deletes the
# install folder, the shortcuts and the uninstall entry. User data in
# %AppData%\Kobold is kept unless you ask for it to go - "Storage" holds files
# that were moved into a widget, and "FolderIcons" is what coloured folders
# point their desktop.ini at.
#
# Also works as a portable cleanup: with no install folder present it still
# clears a stale autostart entry and offers the same data choices.
#
# Double-click tools\uninstall.cmd, or run:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\uninstall.ps1
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Kobold'),
    [string]$DataDir = (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Kobold'),
    [string]$ShortcutDir = [Environment]::GetFolderPath('Programs'),
    [string]$DesktopDir = [Environment]::GetFolderPath('Desktop'),
    [string]$UninstallKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Kobold',
    [string]$RunKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
    [ValidateSet('Keep', 'ConfigOnly', 'All')][string]$DataChoice = 'Keep',
    [switch]$Quiet,
    [string]$ProcessName = 'Kobold'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'installer-common.ps1')

function Write-KoboldDataInventory {
    param($Inventory, [string]$Path)

    Write-Step "Your data lives in ${Path}:"
    if ($Inventory.ConfigFiles.Count -gt 0) { Write-Host '  config.json    your settings - safe to delete' }
    if ($Inventory.StorageFiles.Count -gt 0) {
        Write-Host ("  Storage\       {0} files, {1:N0} KB - files you moved into a widget live here, not in the Recycle Bin" -f $Inventory.StorageFiles.Count, ($Inventory.StorageBytes / 1KB))
    }
    if ($Inventory.IconFiles.Count -gt 0) {
        Write-Host ("  FolderIcons\   {0} files - folders you coloured point at these icons; deleting breaks them" -f $Inventory.IconFiles.Count)
    }
}

# --- remove the install -----------------------------------------------------
if (Test-Path -LiteralPath $InstallDir) {
    Write-Step "Removing the Kobold install in $InstallDir"

    $stopped = Stop-KoboldProcess -ProcessName $ProcessName -InstallDir $InstallDir
    if ($stopped -gt 0) { Write-Ok "stopped $stopped running copy" }

    foreach ($shortcut in @((Join-Path $ShortcutDir 'Kobold.lnk'), (Join-Path $DesktopDir 'Kobold.lnk'))) {
        if (Test-Path -LiteralPath $shortcut) {
            Remove-Item -LiteralPath $shortcut -Force
            Write-Ok "shortcut removed: $shortcut"
        }
    }

    if (Test-Path -LiteralPath $UninstallKeyPath) {
        Remove-Item -LiteralPath $UninstallKeyPath -Recurse -Force
        Write-Ok 'uninstall entry removed'
    }

    Remove-Item -LiteralPath $InstallDir -Recurse -Force
    Write-Ok 'install folder removed'
} else {
    Write-Step "No Kobold install found at $InstallDir - cleaning up leftovers only"
}

# --- the autostart entry ----------------------------------------------------
$runValue = (Get-ItemProperty -Path $RunKeyPath -Name 'Kobold' -ErrorAction SilentlyContinue).Kobold
if ($runValue) {
    if (Test-KoboldRunKeyOwned -RunValue $runValue -InstallDir $InstallDir) {
        Remove-ItemProperty -Path $RunKeyPath -Name 'Kobold' -Force -ErrorAction SilentlyContinue
        Write-Ok 'autostart entry removed'
    } else {
        Write-Warn "left the autostart entry alone - it points at $runValue"
    }
}

# --- the user's data --------------------------------------------------------
$inventory = Get-KoboldDataInventory -Path $DataDir
if ($inventory.Exists) {
    Write-Host ''
    Write-KoboldDataInventory -Inventory $inventory -Path $DataDir

    $choice = $DataChoice
    if (-not $Quiet) {
        Write-Host ''
        Write-Host 'What should happen to that data?'
        Write-Host '  [K] keep it (default)'
        Write-Host '  [C] delete the settings only'
        Write-Host '  [A] delete everything - stored files and folder icons too'
        switch ((Read-Host 'K / C / A').ToUpperInvariant()) {
            'C' { $choice = 'ConfigOnly' }
            'A' {
                Write-Warn 'Storage may hold your own files and coloured folders point at FolderIcons - both go for good, no Recycle Bin.'
                if ((Read-Host 'Type DELETE to confirm') -cne 'DELETE') {
                    $choice = 'Keep'
                    Write-Warn 'not confirmed - keeping your data'
                }
            }
            default { $choice = 'Keep' }
        }
    }

    Write-Host ''
    switch ($choice) {
        'ConfigOnly' {
            Remove-KoboldData -Path $DataDir -Choice 'ConfigOnly'
            Write-Ok 'settings deleted - stored files and folder icons kept'
        }
        'All' {
            Remove-KoboldData -Path $DataDir -Choice 'All'
            Write-Ok 'data folder deleted'
        }
        default { Write-Ok "kept your data in $DataDir" }
    }
}

Write-Host ''
Write-Host 'Kobold has been removed.'
exit 0
