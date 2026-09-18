<#
.SYNOPSIS
Launches the installed generic Grid reference collector through MO2 and SSEEdit64.
.DESCRIPTION
Requires an existing MO2 executable definition, supplies a case-local xEdit
plugin-list override, records launch evidence, and never terminates MO2 or
SSEEdit. The function performs no profile, plugin, save, or game mutation.
#>
function ConvertTo-GridWindowsCommandLineArgument {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Value)
    if ($Value -notmatch '[\s"]') { return $Value }
    $builder = New-Object Text.StringBuilder
    [void]$builder.Append('"')
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') { $slashes++; continue }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * (($slashes * 2) + 1)))
            [void]$builder.Append('"')
            $slashes = 0
            continue
        }
        if ($slashes -gt 0) { [void]$builder.Append(('\' * $slashes)); $slashes = 0 }
        [void]$builder.Append($character)
    }
    if ($slashes -gt 0) { [void]$builder.Append(('\' * ($slashes * 2))) }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Start-GridProcessWithSerializedArguments {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][AllowEmptyString()][string]$ArgumentString,
        [Parameter(Mandatory)][string]$WorkingDirectory
    )
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.Arguments = $ArgumentString
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "ProcessLaunchFailed: '$FilePath' did not start." }
    return $process
}

function Get-GridMo2ConfiguredExecutable {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ConfigurationPath, [Parameter(Mandatory)][string]$Title)
    $section = ''
    $entries = @{}
    foreach ($line in [IO.File]::ReadAllLines($ConfigurationPath)) {
        $trimmed = $line.Trim()
        if ($trimmed -match '^\[(.+)\]$') { $section = $matches[1]; continue }
        if ($section -ne 'customExecutables' -or -not $trimmed -or $trimmed.StartsWith('#') -or $trimmed.StartsWith(';')) { continue }
        $separator = $line.IndexOf('=')
        if ($separator -le 0) { continue }
        $key = $line.Substring(0, $separator).Trim()
        if ($key -notmatch '^(\d+)\\(.+)$') { continue }
        $index = [int]$matches[1]
        $field = $matches[2]
        $value = $line.Substring($separator + 1).Trim()
        if ($value.StartsWith('@ByteArray(') -and $value.EndsWith(')')) { $value = $value.Substring(11, $value.Length - 12) }
        $value = $value.Replace('\\', '\')
        if (-not $entries.ContainsKey($index)) { $entries[$index] = @{} }
        $entries[$index][$field] = $value
    }
    $found = @($entries.GetEnumerator() | Where-Object { $_.Value['title'] -eq $Title })
    if ($found.Count -eq 0) { throw "MO2 configured executable was not found: $Title" }
    if ($found.Count -ne 1) { throw "MO2 configured executable title is ambiguous: $Title" }
    $entry = $found[0]
    $binary = [string]$entry.Value['binary']
    if ([string]::IsNullOrWhiteSpace($binary) -or -not [IO.Path]::IsPathRooted($binary)) { throw "MO2 executable '$Title' does not contain a supported absolute binary path." }
    $binary = [IO.Path]::GetFullPath($binary)
    if ([IO.Path]::GetFileName($binary) -ine 'SSEEdit64.exe') { throw "MO2 executable '$Title' resolves to '$([IO.Path]::GetFileName($binary))'. Grid requires SSEEdit64.exe and refuses SSEEdit.exe." }
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw "Configured SSEEdit64 binary is missing: $binary" }
    [pscustomobject]@{ SourceIndex = $entry.Key; Title = $Title; Binary = $binary; WorkingDirectory = [string]$entry.Value['workingDirectory']; ConfiguredArguments = [string]$entry.Value['arguments'] }
}

function New-GridMo2XEditInvocation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ExecutableTitle,
        [Parameter(Mandatory)][string]$PluginsPath,
        [Parameter(Mandatory)][string]$QueryPath,
        [Parameter(Mandatory)][string]$OutputPath,
        [Parameter(Mandatory)][string]$StatusPath,
        [int]$MaximumTraversalRecords = 250000,
        [int]$MaximumOutputRows = 100000,
        [int]$MaximumOutputBytes = 33554432,
        [string]$ScriptName = 'Trace-GridReference'
    )
    foreach ($value in @($ExecutableTitle, $PluginsPath, $QueryPath, $OutputPath, $StatusPath, $ScriptName)) {
        if ($value.Contains('"') -or $value.IndexOfAny([char[]]@(0, 10, 13)) -ge 0) { throw 'Executable titles, script names, and selection paths cannot contain quotes or control characters.' }
    }
    if ([IO.Path]::GetFileName($PluginsPath) -cne 'plugins.txt') { throw 'The xEdit plugin override must be a case-local plugins.txt file.' }
    $caseRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $PluginsPath) '..')).TrimEnd('\')
    foreach ($path in @($PluginsPath, $QueryPath, $OutputPath, $StatusPath)) {
        if (-not [IO.Path]::IsPathRooted($path)) { throw "Every xEdit case path must be absolute: $path" }
        if ($path.StartsWith('\\')) { throw "UNC and device paths are not supported for xEdit case evidence: $path" }
        $fullPath = [IO.Path]::GetFullPath($path)
        if (-not $fullPath.StartsWith($caseRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Every forwarded xEdit path must remain beneath the same case directory: $path"
        }
    }
    foreach ($limit in @($MaximumTraversalRecords, $MaximumOutputRows, $MaximumOutputBytes)) {
        if ($limit -lt 1) { throw 'Every xEdit collector limit must be positive.' }
    }
    $scriptFile = if ([IO.Path]::GetExtension($ScriptName)) {
        $ScriptName
    }
    else {
        "$ScriptName.pas"
    }
    $xeditArguments = ('-P:"{0}" -autoload -autoexit -script:"{1}" -gridquery:"{2}" -gridoutput:"{3}" -gridstatus:"{4}" -gridmaxtraversal:{5} -gridmaxrows:{6} -gridmaxbytes:{7}' -f $PluginsPath, $scriptFile, $QueryPath, $OutputPath, $StatusPath, $MaximumTraversalRecords, $MaximumOutputRows, $MaximumOutputBytes)
    if ($xeditArguments -notmatch '^-P:".+\\plugins\.txt" -autoload ') { throw 'Refusing autoload because the case-local xEdit plugin-list override was not constructed reliably.' }
    $mo2Tokens = @('run', '-e', $ExecutableTitle, '-a', $xeditArguments)
    [pscustomobject]@{
        XEditArguments = $xeditArguments
        ArgumentString = [string]::Join(' ', @($mo2Tokens | ForEach-Object { ConvertTo-GridWindowsCommandLineArgument $_ }))
    }
}

function Copy-GridXEditLaunchLogs {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string[]]$Roots, [Parameter(Mandatory)][string]$CaseDirectory, [Parameter(Mandatory)][datetime]$StartedAt)
    $destination = Join-Path $CaseDirectory 'launch-logs'
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $copied = New-Object Collections.Generic.List[string]
    foreach ($root in $Roots | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        Get-ChildItem -LiteralPath $root -File -ErrorAction SilentlyContinue | Where-Object {
            $_.LastWriteTime -ge $StartedAt.AddMinutes(-1) -and $_.Name -match '(?i)(sseedit|xedit|modorganizer|mo_interface).*(log|txt)$'
        } | ForEach-Object {
            $safeRootName = ([IO.Path]::GetFileName($root.TrimEnd('\')) -replace '[^A-Za-z0-9._-]', '_')
            $target = Join-Path $destination ($safeRootName + '-' + $_.Name)
            Copy-Item -LiteralPath $_.FullName -Destination $target -Force -ErrorAction SilentlyContinue
            if (Test-Path -LiteralPath $target -PathType Leaf) { $copied.Add($target) }
        }
    }
    return @($copied)
}

function Get-GridXEditFailureKind {
    [CmdletBinding()]
    param([string[]]$LogPaths, [string]$CollectorStage, [string]$Default = 'LaunchFailed')
    $text = [string]::Join("`n", @($LogPaths | ForEach-Object { Get-Content -LiteralPath $_ -Raw -ErrorAction SilentlyContinue }))
    if ($text -match '(?i)(EOutOfMemory|out of memory|not enough storage|memory allocation.*fail)') { return 'ModuleLoadingOutOfMemory' }
    if (($CollectorStage + "`n" + $text) -match '(?i)(script.*(compile|runtime|error|exception)|error in unit|undeclared identifier|exception in unit)') { return 'ScriptFailure' }
    return $Default
}

function Get-GridXEditRunningProcesses {
    [CmdletBinding()]
    param()
    # Isolated so tests can override this function to simulate an already-running or closed xEdit deterministically.
    return @(Get-Process -Name 'SSEEdit', 'SSEEdit64' -ErrorAction SilentlyContinue)
}

function Resolve-GridXEditLaunchProcessGate {
    [CmdletBinding()]
    param([AllowEmptyCollection()][object[]]$Processes = @())

    $blockedNames = @('ModOrganizer', 'SkyrimSE', 'skse64_loader', 'SkyrimSELauncher', 'SSEEdit', 'SSEEdit64')
    $blocked = @($Processes | Where-Object {
        $name = if ($_.PSObject.Properties['ProcessName']) { [string]$_.ProcessName } else { [string]$_.Name }
        $blockedNames -icontains $name
    } | ForEach-Object {
        $name = if ($_.PSObject.Properties['ProcessName']) { [string]$_.ProcessName } else { [string]$_.Name }
        [pscustomobject][ordered]@{ processName = $name; processId = [int]$_.Id }
    } | Sort-Object processName, processId)

    [pscustomobject][ordered]@{
        status = if ($blocked.Count -eq 0) { 'Ready' } else { 'NeedsProcessClosure' }
        blockingProcesses = $blocked
        mutationAuthorized = $false
    }
}

function Get-GridXEditProcessIdentity {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Process)
    $path = $null
    $startedAtUtc = $null
    try { $path = [IO.Path]::GetFullPath([string]$Process.Path) } catch { }
    try { $startedAtUtc = ([datetime]$Process.StartTime).ToUniversalTime() } catch { }
    if ([string]::IsNullOrWhiteSpace($path) -or $null -eq $startedAtUtc) { return $null }
    $ancestorProcessIds = @()
    if ($Process.PSObject.Properties['AncestorProcessIds']) {
        $ancestorProcessIds = @($Process.AncestorProcessIds | ForEach-Object { [int]$_ })
    }
    else {
        try {
            $seen = @{}
            $parentRow = Get-CimInstance Win32_Process -Filter "ProcessId = $([int]$Process.Id)" -ErrorAction Stop
            $parentId = [int]$parentRow.ParentProcessId
            $depth = 0
            $ancestors = New-Object Collections.Generic.List[int]
            while ($parentId -gt 0 -and $depth -lt 32 -and -not $seen.ContainsKey($parentId)) {
                $seen[$parentId] = $true
                $ancestors.Add($parentId)
                $depth++
                $parentRow = Get-CimInstance Win32_Process -Filter "ProcessId = $parentId" -ErrorAction SilentlyContinue
                if (-not $parentRow) { break }
                $parentId = [int]$parentRow.ParentProcessId
            }
            $ancestorProcessIds = @($ancestors)
        } catch { $ancestorProcessIds = @() }
    }
    [pscustomobject]@{
        Process = $Process
        ProcessId = [int]$Process.Id
        ExecutablePath = $path
        StartTimeUtc = $startedAtUtc
        IdentityKey = ('{0}|{1}' -f ([int]$Process.Id), $startedAtUtc.Ticks)
        AncestorProcessIds = @($ancestorProcessIds)
    }
}

function Resolve-GridXEditLaunchProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Candidates,
        [Parameter(Mandatory)][string]$ConfiguredExecutable,
        [Parameter(Mandatory)][datetime]$LaunchBoundaryUtc,
        [Parameter(Mandatory)][int]$ManagerProcessId,
        [string[]]$PrelaunchIdentityKeys = @()
    )
    $configuredPath = [IO.Path]::GetFullPath($ConfiguredExecutable)
    $matches = New-Object Collections.Generic.List[object]
    foreach ($candidate in @($Candidates)) {
        $identity = Get-GridXEditProcessIdentity -Process $candidate
        if (-not $identity) { continue }
        if ([string]$identity.ExecutablePath -ine $configuredPath) { continue }
        if ([datetime]$identity.StartTimeUtc -lt $LaunchBoundaryUtc.ToUniversalTime()) { continue }
        if (@($PrelaunchIdentityKeys) -contains [string]$identity.IdentityKey) { continue }
        if (-not (@($identity.AncestorProcessIds) -contains $ManagerProcessId)) { continue }
        $matches.Add($identity)
    }
    if ($matches.Count -gt 1) {
        return [pscustomobject]@{ Status = 'Ambiguous'; Identity = $null; ProcessIds = @($matches | ForEach-Object ProcessId) }
    }
    if ($matches.Count -eq 1) {
        return [pscustomobject]@{ Status = 'Bound'; Identity = $matches[0]; ProcessIds = @([int]$matches[0].ProcessId) }
    }
    [pscustomobject]@{ Status = 'NotObserved'; Identity = $null; ProcessIds = @() }
}

