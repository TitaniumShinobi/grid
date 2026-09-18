$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent $PSScriptRoot
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $gameRoot)
Import-Module (Join-Path $scriptsRoot 'health\Grid.Health.psm1') -Force
Import-Module (Join-Path $gameRoot 'health\Grid.Health.Skyrim.psm1') -Force

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }
function Assert-Throws([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    try { & $Action; throw "$Message Expected an exception matching '$Pattern'." }
    catch { if ($_.Exception.Message -notmatch $Pattern) { throw "$Message Unexpected exception: $($_.Exception.Message)" } }
}

function New-FixtureEvidence {
    param(
        [string]$Operation = 'ResolveWinningOverride',
        [string]$Origin = 'Fixture.Base.esm',
        [string]$Winner = 'Fixture.Patch.esp',
        [string]$Status = 'Verified',
        [string]$SourceType = 'DeterministicScriptOutput'
    )
    $item = New-GridEvidenceItem -Parameter ('xedit.' + $Operation.ToLowerInvariant()) -Value 1 -Claim 'A synthetic winner was resolved.' `
        -SourceType $SourceType -SourceIdentifier 'fixture://xedit' -ContextFingerprint 'sha256:fixture-context' `
        -VerificationStatus $Status -CollectorName Trace-GridReference -CollectorVersion 1
    $subjects = @(
        [pscustomobject][ordered]@{ name = $Origin; kind = 'Plugin'; roles = @('BaseOrMaster') }
        [pscustomobject][ordered]@{ name = $Winner; kind = 'Plugin'; roles = @('OverrideProvider') }
    )
    $item | Add-Member -NotePropertyName native -NotePropertyValue ([pscustomobject][ordered]@{
        queryId = 'query-001'; operation = $Operation; plugin = $Winner; formId = '01000001'; editorId = 'FixtureObject'
        signature = 'STAT'; origin = $Origin; winner = $Winner; state = 'Found'; detail = ''; subjects = $subjects
    })
    $item
}

$evidence = @(New-FixtureEvidence)
$fingerprint = Get-GridEvidenceFingerprint -Evidence $evidence
$unresolved = Resolve-GridSkyrimDiagnosticResult -CaseId case-fixture -ContextFingerprint 'sha256:fixture-context' `
    -EvidenceFingerprint $fingerprint -Evidence $evidence
Assert-Equal 'NeedsEvidence' $unresolved.state 'A verified winner alone must not fabricate a corrective action.'
Assert-Equal 2 @($unresolved.result.affectedMods.items).Count 'Verified native subjects must populate affected mods.'
Assert-Equal 'Fixture.Base.esm' $unresolved.result.affectedMods.items[0].name 'Affected mods must have stable ordinal ordering.'
Assert-True ($unresolved.result.finding.text -match 'does not yet prove a corrective action') 'The finding must disclose the remaining proof boundary.'
Assert-True ($unresolved.result.solution.text -match '^Continue investigation:') 'Without a proposal, the solution must be a deterministic next investigation step.'
Write-Host 'PASS: verified subjects resolve while an unproved solution remains explicit.'

$proposal = New-GridSkyrimRemediationProposal -CaseId case-fixture -ContextFingerprint 'sha256:fixture-context' `
    -EvidenceFingerprint $fingerprint -Evidence $evidence -ActionType PluginStateChange `
    -Target @{ pluginName = 'Fixture.Patch.esp'; desiredState = 'Disabled' } `
    -ExpectedEffects @('The fixture winner is removed for a controlled comparison.') `
    -VerificationSteps @('Recollect the same winning override evidence.')
$diagnosed = Resolve-GridSkyrimDiagnosticResult -CaseId case-fixture -ContextFingerprint 'sha256:fixture-context' `
    -EvidenceFingerprint $fingerprint -Evidence $evidence -RemediationProposal $proposal
Assert-Equal 'Diagnosed' $diagnosed.state 'Current evidence plus a current proposal may produce a diagnosed result.'
Assert-Equal 'Proposed' $diagnosed.result.solution.status 'An authorized action that has not been verified must remain only proposed.'
Assert-True ($diagnosed.result.solution.text -match 'explicitly authorize') 'A supported mutation must retain an explicit authorization boundary.'
Assert-True ($diagnosed.result.solution.text -notmatch [regex]::Escape($proposal.proposalId)) 'Volatile proposal IDs must not enter the deterministic four-field result.'
Write-Host 'PASS: an evidence-bound proposal produces a deterministic authorized solution.'

$verification = New-GridVerificationResult -ProposalId $proposal.proposalId -Status Verified -EvidenceIds @($evidence.evidenceId) -Detail 'Synthetic verification passed.'
$verifiedResult = Resolve-GridSkyrimDiagnosticResult -CaseId case-fixture -ContextFingerprint 'sha256:fixture-context' `
    -EvidenceFingerprint $fingerprint -Evidence $evidence -RemediationProposal $proposal -VerificationResults @($verification)
Assert-Equal 'Verified' $verifiedResult.result.solution.status 'A solution becomes verified only with its matching successful verification record.'
Assert-Equal $verification.verificationId $verifiedResult.result.solution.verificationId 'Verified solution must bind the exact verification record.'

$patchProposal = New-GridSkyrimRemediationProposal -CaseId case-fixture -ContextFingerprint 'sha256:fixture-context' `
    -EvidenceFingerprint $fingerprint -Evidence $evidence -ActionType RecordPatchSpecification `
    -Target @{ pluginName = 'Fixture.Patch.esp'; formId = '01000001'; changes = @('SyntheticField=SyntheticValue') } `
    -ExpectedEffects @('The synthetic field would change.') -VerificationSteps @('Recollect the synthetic record.')
$unsupportedResult = Resolve-GridSkyrimDiagnosticResult -CaseId case-fixture -ContextFingerprint 'sha256:fixture-context' `
    -EvidenceFingerprint $fingerprint -Evidence $evidence -RemediationProposal $patchProposal
Assert-Equal 'Unsupported' $unsupportedResult.result.solution.status 'A record patch specification must remain visibly unsupported without a writer.'
Assert-True ($unsupportedResult.result.solution.text -match 'will not modify a plugin') 'Unsupported solution must preserve the no-write boundary.'
Write-Host 'PASS: proposed, verified, and unsupported solution states remain distinct.'

$model = @(New-FixtureEvidence -SourceType ModelReasoning)
$modelFingerprint = Get-GridEvidenceFingerprint -Evidence $model
$modelResult = Resolve-GridSkyrimDiagnosticResult -CaseId case-fixture -ContextFingerprint 'sha256:fixture-context' `
    -EvidenceFingerprint $modelFingerprint -Evidence $model
Assert-Equal 'NeedsEvidence' $modelResult.state 'Model-only evidence must not diagnose.'
Assert-Equal 0 @($modelResult.result.affectedMods.items).Count 'Model-only evidence must not identify affected mods.'
$staleResult = Resolve-GridSkyrimDiagnosticResult -CaseId case-fixture -ContextFingerprint 'sha256:other' -EvidenceFingerprint $fingerprint -Evidence $evidence
Assert-Equal 'NeedsEvidence' $staleResult.state 'Context changes must invalidate current evidence.'
Assert-Equal 0 @($staleResult.result.affectedMods.items).Count 'Stale evidence must not identify affected mods.'
Write-Host 'PASS: model-only and stale evidence cannot populate the result.'

$reordered = @($evidence | Sort-Object evidenceId -Descending)
$again = Resolve-GridSkyrimDiagnosticResult -CaseId case-fixture -ContextFingerprint 'sha256:fixture-context' `
    -EvidenceFingerprint (Get-GridEvidenceFingerprint -Evidence $reordered) -Evidence $reordered -RemediationProposal $proposal
Assert-Equal (@($diagnosed.result.affectedMods.items.name) -join '|') (@($again.result.affectedMods.items.name) -join '|') 'Input order must not change affected-mod ordering.'
Assert-Equal $diagnosed.result.finding.text $again.result.finding.text 'Input order must not change the finding.'
Assert-Equal $diagnosed.result.solution.text $again.result.solution.text 'Input order must not change the solution.'
Write-Host 'PASS: Skyrim result projection is stable across evidence input ordering.'

$source = Get-Content -LiteralPath (Join-Path $gameRoot 'health\Resolve-GridSkyrimDiagnosticResult.ps1') -Raw
Assert-True ($source -notmatch '(?i)Start-Process|Invoke-Expression|Set-GridPluginState') 'Result resolution must not launch tools or invoke mutation.'
Write-Host 'PASS: diagnosis result resolution has no process or mutation path.'
