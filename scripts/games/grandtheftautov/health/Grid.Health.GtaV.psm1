#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent $PSScriptRoot
$script:GridGtaScriptsRoot = Split-Path -Parent (Split-Path -Parent $gameRoot)
$sharedHealthRoot = Join-Path $script:GridGtaScriptsRoot 'health'
. (Join-Path $sharedHealthRoot 'Grid.GameUserDataRoots.ps1')
. (Join-Path $sharedHealthRoot 'Grid.Evidence.ps1')
. (Join-Path $sharedHealthRoot 'Grid.DiagnosticResult.ps1')
. (Join-Path $sharedHealthRoot 'Grid.DiagnosticAssessment.ps1')
. (Join-Path $sharedHealthRoot 'Grid.WindowsEvidence.ps1')
. (Join-Path $sharedHealthRoot 'collectors\Get-GridWindowsApplicationFailureEvidence.ps1')
. (Join-Path $sharedHealthRoot 'collectors\Get-GridWindowsErrorReportingEvidence.ps1')
. (Join-Path $sharedHealthRoot 'collectors\Get-GridWindowsProcessModuleEvidence.ps1')
$collectorRoot = Join-Path $PSScriptRoot 'collectors'
. (Join-Path $collectorRoot 'Test-GridGtaConnectedContext.ps1')
. (Join-Path $collectorRoot 'Get-GridGtaInstallationInventory.ps1')
. (Join-Path $collectorRoot 'Get-GridGtaInstallationSet.ps1')
. (Join-Path $collectorRoot 'Resolve-GridGtaSetupReadiness.ps1')
. (Join-Path $collectorRoot 'Resolve-GridGtaRequestContext.ps1')
. (Join-Path $collectorRoot 'Get-GridGtaCrashInvestigationEvidence.ps1')
. (Join-Path $collectorRoot 'Resolve-GridGtaCrashAssessment.ps1')
$setupRoot = Join-Path $gameRoot 'setup'
. (Join-Path $setupRoot 'New-GridGtaDeploymentPlan.ps1')
. (Join-Path $setupRoot 'Invoke-GridAuthorizedGtaDeployment.ps1')

function Invoke-GridGtaBaseline {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$GameRoot,
        [string]$InstallationId,
        [ValidateSet('Foundation','ForeverTogether')][string]$Target = 'ForeverTogether',
        [switch]$BattleEyeDisabled,
        [switch]$MenyooInGameVerified,
        $InvestigationPlan
    )
    $installationSet = Get-GridGtaInstallationSet -CandidateRoots $GameRoot -SelectedInstallationId $InstallationId
    if ($installationSet.status -ne 'Ready') {
        return [pscustomobject]@{
            Tool = 'Grid.GtaV.Baseline'; Status = $installationSet.status; InstallationSet = $installationSet
            Inventory = $null; Readiness = $null; ChangedExternalState = $false
        }
    }
    $inventory = $installationSet.selectedInstallation
    $readiness = Resolve-GridGtaSetupReadiness -Inventory $inventory -Target $Target -BattleEyeDisabled:$BattleEyeDisabled -MenyooInGameVerified:$MenyooInGameVerified
    [pscustomobject]@{ Tool = 'Grid.GtaV.Baseline'; Status = $readiness.status; InstallationSet = $installationSet; Inventory = $inventory; Readiness = $readiness; ChangedExternalState = $false }
}

