#requires -Version 5.1

<#
.SYNOPSIS
Evaluates one explicit Skyrim gameplay capability against a captured MO2 inventory.
.DESCRIPTION
The caller supplies a capability ID, current profile evidence identity, and
normalized MO2 mod rows. This collector never interprets chat prose, performs
network discovery, selects an installation action, or changes external state.
Repository-curated community observations expire and fail closed when stale.
#>

function Get-GridSkyrimCapabilityValue {
    param([AllowNull()][object]$InputObject, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $InputObject) { return $null }
    if ($InputObject -is [Collections.IDictionary]) {
        foreach ($key in @($InputObject.Keys)) {
            if ([string]::Equals([string]$key, $Name, [StringComparison]::OrdinalIgnoreCase)) { return $InputObject[$key] }
        }
        return $null
    }
    $property = @($InputObject.PSObject.Properties | Where-Object { [string]::Equals($_.Name, $Name, [StringComparison]::OrdinalIgnoreCase) })
    if ($property.Count -eq 1) { return $property[0].Value }
    $null
}

function Get-GridSkyrimCapabilityJsonSha256 {
    param([Parameter(Mandatory)]$Value)
    if (Get-Command Get-GridCanonicalJsonSha256 -ErrorAction SilentlyContinue) {
        return Get-GridCanonicalJsonSha256 -InputObject $Value
    }
    $json = $Value | ConvertTo-Json -Depth 40 -Compress
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($algorithm.ComputeHash([Text.UTF8Encoding]::new($false).GetBytes($json)))).Replace('-', '') }
    finally { $algorithm.Dispose() }
}

function ConvertTo-GridSkyrimCapabilityBoolean {
    param([AllowNull()][object]$Value)
    if ($Value -is [bool]) { return [bool]$Value }
    if ($null -eq $Value) { return $false }
    [string]$Value -match '^(?i:true|1|yes|enabled)$'
}

function ConvertTo-GridSkyrimCapabilityName {
    param([AllowNull()][object]$Value)
    if ($null -eq $Value) { return '' }
    (([string]$Value).Trim() -replace '\s+', ' ').ToLowerInvariant()
}

function Test-GridSkyrimCapabilityModMatch {
    param([Parameter(Mandatory)]$Mod, [Parameter(Mandatory)]$Identity)
    $expectedNexusId = Get-GridSkyrimCapabilityValue $Identity 'nexusModId'
    $actualNexusId = Get-GridSkyrimCapabilityValue $Mod 'nexusModId'
    if ($null -ne $expectedNexusId -and $null -ne $actualNexusId -and [string]$expectedNexusId -eq [string]$actualNexusId) { return $true }
    $name = ConvertTo-GridSkyrimCapabilityName (Get-GridSkyrimCapabilityValue $Mod 'name')
    if ([string]::IsNullOrWhiteSpace($name)) { return $false }
    foreach ($alias in @(Get-GridSkyrimCapabilityValue $Identity 'aliases')) {
        if ($name -ceq (ConvertTo-GridSkyrimCapabilityName $alias)) { return $true }
    }
    $false
}

function ConvertTo-GridSkyrimComparableVersion {
    param([AllowNull()][object]$Value)
    if ([string]::IsNullOrWhiteSpace([string]$Value)) { return $null }
    $match = [regex]::Match([string]$Value, '(?<!\d)(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?')
    if (-not $match.Success) { return $null }
    $parts = @(1..4 | ForEach-Object { if ($match.Groups[$_].Success) { [int]$match.Groups[$_].Value } else { 0 } })
    [Version]::new($parts[0], $parts[1], $parts[2], $parts[3])
}

