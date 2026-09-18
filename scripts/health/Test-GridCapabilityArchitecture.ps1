#requires -Version 5.1
<#
.SYNOPSIS
Validates Grid's deterministic diagnostic capability architecture.
.DESCRIPTION
Performs a read-only inventory of scripts, capability contracts, ownership,
dependencies, imports, and production manifests. Unreadable manifests are
recorded while the remaining inventory continues. Without -PassThru, a failed
audit exits with code 1 for CI and operator use.
#>
[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [switch]$PassThru
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Grid.Capabilities.ps1')

$root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\')
$scriptsRoot = Join-Path $root 'scripts'
$violations = New-Object Collections.Generic.List[object]
$unreadable = New-Object Collections.Generic.List[object]
$contracts = New-Object Collections.Generic.List[object]
$loadedManifests = New-Object Collections.Generic.List[object]

function Get-GridArchitectureRelativePath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -ieq $root) { return '.' }
    if (-not $full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { return $full }
    $full.Substring($root.Length + 1).Replace('\', '/')
}

function Add-GridArchitectureViolation([string]$Code, [string]$Path, [string]$Message) {
    $violations.Add([pscustomobject][ordered]@{ code = $Code; path = $Path; message = $Message })
}

if (-not (Test-Path -LiteralPath $scriptsRoot -PathType Container)) {
    Add-GridArchitectureViolation 'ScriptsRootMissing' 'scripts' 'RepositoryRoot must contain scripts/.'
}

$prohibitedMods = Join-Path $scriptsRoot 'mods'
if (Test-Path -LiteralPath $prohibitedMods) {
    Add-GridArchitectureViolation 'ProhibitedRootMods' 'scripts/mods' 'Root-level scripts/mods/ is prohibited.'
}

$manifestPaths = @(Get-GridCapabilityManifestPaths -ScriptsRoot $scriptsRoot)
foreach ($path in $manifestPaths) {
    try {
        $loaded = Read-GridCapabilityManifest -LiteralPath $path
        $loadedManifests.Add($loaded)
        foreach ($contract in @($loaded.Manifest.capabilities)) {
            $contract | Add-Member -NotePropertyName manifestPath -NotePropertyValue $path -Force
            $contracts.Add($contract)
        }
    }
    catch {
        $relative = Get-GridArchitectureRelativePath $path
        $item = [pscustomobject][ordered]@{ path = $relative; error = $_.Exception.Message }
        $unreadable.Add($item)
        Add-GridArchitectureViolation 'ManifestUnreadable' $relative $_.Exception.Message
    }
}
if ($manifestPaths.Count -eq 0) { Add-GridArchitectureViolation 'CapabilityRegistryEmpty' 'scripts' 'No production capability manifests were found.' }

$byId = @{}
foreach ($contract in $contracts) {
    $id = [string]$contract.capabilityId
    $key = $id.ToLowerInvariant()
    if ($byId.ContainsKey($key)) {
        Add-GridArchitectureViolation 'DuplicateCapabilityId' (Get-GridArchitectureRelativePath $contract.manifestPath) "Duplicate capabilityId '$id'."
    }
    else { $byId[$key] = $contract }
}
foreach ($contract in $contracts) {
    foreach ($dependency in @($contract.dependencies)) {
        if (-not $byId.ContainsKey(([string]$dependency).ToLowerInvariant())) {
            Add-GridArchitectureViolation 'MissingCapabilityDependency' (Get-GridArchitectureRelativePath $contract.manifestPath) "'$($contract.capabilityId)' depends on missing '$dependency'."
        }
    }
}
if (@($violations | Where-Object { $_.code -in @('DuplicateCapabilityId','MissingCapabilityDependency') }).Count -eq 0 -and $contracts.Count -gt 0) {
    try { [void](Get-GridCapabilityDependencyClosure -Registry $contracts.ToArray() -CapabilityId @($contracts | ForEach-Object capabilityId)) }
    catch { Add-GridArchitectureViolation 'CapabilityDependencyCycle' 'scripts' $_.Exception.Message }
}

$implementationMap = @{}
$supportingMap = @{}
foreach ($loaded in $loadedManifests) {
    foreach ($supporting in @($loaded.Manifest.supportingFiles)) {
        $key = ([string]$supporting).Replace('\','/').ToLowerInvariant()
        $supportingMap[$key] = $true
        $full = [IO.Path]::GetFullPath((Join-Path $root ([string]$supporting)))
        if (-not $full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $full -PathType Leaf)) {
            Add-GridArchitectureViolation 'MissingSupportingFile' ([string]$supporting) 'A declared supporting file is missing or escapes the repository.'
        }
    }
}
foreach ($contract in $contracts) {
    $ownerKind = [string]$contract.ownerScope.kind
    $gameId = if ($contract.ownerScope.PSObject.Properties['gameId']) { [string]$contract.ownerScope.gameId } else { '' }
    $modId = if ($contract.ownerScope.PSObject.Properties['modId']) { [string]$contract.ownerScope.modId } else { '' }
    foreach ($implementation in @($contract.implementation.files)) {
        $relative = ([string]$implementation).Replace('\','/')
        $key = $relative.ToLowerInvariant()
        if (-not $implementationMap.ContainsKey($key)) { $implementationMap[$key] = New-Object Collections.Generic.List[string] }
        $implementationMap[$key].Add([string]$contract.capabilityId)
        $full = [IO.Path]::GetFullPath((Join-Path $root $relative))
        if (-not $full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $full -PathType Leaf)) {
            Add-GridArchitectureViolation 'MissingImplementation' $relative "Capability '$($contract.capabilityId)' implementation is missing or escapes the repository."
            continue
        }
        $expectedPrefix = switch ($ownerKind) {
            'SharedHealth' { 'scripts/health/' }
            'Game' { "scripts/games/$gameId/" }
            'Mod' { "scripts/games/$gameId/mods/$modId/" }
        }
        if (-not $relative.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            $code = if ($ownerKind -eq 'Mod') { 'MisScopedModCapability' } else { 'OwnerScopeMismatch' }
            Add-GridArchitectureViolation $code $relative "Capability '$($contract.capabilityId)' must be implemented beneath '$expectedPrefix'."
        }
    }
}

# Production manifests may define shapes for runtime values, but may not embed
# concrete machine/case values. Known historical identities are also rejected.
$productionManifestPaths = @(
    Get-ChildItem -LiteralPath $scriptsRoot -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object {
            $_.FullName -notmatch '[\\/]tests[\\/]' -and
            ($_.Name -eq 'capabilities.v1.json' -or $_.Name -eq 'adapter.psd1' -or $_.Name -eq 'class.v1.json' -or $_.Name -like '*.manifest.json')
        }
)
foreach ($manifestFile in $productionManifestPaths) {
    try { $raw = Get-Content -LiteralPath $manifestFile.FullName -Raw -ErrorAction Stop }
    catch {
        $relative = Get-GridArchitectureRelativePath $manifestFile.FullName
        $unreadable.Add([pscustomobject][ordered]@{ path = $relative; error = $_.Exception.Message })
        Add-GridArchitectureViolation 'ManifestUnreadable' $relative $_.Exception.Message
        continue
    }
    $relative = Get-GridArchitectureRelativePath $manifestFile.FullName
    if ($raw -match '"[A-Za-z]:[\\/][^"\r\n]+"') {
        Add-GridArchitectureViolation 'ConcreteCaseData' $relative 'Production manifest contains an absolute machine path.'
    }
    if ($raw -match '(?i)"[^"\r\n]+\.(esp|esm|esl)"') {
        Add-GridArchitectureViolation 'ConcreteCaseData' $relative 'Production manifest contains a concrete plugin identity.'
    }
    if ($raw -match '(?i)"(?:0x)?[0-9a-f]{8}"') {
        Add-GridArchitectureViolation 'ConcreteCaseData' $relative 'Production manifest contains a concrete FormID.'
    }
}

foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $scriptsRoot 'health') -Recurse -File -Include *.ps1,*.psm1 -ErrorAction SilentlyContinue)) {
    if ($file.FullName -match '[\\/]tests[\\/]') { continue }
    $source = Get-Content -LiteralPath $file.FullName -Raw
    if ($source -match '(?im)^\s*(?:Import-Module|\.)[^\r\n]*scripts[\\/]games[\\/]') {
        Add-GridArchitectureViolation 'SharedDirectGameImport' (Get-GridArchitectureRelativePath $file.FullName) 'Shared health code directly imports a game implementation instead of resolving a manifest capability.'
    }
}

