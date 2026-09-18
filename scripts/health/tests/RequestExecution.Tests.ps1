$ErrorActionPreference='Stop'
$healthRoot=Split-Path -Parent $PSScriptRoot
$scriptsRoot=Split-Path -Parent $healthRoot
Import-Module (Join-Path $healthRoot 'Grid.Health.psm1') -Force
function Assert-True([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
function Assert-Equal($Expected,$Actual,[string]$Message){if($Expected-ne$Actual){throw "$Message Expected '$Expected', actual '$Actual'."}}
function Assert-Throws([scriptblock]$Action,[string]$Pattern,[string]$Message){try{&$Action;throw "$Message Expected '$Pattern'."}catch{if($_.Exception.Message-notmatch$Pattern){throw "$Message Unexpected: $($_.Exception.Message)"}}}
$tempRoot=Join-Path $env:TEMP ('grid-request-execution-tests-'+[guid]::NewGuid().ToString('N'))
try{
 New-Item -ItemType Directory -Path $tempRoot -Force|Out-Null
 $legacyTransaction=New-GridCaseStoreTransaction -StoreRoot $tempRoot -CaseId 'legacy-case-with-repair-artifact'
 Write-GridCaseStoreArtifact -Transaction $legacyTransaction -RelativePath 'case.json' -Value ([pscustomobject][ordered]@{schemaVersion=1;caseId='legacy-case-with-repair-artifact';createdAt=[DateTimeOffset]::UtcNow.ToString('o');status='Complete'})|Out-Null
 Write-GridCaseStoreArtifact -Transaction $legacyTransaction -RelativePath 'repair\repair-specification.v1.json' -Value ([pscustomobject][ordered]@{schemaVersion=1})|Out-Null
 $legacyNow=[DateTimeOffset]::UtcNow.ToString('o')
 $legacyRun=[pscustomobject][ordered]@{schemaVersion=1;runId='legacy-run';caseId='legacy-case-with-repair-artifact';state='Completed';startedAt=$legacyNow;completedAt=$legacyNow;planFingerprint=('D'*64);resourcePolicyVersion='grid.legacy-fixture.v1';gates=@();sufficiency=[pscustomobject]@{status='Complete'};checkpoints=@();primaryFailure=$null;secondaryFailures=@()}
 Seal-GridCaseStoreRun -Transaction $legacyTransaction -Run $legacyRun|Out-Null
 $legacySealed=Seal-GridDiagnosticCase -Transaction $legacyTransaction -SemanticBaselineFingerprint ('E'*64) -Runs @($legacyRun)
 Assert-True (Test-GridDiagnosticCaseSeal -StoreRoot $tempRoot -CaseDirectory $legacySealed.CaseDirectory).IsValid 'Legacy fixture must be a valid sealed case.'
 $legacyHistory=@(Get-GridRequestTaskHistory -StoreRoot $tempRoot)
 Assert-Equal 0 @($legacyHistory|Where-Object taskId -eq 'legacy-case-with-repair-artifact').Count 'A legacy case without a RepairPlanning purpose must be ignored without breaking startup history.'
 $legacyIndexPath=Join-Path $tempRoot 'task-transcript.v1.json'
 $legacyIndexBefore=Get-Content -LiteralPath $legacyIndexPath -Raw
 Get-GridRequestTaskHistory -StoreRoot $tempRoot|Out-Null
 Assert-Equal $legacyIndexBefore (Get-Content -LiteralPath $legacyIndexPath -Raw) 'Unchanged sealed history must not rewrite Grid task state during startup.'
 $emptyRepairTransaction=New-GridCaseStoreTransaction -StoreRoot $tempRoot -CaseId 'repair-empty-operations'
 Write-GridCaseStoreArtifact -Transaction $emptyRepairTransaction -RelativePath 'case.json' -Value ([pscustomobject][ordered]@{schemaVersion=1;caseId='repair-empty-operations';createdAt=$legacyNow;status='Complete';purpose='RepairPlanning'})|Out-Null
 Write-GridCaseStoreArtifact -Transaction $emptyRepairTransaction -RelativePath 'repair\repair-specification.v1.json' -Value ([pscustomobject][ordered]@{schemaVersion=1;specificationId='repair-empty';specificationSha256=('A'*64);operations=@();baseline=[pscustomobject]@{caseDirectory=$tempRoot}})|Out-Null
 $emptyRepairRun=[pscustomobject][ordered]@{schemaVersion=1;runId='empty-repair-run';caseId='repair-empty-operations';state='Completed';startedAt=$legacyNow;completedAt=$legacyNow;planFingerprint=('B'*64);resourcePolicyVersion='grid.empty-repair-fixture.v1';gates=@();sufficiency=[pscustomobject]@{status='Complete'};checkpoints=@();primaryFailure=$null;secondaryFailures=@()}
 Seal-GridCaseStoreRun -Transaction $emptyRepairTransaction -Run $emptyRepairRun|Out-Null
 Seal-GridDiagnosticCase -Transaction $emptyRepairTransaction -SemanticBaselineFingerprint ('C'*64) -Runs @($emptyRepairRun)|Out-Null
 $emptyRepairTask=@(Get-GridRequestTaskHistory -StoreRoot $tempRoot|Where-Object taskId -eq 'repair-empty-operations')
 Assert-Equal 1 $emptyRepairTask.Count 'A valid zero-operation repair-planning case must remain discoverable.'
 Assert-True ($emptyRepairTask[0].result.affectedMods -is [System.Array]) 'A zero-operation repair must project affectedMods as an empty JSON array, never an object.'
 Assert-Equal 'skyrimspecialedition' (Resolve-GridRequestGameId 'game.skyrim-special-edition') 'Application catalog ID must map to canonical script ID.'
 $envelope=New-GridRequestEnvelope -GameId skyrimspecialedition -InstallationId installation.fixture -ProfileId profile.fixture -ClassId grid.class.installation-integrity -ClassRecipeVersion 1.1.0 -ModNames @('Claimed Mod') -ToolIds @('grid.tool.loot') -PlainText 'Check installation.'
 $plan=Resolve-GridRequestPlan -Envelope $envelope -ScriptsRoot $scriptsRoot
 $capabilityAssessment=[pscustomobject][ordered]@{
  schemaVersion=1;capabilityId='grid.capability.equipment.multiple-rings';displayName='Wear rings on multiple fingers';installedStatus='Absent';installedProviders=@();discoveryStatus='Current';observedAtUtc='2026-09-11T16:00:00Z'
  communityCandidates=@([pscustomobject][ordered]@{name='Fixture Multi-Ring Provider';provider='Nexus';uri='https://www.nexusmods.com/skyrimspecialedition/mods/12345';version='2.0.0';compatibilityStatus='Compatible';compatibilityDetail='The fixture profile satisfies the declared dependencies and has no evidenced conflict.';requiredPatches=@();evidenceIds=@('provider-evidence.fixture')})
  evidenceIds=@('profile-evidence.fixture','provider-evidence.fixture')
 }
 $capabilityResult=New-GridCapabilityAssessmentResult -Envelope $envelope -Assessment $capabilityAssessment
 Assert-Equal 'EvidenceComplete' $capabilityResult.terminalState 'A current capability assessment must be a useful evidence result, not an unsupported error.'
 Assert-Equal 'CapabilityAssessment' $capabilityResult.resultKind 'Capability coverage must remain distinct from diagnosis and repair.'
 Assert-Equal 'Absent' $capabilityResult.capabilityAssessment.installedStatus 'The result must explicitly confirm that no installed provider satisfies the request.'
 Assert-Equal 'Compatible' $capabilityResult.capabilityAssessment.communityCandidates[0].compatibilityStatus 'A current compatible candidate must retain its compatibility state.'
 Assert-True ($null-eq$capabilityResult.capabilityRequired) 'A completed capability assessment must not collapse into CapabilityRequired.'
 Assert-True (-not$capabilityResult.repairState.applyEnabled) 'A recommendation must never authorize installation or repair.'
 $invalidAssessment=$capabilityAssessment.PSObject.Copy();$invalidAssessment.discoveryStatus='Stale'
 Assert-Throws {New-GridCapabilityAssessmentResult -Envelope $envelope -Assessment $invalidAssessment|Out-Null} 'community candidates require Current' 'Stale provider evidence must not retain current recommendations.'
 $input=@{'grid.tool.loot'=@{candidatePaths=@((Join-Path $tempRoot 'loot.json'));authorizedReadPaths=@((Join-Path $tempRoot 'loot.json'))}}
 $review=New-GridRequestAuthorizationReview -Envelope $envelope -RequestPlan $plan -ScriptsRoot $scriptsRoot -SubmissionId submission-one -ActorId actor.fixture -SessionId session.fixture -ToolInputs $input
 Assert-Equal 'AwaitingAuthorization' $review.status 'Review must not grant read authority.'
 Assert-True ($review.PSObject.Properties['authorizationDigest'] -eq $null) 'Public review digest must not itself be an approval credential.'
 Assert-True ($review.reviewDigest -match '^[A-F0-9]{64}$') 'Review may expose a public integrity digest.'
 $issued=Grant-GridRequestAuthorization -AuthorizationReview $review -StoreRoot $tempRoot
 Assert-True (-not [string]::IsNullOrWhiteSpace($issued.AuthorizationSecret)) 'Explicit grant issuance must return a random secret once.'
 Assert-True (-not (Get-Content -LiteralPath $issued.GrantPath -Raw).Contains($issued.AuthorizationSecret)) 'Random secret must not be persisted.'
 $executor={param($definition,$request)[pscustomobject]@{status='Collected';evidence=@([pscustomobject]@{verified=$true});stdout='';stderr='';exitCode=0;reason=$null}}
 Assert-Throws {Invoke-GridAuthorizedRequestExecution -Envelope $envelope -RequestPlan $plan -AuthorizationReview $review -AuthorizationGrantId $issued.Grant.grantId -AuthorizationSecret wrong -ActorId actor.fixture -SessionId session.fixture -ScriptsRoot $scriptsRoot -CaseStoreRoot $tempRoot -SubmissionId submission-one -Executor $executor|Out-Null} 'AuthorizationSecretInvalid' 'Public digest or wrong secret must never execute.'
 $first=Invoke-GridAuthorizedRequestExecution -Envelope $envelope -RequestPlan $plan -AuthorizationReview $review -AuthorizationGrantId $issued.Grant.grantId -AuthorizationSecret $issued.AuthorizationSecret -ActorId actor.fixture -SessionId session.fixture -ScriptsRoot $scriptsRoot -CaseStoreRoot $tempRoot -SubmissionId submission-one -Executor $executor
 Assert-Equal 'EvidenceComplete' $first.Status 'Authorized read execution must seal evidence.'
 Assert-True (Test-Path -LiteralPath (Join-Path $first.CaseDirectory 'request\authorization-grant.v2.json')) 'Current durable grant identity must be sealed with the case.'
 Assert-True (Test-Path -LiteralPath (Join-Path $first.CaseDirectory 'evidence\tool-evidence-run.v2.json')) 'Current fully bound tool receipt run must be sealed.'
 $firstLedgerPath=Join-Path $first.CaseDirectory 'diagnosis\problem-ledger.v1.json'
 Assert-True (Test-Path -LiteralPath $firstLedgerPath -PathType Leaf) 'Every claimed sealed request must carry its problem ledger projection.'
 $firstLedger=Get-Content -LiteralPath $firstLedgerPath -Raw|ConvertFrom-Json
 Assert-True (Test-GridProblemLedger -Ledger $firstLedger).IsValid 'The sealed request problem ledger must validate.'
 Assert-Equal 'NeedsEvidence' $firstLedger.entries[0].state 'Collected evidence must not silently close or diagnose the preserved claim.'
 Assert-Equal 'Consumed' (Read-GridAuthorizationGrant -StoreRoot $tempRoot -GrantId $issued.Grant.grantId).state 'Successful read execution must durably consume its grant.'
 $resumed=Invoke-GridAuthorizedRequestExecution -Envelope $envelope -RequestPlan $plan -AuthorizationReview $review -AuthorizationGrantId $issued.Grant.grantId -AuthorizationSecret $issued.AuthorizationSecret -ActorId actor.fixture -SessionId session.fixture -ScriptsRoot $scriptsRoot -CaseStoreRoot $tempRoot -SubmissionId submission-one -Executor {throw 'must not rerun'}
 Assert-Equal 'ResumedSealed' $resumed.Status 'Exact semantic identity may resume the already sealed result without consuming authority again.'
 Assert-True (-not $resumed.CollectorsRerun) 'Sealed resume must not rerun collectors.'
 Assert-Throws {Invoke-GridAuthorizedRequestExecution -Envelope $envelope -RequestPlan $plan -AuthorizationReview $review -AuthorizationGrantId $issued.Grant.grantId -AuthorizationSecret $issued.AuthorizationSecret -ActorId actor.other -SessionId session.fixture -ScriptsRoot $scriptsRoot -CaseStoreRoot $tempRoot -SubmissionId submission-one -Executor {throw 'must not execute'}|Out-Null} 'AuthorizationReviewMismatch|AuthorizationBindingMismatch' 'Wrong actor must refuse before sealed reuse.'
 $script:classDispatchInvoked=$false
 $registeredDiagnosis=Invoke-GridRequestDiagnosis -TaskId submission-one -StoreRoot $tempRoot -ScriptsRoot $scriptsRoot -ClassRequestInvoker {param($sealedEnvelope,$sealedPlan,$caseDirectory)$script:classDispatchInvoked=$true;[pscustomobject]@{Plan=$sealedPlan;ExecutionResult=[pscustomobject]@{Status='Failed';CaseId='baseline-fixture-failed';PrimaryFailure=[pscustomobject]@{code='FixtureFailure';detail='Synthetic deterministic collector failure.'}}}}
 Assert-True $script:classDispatchInvoked 'Diagnose must invoke the registered Class request path for a ReadyToCollect sealed request.'
 Assert-Equal 'EvidenceFailed' $registeredDiagnosis.Status 'A failed registered dispatch must preserve its exact evidence failure instead of fabricating CapabilityRequired.'
 Assert-True ($null-eq$registeredDiagnosis.Result.capabilityRequired) 'A collector failure must not be mislabeled as a missing capability.'
 Assert-Equal 'Synthetic deterministic collector failure.' $registeredDiagnosis.Result.solution 'The exact registered collector failure must be visible.'
 $reusedFailure=Invoke-GridRequestDiagnosis -TaskId $registeredDiagnosis.CaseId -StoreRoot $tempRoot -ScriptsRoot $scriptsRoot -ClassRequestInvoker {throw 'persisted dispatch must be reused'}
 Assert-Equal 'EvidenceFailed' $reusedFailure.Status 'A diagnosis successor must reuse its sealed registered dispatch across sessions.'

 $machineDiagnosePath=Join-Path $tempRoot 'machine-diagnose-action.json'
 $machineDiagnoseInput=[pscustomobject][ordered]@{schemaVersion=2;operation='Diagnose';taskId='submission-one';caseStoreRoot=$tempRoot}
 [IO.File]::WriteAllText($machineDiagnosePath,($machineDiagnoseInput|ConvertTo-Json -Depth 10),(New-Object Text.UTF8Encoding($false)))
 $machineDiagnoseOutput=@(& 'C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe' -NoProfile -ExecutionPolicy Bypass -File (Join-Path $healthRoot 'Invoke-GridRequestSubmission.ps1') -InputJsonPath $machineDiagnosePath) -join "`n"
 Assert-True $machineDiagnoseOutput.StartsWith('{',[StringComparison]::Ordinal) ('The machine entry point must emit JSON as its first stdout byte. Output: '+$machineDiagnoseOutput)
 $machineDiagnoseResponse=$machineDiagnoseOutput|ConvertFrom-Json -ErrorAction Stop
 Assert-Equal 'Diagnose' $machineDiagnoseResponse.operation 'The real registered Diagnose subprocess must preserve its machine response contract.'
 Assert-Equal 1 @($machineDiagnoseResponse.tasks).Count 'The real registered Diagnose subprocess must return exactly one persisted task projection.'

 $repeatReview=New-GridRequestAuthorizationReview -Envelope $envelope -RequestPlan $plan -ScriptsRoot $scriptsRoot -SubmissionId submission-two -ActorId actor.fixture -SessionId session.fixture -ToolInputs $input
 $repeatGrant=Grant-GridRequestAuthorization -AuthorizationReview $repeatReview -StoreRoot $tempRoot
 $repeat=Invoke-GridAuthorizedRequestExecution -Envelope $envelope -RequestPlan $plan -AuthorizationReview $repeatReview -AuthorizationGrantId $repeatGrant.Grant.grantId -AuthorizationSecret $repeatGrant.AuthorizationSecret -ActorId actor.fixture -SessionId session.fixture -ScriptsRoot $scriptsRoot -CaseStoreRoot $tempRoot -SubmissionId submission-two -Executor $executor
 Assert-True ($repeat.CaseId-ne$first.CaseId) 'Intentional repeat needs its own submission and grant identity.'

 $postReview=New-GridRequestAuthorizationReview -Envelope $envelope -RequestPlan $plan -ScriptsRoot $scriptsRoot -SubmissionId submission-post-read -ActorId actor.fixture -SessionId session.fixture -ToolInputs $input
 $postGrant=Grant-GridRequestAuthorization -AuthorizationReview $postReview -StoreRoot $tempRoot
 $script:postReadInvoked=$false
 $post=Invoke-GridAuthorizedRequestExecution -Envelope $envelope -RequestPlan $plan -AuthorizationReview $postReview -AuthorizationGrantId $postGrant.Grant.grantId -AuthorizationSecret $postGrant.AuthorizationSecret -ActorId actor.fixture -SessionId session.fixture -ScriptsRoot $scriptsRoot -CaseStoreRoot $tempRoot -SubmissionId submission-post-read -Executor $executor -PostReadCollector {param($boundReview,$caseDirectory)$script:postReadInvoked=$true;[pscustomobject]@{Status='Completed';TerminalState='BaselineComplete';CaseId='baseline-post-read-fixture';Manifest=[pscustomobject]@{manifestSha256=('D'*64)}}}
 Assert-True $script:postReadInvoked 'An explicitly supplied authorized post-read collector must run before grant completion.'
 Assert-True (Test-Path -LiteralPath (Join-Path $post.CaseDirectory 'evidence\post-read-collector.v1.json')) 'The post-read baseline linkage must be sealed with the authorization case.'
 Assert-Equal 'Consumed' (Read-GridAuthorizationGrant -StoreRoot $tempRoot -GrantId $postGrant.Grant.grantId).state 'A successful post-read collection must consume the same exact read grant.'

 $successorIntake=New-GridInvestigationIntake -Problem 'Capture a diagnosed successor.' -ExpectedBehavior 'Preserve a diagnosis without a repair proposal.' -ReproductionLocation 'Fixture profile.' -DesiredOutcome 'Return the sealed successor identity.' -AuthorizationScope SelectedContext -CaptureCurrentState:$false -ParentTaskId submission-one
 $unproposedDiagnostic=New-GridUnresolvedDiagnosticResult -CaseId baseline-unproposed-fixture -State NeedsEvidence -Finding 'Fixture evidence is incomplete.' -NextStep 'Review the sealed evidence before planning repair.'
 $diagnosticReview=New-GridRequestAuthorizationReview -Envelope $envelope -RequestPlan $plan -ScriptsRoot $scriptsRoot -SubmissionId action-unproposed-diagnostic -ActorId actor.fixture -SessionId session.fixture -ToolInputs $input -InvestigationIntake $successorIntake
 $diagnosticGrant=Grant-GridRequestAuthorization -AuthorizationReview $diagnosticReview -StoreRoot $tempRoot
 $script:unproposedDiagnostic=$unproposedDiagnostic
 $diagnosticPost=Invoke-GridAuthorizedRequestExecution -Envelope $envelope -RequestPlan $plan -AuthorizationReview $diagnosticReview -AuthorizationGrantId $diagnosticGrant.Grant.grantId -AuthorizationSecret $diagnosticGrant.AuthorizationSecret -ActorId actor.fixture -SessionId session.fixture -ScriptsRoot $scriptsRoot -CaseStoreRoot $tempRoot -SubmissionId action-unproposed-diagnostic -InvestigationIntake $successorIntake -Executor $executor -PostReadCollector {[pscustomobject]@{Status='Completed';TerminalState='BaselineComplete';CaseId='baseline-unproposed-fixture';Manifest=[pscustomobject]@{manifestSha256=('F'*64)};DiagnosticResult=$script:unproposedDiagnostic;Evidence=@()}}
 Assert-Equal $false $diagnosticPost.Result.repairState.specificationAvailable 'A valid diagnostic solution without optional proposalId must remain readable and must not advertise a repair specification.'
 $successorLedger=Get-Content -LiteralPath (Join-Path $diagnosticPost.CaseDirectory 'diagnosis\problem-ledger.v1.json') -Raw|ConvertFrom-Json
 Assert-True (Test-GridProblemLedger -Ledger $successorLedger).IsValid 'A successor must carry a valid inherited ledger.'
 Assert-Equal 2 @($successorLedger.entries).Count 'A successor must preserve its parent claim and append its distinct structured intake claim.'
 $diagnosticTask=@(Get-GridRequestTaskHistory -StoreRoot $tempRoot|Where-Object caseId -CEQ $diagnosticPost.CaseId)
 Assert-Equal 1 $diagnosticTask.Count 'A diagnosed successor must be discoverable by its sealed case identity.'
 Assert-Equal $diagnosticPost.CaseId $diagnosticTask[0].taskId 'A diagnosed successor task identity must equal its sealed case identity.'
 Assert-Equal 'submission-one' $diagnosticTask[0].parentTaskId 'A diagnosed successor must retain its exact parent task identity.'

 $pausedReview=New-GridRequestAuthorizationReview -Envelope $envelope -RequestPlan $plan -ScriptsRoot $scriptsRoot -SubmissionId submission-post-read-paused -ActorId actor.fixture -SessionId session.fixture -ToolInputs $input
 $pausedGrant=Grant-GridRequestAuthorization -AuthorizationReview $pausedReview -StoreRoot $tempRoot
 $pausedPost=Invoke-GridAuthorizedRequestExecution -Envelope $envelope -RequestPlan $plan -AuthorizationReview $pausedReview -AuthorizationGrantId $pausedGrant.Grant.grantId -AuthorizationSecret $pausedGrant.AuthorizationSecret -ActorId actor.fixture -SessionId session.fixture -ScriptsRoot $scriptsRoot -CaseStoreRoot $tempRoot -SubmissionId submission-post-read-paused -Executor $executor -PostReadCollector {[pscustomobject]@{Status='PausedAtCheckpoint';TerminalState='BaselineFailed';CaseId='baseline-post-read-paused';Manifest=[pscustomobject]@{manifestSha256=('E'*64)};Detail='Synthetic bounded baseline checkpoint.'}}
 Assert-Equal 'EvidencePartial' $pausedPost.Status 'A sealed baseline checkpoint must remain partial evidence instead of throwing or claiming a missing capability.'
 Assert-True ($pausedPost.Result.solution -match 'Capture Current State again') 'A paused baseline must disclose the authorized successor action that resumes its exact checkpoint.'
 Assert-True ($null-eq$pausedPost.Result.capabilityRequired) 'A paused collector is not a missing capability.'
 Assert-Equal 'Consumed' (Read-GridAuthorizationGrant -StoreRoot $tempRoot -GrantId $pausedGrant.Grant.grantId).state 'A read grant used to reach a sealed checkpoint must be consumed.'

 $failedReview=New-GridRequestAuthorizationReview -Envelope $envelope -RequestPlan $plan -ScriptsRoot $scriptsRoot -SubmissionId submission-post-read-failed -ActorId actor.fixture -SessionId session.fixture -ToolInputs $input
 $failedGrant=Grant-GridRequestAuthorization -AuthorizationReview $failedReview -StoreRoot $tempRoot
 $failedPost=Invoke-GridAuthorizedRequestExecution -Envelope $envelope -RequestPlan $plan -AuthorizationReview $failedReview -AuthorizationGrantId $failedGrant.Grant.grantId -AuthorizationSecret $failedGrant.AuthorizationSecret -ActorId actor.fixture -SessionId session.fixture -ScriptsRoot $scriptsRoot -CaseStoreRoot $tempRoot -SubmissionId submission-post-read-failed -Executor $executor -PostReadCollector {[pscustomobject]@{Status='Failed';TerminalState='BaselineFailed';CaseId='baseline-post-read-failed';PrimaryFailure=[pscustomobject]@{code='SyntheticBaselineFailure';detail='Synthetic baseline failed after bounded evidence collection.'}}}
 Assert-Equal 'EvidenceFailed' $failedPost.Status 'A failed post-read baseline must seal its exact failure instead of discarding the task.'
 Assert-Equal 'Synthetic baseline failed after bounded evidence collection.' $failedPost.Result.solution 'A failed post-read baseline must preserve the exact collector detail.'
 Assert-True ($null-eq$failedPost.Result.capabilityRequired) 'A failed collector is not a missing capability.'
 Assert-Equal 'Consumed' (Read-GridAuthorizationGrant -StoreRoot $tempRoot -GrantId $failedGrant.Grant.grantId).state 'A read grant used by a failed collector must still be consumed.'

 $unhandledReview=New-GridRequestAuthorizationReview -Envelope $envelope -RequestPlan $plan -ScriptsRoot $scriptsRoot -SubmissionId submission-unhandled-post-read -ActorId actor.fixture -SessionId session.fixture -ToolInputs $input
 $unhandledGrant=Grant-GridRequestAuthorization -AuthorizationReview $unhandledReview -StoreRoot $tempRoot
 $unhandledPost=Invoke-GridAuthorizedRequestExecution -Envelope $envelope -RequestPlan $plan -AuthorizationReview $unhandledReview -AuthorizationGrantId $unhandledGrant.Grant.grantId -AuthorizationSecret $unhandledGrant.AuthorizationSecret -ActorId actor.fixture -SessionId session.fixture -ScriptsRoot $scriptsRoot -CaseStoreRoot $tempRoot -SubmissionId submission-unhandled-post-read -Executor $executor -PostReadCollector {throw 'SyntheticUncaughtFailure: fixture exploded.'}
 Assert-Equal 'EvidenceFailed' $unhandledPost.Status 'An unhandled collector exception must become a sealed failed task instead of escaping the request bridge.'
 Assert-Equal 'SyntheticUncaughtFailure' $unhandledPost.Result.failureCode 'The sealed failed task must preserve the exact machine-readable failure code.'
 Assert-Equal 'SyntheticUncaughtFailure: fixture exploded.' $unhandledPost.Result.solution 'The sealed failed task must preserve the exact failure detail for the operator.'
 Assert-True (Test-Path -LiteralPath (Join-Path $unhandledPost.CaseDirectory 'failure\request-execution-failure.v1.json')) 'Unhandled execution failure evidence must be sealed with the task.'
 Assert-True (Test-GridDiagnosticCaseSeal -StoreRoot $tempRoot -CaseDirectory $unhandledPost.CaseDirectory).IsValid 'The unhandled execution failure task must have a valid case seal.'
 Assert-Equal 'Failed' (Read-GridAuthorizationGrant -StoreRoot $tempRoot -GrantId $unhandledGrant.Grant.grantId).state 'An unhandled execution failure must durably terminate the one-use grant.'
 Assert-Equal 1 @(Get-GridRequestTaskHistory -StoreRoot $tempRoot | Where-Object taskId -eq 'submission-unhandled-post-read').Count 'The sealed failure must be discoverable in Activity across sessions.'

 $successorFailureReview=New-GridRequestAuthorizationReview -Envelope $envelope -RequestPlan $plan -ScriptsRoot $scriptsRoot -SubmissionId action-successor-failure -ActorId actor.fixture -SessionId session.fixture -ToolInputs $input -InvestigationIntake $successorIntake
 $successorFailureGrant=Grant-GridRequestAuthorization -AuthorizationReview $successorFailureReview -StoreRoot $tempRoot
 $successorFailure=Invoke-GridAuthorizedRequestExecution -Envelope $envelope -RequestPlan $plan -AuthorizationReview $successorFailureReview -AuthorizationGrantId $successorFailureGrant.Grant.grantId -AuthorizationSecret $successorFailureGrant.AuthorizationSecret -ActorId actor.fixture -SessionId session.fixture -ScriptsRoot $scriptsRoot -CaseStoreRoot $tempRoot -SubmissionId action-successor-failure -InvestigationIntake $successorIntake -Executor $executor -PostReadCollector {throw 'SyntheticSuccessorFailure: fixture exploded after lineage was bound.'}
 $successorFailureTask=@(Get-GridRequestTaskHistory -StoreRoot $tempRoot|Where-Object caseId -CEQ $successorFailure.CaseId)
 Assert-Equal 1 $successorFailureTask.Count 'A failed successor must remain discoverable after failure sealing.'
 Assert-Equal $successorFailure.CaseId $successorFailureTask[0].taskId 'A failed successor task identity must equal its sealed case identity.'
 Assert-Equal 'submission-one' $successorFailureTask[0].parentTaskId 'A failed successor must retain its exact parent task identity.'

 $attachmentPath=Join-Path $tempRoot 'symptom.txt';[IO.File]::WriteAllText($attachmentPath,'synthetic symptom evidence',(New-Object Text.UTF8Encoding($false)))
 $unsupportedEnvelope=New-GridRequestEnvelope -GameId skyrimspecialedition -InstallationId installation.fixture -ProfileId profile.fixture -ClassId grid.class.dialogue-scenes -ClassRecipeVersion 1.0.0 -PlainText 'An unsupported but preserved report.'
 $unsupportedEnvelopeValidation=Test-GridRequestEnvelope -Envelope $unsupportedEnvelope
 Assert-True $unsupportedEnvelopeValidation.IsValid ('Fresh unsupported envelope must validate before persistence: ' + ($unsupportedEnvelopeValidation.Errors -join '; '))
 $unsupportedPlan=Resolve-GridRequestPlan -Envelope $unsupportedEnvelope -ScriptsRoot $scriptsRoot
 Assert-Equal 'UnsupportedCoverage' $unsupportedPlan.status 'Unsupported Class coverage must remain explicit.'
 $intake=New-GridInvestigationIntake -Problem 'An unsupported but preserved report.' -ExpectedBehavior 'Expected dialogue arbitration.' -ReproductionLocation 'Synthetic location.' -DesiredOutcome 'Preserve and identify the missing capability.' -AuthorizationScope SelectedContextAndAttachments -CaptureCurrentState:$false -Attachments @([pscustomobject]@{path=$attachmentPath;mediaType='text/plain';assertions=@('User-supplied symptom statement.')})
 $unsupportedReview=New-GridRequestAuthorizationReview -Envelope $unsupportedEnvelope -RequestPlan $unsupportedPlan -ScriptsRoot $scriptsRoot -SubmissionId submission-unsupported -ActorId actor.fixture -SessionId session.fixture -ToolInputs @{} -InvestigationIntake $intake
 Assert-True (@($unsupportedReview.semanticBinding.targets) -contains [IO.Path]::GetFullPath($attachmentPath)) 'Attachment must be an exact reviewed read target.'
 $papyrusPath=Join-Path $tempRoot 'fixture.psc';[IO.File]::WriteAllText($papyrusPath,"Scriptname Fixture extends Quest`nEvent OnInit()`nEndEvent",(New-Object Text.UTF8Encoding($false)))
 $papyrusIntake=New-GridInvestigationIntake -Problem 'Inspect one Papyrus script.' -ExpectedBehavior 'Record static script state.' -ReproductionLocation 'Synthetic fixture.' -DesiredOutcome 'Bind the offline parser.' -AuthorizationScope SelectedContextAndAttachments -CaptureCurrentState:$false -Attachments @([pscustomobject]@{path=$papyrusPath;mediaType='application/octet-stream';assertions=@()})
 $papyrusReview=New-GridRequestAuthorizationReview -Envelope $unsupportedEnvelope -RequestPlan $unsupportedPlan -ScriptsRoot $scriptsRoot -SubmissionId submission-papyrus-review -ActorId actor.fixture -SessionId session.fixture -ToolInputs @{} -InvestigationIntake $papyrusIntake
 Assert-Equal 1 @($papyrusReview.semanticBinding.capabilities|Where-Object capabilityId -eq 'grid.game.skyrimspecialedition.papyrus-state.inspect').Count 'A Skyrim Papyrus attachment must bind the offline inspection capability.'
 Assert-Equal 'InProcessRead+BundledPapyrusInspection' ($papyrusReview.scopes|Where-Object toolId -eq 'grid.intake.attachments').observationMode 'Papyrus attachment review must disclose the bundled collector launch.'
 $unsupportedGrant=Grant-GridRequestAuthorization -AuthorizationReview $unsupportedReview -StoreRoot $tempRoot
 $unsupported=Invoke-GridAuthorizedRequestExecution -Envelope $unsupportedEnvelope -RequestPlan $unsupportedPlan -AuthorizationReview $unsupportedReview -AuthorizationGrantId $unsupportedGrant.Grant.grantId -AuthorizationSecret $unsupportedGrant.AuthorizationSecret -ActorId actor.fixture -SessionId session.fixture -ScriptsRoot $scriptsRoot -CaseStoreRoot $tempRoot -SubmissionId submission-unsupported -InvestigationIntake $intake
 Assert-Equal 'EvidenceComplete' $unsupported.Status 'An imported attachment is complete intake evidence even when diagnosis coverage is unsupported.'
 Assert-True (Test-Path -LiteralPath (Join-Path $unsupported.CaseDirectory 'request\investigation-intake.v1.json')) 'Structured intake must be sealed.'
 $attachmentIndex=Get-Content -LiteralPath (Join-Path $unsupported.CaseDirectory 'attachments\manifest.v1.json') -Raw|ConvertFrom-Json
 Assert-Equal 'Imported' $attachmentIndex.attachments[0].status 'Authorized attachment must be content-addressed and indexed.'
 Assert-True ($attachmentIndex.attachments[0].sha256 -match '^[A-F0-9]{64}$') 'Imported attachment must have a full SHA-256.'
 $unsupportedSeal=Test-GridDiagnosticCaseSeal -StoreRoot $tempRoot -CaseDirectory $unsupported.CaseDirectory
 Assert-True $unsupportedSeal.IsValid ('Unsupported attachment case must remain sealed: ' + ($unsupportedSeal.Errors -join '; '))
 Assert-Equal 1 @(Get-GridRequestTaskHistory -StoreRoot $tempRoot | Where-Object { [string]$_.taskId -ceq 'submission-unsupported' }).Count 'The sealed unsupported task must remain uniquely discoverable.'
 $parentSealBefore=Get-Content -LiteralPath (Join-Path $unsupported.CaseDirectory 'case-manifest.v1.json') -Raw
 $attachInputPath=Join-Path $tempRoot 'attach-action.json'
 $attachInput=[pscustomobject][ordered]@{schemaVersion=2;operation='AttachEvidence';taskId='submission-unsupported';attachments=@([pscustomobject]@{path=$attachmentPath;mediaType='text/plain';assertions=@('Successor attachment.')});captureCurrentState=$false;actorId='actor.fixture';sessionId='session.fixture';gridDataRoot=$tempRoot;caseStoreRoot=$tempRoot}
 [IO.File]::WriteAllText($attachInputPath,($attachInput|ConvertTo-Json -Depth 20),(New-Object Text.UTF8Encoding($false)))
 $attachPrepared=(& 'C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe' -NoProfile -ExecutionPolicy Bypass -File (Join-Path $healthRoot 'Invoke-GridRequestSubmission.ps1') -InputJsonPath $attachInputPath)|ConvertFrom-Json
 Assert-Equal 'AwaitingAuthorization' $attachPrepared.status ('Attach Evidence must materialize an exact successor authorization review. Response: ' + ($attachPrepared | ConvertTo-Json -Depth 10 -Compress))
 Assert-Equal 'SelectedContextAndAttachments' $attachPrepared.investigationIntake.authorizationScope 'Successor attachments must widen only to the explicit attachment read scope.'
 Assert-True (@($attachPrepared.authorizationReview.semanticBinding.targets) -contains [IO.Path]::GetFullPath($attachmentPath)) 'Successor authorization must name the exact attachment target.'
 Assert-Equal $parentSealBefore (Get-Content -LiteralPath (Join-Path $unsupported.CaseDirectory 'case-manifest.v1.json') -Raw) 'Preparing a successor must not reopen or mutate the sealed parent.'
 $attachAuthorizeInput=($attachPrepared.preparedInput|ConvertTo-Json -Depth 100)|ConvertFrom-Json
 $attachAuthorizeInput.operation='Authorize'
 $attachAuthorizePath=Join-Path $tempRoot 'attach-action-authorize.json'
 [IO.File]::WriteAllText($attachAuthorizePath,($attachAuthorizeInput|ConvertTo-Json -Depth 100),(New-Object Text.UTF8Encoding($false)))
 $attachAuthorized=(& 'C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe' -NoProfile -ExecutionPolicy Bypass -File (Join-Path $healthRoot 'Invoke-GridRequestSubmission.ps1') -InputJsonPath $attachAuthorizePath)|ConvertFrom-Json
 Assert-Equal 'Issued' $attachAuthorized.status 'A prepared successor must issue one exact authorization grant.'
 $attachExecuteInput=($attachPrepared.preparedInput|ConvertTo-Json -Depth 100)|ConvertFrom-Json
 $attachExecuteInput.operation='Execute'
 $attachExecuteInput|Add-Member -NotePropertyName authorizationGrantId -NotePropertyValue ([string]$attachAuthorized.grantId)
 $attachExecutePath=Join-Path $tempRoot 'attach-action-execute.json'
 [IO.File]::WriteAllText($attachExecutePath,($attachExecuteInput|ConvertTo-Json -Depth 100),(New-Object Text.UTF8Encoding($false)))
 try{
  $env:GRID_AUTHORIZATION_SECRET=[string]$attachAuthorized.authorizationSecret
  $attachExecutedOutput=@(& 'C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe' -NoProfile -ExecutionPolicy Bypass -File (Join-Path $healthRoot 'Invoke-GridRequestSubmission.ps1') -InputJsonPath $attachExecutePath) -join "`n"
  $attachExecutedExitCode=$LASTEXITCODE
 }finally{Remove-Item Env:GRID_AUTHORIZATION_SECRET -ErrorAction SilentlyContinue}
 Assert-Equal 0 $attachExecutedExitCode ('An authorized successor must execute through the machine JSON boundary. Response: '+$attachExecutedOutput)
 $attachExecuted=$attachExecutedOutput|ConvertFrom-Json
 Assert-Equal 'EvidenceComplete' $attachExecuted.status 'An authorized attachment successor must finish with sealed evidence.'
 Assert-Equal 1 @($attachExecuted.tasks).Count 'An authorized successor must return exactly one authorization-bound sealed task.'
 Assert-Equal ([string]$attachExecuted.execution.CaseId) ([string]@($attachExecuted.tasks)[0].taskId) 'The returned successor task identity must match its sealed request case.'
 Assert-Equal ([string]$attachExecuted.execution.CaseId) ([string]@($attachExecuted.tasks)[0].caseId) 'The returned successor case identity must match its sealed request case.'
 $diagnoseInputPath=Join-Path $tempRoot 'diagnose-action.json';$diagnoseInput=[pscustomobject][ordered]@{schemaVersion=2;operation='Diagnose';taskId='submission-unsupported';caseStoreRoot=$tempRoot}
 [IO.File]::WriteAllText($diagnoseInputPath,($diagnoseInput|ConvertTo-Json -Depth 10),(New-Object Text.UTF8Encoding($false)))
 $diagnoseResponse=(& 'C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe' -NoProfile -ExecutionPolicy Bypass -File (Join-Path $healthRoot 'Invoke-GridRequestSubmission.ps1') -InputJsonPath $diagnoseInputPath)|ConvertFrom-Json
 Assert-Equal 1 (@($diagnoseResponse.tasks).Count) ('Diagnose must return exactly one persisted successor task projection. Response: ' + ($diagnoseResponse | ConvertTo-Json -Depth 10 -Compress))
 Assert-Equal 'submission-unsupported' @($diagnoseResponse.tasks)[0].parentTaskId ('Diagnosis projection must retain immutable parent lineage. Response: ' + ($diagnoseResponse | ConvertTo-Json -Depth 10 -Compress))
 $diagnosis=$diagnoseResponse.execution
 Assert-Equal 'CapabilityRequired' $diagnosis.Status 'Unsupported diagnosis must persist CapabilityRequired without guessing.'
 Assert-Equal 'UNRESOLVED' $diagnosis.Result.finding 'CapabilityRequired must preserve the four-field unresolved finding.'
 Assert-True (@($diagnosis.Result.capabilityRequired.coverageGaps).Count -gt 0) 'CapabilityRequired must identify exact coverage gaps.'
 Assert-Equal 1 @($diagnosis.Result.capabilityRequired.resolutions).Count 'Unsupported coverage must produce one deterministic capability-gap route.'
 $gapResolution=@($diagnosis.Result.capabilityRequired.resolutions)[0]
 Assert-Equal 'grid.game.skyrimspecialedition.class.dialogue-scenes.diagnose' $gapResolution.requiredCapabilityId 'A missing pipeline must receive a reusable game-and-Class capability identity derived only from structured context.'
 Assert-Equal 'Preserve and identify the missing capability.' $gapResolution.requestedOutcome 'The gap route must preserve the structured desired outcome rather than infer intent from prose.'
 Assert-Equal 'CreationProposalRequired' $gapResolution.status 'An absent capability with no current candidate must route to reusable CODE proposal admission.'
 Assert-Equal 'CreateCapabilityProposal' $gapResolution.route 'CapabilityRequired must no longer be a dead-end result.'
 Assert-True $gapResolution.autoMayContinue 'AUTO may continue through inert capability-proposal planning.'
 Assert-True (-not $gapResolution.mutationAuthorized) 'Capability-gap routing must not grant mutation authority.'
 Assert-True ($gapResolution.resolutionSha256 -match '^[A-F0-9]{64}$') 'The capability-gap route must have a deterministic semantic digest.'
 $proposalDraft=@($diagnosis.Result.capabilityRequired.proposalDrafts)[0]
 Assert-Equal 'NeedsDesignEvidence' $proposalDraft.status 'AUTO must materialize an inert CODE design draft instead of stopping at an instruction string.'
 Assert-Equal $gapResolution.requiredCapabilityId $proposalDraft.requiredCapabilityId 'The proposal draft must bind the exact routed capability.'
 Assert-Equal 'scripts/games/skyrimspecialedition/' $proposalDraft.canonicalDirectory 'A game-owned capability draft must identify its canonical owner directory.'
 Assert-True (@($proposalDraft.requiredDesignInputs) -contains 'verificationPlan') 'The draft must require verification design before admission.'
 Assert-True (-not $proposalDraft.executable -and -not $proposalDraft.mutationAuthorized) 'An AUTO-created draft must be inert and grant no mutation authority.'
 Assert-True ($proposalDraft.proposalDraftSha256 -match '^[A-F0-9]{64}$') 'The proposal draft must have a deterministic semantic digest.'
 $gapArtifactPath=Join-Path $diagnosis.CaseDirectory 'result\capability-gap-resolutions.v1.json'
 Assert-True (Test-Path -LiteralPath $gapArtifactPath -PathType Leaf) 'The capability-gap route must be a first-class sealed case artifact.'
 $gapArtifact=Get-Content -LiteralPath $gapArtifactPath -Raw|ConvertFrom-Json
 Assert-Equal $gapResolution.resolutionSha256 @($gapArtifact.resolutions)[0].resolutionSha256 'The request result and first-class gap artifact must bind the same resolution.'
 $proposalArtifactPath=Join-Path $diagnosis.CaseDirectory 'result\capability-proposal-drafts.v1.json'
 Assert-True (Test-Path -LiteralPath $proposalArtifactPath -PathType Leaf) 'The inert CODE proposal draft must be a first-class sealed case artifact.'
 $proposalArtifact=Get-Content -LiteralPath $proposalArtifactPath -Raw|ConvertFrom-Json
 Assert-Equal $proposalDraft.proposalDraftSha256 @($proposalArtifact.proposalDrafts)[0].proposalDraftSha256 'The request result and proposal artifact must bind the same design draft.'
 Assert-True (-not $diagnosis.Result.repairState.applyEnabled) 'Repair must remain disabled without an exact specification.'

 $fixtureMo2=Join-Path $tempRoot 'capability-mo2';$fixtureProfile=Join-Path $fixtureMo2 'profiles\UNDEFEATED';$fixtureMods=Join-Path $fixtureMo2 'mods';$fixtureOverwrite=Join-Path $fixtureMo2 'overwrite';$fixtureGameData=Join-Path $fixtureMo2 'game\Data'
 foreach($directory in @($fixtureProfile,$fixtureMods,$fixtureOverwrite,$fixtureGameData)){New-Item -ItemType Directory -Path $directory -Force|Out-Null}
 [IO.File]::WriteAllText((Join-Path $fixtureProfile 'plugins.txt'),"# fixture`r`n",(New-Object Text.UTF8Encoding($false)))
 [IO.File]::WriteAllText((Join-Path $fixtureProfile 'modlist.txt'),"+Immersive Jewelry SSE`r`n+Immersive Jewelry 1.06a`r`n+Address Library for SKSE Plugins`r`n",(New-Object Text.UTF8Encoding($false)))
 $fixtureConfiguration="[General]`r`nselected_profile=UNDEFEATED`r`ngamePath=$($fixtureMo2.Replace('\','/'))/game`r`n[Settings]`r`nbase_directory=$($fixtureMo2.Replace('\','/'))`r`nmod_directory=$($fixtureMods.Replace('\','/'))`r`noverwrite_directory=$($fixtureOverwrite.Replace('\','/'))`r`n"
 [IO.File]::WriteAllText((Join-Path $fixtureMo2 'ModOrganizer.ini'),$fixtureConfiguration,(New-Object Text.UTF8Encoding($false)))
 foreach($provider in @(
  [pscustomobject]@{name='Immersive Jewelry SSE';modid=5336;version='1.05';archive='Immersive Jewelry SSE 1.05-5336-1-05.7z'},
  [pscustomobject]@{name='Immersive Jewelry 1.06a';modid=5336;version='1.06a';archive='Immersive Jewelry 1.06a-5336-1-06a.7z'},
  [pscustomobject]@{name='Address Library for SKSE Plugins';modid=32444;version='11';archive='Address Library for SKSE Plugins.7z'}
 )){
  $providerDirectory=Join-Path $fixtureMods $provider.name;New-Item -ItemType Directory -Path $providerDirectory -Force|Out-Null
  [IO.File]::WriteAllText((Join-Path $providerDirectory 'meta.ini'),("[General]`r`nmodid={0}`r`nversion={1}`r`ninstallationFile={2}`r`n"-f$provider.modid,$provider.version,$provider.archive),(New-Object Text.UTF8Encoding($false)))
 }

 $fixtureDownloads=Join-Path $fixtureMo2 'downloads';New-Item -ItemType Directory -Path $fixtureDownloads -Force|Out-Null
 $connectionId='reference.capture-fixture';$captureInstallationId='installation.capture-fixture'
 $profileHashBytes=[Text.Encoding]::UTF8.GetBytes($connectionId+"`n"+$fixtureProfile)
 $profileSha=[Security.Cryptography.SHA256]::Create();try{$captureProfileId='profile.mo2.'+([BitConverter]::ToString($profileSha.ComputeHash($profileHashBytes))).Replace('-','').ToLowerInvariant().Substring(0,24)}finally{$profileSha.Dispose()}
 $connectionDirectory=Join-Path $tempRoot 'connections';New-Item -ItemType Directory -Path $connectionDirectory -Force|Out-Null
 $connectionStore=[pscustomobject][ordered]@{schemaVersion=1;references=@([pscustomobject][ordered]@{id=$connectionId;installationId=$captureInstallationId;gameId='game.skyrim-special-edition';adapterId='adapter.mod-organizer-2';instanceDirectory=$fixtureMo2})}
 [IO.File]::WriteAllText((Join-Path $connectionDirectory 'mo2-installations.v1.json'),($connectionStore|ConvertTo-Json -Depth 10),(New-Object Text.UTF8Encoding($false)))

 $refreshBaselineCaseId='baseline-refresh-fixture'
 $refreshBaselineTransaction=New-GridCaseStoreTransaction -StoreRoot $tempRoot -CaseId $refreshBaselineCaseId
 Write-GridCaseStoreArtifact -Transaction $refreshBaselineTransaction -RelativePath 'case.json' -Value ([pscustomobject][ordered]@{schemaVersion=1;caseId=$refreshBaselineCaseId;createdAt=$legacyNow;status='Complete'})|Out-Null
 Write-GridCaseStoreArtifact -Transaction $refreshBaselineTransaction -RelativePath 'investigation-plan.json' -Value ([pscustomobject][ordered]@{schemaVersion=1;caseId=$refreshBaselineCaseId;gameId='skyrimspecialedition';installationId=$captureInstallationId;profileId=$captureProfileId})|Out-Null
 Write-GridCaseStoreArtifact -Transaction $refreshBaselineTransaction -RelativePath 'inventory\plugin-script-dependencies.v1.json' -Value ([pscustomobject][ordered]@{schemaVersion=1;status='Partial';recordCount=1;records=@()})|Out-Null
 Write-GridCaseStoreArtifact -Transaction $refreshBaselineTransaction -RelativePath 'repair\component-recovery-plan.v1.json' -Value ([pscustomobject][ordered]@{schemaVersion=1;planId='component-recovery-refresh-fixture';planSha256=('7'*64);components=@([pscustomobject][ordered]@{pluginName='3DNPC.esp';lineageCandidates=@([pscustomobject][ordered]@{name='Interesting NPCs - 4.5 to 4.54 Update'})})})|Out-Null
 $refreshBaselineRun=[pscustomobject][ordered]@{schemaVersion=1;runId='refresh-baseline-run';caseId=$refreshBaselineCaseId;state='Completed';startedAt=$legacyNow;completedAt=$legacyNow;planFingerprint=('8'*64);resourcePolicyVersion='grid.refresh-baseline-fixture.v1';gates=@();sufficiency=[pscustomobject]@{status='Complete'};checkpoints=@();primaryFailure=$null;secondaryFailures=@()}
 Seal-GridCaseStoreRun -Transaction $refreshBaselineTransaction -Run $refreshBaselineRun|Out-Null
 $refreshBaselineSealed=Seal-GridDiagnosticCase -Transaction $refreshBaselineTransaction -SemanticBaselineFingerprint ('9'*64) -Runs @($refreshBaselineRun)
 $partialBaselineTask=@(Get-GridRequestTaskHistory -StoreRoot $tempRoot|Where-Object taskId -CEQ $refreshBaselineCaseId)
 Assert-Equal 1 $partialBaselineTask.Count 'A sealed partial inventory with an evidence-bound component recovery plan must remain visible in Activity.'
 Assert-Equal 'BaselinePartial' ([string]$partialBaselineTask[0].terminalState) 'A partial recovery baseline must not be mislabeled complete.'
 Assert-True ([bool]$partialBaselineTask[0].repairState.recoveryPlanAvailable) 'A partial recovery baseline with a sealed plan must expose Review Repair.'

 $refreshParentEnvelope=New-GridRequestEnvelope -GameId skyrimspecialedition -InstallationId $captureInstallationId -ProfileId $captureProfileId -ClassId grid.class.installation-integrity -ClassRecipeVersion 1.1.0 -PlainText 'Refresh linked component sources.'
 $refreshParentPlan=Resolve-GridRequestPlan -Envelope $refreshParentEnvelope -ScriptsRoot $scriptsRoot
 $refreshParentIntake=New-GridInvestigationIntake -Problem $refreshParentEnvelope.claims.text -ExpectedBehavior 'Retain the linked recovery baseline.' -ReproductionLocation 'Fixture MO2 profile.' -DesiredOutcome 'Prepare an exact source refresh.' -AuthorizationScope SelectedContext -CaptureCurrentState:$false
 $refreshParentReview=New-GridRequestAuthorizationReview -Envelope $refreshParentEnvelope -RequestPlan $refreshParentPlan -ScriptsRoot $scriptsRoot -SubmissionId submission-refresh-parent -ActorId actor.fixture -SessionId session.fixture -ToolInputs @{} -InvestigationIntake $refreshParentIntake
 $refreshParentGrant=Grant-GridRequestAuthorization -AuthorizationReview $refreshParentReview -StoreRoot $tempRoot
 $script:refreshBaselineCaseId=$refreshBaselineCaseId;$script:refreshBaselineManifestSha256=[string]$refreshBaselineSealed.Manifest.manifestSha256
 $script:refreshEvidence=New-GridEvidenceItem -Parameter 'refresh-fixture' -Value 1 -Claim 'Fixture baseline is linked.' -SourceType 'Fixture' -ContextFingerprint 'refresh-context' -VerificationStatus Collected -CollectorName 'RequestExecution.Tests' -CollectorVersion '1.0.0'
 $script:refreshDiagnostic=New-GridUnresolvedDiagnosticResult -CaseId $refreshBaselineCaseId -State NeedsEvidence -ContextFingerprint 'refresh-context' -Evidence @($script:refreshEvidence) -Finding 'Fixture source archive is absent.' -NextStep 'Verify the exact downloaded source.'
 $refreshParentExecution=Invoke-GridAuthorizedRequestExecution -Envelope $refreshParentEnvelope -RequestPlan $refreshParentPlan -AuthorizationReview $refreshParentReview -AuthorizationGrantId $refreshParentGrant.Grant.grantId -AuthorizationSecret $refreshParentGrant.AuthorizationSecret -ActorId actor.fixture -SessionId session.fixture -ScriptsRoot $scriptsRoot -CaseStoreRoot $tempRoot -SubmissionId submission-refresh-parent -InvestigationIntake $refreshParentIntake -PostReadCollector {[pscustomobject]@{Status='Completed';TerminalState='Diagnosed';CaseId=$script:refreshBaselineCaseId;Manifest=[pscustomobject]@{manifestSha256=$script:refreshBaselineManifestSha256};DiagnosticResult=$script:refreshDiagnostic;Evidence=@($script:refreshEvidence)}}
 $refreshPreparePath=Join-Path $tempRoot 'refresh-recovery-sources-action.json'
 $refreshInput=[pscustomobject][ordered]@{schemaVersion=2;operation='RefreshRecoverySources';taskId='submission-refresh-parent';submissionId='action-refresh-recovery-fixture';attachments=@();actorId='actor.fixture';sessionId='session.fixture';gridDataRoot=$tempRoot;caseStoreRoot=$tempRoot}
 [IO.File]::WriteAllText($refreshPreparePath,($refreshInput|ConvertTo-Json -Depth 20),(New-Object Text.UTF8Encoding($false)))
 $refreshPreparedOutput=@(& 'C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe' -NoProfile -ExecutionPolicy Bypass -File (Join-Path $healthRoot 'Invoke-GridRequestSubmission.ps1') -InputJsonPath $refreshPreparePath)-join"`n"
 $refreshPrepared=$refreshPreparedOutput|ConvertFrom-Json -ErrorAction Stop
 Assert-Equal 'AwaitingAuthorization' $refreshPrepared.status ('Continue Repair must resolve the diagnosis successor to its sealed live-profile recovery baseline. Response: '+$refreshPreparedOutput)
 Assert-Equal $refreshBaselineCaseId ([string]$refreshPrepared.preparedInput.recoverySourceRefresh.parentCaseId) 'Source refresh must bind the linked baseline case, not the visible diagnosis task case.'
 Assert-Equal 'submission-refresh-parent' ([string]$refreshPrepared.preparedInput.parentTaskId) 'Source refresh must retain the visible task as its immutable successor parent.'

 $captureParentEnvelope=New-GridRequestEnvelope -GameId skyrimspecialedition -InstallationId $captureInstallationId -ProfileId $captureProfileId -ClassId grid.class.installation-integrity -ClassRecipeVersion 1.1.0 -PlainText 'Capture-state successor fixture.'
 $captureParentPlan=Resolve-GridRequestPlan -Envelope $captureParentEnvelope -ScriptsRoot $scriptsRoot
 $captureParentIntake=New-GridInvestigationIntake -Problem $captureParentEnvelope.claims.text -ExpectedBehavior 'Collect the selected fixture profile.' -ReproductionLocation 'Fixture MO2 profile.' -DesiredOutcome 'Seal the fixture evidence.' -AuthorizationScope SelectedContext -CaptureCurrentState:$false
 $captureParentReview=New-GridRequestAuthorizationReview -Envelope $captureParentEnvelope -RequestPlan $captureParentPlan -ScriptsRoot $scriptsRoot -SubmissionId submission-capture-parent -ActorId actor.fixture -SessionId session.fixture -ToolInputs @{} -InvestigationIntake $captureParentIntake
 $captureParentGrant=Grant-GridRequestAuthorization -AuthorizationReview $captureParentReview -StoreRoot $tempRoot
 Invoke-GridAuthorizedRequestExecution -Envelope $captureParentEnvelope -RequestPlan $captureParentPlan -AuthorizationReview $captureParentReview -AuthorizationGrantId $captureParentGrant.Grant.grantId -AuthorizationSecret $captureParentGrant.AuthorizationSecret -ActorId actor.fixture -SessionId session.fixture -ScriptsRoot $scriptsRoot -CaseStoreRoot $tempRoot -SubmissionId submission-capture-parent -InvestigationIntake $captureParentIntake|Out-Null
 $captureParentTasks=@(Get-GridRequestTaskHistory -StoreRoot $tempRoot|Where-Object taskId -CEQ 'submission-capture-parent')
 Assert-Equal 1 $captureParentTasks.Count 'Capture Current State regression requires one persisted predecessor task so the strict-mode lineage lookup executes.'

 $capturePreparePath=Join-Path $tempRoot 'capture-state-action.json'
 $captureInput=[pscustomobject][ordered]@{schemaVersion=2;operation='CaptureState';taskId='submission-capture-parent';submissionId='action-capture-fixture';attachments=@();actorId='actor.fixture';sessionId='session.fixture';gridDataRoot=$tempRoot;caseStoreRoot=$tempRoot}
 [IO.File]::WriteAllText($capturePreparePath,($captureInput|ConvertTo-Json -Depth 20),(New-Object Text.UTF8Encoding($false)))
 $capturePrepared=(& 'C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe' -NoProfile -ExecutionPolicy Bypass -File (Join-Path $healthRoot 'Invoke-GridRequestSubmission.ps1') -InputJsonPath $capturePreparePath)|ConvertFrom-Json
 Assert-Equal 'AwaitingAuthorization' $capturePrepared.status 'Capture Current State must prepare an exact successor review when the app omits captureCurrentState.'
 $captureAuthorizeInput=($capturePrepared.preparedInput|ConvertTo-Json -Depth 100)|ConvertFrom-Json;$captureAuthorizeInput.operation='Authorize'
 $captureAuthorizePath=Join-Path $tempRoot 'capture-state-authorize.json';[IO.File]::WriteAllText($captureAuthorizePath,($captureAuthorizeInput|ConvertTo-Json -Depth 100),(New-Object Text.UTF8Encoding($false)))
 $captureAuthorized=(& 'C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe' -NoProfile -ExecutionPolicy Bypass -File (Join-Path $healthRoot 'Invoke-GridRequestSubmission.ps1') -InputJsonPath $captureAuthorizePath)|ConvertFrom-Json
 Assert-Equal 'Issued' $captureAuthorized.status 'Capture Current State must issue its reviewed read grant.'
 $captureExecuteInput=($capturePrepared.preparedInput|ConvertTo-Json -Depth 100)|ConvertFrom-Json;$captureExecuteInput.operation='Execute';$captureExecuteInput|Add-Member authorizationGrantId ([string]$captureAuthorized.grantId)
 $captureExecutePath=Join-Path $tempRoot 'capture-state-execute.json';[IO.File]::WriteAllText($captureExecutePath,($captureExecuteInput|ConvertTo-Json -Depth 100),(New-Object Text.UTF8Encoding($false)))
 try{$env:GRID_AUTHORIZATION_SECRET=[string]$captureAuthorized.authorizationSecret;$captureExecutedOutput=@(& 'C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe' -NoProfile -ExecutionPolicy Bypass -File (Join-Path $healthRoot 'Invoke-GridRequestSubmission.ps1') -InputJsonPath $captureExecutePath)-join"`n";$captureExecutedExitCode=$LASTEXITCODE}finally{Remove-Item Env:GRID_AUTHORIZATION_SECRET -ErrorAction SilentlyContinue}
 Assert-Equal 0 $captureExecutedExitCode ('Capture Current State must cross the authorized machine boundary. Response: '+$captureExecutedOutput)
 $captureExecuted=$captureExecutedOutput|ConvertFrom-Json
 Assert-Equal 1 @($captureExecuted.tasks).Count 'Capture Current State must return its sealed successor task.'
 Assert-True ((Read-GridAuthorizationGrant -StoreRoot $tempRoot -GrantId $captureAuthorized.grantId).state -in @('Consumed','Failed')) 'Capture Current State must terminally consume its one-use grant after execution begins.'

 $gameplayEnvelope=New-GridRequestEnvelope -GameId skyrimspecialedition -InstallationId installation.fixture -ProfileId profile.fixture -ClassId grid.class.outfits-bodies-physics -ClassRecipeVersion 1.1.0 -CapabilityIds @('grid.capability.equipment.multiple-rings') -PlainText 'Can this profile wear rings on multiple fingers?'
 $gameplayPlan=Resolve-GridRequestPlan -Envelope $gameplayEnvelope -ScriptsRoot $scriptsRoot
 $gameplayIntake=New-GridInvestigationIntake -Problem $gameplayEnvelope.claims.text -ExpectedBehavior 'Assess installed capability coverage.' -ReproductionLocation 'UNDEFEATED profile.' -DesiredOutcome 'Report installed and current community coverage.' -AuthorizationScope SelectedContext -CaptureCurrentState:$true
 $gameplayPaths=@(@($fixtureMo2,$fixtureProfile,$fixtureMods,$fixtureOverwrite,$fixtureGameData)|ForEach-Object{[IO.Path]::GetFullPath([string]$_)})
 $gameplayInputs=@{'grid.tool.mo2'=[pscustomobject]@{mo2Root=$fixtureMo2;profileName='UNDEFEATED';authorizedReadPaths=$gameplayPaths}}
 $gameplayReview=New-GridRequestAuthorizationReview -Envelope $gameplayEnvelope -RequestPlan $gameplayPlan -ScriptsRoot $scriptsRoot -SubmissionId submission-gameplay-capability -ActorId actor.fixture -SessionId session.fixture -ToolInputs $gameplayInputs -InvestigationIntake $gameplayIntake
 Assert-Equal 1 @($gameplayReview.semanticBinding.capabilities|Where-Object capabilityId -eq 'grid.game.skyrimspecialedition.gameplay-capability.assess').Count 'Authorization must bind the planned gameplay assessment capability.'
 $gameplayGrant=Grant-GridRequestAuthorization -AuthorizationReview $gameplayReview -StoreRoot $tempRoot
 $gameplayExecution=Invoke-GridAuthorizedRequestExecution -Envelope $gameplayEnvelope -RequestPlan $gameplayPlan -AuthorizationReview $gameplayReview -AuthorizationGrantId $gameplayGrant.Grant.grantId -AuthorizationSecret $gameplayGrant.AuthorizationSecret -ActorId actor.fixture -SessionId session.fixture -ScriptsRoot $scriptsRoot -CaseStoreRoot $tempRoot -SubmissionId submission-gameplay-capability -InvestigationIntake $gameplayIntake
 Assert-Equal 'CapabilityAssessment' $gameplayExecution.Result.resultKind 'The authorized production request path must emit a capability assessment.'
 Assert-Equal 'Satisfied' $gameplayExecution.Result.capabilityAssessment.installedStatus 'MO2 metadata for both required Immersive Jewelry components must satisfy installed coverage.'
 Assert-True (Test-Path -LiteralPath (Join-Path $gameplayExecution.CaseDirectory 'evidence\gameplay-capability-assessment.v1.json')) 'The normalized assessment must be sealed as case evidence.'
 Assert-True (Test-GridDiagnosticCaseSeal -StoreRoot $tempRoot -CaseDirectory $gameplayExecution.CaseDirectory).IsValid 'The end-to-end gameplay capability result must remain fully sealed.'
 Write-Host 'PASS: random read grants, bound receipts, one-use consumption, exact sealed resume, and repeat identity are deterministic.'
}finally{if(Test-Path -LiteralPath $tempRoot){Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue}}
