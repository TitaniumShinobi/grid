#requires -Version 5.1
Set-StrictMode -Version Latest

function Resolve-GridGameUserDataRoots {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ScriptsRoot,
        [Parameter(Mandatory)][ValidatePattern('^[a-z0-9]+$')][string]$GameId,
        [Parameter(Mandatory)][hashtable]$KnownFolders
    )

    $scriptsFull = [IO.Path]::GetFullPath($ScriptsRoot).TrimEnd('\')
    $definitionPath = Join-Path $scriptsFull "games\$GameId\user-data-roots.v1.json"
    if (-not (Test-Path -LiteralPath $definitionPath -PathType Leaf)) {
        throw "GameUserDataRootsUnavailable: '$GameId' has no game-owned user-data root definition."
    }

    try { $definition = Get-Content -LiteralPath $definitionPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "GameUserDataRootsInvalid: '$definitionPath' is unreadable or malformed: $($_.Exception.Message)" }
    if ([int]$definition.schemaVersion -ne 1 -or [string]$definition.gameId -cne $GameId) {
        throw "GameUserDataRootsInvalid: schemaVersion or gameId does not match '$GameId'."
    }

    $sourceIds = @{}
    foreach ($source in @($definition.evidenceSources)) {
        $sourceId = [string]$source.sourceId
        if ([string]::IsNullOrWhiteSpace($sourceId) -or $sourceIds.ContainsKey($sourceId.ToLowerInvariant())) {
            throw "GameUserDataRootsInvalid: evidence source IDs must be nonempty and case-insensitively unique."
        }
        if ([string]$source.authority -notin @('OfficialPrimary','ProjectPrimary','ObservedLocal')) {
            throw "GameUserDataRootsInvalid: evidence source '$sourceId' has unsupported authority."
        }
        $sourceIds[$sourceId.ToLowerInvariant()] = $true
    }

    $rootIds = @{}
    $resolved = New-Object Collections.Generic.List[object]
    foreach ($root in @($definition.roots)) {
        $rootId = [string]$root.rootId
        if ($rootId -notmatch '^[a-z][A-Za-z0-9]+$' -or $rootIds.ContainsKey($rootId.ToLowerInvariant())) {
            throw "GameUserDataRootsInvalid: root IDs must be valid and case-insensitively unique."
        }
        $rootIds[$rootId.ToLowerInvariant()] = $true

        $anchorName = [string]$root.anchor
        if ($anchorName -notin @('Documents','LocalApplicationData') -or -not $KnownFolders.ContainsKey($anchorName)) {
            throw "GameUserDataRootsInvalid: root '$rootId' requires the explicit '$anchorName' known-folder anchor."
        }
        $anchor = Resolve-GridKnownFolderAnchor -Value ([string]$KnownFolders[$anchorName]) -Name $anchorName
        $relative = Test-GridSafeRelativeDefinitionPath -Value ([string]$root.relativePath) -AllowLeafWildcard:$false
        $resolvedRoot = [IO.Path]::GetFullPath((Join-Path $anchor $relative))
        Assert-GridDefinitionContainment -Path $resolvedRoot -Root $anchor -Label "root '$rootId'"

        $role = [string]$root.role
        $policy = [string]$root.collectionPolicy
        if ($role -notin @('UserData','Diagnostics','SharedLauncherDiagnostics') -or
            $policy -notin @('ProtectedMetadataOnly','BoundedTopLevelFiles')) {
            throw "GameUserDataRootsInvalid: root '$rootId' has an unsupported role or collection policy."
        }
        if ([string]$root.authority -notin @('OfficialPrimary','ProjectPrimary','ObservedLocal') -or
            [string]$root.confidence -notin @('Verified','Corroborated','Observed')) {
            throw "GameUserDataRootsInvalid: root '$rootId' has unsupported authority or confidence."
        }
        if ($role -eq 'UserData' -and $policy -ne 'ProtectedMetadataOnly') {
            throw "GameUserDataRootsInvalid: user-data root '$rootId' may expose protected metadata only."
        }
        if ($role -ne 'UserData') {
            $maxFiles = [int]$root.maxFiles
            $maxFileBytes = [long]$root.maxFileBytes
            if ($policy -ne 'BoundedTopLevelFiles' -or $maxFiles -lt 1 -or $maxFiles -gt 1024 -or
                $maxFileBytes -lt 1 -or $maxFileBytes -gt 33554432) {
                throw "GameUserDataRootsInvalid: diagnostic root '$rootId' requires bounded file and byte limits."
            }
        }

        $resolvedSourceIds = @($root.sourceIds | ForEach-Object { [string]$_ })
        if ($resolvedSourceIds.Count -eq 0 -or @($resolvedSourceIds | Where-Object { -not $sourceIds.ContainsKey($_.ToLowerInvariant()) }).Count -gt 0) {
            throw "GameUserDataRootsInvalid: root '$rootId' cites an unresolved evidence source."
        }

        $artifactIds = @{}
        $artifacts = New-Object Collections.Generic.List[object]
        foreach ($artifact in @($root.artifacts)) {
            $artifactId = [string]$artifact.artifactId
            if ($artifactId -notmatch '^[a-z][A-Za-z0-9]+$' -or $artifactIds.ContainsKey($artifactId.ToLowerInvariant())) {
                throw "GameUserDataRootsInvalid: artifact IDs beneath '$rootId' must be valid and unique."
            }
            $artifactIds[$artifactId.ToLowerInvariant()] = $true
            $kind = [string]$artifact.kind
            $observation = [string]$artifact.observation
            $artifactRelative = Test-GridSafeRelativeDefinitionPath -Value ([string]$artifact.relativePath) -AllowLeafWildcard:($kind -eq 'DiagnosticFilePattern')
            if ($kind -eq 'DiagnosticFilePattern') {
                if ($role -eq 'UserData' -or $observation -ne 'BoundedRead' -or
                    $artifactRelative.IndexOfAny([char[]]@('\','/')) -ge 0) {
                    throw "GameUserDataRootsInvalid: diagnostic patterns must be bounded top-level leaf patterns."
                }
            }
            elseif ($kind -eq 'ProtectedDirectory') {
                if ($observation -ne 'PresenceOnly') { throw "GameUserDataRootsInvalid: protected directories are presence-only." }
            }
            elseif ($kind -eq 'ConfigurationFile') {
                if ($observation -ne 'FullContentHash') { throw "GameUserDataRootsInvalid: configuration files require full-content hashing." }
            }
            else { throw "GameUserDataRootsInvalid: artifact '$artifactId' has unsupported kind '$kind'." }

            $artifacts.Add([pscustomobject][ordered]@{
                artifactId = $artifactId
                kind = $kind
                path = Join-Path $resolvedRoot $artifactRelative
                observation = $observation
                optional = [bool]$artifact.optional
            })
        }

        $exclusions = @($root.excludedRelativePaths | ForEach-Object {
            $excludedRelative = Test-GridSafeRelativeDefinitionPath -Value ([string]$_) -AllowLeafWildcard:$false
            $excludedPath = [IO.Path]::GetFullPath((Join-Path $resolvedRoot $excludedRelative))
            Assert-GridDefinitionContainment -Path $excludedPath -Root $resolvedRoot -Label "exclusion beneath '$rootId'"
            $excludedPath
        } | Sort-Object -Unique)

        $resolved.Add([pscustomobject][ordered]@{
            rootId = $rootId
            gameId = $GameId
            anchor = $anchorName
            path = $resolvedRoot
            role = $role
            collectionPolicy = $policy
            authority = [string]$root.authority
            confidence = [string]$root.confidence
            sourceIds = @($resolvedSourceIds | Sort-Object)
            maxFiles = if ($role -eq 'UserData') { $null } else { [int]$root.maxFiles }
            maxFileBytes = if ($role -eq 'UserData') { $null } else { [long]$root.maxFileBytes }
            artifacts = @($artifacts | Sort-Object artifactId)
            excludedPaths = $exclusions
        })
    }

    [pscustomobject][ordered]@{
        schemaVersion = 1
        gameId = $GameId
        displayName = [string]$definition.displayName
        definitionPath = $definitionPath
        roots = @($resolved | Sort-Object rootId)
        evidenceSources = @($definition.evidenceSources | Sort-Object sourceId)
    }
}

function Resolve-GridKnownFolderAnchor {
    param([Parameter(Mandatory)][string]$Value, [Parameter(Mandatory)][string]$Name)
    if ([string]::IsNullOrWhiteSpace($Value) -or -not [IO.Path]::IsPathRooted($Value) -or
        $Value.StartsWith('\\') -or $Value.StartsWith('//') -or $Value.StartsWith('\\?\') -or $Value.StartsWith('\\.\')) {
        throw "GameUserDataRootsInvalid: '$Name' must be an explicit local fully-qualified path."
    }
    [IO.Path]::GetFullPath($Value).TrimEnd('\')
}

function Test-GridSafeRelativeDefinitionPath {
    param([Parameter(Mandatory)][string]$Value, [switch]$AllowLeafWildcard)
    if ([string]::IsNullOrWhiteSpace($Value) -or [IO.Path]::IsPathRooted($Value) -or $Value -match '[\x00-\x1F]') {
        throw 'GameUserDataRootsInvalid: definition paths must be nonempty local relative paths.'
    }
    $normalized = $Value.Replace('/', '\')
    $segments = @($normalized.Split('\') | Where-Object { $_.Length -gt 0 })
    if ($segments.Count -eq 0 -or @($segments | Where-Object { $_ -in @('.','..') }).Count -gt 0) {
        throw 'GameUserDataRootsInvalid: definition paths may not contain traversal segments.'
    }
    for ($index = 0; $index -lt $segments.Count; $index++) {
        $hasWildcard = $segments[$index].Contains('*') -or $segments[$index].Contains('?')
        if ($hasWildcard -and (-not $AllowLeafWildcard -or $index -ne ($segments.Count - 1))) {
            throw 'GameUserDataRootsInvalid: wildcards are allowed only in diagnostic leaf patterns.'
        }
    }
    $normalized
}

function Assert-GridDefinitionContainment {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Label)
    $rootPrefix = $Root.TrimEnd('\') + '\'
    if (-not $Path.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "GameUserDataRootsInvalid: $Label escapes its declared anchor."
    }
}
