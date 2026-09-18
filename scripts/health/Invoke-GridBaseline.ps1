#requires -Version 5.1
<#
.SYNOPSIS
Plans and dispatches a bounded, read-only game baseline collection.
.DESCRIPTION
Resolves one persisted MO2 installation and its active profile, creates an
InvestigationPlan v2 with Baseline purpose, and dispatches the adapter's
manifest-bound baseline capability. Plain-language input is preserved but is
never used to infer plugins, records, winners, or diagnoses.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][ValidateNotNullOrEmpty()][string]$Request,
    [string]$Game = 'skyrimspecialedition',
    [string]$InstallationId,
    [string]$ProfileId,
    [string]$StatedLocation,
    [string[]]$StatedPluginNames = @(),
    [string[]]$StatedProviderNames = @(),
    [string[]]$StatedFormIds = @(),
    [string[]]$StatedEditorIds = @(),
    [string[]]$ParentCaseIds = @(),
    [string[]]$EvidenceIds = @(),
    [string]$PredecessorCaseId,
    [string]$HashCacheCaseId,
    [string]$FinalizationCaseId,
    [string[]]$RequiredGates = @(
        'InstallationBaseline', 'ProfileBaseline', 'PluginBaseline', 'AssetBaseline',
        'PriorCaseBaseline', 'SymptomEvidenceBaseline', 'RuntimeReferenceBaseline'
    ),
    [string]$CaseStoreRoot,
    [string]$ResourcePolicyVersion = 'grid.baseline.resource-policy.v1',
    [switch]$PassThru
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# This entry point is also invoked inside an authorized request callback. A
# forced reload there removes the module whose request-execution function is
# still on the stack, leaving later lifecycle calls unresolved. Importing the
# same module normally is idempotent and also restores its exported commands
# for standalone callers whose session contains shadowing functions.
Import-Module (Join-Path $PSScriptRoot 'Grid.Health.psm1') -DisableNameChecking