function Read-GridSkyrimGameplayCapabilityCatalog {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$LiteralPath)
    $path = [IO.Path]::GetFullPath($LiteralPath)
    try { $catalog = Get-Content -Raw -LiteralPath $path -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "GameplayCapabilityCatalogUnreadable: '$path'. $($_.Exception.Message)" }
    if ([int]$catalog.schemaVersion -ne 1 -or [string]$catalog.gameId -cne 'skyrimspecialedition') {
        throw 'GameplayCapabilityCatalogInvalid: unsupported schemaVersion or gameId.'
    }
    try {
        $observed = [DateTimeOffset]::Parse([string]$catalog.observedAtUtc).ToUniversalTime()
        $expires = [DateTimeOffset]::Parse([string]$catalog.expiresAtUtc).ToUniversalTime()
    }
    catch { throw 'GameplayCapabilityCatalogInvalid: observedAtUtc and expiresAtUtc must be timestamps.' }
    if ($expires -le $observed) { throw 'GameplayCapabilityCatalogInvalid: expiresAtUtc must follow observedAtUtc.' }
    $sourceIds = @{}
    foreach ($source in @($catalog.sources)) {
        $sourceId = [string]$source.sourceId
        $uri = $null
        if ([string]::IsNullOrWhiteSpace($sourceId) -or $sourceIds.ContainsKey($sourceId)) { throw 'GameplayCapabilityCatalogInvalid: source IDs must be unique and non-empty.' }
        if (-not [Uri]::TryCreate([string]$source.uri, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -cne 'https') { throw "GameplayCapabilityCatalogInvalid: source '$sourceId' must use HTTPS." }
        if (@($source.claims).Count -eq 0) { throw "GameplayCapabilityCatalogInvalid: source '$sourceId' has no bounded claims." }
        $sourceIds[$sourceId] = $source
    }
    $capabilityIds = @{}
    foreach ($capability in @($catalog.capabilities)) {
        $capabilityId = [string]$capability.capabilityId
        if ($capabilityId -notmatch '^grid\.capability\.[a-z0-9]+(?:[.-][a-z0-9]+)*$' -or $capabilityIds.ContainsKey($capabilityId)) { throw 'GameplayCapabilityCatalogInvalid: capability IDs must be valid and unique.' }
        $capabilityIds[$capabilityId] = $true
        foreach ($owner in @($capability.installedProviders) + @($capability.communityCandidates)) {
            foreach ($sourceId in @($owner.sourceIds)) {
                if (-not $sourceIds.ContainsKey([string]$sourceId)) { throw "GameplayCapabilityCatalogInvalid: unknown source '$sourceId'." }
            }
        }
        foreach ($candidate in @($capability.communityCandidates)) {
            if ([string]$candidate.provider -cne 'Nexus Mods' -or $null -eq $candidate.PSObject.Properties['providerLocator']) {
                throw "GameplayCapabilityCatalogInvalid: candidate '$($candidate.candidateId)' requires an exact Nexus provider locator."
            }
            $modId = 0L
            if ([string]$candidate.providerLocator.gameId -cne 'skyrimspecialedition' -or
                -not [long]::TryParse([string]$candidate.providerLocator.modId, [ref]$modId) -or $modId -le 0 -or
                [string]$candidate.candidateId -cne "nexus.skyrimspecialedition.$modId") {
                throw "GameplayCapabilityCatalogInvalid: candidate '$($candidate.candidateId)' has an inconsistent Nexus locator."
            }
        }
    }
    [pscustomobject]@{ Path = $path; Catalog = $catalog; ObservedAtUtc = $observed; ExpiresAtUtc = $expires; SourcesById = $sourceIds }
}

