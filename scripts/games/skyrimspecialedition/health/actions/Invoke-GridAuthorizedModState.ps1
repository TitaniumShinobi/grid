#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess,ConfirmImpact='Medium')]
param(
 [Parameter(Mandatory)]$Proposal,[Parameter(Mandatory)][string]$CurrentContextFingerprint,
 [Parameter(Mandatory)][string]$CurrentEvidenceFingerprint,[Parameter(Mandatory)][string]$AuthorizationGrantId,
 [Parameter(Mandatory)][string]$AuthorizationSecret,[Parameter(Mandatory)][string]$AuthorizationStoreRoot,
 [Parameter(Mandatory)]$SemanticBinding,[Parameter(Mandatory)][string]$Mo2Root,[Parameter(Mandatory)][string]$Profile,
 [switch]$PassThru
)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$gameRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot);$scriptsRoot=Split-Path -Parent (Split-Path -Parent $gameRoot)
Import-Module ([IO.Path]::GetFullPath((Join-Path $scriptsRoot 'health\Grid.Health.psm1'))) -ErrorAction Stop
$validation=Test-GridRemediationProposal -Proposal $Proposal -CurrentContextFingerprint $CurrentContextFingerprint -CurrentEvidenceFingerprint $CurrentEvidenceFingerprint
if(-not $validation.IsValid){throw($validation.Errors -join ' ')};if($validation.IsStale){throw 'StaleProposal: context or evidence changed after the proposal was created.'}
if([string]$Proposal.actionType -ne 'ModStateChange'){throw 'UnsupportedAction: only ModStateChange can be executed by this wrapper.'}
$targetMap=@{};foreach($item in @($Proposal.targets)){$separator=([string]$item).IndexOf('=');if($separator-lt 1){throw "Malformed proposal target: $item"};$key=([string]$item).Substring(0,$separator);$value=([string]$item).Substring($separator+1);if($key-notin @('modName','desiredState')-or$targetMap.ContainsKey($key)){throw "Unsupported or duplicate proposal target: $key"};$targetMap[$key]=$value}
if($targetMap.Count-ne 2){throw 'ModStateChange requires exactly modName and desiredState.'};$modName=[string]$targetMap.modName;$desired=[string]$targetMap.desiredState
if($desired-notin @('Enabled','Disabled')){throw 'Proposal desiredState must be Enabled or Disabled.'}
$proposalSha=Get-GridRemediationProposalIdentityHash -Proposal $Proposal
if([string]$SemanticBinding.proposalOrSpecificationId-cne[string]$Proposal.proposalId-or[string]$SemanticBinding.proposalOrSpecificationSha256-cne$proposalSha){throw 'AuthorizationBindingMismatch: proposal identity changed.'}
if(@($SemanticBinding.capabilities|Where-Object{[string]$_.capabilityId-ceq'grid.game.skyrimspecialedition.mod-state.execute'-and[string]$_.capabilityVersion-ceq'1.0.0'}).Count-ne 1){throw 'AuthorizationBindingMismatch: mod-state capability/version is not authorized.'}
$expected=@($Proposal.targets|ForEach-Object{[string]$_}|Sort-Object -Unique);$bound=@($SemanticBinding.targets|ForEach-Object{[string]$_}|Sort-Object -Unique);if(($expected-join"`n")-cne($bound-join"`n")){throw 'AuthorizationBindingMismatch: exact mod-state targets changed.'}
$executor=[IO.Path]::GetFullPath((Join-Path $gameRoot 'mo2\Set-GridModState.ps1'));if(-not(Test-Path -LiteralPath $executor -PathType Leaf)){throw 'The canonical mod-state executor could not be resolved.'}
$lease=Enter-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -AuthorizationSecret $AuthorizationSecret -ExpectedSemanticBinding $SemanticBinding -ConsumerId ('mod-state-'+[string]$Proposal.proposalId)
$authorized=$Proposal|Select-Object *;$authorized.status='Executing';$authorized.authorization.status='Authorized';$authorized.authorization.authorizedAt=[DateTimeOffset]::UtcNow.ToString('o')
$action=if($desired-eq'Enabled'){'Enable'}else{'Disable'}
try{
 $execution=&$executor -ModName $modName -Action $action -Mo2Root $Mo2Root -Profile $Profile -AuthorizedProposal $authorized -CurrentContextFingerprint $CurrentContextFingerprint -CurrentEvidenceFingerprint $CurrentEvidenceFingerprint -WhatIf:$WhatIfPreference -PassThru
 $status=if($WhatIfPreference){'Incomplete'}else{'Verified'};$detail=if($WhatIfPreference){'WhatIf validated authorization and routing; no profile state was changed.'}else{"The executor reread modlist.txt and verified state '$($execution.State)'."}
 $verification=New-GridVerificationResult -ProposalId $authorized.proposalId -Status $status -EvidenceIds @($authorized.supportingEvidenceIds) -Detail $detail
 Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Consumed -Detail $detail|Out-Null
}catch{try{Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Failed -Detail $_.Exception.Message|Out-Null}catch{};throw}
$result=[pscustomobject][ordered]@{Tool='Invoke-GridAuthorizedModState';ProposalId=$authorized.proposalId;AuthorizationStatus=(Read-GridAuthorizationGrant -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId).state;ProposalStatus=$status;Action=$action;ModName=$modName;Execution=$execution;Verification=$verification;RecordedAt=[DateTimeOffset]::UtcNow.ToString('o')}
if($PassThru){$result}else{$result|ConvertTo-Json -Depth 10}
