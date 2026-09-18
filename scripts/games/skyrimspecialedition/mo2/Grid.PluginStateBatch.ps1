#requires -Version 5.1

<#!
.SYNOPSIS
Read-only primitives shared by Grid's atomic MO2 plugin-state batch lifecycle.
.DESCRIPTION
Resolves the current MO2 winning plugin files, parses bounded TES4 headers,
computes dependency closure and slot use, and preserves the exact byte encoding
of plugins.txt. This file contains no profile mutation entry point.
#>

Set-StrictMode -Version Latest

function Get-GridPluginStateBatchSpecificationHash {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Specification)
    if (-not (Get-Command Get-GridCanonicalJsonSha256 -ErrorAction SilentlyContinue)) {
        throw 'PluginBatchHealthModuleRequired: import Grid.Health.psm1 before hashing a specification.'
    }
    $payload = [pscustomobject][ordered]@{
        schemaVersion = [int]$Specification.schemaVersion
        specificationId = [string]$Specification.specificationId
        caseId = [string]$Specification.caseId
        baseline = $Specification.baseline
        contextFingerprint = [string]$Specification.contextFingerprint
        evidenceFingerprint = [string]$Specification.evidenceFingerprint
        supportingEvidenceIds = @($Specification.supportingEvidenceIds)
        paths = $Specification.paths
        requestedPlugins = @($Specification.requestedPlugins)
        dependencyClosure = @($Specification.dependencyClosure)
        operations = @($Specification.operations)
        protectedState = $Specification.protectedState
        slotUsage = $Specification.slotUsage
        authorization = $Specification.authorization
        exclusions = @($Specification.exclusions)
    }
    Get-GridCanonicalJsonSha256 -InputObject $payload
}

function Get-GridPluginStateBatchSha256Bytes {
    param([Parameter(Mandatory)][byte[]]$Bytes)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace('-', '') }
    finally { $algorithm.Dispose() }
}

function Get-GridPluginStateBatchSha256File {
    param([Parameter(Mandatory)][string]$LiteralPath)
    (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256 -ErrorAction Stop).Hash.ToUpperInvariant()
}

function Assert-GridPluginStateBatchLeafName {
    param([Parameter(Mandatory)][string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name) -or [IO.Path]::GetFileName($Name) -cne $Name -or
        [IO.Path]::GetExtension($Name) -notin @('.esp', '.esm', '.esl') -or
        $Name.IndexOfAny([char[]]@(0, 10, 13)) -ge 0) {
        throw "PluginIdentityInvalid: '$Name' must be one .esp, .esm, or .esl filename."
    }
}

