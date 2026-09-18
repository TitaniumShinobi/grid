#requires -Version 5.1

function New-GridXEditReferenceSuppressionWriterProvisioningProposal {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][string]$CaseDirectory,
        [Parameter(Mandatory)]$ProvisioningState,
        [switch]$AllowModifiedExternalReplacement
    )
    if ([string]$ProvisioningState.State -eq 'Ready') { throw 'WriterProvisioningNotRequired: the installed writer already matches the bundle.' }
    if ([string]$ProvisioningState.State -eq 'Unavailable') { throw ('WriterProvisioningUnavailable: ' + [string]$ProvisioningState.Detail) }
    if ([string]$ProvisioningState.State -eq 'ModifiedExternally' -and -not $AllowModifiedExternalReplacement) { throw 'WriterModifiedExternally: replacement requires an explicit modified-external override.' }
    if ([string]$ProvisioningState.State -notin @('Missing','Outdated','ModifiedExternally')) { throw "Unsupported writer provisioning state: $($ProvisioningState.State)" }
    $contextFingerprint = 'sha256:' + (Get-GridStablePathId -Path $ProvisioningState.ExecutablePath)
    $evidence = New-GridEvidenceItem -Parameter 'toolProvisioning.referenceSuppressionWriterState' -Value 1 `
        -Claim "Grid observed reference-suppression writer state '$($ProvisioningState.State)' for the configured x64 xEdit installation." `
        -SourceType 'DeterministicScriptOutput' -SourceIdentifier ([string]$ProvisioningState.DestinationPath) `
        -ContextFingerprint $contextFingerprint -VerificationStatus Verified -CollectorName 'Get-GridXEditReferenceSuppressionWriterProvisioningState' -CollectorVersion '1'
    $evidence | Add-Member -NotePropertyName native -NotePropertyValue ([pscustomobject][ordered]@{
        state=[string]$ProvisioningState.State;writerId=[string]$ProvisioningState.WriterId;writerVersion=[string]$ProvisioningState.WriterVersion
        sourceSha256=[string]$ProvisioningState.SourceSha256;installedSha256=[string]$ProvisioningState.InstalledSha256;executableSha256=[string]$ProvisioningState.ExecutableSha256
    })
    $proposal = New-GridRemediationProposal -CaseId $CaseId -ContextFingerprint $contextFingerprint `
        -EvidenceFingerprint (Get-GridEvidenceFingerprint -Evidence @($evidence)) -ActionType 'XEditReferenceSuppressionWriterProvisioning' `
        -Targets @("sourcePath=$($ProvisioningState.SourcePath)","destinationPath=$($ProvisioningState.DestinationPath)","writerVersion=$($ProvisioningState.WriterVersion)","sourceSha256=$($ProvisioningState.SourceSha256)","installedSha256=$($ProvisioningState.InstalledSha256)") `
        -SupportingEvidenceIds @($evidence.evidenceId) -ExpectedEffects @('The configured xEdit installation contains the exact reviewed Grid reference-suppression writer.') `
        -Exclusions @('Do not execute the writer.','Do not modify unrelated xEdit scripts.','Do not modify MO2 profiles, plugins, saves, or game files.') `
        -AuthorizationRequirement 'Explicit one-use authorization to install or update only Write-GridReferenceSuppression.pas.' `
        -VerificationSteps @('Rehash the installed writer.','Validate the receipt against the configured SSEEdit64 identity.') `
        -BackupRequirement 'Preserve any existing same-name script in Grid-managed rollback storage before replacement.' `
        -RollbackProcedure 'Restore the verified backup, or remove a newly created invalid destination, if verification fails.' -Supported
    $proposal | Add-Member -NotePropertyName provisioning -NotePropertyValue ([pscustomobject][ordered]@{
        expectedState=[string]$ProvisioningState.State;configurationPath=[string]$ProvisioningState.ConfigurationPath;executableTitle=[string]$ProvisioningState.ExecutableTitle
        sourcePath=[string]$ProvisioningState.SourcePath;manifestPath=[string]$ProvisioningState.ManifestPath;sourceSha256=[string]$ProvisioningState.SourceSha256
        destinationPath=[string]$ProvisioningState.DestinationPath;installedSha256=[string]$ProvisioningState.InstalledSha256;writerVersion=[string]$ProvisioningState.WriterVersion
        executableSha256=[string]$ProvisioningState.ExecutableSha256;gridDataRoot=[string]$ProvisioningState.GridDataRoot;allowModifiedExternalReplacement=[bool]$AllowModifiedExternalReplacement
    })
    $proposalDirectory = Join-Path $CaseDirectory 'tool-provisioning'
    New-Item -ItemType Directory -Path $proposalDirectory -Force | Out-Null
    $proposalPath = Join-Path $proposalDirectory 'xedit-reference-suppression-writer-proposal.json'
    $evidencePath = Join-Path $proposalDirectory 'xedit-reference-suppression-writer-state-evidence.json'
    Write-GridJsonAtomic -InputObject $proposal -LiteralPath $proposalPath
    Write-GridJsonAtomic -InputObject $evidence -LiteralPath $evidencePath
    [pscustomobject]@{Proposal=$proposal;Evidence=$evidence;ProposalPath=$proposalPath;EvidencePath=$evidencePath;AuthorizationRequired=$true}
}
