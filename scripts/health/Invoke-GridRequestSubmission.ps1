#requires -Version 5.1
<# .SYNOPSIS Fixed JSON-file entry point for Grid investigation intake, evidence capture, diagnosis, and review. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$InputJsonPath)
$ErrorActionPreference='Stop'
# This file is a machine JSON boundary. PowerShell's auxiliary streams are not
# part of the response contract and must never be merged into redirected stdout.
$WarningPreference='SilentlyContinue'
$InformationPreference='SilentlyContinue'
$VerbosePreference='SilentlyContinue'
$DebugPreference='SilentlyContinue'
$ProgressPreference='SilentlyContinue'
Set-StrictMode -Version Latest
try {
    if (-not [IO.Path]::IsPathRooted($InputJsonPath)) { throw 'InputJsonPath must be absolute.' }
    $item=Get-Item -LiteralPath $InputJsonPath -ErrorAction Stop; if([long]$item.Length -gt 2MB){throw 'Input JSON exceeds 2 MiB.'}
    $input=Get-Content -LiteralPath $item.FullName -Raw | ConvertFrom-Json -ErrorAction Stop
    if ($null -ne $input.PSObject.Properties['authorizationSecret']) { throw 'AuthorizationSecretTransportInvalid: authorizationSecret must not be persisted in the submission JSON; supply GRID_AUTHORIZATION_SECRET to the child process.' }
    if (-not [string]::IsNullOrWhiteSpace([string]$env:GRID_AUTHORIZATION_SECRET)) {
        $transportSecret = [string]$env:GRID_AUTHORIZATION_SECRET
        Remove-Item Env:GRID_AUTHORIZATION_SECRET -ErrorAction SilentlyContinue
        $input | Add-Member -NotePropertyName authorizationSecret -NotePropertyValue $transportSecret
    }
    if([int]$input.schemaVersion -ne 2){throw 'Unsupported submission schemaVersion. Current authorization operations require v2.'}
    Import-Module (Join-Path $PSScriptRoot 'Grid.Health.psm1') -Force -DisableNameChecking
    $scriptsRoot=Split-Path -Parent $PSScriptRoot
    $root=Get-GridDiagnosticStoreRoot -Root ([string]$input.caseStoreRoot) -Ensure

    $directMutationAction = if([string]$input.operation -in @('ApplyRepair','RollBack')){[string]$input.operation}else{$null}
    $boundMutationAction = if($null-ne$input.PSObject.Properties['mutationAction']){[string]$input.mutationAction}else{$null}
    if($directMutationAction -or ($boundMutationAction -and [string]$input.operation -in @('Authorize','Execute'))){
        if($directMutationAction){
            $input|Add-Member -NotePropertyName mutationAction -NotePropertyValue $directMutationAction -Force
            $input.operation='PrepareMutation'
        }
        . (Join-Path $PSScriptRoot 'Grid.RequestMutation.ps1')
        $mutationResponse=Invoke-GridRequestMutationOperation -Request $input -StoreRoot $root -ScriptsRoot $scriptsRoot
        $mutationResponse|ConvertTo-Json -Depth 100 -Compress
        exit 0
    }

    if([string]$input.operation -eq 'ReviewRepair'){
        $requestedTaskId=[string]$input.taskId;$tasks=@(Get-GridRequestTaskHistory -StoreRoot $root|Where-Object{[string]$_.taskId -ceq $requestedTaskId});if($tasks.Count-ne 1){throw 'TaskNotFound: task identity is absent or ambiguous.'}
        $task=$tasks[0];$taskDirectory=[string]$task.caseDirectory
        if([string]::IsNullOrWhiteSpace($taskDirectory)){throw 'RecoveryPlanUnavailable: the task has no sealed case directory.'}
        $baseline=Resolve-GridRequestRecoveryBaseline -StoreRoot $root -TaskCaseDirectory $taskDirectory
        if($null-eq$baseline){throw 'RecoveryPlanUnavailable: no exact sealed baseline recovery plan is bound to this task.'}
        $baselineDirectory=[string]$baseline.CaseDirectory
        . (Join-Path $scriptsRoot 'games\skyrimspecialedition\health\actions\New-GridSkyrimComponentRecoveryPlan.ps1')
        $recoveryPlan=Read-GridSkyrimSealedComponentRecoveryPlan -CaseStoreRoot $root -CaseDirectory $baselineDirectory
        $summary=$recoveryPlan.summary
        $actionManifest=New-GridSkyrimComponentRecoveryActionManifest -Plan $recoveryPlan -AffectedMods @($task.result.affectedMods)
        $manualRoutes=@($actionManifest.manualAcquisitions)
        $manualReady=$manualRoutes.Count
        $nextManual=if($manualRoutes.Count-gt 0){$manualRoutes[0]}else{$null}
        $archiveCandidates=@($recoveryPlan.components|ForEach-Object{$component=$_;$componentCandidates=if($component.PSObject.Properties['archiveCandidates']){@($component.archiveCandidates)}else{@()};$componentCandidates|ForEach-Object{[pscustomobject][ordered]@{pluginName=[string]$component.pluginName;providerName=[string]$_.providerName;archiveLeaf=[string]$_.archiveLeaf;candidateRole=[string]$_.candidateRole;status=[string]$_.status;compatibilityStatus=[string]$_.compatibilityStatus;requiredEvidence=@($_.requiredEvidence);evidenceId=[string]$_.evidenceId}}}|Group-Object evidenceId|ForEach-Object{$_.Group|Select-Object -First 1}|Sort-Object pluginName,providerName,archiveLeaf,candidateRole)
        $additionalSource=if($summary.PSObject.Properties['additionalSourceEvidenceRequiredCount']){[int]$summary.additionalSourceEvidenceRequiredCount}else{0}
        $selectionRequired=if($summary.PSObject.Properties['candidateSelectionRequiredCount']){[int]$summary.candidateSelectionRequiredCount}else{0}
        $compatibilityRequired=if($summary.PSObject.Properties['candidateCompatibilityEvidenceRequiredCount']){[int]$summary.candidateCompatibilityEvidenceRequiredCount}else{0}
        $payloadVerificationRequired=if($summary.PSObject.Properties['archivePayloadVerificationRequiredCount']){[int]$summary.archivePayloadVerificationRequiredCount}else{0}
        $solution="The selected live MO2 profile is the repair baseline; no original modlist is required. Recovery plan $($recoveryPlan.planId) records $($summary.affectedPluginCount) affected plugins and $($summary.missingDependencyCount) missing component files. $($summary.lineageEvidenceRequiredCount) need component-lineage evidence; $($summary.acquisitionEvidenceRequiredCount) need exact source bytes; $manualReady have credential-free official Nexus Files routes ready; $selectionRequired require an exact installer selection; $compatibilityRequired require version/dependent compatibility evidence; $payloadVerificationRequired exact restorations still require packaged payload verification; $additionalSource have only partial verified source coverage; $($summary.repairReadyCount) have complete content-proven sources. Grid will continue this same live-profile repair without another whole-profile capture. Apply remains disabled until an exact staged repair, verification, and rollback are sealed."
        $task.result.solution=$solution
        if(-not$task.PSObject.Properties['repairState']){$task|Add-Member -NotePropertyName repairState -NotePropertyValue ([pscustomobject]@{})}
        $task.repairState|Add-Member -NotePropertyName recoveryPlanAvailable -NotePropertyValue $true -Force
        $task.repairState|Add-Member -NotePropertyName manualAcquisitionReadyCount -NotePropertyValue $manualRoutes.Count -Force
        $task.repairState|Add-Member -NotePropertyName nextManualAcquisitionUri -NotePropertyValue $(if($nextManual){[string]$nextManual.officialFilesUri}else{$null}) -Force
        $task.repairState|Add-Member -NotePropertyName nextExpectedArchiveLeaf -NotePropertyValue $(if($nextManual){[string]$nextManual.expectedArchiveLeaf}else{$null}) -Force
        $task.repairState|Add-Member -NotePropertyName archiveCandidates -NotePropertyValue $archiveCandidates -Force
        $task.repairState|Add-Member -NotePropertyName humanActionManifest -NotePropertyValue $actionManifest -Force
        $task.repairState|Add-Member -NotePropertyName detail -NotePropertyValue $solution -Force
        [pscustomobject][ordered]@{schemaVersion=2;operation='ReviewRepair';status='Completed';repairReview=[pscustomobject][ordered]@{planId=$recoveryPlan.planId;planSha256=$recoveryPlan.planSha256;status=$recoveryPlan.status;summary=$summary;nextManualAcquisition=$nextManual;archiveCandidates=$archiveCandidates;humanActionManifest=$actionManifest};tasks=@($task)}|ConvertTo-Json -Depth 100 -Compress
        exit 0
    }

    if([string]$input.operation -in @('History','ReadTask','ReviewEvidence')){
        $tasks=@(Get-GridRequestTaskHistory -StoreRoot $root)
        if([string]$input.operation -ne 'History'){$requestedTaskId=[string]$input.taskId;$tasks=@($tasks|Where-Object{[string]$_.taskId -ceq $requestedTaskId});if($tasks.Count-ne 1){throw 'TaskNotFound: task identity is absent or ambiguous.'}}
        [pscustomobject][ordered]@{schemaVersion=2;operation=[string]$input.operation;status='Completed';tasks=$tasks}|ConvertTo-Json -Depth 100 -Compress
        exit 0
    }

    if([string]$input.operation -eq 'Diagnose'){
        if($null-eq$input.PSObject.Properties['taskId']-or[string]::IsNullOrWhiteSpace([string]$input.taskId)){throw 'RequestSubmissionInvalid: taskId is required for Diagnose.'}
        $diagnosis=Invoke-GridRequestDiagnosis -TaskId ([string]$input.taskId) -StoreRoot $root -ScriptsRoot $scriptsRoot
        [pscustomobject][ordered]@{schemaVersion=2;operation='Diagnose';status=[string]$diagnosis.Status;execution=$diagnosis;tasks=@(Get-GridRequestTaskHistory -StoreRoot $root|Where-Object{[string]$_.caseId -ceq [string]$diagnosis.CaseId})}|ConvertTo-Json -Depth 100 -Compress
        exit 0
    }

    $parentTask=$null
    if([string]$input.operation -in @('AttachEvidence','CaptureState','RefreshRecoverySources')){
        if($null-eq$input.PSObject.Properties['taskId']-or[string]::IsNullOrWhiteSpace([string]$input.taskId)){throw 'RequestSubmissionInvalid: taskId is required for successor evidence actions.'}
        $requestedTaskId=[string]$input.taskId;$matches=@(Get-GridRequestTaskHistory -StoreRoot $root|Where-Object{[string]$_.taskId -ceq $requestedTaskId});if($matches.Count-ne 1){throw 'TaskNotFound: task identity is absent or ambiguous.'};$parentTask=$matches[0]
        if([string]$input.operation -eq 'RefreshRecoverySources'){
            $taskDirectory=[string]$parentTask.caseDirectory
            if([string]::IsNullOrWhiteSpace($taskDirectory)){throw 'RecoveryPlanUnavailable: the task has no sealed case directory.'}
            # The visible Activity item is normally the sealed request/diagnosis
            # successor. Its component recovery plan lives in the separately
            # sealed post-read baseline, so resolve the same immutable link used
            # by Review Repair instead of treating the UI task as the baseline.
            $baseline=Resolve-GridRequestRecoveryBaseline -StoreRoot $root -TaskCaseDirectory $taskDirectory
            if($null-eq$baseline){throw 'RecoveryPlanUnavailable: no exact sealed baseline recovery plan is bound to this task.'}
            $baselineDirectory=[string]$baseline.CaseDirectory
            $planPath=Join-Path $baselineDirectory 'investigation-plan.json';$recoveryPath=Join-Path $baselineDirectory 'repair\component-recovery-plan.v1.json'
            if(-not(Test-Path -LiteralPath $planPath -PathType Leaf)-or-not(Test-Path -LiteralPath $recoveryPath -PathType Leaf)){throw 'RecoveryPlanUnavailable: the sealed task is not a component-recovery baseline.'}
            $baselinePlan=Get-Content -Raw -LiteralPath $planPath|ConvertFrom-Json -ErrorAction Stop
            $recoveryPlan=Get-Content -Raw -LiteralPath $recoveryPath|ConvertFrom-Json -ErrorAction Stop
            $providerNames=@($recoveryPlan.components|ForEach-Object{@($_.lineageCandidates)|ForEach-Object{[string]$_.name}}|Where-Object{$_}|Sort-Object -Unique)
            if($providerNames.Count-eq0){throw 'RecoveryPlanUnavailable: the sealed recovery plan has no exact provider candidates to verify.'}
            $refreshBinding=[pscustomobject][ordered]@{schemaVersion=1;operation='ComponentRecoverySourceRefresh';parentCaseId=[string]$baselinePlan.caseId;parentManifestSha256=[string]$baseline.Manifest.manifestSha256;hashCacheCaseId=[string]$baselinePlan.caseId;providerNames=$providerNames;recoveryPlanId=[string]$recoveryPlan.planId;recoveryPlanSha256=[string]$recoveryPlan.planSha256}
            $parentValues=[ordered]@{gameId=[string]$baselinePlan.gameId;installationId=[string]$baselinePlan.installationId;profileId=[string]$baselinePlan.profileId;classId='grid.class.installation-integrity';mods=$providerNames;tools=@();capabilityIds=@();request=('Verify downloaded component-recovery sources for sealed baseline '+[string]$baselinePlan.caseId+'.');problem=('Verify downloaded component-recovery sources for sealed baseline '+[string]$baselinePlan.caseId+'.');expectedBehavior='Hash exact MO2 download archives and sidecars for the affected providers, then rebuild the sealed recovery plan.';reproductionLocation='The selected MO2 profile and its configured downloads directory.';desiredOutcome='Advance only recovery components supported by current archive, sidecar, lineage, and content evidence.';authorizationScope='SelectedContext';parentTaskId=[string]$input.taskId;captureCurrentState=$true;recoverySourceRefresh=$refreshBinding}
        }else{
            $parentEnvelope=Get-Content -LiteralPath (Join-Path ([string]$parentTask.caseDirectory) 'request\request-envelope.v1.json') -Raw|ConvertFrom-Json -ErrorAction Stop
            $parentIntakePath=Join-Path ([string]$parentTask.caseDirectory) 'request\investigation-intake.v1.json';$parentIntake=if(Test-Path -LiteralPath $parentIntakePath -PathType Leaf){Get-Content -LiteralPath $parentIntakePath -Raw|ConvertFrom-Json -ErrorAction Stop}else{$null}
            $parentValues=[ordered]@{gameId=[string]$parentEnvelope.context.gameId;installationId=[string]$parentEnvelope.context.installationId;profileId=[string]$parentEnvelope.context.profileId;classId=[string]$parentEnvelope.class.classId;mods=@($parentEnvelope.selections.mods|ForEach-Object providerName);tools=@($parentEnvelope.selections.tools|ForEach-Object toolId);capabilityIds=@(if($parentEnvelope.selections.PSObject.Properties['capabilities']){$parentEnvelope.selections.capabilities|ForEach-Object capabilityId});request=[string]$parentEnvelope.claims.text;problem=[string]$parentEnvelope.claims.text;expectedBehavior=if($parentIntake){[string]$parentIntake.expectedBehavior}else{''};reproductionLocation=if($parentIntake){[string]$parentIntake.reproductionLocation}else{''};desiredOutcome=if($parentIntake){[string]$parentIntake.desiredOutcome}else{''};authorizationScope=if($parentIntake){[string]$parentIntake.authorizationScope}else{'SelectedContext'};parentTaskId=[string]$input.taskId}
        }
        foreach($key in @($parentValues.Keys)){$input|Add-Member -NotePropertyName $key -NotePropertyValue $parentValues[$key] -Force}
        $successorAuthorizationScope=if([string]$input.operation -eq 'AttachEvidence'){'SelectedContextAndAttachments'}else{'SelectedContext'}
        $input|Add-Member -NotePropertyName authorizationScope -NotePropertyValue $successorAuthorizationScope -Force
        if($null-eq$input.PSObject.Properties['submissionId']){$actionIdentity=Get-GridCanonicalJsonSha256 -InputObject ([pscustomobject][ordered]@{operation=[string]$input.operation;parentTaskId=[string]$input.taskId;attachments=if($input.PSObject.Properties['attachments']){@($input.attachments)}else{@()}});$input|Add-Member -NotePropertyName submissionId -NotePropertyValue ('action-'+$actionIdentity.Substring(0,24).ToLowerInvariant())}
    }

    if([string]$input.operation -eq 'Resume'){
        foreach($required in @('taskId','authorizationGrantId','authorizationSecret','actorId','sessionId')){if($null-eq $input.PSObject.Properties[$required]-or[string]::IsNullOrWhiteSpace([string]$input.$required)){throw "AuthorizationRequired: Resume is missing $required."}}
        $requestedTaskId=[string]$input.taskId;$tasks=@(Get-GridRequestTaskHistory -StoreRoot $root|Where-Object{[string]$_.taskId -ceq $requestedTaskId})
        if($tasks.Count-ne 1 -or [string]::IsNullOrWhiteSpace([string]$tasks[0].caseDirectory)){throw 'TaskNotFound: only an exact sealed task can be resumed with the current authorization contract.'}
        $caseDirectory=[string]$tasks[0].caseDirectory
        $envelope=Get-Content -LiteralPath (Join-Path $caseDirectory 'request\request-envelope.v1.json') -Raw|ConvertFrom-Json -ErrorAction Stop
        $plan=Get-Content -LiteralPath (Join-Path $caseDirectory 'request\request-plan.v1.json') -Raw|ConvertFrom-Json -ErrorAction Stop
        $intakePath=Join-Path $caseDirectory 'request\investigation-intake.v1.json';$intake=if(Test-Path -LiteralPath $intakePath -PathType Leaf){Get-Content -LiteralPath $intakePath -Raw|ConvertFrom-Json -ErrorAction Stop}else{$null}
        $reviewPath=Join-Path $caseDirectory 'request\read-authorization.v2.json';if(-not(Test-Path -LiteralPath $reviewPath)){throw 'HistoricalAuthorizationNotExecutable: sealed task predates the current v2 authorization binding contract.'}
        $review=Get-Content -LiteralPath $reviewPath -Raw|ConvertFrom-Json -ErrorAction Stop
        $execution=Invoke-GridAuthorizedRequestExecution -Envelope $envelope -RequestPlan $plan -AuthorizationReview $review -AuthorizationGrantId ([string]$input.authorizationGrantId) -AuthorizationSecret ([string]$input.authorizationSecret) -ActorId ([string]$input.actorId) -SessionId ([string]$input.sessionId) -ScriptsRoot $scriptsRoot -CaseStoreRoot $root -SubmissionId ([string]$input.taskId) -InvestigationIntake $intake
        [pscustomobject][ordered]@{schemaVersion=2;operation='Resume';status=[string]$execution.Status;requestEnvelope=$envelope;plan=$plan;authorizationReview=$review;execution=$execution}|ConvertTo-Json -Depth 100 -Compress
        exit 0
    }

    foreach($required in @('actorId','sessionId','submissionId')){if($null-eq $input.PSObject.Properties[$required]-or[string]::IsNullOrWhiteSpace([string]$input.$required)){throw "RequestSubmissionInvalid: $required is required."}}
    $requestedClassId=[string]$input.classId
    $recipes=@(Get-GridClassRecipeRegistry | Where-Object { [string]$_.classId -ceq $requestedClassId }); if($recipes.Count-ne 1){throw 'ClassNotFound.'}
    $canonicalGameId=Resolve-GridRequestGameId -GameId ([string]$input.gameId)
    $tools=@(if($null-ne$input.PSObject.Properties['tools']){@($input.tools)});$mods=@(if($null-ne$input.PSObject.Properties['mods']){@($input.mods)});$capabilityIds=@(if($null-ne$input.PSObject.Properties['capabilityIds']){@($input.capabilityIds)})
    $problem=if($null-ne$input.PSObject.Properties['problem']){[string]$input.problem}elseif($null-ne$input.PSObject.Properties['request']){[string]$input.request}else{''}
    $expectedBehavior=if($null-ne$input.PSObject.Properties['expectedBehavior']){[string]$input.expectedBehavior}else{''};$reproductionLocation=if($null-ne$input.PSObject.Properties['reproductionLocation']){[string]$input.reproductionLocation}else{''};$desiredOutcome=if($null-ne$input.PSObject.Properties['desiredOutcome']){[string]$input.desiredOutcome}else{''}
    $attachments=@(if($null-ne$input.PSObject.Properties['attachments']){@($input.attachments)});$captureCurrentState=if($null-ne$input.PSObject.Properties['captureCurrentState']){[bool]$input.captureCurrentState}else{$true}
    $authorizationScope=if($null-ne$input.PSObject.Properties['authorizationScope']){[string]$input.authorizationScope}elseif($attachments.Count-gt 0){'SelectedContextAndAttachments'}else{'SelectedContext'}
    if([string]$input.operation -eq 'AttachEvidence' -and $attachments.Count-eq 0){throw 'RequestSubmissionInvalid: AttachEvidence requires at least one attachment.'}
    if([string]$input.operation -eq 'CaptureState'){$captureCurrentState=$true}
    $parentTaskId=if($null-ne$input.PSObject.Properties['parentTaskId']){[string]$input.parentTaskId}else{$null}
    $intake=New-GridInvestigationIntake -Problem $problem -ExpectedBehavior $expectedBehavior -ReproductionLocation $reproductionLocation -DesiredOutcome $desiredOutcome -AuthorizationScope $authorizationScope -CaptureCurrentState $captureCurrentState -Attachments $attachments -ParentTaskId $parentTaskId
    $envelope=New-GridRequestEnvelope -GameId $canonicalGameId -InstallationId ([string]$input.installationId) -ProfileId ([string]$input.profileId) -ClassId ([string]$input.classId) -ClassRecipeVersion ([string]$recipes[0].recipeVersion) -ModNames $mods -ToolIds $tools -CapabilityIds $capabilityIds -PlainText $problem
    $plan=Resolve-GridRequestPlan -Envelope $envelope -ScriptsRoot $scriptsRoot
    $toolInputs=@{}
    if($null-ne $input.PSObject.Properties['toolInputs'] -and $null-ne $input.toolInputs){foreach($p in @($input.toolInputs.PSObject.Properties)){$toolInputs[[string]$p.Name]=$p.Value}}
    if($canonicalGameId -eq 'skyrimspecialedition' -and ($tools.Count-gt 0 -or $captureCurrentState)){
        if($null-eq $input.PSObject.Properties['gridDataRoot'] -or [string]::IsNullOrWhiteSpace([string]$input.gridDataRoot)){throw 'InstallationContextUnresolved: gridDataRoot is required separately from caseStoreRoot.'}
        . (Join-Path $scriptsRoot 'games\skyrimspecialedition\health\collectors\Resolve-GridSkyrimRequestToolInputs.ps1')
        $resolveToolIds=@($tools);if($captureCurrentState -and 'grid.tool.mo2' -notin $resolveToolIds){$resolveToolIds+=@('grid.tool.mo2')}
        $resolvedInputs=Resolve-GridSkyrimRequestToolInputs -GridDataRoot ([string]$input.gridDataRoot) -InstallationId ([string]$input.installationId) -ProfileId ([string]$input.profileId) -ToolIds $resolveToolIds
        foreach($key in @($resolvedInputs.Keys)){if(-not$toolInputs.ContainsKey([string]$key)){$toolInputs[[string]$key]=$resolvedInputs[$key]}}
        if($null-ne$input.PSObject.Properties['recoverySourceRefresh']){
            if(-not$toolInputs.ContainsKey('grid.tool.mo2')){throw 'RecoveryRefreshAuthorizationInvalid: the exact MO2 read scope is absent.'}
            $toolInputs['grid.tool.mo2']|Add-Member -NotePropertyName recoverySourceRefresh -NotePropertyValue $input.recoverySourceRefresh -Force
        }
    }
    $review=New-GridRequestAuthorizationReview -Envelope $envelope -RequestPlan $plan -ScriptsRoot $scriptsRoot -SubmissionId ([string]$input.submissionId) -ActorId ([string]$input.actorId) -SessionId ([string]$input.sessionId) -ToolInputs $toolInputs -InvestigationIntake $intake
    switch([string]$input.operation){
        {$_ -in @('Prepare','AttachEvidence','CaptureState','RefreshRecoverySources')} {
            [pscustomobject][ordered]@{schemaVersion=2;operation=[string]$input.operation;status='AwaitingAuthorization';requestEnvelope=$envelope;investigationIntake=$intake;plan=$plan;authorizationReview=$review;preparedInput=$input;tasks=if($parentTask){@($parentTask)}else{@()}}|ConvertTo-Json -Depth 100 -Compress
            exit 0
        }
        'Authorize' {
            $issued=Grant-GridRequestAuthorization -AuthorizationReview $review -StoreRoot $root
            [pscustomobject][ordered]@{schemaVersion=2;operation='Authorize';status='Issued';authorizationReview=$review;grantId=[string]$issued.Grant.grantId;authorizationSecret=[string]$issued.AuthorizationSecret;expiresAt=[string]$issued.Grant.expiresAt}|ConvertTo-Json -Depth 100 -Compress
            exit 0
        }
        'Execute' {
            foreach($required in @('authorizationGrantId','authorizationSecret')){if($null-eq $input.PSObject.Properties[$required]-or[string]::IsNullOrWhiteSpace([string]$input.$required)){throw "AuthorizationRequired: Execute is missing $required."}}
            $postReadCollector=$null
            if($null-ne$input.PSObject.Properties['recoverySourceRefresh']){
                $postReadCollector={param($boundReview,$requestCaseDirectory)
                    $scopes=@($boundReview.scopes|Where-Object{[string]$_.toolId -ceq 'grid.intake.mo2-context'})
                    if($scopes.Count-ne1-or$null-eq$scopes[0].inputs.PSObject.Properties['recoverySourceRefresh']){throw 'RecoveryRefreshAuthorizationInvalid: the bound recovery input is absent or ambiguous.'}
                    $bound=$scopes[0].inputs.recoverySourceRefresh
                    $collector=Join-Path $scriptsRoot 'games\skyrimspecialedition\health\collectors\Invoke-GridSkyrimRecoverySourceRefresh.ps1'
                    if(-not(Test-Path -LiteralPath $collector -PathType Leaf)){throw 'RecoveryRefreshCollectorUnavailable: the targeted source-refresh collector is absent.'}
                    . $collector
                    Invoke-GridSkyrimRecoverySourceRefresh -ParentCaseId ([string]$bound.parentCaseId) -ExpectedParentManifestSha256 ([string]$bound.parentManifestSha256) -ExpectedRecoveryPlanSha256 ([string]$bound.recoveryPlanSha256) -CaseStoreRoot $root -InstallationId ([string]$envelope.context.installationId) -ProfileId ([string]$envelope.context.profileId) -AuthorizedReadPaths @($scopes[0].inputs.authorizedReadPaths)
                }.GetNewClosure()
            }
            elseif($captureCurrentState -and [string]$plan.status -eq 'ReadyToCollect' -and [string]$plan.dispatch.entryPoint -eq 'Invoke-GridBaseline.ps1'){
                $baselinePredecessorCaseId=$null
                # `$input` is an automatic PowerShell pipeline variable inside a
                # Where-Object scriptblock. Capture the request value first so
                # strict mode never resolves parentTaskId against that enumerator.
                $requestedParentTaskId=if($null-ne$input.PSObject.Properties['parentTaskId']){[string]$input.parentTaskId}else{$null}
                if(-not[string]::IsNullOrWhiteSpace($requestedParentTaskId)){
                    $predecessorTasks=@(Get-GridRequestTaskHistory -StoreRoot $root|Where-Object{[string]$_.taskId -ceq $requestedParentTaskId})
                    if($predecessorTasks.Count-eq 1 -and -not[string]::IsNullOrWhiteSpace([string]$predecessorTasks[0].caseDirectory)){
                        $predecessorDirectory=[string]$predecessorTasks[0].caseDirectory
                        $postReadPath=Join-Path $predecessorDirectory 'evidence\post-read-collector.v1.json'
                        if(Test-Path -LiteralPath $postReadPath -PathType Leaf){
                            $priorPostRead=Get-Content -LiteralPath $postReadPath -Raw|ConvertFrom-Json -ErrorAction Stop
                            if([string]$priorPostRead.status -eq 'PausedAtCheckpoint'){$baselinePredecessorCaseId=[string]$priorPostRead.caseId}
                        }
                        $dispatchPath=Join-Path $predecessorDirectory 'request\class-request-dispatch.v1.json'
                        if([string]::IsNullOrWhiteSpace($baselinePredecessorCaseId) -and (Test-Path -LiteralPath $dispatchPath -PathType Leaf)){
                            $priorDispatch=Get-Content -LiteralPath $dispatchPath -Raw|ConvertFrom-Json -ErrorAction Stop
                            if($priorDispatch.PSObject.Properties['ExecutionResult'] -and $priorDispatch.ExecutionResult -and [string]$priorDispatch.ExecutionResult.Status -eq 'PausedAtCheckpoint'){$baselinePredecessorCaseId=[string]$priorDispatch.ExecutionResult.CaseId}
                        }
                    }
                }
                $postReadCollector={param($boundReview,$requestCaseDirectory)
                    $arguments=@{
                        Request=[string]$envelope.claims.text;Game=[string]$envelope.context.gameId
                        InstallationId=[string]$envelope.context.installationId;ProfileId=[string]$envelope.context.profileId
                        StatedProviderNames=@($envelope.selections.mods|ForEach-Object providerName)
                        RequiredGates=@($plan.dispatch.requiredGates)
                        CaseStoreRoot=$root;PassThru=$true
                    }
                    if(-not[string]::IsNullOrWhiteSpace($baselinePredecessorCaseId)){$arguments.PredecessorCaseId=$baselinePredecessorCaseId}
                    & (Join-Path $PSScriptRoot 'Invoke-GridBaseline.ps1') @arguments
                }.GetNewClosure()
            }
            $execution=Invoke-GridAuthorizedRequestExecution -Envelope $envelope -RequestPlan $plan -AuthorizationReview $review -AuthorizationGrantId ([string]$input.authorizationGrantId) -AuthorizationSecret ([string]$input.authorizationSecret) -ActorId ([string]$input.actorId) -SessionId ([string]$input.sessionId) -ScriptsRoot $scriptsRoot -CaseStoreRoot $root -SubmissionId ([string]$input.submissionId) -InvestigationIntake $intake -PostReadCollector $postReadCollector
            # Return the sealed request successor, whose identity and result are
            # bound to this authorization.  The post-read baseline is linked
            # evidence, not the replacement UI task identity.
            $returnedCaseId=[string]$execution.CaseId
            if($execution.PSObject.Properties['PostReadCollectorResult'] -and $execution.PostReadCollectorResult -and
               $execution.PostReadCollectorResult.PSObject.Properties['RepairPlanningCaseId'] -and
               -not[string]::IsNullOrWhiteSpace([string]$execution.PostReadCollectorResult.RepairPlanningCaseId)){
                # A successful targeted refresh may create its one actionable
                # successor. Return that repair task directly so the app never
                # asks the operator to rediscover it in Activity history.
                $returnedCaseId=[string]$execution.PostReadCollectorResult.RepairPlanningCaseId
            }
            $refreshedTasks=@(Get-GridRequestTaskHistory -StoreRoot $root|Where-Object{[string]$_.caseId -ceq $returnedCaseId})
            [pscustomobject][ordered]@{schemaVersion=2;operation='Execute';status=[string]$execution.Status;requestEnvelope=$envelope;investigationIntake=$intake;plan=$plan;authorizationReview=$review;execution=$execution;tasks=$refreshedTasks}|ConvertTo-Json -Depth 100 -Compress
            exit 0
        }
        default { throw "Unsupported operation '$($input.operation)'." }
    }
} catch {
    $errorCode=if($_.Exception.Message -match '^([A-Za-z][A-Za-z0-9]+):'){$matches[1]}else{'RequestSubmissionFailed'}
    [pscustomobject][ordered]@{schemaVersion=2;operation='Error';status='Failed';errorCode=$errorCode;message=$_.Exception.Message}|ConvertTo-Json -Depth 20 -Compress
    exit 1
}
