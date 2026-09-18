#requires -Version 5.1

<#!
.SYNOPSIS
Creates and executes byte-exact authorized Undo/Redo transitions for ESL flags.
#>
$gameRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $gameRoot 'mo2\Grid.PluginStateBatch.ps1')
. (Join-Path $gameRoot 'mo2\Grid.EslFlag.ps1')

function Get-GridSkyrimEslFlagHistoryTransition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SpecificationPath,
        [Parameter(Mandatory)][string]$TransactionRoot,
        [Parameter(Mandatory)][ValidateSet('Undo','Redo')][string]$Direction
    )
    $spec = Get-Content -LiteralPath $SpecificationPath -Raw | ConvertFrom-Json -ErrorAction Stop
    $sha = Get-GridEslFlagSpecificationHash -Specification $spec
    if ([string]$spec.specificationSha256 -cne $sha) { throw 'EslFlagSpecificationDigestMismatch: specification changed.' }
    $directory = [IO.Path]::GetFullPath((Join-Path $TransactionRoot ('esl-flag-' + $sha)))
    $statePath = Join-Path $directory 'history-state.v1.json'
    $beforePath = Join-Path $directory 'plugin.before.bin'
    $afterPath = Join-Path $directory 'plugin.after.bin'
    foreach ($path in @($statePath,$beforePath,$afterPath)) { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "EslFlagHistoryMissing: $path" } }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -ErrorAction Stop
    if ($Direction -eq 'Undo' -and [string]$state.state -cne 'Applied') { throw "EslFlagHistoryStateInvalid: Undo requires Applied, found $($state.state)." }
    if ($Direction -eq 'Redo' -and [string]$state.state -cne 'Undone') { throw "EslFlagHistoryStateInvalid: Redo requires Undone, found $($state.state)." }
    $fromSha = if ($Direction -eq 'Undo') { [string]$spec.plugin.expectedAfterSha256 } else { [string]$spec.plugin.beforeSha256 }
    $toSha = if ($Direction -eq 'Undo') { [string]$spec.plugin.beforeSha256 } else { [string]$spec.plugin.expectedAfterSha256 }
    $imagePath = if ($Direction -eq 'Undo') { $beforePath } else { $afterPath }
    if ((Get-FileHash -LiteralPath $imagePath -Algorithm SHA256).Hash.ToUpperInvariant() -cne $toSha) { throw 'EslFlagHistoryImageDrift: preserved transition image changed.' }
    $normalized = [pscustomobject][ordered]@{
        schemaVersion=1;transactionId=[string]$state.transactionId;specificationSha256=$sha;direction=$Direction;revision=([int]$state.revision + 1)
        pluginName=[string]$spec.plugin.name;pluginPath=[string]$spec.plugin.path;fromSha256=$fromSha;toSha256=$toSha;imagePath=$imagePath
    }
    $transitionSha = Get-GridCanonicalJsonSha256 -InputObject $normalized
    [pscustomobject][ordered]@{
        schemaVersion=1;transitionId=('esl-flag-history-' + $transitionSha.Substring(0,24).ToLowerInvariant());transitionSha256=$transitionSha
        targets=@("plugin:$($spec.plugin.name):$fromSha=>$toSha");normalizedInput=$normalized;specification=$spec;transactionDirectory=$directory;state=$state;statePath=$statePath
    }
}

function Test-GridEslFlagHistoryProtectedState {
    param([Parameter(Mandatory)]$Specification,[Parameter(Mandatory)][string]$CaseStoreRoot)
    foreach ($pair in @(
        @([string]$Specification.paths.modListFile,[string]$Specification.protectedState.modListFileSha256),
        @([string]$Specification.paths.pluginsFile,[string]$Specification.protectedState.pluginsFileSha256),
        @([string]$Specification.paths.loadOrderFile,[string]$Specification.protectedState.loadOrderFileSha256)
    )) { if ((Get-FileHash -LiteralPath ([string]$pair[0]) -Algorithm SHA256).Hash.ToUpperInvariant() -cne [string]$pair[1]) { throw "EslFlagCurrentStateChanged: $($pair[0])" } }
    $seal = Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory ([string]$Specification.evidenceCase.directory)
    if (-not $seal.IsValid -or [string]$seal.Manifest.manifestSha256 -cne [string]$Specification.evidenceCase.manifestSha256) { throw 'EslFlagEvidenceCaseInvalid: sealed evidence changed.' }
}

