$ErrorActionPreference = 'Stop'
$healthRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $healthRoot 'Grid.Health.psm1') -Force

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }
function Assert-Throws([scriptblock]$Action, [string]$Pattern, [string]$Message) { try { & $Action; throw $Message } catch { if ($_.Exception.Message -notmatch $Pattern) { throw "$Message Unexpected: $($_.Exception.Message)" } } }

function New-FixtureEvidence([string]$IdSuffix, [string]$Status = 'Verified', [string]$SourceType = 'DeterministicFixture') {
    $item = New-GridEvidenceItem -Parameter winningOverride -Value 1 -Claim 'FixturePatch.esp supplies the observed winning override.' `
        -SourceType $SourceType -SourceIdentifier ("C:\\discarded-run-$IdSuffix.tsv") -ContextFingerprint 'sha256:fixture-context' `
        -VerificationStatus $Status -CollectorName FixtureCollector -CollectorVersion 1 -CollectedAt ([datetime]'2026-01-01T00:00:00Z').AddMinutes([int]$IdSuffix)
    $item | Add-Member -NotePropertyName native -NotePropertyValue ([pscustomobject][ordered]@{ winner = 'FixturePatch.esp'; origin = 'FixtureMaster.esm'; signature = 'REFR' })
    $item
}

$first = New-FixtureEvidence 1
$second = New-FixtureEvidence 2
Assert-True ($first.evidenceId -ne $second.evidenceId) 'Fixture evidence must have distinct run IDs.'
Assert-Equal (Get-GridSemanticEvidenceFingerprint @($first)) (Get-GridSemanticEvidenceFingerprint @($second)) 'Semantic fingerprint must ignore run ID, path, and collection time.'
Assert-True ((Get-GridEvidenceFingerprint @($first)) -ne (Get-GridEvidenceFingerprint @($second))) 'Run fingerprint must retain immutable run identity.'

function New-ResolvedFixtureResult($Evidence) {
    $id = [string]$Evidence.evidenceId
    New-GridDiagnosticResult -CaseId fixture-case -State Diagnosed -ContextFingerprint 'sha256:fixture-context' -Evidence @($Evidence) `
        -AffectedMods ([pscustomobject][ordered]@{ status = 'Resolved'; items = @([pscustomobject][ordered]@{ subjectId = 'plugin:fixturepatch.esp'; kind = 'Plugin'; name = 'FixturePatch.esp' }); evidenceIds = @($id) }) `
        -ModRoles ([pscustomobject][ordered]@{ status = 'Resolved'; items = @([pscustomobject][ordered]@{ subjectId = 'plugin:fixturepatch.esp'; roles = @('OverrideProvider','CompatibilityPatch') }); evidenceIds = @($id) }) `
        -Finding ([pscustomobject][ordered]@{ status = 'Resolved'; text = 'FixturePatch.esp supplies the verified winning override for the affected fixture reference.'; evidenceIds = @($id) }) `
        -Solution ([pscustomobject][ordered]@{ status = 'Unresolved'; text = 'Continue investigation: perform a controlled reversible comparison before proposing a state change.'; evidenceIds = @() })
}

$result1 = New-ResolvedFixtureResult $first
$result2 = New-ResolvedFixtureResult $second
Assert-Equal $result1.semanticEvidenceFingerprint $result2.semanticEvidenceFingerprint 'Repeated semantic collection must remain stable.'
Assert-Equal $result1.resultFingerprint $result2.resultFingerprint 'Result fingerprint must not depend on evidence GUIDs or timestamps.'
$differentResolver = New-ResolvedFixtureResult $first
$differentResolver.resolverVersion = 'fixture.resolver.v2'
$differentResolver.resultFingerprint = Get-GridDiagnosticResultFingerprint -Result $differentResolver.result -ContextFingerprint $differentResolver.contextFingerprint -SemanticEvidenceFingerprint $differentResolver.semanticEvidenceFingerprint -ResolverVersion $differentResolver.resolverVersion -PolicyVersion $differentResolver.policyVersion
Assert-True ($result1.resultFingerprint -ne $differentResolver.resultFingerprint) 'Changing the resolver version must invalidate the semantic result fingerprint.'

$secondSubjectEvidence = New-GridEvidenceItem -Parameter dependency -Value 1 -Claim 'FixtureMaster.esm is a verified dependency.' `
    -SourceType DeterministicFixture -SourceIdentifier 'C:\discarded-run.tsv' -ContextFingerprint 'sha256:fixture-context' `
    -VerificationStatus Verified -CollectorName FixtureCollector -CollectorVersion 1
