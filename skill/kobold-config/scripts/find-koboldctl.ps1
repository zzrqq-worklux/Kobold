# find-koboldctl.ps1 - print the first existing koboldctl.ps1 path.
#
# Search order:
#   1. -Path (a file, or a folder containing koboldctl.ps1)
#   2. %LocalAppData%\Programs\Kobold\koboldctl.ps1
#   3. tools\koboldctl.ps1 three levels above this script's folder
#   4. tools\koboldctl.ps1 in the current directory
#
# Exits 0 and prints the absolute path, or exits 1 with a message on stderr.
# This script only locates the client; all commands live in koboldctl.ps1.

[CmdletBinding()]
param(
    [string]$Path
)

$ErrorActionPreference = 'Stop'

function Get-ControlScriptPath {
    param([string]$Candidate)

    if ([string]::IsNullOrWhiteSpace($Candidate)) { return $null }

    try {
        $full = $Candidate
        if (-not [System.IO.Path]::IsPathRooted($full)) {
            $full = Join-Path (Get-Location).Path $full
        }

        if (Test-Path -LiteralPath $full -PathType Container) {
            $full = Join-Path $full 'koboldctl.ps1'
        }

        if (Test-Path -LiteralPath $full -PathType Leaf) {
            return (Resolve-Path -LiteralPath $full).ProviderPath
        }
    }
    catch {
        return $null
    }

    return $null
}

$candidates = New-Object System.Collections.ArrayList

if (-not [string]::IsNullOrWhiteSpace($Path)) {
    [void]$candidates.Add([pscustomobject]@{
        Value = $Path
        Explicit = $true
    })
}

if (-not [string]::IsNullOrEmpty($env:LOCALAPPDATA)) {
    [void]$candidates.Add([pscustomobject]@{
        Value = (Join-Path $env:LOCALAPPDATA 'Programs\Kobold\koboldctl.ps1')
        Explicit = $false
    })
}

$repositoryRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
[void]$candidates.Add([pscustomobject]@{
    Value = (Join-Path $repositoryRoot 'tools\koboldctl.ps1')
    Explicit = $false
})

[void]$candidates.Add([pscustomobject]@{
    Value = (Join-Path (Get-Location).Path 'tools\koboldctl.ps1')
    Explicit = $false
})

foreach ($candidate in $candidates) {
    $found = Get-ControlScriptPath -Candidate $candidate.Value
    if ($null -ne $found) {
        Write-Output $found
        exit 0
    }

    if ($candidate.Explicit) {
        [Console]::Error.WriteLine(
            'koboldctl.ps1 was not found at the -Path value: ' + $candidate.Value)
        exit 1
    }
}

[Console]::Error.WriteLine(
    'koboldctl.ps1 was not found. Checked the install directory, the repository tools folder and the current directory. Pass -Path <file-or-folder> when it lives elsewhere.')
exit 1
