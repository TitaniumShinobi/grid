$ErrorActionPreference = 'Stop'
$healthRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $healthRoot 'Grid.Health.psm1') -Force

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }
function Assert-Throws([scriptblock]$Action, [string]$Pattern, [string]$Message) { try { & $Action; throw $Message } catch { if ($_.Exception.Message -notmatch $Pattern) { throw "$Message Unexpected: $($_.Exception.Message)" } } }

$context = 'sha256:assessment-fixture'
$observed = New-GridEvidenceItem -Parameter windowsApplicationFailureEvent -Value 1 -Claim 'A fixture application event was recorded.' `
    -SourceType DeterministicFixture -SourceIdentifier fixture://event -ContextFingerprint $context -VerificationStatus Verified `
    -CollectorName FixtureCollector -CollectorVersion 1.0.0
$other = New-GridEvidenceItem -Parameter executableIdentity -Value 1 -Claim 'The fixture executable identity was observed.' `
    -SourceType DeterministicFixture -SourceIdentifier fixture://executable -ContextFingerprint $context -VerificationStatus Collected `
    -CollectorName FixtureCollector -CollectorVersion 1.0.0
$evidence = @($observed,$other)

$notRequired = [pscustomobject][ordered]@{ status='NotRequired'; collectorCapabilityId=$null; reason=''; requiredInputs=@() }
$direct = [pscustomobject][ordered]@{
    assertionId='assertion-observed'; classification='ObservedFact'; statement='The fixture event exists in the bounded source.'
    evidenceIds=@($observed.evidenceId); contradictingEvidenceIds=@(); missingInputs=@(); ruleId='grid.assessment.direct-observation.v1'; satisfiedProofObligationIds=@()
}
$directAssessment = New-GridDiagnosticAssessment -CaseId fixture-case -ContextFingerprint $context -Evidence $evidence -Assertions @($direct) `
    -RegisteredRules @() -NextEvidenceDecision $notRequired -AssessorName FixtureAssessment -AssessorVersion 1.0.0
Assert-Equal EvidenceAssessed $directAssessment.state 'A direct observation must not become a causal conclusion.'
Assert-True ((Test-GridDiagnosticAssessment -Assessment $directAssessment -Evidence $evidence).IsValid) 'The direct observation assessment must validate.'

$insufficient = [pscustomobject][ordered]@{
    assertionId='assertion-insufficient'; classification='InsufficientEvidence'; statement='The retained evidence does not associate the event with the reported occurrence.'
    evidenceIds=@($observed.evidenceId); contradictingEvidenceIds=@(); missingInputs=@('occurrenceCorrelation'); ruleId=$null; satisfiedProofObligationIds=@()
}
$next = [pscustomobject][ordered]@{
    status='Required'; collectorCapabilityId='grid.health.windows-error-reporting.collect'
    reason='Collect the next bounded source needed for occurrence correlation.'; requiredInputs=@('exactExecutableIdentity','boundedTimeRange')
}
$needsEvidence = New-GridDiagnosticAssessment -CaseId fixture-case -ContextFingerprint $context -Evidence $evidence -Assertions @($direct,$insufficient) `
    -RegisteredRules @() -NextEvidenceDecision $next -AssessorName FixtureAssessment -AssessorVersion 1.0.0
Assert-Equal InsufficientEvidence $needsEvidence.state 'Missing correlation must remain insufficient evidence.'