function Get-GridPluginStateBatchProfilePaths {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Mo2Root,
        [Parameter(Mandatory)][string]$Profile,
        [string]$ModsRoot,
        [Parameter(Mandatory)][string]$GameDataRoot
    )
    if (-not [IO.Path]::IsPathRooted($Mo2Root) -or -not [IO.Path]::IsPathRooted($GameDataRoot)) {
        throw 'PluginBatchPathInvalid: MO2 and game Data roots must be absolute.'
    }
    if ([string]::IsNullOrWhiteSpace($Profile) -or [IO.Path]::GetFileName($Profile) -cne $Profile -or
        $Profile.IndexOfAny([char[]]@(0, 10, 13)) -ge 0) {
        throw 'PluginBatchProfileInvalid: Profile must be one immediate directory name.'
    }
    $mo2 = [IO.Path]::GetFullPath($Mo2Root).TrimEnd('\', '/')
    $profiles = [IO.Path]::GetFullPath((Join-Path $mo2 'profiles')).TrimEnd('\', '/')
    $profileDirectory = [IO.Path]::GetFullPath((Join-Path $profiles $Profile)).TrimEnd('\', '/')
    if (-not $profileDirectory.StartsWith($profiles + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'PluginBatchBoundaryRefused: profile path escapes the MO2 profiles root.'
    }
    $mods = if ([string]::IsNullOrWhiteSpace($ModsRoot)) { Join-Path $mo2 'mods' } else { [IO.Path]::GetFullPath($ModsRoot) }
    $gameData = [IO.Path]::GetFullPath($GameDataRoot).TrimEnd('\', '/')
    foreach ($directory in @($mo2, $profileDirectory, $mods, $gameData)) {
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw "PluginBatchPathMissing: $directory" }
    }
    [pscustomobject][ordered]@{
        mo2Root = $mo2
        profile = $Profile
        profileDirectory = $profileDirectory
        modsRoot = [IO.Path]::GetFullPath($mods).TrimEnd('\', '/')
        gameDataRoot = $gameData
        creationManifestFile = Join-Path (Split-Path -Parent $gameData) 'Skyrim.ccc'
        pluginsFile = Join-Path $profileDirectory 'plugins.txt'
        loadOrderFile = Join-Path $profileDirectory 'loadorder.txt'
        modListFile = Join-Path $profileDirectory 'modlist.txt'
    }
}

function Get-GridPluginStateBatchCreationManifest {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$LiteralPath)
    $full = [IO.Path]::GetFullPath($LiteralPath)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        return [pscustomobject][ordered]@{ status = 'Missing'; fingerprint = 'MISSING'; entries = @() }
    }
    $item = Get-Item -LiteralPath $full -Force -ErrorAction Stop
    if ($item.Length -gt 65536) { throw "PluginBatchCreationManifestOversized: $full" }
    $document = Read-GridPluginStateBatchTextFile -LiteralPath $full
    $entries = New-Object Collections.Generic.List[string]
    $seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $lines = [Text.RegularExpressions.Regex]::Split($document.text, '\r\n|\n|\r')
    for ($index = 0; $index -lt $lines.Length; $index++) {
        $name = $lines[$index].Trim()
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        try { Assert-GridPluginStateBatchLeafName -Name $name }
        catch { throw "PluginBatchCreationManifestMalformed: invalid entry at line $($index + 1)." }
        if ($name.Length -gt 260 -or -not $seen.Add($name)) { throw "PluginBatchCreationManifestMalformed: invalid or duplicate entry at line $($index + 1)." }
        if ($entries.Count -ge 512) { throw 'PluginBatchCreationManifestOversized: more than 512 entries.' }
        $entries.Add($name)
    }
    [pscustomobject][ordered]@{ status = 'Complete'; fingerprint = [string]$document.sha256; entries = $entries.ToArray() }
}

function Read-GridPluginStateBatchTextFile {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$LiteralPath)
    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) { throw "PluginBatchFileMissing: $LiteralPath" }
    $bytes = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($LiteralPath))
    if ($bytes.Length -gt 33554432) { throw "PluginBatchFileOversized: $LiteralPath" }
    $encodingName = 'Utf8NoBom'; $offset = 0
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $encoding = New-Object Text.UTF8Encoding($true, $true); $encodingName = 'Utf8Bom'; $offset = 3
    }
    elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        $encoding = New-Object Text.UnicodeEncoding($false, $true, $true); $encodingName = 'Utf16Le'; $offset = 2
    }
    elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) {
        $encoding = New-Object Text.UnicodeEncoding($true, $true, $true); $encodingName = 'Utf16Be'; $offset = 2
    }
    else { $encoding = New-Object Text.UTF8Encoding($false, $true) }
    try { $text = $encoding.GetString($bytes, $offset, $bytes.Length - $offset) }
    catch { throw "PluginBatchEncodingInvalid: $LiteralPath is not valid $encodingName text." }
    [pscustomobject][ordered]@{
        path = [IO.Path]::GetFullPath($LiteralPath)
        bytes = $bytes
        text = $text
        encoding = $encodingName
        sha256 = Get-GridPluginStateBatchSha256Bytes -Bytes $bytes
    }
}

