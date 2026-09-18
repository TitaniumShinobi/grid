#requires -Version 5.1

<#
.SYNOPSIS
Executes one authorized, current PluginStateChange proposal.
.DESCRIPTION
Validates the shared Grid proposal contract, fingerprints, and exact one-use
approval token before delegating to the canonical reversible MO2 profile
action. Record patches and arbitrary executors are never accepted.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'Object')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Object')]$Proposal,
    [Parameter(Mandatory, ParameterSetName = 'Path')][string]$ProposalPath,
    [Parameter(Mandatory)][string]$CurrentContextFingerprint,
    [Parameter(Mandatory)][string]$CurrentEvidenceFingerprint,
    [Parameter(Mandatory)][string]$AuthorizationGrantId,
    [Parameter(Mandatory)][string]$AuthorizationSecret,
    [Parameter(Mandatory)][string]$AuthorizationStoreRoot,
    [Parameter(Mandatory)]$SemanticBinding,
    [Parameter(Mandatory)][string]$Mo2Root,
    [Parameter(Mandatory)][string]$Profile,
    [switch]$PassThru
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$gameRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $gameRoot)
$gridHealthModulePath = [IO.Path]::GetFullPath((Join-Path $scriptsRoot 'health\Grid.Health.psm1'))
$gridHealthModule = Get-Module | Where-Object {
    $_.Path -and [IO.Path]::GetFullPath([string]$_.Path) -eq $gridHealthModulePath
} | Select-Object -First 1
if ($null -eq $gridHealthModule) {
    Import-Module $gridHealthModulePath -ErrorAction Stop
}
if ($PSCmdlet.ParameterSetName -eq 'Path') {
    if (-not (Test-Path -LiteralPath $ProposalPath -PathType Leaf)) { throw "Proposal file not found: $ProposalPath" }
    $Proposal = Get-Content -LiteralPath $ProposalPath -Raw | ConvertFrom-Json
}

$validation = Test-GridRemediationProposal -Proposal $Proposal -CurrentContextFingerprint $CurrentContextFingerprint -CurrentEvidenceFingerprint $CurrentEvidenceFingerprint
if (-not $validation.IsValid) { throw ($validation.Errors -join ' ') }
if ($validation.IsStale) { throw 'StaleProposal: context or evidence changed after the proposal was created.' }
if ([string]$Proposal.actionType -ne 'PluginStateChange') { throw "UnsupportedAction: only PluginStateChange can be executed by this wrapper." }

$targetMap = @{}
foreach ($entry in @($Proposal.targets)) {
    $text = [string]$entry
    $separator = $text.IndexOf('=')
    if ($separator -lt 1) { throw "Malformed proposal target: $text" }
    $name = $text.Substring(0, $separator)
    $value = $text.Substring($separator + 1)
    if ($name -notin @('pluginName', 'desiredState') -or $targetMap.ContainsKey($name)) { throw "Unsupported or duplicate proposal target: $name" }
    $targetMap[$name] = $value
}
if ($targetMap.Count -ne 2 -or -not $targetMap.ContainsKey('pluginName') -or -not $targetMap.ContainsKey('desiredState')) { throw 'PluginStateChange requires exactly pluginName and desiredState.' }
$pluginName = [string]$targetMap.pluginName
$desiredState = [string]$targetMap.desiredState
if ([IO.Path]::GetFileName($pluginName) -cne $pluginName -or [IO.Path]::GetExtension($pluginName) -notin @('.esp', '.esm', '.esl')) { throw 'Proposal pluginName must be a plugin filename.' }
if ($desiredState -notin @('Enabled', 'Disabled')) { throw 'Proposal desiredState must be Enabled or Disabled.' }

