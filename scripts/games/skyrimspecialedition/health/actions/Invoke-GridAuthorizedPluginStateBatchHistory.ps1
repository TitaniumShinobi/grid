#requires -Version 5.1

<#!
.SYNOPSIS
Creates and executes exact authorized Undo/Redo transitions for plugin batches.
#>

$gameRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $gameRoot)
Import-Module (Join-Path $scriptsRoot 'health\Grid.Health.psm1') -Force -DisableNameChecking
. (Join-Path $gameRoot 'mo2\Grid.PluginStateBatch.ps1')

function Get-GridSkyrimPluginStateBatchHistoryTransition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SpecificationPath,
        [Parameter(Mandatory)][string]$TransactionRoot,
        [Parameter(Mandatory)][ValidateSet('Undo','Redo')][string]$Direction
    )
    $spec = Get-Content -LiteralPath $SpecificationPath -Raw | ConvertFrom-Json -ErrorAction Stop
    $sha = Get-GridPluginStateBatchSpecificationHash -Specification $spec
    if ([string]$spec.specificationSha256 -cne $sha) { throw 'PluginBatchSpecificationDigestMismatch: specification changed.' }
    $directory = [IO.Path]::GetFullPath((Join-Path $TransactionRoot ('plugin-batch-' + $sha)))
    $statePath = Join-Path $directory 'history-state.v1.json'; $beforePath = Join-Path $directory 'plugins.before.bin'; $afterPath = Join-Path $directory 'plugins.after.bin'
    foreach ($path in @($statePath,$beforePath,$afterPath)) { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "PluginBatchHistoryMissing: $path" } }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -ErrorAction Stop
    if ($Direction -eq 'Undo' -and [string]$state.state -ne 'Applied') { throw "PluginBatchHistoryStateInvalid: Undo requires Applied, found $($state.state)." }
    if ($Direction -eq 'Redo' -and [string]$state.state -ne 'Undone') { throw "PluginBatchHistoryStateInvalid: Redo requires Undone, found $($state.state)." }
    $fromSha = if ($Direction -eq 'Undo') { [string]$spec.protectedState.expectedAfterPluginsFileSha256 } else { [string]$spec.protectedState.pluginsFileSha256 }
    $toSha = if ($Direction -eq 'Undo') { [string]$spec.protectedState.pluginsFileSha256 } else { [string]$spec.protectedState.expectedAfterPluginsFileSha256 }
    $imagePath = if ($Direction -eq 'Undo') { $beforePath } else { $afterPath }
    if ((Get-GridPluginStateBatchSha256File $imagePath) -cne $toSha) { throw 'PluginBatchHistoryImageDrift: preserved transition image changed.' }
    $normalized = [pscustomobject][ordered]@{
        schemaVersion=1; transactionId=[string]$state.transactionId; specificationSha256=$sha; direction=$Direction
        revision=([int]$state.revision + 1); pluginsFile=[string]$spec.paths.pluginsFile; fromSha256=$fromSha; toSha256=$toSha; imagePath=$imagePath
    }
    $transitionSha = Get-GridCanonicalJsonSha256 -InputObject $normalized
    [pscustomobject][ordered]@{
        schemaVersion=1; transitionId=('plugin-batch-history-' + $transitionSha.Substring(0,24).ToLowerInvariant()); transitionSha256=$transitionSha
        targets=@("plugins.txt:$fromSha=>$toSha"); normalizedInput=$normalized; specification=$spec; transactionDirectory=$directory
        state=$state; statePath=$statePath
    }
}