function ConvertTo-GridPluginStateBatchBytes {
    param([Parameter(Mandatory)][string]$Text, [Parameter(Mandatory)][string]$Encoding)
    switch ($Encoding) {
        'Utf8NoBom' { $encoder = New-Object Text.UTF8Encoding($false, $true); return $encoder.GetBytes($Text) }
        'Utf8Bom' {
            $encoder = New-Object Text.UTF8Encoding($true, $true)
            $body = $encoder.GetBytes($Text); $preamble = $encoder.GetPreamble(); $result = New-Object byte[] ($preamble.Length + $body.Length)
            [Array]::Copy($preamble, 0, $result, 0, $preamble.Length); [Array]::Copy($body, 0, $result, $preamble.Length, $body.Length); return $result
        }
        'Utf16Le' {
            $encoder = New-Object Text.UnicodeEncoding($false, $true, $true)
            $body = $encoder.GetBytes($Text); $preamble = $encoder.GetPreamble(); $result = New-Object byte[] ($preamble.Length + $body.Length)
            [Array]::Copy($preamble, 0, $result, 0, $preamble.Length); [Array]::Copy($body, 0, $result, $preamble.Length, $body.Length); return $result
        }
        'Utf16Be' {
            $encoder = New-Object Text.UnicodeEncoding($true, $true, $true)
            $body = $encoder.GetBytes($Text); $preamble = $encoder.GetPreamble(); $result = New-Object byte[] ($preamble.Length + $body.Length)
            [Array]::Copy($preamble, 0, $result, 0, $preamble.Length); [Array]::Copy($body, 0, $result, $preamble.Length, $body.Length); return $result
        }
        default { throw "PluginBatchEncodingUnsupported: $Encoding" }
    }
}

function Get-GridPluginStateBatchEntries {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Text)
    $entries = New-Object Collections.Generic.List[object]
    $seen = New-Object 'Collections.Generic.Dictionary[string,int]' ([StringComparer]::OrdinalIgnoreCase)
    $lines = [Text.RegularExpressions.Regex]::Split($Text, '\r\n|\n|\r')
    for ($index = 0; $index -lt $lines.Length; $index++) {
        $trimmed = $lines[$index].Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith('#')) { continue }
        $enabled = $trimmed.StartsWith('*')
        $name = if ($enabled) { $trimmed.Substring(1).Trim() } else { $trimmed }
        Assert-GridPluginStateBatchLeafName -Name $name
        if ($seen.ContainsKey($name)) { throw "PluginBatchDuplicateEntry: '$name' occurs more than once." }
        $seen[$name] = $index
        $entries.Add([pscustomobject][ordered]@{ name = $name; state = if ($enabled) { 'Enabled' } else { 'Disabled' }; lineIndex = $index })
    }
    $entries.ToArray()
}

function Set-GridPluginStateBatchText {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Text, [Parameter(Mandatory)][object[]]$Operations)
    $desired = New-Object 'Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($operation in @($Operations)) {
        $name = [string]$operation.pluginName; Assert-GridPluginStateBatchLeafName -Name $name
        $state = [string]$operation.desiredState
        if ($state -notin @('Enabled', 'Disabled')) { throw "PluginBatchStateInvalid: $state" }
        if ($desired.ContainsKey($name)) { throw "PluginBatchDuplicateTarget: $name" }
        $desired[$name] = $state
    }
    $found = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $pattern = [Text.RegularExpressions.Regex]::new('(?m)^(?<line>[^\r\n]*)')
    $updated = $pattern.Replace($Text, [Text.RegularExpressions.MatchEvaluator]{
        param($match)
        $line = $match.Groups['line'].Value
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith('#')) { return $line }
        $name = if ($trimmed.StartsWith('*')) { $trimmed.Substring(1).Trim() } else { $trimmed }
        if (-not $desired.ContainsKey($name)) { return $match.Value }
        [void]$found.Add($name)
        $leading = $line.Length - $line.TrimStart().Length
        $indent = if ($leading -gt 0) { $line.Substring(0, $leading) } else { '' }
        $body = $line.Substring($leading)
        if ($body.StartsWith('*')) { $body = $body.Substring(1) }
        $marker = if ($desired[$name] -eq 'Enabled') { '*' } else { '' }
        return $indent + $marker + $body
    })
    foreach ($name in $desired.Keys) { if (-not $found.Contains($name)) { throw "PluginBatchTargetMissing: $name" } }
    $updated
}

