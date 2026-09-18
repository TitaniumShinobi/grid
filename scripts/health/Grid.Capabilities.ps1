#requires -Version 5.1
<#
.SYNOPSIS
Loads and validates Grid's deterministic capability contracts.
.DESCRIPTION
Capability manifests describe stable semantic operations. This component is
game-independent: it discovers shared contracts and contracts beneath
scripts/games without importing game implementations directly.
#>

$script:GridCapabilityManifestSchemaVersion = 1
$script:GridCapabilitySideEffectClasses = @(
    'None', 'RepositoryRead', 'CaseLocalWrite', 'ExternalRead',
    'ExternalProcessLaunch', 'ExternalProcessControl', 'ExternalWrite'
)
$script:GridCapabilityAuthorityKinds = @(
    'None', 'RepositoryRead', 'CaseDirectoryWrite', 'AuthorizedExternalRead',
    'ExplicitProcessLaunch', 'ExplicitOwnedProcessControl', 'OneUseProposalAuthorization',
    'EvidenceBoundArtifactAcquisition', 'ExplicitGrantIssuance', 'DurableOneUseGrant'
)
$script:GridCapabilityOwnerKinds = @('SharedHealth', 'Game', 'Mod')

function Get-GridCapabilityManifestPaths {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ScriptsRoot)

    $root = [IO.Path]::GetFullPath($ScriptsRoot)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "Scripts root does not exist: $root"
    }

    $paths = New-Object Collections.Generic.List[string]
    $shared = Join-Path $root 'health\capabilities.v1.json'
    if (Test-Path -LiteralPath $shared -PathType Leaf) { $paths.Add([IO.Path]::GetFullPath($shared)) }

    $gamesRoot = Join-Path $root 'games'
    if (Test-Path -LiteralPath $gamesRoot -PathType Container) {
        foreach ($game in @(Get-ChildItem -LiteralPath $gamesRoot -Directory -ErrorAction Stop | Sort-Object Name)) {
            $candidate = Join-Path $game.FullName 'capabilities.v1.json'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $paths.Add([IO.Path]::GetFullPath($candidate)) }
        }
    }
    $paths.ToArray()
}

