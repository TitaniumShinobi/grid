#requires -Version 5.1

<#
.SYNOPSIS
Bridges one already-confirmed Grid UI mod toggle into the proposal lifecycle.
.DESCRIPTION
The WinUI caller supplies an exact installation/profile/mod/state request only
after its confirmation dialog succeeds. This bridge re-observes modlist.txt,
refuses stale UI state, creates verified evidence and an inert proposal, issues
a random-secret one-use grant, and immediately consumes it through the
canonical authorized wrapper. The secret is never written to the request JSON.
#>
[CmdletBinding(SupportsShouldProcess,ConfirmImpact='Medium')]
param([Parameter(Mandatory)][string]$InputJsonPath)
$ErrorActionPreference='Stop';$WarningPreference='SilentlyContinue';Set-StrictMode -Version Latest
if(-not(Test-Path -LiteralPath $InputJsonPath -PathType Leaf)){throw 'Confirmed mod-state request file was not found.'}
$input=Get-Content -LiteralPath $InputJsonPath -Raw|ConvertFrom-Json -ErrorAction Stop
if([int]$input.schemaVersion-ne 1-or[bool]$input.userConfirmed-ne $true){throw 'A current explicit Grid UI confirmation is required.'}
$confirmedAt=[DateTimeOffset]::Parse([string]$input.confirmedAtUtc)
if([DateTimeOffset]::UtcNow-$confirmedAt-gt[TimeSpan]::FromMinutes(2)-or$confirmedAt-[DateTimeOffset]::UtcNow-gt[TimeSpan]::FromSeconds(15)){throw 'The mod-state confirmation expired; review the current state again.'}
$desired=[string]$input.desiredState;$expected=[string]$input.expectedCurrentState
if($desired-notin @('Enabled','Disabled')-or$expected-notin @('Enabled','Disabled')-or$desired-eq$expected){throw 'The request must describe one actual Enabled/Disabled transition.'}
$mo2Root=[IO.Path]::GetFullPath([string]$input.mo2Root);$profile=[string]$input.profile;$modName=[string]$input.modName;$gridDataRoot=[IO.Path]::GetFullPath([string]$input.gridDataRoot)
$gameRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot);$scriptsRoot=Split-Path -Parent (Split-Path -Parent $gameRoot)
Import-Module ([IO.Path]::GetFullPath((Join-Path $scriptsRoot 'health\Grid.Health.psm1'))) -ErrorAction Stop
. (Join-Path $PSScriptRoot 'New-GridSkyrimRemediationProposal.ps1')
$executor=Join-Path $gameRoot 'mo2\Set-GridModState.ps1';$wrapper=Join-Path $PSScriptRoot 'Invoke-GridAuthorizedModState.ps1'
$observed=&$executor -ModName $modName -Action Status -Mo2Root $mo2Root -Profile $profile -PassThru
if([string]$observed.State-cne$expected){throw "StaleModState: Grid displayed '$expected', but modlist.txt now reports '$($observed.State)'. Refresh and review again."}
function Get-Sha256([string]$Text){$sha=[Security.Cryptography.SHA256]::Create();try{($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))|ForEach-Object{$_.ToString('x2')})-join''}finally{$sha.Dispose()}}
$contextFingerprint='sha256:'+(Get-Sha256 ((@($mo2Root,$profile,[string]$input.installationId)-join"`n").ToLowerInvariant()))
$evidence=New-GridEvidenceItem -Parameter 'mo2.modlist.entry' -Value 1 -Claim "MO2 modlist.txt contains one exact '$modName' entry in state '$expected'." -SourceType DeterministicScriptOutput -ContextFingerprint $contextFingerprint -VerificationStatus Verified -CollectorName 'Grid.ModStateBridge' -CollectorVersion '1.0.0'
$evidence|Add-Member -NotePropertyName native -NotePropertyValue ([pscustomobject]@{modName=$modName;state=$observed.State;modListFile=$observed.ModListFile;sha256=$observed.ModListSha256;profile=$profile})
$evidenceFingerprint=Get-GridEvidenceFingerprint -Evidence @($evidence)
$proposal=New-GridSkyrimRemediationProposal -CaseId ('mod-state-'+[Guid]::NewGuid().ToString('N')) -ContextFingerprint $contextFingerprint -EvidenceFingerprint $evidenceFingerprint -Evidence @($evidence) -ActionType ModStateChange -Target @{modName=$modName;desiredState=$desired} -ExpectedEffects @("Only '$modName' changes from $expected to $desired in profile '$profile'.") -Exclusions @('No plugin state, priority, ordering, mod content, game file, or other profile is changed.') -VerificationSteps @('Reread the exact modlist.txt entry and require the requested marker.','Restore the timestamped backup if verification fails.')
$proposalSha=Get-GridRemediationProposalIdentityHash -Proposal $proposal;$targets=@($proposal.targets)
$nonce=[Guid]::NewGuid().ToString('N');$hash=Get-Sha256 ($nonce+'|'+$proposalSha)
$binding=New-GridAuthorizationSemanticBinding -ActorId ([string]$input.actorId) -SessionId ([string]$input.sessionId) -WorkspaceId ('installation-'+[string]$input.installationId) -RequestId ('request-'+$nonce) -SubmissionId ('submission-'+$nonce) -EnvelopeSha256 $hash -PlanSha256 $hash -ScopeSha256 $hash -ProposalOrSpecificationId $proposal.proposalId -ProposalOrSpecificationSha256 $proposalSha -Capabilities @([pscustomobject]@{capabilityId='grid.game.skyrimspecialedition.mod-state.execute';capabilityVersion='1.0.0';adapterId='SkyrimSpecialEdition';adapterVersion='1'}) -Targets $targets -NormalizedInput ([pscustomobject]@{targets=$targets;profile=$profile;mo2Root=$mo2Root})
$authorizationRoot=Join-Path $gridDataRoot 'authorization\mod-state';$issued=Grant-GridProposalAuthorization -Proposal $proposal -CurrentContextFingerprint $contextFingerprint -CurrentEvidenceFingerprint $evidenceFingerprint -AuthorizationStoreRoot $authorizationRoot -SemanticBinding $binding
$result=&$wrapper -Proposal $proposal -CurrentContextFingerprint $contextFingerprint -CurrentEvidenceFingerprint $evidenceFingerprint -AuthorizationGrantId $issued.Grant.grantId -AuthorizationSecret $issued.AuthorizationSecret -AuthorizationStoreRoot $authorizationRoot -SemanticBinding $binding -Mo2Root $mo2Root -Profile $profile -WhatIf:$WhatIfPreference -PassThru
$result|ConvertTo-Json -Depth 12 -Compress
