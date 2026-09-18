#requires -Version 5.1
<#
.SYNOPSIS
Selects a deterministic, reviewable route when a requested capability is absent.
.DESCRIPTION
This shared resolver never interprets prose, queries a provider, creates code, acquires
artifacts, or authorizes mutation. It accepts an explicit capability requirement and
already-normalized registry, community, composition, and CODE-admission evidence.
#>

function Get-GridCapabilityGapValue {
    param([AllowNull()][object]$InputObject, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $InputObject) { return $null }
    if ($InputObject -is [Collections.IDictionary]) {
        foreach ($key in @($InputObject.Keys)) {
            if ([string]::Equals([string]$key, $Name, [StringComparison]::OrdinalIgnoreCase)) { return $InputObject[$key] }
        }
        return $null
    }
    $match = @($InputObject.PSObject.Properties | Where-Object { [string]::Equals($_.Name, $Name, [StringComparison]::OrdinalIgnoreCase) })
    if ($match.Count -eq 1) { return $match[0].Value }
    $null
}

function Get-GridCapabilityGapSha256 {
    param([Parameter(Mandatory)]$Value)
    if (Get-Command Get-GridCanonicalJsonSha256 -ErrorAction SilentlyContinue) {
        return Get-GridCanonicalJsonSha256 -InputObject $Value
    }
    $json = $Value | ConvertTo-Json -Depth 40 -Compress
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($algorithm.ComputeHash([Text.UTF8Encoding]::new($false).GetBytes($json)))).Replace('-', '') }
    finally { $algorithm.Dispose() }
}

