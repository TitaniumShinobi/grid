<#
.SYNOPSIS
Builds a minimal, case-local xEdit plugin list for a Skyrim diagnostic.
.DESCRIPTION
Seeds the selection only from exact, case-insensitive plugin identities in the
case InvestigationPlan -- never from a filename or keyword regex. Reads bounded TES4 headers to calculate the
recursive required-master closure, preserves the active MO2 load order, and
writes plugins.txt, loadorder.txt, and JSON evidence beneath the diagnostic
case. It does not modify MO2 profile files.
#>
function Get-GridTes4MasterNames {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$PluginPath,
        [int]$MaximumHeaderBytes = 16777216
    )

    $stream = [IO.File]::Open($PluginPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        if ($stream.Length -lt 24) { throw "TES4 header is truncated: $PluginPath" }
        $reader = New-Object IO.BinaryReader($stream, [Text.Encoding]::ASCII, $true)
        try {
            $signature = [Text.Encoding]::ASCII.GetString($reader.ReadBytes(4))
            if ($signature -ne 'TES4') { throw "Plugin does not begin with a TES4 record: $PluginPath" }
            $dataSize = $reader.ReadUInt32()
            [void]$reader.ReadBytes(16)
            if ($dataSize -gt $MaximumHeaderBytes) { throw "TES4 header exceeds the $MaximumHeaderBytes-byte safety limit: $PluginPath" }
            if ((24L + [long]$dataSize) -gt $stream.Length) { throw "TES4 header extends beyond the plugin: $PluginPath" }
            $data = $reader.ReadBytes([int]$dataSize)
            if ($data.Length -ne [int]$dataSize) { throw "TES4 header could not be read completely: $PluginPath" }
        }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }

    $latin1 = [Text.Encoding]::GetEncoding(28591)
    $masters = New-Object Collections.Generic.List[string]
    $position = 0
    $extendedSize = $null
    while ($position -lt $data.Length) {
        if (($data.Length - $position) -lt 6) { throw "TES4 subrecord header is truncated: $PluginPath" }
        $subrecord = [Text.Encoding]::ASCII.GetString($data, $position, 4)
        $shortSize = [BitConverter]::ToUInt16($data, $position + 4)
        $position += 6
        $size = if ($null -ne $extendedSize) { [uint32]$extendedSize } else { [uint32]$shortSize }
        $extendedSize = $null
        if ($size -gt [int]::MaxValue -or ([long]$position + [long]$size) -gt $data.Length) {
            throw "TES4 subrecord extends beyond the header: $PluginPath"
        }
        if ($subrecord -eq 'XXXX') {
            if ($size -ne 4) { throw "TES4 XXXX subrecord is malformed: $PluginPath" }
            $extendedSize = [BitConverter]::ToUInt32($data, $position)
            $position += 4
            continue
        }
        if ($subrecord -eq 'MAST') {
            $nameBytes = New-Object byte[] ([int]$size)
            [Array]::Copy($data, $position, $nameBytes, 0, [int]$size)
            $nullIndex = [Array]::IndexOf($nameBytes, [byte]0)
            $length = if ($nullIndex -ge 0) { $nullIndex } else { $nameBytes.Length }
            $name = $latin1.GetString($nameBytes, 0, $length).Trim()
            if ([string]::IsNullOrWhiteSpace($name) -or $name.IndexOfAny([char[]]@(0, 10, 13)) -ge 0) {
                throw "TES4 MAST subrecord contains an invalid plugin name: $PluginPath"
            }
            $masters.Add($name)
        }
        $position += [int]$size
    }
    if ($null -ne $extendedSize) { throw "TES4 XXXX subrecord has no following subrecord: $PluginPath" }
    return @($masters)
}

function Test-GridOfficialSkyrimMasterName {
    param([Parameter(Mandatory)][string]$Name)
    return $Name -match '(?i)^(Skyrim|Update|Dawnguard|HearthFires|Dragonborn)\.esm$' -or
        $Name -match '(?i)^cc.+\.(esm|esl)$' -or
        $Name -match '(?i)^(_ResourcePack|MarketplaceTextures)\.esl$'
}

function Resolve-GridPluginDependencyClosure {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$Targets,
        [Parameter(Mandatory)][hashtable]$PluginPaths,
        [Parameter(Mandatory)][string[]]$LoadOrder,
        [Parameter(Mandatory)][string[]]$ActivePlugins
    )

    $pathByName = @{}
    foreach ($entry in $PluginPaths.GetEnumerator()) { $pathByName[[string]$entry.Key] = [string]$entry.Value }
    $active = @{}
    foreach ($name in $ActivePlugins) { $active[$name] = $true }
    $selected = @{}
    $graph = [ordered]@{}
    $pending = New-Object Collections.Generic.Queue[string]
    foreach ($target in $Targets) {
        if (-not $active.ContainsKey($target)) { throw "Target plugin is inactive or absent from the active profile: $target" }
        $pending.Enqueue($target)
    }

    while ($pending.Count -gt 0) {
        $plugin = $pending.Dequeue()
        if ($selected.ContainsKey($plugin)) { continue }
        if (-not $pathByName.ContainsKey($plugin) -or -not (Test-Path -LiteralPath $pathByName[$plugin] -PathType Leaf)) {
            throw "Required plugin file could not be resolved through the active MO2 providers: $plugin"
        }
        $selected[$plugin] = $true
        $masters = @(Get-GridTes4MasterNames -PluginPath $pathByName[$plugin])
        $graph[$plugin] = $masters
        foreach ($master in $masters) {
            if (-not $pathByName.ContainsKey($master) -or -not (Test-Path -LiteralPath $pathByName[$master] -PathType Leaf)) {
                throw "Required master '$master' referenced by '$plugin' could not be found."
            }
            if (-not $active.ContainsKey($master) -and -not (Test-GridOfficialSkyrimMasterName $master)) {
                throw "Required master '$master' referenced by '$plugin' is not active."
            }
            if (-not $selected.ContainsKey($master)) { $pending.Enqueue($master) }
        }
    }

    $orderIndex = @{}
    for ($index = 0; $index -lt $LoadOrder.Count; $index++) {
        if (-not $orderIndex.ContainsKey($LoadOrder[$index])) { $orderIndex[$LoadOrder[$index]] = $index }
    }
    $officialOrder = @('Skyrim.esm', 'Update.esm', 'Dawnguard.esm', 'HearthFires.esm', 'Dragonborn.esm', '_ResourcePack.esl', 'MarketplaceTextures.esl')
    $ordered = @($selected.Keys | Sort-Object -Property @{ Expression = {
        if ($orderIndex.ContainsKey($_)) { return $orderIndex[$_] }
        $officialIndex = [Array]::IndexOf($officialOrder, $_)
        if ($officialIndex -ge 0) { return (-100 + $officialIndex) }
        throw "Selected plugin is absent from the active profile load order: $_"
    } }, @{ Expression = { $_ } })

    [pscustomobject]@{
        Plugins = $ordered
        Targets = @($Targets)
        MasterGraph = $graph
    }
}

