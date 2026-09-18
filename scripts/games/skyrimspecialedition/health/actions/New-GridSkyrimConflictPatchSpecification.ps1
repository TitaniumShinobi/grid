function New-GridSkyrimConflictPatchSpecification {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][string]$ContextFingerprint,
        [Parameter(Mandatory)][string]$EvidenceFingerprint,
        [Parameter(Mandatory)][object[]]$Evidence,
        [Parameter(Mandatory)][object[]]$RecordGraph
    )
    $proven = @($RecordGraph | Where-Object { [string]$_.status -eq 'RootCauseProven' })
    if ($proven.Count -eq 0) { throw 'RootCauseNotProven: no record graph node proved a causal chain.' }
    $targetNames = @($proven | ForEach-Object { [string]$_.patchTargetPlugin } | Where-Object { $_ } | Sort-Object -Unique)
    if ($targetNames.Count -gt 1 -or ($targetNames.Count -eq 1 -and $targetNames[0] -notmatch '^[^\\/:*?"<>|]{1,240}\.(esp|esm|esl)$')) { throw 'PatchTargetAmbiguous: at most one valid inert target plugin is permitted.' }
    $records = @()
    $assetActions = @()
    foreach ($node in @($proven | Sort-Object recordId)) {
        $changes = @(if ($node.PSObject.Properties['requiredChanges']) { $node.requiredChanges })
        foreach ($change in $changes) {
            foreach ($name in @('fieldPath','sourcePlugin')) { if ([string]::IsNullOrWhiteSpace([string]$change.$name)) { throw "PatchChangeMalformed: $name" } }
        }
        if ($changes.Count -gt 0) { $records += [pscustomobject][ordered]@{ recordId = [string]$node.recordId; changes = @($changes | Sort-Object fieldPath, sourcePlugin) } }
        $nodeAssetActions = @(if ($node.PSObject.Properties['requiredAssetActions']) { $node.requiredAssetActions })
        foreach ($assetAction in $nodeAssetActions) {
            foreach ($name in @('virtualPath','action','providerName','expectedCurrentState','postcondition')) {
                if ([string]::IsNullOrWhiteSpace([string]$assetAction.$name)) { throw "AssetActionMalformed: $name" }
            }
            if ([IO.Path]::IsPathRooted([string]$assetAction.virtualPath) -or [string]$assetAction.virtualPath -match '(^|[\\/])\.\.([\\/]|$)') { throw 'AssetActionMalformed: virtualPath must be relative and traversal-free.' }
            $assetActions += [pscustomobject][ordered]@{
                virtualPath = ([string]$assetAction.virtualPath).Replace('/','\')
                action = [string]$assetAction.action; providerName = [string]$assetAction.providerName
                expectedCurrentState = [string]$assetAction.expectedCurrentState; postcondition = [string]$assetAction.postcondition
            }
        }
    }
    $assetActions = @($assetActions | Sort-Object virtualPath, providerName -Unique)
    if ($records.Count -eq 0 -and $assetActions.Count -eq 0) { throw 'PatchChangesMissing: no deterministic record or asset action exists.' }
    $evidenceIds = @($Evidence | ForEach-Object { [string]$_.evidenceId } | Where-Object { $_ } | Sort-Object -Unique)
    $proposalTargets = @()
    if ($targetNames.Count -eq 1) { $proposalTargets += "pluginName=$($targetNames[0])" }
    $proposalTargets += @($records | ForEach-Object { "record=$($_.recordId)" })
    $proposalTargets += @($assetActions | ForEach-Object { "asset=$($_.virtualPath)" })
    $proposal = New-GridRemediationProposal -CaseId $CaseId -ContextFingerprint $ContextFingerprint -EvidenceFingerprint $EvidenceFingerprint `
        -ActionType $(if ($records.Count -gt 0) { 'RecordPatchSpecification' } else { 'InstallationAssetRepairSpecification' }) `
        -Targets $proposalTargets `
        -SupportingEvidenceIds $evidenceIds -ExpectedEffects $(if ($records.Count -gt 0) { @('Apply only the proved record-field changes in a new compatibility plugin.') } else { @('Restore only the proved missing virtual asset through a separate transactional installation repair.') }) `
        -Exclusions @('No executable writer is supplied.', 'No active plugin, profile, load order, save, or installed mod is modified.') `
        -AuthorizationRequirement 'A separately implemented, reviewed, and authorized patch writer is required.' `
        -VerificationSteps @('Recollect the exact record graph.', 'Verify the new winner and required masters.', 'Validate the affected virtual asset graph.') `
        -BackupRequirement 'Not applicable: this specification is inert.' -RollbackProcedure 'Remove only a future separately authorized patch artifact.'
    $unsigned = [pscustomobject][ordered]@{
        schemaVersion = 1; caseId = $CaseId; status = 'Inert'
        solutionKind = if ($records.Count -gt 0 -and $assetActions.Count -gt 0) { 'MixedRecordAndAssetRepair' } elseif ($records.Count -gt 0) { 'RecordPatch' } else { 'InstallationAssetRepair' }
        targetPluginName = if ($targetNames.Count -eq 1) { $targetNames[0] } else { $null }; records = $records; assetActions = $assetActions
        evidenceFingerprint = $EvidenceFingerprint; supportingEvidenceIds = $evidenceIds; proposalId = $proposal.proposalId
        writer = [pscustomobject][ordered]@{ status = 'Unavailable'; capabilityId = $null }
        exclusions = @('No active-state mutation.', 'No load-order change.', 'No save modification.', 'No tool launch.')
    }
    $json = $unsigned | ConvertTo-Json -Depth 30 -Compress
    $sha = [Security.Cryptography.SHA256]::Create(); try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json); $digest = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','') } finally { $sha.Dispose() }
    $unsigned | Add-Member -NotePropertyName specificationSha256 -NotePropertyValue $digest
    [pscustomobject][ordered]@{ Specification = $unsigned; Proposal = $proposal }
}
