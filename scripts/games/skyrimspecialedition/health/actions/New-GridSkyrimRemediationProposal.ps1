#requires -Version 5.1

<#
.SYNOPSIS
Creates an evidence-bound Skyrim remediation proposal without applying it.
#>
function New-GridSkyrimRemediationProposal {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][string]$ContextFingerprint,
        [Parameter(Mandatory)][string]$EvidenceFingerprint,
        [Parameter(Mandatory)][object[]]$Evidence,
        [Parameter(Mandatory)][ValidateSet('ModStateChange', 'PluginStateChange', 'RecordPatchSpecification')][string]$ActionType,
        [Parameter(Mandatory)][hashtable]$Target,
        [Parameter(Mandatory)][string[]]$ExpectedEffects,
        [string[]]$Exclusions = @(),
        [Parameter(Mandatory)][string[]]$VerificationSteps,
        [string]$BackupRequirement = 'A timestamped backup is required before execution.',
        [string]$RollbackRequirement = 'Restore the verified pre-change backup if verification fails.'
    )
    $currentEvidence = @($Evidence | Where-Object {
        $_.contextFingerprint -eq $ContextFingerprint -and $_.verificationStatus -in @('Collected', 'Verified', 'Contradicted') -and
        $_.sourceType -ne 'ModelReasoning' -and -not [string]::IsNullOrWhiteSpace([string]$_.evidenceId)
    })
    $verified = @($currentEvidence | Where-Object {
        $_.verificationStatus -in @('Collected', 'Verified') -and $_.contextFingerprint -eq $ContextFingerprint -and
        $_.sourceType -ne 'ModelReasoning' -and -not [string]::IsNullOrWhiteSpace([string]$_.evidenceId)
    })
    if ($verified.Count -eq 0) { throw 'A remediation proposal requires verified evidence bound to the current context fingerprint.' }
    $calculatedFingerprint = Get-GridEvidenceFingerprint -Evidence $Evidence
    if ($calculatedFingerprint -ne $EvidenceFingerprint) { throw 'EvidenceFingerprint does not match the supplied immutable evidence.' }
    if ($ActionType -eq 'ModStateChange') {
        if (-not $Target.ContainsKey('modName') -or -not $Target.ContainsKey('desiredState')) { throw 'ModStateChange requires modName and desiredState.' }
        $modName = [string]$Target.modName
        if ([string]::IsNullOrWhiteSpace($modName) -or [IO.Path]::GetFileName($modName) -cne $modName -or $modName -in @('.', '..') -or $modName.EndsWith('_separator', [StringComparison]::Ordinal)) { throw 'The mod target must be one non-separator MO2 mod name, not a path.' }
        if ([string]$Target.desiredState -notin @('Enabled', 'Disabled')) { throw 'Mod desiredState must be Enabled or Disabled.' }
    }
    elseif ($ActionType -eq 'PluginStateChange') {
        if (-not $Target.ContainsKey('pluginName') -or -not $Target.ContainsKey('desiredState')) { throw 'PluginStateChange requires pluginName and desiredState.' }
        if ([IO.Path]::GetFileName([string]$Target.pluginName) -cne [string]$Target.pluginName) { throw 'The plugin target must be a filename, not a path.' }
        if ([IO.Path]::GetExtension([string]$Target.pluginName) -notin @('.esp', '.esm', '.esl')) { throw 'The plugin target must end in .esp, .esm, or .esl.' }
        if ([string]$Target.desiredState -notin @('Enabled', 'Disabled')) { throw 'Plugin desiredState must be Enabled or Disabled.' }
    }
    else {
        if (-not $Target.ContainsKey('pluginName') -or (-not $Target.ContainsKey('formId') -and -not $Target.ContainsKey('editorId')) -or -not $Target.ContainsKey('changes')) {
            throw 'RecordPatchSpecification requires pluginName, formId or editorId, and an explicit changes value.'
        }
        if ([IO.Path]::GetFileName([string]$Target.pluginName) -cne [string]$Target.pluginName) { throw 'The plugin target must be a filename, not a path.' }
        if ($Target.ContainsKey('formId') -and [string]$Target.formId -notmatch '^(?i)(0x)?[0-9a-f]{8}$') { throw 'Record patch formId must contain eight hexadecimal digits.' }
        if (@($Target.changes).Count -eq 0) { throw 'Record patch changes cannot be empty.' }
    }
    if (-not (Get-Command New-GridRemediationProposal -ErrorAction SilentlyContinue)) { throw 'The shared Grid health module must be imported before creating a remediation proposal.' }
    $arguments = @{
        CaseId = $CaseId; ContextFingerprint = $ContextFingerprint; EvidenceFingerprint = $EvidenceFingerprint
        ActionType = $ActionType; Targets = @($Target.GetEnumerator() | Sort-Object Key | ForEach-Object { "$($_.Key)=$($_.Value)" })
        SupportingEvidenceIds = @($verified.evidenceId | Sort-Object)
        ContradictingEvidenceIds = @($currentEvidence | Where-Object verificationStatus -eq 'Contradicted' | ForEach-Object evidenceId | Sort-Object)
        ExpectedEffects = $ExpectedEffects; Exclusions = $Exclusions
        AuthorizationRequirement = 'Explicit one-use user authorization bound to this proposal and current fingerprints.'
        VerificationSteps = $VerificationSteps; BackupRequirement = $BackupRequirement; RollbackProcedure = $RollbackRequirement
        Supported = ($ActionType -in @('ModStateChange', 'PluginStateChange'))
    }
    return New-GridRemediationProposal @arguments
}
