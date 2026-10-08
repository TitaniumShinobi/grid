#requires -Version 5.1
<#
.SYNOPSIS
Loads and plans Contract 2 relationship-registration MDBO capabilities.
.DESCRIPTION
The health MDBO registry is the authoritative manifest for registration-engine
capabilities. Request planning and AUTO composition must resolve goals through
this registry rather than ad hoc embedded capability lists.
#>

$script:GridMdboCapabilityManifestSchemaVersion = 1

function Get-GridMdboCapabilityManifestPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ScriptsRoot)
    [IO.Path]::GetFullPath((Join-Path $ScriptsRoot 'health\mdbo-capabilities.v1.json'))
}

function Test-GridMdboCapabilityContract {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Contract, [Parameter(Mandatory)][string]$RegistrationEngineRoot)

    $errors = New-Object Collections.Generic.List[string]
    foreach ($field in @('capabilityId', 'capabilityVersion', 'semanticParent', 'realization', 'sufficiency', 'verificationContract')) {
        if ($null -eq $Contract.PSObject.Properties[$field]) { $errors.Add("Missing MDBO capability field: $field") }
    }
    if ($errors.Count -gt 0) { return [pscustomobject]@{ IsValid = $false; Errors = @($errors) } }

    $id = [string]$Contract.capabilityId
    if ($id -notmatch '^grid\.[a-z0-9]+(?:[.-][a-z0-9]+)*$') { $errors.Add("Invalid capabilityId '$id'.") }
    if ([string]$Contract.capabilityVersion -notmatch '^\d+\.\d+\.\d+$') { $errors.Add("Capability '$id' has an invalid semantic version.") }
    if ([string]::IsNullOrWhiteSpace([string]$Contract.semanticParent)) { $errors.Add("Capability '$id' requires semanticParent.") }
    if ([string]::IsNullOrWhiteSpace([string]$Contract.realization)) { $errors.Add("Capability '$id' requires realization.") }

    [pscustomobject]@{ IsValid = ($errors.Count -eq 0); Errors = @($errors) }
}

function Read-GridMdboCapabilityManifest {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$LiteralPath)

    $path = [IO.Path]::GetFullPath($LiteralPath)
    try { $manifest = Get-Content -LiteralPath $path -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "MdboCapabilityManifestUnreadable: '$path'. $($_.Exception.Message)" }

    if ([int]$manifest.schemaVersion -ne $script:GridMdboCapabilityManifestSchemaVersion) {
        throw "MdboCapabilityManifestInvalid: '$path' has unsupported schemaVersion '$($manifest.schemaVersion)'."
    }
    if (@($manifest.capabilities).Count -eq 0) { throw "MdboCapabilityManifestInvalid: '$path' contains no capabilities." }
    $root = [string]$manifest.registrationEngineRoot
    if ([string]::IsNullOrWhiteSpace($root)) { throw "MdboCapabilityManifestInvalid: '$path' requires registrationEngineRoot." }

    foreach ($contract in @($manifest.capabilities)) {
        $validation = Test-GridMdboCapabilityContract -Contract $contract -RegistrationEngineRoot $root
        if (-not $validation.IsValid) {
            throw "MdboCapabilityManifestInvalid: '$path'. $($validation.Errors -join ' ')"
        }
    }
    [pscustomobject]@{ Path = $path; Manifest = $manifest }
}

function Get-GridMdboCapabilityRegistry {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ScriptsRoot)

    $loaded = Read-GridMdboCapabilityManifest -LiteralPath (Get-GridMdboCapabilityManifestPath -ScriptsRoot $ScriptsRoot)
    $byId = @{}
    foreach ($contract in @($loaded.Manifest.capabilities)) {
        $key = ([string]$contract.capabilityId).ToLowerInvariant()
        if ($byId.ContainsKey($key)) { throw "DuplicateMdboCapabilityId: '$($contract.capabilityId)'." }
        $contract | Add-Member -NotePropertyName manifestPath -NotePropertyValue $loaded.Path -Force
        $byId[$key] = $contract
    }

    $root = [string]$loaded.Manifest.registrationEngineRoot
    foreach ($contract in $byId.Values) {
        $parent = [string]$contract.semanticParent
        if ($parent -eq $root) { continue }
        if (-not $byId.ContainsKey($parent.ToLowerInvariant())) {
            throw "MdboCapabilityInvalidParent: '$($contract.capabilityId)' references unknown semantic parent '$parent'."
        }
    }

    @($byId.Values | Sort-Object capabilityId)
}

function Get-GridMdboExecutionPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$Registry,
        [Parameter(Mandatory)][string[]]$GoalCapabilityIds
    )

    $byId = @{}
    foreach ($contract in @($Registry)) {
        $byId[([string]$contract.capabilityId).ToLowerInvariant()] = $contract
    }

    $selected = @{}
    $pending = New-Object Collections.Generic.List[string]
    foreach ($goal in @($GoalCapabilityIds | Sort-Object -Unique)) {
        $key = ([string]$goal).ToLowerInvariant()
        if (-not $byId.ContainsKey($key)) { throw "MdboCapabilityNotRegistered: '$goal'." }
        $pending.Add($key)
    }

    while ($pending.Count -gt 0) {
        $key = $pending[0]
        $pending.RemoveAt(0)
        if ($selected.ContainsKey($key)) { continue }
        $contract = $byId[$key]
        $selected[$key] = $contract
        $parent = ([string]$contract.semanticParent).ToLowerInvariant()
        if ($byId.ContainsKey($parent)) { $pending.Add($parent) }
    }

    $remaining = @{}
    foreach ($entry in $selected.GetEnumerator()) { $remaining[$entry.Key] = $entry.Value }
    $ordered = New-Object Collections.Generic.List[string]
    while ($remaining.Count -gt 0) {
        $ready = @($remaining.GetEnumerator() | Where-Object {
            $parent = ([string]$_.Value.semanticParent).ToLowerInvariant()
            -not $remaining.ContainsKey($parent)
        } | Sort-Object { [string]$_.Value.capabilityId })
        if ($ready.Count -eq 0) {
            throw ('MdboCapabilityDependencyCycle: ' + (@($remaining.Values | ForEach-Object capabilityId | Sort-Object) -join ', '))
        }
        foreach ($entry in $ready) {
            [void]$ordered.Add([string]$entry.Value.capabilityId)
            $remaining.Remove($entry.Key)
        }
    }
    $ordered.ToArray()
}

function Resolve-GridMdboRegistrationPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ScriptsRoot,
        [Parameter(Mandatory)][string[]]$GoalCapabilityIds
    )

    $registry = @(Get-GridMdboCapabilityRegistry -ScriptsRoot $ScriptsRoot)
    $plan = @(Get-GridMdboExecutionPlan -Registry $registry -GoalCapabilityIds $GoalCapabilityIds)
    [pscustomobject][ordered]@{
        schemaVersion = 1
        goalCapabilityIds = @($GoalCapabilityIds | Sort-Object -Unique)
        executionOrder = @($plan)
        capabilityCount = @($registry).Count
        manifestPath = [string]$registry[0].manifestPath
    }
}
