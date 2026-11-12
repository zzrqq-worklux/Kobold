# koboldctl.ps1 - the local control client for Kobold.
#
# Speaks one JSON line per connection to the app's current-user named pipe.
# Read-only commands never write; config/widget/group writes use the server's
# revision and confirm rules. See docs/superpowers/specs/2026-11-10-kobold-
# agent-control-design.md section 9 for the command surface.
#
# Exit codes: 0 success, 1 validation/conflict/business, 2 connection/protocol,
# 3 restart required but --restart was not passed.

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function New-ErrorResponse {
    param(
        [string]$Code,
        [string]$Message,
        $Details = $null
    )

    $errorObject = [ordered]@{
        code = $Code
        message = $Message
    }
    if ($null -ne $Details) { $errorObject['details'] = $Details }

    return [pscustomobject]@{
        v = 1
        ok = $false
        error = [pscustomobject]$errorObject
    }
}

function Get-Prop {
    param($Object, [string]$Name)

    if ($null -eq $Object) { return $null }
    if ($Object -is [System.Collections.IDictionary]) { return $Object[$Name] }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Test-ResponseOk {
    param($Response)

    return ($null -ne $Response) -and ($Response.ok -eq $true)
}

function Get-DefaultPipeName {
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $session = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
    return "Kobold.Control.v1.$sid.$session"
}

<#
Sends exactly one JSON request line over one pipe connection and returns the
parsed response. A missing pipe becomes an app_not_running response; a broken
or incompatible response becomes control_unavailable/unsupported_protocol.
#>
function Invoke-Control {
    param(
        [Parameter(Mandatory = $true)][string]$PipeName,
        [Parameter(Mandatory = $true)][string]$Method,
        [hashtable]$Params = @{},
        [string]$BaseRevision = $null
    )

    $request = [ordered]@{
        v = 1
        id = [guid]::NewGuid().ToString('N')
        method = $Method
        params = $Params
    }
    if (-not [string]::IsNullOrEmpty($BaseRevision)) {
        $request.baseRevision = $BaseRevision
    }
    $json = ConvertTo-Json -InputObject $request -Depth 20 -Compress

    $client = New-Object System.IO.Pipes.NamedPipeClientStream(
        '.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
    $connected = $false
    try {
        $client.Connect(3000)
        $connected = $true

        $encoding = New-Object System.Text.UTF8Encoding($false)
        $writer = New-Object System.IO.StreamWriter($client, $encoding, 4096, $true)
        $writer.AutoFlush = $true
        $writer.WriteLine($json)
        $writer.Flush()

        $reader = New-Object System.IO.StreamReader(
            $client, [System.Text.Encoding]::UTF8, $true, 4096, $true)
        $line = $reader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($line)) {
            return New-ErrorResponse -Code 'control_unavailable' `
                -Message 'Kobold closed the control pipe without a response.'
        }

        try {
            $response = ConvertFrom-Json -InputObject $line
        }
        catch {
            return New-ErrorResponse -Code 'control_unavailable' `
                -Message 'Kobold returned a response that is not valid JSON.'
        }
        if ($null -eq $response) {
            return New-ErrorResponse -Code 'control_unavailable' `
                -Message 'Kobold returned an empty response.'
        }

        $version = Get-Prop $response 'v'
        if ($null -eq $version -or [int]$version -ne 1) {
            return New-ErrorResponse -Code 'unsupported_protocol' `
                -Message 'Kobold speaks an unsupported control protocol; update the app.'
        }

        return $response
    }
    catch [System.TimeoutException] {
        return New-ErrorResponse -Code 'app_not_running' `
            -Message "Kobold is not running (control pipe '$PipeName' timed out)."
    }
    catch [System.IO.IOException] {
        if ($connected) {
            return New-ErrorResponse -Code 'control_unavailable' `
                -Message "The control pipe '$PipeName' closed unexpectedly."
        }
        return New-ErrorResponse -Code 'app_not_running' `
            -Message "Kobold is not running (control pipe '$PipeName' was not found)."
    }
    catch {
        if ($connected) {
            return New-ErrorResponse -Code 'control_unavailable' `
                -Message ("The control pipe failed: " + $_.Exception.Message)
        }
        return New-ErrorResponse -Code 'app_not_running' `
            -Message ("Kobold is not running (control pipe '$PipeName' was not found).")
    }
    finally {
        if ($null -ne $client) { $client.Dispose() }
    }
}

function Read-TextFile {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { throw '--file is required.' }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw ("File not found: " + $Path)
    }
    return [System.IO.File]::ReadAllText($Path)
}

function Read-JsonFile {
    param([string]$Path)

    $text = Read-TextFile -Path $Path
    if ([string]::IsNullOrWhiteSpace($text)) { throw ("File is empty: " + $Path) }
    try {
        return ConvertFrom-Json -InputObject $text
    }
    catch {
        throw ("File is not valid JSON: " + $Path + " - " + $_.Exception.Message)
    }
}

function ConvertTo-IntOption {
    param([string]$Name, [string]$Value)

    $parsed = 0
    if (-not [int]::TryParse($Value, [ref]$parsed)) {
        throw ("--" + $Name + " must be an integer.")
    }
    return $parsed
}

function Resolve-KoboldExe {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (Test-Path -LiteralPath $ExplicitPath -PathType Leaf) {
            return (Resolve-Path -LiteralPath $ExplicitPath).ProviderPath
        }
        return $null
    }

    $candidates = New-Object System.Collections.ArrayList
    if (-not [string]::IsNullOrEmpty($env:LOCALAPPDATA)) {
        [void]$candidates.Add((Join-Path $env:LOCALAPPDATA 'Programs\Kobold\Kobold.exe'))
    }
    [void]$candidates.Add((Join-Path (Split-Path -Parent $PSScriptRoot) 'bin\Debug\net48\Kobold.exe'))

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    return $null
}

function Test-ControlAvailable {
    param([string]$PipeName)

    $response = Invoke-Control -PipeName $PipeName -Method 'diag.ping'
    return (Test-ResponseOk $response)
}

function Wait-ForStatus {
    param([string]$PipeName, [int]$TimeoutSeconds)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $last = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $last = Invoke-Control -PipeName $PipeName -Method 'diag.snapshot'
        if (Test-ResponseOk $last) { return $last }
        Start-Sleep -Milliseconds 250
    }
    return $last
}

function Ensure-KoboldRunning {
    param([string]$PipeName, [string]$ExePath)

    if (Test-ControlAvailable -PipeName $PipeName) { return $null }

    if ([string]::IsNullOrWhiteSpace($ExePath)) {
        return New-ErrorResponse -Code 'app_not_running' `
            -Message 'Kobold is not running and Kobold.exe was not found. Pass -Exe <path>.'
    }

    try {
        Start-Process -FilePath $ExePath -WorkingDirectory (Split-Path -Parent $ExePath) | Out-Null
    }
    catch {
        return New-ErrorResponse -Code 'app_not_running' `
            -Message ("Kobold could not be started: " + $_.Exception.Message)
    }

    $status = Wait-ForStatus -PipeName $PipeName -TimeoutSeconds 10
    if (-not (Test-ResponseOk $status)) {
        return New-ErrorResponse -Code 'app_not_running' `
            -Message 'Kobold did not open its control pipe within 10 seconds.'
    }
    return $null
}

