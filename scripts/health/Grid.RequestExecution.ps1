#requires -Version 5.1
<#
.SYNOPSIS
Creates an exact read-authorization review and seals an authorized Class request.
.DESCRIPTION
This is the shared submission boundary between a structured Grid intake and
registered read-only tool adapters. Planning grants no read authority. Execution
requires the exact review digest and persists one tamper-evident case. A valid
existing case is returned without rerunning collectors.
#>
Set-StrictMode -Version Latest

function Resolve-GridRequestGameId {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$GameId)
    switch -CaseSensitive ($GameId) {
        'game.skyrim-special-edition' { 'skyrimspecialedition' }
        'skyrimspecialedition' { 'skyrimspecialedition' }
        default { $GameId }
    }
}

function ConvertTo-GridRequestInputObject {
    param([object]$Value)
    if ($null -eq $Value) { return [pscustomobject]@{} }
    if ($Value -is [Collections.IDictionary]) {
        $ordered = [ordered]@{}
        foreach ($key in @($Value.Keys | Sort-Object)) { $ordered[[string]$key] = $Value[$key] }
        return [pscustomobject]$ordered
    }
    $Value
}

function New-GridInvestigationIntake {
    [CmdletBinding()]
    param(
        [AllowEmptyString()][string]$Problem = '',
        [AllowEmptyString()][string]$ExpectedBehavior = '',
        [AllowEmptyString()][string]$ReproductionLocation = '',
        [AllowEmptyString()][string]$DesiredOutcome = '',
        [ValidateSet('SelectedContext','SelectedContextAndAttachments')][string]$AuthorizationScope = 'SelectedContext',
        [bool]$CaptureCurrentState = $true,
        [object[]]$Attachments = @(),
        [string]$ParentTaskId
    )
    foreach ($field in @($Problem,$ExpectedBehavior,$ReproductionLocation,$DesiredOutcome)) {
        if ($null -ne $field -and $field.Length -gt 1MB) { throw 'InvestigationIntakeInvalid: a text field exceeds 1 MiB.' }
    }
    if (@($Attachments).Count -gt 32) { throw 'InvestigationIntakeInvalid: no more than 32 attachments are allowed.' }
    $seen = @{}; $normalized = New-Object Collections.Generic.List[object]
    foreach ($attachment in @($Attachments)) {
        if ($null -eq $attachment -or $null -eq $attachment.PSObject.Properties['path'] -or [string]::IsNullOrWhiteSpace([string]$attachment.path)) { throw 'InvestigationIntakeInvalid: each attachment requires an exact path.' }
        $path = [IO.Path]::GetFullPath([string]$attachment.path)
        $key = $path.ToLowerInvariant(); if ($seen.ContainsKey($key)) { throw "InvestigationIntakeInvalid: duplicate attachment '$path'." }; $seen[$key] = $true
        $assertions = @(if ($null -ne $attachment.PSObject.Properties['assertions']) { @($attachment.assertions | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) })
        if ($assertions.Count -gt 64) { throw "InvestigationIntakeInvalid: attachment '$path' has too many assertions." }
        foreach ($assertion in $assertions) { if ($assertion.Length -gt 4096) { throw 'InvestigationIntakeInvalid: an attachment assertion exceeds 4096 characters.' } }
        $captured = if ($null -ne $attachment.PSObject.Properties['claimedCapturedAt'] -and -not [string]::IsNullOrWhiteSpace([string]$attachment.claimedCapturedAt)) { [string]$attachment.claimedCapturedAt } else { $null }
        if ($captured) { $parsed = [DateTimeOffset]::MinValue; if (-not [DateTimeOffset]::TryParse($captured,[ref]$parsed)) { throw "InvestigationIntakeInvalid: attachment '$path' has an invalid claimedCapturedAt." } }
        $mediaType = if ($null -ne $attachment.PSObject.Properties['mediaType'] -and -not [string]::IsNullOrWhiteSpace([string]$attachment.mediaType)) { [string]$attachment.mediaType } else { 'application/octet-stream' }
        if ($mediaType.Length -gt 255) { throw 'InvestigationIntakeInvalid: attachment mediaType exceeds 255 characters.' }
        $normalized.Add([pscustomobject][ordered]@{ path=$path; mediaType=$mediaType; claimedCapturedAt=$captured; assertions=@($assertions) })
    }
    if ($normalized.Count -gt 0 -and $AuthorizationScope -ne 'SelectedContextAndAttachments') { throw 'InvestigationIntakeInvalid: attachment reads require SelectedContextAndAttachments authorization scope.' }
    $unsigned = [pscustomobject][ordered]@{
        schemaVersion=1; problem=[string]$Problem; expectedBehavior=[string]$ExpectedBehavior
        reproductionLocation=[string]$ReproductionLocation; desiredOutcome=[string]$DesiredOutcome
        authorizationScope=$AuthorizationScope; captureCurrentState=[bool]$CaptureCurrentState
        attachments=@($normalized.ToArray() | Sort-Object path);parentTaskId=if([string]::IsNullOrWhiteSpace($ParentTaskId)){$null}else{$ParentTaskId}
    }
    $unsigned | Add-Member -NotePropertyName intakeSha256 -NotePropertyValue (Get-GridCanonicalJsonSha256 -InputObject $unsigned)
    $unsigned
}

function Get-GridInvestigationAttachmentPaths {
    param($InvestigationIntake)
    if ($null -eq $InvestigationIntake -or $null -eq $InvestigationIntake.PSObject.Properties['attachments']) { return @() }
    @($InvestigationIntake.attachments | ForEach-Object { [IO.Path]::GetFullPath([string]$_.path) } | Sort-Object -Unique)
}

function New-GridRequestAuthorizationReview {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Envelope,
        [Parameter(Mandatory)]$RequestPlan,
        [Parameter(Mandatory)][string]$ScriptsRoot,
        [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$')][string]$SubmissionId,
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,159}$')][string]$ActorId,
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,159}$')][string]$SessionId,
        [hashtable]$ToolInputs = @{},
        $InvestigationIntake
    )
    $validation = Test-GridRequestEnvelope -Envelope $Envelope
    if (-not $validation.IsValid) { throw ('RequestEnvelopeInvalid: ' + ($validation.Errors -join ' ')) }
    if ([string]$RequestPlan.status -notin @('ReadyToCollect','NeedsEvidence','NeedsContext','UnsupportedCoverage')) { throw "RequestNotReady: '$($RequestPlan.status)'." }
    if ([string]$RequestPlan.requestId -cne [string]$Envelope.requestId) { throw 'RequestPlanMismatch: plan is not bound to the envelope.' }
    if ($null -ne $RequestPlan.dispatch -and [bool]$RequestPlan.dispatch.mutationAuthorized) { throw 'RequestPlanUnsafe: a read-only dispatch is required.' }
    if ([string]::IsNullOrWhiteSpace($SubmissionId)) { $SubmissionId = [string]$Envelope.requestId }

    $toolIds = @($Envelope.selections.tools | ForEach-Object { [string]$_.toolId })
    $planned = if($toolIds.Count -gt 0){@(Resolve-GridToolEvidencePlan -ToolIds $toolIds -GameId ([string]$Envelope.context.gameId) -ClassId ([string]$Envelope.class.classId) -ScriptsRoot $ScriptsRoot)}else{@()}
    $capabilityRegistry = @(Get-GridCapabilityRegistry -ScriptsRoot $ScriptsRoot)
    $scopes = New-Object Collections.Generic.List[object]
    $capabilityBindings = New-Object Collections.Generic.List[object]
    $targets = New-Object Collections.Generic.List[string]
    $normalizedInputs = [ordered]@{}
    foreach ($item in $planned) {
        $input = if ($ToolInputs.ContainsKey([string]$item.toolId)) { ConvertTo-GridRequestInputObject $ToolInputs[[string]$item.toolId] } else { [pscustomobject]@{} }
        $normalizedInputs[[string]$item.toolId] = $input
        $paths = @()
        if ($null -ne $input.PSObject.Properties['authorizedReadPaths']) { $paths = @($input.authorizedReadPaths | ForEach-Object { [IO.Path]::GetFullPath([string]$_) } | Sort-Object -Unique) }
        switch ([string]$item.definition.adapter) {
            'MO2Context' { if ($paths.Count -eq 0 -and $null -ne $input.PSObject.Properties['mo2Root'] -and -not [string]::IsNullOrWhiteSpace([string]$input.mo2Root)) { $paths = @([IO.Path]::GetFullPath([string]$input.mo2Root)) } }
            'LootExistingOutput' { if ($paths.Count -eq 0 -and $null -ne $input.PSObject.Properties['candidatePaths']) { $paths = @($input.candidatePaths | ForEach-Object { [IO.Path]::GetFullPath([string]$_) } | Sort-Object -Unique) } }
            'SkyrimExistingToolReport' { if ($paths.Count -eq 0 -and $null -ne $input.PSObject.Properties['candidatePaths']) { $paths = @($input.candidatePaths | ForEach-Object { [IO.Path]::GetFullPath([string]$_) } | Sort-Object -Unique) } }
        }
        foreach ($path in $paths) { $targets.Add($path) }
        if ($item.definition) {
            foreach ($capabilityId in @($item.definition.rootCapabilityIds)) {
                $match = @($capabilityRegistry | Where-Object { [string]$_.capabilityId -ceq [string]$capabilityId })
                if ($match.Count -ne 1) { throw "AuthorizationCapabilityMissing: '$capabilityId'." }
                $capabilityBindings.Add([pscustomobject][ordered]@{ capabilityId=[string]$capabilityId; capabilityVersion=[string]$match[0].capabilityVersion; adapterId=[string]$item.definition.adapter; adapterVersion=[string]$item.definition.schemaVersion })
            }
        }
        $scopes.Add([pscustomobject][ordered]@{ toolId=[string]$item.toolId; adapter=if($item.definition){[string]$item.definition.adapter}else{$null}; observationMode=if($item.definition){[string]$item.definition.observationMode}else{$null}; availability=[string]$item.availability; exactReadPaths=@($paths); inputs=$input })
    }
    if ($null -ne $InvestigationIntake -and [bool]$InvestigationIntake.captureCurrentState -and [string]$Envelope.context.gameId -ceq 'skyrimspecialedition') {
        $contextInput = if ($ToolInputs.ContainsKey('grid.tool.mo2')) { ConvertTo-GridRequestInputObject $ToolInputs['grid.tool.mo2'] } else { [pscustomobject]@{} }
        $paths = @(if ($null -ne $contextInput.PSObject.Properties['authorizedReadPaths']) { $contextInput.authorizedReadPaths | ForEach-Object { [IO.Path]::GetFullPath([string]$_) } | Sort-Object -Unique })
        foreach ($path in $paths) { $targets.Add($path) }
        $contextContract = @($capabilityRegistry | Where-Object { [string]$_.capabilityId -ceq 'grid.game.skyrimspecialedition.mo2-context.collect' })
        if ($contextContract.Count -ne 1) { throw 'AuthorizationCapabilityMissing: grid.game.skyrimspecialedition.mo2-context.collect.' }
        $capabilityBindings.Add([pscustomobject][ordered]@{ capabilityId='grid.game.skyrimspecialedition.mo2-context.collect'; capabilityVersion=[string]$contextContract[0].capabilityVersion; adapterId='MO2Context'; adapterVersion='1' })
        $normalizedInputs['grid.intake.mo2-context'] = $contextInput
        $scopes.Add([pscustomobject][ordered]@{ toolId='grid.intake.mo2-context'; adapter='MO2Context'; observationMode='InProcessRead'; availability=if($paths.Count -gt 0){'Available'}else{'Unavailable'}; exactReadPaths=@($paths); inputs=$contextInput })
    }
    $attachmentPaths = @(Get-GridInvestigationAttachmentPaths -InvestigationIntake $InvestigationIntake)
    if ($attachmentPaths.Count -gt 0) {
        foreach ($path in $attachmentPaths) { $targets.Add($path) }
        $evidenceContract = @($capabilityRegistry | Where-Object { [string]$_.capabilityId -ceq 'grid.health.evidence.record' })
        if ($evidenceContract.Count -ne 1) { throw 'AuthorizationCapabilityMissing: grid.health.evidence.record.' }
        $capabilityBindings.Add([pscustomobject][ordered]@{ capabilityId='grid.health.evidence.record'; capabilityVersion=[string]$evidenceContract[0].capabilityVersion; adapterId='CaseAttachment'; adapterVersion='1' })
        $papyrusPaths=@(if([string]$Envelope.context.gameId -ceq 'skyrimspecialedition'){$attachmentPaths|Where-Object{[IO.Path]::GetExtension([string]$_)-in @('.pex','.psc')}})
        if($papyrusPaths.Count -gt 0){
            $papyrusContract=@($capabilityRegistry|Where-Object{[string]$_.capabilityId -ceq 'grid.game.skyrimspecialedition.papyrus-state.inspect'})
            if($papyrusContract.Count -ne 1){throw 'AuthorizationCapabilityMissing: grid.game.skyrimspecialedition.papyrus-state.inspect.'}
            $capabilityBindings.Add([pscustomobject][ordered]@{capabilityId='grid.game.skyrimspecialedition.papyrus-state.inspect';capabilityVersion=[string]$papyrusContract[0].capabilityVersion;adapterId='CaseAttachment';adapterVersion='1'})
        }
        $normalizedInputs['grid.intake.attachments'] = [pscustomobject][ordered]@{ paths=@($attachmentPaths) }
        $scopes.Add([pscustomobject][ordered]@{ toolId='grid.intake.attachments'; adapter='CaseAttachment'; observationMode=if($papyrusPaths.Count -gt 0){'InProcessRead+BundledPapyrusInspection'}else{'InProcessRead'}; availability='Available'; exactReadPaths=@($attachmentPaths); inputs=[pscustomobject][ordered]@{ paths=@($attachmentPaths) } })
    }
    foreach ($binding in @($RequestPlan.capabilityBindings)) {
        $capabilityId = [string]$binding.capabilityId
        $capabilityVersion = [string]$binding.capabilityVersion
        $match = @($capabilityRegistry | Where-Object {
            [string]$_.capabilityId -ceq $capabilityId -and [string]$_.capabilityVersion -ceq $capabilityVersion
        })
        if ($match.Count -ne 1) { throw "AuthorizationCapabilityMissing: '$capabilityId' version '$capabilityVersion'." }
        $alreadyBound = @($capabilityBindings | Where-Object {
            [string]$_.capabilityId -ceq $capabilityId -and [string]$_.capabilityVersion -ceq $capabilityVersion
        }).Count -gt 0
        if (-not $alreadyBound) {
            $capabilityBindings.Add([pscustomobject][ordered]@{
                capabilityId=$capabilityId; capabilityVersion=$capabilityVersion; adapterId='ClassRecipe'; adapterVersion=[string]$Envelope.class.recipeVersion
            })
        }
    }
    $intakeSha256 = if ($null -ne $InvestigationIntake -and $null -ne $InvestigationIntake.PSObject.Properties['intakeSha256']) { [string]$InvestigationIntake.intakeSha256 } else { Get-GridCanonicalJsonSha256 -InputObject ([pscustomobject][ordered]@{ schemaVersion=1; attachments=@() }) }
    $planSha = Get-GridCanonicalJsonSha256 -InputObject $RequestPlan
    $scopeObject = [pscustomobject][ordered]@{ schemaVersion=2; submissionId=$SubmissionId; requestId=[string]$Envelope.requestId; envelopeSha256=[string]$Envelope.envelopeSha256; planSha256=$planSha; mutationAuthorized=$false; scopes=@($scopes.ToArray()) }
    $scopeSha = Get-GridCanonicalJsonSha256 -InputObject $scopeObject
    $caseKey = Get-GridCanonicalJsonSha256 -InputObject ([pscustomobject][ordered]@{ requestId=[string]$Envelope.requestId; submissionId=$SubmissionId })
    $workspaceId = 'investigation-' + $caseKey.Substring(0,24).ToLowerInvariant() + '-v1'
    $semanticBinding = [pscustomobject][ordered]@{
        schemaVersion=1; actorId=$ActorId; sessionId=$SessionId; workspaceId=$workspaceId; requestId=[string]$Envelope.requestId; submissionId=$SubmissionId
        envelopeSha256=[string]$Envelope.envelopeSha256; planSha256=$planSha; scopeSha256=$scopeSha
        proposalOrSpecificationId=('read-scope-' + $SubmissionId); proposalOrSpecificationSha256=$scopeSha
        capabilities=@($capabilityBindings.ToArray() | Sort-Object capabilityId, capabilityVersion, adapterId); targets=@($targets.ToArray() | Sort-Object -Unique)
        normalizedInputSha256=Get-GridCanonicalJsonSha256 -InputObject ([pscustomobject]$normalizedInputs); investigationIntakeSha256=$intakeSha256
    }
    $semanticBindingSha256 = Get-GridAuthorizationSemanticDigest -SemanticBinding $semanticBinding
    $reviewUnsigned = [pscustomobject][ordered]@{ schemaVersion=2; reviewId=('review-' + $semanticBindingSha256.Substring(0,32).ToLowerInvariant()); status='AwaitingAuthorization'; authorityClass='Read'; semanticBinding=$semanticBinding; scopes=@($scopeObject.scopes); createdAt=[DateTimeOffset]::UtcNow.ToString('o') }
    [pscustomobject][ordered]@{ schemaVersion=2; reviewId=$reviewUnsigned.reviewId; status=$reviewUnsigned.status; authorityClass='Read'; semanticBinding=$semanticBinding; semanticBindingSha256=$semanticBindingSha256; scopes=$reviewUnsigned.scopes; createdAt=$reviewUnsigned.createdAt; reviewDigest=(Get-GridCanonicalJsonSha256 -InputObject $reviewUnsigned) }
}

