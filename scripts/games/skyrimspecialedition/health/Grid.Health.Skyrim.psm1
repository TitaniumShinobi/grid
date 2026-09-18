Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent $PSScriptRoot
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $gameRoot)
Import-Module (Join-Path $scriptsRoot 'health\Grid.Health.psm1') -ErrorAction Stop
$adapterManifest = Import-PowerShellDataFile -LiteralPath (Join-Path $PSScriptRoot 'adapter.psd1')

$collectorRoot = Join-Path $PSScriptRoot 'collectors'
. (Join-Path $gameRoot 'mo2\Grid.EslFlag.ps1')
. (Join-Path $collectorRoot 'Invoke-GridSkyrimBaseline.ps1')
. (Join-Path $collectorRoot 'Invoke-GridSkyrimRootCauseCollector.ps1')
. (Join-Path $collectorRoot 'Get-GridMO2Context.ps1')
. (Join-Path $collectorRoot 'Resolve-GridSkyrimRequestToolInputs.ps1')
. (Join-Path $collectorRoot 'New-GridXEditPluginSelection.ps1')
. (Join-Path $collectorRoot 'New-GridXEditQuery.ps1')
. (Join-Path $collectorRoot 'Get-GridXEditEvidence.ps1')
. (Join-Path $collectorRoot 'Resolve-GridSkyrimEslEligibility.ps1')
. (Join-Path $collectorRoot 'Resolve-GridSkyrimEslEligibilityBatch.ps1')
. (Join-Path $collectorRoot 'Resolve-GridSkyrimPlacedActorReference.ps1')
. (Join-Path $collectorRoot 'Get-GridSkyrimScriptedReferenceEvidence.ps1')
. (Join-Path $collectorRoot 'Get-GridSkyrimPapyrusStateEvidence.ps1')
. (Join-Path $collectorRoot 'Get-GridSkyrimSaveCreatedActorEvidence.ps1')
. (Join-Path $collectorRoot 'Get-GridXEditLoaderProgress.ps1')
. (Join-Path $collectorRoot 'Start-GridXEditProbe.ps1')
. (Join-Path $collectorRoot 'Get-GridLootExistingOutput.ps1')
. (Join-Path $collectorRoot 'Get-GridSkyrimExistingToolReport.ps1')
. (Join-Path $collectorRoot 'Get-GridSkyrimGameplayCapabilityAssessment.ps1')
. (Join-Path $collectorRoot 'Get-GridSkyrimNexusCapabilityObservation.ps1')
. (Join-Path $PSScriptRoot 'actions\Get-GridXEditCollectorProvisioningState.ps1')
. (Join-Path $PSScriptRoot 'actions\New-GridXEditCollectorProvisioningProposal.ps1')
. (Join-Path $PSScriptRoot 'actions\Invoke-GridAuthorizedXEditCollectorProvisioning.ps1')
. (Join-Path $PSScriptRoot 'actions\Get-GridXEditReferenceSuppressionWriterProvisioningState.ps1')
. (Join-Path $PSScriptRoot 'actions\New-GridXEditReferenceSuppressionWriterProvisioningProposal.ps1')
. (Join-Path $PSScriptRoot 'actions\Invoke-GridAuthorizedXEditReferenceSuppressionWriterProvisioning.ps1')
. (Join-Path $PSScriptRoot 'actions\New-GridSkyrimRemediationProposal.ps1')
. (Join-Path $PSScriptRoot 'actions\Resolve-GridSkyrimBaselineIntegrityDiagnosis.ps1')
. (Join-Path $PSScriptRoot 'actions\New-GridSkyrimComponentRecoveryPlan.ps1')
. (Join-Path $PSScriptRoot 'actions\Get-GridSkyrimModChainRepairInspection.ps1')
. (Join-Path $PSScriptRoot 'actions\Invoke-GridSkyrimArtifactAcquisition.ps1')
. (Join-Path $PSScriptRoot 'actions\New-GridSkyrimModChainRepairProposal.ps1')
. (Join-Path $PSScriptRoot 'actions\Invoke-GridAuthorizedModChainRepair.ps1')
. (Join-Path $PSScriptRoot 'actions\Invoke-GridAuthorizedModChainHistory.ps1')
. (Join-Path $PSScriptRoot 'actions\New-GridSkyrimPluginStateBatchProposal.ps1')
. (Join-Path $PSScriptRoot 'actions\New-GridSkyrimEslFlagProposal.ps1')
. (Join-Path $PSScriptRoot 'actions\Invoke-GridAuthorizedEslFlag.ps1')
. (Join-Path $PSScriptRoot 'actions\New-GridSkyrimEslFlagPlanningCase.ps1')
. (Join-Path $PSScriptRoot 'actions\Invoke-GridAuthorizedEslFlagHistory.ps1')
. (Join-Path $PSScriptRoot 'actions\New-GridSkyrimPluginStateBatchPlanningCase.ps1')
. (Join-Path $PSScriptRoot 'actions\Grid.PluginStateBatch.Commands.ps1')
. (Join-Path $PSScriptRoot 'actions\Invoke-GridAuthorizedPluginStateBatchHistory.ps1')
. (Join-Path $PSScriptRoot 'actions\Resolve-GridSkyrimCertificationEligibility.ps1')
. (Join-Path $PSScriptRoot 'actions\Invoke-GridSkyrimRuntimeCertification.ps1')
. (Join-Path $PSScriptRoot 'actions\Invoke-GridSkyrimRuntimeCertificationSession.ps1')
. (Join-Path $PSScriptRoot 'actions\New-GridSkyrimConflictPatchSpecification.ps1')
. (Join-Path $PSScriptRoot 'actions\New-GridSkyrimReferenceSuppressionProposal.ps1')
. (Join-Path $PSScriptRoot 'actions\New-GridSkyrimReferenceSuppressionPlanningCase.ps1')
. (Join-Path $PSScriptRoot 'actions\Invoke-GridAuthorizedReferenceSuppression.ps1')
. (Join-Path $PSScriptRoot 'actions\Invoke-GridAuthorizedReferenceSuppressionHistory.ps1')
. (Join-Path $PSScriptRoot 'actions\Test-GridSkyrimRootCauseContracts.ps1')
. (Join-Path $PSScriptRoot 'actions\Resolve-GridSkyrimRootCause.ps1')
. (Join-Path $PSScriptRoot 'Invoke-GridSkyrimRootCauseDiagnosis.ps1')
. (Join-Path $PSScriptRoot 'Invoke-GridSkyrimRecordRelationshipInvestigation.ps1')
. (Join-Path $PSScriptRoot 'New-GridSkyrimRecordRelationshipRequest.ps1')
. (Join-Path $PSScriptRoot 'Resolve-GridSkyrimDiagnosticResult.ps1')