$deriveRule = [pscustomobject][ordered]@{
    ruleId='grid.fixture.event-identity.derive'; version='1.0.0'; kind='DeterministicDerivation'; source='ProductionManifest'
    requiredEvidenceParameters=@('windowsApplicationFailureEvent','executableIdentity'); proofObligations=@()
}
$derived = [pscustomobject][ordered]@{
    assertionId='assertion-derived'; classification='DerivedFact'; statement='The event and executable observations share the bound fixture context.'
    evidenceIds=@($observed.evidenceId,$other.evidenceId); contradictingEvidenceIds=@(); missingInputs=@(); ruleId=$deriveRule.ruleId; ruleVersion=$deriveRule.version; satisfiedProofObligationIds=@()
}
$derivedAssessment = New-GridDiagnosticAssessment -CaseId fixture-case -ContextFingerprint $context -Evidence $evidence -Assertions @($derived) `
    -RegisteredRules @($deriveRule) -NextEvidenceDecision $notRequired -AssessorName FixtureAssessment -AssessorVersion 1.0.0
Assert-Equal EvidenceAssessed $derivedAssessment.state 'A deterministic derivation must remain non-causal.'

$hypothesisRule = [pscustomobject][ordered]@{
    ruleId='grid.fixture.temporal-association.hypothesis'; version='1.0.0'; kind='HypothesisSupport'; source='ProductionManifest'
    requiredEvidenceParameters=@('windowsApplicationFailureEvent'); proofObligations=@()
}
$hypothesis = [pscustomobject][ordered]@{
    assertionId='assertion-hypothesis'; classification='SupportedHypothesis'; statement='The fixture event supports testing a bounded temporal association.'
    evidenceIds=@($observed.evidenceId); contradictingEvidenceIds=@($other.evidenceId); missingInputs=@('controlledComparison'); ruleId=$hypothesisRule.ruleId; ruleVersion=$hypothesisRule.version; satisfiedProofObligationIds=@()
}
$hypothesisAssessment = New-GridDiagnosticAssessment -CaseId fixture-case -ContextFingerprint $context -Evidence $evidence -Assertions @($hypothesis) `
    -RegisteredRules @($hypothesisRule) -NextEvidenceDecision $notRequired -AssessorName FixtureAssessment -AssessorVersion 1.0.0
Assert-Equal SupportedHypothesis $hypothesisAssessment.assertions[0].classification 'A supported hypothesis must remain visibly distinct from fact and cause.'

$proofRule = [pscustomobject][ordered]@{
    ruleId='grid.fixture.causal-proof'; version='1.0.0'; kind='CausalProof'; source='ProductionManifest'
    requiredEvidenceParameters=@('windowsApplicationFailureEvent','executableIdentity')
    proofObligations=@([pscustomobject]@{obligationId='controlled-reproduction';requiredEvidenceParameters=@('windowsApplicationFailureEvent','executableIdentity')})
}
$unproven = [pscustomobject][ordered]@{
    assertionId='assertion-cause'; classification='ProvenCause'; statement='The fixture condition is causal.'
    evidenceIds=@($observed.evidenceId,$other.evidenceId); contradictingEvidenceIds=@(); missingInputs=@(); ruleId=$proofRule.ruleId; ruleVersion=$proofRule.version; satisfiedProofObligationIds=@()
}
Assert-Throws {
    New-GridDiagnosticAssessment -CaseId fixture-case -ContextFingerprint $context -Evidence $evidence -Assertions @($unproven) `
        -RegisteredRules @($proofRule) -NextEvidenceDecision $notRequired -AssessorName FixtureAssessment -AssessorVersion 1.0.0 | Out-Null
} 'not satisfied proof obligation' 'A causal label must fail closed until every registered proof obligation is satisfied.'

$proven = $unproven.PSObject.Copy()
$proven.satisfiedProofObligationIds = @('controlled-reproduction')
$provenAssessment = New-GridDiagnosticAssessment -CaseId fixture-case -ContextFingerprint $context -Evidence $evidence -Assertions @($proven) `
    -RegisteredRules @($proofRule) -NextEvidenceDecision $notRequired -AssessorName FixtureAssessment -AssessorVersion 1.0.0
Assert-Equal CauseProven $provenAssessment.state 'Only a fully satisfied registered causal proof may produce CauseProven.'

$modelEvidence = New-GridEvidenceItem -Parameter modelOpinion -Value 1 -Claim 'A model supplied an opinion.' -SourceType ModelReasoning `
    -SourceIdentifier fixture://model -ContextFingerprint $context -VerificationStatus Verified -CollectorName FixtureModel -CollectorVersion 1.0.0
$modelAssertion = $direct.PSObject.Copy()
$modelAssertion.assertionId = 'assertion-model'
$modelAssertion.evidenceIds = @($modelEvidence.evidenceId)
Assert-Throws {
    New-GridDiagnosticAssessment -CaseId fixture-case -ContextFingerprint $context -Evidence @($modelEvidence) -Assertions @($modelAssertion) `
        -RegisteredRules @() -NextEvidenceDecision $notRequired -AssessorName FixtureAssessment -AssessorVersion 1.0.0 | Out-Null
} 'model reasoning' 'Model output must never be diagnostic authority.'

Write-Host 'PASS: observed, derived, hypothesized, insufficient, and proven-cause assessment boundaries.'
