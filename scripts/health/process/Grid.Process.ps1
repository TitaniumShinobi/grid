<# .SYNOPSIS Provides game-independent, read-only process lifecycle observations. #>
function Get-GridProcessSnapshot {
    [CmdletBinding()]
    param([Parameter(Mandatory)][ValidateRange(1, 2147483647)][int]$ProcessId)
    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $process) { return [pscustomobject]@{ ProcessId = $ProcessId; Exists = $false; Name = $null; StartTimeUtc = $null; Responding = $null; ExitCode = $null } }
    $start = $null
    try { $start = $process.StartTime.ToUniversalTime().ToString('o') } catch { }
    $path = $null
    try { $path = $process.Path } catch { }
    [pscustomobject]@{ ProcessId = $ProcessId; Exists = $true; Name = $process.ProcessName; ExecutablePath = $path; StartTimeUtc = $start; Responding = try { [bool]$process.Responding } catch { $null }; ExitCode = if ($process.HasExited) { try { $process.ExitCode } catch { $null } } else { $null } }
}

function Test-GridProcessOwnership {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$RecordedProcess, [Parameter(Mandatory)]$CurrentProcess)
    if (-not $CurrentProcess.Exists) { return $false }
    if ([int]$RecordedProcess.ProcessId -ne [int]$CurrentProcess.ProcessId) { return $false }
    if ([string]$RecordedProcess.Name -ne [string]$CurrentProcess.Name) { return $false }
    if ($RecordedProcess.PSObject.Properties['ExecutablePath'] -and $RecordedProcess.ExecutablePath -and $CurrentProcess.PSObject.Properties['ExecutablePath'] -and $CurrentProcess.ExecutablePath -and ([IO.Path]::GetFullPath([string]$RecordedProcess.ExecutablePath) -ine [IO.Path]::GetFullPath([string]$CurrentProcess.ExecutablePath))) { return $false }
    if ($RecordedProcess.StartTimeUtc -and $CurrentProcess.StartTimeUtc -and [string]$RecordedProcess.StartTimeUtc -ne [string]$CurrentProcess.StartTimeUtc) { return $false }
    return $true
}

function Get-GridProcessWindowSnapshot {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)
    Add-Type -AssemblyName UIAutomationClient -ErrorAction Stop
    Add-Type -AssemblyName UIAutomationTypes -ErrorAction Stop
    $windows = New-Object Collections.Generic.List[pscustomobject]
    $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $elements = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $condition)
    foreach ($element in $elements) {
        try {
            $windows.Add([pscustomobject]@{ Title = $element.Current.Name; ClassName = $element.Current.ClassName; IsEnabled = $element.Current.IsEnabled; IsOffscreen = $element.Current.IsOffscreen })
        } catch { continue }
    }
    @($windows)
}

function Get-GridProcessById {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)
    Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
}

function Invoke-GridCloseMainWindow {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Process)
    [bool]$Process.CloseMainWindow()
}

function Invoke-GridForceStopProcess {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)
    Stop-Process -Id $ProcessId -Force -ErrorAction Stop
}

function Test-GridProcessStillRunning {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)
    [bool](Get-GridProcessById -ProcessId $ProcessId)
}

function Test-GridProcessOwnershipMatch {
    [CmdletBinding()]
    param([Parameter(Mandatory)][hashtable]$Ownership, [Parameter(Mandatory)]$Current)
    $pathMatches = $false
    try { $pathMatches = ([IO.Path]::GetFullPath([string]$Current.Path)) -ieq ([IO.Path]::GetFullPath([string]$Ownership.ExecutablePath)) } catch { $pathMatches = $false }
    $startMatches = $false
    try { $startMatches = ($Current.StartTime.ToUniversalTime() -eq ([datetime]$Ownership.StartTime).ToUniversalTime()) } catch { $startMatches = $false }
    $pathMatches -and $startMatches
}

