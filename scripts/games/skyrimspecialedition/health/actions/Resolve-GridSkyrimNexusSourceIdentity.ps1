#requires -Version 5.1

<#
.SYNOPSIS
Resolves MO2 archive-name claims to exact Nexus Mods file identifiers.
.DESCRIPTION
Queries only the fixed Nexus Mods files endpoint through an opaque protected
credential handle.  Results bind a provider game, mod ID, file ID, version,
and exact archive leaf.  Nexus's v1 file-list response reports rounded KiB,
so this capability deliberately leaves exact byte size and archive SHA-256
unresolved until acquisition observes the bytes.
#>

function ConvertTo-GridSkyrimNexusGameDomain {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$GameName)
    $value = $GameName.Trim().ToLowerInvariant()
    $resolved = switch ($value) {
        'skyrimse' { 'skyrimspecialedition' }
        'skyrim special edition' { 'skyrimspecialedition' }
        'game.skyrim-special-edition' { 'skyrimspecialedition' }
        'skyrimspecialedition' { 'skyrimspecialedition' }
        default { $value }
    }
    if ($resolved -notmatch '^[a-z0-9][a-z0-9-]{0,63}$') {
        throw "NexusGameDomainInvalid: '$GameName' is not a supported provider game domain."
    }
    $resolved
}

function Get-GridSkyrimNexusValue {
    param([AllowNull()][object]$InputObject, [Parameter(Mandatory)][string[]]$Names)
    if ($null -eq $InputObject) { return $null }
    foreach ($name in $Names) {
        $property = $InputObject.PSObject.Properties[$name]
        if ($property) { return $property.Value }
    }
    $null
}

function Get-GridSkyrimNexusJsonSha256 {
    param([Parameter(Mandatory)]$Value)
    if (Get-Command Get-GridCanonicalJsonSha256 -ErrorAction SilentlyContinue) {
        return Get-GridCanonicalJsonSha256 -InputObject $Value
    }
    $json = $Value | ConvertTo-Json -Depth 40 -Compress
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        ([BitConverter]::ToString($algorithm.ComputeHash([Text.UTF8Encoding]::new($false).GetBytes($json)))).Replace('-', '')
    }
    finally { $algorithm.Dispose() }
}

