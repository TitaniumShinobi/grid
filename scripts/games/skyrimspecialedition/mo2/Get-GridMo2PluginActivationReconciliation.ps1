[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProfileDirectory,

    [Parameter(Mandatory = $true)]
    [string]$GameDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$Claim = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-GridSha256 {
    param([Parameter(Mandatory = $true)][string]$LiteralPath)
    return (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256).Hash
}

function Get-GridMeaningfulLines {
    param([Parameter(Mandatory = $true)][string[]]$Lines)
    return @($Lines | ForEach-Object {
        $value = $_.Trim()
        if ($value.Length -gt 0 -and -not $value.StartsWith('#', [StringComparison]::Ordinal)) {
            $value
        }
    })
}

$profile = [IO.Path]::GetFullPath($ProfileDirectory)
$game = [IO.Path]::GetFullPath($GameDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $profile -PathType Container)) { throw "ProfileDirectoryNotFound: $profile" }
if (-not (Test-Path -LiteralPath $game -PathType Container)) { throw "GameDirectoryNotFound: $game" }

$pluginsPath = Join-Path $profile 'plugins.txt'
$loadOrderPath = Join-Path $profile 'loadorder.txt'
$creationManifestPath = Join-Path $game 'Skyrim.ccc'
foreach ($path in @($pluginsPath, $loadOrderPath, $creationManifestPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "RequiredEvidenceNotFound: $path" }
}

$sourceHashesBefore = [ordered]@{
    pluginsTxt = Get-GridSha256 $pluginsPath
    loadorderTxt = Get-GridSha256 $loadOrderPath
    skyrimCcc = Get-GridSha256 $creationManifestPath
}

$pluginLines = Get-GridMeaningfulLines ([IO.File]::ReadAllLines($pluginsPath, [Text.Encoding]::Default))
$loadOrderLines = Get-GridMeaningfulLines ([IO.File]::ReadAllLines($loadOrderPath, [Text.UTF8Encoding]::new($false, $true)))
$creationLines = Get-GridMeaningfulLines ([IO.File]::ReadAllLines($creationManifestPath, [Text.UTF8Encoding]::new($false, $true)))

$comparer = [StringComparer]::OrdinalIgnoreCase
$pluginStates = [Collections.Generic.Dictionary[string, string]]::new($comparer)
foreach ($line in $pluginLines) {
    $enabled = $line.StartsWith('*', [StringComparison]::Ordinal)
    $name = if ($enabled) { $line.Substring(1) } else { $line }
    if ($pluginStates.ContainsKey($name)) { throw "DuplicatePluginsTxtRecord: $name" }
    $pluginStates.Add($name, $(if ($enabled) { 'ExplicitEnabled' } else { 'ExplicitDisabled' }))
}

$loadOrder = [Collections.Generic.Dictionary[string, int]]::new($comparer)
for ($index = 0; $index -lt $loadOrderLines.Count; $index++) {
    $name = $loadOrderLines[$index]
    if ($loadOrder.ContainsKey($name)) { throw "DuplicateLoadOrderRecord: $name" }
    $loadOrder.Add($name, $index)
}

$creationPlugins = [Collections.Generic.HashSet[string]]::new($comparer)
foreach ($name in $creationLines) {
    if (-not $creationPlugins.Add($name)) { throw "DuplicateCreationManifestRecord: $name" }
}
$corePlugins = [Collections.Generic.HashSet[string]]::new($comparer)
foreach ($name in @('Skyrim.esm', 'Update.esm', 'Dawnguard.esm', 'HearthFires.esm', 'Dragonborn.esm')) {
    [void]$corePlugins.Add($name)
}

$allNames = [Collections.Generic.List[string]]::new()
$seen = [Collections.Generic.HashSet[string]]::new($comparer)
foreach ($name in $loadOrderLines) { if ($seen.Add($name)) { $allNames.Add($name) } }
foreach ($line in $pluginLines) {
    $name = if ($line.StartsWith('*', [StringComparison]::Ordinal)) { $line.Substring(1) } else { $line }
    if ($seen.Add($name)) { $allNames.Add($name) }
}
foreach ($name in $creationLines) { if ($seen.Add($name)) { $allNames.Add($name) } }
foreach ($name in $corePlugins) { if ($seen.Add($name)) { $allNames.Add($name) } }

