$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent $PSScriptRoot
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $gameRoot)
Import-Module (Join-Path $scriptsRoot 'health\Grid.Health.psm1') -Force
. (Join-Path $gameRoot 'mo2\Grid.PluginStateBatch.ps1')
. (Join-Path $gameRoot 'health\actions\New-GridSkyrimPluginStateBatchProposal.ps1')
. (Join-Path $gameRoot 'health\actions\Invoke-GridAuthorizedPluginStateBatchHistory.ps1')
. (Join-Path $gameRoot 'health\actions\New-GridSkyrimPluginStateBatchPlanningCase.ps1')
. (Join-Path $scriptsRoot 'health\Grid.RequestMutation.ps1')

function Assert-True([bool]$Condition,[string]$Message){if(-not$Condition){throw $Message}}
function Assert-Equal($Expected,$Actual,[string]$Message){if($Expected-ne$Actual){throw "$Message Expected '$Expected', actual '$Actual'."}}
function Assert-Throws([scriptblock]$Action,[string]$Pattern,[string]$Message){try{&$Action;throw "$Message Expected '$Pattern'."}catch{if($_.Exception.Message-notmatch$Pattern){throw "$Message Unexpected: $($_.Exception.Message)"}}}

function Write-TestPlugin([string]$Path,[string[]]$Masters,[uint32]$Flags=0){
    $memory=New-Object IO.MemoryStream; $writer=New-Object IO.BinaryWriter($memory,[Text.Encoding]::ASCII,$true)
    try{
        foreach($master in $Masters){$bytes=[Text.Encoding]::GetEncoding(28591).GetBytes($master+[char]0);$writer.Write([Text.Encoding]::ASCII.GetBytes('MAST'));$writer.Write([uint16]$bytes.Length);$writer.Write($bytes)}
        $writer.Flush();$data=$memory.ToArray()
    }finally{$writer.Dispose();$memory.Dispose()}
    $stream=New-Object IO.FileStream($Path,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None);$binary=New-Object IO.BinaryWriter($stream)
    try{$binary.Write([Text.Encoding]::ASCII.GetBytes('TES4'));$binary.Write([uint32]$data.Length);$binary.Write($Flags);$binary.Write((New-Object byte[] 12));$binary.Write($data)}finally{$binary.Dispose();$stream.Dispose()}
}

function New-BatchGrant($Specification,[string]$Store,[string]$Suffix){
    $targets=@($Specification.operations|Sort-Object sequence|ForEach-Object{"plugin:$($_.pluginName)=Enabled"})
    $binding=New-GridAuthorizationSemanticBinding -ActorId actor.fixture -SessionId session.fixture -WorkspaceId "workspace-$Suffix" -RequestId "request-$Suffix" -SubmissionId "submission-$Suffix" `
        -EnvelopeSha256 ('A'*64) -PlanSha256 ('B'*64) -ScopeSha256 (Get-GridCanonicalJsonSha256 -InputObject $targets) -ProposalOrSpecificationId ([string]$Specification.specificationId) `
        -ProposalOrSpecificationSha256 ([string]$Specification.specificationSha256) -Capabilities @([pscustomobject]@{capabilityId='grid.game.skyrimspecialedition.plugin-state-batch.execute';capabilityVersion='1.0.0';adapterId='SkyrimSpecialEdition';adapterVersion='1'}) `
        -Targets $targets -NormalizedInput $Specification
    $issued=New-GridAuthorizationGrant -StoreRoot $Store -ReviewId "review-$Suffix" -AuthorityClass Mutation -SemanticBinding $binding -LifetimeMinutes 10
    [pscustomobject]@{Binding=$binding;GrantId=[string]$issued.Grant.grantId;Secret=[string]$issued.AuthorizationSecret}
}
function New-HistoryGrant($Transition,[string]$Store,[string]$Suffix){
    $binding=New-GridAuthorizationSemanticBinding -ActorId actor.fixture -SessionId session.fixture -WorkspaceId "workspace-$Suffix" -RequestId "request-$Suffix" -SubmissionId "submission-$Suffix" `
        -EnvelopeSha256 ('A'*64) -PlanSha256 ('B'*64) -ScopeSha256 (Get-GridCanonicalJsonSha256 -InputObject @($Transition.targets)) -ProposalOrSpecificationId ([string]$Transition.transitionId) `
        -ProposalOrSpecificationSha256 ([string]$Transition.transitionSha256) -Capabilities @([pscustomobject]@{capabilityId='grid.game.skyrimspecialedition.plugin-state-batch.history.execute';capabilityVersion='1.0.0';adapterId='SkyrimSpecialEdition';adapterVersion='1'}) `
        -Targets @($Transition.targets) -NormalizedInput $Transition.normalizedInput
    $issued=New-GridAuthorizationGrant -StoreRoot $Store -ReviewId "review-$Suffix" -AuthorityClass Mutation -SemanticBinding $binding -LifetimeMinutes 10
    [pscustomobject]@{Binding=$binding;GrantId=[string]$issued.Grant.grantId;Secret=[string]$issued.AuthorizationSecret}
}