function Grant-GridRequestAuthorization {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$AuthorizationReview, [Parameter(Mandatory)][string]$StoreRoot, [ValidateRange(1,1440)][int]$LifetimeMinutes=15)
    if ([int]$AuthorizationReview.schemaVersion -ne 2 -or [string]$AuthorizationReview.status -cne 'AwaitingAuthorization' -or [string]$AuthorizationReview.authorityClass -cne 'Read') { throw 'AuthorizationReviewInvalid: current v2 read review is required.' }
    if ([string]$AuthorizationReview.semanticBindingSha256 -cne (Get-GridAuthorizationSemanticDigest -SemanticBinding $AuthorizationReview.semanticBinding)) { throw 'AuthorizationReviewInvalid: semantic binding digest mismatch.' }
    New-GridAuthorizationGrant -StoreRoot $StoreRoot -ReviewId ([string]$AuthorizationReview.reviewId) -AuthorityClass Read -SemanticBinding $AuthorizationReview.semanticBinding -LifetimeMinutes $LifetimeMinutes
}

function Invoke-GridRegisteredToolAdapter {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Definition,
        [Parameter(Mandatory)]$Envelope,
        [Parameter(Mandatory)]$AuthorizationScope,
        [Parameter(Mandatory)][string]$ScriptsRoot,
        [Parameter(Mandatory)][string]$CaseDirectory
    )
    switch ([string]$Definition.adapter) {
        'MO2Context' {
            if ($null -eq $AuthorizationScope.inputs.PSObject.Properties['mo2Root'] -or [string]::IsNullOrWhiteSpace([string]$AuthorizationScope.inputs.mo2Root)) {
                return [pscustomobject]@{ status = 'Unavailable'; evidence = @(); stdout = ''; stderr = ''; exitCode = $null; reason = 'An exact authorized MO2 root was not supplied.' }
            }
            $collector = Join-Path $ScriptsRoot 'games\skyrimspecialedition\health\collectors\Get-GridMO2Context.ps1'
            . $collector
            if ($null -eq $AuthorizationScope.inputs.PSObject.Properties['profileName'] -or [string]::IsNullOrWhiteSpace([string]$AuthorizationScope.inputs.profileName)) { throw 'AuthorizationScopeMismatch: exact MO2 profile name is absent.' }
            $observed = Get-GridMO2Context -Mo2Root ([string]$AuthorizationScope.inputs.mo2Root) -Profile ([string]$AuthorizationScope.inputs.profileName) -CaseDirectory $CaseDirectory
            $observedRoots = @(@($observed.Mo2Root, $observed.ProfileDirectory, $observed.ModsRoot, $observed.OverwriteRoot, $observed.GameDataRoot) |
                Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | ForEach-Object { [IO.Path]::GetFullPath([string]$_) })
            $approvedRoots = @($AuthorizationScope.exactReadPaths | ForEach-Object { [IO.Path]::GetFullPath([string]$_) })
            foreach ($observedRoot in $observedRoots) {
                if ($observedRoot -notin $approvedRoots) { throw "AuthorizationScopeMismatch: derived MO2 root '$observedRoot' was not explicitly approved." }
            }
            return [pscustomobject]@{ status = 'Collected'; evidence = @($observed); stdout = ''; stderr = ''; exitCode = 0; reason = $null }
        }
        'LootExistingOutput' {
            $collector = Join-Path $ScriptsRoot 'games\skyrimspecialedition\health\collectors\Get-GridLootExistingOutput.ps1'
            . $collector
            $paths = if ($null -eq $AuthorizationScope.inputs.PSObject.Properties['candidatePaths']) { @() } else { @($AuthorizationScope.inputs.candidatePaths) }
            $approvedPaths=@($AuthorizationScope.exactReadPaths|ForEach-Object{[IO.Path]::GetFullPath([string]$_)})
            foreach($path in $paths){if([IO.Path]::GetFullPath([string]$path) -notin $approvedPaths){throw "AuthorizationScopeMismatch: LOOT report '$path' was not approved for this read."}}
            if($paths.Count -eq 0){return [pscustomobject]@{status='Unavailable';evidence=@();stdout='';stderr='';exitCode=$null;reason='No exact LOOT report path was supplied.'}}
            $contextFingerprint=if($AuthorizationScope.inputs.PSObject.Properties['contextFingerprint']){[string]$AuthorizationScope.inputs.contextFingerprint}else{$null}
            $observed = Get-GridLootExistingOutput -CandidatePaths $paths -MaximumOutputBytes ([long]$Definition.limits.maximumOutputBytes) -ContextFingerprint $contextFingerprint
            return [pscustomobject]@{ status = [string]$observed.status; evidence = @($observed); stdout = ''; stderr = ''; exitCode = $null; reason = [string]$observed.reason }
        }
        'SkyrimExistingToolReport' {
            $collector = Join-Path $ScriptsRoot 'games\skyrimspecialedition\health\collectors\Get-GridSkyrimExistingToolReport.ps1'
            . $collector
            $paths = if ($null -eq $AuthorizationScope.inputs.PSObject.Properties['candidatePaths']) { @() } else { @($AuthorizationScope.inputs.candidatePaths) }
            $approvedPaths=@($AuthorizationScope.exactReadPaths|ForEach-Object{[IO.Path]::GetFullPath([string]$_)})
            foreach($path in $paths){if([IO.Path]::GetFullPath([string]$path) -notin $approvedPaths){throw "AuthorizationScopeMismatch: tool report '$path' was not approved for this read."}}
            if($paths.Count -eq 0){return [pscustomobject]@{status='Unavailable';evidence=@();stdout='';stderr='';exitCode=$null;reason='No exact tool report path was supplied.'}}
            $contextFingerprint=if($AuthorizationScope.inputs.PSObject.Properties['contextFingerprint']){[string]$AuthorizationScope.inputs.contextFingerprint}else{$null}
            $observed = Get-GridSkyrimExistingToolReport -ToolId ([string]$Definition.toolId) -CandidatePaths $paths -MaximumOutputBytes ([long]$Definition.limits.maximumOutputBytes) -ContextFingerprint $contextFingerprint
            return [pscustomobject]@{ status = [string]$observed.status; evidence = @($observed); stdout = ''; stderr = ''; exitCode = $null; reason = [string]$observed.reason }
        }
        default {
            return [pscustomobject]@{ status = 'Unavailable'; evidence = @(); stdout = ''; stderr = ''; exitCode = $null; reason = "No registered in-process execution bridge exists for adapter '$($Definition.adapter)'." }
        }
    }
}

function New-GridRequestEvidenceResult {
    param([Parameter(Mandatory)]$Envelope, [Parameter(Mandatory)]$ToolRun, $AttachmentIndex, $ContextReceipt)
    $collected = @($ToolRun.toolReceipts | Where-Object status -eq 'Collected' | ForEach-Object toolId)
    if ($null -ne $ContextReceipt -and [string]$ContextReceipt.status -eq 'Collected') { $collected += 'grid.intake.mo2-context' }
    $attachmentIds = if ($null -ne $AttachmentIndex) { @($AttachmentIndex.attachments | Where-Object status -eq 'Imported' | ForEach-Object attachmentId) } else { @() }
    $papyrusEvidence=@(if($null-ne$AttachmentIndex){$AttachmentIndex.attachments|ForEach-Object{@($_.derivedEvidence)}|Where-Object{[string]$_.capabilityId -ceq 'grid.game.skyrimspecialedition.papyrus-state.inspect' -and [string]$_.status -eq 'Observed'}})
    [pscustomobject][ordered]@{
        schemaVersion = 1
        requestId = [string]$Envelope.requestId
        terminalState = [string]$ToolRun.terminalState
        resultKind = 'Evidence'
        affectedMods = @()
        modRoles = @()
        finding = if($papyrusEvidence.Count -gt 0){'Static Papyrus evidence: '+(@($papyrusEvidence|ForEach-Object{[string]$_.finding})-join ' ')}elseif ($collected.Count -gt 0) { 'Registered read-only evidence was collected. Selected mods remain user-supplied investigation claims; no affected mod, role, or diagnosis is asserted.' } else { 'No registered tool produced evidence. Selected mods remain user claims and no diagnosis is asserted.' }
        solution = if($papyrusEvidence.Count -gt 0){@($papyrusEvidence|ForEach-Object{[string]$_.solution})-join ' '}elseif ([string]$ToolRun.terminalState -eq 'EvidenceComplete') { 'Review the sealed evidence before planning any diagnosis or repair.' } else { 'Resolve the unavailable or failed tool receipts, then submit a new authorized evidence request.' }
        evidenceToolIds = @($collected)
        evidenceIds = @($collected) + @($attachmentIds) + @($papyrusEvidence|ForEach-Object{[string]$_.relativePath})
        confidence = [pscustomobject][ordered]@{ status='NotEvaluated'; rating=$null; evidenceIds=@() }
        capabilityRequired = $null
        repairState = [pscustomobject][ordered]@{ specificationAvailable=$false; applyEnabled=$false; rollbackAvailable=$false }
        mutationAuthorized = $false
    }
}

function Add-GridInvestigationAttachments {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Transaction, [Parameter(Mandatory)]$InvestigationIntake, [Parameter(Mandatory)][string]$CaseId, [Parameter(Mandatory)][string]$ContextFingerprint, [Parameter(Mandatory)][string]$ScriptsRoot, [Parameter(Mandatory)][string]$GameId)
    $records = New-Object Collections.Generic.List[object]
    foreach ($attachment in @($InvestigationIntake.attachments | Sort-Object path)) {
        $suppliedAt = [DateTimeOffset]::UtcNow.ToString('o'); $path = [IO.Path]::GetFullPath([string]$attachment.path)
        $status='Imported'; $hash=$null; $length=[long]0; $relativePath='unavailable'
        try {
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $status='Missing' }
            else {
                $before=Get-Item -LiteralPath $path -Force -ErrorAction Stop
                $length=[long]$before.Length
                if ($length -gt 32MB) { $status='Oversized' }
                elseif (($before.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $status='Unreadable' }
                else {
                    $blob=Add-GridCaseStoreBlob -Transaction $Transaction -LiteralPath $path
                    $after=Get-Item -LiteralPath $path -Force -ErrorAction Stop
                    if ([long]$after.Length -ne [long]$before.Length -or $after.LastWriteTimeUtc -ne $before.LastWriteTimeUtc) { $status='ChangedDuringImport' }
                    else { $hash=[string]$blob.sha256; $relativePath=('blobs/sha256/{0}/{1}' -f $hash.Substring(0,2),$hash) }
                }
            }
        } catch { $status='Unreadable' }
        $identityInput=[pscustomobject][ordered]@{path=$path;sha256=$hash;sizeBytes=$length;suppliedAt=$suppliedAt}
        $attachmentId='attachment-'+(Get-GridCanonicalJsonSha256 -InputObject $identityInput).Substring(0,24).ToLowerInvariant()
        $derivedEvidence=@()
        if($status -eq 'Imported' -and $GameId -ceq 'skyrimspecialedition' -and [IO.Path]::GetExtension($path) -in @('.pex','.psc')){
            try{
                $collector=Join-Path $ScriptsRoot 'games\skyrimspecialedition\health\collectors\Get-GridSkyrimPapyrusStateEvidence.ps1'
                if(-not(Test-Path -LiteralPath $collector -PathType Leaf)){throw 'Papyrus attachment collector is absent.'}
                . $collector
                $inspectionDirectory=Join-Path $Transaction.CaseDirectory ('evidence\papyrus\'+$attachmentId)
                $importedPath=Join-Path (Join-Path (Join-Path $Transaction.StoreRoot 'blobs\sha256') $hash.Substring(0,2)) $hash
                $inspection=Get-GridSkyrimPapyrusStateEvidence -ScriptPath $importedPath -InputFormat ([IO.Path]::GetExtension($path).TrimStart('.').ToUpperInvariant()) -CaseDirectory $inspectionDirectory -ContextFingerprint $ContextFingerprint -ExpectedSha256 $hash
                $casePrefix=([IO.Path]::GetFullPath([string]$Transaction.CaseDirectory)).TrimEnd('\')+'\'
                $inspectionFull=[IO.Path]::GetFullPath([string]$inspection.Path)
                if(-not $inspectionFull.StartsWith($casePrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Papyrus evidence escaped the case transaction.'}
                $inspectionRelative=$inspectionFull.Substring($casePrefix.Length).Replace('\','/')
                $derivedEvidence=@([pscustomobject][ordered]@{capabilityId='grid.game.skyrimspecialedition.papyrus-state.inspect';status='Observed';relativePath=$inspectionRelative;finding=[string]$inspection.Evidence.inspection.stateAnalysis.finding;solution=[string]$inspection.Evidence.inspection.stateAnalysis.solution;detail=$null})
            }catch{
                $derivedEvidence=@([pscustomobject][ordered]@{capabilityId='grid.game.skyrimspecialedition.papyrus-state.inspect';status='Failed';relativePath=$null;finding='UNRESOLVED';solution='The bounded local Papyrus attachment inspection failed; review the case-local collector error before retrying.';detail=$_.Exception.Message})
            }
        }
        $records.Add([pscustomobject][ordered]@{
            attachmentId=$attachmentId
            relativePath=$relativePath; originalName=[IO.Path]::GetFileName($path); mediaType=[string]$attachment.mediaType
            sizeBytes=$length; sha256=$hash; status=$status; suppliedAt=$suppliedAt; claimedCapturedAt=$attachment.claimedCapturedAt
            contextFingerprint=$ContextFingerprint; assertions=@($attachment.assertions);derivedEvidence=@($derivedEvidence)
        })
    }
    [pscustomobject][ordered]@{schemaVersion=1;caseId=$CaseId;createdAt=[DateTimeOffset]::UtcNow.ToString('o');attachments=@($records.ToArray())}
}

function New-GridCapabilityRequiredResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Envelope,
        [Parameter(Mandatory)]$Plan,
        [object[]]$EvidenceIds=@(),
        [string]$ScriptsRoot=(Split-Path -Parent $PSScriptRoot),
        [string]$RequestedOutcome
    )
    $coverage=@(Get-GridRegisteredCoverage -ScriptsRoot $ScriptsRoot | Where-Object { [string]$_.classId -ceq [string]$Envelope.class.classId })
    $missingCapabilities=if($coverage.Count -eq 1){@($coverage[0].missingCapabilityIds)}else{@()}
    $gaps=@($Plan.coverageGaps); $missing=@($Plan.missingInputs)
    $requiredCapabilities=@($missingCapabilities|ForEach-Object{[string]$_}|Where-Object{$_}|Sort-Object -Unique)
    if($requiredCapabilities.Count-eq 0 -and [string]$Plan.status -eq 'UnsupportedCoverage'){
        $gameId=([string]$Envelope.context.gameId).ToLowerInvariant()
        $classId=([string]$Envelope.class.classId).ToLowerInvariant()
        $classSegment=if($classId.StartsWith('grid.class.')){$classId.Substring('grid.class.'.Length)}else{$classId}
        $requiredCapabilities=@("grid.game.$gameId.class.$classSegment.diagnose")
    }
    $outcome=if(-not[string]::IsNullOrWhiteSpace($RequestedOutcome)){$RequestedOutcome.Trim()}else{"Provide deterministic diagnosis coverage for the structured '$([string]$Envelope.class.classId)' class."}
    $registry=@(Get-GridCapabilityRegistry -ScriptsRoot $ScriptsRoot)
    $resolutions=New-Object Collections.Generic.List[object]
    $proposalDrafts=New-Object Collections.Generic.List[object]
    foreach($capabilityId in $requiredCapabilities){
        $gapIdentity=[pscustomobject][ordered]@{requestId=[string]$Envelope.requestId;gameId=[string]$Envelope.context.gameId;classId=[string]$Envelope.class.classId;requiredCapabilityId=$capabilityId;requestedOutcome=$outcome}
        $gapSha=Get-GridCanonicalJsonSha256 -InputObject $gapIdentity
        $resolution=Resolve-GridCapabilityGap -GapId ('gap.request.'+$gapSha.Substring(0,24).ToLowerInvariant()) -GameId ([string]$Envelope.context.gameId).ToLowerInvariant() -RequiredCapabilityId $capabilityId -RequestedOutcome $outcome -CapabilityRegistry $registry -MissingInputs $missing -Mode Auto
        $resolutions.Add($resolution)
        if([string]$resolution.route -eq 'CreateCapabilityProposal' -and [string]$resolution.status -in @('CreationProposalRequired','CreationProposalReady')){$proposalDrafts.Add((New-GridScriptCapabilityProposalDraft -GapResolution $resolution))}
    }
    $next = if($proposalDrafts.Count-gt 0){[string]$proposalDrafts[0].nextAction}elseif($resolutions.Count-gt 0){[string]$resolutions[0].nextAction}elseif($gaps.Count -gt 0){$gaps -join ' '}elseif($missing.Count -gt 0){'Collect the required structured evidence: '+($missing -join ', ')+'.'}else{'No registered deterministic diagnosis result is present for this case.'}
    [pscustomobject][ordered]@{
        schemaVersion=1;requestId=[string]$Envelope.requestId;terminalState='CapabilityRequired';resultKind='CapabilityRequired'
        affectedMods=@();modRoles=@();finding='UNRESOLVED';solution=$next;evidenceToolIds=@();evidenceIds=@($EvidenceIds)
        confidence=[pscustomobject][ordered]@{status='NotEvaluated';rating=$null;evidenceIds=@()}
        capabilityRequired=[pscustomobject][ordered]@{classId=[string]$Envelope.class.classId;plannerStatus=[string]$Plan.status;missingInputs=@($missing);coverageGaps=@($gaps);missingCapabilityIds=@($missingCapabilities);resolutions=@($resolutions.ToArray());proposalDrafts=@($proposalDrafts.ToArray())}
        repairState=[pscustomobject][ordered]@{specificationAvailable=$false;applyEnabled=$false;rollbackAvailable=$false};mutationAuthorized=$false
    }
}

function New-GridDispatchEvidenceResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Envelope,
        [Parameter(Mandatory)][ValidateSet('EvidencePartial','EvidenceFailed')][string]$TerminalState,
        [Parameter(Mandatory)][string]$FailureCode,
        [Parameter(Mandatory)][string]$Detail,
        [string]$EvidenceCaseId
    )
    $safeCode=if([string]::IsNullOrWhiteSpace($FailureCode)){'RegisteredCollectorIncomplete'}else{$FailureCode}
    $safeDetail=if([string]::IsNullOrWhiteSpace($Detail)){'The registered collector returned no deterministic diagnosis result.'}else{$Detail}
    $evidenceIds=if([string]::IsNullOrWhiteSpace($EvidenceCaseId)){@()}else{@($EvidenceCaseId)}
    [pscustomobject][ordered]@{
        schemaVersion=1;requestId=[string]$Envelope.requestId;terminalState=$TerminalState;resultKind='Evidence';failureCode=$safeCode
        affectedMods=@();modRoles=@();finding=("Registered diagnosis evidence is incomplete ($safeCode).")
        solution=$safeDetail;evidenceToolIds=@();evidenceIds=@($evidenceIds)
        confidence=[pscustomobject][ordered]@{status='NotEvaluated';rating=$null;evidenceIds=@()};capabilityRequired=$null
        repairState=[pscustomobject][ordered]@{specificationAvailable=$false;applyEnabled=$false;rollbackAvailable=$false};mutationAuthorized=$false
    }
}