function Write-GridEslFlagHistoryBytesAtomic {
    param([Parameter(Mandatory)][string]$LiteralPath,[Parameter(Mandatory)][byte[]]$Bytes)
    $temporary = Join-Path (Split-Path -Parent $LiteralPath) ('.grid-esl-history-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $stream = New-Object IO.FileStream($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
        try { $stream.Write($Bytes,0,$Bytes.Length);$stream.Flush($true) } finally { $stream.Dispose() }
        Move-Item -LiteralPath $temporary -Destination $LiteralPath -Force -ErrorAction Stop
    } finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue } }
}

function Invoke-GridAuthorizedEslFlagHistory {
    [CmdletBinding(SupportsShouldProcess,ConfirmImpact='High')]
    param(
        [Parameter(Mandatory)][string]$SpecificationPath,
        [Parameter(Mandatory)][string]$TransactionRoot,
        [Parameter(Mandatory)][ValidateSet('Undo','Redo')][string]$Direction,
        [Parameter(Mandatory)][string]$AuthorizationGrantId,
        [Parameter(Mandatory)][string]$AuthorizationSecret,
        [Parameter(Mandatory)][string]$AuthorizationStoreRoot,
        [Parameter(Mandatory)]$SemanticBinding,
        [Parameter(Mandatory)][string]$CaseStoreRoot,
        [switch]$PassThru
    )
    $transition = Get-GridSkyrimEslFlagHistoryTransition -SpecificationPath $SpecificationPath -TransactionRoot $TransactionRoot -Direction $Direction
    $spec = $transition.specification
    if ([string]$SemanticBinding.proposalOrSpecificationId -cne [string]$transition.transitionId -or [string]$SemanticBinding.proposalOrSpecificationSha256 -cne [string]$transition.transitionSha256 -or [string]$SemanticBinding.normalizedInputSha256 -cne [string]$transition.transitionSha256) { throw 'AuthorizationBindingMismatch: ESL flag history identity changed.' }
    if (@($SemanticBinding.capabilities | Where-Object { [string]$_.capabilityId -ceq 'grid.game.skyrimspecialedition.esl-flag.history.execute' -and [string]$_.capabilityVersion -ceq '1.0.0' }).Count -ne 1) { throw 'AuthorizationBindingMismatch: ESL flag history capability/version is not authorized.' }
    if ((@($SemanticBinding.targets | Sort-Object -Unique) -join "`n") -cne (@($transition.targets | Sort-Object -Unique) -join "`n")) { throw 'AuthorizationBindingMismatch: exact ESL flag history target changed.' }
    $preflight = Test-GridAuthorizationGrantPreflight -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -AuthorizationSecret $AuthorizationSecret -ExpectedSemanticBinding $SemanticBinding
    if ([string]$preflight.state -cne 'Issued') { throw 'AuthorizationReplayRefused: only a newly issued grant can execute this history transition.' }
    Test-GridEslFlagHistoryProtectedState -Specification $spec -CaseStoreRoot $CaseStoreRoot
    $pluginPath = [string]$spec.plugin.path
    if ((Get-FileHash -LiteralPath $pluginPath -Algorithm SHA256).Hash.ToUpperInvariant() -cne [string]$transition.normalizedInput.fromSha256) { throw 'EslFlagHistoryTargetDrift: plugin is not the exact expected source image.' }
    Test-GridEslFlagProcessesClosed -Specification $spec
    $revision=[int]$transition.normalizedInput.revision;$newState=if($Direction-eq'Undo'){'Undone'}else{'Applied'}
    $receiptPath=Join-Path ([string]$transition.transactionDirectory) ("history-receipt-$revision.v1.json")
    if(Test-Path -LiteralPath $receiptPath){throw 'EslFlagHistoryRevisionCollision: receipt already exists.'}
    $lease = Enter-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -AuthorizationSecret $AuthorizationSecret -ExpectedSemanticBinding $SemanticBinding -ConsumerId ([string]$transition.transitionId)
    if ($WhatIfPreference -or -not $PSCmdlet.ShouldProcess($pluginPath,"$Direction exact ESL flag transition and verify byte identity")) {
        Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Consumed -Detail 'WhatIf/ShouldProcess declined; authorization consumed without mutation.' | Out-Null
        $planned=[pscustomobject][ordered]@{schemaVersion=1;status='Planned';direction=$Direction;transitionId=[string]$transition.transitionId;state=[string]$transition.state.state;revision=[int]$transition.state.revision}
        if($PassThru){return $planned}else{return($planned|ConvertTo-Json -Depth 12)}
    }
    $original=[IO.File]::ReadAllBytes($pluginPath);$image=[IO.File]::ReadAllBytes([string]$transition.normalizedInput.imagePath)
    try {
        Test-GridEslFlagProcessesClosed -Specification $spec
        Test-GridEslFlagHistoryProtectedState -Specification $spec -CaseStoreRoot $CaseStoreRoot
        if ((Get-FileHash -LiteralPath $pluginPath -Algorithm SHA256).Hash.ToUpperInvariant() -cne [string]$transition.normalizedInput.fromSha256) { throw 'EslFlagHistoryTargetDrift: plugin changed immediately before commit.' }
        Write-GridEslFlagHistoryBytesAtomic -LiteralPath $pluginPath -Bytes $image
        if ((Get-FileHash -LiteralPath $pluginPath -Algorithm SHA256).Hash.ToUpperInvariant() -cne [string]$transition.normalizedInput.toSha256) { throw 'EslFlagHistoryPostconditionFailed: resulting digest differs.' }
        $receipt=[pscustomobject][ordered]@{schemaVersion=1;status='Verified';direction=$Direction;transitionId=[string]$transition.transitionId;transitionSha256=[string]$transition.transitionSha256;state=$newState;revision=$revision;pluginSha256=[string]$transition.normalizedInput.toSha256;completedAt=[DateTimeOffset]::UtcNow.ToString('o')}
        Write-GridJsonAtomic -InputObject $receipt -LiteralPath $receiptPath
        Write-GridJsonAtomic -InputObject ([pscustomobject][ordered]@{schemaVersion=1;transactionId=[string]$transition.state.transactionId;specificationSha256=[string]$spec.specificationSha256;state=$newState;revision=$revision;currentPluginSha256=[string]$transition.normalizedInput.toSha256;lastReceiptPath=$receiptPath}) -LiteralPath ([string]$transition.statePath)
        Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Consumed -Detail "$Direction completed and verified." | Out-Null
    } catch {
        $failure=$_.Exception.Message
        try { Write-GridEslFlagHistoryBytesAtomic -LiteralPath $pluginPath -Bytes $original;if((Get-FileHash -LiteralPath $pluginPath -Algorithm SHA256).Hash.ToUpperInvariant()-cne[string]$transition.normalizedInput.fromSha256){throw 'rollback digest mismatch'};if(Test-Path -LiteralPath $receiptPath -PathType Leaf){Remove-Item -LiteralPath $receiptPath -Force};Write-GridJsonAtomic -InputObject $transition.state -LiteralPath ([string]$transition.statePath) }
        catch { try{Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Failed -Detail "Indeterminate: $failure"|Out-Null}catch{};throw "EslFlagHistoryIndeterminate: $failure" }
        try{Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Failed -Detail $failure|Out-Null}catch{}
        throw "EslFlagHistoryRolledBack: $failure"
    }
    if($PassThru){$receipt}else{$receipt|ConvertTo-Json -Depth 20}
}