function Get-GridPluginStateBatchTes4Header {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PluginPath, [ValidateRange(24,16777216)][int]$MaximumHeaderBytes = 16777216)
    $full = [IO.Path]::GetFullPath($PluginPath)
    $before = Get-Item -LiteralPath $full -Force -ErrorAction Stop
    if ($before.Length -lt 24) { throw "PluginBatchTes4Malformed: header is truncated: $full" }
    $stream = [IO.File]::Open($full, 'Open', 'Read', 'ReadWrite')
    try {
        $reader = New-Object IO.BinaryReader($stream, [Text.Encoding]::ASCII, $true)
        try {
            if ([Text.Encoding]::ASCII.GetString($reader.ReadBytes(4)) -cne 'TES4') { throw "PluginBatchTes4Malformed: missing TES4 header: $full" }
            $dataSize = $reader.ReadUInt32(); $flags = $reader.ReadUInt32(); [void]$reader.ReadBytes(12)
            if ($dataSize -gt $MaximumHeaderBytes -or (24L + [long]$dataSize) -gt $stream.Length) { throw "PluginBatchTes4Malformed: header exceeds its bound: $full" }
            $data = $reader.ReadBytes([int]$dataSize)
            if ($data.Length -ne [int]$dataSize) { throw "PluginBatchTes4Malformed: incomplete header read: $full" }
        } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
    $after = Get-Item -LiteralPath $full -Force -ErrorAction Stop
    if ($before.Length -ne $after.Length -or $before.LastWriteTimeUtc.Ticks -ne $after.LastWriteTimeUtc.Ticks) { throw "PluginBatchFileChangedDuringRead: $full" }
    $masters = New-Object Collections.Generic.List[string]; $position = 0; $extended = $null; $latin1 = [Text.Encoding]::GetEncoding(28591)
    while ($position -lt $data.Length) {
        if (($data.Length - $position) -lt 6) { throw "PluginBatchTes4Malformed: truncated subrecord: $full" }
        $signature = [Text.Encoding]::ASCII.GetString($data, $position, 4); $short = [BitConverter]::ToUInt16($data, $position + 4); $position += 6
        $size = if ($null -ne $extended) { [uint32]$extended } else { [uint32]$short }; $extended = $null
        if ($size -gt [int]::MaxValue -or ([long]$position + [long]$size) -gt $data.Length) { throw "PluginBatchTes4Malformed: subrecord exceeds header: $full" }
        if ($signature -eq 'XXXX') { if ($size -ne 4) { throw "PluginBatchTes4Malformed: invalid XXXX subrecord: $full" }; $extended = [BitConverter]::ToUInt32($data, $position); $position += 4; continue }
        if ($signature -eq 'MAST') {
            $bytes = New-Object byte[] ([int]$size); [Array]::Copy($data, $position, $bytes, 0, [int]$size); $nullIndex = [Array]::IndexOf($bytes, [byte]0)
            $length = if ($nullIndex -ge 0) { $nullIndex } else { $bytes.Length }; $name = $latin1.GetString($bytes, 0, $length).Trim()
            Assert-GridPluginStateBatchLeafName -Name $name; $masters.Add($name)
        }
        $position += [int]$size
    }
    if ($null -ne $extended) { throw "PluginBatchTes4Malformed: dangling XXXX subrecord: $full" }
    [pscustomobject][ordered]@{
        path = $full; sizeBytes = [long]$after.Length; lastWriteTimeUtcTicks = [long]$after.LastWriteTimeUtc.Ticks
        sha256 = Get-GridPluginStateBatchSha256File -LiteralPath $full
        hasMasterFlag = (($flags -band 0x1) -ne 0); hasLightFlag = (($flags -band 0x200) -ne 0)
        masters = $masters.ToArray()
    }
}