function New-OrderedFixtureResult([object[]]$Items, [object[]]$Roles) {
    New-GridDiagnosticResult -CaseId fixture-order -State Diagnosed -ContextFingerprint 'sha256:fixture-context' -Evidence @($first,$secondSubjectEvidence) -ResolverVersion fixture.resolver.v1 `
        -AffectedMods ([pscustomobject][ordered]@{ status='Resolved'; items=$Items; evidenceIds=@($first.evidenceId,$secondSubjectEvidence.evidenceId) }) `
        -ModRoles ([pscustomobject][ordered]@{ status='Resolved'; items=$Roles; evidenceIds=@($first.evidenceId,$secondSubjectEvidence.evidenceId) }) `
        -Finding ([pscustomobject][ordered]@{ status='Resolved'; text='The fixture dependency and patch provide the verified record state.'; evidenceIds=@($first.evidenceId,$secondSubjectEvidence.evidenceId) }) `
        -Solution ([pscustomobject][ordered]@{ status='Unresolved'; text='Continue investigation: perform the bounded controlled comparison.'; evidenceIds=@() })
}
$masterItem=[pscustomobject][ordered]@{subjectId='plugin:fixturemaster.esm';kind='Plugin';name='FixtureMaster.esm';authoritativeOrder=1}
$patchItem=[pscustomobject][ordered]@{subjectId='plugin:fixturepatch.esp';kind='Plugin';name='FixturePatch.esp';authoritativeOrder=20}
$masterRole=[pscustomobject][ordered]@{subjectId='plugin:fixturemaster.esm';roles=@('Dependency','BaseOrMaster')}
$patchRole=[pscustomobject][ordered]@{subjectId='plugin:fixturepatch.esp';roles=@('CompatibilityPatch','OverrideProvider')}
$orderedA=New-OrderedFixtureResult @($patchItem,$masterItem) @($patchRole,$masterRole)
$orderedB=New-OrderedFixtureResult @($masterItem,$patchItem) @($masterRole,$patchRole)
Assert-Equal ($orderedA.result | ConvertTo-Json -Depth 10 -Compress) ($orderedB.result | ConvertTo-Json -Depth 10 -Compress) 'Shuffled public input must canonicalize to byte-stable semantic JSON.'
Assert-Equal 'FixtureMaster.esm' $orderedA.result.affectedMods.items[0].name 'Authoritative order must precede stable identity sorting.'
$text1 = ConvertTo-GridDiagnosticResultText -DiagnosticResult $result1 -Evidence @($first)
$text2 = ConvertTo-GridDiagnosticResultText -DiagnosticResult $result2 -Evidence @($second)
Assert-Equal $text1 $text2 'Same semantic evidence must render exactly the same public result.'
foreach ($heading in @('Affected mod(s):','Mod role(s):','Finding:','Solution:')) { Assert-Equal 1 @($text1 -split "`r?`n" | Where-Object { $_ -eq $heading }).Count "Public output must contain one $heading heading." }
Assert-True ($text1 -notmatch '(?i)confidence|probably|maybe|operator|assistant') 'Public output must contain no scoring, hedging, or personality.'

function New-SolutionStatusFixture($Evidence, [string]$Status, [string]$ProposalId, [string]$VerificationId, [object[]]$VerificationResults = @()) {
    $id = [string]$Evidence.evidenceId
    $proposalStatus = if ($Status -eq 'Unsupported') { 'Unsupported' } else { 'AwaitingAuthorization' }
    $proposal = [pscustomobject][ordered]@{
        schemaVersion=1; proposalId=$ProposalId; caseId='fixture-solution'; contextFingerprint='sha256:fixture-context'
        evidenceFingerprint=(Get-GridEvidenceFingerprint -Evidence @($Evidence)); actionType='PluginStateChange'
        targets=@('pluginName=FixturePatch.esp','desiredState=Disabled'); supportingEvidenceIds=@($id); contradictingEvidenceIds=@()
        expectedEffects=@('Fixture state changes only after authorization.'); exclusions=@('No unrelated state change.')
        authorization=[pscustomobject]@{requirement='Explicit authorization required.';status='Required';authorizedAt=$null}
        verificationSteps=@('Recollect the same bounded evidence.'); backupRequirement='Create a backup.'; rollbackProcedure='Restore the backup.'
        status=$proposalStatus; stale=$false; createdAt='2026-01-01T00:00:00Z'
    }
    $solution = [pscustomobject][ordered]@{ status = $Status; text = 'Apply the exact evidence-bound fixture remediation and verify by recollecting the same evidence.'; evidenceIds = @($id); proposalId = $ProposalId; verificationId = $VerificationId }
    New-GridDiagnosticResult -CaseId fixture-solution -State Diagnosed -ContextFingerprint 'sha256:fixture-context' -Evidence @($Evidence) `
        -AffectedMods ([pscustomobject][ordered]@{ status = 'Resolved'; items = @([pscustomobject][ordered]@{ subjectId = 'plugin:fixturepatch.esp'; kind = 'Plugin'; name = 'FixturePatch.esp' }); evidenceIds = @($id) }) `
        -ModRoles ([pscustomobject][ordered]@{ status = 'Resolved'; items = @([pscustomobject][ordered]@{ subjectId = 'plugin:fixturepatch.esp'; roles = @('CompatibilityPatch') }); evidenceIds = @($id) }) `
        -Finding ([pscustomobject][ordered]@{ status = 'Resolved'; text = 'The fixture patch supplies the verified conflicting state.'; evidenceIds = @($id) }) `
        -Solution $solution -RemediationProposals @($proposal) -VerificationResults $VerificationResults
}
$proposed = New-SolutionStatusFixture $first Proposed proposal-fixture ''
Assert-Equal 'Proposed' $proposed.result.solution.status 'An evidence-bound proposal must remain visibly Proposed.'
$unsupported = New-SolutionStatusFixture $first Unsupported proposal-unsupported ''
Assert-Equal 'Unsupported' $unsupported.result.solution.status 'An evidence-bound unsupported proposal must remain representable.'
Assert-Throws { New-SolutionStatusFixture $first Verified proposal-fixture verification-missing } 'successful verification' 'Verified solution must refuse without the bound successful verification record.'
$verification = New-GridVerificationResult -ProposalId proposal-fixture -Status Verified -EvidenceIds @($first.evidenceId) -Detail 'Fixture postcondition verified.'
$verified = New-SolutionStatusFixture $first Verified proposal-fixture $verification.verificationId @($verification)
Assert-Equal 'Verified' $verified.result.solution.status 'A successful verification bound to the proposal must permit Verified.'