function New-GridXEditPluginSelection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Context,
        [Parameter(Mandatory)][string]$CaseDirectory,
        [Parameter(Mandatory)][string[]]$Targets
    )

    $requestedTargets = @($Targets | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
    if ($requestedTargets.Count -eq 0) { throw 'New-GridXEditPluginSelection requires at least one requested target plugin identity (from the case InvestigationPlan).' }

    # Exact, case-insensitive identity matching only: no substring, regex, or filename-keyword matching.
    $activeTargets = New-Object Collections.Generic.List[string]
    $inactiveTargets = New-Object Collections.Generic.List[string]
    $missingTargets = New-Object Collections.Generic.List[string]
    foreach ($requested in $requestedTargets) {
        $activeMatch = @($Context.ActivePlugins | Where-Object { $_ -ieq $requested } | Select-Object -First 1)
        if ($activeMatch.Count -gt 0) { $activeTargets.Add($activeMatch[0]); continue }
        $inactiveMatch = @($Context.InactivePlugins | Where-Object { $_ -ieq $requested } | Select-Object -First 1)
        if ($inactiveMatch.Count -gt 0) { $inactiveTargets.Add($inactiveMatch[0]); continue }
        $missingTargets.Add($requested)
    }
    if ($activeTargets.Count -eq 0) { throw 'No requested target plugin identity is active in this MO2 profile.' }

    $closure = Resolve-GridPluginDependencyClosure -Targets $activeTargets.ToArray() -PluginPaths $Context.PluginPaths `
        -LoadOrder $Context.LoadOrder -ActivePlugins $Context.ActivePlugins
    $mastersAdded = @($closure.Plugins | Where-Object { $_ -notin $activeTargets.ToArray() })

    $selectionDirectory = Join-Path $CaseDirectory 'xedit-plugin-selection'
    New-Item -ItemType Directory -Path $selectionDirectory -Force | Out-Null
    $pluginsPath = Join-Path $selectionDirectory 'plugins.txt'
    $loadOrderPath = Join-Path $selectionDirectory 'loadorder.txt'
    @($closure.Plugins | ForEach-Object { "*$_" }) | Set-Content -LiteralPath $pluginsPath -Encoding UTF8
    @($closure.Plugins) | Set-Content -LiteralPath $loadOrderPath -Encoding UTF8

    $selectionEvidencePath = Join-Path $selectionDirectory 'target-selection-evidence.json'
    $selectionEvidence = [ordered]@{
        SchemaVersion = 1
        CreatedAt = (Get-Date).ToString('o')
        RequestedTargets = @($requestedTargets)
        ActiveTargets = $activeTargets.ToArray()
        InactiveTargets = $inactiveTargets.ToArray()
        MissingTargets = $missingTargets.ToArray()
        MastersAddedByClosure = @($mastersAdded)
        FinalOrderedPluginCount = @($closure.Plugins).Count
    }
    $selectionEvidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $selectionEvidencePath -Encoding UTF8

    $evidencePath = Join-Path $selectionDirectory 'selection-evidence.json'
    $evidence = [ordered]@{
        SchemaVersion = 4
        CreatedAt = (Get-Date).ToString('o')
        ContextFingerprint = $Context.Fingerprint
        Targets = @($closure.Targets)
        Plugins = @($closure.Plugins)
        MasterGraph = $closure.MasterGraph
        PluginsFile = $pluginsPath
        LoadOrderFile = $loadOrderPath
        FullProfilePluginCount = @($Context.ActivePlugins).Count
        RequestedTargetCount = @($requestedTargets).Count
        ActiveTargetCount = $activeTargets.Count
        InactiveTargetCount = $inactiveTargets.Count
        MissingTargetCount = $missingTargets.Count
        SelectedPluginCount = @($closure.Plugins).Count
        UsesProfileOverride = $true
        TargetSelectionEvidenceFile = $selectionEvidencePath
    }
    $evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $evidencePath -Encoding UTF8
    [pscustomobject]@{
        PluginsPath = $pluginsPath
        LoadOrderPath = $loadOrderPath
        EvidencePath = $evidencePath
        SelectionEvidencePath = $selectionEvidencePath
        Plugins = @($closure.Plugins)
        Targets = @($closure.Targets)
    }
}