function Resolve-GridCapabilityGap {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidatePattern('^gap\.[a-z0-9]+(?:[.-][a-z0-9]+)*$')][string]$GapId,
        [Parameter(Mandatory)][ValidatePattern('^[a-z0-9]+(?:[.-][a-z0-9]+)*$')][string]$GameId,
        [Parameter(Mandatory)][ValidatePattern('^grid\.[a-z0-9]+(?:[.-][a-z0-9]+)*$')][string]$RequiredCapabilityId,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$RequestedOutcome,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$CapabilityRegistry,
        [string[]]$CompositionCapabilityIds = @(),
        [object[]]$CommunityCandidates = @(),
        [AllowNull()][object]$ScriptAdmission,
        [string[]]$MissingInputs = @(),
        [ValidateSet('Auto','Guided')][string]$Mode = 'Auto',
        [DateTimeOffset]$AsOfUtc = [DateTimeOffset]::UtcNow
    )

    $outcome = $RequestedOutcome.Trim()
    if ([string]::IsNullOrWhiteSpace($outcome)) { throw 'CapabilityGapInvalid: requestedOutcome is required.' }
    $missing = @($MissingInputs | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ } | Sort-Object -Unique)
    $compositionIds = @($CompositionCapabilityIds | ForEach-Object { ([string]$_).Trim().ToLowerInvariant() } | Where-Object { $_ } | Sort-Object -Unique)
    foreach ($id in $compositionIds) {
        if ($id -notmatch '^grid\.[a-z0-9]+(?:[.-][a-z0-9]+)*$') { throw "CapabilityGapInvalid: invalid composition capability '$id'." }
    }

    $registryById = @{}
    foreach ($contract in @($CapabilityRegistry)) {
        $id = ([string](Get-GridCapabilityGapValue $contract 'capabilityId')).Trim().ToLowerInvariant()
        if ([string]::IsNullOrWhiteSpace($id)) { throw 'CapabilityGapInvalid: registry contract has no capabilityId.' }
        if ($registryById.ContainsKey($id)) { throw "CapabilityGapInvalid: duplicate registry capability '$id'." }
        $registryById[$id] = $contract
    }

    $evidenceIds = New-Object Collections.Generic.List[string]
    $normalizedCandidates = New-Object Collections.Generic.List[object]
    foreach ($candidate in @($CommunityCandidates)) {
        $candidateId = ([string](Get-GridCapabilityGapValue $candidate 'candidateId')).Trim()
        $compatibility = [string](Get-GridCapabilityGapValue $candidate 'compatibilityStatus')
        $availability = [string](Get-GridCapabilityGapValue $candidate 'availabilityStatus')
        if ([string]::IsNullOrWhiteSpace($candidateId) -or $compatibility -notin @('Compatible','PatchRequired','Incompatible','Unresolved') -or
            $availability -notin @('Available','Unavailable','AuthenticationRequired','Unknown')) {
            throw "CapabilityGapInvalid: community candidate '$candidateId' is not normalized."
        }
        try {
            $observedAt = [DateTimeOffset]::Parse([string](Get-GridCapabilityGapValue $candidate 'observedAtUtc')).ToUniversalTime()
            $expiresAt = [DateTimeOffset]::Parse([string](Get-GridCapabilityGapValue $candidate 'expiresAtUtc')).ToUniversalTime()
        }
        catch { throw "CapabilityGapInvalid: community candidate '$candidateId' has invalid timestamps." }
        if ($expiresAt -le $observedAt) { throw "CapabilityGapInvalid: community candidate '$candidateId' has a non-positive freshness window." }
        $candidateEvidence = @((Get-GridCapabilityGapValue $candidate 'evidenceIds') | ForEach-Object { [string]$_ } | Where-Object { $_ } | Sort-Object -Unique)
        foreach ($evidenceId in $candidateEvidence) {
            if ($evidenceId -notmatch '^sha256:[A-Fa-f0-9]{64}$') { throw "CapabilityGapInvalid: community candidate '$candidateId' has an invalid evidence identity." }
            $evidenceIds.Add($evidenceId.ToUpperInvariant())
        }
        $normalizedCandidates.Add([pscustomobject][ordered]@{
            candidateId = $candidateId
            compatibilityStatus = $compatibility
            availabilityStatus = $availability
            observedAtUtc = $observedAt.ToString('o')
            expiresAtUtc = $expiresAt.ToString('o')
            isFresh = $expiresAt -gt $AsOfUtc.ToUniversalTime()
            evidenceIds = $candidateEvidence
        })
    }

    $status = 'CreationProposalRequired'
    $route = 'CreateCapabilityProposal'
    $selectedCandidateId = $null
    $nextAction = "Prepare a reusable capability proposal through CODE admission for '$RequiredCapabilityId'."
    $autoMayContinue = $Mode -eq 'Auto'
    $requiredKey = $RequiredCapabilityId.ToLowerInvariant()

    if ($registryById.ContainsKey($requiredKey)) {
        $contract = $registryById[$requiredKey]
        $status = 'RegisteredCapabilityReady'
        $route = 'ReuseRegisteredCapability'
        $nextAction = "Bind the existing '$RequiredCapabilityId' contract to a new evidence-scoped request plan."
        $sideEffect = [string](Get-GridCapabilityGapValue $contract 'sideEffectClassification')
        $authority = Get-GridCapabilityGapValue $contract 'requiredAuthority'
        $authorityKind = [string](Get-GridCapabilityGapValue $authority 'kind')
        $autoMayContinue = $Mode -eq 'Auto' -and $sideEffect -in @('None','RepositoryRead') -and $authorityKind -in @('None','RepositoryRead')
    }
    elseif ($missing.Count -gt 0) {
        $status = 'NeedsStructuredInput'
        $route = 'RequestStructuredInput'
        $nextAction = 'Collect only the missing structured input: ' + ($missing -join ', ') + '.'
        $autoMayContinue = $false
    }
    else {
        $missingComposition = @($compositionIds | Where-Object { -not $registryById.ContainsKey($_) })
        if ($compositionIds.Count -gt 0 -and $missingComposition.Count -eq 0) {
            $status = 'CompositionReady'
            $route = 'ComposeRegisteredCapabilities'
            $nextAction = 'Materialize a reviewable plan from the exact registered capability composition.'
            $autoMayContinue = $Mode -eq 'Auto'
        }
        else {
            $compatible = @($normalizedCandidates.ToArray() | Where-Object {
                $_.compatibilityStatus -eq 'Compatible' -and $_.availabilityStatus -eq 'Available' -and $_.isFresh -and @($_.evidenceIds).Count -gt 0
            } | Sort-Object candidateId)
            if ($compatible.Count -eq 1) {
                $status = 'AcquisitionReady'
                $route = 'AcquireVerifiedCandidate'
                $selectedCandidateId = [string]$compatible[0].candidateId
                $nextAction = "Prepare evidence-bound acquisition of '$selectedCandidateId'; acquisition still requires provider authority."
                $autoMayContinue = $false
            }
            elseif ($compatible.Count -gt 1) {
                $status = 'CandidateSelectionRequired'
                $route = 'SelectCompatibleCandidate'
                $nextAction = 'Select one of the equally compatible current candidates; AUTO will not invent preference.'
                $autoMayContinue = $false
            }
            elseif ($null -ne $ScriptAdmission) {
                $admissionStatus = [string](Get-GridCapabilityGapValue $ScriptAdmission 'status')
                $proposalSha = [string](Get-GridCapabilityGapValue $ScriptAdmission 'proposalSha256')
                if ($proposalSha -match '^[A-Fa-f0-9]{64}$') { $evidenceIds.Add('sha256:' + $proposalSha.ToUpperInvariant()) }
                switch ($admissionStatus) {
                    'ExistingCapability' { $status='RegisteredCapabilityReady'; $route='ReuseRegisteredCapability'; $nextAction='Bind the admitted existing capability to a request plan.'; $autoMayContinue=$Mode -eq 'Auto' }
                    'CompositionReady' { $status='CompositionReady'; $route='ComposeRegisteredCapabilities'; $nextAction='Materialize the admitted reusable composition.'; $autoMayContinue=$Mode -eq 'Auto' }
                    'ReusableProposalReady' { $status='CreationProposalReady'; $route='CreateCapabilityProposal'; $nextAction='Review and implement the admitted reusable capability proposal in its canonical owner directory.'; $autoMayContinue=$Mode -eq 'Auto' }
                    'RuntimeCaseData' { $status='RuntimeCaseData'; $route='KeepRuntimeCaseData'; $nextAction='Keep incident identities in the sealed case and parameterize a reusable capability before implementation.'; $autoMayContinue=$false }
                    'Rejected' { $status='Rejected'; $route='RejectCandidate'; $nextAction='Correct the CODE-admission rejection reasons before capability creation can continue.'; $autoMayContinue=$false }
                    default { throw "CapabilityGapInvalid: unsupported script-admission status '$admissionStatus'." }
                }
            }
        }
    }

    $unsigned = [pscustomobject][ordered]@{
        schemaVersion = 1
        gapId = $GapId
        gameId = $GameId
        requiredCapabilityId = $RequiredCapabilityId
        requestedOutcome = $outcome
        mode = $Mode
        status = $status
        route = $route
        selectedCandidateId = $selectedCandidateId
        compositionCapabilityIds = $compositionIds
        missingInputs = $missing
        candidates = @($normalizedCandidates.ToArray())
        evidenceIds = @($evidenceIds.ToArray() | Sort-Object -Unique)
        autoMayContinue = [bool]$autoMayContinue
        nextAction = $nextAction
        mutationAuthorized = $false
    }
    $unsigned | Add-Member -NotePropertyName resolutionSha256 -NotePropertyValue (Get-GridCapabilityGapSha256 $unsigned)
    $unsigned
}