function New-GridCapabilityAssessmentResult {
    <#
    .SYNOPSIS
    Projects deterministic installed-provider and current community-candidate
    evidence without turning missing gameplay functionality into an error.
    .DESCRIPTION
    The caller supplies a structured capability identity and evidence-backed
    assessment. This function does not interpret the user's prose, search a
    provider, select a mod, or authorize installation.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Envelope,[Parameter(Mandatory)]$Assessment)

    $required=@('schemaVersion','capabilityId','displayName','installedStatus','installedProviders','discoveryStatus','observedAtUtc','communityCandidates','evidenceIds')
    foreach($field in $required){if($null-eq$Assessment.PSObject.Properties[$field]){throw "CapabilityAssessmentInvalid: missing '$field'."}}
    if([int]$Assessment.schemaVersion-ne 1){throw 'CapabilityAssessmentInvalid: unsupported schemaVersion.'}
    if([string]$Assessment.capabilityId-notmatch '^grid\.capability\.[a-z0-9]+(?:[.-][a-z0-9]+)*$'){throw 'CapabilityAssessmentInvalid: invalid capabilityId.'}
    if([string]::IsNullOrWhiteSpace([string]$Assessment.displayName)){throw 'CapabilityAssessmentInvalid: displayName is required.'}
    $installedStatus=[string]$Assessment.installedStatus
    if($installedStatus-notin @('Unresolved','Absent','Partial','Satisfied')){throw "CapabilityAssessmentInvalid: unsupported installedStatus '$installedStatus'."}
    $discoveryStatus=[string]$Assessment.discoveryStatus
    if($discoveryStatus-notin @('NotRequested','Current','Stale','AuthenticationRequired','Unavailable')){throw "CapabilityAssessmentInvalid: unsupported discoveryStatus '$discoveryStatus'."}

    $assessmentEvidenceIds=@($Assessment.evidenceIds|ForEach-Object{[string]$_}|Where-Object{-not[string]::IsNullOrWhiteSpace($_)}|Sort-Object -Unique)
    if($discoveryStatus-eq'Current'-and$assessmentEvidenceIds.Count-eq 0){throw 'CapabilityAssessmentInvalid: Current discovery requires evidence.'}
    $installedProviders=@($Assessment.installedProviders)
    if($installedStatus-eq'Absent'-and$installedProviders.Count-ne 0){throw 'CapabilityAssessmentInvalid: Absent installed coverage cannot assert installed providers.'}
    if($installedStatus-in @('Partial','Satisfied')-and$installedProviders.Count-eq 0){throw "CapabilityAssessmentInvalid: $installedStatus installed coverage requires provider evidence."}
    foreach($provider in $installedProviders){
        foreach($field in @('name','role','status','evidenceIds')){if($null-eq$provider.PSObject.Properties[$field]){throw "CapabilityAssessmentInvalid: installed provider is missing '$field'."}}
        if([string]::IsNullOrWhiteSpace([string]$provider.name)-or[string]::IsNullOrWhiteSpace([string]$provider.role)-or[string]::IsNullOrWhiteSpace([string]$provider.status)){throw 'CapabilityAssessmentInvalid: installed provider fields may not be blank.'}
        $providerEvidence=@($provider.evidenceIds|ForEach-Object{[string]$_}|Where-Object{-not[string]::IsNullOrWhiteSpace($_)}|Sort-Object -Unique)
        if($providerEvidence.Count-eq 0){throw 'CapabilityAssessmentInvalid: installed providers require evidence.'}
        if(@($providerEvidence|Where-Object{$_-notin$assessmentEvidenceIds}).Count-gt 0){throw 'CapabilityAssessmentInvalid: installed provider evidence must be bound to the assessment.'}
    }

    $observedAt=$null
    if(-not[string]::IsNullOrWhiteSpace([string]$Assessment.observedAtUtc)){
        try{$observedAt=[DateTimeOffset]::Parse([string]$Assessment.observedAtUtc).ToUniversalTime().ToString('o')}catch{throw 'CapabilityAssessmentInvalid: observedAtUtc is not a timestamp.'}
    }
    if($discoveryStatus-eq'Current'-and$null-eq$observedAt){throw 'CapabilityAssessmentInvalid: Current discovery requires observedAtUtc.'}

    $communityCandidates=@($Assessment.communityCandidates)
    foreach($candidate in $communityCandidates){
        foreach($field in @('name','provider','uri','version','compatibilityStatus','compatibilityDetail','requiredPatches','evidenceIds')){if($null-eq$candidate.PSObject.Properties[$field]){throw "CapabilityAssessmentInvalid: community candidate is missing '$field'."}}
        if([string]::IsNullOrWhiteSpace([string]$candidate.name)-or[string]::IsNullOrWhiteSpace([string]$candidate.provider)){throw 'CapabilityAssessmentInvalid: community candidate identity may not be blank.'}
        if([string]$candidate.compatibilityStatus-notin @('Unresolved','Compatible','PatchRequired','Incompatible')){throw "CapabilityAssessmentInvalid: unsupported compatibilityStatus '$($candidate.compatibilityStatus)'."}
        $candidateEvidence=@($candidate.evidenceIds|ForEach-Object{[string]$_}|Where-Object{-not[string]::IsNullOrWhiteSpace($_)}|Sort-Object -Unique)
        if([string]$candidate.compatibilityStatus-ne'Unresolved'-and$candidateEvidence.Count-eq 0){throw 'CapabilityAssessmentInvalid: resolved candidate compatibility requires evidence.'}
        if(@($candidateEvidence|Where-Object{$_-notin$assessmentEvidenceIds}).Count-gt 0){throw 'CapabilityAssessmentInvalid: candidate evidence must be bound to the assessment.'}
        if(-not[string]::IsNullOrWhiteSpace([string]$candidate.uri)){
            $candidateUri=$null
            if(-not[Uri]::TryCreate([string]$candidate.uri,[UriKind]::Absolute,[ref]$candidateUri)-or$candidateUri.Scheme-ne'https'){throw 'CapabilityAssessmentInvalid: candidate URI must be absolute HTTPS.'}
        }
    }
    if($discoveryStatus-ne'Current'-and$communityCandidates.Count-gt 0){throw 'CapabilityAssessmentInvalid: community candidates require Current provider discovery.'}

    $normalizedProviders=@($installedProviders|Sort-Object name,role|ForEach-Object{[pscustomobject][ordered]@{name=[string]$_.name;role=[string]$_.role;status=[string]$_.status;evidenceIds=@($_.evidenceIds|ForEach-Object{[string]$_}|Sort-Object -Unique)}})
    $normalizedCandidates=@($communityCandidates|Sort-Object provider,name,version|ForEach-Object{[pscustomobject][ordered]@{name=[string]$_.name;provider=[string]$_.provider;uri=if([string]::IsNullOrWhiteSpace([string]$_.uri)){$null}else{[string]$_.uri};version=if([string]::IsNullOrWhiteSpace([string]$_.version)){$null}else{[string]$_.version};compatibilityStatus=[string]$_.compatibilityStatus;compatibilityDetail=[string]$_.compatibilityDetail;requiredPatches=@($_.requiredPatches|ForEach-Object{[string]$_}|Sort-Object -Unique);evidenceIds=@($_.evidenceIds|ForEach-Object{[string]$_}|Sort-Object -Unique)}})
    $evidenceIds=$assessmentEvidenceIds
    $normalized=[pscustomobject][ordered]@{schemaVersion=1;capabilityId=[string]$Assessment.capabilityId;displayName=[string]$Assessment.displayName;installedStatus=$installedStatus;installedProviders=$normalizedProviders;discoveryStatus=$discoveryStatus;observedAtUtc=$observedAt;communityCandidates=$normalizedCandidates;evidenceIds=$evidenceIds}

    $finding=switch($installedStatus){
        'Satisfied'{"Installed capability coverage for '$($normalized.displayName)' is satisfied by the captured profile."}
        'Partial'{"An installed provider for '$($normalized.displayName)' was found, but its required coverage is incomplete."}
        'Absent'{"No installed mod in the captured profile satisfies '$($normalized.displayName)'."}
        default{"Installed capability coverage for '$($normalized.displayName)' is unresolved."}
    }
    $compatible=@($normalizedCandidates|Where-Object compatibilityStatus -eq 'Compatible')
    $patchRequired=@($normalizedCandidates|Where-Object compatibilityStatus -eq 'PatchRequired')
    $solution=if($installedStatus-eq'Satisfied'){'No acquisition is required. Review the installed provider evidence before changing the profile.'}
        elseif($compatible.Count-gt 0){"A current community candidate is compatible with the captured profile: $($compatible[0].name). Review its exact source and evidence before planning installation."}
        elseif($patchRequired.Count-gt 0){"A current community candidate requires a patch: $($patchRequired[0].name). Resolve the declared patch evidence before planning installation."}
        elseif($discoveryStatus-eq'Current'){'No compatible current community candidate was established. Do not install a candidate until compatibility evidence resolves.'}
        elseif($discoveryStatus-eq'AuthenticationRequired'){'Connect the community provider, refresh its current catalog, and evaluate candidates against the captured profile.'}
        elseif($discoveryStatus-eq'Stale'){'Refresh stale community-provider evidence before presenting or selecting a candidate.'}
        elseif($discoveryStatus-eq'Unavailable'){'Retry the community provider later; no current candidate is asserted.'}
        else{'Run structured community discovery for this capability against the captured profile.'}
    $complete=$installedStatus-ne'Unresolved'-and($installedStatus-eq'Satisfied'-or$discoveryStatus-eq'Current')
    [pscustomobject][ordered]@{
        schemaVersion=1;requestId=[string]$Envelope.requestId;terminalState=if($complete){'EvidenceComplete'}else{'EvidencePartial'};resultKind='CapabilityAssessment'
        affectedMods=@($normalizedProviders|ForEach-Object name);modRoles=@($normalizedProviders|ForEach-Object{[pscustomobject][ordered]@{mod=[string]$_.name;role=[string]$_.role}})
        finding=$finding;solution=$solution;evidenceToolIds=@();evidenceIds=$evidenceIds
        confidence=[pscustomobject][ordered]@{status=if($evidenceIds.Count-gt 0){'Evaluated'}else{'NotEvaluated'};rating=if($evidenceIds.Count-gt 0){'Deterministic'}else{$null};evidenceIds=$evidenceIds}
        capabilityRequired=$null;capabilityAssessment=$normalized
        repairState=[pscustomobject][ordered]@{specificationAvailable=$false;applyEnabled=$false;rollbackAvailable=$false};mutationAuthorized=$false
    }
}

