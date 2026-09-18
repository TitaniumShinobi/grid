#requires -Version 5.1

<#!
.SYNOPSIS
Seals one verified ESL-header specification as a first-class Grid repair task.
.DESCRIPTION
Writes only to Grid's case store. The resulting task is discoverable by the
standard corrective-action authorization and Apply/Undo/Redo lifecycle.
#>
function New-GridSkyrimEslFlagPlanningCase {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseStoreRoot,
        [Parameter(Mandatory)][string]$SpecificationPath,
        [string]$RepairCaseId,
        [switch]$DeferTranscriptIndexSync,
        [switch]$PassThru
    )
    if (-not (Get-Command Test-GridDiagnosticCaseSeal -ErrorAction SilentlyContinue)) {
        throw 'EslFlagHealthModuleRequired: import Grid.Health.psm1 before sealing a planning case.'
    }
    if (-not (Get-Command Get-GridEslFlagSpecificationHash -ErrorAction SilentlyContinue)) {
        $gameRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
        . (Join-Path $gameRoot 'mo2\Grid.EslFlag.ps1')
    }
    $fullSpecificationPath = [IO.Path]::GetFullPath($SpecificationPath)
    if (-not (Test-Path -LiteralPath $fullSpecificationPath -PathType Leaf)) { throw "EslFlagSpecificationMissing: $fullSpecificationPath" }
    $specification = Get-Content -LiteralPath $fullSpecificationPath -Raw | ConvertFrom-Json -ErrorAction Stop
    if ([int]$specification.schemaVersion -ne 1 -or [string]$specification.status -cne 'AwaitingAuthorization') {
        throw 'EslFlagSpecificationInvalid: only an inert v1 AwaitingAuthorization specification can be sealed.'
    }
    $calculated = Get-GridEslFlagSpecificationHash -Specification $specification
    if ([string]$specification.specificationSha256 -cne $calculated) { throw 'EslFlagSpecificationDigestMismatch: specification changed.' }
    $evidenceSeal = Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory ([string]$specification.evidenceCase.directory)
    if (-not $evidenceSeal.IsValid -or [string]$evidenceSeal.Manifest.manifestSha256 -cne [string]$specification.evidenceCase.manifestSha256) {
        throw 'EslFlagEvidenceCaseInvalid: the bound evidence case is invalid or changed.'
    }
    $caseId = if ([string]::IsNullOrWhiteSpace($RepairCaseId)) { [string]$specification.caseId } else { $RepairCaseId }
    if ([string]::IsNullOrWhiteSpace($caseId)) { throw 'EslFlagPlanningCaseIdRequired: no case identity was supplied.' }
    $caseDirectory = Join-Path (Join-Path ([IO.Path]::GetFullPath($CaseStoreRoot)) 'cases\v1') $caseId
    $relativeSpecification = 'repair\esl-flag-specification.v1.json'
    if (Test-Path -LiteralPath $caseDirectory -PathType Container) {
        $seal = Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory $caseDirectory
        $existing = Join-Path $caseDirectory $relativeSpecification
        if (-not $seal.IsValid -or -not (Test-Path -LiteralPath $existing -PathType Leaf) -or
            (Get-FileHash -LiteralPath $existing -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $fullSpecificationPath -Algorithm SHA256).Hash) {
            throw 'EslFlagPlanningCaseCollision: the case identity is already used by different or invalid evidence.'
        }
        $result = [pscustomobject][ordered]@{schemaVersion=1;status='Reused';caseId=$caseId;caseDirectory=$caseDirectory;manifest=$seal.Manifest;specification=$specification}
        if ($PassThru) { return $result } else { return ($result | ConvertTo-Json -Depth 30) }
    }
    $transaction = New-GridCaseStoreTransaction -StoreRoot $CaseStoreRoot -CaseId $caseId
    try {
        $now = [DateTimeOffset]::UtcNow.ToString('o')
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'case.json' -Value ([pscustomobject][ordered]@{
            schemaVersion=1;caseId=$caseId;purpose='EslFlagPlanning';status='Completed';createdAt=$now
        }) | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath $relativeSpecification -SourceLiteralPath $fullSpecificationPath | Out-Null
        $run = [pscustomobject][ordered]@{
            schemaVersion=1;runId=('run-' + ([string]$specification.specificationSha256).Substring(0,16).ToLowerInvariant())
            caseId=$caseId;state='Completed';startedAt=$now;completedAt=$now
            planFingerprint=[string]$specification.specificationSha256;resourcePolicyVersion='grid.esl-flag-planning.v1'
            gates=@([pscustomobject][ordered]@{gateId='EvidenceCaseSeal';status='Pass';evidenceIds=@([string]$specification.evidenceCase.manifestSha256)})
            sufficiency=[pscustomobject][ordered]@{schemaVersion=1;status='SufficientForRequestedDiagnosis'}
            checkpoints=@();primaryFailure=$null;secondaryFailures=@()
        }
        Seal-GridCaseStoreRun -Transaction $transaction -Run $run | Out-Null
        $sealed = Seal-GridDiagnosticCase -Transaction $transaction -SemanticBaselineFingerprint ([string]$specification.specificationSha256) -Runs @($run)
        if (-not $DeferTranscriptIndexSync) {
            Sync-GridRequestTranscriptIndex -StoreRoot $CaseStoreRoot | Out-Null
        }
        $result = [pscustomobject][ordered]@{schemaVersion=1;status='Sealed';caseId=$caseId;caseDirectory=[string]$sealed.CaseDirectory;manifest=$sealed.Manifest;specification=$specification}
        if ($PassThru) { $result } else { $result | ConvertTo-Json -Depth 30 }
    }
    catch {
        if ($transaction.State -eq 'Open' -and (Test-Path -LiteralPath $transaction.TransactionDirectory)) {
            Remove-Item -LiteralPath $transaction.TransactionDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}
