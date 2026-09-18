#requires -Version 5.1
Set-StrictMode -Version Latest

function Invoke-GridAuthorizedGtaDeployment {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]$Plan,
        [Parameter(Mandatory)][string]$AuthorizationGrantId,
        [Parameter(Mandatory)][string]$AuthorizationSecret,
        [Parameter(Mandatory)][string]$AuthorizationStoreRoot,
        [Parameter(Mandatory)]$SemanticBinding,
        [Parameter(Mandatory)][string]$RollbackRoot
    )
    if ([string]$Plan.status -cne 'ReadyForReview') { throw "GtaDeploymentPlanNotReady: '$($Plan.status)'." }
    $current = New-GridGtaDeploymentPlan -GameRoot ([string]$Plan.gameRoot) -ArtifactRoot @($Plan.artifactRoots)
    if ([string]$current.specificationSha256 -cne [string]$Plan.specificationSha256) { throw 'StaleGtaDeploymentPlan: sources, targets, or existing files changed.' }
    if ([string]$SemanticBinding.proposalOrSpecificationId -cne [string]$Plan.specificationId -or
        [string]$SemanticBinding.proposalOrSpecificationSha256 -cne [string]$Plan.specificationSha256) { throw 'AuthorizationBindingMismatch: deployment specification changed.' }
    if ([string]$SemanticBinding.normalizedInputSha256 -cne (Get-GridCanonicalJsonSha256 -InputObject $Plan.operations)) { throw 'AuthorizationBindingMismatch: deployment operations changed.' }
    if (@($SemanticBinding.capabilities | Where-Object { [string]$_.capabilityId -ceq 'grid.game.grandtheftautov.setup-deployment.execute' -and [string]$_.capabilityVersion -ceq '1.0.0' }).Count -ne 1) { throw 'AuthorizationBindingMismatch: GTA deployment capability/version is not authorized.' }
    $expectedTargets=@($Plan.targets|Sort-Object -Unique);$boundTargets=@($SemanticBinding.targets|ForEach-Object{[string]$_}|Sort-Object -Unique)
    if(($expectedTargets-join "`n")-cne($boundTargets-join "`n")){throw 'AuthorizationBindingMismatch: exact deployment targets changed.'}

    $lease=Enter-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -AuthorizationSecret $AuthorizationSecret -ExpectedSemanticBinding $SemanticBinding -ConsumerId ('gta-deploy-' + [string]$Plan.specificationId)
    if(-not $PSCmdlet.ShouldProcess([string]$Plan.gameRoot,'Apply reviewed GTA V deployment plan')){
        Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Consumed -Detail 'WhatIf: no external state changed.'|Out-Null
        return [pscustomobject]@{status='Incomplete';changedExternalState=$false;specificationId=$Plan.specificationId}
    }
    $rollbackDirectory=Join-Path ([IO.Path]::GetFullPath($RollbackRoot)) ([string]$Plan.specificationId)
    $journal=@();$created=@()
    try{
        New-Item -ItemType Directory -Path $rollbackDirectory -Force|Out-Null
        foreach($operation in @($Plan.operations)){
            $destination=[string]$operation.destinationPath
            if(Test-Path -LiteralPath $destination -PathType Leaf){
                $backup=Join-Path $rollbackDirectory (([guid]::NewGuid().ToString('N'))+'-'+[IO.Path]::GetFileName($destination))
                Copy-Item -LiteralPath $destination -Destination $backup -Force
                $journal += [pscustomobject]@{destinationPath=$destination;backupPath=$backup;created=$false}
            }else{$journal += [pscustomobject]@{destinationPath=$destination;backupPath=$null;created=$true};$created += $destination}
            $parent=Split-Path -Parent $destination;if(-not(Test-Path -LiteralPath $parent -PathType Container)){New-Item -ItemType Directory -Path $parent -Force|Out-Null}
            if([string]$operation.kind -eq 'CopyFile'){
                if((Get-FileHash -LiteralPath ([string]$operation.sourcePath) -Algorithm SHA256).Hash.ToUpperInvariant() -cne [string]$operation.sourceSha256){throw "SourceHashChanged: $($operation.sourcePath)"}
                Copy-Item -LiteralPath ([string]$operation.sourcePath) -Destination $destination -Force
                if((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToUpperInvariant() -cne [string]$operation.sourceSha256){throw "DeploymentVerificationFailed: $destination"}
            }elseif([string]$operation.kind -eq 'SetIniValue'){
                $lines=if(Test-Path -LiteralPath $destination -PathType Leaf){@(Get-Content -LiteralPath $destination)}else{@()}
                $pattern='^\s*'+[regex]::Escape([string]$operation.key)+'\s*='
                $replacement=([string]$operation.key)+'='+([string]$operation.value);$replaced=$false
                $updated=@($lines|ForEach-Object{if($_ -match $pattern){$replaced=$true;$replacement}else{$_}});if(-not $replaced){$updated += $replacement}
                [IO.File]::WriteAllLines($destination,[string[]]$updated,(New-Object Text.UTF8Encoding($false)))
                if(@(Get-Content -LiteralPath $destination|Where-Object{$_ -ceq $replacement}).Count -ne 1){throw "ConfigurationVerificationFailed: $destination"}
            }else{throw "UnsupportedGtaDeploymentOperation: $($operation.kind)"}
        }
        $receipt=[pscustomobject][ordered]@{schemaVersion=1;receiptId=('gta-receipt-'+[guid]::NewGuid().ToString('N'));specificationId=[string]$Plan.specificationId;specificationSha256=[string]$Plan.specificationSha256;status='Verified';targets=@($Plan.targets);rollbackDirectory=$rollbackDirectory;completedAt=(Get-Date).ToUniversalTime().ToString('o')}
        Write-GridJsonAtomic -InputObject $receipt -LiteralPath (Join-Path $rollbackDirectory 'deployment-receipt.v1.json')
        Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Consumed -Detail 'GTA deployment applied and verified.'|Out-Null
        [pscustomobject]@{status='Verified';changedExternalState=$true;specificationId=$Plan.specificationId;receipt=$receipt}
    }catch{
        $failure=$_.Exception.Message
        # Walk the journal by descending index for PowerShell 5.1-compatible
        # last-in-first-out rollback.
        for($journalIndex = @($journal).Count - 1; $journalIndex -ge 0; $journalIndex--){
            $entry = @($journal)[$journalIndex]
            if([bool]$entry.created){if(Test-Path -LiteralPath ([string]$entry.destinationPath)-PathType Leaf){Remove-Item -LiteralPath ([string]$entry.destinationPath)-Force}}
            elseif(Test-Path -LiteralPath ([string]$entry.backupPath)-PathType Leaf){Copy-Item -LiteralPath ([string]$entry.backupPath)-Destination ([string]$entry.destinationPath)-Force}
        }
        try{Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Failed -Detail $failure|Out-Null}catch{}
        throw "GtaDeploymentFailed: $failure Rollback attempted."
    }
}