function Resolve-GridRequestRecoveryBaseline {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StoreRoot,
        [Parameter(Mandatory)][string]$TaskCaseDirectory,
        [hashtable]$ValidatedSeals
    )
    $root=Get-GridDiagnosticStoreRoot -Root $StoreRoot -Ensure
    $taskDirectory=[IO.Path]::GetFullPath($TaskCaseDirectory)
    $taskKey=([IO.Path]::GetFileName($taskDirectory)).ToLowerInvariant()
    $taskSeal=if($ValidatedSeals -and $ValidatedSeals.ContainsKey($taskKey)){$ValidatedSeals[$taskKey]}else{Test-GridDiagnosticCaseSeal -StoreRoot $root -CaseDirectory $taskDirectory}
    if(-not$taskSeal.IsValid){throw ('CaseSealInvalid: '+($taskSeal.Errors -join '; '))}

    $candidateCaseId=$null;$expectedManifestSha256=$null
    if((Test-Path -LiteralPath (Join-Path $taskDirectory 'inventory\plugin-script-dependencies.v1.json') -PathType Leaf) -and
       (Test-Path -LiteralPath (Join-Path $taskDirectory 'repair\component-recovery-plan.v1.json') -PathType Leaf)){
        $candidateCaseId=[string]$taskSeal.Manifest.caseId
        $expectedManifestSha256=[string]$taskSeal.Manifest.manifestSha256
    }else{
        $postReadPath=Join-Path $taskDirectory 'evidence\post-read-collector.v1.json'
        if(Test-Path -LiteralPath $postReadPath -PathType Leaf){
            $postRead=Get-Content -LiteralPath $postReadPath -Raw|ConvertFrom-Json -ErrorAction Stop
            if([string]$postRead.status -eq 'Completed'){
                $candidateCaseId=[string]$postRead.caseId
                $expectedManifestSha256=[string]$postRead.manifestSha256
            }
        }
        if([string]::IsNullOrWhiteSpace($candidateCaseId)){
            $dispatchPath=Join-Path $taskDirectory 'request\class-request-dispatch.v1.json'
            if(Test-Path -LiteralPath $dispatchPath -PathType Leaf){
                $dispatch=Get-Content -LiteralPath $dispatchPath -Raw|ConvertFrom-Json -ErrorAction Stop
                if($dispatch.PSObject.Properties['ExecutionResult'] -and $dispatch.ExecutionResult){
                    $candidateCaseId=[string]$dispatch.ExecutionResult.CaseId
                    if($dispatch.ExecutionResult.PSObject.Properties['Manifest'] -and $dispatch.ExecutionResult.Manifest){$expectedManifestSha256=[string]$dispatch.ExecutionResult.Manifest.manifestSha256}
                }
            }
        }
    }
    if([string]::IsNullOrWhiteSpace($candidateCaseId)){return $null}
    if([IO.Path]::GetFileName($candidateCaseId) -cne $candidateCaseId){throw 'RecoveryBaselineLinkInvalid: linked case identity must be one safe path segment.'}
    $baselineDirectory=Join-Path (Join-Path $root 'cases\v1') $candidateCaseId
    if(-not(Test-Path -LiteralPath $baselineDirectory -PathType Container)){return $null}
    $baselineKey=$candidateCaseId.ToLowerInvariant()
    $baselineSeal=if($ValidatedSeals -and $ValidatedSeals.ContainsKey($baselineKey)){$ValidatedSeals[$baselineKey]}else{Test-GridDiagnosticCaseSeal -StoreRoot $root -CaseDirectory $baselineDirectory}
    if(-not$baselineSeal.IsValid){throw ('RecoveryBaselineSealInvalid: '+($baselineSeal.Errors -join '; '))}
    if(-not[string]::IsNullOrWhiteSpace($expectedManifestSha256) -and [string]$baselineSeal.Manifest.manifestSha256 -cne $expectedManifestSha256){throw 'RecoveryBaselineLinkInvalid: linked manifest identity does not match the sealed baseline.'}
    if(-not(Test-Path -LiteralPath (Join-Path $baselineDirectory 'inventory\plugin-script-dependencies.v1.json') -PathType Leaf) -or
       -not(Test-Path -LiteralPath (Join-Path $baselineDirectory 'repair\component-recovery-plan.v1.json') -PathType Leaf)){return $null}
    [pscustomobject][ordered]@{CaseId=$candidateCaseId;CaseDirectory=$baselineDirectory;Manifest=$baselineSeal.Manifest}
}

function ConvertFrom-GridDiagnosticResultForRequest {
    param([Parameter(Mandatory)]$Envelope,[Parameter(Mandatory)]$DiagnosticResult,$RecoveryBaseline)
    $affected=if([string]$DiagnosticResult.result.affectedMods.status -eq 'Resolved'){@($DiagnosticResult.result.affectedMods.items|ForEach-Object name)}else{@()}
    $roles=if([string]$DiagnosticResult.result.modRoles.status -eq 'Resolved'){@($DiagnosticResult.result.modRoles.items|ForEach-Object{$subject=[string]$_.subjectId;foreach($role in @($_.roles)){[pscustomobject][ordered]@{mod=$subject;role=[string]$role}}})}else{@()}
    $evidence=@($DiagnosticResult.result.affectedMods.evidenceIds)+@($DiagnosticResult.result.modRoles.evidenceIds)+@($DiagnosticResult.result.finding.evidenceIds)+@($DiagnosticResult.result.solution.evidenceIds)
    $proposalId=if($DiagnosticResult.result.solution.PSObject.Properties['proposalId']){[string]$DiagnosticResult.result.solution.proposalId}else{$null}
    [pscustomobject][ordered]@{
        schemaVersion=1;requestId=[string]$Envelope.requestId;terminalState='Diagnosed';resultKind='Diagnostic';affectedMods=@($affected);modRoles=@($roles)
        finding=if([string]$DiagnosticResult.result.finding.status -eq 'Resolved'){[string]$DiagnosticResult.result.finding.text}else{'UNRESOLVED'}
        solution=[string]$DiagnosticResult.result.solution.text;evidenceToolIds=@();evidenceIds=@($evidence|Sort-Object -Unique)
        confidence=[pscustomobject][ordered]@{status='NotEvaluated';rating=$null;evidenceIds=@()};capabilityRequired=$null
        repairState=[pscustomobject][ordered]@{
            specificationAvailable=(-not [string]::IsNullOrWhiteSpace($proposalId));applyEnabled=$false;rollbackAvailable=$false
            recoveryPlanAvailable=($null-ne$RecoveryBaseline);recoveryCaseId=if($RecoveryBaseline){[string]$RecoveryBaseline.CaseId}else{$null}
            detail=if($RecoveryBaseline){'The selected live MO2 profile is the repair baseline; no original modlist is required. Continue this recovery plan without another whole-profile capture. Apply remains disabled until every source and verification gate is satisfied.'}else{$null}
        };mutationAuthorized=$false
    }
}

function Invoke-GridRequestDiagnosis {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$TaskId,[Parameter(Mandatory)][string]$StoreRoot,[Parameter(Mandatory)][string]$ScriptsRoot,[scriptblock]$ClassRequestInvoker)
    $root=Get-GridDiagnosticStoreRoot -Root $StoreRoot -Ensure
    $tasks=@(Get-GridRequestTaskHistory -StoreRoot $root|Where-Object{[string]$_.taskId -ceq $TaskId})
    if($tasks.Count-ne 1 -or [string]::IsNullOrWhiteSpace([string]$tasks[0].caseDirectory)){throw 'TaskNotFound: diagnosis requires one exact sealed case.'}
    $parentDirectory=[string]$tasks[0].caseDirectory;$seal=Test-GridDiagnosticCaseSeal -StoreRoot $root -CaseDirectory $parentDirectory
    if(-not$seal.IsValid){throw "CaseSealInvalid: $($seal.Errors -join '; ')"}
    $parentManifest=Get-Content -LiteralPath (Join-Path $parentDirectory 'case-manifest.v1.json') -Raw|ConvertFrom-Json -ErrorAction Stop
    $envelope=Get-Content -LiteralPath (Join-Path $parentDirectory 'request\request-envelope.v1.json') -Raw|ConvertFrom-Json -ErrorAction Stop
    $intakePath=Join-Path $parentDirectory 'request\investigation-intake.v1.json'
    $investigationIntake=if(Test-Path -LiteralPath $intakePath -PathType Leaf){Get-Content -LiteralPath $intakePath -Raw|ConvertFrom-Json -ErrorAction Stop}else{$null}
    $plan=Resolve-GridRequestPlan -Envelope $envelope -ScriptsRoot $ScriptsRoot
    $diagnosticPath=Join-Path $parentDirectory 'result\diagnostic-result.v1.json';$classDispatch=$null;$diagnostic=$null;$dispatchEvidenceResult=$null
    if(Test-Path -LiteralPath $diagnosticPath -PathType Leaf){$diagnostic=Get-Content -LiteralPath $diagnosticPath -Raw|ConvertFrom-Json -ErrorAction Stop}
    elseif([string]$plan.status -eq 'ReadyToCollect'){
        $existingDispatchPath=Join-Path $parentDirectory 'request\class-request-dispatch.v1.json'
        if(Test-Path -LiteralPath $existingDispatchPath -PathType Leaf){$classDispatch=Get-Content -LiteralPath $existingDispatchPath -Raw|ConvertFrom-Json -ErrorAction Stop}
        elseif($ClassRequestInvoker){$classDispatch=&$ClassRequestInvoker $envelope $plan $parentDirectory}
        elseif([string]$plan.dispatch.entryPoint -eq 'Invoke-GridBaseline.ps1'){
            $dispatchEvidenceResult=New-GridDispatchEvidenceResult -Envelope $envelope -TerminalState EvidencePartial -FailureCode BaselineAuthorizationRequired -Detail 'No authorized registered baseline is bound to this case. Use Capture Current State, review the exact read scope, and approve that successor collection.'
        }
        else{
            $arguments=@{Game=[string]$envelope.context.gameId;InstallationId=[string]$envelope.context.installationId;ProfileId=[string]$envelope.context.profileId;ClassId=[string]$envelope.class.classId;ModNames=@($envelope.selections.mods|ForEach-Object providerName);ToolIds=@($envelope.selections.tools|ForEach-Object toolId);Request=[string]$envelope.claims.text;CaseStoreRoot=$root;ActorId='grid.diagnosis';SessionId=('case-'+[string]$parentManifest.caseId);PassThru=$true}
            $classDispatch=& (Join-Path $PSScriptRoot 'Invoke-GridClassRequest.ps1') @arguments
        }
        if($classDispatch){
            if($null-eq$classDispatch.PSObject.Properties['Plan']){throw 'ClassDispatchInvalid: registered Class request returned no bound plan.'}
            if((Get-GridCanonicalJsonSha256 -InputObject $classDispatch.Plan)-cne(Get-GridCanonicalJsonSha256 -InputObject $plan)){throw 'ClassDispatchPlanMismatch: registered Class request did not preserve the sealed request plan.'}
            $candidate=if($classDispatch.PSObject.Properties['ExecutionResult'] -and $classDispatch.ExecutionResult -and $classDispatch.ExecutionResult.PSObject.Properties['DiagnosticResult']){$classDispatch.ExecutionResult.DiagnosticResult}elseif($classDispatch.PSObject.Properties['DiagnosticResult']){$classDispatch.DiagnosticResult}else{$null}
        }
        if($classDispatch -and $candidate){
            $candidateEvidence=if($classDispatch.PSObject.Properties['ExecutionResult'] -and $classDispatch.ExecutionResult -and $classDispatch.ExecutionResult.PSObject.Properties['Evidence']){@($classDispatch.ExecutionResult.Evidence)}else{@()}
            $validation=Test-GridDiagnosticResult -DiagnosticResult $candidate -Evidence $candidateEvidence
            if(-not$validation.IsValid){throw ('ClassDispatchDiagnosticInvalid: '+($validation.Errors -join ' '))}
            $diagnostic=$candidate
        }
        elseif($classDispatch -and $classDispatch.PSObject.Properties['ExecutionResult'] -and $classDispatch.ExecutionResult){
            $execution=$classDispatch.ExecutionResult;$status=[string]$execution.Status
            $evidenceCaseId=if($execution.PSObject.Properties['CaseId']){[string]$execution.CaseId}else{$null}
            if($status -eq 'PausedAtCheckpoint'){
                $detail='The bounded profile baseline paused at a sealed checkpoint. Capture Current State to authorize a successor collection that resumes this exact baseline.'
                $dispatchEvidenceResult=New-GridDispatchEvidenceResult -Envelope $envelope -TerminalState EvidencePartial -FailureCode BaselinePausedAtCheckpoint -Detail $detail -EvidenceCaseId $evidenceCaseId
            }
            else{
                $failure=if($execution.PSObject.Properties['PrimaryFailure']){$execution.PrimaryFailure}else{$null}
                $code=if($failure -and $failure.PSObject.Properties['code']){[string]$failure.code}elseif($status -eq 'Completed'){'DiagnosticResultMissing'}else{'RegisteredCollectorFailed'}
                $detail=if($failure -and $failure.PSObject.Properties['detail']){[string]$failure.detail}elseif($execution.PSObject.Properties['Detail']){[string]$execution.Detail}else{'The registered collector returned no deterministic diagnosis result.'}
                $dispatchEvidenceResult=New-GridDispatchEvidenceResult -Envelope $envelope -TerminalState EvidenceFailed -FailureCode $code -Detail $detail -EvidenceCaseId $evidenceCaseId
            }
        }
    }
    if($diagnostic){$result=ConvertFrom-GridDiagnosticResultForRequest -Envelope $envelope -DiagnosticResult $diagnostic}
    elseif($dispatchEvidenceResult){$result=$dispatchEvidenceResult}
    else{$evidenceIds=if($tasks[0].result -and $tasks[0].result.PSObject.Properties['evidenceIds']){@($tasks[0].result.evidenceIds)}else{@()};$requestedOutcome=if($investigationIntake -and $investigationIntake.PSObject.Properties['desiredOutcome']){[string]$investigationIntake.desiredOutcome}else{$null};$result=New-GridCapabilityRequiredResult -Envelope $envelope -Plan $plan -EvidenceIds $evidenceIds -ScriptsRoot $ScriptsRoot -RequestedOutcome $requestedOutcome}
    if($classDispatch -and $classDispatch.PSObject.Properties['ExecutionResult'] -and $classDispatch.ExecutionResult -and $classDispatch.ExecutionResult.PSObject.Properties['CaseDirectory']){
        $baselineDirectory=[string]$classDispatch.ExecutionResult.CaseDirectory
        if(-not[string]::IsNullOrWhiteSpace($baselineDirectory) -and (Test-Path -LiteralPath (Join-Path $baselineDirectory 'inventory\plugin-script-dependencies.v1.json') -PathType Leaf)){
            $baselineSeal=Test-GridDiagnosticCaseSeal -StoreRoot $root -CaseDirectory $baselineDirectory
            if($baselineSeal.IsValid){$result.repairState|Add-Member -NotePropertyName recoveryPlanAvailable -NotePropertyValue $true -Force;$result.repairState|Add-Member -NotePropertyName detail -NotePropertyValue 'The selected live MO2 profile is the repair baseline; no original modlist is required. Continue this recovery plan without another whole-profile capture. Apply remains disabled until every source and verification gate is satisfied.' -Force}
        }
    }
    $successorIdentity=[pscustomobject][ordered]@{parentCaseId=$parentManifest.caseId;parentManifestSha256=$parentManifest.manifestSha256;operation='Diagnose';resultSha256=(Get-GridCanonicalJsonSha256 -InputObject $result)}
    $identitySha=Get-GridCanonicalJsonSha256 -InputObject $successorIdentity;$caseId='diagnosis-'+$identitySha.Substring(0,24).ToLowerInvariant()+'-v1'
    $existing=Join-Path(Join-Path $root 'cases\v1')$caseId
    if(Test-Path -LiteralPath $existing -PathType Container){$existingSeal=Test-GridDiagnosticCaseSeal -StoreRoot $root -CaseDirectory $existing;if(-not$existingSeal.IsValid){throw "ExistingCaseSemanticMismatch: $($existingSeal.Errors -join '; ')"};Sync-GridRequestTranscriptIndex -StoreRoot $root|Out-Null;return [pscustomobject][ordered]@{Status=[string]$result.terminalState;CaseId=$caseId;CaseDirectory=$existing;Result=$result;Reused=$true}}
    $transaction=New-GridCaseStoreTransaction -StoreRoot $root -CaseId $caseId
    try{
        $transaction.Blobs=@($parentManifest.blobs)
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'request\request-envelope.v1.json' -Value $envelope|Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'request\request-plan.v1.json' -Value $plan|Out-Null
        foreach($relative in @('request\authorization-grant.v2.json','request\investigation-intake.v1.json','evidence\tool-evidence-run.v2.json','evidence\intake-context.v1.json','attachments\manifest.v1.json')){ $source=Join-Path $parentDirectory $relative;if(Test-Path -LiteralPath $source -PathType Leaf){Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath $relative -SourceLiteralPath $source|Out-Null} }
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'request\successor.v1.json' -Value ([pscustomobject][ordered]@{schemaVersion=1;parentTaskId=$TaskId;parentCaseId=[string]$parentManifest.caseId;parentManifestSha256=[string]$parentManifest.manifestSha256;operation='Diagnose'})|Out-Null
        if($null-ne$classDispatch){Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'request\class-request-dispatch.v1.json' -Value $classDispatch|Out-Null}
        if($result.capabilityRequired -and @($result.capabilityRequired.resolutions).Count-gt 0){Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'result\capability-gap-resolutions.v1.json' -Value ([pscustomobject][ordered]@{schemaVersion=1;requestId=[string]$envelope.requestId;resolutions=@($result.capabilityRequired.resolutions)})|Out-Null}
        if($result.capabilityRequired -and @($result.capabilityRequired.proposalDrafts).Count-gt 0){Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'result\capability-proposal-drafts.v1.json' -Value ([pscustomobject][ordered]@{schemaVersion=1;requestId=[string]$envelope.requestId;proposalDrafts=@($result.capabilityRequired.proposalDrafts)})|Out-Null}
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'result\request-evidence-result.v1.json' -Value $result|Out-Null
        $now=[DateTimeOffset]::UtcNow.ToString('o');$run=[pscustomobject][ordered]@{schemaVersion=1;runId='run-'+$identitySha.Substring(0,16).ToLowerInvariant();caseId=$caseId;state='Completed';startedAt=$now;completedAt=$now;planFingerprint=(Get-GridCanonicalJsonSha256 -InputObject $plan);resourcePolicyVersion='grid.request-diagnosis.v1';gates=@([pscustomobject]@{gateId='ParentCaseSeal';status='Pass';evidenceIds=@([string]$parentManifest.manifestSha256)});sufficiency=[pscustomobject]@{status=[string]$result.terminalState;completionAssertsDiagnosis=([string]$result.terminalState -eq 'Diagnosed')};checkpoints=@();primaryFailure=$null;secondaryFailures=@()}
        Seal-GridCaseStoreRun -Transaction $transaction -Run $run|Out-Null;$sealed=Seal-GridDiagnosticCase -Transaction $transaction -SemanticBaselineFingerprint $identitySha -Runs @($run)
        Sync-GridRequestTranscriptIndex -StoreRoot $root|Out-Null
        [pscustomobject][ordered]@{Status=[string]$result.terminalState;CaseId=$caseId;CaseDirectory=$sealed.CaseDirectory;Result=$result;Reused=$false}
    }catch{if($transaction.State-eq'Open' -and(Test-Path -LiteralPath $transaction.TransactionDirectory)){Remove-Item -LiteralPath $transaction.TransactionDirectory -Recurse -Force -ErrorAction SilentlyContinue};throw}
}