function Wait-ForRestart {
    param([string]$PipeName, [string]$PreviousPid, [int]$TimeoutSeconds)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $started = [DateTime]::UtcNow
    Start-Sleep -Milliseconds 500
    while ([DateTime]::UtcNow -lt $deadline) {
        $probe = Invoke-Control -PipeName $PipeName -Method 'diag.snapshot'
        if (Test-ResponseOk $probe) {
            $probePid = [string](Get-Prop (Get-Prop (Get-Prop $probe 'result') 'app') 'pid')
            if (-not [string]::IsNullOrEmpty($PreviousPid)) {
                if ($probePid -ne $PreviousPid) { return $probe }
            }
            elseif (([DateTime]::UtcNow - $started).TotalSeconds -ge 2) {
                return $probe
            }
        }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Resolve-BaseRevision {
    param([string]$PipeName, [string]$ExplicitRevision)

    if (-not [string]::IsNullOrEmpty($ExplicitRevision)) {
        return [pscustomobject]@{
            Ok = $true
            Revision = $ExplicitRevision
            Response = $null
        }
    }

    $response = Invoke-Control -PipeName $PipeName -Method 'config.get' -Params @{ view = 'summary' }
    if (-not (Test-ResponseOk $response)) {
        return [pscustomobject]@{
            Ok = $false
            Revision = $null
            Response = $response
        }
    }

    $revision = [string](Get-Prop (Get-Prop $response 'result') 'revision')
    if ([string]::IsNullOrEmpty($revision)) {
        return [pscustomobject]@{
            Ok = $false
            Revision = $null
            Response = (New-ErrorResponse -Code 'control_unavailable' `
                -Message 'config.get returned no revision.')
        }
    }

    return [pscustomobject]@{
        Ok = $true
        Revision = $revision
        Response = $null
    }
}

function Get-ResponseExitCode {
    param($Response)

    $code = [string](Get-Prop (Get-Prop $Response 'error') 'code')
    switch ($code) {
        'app_not_running' { return 2 }
        'control_unavailable' { return 2 }
        'invalid_request' { return 2 }
        'unknown_method' { return 2 }
        'unsupported_protocol' { return 2 }
        'restart_required' { return 3 }
        default { return 1 }
    }
}

function Format-Revision {
    param([string]$Revision)

    if ([string]::IsNullOrEmpty($Revision)) { return '' }
    if ($Revision.Length -le 12) { return $Revision }
    return $Revision.Substring(0, 12)
}

function Join-Values {
    param($Value)

    if ($null -eq $Value) { return '' }
    if ($Value -is [string]) { return $Value }
    return (@($Value) | ForEach-Object { [string]$_ }) -join ', '
}

function Get-Count {
    param($Value)

    if ($null -eq $Value) { return 0 }
    if ($Value -is [string]) { return 1 }
    if ($Value -is [System.Collections.ICollection]) { return $Value.Count }
    return @($Value.PSObject.Properties).Count
}

function Write-HumanResponse {
    param($Response)

    if ($null -eq $Response) {
        Write-Output ('error: no response')
        return
    }

    if (-not (Test-ResponseOk $Response)) {
        $errorObject = Get-Prop $Response 'error'
        $code = Get-Prop $errorObject 'code'
        $message = Get-Prop $errorObject 'message'
        Write-Output (("error: [{0}] {1}" -f $code, $message))
        $currentRevision = Get-Prop (Get-Prop $errorObject 'details') 'currentRevision'
        if (-not [string]::IsNullOrEmpty([string]$currentRevision)) {
            Write-Output ('currentRevision: ' + (Format-Revision ([string]$currentRevision)))
        }
        return
    }

    $result = Get-Prop $Response 'result'
    if ($null -eq $result) {
        Write-Output ('ok')
        return
    }

    if ($result -is [System.Collections.IEnumerable] -and $result -isnot [string] -and
        $result -isnot [System.Management.Automation.PSCustomObject]) {
        Write-Output ('items: ' + (@($result).Count))
        return
    }

    $config = Get-Prop $result 'config'
    $path = Get-Prop $result 'path'
    if ([string]::IsNullOrEmpty([string]$path)) { $path = Get-Prop $config 'path' }
    if (-not [string]::IsNullOrEmpty([string]$path)) {
        Write-Output ('path: ' + $path)
    }

    $revision = Get-Prop $result 'revision'
    if ([string]::IsNullOrEmpty([string]$revision)) { $revision = Get-Prop $config 'revision' }
    if (-not [string]::IsNullOrEmpty([string]$revision)) {
        Write-Output ('revision: ' + (Format-Revision ([string]$revision)))
    }

    $changes = Get-Prop $result 'changes'
    if ($null -ne $changes) {
        Write-Output ('changes: ' + (Join-Values $changes))
    }

    $restartRequired = Get-Prop $result 'restartRequired'
    if ($null -ne $restartRequired) {
        Write-Output ('restartRequired: ' + ([bool]$restartRequired).ToString().ToLowerInvariant())
    }

    $restarting = Get-Prop $result 'restarting'
    if ($null -ne $restarting) {
        Write-Output ('restarting: ' + ([bool]$restarting).ToString().ToLowerInvariant())
    }

    if ((Get-Prop $result 'restartFailed') -eq $true) {
        Write-Output ('restartFailed: true')
    }

    $app = Get-Prop $result 'app'
    if ($null -ne $app) {
        Write-Output (("app: version {0}, pid {1}" -f
            (Get-Prop $app 'version'), (Get-Prop $app 'pid')))
    }
    $widgetCount = Get-Prop $config 'widgetCount'
    $groupCount = Get-Prop $config 'groupCount'
    if ($null -ne $widgetCount -or $null -ne $groupCount) {
        Write-Output (("config: {0} widgets, {1} groups" -f $widgetCount, $groupCount))
    }

    $state = Get-Prop $result 'state'
    if (-not [string]::IsNullOrEmpty([string]$state)) {
        Write-Output ('state: ' + $state)
    }
    $filesIndexed = Get-Prop $result 'filesIndexed'
    if ($null -ne $filesIndexed) {
        Write-Output ('filesIndexed: ' + $filesIndexed)
    }
    $totalMatches = Get-Prop $result 'totalMatches'
    if ($null -ne $totalMatches) {
        Write-Output ('matches: ' + $totalMatches)
    }

    foreach ($name in 'id', 'groupId', 'removed', 'standaloneId') {
        $value = Get-Prop $result $name
        if (-not [string]::IsNullOrEmpty([string]$value)) {
            Write-Output (($name + ': ' + $value))
        }
    }
    $changed = Get-Prop $result 'changed'
    if ($null -ne $changed) {
        Write-Output ('changed: ' + (Join-Values $changed))
    }
    $standaloneIds = Get-Prop $result 'standaloneIds'
    if ($null -ne $standaloneIds) {
        Write-Output ('standaloneIds: ' + (Join-Values $standaloneIds))
    }
    $dissolved = Get-Prop $result 'dissolved'
    if ($null -ne $dissolved) {
        Write-Output ('dissolved: ' + ([bool]$dissolved).ToString().ToLowerInvariant())
    }

    $validation = Get-Prop $result 'ok'
    if ($null -ne $validation) {
        Write-Output ('validation: ' + $(if ($validation -eq $true) { 'ok' } else { 'failed' }))
    }
    $warnings = Get-Prop $result 'warnings'
    if ($null -ne $warnings) {
        Write-Output ('warnings: ' + (Get-Count $warnings))
    }
    $fields = Get-Prop $result 'fields'
    if ($null -ne $fields) {
        Write-Output ('fields: ' + (Get-Count $fields))
    }
    $widgetKinds = Get-Prop $result 'widgetKinds'
    if ($null -ne $widgetKinds) {
        Write-Output ('widget kinds: ' + (Get-Count $widgetKinds))
    }
    $lines = Get-Prop $result 'lines'
    if ($null -ne $lines) {
        Write-Output ('lines: ' + (Get-Count $lines))
    }
}

function Write-Response {
    param($Response, [bool]$AsJson)

    if ($AsJson) {
        Write-Output ((ConvertTo-Json -InputObject $Response -Depth 20))
        return
    }
    Write-HumanResponse -Response $Response
}

function Assert-Positionals {
    param([System.Collections.ArrayList]$List, [int]$Expected, [string]$Usage)

    if ($List.Count -ne $Expected) {
        throw ('Unexpected arguments. Usage: ' + $Usage)
    }
}

function Show-Usage {
    Write-Output @'
Usage: koboldctl.ps1 <command> [options]

  status [--json]
  config get [--summary|--full] [--json]
  config describe [--json]
  config validate --file <json> [--json]
  config patch --file <patch.json> [--base-revision <r>] [--dry-run] [--yes]
  config replace --file <config.json> [--base-revision <r>] [--dry-run] [--yes] [--restart]
  config backup [--label <name>] [--json]
  config restore --backup <path> [--yes] [--restart]
  widget add --kind <kind> [--name <n>] [--color <hex>] [--pinned [<bool>]]
  widget update --id <id> [--name <n>] [--color <hex>] [--locked [<bool>]] [--pinned [<bool>]]
  widget delete --id <id> --yes
  group merge --target <id> --member <id> [--member <id> ...] [--json]
  group detach --group <id> --widget <id> [--json]
  group reorder --group <id> --from <n> --to <n> [--json]
  group dissolve --group <id> --yes
  search status [--json]
  search query --text <text> [--limit <n>] [--json]
  search rebuild|cancel|pause|resume|reconcile [--yes]
  diag snapshot [--json]
  diag logs --name app|search [--lines <n>] [--json]
  diag folders [--json]

Global options:
  -Exe <path>     Explicit Kobold.exe used by --start (--exe also works).
  --pipe <name>   Override the control pipe (tests/multi-session debugging).
  --json          Print the raw response JSON; errors are JSON too.
  --start         Start Kobold and wait up to 10s for its control pipe.
  --restart       Restart after config replace/restore.
'@
}

$jsonRequested = $false
foreach ($argument in @($args)) {
    if ([string]$argument -eq '--json' -or [string]$argument -like '--json=*') {
        $jsonRequested = $true
    }
}

try {
    $arguments = @($args)
    $options = @{
        'member' = New-Object System.Collections.ArrayList
    }
    $positionals = New-Object System.Collections.ArrayList

    $flagOptions = @('json', 'summary', 'full', 'dry-run', 'yes', 'restart', 'start', 'help', 'h')
    $optionalBoolOptions = @('pinned', 'locked')
    $valueOptions = @(
        'pipe', 'file', 'base-revision', 'label', 'kind', 'name', 'color', 'id',
        'target', 'group', 'widget', 'from', 'to', 'text', 'limit', 'backup',
        'lines', 'member', 'exe'
    )

    for ($i = 0; $i -lt $arguments.Count; $i++) {
        $token = [string]$arguments[$i]

        # The exe path follows PowerShell's single-dash convention in the spec.
        if ($token -ieq '-Exe') {
            if (($i + 1) -ge $arguments.Count) { throw '-Exe requires a path.' }
            $i++
            $options['exe'] = [string]$arguments[$i]
            continue
        }
        if ($token -imatch '^-Exe=(.*)$') {
            $options['exe'] = $Matches[1]
            if ([string]::IsNullOrWhiteSpace([string]$options['exe'])) {
                throw '-Exe requires a path.'
            }
            continue
        }

        if ($token -match '^--([^=]+)=(.*)$') {
            $name = $Matches[1].ToLowerInvariant()
            $value = $Matches[2]
            if ($optionalBoolOptions -contains $name) {
                if ($value -notmatch '^(?i:true|false)$') {
                    throw ("--" + $name + " must be true or false.")
                }
                $options[$name] = [bool]::Parse($value)
            }
            elseif ($flagOptions -contains $name) {
                $options[$name] = $true
            }
            elseif ($valueOptions -contains $name) {
                if ($name -eq 'member') {
                    [void]$options['member'].Add($value)
                }
                else {
                    $options[$name] = $value
                }
            }
            else {
                throw ('Unknown option: --' + $name)
            }
            continue
        }

        if ($token -match '^--(.+)$') {
            $name = $Matches[1].ToLowerInvariant()
            if ($flagOptions -contains $name) {
                $options[$name] = $true
                continue
            }
            if ($optionalBoolOptions -contains $name) {
                if (($i + 1) -lt $arguments.Count -and
                    ([string]$arguments[$i + 1] -match '^(?i:true|false)$')) {
                    $i++
                    $options[$name] = [bool]::Parse([string]$arguments[$i])
                }
                else {
                    $options[$name] = $true
                }
                continue
            }
            if ($valueOptions -notcontains $name) {
                throw ('Unknown option: --' + $name)
            }
            if (($i + 1) -ge $arguments.Count) {
                throw ('Option --' + $name + ' requires a value.')
            }
            $i++
            $value = [string]$arguments[$i]
            if ($name -eq 'member') {
                [void]$options['member'].Add($value)
            }
            else {
                $options[$name] = $value
            }
            continue
        }

        [void]$positionals.Add($token)
    }

    if ($options.ContainsKey('help') -or $options.ContainsKey('h')) {
        Show-Usage
        exit 0
    }

    if ($positionals.Count -eq 0) {
        throw 'A command is required. Run koboldctl.ps1 --help for usage.'
    }

    $command = [string]$positionals[0]
    $subcommand = if ($positionals.Count -gt 1) { [string]$positionals[1] } else { $null }
    $method = $null
    $params = @{}
    $needsRevision = $false
    $restartRequested = [bool]$options['restart']
    $dryRun = [bool]$options['dry-run']
    $validateResult = $false

    switch ($command) {
        'status' {
            Assert-Positionals $positionals 1 'koboldctl.ps1 status [--json]'
            $method = 'diag.snapshot'
        }
        'config' {
            if ([string]::IsNullOrEmpty($subcommand)) {
                throw 'config requires a subcommand: get, describe, validate, patch, replace, backup or restore.'
            }
            switch ($subcommand) {
                'get' {
                    Assert-Positionals $positionals 2 'koboldctl.ps1 config get [--summary|--full]'
                    if ($options.ContainsKey('summary') -and $options.ContainsKey('full')) {
                        throw '--summary and --full cannot be combined.'
                    }
                    $method = 'config.get'
                    $params['view'] = if ($options.ContainsKey('full')) { 'full' } else { 'summary' }
                }
                'describe' {
                    Assert-Positionals $positionals 2 'koboldctl.ps1 config describe'
                    $method = 'config.describe'
                }
                'validate' {
                    Assert-Positionals $positionals 2 'koboldctl.ps1 config validate --file <json>'
                    $method = 'config.validate'
                    $params['document'] = Read-TextFile -Path ([string]$options['file'])
                    $validateResult = $true
                }
                'patch' {
                    Assert-Positionals $positionals 2 'koboldctl.ps1 config patch --file <patch.json>'
                    $method = 'config.patch'
                    $patch = Read-JsonFile -Path ([string]$options['file'])
                    if ($null -eq $patch -or $patch -isnot [System.Management.Automation.PSCustomObject]) {
                        throw 'The patch file must contain a JSON object.'
                    }
                    $params['patch'] = $patch
                    if ($dryRun) { $params['dryRun'] = $true }
                    if ($options['yes']) { $params['confirm'] = $true }
                    $needsRevision = $true
                }
                'replace' {
                    Assert-Positionals $positionals 2 'koboldctl.ps1 config replace --file <config.json>'
                    $method = 'config.replace'
                    $params['document'] = Read-TextFile -Path ([string]$options['file'])
                    if ($dryRun) { $params['dryRun'] = $true }
                    if ($restartRequested) { $params['restart'] = $true }
                    if ($options['yes']) { $params['confirm'] = $true }
                    $needsRevision = $true
                }
                'backup' {
                    Assert-Positionals $positionals 2 'koboldctl.ps1 config backup [--label <name>]'
                    $method = 'config.backup'
                    if ($options.ContainsKey('label')) { $params['label'] = [string]$options['label'] }
                }
                'restore' {
                    Assert-Positionals $positionals 2 'koboldctl.ps1 config restore --backup <path>'
                    if ([string]::IsNullOrWhiteSpace([string]$options['backup'])) {
                        throw '--backup is required.'
                    }
                    $method = 'config.restore'
                    $params['backup'] = [string]$options['backup']
                    if ($restartRequested) { $params['restart'] = $true }
                    if ($options['yes']) { $params['confirm'] = $true }
                    $needsRevision = $true
                }
                default { throw ('Unknown config command: ' + $subcommand) }
            }
        }
        'widget' {
            if ([string]::IsNullOrEmpty($subcommand)) {
                throw 'widget requires a subcommand: add, update or delete.'
            }
            switch ($subcommand) {
                'add' {
                    Assert-Positionals $positionals 2 'koboldctl.ps1 widget add --kind <kind>'
                    if ([string]::IsNullOrWhiteSpace([string]$options['kind'])) {
                        throw '--kind is required.'
                    }
                    $method = 'widget.add'
                    $params['kind'] = [string]$options['kind']
                    foreach ($name in 'name', 'color') {
                        if ($options.ContainsKey($name)) { $params[$name] = [string]$options[$name] }
                    }
                    if ($options.ContainsKey('pinned')) { $params['pinned'] = [bool]$options['pinned'] }
                }
                'update' {
                    Assert-Positionals $positionals 2 'koboldctl.ps1 widget update --id <id>'
                    if ([string]::IsNullOrWhiteSpace([string]$options['id'])) {
                        throw '--id is required.'
                    }
                    $method = 'widget.update'
                    $params['id'] = [string]$options['id']
                    foreach ($name in 'name', 'color') {
                        if ($options.ContainsKey($name)) { $params[$name] = [string]$options[$name] }
                    }
                    foreach ($name in 'locked', 'pinned') {
                        if ($options.ContainsKey($name)) { $params[$name] = [bool]$options[$name] }
                    }
                }
                'delete' {
                    Assert-Positionals $positionals 2 'koboldctl.ps1 widget delete --id <id> --yes'
                    if ([string]::IsNullOrWhiteSpace([string]$options['id'])) {
                        throw '--id is required.'
                    }
                    $method = 'widget.delete'
                    $params['id'] = [string]$options['id']
                    if ($options['yes']) { $params['confirm'] = $true }
                }
                default { throw ('Unknown widget command: ' + $subcommand) }
            }
        }
        'group' {
            if ([string]::IsNullOrEmpty($subcommand)) {
                throw 'group requires a subcommand: merge, detach, reorder or dissolve.'
            }
            switch ($subcommand) {
                'merge' {
                    Assert-Positionals $positionals 2 'koboldctl.ps1 group merge --target <id> --member <id>'
                    if ([string]::IsNullOrWhiteSpace([string]$options['target'])) {
                        throw '--target is required.'
                    }
                    $members = @($options['member'])
                    if ($members.Count -eq 0) { throw 'At least one --member is required.' }
                    $method = 'group.merge'
                    $params['targetId'] = [string]$options['target']
                    $params['memberIds'] = $members
                }
                'detach' {
                    Assert-Positionals $positionals 2 'koboldctl.ps1 group detach --group <id> --widget <id>'
                    if ([string]::IsNullOrWhiteSpace([string]$options['group']) -or
                        [string]::IsNullOrWhiteSpace([string]$options['widget'])) {
                        throw '--group and --widget are required.'
                    }
                    $method = 'group.detach'
                    $params['groupId'] = [string]$options['group']
                    $params['widgetId'] = [string]$options['widget']
                }
                'reorder' {
                    Assert-Positionals $positionals 2 'koboldctl.ps1 group reorder --group <id> --from <n> --to <n>'
                    if ([string]::IsNullOrWhiteSpace([string]$options['group']) -or
                        -not $options.ContainsKey('from') -or -not $options.ContainsKey('to')) {
                        throw '--group, --from and --to are required.'
                    }
                    $method = 'group.reorder'
                    $params['groupId'] = [string]$options['group']
                    $params['from'] = ConvertTo-IntOption -Name 'from' -Value ([string]$options['from'])
                    $params['to'] = ConvertTo-IntOption -Name 'to' -Value ([string]$options['to'])
                }
                'dissolve' {
                    Assert-Positionals $positionals 2 'koboldctl.ps1 group dissolve --group <id> --yes'
                    if ([string]::IsNullOrWhiteSpace([string]$options['group'])) {
                        throw '--group is required.'
                    }
                    $method = 'group.dissolve'
                    $params['groupId'] = [string]$options['group']
                    if ($options['yes']) { $params['confirm'] = $true }
                }
                default { throw ('Unknown group command: ' + $subcommand) }
            }
        }
        'search' {
            if ([string]::IsNullOrEmpty($subcommand)) {
                throw 'search requires a subcommand: status, query, rebuild, cancel, pause, resume or reconcile.'
            }
            switch ($subcommand) {
                'status' {
                    Assert-Positionals $positionals 2 'koboldctl.ps1 search status'
                    $method = 'diag.search.status'
                }
                'query' {
                    Assert-Positionals $positionals 2 'koboldctl.ps1 search query --text <text>'
                    if (-not $options.ContainsKey('text')) { throw '--text is required.' }
                    $method = 'diag.search.query'
                    $params['text'] = [string]$options['text']
                    if ($options.ContainsKey('limit')) {
                        $params['limit'] = ConvertTo-IntOption -Name 'limit' -Value ([string]$options['limit'])
                    }
                }
                default {
                    if (@('rebuild', 'cancel', 'pause', 'resume', 'reconcile') -notcontains $subcommand) {
                        throw ('Unknown search command: ' + $subcommand)
                    }
                    Assert-Positionals $positionals 2 ('koboldctl.ps1 search ' + $subcommand)
                    $method = 'search.' + $subcommand
                    if ($subcommand -eq 'rebuild' -and $options['yes']) {
                        $params['confirm'] = $true
                    }
                }
            }
        }
        'diag' {
            if ([string]::IsNullOrEmpty($subcommand)) {
                throw 'diag requires a subcommand: snapshot, logs or folders.'
            }
            switch ($subcommand) {
                'snapshot' {
                    Assert-Positionals $positionals 2 'koboldctl.ps1 diag snapshot'
                    $method = 'diag.snapshot'
                }
                'logs' {
                    Assert-Positionals $positionals 2 'koboldctl.ps1 diag logs --name app|search'
                    $name = [string]$options['name']
                    if ($name -ne 'app' -and $name -ne 'search') {
                        throw '--name must be app or search.'
                    }
                    $method = 'diag.logs.tail'
                    $params['name'] = $name
                    if ($options.ContainsKey('lines')) {
                        $params['lines'] = ConvertTo-IntOption -Name 'lines' -Value ([string]$options['lines'])
                    }
                }
                'folders' {
                    Assert-Positionals $positionals 2 'koboldctl.ps1 diag folders'
                    $method = 'diag.folders'
                }
                default { throw ('Unknown diag command: ' + $subcommand) }
            }
        }
        default { throw ('Unknown command: ' + $command) }
    }

    if ($options.ContainsKey('summary') -or $options.ContainsKey('full')) {
        if ($method -ne 'config.get') { throw '--summary and --full only apply to config get.' }
    }
    if ($dryRun -and $method -notin @('config.patch', 'config.replace')) {
        throw '--dry-run only applies to config patch and config replace.'
    }
    if ($restartRequested -and $method -notin @('config.replace', 'config.restore')) {
        throw '--restart only applies to config replace and config restore.'
    }
    if ($options.ContainsKey('base-revision') -and -not $needsRevision) {
        throw '--base-revision only applies to config patch, replace and restore.'
    }
    if ($options.ContainsKey('file') -and $method -notin @(
            'config.validate', 'config.patch', 'config.replace')) {
        throw '--file only applies to config validate, patch and replace.'
    }
    if ($options.ContainsKey('backup') -and $method -ne 'config.restore') {
        throw '--backup only applies to config restore.'
    }
    if ($options.ContainsKey('label') -and $method -ne 'config.backup') {
        throw '--label only applies to config backup.'
    }

    $pipeName = if ($options.ContainsKey('pipe') -and
        -not [string]::IsNullOrWhiteSpace([string]$options['pipe'])) {
        [string]$options['pipe']
    }
    else {
        Get-DefaultPipeName
    }
    $asJson = [bool]$options['json']

    if ($options['start']) {
        $exePath = Resolve-KoboldExe -ExplicitPath ([string]$options['exe'])
        $startError = Ensure-KoboldRunning -PipeName $pipeName -ExePath $exePath
        if ($null -ne $startError) {
            Write-Response -Response $startError -AsJson $asJson
            exit (Get-ResponseExitCode $startError)
        }
    }

    $baseRevision = $null
    if ($needsRevision) {
        $resolved = Resolve-BaseRevision -PipeName $pipeName `
            -ExplicitRevision ([string]$options['base-revision'])
        if (-not $resolved.Ok) {
            Write-Response -Response $resolved.Response -AsJson $asJson
            exit (Get-ResponseExitCode $resolved.Response)
        }
        $baseRevision = [string]$resolved.Revision
    }

    $previousPid = $null
    if ($restartRequested) {
        $before = Invoke-Control -PipeName $pipeName -Method 'diag.snapshot'
        if (Test-ResponseOk $before) {
            $previousPid = [string](Get-Prop (Get-Prop (Get-Prop $before 'result') 'app') 'pid')
        }
    }

    $response = Invoke-Control -PipeName $pipeName -Method $method -Params $params `
        -BaseRevision $baseRevision

    if (Test-ResponseOk $response) {
        $result = Get-Prop $response 'result'
        if ((Get-Prop $result 'restarting') -eq $true) {
            if (-not $asJson) {
                Write-Output ('waiting for Kobold to restart...')
            }
            $probe = Wait-ForRestart -PipeName $pipeName -PreviousPid $previousPid `
                -TimeoutSeconds 20
            if ($null -eq $probe) {
                $response = New-ErrorResponse -Code 'restart_failed' `
                    -Message 'Kobold did not come back on the control pipe within 20 seconds.'
                Write-Response -Response $response -AsJson $asJson
                exit 1
            }
        }
        elseif ((Get-Prop $result 'restartFailed') -eq $true) {
            $response = New-ErrorResponse -Code 'restart_failed' `
                -Message 'Kobold could not start the restart helper; the config backup was kept.'
            Write-Response -Response $response -AsJson $asJson
            exit 1
        }
    }

    Write-Response -Response $response -AsJson $asJson
    if (-not (Test-ResponseOk $response)) {
        exit (Get-ResponseExitCode $response)
    }

    $result = Get-Prop $response 'result'
    if ($validateResult -and (Get-Prop $result 'ok') -eq $false) { exit 1 }
    # Exit 3 means "a real apply needs --restart": a dry run writes nothing and
    # already reported the requirement in its result.
    if ((Get-Prop $result 'restartRequired') -eq $true -and
        -not $dryRun -and -not $restartRequested) {
        exit 3
    }
    exit 0
}
catch {
    $message = $_.Exception.Message
    $errorResponse = New-ErrorResponse -Code 'validation_failed' -Message $message
    Write-Response -Response $errorResponse -AsJson $jsonRequested
    exit 1
}
