$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent $PSScriptRoot
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $gameRoot)
Import-Module (Join-Path $scriptsRoot 'health\Grid.Health.psm1') -Force
Import-Module (Join-Path $gameRoot 'health\Grid.Health.Skyrim.psm1') -Force
. (Join-Path $gameRoot 'health\actions\Get-GridSkyrimModChainRepairInspection.ps1')
. (Join-Path $gameRoot 'health\actions\New-GridSkyrimModChainRepairProposal.ps1')
. (Join-Path $gameRoot 'health\actions\Invoke-GridAuthorizedModChainRepair.ps1')

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }
function Assert-Throws([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    try { & $Action; throw "$Message Expected an exception matching '$Pattern'." }
    catch { if ($_.Exception.Message -notmatch $Pattern) { throw "$Message Unexpected exception: $($_.Exception.Message)" } }
}

function New-RepairAuthorization($Specification, [string]$StoreRoot, [string]$Suffix) {
    $targets = @($Specification.operations | ForEach-Object { [string]$_.targetDirectory } | Sort-Object -Unique)
    $binding = New-GridAuthorizationSemanticBinding -ActorId 'actor.fixture' -SessionId 'session.fixture' `
        -WorkspaceId ("workspace-$Suffix") -RequestId ("request-$Suffix") -SubmissionId ("submission-$Suffix") `
        -EnvelopeSha256 ('A' * 64) -PlanSha256 ('B' * 64) -ScopeSha256 (Get-GridCanonicalJsonSha256 -InputObject $targets) `
        -ProposalOrSpecificationId ([string]$Specification.specificationId) -ProposalOrSpecificationSha256 ([string]$Specification.specificationSha256) `
        -Capabilities @([pscustomobject]@{ capabilityId = 'grid.game.skyrimspecialedition.mod-chain-repair.execute'; capabilityVersion = '2.0.0'; adapterId = 'SkyrimSpecialEdition'; adapterVersion = '1' }) `
        -Targets $targets -NormalizedInput $Specification
    $issued = New-GridAuthorizationGrant -StoreRoot $StoreRoot -ReviewId ("review-$Suffix") -AuthorityClass Mutation -SemanticBinding $binding -LifetimeMinutes 10
    [pscustomobject]@{ Binding = $binding; GrantId = [string]$issued.Grant.grantId; Secret = [string]$issued.AuthorizationSecret }
}

function New-HistoryAuthorization($Transition, [string]$StoreRoot, [string]$Suffix) {
    $binding = New-GridAuthorizationSemanticBinding -ActorId 'actor.fixture' -SessionId 'session.fixture' `
        -WorkspaceId ("workspace-$Suffix") -RequestId ("request-$Suffix") -SubmissionId ("submission-$Suffix") `
        -EnvelopeSha256 ('A' * 64) -PlanSha256 ('B' * 64) -ScopeSha256 (Get-GridCanonicalJsonSha256 -InputObject @($Transition.targets)) `
        -ProposalOrSpecificationId ([string]$Transition.transitionId) -ProposalOrSpecificationSha256 ([string]$Transition.transitionSha256) `
        -Capabilities @([pscustomobject]@{ capabilityId = 'grid.game.skyrimspecialedition.mod-chain-repair.history.execute'; capabilityVersion = '1.0.0'; adapterId = 'SkyrimSpecialEdition'; adapterVersion = '1' }) `
        -Targets @($Transition.targets) -NormalizedInput $Transition.normalizedInput
    $issued = New-GridAuthorizationGrant -StoreRoot $StoreRoot -ReviewId ("review-$Suffix") -AuthorityClass Mutation -SemanticBinding $binding -LifetimeMinutes 10
    [pscustomobject]@{ Binding = $binding; GrantId = [string]$issued.Grant.grantId; Secret = [string]$issued.AuthorizationSecret }
}

$root = Join-Path $env:TEMP ('grid-repair-' + [guid]::NewGuid().ToString('N'))
$store = Join-Path $root 'store'
$mods = Join-Path $root 'instance\mods'
$source = Join-Path $root 'quarantine\expanded\replacement'
$stageParent = Join-Path $root 'stage'
$rollbackParent = Join-Path $root 'rollback'
$transactions = Join-Path $root 'transactions'
$target = Join-Path $mods 'Fixture Component'
$staging = Join-Path $stageParent 'Fixture Component'
$rollback = Join-Path $rollbackParent 'Fixture Component'
$protected = Join-Path $root 'instance\profiles\Fixture\modlist.txt'
$fixtureGameData = Join-Path $root 'game\Data'
foreach ($directory in @($target, $source, $stageParent, $rollbackParent, $transactions, (Split-Path -Parent $protected), $fixtureGameData)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
[IO.File]::WriteAllText((Join-Path $target 'old.txt'), 'old', (New-Object Text.UTF8Encoding($false)))
[IO.File]::WriteAllText((Join-Path $source 'new.txt'), 'new', (New-Object Text.UTF8Encoding($false)))
[IO.File]::WriteAllText($protected, '+Fixture Component', (New-Object Text.UTF8Encoding($false)))

try {
    $artifactSource = Join-Path $root 'artifact-source.zip'
    [IO.File]::WriteAllBytes($artifactSource, [byte[]](1, 2, 3, 4, 5, 6))
    $artifactHash = (Get-FileHash -LiteralPath $artifactSource -Algorithm SHA256).Hash
    $quarantineRoot = Join-Path $root 'quarantine\objects'
    $quarantined = Add-GridSkyrimArtifactToQuarantine -SourceLiteralPath $artifactSource -QuarantineRoot $quarantineRoot `
        -ExpectedSha256 $artifactHash -ExpectedSizeBytes 6 -IdentityEvidenceIds @('artifact-evidence') -Confirm:$false -PassThru
    Assert-Equal 'Verified' $quarantined.status 'An exact local artifact must enter quarantine only after size and digest verification.'
    Assert-Equal $artifactHash $quarantined.sha256 'The quarantine identity must be content-addressed.'
    $deduplicated = Add-GridSkyrimArtifactToQuarantine -SourceLiteralPath $artifactSource -QuarantineRoot $quarantineRoot `
        -ExpectedSha256 $artifactHash -ExpectedSizeBytes 6 -IdentityEvidenceIds @('artifact-evidence') -Confirm:$false -PassThru
    Assert-Equal $quarantined.path $deduplicated.path 'Reacquiring identical evidence must deduplicate to the same object.'
    $rejected = Add-GridSkyrimArtifactToQuarantine -SourceLiteralPath $artifactSource -QuarantineRoot $quarantineRoot `
        -ExpectedSha256 ('E' * 64) -ExpectedSizeBytes 6 -IdentityEvidenceIds @('artifact-evidence') -Confirm:$false -PassThru
    Assert-Equal 'Rejected' $rejected.status 'A checksum mismatch must remain rejected and never enter the verified CAS namespace.'
    Assert-Throws {
        Add-GridSkyrimArtifactToQuarantine -SourceUri 'https://user:secret@example.invalid/artifact' -AllowedProviderHosts @('example.invalid') `
            -QuarantineRoot $quarantineRoot -ExpectedSha256 $artifactHash -ExpectedSizeBytes 6 -IdentityEvidenceIds @('artifact-evidence') -Confirm:$false
    } 'credentials.*prohibited' 'Credential-bearing acquisition URLs must fail before a network request.'
    Write-Host 'PASS: quarantine verifies, deduplicates, rejects mismatches, and refuses credential-bearing network requests.'

    $caseId = 'baseline-fixture'
    $transaction = New-GridCaseStoreTransaction -StoreRoot $store -CaseId $caseId
    Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'case.json' -Value ([pscustomobject]@{ caseId = $caseId }) | Out-Null
    $plan = [pscustomobject][ordered]@{
        schemaVersion = 2; purpose = 'Baseline'; caseId = $caseId; installationId = 'installation-fixture'; profileId = 'profile-fixture'
        providerSeeds = @([pscustomobject]@{ name = 'Fixture Component' }); requiredGates = @()
        capabilities = @(
            [pscustomobject]@{ capabilityId = 'grid.game.skyrimspecialedition.baseline.collect'; capabilityVersion = '2.6.0' },
            [pscustomobject]@{ capabilityId = 'grid.game.skyrimspecialedition.mod-chain-repair.inspect'; capabilityVersion = '1.0.0' }
        )
    }
    Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'investigation-plan.json' -Value $plan | Out-Null
    Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'installation/installation-baseline.v1.json' -Value ([pscustomobject]@{ schemaVersion = 1; installationId = 'installation-fixture'; profileId = 'profile-fixture'; roots=@([pscustomobject]@{label='Skyrim Data directory';path=$fixtureGameData}) }) | Out-Null
    $protectedHash = (Get-FileHash -LiteralPath $protected -Algorithm SHA256).Hash
    $snapshot = [pscustomobject]@{ schemaVersion = 1; installationId = 'installation-fixture'; profileId = 'profile-fixture'; files = @([pscustomobject]@{ path = $protected; sha256 = $protectedHash }) }
    Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'snapshots/protected-before.v1.json' -Value $snapshot | Out-Null
    Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'snapshots/protected-after.v1.json' -Value $snapshot | Out-Null
    $modRecord = [pscustomobject]@{ modId = 'provider:fixture'; name = 'Fixture Component'; version = '1.0.0'; canonicalPath = $target }
    Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'inventory/mods.v1.ndjson' -Text (($modRecord | ConvertTo-Json -Compress) + "`n") | Out-Null
    foreach ($emptyPath in @('inventory/plugins.v1.ndjson','inventory/plugin-dependencies.v1.ndjson','inventory/archives.v1.ndjson','inventory/virtual-winners.v1.ndjson','provenance/source-archives.v1.ndjson','provenance/source-archive-sidecars.v1.ndjson')) {
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath $emptyPath -Text '' | Out-Null
    }
    Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'provenance/mod-metadata.v1.json' -Value ([pscustomobject]@{ schemaVersion = 1; records = @($modRecord) }) | Out-Null
    $run = [pscustomobject][ordered]@{
        schemaVersion = 1; runId = 'run-fixture'; caseId = $caseId; state = 'Completed'; startedAt = [DateTimeOffset]::UtcNow.ToString('o')
        completedAt = [DateTimeOffset]::UtcNow.ToString('o'); planFingerprint = ('A' * 64); resourcePolicyVersion = 'fixture'
        gates = @(); sufficiency = [pscustomobject]@{ schemaVersion = 1; status = 'SufficientForRequestedDiagnosis' }; checkpoints = @(); primaryFailure = $null; secondaryFailures = @()
    }
    Seal-GridCaseStoreRun -Transaction $transaction -Run $run | Out-Null
    $sealed = Seal-GridDiagnosticCase -Transaction $transaction -SemanticBaselineFingerprint ('B' * 64) -Runs @($run)
    $components = @([pscustomobject]@{ componentId = 'provider:fixture'; version = '1.0.0'; installedDirectory = $target; evidenceIds = @('evidence-fixture') })
    $protectedState = @([pscustomobject]@{ path = $protected; sha256 = $protectedHash })
    $inspection = Get-GridSkyrimModChainRepairInspection -CaseStoreRoot $store -BaselineCaseDirectory $sealed.CaseDirectory -Components $components -CurrentProtectedState $protectedState -PassThru
    Assert-Equal 'InputsVerified' $inspection.status ('A sealed current baseline should be repair-ready. ' + ($inspection.failures | ConvertTo-Json -Depth 8 -Compress))
    Assert-Equal $sealed.Manifest.manifestSha256 $inspection.baselineManifestSha256 'Inspection must bind the exact baseline seal.'

    $old = Get-GridRepairTreeObservation -LiteralPath $target
    $new = Get-GridRepairTreeObservation -LiteralPath $source
    $operation = [pscustomobject]@{
        sequence = 1; componentId = 'provider:fixture'; sourceDirectory = $source; targetDirectory = $target
        stagingDirectory = $staging; rollbackDirectory = $rollback; beforeTreeSha256 = $old.treeSha256
        expectedTreeSha256 = $new.treeSha256; baselineEvidenceIds = @('evidence-fixture'); artifactSha256 = $artifactHash
        sourceToDestinationMappings = @([pscustomobject]@{ source = 'new.txt'; destination = 'new.txt' })
        expectedFiles = @([pscustomobject]@{ path = 'new.txt'; sha256 = (Get-FileHash -LiteralPath (Join-Path $source 'new.txt') -Algorithm SHA256).Hash })
    }
    $artifactEvidence = @([pscustomobject]@{ schemaVersion = 1; artifactId = "sha256:$artifactHash"; path = $quarantined.path; status = 'Verified'; sizeBytes = 6; sha256 = $artifactHash; identityEvidenceIds = @('artifact-evidence'); observedAt = [DateTimeOffset]::UtcNow.ToString('o') })
    $fomod = @([pscustomobject]@{ schemaVersion = 1; artifactSha256 = $artifactHash; moduleConfigSha256 = ('F' * 64); status = 'NotPresent'; selections = @(); fileMappings = @([pscustomobject]@{ source = 'new.txt'; destination = 'new.txt' }) })
    $compatibility = @([pscustomobject]@{ schemaVersion = 1; components = @('provider:fixture'); requirements = @('fixture requirement'); status = 'VerifiedCompatible' })
    $specPath = Join-Path $root 'case\repair-specification.v1.json'
    $proposal = New-GridSkyrimModChainRepairProposal -CaseId repair-fixture -Inspection $inspection -ContextFingerprint ('C' * 64) -EvidenceFingerprint ('D' * 64) -ModsRoot $mods -Operations @($operation) -ProtectedState $protectedState `
        -ArtifactIntegrity $artifactEvidence -FomodDecisions $fomod -CompatibilityMatrix $compatibility -Preconditions @('Baseline and target hashes remain current') -Postconditions @('Replacement tree and protected hashes verify') `
        -ProfilePreservation $protectedState -Exclusions @('Profile writes') -OutputPath $specPath -PassThru
    Assert-Equal 'AwaitingAuthorization' $proposal.status 'Proposal must remain inert.'
    Assert-True (Test-Path -LiteralPath $target) 'Proposal must not alter the target tree.'
    $replaySpecPath = Join-Path $root 'case\repair-specification-replay.v1.json'
    $proposalReplay = New-GridSkyrimModChainRepairProposal -CaseId repair-fixture -Inspection $inspection -ContextFingerprint ('C' * 64) -EvidenceFingerprint ('D' * 64) -ModsRoot $mods -Operations @($operation) -ProtectedState $protectedState `
        -ArtifactIntegrity $artifactEvidence -FomodDecisions $fomod -CompatibilityMatrix $compatibility -Preconditions @('Baseline and target hashes remain current') -Postconditions @('Replacement tree and protected hashes verify') `
        -ProfilePreservation $protectedState -Exclusions @('Profile writes') -OutputPath $replaySpecPath -PassThru
    Assert-Equal $proposal.specification.specificationId $proposalReplay.specification.specificationId 'Identical evidence must reproduce the same repair specification ID.'
    Assert-Equal $proposal.specification.specificationSha256 $proposalReplay.specification.specificationSha256 'Identical evidence must reproduce the same repair specification digest.'
    $roundTrippedSpecification = Get-Content -LiteralPath $specPath -Raw | ConvertFrom-Json
    Assert-Equal $proposal.specification.specificationSha256 (Get-GridRepairSpecificationHash -Specification $roundTrippedSpecification) 'The repair specification digest must survive a JSON write/read cycle.'
    Assert-True $proposal.authorizationRequired 'A nonempty repair specification must require explicit durable authorization.'
    Assert-True $proposalReplay.authorizationRequired 'Deterministic proposal replay must still require a newly issued one-use grant.'
    $wrongAuth = New-RepairAuthorization -Specification $proposal.specification -StoreRoot $store -Suffix 'wrong-secret'
    Assert-Throws {
        Invoke-GridAuthorizedModChainRepair -SpecificationPath $specPath -AuthorizationGrantId $wrongAuth.GrantId -AuthorizationSecret 'wrong-secret' -AuthorizationStoreRoot $store -SemanticBinding $wrongAuth.Binding -CaseStoreRoot $store -TransactionRoot $transactions -Confirm:$false -PassThru
    } 'AuthorizationSecretInvalid' 'A nonmatching authorization secret must fail before staging or mutation.'

    $whatIfAuth = New-RepairAuthorization -Specification $proposal.specification -StoreRoot $store -Suffix 'whatif'
    $whatIf = Invoke-GridAuthorizedModChainRepair -SpecificationPath $specPath -AuthorizationGrantId $whatIfAuth.GrantId -AuthorizationSecret $whatIfAuth.Secret -AuthorizationStoreRoot $store -SemanticBinding $whatIfAuth.Binding -CaseStoreRoot $store -TransactionRoot $transactions -WhatIf -PassThru
    Assert-True ($null -eq $whatIf.summary) 'A nonexecuted WhatIf must not fabricate a derived terminal summary.'
    Assert-Equal $old.treeSha256 (Get-GridRepairTreeObservation -LiteralPath $target).treeSha256 'WhatIf must preserve the target.'
    Assert-True (-not (Test-Path -LiteralPath $staging)) 'WhatIf must not create staging.'

    $executeAuth = New-RepairAuthorization -Specification $proposal.specification -StoreRoot $store -Suffix 'execute'
    $receipt = Invoke-GridAuthorizedModChainRepair -SpecificationPath $specPath -AuthorizationGrantId $executeAuth.GrantId -AuthorizationSecret $executeAuth.Secret -AuthorizationStoreRoot $store -SemanticBinding $executeAuth.Binding -CaseStoreRoot $store -TransactionRoot $transactions -Confirm:$false -PassThru
    Assert-Equal 'InputsRepairedAndVerified' $receipt.result.summary 'Verified promotion must report the repaired terminal state.'
    Assert-Equal $new.treeSha256 (Get-GridRepairTreeObservation -LiteralPath $target).treeSha256 'Target must equal the staged replacement.'
    Assert-Equal $old.treeSha256 (Get-GridRepairTreeObservation -LiteralPath $rollback).treeSha256 'Rollback storage must retain the exact prior tree.'
    Assert-Equal $protectedHash (Get-FileHash -LiteralPath $protected -Algorithm SHA256).Hash 'Protected profile state must remain byte-identical.'
    $replay = Invoke-GridAuthorizedModChainRepair -SpecificationPath $specPath -AuthorizationGrantId $executeAuth.GrantId -AuthorizationSecret $executeAuth.Secret -AuthorizationStoreRoot $store -SemanticBinding $executeAuth.Binding -CaseStoreRoot $store -TransactionRoot $transactions -Confirm:$false -PassThru
    Assert-Equal $receipt.transactionId $replay.transactionId 'Terminal replay must return the existing receipt.'
    Assert-Equal $receipt.journalSha256 $replay.journalSha256 'Terminal replay must not append another mutation.'
    Write-Host 'PASS: sealed-baseline repair stages, preserves, promotes, verifies, journals, and replays idempotently.'

    $undoTransition = Get-GridSkyrimModChainHistoryTransition -SpecificationPath $specPath -TransactionRoot $transactions -Direction Undo
    $undoAuth = New-HistoryAuthorization -Transition $undoTransition -StoreRoot $store -Suffix 'undo'
    $undoReceipt = Invoke-GridAuthorizedModChainHistory -SpecificationPath $specPath -TransactionRoot $transactions -Direction Undo -AuthorizationGrantId $undoAuth.GrantId -AuthorizationSecret $undoAuth.Secret -AuthorizationStoreRoot $store -SemanticBinding $undoAuth.Binding -CaseStoreRoot $store -Confirm:$false -PassThru
    Assert-Equal 'Undone' $undoReceipt.state 'Undo must record the verified original state.'
    Assert-Equal $old.treeSha256 (Get-GridRepairTreeObservation -LiteralPath $target).treeSha256 'Undo must restore the exact original target tree.'
    Assert-True (-not (Test-Path -LiteralPath $rollback)) 'Undo must move the preserved original back into the target path.'
    $redoTree = $rollback + '.grid-redo-' + $proposal.specification.specificationSha256.Substring(0, 16).ToLowerInvariant()
    Assert-Equal $new.treeSha256 (Get-GridRepairTreeObservation -LiteralPath $redoTree).treeSha256 'Undo must preserve the applied tree for Redo.'

    $redoTransition = Get-GridSkyrimModChainHistoryTransition -SpecificationPath $specPath -TransactionRoot $transactions -Direction Redo
    $redoAuth = New-HistoryAuthorization -Transition $redoTransition -StoreRoot $store -Suffix 'redo'
    $redoReceipt = Invoke-GridAuthorizedModChainHistory -SpecificationPath $specPath -TransactionRoot $transactions -Direction Redo -AuthorizationGrantId $redoAuth.GrantId -AuthorizationSecret $redoAuth.Secret -AuthorizationStoreRoot $store -SemanticBinding $redoAuth.Binding -CaseStoreRoot $store -Confirm:$false -PassThru
    Assert-Equal 'Applied' $redoReceipt.state 'Redo must record the verified repaired state.'
    Assert-Equal $new.treeSha256 (Get-GridRepairTreeObservation -LiteralPath $target).treeSha256 'Redo must restore the exact applied target tree.'
    Assert-Equal $old.treeSha256 (Get-GridRepairTreeObservation -LiteralPath $rollback).treeSha256 'Redo must preserve the original tree for another Undo.'
    Assert-True (-not (Test-Path -LiteralPath $redoTree)) 'Redo must consume its exact preserved redo tree.'
    Write-Host 'PASS: Edit-history Undo and Redo consume distinct authorizations and verify both tree states.'

    . (Join-Path $scriptsRoot 'health\Grid.RequestMutation.ps1')
    $planningCaseId = 'repair-planning-fixture'
    $planningTransaction = New-GridCaseStoreTransaction -StoreRoot $store -CaseId $planningCaseId
    $planningCreated = [DateTimeOffset]::UtcNow.ToString('o')
    Write-GridCaseStoreArtifact -Transaction $planningTransaction -RelativePath 'case.json' -Value ([pscustomobject][ordered]@{schemaVersion=1;caseId=$planningCaseId;purpose='RepairPlanning';status='Completed';createdAt=$planningCreated}) | Out-Null
    Write-GridCaseStoreArtifact -Transaction $planningTransaction -RelativePath 'repair/repair-specification.v1.json' -SourceLiteralPath $specPath | Out-Null
    $planningRun = [pscustomobject][ordered]@{schemaVersion=1;runId='run-repair-planning-fixture';caseId=$planningCaseId;state='Completed';startedAt=$planningCreated;completedAt=$planningCreated;planFingerprint=[string]$proposal.specification.specificationSha256;resourcePolicyVersion='grid.repair-planning.v1';gates=@();sufficiency=[pscustomobject]@{schemaVersion=1;status='SufficientForRequestedDiagnosis'};checkpoints=@();primaryFailure=$null;secondaryFailures=@()}
    Seal-GridCaseStoreRun -Transaction $planningTransaction -Run $planningRun | Out-Null
    Seal-GridDiagnosticCase -Transaction $planningTransaction -SemanticBaselineFingerprint ([string]$proposal.specification.specificationSha256) -Runs @($planningRun) | Out-Null
    $bridgeTransactionRoot = Join-Path $store 'repair-transactions'
    New-Item -ItemType Directory -Path $bridgeTransactionRoot -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $transactions ('transaction-' + $proposal.specification.specificationSha256)) -Destination $bridgeTransactionRoot -Recurse
    $prepareUndoInput = [pscustomobject]@{operation='PrepareMutation';mutationAction='RollBack';taskId=$planningCaseId;actorId='actor.fixture';sessionId='session.fixture';submissionId='submission-bridge-undo'}
    $preparedUndo = Invoke-GridRequestMutationOperation -Request $prepareUndoInput -StoreRoot $store -ScriptsRoot $scriptsRoot
    Assert-Equal 'Mutation' $preparedUndo.authorizationReview.authorityClass 'Undo preparation must disclose mutation authority.'
    $authorizeUndoInput = $prepareUndoInput.PSObject.Copy(); $authorizeUndoInput.operation = 'Authorize'
    $authorizedUndo = Invoke-GridRequestMutationOperation -Request $authorizeUndoInput -StoreRoot $store -ScriptsRoot $scriptsRoot
    $executeUndoInput = $prepareUndoInput.PSObject.Copy(); $executeUndoInput.operation = 'Execute'; $executeUndoInput | Add-Member authorizationGrantId $authorizedUndo.grantId; $executeUndoInput | Add-Member authorizationSecret $authorizedUndo.authorizationSecret
    $bridgeUndo = Invoke-GridRequestMutationOperation -Request $executeUndoInput -StoreRoot $store -ScriptsRoot $scriptsRoot
    Assert-Equal 'Undone' $bridgeUndo.tasks[0].repairState.historyState 'Authorized request bridge Undo must refresh task history to Undone.'
    $prepareRedoInput = [pscustomobject]@{operation='PrepareMutation';mutationAction='ApplyRepair';taskId=$planningCaseId;actorId='actor.fixture';sessionId='session.fixture';submissionId='submission-bridge-redo'}
    $preparedRedo = Invoke-GridRequestMutationOperation -Request $prepareRedoInput -StoreRoot $store -ScriptsRoot $scriptsRoot
    Assert-Equal 'Mutation' $preparedRedo.authorizationReview.authorityClass 'Redo preparation must disclose mutation authority.'
    $authorizeRedoInput = $prepareRedoInput.PSObject.Copy(); $authorizeRedoInput.operation = 'Authorize'
    $authorizedRedo = Invoke-GridRequestMutationOperation -Request $authorizeRedoInput -StoreRoot $store -ScriptsRoot $scriptsRoot
    $executeRedoInput = $prepareRedoInput.PSObject.Copy(); $executeRedoInput.operation = 'Execute'; $executeRedoInput | Add-Member authorizationGrantId $authorizedRedo.grantId; $executeRedoInput | Add-Member authorizationSecret $authorizedRedo.authorizationSecret
    $bridgeRedo = Invoke-GridRequestMutationOperation -Request $executeRedoInput -StoreRoot $store -ScriptsRoot $scriptsRoot
    Assert-Equal 'Applied' $bridgeRedo.tasks[0].repairState.historyState 'Authorized request bridge Redo must refresh task history to Applied.'
    Write-Host 'PASS: assistant mutation review, one-use authorization, Undo, Redo, and refreshed task state are end-to-end.'

    $verifiedPath = Join-Path $root 'case\inputs-verified-specification.v1.json'
    $verifiedProposal = New-GridSkyrimModChainRepairProposal -CaseId verified-fixture -Inspection $inspection -ContextFingerprint ('C' * 64) `
        -EvidenceFingerprint ('D' * 64) -Operations @() -OutputPath $verifiedPath -PassThru
    Assert-Equal 'InputsVerified' $verifiedProposal.status 'A deterministic no-change result must support a zero-operation specification.'
    Assert-Equal 'NotRequired' $verifiedProposal.specification.authorization.status 'A zero-operation specification must not request mutation authority.'
    Assert-True (-not $verifiedProposal.authorizationRequired) 'A zero-operation specification must not request a mutation authorization grant.'

    $diagnosisId = 'diagnosis-fixture'
    $diagnosisTransaction = New-GridCaseStoreTransaction -StoreRoot $store -CaseId $diagnosisId
    Write-GridCaseStoreArtifact -Transaction $diagnosisTransaction -RelativePath 'case.json' -Value ([pscustomobject]@{ schemaVersion = 1; caseId = $diagnosisId; purpose = 'RootCauseDiagnosis'; status = 'RootCauseResolved' }) | Out-Null
    $assetAction = [pscustomobject][ordered]@{ virtualPath = 'meshes\CWI\Architecture\fixture.nif'; action = 'RestoreVerifiedAssetPayload'; providerName = 'Fixture Component'; expectedCurrentState = 'Missing'; postcondition = 'Verified payload is the virtual winner.' }
    $unsignedPatch = [pscustomobject][ordered]@{
        schemaVersion = 1; caseId = $diagnosisId; status = 'Inert'; solutionKind = 'InstallationAssetRepair'; targetPluginName = $null
        records = @(); assetActions = @($assetAction); evidenceFingerprint = ('9' * 64); supportingEvidenceIds = @('root-cause-evidence')
        proposalId = 'proposal-fixture'; writer = [pscustomobject]@{ status = 'Unavailable'; capabilityId = $null }; exclusions = @('No record mutation.')
    }
    $unsignedPatch | Add-Member -NotePropertyName specificationSha256 -NotePropertyValue (Get-GridRootCausePatchSpecificationHash -Specification $unsignedPatch)
    Write-GridCaseStoreArtifact -Transaction $diagnosisTransaction -RelativePath 'patch/conflict-patch-specification.v1.json' -Value $unsignedPatch | Out-Null
    $baselineManifestFileHash = (Get-FileHash -LiteralPath (Join-Path $sealed.CaseDirectory 'case-manifest.v1.json') -Algorithm SHA256).Hash
    Write-GridCaseStoreArtifact -Transaction $diagnosisTransaction -RelativePath 'collector/root-cause-request.v1.json' -Value ([pscustomobject]@{ schemaVersion = 1; caseId = $diagnosisId; baselineManifestSha256 = $baselineManifestFileHash; installationId = 'installation-fixture'; profileId = 'profile-fixture' }) | Out-Null
    $diagnosisRun = [pscustomobject][ordered]@{ schemaVersion = 1; runId = 'run-diagnosis-fixture'; caseId = $diagnosisId; state = 'Completed'; startedAt = [DateTimeOffset]::UtcNow.ToString('o'); completedAt = [DateTimeOffset]::UtcNow.ToString('o'); planFingerprint = ('7' * 64); resourcePolicyVersion = 'fixture'; gates = @(); sufficiency = [pscustomobject]@{ schemaVersion = 1; status = 'SufficientForRequestedDiagnosis' }; checkpoints = @(); primaryFailure = $null; secondaryFailures = @() }
    Seal-GridCaseStoreRun -Transaction $diagnosisTransaction -Run $diagnosisRun | Out-Null
    $diagnosis = Seal-GridDiagnosticCase -Transaction $diagnosisTransaction -SemanticBaselineFingerprint ('8' * 64) -Runs @($diagnosisRun)
    $diagnosisInspection = Get-GridSkyrimModChainRepairInspection -CaseStoreRoot $store -BaselineCaseDirectory $sealed.CaseDirectory -DiagnosisCaseDirectory $diagnosis.CaseDirectory -Components $components -CurrentProtectedState $protectedState -PassThru
    Assert-Equal 'InstallationAssetRepair' $diagnosisInspection.diagnosis.solutionKind 'Inspection must retain an exact sealed asset-only diagnosis binding.'

    $overlaySource = Join-Path $root 'quarantine\expanded\overlay'
    $overlayTarget = Join-Path $mods 'Grid - Fixture Asset Repair'
    $overlayStaging = Join-Path $stageParent 'Grid - Fixture Asset Repair'
    $overlayRollback = Join-Path $rollbackParent 'Grid - Fixture Asset Repair'
    $overlayFile = Join-Path $overlaySource 'meshes\CWI\Architecture\fixture.nif'
    New-Item -ItemType Directory -Path (Split-Path -Parent $overlayFile) -Force | Out-Null
    [IO.File]::WriteAllBytes($overlayFile, [Text.Encoding]::ASCII.GetBytes("Gamebryo File Format, Version 20.2.0.7`nfixture-payload"))
    $overlayHash = (Get-FileHash -LiteralPath $overlayFile -Algorithm SHA256).Hash
    $overlayTree = Get-GridRepairTreeObservation -LiteralPath $overlaySource
    $overlayOperation = [pscustomobject]@{
        sequence = 1; componentId = 'provider:fixture-overlay'; repairStrategy = 'EvidenceBoundOverlay'; beforeTreeState = 'Absent'
        sourceDirectory = $overlaySource; targetDirectory = $overlayTarget; stagingDirectory = $overlayStaging; rollbackDirectory = $overlayRollback
        beforeTreeSha256 = ('0' * 64); expectedTreeSha256 = $overlayTree.treeSha256; baselineEvidenceIds = @('root-cause-evidence'); artifactSha256 = $artifactHash
        sourceToDestinationMappings = @([pscustomobject]@{ source = 'meshes\CWI\Architecture\fixture.nif'; destination = 'meshes\CWI\Architecture\fixture.nif' })
        expectedFiles = @([pscustomobject]@{ path = 'meshes\CWI\Architecture\fixture.nif'; sha256 = $overlayHash })
        virtualAssetPostconditions = @([pscustomobject]@{ virtualPath = 'meshes\CWI\Architecture\fixture.nif'; requiredSha256 = $overlayHash; validationKind = 'Nif' })
    }
    $diagnosisArtifact = @([pscustomobject]@{ schemaVersion = 1; artifactId = "sha256:$artifactHash"; path = $quarantined.path; status = 'Verified'; sizeBytes = 6; sha256 = $artifactHash; identityEvidenceIds = @('artifact-evidence'); observedAt = [DateTimeOffset]::UtcNow.ToString('o'); provenance = [pscustomobject]@{ providerName = 'Fixture Component'; sourceIdentity = 'fixture-provider:file-1' } })
    $diagnosisFomod = @([pscustomobject]@{ schemaVersion = 1; artifactSha256 = $artifactHash; moduleConfigSha256 = ('F' * 64); status = 'NotPresent'; selections = @(); fileMappings = @([pscustomobject]@{ source = 'meshes\CWI\Architecture\fixture.nif'; destination = 'meshes\CWI\Architecture\fixture.nif' }) })
    $overlaySpecPath = Join-Path $root 'case\diagnosis-overlay-specification.v1.json'
    $overlayProposal = New-GridSkyrimModChainRepairProposal -CaseId diagnosis-repair-fixture -Inspection $diagnosisInspection -ContextFingerprint ('C' * 64) -EvidenceFingerprint ('9' * 64) -ModsRoot $mods -Operations @($overlayOperation) -ProtectedState $protectedState `
        -ArtifactIntegrity $diagnosisArtifact -FomodDecisions $diagnosisFomod -CompatibilityMatrix $compatibility -Preconditions @('Diagnosis, source, and absent target remain current') -Postconditions @('Exact NIF is provided by the overlay tree') `
        -ProfilePreservation $protectedState -Exclusions @('Record and plugin mutation') -OutputPath $overlaySpecPath -PassThru
    Assert-Equal 'NotApplicable' $overlayProposal.specification.recordPatch.status 'Asset repair must explicitly reject record patching.'
    Assert-Equal 'EvidenceBoundOverlay' $overlayProposal.specification.operations[0].repairStrategy 'An absent target must remain an explicit overlay operation.'
    Assert-Throws {
        $tampered = Get-Content -LiteralPath $overlaySpecPath -Raw | ConvertFrom-Json
        $tampered.recordPatch.required = $true
        $tampered.specificationSha256 = Get-GridRepairSpecificationHash -Specification $tampered
        $tampered | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $overlaySpecPath -Encoding UTF8
        $tamperedAuth = New-RepairAuthorization -Specification $tampered -StoreRoot $store -Suffix 'tampered'
        Invoke-GridAuthorizedModChainRepair -SpecificationPath $overlaySpecPath -AuthorizationGrantId $tamperedAuth.GrantId -AuthorizationSecret $tamperedAuth.Secret -AuthorizationStoreRoot $store -SemanticBinding $tamperedAuth.Binding -CaseStoreRoot $store -TransactionRoot $transactions -Confirm:$false -PassThru
    } 'UnsupportedRecordPatch' 'The executor must reject a diagnosis-bound record edit even with a recomputed outer digest.'
    Write-GridJsonAtomic -InputObject $overlayProposal.specification -LiteralPath $overlaySpecPath
    $overlayAuth = New-RepairAuthorization -Specification $overlayProposal.specification -StoreRoot $store -Suffix 'overlay'
    $overlayReceipt = Invoke-GridAuthorizedModChainRepair -SpecificationPath $overlaySpecPath -AuthorizationGrantId $overlayAuth.GrantId -AuthorizationSecret $overlayAuth.Secret -AuthorizationStoreRoot $store -SemanticBinding $overlayAuth.Binding -CaseStoreRoot $store -TransactionRoot $transactions -Confirm:$false -PassThru
    Assert-Equal 'InputsRepairedAndVerified' $overlayReceipt.result.summary 'A diagnosis-bound absent overlay must promote and verify.'
    Assert-Equal $overlayHash (Get-FileHash -LiteralPath (Join-Path $overlayTarget 'meshes\CWI\Architecture\fixture.nif') -Algorithm SHA256).Hash 'The promoted virtual NIF must match the diagnosis-bound digest.'
    $journal = @(Get-Content -LiteralPath (Join-Path $transactions ("transaction-$($overlayProposal.specification.specificationSha256)\journal.v1.ndjson")) | ForEach-Object { $_ | ConvertFrom-Json })
    Assert-True (@($journal | Where-Object { $_.operation -eq 'PromoteAndVerify' -and $_.state -eq 'Intent' }).Count -eq 1) 'The journal must flush promotion intent before active-tree mutation.'
    Write-Host 'PASS: sealed diagnosis binding, asset-only semantics, overlay promotion, NIF verification, and intent-before journal are enforced.'

    $containerSource = Join-Path $root 'quarantine\expanded\container-repair'
    $containerTarget = Join-Path $mods 'Fixture Container Repair'
    $containerStage = Join-Path $stageParent 'Fixture Container Repair'
    $containerRollback = Join-Path $rollbackParent 'Fixture Container Repair'
    New-Item -ItemType Directory -Path $containerSource -Force | Out-Null
    $containerPath = Join-Path $containerSource 'Fixture.bsa'
    [IO.File]::WriteAllBytes($containerPath, [Text.Encoding]::ASCII.GetBytes('exact-container-bytes'))
    $containerHash = (Get-FileHash -LiteralPath $containerPath -Algorithm SHA256).Hash
    $containerOperation = [pscustomobject]@{
        sequence = 1; componentId = 'provider:fixture-container'; repairStrategy = 'EvidenceBoundOverlay'; beforeTreeState = 'Absent'
        sourceDirectory = $containerSource; targetDirectory = $containerTarget; stagingDirectory = $containerStage; rollbackDirectory = $containerRollback
        beforeTreeSha256 = ('0' * 64); expectedTreeSha256 = (Get-GridRepairTreeObservation -LiteralPath $containerSource).treeSha256
        baselineEvidenceIds = @('root-cause-evidence'); artifactSha256 = $artifactHash
        sourceToDestinationMappings = @([pscustomobject]@{ source = 'Fixture.bsa'; destination = 'Fixture.bsa' })
        expectedFiles = @([pscustomobject]@{ path = 'Fixture.bsa'; sha256 = $containerHash })
        virtualAssetPostconditions = @([pscustomobject]@{
            virtualPath = 'meshes\CWI\Architecture\fixture.nif'; requiredSha256 = ('1' * 64); validationKind = 'Nif'
            archiveRelativePath = 'Fixture.bsa'; archiveSha256 = $containerHash
            archiveMemberPath = 'meshes\CWI\Architecture\fixture.nif'; assetEvidenceSha256 = ('2' * 64)
        })
    }
    $containerFomod = @([pscustomobject]@{ schemaVersion = 1; artifactSha256 = $artifactHash; moduleConfigSha256 = ('F' * 64); status = 'Deterministic'; selections = @(); fileMappings = @([pscustomobject]@{ source = 'Fixture.bsa'; destination = 'Fixture.bsa' }) })
    $containerSpecPath = Join-Path $root 'case\diagnosis-container-specification.v1.json'
    $containerProposal = New-GridSkyrimModChainRepairProposal -CaseId diagnosis-container-fixture -Inspection $diagnosisInspection -ContextFingerprint ('C' * 64) -EvidenceFingerprint ('9' * 64) -ModsRoot $mods -Operations @($containerOperation) -ProtectedState $protectedState `
        -ArtifactIntegrity $diagnosisArtifact -FomodDecisions $containerFomod -CompatibilityMatrix $compatibility -Preconditions @('Exact container and member evidence remain current') -Postconditions @('Exact container provides the diagnosis-bound member') `
        -ProfilePreservation $protectedState -Exclusions @('Record and plugin mutation') -OutputPath $containerSpecPath -PassThru
    Assert-Equal 'ExactContainerDigest' $containerProposal.specification.operations[0].virtualAssetPostconditions[0].validationBasis 'An archive-contained asset must bind its exact container digest.'
    Assert-Equal $containerHash $containerProposal.specification.operations[0].virtualAssetPostconditions[0].archiveSha256 'The repair specification must retain the exact container digest.'
    Assert-Throws {
        $badOperation = $containerProposal.specification.operations[0].PSObject.Copy()
        $badPostcondition = $badOperation.virtualAssetPostconditions[0].PSObject.Copy()
        $badPostcondition.archiveSha256 = ('0' * 64)
        $badOperation.virtualAssetPostconditions = @($badPostcondition)
        Assert-GridRepairOperationPostconditions -Operation $badOperation -Root $containerSource
    } 'VirtualAssetContainerPostconditionFailed' 'A changed archive container must fail before promotion.'
    Write-Host 'PASS: archive-contained virtual assets bind exact container, member, and collector-evidence digests.'

    [IO.File]::WriteAllText($protected, 'changed', (New-Object Text.UTF8Encoding($false)))
    $drift = Get-GridSkyrimModChainRepairInspection -CaseStoreRoot $store -BaselineCaseDirectory $sealed.CaseDirectory -Components $components -CurrentProtectedState $protectedState -PassThru
    Assert-Equal 'BaselineStale' $drift.status 'Protected drift must prevent repair planning without becoming an external dependency.'
    Assert-Equal 'BaselineStale' $drift.failures[0].code 'Protected drift must identify its exact failed primitive.'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