function Test-GridCapabilityContract {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Contract)

    $errors = New-Object Collections.Generic.List[string]
    foreach ($field in @(
        'capabilityId', 'capabilityVersion', 'ownerScope', 'implementation',
        'inputSchema', 'outputSchema', 'preconditions', 'dependencies',
        'sideEffectClassification', 'requiredAuthority', 'rollback', 'terminalStates'
    )) {
        if ($null -eq $Contract.PSObject.Properties[$field]) { $errors.Add("Missing capability field: $field") }
    }
    if ($errors.Count -gt 0) { return [pscustomobject]@{ IsValid = $false; Errors = @($errors) } }

    $allowedFields = @(
        'capabilityId', 'capabilityVersion', 'ownerScope', 'implementation',
        'inputSchema', 'outputSchema', 'preconditions', 'dependencies',
        'sideEffectClassification', 'requiredAuthority', 'rollback', 'terminalStates', 'manifestPath'
    )
    foreach ($property in @($Contract.PSObject.Properties.Name)) {
        if ($property -notin $allowedFields) { $errors.Add("Unsupported capability field: $property") }
    }

    $id = [string]$Contract.capabilityId
    if ($id -notmatch '^grid\.[a-z0-9]+(?:[.-][a-z0-9]+)*$') { $errors.Add("Invalid capabilityId '$id'.") }
    if ([string]$Contract.capabilityVersion -notmatch '^\d+\.\d+\.\d+$') { $errors.Add("Capability '$id' has an invalid semantic version.") }

    $ownerKind = [string]$Contract.ownerScope.kind
    if ($ownerKind -notin $script:GridCapabilityOwnerKinds) { $errors.Add("Capability '$id' has invalid ownerScope.kind '$ownerKind'.") }
    if ($ownerKind -in @('Game', 'Mod') -and [string]::IsNullOrWhiteSpace([string]$Contract.ownerScope.gameId)) {
        $errors.Add("Capability '$id' requires ownerScope.gameId.")
    }
    if ($ownerKind -eq 'Mod' -and [string]::IsNullOrWhiteSpace([string]$Contract.ownerScope.modId)) {
        $errors.Add("Capability '$id' requires ownerScope.modId.")
    }

    if ([string]$Contract.sideEffectClassification -notin $script:GridCapabilitySideEffectClasses) {
        $errors.Add("Capability '$id' has invalid sideEffectClassification '$($Contract.sideEffectClassification)'.")
    }
    $authorityKind = [string]$Contract.requiredAuthority.kind
    if ($authorityKind -notin $script:GridCapabilityAuthorityKinds) {
        $errors.Add("Capability '$id' has invalid requiredAuthority.kind '$authorityKind'.")
    }
    if ([string]::IsNullOrWhiteSpace([string]$Contract.requiredAuthority.description)) {
        $errors.Add("Capability '$id' requires an authority description.")
    }
    if ([string]::IsNullOrWhiteSpace([string]$Contract.rollback.mode) -or [string]::IsNullOrWhiteSpace([string]$Contract.rollback.description)) {
        $errors.Add("Capability '$id' requires rollback mode and description.")
    }
    if (@($Contract.implementation.files).Count -eq 0 -or [string]::IsNullOrWhiteSpace([string]$Contract.implementation.entryPoint)) {
        $errors.Add("Capability '$id' requires implementation files and an entry point.")
    }
    if ($null -eq $Contract.inputSchema -or $null -eq $Contract.outputSchema) {
        $errors.Add("Capability '$id' requires inputSchema and outputSchema objects.")
    }
    $schemaText = @($Contract.inputSchema, $Contract.outputSchema) | ConvertTo-Json -Depth 30
    if ($schemaText -match '(?i)"(?:default|const|examples)"\s*:') {
        $errors.Add("Capability '$id' embeds concrete schema values; production contracts may define shapes only.")
    }
    if (@($Contract.terminalStates).Count -eq 0) { $errors.Add("Capability '$id' requires at least one terminal state.") }

    $externalEffects = @('ExternalProcessLaunch', 'ExternalProcessControl', 'ExternalWrite')
    if ([string]$Contract.sideEffectClassification -in $externalEffects -and $authorityKind -in @('None', 'RepositoryRead', 'CaseDirectoryWrite')) {
        $errors.Add("Capability '$id' has an external side effect without external authority.")
    }
    if ([string]$Contract.sideEffectClassification -in @('ExternalProcessControl', 'ExternalWrite') -and [string]$Contract.rollback.mode -eq 'NotDeclared') {
        $errors.Add("Capability '$id' has a state-changing side effect without rollback behavior.")
    }

    [pscustomobject]@{ IsValid = ($errors.Count -eq 0); Errors = @($errors) }
}

function Read-GridCapabilityManifest {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$LiteralPath)

    $path = [IO.Path]::GetFullPath($LiteralPath)
    try { $manifest = Get-Content -LiteralPath $path -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "CapabilityManifestUnreadable: '$path'. $($_.Exception.Message)" }

    if ([int]$manifest.schemaVersion -ne $script:GridCapabilityManifestSchemaVersion) {
        throw "CapabilityManifestInvalid: '$path' has unsupported schemaVersion '$($manifest.schemaVersion)'."
    }
    if (@($manifest.capabilities).Count -eq 0) { throw "CapabilityManifestInvalid: '$path' contains no capabilities." }
    foreach ($contract in @($manifest.capabilities)) {
        $validation = Test-GridCapabilityContract -Contract $contract
        if (-not $validation.IsValid) {
            throw "CapabilityManifestInvalid: '$path'. $($validation.Errors -join ' ')"
        }
    }
    [pscustomobject]@{ Path = $path; Manifest = $manifest }
}