function Add-GridPluginStateBatchProviderFiles {
    param([Parameter(Mandatory)]$Map, [Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$Provider)
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { return }
    foreach ($file in @(Get-ChildItem -LiteralPath $Directory -File -Force -ErrorAction Stop | Where-Object { $_.Extension -in @('.esp','.esm','.esl') } | Sort-Object Name)) {
        $Map[[string]$file.Name] = [pscustomobject][ordered]@{ pluginName = [string]$file.Name; path = [string]$file.FullName; provider = $Provider }
    }
}

function Get-GridPluginStateBatchProviderMap {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Paths, [Parameter(Mandatory)][string]$ModListText)
    $map = New-Object 'Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
    Add-GridPluginStateBatchProviderFiles -Map $map -Directory ([string]$Paths.gameDataRoot) -Provider 'Base game Data'
    $lines = [Text.RegularExpressions.Regex]::Split($ModListText, '\r\n|\n|\r')
    for ($index = $lines.Length - 1; $index -ge 0; $index--) {
        $line = $lines[$index].Trim()
        if (-not $line.StartsWith('+') -or $line.Length -lt 2) { continue }
        $name = $line.Substring(1).Trim()
        if ([string]::IsNullOrWhiteSpace($name) -or [IO.Path]::GetFileName($name) -cne $name) { throw "PluginBatchModIdentityInvalid: $name" }
        $directory = [IO.Path]::GetFullPath((Join-Path ([string]$Paths.modsRoot) $name))
        if (-not $directory.StartsWith(([string]$Paths.modsRoot).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "PluginBatchBoundaryRefused: $directory" }
        Add-GridPluginStateBatchProviderFiles -Map $map -Directory $directory -Provider $name
    }
    $map
}

function Get-GridPluginStateBatchInspection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Mo2Root,
        [Parameter(Mandatory)][string]$Profile,
        [string]$ModsRoot,
        [Parameter(Mandatory)][string]$GameDataRoot,
        [Parameter(Mandatory)][string[]]$RequestedPlugins,
        [ValidateRange(1,254)][int]$MaximumFullPlugins = 254,
        [ValidateRange(1,4096)][int]$MaximumLightPlugins = 4096
    )
    $requested = @($RequestedPlugins | ForEach-Object { Assert-GridPluginStateBatchLeafName -Name ([string]$_); [string]$_ } | Sort-Object -Unique)
    if ($requested.Count -eq 0) { throw 'PluginBatchTargetRequired: at least one exact plugin is required.' }
    $paths = Get-GridPluginStateBatchProfilePaths -Mo2Root $Mo2Root -Profile $Profile -ModsRoot $ModsRoot -GameDataRoot $GameDataRoot
    $pluginsDocument = Read-GridPluginStateBatchTextFile -LiteralPath $paths.pluginsFile
    $modListDocument = Read-GridPluginStateBatchTextFile -LiteralPath $paths.modListFile
    $loadOrderDocument = Read-GridPluginStateBatchTextFile -LiteralPath $paths.loadOrderFile
    $entries = @(Get-GridPluginStateBatchEntries -Text $pluginsDocument.text)
    $entryByName = New-Object 'Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $entries) { $entryByName[[string]$entry.name] = $entry }
    $providers = Get-GridPluginStateBatchProviderMap -Paths $paths -ModListText $modListDocument.text
    $creationManifest = Get-GridPluginStateBatchCreationManifest -LiteralPath $paths.creationManifestFile
    $implicitNames = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $implicitCandidates = New-Object Collections.Generic.List[string]
    foreach ($name in @('Skyrim.esm','Update.esm','Dawnguard.esm','HearthFires.esm','Dragonborn.esm')) {
        if ($providers.ContainsKey($name)) { $implicitCandidates.Add($name) }
    }
    foreach ($name in @($creationManifest.entries)) { $implicitCandidates.Add([string]$name) }
    foreach ($name in $implicitCandidates) {
        Assert-GridPluginStateBatchLeafName -Name ([string]$name)
        [void]$implicitNames.Add([string]$name)
        $entryByName[[string]$name] = [pscustomobject][ordered]@{ name = [string]$name; state = 'Enabled'; lineIndex = -1; activation = 'Implicit' }
    }
    $headerCache = New-Object 'Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
    $getHeader = {
        param([string]$Name)
        if (-not $providers.ContainsKey($Name)) { throw "PluginBatchProviderMissing: no winning file resolves for '$Name'." }
        if (-not $headerCache.ContainsKey($Name)) { $headerCache[$Name] = Get-GridPluginStateBatchTes4Header -PluginPath ([string]$providers[$Name].path) }
        $headerCache[$Name]
    }
    $enabledFull = 0; $enabledLight = 0; $unresolvedEnabled = New-Object Collections.Generic.List[string]
    foreach ($entry in @($entryByName.Values | Where-Object state -eq 'Enabled')) {
        if (-not $providers.ContainsKey([string]$entry.name)) { $unresolvedEnabled.Add([string]$entry.name); continue }
        $header = & $getHeader ([string]$entry.name)
        if ([IO.Path]::GetExtension([string]$entry.name) -ieq '.esl' -or [bool]$header.hasLightFlag) { $enabledLight++ } else { $enabledFull++ }
    }
    if ($unresolvedEnabled.Count -gt 0) { throw ('PluginBatchEnabledProviderMissing: ' + (@($unresolvedEnabled | Sort-Object) -join ', ')) }
    $visiting = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $visited = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $orderedClosure = New-Object Collections.Generic.List[string]
    function Visit-GridPluginStateBatchDependency([string]$Name) {
        Assert-GridPluginStateBatchLeafName -Name $Name
        if ($visited.Contains($Name)) { return }
        if ($visiting.Contains($Name)) { throw "PluginBatchDependencyCycle: $Name" }
        if (-not $entryByName.ContainsKey($Name)) { throw "PluginBatchDependencyEntryMissing: $Name" }
        [void]$visiting.Add($Name); $header = & $getHeader $Name
        foreach ($master in @($header.masters)) { Visit-GridPluginStateBatchDependency -Name ([string]$master) }
        [void]$visiting.Remove($Name); [void]$visited.Add($Name); $orderedClosure.Add([string]$entryByName[$Name].name)
    }
    foreach ($name in $requested) { Visit-GridPluginStateBatchDependency -Name $name }
    $operations = New-Object Collections.Generic.List[object]
    $sequence = 0
    foreach ($name in $orderedClosure) {
        $entry = $entryByName[$name]
        if ([string]$entry.state -eq 'Enabled') { continue }
        $sequence++; $header = & $getHeader $name; $provider = $providers[$name]
        $operations.Add([pscustomobject][ordered]@{
            sequence = $sequence; pluginName = $name; desiredState = 'Enabled'; beforeState = [string]$entry.state
            provider = [string]$provider.provider; pluginPath = [string]$header.path; pluginSha256 = [string]$header.sha256
            sizeBytes = [long]$header.sizeBytes; hasMasterFlag = [bool]$header.hasMasterFlag; hasLightFlag = [bool]$header.hasLightFlag
            masters = @($header.masters)
        })
    }
    if ($operations.Count -eq 0) { throw 'PluginBatchAlreadySatisfied: every requested plugin and required master is already enabled.' }
    $addedFull = @($operations | Where-Object { -not $_.hasLightFlag -and [IO.Path]::GetExtension([string]$_.pluginName) -ine '.esl' }).Count
    $addedLight = $operations.Count - $addedFull
    $afterFull = $enabledFull + $addedFull; $afterLight = $enabledLight + $addedLight
    if ($afterFull -gt $MaximumFullPlugins) { throw "PluginBatchFullSlotLimit: $enabledFull + $addedFull exceeds $MaximumFullPlugins." }
    if ($afterLight -gt $MaximumLightPlugins) { throw "PluginBatchLightSlotLimit: $enabledLight + $addedLight exceeds $MaximumLightPlugins." }
    $updatedText = Set-GridPluginStateBatchText -Text $pluginsDocument.text -Operations $operations.ToArray()
    $updatedBytes = ConvertTo-GridPluginStateBatchBytes -Text $updatedText -Encoding $pluginsDocument.encoding
    [pscustomobject][ordered]@{
        schemaVersion = 1; status = 'InputsVerified'; inspectedAt = [DateTimeOffset]::UtcNow.ToString('o')
        paths = $paths; requestedPlugins = $requested; dependencyClosure = $orderedClosure.ToArray(); operations = $operations.ToArray()
        protectedState = [pscustomobject][ordered]@{
            pluginsFileSha256 = [string]$pluginsDocument.sha256; modListFileSha256 = [string]$modListDocument.sha256
            loadOrderFileSha256 = [string]$loadOrderDocument.sha256; expectedAfterPluginsFileSha256 = Get-GridPluginStateBatchSha256Bytes -Bytes $updatedBytes
            pluginsFileEncoding = [string]$pluginsDocument.encoding; creationManifestStatus = [string]$creationManifest.status
            creationManifestFingerprint = [string]$creationManifest.fingerprint
        }
        slotUsage = [pscustomobject][ordered]@{
            fullBefore = $enabledFull; fullAdded = $addedFull; fullAfter = $afterFull; fullMaximum = $MaximumFullPlugins
            lightBefore = $enabledLight; lightAdded = $addedLight; lightAfter = $afterLight; lightMaximum = $MaximumLightPlugins
            unresolvedEnabledEntries = $unresolvedEnabled.ToArray()
        }
    }
}

