<# .SYNOPSIS Defines evidence-bound remediation proposals and verification records. #>
$script:GridProposalSchemaVersion = 1

function New-GridRemediationProposal {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][string]$ContextFingerprint,
        [Parameter(Mandatory)][string]$EvidenceFingerprint,
        [Parameter(Mandatory)][string]$ActionType,
        [Parameter(Mandatory)][string[]]$Targets,
        [Parameter(Mandatory)][string[]]$SupportingEvidenceIds,
        [string[]]$ContradictingEvidenceIds = @(),
        [Parameter(Mandatory)][string[]]$ExpectedEffects,
        [string[]]$Exclusions = @(),
        [Parameter(Mandatory)][string]$AuthorizationRequirement,
        [Parameter(Mandatory)][string[]]$VerificationSteps,
        [Parameter(Mandatory)][string]$BackupRequirement,
        [Parameter(Mandatory)][string]$RollbackProcedure,
        [switch]$Supported
    )
    if (@($SupportingEvidenceIds).Count -eq 0) { throw 'A remediation proposal requires verified supporting evidence.' }
    [pscustomobject][ordered]@{
        schemaVersion = $script:GridProposalSchemaVersion
        proposalId = 'proposal-' + [guid]::NewGuid().ToString('N')
        caseId = $CaseId
        contextFingerprint = $ContextFingerprint
        evidenceFingerprint = $EvidenceFingerprint
        actionType = $ActionType
        targets = @($Targets)
        supportingEvidenceIds = @($SupportingEvidenceIds)
        contradictingEvidenceIds = @($ContradictingEvidenceIds)
        expectedEffects = @($ExpectedEffects)
        exclusions = @($Exclusions)
        authorization = [pscustomobject][ordered]@{ requirement = $AuthorizationRequirement; status = 'Required'; authorizedAt = $null }
        verificationSteps = @($VerificationSteps)
        backupRequirement = $BackupRequirement
        rollbackProcedure = $RollbackProcedure
        status = if ($Supported) { 'AwaitingAuthorization' } else { 'Unsupported' }
        stale = $false
        createdAt = (Get-Date).ToUniversalTime().ToString('o')
    }
}

function Test-GridRemediationProposal {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Proposal, [string]$CurrentContextFingerprint, [string]$CurrentEvidenceFingerprint)
    $errors = New-Object Collections.Generic.List[string]
    foreach ($field in @('schemaVersion','proposalId','caseId','contextFingerprint','evidenceFingerprint','actionType','targets','supportingEvidenceIds','authorization','verificationSteps','backupRequirement','rollbackProcedure','status')) {
        if ($null -eq $Proposal.PSObject.Properties[$field]) { $errors.Add("Missing proposal field: $field") }
    }
    if ($errors.Count -eq 0 -and @($Proposal.supportingEvidenceIds).Count -eq 0) { $errors.Add('Proposal has no supporting evidence.') }
    $stale = ($CurrentContextFingerprint -and $Proposal.contextFingerprint -ne $CurrentContextFingerprint) -or ($CurrentEvidenceFingerprint -and $Proposal.evidenceFingerprint -ne $CurrentEvidenceFingerprint)
    [pscustomobject]@{ IsValid = ($errors.Count -eq 0); IsStale = [bool]$stale; Errors = @($errors) }
}

function Get-GridRemediationProposalIdentityHash {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Proposal)
    $stable = [pscustomobject][ordered]@{
        schemaVersion=[int]$Proposal.schemaVersion; proposalId=[string]$Proposal.proposalId; caseId=[string]$Proposal.caseId
        contextFingerprint=[string]$Proposal.contextFingerprint; evidenceFingerprint=[string]$Proposal.evidenceFingerprint; actionType=[string]$Proposal.actionType
        targets=@($Proposal.targets); supportingEvidenceIds=@($Proposal.supportingEvidenceIds); contradictingEvidenceIds=@($Proposal.contradictingEvidenceIds)
        expectedEffects=@($Proposal.expectedEffects); exclusions=@($Proposal.exclusions); verificationSteps=@($Proposal.verificationSteps)
        backupRequirement=[string]$Proposal.backupRequirement; rollbackProcedure=[string]$Proposal.rollbackProcedure
    }
    Get-GridCanonicalJsonSha256 -InputObject $stable
}

function Grant-GridProposalAuthorization {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Proposal,
        [Parameter(Mandatory)][string]$CurrentContextFingerprint,
        [Parameter(Mandatory)][string]$CurrentEvidenceFingerprint,
        [Parameter(Mandatory)][string]$AuthorizationStoreRoot,
        [Parameter(Mandatory)]$SemanticBinding,
        [ValidateRange(1,1440)][int]$LifetimeMinutes = 15
    )
    $validation = Test-GridRemediationProposal -Proposal $Proposal -CurrentContextFingerprint $CurrentContextFingerprint -CurrentEvidenceFingerprint $CurrentEvidenceFingerprint
    if (-not $validation.IsValid) { throw ($validation.Errors -join ' ') }
    if ($validation.IsStale) { throw 'StaleProposal: context or evidence changed after the proposal was created.' }
    if ([string]$Proposal.status -notin @('AwaitingAuthorization','Authorized')) { throw "Proposal status '$($Proposal.status)' cannot issue a new authorization grant." }
    if ([string]$SemanticBinding.proposalOrSpecificationId -cne [string]$Proposal.proposalId) { throw 'AuthorizationBindingMismatch: proposal identity differs from the semantic binding.' }
    $proposalSha = Get-GridRemediationProposalIdentityHash -Proposal $Proposal
    if ([string]$SemanticBinding.proposalOrSpecificationSha256 -cne $proposalSha) { throw 'AuthorizationBindingMismatch: proposal digest differs from the semantic binding.' }
    $expectedTargets = @($Proposal.targets | ForEach-Object { [string]$_ } | Sort-Object)
    $actualTargets = @($SemanticBinding.targets | ForEach-Object { [string]$_ } | Sort-Object)
    if (($expectedTargets -join "`n") -cne ($actualTargets -join "`n")) { throw 'AuthorizationBindingMismatch: exact proposal targets differ from the semantic binding.' }
    $issued = New-GridAuthorizationGrant -StoreRoot $AuthorizationStoreRoot -ReviewId ('proposal-review-' + [string]$Proposal.proposalId) -AuthorityClass Mutation -SemanticBinding $SemanticBinding -LifetimeMinutes $LifetimeMinutes
    $issued
}

function New-GridVerificationResult {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProposalId, [Parameter(Mandatory)][ValidateSet('Verified','Failed','RolledBack','Incomplete')][string]$Status, [Parameter(Mandatory)][string[]]$EvidenceIds, [string]$Detail = '')
    [pscustomobject][ordered]@{
        schemaVersion = 1; verificationId = 'verification-' + [guid]::NewGuid().ToString('N'); proposalId = $ProposalId
        status = $Status; evidenceIds = @($EvidenceIds); detail = $Detail; recordedAt = (Get-Date).ToUniversalTime().ToString('o')
    }
}

