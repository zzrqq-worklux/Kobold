# Shared constants and mechanics for install.ps1 and uninstall.ps1.
#
# Nothing in here touches the machine on its own: every function takes the paths
# it works on, which is what lets tools\tests\installer.Tests.ps1 drive it all
# against temp folders.

$KoboldAppName = 'Kobold'

# The runtime files a Kobold build ships - everything the app needs to run.
$KoboldRuntimeFiles = @('Kobold.exe', 'Kobold.exe.config', 'Hardcodet.NotifyIcon.Wpf.dll', 'Newtonsoft.Json.dll')

# The uninstaller travels in tools\ next to the installer and is copied flat
# into the install folder, so the uninstall entry can point at it.
$KoboldUninstallerFiles = @('uninstall.cmd', 'uninstall.ps1', 'installer-common.ps1')

function Write-Step {
    param([string]$Message)
    Write-Host $Message
}

function Write-Ok {
    param([string]$Message)
    Write-Host "  ok  $Message" -ForegroundColor Green
}

function Write-Warn {
    param([string]$Message)
    Write-Host "  !   $Message" -ForegroundColor Yellow
}

<#
Copies one file, retrying briefly: an exe that was just stopped - or a virus
scanner that just met the file - can keep it locked for a moment, and copying
into a lock would leave a half-updated install. Throws when it still fails.
#>
function Copy-KoboldFile {
    param(
        [string]$Source,
        [string]$Destination,
        [int]$Attempts = 5
    )

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            Copy-Item -LiteralPath $Source -Destination $Destination -Force -ErrorAction Stop
            return
        } catch {
            if ($attempt -eq $Attempts) {
                throw "install: could not copy $(Split-Path -Leaf $Source) - the file is locked by another process. Close Kobold and try again. ($($_.Exception.Message))"
            }
            Start-Sleep -Milliseconds 500
        }
    }
}

function New-KoboldShortcut {
    param(
        [string]$ShortcutPath,
        [string]$TargetPath,
        [string]$WorkingDirectory
    )

    $parent = Split-Path -Parent $ShortcutPath
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $TargetPath
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.IconLocation = "$TargetPath,0"
    $shortcut.Description = 'Kobold - desktop folder widgets'
    $shortcut.Save()
}

<#
Returns the version to show in Apps & Features. Reads the built exe, so the
number cannot drift from the binary; falls back to 0.0.0 when the file carries
no version info (SourceLink appends "+<commit>" - that part is dropped).
#>
function Get-KoboldSourceVersion {
    param([string]$ExePath)

    try {
        $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($ExePath)
        foreach ($candidate in @($info.ProductVersion, $info.FileVersion)) {
            if ($candidate) { return ($candidate -split '\+')[0] }
        }
    } catch { }
    return '0.0.0'
}

<#
True when the path is a file or folder inside the install folder. Used to pick
out the running copy that holds a lock on the files being replaced - a portable
copy elsewhere must never be touched.
#>
function Test-KoboldProcessOwnedBy {
    param(
        [string]$ProcessPath,
        [string]$InstallDir
    )

    if (-not $ProcessPath) { return $false }
    $prefix = $InstallDir.TrimEnd('\') + '\'
    return $ProcessPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
}

<#
Stops the Kobold processes that run FROM the install folder; returns how many.
#>
function Stop-KoboldProcess {
    param(
        [string]$ProcessName,
        [string]$InstallDir
    )

    $stopped = 0
    foreach ($process in @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)) {
        try {
            if (-not (Test-KoboldProcessOwnedBy -ProcessPath $process.Path -InstallDir $InstallDir)) { continue }
            Stop-Process -Id $process.Id -Force
            # The exe stays locked for a moment after the kill; copying into a
            # lock throws, which used to leave a half-updated install.
            $process.WaitForExit(5000) | Out-Null
            $stopped++
        } catch { }
    }
    return $stopped
}

<#
The autostart entry (HKCU\...\Run\Kobold) is ours to remove when it points into
the install folder we just deleted, or at a copy that no longer exists. A live
copy somewhere else - a portable one, say - keeps its entry.
#>
function Test-KoboldRunKeyOwned {
    param(
        [string]$RunValue,
        [string]$InstallDir
    )

    if (-not $RunValue) { return $false }
    $target = $RunValue.Trim().Trim('"')
    if (-not $target) { return $false }
    if (Test-KoboldProcessOwnedBy -ProcessPath $target -InstallDir $InstallDir) { return $true }
    return -not (Test-Path -LiteralPath $target)
}

