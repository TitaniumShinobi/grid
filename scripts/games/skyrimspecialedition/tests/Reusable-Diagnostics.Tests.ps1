$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent $PSScriptRoot
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $gameRoot)
Import-Module (Join-Path $scriptsRoot 'health\Grid.Health.psm1') -Force
Import-Module (Join-Path $gameRoot 'health\Grid.Health.Skyrim.psm1') -Force
. (Join-Path $gameRoot 'health\collectors\New-GridXEditQuery.ps1')
. (Join-Path $gameRoot 'health\collectors\Get-GridXEditEvidence.ps1')
. (Join-Path $gameRoot 'health\collectors\Resolve-GridSkyrimPlacedActorReference.ps1')
. (Join-Path $gameRoot 'health\collectors\Get-GridSkyrimSaveCreatedActorEvidence.ps1')
. (Join-Path $gameRoot 'health\actions\New-GridSkyrimRemediationProposal.ps1')

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }
function Assert-Throws([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    try { & $Action; throw "$Message Expected an exception matching '$Pattern'." }
    catch { if ($_.Exception.Message -notmatch $Pattern) { throw "$Message Unexpected exception: $($_.Exception.Message)" } }
}

$tempRoot = Join-Path $env:TEMP ('grid-reusable-diagnostics-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
try {
    Assert-True ($null -ne ('Grid.SkyrimSave.CreatedActorReader' -as [type])) 'The Skyrim save collector must compile in Windows PowerShell.'
    Write-Host 'PASS: Skyrim save created-actor collector compiles against the host framework.'

    $emptyPlan = [pscustomobject]@{ status = 'NeedsEvidence'; candidatePlugins = @(); observedForms = @(); collectorQueries = @() }
    $adapterResult = Invoke-GridSkyrimHealthAdapter -Request 'A reported object is absent.' -CaseId 'case-empty' -CaseDirectory $tempRoot -InvestigationPlan $emptyPlan
    Assert-Equal 'NeedsEvidence' $adapterResult.Status 'Adapter must return an honest state rather than matching a case recipe.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $tempRoot 'diagnosis.md'))) 'The adapter must leave the public four-field rendering to the shared dispatcher.'
    Write-Host 'PASS: generic adapter refuses to invent technical evidence from prose.'

    $query = New-GridXEditQuery -Queries @(
        [pscustomobject]@{ operation = 'FindRecordByEditorId'; plugin = 'Fixture.esp'; editorId = 'FixtureObject'; formId = '' }
        [pscustomobject]@{ operation = 'ResolveWinningOverride'; plugin = 'Fixture.esp'; editorId = 'FixtureObject'; formId = '' }
    ) -CaseDirectory $tempRoot -ContextFingerprint 'sha256:current'
    $report = Join-Path $tempRoot 'xedit-evidence.tsv'
    @(
        @('QueryId','Operation','Claim','Plugin','FormId','EditorId','Signature','Origin','Winner','State','Detail') -join [char]9
        @('query-001','FindRecordByEditorId','Record found.','Fixture.esp','01000001','FixtureObject','STAT','Fixture.esm','Fixture.esp','Found','') -join [char]9
        @('query-002','ResolveWinningOverride','Winner resolved.','Fixture.esp','01000001','FixtureObject','STAT','Fixture.esm','Fixture.esp','Found','') -join [char]9
    ) | Set-Content -LiteralPath $report -Encoding UTF8
    $evidence = Get-GridXEditEvidence -ReportPath $report -QueryPackage $query -CaseDirectory $tempRoot -ContextFingerprint 'sha256:current'
    Assert-Equal 2 $evidence.RowCount 'Evidence normalizer must preserve verified rows.'
    Assert-True (@($evidence.Evidence | Where-Object verificationStatus -eq 'Verified').Count -eq 2) 'Every normalized collector row must be verified.'
    $winnerEvidence = @($evidence.Evidence | Where-Object { $_.native.operation -eq 'ResolveWinningOverride' })[0]
    Assert-Equal 'Fixture.esm' $winnerEvidence.native.subjects[0].name 'The native origin must become a typed evidence subject.'
    Assert-True (@($winnerEvidence.native.subjects[0].roles) -contains 'BaseOrMaster') 'The native origin must receive only its evidence-derived role.'
    Assert-Equal 'Fixture.esp' $winnerEvidence.native.subjects[1].name 'The native winner must become a typed evidence subject.'
    Assert-True (@($winnerEvidence.native.subjects[1].roles) -contains 'OverrideProvider') 'The native winner must receive the override-provider role.'
    $eslQuery = New-GridXEditQuery -Queries @(
        [pscustomobject]@{ operation = 'AuditEslEligibility'; plugin = 'Fixture.esp'; editorId = ''; formId = '' }
    ) -CaseDirectory $tempRoot -ContextFingerprint 'sha256:current'
    Assert-Equal '2.2.0' $eslQuery.CollectorVersion 'The query package must bind the ESL-aware collector version.'
    $eslReport = Join-Path $tempRoot 'xedit-esl-evidence.tsv'
    @(
        @('QueryId','Operation','Claim','Plugin','FormId','EditorId','Signature','Origin','Winner','State','Detail') -join [char]9
        @('query-001','AuditEslEligibility','The plugin is technically eligible for an ESL header flag without FormID compaction.','Fixture.esp','','','TES4','Fixture.esp','Fixture.esp','Found','Eligibility=HeaderFlagOnly;NewRecordCount=12;MaximumObjectId=000FFE;HasNewCell=false;HasEsmFlag=false;Warning=') -join [char]9
    ) | Set-Content -LiteralPath $eslReport -Encoding UTF8
    $eslEvidence = Get-GridXEditEvidence -ReportPath $eslReport -QueryPackage $eslQuery -CaseDirectory $tempRoot -ContextFingerprint 'sha256:current'
    Assert-Equal 'AuditEslEligibility' $eslEvidence.Evidence[0].native.operation 'ESL eligibility must normalize as verified xEdit evidence.'
    Assert-True (@($eslEvidence.Evidence[0].native.subjects[0].roles) -contains 'EslEligibilitySubject') 'ESL eligibility must type the exact audited plugin.'
    $eslResult = Resolve-GridSkyrimEslEligibility -Evidence $eslEvidence.Evidence -PluginName 'Fixture.esp' -ContextFingerprint 'sha256:current' -CaseDirectory $tempRoot
    Assert-Equal 'EligibleWithoutCompaction' $eslResult.Result.status 'A bounded header-only result must become eligible without implying authorization.'
    Assert-Equal '000FFE' $eslResult.Result.maximumObjectId 'The reducer must preserve the measured hexadecimal object-ID bound.'
    Assert-True (-not $eslResult.Result.mutationAuthorized) 'Read-only eligibility must never grant mutation authority.'
    $eslBatch = Resolve-GridSkyrimEslEligibilityBatch -Evidence $eslEvidence.Evidence -PluginNames @('Fixture.esp') -ContextFingerprint 'sha256:current' -CaseDirectory $tempRoot
    Assert-Equal 'Collected' $eslBatch.Result.status 'A bounded ESL-only collection must reduce without invoking the generic role renderer.'
    Assert-Equal 1 $eslBatch.Result.counts.EligibleWithoutCompaction 'The batch reducer must count header-only candidates.'
    Assert-True (-not $eslBatch.Result.mutationAuthorized) 'A batch reduction must never grant mutation authority.'
    Assert-Throws { Resolve-GridSkyrimEslEligibilityBatch -Evidence $eslEvidence.Evidence -PluginNames @('Fixture.esp') -ContextFingerprint 'sha256:current' -CaseDirectory $tempRoot -MaximumPlugins 0 } 'range|MaximumPlugins' 'The batch reducer must enforce a positive plugin limit.'
    Assert-Throws { New-GridXEditQuery -Queries @([pscustomobject]@{ operation='AuditEslEligibility'; plugin=''; formId=''; editorId='' }) -CaseDirectory $tempRoot -ContextFingerprint 'sha256:current' } 'requires one exact plugin' 'ESL auditing must reject a missing plugin identity.'
    Assert-Throws { New-GridXEditQuery -Queries @([pscustomobject]@{ operation='AuditEslEligibility'; plugin='Fixture.esp'; formId='01000001'; editorId='' }) -CaseDirectory $tempRoot -ContextFingerprint 'sha256:current' } 'plugin filename only' 'ESL auditing must reject unrelated record identity fields.'
    Assert-Throws { Get-GridXEditEvidence -ReportPath $report -QueryPackage $query -CaseDirectory $tempRoot -ContextFingerprint 'sha256:changed' } 'stale' 'A changed context must refuse prior query evidence.'
    $malformedReport = Join-Path $tempRoot 'xedit-malformed.tsv'
    @(
        @('QueryId','Operation','Claim','Plugin','FormId','EditorId','Signature','Origin','Winner','State','Detail') -join [char]9
        @('query-001','FindRecordByEditorId','Unrecognized state.','Fixture.esp','','FixtureObject','','','','Maybe','') -join [char]9
        @('query-002','ResolveWinningOverride','Winner resolved.','Fixture.esp','','FixtureObject','STAT','Fixture.esm','Fixture.esp','Found','') -join [char]9
    ) | Set-Content -LiteralPath $malformedReport -Encoding UTF8
    Assert-Throws { Get-GridXEditEvidence -ReportPath $malformedReport -QueryPackage $query -CaseDirectory $tempRoot -ContextFingerprint 'sha256:current' } 'unsupported evidence state' 'Unknown collector states must not become verified zero measurements.'
    $incompleteReport = Join-Path $tempRoot 'xedit-incomplete.tsv'
    @(
        @('QueryId','Operation','Claim','Plugin','FormId','EditorId','Signature','Origin','Winner','State','Detail') -join [char]9
        @('query-001','FindRecordByEditorId','Record absent.','Fixture.esp','','FixtureObject','','','','NotFound','') -join [char]9
    ) | Set-Content -LiteralPath $incompleteReport -Encoding UTF8
    Assert-Throws { Get-GridXEditEvidence -ReportPath $incompleteReport -QueryPackage $query -CaseDirectory $tempRoot -ContextFingerprint 'sha256:current' } 'did not return a measurement' 'Every bounded query must return an explicit measurement.'

    $placedQuery = New-GridXEditQuery -Queries @(
        [pscustomobject]@{ operation = 'FindReferencesToBase'; plugin = 'Fixture.esp'; formId = 'FE001800'; editorId = '' }
    ) -CaseDirectory $tempRoot -ContextFingerprint 'sha256:current'
    $placedReport = Join-Path $tempRoot 'xedit-placed-actor.tsv'
    @(
        @('QueryId','Operation','Claim','Plugin','FormId','EditorId','Signature','Origin','Winner','State','Detail') -join [char]9
        @('query-001','FindReferencesToBase','A reference was found.','Fixture.esp','FE00181A','FixtureActorREF','ACHR','Fixture.esp','FixturePatch.esp','Found','') -join [char]9
        @('query-001','FindReferencesToBase','A non-actor reference was found.','Fixture.esp','FE001900','FixtureQuest','QUST','Fixture.esp','Fixture.esp','Found','') -join [char]9
    ) | Set-Content -LiteralPath $placedReport -Encoding UTF8
    $placedEvidence = Get-GridXEditEvidence -ReportPath $placedReport -QueryPackage $placedQuery -CaseDirectory $tempRoot -ContextFingerprint 'sha256:current'
    $placed = Resolve-GridSkyrimPlacedActorReference -Evidence $placedEvidence.Evidence -QueryPackage $placedQuery -ContextFingerprint 'sha256:current' -CaseDirectory $tempRoot
    Assert-Equal 'Resolved' $placed.Result.status 'One verified ACHR must resolve as the placed actor.'
    Assert-Equal 'FE00181A' $placed.Result.results[0].selectedReference.referenceFormId 'The ACHR FormID must be preserved as the prid target.'
    Assert-Equal 'prid FE00181A' $placed.Result.results[0].console.diagnosticCommands[0] 'Recovery guidance must use the placed reference.'
    Assert-True (-not (($placed.Result | ConvertTo-Json -Depth 12) -match 'placeatme FE001800')) 'The NPC base FormID must never become a placeatme command.'

    $ambiguousReport = Join-Path $tempRoot 'xedit-placed-actor-ambiguous.tsv'
    @(
        @('QueryId','Operation','Claim','Plugin','FormId','EditorId','Signature','Origin','Winner','State','Detail') -join [char]9
        @('query-001','FindReferencesToBase','First actor reference.','Fixture.esp','FE00181A','FixtureActorREF','ACHR','Fixture.esp','FixturePatch.esp','Found','') -join [char]9
        @('query-001','FindReferencesToBase','Second actor reference.','Fixture.esp','FE00181B','FixtureActorREF2','ACHR','Fixture.esp','FixturePatch.esp','Found','') -join [char]9
    ) | Set-Content -LiteralPath $ambiguousReport -Encoding UTF8
    $ambiguousEvidence = Get-GridXEditEvidence -ReportPath $ambiguousReport -QueryPackage $placedQuery -CaseDirectory $tempRoot -ContextFingerprint 'sha256:current'
    $ambiguous = Resolve-GridSkyrimPlacedActorReference -Evidence $ambiguousEvidence.Evidence -QueryPackage $placedQuery -ContextFingerprint 'sha256:current' -CaseDirectory $tempRoot
    Assert-Equal 'Ambiguous' $ambiguous.Result.status 'Multiple placed actor references must fail closed.'
    Assert-True ($null -eq $ambiguous.Result.results[0].console) 'Ambiguous evidence must not emit a console target.'
    Assert-Throws { Resolve-GridSkyrimPlacedActorReference -Evidence $placedEvidence.Evidence -QueryPackage $placedQuery -ContextFingerprint 'sha256:changed' -CaseDirectory $tempRoot } 'ContextMismatch' 'Stale placed-actor evidence must be rejected.'
    Write-Host 'PASS: verified xEdit references resolve base NPCs to unique placed ACHRs without duplicate-actor guidance.'

    $evidenceFingerprint = Get-GridEvidenceFingerprint -Evidence $evidence.Evidence
    $proposalArguments = @{
        CaseId = 'case-fixture'; ContextFingerprint = 'sha256:current'; EvidenceFingerprint = $evidenceFingerprint
        Evidence = $evidence.Evidence; ActionType = 'PluginStateChange'; Target = @{ pluginName = 'Fixture.esp'; desiredState = 'Disabled' }
        ExpectedEffects = @('Controlled comparison can distinguish the candidate.'); Exclusions = @('No plugin binary edit.')
        VerificationSteps = @('Recollect the same query.')
    }
    $proposal = New-GridSkyrimRemediationProposal @proposalArguments
    Assert-Equal 'Required' $proposal.authorization.status 'Proposal must require explicit authorization and never self-authorize.'
    Assert-Equal 'AwaitingAuthorization' $proposal.status 'Supported proposal must await authorization and not execute.'
    Assert-Throws { New-GridSkyrimRemediationProposal -CaseId x -ContextFingerprint 'sha256:stale' -EvidenceFingerprint $evidenceFingerprint -Evidence $evidence.Evidence -ActionType RecordPatchSpecification -Target @{} -ExpectedEffects x -VerificationSteps x } 'verified evidence bound' 'Stale context evidence must refuse.'
    Assert-Throws { New-GridSkyrimRemediationProposal -CaseId x -ContextFingerprint 'sha256:current' -EvidenceFingerprint wrong -Evidence $evidence.Evidence -ActionType PluginStateChange -Target @{ pluginName = 'Fixture.esp'; desiredState = 'Disabled' } -ExpectedEffects x -VerificationSteps x } 'does not match' 'Mismatched evidence fingerprint must refuse.'
    $unobservedProposal = New-GridSkyrimRemediationProposal -CaseId x -ContextFingerprint 'sha256:current' -EvidenceFingerprint $evidenceFingerprint -Evidence $evidence.Evidence -ActionType PluginStateChange -Target @{ pluginName = 'Unobserved.esp'; desiredState = 'Disabled' } -ExpectedEffects x -VerificationSteps x
    $unobservedResult = Resolve-GridSkyrimDiagnosticResult -CaseId x -ContextFingerprint 'sha256:current' -EvidenceFingerprint $evidenceFingerprint -Evidence $evidence.Evidence -RemediationProposal $unobservedProposal
    Assert-Equal 'NeedsEvidence' $unobservedResult.state 'A proposal targeting a plugin absent from verified subject evidence must not diagnose.'
    $modelEvidence = New-GridEvidenceItem -Parameter model -Value 1 -Claim 'A model-only claim.' -SourceType ModelReasoning -ContextFingerprint 'sha256:current' -VerificationStatus Verified -CollectorName Fixture -CollectorVersion 1
    Assert-Throws { New-GridSkyrimRemediationProposal -CaseId x -ContextFingerprint 'sha256:current' -EvidenceFingerprint (Get-GridEvidenceFingerprint @($modelEvidence)) -Evidence @($modelEvidence) -ActionType PluginStateChange -Target @{ pluginName = 'Fixture.esp'; desiredState = 'Disabled' } -ExpectedEffects x -VerificationSteps x } 'requires verified evidence' 'Model-only support must never produce a remediation proposal.'
    Write-Host 'PASS: evidence normalization and authorization-bound proposal lifecycle.'

    $source = Get-Content -Raw (Join-Path $gameRoot 'health\collectors\Start-GridXEditProbe.ps1')
    Assert-True ($source -match 'Trace-GridReference') 'Active launcher must invoke the generic collector.'
    Assert-True ($source -match '\-P:') 'Active launcher must require a custom plugin list.'
    Write-Host 'PASS: active xEdit launch path is generic and case-local.'
}
finally { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