function Invoke-GridGtaHealthAdapter {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Request,
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][string]$CaseDirectory,
        [string[]]$GameRoot,
        [string]$InstallationId,
        $InvestigationPlan,
        [ValidateSet('Foundation','ForeverTogether')][string]$Target = 'ForeverTogether',
        [switch]$BattleEyeDisabled,
        [switch]$MenyooInGameVerified
    )
    if (@($GameRoot | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }).Count -eq 0) {
        return [pscustomobject]@{ Tool = 'Grid.Health.GtaV'; CaseId = $CaseId; Status = 'NeedsContext'; CaseDirectory = $CaseDirectory; CollectorReason = 'An explicit connected GTA V installation root is required.' }
    }
    $baseline = Invoke-GridGtaBaseline -GameRoot $GameRoot -InstallationId $InstallationId -Target $Target -BattleEyeDisabled:$BattleEyeDisabled -MenyooInGameVerified:$MenyooInGameVerified
    $adapterStatus = if ($baseline.Status -eq 'Ready') { 'ReadyToCollect' } elseif ($baseline.Status -in @('Blocked','SelectionNotFound','NoInstallations')) { 'Unavailable' } else { 'NeedsContext' }
    [pscustomobject]@{
        Tool = 'Grid.Health.GtaV'; CaseId = $CaseId; Status = $adapterStatus; CaseDirectory = $CaseDirectory
        CollectorReason = if ($baseline.Status -eq 'NeedsSelection') { 'More than one GTA V installation is connected; select one exact InstallationId.' } elseif ($baseline.Readiness -and $baseline.Readiness.nextAction) { [string]$baseline.Readiness.nextAction.instruction } else { 'The selected setup target is ready.' }
        InstallationSet = $baseline.InstallationSet; Inventory = $baseline.Inventory; SetupReadiness = $baseline.Readiness; Evidence = @(); ChangedExternalState = $false
    }
}

function Invoke-GridGtaCrashInvestigation {
    <#
    .SYNOPSIS
    Runs the authorized read-only GTA crash evidence callback.
    .DESCRIPTION
    The shared request transaction owns case persistence and receipts. This
    adapter returns evidence and an unresolved deterministic assessment; it
    never launches the game, writes a case, or performs remediation.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Request,
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][string]$CaseDirectory,
        [Parameter(Mandatory)]$ResolvedContext,
        [datetime]$SinceUtc = ([datetime]::UtcNow.AddDays(-120)),
        [hashtable]$KnownFolders,
        $ApplicationFailureEvidence,
        $WindowsErrorReportingEvidence,
        $ProcessModuleEvidence
    )
    if ([string]$ResolvedContext.status -cne 'Resolved') {
        return [pscustomobject][ordered]@{
            Tool = 'Grid.Health.GtaV'; Status = 'NeedsContext'; TerminalState = 'NeedsContext'; CaseId = $CaseId
            CaseDirectory = $CaseDirectory; Evidence = @(); DiagnosticResult = $null
            Detail = 'The authorized callback did not receive a resolved account-connected GTA context.'; ChangedExternalState = $false
        }
    }
    $package = Get-GridGtaCrashInvestigationEvidence -Context $ResolvedContext -KnownFolders $KnownFolders -SinceUtc $SinceUtc `
        -ApplicationFailureEvidence $ApplicationFailureEvidence -WindowsErrorReportingEvidence $WindowsErrorReportingEvidence `
        -ProcessModuleEvidence $ProcessModuleEvidence
    $assessment = Resolve-GridGtaCrashAssessment -EvidencePackage $package -CaseId $CaseId
    [pscustomobject][ordered]@{
        Tool = 'Grid.Health.GtaV'; Status = 'Completed'; TerminalState = 'EvidencePartial'; CaseId = $CaseId
        CaseDirectory = $CaseDirectory; Evidence = @($package.evidence); EvidencePackage = $package
        Assessment = $assessment; DiagnosticResult = $assessment.diagnosticResult
        Detail = $assessment.nextObservation; ChangedExternalState = $false
    }
}

Export-ModuleMember -Function Test-GridGtaConnectedContext, Get-GridGtaInstallationId, Get-GridGtaInstallationInventory, Get-GridGtaInstallationSet, Resolve-GridGtaSetupReadiness, Resolve-GridGtaRequestContext, Get-GridGtaCrashInvestigationEvidence, Resolve-GridGtaCrashAssessment, Invoke-GridGtaCrashInvestigation, Invoke-GridGtaBaseline, Invoke-GridGtaHealthAdapter, New-GridGtaDeploymentPlan, Invoke-GridAuthorizedGtaDeployment