function Close-GridOwnedProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][hashtable]$Ownership,
        [ValidateRange(0, 300)][int]$GraceSeconds = 15,
        [switch]$Force
    )
    if ($Ownership.State -ne 'Owned') { return [pscustomobject]@{ Result = 'Refused'; Reason = 'This process is not recorded as Grid-owned; Grid never closes a pre-existing process.' } }
    foreach ($field in @('ProcessId','ExecutablePath','StartTime')) {
        if (-not $Ownership.ContainsKey($field) -or $null -eq $Ownership[$field]) { return [pscustomobject]@{ Result = 'Refused'; Reason = "Grid-owned process evidence is missing '$field'." } }
    }
    $current = Get-GridProcessById -ProcessId ([int]$Ownership.ProcessId)
    if (-not $current) { return [pscustomobject]@{ Result = 'AlreadyExited'; Reason = 'The Grid-owned process had already exited.' } }
    if (-not (Test-GridProcessOwnershipMatch -Ownership $Ownership -Current $current)) { return [pscustomobject]@{ Result = 'OwnershipMismatch'; Reason = 'The process ID, executable path, or start time no longer matches. Grid refuses to close it.' } }
    $windows = @()
    try { $windows = @(Get-GridProcessWindowSnapshot -ProcessId ([int]$Ownership.ProcessId)) } catch { $windows = @() }
    $mainTitle = $null
    try { $mainTitle = $current.MainWindowTitle } catch { }
    if (@($windows | Where-Object { $_.Title -ne $mainTitle -and -not $_.IsOffscreen }).Count -gt 0) { return [pscustomobject]@{ Result = 'ModalDialogDetected'; Reason = 'A modal dialog or unsaved-change prompt was detected; Grid refuses to close the process.' } }
    $posted = $false
    try { $posted = Invoke-GridCloseMainWindow -Process $current } catch { }
    if (-not $posted) { return [pscustomobject]@{ Result = 'NormalCloseFailed'; Reason = 'CloseMainWindow could not post a close request.' } }
    $deadline = [datetime]::UtcNow.AddSeconds($GraceSeconds)
    do {
        if (-not (Test-GridProcessStillRunning -ProcessId ([int]$Ownership.ProcessId))) { return [pscustomobject]@{ Result = 'NormalCloseSucceeded'; Reason = $null } }
        Start-Sleep -Milliseconds 250
    } while ([datetime]::UtcNow -lt $deadline)
    if (-not $Force) { return [pscustomobject]@{ Result = 'NormalCloseFailed'; Reason = "The process remained after the $GraceSeconds-second grace period; force close was not explicitly requested." } }
    # Revalidate immediately before the irreversible force operation.
    $current = Get-GridProcessById -ProcessId ([int]$Ownership.ProcessId)
    if (-not $current) { return [pscustomobject]@{ Result = 'AlreadyExited'; Reason = 'The process exited before force close.' } }
    if (-not (Test-GridProcessOwnershipMatch -Ownership $Ownership -Current $current)) { return [pscustomobject]@{ Result = 'OwnershipMismatch'; Reason = 'Ownership changed before force close; Grid refused.' } }
    try {
        Invoke-GridForceStopProcess -ProcessId ([int]$Ownership.ProcessId)
        Start-Sleep -Milliseconds 250
        if (-not (Test-GridProcessStillRunning -ProcessId ([int]$Ownership.ProcessId))) { return [pscustomobject]@{ Result = 'ForceClosed'; Reason = 'Force termination occurred only after explicit -Force and repeated ownership validation.' } }
        [pscustomobject]@{ Result = 'ForceCloseFailed'; Reason = 'The process remained after force termination.' }
    } catch { [pscustomobject]@{ Result = 'ForceCloseFailed'; Reason = $_.Exception.Message } }
}

function Wait-GridProcessObservation {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId, [ValidateRange(1, 86400)][int]$TimeoutSeconds = 30, [ValidateRange(50, 5000)][int]$PollMilliseconds = 250)
    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $snapshot = Get-GridProcessSnapshot -ProcessId $ProcessId
        if (-not $snapshot.Exists) { return [pscustomobject]@{ Status = 'Exited'; Snapshot = $snapshot } }
        Start-Sleep -Milliseconds $PollMilliseconds
    } while ([datetime]::UtcNow -lt $deadline)
    [pscustomobject]@{ Status = 'TimedOut'; Snapshot = (Get-GridProcessSnapshot -ProcessId $ProcessId) }
}
