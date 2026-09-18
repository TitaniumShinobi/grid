#requires -Version 5.1

<#!
.SYNOPSIS
Consumes one exact authorization grant and executes a plugin activation batch.
#>

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$SpecificationPath,
    [Parameter(Mandatory)][string]$CurrentContextFingerprint,
    [Parameter(Mandatory)][string]$CurrentEvidenceFingerprint,
    [Parameter(Mandatory)][string]$AuthorizationGrantId,
    [Parameter(Mandatory)][string]$AuthorizationSecret,
    [Parameter(Mandatory)][string]$AuthorizationStoreRoot,
    [Parameter(Mandatory)]$SemanticBinding,
    [Parameter(Mandatory)][string]$CaseStoreRoot,
    [Parameter(Mandatory)][string]$TransactionRoot,
    [switch]$PassThru
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$gameRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $gameRoot)
Import-Module (Join-Path $scriptsRoot 'health\Grid.Health.psm1') -Force -DisableNameChecking
. (Join-Path $gameRoot 'mo2\Grid.PluginStateBatch.ps1')
if (-not (Test-Path -LiteralPath $SpecificationPath -PathType Leaf)) { throw "PluginBatchSpecificationMissing: $SpecificationPath" }
$spec = Get-Content -LiteralPath $SpecificationPath -Raw | ConvertFrom-Json -ErrorAction Stop
if ([int]$spec.schemaVersion -ne 1 -or [string]$spec.status -ne 'AwaitingAuthorization') { throw 'PluginBatchSpecificationInvalid: unsupported state or schema.' }
$specSha = Get-GridPluginStateBatchSpecificationHash -Specification $spec
if ([string]$spec.specificationSha256 -cne $specSha) { throw 'PluginBatchSpecificationDigestMismatch: specification changed after review.' }
if ([string]$spec.contextFingerprint -cne $CurrentContextFingerprint.ToUpperInvariant() -or [string]$spec.evidenceFingerprint -cne $CurrentEvidenceFingerprint.ToUpperInvariant()) {
    throw 'PluginBatchStaleProposal: current context or evidence fingerprint differs.'
}
$targets = @($spec.operations | Sort-Object sequence | ForEach-Object { "plugin:$($_.pluginName)=Enabled" })
if ([string]$SemanticBinding.proposalOrSpecificationId -cne [string]$spec.specificationId -or
    [string]$SemanticBinding.proposalOrSpecificationSha256 -cne $specSha) { throw 'AuthorizationBindingMismatch: plugin batch identity changed.' }
if (@($SemanticBinding.capabilities | Where-Object { [string]$_.capabilityId -ceq 'grid.game.skyrimspecialedition.plugin-state-batch.execute' -and [string]$_.capabilityVersion -ceq '1.0.0' }).Count -ne 1) {
    throw 'AuthorizationBindingMismatch: plugin-state-batch capability/version is not authorized.'
}
if ((@($SemanticBinding.targets | Sort-Object -Unique) -join "`n") -cne (@($targets | Sort-Object -Unique) -join "`n")) { throw 'AuthorizationBindingMismatch: exact plugin targets changed.' }
if ([string]$SemanticBinding.normalizedInputSha256 -cne (Get-GridCanonicalJsonSha256 -InputObject $spec)) { throw 'AuthorizationBindingMismatch: normalized specification changed.' }
$grantPreflight = Test-GridAuthorizationGrantPreflight -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -AuthorizationSecret $AuthorizationSecret -ExpectedSemanticBinding $SemanticBinding
if ([string]$grantPreflight.state -ne 'Issued') { throw 'AuthorizationReplayRefused: only a newly issued grant can execute this batch.' }

# Revalidate every read-only primitive before the grant enters its exclusive lease.
$seal = Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory ([string]$spec.baseline.caseDirectory)
if (-not $seal.IsValid -or [string]$seal.Manifest.manifestSha256 -cne [string]$spec.baseline.manifestSha256) { throw 'PluginBatchBaselineSealInvalid: sealed evidence changed or is unavailable.' }
$fresh = Get-GridPluginStateBatchInspection -Mo2Root ([string]$spec.paths.mo2Root) -Profile ([string]$spec.paths.profile) `
    -ModsRoot ([string]$spec.paths.modsRoot) -GameDataRoot ([string]$spec.paths.gameDataRoot) -RequestedPlugins @($spec.requestedPlugins) `
    -MaximumFullPlugins ([int]$spec.slotUsage.fullMaximum) -MaximumLightPlugins ([int]$spec.slotUsage.lightMaximum)
[void](Test-GridPluginStateBatchInspectionEquivalent -Expected $spec -Actual $fresh)

$lease = Enter-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -AuthorizationSecret $AuthorizationSecret `
    -ExpectedSemanticBinding $SemanticBinding -ConsumerId ([string]$spec.specificationId)
$authorized = $spec | Select-Object *
$authorized | Add-Member -NotePropertyName executionStatus -NotePropertyValue 'Executing' -Force
$executor = [IO.Path]::GetFullPath((Join-Path $gameRoot 'mo2\Set-GridPluginStateBatch.ps1'))
try {
    $execution = & $executor -AuthorizedSpecification $authorized -CaseStoreRoot $CaseStoreRoot -TransactionRoot $TransactionRoot `
        -WhatIf:$WhatIfPreference -Confirm:$false -PassThru
    $detail = if ([string]$execution.status -eq 'Planned') { 'WhatIf validated the exact batch and consumed authorization without profile mutation.' } else { 'The atomic batch was applied and its exact after image verified.' }
    Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Consumed -Detail $detail | Out-Null
}
catch {
    try { Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Failed -Detail $_.Exception.Message | Out-Null } catch {}
    throw
}
$result = [pscustomobject][ordered]@{
    schemaVersion=1; status=[string]$execution.status; specificationId=[string]$spec.specificationId
    authorizationStatus=(Read-GridAuthorizationGrant -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId).state
    execution=$execution; recordedAt=[DateTimeOffset]::UtcNow.ToString('o')
}
if ($PassThru) { $result } else { $result | ConvertTo-Json -Depth 30 }
