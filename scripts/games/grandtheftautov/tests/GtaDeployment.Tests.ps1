#requires -Version 5.1
$ErrorActionPreference='Stop'
$gameRoot=Split-Path -Parent $PSScriptRoot
$repoRoot=[IO.Path]::GetFullPath((Join-Path $gameRoot '..\..\..'))
Import-Module (Join-Path $repoRoot 'scripts\health\Grid.Health.psm1') -Force
Import-Module (Join-Path $gameRoot 'health\Grid.Health.GtaV.psm1') -Force
function Assert-Grid([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
$fixture=Join-Path $env:TEMP ('grid-gta-deployment-'+[guid]::NewGuid().ToString('N'))
try{
    $game=Join-Path $fixture 'Grand Theft Auto V';$artifacts=Join-Path $fixture 'artifacts';$store=Join-Path $fixture 'store';$rollback=Join-Path $fixture 'rollback'
    New-Item -ItemType Directory -Path $game,$artifacts -Force|Out-Null
    Set-Content -LiteralPath (Join-Path $game 'GTA5.exe') -Value fixture
    foreach($name in @('ScriptHookVDotNet.asi','ScriptHookVDotNet.ini','ScriptHookVDotNet2.dll','ScriptHookVDotNet3.dll','NativeUI.dll','iFruitAddon2.dll','Dealien_ForeverTogether.dll','Dealien_ForeverTogether.ini')){Set-Content -LiteralPath (Join-Path $artifacts $name)-Value $name}
    $plan=New-GridGtaDeploymentPlan -GameRoot $game -ArtifactRoot $artifacts
    Assert-Grid ($plan.status -eq 'ReadyForReview') 'Complete artifacts must create a reviewable plan.'
    Assert-Grid (-not(Test-Path -LiteralPath (Join-Path $game 'scripts'))) 'Planning must not create the scripts directory.'
    $capability=@([pscustomobject]@{capabilityId='grid.game.grandtheftautov.setup-deployment.execute';capabilityVersion='1.0.0';adapterId='GrandTheftAutoV';adapterVersion='1'})
    $binding=New-GridAuthorizationSemanticBinding -ActorId actor.fixture -SessionId session.fixture -WorkspaceId workspace.fixture -RequestId request.fixture -SubmissionId submission.fixture `
      -EnvelopeSha256 ('A'*64)-PlanSha256 ('B'*64)-ScopeSha256 ('C'*64)-ProposalOrSpecificationId $plan.specificationId -ProposalOrSpecificationSha256 $plan.specificationSha256 `
      -Capabilities $capability -Targets $plan.targets -NormalizedInput $plan.operations
    $issued=New-GridAuthorizationGrant -StoreRoot $store -ReviewId review.fixture -AuthorityClass Mutation -SemanticBinding $binding
    $result=Invoke-GridAuthorizedGtaDeployment -Plan $plan -AuthorizationGrantId $issued.Grant.grantId -AuthorizationSecret $issued.AuthorizationSecret -AuthorizationStoreRoot $store -SemanticBinding $binding -RollbackRoot $rollback -Confirm:$false
    Assert-Grid ($result.status -eq 'Verified') 'Authorized deployment must verify.'
    Assert-Grid (Test-Path -LiteralPath (Join-Path $game 'scripts\Dealien_ForeverTogether.dll')) 'Forever Together must deploy to scripts.'
    Assert-Grid (@(Get-Content -LiteralPath (Join-Path $game 'ScriptHookVDotNet.ini')|Where-Object{$_ -eq 'ScriptTimeoutThreshold=60000'}).Count -eq 1) 'Required timeout must be applied exactly once.'
    $replayed=$false;try{Invoke-GridAuthorizedGtaDeployment -Plan (New-GridGtaDeploymentPlan -GameRoot $game -ArtifactRoot $artifacts) -AuthorizationGrantId $issued.Grant.grantId -AuthorizationSecret $issued.AuthorizationSecret -AuthorizationStoreRoot $store -SemanticBinding $binding -RollbackRoot $rollback -Confirm:$false|Out-Null}catch{$replayed=$_.Exception.Message -match 'Replay|Stale|Binding'}
    Assert-Grid $replayed 'A consumed or stale grant must not execute again.'
    Write-Host 'PASS: GTA deployment is recipe-driven, reviewable, one-use authorized, verified, and rollback-capable.'
}finally{Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue}
