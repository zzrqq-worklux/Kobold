# Applies a staged Kobold update after the running copy has exited.
#
# The app downloads and unpacks a release into a stage folder, then starts this
# script as a detached process and shuts down. Here we wait for that process to
# go away, back up the install folder, copy the staged files over it, and start
# the new exe. If anything fails the backup is copied back and the old exe is
# started again, so the install is never left half updated.
#
# A process that is still running after the wait is force-stopped, and because
# a force kill skips the app's normal exit this script first asks the installed
# exe to show the desktop icons again.
#
# -DryRun exercises the backup and the copy against temporary folders only: it
# never waits for a process and never starts an exe.
#
# Exit codes: 0 = applied (or dry run succeeded), 2 = backup failed / nothing
# was changed, 3 = copy or launch failed and the backup was restored.
param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][string]$StageDir,
    [Parameter(Mandatory=$true)][string]$InstallDir,
    [string]$LogPath,
    [int]$WaitSeconds = 30,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$exeName = 'Kobold.exe'
$backupPrefix = '.update-backup-'
$copyAttempts = 5
$retryDelaySeconds = 1
$verifySeconds = 15
$iconRestoreTimeoutMs = 3000

if (-not $LogPath) {
    if ($env:LOCALAPPDATA) {
        $LogPath = Join-Path (Join-Path $env:LOCALAPPDATA 'Kobold') 'update.log'
    } else {
        $LogPath = Join-Path ([System.IO.Path]::GetTempPath()) 'Kobold-update.log'
    }
}

<#
Writes one timestamped line to the console and appends it to the log. Logging
problems must never stop an update, so a failed append only prints a warning.
#>
function Write-UpdateLog {
    param([string]$Message)

    $line = '{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Write-Host $line
    try {
        $parent = Split-Path -Parent $LogPath
        if ($parent -and -not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
        }
        Add-Content -LiteralPath $LogPath -Value $line -Encoding ASCII -ErrorAction Stop
    } catch {
        Write-Host ("  (could not write the log: {0})" -f $_.Exception.Message)
    }
}