function Write-GridRequestWorkspaceJson {
    param([Parameter(Mandatory)][string]$LiteralPath, [Parameter(Mandatory)]$Value)
    Write-GridCaseStoreTextAtomic -LiteralPath $LiteralPath -Text ($Value | ConvertTo-Json -Depth 100 -Compress)
}

function Sync-GridRequestTranscriptIndex {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StoreRoot,
        [switch]$NoPersist
    )
    $root = Get-GridDiagnosticStoreRoot -Root $StoreRoot -Ensure
    $tasks = New-Object Collections.Generic.List[object]
    $casesRoot = Join-Path $root 'cases\v1'
    $validatedSeals=@{};$validCaseDirectories=@()
    foreach($candidateDirectory in @(Get-ChildItem -LiteralPath $casesRoot -Directory -ErrorAction SilentlyContinue|Sort-Object Name)){
        $candidateSeal=Test-GridDiagnosticCaseSeal -StoreRoot $root -CaseDirectory $candidateDirectory.FullName
        if($candidateSeal.IsValid){$validatedSeals[$candidateDirectory.Name.ToLowerInvariant()]=$candidateSeal;$validCaseDirectories+=@($candidateDirectory)}
    }
    foreach ($caseDirectory in @($validCaseDirectories)) {
        $seal=$validatedSeals[$caseDirectory.Name.ToLowerInvariant()]
        $envelopePath = Join-Path $caseDirectory.FullName 'request\request-envelope.v1.json'
        $grantV2Path = Join-Path $caseDirectory.FullName 'request\authorization-grant.v2.json'
        $grantV1Path = Join-Path $caseDirectory.FullName 'request\read-authorization-grant.v1.json'
        $grantPath = if (Test-Path -LiteralPath $grantV2Path -PathType Leaf) { $grantV2Path } else { $grantV1Path }
        $resultPath = Join-Path $caseDirectory.FullName 'result\request-evidence-result.v1.json'
        $toolRunV2Path = Join-Path $caseDirectory.FullName 'evidence\tool-evidence-run.v2.json'
        $toolRunV1Path = Join-Path $caseDirectory.FullName 'evidence\tool-evidence-run.v1.json'
        $toolRunPath = if(Test-Path -LiteralPath $toolRunV2Path -PathType Leaf){$toolRunV2Path}else{$toolRunV1Path}
        if (-not (Test-Path -LiteralPath $envelopePath -PathType Leaf) -or -not (Test-Path -LiteralPath $grantPath -PathType Leaf) -or -not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
            # A sealed repair-planning case is a first-class task. Its immutable
            # specification is stored in the case; mutable transaction receipts
            # are observed separately so Apply/Undo/Redo state cannot be forged
            # by changing the task transcript.
            $planningCasePath = Join-Path $caseDirectory.FullName 'case.json'
            $modChainSpecificationPath = Join-Path $caseDirectory.FullName 'repair\repair-specification.v1.json'
             $pluginBatchSpecificationPath = Join-Path $caseDirectory.FullName 'repair\plugin-state-batch-specification.v1.json'
             $eslFlagSpecificationPath = Join-Path $caseDirectory.FullName 'repair\esl-flag-specification.v1.json'
             $referenceSuppressionSpecificationPath = Join-Path $caseDirectory.FullName 'repair\reference-suppression-specification.v1.json'
             $planningSpecifications = @(@($modChainSpecificationPath,$pluginBatchSpecificationPath,$eslFlagSpecificationPath,$referenceSuppressionSpecificationPath) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
            if ((Test-Path -LiteralPath $planningCasePath -PathType Leaf) -and $planningSpecifications.Count -eq 1) {
                $planningCase = Get-Content -LiteralPath $planningCasePath -Raw | ConvertFrom-Json -ErrorAction Stop
                $repairKind = if ($planningSpecifications[0] -ceq $pluginBatchSpecificationPath) { 'PluginStateBatch' } elseif ($planningSpecifications[0] -ceq $eslFlagSpecificationPath) { 'EslFlag' } elseif ($planningSpecifications[0] -ceq $referenceSuppressionSpecificationPath) { 'ReferenceSuppression' } else { 'ModChain' }
                $expectedPurpose = switch ($repairKind) { 'PluginStateBatch' { 'PluginStateBatchPlanning' } 'EslFlag' { 'EslFlagPlanning' } 'ReferenceSuppression' { 'ReferenceSuppressionPlanning' } default { 'RepairPlanning' } }
                if ($planningCase.PSObject.Properties['purpose'] -and [string]$planningCase.purpose -ceq $expectedPurpose) {
                    $repairSpecificationPath = [string]$planningSpecifications[0]
                    $specification = Get-Content -LiteralPath $repairSpecificationPath -Raw | ConvertFrom-Json -ErrorAction Stop
                    if ([string]::IsNullOrWhiteSpace([string]$specification.specificationId) -or [string]$specification.specificationSha256 -notmatch '^[A-Fa-f0-9]{64}$') { continue }
                    $repairTransactionRoot = Join-Path $root 'repair-transactions'
                    $transactionPrefix = switch ($repairKind) { 'PluginStateBatch' { 'plugin-batch-' } 'EslFlag' { 'esl-flag-' } 'ReferenceSuppression' { 'reference-suppression-' } default { 'transaction-' } }
                    $repairTransactionDirectory = Join-Path $repairTransactionRoot ($transactionPrefix + [string]$specification.specificationSha256)
                    $repairReceiptPath = Join-Path $repairTransactionDirectory $(if($repairKind-eq'ModChain'){'receipt.v2.json'}else{'receipt.v1.json'})
                    $historyStatePath = Join-Path $repairTransactionDirectory 'history-state.v1.json'
                    $historyState = $null
                    if (Test-Path -LiteralPath $historyStatePath -PathType Leaf) {
                        try { $historyState = Get-Content -LiteralPath $historyStatePath -Raw | ConvertFrom-Json -ErrorAction Stop } catch { $historyState = $null }
                    }
                    $repairApplied = Test-Path -LiteralPath $repairReceiptPath -PathType Leaf
                    $effectiveState = if ($historyState -and [string]$historyState.state -in @('Applied','Undone','Transitioning')) { [string]$historyState.state } elseif ($repairApplied) { 'Applied' } else { 'NotApplied' }
                    $applyEnabled = $effectiveState -in @('NotApplied','Undone')
                    if ($repairKind -eq 'ReferenceSuppression' -and [int]$specification.schemaVersion -lt 2) { $applyEnabled = $false }
                    $writerProvisioningState = $null
                    $writerProvisioningDestination = $null
                    if ($repairKind -eq 'ReferenceSuppression' -and [int]$specification.schemaVersion -ge 2 -and $effectiveState -in @('NotApplied','Undone')) {
                        if ((Get-Command Get-GridReferenceSuppressionExecutionContext -ErrorAction SilentlyContinue) -and (Get-Command Get-GridXEditReferenceSuppressionWriterProvisioningState -ErrorAction SilentlyContinue)) {
                            try {
                                $referenceProjectionContext = Get-GridReferenceSuppressionExecutionContext -Specification $specification -CaseStoreRoot $root
                                $writerProjection = Get-GridXEditReferenceSuppressionWriterProvisioningState -ConfigurationPath $referenceProjectionContext.configurationPath -ExecutableTitle SSEEdit -GridDataRoot $referenceProjectionContext.gridDataRoot
                                $writerProvisioningState = [string]$writerProjection.State
                                $writerProvisioningDestination = [string]$writerProjection.DestinationPath
                                if ($writerProvisioningState -cne 'Ready') { $applyEnabled = $false }
                            } catch {
                                $writerProvisioningState = 'Unavailable'
                                $applyEnabled = $false
                            }
                        } else {
                            $writerProvisioningState = 'Unavailable'
                            $applyEnabled = $false
                        }
                    }
                    $rollbackAvailable = $effectiveState -ceq 'Applied'
                    $affected = if ($repairKind -eq 'PluginStateBatch') {
                        @($specification.operations | Sort-Object sequence | ForEach-Object { [string]$_.pluginName })
                    } elseif ($repairKind -eq 'EslFlag') {
                        @([string]$specification.plugin.name)
                    } elseif ($repairKind -eq 'ReferenceSuppression') {
                        @($specification.records | ForEach-Object { [string]$_.winningPlugin } | Where-Object { $_ } | Sort-Object -Unique)
                    } else { @($specification.operations | ForEach-Object { [string]$_.componentId } | Where-Object { $_ } | Sort-Object -Unique) }
                    $baselineCaseDirectory = if ($repairKind -eq 'EslFlag') { [string]$specification.evidenceCase.directory } elseif ($repairKind -eq 'ReferenceSuppression') { if($specification.PSObject.Properties['context']){[string]$specification.context.baselineCaseDirectory}else{[string]$specification.evidenceCase.directory} } else { [string]$specification.baseline.caseDirectory }
                    $baselinePlanPath = Join-Path $baselineCaseDirectory 'investigation-plan.json'
                    $baselinePlan = if (Test-Path -LiteralPath $baselinePlanPath -PathType Leaf) { Get-Content -LiteralPath $baselinePlanPath -Raw | ConvertFrom-Json -ErrorAction Stop } else { $null }
                    $detail = switch ($effectiveState) {
                        'NotApplied' { 'An exact sealed repair specification is ready for explicit mutation authorization.' }
                        'Applied' { 'The exact repair is applied and verified; Undo requires a fresh one-use authorization.' }
                        'Undone' { 'The exact repair is undone and preserved; Redo requires a fresh one-use authorization.' }
                        default { 'A prior history transition is incomplete; mutation is disabled until deterministic recovery.' }
                    }
                    if ($repairKind -eq 'ReferenceSuppression' -and $effectiveState -in @('NotApplied','Undone') -and $writerProvisioningState -and $writerProvisioningState -cne 'Ready') {
                        $detail = "The exact patch is sealed, but its manifest-bound xEdit writer is '$writerProvisioningState'. Provision that reviewed writer under a separate one-use authorization before Apply."
                    }
                    $planningResult = [pscustomobject][ordered]@{
                        schemaVersion=1; requestId=$caseDirectory.Name; terminalState='RepairPlanned'; resultKind='RepairSpecification'
                        affectedMods=@($affected); modRoles=@($affected | ForEach-Object { [pscustomobject][ordered]@{mod=$_;role='RepairTarget'} })
                        finding=switch($repairKind){'PluginStateBatch'{'A sealed evidence-bound plugin activation batch is available.'}'EslFlag'{"A sealed xEdit-audited header-only ESL transition is available for $([string]$specification.plugin.name)."}'ReferenceSuppression'{"A sealed xEdit reference-suppression patch for $(@($specification.records).Count) exact placed record(s) is available."}default{'A sealed evidence-bound mod-chain repair specification is available.'}}; solution=$detail
                        evidenceToolIds=@(); evidenceIds=@([string]$seal.Manifest.manifestSha256); confidence=[pscustomobject]@{status='Verified';rating='Deterministic';evidenceIds=@([string]$seal.Manifest.manifestSha256)}; capabilityRequired=$null
                        repairState=[pscustomobject][ordered]@{repairKind=$repairKind;specificationAvailable=$true;specificationId=[string]$specification.specificationId;specificationSha256=[string]$specification.specificationSha256;specificationPath=$repairSpecificationPath;transactionRoot=$repairTransactionRoot;historyState=$effectiveState;applyEnabled=$applyEnabled;rollbackAvailable=$rollbackAvailable;installationId=if($baselinePlan){[string]$baselinePlan.installationId}else{$null};profileId=if($baselinePlan){[string]$baselinePlan.profileId}else{$null};writerProvisioningState=$writerProvisioningState;writerProvisioningDestination=$writerProvisioningDestination;detail=$detail};mutationAuthorized=$false
                    }
                    $tasks.Add([pscustomobject][ordered]@{
                        taskId=$caseDirectory.Name;parentTaskId=$null;requestId=$caseDirectory.Name;createdAt=if($planningCase.PSObject.Properties['createdAt']){[string]$planningCase.createdAt}else{[string]$caseDirectory.CreationTimeUtc.ToString('o')};approvedAt=$null
                        classId='grid.class.installation-integrity';terminalState='RepairPlanned';caseId=$caseDirectory.Name;caseDirectory=$caseDirectory.FullName;rawPrompt=switch($repairKind){'PluginStateBatch'{'Apply the sealed evidence-bound plugin activation batch.'}'EslFlag'{"Apply the sealed xEdit-audited ESL flag to $([string]$specification.plugin.name)."}'ReferenceSuppression'{"Create the sealed exact-reference suppression patch $([string]$specification.patchPluginName)."}default{'Apply the sealed evidence-bound repair specification.'}};intake=$null
                        mods=@();tools=@();result=$planningResult;toolReceipts=@();repairState=$planningResult.repairState;resumable=$false
                    })
                    continue
                }
            }
            # A baseline may be created by the registered baseline command rather
            # than through assistant intake. Preserve complete sealed baseline
            # evidence as a reviewable system result even before a separate root-
            # cause diagnosis exists; never fabricate a user prompt or grant.
            $baselineDiagnosisPath=Join-Path $caseDirectory.FullName 'diagnosis\diagnostic-result.v1.json'
            $baselineInspectionPath=Join-Path $caseDirectory.FullName 'inventory\plugin-script-dependencies.v1.json'
            $baselineCasePath=Join-Path $caseDirectory.FullName 'case.json'
            if((Test-Path -LiteralPath $baselineInspectionPath -PathType Leaf) -and (Test-Path -LiteralPath $baselineCasePath -PathType Leaf)){
                $inspection=Get-Content -Raw -LiteralPath $baselineInspectionPath|ConvertFrom-Json -ErrorAction Stop
                $caseRecord=Get-Content -Raw -LiteralPath $baselineCasePath|ConvertFrom-Json -ErrorAction Stop
                $diagnostic=if(Test-Path -LiteralPath $baselineDiagnosisPath -PathType Leaf){Get-Content -Raw -LiteralPath $baselineDiagnosisPath|ConvertFrom-Json -ErrorAction Stop}else{$null}
                $recoveryPlanPath=Join-Path $caseDirectory.FullName 'repair\component-recovery-plan.v1.json'
                $inspectionUsable=([string]$inspection.status -ieq 'complete') -or
                    (([string]$inspection.status -ieq 'partial') -and (Test-Path -LiteralPath $recoveryPlanPath -PathType Leaf))
                if($inspectionUsable){
                    $hasDiagnosis=$diagnostic -and [string]$diagnostic.state -eq 'Diagnosed'
                    $affected=if($hasDiagnosis -and [string]$diagnostic.result.affectedMods.status -eq 'Resolved'){@($diagnostic.result.affectedMods.items|ForEach-Object name)}else{@()}
                    $roles=if($hasDiagnosis -and [string]$diagnostic.result.modRoles.status -eq 'Resolved'){@($diagnostic.result.modRoles.items|ForEach-Object{$subject=[string]$_.subjectId;foreach($role in @($_.roles)){[pscustomobject][ordered]@{mod=$subject;role=[string]$role}}})}else{@()}
                    $finding=if($hasDiagnosis){[string]$diagnostic.result.finding.text}elseif([string]$inspection.status -ieq 'partial'){"Sealed partial baseline evidence contains $([long]$inspection.recordCount) unresolved attached Papyrus script dependencies and an evidence-bound component recovery plan."}else{"Sealed baseline evidence contains $([long]$inspection.recordCount) unresolved attached Papyrus script dependencies."}
                    $solution=if($hasDiagnosis){[string]$diagnostic.result.solution.text}else{'Use Review Repair to derive exact component-lineage and source-acquisition gates. Apply remains disabled until source contents, compatibility, staging, verification, and rollback are proven.'}
                    $terminalState=if($hasDiagnosis){'Diagnosed'}elseif([string]$inspection.status -ieq 'partial'){'BaselinePartial'}else{'BaselineComplete'}
                    $baselineResult=[pscustomobject][ordered]@{
                        schemaVersion=1;requestId=$caseDirectory.Name;terminalState=$terminalState;resultKind=if($hasDiagnosis){'Diagnostic'}else{'BaselineEvidence'}
                        affectedMods=@($affected);modRoles=@($roles);finding=$finding;solution=$solution
                        evidenceToolIds=@();evidenceIds=if($hasDiagnosis){@($diagnostic.result.finding.evidenceIds)}else{@()};confidence=[pscustomobject]@{status='NotEvaluated';rating=$null;evidenceIds=@()};capabilityRequired=$null
                        repairState=[pscustomobject][ordered]@{specificationAvailable=$false;applyEnabled=$false;rollbackAvailable=$false;recoveryPlanAvailable=$true;detail='A deterministic component recovery plan can be derived from this sealed baseline.'};mutationAuthorized=$false
                    }
                    $tasks.Add([pscustomobject][ordered]@{
                        taskId=$caseDirectory.Name;parentTaskId=$null;requestId=$caseDirectory.Name;createdAt=[string]$caseRecord.createdAt;approvedAt=$null
                        classId='grid.class.installation-integrity';terminalState=$terminalState;caseId=$caseDirectory.Name;caseDirectory=$caseDirectory.FullName;rawPrompt='';intake=$null
                        mods=@();tools=@();result=$baselineResult;toolReceipts=@();repairState=$baselineResult.repairState;resumable=$false
                    })
                }
            }
            continue
        }
        $envelope = Get-Content -LiteralPath $envelopePath -Raw | ConvertFrom-Json -ErrorAction Stop
        $grant = Get-Content -LiteralPath $grantPath -Raw | ConvertFrom-Json -ErrorAction Stop
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json -ErrorAction Stop
        $toolRun = if (Test-Path -LiteralPath $toolRunPath -PathType Leaf) { Get-Content -LiteralPath $toolRunPath -Raw | ConvertFrom-Json -ErrorAction Stop } else { $null }
        $intakePath=Join-Path $caseDirectory.FullName 'request\investigation-intake.v1.json';$intake=if(Test-Path -LiteralPath $intakePath -PathType Leaf){Get-Content -LiteralPath $intakePath -Raw|ConvertFrom-Json -ErrorAction Stop}else{$null}
        $successorPath=Join-Path $caseDirectory.FullName 'request\successor.v1.json';$successor=if(Test-Path -LiteralPath $successorPath -PathType Leaf){Get-Content -LiteralPath $successorPath -Raw|ConvertFrom-Json -ErrorAction Stop}else{$null}
        $postReadLinkPath=Join-Path $caseDirectory.FullName 'evidence\post-read-collector.v1.json'
        $hasProjectedRecovery=$result.PSObject.Properties['repairState'] -and $result.repairState -and $result.repairState.PSObject.Properties['recoveryPlanAvailable'] -and $result.repairState.recoveryPlanAvailable -eq $true
        if(-not$hasProjectedRecovery -and (Test-Path -LiteralPath $postReadLinkPath -PathType Leaf)){
            $recoveryBaseline=Resolve-GridRequestRecoveryBaseline -StoreRoot $root -TaskCaseDirectory $caseDirectory.FullName -ValidatedSeals $validatedSeals
            if($recoveryBaseline){
                if(-not$result.PSObject.Properties['repairState']){$result|Add-Member -NotePropertyName repairState -NotePropertyValue ([pscustomobject]@{})}
                $result.repairState|Add-Member -NotePropertyName recoveryPlanAvailable -NotePropertyValue $true -Force
                $result.repairState|Add-Member -NotePropertyName recoveryCaseId -NotePropertyValue ([string]$recoveryBaseline.CaseId) -Force
                $result.repairState|Add-Member -NotePropertyName detail -NotePropertyValue 'The selected live MO2 profile is the repair baseline; no original modlist is required. Continue this recovery plan without another whole-profile capture. Apply remains disabled until every source and verification gate is satisfied.' -Force
            }
        }
        $tasks.Add([pscustomobject][ordered]@{
            taskId = if($successor){$caseDirectory.Name}elseif ($grant.PSObject.Properties['semanticBinding']) { [string]$grant.semanticBinding.submissionId } else { [string]$grant.submissionId }; parentTaskId=if($successor){[string]$successor.parentTaskId}else{$null}; requestId = [string]$envelope.requestId; createdAt = [string]$envelope.submittedAt
            approvedAt = if ($grant.PSObject.Properties['issuedAt']) { [string]$grant.issuedAt } else { [string]$grant.approvedAt }; classId = [string]$envelope.class.classId; terminalState = [string]$result.terminalState
            caseId = $caseDirectory.Name; caseDirectory = $caseDirectory.FullName; rawPrompt = [string]$envelope.claims.text; intake=$intake
            mods = @($envelope.selections.mods); tools = @($envelope.selections.tools); result = $result
            toolReceipts = if ($toolRun) { @($toolRun.toolReceipts) } else { @() }; repairState=if($result.PSObject.Properties['repairState']){$result.repairState}else{[pscustomobject]@{specificationAvailable=$false;applyEnabled=$false;rollbackAvailable=$false}}; resumable = $false
        })
    }
    $sortedTasks = @($tasks.ToArray() | Sort-Object createdAt, taskId)
    if ($NoPersist) {
        return [pscustomobject][ordered]@{ schemaVersion = 1; updatedAt = $null; tasks = $sortedTasks }
    }
    $indexPath = Join-Path $root 'task-transcript.v1.json'
    if (Test-Path -LiteralPath $indexPath -PathType Leaf) {
        $existingIndex = $null
        try {
            $existingIndex = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            # A malformed or incompatible singleton is rebuilt exclusively from
            # validated sealed cases below; it is never treated as authority.
        }
        if ($existingIndex -and $existingIndex.PSObject.Properties['schemaVersion'] -and $existingIndex.PSObject.Properties['tasks']) {
            $existingTasksHash = Get-GridCanonicalJsonSha256 -InputObject ([pscustomobject][ordered]@{ tasks = @($existingIndex.tasks) })
            $sortedTasksHash = Get-GridCanonicalJsonSha256 -InputObject ([pscustomobject][ordered]@{ tasks = @($sortedTasks) })
            if ([int]$existingIndex.schemaVersion -eq 1 -and $existingTasksHash -ceq $sortedTasksHash) {
                return $existingIndex
            }
        }
    }
    $index = [pscustomobject][ordered]@{ schemaVersion = 1; updatedAt = [DateTimeOffset]::UtcNow.ToString('o'); tasks = $sortedTasks }
    Write-GridCaseStoreTextAtomic -LiteralPath $indexPath -Text ($index | ConvertTo-Json -Depth 30 -Compress)
    $index
}

