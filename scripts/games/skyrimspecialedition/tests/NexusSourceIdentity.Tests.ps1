#requires -Version 5.1

$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $gameRoot 'health\actions\Resolve-GridSkyrimNexusSourceIdentity.ps1')

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }
function Assert-Throws([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    try { & $Action; throw "$Message Expected '$Pattern'." }
    catch { if ($_.Exception.Message -notmatch $Pattern) { throw "$Message Unexpected: $($_.Exception.Message)" } }
}

$script:requestCount = 0
$credentialResolver = {
    param($Handle)
    Assert-Equal 'os-protected:nexus-fixture' $Handle 'Only the selected protected handle may be resolved.'
    @{ apikey = 'fixture-secret-never-returned' }
}
$responseFactory = {
    param($Uri, $Headers, $TimeoutSeconds)
    $script:requestCount++
    Assert-Equal 'api.nexusmods.com' $Uri.Host 'Provider requests must use the fixed Nexus API host.'
    Assert-Equal '/v1/games/skyrimspecialedition/mods/29194/files' $Uri.AbsolutePath 'The provider route must bind the exact game and mod ID.'
    Assert-Equal 'fixture-secret-never-returned' $Headers.apikey 'The protected credential must be supplied only to the request boundary.'
    Assert-Equal 20 $TimeoutSeconds 'The bounded default timeout must be preserved.'
    [pscustomobject]@{ files = @(
        [pscustomobject]@{ file_id=100; file_name='3DNPC-base.7z'; name='Main'; version='4.5'; category_name='MAIN'; size_kb=200; uploaded_timestamp=1 },
        [pscustomobject]@{ file_id=101; file_name='3DNPC-update.7z'; name='Update'; version='4.54'; category_name='UPDATE'; size_kb=25; uploaded_timestamp=2 },
        [pscustomobject]@{ file_id=0; file_name='../unsafe.7z'; name='Unsafe'; version='0'; size_kb=1 }
    ) }
}

$index = Get-GridSkyrimNexusFileIndex -GameName SkyrimSE -NexusModId 29194 -CredentialHandle 'os-protected:nexus-fixture' `
    -CredentialResolver $credentialResolver -HttpResponseFactory $responseFactory
Assert-Equal 'Observed' $index.status 'An authenticated fixed-host file index must be observed.'
Assert-Equal 'skyrimspecialedition' $index.gameId 'MO2 game names must map to the Nexus domain deterministically.'
Assert-Equal 2 @($index.files).Count 'Malformed provider file records must be excluded.'
Assert-True ([string]$index.evidenceSha256 -match '^[A-F0-9]{64}$') 'Provider evidence must receive a deterministic SHA-256.'
Assert-True (($index | ConvertTo-Json -Depth 20 -Compress) -notmatch 'fixture-secret-never-returned') 'Provider evidence must never serialize the credential.'

$exact = Resolve-GridSkyrimNexusArchiveClaim -FileIndex $index -ClaimedLeafName '3dnpc-UPDATE.7z'
Assert-Equal 'ExactSourceLocatorResolved' $exact.status 'Archive leaf matching is exact and case-insensitive.'
Assert-Equal '101' $exact.sourceLocator.fileId 'The source locator must retain the provider file ID.'
Assert-True ($null -eq $exact.sourceLocator.exactSizeBytes) 'Rounded provider KiB must not be promoted to exact byte evidence.'
Assert-Equal 'PendingAcquisitionObservation' $exact.sourceLocator.exactSizeStatus 'Exact size remains pending until bytes are observed.'

$missing = Resolve-GridSkyrimNexusArchiveClaim -FileIndex $index -ClaimedLeafName 'not-there.7z'
Assert-Equal 'ExactArchiveClaimNotFound' $missing.status 'A filename mismatch must not select a nearby provider file.'
Assert-Throws { Resolve-GridSkyrimNexusArchiveClaim -FileIndex $index -ClaimedLeafName '..\escape.7z' } 'ArchiveClaimInvalid' 'Path-like claims must fail closed.'

$plan = [pscustomobject]@{
    planId='component-recovery-fixture'; planSha256=('A' * 64); components=@(
        [pscustomobject]@{ pluginName='3DNPC.esp'; lineageCandidates=@(
            [pscustomobject]@{ name='Interesting NPCs Base'; repository='Nexus'; nexusGameName='SkyrimSE'; nexusModId=29194; archiveClaim=[pscustomobject]@{claimedLeafName='3DNPC-base.7z'} },
            [pscustomobject]@{ name='Interesting NPCs Update'; repository='Nexus'; nexusGameName='SkyrimSE'; nexusModId=29194; archiveClaim=[pscustomobject]@{claimedLeafName='3DNPC-update.7z'} }
        ) },
        [pscustomobject]@{ pluginName='3DNPC-Patch.esp'; lineageCandidates=@(
            [pscustomobject]@{ name='Interesting NPCs Update'; repository='Nexus'; nexusGameName='SkyrimSE'; nexusModId=29194; archiveClaim=[pscustomobject]@{claimedLeafName='3DNPC-update.7z'} }
        ) }
    )
}
$script:requestCount = 0
$resolved = Resolve-GridSkyrimNexusRecoveryPlanSources -RecoveryPlan $plan -CredentialHandle 'os-protected:nexus-fixture' `
    -CredentialResolver $credentialResolver -HttpResponseFactory $responseFactory
Assert-Equal 'ExactSourceLocatorsResolved' $resolved.status 'All exact fixture archive claims must resolve.'
Assert-Equal 2 $resolved.summary.archiveClaimCount 'Duplicate component claims must collapse to one provider source request.'
Assert-Equal 2 $resolved.summary.exactSourceLocatorCount 'Both distinct archive claims must retain exact source locators.'
Assert-Equal 1 $script:requestCount 'Files for the same Nexus mod must be fetched exactly once.'
Assert-True (($resolved | ConvertTo-Json -Depth 30 -Compress) -notmatch 'fixture-secret-never-returned') 'The aggregate result must remain credential-free.'
Assert-True (-not $resolved.mutationPerformed) 'Source resolution must be read-only.'

$noCredential = Resolve-GridSkyrimNexusRecoveryPlanSources -RecoveryPlan $plan
Assert-Equal 'AuthenticationRequired' $noCredential.status 'A missing protected credential must return an actionable state without network access.'
Assert-Equal 0 $noCredential.summary.exactSourceLocatorCount 'No identity may be claimed without provider evidence.'

$ambiguousIndex = [pscustomobject]@{ status='Observed'; evidenceSha256=('B' * 64); gameId='skyrimspecialedition'; modId='1'; files=@(
    [pscustomobject]@{fileId='1';fileName='same.7z';version='1'},[pscustomobject]@{fileId='2';fileName='SAME.7z';version='2'}) }
$ambiguous = Resolve-GridSkyrimNexusArchiveClaim -FileIndex $ambiguousIndex -ClaimedLeafName 'same.7z'
Assert-Equal 'ExactArchiveClaimAmbiguous' $ambiguous.status 'Case-colliding provider file names must fail closed.'

'Nexus source identity checks passed.'