function Invoke-GridSkyrimHealthAdapter {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Request,
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][string]$CaseDirectory,
        [string]$Mo2Root,
        [string]$Profile,
        $InvestigationPlan,
        $RemediationProposal,
        [object[]]$VerificationResults = @(),
        [switch]$SkipXEdit,
        [ValidateRange(1, 3600)][int]$XEditTimeoutSeconds = 600,
        [switch]$CloseOwnedProcesses,
        [int]$CloseGraceSeconds = 15,
        [switch]$ForceCloseOwnedProcesses
    )

    if ($InvestigationPlan -and $InvestigationPlan.PSObject.Properties['schemaVersion'] -and [int]$InvestigationPlan.schemaVersion -eq 2) {
        $invocationBinding = @($InvestigationPlan.capabilities | Where-Object {
            [string]$_.capabilityId -ieq [string]$adapterManifest.InvocationCapabilityId
        })
        if ($invocationBinding.Count -ne 1) {
            return [pscustomobject]@{
                Tool = 'Grid.Health.Skyrim'; CaseId = $CaseId; Status = 'Failed'; CaseDirectory = $CaseDirectory
                Detail = "InvestigationPlan v2 does not bind the adapter invocation capability '$($adapterManifest.InvocationCapabilityId)'."
            }
        }
        $registry = @(Get-GridCapabilityRegistry -ScriptsRoot $scriptsRoot)
        $contract = Resolve-GridCapabilityContract -Registry $registry -CapabilityId ([string]$adapterManifest.InvocationCapabilityId)
        if ([string]$invocationBinding[0].capabilityVersion -ne [string]$contract.capabilityVersion) {
            return [pscustomobject]@{
                Tool = 'Grid.Health.Skyrim'; CaseId = $CaseId; Status = 'Failed'; CaseDirectory = $CaseDirectory
                Detail = "InvestigationPlan capability version is stale for '$($adapterManifest.InvocationCapabilityId)'."
            }
        }
    }

    if (-not $InvestigationPlan -or [string]$InvestigationPlan.status -notin @('ReadyToCollect', 'Ready')) {
        return [pscustomobject]@{
            Tool = 'Grid.Health.Skyrim'; CaseId = $CaseId; Status = 'NeedsEvidence'; CaseDirectory = $CaseDirectory
            Detail = 'No ready InvestigationPlan was supplied. Explicit candidate plugin identities and a FormID or EditorID are required before xEdit collection.'
        }
    }
    if ([string]::IsNullOrWhiteSpace($Mo2Root) -or [string]::IsNullOrWhiteSpace($Profile)) {
        return [pscustomobject]@{ Tool = 'Grid.Health.Skyrim'; CaseId = $CaseId; Status = 'NeedsContext'; CaseDirectory = $CaseDirectory; Detail = 'An explicit or connected MO2 root and profile are required.' }
    }

    $targets = @($InvestigationPlan.candidatePlugins | ForEach-Object { [string]$_.name } | Where-Object { $_ } | Select-Object -Unique)
    if ($targets.Count -eq 0) {
        return [pscustomobject]@{ Tool = 'Grid.Health.Skyrim'; CaseId = $CaseId; Status = 'NeedsEvidence'; CaseDirectory = $CaseDirectory; Detail = 'The InvestigationPlan contains no explicit candidate plugin identities.' }
    }

    $queries = @($InvestigationPlan.collectorQueries)
    if ($queries.Count -eq 0) {
        $queries = @($InvestigationPlan.observedForms | ForEach-Object {
            if ($_.formId) { [pscustomobject]@{ operation = 'FindRecordByFormId'; plugin = $targets[0]; formId = [string]$_.formId; editorId = '' } }
            elseif ($_.editorId) { [pscustomobject]@{ operation = 'FindRecordByEditorId'; plugin = $targets[0]; formId = ''; editorId = [string]$_.editorId } }
        })
    }
    if ($queries.Count -eq 0) {
        return [pscustomobject]@{ Tool = 'Grid.Health.Skyrim'; CaseId = $CaseId; Status = 'NeedsEvidence'; CaseDirectory = $CaseDirectory; Detail = 'The InvestigationPlan contains no explicit FormID, EditorID, or collector query.' }
    }

    $context = Get-GridMO2Context -Mo2Root $Mo2Root -Profile $Profile -CaseDirectory $CaseDirectory
    $queryPackage = New-GridXEditQuery -Queries $queries -CaseDirectory $CaseDirectory -ContextFingerprint $context.Fingerprint
    if ($SkipXEdit) {
        return [pscustomobject]@{
            Tool = 'Grid.Health.Skyrim'; CaseId = $CaseId; Status = 'ReadyToCollect'; CaseDirectory = $CaseDirectory
            QueryPath = $queryPackage.JsonPath; Detail = "Prepared $($queries.Count) bounded query or queries; xEdit collection was skipped by the caller."
        }
    }

    $probeArguments = @{
        Context = $context; CaseDirectory = $CaseDirectory; Targets = $targets; QueryPackage = $queryPackage
        TimeoutSeconds = $XEditTimeoutSeconds
        CloseOwnedProcesses = $CloseOwnedProcesses; CloseGraceSeconds = $CloseGraceSeconds
        ForceCloseOwnedProcesses = $ForceCloseOwnedProcesses
    }
    $probe = Start-GridXEditProbe @probeArguments
    if ($probe.Status -ne 'Collected') {
        return [pscustomobject]@{
            Tool = 'Grid.Health.Skyrim'; CaseId = $CaseId; Status = 'NeedsEvidence'; CaseDirectory = $CaseDirectory
            QueryPath = $queryPackage.JsonPath; CollectorReason = $probe.Reason; Detail = 'xEdit evidence was not collected.'
        }
    }

    $evidence = Get-GridXEditEvidence -ReportPath $probe.ReportPath -QueryPackage $queryPackage -CaseDirectory $CaseDirectory -ContextFingerprint $context.Fingerprint
    $evidenceItems = @($evidence.Evidence)
    $eslQueries = @($queryPackage.Queries | Where-Object { [string]$_.operation -ceq 'AuditEslEligibility' })
    if ($eslQueries.Count -eq @($queryPackage.Queries).Count) {
        $eligibility = Resolve-GridSkyrimEslEligibilityBatch -Evidence $evidenceItems `
            -PluginNames @($eslQueries | ForEach-Object { [string]$_.plugin }) `
            -ContextFingerprint $context.Fingerprint -CaseDirectory $CaseDirectory
        return [pscustomobject]@{
            Tool = 'Grid.Health.Skyrim'; CaseId = $CaseId; Status = 'EvidenceCollected'; CaseDirectory = $CaseDirectory
            EvidencePath = $evidence.Path; Evidence = $evidenceItems; DiagnosticResult = $null
            EslEligibilityBatchPath = $eligibility.Path; EslEligibilityBatch = $eligibility.Result
            PlacedActorReferencePath = $null; PlacedActorReference = $null
            RemediationProposals = @(); VerificationResults = @($VerificationResults)
            Detail = "Collected and reduced $($evidence.RowCount) verified read-only ESL eligibility row or rows; no remediation was automatically proposed or applied."
        }
    }
    $placedActorReference = Resolve-GridSkyrimPlacedActorReference -Evidence $evidenceItems -QueryPackage $queryPackage -ContextFingerprint $context.Fingerprint -CaseDirectory $CaseDirectory
    $diagnosticResult = Resolve-GridSkyrimDiagnosticResult -CaseId $CaseId -ContextFingerprint $context.Fingerprint `
        -EvidenceFingerprint (Get-GridEvidenceFingerprint -Evidence $evidenceItems) -Evidence $evidenceItems `
        -RemediationProposal $RemediationProposal -VerificationResults $VerificationResults
    [pscustomobject]@{
        Tool = 'Grid.Health.Skyrim'; CaseId = $CaseId; Status = $diagnosticResult.state; CaseDirectory = $CaseDirectory
        EvidencePath = $evidence.Path; Evidence = $evidenceItems; DiagnosticResult = $diagnosticResult
        PlacedActorReferencePath = $placedActorReference.Path; PlacedActorReference = $placedActorReference.Result
        RemediationProposals = @($RemediationProposal | Where-Object { $null -ne $_ })
        VerificationResults = @($VerificationResults)
        Detail = "Collected $($evidence.RowCount) verified xEdit evidence row or rows; no remediation was automatically proposed or applied."
    }
}

