#requires -Version 5.1

<#
.SYNOPSIS
Refreshes exact cataloged Skyrim gameplay-capability candidates from Nexus Mods.
.DESCRIPTION
The caller supplies one explicit capability ID and an opaque OS-protected Nexus
credential handle. Candidate game/mod identities come only from the versioned
repository catalog. The collector queries fixed Nexus API metadata endpoints,
records credential-free bounded observations, and never downloads or changes
MO2, the game, or a profile.
#>

function Get-GridSkyrimNexusCapabilityObservation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidatePattern('^grid\.capability\.[a-z0-9]+(?:[.-][a-z0-9]+)*$')][string]$CapabilityId,
        [string]$CredentialHandle,
        [scriptblock]$CredentialResolver,
        [scriptblock]$HttpResponseFactory,
        [ValidateRange(1, 120)][int]$TimeoutSeconds = 20,
        [ValidateRange(1, 168)][int]$FreshnessHours = 24,
        [DateTimeOffset]$AsOfUtc = [DateTimeOffset]::UtcNow,
        [string]$CatalogPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'catalogs\community-capabilities.v1.json')
    )

    if (-not (Get-Command Read-GridSkyrimGameplayCapabilityCatalog -ErrorAction SilentlyContinue)) {
        . (Join-Path $PSScriptRoot 'Get-GridSkyrimGameplayCapabilityAssessment.ps1')
    }
    if (-not (Get-Command Get-GridSkyrimProtectedCredentialHandleState -ErrorAction SilentlyContinue)) {
        . (Join-Path (Split-Path -Parent $PSScriptRoot) 'actions\Invoke-GridSkyrimArtifactAcquisition.ps1')
    }

    $loaded = Read-GridSkyrimGameplayCapabilityCatalog -LiteralPath $CatalogPath
    $matches = @($loaded.Catalog.capabilities | Where-Object { [string]$_.capabilityId -ceq $CapabilityId })
    if ($matches.Count -ne 1) { throw "GameplayCapabilityNotCataloged: '$CapabilityId'." }
    $catalogCapability = $matches[0]
    $now = $AsOfUtc.ToUniversalTime()
    $expires = $now.AddHours($FreshnessHours)
    $credential = Get-GridSkyrimProtectedCredentialHandleState -CredentialHandle $CredentialHandle -CredentialResolver $CredentialResolver

    if ([string]$credential.status -ne 'Available') {
        $unsigned = [pscustomobject][ordered]@{
            schemaVersion = 1; capabilityId = $CapabilityId; status = 'AuthenticationRequired'
            observedAtUtc = $now.ToString('o'); expiresAtUtc = $expires.ToString('o'); candidates = @()
            credentialHandle = if ([string]::IsNullOrWhiteSpace($CredentialHandle)) { $null } else { $CredentialHandle }
            mutationPerformed = $false
        }
        $unsigned | Add-Member -NotePropertyName observationSha256 -NotePropertyValue (Get-GridSkyrimCapabilityJsonSha256 $unsigned)
        return $unsigned
    }

    $headers = @{}
    foreach ($key in @($credential.headers.Keys)) { $headers[[string]$key] = [string]$credential.headers[$key] }
    $headers['Application-Name'] = 'Grid'
    $headers['Application-Version'] = '0.1.5'
    $observations = New-Object Collections.Generic.List[object]

    foreach ($candidate in @($catalogCapability.communityCandidates | Sort-Object candidateId)) {
        if ([string]$candidate.provider -cne 'Nexus Mods' -or $null -eq $candidate.PSObject.Properties['providerLocator']) {
            throw "GameplayCapabilityCatalogInvalid: candidate '$($candidate.candidateId)' has no supported provider locator."
        }
        $gameId = [string]$candidate.providerLocator.gameId
        $modId = 0L
        if ($gameId -cne 'skyrimspecialedition' -or -not [long]::TryParse([string]$candidate.providerLocator.modId, [ref]$modId) -or $modId -le 0) {
            throw "GameplayCapabilityCatalogInvalid: candidate '$($candidate.candidateId)' has an invalid Nexus locator."
        }
        $endpoint = [uri]("https://api.nexusmods.com/v1/games/{0}/mods/{1}.json" -f $gameId, $modId)
        try {
            $response = if ($HttpResponseFactory) { & $HttpResponseFactory $endpoint $headers $TimeoutSeconds }
                else { Invoke-RestMethod -Method Get -Uri $endpoint -Headers $headers -TimeoutSec $TimeoutSeconds -ErrorAction Stop }
        }
        catch {
            $observations.Add([pscustomobject][ordered]@{
                candidateId = [string]$candidate.candidateId; provider = 'Nexus Mods'; gameId = $gameId; modId = $modId
                uri = [string]$candidate.uri; status = 'ProviderRequestFailed'; version = $null; updatedAtUtc = $null
                evidenceId = $null; detail = $_.Exception.Message
            })
            continue
        }

        $responseModId = Get-GridSkyrimCapabilityValue $response 'mod_id'
        if ($null -eq $responseModId) { $responseModId = Get-GridSkyrimCapabilityValue $response 'modId' }
        $parsedResponseModId = 0L
        $version = [string](Get-GridSkyrimCapabilityValue $response 'version')
        if (-not [long]::TryParse([string]$responseModId, [ref]$parsedResponseModId) -or $parsedResponseModId -ne $modId -or [string]::IsNullOrWhiteSpace($version)) {
            $observations.Add([pscustomobject][ordered]@{
                candidateId = [string]$candidate.candidateId; provider = 'Nexus Mods'; gameId = $gameId; modId = $modId
                uri = [string]$candidate.uri; status = 'ProviderResponseInvalid'; version = $null; updatedAtUtc = $null
                evidenceId = $null; detail = 'The provider response did not bind the requested mod ID and a non-empty current version.'
            })
            continue
        }

        $updatedValue = Get-GridSkyrimCapabilityValue $response 'updated_timestamp'
        if ($null -eq $updatedValue) { $updatedValue = Get-GridSkyrimCapabilityValue $response 'updatedTimestamp' }
        $updatedAt = $null
        $epoch = 0L
        if ([long]::TryParse([string]$updatedValue, [ref]$epoch) -and $epoch -gt 0) {
            try { $updatedAt = [DateTimeOffset]::FromUnixTimeSeconds($epoch).ToUniversalTime().ToString('o') } catch { $updatedAt = $null }
        }
        $identity = [pscustomobject][ordered]@{
            candidateId = [string]$candidate.candidateId; provider = 'Nexus Mods'; gameId = $gameId; modId = $modId
            uri = [string]$candidate.uri; status = 'Observed'; version = $version; updatedAtUtc = $updatedAt
            observedAtUtc = $now.ToString('o'); endpoint = $endpoint.AbsoluteUri
        }
        $observations.Add([pscustomobject][ordered]@{
            candidateId = [string]$candidate.candidateId; provider = 'Nexus Mods'; gameId = $gameId; modId = $modId
            uri = [string]$candidate.uri; status = 'Observed'; version = $version; updatedAtUtc = $updatedAt
            evidenceId = 'sha256:' + (Get-GridSkyrimCapabilityJsonSha256 $identity); detail = $null
        })
    }

    $items = @($observations.ToArray())
    $observedCount = @($items | Where-Object status -eq 'Observed').Count
    $invalidCount = @($items | Where-Object status -eq 'ProviderResponseInvalid').Count
    $failedCount = @($items | Where-Object status -eq 'ProviderRequestFailed').Count
    $status = if ($items.Count -gt 0 -and $observedCount -eq $items.Count) { 'Observed' }
        elseif ($observedCount -gt 0) { 'Partial' }
        elseif ($invalidCount -gt 0 -and $failedCount -eq 0) { 'ProviderResponseInvalid' }
        else { 'ProviderRequestFailed' }
    $unsigned = [pscustomobject][ordered]@{
        schemaVersion = 1; capabilityId = $CapabilityId; status = $status
        observedAtUtc = $now.ToString('o'); expiresAtUtc = $expires.ToString('o'); candidates = $items
        credentialHandle = $CredentialHandle; mutationPerformed = $false
    }
    $unsigned | Add-Member -NotePropertyName observationSha256 -NotePropertyValue (Get-GridSkyrimCapabilityJsonSha256 $unsigned)
    $unsigned
}
