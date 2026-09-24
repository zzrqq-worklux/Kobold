# Self-checks for the Kobold installer scripts.
#
# Everything here is logic-level and quiet: the installer mechanics live in
# installer-common.ps1 as functions, and this file drives them against temp
# folders. No child processes, no registry writes, no shortcuts, nothing
# executed - so an antivirus has no reason to react.
#
# The end-to-end path (shortcut, uninstall entry, launching the app) is covered
# by a one-off manual drill before a release, not by this suite.
#
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File tools\tests\installer.Tests.ps1
# Exit: 0 = all green, 1 = failures

$ErrorActionPreference = 'Stop'

$toolsDir = Split-Path $PSScriptRoot -Parent
. (Join-Path $toolsDir 'installer-common.ps1')

$runtimeFiles = @('Kobold.exe', 'Kobold.exe.config', 'Hardcodet.NotifyIcon.Wpf.dll', 'Newtonsoft.Json.dll')
$uninstallerFiles = @('uninstall.cmd', 'uninstall.ps1', 'installer-common.ps1')

$script:Passed = 0
$script:Failed = 0

function Check {
    param([bool]$Condition, [string]$Name)
    if ($Condition) {
        $script:Passed++
        Write-Host "  ok   $Name"
    } else {
        $script:Failed++
        Write-Host "  FAIL $Name" -ForegroundColor Red
    }
}

function Check-Throws {
    param([scriptblock]$Body, [string]$Match, [string]$Name)
    try {
        & $Body
        $script:Failed++
        Write-Host "  FAIL $Name (nothing was thrown)" -ForegroundColor Red
    } catch {
        if ($_.Exception.Message -match $Match) {
            $script:Passed++
            Write-Host "  ok   $Name"
        } else {
            $script:Failed++
            Write-Host "  FAIL $Name (message was: $($_.Exception.Message))" -ForegroundColor Red
        }
    }
}

function Section {
    param([string]$Name, [scriptblock]$Body)
    Write-Host ''
    Write-Host $Name -ForegroundColor Cyan
    try { & $Body } catch {
        $script:Failed++
        Write-Host "  FAIL section threw: $($_.Exception.Message)" -ForegroundColor Red
    }
}

# Sandbox root: %TEMP% normally, but some antivirus products refuse writes of
# '<exe>.config' inside user temp (a known sidecar-hijack vector), so probe for
# a writable location and fall back to an ignored repo-local folder.
function Get-KoboldTestSandbox {
    param([string]$Name)
    foreach ($root in @($env:TEMP, (Join-Path $PSScriptRoot '.sandbox'))) {
        if (-not $root) { continue }
        try {
            $candidate = Join-Path $root $Name
            New-Item -ItemType Directory -Force -Path $candidate | Out-Null
            $probe = Join-Path $candidate 'probe.exe.config'
            Set-Content -LiteralPath $probe -Value 'probe' -ErrorAction Stop
            Remove-Item -LiteralPath $probe -Force
            return $candidate
        } catch { }
    }
    throw 'installer tests: no writable sandbox location found'
}