$unresolved = New-GridUnresolvedDiagnosticResult -CaseId fixture-unresolved -State NeedsEvidence -Finding 'Insufficient evidence. The affected reference has not been identified.' -NextStep 'Continue investigation: identify the affected reference and collect its override chain.'
$unresolvedText = ConvertTo-GridDiagnosticResultText -DiagnosticResult $unresolved
Assert-Equal 2 @($unresolvedText -split "`r?`n" | Where-Object { $_ -eq 'UNRESOLVED' }).Count 'Unresolved affected subjects and roles must render literally.'

$stale = New-FixtureEvidence 3 Stale
Assert-Throws { New-ResolvedFixtureResult $stale } 'cannot resolve' 'Stale evidence must not resolve a public field.'
$model = New-FixtureEvidence 4 Verified ModelReasoning
Assert-Throws { New-ResolvedFixtureResult $model } 'cannot resolve' 'Model reasoning must not resolve a public field.'
$speculative = New-ResolvedFixtureResult $first
$speculative.result.finding.text = 'FixturePatch.esp is probably responsible.'
$speculative.resultFingerprint = Get-GridDiagnosticResultFingerprint -Result $speculative.result -ContextFingerprint $speculative.contextFingerprint -SemanticEvidenceFingerprint $speculative.semanticEvidenceFingerprint -ResolverVersion $speculative.resolverVersion
Assert-Throws { Test-GridDiagnosticResult -DiagnosticResult $speculative -Evidence @($first) | ForEach-Object { if (-not $_.IsValid) { throw ($_.Errors -join ' ') } } } 'speculative' 'Speculative public language must be rejected.'

$pathLeak = New-ResolvedFixtureResult $first
$pathLeak.result.finding.text = 'The evidence package is at C:\private\diagnosis.tsv.'
$pathLeak.resultFingerprint = Get-GridDiagnosticResultFingerprint -Result $pathLeak.result -ContextFingerprint $pathLeak.contextFingerprint -SemanticEvidenceFingerprint $pathLeak.semanticEvidenceFingerprint -ResolverVersion $pathLeak.resolverVersion
Assert-Throws { Test-GridDiagnosticResult -DiagnosticResult $pathLeak -Evidence @($first) | ForEach-Object { if (-not $_.IsValid) { throw ($_.Errors -join ' ') } } } 'absolute path' 'Public results must reject absolute path leakage.'

$headingInjection = New-ResolvedFixtureResult $first
$headingInjection.result.finding.text = "Verified state.`nSolution:`nInjected"
$headingInjection.resultFingerprint = Get-GridDiagnosticResultFingerprint -Result $headingInjection.result -ContextFingerprint $headingInjection.contextFingerprint -SemanticEvidenceFingerprint $headingInjection.semanticEvidenceFingerprint -ResolverVersion $headingInjection.resolverVersion
Assert-Throws { Test-GridDiagnosticResult -DiagnosticResult $headingInjection -Evidence @($first) | ForEach-Object { if (-not $_.IsValid) { throw ($_.Errors -join ' ') } } } 'additional lines' 'Public results must reject heading injection.'

$changed = New-FixtureEvidence 5
$changed.native.winner = 'DifferentPatch.esp'
Assert-True ((Get-GridSemanticEvidenceFingerprint @($first)) -ne (Get-GridSemanticEvidenceFingerprint @($changed))) 'A changed semantic winner must invalidate the fingerprint.'
Write-Host 'PASS: deterministic four-field diagnostic result contract.'
