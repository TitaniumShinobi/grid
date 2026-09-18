#requires -Version 5.1
$ErrorActionPreference='Stop'
$gameRoot=Split-Path -Parent $PSScriptRoot
$scriptsRoot=Split-Path -Parent(Split-Path -Parent $gameRoot)
Import-Module (Join-Path $scriptsRoot 'health\Grid.Health.psm1') -Force
. (Join-Path $gameRoot 'health\collectors\Start-GridXEditProbe.ps1')
. (Join-Path $gameRoot 'health\actions\Get-GridXEditCollectorProvisioningState.ps1')
. (Join-Path $gameRoot 'health\actions\Get-GridXEditReferenceSuppressionWriterProvisioningState.ps1')
. (Join-Path $gameRoot 'health\actions\New-GridXEditReferenceSuppressionWriterProvisioningProposal.ps1')
. (Join-Path $gameRoot 'health\actions\Invoke-GridAuthorizedXEditReferenceSuppressionWriterProvisioning.ps1')
function Assert-True([bool]$Condition,[string]$Message){if(-not$Condition){throw $Message}}
function Assert-Equal($Expected,$Actual,[string]$Message){if($Expected-ne$Actual){throw "$Message Expected '$Expected', actual '$Actual'."}}
function Assert-Throws([scriptblock]$Action,[string]$Pattern,[string]$Message){try{&$Action;throw "$Message Expected '$Pattern'."}catch{if($_.Exception.Message-notmatch$Pattern){throw "$Message Unexpected: $($_.Exception.Message)"}}}
function New-X64Fixture([string]$Path){$bytes=New-Object byte[] 512;$bytes[0]=0x4D;$bytes[1]=0x5A;$bytes[0x3C]=0x80;$bytes[0x80]=0x50;$bytes[0x81]=0x45;$bytes[0x84]=0x64;$bytes[0x85]=0x86;[IO.File]::WriteAllBytes($Path,$bytes)}
function New-WriterGrant($Proposal,[string]$StoreRoot){
 $sha=Get-GridRemediationProposalIdentityHash -Proposal $Proposal
 $cap=[pscustomobject]@{capabilityId='grid.game.skyrimspecialedition.xedit-reference-suppression-writer-provisioning.execute';capabilityVersion='1.0.0';adapterId='SkyrimSpecialEdition';adapterVersion='1'}
 $binding=New-GridAuthorizationSemanticBinding -ActorId actor.fixture -SessionId session.fixture -WorkspaceId workspace.writer -RequestId request.writer -SubmissionId submission.writer -EnvelopeSha256 ('A'*64) -PlanSha256 ('B'*64) -ScopeSha256 ('C'*64) -ProposalOrSpecificationId $Proposal.proposalId -ProposalOrSpecificationSha256 $sha -Capabilities @($cap) -Targets @($Proposal.targets) -NormalizedInput $Proposal.provisioning
 $issued=Grant-GridProposalAuthorization -Proposal $Proposal -CurrentContextFingerprint ([string]$Proposal.contextFingerprint) -CurrentEvidenceFingerprint ([string]$Proposal.evidenceFingerprint) -AuthorizationStoreRoot $StoreRoot -SemanticBinding $binding
 [pscustomobject]@{Binding=$binding;Issued=$issued}
}
$root=Join-Path $env:TEMP ('grid-xedit-writer-provisioning-'+[guid]::NewGuid().ToString('N'))
try{
 $tool=Join-Path $root tool;$edit=Join-Path $tool 'Edit Scripts';$case=Join-Path $root case;$data=Join-Path $root data
 New-Item -ItemType Directory -Path $edit,$case,$data -Force|Out-Null
 $exe=Join-Path $tool SSEEdit64.exe;New-X64Fixture $exe
 $ini=Join-Path $root ModOrganizer.ini;Set-Content $ini -Encoding UTF8 -Value @('[customExecutables]','size=1','1\title=SSEEdit',"1\binary=$exe")
 $source=Join-Path $gameRoot 'sseedit\Write-GridReferenceSuppression.pas';$manifest=Get-Content -Raw -LiteralPath (Join-Path $gameRoot 'sseedit\Write-GridReferenceSuppression.manifest.json')|ConvertFrom-Json
 Assert-Equal ([string]$manifest.sha256) (Get-FileHash $source -Algorithm SHA256).Hash 'Writer manifest hash must bind the exact reviewed source.'
 $text=Get-Content -Raw -LiteralPath $source
 foreach($required in @('AddNewFileName','wbCopyElementToFile','SetIsInitiallyDisabled','WinningOverride','GetIsInitiallyDisabled','XESP - Enable Parent')){Assert-True($text.Contains($required)) "Writer must contain the bounded '$required' contract."}
 Assert-True($text -notmatch '(?i)SetEditValue\s*\(') 'Writer must not contain a generic unbounded value mutation.'
 $state=Get-GridXEditReferenceSuppressionWriterProvisioningState -ConfigurationPath $ini -GridDataRoot $data
 Assert-Equal Missing $state.State 'Absent writer must be Missing.'
 $package=New-GridXEditReferenceSuppressionWriterProvisioningProposal -CaseId case-writer -CaseDirectory $case -ProvisioningState $state
 Assert-True $package.AuthorizationRequired 'Provisioning must require external one-use authorization.'
 $grant=New-WriterGrant $package.Proposal $root
 Assert-Throws {Invoke-GridAuthorizedXEditReferenceSuppressionWriterProvisioning -Proposal $package.Proposal -AuthorizationGrantId $grant.Issued.Grant.grantId -AuthorizationSecret wrong -AuthorizationStoreRoot $root -SemanticBinding $grant.Binding -PassThru} 'AuthorizationSecretInvalid' 'Wrong secret must refuse.'
 $result=Invoke-GridAuthorizedXEditReferenceSuppressionWriterProvisioning -ProposalPath $package.ProposalPath -AuthorizationGrantId $grant.Issued.Grant.grantId -AuthorizationSecret $grant.Issued.AuthorizationSecret -AuthorizationStoreRoot $root -SemanticBinding $grant.Binding -PassThru
 Assert-Equal Verified $result.State 'Authorized provisioning must verify.'
 Assert-Equal (Get-FileHash $source -Algorithm SHA256).Hash (Get-FileHash $state.DestinationPath -Algorithm SHA256).Hash 'Installed writer must match the reviewed source.'
 Assert-True(Test-Path $result.ReceiptPath -PathType Leaf) 'Provisioning must persist a verified receipt.'
 Assert-Throws {Invoke-GridAuthorizedXEditReferenceSuppressionWriterProvisioning -ProposalPath $package.ProposalPath -AuthorizationGrantId $grant.Issued.Grant.grantId -AuthorizationSecret $grant.Issued.AuthorizationSecret -AuthorizationStoreRoot $root -SemanticBinding $grant.Binding -PassThru} 'AuthorizationReplayRefused' 'A consumed grant must not replay.'
 Set-Content -LiteralPath $state.DestinationPath -Value 'external mutation' -Encoding UTF8
 $modified=Get-GridXEditReferenceSuppressionWriterProvisioningState -ConfigurationPath $ini -GridDataRoot $data
 Assert-Equal ModifiedExternally $modified.State 'Unreceived changes must be classified as ModifiedExternally.'
 Assert-Throws {New-GridXEditReferenceSuppressionWriterProvisioningProposal -CaseId case-modified -CaseDirectory (Join-Path $root modified) -ProvisioningState $modified} 'modified-external override' 'Externally modified writer must refuse ordinary replacement.'
 'PASS: reference-suppression writer provisioning is manifest-bound, one-use authorized, replay resistant, and rollback constrained.'
}finally{if(Test-Path $root){Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue}}
