#requires -Version 5.1

function Invoke-GridAuthorizedXEditReferenceSuppressionWriterProvisioning {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory,ParameterSetName='Object')]$Proposal,
        [Parameter(Mandatory,ParameterSetName='Path')][string]$ProposalPath,
        [Parameter(Mandatory)][string]$AuthorizationGrantId,
        [Parameter(Mandatory)][string]$AuthorizationSecret,
        [Parameter(Mandatory)][string]$AuthorizationStoreRoot,
        [Parameter(Mandatory)]$SemanticBinding,
        [switch]$PassThru
    )
    $proposalFile=$null
    if ($PSCmdlet.ParameterSetName -eq 'Path') { $proposalFile=[IO.Path]::GetFullPath($ProposalPath);$Proposal=Get-Content -LiteralPath $proposalFile -Raw|ConvertFrom-Json -ErrorAction Stop }
    if ([string]$Proposal.actionType -cne 'XEditReferenceSuppressionWriterProvisioning' -or -not $Proposal.PSObject.Properties['provisioning']) { throw 'UnsupportedAction: proposal is not an xEdit reference-suppression writer provisioning action.' }
    $p=$Proposal.provisioning
    $state=Get-GridXEditReferenceSuppressionWriterProvisioningState -ConfigurationPath ([string]$p.configurationPath) -ExecutableTitle ([string]$p.executableTitle) -GridDataRoot ([string]$p.gridDataRoot)
    if ([string]$state.State -eq 'Unavailable') { throw ('WriterProvisioningUnavailable: '+[string]$state.Detail) }
    if ([string]$state.SourceSha256 -cne [string]$p.sourceSha256 -or [string]$state.ExecutableSha256 -cne [string]$p.executableSha256 -or [string]$state.DestinationPath -ine [string]$p.destinationPath) { throw 'StaleProposal: writer source, executable identity, or destination changed.' }
    if ([string]$state.State -ne 'Ready' -and ([string]$state.State -cne [string]$p.expectedState -or [string]$state.InstalledSha256 -cne [string]$p.installedSha256)) { throw 'StaleProposal: installed writer state changed.' }
    if ([string]$state.State -eq 'ModifiedExternally' -and -not [bool]$p.allowModifiedExternalReplacement) { throw 'WriterModifiedExternally: replacement was not authorized.' }
    $proposalSha=Get-GridRemediationProposalIdentityHash -Proposal $Proposal
    if ([string]$SemanticBinding.proposalOrSpecificationId -cne [string]$Proposal.proposalId -or [string]$SemanticBinding.proposalOrSpecificationSha256 -cne $proposalSha) { throw 'AuthorizationBindingMismatch: writer provisioning proposal identity changed.' }
    if ([string]$SemanticBinding.normalizedInputSha256 -cne (Get-GridCanonicalJsonSha256 -InputObject $p)) { throw 'AuthorizationBindingMismatch: writer provisioning inputs changed.' }
    if (@($SemanticBinding.capabilities|Where-Object{[string]$_.capabilityId -ceq 'grid.game.skyrimspecialedition.xedit-reference-suppression-writer-provisioning.execute' -and [string]$_.capabilityVersion -ceq '1.0.0'}).Count -ne 1) { throw 'AuthorizationBindingMismatch: writer provisioning capability/version is not authorized.' }
    $expectedTargets=@($Proposal.targets|ForEach-Object{[string]$_}|Sort-Object -Unique);$boundTargets=@($SemanticBinding.targets|ForEach-Object{[string]$_}|Sort-Object -Unique)
    if (($expectedTargets -join "`n") -cne ($boundTargets -join "`n")) { throw 'AuthorizationBindingMismatch: exact writer provisioning targets changed.' }
    $lease=Enter-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -AuthorizationSecret $AuthorizationSecret -ExpectedSemanticBinding $SemanticBinding -ConsumerId ('xedit-reference-suppression-writer-provisioning-'+[string]$Proposal.proposalId)
    $authorized=$Proposal|Select-Object *;$authorized.authorization.status='Authorized';$authorized.authorization.authorizedAt=[DateTimeOffset]::UtcNow.ToString('o')
    if (-not $PSCmdlet.ShouldProcess([string]$state.DestinationPath,'Provision the reviewed Grid xEdit reference-suppression writer')) {
        $authorized.status='Incomplete';Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Consumed -Detail 'WhatIf: no external file was written.'|Out-Null
        if($proposalFile){Write-GridJsonAtomic -InputObject $authorized -LiteralPath $proposalFile}
        return [pscustomobject]@{State='Incomplete';Proposal=$authorized;ReceiptPath=$null;BackupPath=$null}
    }
    $authorized.status='Executing';if($proposalFile){Write-GridJsonAtomic -InputObject $authorized -LiteralPath $proposalFile}
    $destination=[string]$state.DestinationPath;$source=[string]$state.SourcePath
    $rollbackDirectory=Join-Path ([string]$state.GridDataRoot) "diagnostics\tool-provisioning\xedit\reference-suppression-writer\rollback\$($authorized.proposalId)"
    $backupPath=$null;$destinationExisted=Test-Path -LiteralPath $destination -PathType Leaf
    try {
        if($destinationExisted -and [string]$state.InstalledSha256 -cne [string]$state.SourceSha256){
            New-Item -ItemType Directory -Path $rollbackDirectory -Force|Out-Null;$backupPath=Join-Path $rollbackDirectory 'Write-GridReferenceSuppression.pas'
            Copy-Item -LiteralPath $destination -Destination $backupPath -Force -ErrorAction Stop
            if((Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash.ToUpperInvariant() -cne [string]$state.InstalledSha256){throw 'Rollback backup verification failed before writer provisioning.'}
        }
        if([string]$state.State -ne 'Ready'){Copy-Item -LiteralPath $source -Destination $destination -Force -ErrorAction Stop}
        $installedHash=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToUpperInvariant()
        if($installedHash -cne [string]$state.SourceSha256){throw 'Installed writer hash does not match the approved bundle.'}
        $receipt=[pscustomobject][ordered]@{schemaVersion=1;receiptId=('receipt-'+[guid]::NewGuid().ToString('N'));writerId=[string]$state.WriterId;writerVersion=[string]$state.WriterVersion;sourceSha256=[string]$state.SourceSha256;installedSha256=$installedHash;executableSha256=[string]$state.ExecutableSha256;destinationPath=$destination;installedAt=[DateTimeOffset]::UtcNow.ToString('o');proposalId=[string]$authorized.proposalId}
        Write-GridJsonAtomic -InputObject $receipt -LiteralPath ([string]$state.ReceiptPath)
        $verified=Get-GridXEditReferenceSuppressionWriterProvisioningState -ConfigurationPath ([string]$p.configurationPath) -ExecutableTitle ([string]$p.executableTitle) -GridDataRoot ([string]$p.gridDataRoot)
        if([string]$verified.State -ne 'Ready' -or [string]$verified.ReceiptStatus -ne 'Valid'){throw 'Post-install writer provisioning state or receipt verification failed.'}
        $authorized.status='Verified';Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Consumed -Detail "Provisioned and verified $destination"|Out-Null
        if($proposalFile){Write-GridJsonAtomic -InputObject $authorized -LiteralPath $proposalFile}
        $result=[pscustomobject]@{State='Verified';Proposal=$authorized;ReceiptPath=[string]$state.ReceiptPath;BackupPath=$backupPath;InstalledSha256=$installedHash}
    } catch {
        $failure=$_.Exception.Message
        try { if($backupPath){Copy-Item -LiteralPath $backupPath -Destination $destination -Force -ErrorAction Stop}elseif(-not$destinationExisted -and(Test-Path -LiteralPath $destination -PathType Leaf)){Remove-Item -LiteralPath $destination -Force -ErrorAction Stop} } catch { $failure+=' Rollback failed: '+$_.Exception.Message }
        $authorized.status='Failed';if($proposalFile){Write-GridJsonAtomic -InputObject $authorized -LiteralPath $proposalFile}
        Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Failed -Detail $failure|Out-Null
        throw "WriterProvisioningFailedRolledBack: $failure"
    }
    if($PassThru){$result}else{$result|ConvertTo-Json -Depth 30}
}