$sandbox = Get-KoboldTestSandbox -Name ('kobold-installer-tests-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $sandbox 'source'
$installDir = Join-Path $sandbox 'install'
$dataDir = Join-Path $sandbox 'data'

function New-FakeSource {
    New-Item -ItemType Directory -Force -Path $source | Out-Null
    foreach ($file in $runtimeFiles) { Set-Content -LiteralPath (Join-Path $source $file) -Value "stub: $file" }
}

function New-DataDir {
    New-Item -ItemType Directory -Force -Path (Join-Path $dataDir 'Storage'), (Join-Path $dataDir 'FolderIcons') | Out-Null
    Set-Content -LiteralPath (Join-Path $dataDir 'config.json') -Value '{}'
    Set-Content -LiteralPath (Join-Path $dataDir 'config.json.backup') -Value '{}'
    Set-Content -LiteralPath (Join-Path $dataDir 'Storage\note.md') -Value 'stub'
    Set-Content -LiteralPath (Join-Path $dataDir 'Storage\photo.png') -Value 'stub'
    Set-Content -LiteralPath (Join-Path $dataDir 'FolderIcons\blue.ico') -Value 'stub'
}

try {
    New-FakeSource

    Section 'Copy-KoboldFile copies a file' {
        $from = Join-Path $sandbox 'copy-from.txt'
        $to = Join-Path $sandbox 'copy-to.txt'
        Set-Content -LiteralPath $from -Value 'payload'
        Copy-KoboldFile -Source $from -Destination $to
        Check ((Get-Content -LiteralPath $to -Raw) -match 'payload') 'the destination holds the content'
    }

    Section 'Copy-KoboldFile reports a locked destination' {
        $from = Join-Path $sandbox 'copy-from-2.txt'
        Set-Content -LiteralPath $from -Value 'payload'
        $locked = Join-Path $sandbox 'locked.txt'
        Set-Content -LiteralPath $locked -Value 'x'
        $handle = [System.IO.File]::Open($locked, 'Open', 'ReadWrite', 'None')
        try {
            Check-Throws { Copy-KoboldFile -Source $from -Destination $locked -Attempts 2 } 'locked' 'it throws and says the file is locked'
        } finally { $handle.Dispose() }
    }

    Section 'Get-KoboldSourceVersion reads the exe' {
        Check ((Get-KoboldSourceVersion -ExePath (Join-Path $source 'Kobold.exe')) -eq '0.0.0') 'a file without version info reports 0.0.0'
        $systemExe = Join-Path $env:SystemRoot 'System32\cmd.exe'
        $version = Get-KoboldSourceVersion -ExePath $systemExe
        Check ($version -match '^\d+\.') 'a real exe reports a version'
        Check ($version -notmatch '\+') 'SourceLink build metadata is dropped'
    }

    Section 'Install-KoboldFiles copies the runtime files and the uninstaller' {
        Install-KoboldFiles -Source $source -InstallDir $installDir -ScriptsDir $toolsDir
        foreach ($file in ($runtimeFiles + $uninstallerFiles)) {
            Check (Test-Path -LiteralPath (Join-Path $installDir $file)) "installed $file"
        }
        Check (@(Get-ChildItem -LiteralPath $installDir -File).Count -eq 7) 'the install folder holds exactly the expected files'
    }

    Section 'Install-KoboldFiles upgrades in place' {
        Set-Content -LiteralPath (Join-Path $source 'Kobold.exe') -Value 'stub v2'
        Install-KoboldFiles -Source $source -InstallDir $installDir -ScriptsDir $toolsDir
        Check ((Get-Content -LiteralPath (Join-Path $installDir 'Kobold.exe') -Raw) -match 'stub v2') 'the runtime files are refreshed'
        Check (@(Get-ChildItem -LiteralPath $installDir -File).Count -eq 7) 'no files pile up'
    }

    Section 'Install-KoboldFiles refuses an incomplete source' {
        $brokenSource = Join-Path $sandbox 'broken-source'
        $brokenTarget = Join-Path $sandbox 'install-broken'
        New-Item -ItemType Directory -Force -Path $brokenSource | Out-Null
        Set-Content -LiteralPath (Join-Path $brokenSource 'Kobold.exe') -Value 'stub'
        Check-Throws { Install-KoboldFiles -Source $brokenSource -InstallDir $brokenTarget -ScriptsDir $toolsDir } 'Hardcodet\.NotifyIcon\.Wpf\.dll' 'the error names the missing file'
        Check (-not (Test-Path -LiteralPath $brokenTarget)) 'no half-install is left behind'
    }

    Section 'Get-KoboldUninstallEntry builds the Apps & Features values' {
        $entry = Get-KoboldUninstallEntry -InstallDir 'C:\Apps\Kobold' -Version '1.2.3' -SizeKb 4096 -Publisher 'zzrqq-worklux'
        Check ($entry.DisplayName.Value -eq 'Kobold') 'DisplayName'
        Check ($entry.DisplayVersion.Value -eq '1.2.3') 'DisplayVersion'
        Check ($entry.Publisher.Value -eq 'zzrqq-worklux') 'Publisher'
        Check ($entry.InstallLocation.Value -eq 'C:\Apps\Kobold') 'InstallLocation'
        Check ($entry.DisplayIcon.Value -eq '"C:\Apps\Kobold\Kobold.exe",0') 'DisplayIcon is the exe'
        Check ($entry.UninstallString.Value -eq '"C:\Apps\Kobold\uninstall.cmd"') 'UninstallString is quoted'
        Check ($entry.QuietUninstallString.Value -eq '"C:\Apps\Kobold\uninstall.cmd" -Quiet') 'QuietUninstallString asks for a quiet run'
        Check ($entry.NoModify.Value -eq 1 -and $entry.NoModify.Type -eq 'DWord') 'NoModify is a DWord of 1'
        Check ($entry.NoRepair.Value -eq 1 -and $entry.NoRepair.Type -eq 'DWord') 'NoRepair is a DWord of 1'
        Check ($entry.EstimatedSize.Value -eq 4096 -and $entry.EstimatedSize.Type -eq 'DWord') 'EstimatedSize is a DWord'
    }

    Section 'Test-KoboldProcessOwnedBy tells copies apart' {
        Check (Test-KoboldProcessOwnedBy -ProcessPath 'C:\Apps\Kobold\Kobold.exe' -InstallDir 'C:\Apps\Kobold') 'a copy inside the folder is ours'
        Check (Test-KoboldProcessOwnedBy -ProcessPath 'c:\apps\kobold\sub\Kobold.exe' -InstallDir 'C:\Apps\Kobold') 'the comparison ignores case'
        Check (-not (Test-KoboldProcessOwnedBy -ProcessPath 'D:\Portable\Kobold\Kobold.exe' -InstallDir 'C:\Apps\Kobold')) 'a copy elsewhere is not'
        Check (-not (Test-KoboldProcessOwnedBy -ProcessPath 'C:\Apps\Kobold' -InstallDir 'C:\Apps\Kobold')) 'the folder itself is not a process path'
        Check (-not (Test-KoboldProcessOwnedBy -ProcessPath '' -InstallDir 'C:\Apps\Kobold')) 'an inaccessible path is not ours'
    }

    Section 'Test-KoboldRunKeyOwned guards the autostart entry' {
        $ownExe = Join-Path $sandbox 'own\Kobold.exe'
        $otherExe = Join-Path $sandbox 'other\Kobold.exe'
        $goneExe = Join-Path $sandbox 'gone\Kobold.exe'
        New-Item -ItemType Directory -Force -Path (Split-Path $ownExe), (Split-Path $otherExe) | Out-Null
        Set-Content -LiteralPath $ownExe -Value 'stub'
        Set-Content -LiteralPath $otherExe -Value 'stub'
        $ownDir = Split-Path $ownExe -Parent

        Check (Test-KoboldRunKeyOwned -RunValue ('"' + $ownExe + '"') -InstallDir $ownDir) 'our own entry is ours to remove'
        Check (Test-KoboldRunKeyOwned -RunValue $ownExe -InstallDir $ownDir) 'an unquoted path works too'
        Check (-not (Test-KoboldRunKeyOwned -RunValue ('"' + $otherExe + '"') -InstallDir $ownDir)) 'a live copy elsewhere keeps its entry'
        Check (Test-KoboldRunKeyOwned -RunValue ('"' + $goneExe + '"') -InstallDir $ownDir) 'an entry pointing at a deleted copy is cleaned up'
        Check (-not (Test-KoboldRunKeyOwned -RunValue '' -InstallDir $ownDir)) 'an empty value is ignored'
    }

    Section 'Get-KoboldDataInventory counts the user data' {
        New-DataDir
        $inventory = Get-KoboldDataInventory -Path $dataDir
        Check ($inventory.Exists) 'the data folder is found'
        Check ($inventory.ConfigFiles.Count -eq 2) 'both config files are counted'
        Check ($inventory.StorageFiles.Count -eq 2) 'the stored files are counted'
        Check ($inventory.StorageBytes -gt 0) 'their size is reported'
        Check ($inventory.IconFiles.Count -eq 1) 'the folder icons are counted'

        $nothing = Get-KoboldDataInventory -Path (Join-Path $sandbox 'no-data')
        Check (-not $nothing.Exists) 'a missing folder is reported as missing'
        Check ($nothing.StorageFiles.Count -eq 0 -and $nothing.StorageBytes -eq 0) 'and counts nothing'
    }

    Section 'Remove-KoboldData honours the choices' {
        Remove-KoboldData -Path $dataDir -Choice 'Keep'
        Check (Test-Path -LiteralPath (Join-Path $dataDir 'config.json')) 'Keep leaves the settings alone'

        Remove-KoboldData -Path $dataDir -Choice 'ConfigOnly'
        Check (-not (Test-Path -LiteralPath (Join-Path $dataDir 'config.json'))) 'ConfigOnly deletes config.json'
        Check (-not (Test-Path -LiteralPath (Join-Path $dataDir 'config.json.backup'))) 'ConfigOnly deletes the backup'
        Check (Test-Path -LiteralPath (Join-Path $dataDir 'Storage\note.md')) 'ConfigOnly keeps the stored files'
        Check (Test-Path -LiteralPath (Join-Path $dataDir 'FolderIcons\blue.ico')) 'ConfigOnly keeps the folder icons'

        Remove-KoboldData -Path $dataDir -Choice 'All'
        Check (-not (Test-Path -LiteralPath $dataDir)) 'All deletes the data folder'
    }

    Section 'The double-click wrappers point at the right scripts' {
        foreach ($pair in @(@('install.cmd', 'install.ps1'), @('uninstall.cmd', 'uninstall.ps1'))) {
            $wrapper = Join-Path $toolsDir $pair[0]
            Check (Test-Path -LiteralPath $wrapper) "$($pair[0]) exists"
            if (Test-Path -LiteralPath $wrapper) {
                $content = Get-Content -LiteralPath $wrapper -Raw
                Check ($content -match [regex]::Escape($pair[1])) "$($pair[0]) runs $($pair[1])"
                Check ($content -match 'ExecutionPolicy Bypass') "$($pair[0]) bypasses the execution policy"
                Check ($content -match '"%~1"==""') "$($pair[0]) only pauses when double-clicked"
            }
        }
    }

    # The suite loads installer-common.ps1 only; a syntax error in an entry
    # script would otherwise stay invisible until someone runs it.
    Section 'The entry scripts parse' {
        foreach ($script in 'install.ps1', 'uninstall.ps1') {
            $tokens = $null
            $errors = $null
            [void][System.Management.Automation.Language.Parser]::ParseFile((Join-Path $toolsDir $script), [ref]$tokens, [ref]$errors)
            if ($errors.Count -eq 0) { Check $true "$script parses" }
            else { Check $false "$script parses - $($errors[0].Message)" }
        }
    }
}
finally {
    if (Test-Path -LiteralPath $sandbox) { Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host "passed: $script:Passed, failed: $script:Failed"
if ($script:Failed -gt 0) { exit 1 }
exit 0
