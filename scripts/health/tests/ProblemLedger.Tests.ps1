$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
. (Join-Path $root 'scripts\health\Grid.ProblemLedger.ps1')
. (Join-Path $root 'scripts\health\Grid.CaseStore.ps1')

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }
function Assert-Throws([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    try { & $Action; throw "$Message No exception was raised." }
    catch { if ($_.Exception.Message -notmatch $Pattern) { throw "$Message Actual: $($_.Exception.Message)" } }
}

$contextA = 'A' * 64
$contextB = 'B' * 64
$ledger = New-GridProblemLedger -GameId 'game.fixture' -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' -ContextFingerprint $contextA -LedgerId 'problem-ledger.fixture'
Add-GridProblemClaim -Ledger $ledger -IssueId 'problem.input' -Claim 'Input stops responding after switching programs.' -DesiredOutcome 'Input continues after focus is restored.' | Out-Null
Assert-Equal 'Claimed' $ledger.entries[0].state 'A user claim must remain a claim before deterministic collection.'

Set-GridProblemState -Ledger $ledger -IssueId 'problem.input' -State IntakeBound -AuthorityType RequestPlanner -AuthorityId 'request-plan.fixture' -ContextFingerprint $contextA -ClassId 'grid.class.ui-hud-input' -CapabilityIds @('grid.fixture.input') | Out-Null
Set-GridProblemState -Ledger $ledger -IssueId 'problem.input' -State NeedsEvidence -AuthorityType Collector -AuthorityId 'collector.fixture' -ContextFingerprint $contextA | Out-Null
Assert-Throws { Set-GridProblemState -Ledger $ledger -IssueId 'problem.input' -State Diagnosed -AuthorityType Resolver -AuthorityId 'resolver.fixture' -ContextFingerprint $contextA } 'EvidenceRequired' 'Diagnosis must not advance without evidence.'
Set-GridProblemState -Ledger $ledger -IssueId 'problem.input' -State Diagnosed -AuthorityType Resolver -AuthorityId 'resolver.fixture' -ContextFingerprint $contextA -EvidenceIds @('evidence.fixture.diagnosis') | Out-Null
Set-GridProblemState -Ledger $ledger -IssueId 'problem.input' -State SolutionProposed -AuthorityType Proposal -AuthorityId 'proposal.fixture' -ContextFingerprint $contextA -EvidenceIds @('evidence.fixture.solution') -ProposalId 'proposal.fixture' | Out-Null
Assert-Throws { Set-GridProblemState -Ledger $ledger -IssueId 'problem.input' -State Applied -AuthorityType Executor -AuthorityId 'executor.fixture' -ContextFingerprint $contextA -EvidenceIds @('evidence.fixture.execution') -ExecutionReceiptId 'receipt.fixture' } 'TransitionRefused' 'A proposal must not skip authorization.'
Set-GridProblemState -Ledger $ledger -IssueId 'problem.input' -State Authorized -AuthorityType Authorization -AuthorityId 'grant.fixture' -ContextFingerprint $contextA -EvidenceIds @('evidence.fixture.authorization') -AuthorizationId 'grant.fixture' | Out-Null
Set-GridProblemState -Ledger $ledger -IssueId 'problem.input' -State Applied -AuthorityType Executor -AuthorityId 'executor.fixture' -ContextFingerprint $contextA -EvidenceIds @('evidence.fixture.execution') -ExecutionReceiptId 'receipt.fixture' | Out-Null
Assert-Throws { Set-GridProblemState -Ledger $ledger -IssueId 'problem.input' -State Resolved -AuthorityType Resolver -AuthorityId 'resolver.fixture' -ContextFingerprint $contextA -EvidenceIds @('evidence.fixture.verify') -VerificationId 'verify.fixture' } 'AuthorityRefused' 'Only verification authority can resolve a problem.'
Set-GridProblemState -Ledger $ledger -IssueId 'problem.input' -State Resolved -AuthorityType Verifier -AuthorityId 'verify.fixture' -ContextFingerprint $contextA -EvidenceIds @('evidence.fixture.verify') -VerificationId 'verify.fixture' | Out-Null
Assert-Equal 'Resolved' $ledger.entries[0].state 'A verified lifecycle may resolve the claim.'

Update-GridProblemLedgerContext -Ledger $ledger -ContextFingerprint $contextB -DriftEvidenceId 'evidence.fixture.context-drift' | Out-Null
Assert-Equal 'NeedsEvidence' $ledger.entries[0].state 'A context change must reopen a resolved result.'
Assert-True ($ledger.entries[0].evidenceIds -contains 'evidence.fixture.context-drift') 'Context-drift evidence must remain attached to the reopened entry.'
$contextC = 'C' * 64
Update-GridProblemLedgerContext -Ledger $ledger -ContextFingerprint $contextC -DriftEvidenceId 'evidence.fixture.second-context-drift' | Out-Null
Assert-Equal 'NeedsEvidence' $ledger.entries[0].state 'Repeated context drift must keep an unresolved problem open without refusing the refresh.'
Assert-True ($ledger.entries[0].evidenceIds -contains 'evidence.fixture.second-context-drift') 'Repeated drift evidence must remain attached.'
$validation = Test-GridProblemLedger -Ledger $ledger
Assert-True $validation.IsValid ('The projected problem ledger must validate: ' + ($validation.Errors -join ' '))

$storeRoot = Join-Path ([IO.Path]::GetTempPath()) ('grid-problem-ledger-test-' + [Guid]::NewGuid().ToString('N'))
try {
    $transaction = New-GridCaseStoreTransaction -StoreRoot $storeRoot -CaseId 'case-problem-ledger-fixture'
    $artifact = Write-GridProblemLedgerArtifact -Transaction $transaction -Ledger $ledger
    Assert-Equal 'diagnosis/problem-ledger.v1.json' $artifact.path 'The ledger must use the canonical case artifact path.'
    Assert-True (Test-Path -LiteralPath (Join-Path $transaction.CaseDirectory $artifact.path) -PathType Leaf) 'The case transaction must contain the ledger artifact.'
}
finally {
    if (Test-Path -LiteralPath $storeRoot) { Remove-Item -LiteralPath $storeRoot -Recurse -Force }
}

$ledger.entries[0].claim = 'tampered'
$tampered = Test-GridProblemLedger -Ledger $ledger
Assert-True (-not $tampered.IsValid -and $tampered.Errors -contains 'Problem ledger fingerprint mismatch.') 'Semantic tampering must invalidate the ledger fingerprint.'
Assert-Throws { Add-GridProblemClaim -Ledger $ledger -IssueId 'problem.after-tamper' -Claim 'Must not append.' -DesiredOutcome 'Mutation refused.' } 'IntegrityRefused' 'A tampered ledger must refuse further mutation.'

$envelope = [pscustomobject][ordered]@{
    requestId='request.fixture'; context=[pscustomobject]@{gameId='game.fixture';installationId='installation.fixture';profileId='profile.fixture'}
    class=[pscustomobject]@{classId='grid.class.fixture'}; claims=[pscustomobject]@{text='A fixture is broken.'}
}
$requestPlan = [pscustomobject]@{capabilityBindings=@([pscustomobject]@{capabilityId='grid.fixture.inspect'})}
$intake = [pscustomobject]@{problem='A fixture is broken.';expectedBehavior='The fixture works.';desiredOutcome='Restore the fixture.'}
$evidenceResult = [pscustomobject]@{resultKind='Evidence';terminalState='EvidenceComplete';evidenceIds=@('evidence.fixture.request')}
$requestLedger = New-GridRequestProblemLedger -Envelope $envelope -RequestPlan $requestPlan -InvestigationIntake $intake -Result $evidenceResult -ContextFingerprint $contextA
Assert-Equal 'NeedsEvidence' $requestLedger.entries[0].state 'Evidence collection must not be projected as diagnosis.'
Assert-Equal 'A fixture is broken.' $requestLedger.entries[0].claim 'The structured intake claim must be preserved verbatim.'
Assert-Equal 'Restore the fixture.' $requestLedger.entries[0].desiredOutcome 'The structured desired outcome must be preserved verbatim.'
$diagnosticResult = [pscustomobject]@{resultKind='Diagnostic';terminalState='Diagnosed';evidenceIds=@('evidence.fixture.diagnostic')}
$diagnosedLedger = New-GridRequestProblemLedger -Envelope $envelope -RequestPlan $requestPlan -InvestigationIntake $intake -Result $diagnosticResult -ParentLedger $requestLedger -ContextFingerprint $contextA
Assert-Equal 'Diagnosed' $diagnosedLedger.entries[0].state 'Only an evidence-backed deterministic Diagnostic result may project diagnosis.'

$intakeStoreRoot = Join-Path ([IO.Path]::GetTempPath()) ('grid-problem-intake-test-' + [Guid]::NewGuid().ToString('N'))
try {
    $intakeCase = New-GridProblemLedgerCase -StoreRoot $intakeStoreRoot -GameId 'game.fixture' -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' -ContextFingerprint $contextA -CaseId 'problem-intake-fixture' -Claims @(
        [pscustomobject]@{claim='First user claim.';desiredOutcome='First desired outcome.'},
        [pscustomobject]@{claim='Second user claim.';desiredOutcome='Second desired outcome.'}
    )
    Assert-Equal 'ClaimsRecorded' $intakeCase.Status 'Problem intake must report only that claims were recorded.'
    Assert-Equal 2 @($intakeCase.Ledger.entries).Count 'Problem intake must preserve every supplied claim.'
    Assert-Equal 2 @($intakeCase.Ledger.entries|Where-Object state -eq 'Claimed').Count 'Intake must not diagnose user claims.'
    Assert-True (Test-GridDiagnosticCaseSeal -StoreRoot $intakeStoreRoot -CaseDirectory $intakeCase.CaseDirectory).IsValid 'Problem intake must seal a valid immutable case.'
}
finally {
    if (Test-Path -LiteralPath $intakeStoreRoot) { Remove-Item -LiteralPath $intakeStoreRoot -Recurse -Force }
}
'Problem ledger checks passed.'