$root=Join-Path $env:TEMP ('grid-plugin-batch-'+[Guid]::NewGuid().ToString('N'));$store=Join-Path $root 'store';$mo2=Join-Path $root 'instance';$profileDir=Join-Path $mo2 'profiles\Fixture';$mods=Join-Path $mo2 'mods';$feature=Join-Path $mods 'Feature';$gameData=Join-Path $root 'game\Data';$transactions=Join-Path $root 'transactions'
foreach($directory in @($profileDir,$feature,$gameData,$transactions)){New-Item -ItemType Directory -Path $directory -Force|Out-Null}
$utf8=New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $profileDir 'modlist.txt'),"+Feature`r`n",$utf8)
[IO.File]::WriteAllText((Join-Path $profileDir 'loadorder.txt'),"Skyrim.esm`r`nCreation.esl`r`nBase.esp`r`nPatch.esp`r`nOrphan.esp`r`n",$utf8)
[IO.File]::WriteAllText((Join-Path $profileDir 'plugins.txt'),"# fixture`r`nBase.esp`r`nPatch.esp`r`nOrphan.esp`r`n",$utf8)
[IO.File]::WriteAllText((Join-Path (Split-Path -Parent $gameData) 'Skyrim.ccc'),"Creation.esl`r`n",$utf8)
Write-TestPlugin (Join-Path $gameData 'Skyrim.esm') @() 1
Write-TestPlugin (Join-Path $gameData 'Creation.esl') @('Skyrim.esm') 0
Write-TestPlugin (Join-Path $feature 'Base.esp') @('Skyrim.esm') 0
Write-TestPlugin (Join-Path $feature 'Patch.esp') @('Skyrim.esm','Base.esp') 0x200
Write-TestPlugin (Join-Path $feature 'Orphan.esp') @('Skyrim.esm','Missing.esm') 0

