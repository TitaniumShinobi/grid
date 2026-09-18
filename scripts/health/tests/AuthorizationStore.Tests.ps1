$ErrorActionPreference='Stop'
$healthRoot=Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $healthRoot 'Grid.Health.psm1') -Force
function Assert-True([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
function Assert-Throws([scriptblock]$Action,[string]$Pattern,[string]$Message){try{&$Action;throw "$Message Expected '$Pattern'."}catch{if($_.Exception.Message -notmatch $Pattern){throw "$Message Unexpected: $($_.Exception.Message)"}}}
function New-Binding([string]$Actor='actor.fixture',[string]$Session='session.fixture',[string]$Workspace='workspace.fixture',[string]$Request='request.fixture',[string]$Submission='submission.fixture',[string]$Scope=('C'*64)){
    New-GridAuthorizationSemanticBinding -ActorId $Actor -SessionId $Session -WorkspaceId $Workspace -RequestId $Request -SubmissionId $Submission `
      -EnvelopeSha256 ('A'*64) -PlanSha256 ('B'*64) -ScopeSha256 $Scope -ProposalOrSpecificationId 'proposal.fixture' -ProposalOrSpecificationSha256 ('D'*64) `
      -Capabilities @([pscustomobject]@{capabilityId='grid.fixture.execute';capabilityVersion='1.0.0';adapterId='Fixture';adapterVersion='1'}) -Targets @('target.fixture') -NormalizedInput ([pscustomobject]@{value=1})
}
$root=Join-Path $env:TEMP ('grid-auth-store-'+[guid]::NewGuid().ToString('N'))
try{
  New-Item -ItemType Directory -Path $root -Force|Out-Null
  $binding=New-Binding
  $issued=New-GridAuthorizationGrant -StoreRoot $root -ReviewId 'review.fixture' -AuthorityClass Mutation -SemanticBinding $binding -LifetimeMinutes 15
  Assert-True ($issued.AuthorizationSecret.Length -ge 40) 'Random authorization secret is unexpectedly short.'
  $persisted=Get-Content -LiteralPath $issued.GrantPath -Raw
  Assert-True (-not $persisted.Contains($issued.AuthorizationSecret)) 'Authorization secret must never be persisted.'
  Assert-True ($persisted -match 'PBKDF2-HMAC-SHA1') 'Persisted grant must retain only a protected secret proof.'

  Assert-Throws {Test-GridAuthorizationGrantPreflight -StoreRoot $root -GrantId $issued.Grant.grantId -AuthorizationSecret 'wrong-secret' -ExpectedSemanticBinding $binding|Out-Null} 'AuthorizationSecretInvalid' 'Wrong secret must fail during non-mutating preflight.'
  Assert-True ($null -ne (Test-GridAuthorizationGrantPreflight -StoreRoot $root -GrantId $issued.Grant.grantId -AuthorizationSecret $issued.AuthorizationSecret -ExpectedSemanticBinding $binding)) 'A valid grant must pass non-mutating preflight.'
  Assert-Throws {Enter-GridAuthorizationLease -StoreRoot $root -GrantId $issued.Grant.grantId -AuthorizationSecret 'wrong-secret' -ExpectedSemanticBinding $binding -ConsumerId consumer.one|Out-Null} 'AuthorizationSecretInvalid' 'Wrong secret must refuse.'
  Assert-Throws {Enter-GridAuthorizationLease -StoreRoot $root -GrantId $issued.Grant.grantId -AuthorizationSecret $issued.AuthorizationSecret -ExpectedSemanticBinding (New-Binding -Actor 'actor.other') -ConsumerId consumer.one|Out-Null} 'AuthorizationBindingMismatch' 'Wrong actor must refuse.'
  Assert-Throws {Enter-GridAuthorizationLease -StoreRoot $root -GrantId $issued.Grant.grantId -AuthorizationSecret $issued.AuthorizationSecret -ExpectedSemanticBinding (New-Binding -Session 'session.other') -ConsumerId consumer.one|Out-Null} 'AuthorizationBindingMismatch' 'Wrong session must refuse.'
  Assert-Throws {Enter-GridAuthorizationLease -StoreRoot $root -GrantId $issued.Grant.grantId -AuthorizationSecret $issued.AuthorizationSecret -ExpectedSemanticBinding (New-Binding -Scope ('E'*64)) -ConsumerId consumer.one|Out-Null} 'AuthorizationBindingMismatch' 'Altered scope must refuse.'
  Assert-Throws {Enter-GridAuthorizationLease -StoreRoot $root -GrantId $issued.Grant.grantId -AuthorizationSecret $issued.AuthorizationSecret -ExpectedSemanticBinding (New-Binding -Request 'request.stale') -ConsumerId consumer.one|Out-Null} 'AuthorizationBindingMismatch' 'Stale request context must refuse.'
  $alteredTarget=New-Binding; $alteredTarget.targets=@('target.other')
  Assert-Throws {Enter-GridAuthorizationLease -StoreRoot $root -GrantId $issued.Grant.grantId -AuthorizationSecret $issued.AuthorizationSecret -ExpectedSemanticBinding $alteredTarget -ConsumerId consumer.one|Out-Null} 'AuthorizationBindingMismatch' 'Altered exact targets must refuse.'

  $lease=Enter-GridAuthorizationLease -StoreRoot $root -GrantId $issued.Grant.grantId -AuthorizationSecret $issued.AuthorizationSecret -ExpectedSemanticBinding $binding -ConsumerId consumer.one
  Assert-True ($null -ne $lease -and $null -ne $lease.Lease -and -not [string]::IsNullOrWhiteSpace([string]$lease.Lease.leaseId)) 'Lease acquisition must return the persisted exclusive lease.'
  Assert-True ($lease.Grant.state -eq 'Executing') 'Issued grant must transition to Executing under an exclusive lease.'
  $executing=Read-GridAuthorizationGrant -StoreRoot $root -GrantId $issued.Grant.grantId
  Assert-True ($executing.state -eq 'Executing' -and [string]$executing.lease.leaseId -ceq [string]$lease.Lease.leaseId) 'Executing lease state must be durably persisted before control returns.'
  Assert-Throws {Enter-GridAuthorizationLease -StoreRoot $root -GrantId $issued.Grant.grantId -AuthorizationSecret $issued.AuthorizationSecret -ExpectedSemanticBinding $binding -ConsumerId consumer.two|Out-Null} 'AuthorizationConcurrentConsumer' 'Parallel consumer must refuse while lease executes.'
  $consumption=Complete-GridAuthorizationLease -StoreRoot $root -GrantId $issued.Grant.grantId -LeaseId $lease.Lease.leaseId -Result Consumed -Detail 'fixture'
  Assert-True ($null -ne $consumption -and $consumption.state -eq 'Consumed' -and -not [string]::IsNullOrWhiteSpace([string]$consumption.consumptionId)) 'Lease completion must return the persisted terminal consumption record.'
  $consumed=Read-GridAuthorizationGrant -StoreRoot $root -GrantId $issued.Grant.grantId
  Assert-True ($consumed.state -eq 'Consumed' -and [string]$consumed.consumption.consumptionId -ceq [string]$consumption.consumptionId) 'Consumed state must be durably persisted before control returns.'
  Assert-Throws {Enter-GridAuthorizationLease -StoreRoot $root -GrantId $issued.Grant.grantId -AuthorizationSecret $issued.AuthorizationSecret -ExpectedSemanticBinding $binding -ConsumerId consumer.replay|Out-Null} 'AuthorizationReplayRefused' 'Consumed grant must refuse replay.'

  $expired=New-GridAuthorizationGrant -StoreRoot $root -ReviewId 'review.expired' -AuthorityClass Read -SemanticBinding $binding
  $expiredRecord=Get-Content -LiteralPath $expired.GrantPath -Raw|ConvertFrom-Json;$expiredRecord.expiresAt=[DateTimeOffset]::UtcNow.AddMinutes(-1).ToString('o');Write-GridJsonAtomic -InputObject $expiredRecord -LiteralPath $expired.GrantPath
  Assert-Throws {Enter-GridAuthorizationLease -StoreRoot $root -GrantId $expired.Grant.grantId -AuthorizationSecret $expired.AuthorizationSecret -ExpectedSemanticBinding $binding -ConsumerId consumer.expired|Out-Null} 'AuthorizationExpired' 'Expired grant must refuse.'

  $cloneId='grant-'+[guid]::NewGuid().ToString('N');$cloneDir=Join-Path (Get-GridAuthorizationStoreRoot -StoreRoot $root -Ensure) $cloneId;New-Item -ItemType Directory -Path $cloneDir|Out-Null;Copy-Item -LiteralPath $issued.GrantPath -Destination (Join-Path $cloneDir 'grant.v2.json')
  Assert-Throws {Enter-GridAuthorizationLease -StoreRoot $root -GrantId $cloneId -AuthorizationSecret $issued.AuthorizationSecret -ExpectedSemanticBinding $binding -ConsumerId consumer.clone|Out-Null} 'AuthorizationGrantIdentityMismatch' 'Cloned grant under another identity must refuse.'

  $legacy=[pscustomobject]@{schemaVersion=1;reviewId='legacy';requestId='request-old';submissionId='old';authorizationDigest=('F'*64);approvedAt='2026-01-01T00:00:00Z';mutationAuthorized=$false}
  $compat=Test-GridAuthorizationHistoricalRecord -Record $legacy
  Assert-True ($compat.IsCompatible -and -not $compat.Executable) 'Historical v1 grant must be readable but non-executable.'
  $fixtureRoot=Join-Path $PSScriptRoot 'fixtures\authorization'
  $historicalFixture=Get-Content -LiteralPath (Join-Path $fixtureRoot 'historical-read-grant.v1.json') -Raw|ConvertFrom-Json
  $fixtureCompat=Test-GridAuthorizationHistoricalRecord -Record $historicalFixture
  Assert-True ($fixtureCompat.IsCompatible -and -not $fixtureCompat.Executable) 'Historical fixture must remain readable but non-executable.'
  foreach($schemaName in @('authorization-semantic-binding.v1.schema.json','authorization-review.v2.schema.json','authorization-grant.v2.schema.json','authorization-lease.v1.schema.json','authorization-consumption.v1.schema.json','authorization-failure.v1.schema.json','request-read-authorization.v2.schema.json','tool-evidence-receipt.v2.schema.json','tool-evidence-run.v2.schema.json','repair-transaction-receipt.v2.schema.json')){
    $schema=Get-Content -LiteralPath (Join-Path (Join-Path $healthRoot 'schemas') $schemaName) -Raw|ConvertFrom-Json
    Assert-True ($schema.additionalProperties -eq $false -or $schemaName -eq 'authorization-grant.v2.schema.json') "Authorization/receipt schema '$schemaName' must be closed."
  }
  Write-Host 'PASS: random one-use grants, semantic binding, expiry, concurrency, replay, clone refusal, and historical compatibility.'
}finally{if(Test-Path -LiteralPath $root){Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue}}
