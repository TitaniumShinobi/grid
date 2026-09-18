#requires -Version 5.1
<#
.SYNOPSIS
Classifies proposed Grid scripting work before production code is admitted.
.DESCRIPTION
This repository-only capability prevents incidents, mods, plugins, records,
paths, and other runtime identities from becoming permanent production code.
It deterministically chooses reuse, composition, a reusable proposal, a private
helper, runtime case data, or rejection. It never writes the repository.
#>

function Get-GridScriptAdmissionSha256 {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Value)
    $canonicalizer = Get-Command ConvertTo-GridCanonicalJsonValue -ErrorAction SilentlyContinue
    $canonical = if ($canonicalizer) { ConvertTo-GridCanonicalJsonValue -Value $Value } else { $Value }
    $json = $canonical | ConvertTo-Json -Depth 40 -Compress
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($json)))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function New-GridScriptCapabilityProposalDraft {
    <#
    .SYNOPSIS
    Materializes an inert, deterministic CODE-design draft for one capability gap.
    .DESCRIPTION
    The draft is not executable source and is not an admission decision. It carries
    only structured gap context into the CODE boundary and enumerates the design
    evidence that must exist before Resolve-GridScriptCapabilityAdmission can admit
    a reusable implementation.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$GapResolution)

    $requiredCapabilityId = ([string]$GapResolution.requiredCapabilityId).Trim().ToLowerInvariant()
    $gameId = ([string]$GapResolution.gameId).Trim().ToLowerInvariant()
    $gapId = ([string]$GapResolution.gapId).Trim().ToLowerInvariant()
    $requestedOutcome = ([string]$GapResolution.requestedOutcome).Trim()
    if ($requiredCapabilityId -notmatch '^grid\.[a-z0-9]+(?:[.-][a-z0-9]+)*$') { throw 'CapabilityProposalDraftInvalid: requiredCapabilityId is invalid.' }
    if ($gapId -notmatch '^gap\.[a-z0-9]+(?:[.-][a-z0-9]+)*$') { throw 'CapabilityProposalDraftInvalid: gapId is invalid.' }
    if ([string]::IsNullOrWhiteSpace($requestedOutcome)) { throw 'CapabilityProposalDraftInvalid: requestedOutcome is required.' }
    if ([string]$GapResolution.route -ne 'CreateCapabilityProposal' -or [string]$GapResolution.status -notin @('CreationProposalRequired','CreationProposalReady')) {
        throw "CapabilityProposalDraftInvalid: gap '$gapId' is not routed to capability creation."
    }

    $owner = if ($requiredCapabilityId.StartsWith("grid.game.$gameId.", [StringComparison]::Ordinal)) {
        [pscustomobject][ordered]@{ kind='Game'; gameId=$gameId; toolId=$null; modId=$null }
    } else {
        [pscustomobject][ordered]@{ kind='SharedHealth'; gameId=$null; toolId=$null; modId=$null }
    }
    $canonicalDirectory = if ([string]$owner.kind -eq 'Game') { "scripts/games/$gameId/" } else { 'scripts/health/' }
    $candidate = [pscustomobject][ordered]@{
        requestedOperation = $requestedOutcome
        capabilityId = $requiredCapabilityId
        requestedBy = 'Grid'
        publicCapability = $true
        uniqueModFormat = $false
        owner = $owner
        reusedCapabilityIds = @()
        proposedFiles = @()
        exampleCases = @()
        caseSpecificIdentifiers = @()
        parameterContract = $null
        authority = $null
        terminalStates = @()
    }
    $unsigned = [pscustomobject][ordered]@{
        schemaVersion = 1
        gapId = $gapId
        requiredCapabilityId = $requiredCapabilityId
        requestedOutcome = $requestedOutcome
        status = 'NeedsDesignEvidence'
        canonicalDirectory = $canonicalDirectory
        candidate = $candidate
        requiredDesignInputs = @('parameterContract','authority','terminalStates','reusableExampleCases','proposedImplementationFiles','verificationPlan')
        nextAction = "CODE must complete and verify the reusable design evidence for '$requiredCapabilityId' before script admission."
        executable = $false
        mutationAuthorized = $false
    }
    $unsigned | Add-Member -NotePropertyName proposalDraftSha256 -NotePropertyValue (Get-GridScriptAdmissionSha256 -Value $unsigned)
    $unsigned
}