<#
Full path without a trailing separator, or $null when the path is blank or
unusable. Matches the normalization UpdateInstall does on the app side.
#>
function Get-NormalizedDirectory {
    param([string]$Path)

    if (-not $Path) { return $null }
    try {
        $full = [System.IO.Path]::GetFullPath($Path)
        $root = [System.IO.Path]::GetPathRoot($full)
        if ($root -and $full.Length -gt $root.Length) {
            $full = $full.TrimEnd([char[]]@('\', '/'))
        }
        return $full
    } catch {
        return $null
    }
}

<#
Every file under $Root as objects with an absolute Path and a Relative path.
Backup folders are skipped when requested so a backup never contains an older
backup (the install folder is where .update-backup-* lives).
#>
function Get-InstallFileList {
    param(
        [string]$Root,
        [switch]$SkipBackups
    )

    $normalizedRoot = Get-NormalizedDirectory $Root
    if (-not $normalizedRoot -or -not (Test-Path -LiteralPath $normalizedRoot -PathType Container)) {
        throw "not a folder: $Root"
    }

    $files = @()
    foreach ($file in @(Get-ChildItem -LiteralPath $normalizedRoot -Recurse -File -Force -ErrorAction Stop)) {
        $relative = $file.FullName.Substring($normalizedRoot.Length).TrimStart([char[]]@('\', '/'))
        if ($SkipBackups) {
            $skip = $false
            foreach ($segment in @($relative -split '[\\/]')) {
                if ($segment -like ($backupPrefix + '*')) {
                    $skip = $true
                    break
                }
            }
            if ($skip) { continue }
        }
        $files += [pscustomobject]@{ Path = $file.FullName; Relative = $relative }
    }
    return $files
}

<#
Copies one file with the 5 x 1 s retry the plan calls for. A file that was just
released by a stopped process, or that an antivirus is still scanning, often
stays locked for a moment; retrying keeps that from failing the whole update.
#>
function Copy-UpdateFile {
    param(
        [string]$Source,
        [string]$Destination
    )

    for ($attempt = 1; $attempt -le $copyAttempts; $attempt++) {
        try {
            $parent = Split-Path -Parent $Destination
            if ($parent -and -not (Test-Path -LiteralPath $parent)) {
                New-Item -ItemType Directory -Force -Path $parent -ErrorAction Stop | Out-Null
            }
            Copy-Item -LiteralPath $Source -Destination $Destination -Force -ErrorAction Stop
            return $true
        } catch {
            if ($attempt -lt $copyAttempts) {
                Start-Sleep -Seconds $retryDelaySeconds
            } else {
                Write-UpdateLog ("  copy failed after {0} attempts: {1} ({2})" -f $copyAttempts, $Destination, $_.Exception.Message)
            }
        }
    }
    return $false
}

<#
Copies every file in $SourceDir to the same relative path under $DestinationDir.
Returns $false as soon as one file still fails after its retries.
#>
function Copy-UpdateTree {
    param(
        [string]$SourceDir,
        [string]$DestinationDir,
        [switch]$SkipBackups
    )

    $files = @(Get-InstallFileList -Root $SourceDir -SkipBackups:$SkipBackups)
    Write-UpdateLog ("  copying {0} file(s) from {1} to {2}" -f $files.Count, $SourceDir, $DestinationDir)
    foreach ($file in $files) {
        $destination = Join-Path $DestinationDir $file.Relative
        if (-not (Copy-UpdateFile -Source $file.Path -Destination $destination)) {
            return $false
        }
    }
    return $true
}

<#
The installer puts the tool scripts flat beside Kobold.exe (koboldctl.ps1,
apply-update.ps1), while the package carries them under tools\. After the tree
copy, mirror the staged tools\ files flat too - keeping the tools\ copies - so
the installed CLI and the next update helper stay where the installer and
find-koboldctl.ps1 look first.
#>
function Copy-UpdateToolsFlat {
    param(
        [string]$StageDir,
        [string]$InstallRoot
    )

    $toolsRoot = Join-Path $StageDir 'tools'
    if (-not (Test-Path -LiteralPath $toolsRoot -PathType Container)) {
        Write-UpdateLog '  tools: the package carries no tools folder; nothing to mirror'
        return $true
    }

    $files = @(Get-InstallFileList -Root $toolsRoot)
    Write-UpdateLog ("  tools: mirroring {0} tool file(s) flat into {1}" -f $files.Count, $InstallRoot)
    foreach ($file in $files) {
        $destination = Join-Path $InstallRoot $file.Relative
        if (-not (Copy-UpdateFile -Source $file.Path -Destination $destination)) {
            return $false
        }
    }
    return $true
}

<#
Copies the whole install folder into $BackupDir, excluding older backup folders.
$false means the install has no safety net, so the caller must stop before
touching a single file.
#>
function New-UpdateBackup {
    param(
        [string]$Root,
        [string]$BackupDir
    )

    $files = @(Get-InstallFileList -Root $Root -SkipBackups)
    Write-UpdateLog ("  backup: copying {0} file(s) from {1} to {2}" -f $files.Count, $Root, $BackupDir)

    if (-not (Test-Path -LiteralPath $BackupDir)) {
        New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
    }

    foreach ($file in $files) {
        $destination = Join-Path $BackupDir $file.Relative
        if (-not (Copy-UpdateFile -Source $file.Path -Destination $destination)) {
            return $false
        }
    }
    return $true
}

<#
Copies every backed-up file back over the install folder, with the same retries.
Returns $false when any file could not be restored.
#>
function Restore-UpdateBackup {
    param(
        [string]$BackupDir,
        [string]$InstallRoot
    )

    if (-not (Test-Path -LiteralPath $BackupDir -PathType Container)) {
        Write-UpdateLog ("  rollback: the backup is missing: {0}" -f $BackupDir)
        return $false
    }

    $files = @(Get-InstallFileList -Root $BackupDir)
    Write-UpdateLog ("  rollback: restoring {0} file(s) from {1}" -f $files.Count, $BackupDir)
    $allRestored = $true
    foreach ($file in $files) {
        $destination = Join-Path $InstallRoot $file.Relative
        if (-not (Copy-UpdateFile -Source $file.Path -Destination $destination)) {
            $allRestored = $false
        }
    }
    return $allRestored
}

<#
Removes files the update copied in that did not exist before it (no counterpart
in the backup). Restoring the backup brings every old file back; this makes the
rollback exact instead of leaving new DLLs, docs or flat tool copies from the
failed package. Never throws: the caller must still restart the old exe.
#>
function Remove-UpdateAddedFiles {
    param(
        [string]$StageDir,
        [string]$BackupDir,
        [string]$InstallRoot
    )

    $removed = 0
    try {
        $candidates = @()
        foreach ($file in @(Get-InstallFileList -Root $StageDir)) {
            $candidates += [pscustomobject]@{
                Installed = (Join-Path $InstallRoot $file.Relative)
                Tracked   = (Join-Path $BackupDir $file.Relative)
            }
        }

        # A successful copy also mirrors the package's tools\ files flat into
        # the install folder; those added files need the same cleanup.
        $toolsRoot = Join-Path $StageDir 'tools'
        if (Test-Path -LiteralPath $toolsRoot -PathType Container) {
            foreach ($tool in @(Get-InstallFileList -Root $toolsRoot)) {
                $candidates += [pscustomobject]@{
                    Installed = (Join-Path $InstallRoot $tool.Relative)
                    Tracked   = (Join-Path $BackupDir $tool.Relative)
                }
            }
        }

        foreach ($candidate in $candidates) {
            if (Test-Path -LiteralPath $candidate.Tracked -PathType Leaf) { continue }
            if (-not (Test-Path -LiteralPath $candidate.Installed -PathType Leaf)) { continue }
            try {
                Remove-Item -LiteralPath $candidate.Installed -Force -ErrorAction Stop
                $removed++
            } catch {
                Write-UpdateLog ("  rollback: could not remove the new file {0}: {1}" -f $candidate.Installed, $_.Exception.Message)
            }
        }
    } catch {
        Write-UpdateLog ("  rollback: could not clean up the files the update added: {0}" -f $_.Exception.Message)
    }
    Write-UpdateLog ("  rollback: removed {0} file(s) that the failed update had added" -f $removed)
}

<#
After a successful update only one backup may survive: the newest remaining
.update-backup-* folder. The backup from this run is removed first, so this
prunes leftovers from earlier failed updates. A backup kept by a rollback is
never pruned by this function.
#>
function Remove-OldUpdateBackups {
    param([string]$InstallRoot)

    try {
        $backups = @(Get-ChildItem -LiteralPath $InstallRoot -Directory -Force -ErrorAction Stop |
            Where-Object { $_.Name -like ($backupPrefix + '*') } |
            Sort-Object -Property Name -Descending)
        for ($index = 1; $index -lt $backups.Count; $index++) {
            $old = $backups[$index]
            try {
                Remove-Item -LiteralPath $old.FullName -Recurse -Force -ErrorAction Stop
                Write-UpdateLog ("  cleanup: removed the older backup {0}" -f $old.FullName)
            } catch {
                Write-UpdateLog ("  cleanup: could not remove the older backup {0}: {1}" -f $old.FullName, $_.Exception.Message)
            }
        }
    } catch {
        Write-UpdateLog ("  cleanup: could not list the older backups: {0}" -f $_.Exception.Message)
    }
}

<#
Asks the installed exe to show the desktop icons again. The app hides them
while it runs, and the Stop-Process in Wait-ForKoboldExit skips its OnExit, so
a force-killed update would otherwise leave the user without desktop icons.
The helper mode runs before the single-instance mutex and only touches the
desktop, so it works while the app is still running. Best effort: a failure is
logged and the kill goes ahead.
#>
function Restore-DesktopIcons {
    param([string]$ExePath)

    if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
        Write-UpdateLog ("  icons: {0} is missing; not restoring the desktop icons" -f $ExePath)
        return
    }

    $helper = $null
    try {
        $helper = Start-Process -FilePath $ExePath -ArgumentList '--restore-desktop-icons' -PassThru -WindowStyle Hidden -ErrorAction Stop
        Write-UpdateLog ("  icons: asked {0} to run --restore-desktop-icons" -f $ExePath)
        if (-not $helper.WaitForExit($iconRestoreTimeoutMs)) {
            Write-UpdateLog ("  icons: the helper did not exit within {0} ms; stopping it" -f $iconRestoreTimeoutMs)
            try { $helper.Kill() } catch { }
        }
    } catch {
        Write-UpdateLog ("  icons: could not restore the desktop icons: {0}" -f $_.Exception.Message)
    }
}

<#
Waits up to $Seconds for the process to exit; if it is still alive, stops only
that PID. A short wait after the kill lets Windows release the exe's file locks.
#>
function Wait-ForKoboldExit {
    param(
        [int]$Id,
        [int]$Seconds,
        [string]$InstallRoot
    )

    if ($Id -le 0) {
        Write-UpdateLog '  wait: no process id was supplied; skipping the wait'
        return
    }

    Write-UpdateLog ("  wait: waiting up to {0} second(s) for process {1} to exit" -f $Seconds, $Id)
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        $process = Get-Process -Id $Id -ErrorAction SilentlyContinue
        if (-not $process) {
            Write-UpdateLog ("  wait: process {0} has exited" -f $Id)
            return
        }
        Start-Sleep -Milliseconds 250
    }

    $process = Get-Process -Id $Id -ErrorAction SilentlyContinue
    if (-not $process) {
        Write-UpdateLog ("  wait: process {0} has exited" -f $Id)
        return
    }

    # PIDs are recycled: after a 30 second wait this may no longer be the app we
    # started out with, and force-stopping a stranger would be a nasty surprise.
    $processName = $null
    try {
        $processName = $process.ProcessName
    } catch {
    }
    if ($processName -ne [System.IO.Path]::GetFileNameWithoutExtension($exeName)) {
        Write-UpdateLog ("  wait: process {0} is now '{1}', not Kobold; leaving it alone" -f $Id, $processName)
        return
    }

    # The force kill below skips the app's normal exit, which is what shows the
    # desktop icons again; a failed relaunch after such a kill must not leave
    # them hidden. Restore them first, then stop the process.
    Restore-DesktopIcons -ExePath (Join-Path $InstallRoot $exeName)

    Write-UpdateLog ("  wait: process {0} is still running; stopping it" -f $Id)
    try {
        Stop-Process -Id $Id -Force -ErrorAction Stop
        $process.WaitForExit(5000) | Out-Null
        Write-UpdateLog ("  wait: process {0} was stopped" -f $Id)
    } catch {
        Write-UpdateLog ("  wait: could not stop process {0}: {1}" -f $Id, $_.Exception.Message)
    }
}

<#
Starts the exe and reports success only when it is still running after
$verifySeconds. A process that exits in that window means the new build is
broken, so the caller rolls back.
#>
function Start-KoboldAndVerify {
    param(
        [string]$ExePath,
        [string]$WorkingDirectory
    )

    if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
        Write-UpdateLog ("  launch: missing {0}" -f $ExePath)
        return $false
    }

    try {
        $process = Start-Process -FilePath $ExePath -WorkingDirectory $WorkingDirectory -PassThru -ErrorAction Stop
    } catch {
        Write-UpdateLog ("  launch: could not start {0}: {1}" -f $ExePath, $_.Exception.Message)
        return $false
    }

    Write-UpdateLog ("  launch: started {0} (pid {1}); waiting {2} second(s)" -f $ExePath, $process.Id, $verifySeconds)
    Start-Sleep -Seconds $verifySeconds
    $process.Refresh()
    if ($process.HasExited) {
        Write-UpdateLog ("  launch: process {0} exited with code {1}" -f $process.Id, $process.ExitCode)
        return $false
    }

    Write-UpdateLog ("  launch: process {0} is still running" -f $process.Id)
    return $true
}

<#
Best-effort restart of the previous copy during rollback. Failing to start it is
logged but does not change the exit code: the update already failed.
#>
function Start-OldKobold {
    param(
        [string]$ExePath,
        [string]$WorkingDirectory
    )

    if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
        Write-UpdateLog ("  rollback: {0} is missing; not restarting the app" -f $ExePath)
        return
    }

    try {
        $process = Start-Process -FilePath $ExePath -WorkingDirectory $WorkingDirectory -PassThru -ErrorAction Stop
        Write-UpdateLog ("  rollback: started the previous copy (pid {0})" -f $process.Id)
    } catch {
        Write-UpdateLog ("  rollback: could not start the previous copy: {0}" -f $_.Exception.Message)
    }
}

