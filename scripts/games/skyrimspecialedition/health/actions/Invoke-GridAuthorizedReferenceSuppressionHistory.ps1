#requires -Version 5.1

function Get-GridSkyrimReferenceSuppressionHistoryTransition {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$SpecificationPath,[Parameter(Mandatory)][string]$TransactionRoot,[Parameter(Mandatory)][ValidateSet('Undo','Redo')][string]$Direction)
    $spec=Get-Content -LiteralPath ([IO.Path]::GetFullPath($SpecificationPath)) -Raw|ConvertFrom-Json -ErrorAction Stop
    if([int]$spec.schemaVersion-ne2){throw 'ReferenceSuppressionSpecificationVersionUnsupported.'}
    $sha=Get-GridSkyrimReferenceSuppressionSpecificationHash -Specification $spec
    if([string]$spec.specificationSha256-cne$sha){throw 'ReferenceSuppressionSpecificationDigestMismatch.'}
    $directory=[IO.Path]::GetFullPath((Join-Path $TransactionRoot ('reference-suppression-'+$sha)))
    $statePath=Join-Path $directory 'history-state.v1.json';$receiptPath=Join-Path $directory 'receipt.v1.json';$afterPath=Join-Path $directory 'patch.after.esp'
    foreach($path in @($statePath,$receiptPath,$afterPath)){if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw "ReferenceSuppressionHistoryMissing: $path"}}
    $state=Get-Content -LiteralPath $statePath -Raw|ConvertFrom-Json -ErrorAction Stop;$receipt=Get-Content -LiteralPath $receiptPath -Raw|ConvertFrom-Json -ErrorAction Stop
    if($Direction-eq'Undo'-and[string]$state.state-cne'Applied'){throw "ReferenceSuppressionHistoryStateInvalid: Undo requires Applied, found $($state.state)."}
    if($Direction-eq'Redo'-and[string]$state.state-cne'Undone'){throw "ReferenceSuppressionHistoryStateInvalid: Redo requires Undone, found $($state.state)."}
    $afterSha=(Get-FileHash -LiteralPath $afterPath -Algorithm SHA256).Hash.ToUpperInvariant();if($afterSha-cne[string]$receipt.patchSha256){throw 'ReferenceSuppressionHistoryImageDrift.'}
    $revision=[int]$state.revision+1;$target=[IO.Path]::GetFullPath([string]$receipt.patchPath)
    $normalized=[pscustomobject][ordered]@{direction=$Direction;specificationSha256=$sha;fromState=[string]$state.state;toState=if($Direction-eq'Undo'){'Undone'}else{'Applied'};patchPath=$target;patchSha256=$afterSha;revision=$revision}
    $transitionSha=Get-GridCanonicalJsonSha256 -InputObject $normalized
    [pscustomobject][ordered]@{schemaVersion=1;transitionId=('reference-suppression-history-'+$transitionSha.Substring(0,24).ToLowerInvariant());transitionSha256=$transitionSha;direction=$Direction;normalizedInput=$normalized;targets=@("patch:$target");directory=$directory;statePath=$statePath;receiptPath=$receiptPath;afterPath=$afterPath;state=$state;receipt=$receipt;revision=$revision;revisionReceiptPath=Join-Path $directory ('history-receipt-{0:D4}.v1.json'-f$revision)}
}

function Copy-GridReferenceSuppressionHistoryImageAtomic {
    param([Parameter(Mandatory)][string]$Source,[Parameter(Mandatory)][string]$Destination)
    $parent=Split-Path -Parent $Destination;if(-not(Test-Path -LiteralPath $parent -PathType Container)){New-Item -ItemType Directory -Path $parent -ErrorAction Stop|Out-Null}
    $temp=Join-Path $parent ('.grid-history-'+[guid]::NewGuid().ToString('N')+'.tmp')
    try{Copy-Item -LiteralPath $Source -Destination $temp -ErrorAction Stop;Move-Item -LiteralPath $temp -Destination $Destination -ErrorAction Stop}finally{if(Test-Path -LiteralPath $temp -PathType Leaf){Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue}}
}

