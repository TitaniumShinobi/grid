$ErrorActionPreference='Stop'
$gameRoot=Split-Path -Parent $PSScriptRoot
$scriptsRoot=Split-Path -Parent (Split-Path -Parent $gameRoot)
Import-Module (Join-Path $scriptsRoot 'health\Grid.Health.psm1') -Force
. (Join-Path $gameRoot 'health\actions\New-GridSkyrimRemediationProposal.ps1')
function Assert-True([bool]$Condition,[string]$Message){if(-not$Condition){throw $Message}}
function Assert-Equal($Expected,$Actual,[string]$Message){if($Expected-ne$Actual){throw "$Message Expected '$Expected', actual '$Actual'."}}
function Assert-Throws([scriptblock]$Action,[string]$Pattern,[string]$Message){try{&$Action;throw "$Message Expected '$Pattern'."}catch{if($_.Exception.Message-notmatch$Pattern){throw "$Message Unexpected: $($_.Exception.Message)"}}}
function New-TestProposal {
 $e=New-GridEvidenceItem -Parameter fixture.winner -Value 1 -Claim 'Synthetic winner observed.' -SourceType DeterministicScriptOutput -ContextFingerprint 'sha256:context' -VerificationStatus Verified -CollectorName Fixture -CollectorVersion 1
 $e|Add-Member -NotePropertyName native -NotePropertyValue ([pscustomobject]@{queryId='query-001'})
 $fp=Get-GridEvidenceFingerprint -Evidence @($e)
 $p=New-GridSkyrimRemediationProposal -CaseId case-fixture -ContextFingerprint 'sha256:context' -EvidenceFingerprint $fp -Evidence @($e) -ActionType PluginStateChange -Target @{pluginName='Fixture.esp';desiredState='Enabled'} -ExpectedEffects @('The synthetic entry becomes enabled.') -VerificationSteps @('Read the synthetic profile entry.')
 [pscustomobject]@{Proposal=$p;Fingerprint=$fp}
}
function New-ProposalGrant($Proposal,[string]$StoreRoot){
 $sha=Get-GridRemediationProposalIdentityHash -Proposal $Proposal
 $binding=New-GridAuthorizationSemanticBinding -ActorId actor.fixture -SessionId session.fixture -WorkspaceId workspace.fixture -RequestId request.fixture -SubmissionId submission.fixture -EnvelopeSha256 ('A'*64) -PlanSha256 ('B'*64) -ScopeSha256 ('C'*64) -ProposalOrSpecificationId $Proposal.proposalId -ProposalOrSpecificationSha256 $sha -Capabilities @([pscustomobject]@{capabilityId='grid.game.skyrimspecialedition.plugin-state.execute';capabilityVersion='2.0.0';adapterId='SkyrimSpecialEdition';adapterVersion='1'}) -Targets @($Proposal.targets) -NormalizedInput ([pscustomobject]@{targets=@($Proposal.targets)})
 $issued=Grant-GridProposalAuthorization -Proposal $Proposal -CurrentContextFingerprint 'sha256:context' -CurrentEvidenceFingerprint $Proposal.evidenceFingerprint -AuthorizationStoreRoot $StoreRoot -SemanticBinding $binding
 [pscustomobject]@{Binding=$binding;Issued=$issued}
}
$tempRoot=Join-Path $env:TEMP ('grid-authorized-action-'+[guid]::NewGuid().ToString('N'));$profileRoot=Join-Path $tempRoot 'profiles\Fixture';New-Item -ItemType Directory -Path $profileRoot -Force|Out-Null;$pluginsPath=Join-Path $profileRoot 'plugins.txt';Set-Content -LiteralPath $pluginsPath -Value 'Fixture.esp' -Encoding UTF8;$wrapper=Join-Path $gameRoot 'health\actions\Invoke-GridAuthorizedPluginState.ps1'
try{
 $executor=Join-Path $gameRoot 'mo2\Set-GridPluginState.ps1';Assert-Throws {&$executor -PluginName Fixture.esp -Action Enable -Mo2Root $tempRoot -Profile Fixture -WhatIf} 'AuthorizedProposalRequired' 'Direct mutation must not bypass authorization wrapper.'
 $fixture=New-TestProposal;$before=(Get-FileHash $pluginsPath -Algorithm SHA256).Hash;$grant=New-ProposalGrant $fixture.Proposal $tempRoot
 $invokeArgs=@{Proposal=$fixture.Proposal;CurrentContextFingerprint='sha256:context';CurrentEvidenceFingerprint=$fixture.Fingerprint;AuthorizationGrantId=$grant.Issued.Grant.grantId;AuthorizationSecret=$grant.Issued.AuthorizationSecret;AuthorizationStoreRoot=$tempRoot;SemanticBinding=$grant.Binding;Mo2Root=$tempRoot;Profile='Fixture';WhatIf=$true;PassThru=$true}
 $result=&$wrapper @invokeArgs
 Assert-Equal 'Consumed' $result.AuthorizationStatus 'WhatIf must consume the one-use grant.';Assert-Equal 'Incomplete' $result.ProposalStatus 'WhatIf must remain non-mutating.';Assert-True(-not$result.Execution.Changed)'WhatIf must not change state.';Assert-Equal $before (Get-FileHash $pluginsPath -Algorithm SHA256).Hash 'WhatIf must preserve fixture.'
 Assert-Throws {&$wrapper @invokeArgs} 'AuthorizationReplayRefused' 'Consumed grant must not replay.'

 $fixture=New-TestProposal;$grant=New-ProposalGrant $fixture.Proposal $tempRoot;$bad=$invokeArgs.Clone();$bad.Proposal=$fixture.Proposal;$bad.CurrentEvidenceFingerprint=$fixture.Fingerprint;$bad.AuthorizationGrantId=$grant.Issued.Grant.grantId;$bad.AuthorizationSecret='wrong';$bad.SemanticBinding=$grant.Binding
 Assert-Throws {&$wrapper @bad} 'AuthorizationSecretInvalid' 'Wrong random secret must refuse.'
 $stale=$bad.Clone();$stale.AuthorizationSecret=$grant.Issued.AuthorizationSecret;$stale.CurrentContextFingerprint='sha256:changed';Assert-Throws {&$wrapper @stale} 'StaleProposal' 'Changed context must refuse before lease.'

 $recordEvidence=New-GridEvidenceItem -Parameter fixture.record -Value 1 -Claim 'Synthetic record observed.' -SourceType DeterministicScriptOutput -ContextFingerprint 'sha256:context' -VerificationStatus Verified -CollectorName Fixture -CollectorVersion 1
 $recordProposal=New-GridRemediationProposal -CaseId case -ContextFingerprint 'sha256:context' -EvidenceFingerprint (Get-GridEvidenceFingerprint @($recordEvidence)) -ActionType RecordPatchSpecification -Targets @('pluginName=Fixture.esp','formId=01000001','changes=EDID') -SupportingEvidenceIds @($recordEvidence.evidenceId) -ExpectedEffects @('Synthetic') -AuthorizationRequirement Explicit -VerificationSteps @('Synthetic') -BackupRequirement Required -RollbackProcedure Required
 Assert-Throws {&$wrapper -Proposal $recordProposal -CurrentContextFingerprint 'sha256:context' -CurrentEvidenceFingerprint $recordProposal.evidenceFingerprint -AuthorizationGrantId 'grant-none' -AuthorizationSecret wrong -AuthorizationStoreRoot $tempRoot -SemanticBinding $grant.Binding -Mo2Root $tempRoot -Profile Fixture -WhatIf} 'UnsupportedAction' 'Record patch execution must remain unsupported.'
 Write-Host 'PASS: random one-use plugin authorization reaches only the canonical executor and refuses replay/stale context.'
}finally{if(Test-Path $tempRoot){Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue}}
