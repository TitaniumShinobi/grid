#requires -Version 5.1
<#
.SYNOPSIS
Dispatches evidence-bound mod-chain repair inspection, proposal, or execution.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Inspect','Propose','Execute')][string]$Mode,
    [Parameter(Mandatory)][string]$InputPath,
    [string]$Game = 'skyrimspecialedition',
    [string]$CaseStoreRoot,
    [switch]$WhatIf,
    [switch]$PassThru
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Grid.Health.psm1') -Force -DisableNameChecking

function New-GridUnavailableRepairResult {
    param([string]$Detail, [string]$Code = 'InstallationContextUnresolved', [string]$Recovery = 'ConfigurationRequired')
    [pscustomobject][ordered]@{
        schemaVersion = 1; runLifecycle = 'Failed'; repairOutcome = 'NotAttempted'; mutationState = 'NotStarted'
        rollbackState = 'NotRequired'; recoveryDisposition = $Recovery; summary = $null
        failures = @([pscustomobject][ordered]@{
            schemaVersion = 1; code = $Code; failedPrimitive = 'ResolveValidatedBaseline'
            affectedResources = @($InputPath); expectedState = 'One sealed sufficient current baseline'
            observedState = $Detail; lastVerifiedJournalEntry = $null; currentHashes = @()
            boundedRecoveryAttempted = @('Validated the exact supplied baseline reference')
            recoveryChoices = @('Register one validated installation context and seal a sufficient baseline before repair')
            externalStateTrustworthy = $true
        })
    }
}

