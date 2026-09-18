#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent $PSScriptRoot
$collectorRoot = Join-Path $PSScriptRoot 'collectors'
. (Join-Path $collectorRoot 'Test-GridGtaConnectedContext.ps1')
. (Join-Path $collectorRoot 'Get-GridGtaInstallationInventory.ps1')
. (Join-Path $collectorRoot 'Get-GridGtaInstallationSet.ps1')
. (Join-Path $collectorRoot 'Resolve-GridGtaSetupReadiness.ps1')
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

Export-ModuleMember -Function Test-GridGtaConnectedContext, Get-GridGtaInstallationId, Get-GridGtaInstallationInventory, Get-GridGtaInstallationSet, Resolve-GridGtaSetupReadiness, Invoke-GridGtaBaseline, Invoke-GridGtaHealthAdapter, New-GridGtaDeploymentPlan, Invoke-GridAuthorizedGtaDeployment