function Get-GridRequestTaskHistory {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$StoreRoot,[switch]$NoPersist)
    # Rebuild from validated sealed cases so a missing/stale singleton never
    # destroys canonical task history.
    $root = Get-GridDiagnosticStoreRoot -Root $StoreRoot -Ensure
    $tasks = @((Sync-GridRequestTranscriptIndex -StoreRoot $root -NoPersist).tasks)
    foreach ($workspaceRootName in @('workspaces\request-evidence-v1','workspaces\request-evidence-v2')) {
        $workspaceRoot = Join-Path $root $workspaceRootName
        foreach ($workspace in @(Get-ChildItem -LiteralPath $workspaceRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name)) {
            $envelopePath=Join-Path $workspace.FullName 'request-envelope.v1.json'
            $grantV2Path=Join-Path $workspace.FullName 'authorization-grant.v2.json'; $grantV1Path=Join-Path $workspace.FullName 'authorization-grant.v1.json'
            $grantPath=if(Test-Path -LiteralPath $grantV2Path -PathType Leaf){$grantV2Path}else{$grantV1Path}
            if (-not (Test-Path -LiteralPath $envelopePath -PathType Leaf) -or -not (Test-Path -LiteralPath $grantPath -PathType Leaf)) { continue }
            $envelope=Get-Content -LiteralPath $envelopePath -Raw|ConvertFrom-Json -ErrorAction Stop; $grant=Get-Content -LiteralPath $grantPath -Raw|ConvertFrom-Json -ErrorAction Stop
            $receipts=@(Get-ChildItem -LiteralPath (Join-Path $workspace.FullName 'receipts') -Filter '*.json' -File -ErrorAction SilentlyContinue|Sort-Object Name|ForEach-Object{Get-Content -LiteralPath $_.FullName -Raw|ConvertFrom-Json})
            $taskId=if($grant.PSObject.Properties['semanticBinding']){[string]$grant.semanticBinding.submissionId}else{[string]$grant.submissionId};$approvedAt=if($grant.PSObject.Properties['issuedAt']){[string]$grant.issuedAt}else{[string]$grant.approvedAt}
            $tasks += [pscustomobject][ordered]@{taskId=$taskId;requestId=[string]$envelope.requestId;createdAt=[string]$envelope.submittedAt;approvedAt=$approvedAt;classId=[string]$envelope.class.classId;terminalState='Interrupted';caseId=$workspace.Name;caseDirectory=$null;rawPrompt=[string]$envelope.claims.text;mods=@($envelope.selections.mods);tools=@($envelope.selections.tools);result=$null;toolReceipts=$receipts;resumable=$false}
        }
    }
    $orderedTasks=@($tasks | Sort-Object createdAt,taskId)
    if($NoPersist){return $orderedTasks}
    $indexPath=Join-Path $root 'task-transcript.v1.json'
    if(Test-Path -LiteralPath $indexPath -PathType Leaf){
        $existingIndex=$null
        try{$existingIndex=Get-Content -LiteralPath $indexPath -Raw|ConvertFrom-Json -ErrorAction Stop}catch{$existingIndex=$null}
        if($existingIndex -and $existingIndex.PSObject.Properties['schemaVersion'] -and $existingIndex.PSObject.Properties['tasks']){
            $existingTasksText=ConvertTo-Json -InputObject ([pscustomobject][ordered]@{tasks=@($existingIndex.tasks)}) -Depth 100 -Compress
            $orderedTasksText=ConvertTo-Json -InputObject ([pscustomobject][ordered]@{tasks=@($orderedTasks)}) -Depth 100 -Compress
            if([int]$existingIndex.schemaVersion -eq 1 -and $existingTasksText -ceq $orderedTasksText){return @($existingIndex.tasks)}
        }
    }
    $combined=[pscustomobject][ordered]@{schemaVersion=1;updatedAt=[DateTimeOffset]::UtcNow.ToString('o');tasks=$orderedTasks}
    Write-GridCaseStoreTextAtomic -LiteralPath $indexPath -Text ($combined|ConvertTo-Json -Depth 100 -Compress)
    $orderedTasks
}