try{
    $caseId='baseline-plugin-batch-fixture';$transaction=New-GridCaseStoreTransaction -StoreRoot $store -CaseId $caseId
    Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'case.json' -Value ([pscustomobject]@{schemaVersion=1;caseId=$caseId})|Out-Null
    $protectedFiles=@(@('plugins.txt','modlist.txt','loadorder.txt')|ForEach-Object{$path=Join-Path $profileDir $_;[pscustomobject]@{path=$path;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}})
    $snapshot=[pscustomobject]@{schemaVersion=1;caseId=$caseId;files=$protectedFiles}
    Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'snapshots/protected-after.v1.json' -Value $snapshot|Out-Null
    Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'investigation-plan.json' -Value ([pscustomobject]@{schemaVersion=2;installationId='installation.fixture';profileId='profile.fixture'})|Out-Null
    $spidText=(([pscustomobject]@{virtualPath='Fixture_DISTR.ini';providerName='Feature';pluginName='Patch.esp';status='disabled';ruleCount=2}|ConvertTo-Json -Compress)+"`n"+([pscustomobject]@{virtualPath='Orphan_DISTR.ini';providerName='Feature';pluginName='Orphan.esp';status='disabled';ruleCount=1}|ConvertTo-Json -Compress)+"`n")
    Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'inventory/spid-source-issues.v1.ndjson' -Text $spidText|Out-Null
    $run=[pscustomobject]@{schemaVersion=1;runId='run-fixture';caseId=$caseId;state='Completed';startedAt=[DateTimeOffset]::UtcNow.ToString('o');completedAt=[DateTimeOffset]::UtcNow.ToString('o');planFingerprint=('A'*64);resourcePolicyVersion='fixture';gates=@();sufficiency=[pscustomobject]@{schemaVersion=1;status='SufficientForRequestedDiagnosis'};checkpoints=@();primaryFailure=$null;secondaryFailures=@()}
    Seal-GridCaseStoreRun -Transaction $transaction -Run $run|Out-Null;$sealed=Seal-GridDiagnosticCase -Transaction $transaction -SemanticBaselineFingerprint ('B'*64) -Runs @($run)
    $specPath=Join-Path $root 'proposal\plugin-state-batch.v1.json'
    $proposal=New-GridSkyrimPluginStateBatchProposal -CaseStoreRoot $store -BaselineCaseDirectory $sealed.CaseDirectory -CaseId repair-fixture -ContextFingerprint ('C'*64) -EvidenceFingerprint ('D'*64) -Mo2Root $mo2 -Profile Fixture -GameDataRoot $gameData -RequestedPlugins Patch.esp -OutputPath $specPath -PassThru
    Assert-Equal 'AwaitingAuthorization' $proposal.status 'Planner must emit an inert proposal.'
    Assert-Equal 2 @($proposal.specification.operations).Count 'Dependency closure must include the disabled master and requested patch.'
    Assert-Equal 'Base.esp' $proposal.specification.operations[0].pluginName 'Master must precede dependent.'
    Assert-Equal 'Patch.esp' $proposal.specification.operations[1].pluginName 'Requested patch must follow its master.'
    Assert-Equal 1 $proposal.specification.slotUsage.fullAdded 'The full master must consume one full slot.'
    Assert-Equal 1 $proposal.specification.slotUsage.lightAdded 'The light-flagged patch must consume one light slot.'
    $planning=New-GridSkyrimPluginStateBatchPlanningCase -CaseStoreRoot $store -SpecificationPath $specPath -RepairCaseId repair-plugin-batch-fixture -PassThru
    Assert-Equal 'Sealed' $planning.status 'The exact plugin batch must become a sealed first-class repair task.'
    $task=@(Get-GridRequestTaskHistory -StoreRoot $store|Where-Object taskId -eq 'repair-plugin-batch-fixture')
    Assert-Equal 1 $task.Count 'The task index must discover exactly one plugin batch planning case.'
    Assert-Equal 'PluginStateBatch' $task[0].repairState.repairKind 'The task must preserve its plugin-batch repair kind.'
    Assert-Equal 'NotApplied' $task[0].repairState.historyState 'A new plugin batch task must begin unapplied.'
    $before=(Get-FileHash -LiteralPath (Join-Path $profileDir 'plugins.txt') -Algorithm SHA256).Hash
    $executor=Join-Path $gameRoot 'mo2\Set-GridPluginStateBatch.ps1'
    Assert-Throws {&$executor -AuthorizedSpecification $proposal.specification -CaseStoreRoot $store -TransactionRoot $transactions -WhatIf} 'PluginBatchAuthorizationRequired' 'Direct low-level mutation must fail closed.'
    Assert-Throws {New-GridSkyrimPluginStateBatchProposal -CaseStoreRoot $store -BaselineCaseDirectory $sealed.CaseDirectory -CaseId overflow -ContextFingerprint ('C'*64) -EvidenceFingerprint ('D'*64) -Mo2Root $mo2 -Profile Fixture -GameDataRoot $gameData -RequestedPlugins Patch.esp -MaximumFullPlugins 1 -OutputPath (Join-Path $root 'overflow.json')} 'PluginBatchFullSlotLimit' 'Planner must reject full-slot overflow.'
    Assert-Throws {New-GridSkyrimPluginStateBatchProposal -CaseStoreRoot $store -BaselineCaseDirectory $sealed.CaseDirectory -CaseId missing-master -ContextFingerprint ('C'*64) -EvidenceFingerprint ('D'*64) -Mo2Root $mo2 -Profile Fixture -GameDataRoot $gameData -RequestedPlugins Orphan.esp -OutputPath (Join-Path $root 'missing-master.json')} 'PluginBatchDependencyEntryMissing: Missing.esm' 'Planner must reject an absent dependency instead of activating an orphan.'

    $wrapper=Join-Path $gameRoot 'health\actions\Invoke-GridAuthorizedPluginStateBatch.ps1';$whatIfGrant=New-BatchGrant $proposal.specification $store whatif
    $whatIf=&$wrapper -SpecificationPath $specPath -CurrentContextFingerprint ('C'*64) -CurrentEvidenceFingerprint ('D'*64) -AuthorizationGrantId $whatIfGrant.GrantId -AuthorizationSecret $whatIfGrant.Secret -AuthorizationStoreRoot $store -SemanticBinding $whatIfGrant.Binding -CaseStoreRoot $store -TransactionRoot $transactions -WhatIf -PassThru
    Assert-Equal 'Planned' $whatIf.status 'WhatIf must validate without mutation.';Assert-Equal $before (Get-FileHash -LiteralPath (Join-Path $profileDir 'plugins.txt') -Algorithm SHA256).Hash 'WhatIf must preserve plugins.txt.'
    Assert-Throws {&$wrapper -SpecificationPath $specPath -CurrentContextFingerprint ('C'*64) -CurrentEvidenceFingerprint ('D'*64) -AuthorizationGrantId $whatIfGrant.GrantId -AuthorizationSecret $whatIfGrant.Secret -AuthorizationStoreRoot $store -SemanticBinding $whatIfGrant.Binding -CaseStoreRoot $store -TransactionRoot $transactions -PassThru} 'AuthorizationReplayRefused' 'A consumed batch grant must not replay.'

    $patchPath=Join-Path $feature 'Patch.esp';$patchBytes=[IO.File]::ReadAllBytes($patchPath);[IO.File]::WriteAllBytes($patchPath,($patchBytes+([byte]0)))
    $providerDriftGrant=New-BatchGrant $proposal.specification $store provider-drift
    Assert-Throws {&$wrapper -SpecificationPath $specPath -CurrentContextFingerprint ('C'*64) -CurrentEvidenceFingerprint ('D'*64) -AuthorizationGrantId $providerDriftGrant.GrantId -AuthorizationSecret $providerDriftGrant.Secret -AuthorizationStoreRoot $store -SemanticBinding $providerDriftGrant.Binding -CaseStoreRoot $store -TransactionRoot $transactions -PassThru} 'PluginBatchCurrentStateChanged' 'Executor must reject a winning plugin that changed after planning.'
    [IO.File]::WriteAllBytes($patchPath,$patchBytes)

    $grant=New-BatchGrant $proposal.specification $store apply
    $result=&$wrapper -SpecificationPath $specPath -CurrentContextFingerprint ('C'*64) -CurrentEvidenceFingerprint ('D'*64) -AuthorizationGrantId $grant.GrantId -AuthorizationSecret $grant.Secret -AuthorizationStoreRoot $store -SemanticBinding $grant.Binding -CaseStoreRoot $store -TransactionRoot $transactions -Confirm:$false -PassThru
    Assert-Equal 'Verified' $result.status 'Atomic apply must verify.';Assert-Equal 'Consumed' $result.authorizationStatus 'Apply grant must be terminal.'
    $states=Get-GridPluginStateBatchEntries -Text ([IO.File]::ReadAllText((Join-Path $profileDir 'plugins.txt')));Assert-Equal 2 @($states|Where-Object{$_.name-in@('Base.esp','Patch.esp')-and$_.state-eq'Enabled'}).Count 'Both dependency-closed plugins must be enabled.'
    Assert-Equal $before (Get-FileHash -LiteralPath $result.execution.backupPath -Algorithm SHA256).Hash 'Backup must be byte-exact.'

    $pluginsPath=Join-Path $profileDir 'plugins.txt';$appliedBytes=[IO.File]::ReadAllBytes($pluginsPath);[IO.File]::WriteAllBytes($pluginsPath,($appliedBytes+([byte]0)))
    $bridgeTransactionRoot=Join-Path $store 'repair-transactions';$bridgeTransactionDirectory=Join-Path $bridgeTransactionRoot ('plugin-batch-'+[string]$proposal.specification.specificationSha256)
    New-Item -ItemType Directory -Path $bridgeTransactionRoot -Force|Out-Null
    Move-Item -LiteralPath (Join-Path $transactions ('plugin-batch-'+[string]$proposal.specification.specificationSha256)) -Destination $bridgeTransactionDirectory
    $sealedSpecPath=Join-Path ([string]$planning.caseDirectory) 'repair\plugin-state-batch-specification.v1.json'
    $undo=Get-GridSkyrimPluginStateBatchHistoryTransition -SpecificationPath $sealedSpecPath -TransactionRoot $bridgeTransactionRoot -Direction Undo;$undoDriftGrant=New-HistoryGrant $undo $store undo-drift
    Assert-Throws {Invoke-GridAuthorizedPluginStateBatchHistory -SpecificationPath $sealedSpecPath -TransactionRoot $bridgeTransactionRoot -Direction Undo -AuthorizationGrantId $undoDriftGrant.GrantId -AuthorizationSecret $undoDriftGrant.Secret -AuthorizationStoreRoot $store -SemanticBinding $undoDriftGrant.Binding -CaseStoreRoot $store -Confirm:$false -PassThru} 'PluginBatchHistoryTargetDrift' 'Undo must reject a plugins.txt file changed after apply.'
    [IO.File]::WriteAllBytes($pluginsPath,$appliedBytes)
    $undoRequest=[pscustomobject]@{operation='PrepareMutation';mutationAction='RollBack';taskId='repair-plugin-batch-fixture';actorId='actor.bridge';sessionId='session.bridge';submissionId='submission-bridge-undo'}
    $undoReview=Invoke-GridRequestMutationOperation -Request $undoRequest -StoreRoot $store -ScriptsRoot $scriptsRoot
    Assert-Equal 'grid.game.skyrimspecialedition.plugin-state-batch.history.execute' $undoReview.authorizationReview.semanticBinding.capabilities[0].capabilityId 'Edit Undo must review the plugin-batch history capability.'
    $undoRequest.operation='Authorize';$undoAuthorization=Invoke-GridRequestMutationOperation -Request $undoRequest -StoreRoot $store -ScriptsRoot $scriptsRoot
    $undoRequest.operation='Execute';$undoRequest|Add-Member -NotePropertyName authorizationGrantId -NotePropertyValue $undoAuthorization.grantId;$undoRequest|Add-Member -NotePropertyName authorizationSecret -NotePropertyValue $undoAuthorization.authorizationSecret
    $undoResponse=Invoke-GridRequestMutationOperation -Request $undoRequest -StoreRoot $store -ScriptsRoot $scriptsRoot;$undoReceipt=$undoResponse.mutationResult
    Assert-Equal 'Undone' $undoReceipt.state 'Undo must restore the original image.';Assert-Equal $before (Get-FileHash -LiteralPath (Join-Path $profileDir 'plugins.txt') -Algorithm SHA256).Hash 'Undo must be byte-exact.'
    Assert-Equal 'Undone' $undoResponse.tasks[0].repairState.historyState 'Task refresh must expose the new Undo state.'
    $redoRequest=[pscustomobject]@{operation='PrepareMutation';mutationAction='ApplyRepair';taskId='repair-plugin-batch-fixture';actorId='actor.bridge';sessionId='session.bridge';submissionId='submission-bridge-redo'}
    $redoReview=Invoke-GridRequestMutationOperation -Request $redoRequest -StoreRoot $store -ScriptsRoot $scriptsRoot
    Assert-Equal 'grid.game.skyrimspecialedition.plugin-state-batch.history.execute' $redoReview.authorizationReview.semanticBinding.capabilities[0].capabilityId 'Edit Redo must review the plugin-batch history capability.'
    $redoRequest.operation='Authorize';$redoAuthorization=Invoke-GridRequestMutationOperation -Request $redoRequest -StoreRoot $store -ScriptsRoot $scriptsRoot
    $redoRequest.operation='Execute';$redoRequest|Add-Member -NotePropertyName authorizationGrantId -NotePropertyValue $redoAuthorization.grantId;$redoRequest|Add-Member -NotePropertyName authorizationSecret -NotePropertyValue $redoAuthorization.authorizationSecret
    $redoResponse=Invoke-GridRequestMutationOperation -Request $redoRequest -StoreRoot $store -ScriptsRoot $scriptsRoot;$redoReceipt=$redoResponse.mutationResult
    Assert-Equal 'Applied' $redoReceipt.state 'Redo must restore the exact applied image.';Assert-Equal $proposal.specification.protectedState.expectedAfterPluginsFileSha256 (Get-FileHash -LiteralPath (Join-Path $profileDir 'plugins.txt') -Algorithm SHA256).Hash 'Redo must be byte-exact.'
    Write-Host 'PASS: plugin dependency closure, slot gates, one-use authorization, atomic apply, and exact Undo/Redo are verified.'
}finally{if(Test-Path -LiteralPath $root){Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue}}