function Get-GridCapabilityRegistry {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ScriptsRoot)

    $contracts = New-Object Collections.Generic.List[object]
    $seen = @{}
    foreach ($path in @(Get-GridCapabilityManifestPaths -ScriptsRoot $ScriptsRoot)) {
        $loaded = Read-GridCapabilityManifest -LiteralPath $path
        foreach ($contract in @($loaded.Manifest.capabilities)) {
            $key = ([string]$contract.capabilityId).ToLowerInvariant()
            if ($seen.ContainsKey($key)) {
                throw "DuplicateCapabilityId: '$($contract.capabilityId)' appears in '$path' and '$($seen[$key])'."
            }
            $seen[$key] = $path
            $contract | Add-Member -NotePropertyName manifestPath -NotePropertyValue $path -Force
            $contracts.Add($contract)
        }
    }
    if ($contracts.Count -eq 0) { throw 'CapabilityRegistryEmpty: no capability manifests were found.' }

    $byId = @{}
    foreach ($contract in $contracts) { $byId[([string]$contract.capabilityId).ToLowerInvariant()] = $contract }
    foreach ($contract in $contracts) {
        foreach ($dependency in @($contract.dependencies)) {
            if (-not $byId.ContainsKey(([string]$dependency).ToLowerInvariant())) {
                throw "MissingCapabilityDependency: '$($contract.capabilityId)' depends on '$dependency'."
            }
        }
    }

    # Resolving the complete graph proves it is acyclic.
    [void](Get-GridCapabilityDependencyClosure -Registry $contracts.ToArray() -CapabilityId @($contracts | ForEach-Object capabilityId))
    $contracts.ToArray() | Sort-Object capabilityId
}

function Resolve-GridCapabilityContract {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object[]]$Registry, [Parameter(Mandatory)][string]$CapabilityId)

    $matched = @($Registry | Where-Object { [string]$_.capabilityId -ieq $CapabilityId })
    if ($matched.Count -ne 1) { throw "CapabilityNotFound: '$CapabilityId'." }
    $matched[0]
}

function Get-GridCapabilityDependencyClosure {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$Registry,
        [Parameter(Mandatory)][string[]]$CapabilityId
    )

    $selected = @{}
    $pending = New-Object Collections.Generic.List[string]
    foreach ($id in @($CapabilityId | Sort-Object -Unique)) { $pending.Add([string]$id) }
    while ($pending.Count -gt 0) {
        $id = $pending[0]
        $pending.RemoveAt(0)
        $key = $id.ToLowerInvariant()
        if ($selected.ContainsKey($key)) { continue }
        $contract = Resolve-GridCapabilityContract -Registry $Registry -CapabilityId $id
        $selected[$key] = $contract
        foreach ($dependency in @($contract.dependencies | Sort-Object -Unique)) { $pending.Add([string]$dependency) }
    }

    $remaining = @{}
    foreach ($entry in $selected.GetEnumerator()) { $remaining[$entry.Key] = $entry.Value }
    $ordered = New-Object Collections.Generic.List[object]
    while ($remaining.Count -gt 0) {
        $ready = @($remaining.Values | Where-Object {
            $contract = $_
            @($contract.dependencies | Where-Object { $remaining.ContainsKey(([string]$_).ToLowerInvariant()) }).Count -eq 0
        } | Sort-Object capabilityId)
        if ($ready.Count -eq 0) {
            throw ('CapabilityDependencyCycle: ' + (@($remaining.Values | ForEach-Object capabilityId | Sort-Object) -join ', '))
        }
        foreach ($contract in $ready) {
            $ordered.Add($contract)
            $remaining.Remove(([string]$contract.capabilityId).ToLowerInvariant())
        }
    }
    $ordered.ToArray()
}

function ConvertTo-GridCapabilityBindings {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object[]]$Contracts)

    @($Contracts | ForEach-Object {
        [pscustomobject][ordered]@{
            capabilityId = [string]$_.capabilityId
            capabilityVersion = [string]$_.capabilityVersion
        }
    })
}