<#
What goes into the per-user uninstall entry: names, values and registry types.
Kept apart from the writing so the content can be checked without touching the
registry.
#>
function Get-KoboldUninstallEntry {
    param(
        [string]$InstallDir,
        [string]$Version,
        [int]$SizeKb,
        [string]$Publisher
    )

    $exePath = Join-Path $InstallDir 'Kobold.exe'
    $uninstallCmd = Join-Path $InstallDir 'uninstall.cmd'
    $entry = [ordered]@{}
    $entry.DisplayName = @{ Value = $KoboldAppName; Type = 'String' }
    $entry.DisplayVersion = @{ Value = $Version; Type = 'String' }
    $entry.Publisher = @{ Value = $Publisher; Type = 'String' }
    $entry.InstallLocation = @{ Value = $InstallDir; Type = 'String' }
    $entry.DisplayIcon = @{ Value = ('"' + $exePath + '",0'); Type = 'String' }
    $entry.UninstallString = @{ Value = ('"' + $uninstallCmd + '"'); Type = 'String' }
    $entry.QuietUninstallString = @{ Value = ('"' + $uninstallCmd + '" -Quiet'); Type = 'String' }
    $entry.NoModify = @{ Value = 1; Type = 'DWord' }
    $entry.NoRepair = @{ Value = 1; Type = 'DWord' }
    $entry.EstimatedSize = @{ Value = $SizeKb; Type = 'DWord' }
    return $entry
}

<#
Copies the runtime files and the uninstaller into place. Validation happens
first, so a bad source cannot leave a half-installed folder behind. Running it
again over an existing install is the upgrade path.
#>
function Install-KoboldFiles {
    param(
        [string]$Source,
        [string]$InstallDir,
        [string]$ScriptsDir
    )

    $missing = @()
    foreach ($file in $KoboldRuntimeFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $Source $file) -PathType Leaf)) { $missing += "source\$file" }
    }
    foreach ($file in $KoboldUninstallerFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $ScriptsDir $file) -PathType Leaf)) { $missing += "tools\$file" }
    }
    if ($missing.Count -gt 0) {
        throw ('install: these files are missing: ' + ($missing -join ', '))
    }

    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    foreach ($file in $KoboldRuntimeFiles) {
        Copy-KoboldFile -Source (Join-Path $Source $file) -Destination $InstallDir
    }
    foreach ($file in $KoboldUninstallerFiles) {
        Copy-KoboldFile -Source (Join-Path $ScriptsDir $file) -Destination $InstallDir
    }
}

<#
The three things that live in %AppData%\Kobold, counted and weighed so the
uninstaller can tell the user what deleting them would cost: settings, files
that were moved into a widget, and the icons coloured folders point at.
#>
function Get-KoboldDataInventory {
    param([string]$Path)

    $configFiles = @()
    $storageFiles = @()
    $iconFiles = @()
    if (Test-Path -LiteralPath $Path) {
        $configFiles = @(Get-ChildItem -LiteralPath $Path -File -Filter 'config.json*' -ErrorAction SilentlyContinue)
        $storage = Join-Path $Path 'Storage'
        if (Test-Path -LiteralPath $storage) { $storageFiles = @(Get-ChildItem -LiteralPath $storage -Recurse -File -ErrorAction SilentlyContinue) }
        $icons = Join-Path $Path 'FolderIcons'
        if (Test-Path -LiteralPath $icons) { $iconFiles = @(Get-ChildItem -LiteralPath $icons -File -ErrorAction SilentlyContinue) }
    }

    [pscustomobject]@{
        Exists       = (Test-Path -LiteralPath $Path)
        ConfigFiles  = $configFiles
        StorageFiles = $storageFiles
        IconFiles    = $iconFiles
        StorageBytes = [long](($storageFiles | Measure-Object -Property Length -Sum).Sum)
    }
}

<#
Applies the user's data choice: Keep, ConfigOnly (settings only) or All.
#>
function Remove-KoboldData {
    param(
        [string]$Path,
        [ValidateSet('Keep', 'ConfigOnly', 'All')][string]$Choice
    )

    switch ($Choice) {
        'ConfigOnly' {
            Get-ChildItem -LiteralPath $Path -File -Filter 'config.json*' -ErrorAction SilentlyContinue | Remove-Item -Force
        }
        'All' {
            Remove-Item -LiteralPath $Path -Recurse -Force
        }
    }
}
