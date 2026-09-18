$ErrorActionPreference='Stop'
$gameRoot=Split-Path -Parent $PSScriptRoot
$scriptsRoot=Split-Path -Parent (Split-Path -Parent $gameRoot)
Import-Module (Join-Path $scriptsRoot 'health\Grid.Health.psm1') -Force
Import-Module (Join-Path $gameRoot 'health\Grid.Health.Skyrim.psm1') -Force
. (Join-Path $scriptsRoot 'health\Grid.RequestMutation.ps1')
function Assert-True([bool]$Condition,[string]$Message){if(-not$Condition){throw $Message}}
function Assert-Equal($Expected,$Actual,[string]$Message){if($Expected-ne$Actual){throw "$Message Expected '$Expected', actual '$Actual'."}}
function Assert-Throws([scriptblock]$Action,[string]$Pattern,[string]$Message){try{&$Action;throw "$Message Expected '$Pattern'."}catch{if($_.Exception.Message-notmatch$Pattern){throw "$Message Unexpected: $($_.Exception.Message)"}}}
function Write-EslFixturePlugin([string]$Path){
    $stream=[IO.File]::Open($Path,'Create','Write','None');$writer=New-Object IO.BinaryWriter($stream)
    try{$writer.Write([Text.Encoding]::ASCII.GetBytes('TES4'));$writer.Write([uint32]0);$writer.Write([uint32]0);$writer.Write((New-Object byte[] 12))}finally{$writer.Dispose();$stream.Dispose()}
}
$root=Join-Path $env:TEMP ('grid-esl-flag-'+[Guid]::NewGuid().ToString('N'));$store=Join-Path $root 'store';$mo2=Join-Path $root 'instance';$profileDir=Join-Path $mo2 'profiles\Fixture';$mods=Join-Path $mo2 'mods';$feature=Join-Path $mods 'Feature';$gameData=Join-Path $root 'game\Data';$transactions=Join-Path $root 'transactions'
foreach($directory in @($profileDir,$feature,$gameData,$transactions)){New-Item -ItemType Directory -Path $directory -Force|Out-Null}
$utf8=New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $profileDir 'modlist.txt'),"+Feature`r`n",$utf8)
[IO.File]::WriteAllText((Join-Path $profileDir 'plugins.txt'),"*Candidate.esp`r`n",$utf8)
[IO.File]::WriteAllText((Join-Path $profileDir 'loadorder.txt'),"Candidate.esp`r`n",$utf8)
$plugin=Join-Path $feature 'Candidate.esp';Write-EslFixturePlugin $plugin
try{
    $caseId='esl-evidence-fixture';$transaction=New-GridCaseStoreTransaction -StoreRoot $store -CaseId $caseId
    Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'case.json' -Value ([pscustomobject]@{schemaVersion=1;caseId=$caseId})|Out-Null
    Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'investigation-plan.json' -Value ([pscustomobject]@{schemaVersion=2;installationId='installation.fixture';profileId='Fixture'})|Out-Null
    $eligibility=[pscustomobject][ordered]@{schemaVersion=1;status='EligibleWithoutCompaction';pluginName='Candidate.esp';classification='HeaderFlagOnly';newRecordCount=1;maximumObjectId='000800';hasNewCell=$false;hasEsmFlag=$false;warning=$null;contextFingerprint=('C'*64);supportingEvidenceIds=@('evidence-fixture');observedAt=[DateTimeOffset]::UtcNow.ToString('o');mutationAuthorized=$false}
    $eligibilityPath=Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'xedit-esl-eligibility.v1.json' -Value $eligibility
    $now=[DateTimeOffset]::UtcNow.ToString('o');$run=[pscustomobject]@{schemaVersion=1;runId='run-esl-fixture';caseId=$caseId;state='Completed';startedAt=$now;completedAt=$now;planFingerprint=('A'*64);resourcePolicyVersion='fixture';gates=@();sufficiency=[pscustomobject]@{schemaVersion=1;status='SufficientForRequestedDiagnosis'};checkpoints=@();primaryFailure=$null;secondaryFailures=@()}
    Seal-GridCaseStoreRun -Transaction $transaction -Run $run|Out-Null;$sealed=Seal-GridDiagnosticCase -Transaction $transaction -SemanticBaselineFingerprint ('B'*64) -Runs @($run)
    $eligibilityPath=Join-Path $sealed.CaseDirectory 'xedit-esl-eligibility.v1.json';$specPath=Join-Path $root 'proposal\esl-flag.v1.json'
    $proposal=New-GridSkyrimEslFlagProposal -EligibilityResultPath $eligibilityPath -CaseStoreRoot $store -EvidenceCaseDirectory $sealed.CaseDirectory -CaseId 'repair-esl-fixture' -ContextFingerprint ('C'*64) -Mo2Root $mo2 -Profile Fixture -GameDataRoot $gameData -OutputPath $specPath -PassThru
    Assert-Equal 'AwaitingAuthorization' $proposal.status 'Eligible evidence must produce only an inert proposal.'
    Assert-True (-not$proposal.changedExternalState) 'Planning must not mutate the plugin.'
    $before=[IO.File]::ReadAllBytes($plugin);Assert-Equal 0 ([BitConverter]::ToUInt32($before,8)-band0x200) 'Fixture must begin without the ESL flag.'
    $planning=New-GridSkyrimEslFlagPlanningCase -CaseStoreRoot $store -SpecificationPath $specPath -RepairCaseId repair-esl-fixture -PassThru
    $task=@(Get-GridRequestTaskHistory -StoreRoot $store|Where-Object taskId -eq 'repair-esl-fixture');Assert-Equal 1 $task.Count 'A sealed ESL proposal must become one Grid repair task.';Assert-Equal 'EslFlag' $task[0].repairState.repairKind 'The repair task must preserve its ESL kind.'
    $apply=[pscustomobject]@{operation='PrepareMutation';mutationAction='ApplyRepair';taskId='repair-esl-fixture';actorId='actor.fixture';sessionId='session.fixture';submissionId='submission.apply'}
    $prepared=Invoke-GridRequestMutationOperation -Request $apply -StoreRoot $store -ScriptsRoot $scriptsRoot;Assert-Equal 'AwaitingAuthorization' $prepared.status 'Grid must prepare the ESL repair for explicit authorization.'
    $apply.operation='Authorize';$authorized=Invoke-GridRequestMutationOperation -Request $apply -StoreRoot $store -ScriptsRoot $scriptsRoot
    $apply.operation='Execute';$apply|Add-Member authorizationGrantId ([string]$authorized.grantId);$apply|Add-Member authorizationSecret ([string]$authorized.authorizationSecret)
    $executed=Invoke-GridRequestMutationOperation -Request $apply -StoreRoot $store -ScriptsRoot $scriptsRoot;$result=$executed.mutationResult
    Assert-Equal 'Verified' $result.status 'Authorized header-only flagging must verify the exact after image.'
    $after=[IO.File]::ReadAllBytes($plugin);Assert-Equal 0x200 ([BitConverter]::ToUInt32($after,8)-band0x200) 'Executor must set the ESL bit.'
    Assert-Equal $before.Length $after.Length 'Header-only flagging must preserve plugin length.'
    $changed=@();for($i=0;$i-lt$before.Length;$i++){if($before[$i]-ne$after[$i]){$changed+=$i}}
    Assert-True (@($changed|Where-Object{$_-lt8-or$_-gt11}).Count-eq0) 'Header-only flagging must not change bytes outside the TES4 flags field.'
    Assert-Equal $proposal.specification.plugin.beforeSha256 (Get-FileHash -LiteralPath $result.backupPath -Algorithm SHA256).Hash 'Backup must be byte-exact.'
    Assert-Throws {Invoke-GridRequestMutationOperation -Request $apply -StoreRoot $store -ScriptsRoot $scriptsRoot} 'RepairHistoryDirectionInvalid|AuthorizationReplayRefused' 'Applied repair state or one-use authorization must prevent replay.'
    $undo=[pscustomobject]@{operation='PrepareMutation';mutationAction='RollBack';taskId='repair-esl-fixture';actorId='actor.fixture';sessionId='session.fixture';submissionId='submission.undo'}
    [void](Invoke-GridRequestMutationOperation -Request $undo -StoreRoot $store -ScriptsRoot $scriptsRoot);$undo.operation='Authorize';$undoGrant=Invoke-GridRequestMutationOperation -Request $undo -StoreRoot $store -ScriptsRoot $scriptsRoot
    $undo.operation='Execute';$undo|Add-Member authorizationGrantId ([string]$undoGrant.grantId);$undo|Add-Member authorizationSecret ([string]$undoGrant.authorizationSecret);$undoResult=Invoke-GridRequestMutationOperation -Request $undo -StoreRoot $store -ScriptsRoot $scriptsRoot
    Assert-Equal 'Verified' $undoResult.mutationResult.status 'Grid must verify byte-exact ESL Undo.';Assert-Equal $proposal.specification.plugin.beforeSha256 (Get-FileHash -LiteralPath $plugin -Algorithm SHA256).Hash 'Undo must restore the exact before image.'
    $redo=[pscustomobject]@{operation='PrepareMutation';mutationAction='ApplyRepair';taskId='repair-esl-fixture';actorId='actor.fixture';sessionId='session.fixture';submissionId='submission.redo'}
    [void](Invoke-GridRequestMutationOperation -Request $redo -StoreRoot $store -ScriptsRoot $scriptsRoot);$redo.operation='Authorize';$redoGrant=Invoke-GridRequestMutationOperation -Request $redo -StoreRoot $store -ScriptsRoot $scriptsRoot
    $redo.operation='Execute';$redo|Add-Member authorizationGrantId ([string]$redoGrant.grantId);$redo|Add-Member authorizationSecret ([string]$redoGrant.authorizationSecret);$redoResult=Invoke-GridRequestMutationOperation -Request $redo -StoreRoot $store -ScriptsRoot $scriptsRoot
    Assert-Equal 'Verified' $redoResult.mutationResult.status 'Grid must verify byte-exact ESL Redo.';Assert-Equal $proposal.specification.plugin.expectedAfterSha256 (Get-FileHash -LiteralPath $plugin -Algorithm SHA256).Hash 'Redo must restore the exact reviewed after image.'
    Write-Host 'PASS: Grid exposes xEdit-audited ESL flagging through its repair bridge with exact apply, backup, replay refusal, Undo, and Redo.'
}finally{if(Test-Path -LiteralPath $root){Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue}}
