function New-GridSkyrimRecordRelationshipRequest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$BaselineCaseDirectory,
        [Parameter(Mandatory)][object[]]$Targets,
        [object[]]$AssetTargets = @(),
        [ValidateRange(1, 14400)][int]$MaximumWallClockSeconds = 3600,
        [ValidateRange(1, 100000000)][long]$MaximumRecords = 50000000,
        [ValidateRange(0, 100000)][long]$MaximumAssets = 0,
        [ValidateRange(1, 68719476736)][long]$MaximumBytes = 34359738368,
        [switch]$PassThru
    )

    $baseline = [IO.Path]::GetFullPath($BaselineCaseDirectory)
    $installationPath = Join-Path $baseline 'installation\installation-baseline.v1.json'
    $modsPath = Join-Path $baseline 'inventory\mods.v1.ndjson'
    $pluginsPath = Join-Path $baseline 'inventory\plugins.v1.ndjson'
    $winnersPath = Join-Path $baseline 'inventory\virtual-winners.v1.ndjson'
    foreach ($required in @($installationPath, $modsPath, $pluginsPath, $winnersPath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "RecordRelationshipRequestBaselineArtifactMissing: $required" }
    }

    $installation = Get-Content -LiteralPath $installationPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $rootMap = New-Object 'Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($root in @($installation.roots)) {
        if ($root.label -and $root.path) { $rootMap[[string]$root.label] = [IO.Path]::GetFullPath([string]$root.path) }
    }
    foreach ($label in @('Skyrim Data directory','Mods directory','Overwrite directory')) {
        if (-not $rootMap.ContainsKey($label)) { throw "RecordRelationshipRequestRootMissing: $label" }
    }

    $modMap = New-Object 'Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
    Get-Content -LiteralPath $modsPath -ReadCount 256 -Encoding UTF8 | ForEach-Object {
        foreach ($line in $_) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $mod = $line | ConvertFrom-Json
            if ($mod.PSObject.Properties['name'] -and $mod.PSObject.Properties['canonicalPath'] -and
                -not [string]::IsNullOrWhiteSpace([string]$mod.name) -and -not [string]::IsNullOrWhiteSpace([string]$mod.canonicalPath)) {
                $modMap[[string]$mod.name] = [IO.Path]::GetFullPath([string]$mod.canonicalPath)
            }
        }
    }

    $winnerMap = New-Object 'Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
    Get-Content -LiteralPath $winnersPath -ReadCount 2048 -Encoding UTF8 | ForEach-Object {
        foreach ($line in $_) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $winner = $line | ConvertFrom-Json
            if (-not $winner.provider.isWinner -or [string]$winner.winnerConfidence -ne 'established') { continue }
            $virtualPath = [string]$winner.virtualPath
            if ($virtualPath -notmatch '(?i)\.(esm|esp|esl)$' -or $virtualPath.Contains('\')) { continue }
            if ($winnerMap.ContainsKey($virtualPath)) { throw "RecordRelationshipRequestDuplicateWinner: $virtualPath" }
            $winnerMap[$virtualPath] = $winner.provider
        }
    }

    $pluginInputs = New-Object Collections.Generic.List[object]
    $allowedSources = New-Object Collections.Generic.List[object]
    $seenSourceOrders = New-Object 'Collections.Generic.HashSet[int]'
    Get-Content -LiteralPath $pluginsPath -ReadCount 256 -Encoding UTF8 | ForEach-Object {
        foreach ($line in $_) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $row = $line | ConvertFrom-Json
            $plugin = $row.plugin
            if ([string]$plugin.observation.fileAvailability -ne 'present') { continue }
            $name = [string]$plugin.name
            if (-not $winnerMap.ContainsKey($name)) { throw "RecordRelationshipRequestWinnerMissing: $name" }
            $provider = $winnerMap[$name]
            $kind = [string]$provider.kind
            $sourceName = [string]$provider.sourceName
            $canonicalPath = switch -Regex ($kind) {
                '^(baseGameLooseFile|unmanagedLooseFile)$' { Join-Path $rootMap['Skyrim Data directory'] $name; break }
                '^modLooseFile$' {
                    if (-not $modMap.ContainsKey($sourceName)) { throw "RecordRelationshipRequestModPathMissing: $name from $sourceName" }
                    Join-Path $modMap[$sourceName] $name
                    break
                }
                '^overwriteLooseFile$' { Join-Path $rootMap['Overwrite directory'] $name; break }
                default { throw "RecordRelationshipRequestProviderUnsupported: $name uses $kind" }
            }
            $canonicalPath = [IO.Path]::GetFullPath($canonicalPath)
            if (-not (Test-Path -LiteralPath $canonicalPath -PathType Leaf)) { throw "RecordRelationshipRequestPluginMissing: $canonicalPath" }
            $sourceOrder = [int]$plugin.sourcePriority
            if (-not $seenSourceOrders.Add($sourceOrder)) { throw "RecordRelationshipRequestSourceOrderDuplicate: $sourceOrder" }
            $loadOrder = if ($plugin.PSObject.Properties['loadOrder'] -and $null -ne $plugin.loadOrder) { [int]$plugin.loadOrder } else { $null }
            $pluginInputs.Add([pscustomobject][ordered]@{
                name = $name
                canonicalPath = $canonicalPath
                sourceOrder = $sourceOrder
                loadOrder = $loadOrder
                isEnabled = [bool]$plugin.isEnabled
            })
        }
    }
    if ($pluginInputs.Count -eq 0) { throw 'RecordRelationshipRequestHasNoPlugins.' }
    foreach ($input in @($pluginInputs | Sort-Object sourceOrder)) {
        $allowedSources.Add([pscustomobject][ordered]@{
            path = [string]$input.canonicalPath
            sha256 = (Get-FileHash -LiteralPath ([string]$input.canonicalPath) -Algorithm SHA256).Hash
        })
    }

    $request = [pscustomobject][ordered]@{
        plugins = @($pluginInputs | Sort-Object sourceOrder)
        targets = @($Targets)
        cellScope = $null
        assetTargets = @($AssetTargets)
        allowedSources = @($allowedSources | Sort-Object path)
        limits = [pscustomobject][ordered]@{
            maximumWallClockSeconds = $MaximumWallClockSeconds
            maximumRecords = $MaximumRecords
            maximumAssets = $MaximumAssets
            maximumBytes = $MaximumBytes
            recordLimits = [pscustomobject][ordered]@{
                maximumPlugins = 2048
                maximumAggregateBytesScanned = $MaximumBytes
                maximumRecordHeaders = $MaximumRecords
                maximumTargetKeys = 100000
                maximumGraphNodes = 10000
                maximumGraphDepth = 32
                maximumGroupDepth = 64
                maximumOutputRecords = 100000
                maximumRecordDataBytes = 67108864
                bufferBytes = 65536
            }
            assetLimits = [pscustomobject][ordered]@{
                maximumTargets = 4096
                maximumSingleAssetBytes = 536870912
                maximumAggregateBytes = 4294967296
                maximumArchiveMembers = 1000000
                maximumArchiveBytesScanned = 8589934592
                bufferBytes = 65536
            }
        }
    }
    if ($PassThru) { $request } else { $request | ConvertTo-Json -Depth 20 }
}
