#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

foreach ($component in @(
    'Grid.Capabilities.ps1', 'Grid.RequestPlanner.ps1', 'Grid.AuthorizationStore.ps1', 'Grid.ToolEvidence.ps1', 'Grid.FullHouseEvidenceCoverage.ps1', 'Grid.CaseStore.ps1', 'Grid.RequestExecution.ps1', 'Grid.ScriptAdmission.ps1', 'Grid.ResourceGovernor.ps1', 'Grid.BaselineContext.ps1', 'New-GridDiagnosticCase.ps1', 'Grid.InvestigationPlan.ps1', 'Grid.Evidence.ps1',
    'Grid.DiagnosticResult.ps1', 'Grid.ProblemLedger.ps1', 'Grid.CapabilityGap.ps1', 'Grid.ActionLifecycle.ps1', 'Resolve-GridGameAdapter.ps1', 'process\Grid.Process.ps1'
)) { . (Join-Path $PSScriptRoot $component) }
. (Join-Path $PSScriptRoot 'Grid.ProviderDiscovery.ps1')

Export-ModuleMember -Function @(
    'Get-GridCapabilityManifestPaths','Test-GridCapabilityContract','Read-GridCapabilityManifest',
    'Get-GridCapabilityRegistry','Resolve-GridCapabilityContract','Get-GridCapabilityDependencyClosure','ConvertTo-GridCapabilityBindings',
    'Get-GridCanonicalJsonSha256','New-GridRequestEnvelope','Test-GridRequestEnvelope','Test-GridClassRecipe','Get-GridClassRecipeRegistry','Resolve-GridRequestPlan','Get-GridRegisteredCoverage',
    'Test-GridToolDefinition','Get-GridToolRegistry','Resolve-GridToolEvidencePlan','Invoke-GridToolEvidenceOrchestration','Get-GridFullHouseEvidenceCoverage',
    'Resolve-GridRequestGameId','New-GridInvestigationIntake','New-GridRequestAuthorizationReview','Grant-GridRequestAuthorization','Invoke-GridRegisteredToolAdapter','New-GridRequestEvidenceResult','New-GridCapabilityRequiredResult','New-GridCapabilityAssessmentResult','Resolve-GridRequestRecoveryBaseline','Invoke-GridRequestDiagnosis','Invoke-GridAuthorizedRequestExecution','Sync-GridRequestTranscriptIndex','Get-GridRequestTaskHistory',
    'New-GridScriptCapabilityProposalDraft','Resolve-GridScriptCapabilityAdmission','Resolve-GridCapabilityGap',
    'Get-GridDiagnosticStoreRoot','New-GridCaseStoreTransaction','Add-GridCaseStoreBlob','Write-GridCaseStoreArtifact','New-GridCaseStoreCheckpoint',
    'Seal-GridCaseStoreRun','Seal-GridDiagnosticCase','Test-GridDiagnosticCaseSeal','Test-GridDiagnosticCaseSemanticIdentity','Export-GridDiagnosticCase','Import-GridDiagnosticCase','Remove-GridDiagnosticCase',
    'New-GridBaselineResourceBudget','Test-GridBaselineBudget',
    'Read-GridBoundedBaselineText','Resolve-GridPersistedMo2BaselineContext',
    'Write-GridJsonAtomic','New-GridDiagnosticCase','Set-GridDiagnosticCaseStatus',
    'New-GridProvenanceRecord','New-GridInvestigationPlan','Test-GridInvestigationPlanSchema','Save-GridInvestigationPlan',
    'New-GridEvidenceItem','Test-GridEvidenceItem','Add-GridCaseEvidence','Get-GridEvidenceFingerprint','Get-GridSemanticEvidenceFingerprint',
    'New-GridProblemLedger','Add-GridProblemClaim','Set-GridProblemState','Update-GridProblemLedgerContext','Test-GridProblemLedger','Get-GridProblemLedgerHash','Write-GridProblemLedgerArtifact','New-GridRequestProblemLedger','New-GridProblemLedgerCase',
    'New-GridDiagnosticResult','New-GridUnresolvedDiagnosticResult','Test-GridDiagnosticResult','Get-GridDiagnosticResultFingerprint','ConvertTo-GridDiagnosticResultText','Save-GridDiagnosticResult',
    'New-GridRemediationProposal','Test-GridRemediationProposal','Get-GridRemediationProposalIdentityHash','Grant-GridProposalAuthorization','New-GridVerificationResult',
    'New-GridAuthorizationSecret','New-GridAuthorizationSemanticBinding','Get-GridAuthorizationSemanticDigest','Get-GridAuthorizationStoreRoot','New-GridAuthorizationGrant','Read-GridAuthorizationGrant','Test-GridAuthorizationGrantPreflight','Enter-GridAuthorizationLease','Complete-GridAuthorizationLease','Test-GridAuthorizationSemanticBinding','Test-GridAuthorizationHistoricalRecord',
    'Get-GridInstalledAdapters','Test-GridAdapterConnectedContext','Resolve-GridGameAdapter','Get-GridAdapterInvocationCommand','Get-GridAdapterBaselineCommand',
    'Get-GridProcessSnapshot','Test-GridProcessOwnership','Wait-GridProcessObservation',
    'Get-GridProcessWindowSnapshot','Close-GridOwnedProcess',
    'ConvertFrom-GridProviderKeyValueText','Get-GridRegisteredProviderExecutables','Get-GridRunningProviderProcesses','Get-GridSteamClientRoots','Get-GridSteamLibraryRoots','Get-GridSteamGameCandidates','Get-GridModManagerCandidates','Merge-GridGameInstallationCandidates'
)