$installRoot = Get-NormalizedDirectory $InstallDir
$stageRoot = Get-NormalizedDirectory $StageDir

if (-not $installRoot) {
    Write-UpdateLog ("error: invalid install folder: {0}" -f $InstallDir)
    exit 2
}
if (-not (Test-Path -LiteralPath $installRoot -PathType Container)) {
    Write-UpdateLog ("error: the install folder does not exist: {0}" -f $installRoot)
    exit 2
}
if (-not $stageRoot -or -not (Test-Path -LiteralPath $stageRoot -PathType Container)) {
    Write-UpdateLog ("error: the stage folder does not exist: {0}" -f $StageDir)
    exit 3
}
if (-not (Test-Path -LiteralPath (Join-Path $stageRoot $exeName) -PathType Leaf)) {
    Write-UpdateLog ("error: the stage folder has no {0}" -f $exeName)
    exit 3
}

Write-UpdateLog ("apply-update: pid={0} stage={1} install={2}" -f $ProcessId, $stageRoot, $installRoot)

if ($DryRun) {
    $stamp = Get-Date -Format 'yyyyMMddHHmmss'
    $dryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("Kobold-update-dryrun-{0}-{1}" -f $stamp, $PID)
    $dryBackup = Join-Path $dryRoot 'backup'
    $dryTarget = Join-Path $dryRoot 'target'
    New-Item -ItemType Directory -Force -Path $dryBackup, $dryTarget | Out-Null

    Write-UpdateLog ("dry run: temporary folder {0}" -f $dryRoot)
    Write-UpdateLog 'dry run: step 1/3 - backup the install folder (the process is NOT waited for)'
    if (-not (New-UpdateBackup -Root $installRoot -BackupDir $dryBackup)) {
        Write-UpdateLog 'dry run: the backup failed; stopping'
        exit 2
    }
    Write-UpdateLog 'dry run: step 2/3 - copy the stage into the temporary target and mirror tools flat'
    if (-not (Copy-UpdateTree -SourceDir $stageRoot -DestinationDir $dryTarget)) {
        Write-UpdateLog 'dry run: the copy failed; stopping'
        exit 3
    }
    if (-not (Copy-UpdateToolsFlat -StageDir $stageRoot -InstallRoot $dryTarget)) {
        Write-UpdateLog 'dry run: the tools mirror failed; stopping'
        exit 3
    }
    Write-UpdateLog 'dry run: step 3/3 - skipping the launch (dry run)'
    Write-UpdateLog ("dry run: backup={0} target={1}" -f $dryBackup, $dryTarget)
    Write-UpdateLog 'dry run: success'
    exit 0
}

