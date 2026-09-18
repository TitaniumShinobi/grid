[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'Grid.CaseStore.ps1')

function Assert-GridCaseStoreTest {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-GridCaseStoreThrows {
    param([scriptblock]$Action, [string]$Message)
    $threw = $false
    try { & $Action } catch { $threw = $true }
    if (-not $threw) { throw $Message }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('grid-case-store-test-{0}' -f ([Guid]::NewGuid().ToString('N')))
$sourceRoot = Join-Path $testRoot 'source-store'
$importRoot = Join-Path $testRoot 'import-store'

try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    $transaction = New-GridCaseStoreTransaction -StoreRoot $sourceRoot -CaseId 'synthetic-case'
    Assert-GridCaseStoreTest ($transaction.State -eq 'Open') 'A new transaction must be open.'

    $context = [pscustomobject][ordered]@{ schemaVersion = 1; kind = 'SyntheticContext'; value = 7 }
    $contextArtifact = Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'evidence/context.json' -Value $context
    Assert-GridCaseStoreTest ($contextArtifact.sha256 -match '^[A-F0-9]{64}$') 'Artifact SHA-256 was not recorded.'
    Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'notes/readme.txt' -Text 'synthetic only' | Out-Null
    Assert-GridCaseStoreThrows { Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath '../escape.txt' -Text 'refused' } 'Traversal artifact path was not refused.'
    Assert-GridCaseStoreThrows { Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'case-manifest.v1.json' -Text 'refused' } 'Reserved manifest path was not refused.'

    $blobSource = Join-Path $testRoot 'attachment.bin'
    [IO.File]::WriteAllBytes($blobSource, [byte[]](1, 3, 3, 7))
    $blob = Add-GridCaseStoreBlob -Transaction $transaction -LiteralPath $blobSource
    Assert-GridCaseStoreTest ($blob.sizeBytes -eq 4) 'Blob size was not recorded.'

    $usage = [pscustomobject][ordered]@{
        wallClockSeconds = 1
        filesObserved = 2
        bytesRead = 10
        bytesHashed = 4
        attachmentBytes = 4
        priorCases = 0
        concurrentReads = 1
        filesSinceCheckpoint = 2
        secondsSinceCheckpoint = 1
    }
    $gate = [pscustomobject][ordered]@{
        schemaVersion = 1
        gate = 'InstallationBaseline'
        status = 'Complete'
        required = $true
        evidenceIds = @('context')
        unavailableReasons = @()
        contradictions = @()
        updatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    }
    $checkpoint = New-GridCaseStoreCheckpoint -Transaction $transaction -RunId 'run-1' -Sequence 1 -ResourceUsage $usage -Gates @($gate)
    Assert-GridCaseStoreTest ($checkpoint.state -eq 'Sealed') 'Checkpoint was not sealed.'

    $checkpointArtifact = @($transaction.Artifacts | Where-Object { $_.path -eq 'runs/run-1/checkpoints/00000001.json' })[0]
    $sufficiency = [pscustomobject][ordered]@{
        schemaVersion = 1
        caseId = 'synthetic-case'
        status = 'SufficientForRequestedDiagnosis'
        evaluatedAt = [DateTimeOffset]::UtcNow.ToString('o')
        requiredGates = @('InstallationBaseline')
        incompleteGates = @()
        contradictions = @()
        nextRequiredEvidence = @()
    }
    $run = [pscustomobject][ordered]@{
        schemaVersion = 1
        runId = 'run-1'
        caseId = 'synthetic-case'
        state = 'Completed'
        startedAt = [DateTimeOffset]::UtcNow.AddSeconds(-1).ToString('o')
        completedAt = [DateTimeOffset]::UtcNow.ToString('o')
        planFingerprint = (('A' * 64) -join '')
        resourcePolicyVersion = 'grid.baseline.resource-policy.v1'
        gates = @($gate)
        sufficiency = $sufficiency
        checkpoints = @([pscustomobject][ordered]@{ checkpointId = $checkpoint.checkpointId; state = $checkpoint.state; path = $checkpointArtifact.path; sha256 = $checkpointArtifact.sha256 })
        primaryFailure = $null
        secondaryFailures = @()
    }
    Seal-GridCaseStoreRun -Transaction $transaction -Run $run | Out-Null
    $sealed = Seal-GridDiagnosticCase -Transaction $transaction -SemanticBaselineFingerprint ((('B' * 64) -join '')) -Runs @($run)
    Assert-GridCaseStoreTest ($transaction.State -eq 'Sealed') 'Transaction did not transition to Sealed.'

    $sealResult = Test-GridDiagnosticCaseSeal -StoreRoot $sourceRoot -CaseDirectory $sealed.CaseDirectory
    Assert-GridCaseStoreTest $sealResult.IsValid ('Case seal failed: ' + ($sealResult.Errors -join '; '))
    $semanticExact = Test-GridDiagnosticCaseSemanticIdentity -StoreRoot $sourceRoot -CaseDirectory $sealed.CaseDirectory -ExpectedArtifactSha256 @{} -ExpectedSemanticBaselineFingerprint ('B' * 64)
    Assert-GridCaseStoreTest $semanticExact.IsValid 'Exact sealed semantic identity must validate.'
    $semanticWrong = Test-GridDiagnosticCaseSemanticIdentity -StoreRoot $sourceRoot -CaseDirectory $sealed.CaseDirectory -ExpectedArtifactSha256 @{} -ExpectedSemanticBaselineFingerprint ('C' * 64)
    Assert-GridCaseStoreTest (-not $semanticWrong.IsValid) 'A sealed case with another semantic baseline must not be reusable.'

    $export1 = Join-Path $testRoot 'case-1.zip'
    $export2 = Join-Path $testRoot 'case-2.zip'
    Export-GridDiagnosticCase -StoreRoot $sourceRoot -CaseId 'synthetic-case' -DestinationLiteralPath $export1 | Out-Null
    Export-GridDiagnosticCase -StoreRoot $sourceRoot -CaseId 'synthetic-case' -DestinationLiteralPath $export2 | Out-Null
    Assert-GridCaseStoreTest ((Get-FileHash -LiteralPath $export1 -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $export2 -Algorithm SHA256).Hash) 'Repeated exports were not byte-for-byte deterministic.'

    $imported = Import-GridDiagnosticCase -StoreRoot $importRoot -ArchiveLiteralPath $export1
    $importSeal = Test-GridDiagnosticCaseSeal -StoreRoot $importRoot -CaseDirectory $imported.CaseDirectory
    Assert-GridCaseStoreTest $importSeal.IsValid ('Imported case seal failed: ' + ($importSeal.Errors -join '; '))
    Assert-GridCaseStoreThrows { Import-GridDiagnosticCase -StoreRoot $importRoot -ArchiveLiteralPath $export1 } 'Import did not refuse a case-id collision.'

    Remove-GridDiagnosticCase -StoreRoot $importRoot -CaseId 'synthetic-case' -WhatIf | Out-Null
    Assert-GridCaseStoreTest (Test-Path -LiteralPath $imported.CaseDirectory -PathType Container) 'WhatIf unexpectedly removed a case.'

    [IO.File]::AppendAllText((Join-Path $imported.CaseDirectory 'notes\readme.txt'), 'tamper')
    $tampered = Test-GridDiagnosticCaseSeal -StoreRoot $importRoot -CaseDirectory $imported.CaseDirectory
    Assert-GridCaseStoreTest (-not $tampered.IsValid) 'Tampering was not detected.'

    $schemaNames = @(
        'case-manifest.v1.schema.json',
        'collection-run.v1.schema.json',
        'collection-checkpoint.v1.schema.json',
        'resource-budget.v1.schema.json',
        'evidence-gate.v1.schema.json',
        'profile-snapshot.v1.schema.json',
        'file-observation.v1.schema.json',
        'baseline-sufficiency.v1.schema.json',
        'attachment-index.v1.schema.json',
        'prior-evidence-index.v1.schema.json'
    )
    $schemaRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'schemas'
    foreach ($schemaName in $schemaNames) {
        $schema = Get-Content -Raw -LiteralPath (Join-Path $schemaRoot $schemaName) | ConvertFrom-Json -ErrorAction Stop
        Assert-GridCaseStoreTest ($schema.additionalProperties -eq $false) "Schema '$schemaName' is not closed."
    }

    Write-Host 'CaseStore.Tests.ps1: PASS'
}
finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
