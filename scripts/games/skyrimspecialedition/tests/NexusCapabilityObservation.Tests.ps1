#requires -Version 5.1

$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent $PSScriptRoot
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $gameRoot)
Import-Module (Join-Path $scriptsRoot 'health\Grid.Health.psm1') -Force
Import-Module (Join-Path $gameRoot 'health\Grid.Health.Skyrim.psm1') -Force

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }

$script:requestCount = 0
$credentialResolver = {
    param($Handle)
    Assert-Equal 'os-protected:nexus-capability-fixture' $Handle 'Only the selected protected handle may be resolved.'
    @{ apikey = 'fixture-secret-never-returned' }
}
$responseFactory = {
    param($Uri, $Headers, $TimeoutSeconds)
    $script:requestCount++
    Assert-Equal 'api.nexusmods.com' $Uri.Host 'Provider requests must use the fixed Nexus API host.'
    Assert-Equal '/v1/games/skyrimspecialedition/mods/180015.json' $Uri.AbsolutePath 'The provider route must bind the exact catalog locator.'
    Assert-Equal 'fixture-secret-never-returned' $Headers.apikey 'The protected credential must exist only at the request boundary.'
    Assert-Equal 20 $TimeoutSeconds 'The bounded timeout must be preserved.'
    [pscustomobject]@{ mod_id=180015; version='0.4.6'; updated_timestamp=1782101280 }
}

$observedAt = [DateTimeOffset]::Parse('2026-09-12T00:00:00Z')
$observation = Get-GridSkyrimNexusCapabilityObservation -CapabilityId 'grid.capability.equipment.multiple-rings' `
    -CredentialHandle 'os-protected:nexus-capability-fixture' -CredentialResolver $credentialResolver `
    -HttpResponseFactory $responseFactory -AsOfUtc $observedAt
Assert-Equal 'Observed' $observation.status 'Every exact provider candidate must be observed.'
Assert-Equal 1 @($observation.candidates).Count 'The exact selected capability has one cataloged candidate.'
Assert-Equal '0.4.6' $observation.candidates[0].version 'The current provider version must be preserved.'
Assert-True ([string]$observation.candidates[0].evidenceId -match '^sha256:[A-F0-9]{64}$') 'Each observed candidate must have bounded evidence identity.'
Assert-True ([string]$observation.observationSha256 -match '^[A-F0-9]{64}$') 'The aggregate observation must be content-addressed.'
Assert-True (-not $observation.mutationPerformed) 'Provider observation must remain read-only.'
Assert-Equal 1 $script:requestCount 'Each exact candidate must be queried once.'
Assert-True (($observation | ConvertTo-Json -Depth 20 -Compress) -notmatch 'fixture-secret-never-returned') 'Provider evidence must not serialize credentials.'

$authenticationRequired = Get-GridSkyrimNexusCapabilityObservation -CapabilityId 'grid.capability.equipment.multiple-rings' -AsOfUtc $observedAt
Assert-Equal 'AuthenticationRequired' $authenticationRequired.status 'A missing protected credential must fail closed without a provider request.'
Assert-Equal 0 @($authenticationRequired.candidates).Count 'Authentication failure must not fabricate candidates.'

$invalidResponse = Get-GridSkyrimNexusCapabilityObservation -CapabilityId 'grid.capability.equipment.multiple-rings' `
    -CredentialHandle 'os-protected:nexus-capability-fixture' -CredentialResolver $credentialResolver `
    -HttpResponseFactory { param($Uri,$Headers,$TimeoutSeconds) [pscustomobject]@{ mod_id=999; version='9.9.9' } } -AsOfUtc $observedAt
Assert-Equal 'ProviderResponseInvalid' $invalidResponse.status 'A mismatched provider mod identity must fail closed.'

'Skyrim Nexus capability observation checks passed.'