try {
    if ([string]::IsNullOrWhiteSpace($CaseStoreRoot)) {
        $storeCommand = Get-Command Get-GridDiagnosticStoreRoot -ErrorAction SilentlyContinue
        if (-not $storeCommand) { throw 'CollectorUnavailable: Grid.CaseStore.ps1 does not expose Get-GridDiagnosticStoreRoot.' }
        $CaseStoreRoot = [string](& $storeCommand)
    }
    $CaseStoreRoot = [IO.Path]::GetFullPath($CaseStoreRoot)
    $adapter = Resolve-GridGameAdapter -ScriptsRoot (Split-Path -Parent $PSScriptRoot) -Request $Request -Game $Game
    if ([string]::IsNullOrWhiteSpace([string]$adapter.BaselineCapabilityId)) { throw "CollectorUnavailable: adapter '$($adapter.Id)' does not declare a baseline capability." }
    $context = Resolve-GridPersistedMo2BaselineContext -DiagnosticStoreRoot $CaseStoreRoot -InstallationId $InstallationId -ProfileId $ProfileId
    $continuationModes = @(
        @($PredecessorCaseId, $HashCacheCaseId, $FinalizationCaseId) |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
    )
    if ($continuationModes.Count -gt 1) {
        throw 'PlanInvalid: predecessor resume, completed hash-cache reuse, and post-collection finalization are mutually exclusive.'
    }
    # Refuse structurally non-resumable predecessors before allocating a new
    # case identity or entering the game adapter. The adapter performs the
    # deeper plan/context/capability and digest comparison after this gate.
    if (-not [string]::IsNullOrWhiteSpace($PredecessorCaseId)) {
        if ([IO.Path]::GetFileName($PredecessorCaseId) -cne $PredecessorCaseId) { throw 'ResumeInvalid: predecessor case ID is not a safe exact segment.' }
        $predecessorDirectory = Join-Path (Join-Path $CaseStoreRoot 'cases\v1') $PredecessorCaseId
        $seal = Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory $predecessorDirectory
        if (-not $seal.IsValid) { throw ('ResumeInvalid: predecessor case seal failed: ' + ($seal.Errors -join '; ')) }
        $protectedAfterPath = Join-Path $predecessorDirectory 'snapshots\protected-after.v1.json'
        if (-not (Test-Path -LiteralPath $protectedAfterPath -PathType Leaf)) { throw 'ResumeInvalid: predecessor protected-after snapshot is missing.' }
        $predecessorManifest = Get-Content -LiteralPath (Join-Path $predecessorDirectory 'case-manifest.v1.json') -Raw | ConvertFrom-Json -ErrorAction Stop
        $resumableRuns = @($predecessorManifest.runs | Where-Object { [string]$_.state -eq 'PausedAtCheckpoint' })
        if ($resumableRuns.Count -ne 1) { throw 'ResumeInvalid: predecessor has no unique PausedAtCheckpoint run and is not resumable.' }
        $retainedPartitions = @($predecessorManifest.artifacts | Where-Object { [string]$_.path -like 'runs/*/raw/mo2-baseline-attempt-*.ndjson' })
        if ($retainedPartitions.Count -ne 1) { throw 'ResumeInvalid: predecessor has no unique retained collector NDJSON partition.' }
    }
    if (-not [string]::IsNullOrWhiteSpace($FinalizationCaseId)) {
        if ([IO.Path]::GetFileName($FinalizationCaseId) -cne $FinalizationCaseId) { throw 'FinalizationInvalid: source case ID is not a safe exact segment.' }
        $sourceDirectory = Join-Path (Join-Path $CaseStoreRoot 'cases\v1') $FinalizationCaseId
        $seal = Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory $sourceDirectory
        if (-not $seal.IsValid) { throw ('FinalizationInvalid: source case seal failed: ' + ($seal.Errors -join '; ')) }
        $failedRuns = @($seal.Manifest.runs | Where-Object { [string]$_.state -eq 'Failed' })
        if ($failedRuns.Count -ne 1) { throw 'FinalizationInvalid: source case has no unique failed run.' }
        $retainedPartitions = @($seal.Manifest.artifacts | Where-Object { [string]$_.path -like 'runs/*/raw/mo2-baseline-attempt-*.ndjson' })
        if ($retainedPartitions.Count -eq 0) { throw 'FinalizationInvalid: source case has no retained collector NDJSON partition.' }
    }
    $caseId = 'baseline-' + [guid]::NewGuid().ToString('N')
    $plan = New-GridInvestigationPlan -CaseId $caseId -GameId $adapter.Id -InstallationId $context.installationId -ProfileId $context.profileId `
        -OriginalRequest $Request -StatedLocation $StatedLocation -StatedPluginNames $StatedPluginNames -StatedProviderNames $StatedProviderNames -StatedFormIds $StatedFormIds `
        -StatedEditorIds $StatedEditorIds -ParentCaseIds $ParentCaseIds -EvidenceIds $EvidenceIds -Purpose Baseline `
        -RequiredGates $RequiredGates -CapabilityBindings $adapter.BaselineCapabilityBindings
    $validation = Test-GridInvestigationPlanSchema -Plan $plan
    if (-not $validation.IsValid) { throw ('PlanInvalid: ' + ($validation.Errors -join ' ')) }

    $command = Get-GridAdapterBaselineCommand -Adapter $adapter
    $arguments = @{
        CaseId = $caseId; InvestigationPlan = $plan; CaseStoreRoot = $CaseStoreRoot
        InstallationId = $context.installationId; ProfileId = $context.profileId
        ParentCaseIds = @($ParentCaseIds); EvidenceIds = @($EvidenceIds)
        ResourcePolicyVersion = $ResourcePolicyVersion
        ApplicationPath = $context.applicationPath; InstancePath = $context.instancePath
        ResolvedContext = $context
        ProfileName = $context.profileName
        PredecessorCaseId = $PredecessorCaseId
        HashCacheCaseId = $HashCacheCaseId
        FinalizationCaseId = $FinalizationCaseId
    }
    if ($command.Parameters.ContainsKey('PassThru')) { $arguments.PassThru = $true }
    $result = & $command @arguments
}
catch {
    $message = $_.Exception.Message
    $code = if ($message -match '^([A-Za-z]+(?:[A-Za-z]+)?):') { $matches[1] } else { 'UnexpectedFailure' }
    $result = [pscustomobject][ordered]@{
        Tool = 'Invoke-GridBaseline'
        Status = 'Failed'
        TerminalState = 'BaselineFailed'
        CaseId = $null
        CaseDirectory = $null
        PrimaryFailure = [pscustomobject][ordered]@{ code = $code; detail = $message }
        RecoveryDisposition = if ($code -eq 'InstallationContextUnresolved') { 'ConfigurationRequired' } else { 'None' }
        Detail = 'Baseline collection did not start; no game, tool, or external mutation was performed.'
    }
}

if ($PassThru) { $result } else { $result | ConvertTo-Json -Depth 30 }