function Test-GridXEditProcessOwnershipMatch {
    [CmdletBinding()]
    param([Parameter(Mandatory)][hashtable]$Ownership, [Parameter(Mandatory)]$Process)
    if ([int]$Ownership.ProcessId -ne [int]$Process.Id) { return $false }
    $currentPath = $null
    $currentStartUtc = $null
    try { $currentPath = [IO.Path]::GetFullPath([string]$Process.Path) } catch { return $false }
    try { $currentStartUtc = ([datetime]$Process.StartTime).ToUniversalTime() } catch { return $false }
    if ($currentPath -ine [IO.Path]::GetFullPath([string]$Ownership.ExecutablePath)) { return $false }
    $currentStartUtc -eq ([datetime]$Ownership.StartTime).ToUniversalTime()
}

function Test-GridXEditCurrentLaunchReport {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ReportPath, $Ownership, [Parameter(Mandatory)][datetime]$LaunchBoundaryUtc)
    if (-not $Ownership -or -not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) { return $false }
    try { (Get-Item -LiteralPath $ReportPath -ErrorAction Stop).LastWriteTimeUtc -ge $LaunchBoundaryUtc.ToUniversalTime() } catch { $false }
}

function Assert-GridXEditQuerySelectionBinding {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Context, [Parameter(Mandatory)]$QueryPackage, [Parameter(Mandatory)]$Selection)
    if ([string]$QueryPackage.ContextFingerprint -ne [string]$Context.Fingerprint) { throw 'The xEdit query package is stale for the selected MO2 context.' }
    if (@($QueryPackage.Queries).Count -eq 0) { throw 'The xEdit query package contains no bounded queries.' }
    foreach ($query in @($QueryPackage.Queries)) {
        $plugin = [string]$query.plugin
        if ($plugin -and -not (@($Selection.Plugins) -contains $plugin)) {
            throw "Query plugin is outside the selected target/master closure: $plugin"
        }
    }
}