function Test-GridPluginStateBatchHistoryProcessGate {
    param([Parameter(Mandatory)]$Specification)
    $blocked = @('ModOrganizer','SkyrimSE','skse64_loader','SkyrimSELauncher','SSEEdit','xEdit')
    $mo2Root=([string]$Specification.paths.mo2Root).TrimEnd('\','/'); $gameRoot=(Split-Path -Parent ([string]$Specification.paths.gameDataRoot)).TrimEnd('\','/')
    $running=@(Get-Process -ErrorAction SilentlyContinue|Where-Object{if($blocked-notcontains$_.ProcessName){return $false};try{$path=[string]$_.Path}catch{$path=''};[string]::IsNullOrWhiteSpace($path)-or$path.StartsWith($mo2Root+'\',[StringComparison]::OrdinalIgnoreCase)-or$path.StartsWith($gameRoot+'\',[StringComparison]::OrdinalIgnoreCase)}|Select-Object -ExpandProperty ProcessName -Unique)
    if ($running.Count) { throw ('PluginBatchProcessGateRefused: close the target MO2, Skyrim, and xEdit processes first. Running: ' + (@($running | Sort-Object) -join ', ')) }
}

function Write-GridPluginStateBatchHistoryBytesAtomic {
    param([Parameter(Mandatory)][string]$LiteralPath, [Parameter(Mandatory)][byte[]]$Bytes)
    $temporary = Join-Path (Split-Path -Parent $LiteralPath) ('.grid-plugin-history-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $stream = New-Object IO.FileStream($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
        try { $stream.Write($Bytes,0,$Bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        Move-Item -LiteralPath $temporary -Destination $LiteralPath -Force -ErrorAction Stop
    } finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue } }
}

function Test-GridPluginStateBatchHistoryProtectedSources {
    param([Parameter(Mandatory)]$Specification)
    foreach ($pair in @(
        @([string]$Specification.paths.modListFile,[string]$Specification.protectedState.modListFileSha256),
        @([string]$Specification.paths.loadOrderFile,[string]$Specification.protectedState.loadOrderFileSha256)
    )) { if ((Get-GridPluginStateBatchSha256File ([string]$pair[0])) -cne [string]$pair[1]) { throw "PluginBatchCurrentStateChanged: $($pair[0])" } }
    $manifest = Get-GridPluginStateBatchCreationManifest -LiteralPath ([string]$Specification.paths.creationManifestFile)
    if ([string]$manifest.status -cne [string]$Specification.protectedState.creationManifestStatus -or
        [string]$manifest.fingerprint -cne [string]$Specification.protectedState.creationManifestFingerprint) {
        throw 'PluginBatchCurrentStateChanged: Skyrim.ccc'
    }
}

function Invoke-GridAuthorizedPluginStateBatchHistory {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
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
    $transition = Get-GridSkyrimPluginStateBatchHistoryTransition -SpecificationPath $SpecificationPath -TransactionRoot $TransactionRoot -Direction $Direction
    $spec = $transition.specification
    if ([string]$SemanticBinding.proposalOrSpecificationId -cne [string]$transition.transitionId -or
        [string]$SemanticBinding.proposalOrSpecificationSha256 -cne [string]$transition.transitionSha256 -or
        [string]$SemanticBinding.normalizedInputSha256 -cne [string]$transition.transitionSha256) { throw 'AuthorizationBindingMismatch: plugin batch history identity changed.' }
    if (@($SemanticBinding.capabilities | Where-Object { [string]$_.capabilityId -ceq 'grid.game.skyrimspecialedition.plugin-state-batch.history.execute' -and [string]$_.capabilityVersion -ceq '1.0.0' }).Count -ne 1) { throw 'AuthorizationBindingMismatch: plugin batch history capability/version is not authorized.' }
    if ((@($SemanticBinding.targets | Sort-Object -Unique) -join "`n") -cne (@($transition.targets | Sort-Object -Unique) -join "`n")) { throw 'AuthorizationBindingMismatch: exact history target changed.' }
    $grantPreflight=Test-GridAuthorizationGrantPreflight -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -AuthorizationSecret $AuthorizationSecret -ExpectedSemanticBinding $SemanticBinding
    if ([string]$grantPreflight.state -ne 'Issued') { throw 'AuthorizationReplayRefused: only a newly issued grant can execute this history transition.' }
    $seal = Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory ([string]$spec.baseline.caseDirectory)
    if (-not $seal.IsValid -or [string]$seal.Manifest.manifestSha256 -cne [string]$spec.baseline.manifestSha256) { throw 'PluginBatchBaselineSealInvalid: sealed evidence changed.' }
    Test-GridPluginStateBatchHistoryProtectedSources -Specification $spec
    $pluginsFile = [string]$spec.paths.pluginsFile
    if ((Get-GridPluginStateBatchSha256File $pluginsFile) -cne [string]$transition.normalizedInput.fromSha256) { throw 'PluginBatchHistoryTargetDrift: plugins.txt is not the exact expected source image.' }
    Test-GridPluginStateBatchHistoryProcessGate -Specification $spec
    $lease = Enter-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -AuthorizationSecret $AuthorizationSecret -ExpectedSemanticBinding $SemanticBinding -ConsumerId ([string]$transition.transitionId)
    if ($WhatIfPreference -or -not $PSCmdlet.ShouldProcess($pluginsFile, "$Direction exact plugin batch and verify byte identity")) {
        Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Consumed -Detail 'WhatIf/ShouldProcess declined; authorization consumed without mutation.' | Out-Null
        $planned=[pscustomobject][ordered]@{schemaVersion=1;status='Planned';direction=$Direction;transitionId=[string]$transition.transitionId;state=[string]$transition.state.state;revision=[int]$transition.state.revision}
        if($PassThru){return $planned}else{return($planned|ConvertTo-Json -Depth 12)}
    }
    $original = [IO.File]::ReadAllBytes($pluginsFile); $image = [IO.File]::ReadAllBytes([string]$transition.normalizedInput.imagePath)
    try {
        Test-GridPluginStateBatchHistoryProcessGate -Specification $spec
        if ((Get-GridPluginStateBatchSha256File $pluginsFile) -cne [string]$transition.normalizedInput.fromSha256) { throw 'PluginBatchHistoryTargetDrift: plugins.txt changed immediately before commit.' }
        Test-GridPluginStateBatchHistoryProtectedSources -Specification $spec
        Write-GridPluginStateBatchHistoryBytesAtomic -LiteralPath $pluginsFile -Bytes $image
        if ((Get-GridPluginStateBatchSha256File $pluginsFile) -cne [string]$transition.normalizedInput.toSha256) { throw 'PluginBatchHistoryPostconditionFailed: resulting digest differs.' }
    }
    catch {
        $failure=$_.Exception.Message
        try { Write-GridPluginStateBatchHistoryBytesAtomic -LiteralPath $pluginsFile -Bytes $original; if((Get-GridPluginStateBatchSha256File $pluginsFile)-cne [string]$transition.normalizedInput.fromSha256){throw 'rollback digest mismatch'} }
        catch { try{Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Failed -Detail "Indeterminate: $failure"|Out-Null}catch{}; throw "PluginBatchHistoryIndeterminate: $failure" }
        try{Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Failed -Detail $failure|Out-Null}catch{}
        throw "PluginBatchHistoryRolledBack: $failure"
    }
    $revision=[int]$transition.normalizedInput.revision; $newState=if($Direction-eq'Undo'){'Undone'}else{'Applied'}
    $receiptPath=Join-Path ([string]$transition.transactionDirectory) ("history-receipt-$revision.v1.json")
    if(Test-Path -LiteralPath $receiptPath){throw 'PluginBatchHistoryRevisionCollision: receipt already exists.'}
    $receipt=[pscustomobject][ordered]@{schemaVersion=1;status='Verified';direction=$Direction;transitionId=[string]$transition.transitionId;transitionSha256=[string]$transition.transitionSha256;state=$newState;revision=$revision;pluginsFileSha256=[string]$transition.normalizedInput.toSha256;completedAt=[DateTimeOffset]::UtcNow.ToString('o')}
    Write-GridJsonAtomic -InputObject $receipt -LiteralPath $receiptPath
    Write-GridJsonAtomic -InputObject ([pscustomobject][ordered]@{schemaVersion=1;transactionId=[string]$transition.state.transactionId;specificationSha256=[string]$spec.specificationSha256;state=$newState;revision=$revision;currentPluginsFileSha256=[string]$transition.normalizedInput.toSha256;lastReceiptPath=$receiptPath}) -LiteralPath ([string]$transition.statePath)
    Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Consumed -Detail "$Direction completed and verified." | Out-Null
    if($PassThru){$receipt}else{$receipt|ConvertTo-Json -Depth 20}
}
