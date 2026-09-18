<# .SYNOPSIS Records xEdit-specific loader telemetry using shared Grid process/window observation. #>

function Get-GridXEditLoaderProgressSample {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][Diagnostics.Process]$XEditProcess,
        [Parameter(Mandatory)][datetime]$StartedAt,
        [TimeSpan]$PreviousCpuTime
    )
    $XEditProcess.Refresh()
    $cpuTime = $XEditProcess.TotalProcessorTime
    $mainTitle = $null
    try { $mainTitle = $XEditProcess.MainWindowTitle } catch { $mainTitle = $null }
    $responding = $true
    try { $responding = $XEditProcess.Responding } catch { $responding = $true }

    [pscustomobject]@{
        Timestamp = (Get-Date).ToString('o')
        ElapsedSeconds = [math]::Round(((Get-Date) - $StartedAt).TotalSeconds, 1)
        ProcessId = $XEditProcess.Id
        CpuSeconds = [math]::Round($cpuTime.TotalSeconds, 2)
        CpuDeltaSeconds = if ($PSBoundParameters.ContainsKey('PreviousCpuTime')) { [math]::Round(($cpuTime - $PreviousCpuTime).TotalSeconds, 2) } else { $null }
        WorkingSetBytes = $XEditProcess.WorkingSet64
        Responding = $responding
        # Native child-window enumeration can block while xEdit creates or destroys
        # its custom-drawn loader surface. Keep this hot-loop sample bounded to the
        # process' directly exposed main-window state. The custom Module Selection
        # overlay is not a separately observable Win32 window in xEdit 4.1.5f.
        WindowCount = if ([string]::IsNullOrWhiteSpace($mainTitle)) { 0 } else { 1 }
        WindowTitles = [string]$mainTitle
        ModalDialogDetected = $false
        CpuTime = $cpuTime
    }
}

function Add-GridXEditLoaderProgressRow {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$StatusPath, [Parameter(Mandatory)][pscustomobject]$Sample, [switch]$IncludeHeader)
    $line = [string]::Join("`t", @(
        $Sample.Timestamp, $Sample.ElapsedSeconds, $Sample.ProcessId, $Sample.CpuSeconds, $Sample.CpuDeltaSeconds,
        $Sample.WorkingSetBytes, $Sample.Responding, $Sample.WindowCount, $Sample.WindowTitles, $Sample.ModalDialogDetected
    ))
    if ($IncludeHeader -and -not (Test-Path -LiteralPath $StatusPath -PathType Leaf)) {
        Set-Content -LiteralPath $StatusPath -Value "Timestamp`tElapsedSeconds`tProcessId`tCpuSeconds`tCpuDeltaSeconds`tWorkingSetBytes`tResponding`tWindowCount`tWindowTitles`tModalDialogDetected" -Encoding UTF8
    }
    Add-Content -LiteralPath $StatusPath -Value $line -Encoding UTF8
}

function Get-GridXEditLoaderInference {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][bool]$ScriptInvoked,
        [pscustomobject[]]$Samples = @()
    )
    # Terminal states (Collected / ExitedBeforeScript) are assigned directly by the caller, not by this heuristic.
    if ($ScriptInvoked) { return 'ScriptInvoked' }
    $recent = @($Samples | Select-Object -Last 6)
    if ($recent.Count -eq 0) { return 'Unknown' }
    if (@($recent | Where-Object { $_.ModalDialogDetected }).Count -gt 0) { return 'ModalDialogBlocking' }
    $busyDeltas = @($recent | Where-Object { $null -ne $_.CpuDeltaSeconds } | ForEach-Object { $_.CpuDeltaSeconds })
    if ($busyDeltas.Count -gt 0 -and (($busyDeltas | Measure-Object -Sum).Sum) -gt 0.05) { return 'StillLoading' }
    return 'PossiblyHung'
}
