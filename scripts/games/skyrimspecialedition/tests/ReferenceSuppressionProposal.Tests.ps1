$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
Import-Module (Join-Path $repo 'scripts\health\Grid.Health.psm1') -Force
Import-Module (Join-Path $repo 'scripts\games\skyrimspecialedition\health\Grid.Health.Skyrim.psm1') -Force
function Assert-True([bool]$Condition,[string]$Message){if(-not$Condition){throw $Message}}
function Assert-Equal($Expected,$Actual,[string]$Message){if($Expected-ne$Actual){throw "$Message Expected '$Expected', actual '$Actual'."}}
function Assert-Throws([scriptblock]$Action,[string]$Pattern,[string]$Message){try{&$Action;throw "$Message No exception."}catch{if($_.Exception.Message-notmatch$Pattern){throw "$Message Actual: $($_.Exception.Message)"}}}
$root=Join-Path $env:TEMP ('grid-reference-suppression-'+[guid]::NewGuid().ToString('N'));$store=Join-Path $root 'store'
try{
 $caseId='evidence-placement-fixture';$tx=New-GridCaseStoreTransaction -StoreRoot $store -CaseId $caseId
 Write-GridCaseStoreArtifact -Transaction $tx -RelativePath 'case.json' -Value ([pscustomobject]@{schemaVersion=1;caseId=$caseId})|Out-Null
 Write-GridCaseStoreArtifact -Transaction $tx -RelativePath 'investigation-plan.json' -Value ([pscustomobject]@{schemaVersion=1;installationId='installation.fixture';profileId='profile.fixture';baseline=[pscustomobject]@{caseDirectory=(Join-Path $root 'baseline');manifestSha256=('E'*64)}})|Out-Null
 $sourcePath=Join-Path $root 'Source.esp';[IO.File]::WriteAllBytes($sourcePath,[byte[]]@(1,2,3))
 $graph=[pscustomobject][ordered]@{schemaVersion=1;status='Complete';plugins=@([pscustomobject]@{name='Source.esp';canonicalPath=$sourcePath;rawSha256=('A'*64)});chains=@([pscustomobject][ordered]@{
   target=[pscustomobject]@{originPlugin='Source.esp';localFormId=0x1234};records=@();winner=[pscustomobject][ordered]@{
     signature='REFR';pluginName='Source.esp';isEnabled=$true;isDeleted=$false;isInitiallyDisabled=$false;recordFlags=0
     pluginRawSha256=('A'*64);enableParent=$null;linkedReferences=@();baseObject=[pscustomobject]@{originPlugin='Skyrim.esm';localFormId=1}
     worldspace=[pscustomobject]@{originPlugin='Skyrim.esm';localFormId=0x3c};cell=[pscustomobject]@{originPlugin='Skyrim.esm';localFormId=0x9278}
     placement='Temporary';transform=[pscustomobject]@{x=1;y=2;z=3;rotationX=0;rotationY=0;rotationZ=0};scale=2.09
   }
 })}
 Write-GridCaseStoreArtifact -Transaction $tx -RelativePath 'evidence/plugin-record-graph.v1.json' -Value $graph|Out-Null
 $now=[DateTimeOffset]::UtcNow.ToString('o');$run=[pscustomobject]@{schemaVersion=1;runId='run-fixture';caseId=$caseId;state='Completed';startedAt=$now;completedAt=$now;planFingerprint=('B'*64);resourcePolicyVersion='fixture';gates=@();sufficiency=[pscustomobject]@{schemaVersion=1;status='SufficientForRequestedDiagnosis'};checkpoints=@();primaryFailure=$null;secondaryFailures=@()}
 Seal-GridCaseStoreRun -Transaction $tx -Run $run|Out-Null;$sealed=Seal-GridDiagnosticCase -Transaction $tx -SemanticBaselineFingerprint ('C'*64) -Runs @($run)
 $output=Join-Path $root 'proposal\reference-suppression.v1.json'
 $result=New-GridSkyrimReferenceSuppressionProposal -EvidenceCaseDirectory $sealed.CaseDirectory -CaseStoreRoot $store -CaseId repair-fixture -ContextFingerprint ('D'*64) -TargetReferences @([pscustomobject]@{originPlugin='Source.esp';localFormId='001234'}) -PatchPluginName 'Grid_Placement_Fixes.esp' -Intent 'Suppress the user-identified overlapping placement.' -OutputPath $output -PassThru
 Assert-Equal 'AwaitingAuthorization' $result.status 'Planning must stop for authorization.'
 Assert-Equal 2 $result.specification.schemaVersion 'New proposals must carry the complete executable-context binding.'
 Assert-True (-not$result.changedExternalState) 'Planning must not mutate external state.'
 Assert-Equal 1 @($result.specification.records).Count 'Exactly one reviewed reference must be proposed.'
 Assert-Equal 'SetInitiallyDisabled' $result.specification.records[0].action 'The action must be bounded to Initially Disabled.'
 Assert-Equal 2.09 $result.specification.records[0].scale 'Observed placement evidence must remain reviewable.'
 Assert-Equal (Get-GridSkyrimReferenceSuppressionSpecificationHash -Specification $result.specification) $result.specification.specificationSha256 'The specification digest must verify.'
 $planning=New-GridSkyrimReferenceSuppressionPlanningCase -CaseStoreRoot $store -EvidenceCaseDirectory $sealed.CaseDirectory -PlanningCaseId repair-planning-fixture -ContextFingerprint ('D'*64) -TargetReferences @([pscustomobject]@{originPlugin='Source.esp';localFormId='001234'}) -PatchPluginName 'Grid_Placement_Fixes.esp' -Intent 'Suppress the user-identified overlapping placement.' -PassThru
 Assert-Equal 'AwaitingAuthorization' $planning.status 'A sealed planning case must preserve the authorization boundary.'
 Assert-True (Test-GridDiagnosticCaseSeal -StoreRoot $store -CaseDirectory $planning.caseDirectory).IsValid 'The durable planning case must have a valid seal.'
 Assert-Throws {New-GridSkyrimReferenceSuppressionProposal -EvidenceCaseDirectory $sealed.CaseDirectory -CaseStoreRoot $store -CaseId repair-bad -ContextFingerprint ('D'*64) -TargetReferences @([pscustomobject]@{originPlugin='Source.esp';localFormId='001235'}) -PatchPluginName 'Grid.esp' -Intent 'bad target' -OutputPath (Join-Path $root 'bad.json')} 'TargetUnresolved' 'Unobserved references must fail closed.'
 $graphPath=Join-Path $sealed.CaseDirectory 'evidence\plugin-record-graph.v1.json';Add-Content -LiteralPath $graphPath -Value 'tamper'
 Assert-Throws {New-GridSkyrimReferenceSuppressionProposal -EvidenceCaseDirectory $sealed.CaseDirectory -CaseStoreRoot $store -CaseId repair-tampered -ContextFingerprint ('D'*64) -TargetReferences @([pscustomobject]@{originPlugin='Source.esp';localFormId='001234'}) -PatchPluginName 'Grid.esp' -Intent 'tampered' -OutputPath (Join-Path $root 'tampered.json')} 'EvidenceCaseInvalid' 'Tampered evidence must fail closed.'
 'PASS: exact placed-reference suppression proposals are sealed-evidence-bound, inert, and authorization-gated.'
}finally{if(Test-Path -LiteralPath $root){Remove-Item -LiteralPath $root -Recurse -Force}}