$backupDir = Join-Path $installRoot ($backupPrefix + (Get-Date -Format 'yyyyMMddHHmmss'))
if (Test-Path -LiteralPath $backupDir) {
    Write-UpdateLog ("error: the backup folder already exists: {0}" -f $backupDir)
    exit 2
}

Wait-ForKoboldExit -Id $ProcessId -Seconds $WaitSeconds -InstallRoot $installRoot

Write-UpdateLog ("step 1/3: backing up the install folder to {0}" -f $backupDir)
try {
    $backupOk = New-UpdateBackup -Root $installRoot -BackupDir $backupDir
} catch {
    Write-UpdateLog ("error: the backup failed: {0}" -f $_.Exception.Message)
    exit 2
}
if (-not $backupOk) {
    Write-UpdateLog 'error: the backup failed; the install folder was not changed'
    exit 2
}

$updated = $false
try {
    Write-UpdateLog 'step 2/3: copying the staged update into the install folder'
    if (-not (Copy-UpdateTree -SourceDir $stageRoot -DestinationDir $installRoot)) {
        throw 'one or more files could not be copied'
    }
    if (-not (Copy-UpdateToolsFlat -StageDir $stageRoot -InstallRoot $installRoot)) {
        throw 'one or more tool scripts could not be mirrored flat'
    }

    Write-UpdateLog 'step 3/3: starting the new copy and checking that it stays up'
    if (-not (Start-KoboldAndVerify -ExePath (Join-Path $installRoot $exeName) -WorkingDirectory $installRoot)) {
        throw 'the new copy did not stay running'
    }
    $updated = $true
} catch {
    Write-UpdateLog ("update failed: {0}" -f $_.Exception.Message)
}