function Resolve-GridScriptCapabilityAdmission {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Candidate,
        [Parameter(Mandatory)][object[]]$CapabilityRegistry
    )

    $reasons = New-Object Collections.Generic.List[string]
    $operation = ([string]$Candidate.requestedOperation).Trim()
    $capabilityId = ([string]$Candidate.capabilityId).Trim().ToLowerInvariant()
    $ownerKind = [string]$Candidate.owner.kind
    $gameId = ([string]$Candidate.owner.gameId).Trim().ToLowerInvariant()
    $toolId = ([string]$Candidate.owner.toolId).Trim().ToLowerInvariant()
    $modId = ([string]$Candidate.owner.modId).Trim().ToLowerInvariant()
    $reuseIds = @($Candidate.reusedCapabilityIds | ForEach-Object { ([string]$_).Trim().ToLowerInvariant() } | Where-Object { $_ } | Sort-Object -Unique)
    $examples = @($Candidate.exampleCases)
    $specific = @($Candidate.caseSpecificIdentifiers | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ } | Sort-Object -Unique)

    if ([string]::IsNullOrWhiteSpace($operation)) { $reasons.Add('requestedOperation is required.') }
    if ($capabilityId -notmatch '^grid\.[a-z0-9]+(?:[.-][a-z0-9]+)*$') { $reasons.Add('capabilityId must be a stable semantic Grid identifier.') }
    if ($operation -match '(?i)[A-Za-z]:[\\/]|(?:0x)?[0-9a-f]{8}|[^\s"'']+\.(?:esp|esm|esl)\b') { $reasons.Add('requestedOperation contains runtime case identity.') }
    foreach ($file in @($Candidate.proposedFiles)) {
        $path = ([string]$file.path).Replace('\','/')
        $source = [string]$file.sourceText
        if ($path -match '(?i)(?:fix|repair|restore)-[^/]+\.(?:ps1|psm1)$') { $reasons.Add("Case-shaped production filename is prohibited: $path") }
        if (($path + "`n" + $source) -match '(?i)[A-Za-z]:[\\/]|(?:0x)?[0-9a-f]{8}|[^\s"'']+\.(?:esp|esm|esl)\b') { $reasons.Add("Proposed implementation contains runtime identity: $path") }
    }

    $byId = @{}
    foreach ($contract in @($CapabilityRegistry)) { $byId[([string]$contract.capabilityId).ToLowerInvariant()] = $contract }
    if ($byId.ContainsKey($capabilityId)) {
        return [pscustomobject][ordered]@{
            schemaVersion = 1; status = 'ExistingCapability'; capabilityId = $capabilityId
            reusedCapabilityIds = @($capabilityId); canonicalOwner = $byId[$capabilityId].ownerScope
            canonicalDirectory = $null; rejectionReasons = @(); mutationAuthorized = $false
            proposalSha256 = Get-GridScriptAdmissionSha256 -Value ([ordered]@{ status='ExistingCapability'; capabilityId=$capabilityId })
        }
    }
    foreach ($id in $reuseIds) { if (-not $byId.ContainsKey($id)) { $reasons.Add("Referenced capability is not registered: $id") } }

    $directory = switch ($ownerKind) {
        'SharedHealth' { 'scripts/health/' }
        'Tool' {
            if ($toolId -notmatch '^[a-z0-9]+(?:[.-][a-z0-9]+)*$') { $reasons.Add('Tool ownership requires owner.toolId.') }
            "scripts/tools/$toolId/"
        }
        'Game' {
            if ($gameId -notmatch '^[a-z0-9]+(?:[.-][a-z0-9]+)*$') { $reasons.Add('Game ownership requires owner.gameId.') }
            "scripts/games/$gameId/"
        }
        'Mod' {
            if ($gameId -notmatch '^[a-z0-9]+(?:[.-][a-z0-9]+)*$' -or $modId -notmatch '^[a-z0-9]+(?:[.-][a-z0-9]+)*$') { $reasons.Add('Mod ownership requires owner.gameId and owner.modId.') }
            "scripts/games/$gameId/mods/$modId/"
        }
        default { $reasons.Add("Unsupported owner kind '$ownerKind'."); $null }
    }
    foreach ($file in @($Candidate.proposedFiles)) {
        $path = ([string]$file.path).Replace('\','/')
        if ($directory -and -not $path.StartsWith($directory, [StringComparison]::OrdinalIgnoreCase)) { $reasons.Add("Proposed file is outside canonical ownership: $path") }
    }

    $distinctExamples = @($examples | ForEach-Object { [string]$_.situationId } | Where-Object { $_ } | Sort-Object -Unique)
    if ([bool]$Candidate.publicCapability -and $reuseIds.Count -eq 0 -and $distinctExamples.Count -lt 2) { $reasons.Add('A new public capability requires two distinct reusable situations.') }
    if ([bool]$Candidate.publicCapability -and @($Candidate.terminalStates).Count -eq 0) { $reasons.Add('A public capability requires terminal states.') }
    if ([bool]$Candidate.publicCapability -and $null -eq $Candidate.parameterContract) { $reasons.Add('A public capability requires a parameter contract.') }

    $status = if ($specific.Count -gt 0 -and -not [bool]$Candidate.uniqueModFormat) { 'RuntimeCaseData' }
        elseif ($reasons.Count -gt 0) { 'Rejected' }
        elseif (-not [bool]$Candidate.publicCapability) { 'PrivateHelperOnly' }
        elseif ($reuseIds.Count -gt 0) { 'CompositionReady' }
        else { 'ReusableProposalReady' }
    $payload = [ordered]@{
        status = $status; capabilityId = $capabilityId; reusedCapabilityIds = $reuseIds
        canonicalDirectory = $directory; parameterContract = $Candidate.parameterContract
        authority = $Candidate.authority; terminalStates = @($Candidate.terminalStates)
        caseSpecificIdentifiers = $specific; rejectionReasons = @($reasons)
    }
    [pscustomobject][ordered]@{
        schemaVersion = 1; status = $status; capabilityId = $capabilityId
        reusedCapabilityIds = $reuseIds; canonicalOwner = $Candidate.owner
        canonicalDirectory = $directory; parameterContract = $Candidate.parameterContract
        authority = $Candidate.authority; terminalStates = @($Candidate.terminalStates)
        rejectionReasons = @($reasons); mutationAuthorized = $false
        proposalSha256 = Get-GridScriptAdmissionSha256 -Value $payload
    }
}
