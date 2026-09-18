$ErrorActionPreference = 'Stop'
$healthRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $healthRoot 'Grid.Health.psm1') -Force

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }
function Assert-Throws([scriptblock]$Action, [string]$Pattern, [string]$Message) { try { & $Action; throw "$Message Expected '$Pattern'." } catch { if ($_.Exception.Message -notmatch $Pattern) { throw "$Message Unexpected: $($_.Exception.Message)" } } }

$root = Join-Path $env:TEMP ('grid-shared-health-' + [guid]::NewGuid().ToString('N'))
try {
    $envelope = New-GridDiagnosticCase -RawPrompt 'Exact synthetic request.' -OutputRoot $root -GameId 'FixtureGame'
    Assert-True (Test-Path -LiteralPath $envelope.CasePath -PathType Leaf) 'Case must be persisted.'
    Assert-Equal 'Exact synthetic request.' $envelope.Case.rawPrompt 'Raw prompt must be preserved verbatim.'

    $evidence = New-GridEvidenceItem -Parameter 'runtimeAttribution' -Value 1 -Claim 'Synthetic collector resolved ownership.' -SourceType 'DeterministicFixture' -SourceIdentifier 'fixture://one' -ContextFingerprint 'context-a' -VerificationStatus Verified -CollectorName 'FixtureCollector' -CollectorVersion '1.0.0'
    $updated = Add-GridCaseEvidence -CasePath $envelope.CasePath -Evidence $evidence
    Assert-Equal 1 @($updated.evidence).Count 'Validated evidence must append to the case.'
    $fingerprint = Get-GridEvidenceFingerprint -Evidence @($updated.evidence)
    Assert-True ($fingerprint -match '^[A-F0-9]{64}$') 'Evidence fingerprint must be SHA-256.'

    $proposal = New-GridRemediationProposal -CaseId $envelope.Case.caseId -ContextFingerprint 'context-a' -EvidenceFingerprint $fingerprint -ActionType 'FixtureReversibleAction' -Targets @('fixture-target') -SupportingEvidenceIds @($evidence.evidenceId) -ExpectedEffects @('Fixture state changes.') -AuthorizationRequirement 'Explicit approval' -VerificationSteps @('Reobserve fixture.') -BackupRequirement 'Create fixture backup.' -RollbackProcedure 'Restore fixture backup.' -Supported
    $proposalSha = Get-GridRemediationProposalIdentityHash -Proposal $proposal
    $binding = New-GridAuthorizationSemanticBinding -ActorId actor.fixture -SessionId session.fixture -WorkspaceId workspace.fixture -RequestId request.fixture -SubmissionId submission.fixture `
        -EnvelopeSha256 ('A'*64) -PlanSha256 ('B'*64) -ScopeSha256 ('C'*64) -ProposalOrSpecificationId $proposal.proposalId -ProposalOrSpecificationSha256 $proposalSha `
        -Capabilities @([pscustomobject]@{capabilityId='grid.health.remediation.authorize';capabilityVersion='2.0.0';adapterId=$null;adapterVersion=$null}) -Targets @($proposal.targets) -NormalizedInput ([pscustomobject]@{targets=@($proposal.targets)})
    Assert-Throws { Grant-GridProposalAuthorization -Proposal $proposal -CurrentContextFingerprint 'context-b' -CurrentEvidenceFingerprint $fingerprint -AuthorizationStoreRoot $root -SemanticBinding $binding } 'StaleProposal' 'Changed context must invalidate authorization issuance.'
    $issued = Grant-GridProposalAuthorization -Proposal $proposal -CurrentContextFingerprint 'context-a' -CurrentEvidenceFingerprint $fingerprint -AuthorizationStoreRoot $root -SemanticBinding $binding
    Assert-True (-not [string]::IsNullOrWhiteSpace($issued.AuthorizationSecret)) 'Explicit approval must issue a random one-use secret.'
    Assert-Throws { Enter-GridAuthorizationLease -StoreRoot $root -GrantId $issued.Grant.grantId -AuthorizationSecret wrong -ExpectedSemanticBinding $binding -ConsumerId fixture.consumer | Out-Null } 'AuthorizationSecretInvalid' 'A hidden or derived token must not authorize execution.'
    Write-Host 'PASS: case, evidence, fingerprint, stale binding, and random-secret authorization lifecycle.'

    $unresolved = New-GridUnresolvedDiagnosticResult -CaseId $envelope.Case.caseId -State NeedsEvidence -Finding 'Insufficient evidence. No affected subject is verified.' -NextStep 'Continue investigation: collect authoritative evidence.'
    $resultPath = Save-GridDiagnosticResult -DiagnosticResult $unresolved -CaseDirectory $envelope.CaseDirectory
    Assert-True (Test-Path -LiteralPath $resultPath -PathType Leaf) 'Diagnostic result must persist beneath the case directory.'
    $rendered = ConvertTo-GridDiagnosticResultText -DiagnosticResult $unresolved
    Assert-True ($rendered -match '^Affected mod\(s\):') 'Four-field renderer must begin with Affected mod(s).'
    Write-Host 'PASS: unresolved diagnostic result persists and renders honestly.'

    $recorded = [pscustomobject]@{ ProcessId = 42; Name = 'Fixture'; StartTimeUtc = '2026-01-01T00:00:00Z' }
    Assert-True (-not (Test-GridProcessOwnership -RecordedProcess $recorded -CurrentProcess ([pscustomobject]@{ ProcessId = 42; Exists = $true; Name = 'Fixture'; StartTimeUtc = '2026-01-02T00:00:00Z' }))) 'PID reuse with a new start time must not pass ownership.'
    $refusedClose = Close-GridOwnedProcess -Ownership @{ State = 'External'; ProcessId = 1; ExecutablePath = 'C:\Fixture\fixture.exe'; StartTime = [datetime]::UtcNow }
    Assert-Equal 'Refused' $refusedClose.Result 'A process not positively recorded as Grid-owned must be refused before lookup or closure.'
    Write-Host 'PASS: process ownership rejects PID reuse.'
}
finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
