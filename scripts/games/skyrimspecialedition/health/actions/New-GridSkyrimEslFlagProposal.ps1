#requires -Version 5.1

<#
.SYNOPSIS
Builds an inert proposal to add only the TES4 ESL header flag to one proven plugin.
#>
function New-GridSkyrimEslFlagProposal {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$EligibilityResultPath,
        [Parameter(Mandatory)][string]$CaseStoreRoot,
        [Parameter(Mandatory)][string]$EvidenceCaseDirectory,
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][string]$ContextFingerprint,
        [Parameter(Mandatory)][string]$Mo2Root,
        [Parameter(Mandatory)][string]$Profile,
        [string]$ModsRoot,
        [Parameter(Mandatory)][string]$GameDataRoot,
        [Parameter(Mandatory)][string]$OutputPath,
        [switch]$PassThru
    )
    if (-not (Get-Command Get-GridCanonicalJsonSha256 -ErrorAction SilentlyContinue)) { throw 'EslFlagHealthModuleRequired: import Grid.Health.psm1 before planning.' }
    $gameRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    . (Join-Path $gameRoot 'mo2\Grid.PluginStateBatch.ps1')
    . (Join-Path $gameRoot 'mo2\Grid.EslFlag.ps1')
    $seal=Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory $EvidenceCaseDirectory
    if(-not$seal.IsValid){throw ('EslFlagEvidenceCaseInvalid: '+(@($seal.Errors)-join' '))}
    $sealedCase=[IO.Path]::GetFullPath($EvidenceCaseDirectory).TrimEnd('\','/')
    $evidencePath=[IO.Path]::GetFullPath($EligibilityResultPath)
    if(-not$evidencePath.StartsWith($sealedCase+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'EslFlagEvidenceBoundaryRefused: eligibility result must be inside the sealed evidence case.'}
    if(-not(Test-Path -LiteralPath $evidencePath -PathType Leaf)){throw "EslFlagEligibilityMissing: $evidencePath"}
    $eligibility=Get-Content -LiteralPath $evidencePath -Raw|ConvertFrom-Json -ErrorAction Stop
    if([int]$eligibility.schemaVersion-ne 1-or[string]$eligibility.status-cne'EligibleWithoutCompaction'-or[string]$eligibility.classification-cne'HeaderFlagOnly'-or[bool]$eligibility.mutationAuthorized){throw 'EslFlagEligibilityInsufficient: only a read-only HeaderFlagOnly result can be proposed.'}
    if([string]$eligibility.contextFingerprint-cne$ContextFingerprint){throw 'EslFlagEligibilityStale: context fingerprint changed.'}
    if($eligibility.warning-or[bool]$eligibility.hasEsmFlag){throw 'EslFlagEligibilityUnsafe: warned or ESM-flagged plugins require manual review.'}
    $inspection=Get-GridEslFlagInspection -Mo2Root $Mo2Root -Profile $Profile -ModsRoot $ModsRoot -GameDataRoot $GameDataRoot -PluginName ([string]$eligibility.pluginName)
    $evidenceHash=(Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash.ToUpperInvariant()
    $seed=[pscustomobject][ordered]@{schemaVersion=1;caseId=$CaseId;contextFingerprint=$ContextFingerprint;eligibilityEvidenceSha256=$evidenceHash;plugin=$inspection.plugin;protectedState=$inspection.protectedState}
    $id='esl-flag-'+(Get-GridCanonicalJsonSha256 -InputObject $seed).Substring(0,24).ToLowerInvariant()
    $spec=[pscustomobject][ordered]@{
        schemaVersion=1;specificationId=$id;specificationSha256=$null;caseId=$CaseId;contextFingerprint=$ContextFingerprint
        evidenceCase=[pscustomobject][ordered]@{directory=$sealedCase;manifestSha256=[string]$seal.Manifest.manifestSha256}
        eligibilityEvidence=[pscustomobject][ordered]@{path=$evidencePath;sha256=$evidenceHash;supportingEvidenceIds=@($eligibility.supportingEvidenceIds);classification='HeaderFlagOnly'}
        paths=$inspection.paths;plugin=$inspection.plugin;protectedState=$inspection.protectedState
        authorization=[pscustomobject][ordered]@{requirement='Fresh explicit durable one-use authorization bound to this exact plugin before/after image.';status='Required'}
        exclusions=@('No FormID compaction.','No record edits.','No plugin rename.','No load-order, plugin-state, mod-state, archive, asset, save, or game-file mutation.')
        status='AwaitingAuthorization';createdAt=[DateTimeOffset]::UtcNow.ToString('o')
    }
    $spec.specificationSha256=Get-GridEslFlagSpecificationHash -Specification $spec
    Write-GridJsonAtomic -InputObject $spec -LiteralPath ([IO.Path]::GetFullPath($OutputPath))
    $result=[pscustomobject][ordered]@{schemaVersion=1;status='AwaitingAuthorization';specification=$spec;specificationPath=[IO.Path]::GetFullPath($OutputPath);authorizationRequired=$true;changedExternalState=$false}
    if($PassThru){$result}else{$result|ConvertTo-Json -Depth 30}
}