function Get-GridSkyrimGameplayCapabilityAssessment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidatePattern('^grid\.capability\.[a-z0-9]+(?:[.-][a-z0-9]+)*$')][string]$CapabilityId,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$ModInventory,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ProfileEvidenceId,
        [object[]]$ComponentEvidence = @(),
        [AllowNull()][object]$CommunityObservation,
        [DateTimeOffset]$AsOfUtc = [DateTimeOffset]::UtcNow,
        [string]$CatalogPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'catalogs\community-capabilities.v1.json')
    )
    $loaded = Read-GridSkyrimGameplayCapabilityCatalog -LiteralPath $CatalogPath
    $matches = @($loaded.Catalog.capabilities | Where-Object { [string]$_.capabilityId -ceq $CapabilityId })
    if ($matches.Count -ne 1) { throw "GameplayCapabilityNotCataloged: '$CapabilityId'." }
    $capability = $matches[0]
    $validatedObservation = $null
    if ($null -ne $CommunityObservation) {
        if ([int](Get-GridSkyrimCapabilityValue $CommunityObservation 'schemaVersion') -ne 1 -or
            [string](Get-GridSkyrimCapabilityValue $CommunityObservation 'capabilityId') -cne $CapabilityId) {
            throw 'GameplayCapabilityObservationInvalid: schemaVersion or capabilityId does not match the request.'
        }
        $observationSha256 = [string](Get-GridSkyrimCapabilityValue $CommunityObservation 'observationSha256')
        $unsignedObservation = $CommunityObservation | Select-Object * -ExcludeProperty observationSha256
        if ($observationSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
            $observationSha256.ToUpperInvariant() -cne (Get-GridSkyrimCapabilityJsonSha256 $unsignedObservation).ToUpperInvariant()) {
            throw 'GameplayCapabilityObservationInvalid: observation digest does not match its contents.'
        }
        try {
            $observationObservedAt = [DateTimeOffset]::Parse([string](Get-GridSkyrimCapabilityValue $CommunityObservation 'observedAtUtc')).ToUniversalTime()
            $observationExpiresAt = [DateTimeOffset]::Parse([string](Get-GridSkyrimCapabilityValue $CommunityObservation 'expiresAtUtc')).ToUniversalTime()
        }
        catch { throw 'GameplayCapabilityObservationInvalid: observation timestamps are invalid.' }
        if ($observationExpiresAt -le $observationObservedAt) { throw 'GameplayCapabilityObservationInvalid: expiresAtUtc must follow observedAtUtc.' }
        $catalogCandidates = @{}
        foreach ($catalogCandidate in @($capability.communityCandidates)) { $catalogCandidates[[string]$catalogCandidate.candidateId] = $catalogCandidate }
        $seenObservationCandidates = @{}
        foreach ($observedCandidate in @((Get-GridSkyrimCapabilityValue $CommunityObservation 'candidates'))) {
            $candidateId = [string](Get-GridSkyrimCapabilityValue $observedCandidate 'candidateId')
            if ([string]::IsNullOrWhiteSpace($candidateId) -or $seenObservationCandidates.ContainsKey($candidateId) -or -not $catalogCandidates.ContainsKey($candidateId)) {
                throw "GameplayCapabilityObservationInvalid: unknown or duplicate candidate '$candidateId'."
            }
            $catalogCandidate = $catalogCandidates[$candidateId]
            $observedModId = 0L
            if ([string](Get-GridSkyrimCapabilityValue $observedCandidate 'provider') -cne 'Nexus Mods' -or
                [string](Get-GridSkyrimCapabilityValue $observedCandidate 'gameId') -cne [string]$catalogCandidate.providerLocator.gameId -or
                -not [long]::TryParse([string](Get-GridSkyrimCapabilityValue $observedCandidate 'modId'), [ref]$observedModId) -or
                $observedModId -ne [long]$catalogCandidate.providerLocator.modId -or
                [string](Get-GridSkyrimCapabilityValue $observedCandidate 'uri') -cne [string]$catalogCandidate.uri) {
                throw "GameplayCapabilityObservationInvalid: candidate '$candidateId' does not match its cataloged provider locator."
            }
            $seenObservationCandidates[$candidateId] = $observedCandidate
        }
        $validatedObservation = [pscustomobject]@{
            Value = $CommunityObservation; ObservedAtUtc = $observationObservedAt; ExpiresAtUtc = $observationExpiresAt
            CandidatesById = $seenObservationCandidates; Status = [string](Get-GridSkyrimCapabilityValue $CommunityObservation 'status')
            EvidenceId = 'sha256:' + $observationSha256.ToUpperInvariant()
        }
    }
    $allEvidenceIds = New-Object Collections.Generic.List[string]
    $allEvidenceIds.Add($ProfileEvidenceId)
    $sourceEvidenceIds = @{}
    foreach ($source in @($loaded.Catalog.sources)) {
        $identity = [pscustomobject][ordered]@{
            sourceId = [string]$source.sourceId; provider = [string]$source.provider; uri = [string]$source.uri
            claims = @($source.claims | ForEach-Object { [string]$_ }); observedAtUtc = $loaded.ObservedAtUtc.ToString('o')
        }
        $evidenceId = 'sha256:' + (Get-GridSkyrimCapabilityJsonSha256 $identity)
        $sourceEvidenceIds[[string]$source.sourceId] = $evidenceId
    }

    $installedProviders = New-Object Collections.Generic.List[object]
    $activeProviderIds = New-Object Collections.Generic.List[string]
    $providerCoverageStates = New-Object Collections.Generic.List[string]
    foreach ($provider in @($capability.installedProviders)) {
        $providerMods = @($ModInventory | Where-Object { Test-GridSkyrimCapabilityModMatch -Mod $_ -Identity $provider })
        if ($providerMods.Count -eq 0) { continue }
        $enabledMods = @($providerMods | Where-Object { ConvertTo-GridSkyrimCapabilityBoolean (Get-GridSkyrimCapabilityValue $_ 'enabled') })
        $providerEvidence = New-Object Collections.Generic.List[string]
        $providerEvidence.Add($ProfileEvidenceId)
        foreach ($mod in $providerMods) {
            $metadataSha256 = [string](Get-GridSkyrimCapabilityValue $mod 'metadataSha256')
            if ($metadataSha256 -match '^[A-Fa-f0-9]{64}$') {
                $id = 'sha256:' + $metadataSha256.ToUpperInvariant()
                $providerEvidence.Add($id); $allEvidenceIds.Add($id)
            }
        }
        foreach ($sourceId in @($provider.sourceIds)) {
            $sourceEvidenceId = [string]$sourceEvidenceIds[[string]$sourceId]
            $providerEvidence.Add($sourceEvidenceId); $allEvidenceIds.Add($sourceEvidenceId)
        }
        $missingComponents = New-Object Collections.Generic.List[string]
        foreach ($component in @($provider.requiredComponents)) {
            $observed = $false
            foreach ($mod in $enabledMods) {
                $leaf = [IO.Path]::GetFileName([string](Get-GridSkyrimCapabilityValue $mod 'installationFile'))
                foreach ($prefix in @($component.archivePrefixes)) {
                    if (-not [string]::IsNullOrWhiteSpace($leaf) -and $leaf.StartsWith([string]$prefix, [StringComparison]::OrdinalIgnoreCase)) { $observed = $true; break }
                }
                if ($observed) { break }
            }
            foreach ($componentObservation in @($ComponentEvidence | Where-Object {
                [string](Get-GridSkyrimCapabilityValue $_ 'providerId') -ceq [string]$provider.providerId -and
                [string](Get-GridSkyrimCapabilityValue $_ 'componentId') -ceq [string]$component.componentId -and
                [string](Get-GridSkyrimCapabilityValue $_ 'status') -in @('Observed','Present','Verified')
            })) {
                $observed = $true
                foreach ($evidenceId in @(Get-GridSkyrimCapabilityValue $componentObservation 'evidenceIds')) {
                    if (-not [string]::IsNullOrWhiteSpace([string]$evidenceId)) { $providerEvidence.Add([string]$evidenceId); $allEvidenceIds.Add([string]$evidenceId) }
                }
            }
            if (-not $observed) { $missingComponents.Add([string]$component.displayName) }
        }
        $state = if ($enabledMods.Count -eq 0) { 'Partial' } elseif ($missingComponents.Count -eq 0) { 'Satisfied' } else { 'Partial' }
        $providerCoverageStates.Add($state)
        if ($enabledMods.Count -gt 0) { $activeProviderIds.Add([string]$provider.providerId) }
        $status = if ($enabledMods.Count -eq 0) { 'Installed but disabled in the captured profile' }
            elseif ($missingComponents.Count -eq 0) { 'Enabled; every required component is evidenced' }
            else { 'Enabled; missing evidence for: ' + ($missingComponents.ToArray() -join ', ') }
        $installedProviders.Add([pscustomobject][ordered]@{
            name = [string]$provider.name; role = [string]$provider.role; status = $status
            evidenceIds = @($providerEvidence.ToArray() | Sort-Object -Unique)
        })
    }
    $installedStatus = if ($installedProviders.Count -eq 0) { 'Absent' }
        elseif (@($providerCoverageStates | Where-Object { $_ -eq 'Satisfied' }).Count -gt 0) { 'Satisfied' }
        else { 'Partial' }

    $asOf = $AsOfUtc.ToUniversalTime()
    $catalogClaimsStale = $asOf -gt $loaded.ExpiresAtUtc
    $providerObservationCurrent = $null -ne $validatedObservation -and $asOf -le $validatedObservation.ExpiresAtUtc -and
        @($validatedObservation.CandidatesById.Values | Where-Object { [string](Get-GridSkyrimCapabilityValue $_ 'status') -eq 'Observed' }).Count -gt 0
    $discoveryStatus = if (-not $catalogClaimsStale) { 'Current' }
        elseif ($providerObservationCurrent) { 'Current' }
        elseif ($null -ne $validatedObservation -and $validatedObservation.Status -eq 'AuthenticationRequired') { 'AuthenticationRequired' }
        elseif ($null -ne $validatedObservation -and $validatedObservation.Status -in @('ProviderRequestFailed','ProviderResponseInvalid','Partial')) { 'Unavailable' }
        else { 'Stale' }
    $discoveryObservedAt = if ($providerObservationCurrent) { $validatedObservation.ObservedAtUtc } else { $loaded.ObservedAtUtc }
    if ($null -ne $validatedObservation) { $allEvidenceIds.Add([string]$validatedObservation.EvidenceId) }
    $communityCandidates = New-Object Collections.Generic.List[object]
    if ($discoveryStatus -eq 'Current') {
        foreach ($candidate in @($capability.communityCandidates)) {
            $providerCandidate = if ($null -ne $validatedObservation -and $validatedObservation.CandidatesById.ContainsKey([string]$candidate.candidateId)) {
                $validatedObservation.CandidatesById[[string]$candidate.candidateId]
            } else { $null }
            if ($catalogClaimsStale -and ($null -eq $providerCandidate -or [string](Get-GridSkyrimCapabilityValue $providerCandidate 'status') -ne 'Observed')) { continue }
            $candidateEvidence = New-Object Collections.Generic.List[string]
            $candidateEvidence.Add($ProfileEvidenceId)
            foreach ($sourceId in @($candidate.sourceIds)) {
                $sourceEvidenceId = [string]$sourceEvidenceIds[[string]$sourceId]
                $candidateEvidence.Add($sourceEvidenceId); $allEvidenceIds.Add($sourceEvidenceId)
            }
            if ($null -ne $providerCandidate) {
                $providerCandidateEvidenceId = [string](Get-GridSkyrimCapabilityValue $providerCandidate 'evidenceId')
                if ($providerCandidateEvidenceId -match '^sha256:[A-Fa-f0-9]{64}$') {
                    $providerCandidateEvidenceId = $providerCandidateEvidenceId.ToUpperInvariant().Replace('SHA256:', 'sha256:')
                    $candidateEvidence.Add($providerCandidateEvidenceId); $allEvidenceIds.Add($providerCandidateEvidenceId)
                }
            }
            $missingRequired = New-Object Collections.Generic.List[string]
            foreach ($dependency in @($candidate.requiredDependencies)) {
                $enabled = @($ModInventory | Where-Object {
                    (Test-GridSkyrimCapabilityModMatch -Mod $_ -Identity $dependency) -and
                    (ConvertTo-GridSkyrimCapabilityBoolean (Get-GridSkyrimCapabilityValue $_ 'enabled'))
                })
                if ($enabled.Count -eq 0) { $missingRequired.Add([string]$dependency.displayName) }
            }
            $incompatible = New-Object Collections.Generic.List[string]
            $unknownVersions = New-Object Collections.Generic.List[string]
            foreach ($constraint in @($candidate.minimumVersionsWhenPresent)) {
                $present = @($ModInventory | Where-Object {
                    (Test-GridSkyrimCapabilityModMatch -Mod $_ -Identity $constraint) -and
                    (ConvertTo-GridSkyrimCapabilityBoolean (Get-GridSkyrimCapabilityValue $_ 'enabled'))
                })
                foreach ($mod in $present) {
                    $actualVersion = ConvertTo-GridSkyrimComparableVersion (Get-GridSkyrimCapabilityValue $mod 'version')
                    $minimumVersion = ConvertTo-GridSkyrimComparableVersion $constraint.minimumVersion
                    if ($null -eq $actualVersion) { $unknownVersions.Add([string]$constraint.displayName) }
                    elseif ($actualVersion -lt $minimumVersion) { $incompatible.Add("$($constraint.displayName) $actualVersion is below required $minimumVersion") }
                }
            }
            $coexistence = @($candidate.unresolvedCoexistenceProviderIds | Where-Object { [string]$_ -in @($activeProviderIds) })
            $observedCandidateVersion = if ($null -ne $providerCandidate) { [string](Get-GridSkyrimCapabilityValue $providerCandidate 'version') } else { '' }
            $candidateVersion = if ([string]::IsNullOrWhiteSpace($observedCandidateVersion)) { [string]$candidate.version } else { $observedCandidateVersion }
            $providerVersionChanged = -not [string]::IsNullOrWhiteSpace($observedCandidateVersion) -and
                -not [string]::Equals($observedCandidateVersion, [string]$candidate.version, [StringComparison]::OrdinalIgnoreCase)
            $compatibilityStatus = 'Compatible'
            $requiredPatches = @()
            $detail = 'The captured profile satisfies every cataloged required dependency and has no cataloged incompatibility. Uncataloged interactions remain unverified.'
            if ($catalogClaimsStale) {
                $compatibilityStatus = 'Unresolved'
                $detail = 'Nexus confirms that this provider still exists, but the cataloged requirements and compatibility claims are stale and require source review.'
            }
            elseif ($providerVersionChanged) {
                $compatibilityStatus = 'Unresolved'
                $detail = "Nexus reports version $observedCandidateVersion while Grid's reviewed compatibility record covers $($candidate.version); the changed release requires source review."
            }
            elseif ($incompatible.Count -gt 0) {
                $compatibilityStatus = 'Incompatible'; $detail = $incompatible.ToArray() -join '; '
            }
            elseif ($missingRequired.Count -gt 0) {
                $compatibilityStatus = 'PatchRequired'; $requiredPatches = @($missingRequired.ToArray())
                $detail = 'The captured profile is missing required support: ' + ($missingRequired.ToArray() -join ', ') + '.'
            }
            elseif ($unknownVersions.Count -gt 0) {
                $compatibilityStatus = 'Unresolved'; $detail = 'Installed version evidence is unresolved for: ' + ($unknownVersions.ToArray() -join ', ') + '.'
            }
            elseif ($coexistence.Count -gt 0) {
                $compatibilityStatus = 'Unresolved'
                $detail = 'Declared dependencies are satisfied, but coexistence with the active Immersive Jewelry provider is not established by current source evidence; no compatibility patch is asserted.'
            }
            $communityCandidates.Add([pscustomobject][ordered]@{
                name = [string]$candidate.name; provider = [string]$candidate.provider; uri = [string]$candidate.uri; version = $candidateVersion
                compatibilityStatus = $compatibilityStatus; compatibilityDetail = $detail; requiredPatches = @($requiredPatches)
                evidenceIds = @($candidateEvidence.ToArray() | Sort-Object -Unique)
            })
        }
    }
    [pscustomobject][ordered]@{
        schemaVersion = 1; capabilityId = [string]$capability.capabilityId; displayName = [string]$capability.displayName
        installedStatus = $installedStatus; installedProviders = @($installedProviders.ToArray())
        discoveryStatus = $discoveryStatus; observedAtUtc = $discoveryObservedAt.ToString('o')
        communityCandidates = @($communityCandidates.ToArray()); evidenceIds = @($allEvidenceIds.ToArray() | Sort-Object -Unique)
    }
}
