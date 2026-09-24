# Self-check for tools\release-notes.ps1: runs the real script against
# throwaway changelogs in a temp sandbox. Logic only - no network, no gh.
# Exit code 0 = all green; 1 = failures.
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File tools\tests\release-notes.Tests.ps1

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

$notesScript = Join-Path (Split-Path $PSScriptRoot -Parent) 'release-notes.ps1'
$tmp = Join-Path $env:TEMP ('kobold-release-notes-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

# The scripts are invoked in-process: a child powershell.exe may be blocked by
# security software on some machines, and release-notes.ps1 signals failure the
# PowerShell way (a terminating error, which also exits -File runs with code 1).
function Invoke-Notes {
    param([string]$Version, [string]$Csproj, [string]$Changelog)
    $out = Join-Path $tmp ('notes-' + [guid]::NewGuid().ToString('N') + '.md')
    $arguments = @{ OutFile = $out }
    if ($Version) { $arguments.Version = $Version }
    if ($Csproj) { $arguments.Csproj = $Csproj }
    if ($Changelog) { $arguments.Changelog = $Changelog }

    $threw = $false
    try {
        & $notesScript @arguments | Out-Null
    } catch {
        $threw = $true
    }
    $text = ''
    if (Test-Path -LiteralPath $out) { $text = Get-Content -LiteralPath $out -Raw -Encoding UTF8 }
    $code = 0
    if ($threw) { $code = 1 }
    return @{ Code = $code; Text = $text }
}

try {
    $changelog = Join-Path $tmp 'CHANGELOG.md'
    @'
# Log

## Unreleased

- work in progress

## v1.2.0 (2026-09-23)

- folder colors
- quieter windows

## v1.20.0 (2027-01-01)

- future release
'@ | Set-Content -LiteralPath $changelog -Encoding UTF8

    Write-Host ''
    Write-Host 'Extraction'

    $r = Invoke-Notes -Version '1.2.0' -Changelog $changelog
    Check ($r.Code -eq 0) 'exits 0 for an existing section'
    Check ($r.Text -match 'folder colors' -and $r.Text -match 'quieter windows') 'keeps the section body'
    Check ($r.Text -notmatch 'future release') 'v1.2.0 does not swallow v1.20.0'
    Check ($r.Text -notmatch 'work in progress') 'does not swallow the previous section'

    Write-Host ''
    Write-Host 'Downloads'

    Check ($r.Text -match 'releases/download/v1\.2\.0/Kobold-v1\.2\.0-win-x64\.zip') 'appends the zip download link'
    Check ($r.Text -match 'Downloads') 'has a downloads heading'

    Write-Host ''
    Write-Host 'Encoding'

    # Repo markdown carries no BOM, and a PowerShell 5.1 Get-Content without an
    # explicit encoding would then read it as ANSI and mangle every Chinese line.
    $cnLog = @'
# Log

## v2.0.0

- 中文条目与下载
'@
    [IO.File]::WriteAllText($changelog, $cnLog, (New-Object System.Text.UTF8Encoding($false)))
    $r = Invoke-Notes -Version '2.0.0' -Changelog $changelog
    Check ($r.Text -match '中文条目与下载') 'non-ASCII changelog entries survive the round trip'
    Check ($r.Text -match '下载') 'the downloads heading keeps its Chinese half'

    Write-Host ''
    Write-Host 'Failure cases'

    $r = Invoke-Notes -Version '9.9.9' -Changelog $changelog
    Check ($r.Code -ne 0) 'missing version section exits non-zero'

    @'
# Log

## v0.0.1

## v1.0.0

- real
'@ | Set-Content -LiteralPath $changelog -Encoding UTF8
    $r = Invoke-Notes -Version '0.0.1' -Changelog $changelog
    Check ($r.Code -ne 0) 'empty version section exits non-zero'

    Write-Host ''
    Write-Host 'Version source'

    $csproj = Join-Path $tmp 'Kobold.csproj'
    '<Project><PropertyGroup><Version>1.2.0</Version></PropertyGroup></Project>' |
        Set-Content -LiteralPath $csproj -Encoding UTF8
    @'
# Log

## v1.2.0

- read from the csproj
'@ | Set-Content -LiteralPath $changelog -Encoding UTF8
    $r = Invoke-Notes -Csproj $csproj -Changelog $changelog
    Check ($r.Code -eq 0 -and $r.Text -match 'read from the csproj') 'reads the version from the csproj when -Version is omitted'
}
finally {
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host "passed: $script:Passed, failed: $script:Failed"
if ($script:Failed -gt 0) { exit 1 }
exit 0