function Invoke-GridAuthorizedRequestExecution {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Envelope,
        [Parameter(Mandatory)]$RequestPlan,
        [Parameter(Mandatory)]$AuthorizationReview,
        [Parameter(Mandatory)][string]$AuthorizationGrantId,
        [Parameter(Mandatory)][string]$AuthorizationSecret,
        [Parameter(Mandatory)][string]$ActorId,
        [Parameter(Mandatory)][string]$SessionId,
        [Parameter(Mandatory)][string]$ScriptsRoot,
        [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$')][string]$SubmissionId,
        [string]$CaseStoreRoot,
        $InvestigationIntake,
        [scriptblock]$Executor,
        [scriptblock]$CheckpointObserver,
        [scriptblock]$PostReadCollector
    )
    $authorizedInputs=@{}; foreach($scope in @($AuthorizationReview.scopes)){
        $inputKey=if([string]$scope.toolId -ceq 'grid.intake.mo2-context'){'grid.tool.mo2'}else{[string]$scope.toolId}
        $authorizedInputs[$inputKey]=$scope.inputs
    }
    if([string]::IsNullOrWhiteSpace($SubmissionId)){ $SubmissionId=[string]$AuthorizationReview.semanticBinding.submissionId }
    $currentPlan=Resolve-GridRequestPlan -Envelope $Envelope -ScriptsRoot $ScriptsRoot
    if((Get-GridCanonicalJsonSha256 -InputObject $currentPlan)-cne(Get-GridCanonicalJsonSha256 -InputObject $RequestPlan)){throw 'RequestPlanStale: installed capability or Class coverage changed.'}
    $expected=New-GridRequestAuthorizationReview -Envelope $Envelope -RequestPlan $RequestPlan -ScriptsRoot $ScriptsRoot -SubmissionId $SubmissionId -ActorId $ActorId -SessionId $SessionId -ToolInputs $authorizedInputs -InvestigationIntake $InvestigationIntake
    if([string]$AuthorizationReview.semanticBindingSha256-cne[string]$expected.semanticBindingSha256){throw 'AuthorizationReviewMismatch: the reviewed semantic identity is stale or changed.'}
    $root=Get-GridDiagnosticStoreRoot -Root $CaseStoreRoot -Ensure
    $caseId=[string]$expected.semanticBinding.workspaceId
    $grant=Read-GridAuthorizationGrant -StoreRoot $root -GrantId $AuthorizationGrantId
    if([int]$grant.schemaVersion-ne 2){throw 'HistoricalAuthorizationNotExecutable: historical grants cannot confer current authority.'}
    if(-not(Test-GridAuthorizationSecretProof -Secret $AuthorizationSecret -Proof $grant.secretProof)){throw 'AuthorizationSecretInvalid: supplied secret does not match the grant.'}
    if(-not(Test-GridAuthorizationSemanticBinding -Expected $expected.semanticBinding -Actual $grant.semanticBinding)){throw 'AuthorizationBindingMismatch: current request semantics differ from the grant.'}

    $semanticCaseFingerprint=Get-GridCanonicalJsonSha256 -InputObject ([pscustomobject][ordered]@{envelopeSha256=[string]$Envelope.envelopeSha256;planSha256=[string]$expected.semanticBinding.planSha256;authorizationBindingSha256=[string]$expected.semanticBindingSha256})
    $existing=Join-Path(Join-Path $root 'cases\v1')$caseId
    if(Test-Path -LiteralPath $existing -PathType Container){
        $semanticSeal=Test-GridDiagnosticCaseSemanticIdentity -StoreRoot $root -CaseDirectory $existing -ExpectedArtifactSha256 @{} -ExpectedSemanticBaselineFingerprint $semanticCaseFingerprint
        if(-not $semanticSeal.IsValid){throw "ExistingCaseSemanticMismatch: $($semanticSeal.Errors -join '; ')"}
        $sealedEnvelope=Get-Content -LiteralPath (Join-Path $existing 'request\request-envelope.v1.json') -Raw|ConvertFrom-Json -ErrorAction Stop
        $sealedPlan=Get-Content -LiteralPath (Join-Path $existing 'request\request-plan.v1.json') -Raw|ConvertFrom-Json -ErrorAction Stop
        $sealedGrantPath=Join-Path $existing 'request\authorization-grant.v2.json'
        if(-not(Test-Path -LiteralPath $sealedGrantPath -PathType Leaf)){throw 'ExistingCaseHistoricalAuthorization: sealed v1 authorization is readable but cannot satisfy current semantic resume validation.'}
        $sealedGrant=Get-Content -LiteralPath $sealedGrantPath -Raw|ConvertFrom-Json -ErrorAction Stop
        if([string]$sealedEnvelope.envelopeSha256-cne[string]$Envelope.envelopeSha256 -or (Get-GridCanonicalJsonSha256 -InputObject $sealedPlan)-cne(Get-GridCanonicalJsonSha256 -InputObject $RequestPlan) -or [string]$sealedGrant.grantId-cne$AuthorizationGrantId -or [string]$sealedGrant.semanticBindingSha256-cne[string]$expected.semanticBindingSha256){throw 'ExistingCaseSemanticMismatch: sealed artifacts do not match the current request, plan, or authorization identity.'}
        $resultPath=Join-Path $existing 'result\request-evidence-result.v1.json'; if(-not(Test-Path -LiteralPath $resultPath -PathType Leaf)){throw 'ExistingCaseInvalid: result artifact is missing.'}
        Sync-GridRequestTranscriptIndex -StoreRoot $root|Out-Null
        return [pscustomobject][ordered]@{Status='ResumedSealed';CaseId=$caseId;CaseDirectory=$existing;Result=(Get-Content -LiteralPath $resultPath -Raw|ConvertFrom-Json);CollectorsRerun=$false}
    }

    $lease=Enter-GridAuthorizationLease -StoreRoot $root -GrantId $AuthorizationGrantId -AuthorizationSecret $AuthorizationSecret -ExpectedSemanticBinding $expected.semanticBinding -ConsumerId ('request-' + $SubmissionId)
    $workspaceRoot=Join-Path $root 'workspaces\request-evidence-v2'; $workspace=Join-Path $workspaceRoot $caseId
    if(Test-Path -LiteralPath $workspace -PathType Container){throw 'RequestWorkspaceCollision: unfinished v2 workspace already exists; concurrent/replay execution is refused.'}
    New-Item -ItemType Directory -Path (Join-Path $workspace 'receipts') -Force -ErrorAction Stop|Out-Null
    $identity=[pscustomobject][ordered]@{schemaVersion=2;workspaceId=$caseId;requestId=[string]$Envelope.requestId;submissionId=$SubmissionId;envelopeSha256=[string]$Envelope.envelopeSha256;planSha256=[string]$expected.semanticBinding.planSha256;authorizationGrantId=$AuthorizationGrantId;authorizationBindingSha256=[string]$expected.semanticBindingSha256}
    $identity|Add-Member -NotePropertyName workspaceSha256 -NotePropertyValue(Get-GridCanonicalJsonSha256 -InputObject $identity)
    Write-GridRequestWorkspaceJson -LiteralPath(Join-Path $workspace 'workspace-identity.v2.json') -Value $identity
    Write-GridRequestWorkspaceJson -LiteralPath(Join-Path $workspace 'request-envelope.v1.json') -Value $Envelope
    Write-GridRequestWorkspaceJson -LiteralPath(Join-Path $workspace 'request-plan.v1.json') -Value $RequestPlan
    Write-GridRequestWorkspaceJson -LiteralPath(Join-Path $workspace 'authorization-review.v2.json') -Value $expected
    Write-GridRequestWorkspaceJson -LiteralPath(Join-Path $workspace 'authorization-grant.v2.json') -Value $grant

    $transaction=$null
    $toolRun=$null
    $contextReceipt=$null
    $postReadResult=$null
    $attachmentIndex=$null
    $parentProblemLedger=$null
    try{
        $transaction=New-GridCaseStoreTransaction -StoreRoot $root -CaseId $caseId
        # Persist successor lineage before any collector or post-processing work.
        # A downstream failure is still a first-class successor task and must not
        # fall back to its temporary action submission identity in Activity.
        if($null-ne$InvestigationIntake -and $null-ne$InvestigationIntake.PSObject.Properties['parentTaskId'] -and -not[string]::IsNullOrWhiteSpace([string]$InvestigationIntake.parentTaskId)){
            $parentTasks=@(Get-GridRequestTaskHistory -StoreRoot $root|Where-Object{[string]$_.taskId -ceq [string]$InvestigationIntake.parentTaskId})
            if($parentTasks.Count-ne 1 -or [string]::IsNullOrWhiteSpace([string]$parentTasks[0].caseDirectory)){throw 'ParentTaskNotFound: successor intake requires one exact sealed parent.'}
            $parentManifest=Get-Content -LiteralPath (Join-Path ([string]$parentTasks[0].caseDirectory) 'case-manifest.v1.json') -Raw|ConvertFrom-Json -ErrorAction Stop
            Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'request\successor.v1.json' -Value ([pscustomobject][ordered]@{schemaVersion=1;parentTaskId=[string]$InvestigationIntake.parentTaskId;parentCaseId=[string]$parentManifest.caseId;parentManifestSha256=[string]$parentManifest.manifestSha256;operation='EvidenceSuccessor'})|Out-Null
            $parentLedgerPath=Join-Path ([string]$parentTasks[0].caseDirectory) 'diagnosis\problem-ledger.v1.json'
            if(Test-Path -LiteralPath $parentLedgerPath -PathType Leaf){
                $parentProblemLedger=Get-Content -LiteralPath $parentLedgerPath -Raw|ConvertFrom-Json -ErrorAction Stop
                $parentLedgerValidation=Test-GridProblemLedger -Ledger $parentProblemLedger
                if(-not$parentLedgerValidation.IsValid){throw ('ParentProblemLedgerInvalid: '+($parentLedgerValidation.Errors -join '; '))}
            }
        }
        $scopeMap=@{};foreach($scope in @($expected.scopes)){$scopeMap[[string]$scope.toolId]=$scope}
        $boundExecutor=if($Executor){$Executor}else{{param($definition,$request)Invoke-GridRegisteredToolAdapter -Definition $definition -Envelope $request -AuthorizationScope $scopeMap[[string]$definition.toolId] -ScriptsRoot $ScriptsRoot -CaseDirectory $transaction.CaseDirectory}.GetNewClosure()}
        $checkpointEncoding=New-Object Text.UTF8Encoding($false)
        $receiptSink={param($receipt)$safeToolName=([string]$receipt.toolId).Replace('.','_')+'.json';$receiptPath=Join-Path(Join-Path $workspace 'receipts')$safeToolName;$temp=Join-Path(Split-Path -Parent $receiptPath)('.grid-write-{0}.tmp'-f([Guid]::NewGuid().ToString('N')));try{[IO.File]::WriteAllText($temp,($receipt|ConvertTo-Json -Depth 100 -Compress),$checkpointEncoding);Move-Item -LiteralPath $temp -Destination $receiptPath -Force -ErrorAction Stop}finally{if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue}};if($CheckpointObserver){&$CheckpointObserver $receipt}}.GetNewClosure()
        $toolInputs=[ordered]@{};foreach($scope in @($expected.scopes | Where-Object { [string]$_.toolId -notlike 'grid.intake.*' })){$toolInputs[[string]$scope.toolId]=$scope.inputs}
        $bindingContext=[pscustomobject][ordered]@{workspaceId=$caseId;requestId=[string]$Envelope.requestId;submissionId=$SubmissionId;envelopeSha256=[string]$Envelope.envelopeSha256;planSha256=[string]$expected.semanticBinding.planSha256;authorizationGrantId=$AuthorizationGrantId;authorizationBindingSha256=[string]$expected.semanticBindingSha256;toolInputs=[pscustomobject]$toolInputs}
        if ([string]$RequestPlan.status -eq 'ReadyToCollect' -and @($Envelope.selections.tools).Count -gt 0) {
            $toolRun=Invoke-GridToolEvidenceOrchestration -Envelope $Envelope -RequestPlan $RequestPlan -ScriptsRoot $ScriptsRoot -Executor $boundExecutor -BindingContext $bindingContext -ReceiptSink $receiptSink
        } else {
            $runUnsigned=[pscustomobject][ordered]@{schemaVersion=2;workspaceId=$caseId;requestId=[string]$Envelope.requestId;submissionId=$SubmissionId;envelopeSha256=[string]$Envelope.envelopeSha256;planSha256=[string]$expected.semanticBinding.planSha256;authorizationGrantId=$AuthorizationGrantId;authorizationBindingSha256=[string]$expected.semanticBindingSha256;terminalState='EvidenceUnavailable';mutationAuthorized=$false;receiptSha256s=@()}
            $toolRun=[pscustomobject][ordered]@{schemaVersion=2;workspaceId=$caseId;requestId=[string]$Envelope.requestId;submissionId=$SubmissionId;envelopeSha256=[string]$Envelope.envelopeSha256;planSha256=[string]$expected.semanticBinding.planSha256;authorizationGrantId=$AuthorizationGrantId;authorizationBindingSha256=[string]$expected.semanticBindingSha256;terminalState='EvidenceUnavailable';mutationAuthorized=$false;toolReceipts=@();runSha256=(Get-GridCanonicalJsonSha256 -InputObject $runUnsigned)}
        }
        $contextResult=$null
        $contextScope=@($expected.scopes | Where-Object { [string]$_.toolId -ceq 'grid.intake.mo2-context' })
        if ($contextScope.Count -eq 1) {
            $started=[DateTimeOffset]::UtcNow.ToString('o'); $contextDefinition=@(Get-GridToolRegistry -ScriptsRoot $ScriptsRoot | Where-Object { [string]$_.toolId -ceq 'grid.tool.mo2' })
            if ($contextDefinition.Count -ne 1) { throw 'ContextCaptureUnavailable: registered MO2 tool definition is absent or ambiguous.' }
            $contextResult=Invoke-GridRegisteredToolAdapter -Definition $contextDefinition[0] -Envelope $Envelope -AuthorizationScope $contextScope[0] -ScriptsRoot $ScriptsRoot -CaseDirectory $transaction.CaseDirectory
            $contextUnsigned=[pscustomobject][ordered]@{schemaVersion=1;capabilityId='grid.game.skyrimspecialedition.mo2-context.collect';status=[string]$contextResult.status;reason=$contextResult.reason;startedAt=$started;completedAt=[DateTimeOffset]::UtcNow.ToString('o');evidence=@($contextResult.evidence);authorizationBindingSha256=[string]$expected.semanticBindingSha256}
            $contextReceipt=[pscustomobject][ordered]@{schemaVersion=1;capabilityId=$contextUnsigned.capabilityId;status=$contextUnsigned.status;reason=$contextUnsigned.reason;startedAt=$contextUnsigned.startedAt;completedAt=$contextUnsigned.completedAt;evidence=$contextUnsigned.evidence;authorizationBindingSha256=$contextUnsigned.authorizationBindingSha256;receiptSha256=(Get-GridCanonicalJsonSha256 -InputObject $contextUnsigned)}
        }
        $attachmentIndex=if($null-ne$InvestigationIntake){Add-GridInvestigationAttachments -Transaction $transaction -InvestigationIntake $InvestigationIntake -CaseId $caseId -ContextFingerprint ([string]$Envelope.envelopeSha256) -ScriptsRoot $ScriptsRoot -GameId ([string]$Envelope.context.gameId)}else{[pscustomobject][ordered]@{schemaVersion=1;caseId=$caseId;createdAt=[DateTimeOffset]::UtcNow.ToString('o');attachments=@()}}
        $contextCollected=($null-ne$contextReceipt -and [string]$contextReceipt.status -eq 'Collected')
        $attachmentCount=@($attachmentIndex.attachments).Count; $attachmentImported=@($attachmentIndex.attachments|Where-Object status -eq 'Imported').Count
        if ([string]$toolRun.terminalState -eq 'EvidenceUnavailable' -and ($contextCollected -or $attachmentImported -gt 0)) {
            $toolRun.terminalState=if($attachmentImported -eq $attachmentCount){'EvidenceComplete'}else{'EvidencePartial'}
            $toolRunForHash=[pscustomobject][ordered]@{schemaVersion=[int]$toolRun.schemaVersion;workspaceId=[string]$toolRun.workspaceId;requestId=[string]$toolRun.requestId;submissionId=[string]$toolRun.submissionId;envelopeSha256=[string]$toolRun.envelopeSha256;planSha256=[string]$toolRun.planSha256;authorizationGrantId=[string]$toolRun.authorizationGrantId;authorizationBindingSha256=[string]$toolRun.authorizationBindingSha256;terminalState=[string]$toolRun.terminalState;mutationAuthorized=$false;receiptSha256s=@($toolRun.toolReceipts|ForEach-Object receiptSha256)}
            $toolRun.runSha256=Get-GridCanonicalJsonSha256 -InputObject $toolRunForHash
        }
        if($PostReadCollector){
            $postReadResult=&$PostReadCollector $expected $transaction.CaseDirectory
            if($null-eq$postReadResult){throw 'PostReadCollectorFailed: the authorized post-read collector returned no result.'}
            $postReadStatus=[string]$postReadResult.Status
            $postReadCaseId=if($postReadResult.PSObject.Properties['CaseId']){[string]$postReadResult.CaseId}else{$null}
            $postReadFailure=if($postReadResult.PSObject.Properties['PrimaryFailure']){$postReadResult.PrimaryFailure}else{$null}
            $postReadCode=if($postReadFailure -and $postReadFailure.PSObject.Properties['code']){[string]$postReadFailure.code}elseif($postReadStatus -eq 'PausedAtCheckpoint'){'BaselinePausedAtCheckpoint'}elseif($postReadStatus -eq 'Completed'){'DiagnosticResultMissing'}else{'RegisteredCollectorFailed'}
            $postReadDetail=if($postReadFailure -and $postReadFailure.PSObject.Properties['detail']){[string]$postReadFailure.detail}elseif($postReadResult.PSObject.Properties['Detail']){[string]$postReadResult.Detail}elseif($postReadStatus -eq 'PausedAtCheckpoint'){'The bounded profile baseline paused at a sealed checkpoint.'}else{'The authorized post-read collector returned no deterministic diagnosis result.'}
            Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'evidence\post-read-collector.v1.json' -Value ([pscustomobject][ordered]@{
                schemaVersion=1;authorizationBindingSha256=[string]$expected.semanticBindingSha256
                status=$postReadStatus;terminalState=[string]$postReadResult.TerminalState
                caseId=$postReadCaseId;failureCode=$postReadCode;detail=$postReadDetail
                manifestSha256=if($postReadResult.PSObject.Properties['Manifest'] -and $postReadResult.Manifest){[string]$postReadResult.Manifest.manifestSha256}else{$null}
            })|Out-Null
        }
        $capabilityAssessment=$null
        $result=$null
        if($postReadResult){
            if([string]$postReadResult.Status -eq 'Completed' -and $postReadResult.PSObject.Properties['DiagnosticResult'] -and $postReadResult.DiagnosticResult){
                $postReadEvidence=if($postReadResult.PSObject.Properties['Evidence']){@($postReadResult.Evidence)}else{@()}
                $postReadValidation=Test-GridDiagnosticResult -DiagnosticResult $postReadResult.DiagnosticResult -Evidence $postReadEvidence
                if(-not$postReadValidation.IsValid){throw ('PostReadCollectorDiagnosticInvalid: '+($postReadValidation.Errors -join ' '))}
                $recoveryBaseline=$null
                if(-not[string]::IsNullOrWhiteSpace($postReadCaseId)){
                    if([IO.Path]::GetFileName($postReadCaseId) -cne $postReadCaseId){throw 'RecoveryBaselineLinkInvalid: linked case identity must be one safe path segment.'}
                    $linkedDirectory=Join-Path (Join-Path $root 'cases\v1') $postReadCaseId
                    if(Test-Path -LiteralPath $linkedDirectory -PathType Container){$recoveryBaseline=Resolve-GridRequestRecoveryBaseline -StoreRoot $root -TaskCaseDirectory $linkedDirectory}
                }
                $result=ConvertFrom-GridDiagnosticResultForRequest -Envelope $Envelope -DiagnosticResult $postReadResult.DiagnosticResult -RecoveryBaseline $recoveryBaseline
            }
            elseif([string]$postReadResult.Status -eq 'PausedAtCheckpoint'){
                $result=New-GridDispatchEvidenceResult -Envelope $Envelope -TerminalState EvidencePartial -FailureCode $postReadCode -Detail ($postReadDetail+' Capture Current State again to authorize a successor that resumes this exact sealed checkpoint.') -EvidenceCaseId $postReadCaseId
            }
            elseif([string]$postReadResult.Status -ne 'Completed'){
                $result=New-GridDispatchEvidenceResult -Envelope $Envelope -TerminalState EvidenceFailed -FailureCode $postReadCode -Detail $postReadDetail -EvidenceCaseId $postReadCaseId
            }
        }
        if($null-eq$result -and $null-ne$RequestPlan.PSObject.Properties['requestedCapabilityId'] -and -not[string]::IsNullOrWhiteSpace([string]$RequestPlan.requestedCapabilityId)){
            if([string]$Envelope.context.gameId -cne 'skyrimspecialedition'){throw 'GameplayCapabilityAssessmentUnavailable: the selected capability has no adapter for this game.'}
            if($null-eq$contextResult -or [string]$contextResult.status -cne 'Collected' -or @($contextResult.evidence).Count-ne 1){throw 'GameplayCapabilityAssessmentUnavailable: one current authorized MO2 context is required.'}
            if(@($RequestPlan.capabilityBindings|Where-Object{[string]$_.capabilityId -ceq 'grid.game.skyrimspecialedition.gameplay-capability.assess'}).Count-ne 1){throw 'GameplayCapabilityAssessmentUnavailable: the exact assessment capability is not bound to the request plan.'}
            $collector=Join-Path $ScriptsRoot 'games\skyrimspecialedition\health\collectors\Get-GridSkyrimGameplayCapabilityAssessment.ps1'
            if(-not(Test-Path -LiteralPath $collector -PathType Leaf)){throw 'GameplayCapabilityAssessmentUnavailable: the registered collector is absent.'}
            . $collector
            $profileEvidenceId='sha256:'+([string]$contextReceipt.receiptSha256).ToUpperInvariant()
            $capabilityAssessment=Get-GridSkyrimGameplayCapabilityAssessment -CapabilityId ([string]$RequestPlan.requestedCapabilityId) -ModInventory @($contextResult.evidence[0].ModInventory) -ProfileEvidenceId $profileEvidenceId
            $result=New-GridCapabilityAssessmentResult -Envelope $Envelope -Assessment $capabilityAssessment
            if([string]$toolRun.terminalState -cne [string]$result.terminalState){
                $toolRun.terminalState=[string]$result.terminalState
                $toolRunForHash=[pscustomobject][ordered]@{schemaVersion=[int]$toolRun.schemaVersion;workspaceId=[string]$toolRun.workspaceId;requestId=[string]$toolRun.requestId;submissionId=[string]$toolRun.submissionId;envelopeSha256=[string]$toolRun.envelopeSha256;planSha256=[string]$toolRun.planSha256;authorizationGrantId=[string]$toolRun.authorizationGrantId;authorizationBindingSha256=[string]$toolRun.authorizationBindingSha256;terminalState=[string]$toolRun.terminalState;mutationAuthorized=$false;receiptSha256s=@($toolRun.toolReceipts|ForEach-Object receiptSha256)}
                $toolRun.runSha256=Get-GridCanonicalJsonSha256 -InputObject $toolRunForHash
            }
        }
        if($null-ne$result -and [string]$toolRun.terminalState -cne [string]$result.terminalState){
            $toolRun.terminalState=[string]$result.terminalState
            $toolRunForHash=[pscustomobject][ordered]@{schemaVersion=[int]$toolRun.schemaVersion;workspaceId=[string]$toolRun.workspaceId;requestId=[string]$toolRun.requestId;submissionId=[string]$toolRun.submissionId;envelopeSha256=[string]$toolRun.envelopeSha256;planSha256=[string]$toolRun.planSha256;authorizationGrantId=[string]$toolRun.authorizationGrantId;authorizationBindingSha256=[string]$toolRun.authorizationBindingSha256;terminalState=[string]$toolRun.terminalState;mutationAuthorized=$false;receiptSha256s=@($toolRun.toolReceipts|ForEach-Object receiptSha256)}
            $toolRun.runSha256=Get-GridCanonicalJsonSha256 -InputObject $toolRunForHash
        }
        Complete-GridAuthorizationLease -StoreRoot $root -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Consumed -Detail ('Read execution terminal state: '+[string]$toolRun.terminalState)|Out-Null
        $grant=Read-GridAuthorizationGrant -StoreRoot $root -GrantId $AuthorizationGrantId
        if($null-eq$result){$result=New-GridRequestEvidenceResult -Envelope $Envelope -ToolRun $toolRun -AttachmentIndex $attachmentIndex -ContextReceipt $contextReceipt}
        $problemContextFingerprint=$null
        if($null-ne$contextResult -and @($contextResult.evidence).Count-eq 1 -and $contextResult.evidence[0].PSObject.Properties['Fingerprint']){
            $problemContextFingerprint=[string]$contextResult.evidence[0].Fingerprint
        }
        $problemLedger=New-GridRequestProblemLedger -Envelope $Envelope -RequestPlan $RequestPlan -InvestigationIntake $InvestigationIntake -Result $result -ParentLedger $parentProblemLedger -ContextFingerprint $problemContextFingerprint
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'request\request-envelope.v1.json' -Value $Envelope|Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'request\request-plan.v1.json' -Value $RequestPlan|Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'request\read-authorization.v2.json' -Value $expected|Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'request\authorization-grant.v2.json' -Value $grant|Out-Null
        if($null-ne$InvestigationIntake){Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'request\investigation-intake.v1.json' -Value $InvestigationIntake|Out-Null}
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'evidence\tool-evidence-run.v2.json' -Value $toolRun|Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'attachments\manifest.v1.json' -Value $attachmentIndex|Out-Null
        if($null-ne$contextReceipt){Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'evidence\intake-context.v1.json' -Value $contextReceipt|Out-Null}
        if($null-ne$capabilityAssessment){Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'evidence\gameplay-capability-assessment.v1.json' -Value $capabilityAssessment|Out-Null}
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'result\request-evidence-result.v1.json' -Value $result|Out-Null
        Write-GridProblemLedgerArtifact -Transaction $transaction -Ledger $problemLedger|Out-Null
        $completedAt=[DateTimeOffset]::UtcNow.ToString('o');$startedAt=@($toolRun.toolReceipts|ForEach-Object startedAt|Sort-Object|Select-Object -First 1);if($startedAt.Count-eq0){$startedAt=@($grant.issuedAt)}
        $run=[pscustomobject][ordered]@{schemaVersion=1;runId='run-'+([string]$Envelope.envelopeSha256).Substring(0,16).ToLowerInvariant();caseId=$caseId;state='Completed';startedAt=[string]$startedAt[0];completedAt=$completedAt;planFingerprint=[string]$expected.semanticBinding.planSha256;resourcePolicyVersion='grid.request-evidence.v2';gates=@([pscustomobject]@{gateId='ExactReadAuthorization';status='Pass';evidenceIds=@($AuthorizationGrantId,[string]$expected.semanticBindingSha256)});sufficiency=[pscustomobject]@{status=[string]$toolRun.terminalState;completionAssertsDiagnosis=$false};checkpoints=@();primaryFailure=$null;secondaryFailures=@()}
        Seal-GridCaseStoreRun -Transaction $transaction -Run $run|Out-Null
        $sealed=Seal-GridDiagnosticCase -Transaction $transaction -SemanticBaselineFingerprint $semanticCaseFingerprint -Runs @($run)
        if(Test-Path -LiteralPath $workspace){Remove-Item -LiteralPath $workspace -Recurse -Force -ErrorAction Stop}
        Sync-GridRequestTranscriptIndex -StoreRoot $root|Out-Null
        [pscustomobject][ordered]@{Status=[string]$toolRun.terminalState;CaseId=$caseId;CaseDirectory=$sealed.CaseDirectory;Result=$result;ToolRun=$toolRun;PostReadCollectorResult=$postReadResult;CollectorsRerun=$true}
    }catch{
        $executionError=$_
        $failureDetail=[string]$executionError.Exception.Message
        $failureCode=if($failureDetail -match '^([A-Za-z][A-Za-z0-9]+):'){$matches[1]}elseif($executionError.Exception -is [OutOfMemoryException]){'ExecutionMemoryBudgetExceeded'}else{'RequestExecutionFailed'}
        try{$current=Read-GridAuthorizationGrant -StoreRoot $root -GrantId $AuthorizationGrantId;if([string]$current.state-eq'Executing'){Complete-GridAuthorizationLease -StoreRoot $root -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Failed -Detail $failureDetail|Out-Null}}catch{}

        # A collector/post-processing exception is itself deterministic evidence.
        # Seal a compact failed task whenever the request transaction is still
        # usable instead of deleting both the cause and the user's resumable task.
        try{
            if($transaction -and [string]$transaction.State -eq 'Open' -and (Test-Path -LiteralPath $transaction.CaseDirectory -PathType Container)){
                $failedGrant=Read-GridAuthorizationGrant -StoreRoot $root -GrantId $AuthorizationGrantId
                $failedResult=New-GridDispatchEvidenceResult -Envelope $Envelope -TerminalState EvidenceFailed -FailureCode $failureCode -Detail $failureDetail
                if($null-eq$toolRun){
                    $failedRunUnsigned=[pscustomobject][ordered]@{schemaVersion=2;workspaceId=$caseId;requestId=[string]$Envelope.requestId;submissionId=$SubmissionId;envelopeSha256=[string]$Envelope.envelopeSha256;planSha256=[string]$expected.semanticBinding.planSha256;authorizationGrantId=$AuthorizationGrantId;authorizationBindingSha256=[string]$expected.semanticBindingSha256;terminalState='EvidenceFailed';mutationAuthorized=$false;receiptSha256s=@()}
                    $toolRun=[pscustomobject][ordered]@{schemaVersion=2;workspaceId=$caseId;requestId=[string]$Envelope.requestId;submissionId=$SubmissionId;envelopeSha256=[string]$Envelope.envelopeSha256;planSha256=[string]$expected.semanticBinding.planSha256;authorizationGrantId=$AuthorizationGrantId;authorizationBindingSha256=[string]$expected.semanticBindingSha256;terminalState='EvidenceFailed';mutationAuthorized=$false;toolReceipts=@();runSha256=(Get-GridCanonicalJsonSha256 -InputObject $failedRunUnsigned)}
                }else{
                    $toolRun.terminalState='EvidenceFailed'
                    $failedRunUnsigned=[pscustomobject][ordered]@{schemaVersion=[int]$toolRun.schemaVersion;workspaceId=[string]$toolRun.workspaceId;requestId=[string]$toolRun.requestId;submissionId=[string]$toolRun.submissionId;envelopeSha256=[string]$toolRun.envelopeSha256;planSha256=[string]$toolRun.planSha256;authorizationGrantId=[string]$toolRun.authorizationGrantId;authorizationBindingSha256=[string]$toolRun.authorizationBindingSha256;terminalState='EvidenceFailed';mutationAuthorized=$false;receiptSha256s=@($toolRun.toolReceipts|ForEach-Object receiptSha256)}
                    $toolRun.runSha256=Get-GridCanonicalJsonSha256 -InputObject $failedRunUnsigned
                }
                Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'request\request-envelope.v1.json' -Value $Envelope|Out-Null
                Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'request\request-plan.v1.json' -Value $RequestPlan|Out-Null
                Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'request\read-authorization.v2.json' -Value $expected|Out-Null
                Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'request\authorization-grant.v2.json' -Value $failedGrant|Out-Null
                if($null-ne$InvestigationIntake){Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'request\investigation-intake.v1.json' -Value $InvestigationIntake|Out-Null}
                Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'evidence\tool-evidence-run.v2.json' -Value $toolRun|Out-Null
                Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'failure\request-execution-failure.v1.json' -Value ([pscustomobject][ordered]@{schemaVersion=1;failureCode=$failureCode;detail=$failureDetail;failedAt=[DateTimeOffset]::UtcNow.ToString('o');authorizationBindingSha256=[string]$expected.semanticBindingSha256})|Out-Null
                Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'result\request-evidence-result.v1.json' -Value $failedResult|Out-Null
                $failureEvidenceId='failure.'+(Get-GridCanonicalJsonSha256 -InputObject ([pscustomobject][ordered]@{failureCode=$failureCode;detail=$failureDetail;authorizationBindingSha256=[string]$expected.semanticBindingSha256})).Substring(0,24).ToLowerInvariant()
                $failedProblemLedger=New-GridRequestProblemLedger -Envelope $Envelope -RequestPlan $RequestPlan -InvestigationIntake $InvestigationIntake -Result $failedResult -ParentLedger $parentProblemLedger -AdditionalEvidenceIds @($failureEvidenceId)
                Write-GridProblemLedgerArtifact -Transaction $transaction -Ledger $failedProblemLedger|Out-Null
                $failedAt=[DateTimeOffset]::UtcNow.ToString('o')
                $failedRun=[pscustomobject][ordered]@{schemaVersion=1;runId='run-'+([string]$Envelope.envelopeSha256).Substring(0,16).ToLowerInvariant();caseId=$caseId;state='Failed';startedAt=[string]$failedGrant.issuedAt;completedAt=$failedAt;planFingerprint=[string]$expected.semanticBinding.planSha256;resourcePolicyVersion='grid.request-evidence.v2';gates=@([pscustomobject]@{gateId='ExactReadAuthorization';status='Pass';evidenceIds=@($AuthorizationGrantId,[string]$expected.semanticBindingSha256)});sufficiency=[pscustomobject]@{status='EvidenceFailed';completionAssertsDiagnosis=$false};checkpoints=@();primaryFailure=[pscustomobject]@{code=$failureCode;message=$failureDetail;at=$failedAt};secondaryFailures=@()}
                Seal-GridCaseStoreRun -Transaction $transaction -Run $failedRun|Out-Null
                $failedSealed=Seal-GridDiagnosticCase -Transaction $transaction -SemanticBaselineFingerprint $semanticCaseFingerprint -Runs @($failedRun)
                if(Test-Path -LiteralPath $workspace){Remove-Item -LiteralPath $workspace -Recurse -Force -ErrorAction Stop}
                Sync-GridRequestTranscriptIndex -StoreRoot $root|Out-Null
                return [pscustomobject][ordered]@{Status='EvidenceFailed';CaseId=$caseId;CaseDirectory=$failedSealed.CaseDirectory;Result=$failedResult;ToolRun=$toolRun;PostReadCollectorResult=$postReadResult;CollectorsRerun=$true}
            }
        }catch{
            # Keep the unfinished workspace when even failure sealing cannot
            # complete; startup history can then expose it as interrupted rather
            # than erasing the only durable execution trace.
        }
        throw $executionError
    }
}