$inventory = New-Object Collections.Generic.List[object]
$productionExtensions = @('.ps1','.psm1','.psd1','.pas','.json')
foreach ($file in @(Get-ChildItem -LiteralPath $scriptsRoot -Recurse -File -ErrorAction SilentlyContinue | Sort-Object FullName)) {
    $relative = Get-GridArchitectureRelativePath $file.FullName
    $key = $relative.ToLowerInvariant()
    $classification = $null
    $capabilityIds = @()
    if ($relative -match '/tests/') { $classification = 'FixtureOrTest' }
    elseif ($relative -match '/schemas/') { $classification = 'Schema' }
    elseif ($file.Name -eq 'capabilities.v1.json') { $classification = 'CapabilityManifest' }
    elseif ($file.Name -eq 'adapter.psd1') { $classification = 'AdapterBinding' }
    elseif ($file.Name -eq 'class.v1.json' -and $relative -match '^scripts/health/classes/[^/]+/class\.v1\.json$') { $classification = 'ClassRecipe' }
    elseif ($file.Name -eq 'user-data-roots.v1.json' -and $relative -match '^scripts/games/[^/]+/user-data-roots\.v1\.json$') { $classification = 'GameUserDataRootDefinition' }
    elseif ($file.Extension -eq '.md') { $classification = 'Documentation' }
    elseif ($implementationMap.ContainsKey($key)) { $classification = 'CapabilityImplementation'; $capabilityIds = @($implementationMap[$key] | Sort-Object -Unique) }
    elseif ($supportingMap.ContainsKey($key)) { $classification = 'SupportingImplementation' }
    else { $classification = 'Unclassified' }

    $inventory.Add([pscustomobject][ordered]@{
        path = $relative
        classification = $classification
        capabilityIds = $capabilityIds
        size = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    })
    if ($classification -eq 'Unclassified' -and $file.Extension.ToLowerInvariant() -in $productionExtensions) {
        Add-GridArchitectureViolation 'UnclassifiedProductionFile' $relative 'Production script/native/manifest file is not classified by a capability or supporting-file declaration.'
    }
}

$orderedViolations = @($violations | Sort-Object code, path, message)
$result = [pscustomobject][ordered]@{
    schemaVersion = 1
    status = if ($orderedViolations.Count -eq 0) { 'Passed' } elseif ($unreadable.Count -gt 0) { 'Incomplete' } else { 'Failed' }
    repositoryRoot = $root
    capabilityCount = $contracts.Count
    manifestCount = $manifestPaths.Count
    inventory = $inventory.ToArray()
    violations = $orderedViolations
    unreadableFiles = @($unreadable | Sort-Object path)
}

if ($PassThru) { $result; return }
$result | ConvertTo-Json -Depth 12
if ($result.status -ne 'Passed') { exit 1 }