function Get-GridSkyrimNexusFileIndex {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$GameName,
        [Parameter(Mandatory)][ValidateRange(1, 2147483647)][long]$NexusModId,
        [Parameter(Mandatory)][string]$CredentialHandle,
        [Parameter(Mandatory)][scriptblock]$CredentialResolver,
        [scriptblock]$HttpResponseFactory,
        [ValidateRange(1, 120)][int]$TimeoutSeconds = 20
    )
    if (-not (Get-Command Get-GridSkyrimProtectedCredentialHandleState -ErrorAction SilentlyContinue)) {
        . (Join-Path $PSScriptRoot 'Invoke-GridSkyrimArtifactAcquisition.ps1')
    }
    $credential = Get-GridSkyrimProtectedCredentialHandleState -CredentialHandle $CredentialHandle -CredentialResolver $CredentialResolver
    if ([string]$credential.status -ne 'Available') {
        return [pscustomobject][ordered]@{
            schemaVersion = 1; status = 'AuthenticationRequired'; provider = 'Nexus'; gameId = ConvertTo-GridSkyrimNexusGameDomain $GameName
            modId = [string]$NexusModId; endpoint = $null; credentialHandle = $CredentialHandle; files = @()
            detail = [string]$credential.reason; evidenceSha256 = $null
        }
    }

    $gameId = ConvertTo-GridSkyrimNexusGameDomain $GameName
    $endpoint = [uri]("https://api.nexusmods.com/v1/games/{0}/mods/{1}/files" -f $gameId, $NexusModId)
    $headers = @{}
    foreach ($key in @($credential.headers.Keys)) { $headers[[string]$key] = [string]$credential.headers[$key] }
    $headers['Application-Name'] = 'Grid'
    $headers['Application-Version'] = '0.1.5'

    try {
        $response = if ($HttpResponseFactory) {
            & $HttpResponseFactory $endpoint $headers $TimeoutSeconds
        }
        else {
            Invoke-RestMethod -Method Get -Uri $endpoint -Headers $headers -TimeoutSec $TimeoutSeconds -ErrorAction Stop
        }
    }
    catch {
        return [pscustomobject][ordered]@{
            schemaVersion = 1; status = 'ProviderRequestFailed'; provider = 'Nexus'; gameId = $gameId
            modId = [string]$NexusModId; endpoint = $endpoint.AbsoluteUri; credentialHandle = $CredentialHandle; files = @()
            detail = $_.Exception.Message; evidenceSha256 = $null
        }
    }

    $rawFiles = Get-GridSkyrimNexusValue $response @('files','Files')
    if ($null -eq $rawFiles) {
        return [pscustomobject][ordered]@{
            schemaVersion = 1; status = 'ProviderResponseInvalid'; provider = 'Nexus'; gameId = $gameId
            modId = [string]$NexusModId; endpoint = $endpoint.AbsoluteUri; credentialHandle = $CredentialHandle; files = @()
            detail = 'The provider response omitted the files collection.'; evidenceSha256 = $null
        }
    }

    $files = New-Object Collections.Generic.List[object]
    foreach ($record in @($rawFiles)) {
        $fileIdValue = Get-GridSkyrimNexusValue $record @('file_id','fileId')
        $fileId = 0L
        $fileName = [string](Get-GridSkyrimNexusValue $record @('file_name','fileName'))
        if (-not [long]::TryParse([string]$fileIdValue, [ref]$fileId) -or $fileId -le 0 -or
            [string]::IsNullOrWhiteSpace($fileName) -or [IO.Path]::GetFileName($fileName) -cne $fileName -or
            $fileName.IndexOfAny([char[]]@(0, 9, 10, 13)) -ge 0) { continue }
        $sizeKbValue = Get-GridSkyrimNexusValue $record @('size_kb','sizeKb','size')
        $sizeKb = 0L
        [void][long]::TryParse([string]$sizeKbValue, [ref]$sizeKb)
        $files.Add([pscustomobject][ordered]@{
            fileId = [string]$fileId
            fileName = $fileName
            displayName = [string](Get-GridSkyrimNexusValue $record @('name','displayName'))
            version = [string](Get-GridSkyrimNexusValue $record @('version','mod_version','modVersion'))
            category = [string](Get-GridSkyrimNexusValue $record @('category_name','categoryName'))
            sizeKilobytesClaim = if ($sizeKb -gt 0) { $sizeKb } else { $null }
            exactSizeBytes = $null
            exactSizeStatus = 'UnresolvedRoundedProviderValue'
            uploadedTimestamp = Get-GridSkyrimNexusValue $record @('uploaded_timestamp','uploadedTimestamp')
        })
    }
    $ordered = @($files.ToArray() | Sort-Object { [long]$_.fileId }, fileName)
    $unsigned = [pscustomobject][ordered]@{
        schemaVersion = 1; status = 'Observed'; provider = 'Nexus'; gameId = $gameId; modId = [string]$NexusModId
        endpoint = $endpoint.AbsoluteUri; credentialHandle = $CredentialHandle; files = $ordered
        detail = $null
    }
    $unsigned | Add-Member -NotePropertyName evidenceSha256 -NotePropertyValue (Get-GridSkyrimNexusJsonSha256 $unsigned)
    $unsigned
}