function Start-GridXEditProbe {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Context,
        [Parameter(Mandatory)][string]$CaseDirectory,
        [string]$ExecutableTitle = 'SSEEdit',
        [int]$TimeoutSeconds = 600,
        [int]$ExitGraceSeconds = 15,
        [int]$MaxExpectedPluginCount = 256,
        [Parameter(Mandatory)][string[]]$Targets,
        [Parameter(Mandatory)]$QueryPackage,
        [switch]$CloseOwnedProcesses,
        [int]$CloseGraceSeconds = 15,
        [switch]$ForceCloseOwnedProcesses
    )
    $mo2Executable = Join-Path $Context.Mo2Root 'ModOrganizer.exe'
    if (-not (Test-Path -LiteralPath $mo2Executable -PathType Leaf)) { return [pscustomobject]@{ Status = 'Unavailable'; Reason = 'ModOrganizer.exe was not found.'; ReportPath = $null } }
    $processGate = Resolve-GridXEditLaunchProcessGate -Processes @(Get-Process -Name 'ModOrganizer', 'SkyrimSE', 'skse64_loader', 'SkyrimSELauncher', 'SSEEdit', 'SSEEdit64' -ErrorAction SilentlyContinue)
    if ($processGate.status -ne 'Ready') {
        $labels = @($processGate.blockingProcesses | ForEach-Object { "$($_.processName) (PID $($_.processId))" })
        return [pscustomobject]@{
            Status = 'NeedsProcessClosure'
            Reason = 'Close the connected MO2, Skyrim, SKSE, launcher, and xEdit processes before this bounded read-only xEdit collection. Running: ' + ($labels -join ', ') + '. Grid did not launch or terminate anything.'
            ReportPath = $null
            BlockingProcesses = @($processGate.blockingProcesses)
            MutationAuthorized = $false
        }
    }

    try {
        $definition = Get-GridMo2ConfiguredExecutable -ConfigurationPath $Context.Mo2ConfigurationPath -Title $ExecutableTitle
        $selection = New-GridXEditPluginSelection -Context $Context -CaseDirectory $CaseDirectory -Targets $Targets
        Assert-GridXEditQuerySelectionBinding -Context $Context -QueryPackage $QueryPackage -Selection $selection
    } catch { return [pscustomobject]@{ Status = 'Unavailable'; Reason = $_.Exception.Message; ReportPath = $null } }

    if (@($selection.Plugins).Count -gt $MaxExpectedPluginCount) {
        return [pscustomobject]@{
            Status = 'Unavailable'
            Reason = "The exact-target dependency closure selected $(@($selection.Plugins).Count) plugins, exceeding the $MaxExpectedPluginCount-plugin safety threshold. Grid did not launch MO2 or SSEEdit64. Review the preserved selection evidence before rerunning: $($selection.EvidencePath)"
            ReportPath = $null
        }
    }
    if (@($selection.Plugins).Count -gt [int]$QueryPackage.Limits.maximumPlugins) {
        return [pscustomobject]@{
            Status = 'Unavailable'
            Reason = "The dependency closure exceeds the query package's $($QueryPackage.Limits.maximumPlugins)-plugin safety limit."
            ReportPath = $null
        }
    }

    if (-not (Get-Command Get-GridXEditCollectorProvisioningState -ErrorAction SilentlyContinue)) { return [pscustomobject]@{ Status = 'Unavailable'; Reason = 'Grid xEdit collector provisioning state service is unavailable.'; ReportPath = $null } }
    $collectorState = Get-GridXEditCollectorProvisioningState -ConfigurationPath $Context.Mo2ConfigurationPath -ExecutableTitle $ExecutableTitle
    if ($collectorState.State -ne 'Ready') { return [pscustomobject]@{ Status = 'Unavailable'; Reason = "Grid xEdit collector state is '$($collectorState.State)'. $($collectorState.Detail) Provisioning requires a separate typed proposal and explicit authorization."; ReportPath = $null } }
    $installedScript = $collectorState.DestinationPath

    $reportPath = Join-Path $CaseDirectory 'xedit-evidence.tsv'
    $statusPath = Join-Path $CaseDirectory 'xedit-status.tsv'
    $launchPath = Join-Path $CaseDirectory 'mo2-xedit-launch.json'
    $loaderProgressPath = Join-Path $CaseDirectory 'xedit-loader-progress.tsv'
    $invocationArguments = @{
        ExecutableTitle = $ExecutableTitle; PluginsPath = $selection.PluginsPath; ScriptName = 'Trace-GridReference'
        QueryPath = $QueryPackage.TransportPath; OutputPath = $reportPath; StatusPath = $statusPath
        MaximumTraversalRecords = $QueryPackage.Limits.maximumTraversalRecords
        MaximumOutputRows = $QueryPackage.Limits.maximumOutputRows; MaximumOutputBytes = $QueryPackage.Limits.maximumOutputBytes
    }
    $invocation = New-GridMo2XEditInvocation @invocationArguments
    $xeditArguments = $invocation.XEditArguments
    $argumentString = $invocation.ArgumentString
    $env:GRID_HEALTH_OUTPUT = $reportPath
    $env:GRID_HEALTH_STATUS = $statusPath
    $env:GRID_HEALTH_QUERY = $QueryPackage.TransportPath
    $env:GRID_HEALTH_MAX_TRAVERSAL = [string]$QueryPackage.Limits.maximumTraversalRecords
    $env:GRID_HEALTH_MAX_ROWS = [string]$QueryPackage.Limits.maximumOutputRows
    $env:GRID_HEALTH_MAX_OUTPUT_BYTES = [string]$QueryPackage.Limits.maximumOutputBytes
    $launch = [ordered]@{
        StartedAt = (Get-Date).ToString('o'); Mo2Executable = $mo2Executable; WorkingDirectory = $Context.Mo2Root; Profile = $Context.Profile; ProfileWasChanged = $false
        ExecutableTitle = $ExecutableTitle; ConfiguredExecutableIndex = $definition.SourceIndex; XEditExecutable = $definition.Binary; InstalledCollector = $installedScript
        PluginSelectionPath = $selection.PluginsPath; PluginSelectionEvidence = $selection.EvidencePath; SelectedPlugins = @($selection.Plugins)
        XEditArguments = $xeditArguments; ArgumentString = $argumentString; ProcessId = $null; Mo2Exited = $false; Mo2ExitCode = $null
        SseEditObserved = $false; XEditProcessId = $null; XEditExitCode = $null; Outcome = 'Starting'; Exception = $null
        CollectorStatusPath = $statusPath; LastCollectorStage = $null; CapturedLogs = @()
        QueryPath = $QueryPackage.JsonPath; QueryTransportPath = $QueryPackage.TransportPath
        LoaderProgressPath = $loaderProgressPath; LoaderInference = 'Unknown'
        Mo2Ownership = $null; XEditOwnership = $null; ProcessLifecycle = $null
    }
    $loaderSamples = New-Object Collections.Generic.List[pscustomobject]
    function Write-GridLoaderInference {
        $launch.LoaderInference = Get-GridXEditLoaderInference -ScriptInvoked ([bool]$launch.LastCollectorStage) -Samples @($loaderSamples)
    }
    function Invoke-GridCloseOwnedProcessesForCase {
        if (-not $CloseOwnedProcesses) { return }
        $lifecycle = [ordered]@{}
        if ($launch.XEditOwnership) {
            $lifecycle.XEdit = Close-GridOwnedProcess -Ownership $launch.XEditOwnership -GraceSeconds $CloseGraceSeconds -Force:$ForceCloseOwnedProcesses
        }
        if ($launch.Mo2Ownership) {
            $lifecycle.Mo2 = Close-GridOwnedProcess -Ownership $launch.Mo2Ownership -GraceSeconds $CloseGraceSeconds -Force:$ForceCloseOwnedProcesses
        }
        $launch.ProcessLifecycle = $lifecycle
    }

    try {
        $prelaunchXEdit = @(Get-GridXEditRunningProcesses)
        if ($prelaunchXEdit.Count -gt 0) {
            $launch.Outcome = 'PreExistingProcess'
            $launch.Exception = "SSEEdit appeared before the MO2 launch boundary (PID(s): $($prelaunchXEdit.Id -join ', ')). Grid refused to launch or bind it."
            $launch | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $launchPath -Encoding UTF8
            return [pscustomobject]@{ Status = 'Unavailable'; Reason = "$($launch.Exception) Launch evidence: $launchPath"; ReportPath = $null }
        }
        $prelaunchIdentityKeys = @($prelaunchXEdit | ForEach-Object { $identity = Get-GridXEditProcessIdentity -Process $_; if ($identity) { [string]$identity.IdentityKey } })
        $process = Start-GridProcessWithSerializedArguments -FilePath $mo2Executable -ArgumentString $argumentString -WorkingDirectory $Context.Mo2Root
        $launch.ProcessId = $process.Id
        $mo2Ownership = @{ ProcessId = $process.Id; ExecutablePath = $mo2Executable; StartTime = $null; State = 'Owned' }
        try { $mo2Ownership.StartTime = $process.StartTime } catch { $mo2Ownership.StartTime = $null }
        $launch.Mo2Ownership = $mo2Ownership
        $startedAt = [datetime]$launch.StartedAt
        $launchBoundaryUtc = $startedAt.ToUniversalTime()
        try { $launchBoundaryUtc = $process.StartTime.ToUniversalTime() } catch { }
        $logRoots = @((Split-Path -Parent $definition.Binary), (Join-Path $Context.Mo2Root 'logs'))
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        $mo2ExitObservedAt = $null
        $xeditProcess = $null
        $xeditOwnership = $null
        $previousCpuTime = $null
        while ((Get-Date) -lt $deadline) {
            # Once this exact run has bound its xEdit process, the current
            # report is the terminal signal. Check it before refreshing
            # process ancestry or windows because either native operation can
            # block while a scripted xEdit process is disappearing.
            if (Test-GridXEditCurrentLaunchReport -ReportPath $reportPath -Ownership $xeditOwnership -LaunchBoundaryUtc $launchBoundaryUtc) {
                $launch.Outcome = 'Collected'; $launch.LoaderInference = 'Collected'; Invoke-GridCloseOwnedProcessesForCase; $launch | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $launchPath -Encoding UTF8
                return [pscustomobject]@{ Status = 'Collected'; Reason = $null; ReportPath = $reportPath; ProcessId = $process.Id }
            }
            $currentXEdit = $null
            $resolution = Resolve-GridXEditLaunchProcess -Candidates @(Get-GridXEditRunningProcesses) -ConfiguredExecutable $definition.Binary -LaunchBoundaryUtc $launchBoundaryUtc -ManagerProcessId $process.Id -PrelaunchIdentityKeys $prelaunchIdentityKeys
            if ($resolution.Status -eq 'Ambiguous') {
                $launch.Outcome = 'ProcessIdentityAmbiguous'
                $launch.Exception = "Multiple newly launched SSEEdit64 processes match the configured executable and MO2 ancestry (PID(s): $($resolution.ProcessIds -join ', ')). Grid refused to bind any of them."
                $launch | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $launchPath -Encoding UTF8
                return [pscustomobject]@{ Status = 'Unavailable'; Reason = "$($launch.Exception) Launch evidence: $launchPath"; ReportPath = $null }
            }
            if ($xeditOwnership -and $resolution.Status -eq 'Bound' -and [int]$resolution.Identity.ProcessId -ne [int]$xeditOwnership.ProcessId) {
                $launch.Outcome = 'ProcessIdentityAmbiguous'
                $launch.Exception = 'A second matching SSEEdit64 process appeared after Grid bound the current launch. Grid refused to rebind.'
                $launch | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $launchPath -Encoding UTF8
                return [pscustomobject]@{ Status = 'Unavailable'; Reason = "$($launch.Exception) Launch evidence: $launchPath"; ReportPath = $null }
            }
            if (-not $xeditOwnership -and $resolution.Status -eq 'Bound') {
                $currentXEdit = $resolution.Identity.Process
                $identity = $resolution.Identity
                $xeditOwnership = @{ ProcessId = $identity.ProcessId; ExecutablePath = $identity.ExecutablePath; StartTime = $identity.StartTimeUtc; State = 'Owned' }
                $launch.SseEditObserved = $true
                $launch.XEditProcessId = $identity.ProcessId
                $launch.XEditOwnership = $xeditOwnership
            }
            elseif ($xeditOwnership) {
                $currentXEdit = Get-Process -Id ([int]$xeditOwnership.ProcessId) -ErrorAction SilentlyContinue
                if ($currentXEdit -and -not (Test-GridXEditProcessOwnershipMatch -Ownership $xeditOwnership -Process $currentXEdit)) {
                    $launch.Outcome = 'ProcessOwnershipMismatch'
                    $launch.Exception = 'The bound SSEEdit64 PID, executable path, or start time no longer matches the current process.'
                    $launch | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $launchPath -Encoding UTF8
                    return [pscustomobject]@{ Status = 'Unavailable'; Reason = "$($launch.Exception) Launch evidence: $launchPath"; ReportPath = $null }
                }
            }
            # A scripted xEdit run can become headless while it is finishing
            # native auto-exit. Accept its current, ownership-bound report
            # before asking Windows for another window snapshot; querying a
            # disappearing window must never delay completed evidence.
            if (Test-GridXEditCurrentLaunchReport -ReportPath $reportPath -Ownership $xeditOwnership -LaunchBoundaryUtc $launchBoundaryUtc) {
                $launch.Outcome = 'Collected'; $launch.LoaderInference = 'Collected'; Invoke-GridCloseOwnedProcessesForCase; $launch | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $launchPath -Encoding UTF8
                return [pscustomobject]@{ Status = 'Collected'; Reason = $null; ReportPath = $reportPath; ProcessId = $process.Id }
            }
            if ($currentXEdit) {
                $xeditProcess = $currentXEdit
                try {
                    $sampleArgs = @{ XEditProcess = $currentXEdit; StartedAt = $startedAt }
                    if ($null -ne $previousCpuTime) { $sampleArgs.PreviousCpuTime = $previousCpuTime }
                    $sample = Get-GridXEditLoaderProgressSample @sampleArgs
                    Add-GridXEditLoaderProgressRow -StatusPath $loaderProgressPath -Sample $sample -IncludeHeader
                    $loaderSamples.Add($sample)
                    $previousCpuTime = $sample.CpuTime
                } catch { }
            }
            elseif ($xeditProcess) {
                if (Test-GridXEditCurrentLaunchReport -ReportPath $reportPath -Ownership $xeditOwnership -LaunchBoundaryUtc $launchBoundaryUtc) {
                    $launch.Outcome = 'Collected'; $launch.LoaderInference = 'Collected'; Invoke-GridCloseOwnedProcessesForCase; $launch | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $launchPath -Encoding UTF8
                    return [pscustomobject]@{ Status = 'Collected'; Reason = $null; ReportPath = $reportPath; ProcessId = $process.Id }
                }
                try { $xeditProcess.Refresh(); if ($xeditProcess.HasExited) { $launch.XEditExitCode = $xeditProcess.ExitCode } } catch {}
                $launch.CapturedLogs = @(Copy-GridXEditLaunchLogs -Roots $logRoots -CaseDirectory $CaseDirectory -StartedAt $startedAt)
                $launch.Outcome = Get-GridXEditFailureKind -LogPaths $launch.CapturedLogs -CollectorStage $launch.LastCollectorStage -Default 'XEditEarlyExit'
                $launch.LoaderInference = if ($launch.LastCollectorStage) { Get-GridXEditLoaderInference -ScriptInvoked $true -Samples @($loaderSamples) } else { 'ExitedBeforeScript' }
                Invoke-GridCloseOwnedProcessesForCase; $launch | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $launchPath -Encoding UTF8
                return [pscustomobject]@{ Status = 'Unavailable'; Reason = "SSEEdit64 exited before producing a report. Outcome: $($launch.Outcome). Launch evidence: $launchPath"; ReportPath = $null; ProcessId = $process.Id }
            }
            if (Test-GridXEditCurrentLaunchReport -ReportPath $reportPath -Ownership $xeditOwnership -LaunchBoundaryUtc $launchBoundaryUtc) {
                $launch.Outcome = 'Collected'; $launch.LoaderInference = 'Collected'; Invoke-GridCloseOwnedProcessesForCase; $launch | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $launchPath -Encoding UTF8
                return [pscustomobject]@{ Status = 'Collected'; Reason = $null; ReportPath = $reportPath; ProcessId = $process.Id }
            }
            if (Test-Path -LiteralPath $statusPath -PathType Leaf) { $lastStage = Get-Content -LiteralPath $statusPath -ErrorAction SilentlyContinue | Select-Object -Last 1; if ($lastStage) { $launch.LastCollectorStage = $lastStage } }
            $process.Refresh()
            if ($process.HasExited -and -not $launch.Mo2Exited) { $launch.Mo2Exited = $true; $launch.Mo2ExitCode = $process.ExitCode; $mo2ExitObservedAt = Get-Date }
            if ($launch.Mo2Exited -and -not $launch.SseEditObserved -and $mo2ExitObservedAt -and ((Get-Date) - $mo2ExitObservedAt).TotalSeconds -ge $ExitGraceSeconds) {
                $launch.CapturedLogs = @(Copy-GridXEditLaunchLogs -Roots $logRoots -CaseDirectory $CaseDirectory -StartedAt $startedAt)
                $launch.Outcome = Get-GridXEditFailureKind -LogPaths $launch.CapturedLogs -CollectorStage $launch.LastCollectorStage
                $launch.LoaderInference = 'ExitedBeforeScript'
                Invoke-GridCloseOwnedProcessesForCase; $launch | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $launchPath -Encoding UTF8
                return [pscustomobject]@{ Status = 'Unavailable'; Reason = "MO2 exited with code $($launch.Mo2ExitCode), SSEEdit64 did not appear, and no report was produced. Launch evidence: $launchPath"; ReportPath = $null; ProcessId = $process.Id }
            }
            Start-Sleep -Milliseconds 500
        }
        $launch.CapturedLogs = @(Copy-GridXEditLaunchLogs -Roots $logRoots -CaseDirectory $CaseDirectory -StartedAt $startedAt)
        $launch.Outcome = Get-GridXEditFailureKind -LogPaths $launch.CapturedLogs -CollectorStage $launch.LastCollectorStage -Default 'TimedOut'
        Write-GridLoaderInference; Invoke-GridCloseOwnedProcessesForCase; $launch | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $launchPath -Encoding UTF8
        return [pscustomobject]@{ Status = 'Unavailable'; Reason = "xEdit collection did not complete within $TimeoutSeconds seconds. Outcome: $($launch.Outcome). Loader inference: $($launch.LoaderInference). Grid did not terminate MO2 or SSEEdit64 unless -CloseOwnedProcesses was requested and ownership was verified. Launch evidence: $launchPath"; ReportPath = $null; ProcessId = $process.Id }
    } catch {
        $launch.Outcome = 'Exception'; $launch.Exception = $_.Exception.Message; Write-GridLoaderInference; Invoke-GridCloseOwnedProcessesForCase; $launch | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $launchPath -Encoding UTF8
        return [pscustomobject]@{ Status = 'Unavailable'; Reason = $_.Exception.Message; ReportPath = $null }
    } finally {
        Remove-Item Env:\GRID_HEALTH_OUTPUT -ErrorAction SilentlyContinue
        Remove-Item Env:\GRID_HEALTH_STATUS -ErrorAction SilentlyContinue
        Remove-Item Env:\GRID_HEALTH_QUERY -ErrorAction SilentlyContinue
        Remove-Item Env:\GRID_HEALTH_MAX_TRAVERSAL -ErrorAction SilentlyContinue
        Remove-Item Env:\GRID_HEALTH_MAX_ROWS -ErrorAction SilentlyContinue
        Remove-Item Env:\GRID_HEALTH_MAX_OUTPUT_BYTES -ErrorAction SilentlyContinue
    }
}