Export-ModuleMember -Function Invoke-GridSkyrimHealthAdapter, Invoke-GridSkyrimBaseline, Invoke-GridMo2BaselineCollection, New-GridSkyrimRemediationProposal, Resolve-GridSkyrimDiagnosticResult, `
    Get-GridXEditCollectorProvisioningState, New-GridXEditCollectorProvisioningProposal, Invoke-GridAuthorizedXEditCollectorProvisioning, Get-GridLootExistingOutput, Get-GridSkyrimExistingToolReport
Export-ModuleMember -Function Get-GridXEditReferenceSuppressionWriterProvisioningState, New-GridXEditReferenceSuppressionWriterProvisioningProposal, Invoke-GridAuthorizedXEditReferenceSuppressionWriterProvisioning
Export-ModuleMember -Function Get-GridSkyrimScriptedReferenceEvidence
Export-ModuleMember -Function Get-GridSkyrimPapyrusStateEvidence
Export-ModuleMember -Function Get-GridSkyrimSaveCreatedActorEvidence
Export-ModuleMember -Function Resolve-GridSkyrimPlacedActorReference
Export-ModuleMember -Function Resolve-GridSkyrimEslEligibility, Resolve-GridSkyrimEslEligibilityBatch
Export-ModuleMember -Function Get-GridSkyrimGameplayCapabilityAssessment
Export-ModuleMember -Function Get-GridSkyrimNexusCapabilityObservation
Export-ModuleMember -Function Resolve-GridSkyrimRequestToolInputs

Export-ModuleMember -Function Add-GridSkyrimArtifactToQuarantine, Get-GridSkyrimModChainRepairInspection, New-GridSkyrimModChainRepairProposal, Get-GridRepairSpecificationHash, Invoke-GridAuthorizedModChainRepair, Get-GridSkyrimModChainHistoryTransition, Invoke-GridAuthorizedModChainHistory
Export-ModuleMember -Function New-GridSkyrimPluginStateBatchProposal, New-GridSkyrimPluginStateBatchPlanningCase, Get-GridPluginStateBatchSpecificationHash, Invoke-GridAuthorizedPluginStateBatch, Get-GridSkyrimPluginStateBatchHistoryTransition, Invoke-GridAuthorizedPluginStateBatchHistory
Export-ModuleMember -Function New-GridSkyrimEslFlagProposal, New-GridSkyrimEslFlagPlanningCase, Get-GridEslFlagSpecificationHash, Invoke-GridAuthorizedEslFlag, Get-GridSkyrimEslFlagHistoryTransition, Invoke-GridAuthorizedEslFlagHistory
Export-ModuleMember -Function New-GridSkyrimComponentRecoveryPlan, Get-GridSkyrimComponentRecoveryPlanFromCase, Test-GridSkyrimComponentRecoveryPlan, Read-GridSkyrimSealedComponentRecoveryPlan, New-GridSkyrimComponentRecoveryActionManifest
Export-ModuleMember -Function Invoke-GridSkyrimArtifactAcquisition, Complete-GridSkyrimManagedAcquisition, New-GridSkyrimUserAcquisitionAction, Resolve-GridSkyrimArtifactAcquisitionOutcome, Test-GridSkyrimArtifactProviderIdentity, Get-GridSkyrimProtectedCredentialHandleState
Export-ModuleMember -Function Resolve-GridSkyrimCertificationEligibility, Test-GridSkyrimEnabledPluginIntegrity, New-GridSkyrimCertificationPreflight
Export-ModuleMember -Function New-GridSkyrimDisposableProfilePlan, Test-GridSkyrimDisposableProfileIsolation, New-GridSkyrimMo2ProfileLaunchInvocation, Resolve-GridSkyrimOwnedRuntimeProcess, Get-GridSkyrimRuntimeFileAudit, Invoke-GridSkyrimRuntimeCertification
Export-ModuleMember -Function Invoke-GridSkyrimRuntimeCertificationSession
Export-ModuleMember -Function Invoke-GridSkyrimRootCauseDiagnosis, Invoke-GridSkyrimRootCauseCollector, Resolve-GridSkyrimRootCause, New-GridSkyrimConflictPatchSpecification
Export-ModuleMember -Function New-GridSkyrimReferenceSuppressionProposal, Get-GridSkyrimReferenceSuppressionSpecificationHash
Export-ModuleMember -Function New-GridSkyrimReferenceSuppressionPlanningCase
Export-ModuleMember -Function Get-GridReferenceSuppressionExecutionContext, Get-GridReferenceSuppressionTargets, Invoke-GridAuthorizedReferenceSuppression
Export-ModuleMember -Function Get-GridSkyrimReferenceSuppressionHistoryTransition, Invoke-GridAuthorizedReferenceSuppressionHistory
Export-ModuleMember -Function Invoke-GridSkyrimRecordRelationshipInvestigation
Export-ModuleMember -Function New-GridSkyrimRecordRelationshipRequest