function Resolve-GridSkyrimNexusArchiveClaim {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$FileIndex,
        [Parameter(Mandatory)][string]$ClaimedLeafName
    )
    if ([IO.Path]::GetFileName($ClaimedLeafName) -cne $ClaimedLeafName -or
        [string]::IsNullOrWhiteSpace($ClaimedLeafName)) {
        throw 'ArchiveClaimInvalid: the Nexus archive claim must be one exact leaf name.'
    }
    if ([string]$FileIndex.status -ne 'Observed') {
        return [pscustomobject][ordered]@{
            status = [string]$FileIndex.status; claimedLeafName = $ClaimedLeafName; sourceLocator = $null
            candidateCount = 0; evidenceSha256 = [string]$FileIndex.evidenceSha256; detail = [string]$FileIndex.detail
        }
    }
    $matches = @($FileIndex.files | Where-Object {
        [string]::Equals([string]$_.fileName, $ClaimedLeafName, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($matches.Count -ne 1) {
        return [pscustomobject][ordered]@{
            status = if ($matches.Count -eq 0) { 'ExactArchiveClaimNotFound' } else { 'ExactArchiveClaimAmbiguous' }
            claimedLeafName = $ClaimedLeafName; sourceLocator = $null; candidateCount = $matches.Count
            evidenceSha256 = [string]$FileIndex.evidenceSha256
            detail = if ($matches.Count -eq 0) { 'No provider file has the exact claimed archive leaf.' } else { 'Multiple provider files have the same case-insensitive archive leaf.' }
        }
    }
    $match = $matches[0]
    [pscustomobject][ordered]@{
        status = 'ExactSourceLocatorResolved'; claimedLeafName = $ClaimedLeafName
        sourceLocator = [pscustomobject][ordered]@{
            provider = 'Nexus'; gameId = [string]$FileIndex.gameId; modId = [string]$FileIndex.modId
            fileId = [string]$match.fileId; version = [string]$match.version; fileName = [string]$match.fileName
            sizeKilobytesClaim = $match.sizeKilobytesClaim; exactSizeBytes = $null
            exactSizeStatus = 'PendingAcquisitionObservation'
            fileMetadataEndpoint = "https://api.nexusmods.com/v1/games/$($FileIndex.gameId)/mods/$($FileIndex.modId)/files/$($match.fileId)"
            downloadLinkEndpoint = "https://api.nexusmods.com/v1/games/$($FileIndex.gameId)/mods/$($FileIndex.modId)/files/$($match.fileId)/download_link"
        }
        candidateCount = 1; evidenceSha256 = [string]$FileIndex.evidenceSha256
        detail = 'Provider file ID and exact leaf are resolved; exact bytes, size, and SHA-256 remain untrusted until acquisition.'
    }
}

function Resolve-GridSkyrimNexusRecoveryPlanSources {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$RecoveryPlan,
        [string]$CredentialHandle,
        [scriptblock]$CredentialResolver,
        [scriptblock]$HttpResponseFactory,
        [ValidateRange(1, 120)][int]$TimeoutSeconds = 20
    )
    $claims = @{}
    $unresolvedClaimCount = 0
    foreach ($component in @($RecoveryPlan.components | Sort-Object pluginName)) {
        foreach ($candidate in @($component.lineageCandidates | Sort-Object name)) {
            if ([string]$candidate.repository -ine 'Nexus' -or $null -eq $candidate.nexusModId) { continue }
            $leaf = [string]$candidate.archiveClaim.claimedLeafName
            if ([string]::IsNullOrWhiteSpace($leaf)) { $unresolvedClaimCount++; continue }
            $game = ConvertTo-GridSkyrimNexusGameDomain ([string]$candidate.nexusGameName)
            $providerModId = 0L
            if (-not [long]::TryParse([string]$candidate.nexusModId, [ref]$providerModId) -or $providerModId -le 0) { $unresolvedClaimCount++; continue }
            $key = "$game|$providerModId|$($leaf.ToUpperInvariant())"
            if (-not $claims.ContainsKey($key)) {
                $claims[$key] = [pscustomobject][ordered]@{
                    gameId = $game; nexusModId = $providerModId; claimedLeafName = $leaf
                    candidateMods = New-Object Collections.Generic.List[string]
                    consumingPlugins = New-Object Collections.Generic.List[string]
                }
            }
            $claims[$key].candidateMods.Add([string]$candidate.name)
            $claims[$key].consumingPlugins.Add([string]$component.pluginName)
        }
    }

    if ([string]::IsNullOrWhiteSpace($CredentialHandle) -or -not $CredentialResolver) {
        return [pscustomobject][ordered]@{
            schemaVersion = 1; planId = [string]$RecoveryPlan.planId; planSha256 = [string]$RecoveryPlan.planSha256
            status = 'AuthenticationRequired'; summary = [pscustomobject][ordered]@{
                archiveClaimCount = $claims.Count; exactSourceLocatorCount = 0; unresolvedArchiveClaimCount = $unresolvedClaimCount
                notFoundCount = 0; ambiguousCount = 0; providerFailureCount = 0
            }
            resolutions = @(); credentialHandle = $CredentialHandle; mutationPerformed = $false
            detail = 'An opaque OS-protected Nexus credential handle is required; no provider request was made.'
        }
    }

    $indexes = @{}
    $resolutions = New-Object Collections.Generic.List[object]
    foreach ($claim in @($claims.Values | Sort-Object gameId, nexusModId, claimedLeafName)) {
        $indexKey = "$($claim.gameId)|$($claim.nexusModId)"
        if (-not $indexes.ContainsKey($indexKey)) {
            $indexes[$indexKey] = Get-GridSkyrimNexusFileIndex -GameName $claim.gameId -NexusModId $claim.nexusModId `
                -CredentialHandle $CredentialHandle -CredentialResolver $CredentialResolver -HttpResponseFactory $HttpResponseFactory -TimeoutSeconds $TimeoutSeconds
        }
        $resolution = Resolve-GridSkyrimNexusArchiveClaim -FileIndex $indexes[$indexKey] -ClaimedLeafName $claim.claimedLeafName
        $resolutions.Add([pscustomobject][ordered]@{
            status = [string]$resolution.status; gameId = [string]$claim.gameId; nexusModId = [string]$claim.nexusModId
            claimedLeafName = [string]$claim.claimedLeafName
            candidateMods = @($claim.candidateMods | Sort-Object -Unique)
            consumingPlugins = @($claim.consumingPlugins | Sort-Object -Unique)
            sourceLocator = $resolution.sourceLocator; providerEvidenceSha256 = [string]$resolution.evidenceSha256
            detail = [string]$resolution.detail
        })
    }
    $items = $resolutions.ToArray()
    $summary = [pscustomobject][ordered]@{
        archiveClaimCount = $claims.Count
        exactSourceLocatorCount = @($items | Where-Object status -eq 'ExactSourceLocatorResolved').Count
        unresolvedArchiveClaimCount = $unresolvedClaimCount
        notFoundCount = @($items | Where-Object status -eq 'ExactArchiveClaimNotFound').Count
        ambiguousCount = @($items | Where-Object status -eq 'ExactArchiveClaimAmbiguous').Count
        providerFailureCount = @($items | Where-Object { [string]$_.status -in @('AuthenticationRequired','ProviderRequestFailed','ProviderResponseInvalid') }).Count
    }
    $status = if ($summary.providerFailureCount -gt 0) { 'ProviderResolutionIncomplete' }
        elseif ($summary.ambiguousCount -gt 0) { 'SourceIdentityConflict' }
        elseif ($summary.notFoundCount -gt 0 -or $summary.unresolvedArchiveClaimCount -gt 0) { 'SourceIdentityIncomplete' }
        else { 'ExactSourceLocatorsResolved' }
    $unsigned = [pscustomobject][ordered]@{
        schemaVersion = 1; planId = [string]$RecoveryPlan.planId; planSha256 = [string]$RecoveryPlan.planSha256
        status = $status; summary = $summary; resolutions = @($items); credentialHandle = $CredentialHandle
        mutationPerformed = $false
        detail = 'This result resolves provider locators only. No archive was downloaded and no MO2 or game file was changed.'
    }
    $unsigned | Add-Member -NotePropertyName resolutionSha256 -NotePropertyValue (Get-GridSkyrimNexusJsonSha256 $unsigned)
    $unsigned
}