$records = @($allNames | ForEach-Object {
    $name = $_
    $pluginState = if ($pluginStates.ContainsKey($name)) { $pluginStates[$name] } else { 'Absent' }
    $implicitSource = if ($corePlugins.Contains($name)) {
        'CoreGame'
    } elseif ($creationPlugins.Contains($name)) {
        'Skyrim.ccc'
    } else {
        'None'
    }
    $implicitActive = $implicitSource -ne 'None'
    $mo2Active = $pluginState -eq 'ExplicitEnabled' -or $implicitActive
    $gridProjected = $loadOrder.ContainsKey($name) -and ($pluginState -ne 'Absent' -or $implicitActive)
    $gridActive = $gridProjected -and $mo2Active
    $difference = if (-not $gridProjected) {
        'MissingFromGridProjection'
    } elseif ($gridActive -ne $mo2Active) {
        'ActivationMismatch'
    } elseif ($mo2Active) {
        'MatchActive'
    } else {
        'MatchInactive'
    }
    [pscustomobject][ordered]@{
        loadOrderIndex = if ($loadOrder.ContainsKey($name)) { $loadOrder[$name] } else { $null }
        pluginName = $name
        pluginsTxtState = $pluginState
        implicitSource = $implicitSource
        inCreationManifest = $creationPlugins.Contains($name)
        inLoadOrder = $loadOrder.ContainsKey($name)
        mo2Active = $mo2Active
        gridProjected = $gridProjected
        gridActive = $gridActive
        difference = $difference
    }
})

$sourceHashesAfter = [ordered]@{
    pluginsTxt = Get-GridSha256 $pluginsPath
    loadorderTxt = Get-GridSha256 $loadOrderPath
    skyrimCcc = Get-GridSha256 $creationManifestPath
}
foreach ($key in $sourceHashesBefore.Keys) {
    if ($sourceHashesBefore[$key] -cne $sourceHashesAfter[$key]) { throw "EvidenceChangedDuringRead: $key" }
}

$summary = [pscustomobject][ordered]@{
    schemaVersion = 1
    collector = 'grid.skyrimspecialedition.mo2.plugin-activation-reconciliation.v1'
    collectedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    claim = $Claim
    profileDirectory = $profile
    gameDirectory = $game
    sourceHashes = $sourceHashesAfter
    counts = [pscustomobject][ordered]@{
        mo2Active = @($records | Where-Object mo2Active).Count
        explicitEnabled = @($records | Where-Object { $_.pluginsTxtState -eq 'ExplicitEnabled' }).Count
        implicitActive = @($records | Where-Object { $_.mo2Active -and $_.pluginsTxtState -ne 'ExplicitEnabled' }).Count
        implicitCore = @($records | Where-Object { $_.implicitSource -eq 'CoreGame' }).Count
        implicitCreationManifest = @($records | Where-Object { $_.implicitSource -eq 'Skyrim.ccc' }).Count
        explicitDisabled = @($records | Where-Object { $_.pluginsTxtState -eq 'ExplicitDisabled' }).Count
        gridProjectedTotal = @($records | Where-Object gridProjected).Count
        gridProjectedActive = @($records | Where-Object gridActive).Count
        missingFromGridProjection = @($records | Where-Object { $_.difference -eq 'MissingFromGridProjection' }).Count
        activationMismatches = @($records | Where-Object { $_.difference -eq 'ActivationMismatch' }).Count
    }
}

[IO.Directory]::CreateDirectory($output) | Out-Null
$recordsPath = Join-Path $output 'plugin-reconciliation.csv'
$activePath = Join-Path $output 'mo2-active-membership.txt'
$summaryPath = Join-Path $output 'summary.json'
$records | Export-Csv -LiteralPath $recordsPath -NoTypeInformation -Encoding UTF8
[IO.File]::WriteAllLines($activePath, @($records | Where-Object mo2Active | ForEach-Object pluginName), [Text.UTF8Encoding]::new($false))
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

[pscustomobject][ordered]@{
    summaryPath = $summaryPath
    recordsPath = $recordsPath
    activeMembershipPath = $activePath
    summarySha256 = Get-GridSha256 $summaryPath
    recordsSha256 = Get-GridSha256 $recordsPath
    activeMembershipSha256 = Get-GridSha256 $activePath
    counts = $summary.counts
}