$proposalSha = Get-GridRemediationProposalIdentityHash -Proposal $Proposal
if ([string]$SemanticBinding.proposalOrSpecificationId -cne [string]$Proposal.proposalId -or [string]$SemanticBinding.proposalOrSpecificationSha256 -cne $proposalSha) { throw 'AuthorizationBindingMismatch: proposal identity changed.' }
if (@($SemanticBinding.capabilities | Where-Object { [string]$_.capabilityId -ceq 'grid.game.skyrimspecialedition.plugin-state.execute' -and [string]$_.capabilityVersion -ceq '2.0.0' }).Count -ne 1) { throw 'AuthorizationBindingMismatch: plugin-state capability/version is not authorized.' }
$expectedTargets=@($Proposal.targets | ForEach-Object { [string]$_ } | Sort-Object -Unique); $boundTargets=@($SemanticBinding.targets | ForEach-Object { [string]$_ } | Sort-Object -Unique)
if (($expectedTargets -join "`n") -cne ($boundTargets -join "`n")) { throw 'AuthorizationBindingMismatch: exact plugin-state targets changed.' }
$executorPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\mo2\Set-GridPluginState.ps1'))
$expectedPath = [IO.Path]::GetFullPath((Join-Path $gameRoot 'mo2\Set-GridPluginState.ps1'))
if ($executorPath -ne $expectedPath -or -not (Test-Path -LiteralPath $executorPath -PathType Leaf)) { throw 'The canonical plugin-state executor could not be resolved.' }
$lease = Enter-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -AuthorizationSecret $AuthorizationSecret -ExpectedSemanticBinding $SemanticBinding -ConsumerId ('plugin-state-' + [string]$Proposal.proposalId)
$authorized = $Proposal | Select-Object *
$authorized.status = 'Executing'
$authorized.authorization.status = 'Authorized'
$authorized.authorization.authorizedAt = [DateTimeOffset]::UtcNow.ToString('o')
$authorized | Add-Member -NotePropertyName execution -NotePropertyValue ([pscustomobject][ordered]@{ status='Executing'; startedAt=[DateTimeOffset]::UtcNow.ToString('o'); completedAt=$null; detail=$null; grantId=$AuthorizationGrantId; leaseId=[string]$lease.Lease.leaseId }) -Force
if ($PSCmdlet.ParameterSetName -eq 'Path') { Write-GridJsonAtomic -InputObject $authorized -LiteralPath ([IO.Path]::GetFullPath($ProposalPath)) }
$action = if ($desiredState -eq 'Enabled') { 'Enable' } else { 'Disable' }
$executorArguments = @{
    PluginName = $pluginName; Action = $action; Mo2Root = $Mo2Root; Profile = $Profile
    AuthorizedProposal = $authorized; CurrentContextFingerprint = $CurrentContextFingerprint
    CurrentEvidenceFingerprint = $CurrentEvidenceFingerprint; WhatIf = $WhatIfPreference; PassThru = $true
}
$execution = $null
$verification = $null
try {
    $execution = & $executorPath @executorArguments
    $verificationStatus = if ($WhatIfPreference) { 'Incomplete' } else { 'Verified' }
    $verificationDetail = if ($WhatIfPreference) { 'WhatIf validated authorization and routing; no profile state was changed.' } else { "The canonical executor reread plugins.txt and verified state '$($execution.State)'." }
    $verification = New-GridVerificationResult -ProposalId $authorized.proposalId -Status $verificationStatus -EvidenceIds @($authorized.supportingEvidenceIds) -Detail $verificationDetail
    $authorized.status = $verificationStatus
    $authorized.execution.status = $verificationStatus
    $authorized.execution.completedAt = (Get-Date).ToUniversalTime().ToString('o')
    $authorized.execution.detail = $verificationDetail
    Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Consumed -Detail $verificationDetail | Out-Null
    if ($PSCmdlet.ParameterSetName -eq 'Path') { Write-GridJsonAtomic -InputObject $authorized -LiteralPath ([IO.Path]::GetFullPath($ProposalPath)) }
}
catch {
    $authorized.status = 'Failed'
    $authorized.execution.status = 'Failed'
    $authorized.execution.completedAt = (Get-Date).ToUniversalTime().ToString('o')
    $authorized.execution.detail = $_.Exception.Message
    try { Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Failed -Detail $_.Exception.Message | Out-Null } catch {}
    if ($PSCmdlet.ParameterSetName -eq 'Path') { Write-GridJsonAtomic -InputObject $authorized -LiteralPath ([IO.Path]::GetFullPath($ProposalPath)) }
    throw
}
$result = [pscustomobject][ordered]@{
    Tool = 'Invoke-GridAuthorizedPluginState'; ProposalId = $authorized.proposalId
    AuthorizationStatus = (Read-GridAuthorizationGrant -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId).state; ProposalStatus = $authorized.status; Action = $action
    PluginName = $pluginName; Execution = $execution; Verification = $verification; RecordedAt = (Get-Date).ToUniversalTime().ToString('o')
}
if ($PassThru) { $result } else { $result | ConvertTo-Json -Depth 8 }