try {
    if (-not (Test-Path -LiteralPath $InputPath -PathType Leaf)) { throw "Input package is missing: $InputPath" }
    $input = Get-Content -LiteralPath $InputPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    if ($input.PSObject.Properties['authorizationSecret']) { throw 'AuthorizationSecretTransportInvalid: authorizationSecret must not be persisted in the repair input package.' }
    $authorizationSecret = if (-not [string]::IsNullOrWhiteSpace([string]$env:GRID_AUTHORIZATION_SECRET)) { [string]$env:GRID_AUTHORIZATION_SECRET } else { $null }
    Remove-Item Env:GRID_AUTHORIZATION_SECRET -ErrorAction SilentlyContinue
    if ([string]::IsNullOrWhiteSpace($CaseStoreRoot)) { $CaseStoreRoot = Get-GridDiagnosticStoreRoot }
    $adapter = Resolve-GridGameAdapter -ScriptsRoot (Split-Path -Parent $PSScriptRoot) -Request 'mod-chain repair' -Game $Game
    $manifest = Import-PowerShellDataFile -LiteralPath $adapter.ManifestPath
    $bindingName = switch ($Mode) { 'Inspect' { 'RepairInspectionCapabilityId' }; 'Propose' { 'RepairProposalCapabilityId' }; 'Execute' { 'RepairExecutionCapabilityId' } }
    $capabilityId = [string]$manifest[$bindingName]
    if ([string]::IsNullOrWhiteSpace($capabilityId)) { throw "Adapter '$Game' does not declare $bindingName." }
    $registry = @(Get-GridCapabilityRegistry -ScriptsRoot ([string]$adapter.ScriptsRoot))
    $contract = Resolve-GridCapabilityContract -Registry $registry -CapabilityId $capabilityId
    $module = Import-Module -Name (Join-Path $adapter.AdapterRoot $adapter.Module) -Force -PassThru
    $command = Get-Command -Name ([string]$contract.implementation.entryPoint) -Module $module.Name -ErrorAction Stop
    if ($Mode -eq 'Inspect') {
        $arguments = @{ CaseStoreRoot = $CaseStoreRoot; BaselineCaseDirectory = [string]$input.baselineCaseDirectory; PassThru = $true }
        if ($input.PSObject.Properties['components']) { $arguments.Components = @($input.components) }
        if ($input.PSObject.Properties['protectedState']) { $arguments.CurrentProtectedState = @($input.protectedState) }
    }
    elseif ($Mode -eq 'Propose') {
        $arguments = @{ CaseId = [string]$input.caseId; Inspection = $input.inspection; ContextFingerprint = [string]$input.contextFingerprint; EvidenceFingerprint = [string]$input.evidenceFingerprint; ModsRoot = [string]$input.modsRoot; Operations = @($input.operations); ProtectedState = @($input.protectedState); Exclusions = @($input.exclusions); OutputPath = [string]$input.outputPath; PassThru = $true }
        foreach ($binding in @(@('ArtifactIntegrity','artifactIntegrity'), @('FomodDecisions','fomodDecisions'), @('CompatibilityMatrix','compatibilityMatrix'), @('Preconditions','preconditions'), @('Postconditions','postconditions'), @('ProfilePreservation','profilePreservation'))) {
            if ($input.PSObject.Properties[$binding[1]]) { $arguments[$binding[0]] = @($input.($binding[1])) }
        }
    }
    else {
        foreach ($required in @('authorizationGrantId','authorizationStoreRoot','semanticBinding')) { if (-not $input.PSObject.Properties[$required]) { throw "AuthorizationRequired: execute input is missing $required." } }
        if ([string]::IsNullOrWhiteSpace($authorizationSecret)) { throw 'AuthorizationRequired: Execute requires GRID_AUTHORIZATION_SECRET in process memory.' }
        $arguments = @{ SpecificationPath = [string]$input.specificationPath; AuthorizationGrantId = [string]$input.authorizationGrantId; AuthorizationSecret = $authorizationSecret; AuthorizationStoreRoot = [string]$input.authorizationStoreRoot; SemanticBinding = $input.semanticBinding; CaseStoreRoot = $CaseStoreRoot; TransactionRoot = [string]$input.transactionRoot; WhatIf = [bool]$WhatIf; PassThru = $true }
    }
    $result = & $command @arguments
    if ($Mode -eq 'Propose' -and $input.PSObject.Properties['repairCaseId'] -and -not [string]::IsNullOrWhiteSpace([string]$input.repairCaseId)) {
        $repairCaseId = [string]$input.repairCaseId
        $existingCase = Join-Path (Join-Path ([IO.Path]::GetFullPath($CaseStoreRoot)) 'cases\v1') $repairCaseId
        if (Test-Path -LiteralPath $existingCase -PathType Container) {
            $existingSeal = Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory $existingCase
            if (-not $existingSeal.IsValid) { throw 'RepairPlanningCaseInvalid: the existing planning case seal is invalid.' }
            $existingSpecification = Join-Path $existingCase 'repair\repair-specification.v1.json'
            if (-not (Test-Path -LiteralPath $existingSpecification -PathType Leaf) -or (Get-FileHash -LiteralPath $existingSpecification -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath ([string]$result.specificationPath) -Algorithm SHA256).Hash) {
                throw 'RepairPlanningCaseCollision: the case ID is already sealed for different evidence.'
            }
            $planningSeal = [pscustomobject]@{ CaseDirectory = $existingCase; Manifest = $existingSeal.Manifest }
        }
        else {
            $transaction = New-GridCaseStoreTransaction -StoreRoot $CaseStoreRoot -CaseId $repairCaseId
            Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'case.json' -Value ([pscustomobject][ordered]@{ schemaVersion = 1; caseId = $repairCaseId; purpose = 'RepairPlanning'; status = 'Completed' }) | Out-Null
            Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'inspection/mod-chain-inspection.v1.json' -Value $input.inspection | Out-Null
            Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'repair/repair-specification.v1.json' -SourceLiteralPath ([string]$result.specificationPath) | Out-Null
            $runId = 'run-' + [guid]::NewGuid().ToString('N')
            $now = [DateTimeOffset]::UtcNow.ToString('o')
            $planningRun = [pscustomobject][ordered]@{ schemaVersion = 1; runId = $runId; caseId = $repairCaseId; state = 'Completed'; startedAt = $now; completedAt = $now; planFingerprint = [string]$result.specification.specificationSha256; resourcePolicyVersion = 'grid.repair-planning.v1'; gates = @(); sufficiency = [pscustomobject]@{ schemaVersion = 1; status = if ([string]$result.status -eq 'InputsVerified') { 'SufficientForRequestedDiagnosis' } else { 'NotEvaluated' } }; checkpoints = @(); primaryFailure = $null; secondaryFailures = @() }
            Seal-GridCaseStoreRun -Transaction $transaction -Run $planningRun | Out-Null
            $planningSeal = Seal-GridDiagnosticCase -Transaction $transaction -SemanticBaselineFingerprint ([string]$result.specification.specificationSha256) -Runs @($planningRun)
        }
        $result | Add-Member -NotePropertyName planningCaseDirectory -NotePropertyValue ([string]$planningSeal.CaseDirectory) -Force
        $result | Add-Member -NotePropertyName planningCaseManifest -NotePropertyValue $planningSeal.Manifest -Force
    }
}
catch {
    $message = $_.Exception.Message
    if ($message -match 'BaselineStale|ProtectedHashChanged|ProtectedStateChanged') {
        $result = New-GridUnavailableRepairResult -Detail $message -Code 'BaselineStale' -Recovery 'AutomaticRetryAvailable'
    }
    elseif ($message -match 'ExactArtifactUnavailable') {
        $result = New-GridUnavailableRepairResult -Detail $message -Code 'ExternalDependencyRequired' -Recovery 'ExternalDependencyRequired'
    }
    elseif ($message -match 'Input package is missing|InstallationContextUnresolved|baselineCaseDirectory|BaselineArtifactMissing') {
        $result = New-GridUnavailableRepairResult -Detail $message
    }
    else { $result = New-GridUnavailableRepairResult -Detail $message -Code 'RepairInspectionFailed' -Recovery 'AutomaticRetryAvailable' }
}
if ($PassThru) { $result } else { $result | ConvertTo-Json -Depth 40 }
