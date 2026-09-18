#requires -Version 5.1

<#
.SYNOPSIS
Adds the TES4 ESL header flag to one evidence-proven plugin under one-use authorization.
#>
function Test-GridEslFlagProcessesClosed {
    param([Parameter(Mandatory)]$Specification)
    $mo2Root=[IO.Path]::GetFullPath([string]$Specification.paths.mo2Root).TrimEnd('\','/')
    $gameRoot=[IO.Path]::GetFullPath((Split-Path -Parent ([string]$Specification.paths.gameDataRoot))).TrimEnd('\','/')
    $running=@(Get-Process -ErrorAction SilentlyContinue|Where-Object{
        $name=[string]$_.ProcessName
        if($name -in @('SSEEdit','SSEEdit64','xEdit')){return $true}
        $path='';try{$path=[string]$_.Path}catch{}
        if([string]::IsNullOrWhiteSpace($path)){return $false}
        $full=[IO.Path]::GetFullPath($path)
        if($name-eq'ModOrganizer'){return $full.StartsWith($mo2Root+'\',[StringComparison]::OrdinalIgnoreCase)}
        if($name-in@('SkyrimSE','skse64_loader','SkyrimSELauncher')){return $full.StartsWith($gameRoot+'\',[StringComparison]::OrdinalIgnoreCase)}
        $false
    })
    if($running.Count-gt 0){throw 'EslFlagProcessesOpen: close the connected MO2, Skyrim, and xEdit processes before applying the ESL flag.'}
}

function Invoke-GridAuthorizedEslFlag {
    [CmdletBinding(SupportsShouldProcess,ConfirmImpact='High')]
    param(
        [Parameter(Mandatory)][string]$SpecificationPath,
        [Parameter(Mandatory)][string]$CurrentContextFingerprint,
        [Parameter(Mandatory)][string]$AuthorizationGrantId,
        [Parameter(Mandatory)][string]$AuthorizationSecret,
        [Parameter(Mandatory)][string]$AuthorizationStoreRoot,
        [Parameter(Mandatory)][string]$CaseStoreRoot,
        [Parameter(Mandatory)]$SemanticBinding,
        [Parameter(Mandatory)][string]$TransactionRoot,
        [switch]$PassThru
    )
    $gameRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    . (Join-Path $gameRoot 'mo2\Grid.PluginStateBatch.ps1')
    . (Join-Path $gameRoot 'mo2\Grid.EslFlag.ps1')
    $path=[IO.Path]::GetFullPath($SpecificationPath)
    if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw "EslFlagSpecificationMissing: $path"}
    $spec=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json -ErrorAction Stop
    if([int]$spec.schemaVersion-ne 1-or[string]$spec.status-cne'AwaitingAuthorization'){throw 'EslFlagSpecificationInvalid: unsupported state or schema.'}
    $sha=Get-GridEslFlagSpecificationHash -Specification $spec
    if([string]$spec.specificationSha256-cne$sha){throw 'EslFlagSpecificationDigestMismatch: specification changed after review.'}
    if([string]$spec.contextFingerprint-cne$CurrentContextFingerprint){throw 'EslFlagStaleProposal: current context fingerprint differs.'}
    $seal=Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory ([string]$spec.evidenceCase.directory)
    if(-not$seal.IsValid-or[string]$seal.Manifest.manifestSha256-cne[string]$spec.evidenceCase.manifestSha256){throw 'EslFlagEvidenceCaseInvalid: sealed evidence changed or is unavailable.'}
    $evidencePath=[string]$spec.eligibilityEvidence.path
    if(-not(Test-Path -LiteralPath $evidencePath -PathType Leaf)-or(Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash.ToUpperInvariant()-cne[string]$spec.eligibilityEvidence.sha256){throw 'EslFlagEligibilityChanged: bound evidence is absent or changed.'}
    $target="plugin:$($spec.plugin.name):TES4.ESL=true"
    if([string]$SemanticBinding.proposalOrSpecificationId-cne[string]$spec.specificationId-or[string]$SemanticBinding.proposalOrSpecificationSha256-cne$sha){throw 'AuthorizationBindingMismatch: ESL flag specification identity changed.'}
    if(@($SemanticBinding.capabilities|Where-Object{[string]$_.capabilityId-ceq'grid.game.skyrimspecialedition.esl-flag.execute'-and[string]$_.capabilityVersion-ceq'1.0.0'}).Count-ne1){throw 'AuthorizationBindingMismatch: ESL flag capability/version is not authorized.'}
    if((@($SemanticBinding.targets|Sort-Object -Unique)-join"`n")-cne$target){throw 'AuthorizationBindingMismatch: exact ESL flag target changed.'}
    if([string]$SemanticBinding.normalizedInputSha256-cne(Get-GridCanonicalJsonSha256 -InputObject $spec)){throw 'AuthorizationBindingMismatch: normalized specification changed.'}
    $preflight=Test-GridAuthorizationGrantPreflight -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -AuthorizationSecret $AuthorizationSecret -ExpectedSemanticBinding $SemanticBinding
    if([string]$preflight.state-cne'Issued'){throw 'AuthorizationReplayRefused: only a newly issued grant can execute this mutation.'}
    $fresh=Get-GridEslFlagInspection -Mo2Root ([string]$spec.paths.mo2Root) -Profile ([string]$spec.paths.profile) -ModsRoot ([string]$spec.paths.modsRoot) -GameDataRoot ([string]$spec.paths.gameDataRoot) -PluginName ([string]$spec.plugin.name)
    [void](Test-GridEslFlagInspectionEquivalent -Expected $spec -Actual $fresh)
    Test-GridEslFlagProcessesClosed -Specification $spec
    $lease=Enter-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -AuthorizationSecret $AuthorizationSecret -ExpectedSemanticBinding $SemanticBinding -ConsumerId ([string]$spec.specificationId)
    try{
        if($WhatIfPreference-or-not$PSCmdlet.ShouldProcess([string]$spec.plugin.path,'Set only the TES4 ESL header flag and verify the exact after image')){
            $result=[pscustomobject][ordered]@{schemaVersion=1;status='Planned';specificationId=[string]$spec.specificationId;pluginName=[string]$spec.plugin.name;changedExternalState=$false;backupPath=$null;beforeSha256=[string]$spec.plugin.beforeSha256;afterSha256=$null;rollbackState='NotRequired'}
            Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Consumed -Detail 'WhatIf validated the exact mutation and consumed authorization without changing the plugin.'|Out-Null
            if($PassThru){return$result}else{return($result|ConvertTo-Json -Depth 20)}
        }
        Test-GridEslFlagProcessesClosed -Specification $spec
        $transaction=[IO.Path]::GetFullPath((Join-Path $TransactionRoot ('esl-flag-'+[string]$spec.specificationSha256)))
        if(Test-Path -LiteralPath $transaction){throw 'EslFlagTransactionCollision: the transaction directory already exists.'}
        New-Item -ItemType Directory -Path $transaction -ErrorAction Stop|Out-Null
        $backup=Join-Path $transaction 'plugin.before.bin'
        $afterImage=Join-Path $transaction 'plugin.after.bin'
        $receiptPath=Join-Path $transaction 'receipt.v1.json'
        $historyStatePath=Join-Path $transaction 'history-state.v1.json'
        [IO.File]::Copy([string]$spec.plugin.path,$backup,$false)
        if((Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash.ToUpperInvariant()-cne[string]$spec.plugin.beforeSha256){throw 'EslFlagBackupVerificationFailed: byte-exact backup could not be established.'}
        $before=[IO.File]::ReadAllBytes([string]$spec.plugin.path)
        $after=Get-GridEslFlagAfterBytes -BeforeBytes $before
        try{
            $stream=[IO.File]::Open([string]$spec.plugin.path,'Open','Write','None')
            try{$stream.Position=8;$bytes=[BitConverter]::GetBytes([uint32]$spec.plugin.expectedAfterFlags);$stream.Write($bytes,0,4);$stream.Flush($true)}finally{$stream.Dispose()}
            $actual=(Get-FileHash -LiteralPath ([string]$spec.plugin.path) -Algorithm SHA256).Hash.ToUpperInvariant()
            $header=Get-GridPluginStateBatchTes4Header -PluginPath ([string]$spec.plugin.path)
            if($actual-cne[string]$spec.plugin.expectedAfterSha256-or-not$header.hasLightFlag-or[long]$header.sizeBytes-ne[long]$spec.plugin.sizeBytes){throw 'EslFlagPostconditionFailed: exact after image or TES4 flag verification failed.'}
            [IO.File]::WriteAllBytes($afterImage,$after)
            if((Get-FileHash -LiteralPath $afterImage -Algorithm SHA256).Hash.ToUpperInvariant()-cne$actual){throw 'EslFlagHistoryImageVerificationFailed: exact after image could not be preserved.'}
            $result=[pscustomobject][ordered]@{schemaVersion=1;status='Verified';specificationId=[string]$spec.specificationId;pluginName=[string]$spec.plugin.name;changedExternalState=$true;backupPath=$backup;beforeSha256=[string]$spec.plugin.beforeSha256;afterSha256=$actual;rollbackState='Available'}
            Write-GridJsonAtomic -InputObject $result -LiteralPath $receiptPath
            Write-GridJsonAtomic -InputObject ([pscustomobject][ordered]@{schemaVersion=1;transactionId=('esl-flag-'+[string]$spec.specificationSha256);specificationSha256=[string]$spec.specificationSha256;state='Applied';revision=0;currentPluginSha256=$actual;lastReceiptPath=$receiptPath}) -LiteralPath $historyStatePath
            Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Consumed -Detail 'The TES4 ESL flag was applied and the exact after image was verified.'|Out-Null
        }catch{
            [IO.File]::Copy($backup,[string]$spec.plugin.path,$true)
            if((Get-FileHash -LiteralPath ([string]$spec.plugin.path) -Algorithm SHA256).Hash.ToUpperInvariant()-cne[string]$spec.plugin.beforeSha256){throw "EslFlagRollbackFailed: $($_.Exception.Message)"}
            foreach($partialPath in @($receiptPath,$historyStatePath,$afterImage)){if(Test-Path -LiteralPath $partialPath -PathType Leaf){Remove-Item -LiteralPath $partialPath -Force -ErrorAction SilentlyContinue}}
            Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Failed -Detail $_.Exception.Message|Out-Null
            throw "EslFlagMutationFailedRolledBack: $($_.Exception.Message)"
        }
    }catch{
        try{if((Read-GridAuthorizationGrant -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId).state-eq'Executing'){Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Failed -Detail $_.Exception.Message|Out-Null}}catch{}
        throw
    }
    if($PassThru){$result}else{$result|ConvertTo-Json -Depth 20}
}
