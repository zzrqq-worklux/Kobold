# One entry point for the console self-checks and the installer tests - shared
# by local runs and CI. Fails (exit 1) when any selected check fails; never
# reports a skipped or failed check as passed.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -List
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group checks
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group installer
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group all
[CmdletBinding()]
param(
    [ValidateSet('checks', 'ci', 'installer', 'all')] [string]$Group = 'checks',
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Debug',
    [switch]$List
)
$ErrorActionPreference = 'Stop'

# Explicit matrix. Name = tests\<Name>\<Name>.csproj. Ci marks the checks that
# run on GitHub runners; the window/desktop-dependent ones stay local for now
# (they need a real interactive desktop session).
$checks = @(
    @{ Name = 'AtomicFileCheck';       Ci = $true  }
    @{ Name = 'ConfigSafetyCheck';     Ci = $true  }
    @{ Name = 'LangCheck';             Ci = $true  }
    @{ Name = 'WidgetItemsCheck';      Ci = $true  }
    @{ Name = 'ScreenGeometryCheck';   Ci = $true  }
    @{ Name = 'IslandLayoutCheck';     Ci = $true  }
    @{ Name = 'StorageOpsCheck';       Ci = $true  }
    @{ Name = 'UiTokensCheck';         Ci = $true  }
    @{ Name = 'FolderListingCheck';    Ci = $true  }
    @{ Name = 'ShellOpsCheck';         Ci = $true  }
    @{ Name = 'MemoryTrimCheck';       Ci = $true  }
    @{ Name = 'DragOutCheck';          Ci = $true  }
    @{ Name = 'FolderIconCheck';       Ci = $true  }
    @{ Name = 'FolderWatchCheck';      Ci = $true  }
    @{ Name = 'StartupPanelsCheck';    Ci = $true  }
    @{ Name = 'MonitorPlacementCheck'; Ci = $true  }
    @{ Name = 'XamlLoadCheck';         Ci = $false }   # needs an interactive desktop
    @{ Name = 'MenuRenderCheck';       Ci = $false }   # WPF off-screen render details
    @{ Name = 'WindowSwitcherCheck';   Ci = $false }   # real window management
    @{ Name = 'DesktopIconsCheck';     Ci = $false }   # Explorer desktop icon ListView
    @{ Name = 'FolderColorCheck';      Ci = $false }   # end-to-end desktop.ini writes
)

$selected = switch ($Group) {
    'checks'    { $checks }
    'ci'        { @($checks | Where-Object { $_.Ci }) }
    'installer' { @() }
    'all'       { $checks }
}

if ($List) {
    $selected | ForEach-Object { '{0}  Ci={1}' -f $_.Name, $_.Ci }
    if ($Group -in @('installer', 'all')) { Write-Host 'tools\tests\installer.Tests.ps1' }
    return
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet SDK not found.' }

$results = @()
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    foreach ($check in $selected) {
        $project = Join-Path 'tests' (Join-Path $check.Name ("$($check.Name).csproj"))
        Write-Host "`n=== $($check.Name) [$Configuration] ==="
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $passed = $true
        try {
            & dotnet run --project $project -c $Configuration --nologo
            if ($LASTEXITCODE -ne 0) { throw "dotnet exited with $LASTEXITCODE" }
        }
        catch {
            $passed = $false
            Write-Host "FAIL $($check.Name): $($_.Exception.Message)"
        }
        finally {
            $clock.Stop()
            $results += [pscustomobject]@{
                Check   = $check.Name
                Passed  = $passed
                Seconds = [Math]::Round($clock.Elapsed.TotalSeconds, 1)
            }
        }
    }

    if ($Group -in @('installer', 'all')) {
        Write-Host "`n=== installer.Tests.ps1 ==="
        $passed = $true
        try {
            & powershell -NoProfile -ExecutionPolicy Bypass -File 'tools\tests\installer.Tests.ps1'
            if ($LASTEXITCODE -ne 0) { throw "installer tests exited with $LASTEXITCODE" }
        }
        catch {
            $passed = $false
            Write-Host "FAIL installer: $($_.Exception.Message)"
        }
        $results += [pscustomobject]@{ Check = 'installer'; Passed = $passed; Seconds = 0 }
    }
}
finally { Pop-Location }

$results | Format-Table -AutoSize | Out-Host
$failed = @($results | Where-Object { -not $_.Passed }).Count
if ($failed) {
    Write-Host "$failed check(s) failed."
    exit 1
}
Write-Host "All $($results.Count) check(s) passed."
