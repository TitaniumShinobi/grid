#requires -Version 5.1
<# .SYNOPSIS Reports registered Class coverage and optionally runs one whole-profile CleanHouse baseline. #>
[CmdletBinding()]
param(
    [string]$Game = 'skyrimspecialedition',
    [string]$InstallationId,
    [string]$ProfileId,
    [string[]]$ModNames = @(),
    [string[]]$ToolIds = @(),
    [string]$CaseStoreRoot,
    [string]$FinalizationCaseId,
    [switch]$Execute,
    [switch]$PassThru
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Grid.Health.psm1') -Force -DisableNameChecking
$scriptsRoot = Split-Path -Parent $PSScriptRoot
$coverage = @(Get-GridRegisteredCoverage -ScriptsRoot $scriptsRoot)
$registeredCoverage = @($coverage | Where-Object coverage -eq 'Registered')
$baselineCapabilityId = 'grid.game.skyrimspecialedition.baseline.collect'
$primaryClassId = 'grid.class.installation-integrity'
$primaryCoverage = @($registeredCoverage | Where-Object { [string]$_.classId -ceq $primaryClassId })
if ($primaryCoverage.Count -ne 1 -or $baselineCapabilityId -notin @($primaryCoverage[0].rootCapabilityIds)) {
    throw "CoverageExecutionPlanInvalid: '$primaryClassId' must be the sole whole-profile baseline entry point."
}
$sharedBaselineCoverage = @($registeredCoverage | Where-Object { $baselineCapabilityId -in @($_.rootCapabilityIds) } | Sort-Object classId)
$deferredTargetCoverage = @($registeredCoverage | Where-Object { $baselineCapabilityId -notin @($_.rootCapabilityIds) } | Sort-Object classId)
$executionPlan = [pscustomobject][ordered]@{
    schemaVersion = 1
    mode = 'SingleWholeProfileBaseline'
    primaryClassId = $primaryClassId
    plannedRunCount = 1
    coveredClassIds = @($sharedBaselineCoverage | ForEach-Object classId)
    deferredClassIds = @($deferredTargetCoverage | ForEach-Object classId)
    deferredReason = 'These registered collectors require exact provider, FormID, EditorID, or tool evidence and cannot be inferred by a whole-profile run.'
}
$runs = @()
if ($Execute) {
    if ($ToolIds.Count -gt 0) {
        throw 'CoverageExecutionScopeInvalid: the one-pass coverage command does not accept tool selections; tool reads require their own displayed authorization review.'
    }
    if ([string]::IsNullOrWhiteSpace($CaseStoreRoot)) { $CaseStoreRoot = Get-GridDiagnosticStoreRoot }
    $resolvedContext = Resolve-GridPersistedMo2BaselineContext -DiagnosticStoreRoot $CaseStoreRoot `
        -InstallationId $InstallationId -ProfileId $ProfileId
    $arguments = @{
        Game = $Game; InstallationId = [string]$resolvedContext.installationId; ProfileId = [string]$resolvedContext.profileId; ClassId = $primaryClassId
        ModNames = $ModNames; Request = 'Run one CleanHouse Repair baseline for the complete selected profile.'
        Execute = $true; PassThru = $true
    }
    $arguments.CaseStoreRoot = $CaseStoreRoot
    if (-not [string]::IsNullOrWhiteSpace($FinalizationCaseId)) { $arguments.FinalizationCaseId = $FinalizationCaseId }
    $runs = @(& (Join-Path $PSScriptRoot 'Invoke-GridClassRequest.ps1') @arguments)
}
$failedRuns = @($runs | Where-Object { [string]$_.Status -in @('Failed','BaselineFailed') -or $_.Executed -ne $true })
$result = [pscustomobject][ordered]@{
    Tool = 'Invoke-GridRegisteredCoverage'; Status = if (-not $Execute) { 'Preview' } elseif ($failedRuns.Count -gt 0) { 'Incomplete' } else { 'Completed' }
    RegisteredCount = $registeredCoverage.Count
    UnsupportedCount = @($coverage | Where-Object coverage -eq 'Unsupported').Count
    Coverage = $coverage; ExecutionPlan = $executionPlan; Executed = [bool]$Execute -and $runs.Count -gt 0; Runs = @($runs)
}
if ($PassThru) { $result } else { $result | ConvertTo-Json -Depth 50 }
