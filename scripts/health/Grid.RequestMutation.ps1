#requires -Version 5.1

function Get-GridRequestMutationContext {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StoreRoot,
        [Parameter(Mandatory)][string]$TaskId,
        [Parameter(Mandatory)][ValidateSet('ApplyRepair','RollBack')][string]$Action,
        [Parameter(Mandatory)][string]$ActorId,
        [Parameter(Mandatory)][string]$SessionId,
        [Parameter(Mandatory)][string]$SubmissionId,
        [Parameter(Mandatory)][string]$ScriptsRoot
    )
    # Preparing a mutation review is a read-only operation. Rebuild the task
    # projection in memory so merely opening Review Repair never rewrites the
    # durable transcript or requires filesystem mutation authority.
    $tasks = @(Get-GridRequestTaskHistory -StoreRoot $StoreRoot -NoPersist | Where-Object { [string]$_.taskId -ceq $TaskId })
    if ($tasks.Count -ne 1) { throw 'TaskNotFound: task identity is absent or ambiguous.' }
    $task = $tasks[0]
    $modChainSpecificationPath = Join-Path ([string]$task.caseDirectory) 'repair\repair-specification.v1.json'
    $pluginBatchSpecificationPath = Join-Path ([string]$task.caseDirectory) 'repair\plugin-state-batch-specification.v1.json'
    $eslFlagSpecificationPath = Join-Path ([string]$task.caseDirectory) 'repair\esl-flag-specification.v1.json'
    $referenceSuppressionSpecificationPath = Join-Path ([string]$task.caseDirectory) 'repair\reference-suppression-specification.v1.json'
    $availableSpecifications = @(@($modChainSpecificationPath,$pluginBatchSpecificationPath,$eslFlagSpecificationPath,$referenceSuppressionSpecificationPath) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($availableSpecifications.Count -ne 1) { throw 'RepairSpecificationUnavailable: the task must contain exactly one sealed exact repair specification.' }
    $repairKind = if ($availableSpecifications[0] -ceq $pluginBatchSpecificationPath) { 'PluginStateBatch' } elseif ($availableSpecifications[0] -ceq $eslFlagSpecificationPath) { 'EslFlag' } elseif ($availableSpecifications[0] -ceq $referenceSuppressionSpecificationPath) { 'ReferenceSuppression' } else { 'ModChain' }
    $specificationPath = [string]$availableSpecifications[0]
    $requiredHistoryCommand = switch ($repairKind) { 'PluginStateBatch' { 'Get-GridSkyrimPluginStateBatchHistoryTransition' } 'EslFlag' { 'Get-GridSkyrimEslFlagHistoryTransition' } 'ReferenceSuppression' { 'Get-GridSkyrimReferenceSuppressionHistoryTransition' } default { 'Get-GridSkyrimModChainHistoryTransition' } }
    if (-not (Get-Command -Name $requiredHistoryCommand -ErrorAction SilentlyContinue)) {
        $adapter = Resolve-GridGameAdapter -ScriptsRoot $ScriptsRoot -Request 'repair mutation' -Game 'skyrimspecialedition'
        Import-Module (Join-Path $adapter.AdapterRoot $adapter.Module) -Force -DisableNameChecking
    }
    $specification = Get-Content -LiteralPath $specificationPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    $calculatedSpecificationHash = switch ($repairKind) { 'PluginStateBatch' { Get-GridPluginStateBatchSpecificationHash -Specification $specification } 'EslFlag' { Get-GridEslFlagSpecificationHash -Specification $specification } 'ReferenceSuppression' { Get-GridSkyrimReferenceSuppressionSpecificationHash -Specification $specification } default { Get-GridRepairSpecificationHash -Specification $specification } }
    if ($calculatedSpecificationHash -cne [string]$specification.specificationSha256) { throw 'SpecificationDigestMismatch: immutable repair specification has changed.' }
    $transactionRoot = Join-Path ([IO.Path]::GetFullPath($StoreRoot)) 'repair-transactions'
    $transactionPrefix = switch ($repairKind) { 'PluginStateBatch' { 'plugin-batch-' } 'EslFlag' { 'esl-flag-' } 'ReferenceSuppression' { 'reference-suppression-' } default { 'transaction-' } }
    $transactionDirectory = Join-Path $transactionRoot ($transactionPrefix + [string]$specification.specificationSha256)
    $originalReceiptPath = Join-Path $transactionDirectory $(if ($repairKind -eq 'ModChain') { 'receipt.v2.json' } else { 'receipt.v1.json' })
    $historyStatePath = Join-Path $transactionDirectory 'history-state.v1.json'
    $historyState = if (Test-Path -LiteralPath $historyStatePath -PathType Leaf) { Get-Content -LiteralPath $historyStatePath -Raw | ConvertFrom-Json -ErrorAction Stop } elseif (Test-Path -LiteralPath $originalReceiptPath -PathType Leaf) { [pscustomobject]@{state='Applied';revision=0} } else { [pscustomobject]@{state='NotApplied';revision=0} }
    $direction = $null
    if ($Action -eq 'ApplyRepair') {
        if ([string]$historyState.state -ceq 'NotApplied') { $mode = 'Apply' }
        elseif ([string]$historyState.state -ceq 'Undone') { $mode = 'Redo'; $direction = 'Redo' }
        else { throw "RepairHistoryDirectionInvalid: Apply/Redo requires state 'NotApplied' or 'Undone', observed '$($historyState.state)'." }
    } else {
        if ([string]$historyState.state -cne 'Applied') { throw "RepairHistoryDirectionInvalid: Undo requires state 'Applied', observed '$($historyState.state)'." }
        $mode = 'Undo'; $direction = 'Undo'
    }
    if ($mode -eq 'Apply') {
        $identityId = [string]$specification.specificationId
        $identitySha256 = [string]$specification.specificationSha256
        $normalizedInput = $specification
        if ($repairKind -eq 'PluginStateBatch') {
            $targets = @($specification.operations | Sort-Object sequence | ForEach-Object { "plugin:$($_.pluginName)=Enabled" })
            $capability = [pscustomobject]@{capabilityId='grid.game.skyrimspecialedition.plugin-state-batch.execute';capabilityVersion='1.0.0';adapterId='SkyrimSpecialEdition';adapterVersion='1'}
        } elseif ($repairKind -eq 'EslFlag') {
            $targets = @("plugin:$($specification.plugin.name):TES4.ESL=true")
            $capability = [pscustomobject]@{capabilityId='grid.game.skyrimspecialedition.esl-flag.execute';capabilityVersion='1.0.0';adapterId='SkyrimSpecialEdition';adapterVersion='1'}
        } elseif ($repairKind -eq 'ReferenceSuppression') {
            $referenceExecutionContext = Get-GridReferenceSuppressionExecutionContext -Specification $specification -CaseStoreRoot $StoreRoot
            $referenceWriterState = Get-GridXEditReferenceSuppressionWriterProvisioningState -ConfigurationPath $referenceExecutionContext.configurationPath -ExecutableTitle SSEEdit -GridDataRoot $referenceExecutionContext.gridDataRoot
            if ([string]$referenceWriterState.State -cne 'Ready') {
                throw "RepairPrerequisiteRequired: the manifest-bound xEdit reference-suppression writer is '$($referenceWriterState.State)' and requires its own reviewed one-use provisioning authorization before patch review."
            }
            $targets = @(Get-GridReferenceSuppressionTargets -Specification $specification -ExecutionContext $referenceExecutionContext)
            $capability = [pscustomobject]@{capabilityId='grid.game.skyrimspecialedition.reference-suppression.execute';capabilityVersion='1.0.0';adapterId='SkyrimSpecialEdition';adapterVersion='1'}
        } else {
            $targets = @($specification.operations | ForEach-Object { [IO.Path]::GetFullPath([string]$_.targetDirectory) } | Sort-Object -Unique)
            $capability = [pscustomobject]@{capabilityId='grid.game.skyrimspecialedition.mod-chain-repair.execute';capabilityVersion='2.0.0';adapterId='SkyrimSpecialEdition';adapterVersion='1'}
        }
    } else {
        $transition = switch ($repairKind) { 'PluginStateBatch' { Get-GridSkyrimPluginStateBatchHistoryTransition -SpecificationPath $specificationPath -TransactionRoot $transactionRoot -Direction $direction } 'EslFlag' { Get-GridSkyrimEslFlagHistoryTransition -SpecificationPath $specificationPath -TransactionRoot $transactionRoot -Direction $direction } 'ReferenceSuppression' { Get-GridSkyrimReferenceSuppressionHistoryTransition -SpecificationPath $specificationPath -TransactionRoot $transactionRoot -Direction $direction } default { Get-GridSkyrimModChainHistoryTransition -SpecificationPath $specificationPath -TransactionRoot $transactionRoot -Direction $direction } }
        $identityId = [string]$transition.transitionId
        $identitySha256 = [string]$transition.transitionSha256
        $normalizedInput = $transition.normalizedInput
        $targets = @($transition.targets)
        $capability = if ($repairKind -eq 'PluginStateBatch') {
            [pscustomobject]@{capabilityId='grid.game.skyrimspecialedition.plugin-state-batch.history.execute';capabilityVersion='1.0.0';adapterId='SkyrimSpecialEdition';adapterVersion='1'}
        } elseif ($repairKind -eq 'EslFlag') {
            [pscustomobject]@{capabilityId='grid.game.skyrimspecialedition.esl-flag.history.execute';capabilityVersion='1.0.0';adapterId='SkyrimSpecialEdition';adapterVersion='1'}
        } elseif ($repairKind -eq 'ReferenceSuppression') {
            [pscustomobject]@{capabilityId='grid.game.skyrimspecialedition.reference-suppression.history.execute';capabilityVersion='1.0.0';adapterId='SkyrimSpecialEdition';adapterVersion='1'}
        } else { [pscustomobject]@{capabilityId='grid.game.skyrimspecialedition.mod-chain-repair.history.execute';capabilityVersion='1.0.0';adapterId='SkyrimSpecialEdition';adapterVersion='1'} }
    }
    $baselineCaseDirectory = if ($repairKind -eq 'EslFlag') { [string]$specification.evidenceCase.directory } elseif ($repairKind -eq 'ReferenceSuppression') { [string]$specification.context.baselineCaseDirectory } else { [string]$specification.baseline.caseDirectory }
    $baselinePlanPath = Join-Path $baselineCaseDirectory 'investigation-plan.json'
    if (-not (Test-Path -LiteralPath $baselinePlanPath -PathType Leaf)) { throw 'BaselineArtifactMissing: investigation-plan.json is required for mutation context.' }
    $baselinePlan = Get-Content -LiteralPath $baselinePlanPath -Raw | ConvertFrom-Json -ErrorAction Stop
    $mods = if ($repairKind -eq 'PluginStateBatch') {
        @($specification.operations | ForEach-Object { [string]$_.provider } | Where-Object { $_ } | Sort-Object -Unique)
    } elseif ($repairKind -eq 'EslFlag') {
        @([string]$specification.plugin.provider | Where-Object { $_ })
    } elseif ($repairKind -eq 'ReferenceSuppression') {
        @($specification.records | ForEach-Object { [string]$_.winningPlugin } | Where-Object { $_ } | Sort-Object -Unique)
    } else { @($specification.operations | ForEach-Object { [string]$_.componentId } | Where-Object { $_ } | Sort-Object -Unique) }
    $claim = "$mode sealed repair $($specification.specificationId)."
    $envelope = New-GridRequestEnvelope -GameId 'skyrimspecialedition' -InstallationId ([string]$baselinePlan.installationId) -ProfileId ([string]$baselinePlan.profileId) -ClassId 'grid.class.installation-integrity' -ClassRecipeVersion '1.0.0' -ModNames $mods -ToolIds @() -PlainText $claim
    $intake = New-GridInvestigationIntake -Problem $claim -ExpectedBehavior 'Change only the displayed exact repair targets.' -ReproductionLocation 'Selected MO2 installation and repair transaction.' -DesiredOutcome "$mode the verified repair and preserve a reversible history." -AuthorizationScope 'SelectedContext' -CaptureCurrentState:$false
    $caseSeal = Test-GridDiagnosticCaseSeal -StoreRoot $StoreRoot -CaseDirectory ([string]$task.caseDirectory)
    if (-not $caseSeal.IsValid) { throw 'RepairPlanningCaseInvalid: the planning case seal is invalid.' }
    $scopeSha = Get-GridCanonicalJsonSha256 -InputObject $targets
    $planSha = Get-GridCanonicalJsonSha256 -InputObject ([pscustomobject][ordered]@{action=$Action;mode=$mode;taskId=$TaskId;identity=$identitySha256})
    $binding = New-GridAuthorizationSemanticBinding -ActorId $ActorId -SessionId $SessionId -WorkspaceId $TaskId -RequestId ([string]$envelope.requestId) -SubmissionId $SubmissionId `
        -EnvelopeSha256 ([string]$envelope.envelopeSha256) -PlanSha256 $planSha -ScopeSha256 $scopeSha `
        -ProposalOrSpecificationId $identityId -ProposalOrSpecificationSha256 $identitySha256 -Capabilities @($capability) -Targets $targets -NormalizedInput $normalizedInput
    $review = [pscustomobject][ordered]@{
        schemaVersion=2; reviewId=('mutation-review-' + (Get-GridAuthorizationSemanticDigest -SemanticBinding $binding).Substring(0,32).ToLowerInvariant())
        authorityClass='Mutation'; semanticBinding=$binding; semanticBindingSha256=Get-GridAuthorizationSemanticDigest -SemanticBinding $binding
        scopes=@([pscustomobject][ordered]@{toolId=$(switch($repairKind){'PluginStateBatch'{'grid.mutation.plugin-state-batch'}'EslFlag'{'grid.mutation.esl-flag'}'ReferenceSuppression'{'grid.mutation.reference-suppression'}default{'grid.mutation.mod-chain'}});adapter='SkyrimSpecialEdition';observationMode='ExactReversibleFilesystemMutation';availability='Available';exactReadPaths=$targets})
    }
    [pscustomobject][ordered]@{task=$task;repairKind=$repairKind;action=$Action;mode=$mode;direction=$direction;specification=$specification;specificationPath=$specificationPath;transactionRoot=$transactionRoot;envelope=$envelope;intake=$intake;review=$review;binding=$binding;targets=$targets}
}

