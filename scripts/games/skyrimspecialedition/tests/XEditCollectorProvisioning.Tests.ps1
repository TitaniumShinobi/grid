#requires -Version 5.1
$ErrorActionPreference='Stop'
$gameRoot=Split-Path -Parent $PSScriptRoot;$scriptsRoot=Split-Path -Parent(Split-Path -Parent $gameRoot)
Import-Module (Join-Path $scriptsRoot 'health\Grid.Health.psm1') -Force
. (Join-Path $gameRoot 'health\collectors\Start-GridXEditProbe.ps1');. (Join-Path $gameRoot 'health\actions\Get-GridXEditCollectorProvisioningState.ps1');. (Join-Path $gameRoot 'health\actions\New-GridXEditCollectorProvisioningProposal.ps1');. (Join-Path $gameRoot 'health\actions\Invoke-GridAuthorizedXEditCollectorProvisioning.ps1')
function Assert-True([bool]$Condition,[string]$Message){if(-not$Condition){throw $Message}}
function Assert-Equal($Expected,$Actual,[string]$Message){if($Expected-ne$Actual){throw "$Message Expected '$Expected', actual '$Actual'."}}
function Assert-Throws([scriptblock]$Action,[string]$Pattern,[string]$Message){try{&$Action;throw "$Message Expected '$Pattern'."}catch{if($_.Exception.Message-notmatch$Pattern){throw "$Message Unexpected: $($_.Exception.Message)"}}}
function Get-FixtureFieldAt([string]$Line,[int]$Index){
 $startAt=0;$current=0
 for($i=0;$i-le$Line.Length;$i++){
  if(($i-eq$Line.Length)-or($Line.Substring($i,1)-eq"`t")){
   if($current-eq$Index){return $Line.Substring($startAt,$i-$startAt)}
   $current++;$startAt=$i+1
  }
 }
 return ''
}
function New-X64Fixture([string]$Path){$bytes=New-Object byte[] 512;$bytes[0]=0x4D;$bytes[1]=0x5A;$bytes[0x3C]=0x80;$bytes[0x80]=0x50;$bytes[0x81]=0x45;$bytes[0x84]=0x64;$bytes[0x85]=0x86;[IO.File]::WriteAllBytes($Path,$bytes)}
function New-XEditGrant($Proposal,[string]$StoreRoot){
 $sha=Get-GridRemediationProposalIdentityHash -Proposal $Proposal
 $binding=New-GridAuthorizationSemanticBinding -ActorId actor.fixture -SessionId session.fixture -WorkspaceId workspace.xedit -RequestId request.xedit -SubmissionId submission.xedit -EnvelopeSha256 ('A'*64) -PlanSha256 ('B'*64) -ScopeSha256 ('C'*64) -ProposalOrSpecificationId $Proposal.proposalId -ProposalOrSpecificationSha256 $sha -Capabilities @([pscustomobject]@{capabilityId='grid.game.skyrimspecialedition.xedit-provisioning.execute';capabilityVersion='2.0.0';adapterId='SkyrimSpecialEdition';adapterVersion='1'}) -Targets @($Proposal.targets) -NormalizedInput $Proposal.provisioning
 $issued=Grant-GridProposalAuthorization -Proposal $Proposal -CurrentContextFingerprint ([string]$Proposal.contextFingerprint) -CurrentEvidenceFingerprint ([string]$Proposal.evidenceFingerprint) -AuthorizationStoreRoot $StoreRoot -SemanticBinding $binding
 [pscustomobject]@{Binding=$binding;Issued=$issued}
}
function Invoke-XEditPackage($Package,[string]$StoreRoot){$g=New-XEditGrant $Package.Proposal $StoreRoot;Invoke-GridAuthorizedXEditCollectorProvisioning -ProposalPath $Package.ProposalPath -AuthorizationGrantId $g.Issued.Grant.grantId -AuthorizationSecret $g.Issued.AuthorizationSecret -AuthorizationStoreRoot $StoreRoot -SemanticBinding $g.Binding -PassThru}
$root=Join-Path $env:TEMP ('grid-xedit-provisioning-'+[guid]::NewGuid().ToString('N'))
try{
 $tool=Join-Path $root tool;$edit=Join-Path $tool 'Edit Scripts';$case=Join-Path $root case;$data=Join-Path $root grid-data;New-Item -ItemType Directory -Path $edit,$case,$data -Force|Out-Null
 $exe=Join-Path $tool SSEEdit64.exe;New-X64Fixture $exe;$ini=Join-Path $root ModOrganizer.ini;Set-Content $ini -Encoding UTF8 -Value @('[customExecutables]','size=1','1\title=SSEEdit',"1\binary=$exe")
 $source=Join-Path $gameRoot 'sseedit\Trace-GridReference.pas';$destination=Join-Path $edit 'Trace-GridReference.pas';$unrelated=Join-Path $edit Unrelated.pas;Set-Content $unrelated preserve -Encoding UTF8;$unrelatedBefore=(Get-FileHash $unrelated -Algorithm SHA256).Hash
 $sourceText=Get-Content -Raw -LiteralPath $source;$manifest=Get-Content -Raw -LiteralPath (Join-Path $gameRoot 'sseedit\Trace-GridReference.manifest.json')|ConvertFrom-Json
 $fieldAtStart=$sourceText.IndexOf('function FieldAt');$fieldAtEnd=$sourceText.IndexOf('procedure WriteStatus',$fieldAtStart);$fieldAtSource=$sourceText.Substring($fieldAtStart,$fieldAtEnd-$fieldAtStart)
 Assert-True($fieldAtSource-match'Copy\(S, I, 1\) = #9')'FieldAt must use the xEdit-interpreter-compatible Copy construct.'
 Assert-True($fieldAtSource-notmatch'S\s*\[\s*I\s*\]')'FieldAt must not use unsupported direct indexing of its string parameter.'
 Assert-True($sourceText-match'OutputBytes\s*:=\s*OutputBytes\s*\+\s*Length\(Row\)\s*\+\s*2')'Output accounting must use interpreter-compatible explicit arithmetic.'
 Assert-True($sourceText-notmatch'Inc\s*\(\s*OutputBytes\s*,')'Output accounting must not use the unsupported two-argument Inc overload.'
 Assert-Equal 2 ([regex]::Matches($sourceText,'GetIsDeleted\s*\(').Count) 'Deleted-state queries must use xEdit''s registered GetIsDeleted API.'
 Assert-True($sourceText-notmatch'(?<!Get)IsDeleted\s*\(')'Collector must not call the unregistered IsDeleted identifier.'
 Assert-True($sourceText-match'RecordByFormID\s*\(')'Exact FormID queries must use xEdit''s direct record lookup.'
 Assert-True($sourceText-match'RecordCount\s*\(')'Exact EditorID queries must enumerate main records only.'
 Assert-True($sourceText-match'RecordByIndex\s*\(')'Exact EditorID queries must inspect bounded main records.'
 Assert-True($sourceText-notmatch'function\s+FindInElement')'Collector must not recursively traverse every record element to locate an exact identity.'
 Assert-True($sourceText-match'LastLookupValid\s+and\s+SameText\(LastPluginName, PluginName\)')'Repeated operations for one identity must reuse the validated record lookup.'
 Assert-Equal '2.2.0' $manifest.collectorVersion 'ESL eligibility auditing must have a distinct semantic version.'
 Assert-True($sourceText-match'procedure\s+AuditEslEligibility')'Collector must expose a bounded read-only ESL eligibility audit.'
 Assert-True($sourceText-match'not IsMaster\(E\) or IsInjected\(E\)')'ESL eligibility must exclude overrides and injected records like xEdit''s canonical audit.'
 Assert-True($sourceText-match'EslMaximumNewRecords\s*=\s*\$FFE')'ESL eligibility must use xEdit''s current maximum-new-record bound.'
 Assert-True($sourceText-match'EslMaximumObjectId\s*=\s*\$FFF')'ESL eligibility must reject object IDs outside the light-plugin address space.'
 Assert-Equal $manifest.sha256 (Get-FileHash $source -Algorithm SHA256).Hash 'Collector manifest hash must match the corrected source.'
 $fixtureLine="query-001`tInspectReferenceLinks`t`t`tFixtureDoor`t"
 Assert-Equal 'query-001' (Get-FixtureFieldAt $fixtureLine 0) 'FieldAt semantics must preserve the first field.'
 Assert-Equal '' (Get-FixtureFieldAt $fixtureLine 2) 'FieldAt semantics must preserve consecutive empty fields.'
 Assert-Equal 'FixtureDoor' (Get-FixtureFieldAt $fixtureLine 4) 'FieldAt semantics must preserve later fields.'
 Assert-Equal '' (Get-FixtureFieldAt $fixtureLine 5) 'FieldAt semantics must preserve a trailing empty field.'
 Write-Host 'PASS: collector uses compatible string slicing without changing bounded TSV field semantics.'
 $missing=Get-GridXEditCollectorProvisioningState -ConfigurationPath $ini -GridDataRoot $data;Assert-Equal Missing $missing.State 'Absent collector must be Missing.';$package=New-GridXEditCollectorProvisioningProposal -CaseId case-fixture -CaseDirectory $case -ProvisioningState $missing;Assert-True $package.AuthorizationRequired 'Proposal must request external grant issuance without manufacturing a token.'
 $g=New-XEditGrant $package.Proposal $root
 Assert-Throws {Invoke-GridAuthorizedXEditCollectorProvisioning -Proposal $package.Proposal -AuthorizationGrantId $g.Issued.Grant.grantId -AuthorizationSecret wrong -AuthorizationStoreRoot $root -SemanticBinding $g.Binding -PassThru} 'AuthorizationSecretInvalid' 'Wrong secret must refuse.'
 $result=Invoke-GridAuthorizedXEditCollectorProvisioning -ProposalPath $package.ProposalPath -AuthorizationGrantId $g.Issued.Grant.grantId -AuthorizationSecret $g.Issued.AuthorizationSecret -AuthorizationStoreRoot $root -SemanticBinding $g.Binding -PassThru
 Assert-Equal Ready $result.State 'Authorized copy must become Ready.';Assert-Equal (Get-FileHash $source -Algorithm SHA256).Hash (Get-FileHash $destination -Algorithm SHA256).Hash 'Installed hash must match.';Assert-True(Test-Path $result.ReceiptPath)'Receipt must persist.';Assert-Equal $unrelatedBefore (Get-FileHash $unrelated -Algorithm SHA256).Hash 'Unrelated script must remain unchanged.'
 Assert-Throws {Invoke-GridAuthorizedXEditCollectorProvisioning -ProposalPath $package.ProposalPath -AuthorizationGrantId $g.Issued.Grant.grantId -AuthorizationSecret $g.Issued.AuthorizationSecret -AuthorizationStoreRoot $root -SemanticBinding $g.Binding -PassThru} 'AuthorizationReplayRefused' 'Consumed grant must not replay.'

 Set-Content $destination 'received older collector' -Encoding UTF8;$receivedHash=(Get-FileHash $destination -Algorithm SHA256).Hash;$receiptObject=Get-Content $result.ReceiptPath -Raw|ConvertFrom-Json;$receiptObject.collectorVersion='2.1.0';$receiptObject.sourceSha256=$receivedHash;$receiptObject.installedSha256=$receivedHash;$receiptObject|ConvertTo-Json -Depth 20|Set-Content -LiteralPath $result.ReceiptPath -Encoding UTF8
 $outdated=Get-GridXEditCollectorProvisioningState -ConfigurationPath $ini -GridDataRoot $data;Assert-Equal Outdated $outdated.State 'Received prior version must be Outdated.';Assert-Equal Historic $outdated.ReceiptStatus 'A verified receipt from a prior collector version must remain trusted history.';$update=New-GridXEditCollectorProvisioningProposal -CaseId case-update -CaseDirectory (Join-Path $root update-case) -ProvisioningState $outdated;$oldHash=(Get-FileHash $destination -Algorithm SHA256).Hash;$updated=Invoke-XEditPackage $update $root;Assert-True(Test-Path $updated.BackupPath)'Outdated collector must be backed up.';Assert-Equal $oldHash (Get-FileHash $updated.BackupPath -Algorithm SHA256).Hash 'Backup must preserve prior collector.'

 Set-Content $destination 'external modification' -Encoding UTF8;$modified=Get-GridXEditCollectorProvisioningState -ConfigurationPath $ini -GridDataRoot $data;Assert-Equal ModifiedExternally $modified.State 'Unexpected changes must be ModifiedExternally.';Assert-Throws {New-GridXEditCollectorProvisioningProposal -CaseId case-modified -CaseDirectory (Join-Path $root modified-case) -ProvisioningState $modified} 'modified-external override' 'Modified content must refuse ordinary replacement.'
 Write-Host 'PASS: xEdit provisioning uses external random grants, persists verified receipts, refuses replay, and preserves rollback boundaries.'
}finally{if(Test-Path $root){Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue}}