if ($updated) {
    try {
        Remove-Item -LiteralPath $backupDir -Recurse -Force -ErrorAction Stop
        Write-UpdateLog 'success: the update is installed and this backup was removed'
    } catch {
        Write-UpdateLog ("success: the update is installed, but the backup could not be removed: {0}" -f $_.Exception.Message)
    }
    Remove-OldUpdateBackups -InstallRoot $installRoot
    exit 0
}

Write-UpdateLog 'rollback: restoring the backed-up files'
$restored = $false
try {
    try {
        $restored = Restore-UpdateBackup -BackupDir $backupDir -InstallRoot $installRoot
    } catch {
        Write-UpdateLog ("rollback: the restore failed: {0}" -f $_.Exception.Message)
    }
    if (-not $restored) {
        Write-UpdateLog 'rollback: WARNING - the restore was incomplete'
    }

    Remove-UpdateAddedFiles -StageDir $stageRoot -BackupDir $backupDir -InstallRoot $installRoot
} catch {
    # Belt and braces: the added-file cleanup swallows its own errors, but any
    # other surprise here must not skip the restart of the old exe below.
    Write-UpdateLog ("rollback: cleanup failed: {0}" -f $_.Exception.Message)
} finally {
    try {
        Start-OldKobold -ExePath (Join-Path $installRoot $exeName) -WorkingDirectory $installRoot
    } catch {
        Write-UpdateLog ("rollback: could not start the previous copy: {0}" -f $_.Exception.Message)
    }
    Write-UpdateLog 'rollback: finished; exiting 3'
}
exit 3