function Invoke-GridRequestMutationOperation {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Request,[Parameter(Mandatory)][string]$StoreRoot,[Parameter(Mandatory)][string]$ScriptsRoot)
    foreach ($required in @('taskId','actorId','sessionId','submissionId','mutationAction')) {
        if ($null -eq $Request.PSObject.Properties[$required] -or [string]::IsNullOrWhiteSpace([string]$Request.$required)) { throw "RequestSubmissionInvalid: $required is required for mutation review." }
    }
    $context = Get-GridRequestMutationContext -StoreRoot $StoreRoot -TaskId ([string]$Request.taskId) -Action ([string]$Request.mutationAction) -ActorId ([string]$Request.actorId) -SessionId ([string]$Request.sessionId) -SubmissionId ([string]$Request.submissionId) -ScriptsRoot $ScriptsRoot
    switch ([string]$Request.operation) {
        'PrepareMutation' {
            return [pscustomobject][ordered]@{schemaVersion=2;operation='PrepareMutation';status='AwaitingAuthorization';requestEnvelope=$context.envelope;investigationIntake=$context.intake;authorizationReview=$context.review;preparedInput=$Request;tasks=@($context.task)}
        }
        'Authorize' {
            $issued = New-GridAuthorizationGrant -StoreRoot $StoreRoot -ReviewId ([string]$context.review.reviewId) -AuthorityClass Mutation -SemanticBinding $context.binding -LifetimeMinutes 15
            return [pscustomobject][ordered]@{schemaVersion=2;operation='Authorize';status='Issued';authorizationReview=$context.review;grantId=[string]$issued.Grant.grantId;authorizationSecret=[string]$issued.AuthorizationSecret;expiresAt=[string]$issued.Grant.expiresAt}
        }
        'Execute' {
            foreach ($required in @('authorizationGrantId','authorizationSecret')) { if ($null -eq $Request.PSObject.Properties[$required] -or [string]::IsNullOrWhiteSpace([string]$Request.$required)) { throw "AuthorizationRequired: mutation Execute is missing $required." } }
            if ($context.mode -eq 'Apply' -and $context.repairKind -eq 'PluginStateBatch') {
                $mutationResult = Invoke-GridAuthorizedPluginStateBatch -SpecificationPath $context.specificationPath -CurrentContextFingerprint ([string]$context.specification.contextFingerprint) -CurrentEvidenceFingerprint ([string]$context.specification.evidenceFingerprint) -AuthorizationGrantId ([string]$Request.authorizationGrantId) -AuthorizationSecret ([string]$Request.authorizationSecret) -AuthorizationStoreRoot $StoreRoot -SemanticBinding $context.binding -CaseStoreRoot $StoreRoot -TransactionRoot $context.transactionRoot -Confirm:$false -PassThru
            } elseif ($context.mode -eq 'Apply' -and $context.repairKind -eq 'EslFlag') {
                $mutationResult = Invoke-GridAuthorizedEslFlag -SpecificationPath $context.specificationPath -CurrentContextFingerprint ([string]$context.specification.contextFingerprint) -AuthorizationGrantId ([string]$Request.authorizationGrantId) -AuthorizationSecret ([string]$Request.authorizationSecret) -AuthorizationStoreRoot $StoreRoot -SemanticBinding $context.binding -CaseStoreRoot $StoreRoot -TransactionRoot $context.transactionRoot -Confirm:$false -PassThru
            } elseif ($context.mode -eq 'Apply' -and $context.repairKind -eq 'ReferenceSuppression') {
                $mutationResult = Invoke-GridAuthorizedReferenceSuppression -SpecificationPath $context.specificationPath -CurrentContextFingerprint ([string]$context.specification.contextFingerprint) -AuthorizationGrantId ([string]$Request.authorizationGrantId) -AuthorizationSecret ([string]$Request.authorizationSecret) -AuthorizationStoreRoot $StoreRoot -SemanticBinding $context.binding -CaseStoreRoot $StoreRoot -TransactionRoot $context.transactionRoot -Confirm:$false -PassThru
            } elseif ($context.mode -eq 'Apply') {
                $mutationResult = Invoke-GridAuthorizedModChainRepair -SpecificationPath $context.specificationPath -AuthorizationGrantId ([string]$Request.authorizationGrantId) -AuthorizationSecret ([string]$Request.authorizationSecret) -AuthorizationStoreRoot $StoreRoot -SemanticBinding $context.binding -CaseStoreRoot $StoreRoot -TransactionRoot $context.transactionRoot -Confirm:$false -PassThru
            } elseif ($context.repairKind -eq 'PluginStateBatch') {
                $mutationResult = Invoke-GridAuthorizedPluginStateBatchHistory -SpecificationPath $context.specificationPath -TransactionRoot $context.transactionRoot -Direction $context.direction -AuthorizationGrantId ([string]$Request.authorizationGrantId) -AuthorizationSecret ([string]$Request.authorizationSecret) -AuthorizationStoreRoot $StoreRoot -SemanticBinding $context.binding -CaseStoreRoot $StoreRoot -Confirm:$false -PassThru
            } elseif ($context.repairKind -eq 'EslFlag') {
                $mutationResult = Invoke-GridAuthorizedEslFlagHistory -SpecificationPath $context.specificationPath -TransactionRoot $context.transactionRoot -Direction $context.direction -AuthorizationGrantId ([string]$Request.authorizationGrantId) -AuthorizationSecret ([string]$Request.authorizationSecret) -AuthorizationStoreRoot $StoreRoot -SemanticBinding $context.binding -CaseStoreRoot $StoreRoot -Confirm:$false -PassThru
            } elseif ($context.repairKind -eq 'ReferenceSuppression') {
                $mutationResult = Invoke-GridAuthorizedReferenceSuppressionHistory -SpecificationPath $context.specificationPath -TransactionRoot $context.transactionRoot -Direction $context.direction -AuthorizationGrantId ([string]$Request.authorizationGrantId) -AuthorizationSecret ([string]$Request.authorizationSecret) -AuthorizationStoreRoot $StoreRoot -SemanticBinding $context.binding -CaseStoreRoot $StoreRoot -Confirm:$false -PassThru
            } else {
                $mutationResult = Invoke-GridAuthorizedModChainHistory -SpecificationPath $context.specificationPath -TransactionRoot $context.transactionRoot -Direction $context.direction -AuthorizationGrantId ([string]$Request.authorizationGrantId) -AuthorizationSecret ([string]$Request.authorizationSecret) -AuthorizationStoreRoot $StoreRoot -SemanticBinding $context.binding -CaseStoreRoot $StoreRoot -Confirm:$false -PassThru
            }
            $updated = @(Get-GridRequestTaskHistory -StoreRoot $StoreRoot | Where-Object { [string]$_.taskId -ceq [string]$Request.taskId })
            if ($updated.Count -ne 1) { throw 'TaskNotFound: repaired task could not be reloaded.' }
            return [pscustomobject][ordered]@{schemaVersion=2;operation='Execute';status='Completed';mutationResult=$mutationResult;tasks=$updated}
        }
        default { throw "Unsupported mutation operation '$($Request.operation)'." }
    }
}