function Test-GridPluginStateBatchInspectionEquivalent {
    param([Parameter(Mandatory)]$Expected, [Parameter(Mandatory)]$Actual)
    foreach ($name in @('pluginsFileSha256','modListFileSha256','loadOrderFileSha256','expectedAfterPluginsFileSha256','pluginsFileEncoding','creationManifestStatus','creationManifestFingerprint')) {
        if ([string]$Expected.protectedState.$name -cne [string]$Actual.protectedState.$name) { throw "PluginBatchCurrentStateChanged: $name" }
    }
    $expectedOps = @($Expected.operations | ForEach-Object { "$($_.sequence)|$($_.pluginName)|$($_.desiredState)|$($_.provider)|$($_.pluginPath)|$($_.pluginSha256)|$($_.hasLightFlag)|$(@($_.masters) -join ',')" })
    $actualOps = @($Actual.operations | ForEach-Object { "$($_.sequence)|$($_.pluginName)|$($_.desiredState)|$($_.provider)|$($_.pluginPath)|$($_.pluginSha256)|$($_.hasLightFlag)|$(@($_.masters) -join ',')" })
    if (($expectedOps -join "`n") -cne ($actualOps -join "`n")) { throw 'PluginBatchCurrentStateChanged: dependency closure or winning plugin files changed.' }
    foreach ($name in @('fullBefore','fullAdded','fullAfter','fullMaximum','lightBefore','lightAdded','lightAfter','lightMaximum')) {
        if ([int]$Expected.slotUsage.$name -ne [int]$Actual.slotUsage.$name) { throw "PluginBatchCurrentStateChanged: slotUsage.$name" }
    }
    $true
}