function Invoke-GridAuthorizedReferenceSuppressionHistory {
    [CmdletBinding(SupportsShouldProcess,ConfirmImpact='High')]
    param([Parameter(Mandatory)][string]$SpecificationPath,[Parameter(Mandatory)][string]$TransactionRoot,[Parameter(Mandatory)][ValidateSet('Undo','Redo')][string]$Direction,[Parameter(Mandatory)][string]$AuthorizationGrantId,[Parameter(Mandatory)][string]$AuthorizationSecret,[Parameter(Mandatory)][string]$AuthorizationStoreRoot,[Parameter(Mandatory)]$SemanticBinding,[Parameter(Mandatory)][string]$CaseStoreRoot,[switch]$PassThru)
    $transition=Get-GridSkyrimReferenceSuppressionHistoryTransition -SpecificationPath $SpecificationPath -TransactionRoot $TransactionRoot -Direction $Direction
    if([string]$SemanticBinding.proposalOrSpecificationId-cne[string]$transition.transitionId-or[string]$SemanticBinding.proposalOrSpecificationSha256-cne[string]$transition.transitionSha256){throw 'AuthorizationBindingMismatch: reference-suppression history identity changed.'}
    if([string]$SemanticBinding.normalizedInputSha256-cne(Get-GridCanonicalJsonSha256 -InputObject $transition.normalizedInput)){throw 'AuthorizationBindingMismatch: reference-suppression history input changed.'}
    if(@($SemanticBinding.capabilities|Where-Object{[string]$_.capabilityId-ceq'grid.game.skyrimspecialedition.reference-suppression.history.execute'-and[string]$_.capabilityVersion-ceq'1.0.0'}).Count-ne1){throw 'AuthorizationBindingMismatch: reference-suppression history capability/version is not authorized.'}
    $bound=@($SemanticBinding.targets|ForEach-Object{[string]$_}|Sort-Object -Unique);$expected=@($transition.targets|Sort-Object -Unique);if(($bound-join"`n")-cne($expected-join"`n")){throw 'AuthorizationBindingMismatch: exact history target changed.'}
    $spec=Get-Content -LiteralPath ([IO.Path]::GetFullPath($SpecificationPath)) -Raw|ConvertFrom-Json -ErrorAction Stop
    $execution=Get-GridReferenceSuppressionExecutionContext -Specification $spec -CaseStoreRoot $CaseStoreRoot
    if([IO.Path]::GetFullPath([string]$transition.receipt.patchPath) -ine [IO.Path]::GetFullPath([string]$execution.patchDestinationPath)){throw 'ReferenceSuppressionHistoryTargetChanged.'}
    Test-GridReferenceSuppressionProcessesClosed
    $target=[IO.Path]::GetFullPath([string]$transition.normalizedInput.patchPath);$modDirectory=Split-Path -Parent $target;$expectedSha=[string]$transition.normalizedInput.patchSha256
    if($Direction-eq'Undo'){
        if(-not(Test-Path -LiteralPath $target -PathType Leaf)-or(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToUpperInvariant()-cne$expectedSha){throw 'ReferenceSuppressionHistoryCurrentStateChanged: patch is not the exact applied image.'}
    }else{if(Test-Path -LiteralPath $target){throw 'ReferenceSuppressionHistoryCurrentStateChanged: redo target already exists.'};if((Test-Path -LiteralPath $modDirectory -PathType Container) -and @(Get-ChildItem -LiteralPath $modDirectory -Force).Count){throw 'ReferenceSuppressionHistoryCurrentStateChanged: redo mod directory is not empty.'}}
    if(Test-Path -LiteralPath $transition.revisionReceiptPath){throw 'ReferenceSuppressionHistoryRevisionCollision.'}
    $lease=Enter-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -AuthorizationSecret $AuthorizationSecret -ExpectedSemanticBinding $SemanticBinding -ConsumerId ('reference-suppression-history-'+[string]$transition.transitionId)
    if(-not$PSCmdlet.ShouldProcess($target,"$Direction exact generated reference-suppression patch")){Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Consumed -Detail 'WhatIf: no history mutation.'|Out-Null;return [pscustomobject]@{State='Incomplete';ChangedExternalState=$false}}
    $originalExists=Test-Path -LiteralPath $target -PathType Leaf
    try{
        Test-GridReferenceSuppressionProcessesClosed
        if($Direction-eq'Undo'){
            if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToUpperInvariant()-cne$expectedSha){throw 'ReferenceSuppressionHistoryCurrentStateChangedImmediatelyBeforeCommit.'}
            Remove-Item -LiteralPath $target -Force -ErrorAction Stop
            if(@(Get-ChildItem -LiteralPath $modDirectory -Force).Count-eq0){Remove-Item -LiteralPath $modDirectory -Force -ErrorAction Stop}
            if(Test-Path -LiteralPath $target){throw 'ReferenceSuppressionHistoryUndoPostconditionFailed.'}
            $newState='Undone'
        }else{
            Copy-GridReferenceSuppressionHistoryImageAtomic -Source $transition.afterPath -Destination $target
            if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToUpperInvariant()-cne$expectedSha){throw 'ReferenceSuppressionHistoryRedoPostconditionFailed.'}
            $newState='Applied'
        }
        $revisionReceipt=[pscustomobject][ordered]@{schemaVersion=1;transitionId=[string]$transition.transitionId;transitionSha256=[string]$transition.transitionSha256;direction=$Direction;revision=[int]$transition.revision;state=$newState;patchPath=$target;patchSha256=$expectedSha;completedAt=[DateTimeOffset]::UtcNow.ToString('o')}
        Write-GridJsonAtomic -InputObject $revisionReceipt -LiteralPath $transition.revisionReceiptPath
        Write-GridJsonAtomic -InputObject ([pscustomobject][ordered]@{schemaVersion=1;transactionId=[string]$transition.receipt.transactionId;state=$newState;revision=[int]$transition.revision;currentPatchSha256=if($newState-eq'Applied'){$expectedSha}else{$null};lastReceiptPath=[string]$transition.revisionReceiptPath}) -LiteralPath $transition.statePath
        Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Consumed -Detail "$Direction verified for $target"|Out-Null
        $result=[pscustomobject][ordered]@{State=$newState;ChangedExternalState=$true;Direction=$Direction;PatchPath=$target;PatchSha256=if($newState-eq'Applied'){$expectedSha}else{$null};ReceiptPath=$transition.revisionReceiptPath}
    }catch{
        $failure=$_.Exception.Message
        try{if($originalExists-and-not(Test-Path -LiteralPath $target -PathType Leaf)){Copy-GridReferenceSuppressionHistoryImageAtomic -Source $transition.afterPath -Destination $target}elseif(-not$originalExists-and(Test-Path -LiteralPath $target -PathType Leaf)){Remove-Item -LiteralPath $target -Force};Write-GridJsonAtomic -InputObject $transition.state -LiteralPath $transition.statePath}catch{$failure+=' Rollback failed: '+$_.Exception.Message}
        try{Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Failed -Detail $failure|Out-Null}catch{}
        throw "ReferenceSuppressionHistoryRolledBack: $failure"
    }
    if($PassThru){$result}else{$result|ConvertTo-Json -Depth 20}
}
