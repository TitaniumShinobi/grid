#requires -Version 5.1

function Copy-GridProvisioningFile {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Destination)
    Copy-Item -LiteralPath $Source -Destination $Destination -Force -ErrorAction Stop
}

function Invoke-GridAuthorizedXEditCollectorProvisioning {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory, ParameterSetName='Object')]$Proposal,
        [Parameter(Mandatory, ParameterSetName='Path')][string]$ProposalPath,
        [Parameter(Mandatory)][string]$AuthorizationGrantId,
        [Parameter(Mandatory)][string]$AuthorizationSecret,
        [Parameter(Mandatory)][string]$AuthorizationStoreRoot,
        [Parameter(Mandatory)]$SemanticBinding,
        [switch]$PassThru
    )
    $proposalFile = $null
    if ($PSCmdlet.ParameterSetName -eq 'Path') {
        $proposalFile = [IO.Path]::GetFullPath($ProposalPath)
        $Proposal = Get-Content -LiteralPath $proposalFile -Raw | ConvertFrom-Json
    }
    if ([string]$Proposal.actionType -ne 'XEditCollectorProvisioning' -or -not $Proposal.PSObject.Properties['provisioning']) { throw 'UnsupportedAction: proposal is not an XEditCollectorProvisioning action.' }
    $p = $Proposal.provisioning
    $state = Get-GridXEditCollectorProvisioningState -ConfigurationPath ([string]$p.configurationPath) -ExecutableTitle ([string]$p.executableTitle) `
        -GridDataRoot ([string]$p.gridDataRoot)
    if ($state.State -eq 'Unavailable') { throw ('CollectorProvisioningUnavailable: ' + $state.Detail) }
    if ([string]$state.SourceSha256 -cne [string]$p.sourceSha256 -or [string]$state.ExecutableSha256 -cne [string]$p.executableSha256 -or [string]$state.DestinationPath -ine [string]$p.destinationPath) {
        throw 'StaleProposal: collector source, executable identity, or destination changed after proposal creation.'
    }
    $expectedInstalled = [string]$p.installedSha256
    if ($state.State -ne 'Ready' -and ([string]$state.State -ne [string]$p.expectedState -or [string]$state.InstalledSha256 -cne $expectedInstalled)) {
        throw 'StaleProposal: installed collector state changed after proposal creation.'
    }
    if ($state.State -eq 'ModifiedExternally' -and -not [bool]$p.allowModifiedExternalReplacement) { throw 'CollectorModifiedExternally: the proposal does not authorize replacement of externally modified content.' }
    $proposalSha = Get-GridRemediationProposalIdentityHash -Proposal $Proposal
    if ([string]$SemanticBinding.proposalOrSpecificationId -cne [string]$Proposal.proposalId -or [string]$SemanticBinding.proposalOrSpecificationSha256 -cne $proposalSha) { throw 'AuthorizationBindingMismatch: provisioning proposal identity changed.' }
    if ([string]$SemanticBinding.normalizedInputSha256 -cne (Get-GridCanonicalJsonSha256 -InputObject $p)) { throw 'AuthorizationBindingMismatch: provisioning inputs changed.' }
    if (@($SemanticBinding.capabilities | Where-Object { [string]$_.capabilityId -ceq 'grid.game.skyrimspecialedition.xedit-provisioning.execute' -and [string]$_.capabilityVersion -ceq '2.0.0' }).Count -ne 1) { throw 'AuthorizationBindingMismatch: provisioning capability/version is not authorized.' }
    $expectedTargets=@($Proposal.targets | ForEach-Object { [string]$_ } | Sort-Object -Unique); $boundTargets=@($SemanticBinding.targets | ForEach-Object { [string]$_ } | Sort-Object -Unique)
    if (($expectedTargets -join "`n") -cne ($boundTargets -join "`n")) { throw 'AuthorizationBindingMismatch: exact provisioning targets changed.' }
    $lease = Enter-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -AuthorizationSecret $AuthorizationSecret -ExpectedSemanticBinding $SemanticBinding -ConsumerId ('xedit-provisioning-' + [string]$Proposal.proposalId)
    $authorized = $Proposal | Select-Object *
    $authorized.authorization.status='Authorized'; $authorized.authorization.authorizedAt=[DateTimeOffset]::UtcNow.ToString('o')
    if (-not $PSCmdlet.ShouldProcess([string]$state.DestinationPath, 'Provision the versioned Grid xEdit collector')) {
        $authorized.status = 'Incomplete'
        Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Consumed -Detail 'WhatIf: no external file was written.' | Out-Null
        if ($proposalFile) { Write-GridJsonAtomic -InputObject $authorized -LiteralPath $proposalFile }
        return [pscustomobject]@{ State='Incomplete'; Proposal=$authorized; Verification=(New-GridVerificationResult -ProposalId $authorized.proposalId -Status Incomplete -EvidenceIds @($authorized.supportingEvidenceIds) -Detail 'WhatIf: no external file was written.'); ReceiptPath=$null; BackupPath=$null }
    }
    $authorized.status = 'Executing'
    if ($proposalFile) { Write-GridJsonAtomic -InputObject $authorized -LiteralPath $proposalFile }
    $destination = [string]$state.DestinationPath
    $source = [string]$state.SourcePath
    $rollbackDirectory = Join-Path ([string]$state.GridDataRoot) ("diagnostics\tool-provisioning\xedit\rollback\$($authorized.proposalId)")
    $backupPath = $null
    $destinationExisted = Test-Path -LiteralPath $destination -PathType Leaf
    try {
        if ($destinationExisted -and [string]$state.InstalledSha256 -cne [string]$state.SourceSha256) {
            New-Item -ItemType Directory -Path $rollbackDirectory -Force | Out-Null
            $backupPath = Join-Path $rollbackDirectory 'Trace-GridReference.pas'
            Copy-Item -LiteralPath $destination -Destination $backupPath -Force -ErrorAction Stop
            if ((Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash.ToUpperInvariant() -cne [string]$state.InstalledSha256) { throw 'Rollback backup verification failed before provisioning.' }
        }
        if ($state.State -ne 'Ready') { Copy-GridProvisioningFile -Source $source -Destination $destination }
        $installedHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($installedHash -cne [string]$state.SourceSha256) { throw 'Installed collector hash does not match the approved bundled collector.' }
        $receipt = [pscustomobject][ordered]@{
            schemaVersion=1; receiptId=('receipt-' + [guid]::NewGuid().ToString('N')); collectorId=[string]$state.CollectorId
            collectorVersion=[string]$state.CollectorVersion; sourceSha256=[string]$state.SourceSha256; installedSha256=$installedHash
            executableSha256=[string]$state.ExecutableSha256; destinationPath=$destination
            installedAt=(Get-Date).ToUniversalTime().ToString('o'); proposalId=[string]$authorized.proposalId
        }
        Write-GridJsonAtomic -InputObject $receipt -LiteralPath ([string]$state.ReceiptPath)
        $verifiedState = Get-GridXEditCollectorProvisioningState -ConfigurationPath ([string]$p.configurationPath) -ExecutableTitle ([string]$p.executableTitle) `
            -GridDataRoot ([string]$p.gridDataRoot)
        if ($verifiedState.State -ne 'Ready' -or $verifiedState.ReceiptStatus -ne 'Valid') { throw 'Post-install provisioning state or receipt verification failed.' }
        $authorized.status = 'Verified'
        Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Consumed -Detail 'Collector provisioned and verified.' | Out-Null
        $verification = New-GridVerificationResult -ProposalId $authorized.proposalId -Status Verified -EvidenceIds @($authorized.supportingEvidenceIds) -Detail 'Installed collector and receipt hashes match the approved bundle and configured SSEEdit64 identity.'
        if ($proposalFile) { Write-GridJsonAtomic -InputObject $authorized -LiteralPath $proposalFile }
        $result = [pscustomobject]@{ State='Ready'; Proposal=$authorized; Verification=$verification; ReceiptPath=$state.ReceiptPath; BackupPath=$backupPath; InstalledSha256=$installedHash; DestinationPath=$destination }
        if ($PassThru) { return $result }
        return $result
    }
    catch {
        $failure = $_.Exception.Message
        $rolledBack = $false
        try {
            if ($backupPath -and (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
                Copy-Item -LiteralPath $backupPath -Destination $destination -Force -ErrorAction Stop
                $rolledBack = ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToUpperInvariant() -ceq $expectedInstalled)
            }
            elseif (-not $destinationExisted -and (Test-Path -LiteralPath $destination -PathType Leaf)) {
                Remove-Item -LiteralPath $destination -Force -ErrorAction Stop
                $rolledBack = -not (Test-Path -LiteralPath $destination)
            }
        } catch { $failure += ' Rollback failed: ' + $_.Exception.Message }
        $authorized.status = if ($rolledBack) { 'RolledBack' } else { 'Failed' }
        try { Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Failed -Detail $failure | Out-Null } catch {}
        if ($proposalFile) { Write-GridJsonAtomic -InputObject $authorized -LiteralPath $proposalFile }
        $verification = New-GridVerificationResult -ProposalId $authorized.proposalId -Status $(if($rolledBack){'RolledBack'}else{'Failed'}) -EvidenceIds @($authorized.supportingEvidenceIds) -Detail $failure
        throw "CollectorProvisioningFailed: $failure Status=$($authorized.status)."
    }
}
