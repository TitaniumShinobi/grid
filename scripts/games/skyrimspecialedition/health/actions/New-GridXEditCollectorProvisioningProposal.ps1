#requires -Version 5.1

function New-GridXEditCollectorProvisioningProposal {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][string]$CaseDirectory,
        [Parameter(Mandatory)]$ProvisioningState,
        [switch]$AllowModifiedExternalReplacement
    )
    if ([string]$ProvisioningState.State -eq 'Ready') { throw 'CollectorProvisioningNotRequired: the installed collector already matches the bundle.' }
    if ([string]$ProvisioningState.State -eq 'Unavailable') { throw ('CollectorProvisioningUnavailable: ' + [string]$ProvisioningState.Detail) }
    if ([string]$ProvisioningState.State -eq 'ModifiedExternally' -and -not $AllowModifiedExternalReplacement) { throw 'CollectorModifiedExternally: replacement requires an explicit modified-external override before a proposal can be created.' }
    if ([string]$ProvisioningState.State -notin @('Missing','Outdated','ModifiedExternally')) { throw "Unsupported collector provisioning state: $($ProvisioningState.State)" }
    $contextFingerprint = 'sha256:' + (Get-GridStablePathId -Path $ProvisioningState.ExecutablePath)
    $claim = "Grid observed collector state '$($ProvisioningState.State)' for the configured x64 xEdit installation."
    $evidence = New-GridEvidenceItem -Parameter 'toolProvisioning.collectorState' -Value 1 -Claim $claim -SourceType 'DeterministicScriptOutput' `
        -SourceIdentifier ([string]$ProvisioningState.DestinationPath) -ContextFingerprint $contextFingerprint -VerificationStatus Verified `
        -CollectorName 'Get-GridXEditCollectorProvisioningState' -CollectorVersion '1'
    $evidence | Add-Member -NotePropertyName native -NotePropertyValue ([pscustomobject][ordered]@{
        state = [string]$ProvisioningState.State; collectorId = [string]$ProvisioningState.CollectorId; collectorVersion = [string]$ProvisioningState.CollectorVersion
        sourceSha256 = [string]$ProvisioningState.SourceSha256; installedSha256 = [string]$ProvisioningState.InstalledSha256
        executableSha256 = [string]$ProvisioningState.ExecutableSha256
    })
    $evidenceFingerprint = Get-GridEvidenceFingerprint -Evidence @($evidence)
    $proposal = New-GridRemediationProposal -CaseId $CaseId -ContextFingerprint $contextFingerprint -EvidenceFingerprint $evidenceFingerprint `
        -ActionType 'XEditCollectorProvisioning' -Targets @("sourcePath=$($ProvisioningState.SourcePath)", "destinationPath=$($ProvisioningState.DestinationPath)", "collectorVersion=$($ProvisioningState.CollectorVersion)", "sourceSha256=$($ProvisioningState.SourceSha256)", "installedSha256=$($ProvisioningState.InstalledSha256)") `
        -SupportingEvidenceIds @($evidence.evidenceId) -ExpectedEffects @('The configured xEdit installation contains the exact versioned Grid generic collector.') `
        -Exclusions @('Do not modify unrelated xEdit scripts.','Do not modify MO2 profiles, plugins, saves, or game files.') `
        -AuthorizationRequirement 'Explicit one-use authorization to install or update only Trace-GridReference.pas.' `
        -VerificationSteps @('Rehash the installed collector.','Validate the receipt against the configured SSEEdit64 identity.') `
        -BackupRequirement 'Preserve an existing same-name collector beneath Grid-managed rollback storage before replacement.' `
        -RollbackProcedure 'Restore the Grid-managed backup, or remove a newly created invalid destination, if verification fails.' -Supported
    $proposal | Add-Member -NotePropertyName provisioning -NotePropertyValue ([pscustomobject][ordered]@{
        expectedState = [string]$ProvisioningState.State; configurationPath = [string]$ProvisioningState.ConfigurationPath
        executableTitle = [string]$ProvisioningState.ExecutableTitle; sourcePath = [string]$ProvisioningState.SourcePath
        manifestPath = [string]$ProvisioningState.ManifestPath
        sourceSha256 = [string]$ProvisioningState.SourceSha256; destinationPath = [string]$ProvisioningState.DestinationPath
        installedSha256 = [string]$ProvisioningState.InstalledSha256; collectorVersion = [string]$ProvisioningState.CollectorVersion
        executableSha256 = [string]$ProvisioningState.ExecutableSha256; gridDataRoot = [string]$ProvisioningState.GridDataRoot
        allowModifiedExternalReplacement = [bool]$AllowModifiedExternalReplacement
    })
    $proposalDirectory = Join-Path $CaseDirectory 'tool-provisioning'
    New-Item -ItemType Directory -Path $proposalDirectory -Force | Out-Null
    $proposalPath = Join-Path $proposalDirectory 'xedit-collector-proposal.json'
    $evidencePath = Join-Path $proposalDirectory 'xedit-collector-state-evidence.json'
    Write-GridJsonAtomic -InputObject $proposal -LiteralPath $proposalPath
    Write-GridJsonAtomic -InputObject $evidence -LiteralPath $evidencePath
    [pscustomobject]@{ Proposal = $proposal; Evidence = $evidence; ProposalPath = $proposalPath; EvidencePath = $evidencePath; AuthorizationRequired = $true }
}
