[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$InstallationRoot,
    [Parameter(Mandatory)] [string]$OutputPath,
    [ValidateRange(1, 2000000)] [int]$MaximumEntries = 500000,
    [ValidateRange(1, 128)] [int]$MaximumDepth = 64,
    [ValidateRange(1, 128)] [int]$MaximumSemanticFileMiB = 32
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest

$semanticProfileFiles = @(
    'modlist.txt',
    'plugins.txt',
    'loadorder.txt',
    'archives.txt',
    'settings.ini',
    'Skyrim.ini',
    'SkyrimPrefs.ini',
    'SkyrimCustom.ini'
)
$semanticRootFiles = @('ModOrganizer.exe', 'ModOrganizer.ini', 'portable.txt', 'categories.dat')

function Get-NormalizedRoot {
    param([Parameter(Mandatory)] [string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
    if (-not $item.PSIsContainer) { throw "Installation root is not a directory: '$fullPath'." }
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The installation root must not be a reparse point: '$fullPath'."
    }
    return $fullPath
}

function Get-ContainedRelativePath {
    param(
        [Parameter(Mandatory)] [string]$Root,
        [Parameter(Mandatory)] [string]$Path
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $prefix = $Root + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Observed path escaped the installation root: '$fullPath'."
    }
    return $fullPath.Substring($prefix.Length).Replace('\', '/')
}

function Test-IsProfileSaveContent {
    param([Parameter(Mandatory)] [string]$RelativePath)

    $segments = $RelativePath -split '/'
    return $segments.Length -gt 2 -and
        $segments[0].Equals('profiles', [System.StringComparison]::OrdinalIgnoreCase) -and
        $segments[2].Equals('saves', [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-SafeTreeObservation {
    param(
        [Parameter(Mandatory)] [string]$Root,
        [Parameter(Mandatory)] [int]$EntryLimit,
        [Parameter(Mandatory)] [int]$DepthLimit
    )

    $rows = [System.Collections.Generic.List[object]]::new()
    $warnings = [System.Collections.Generic.List[string]]::new()
    $stack = [System.Collections.Generic.Stack[object]]::new()
    $stack.Push([pscustomobject]@{ Path = $Root; Depth = 0 })

    while ($stack.Count -gt 0) {
        $current = $stack.Pop()
        if ($current.Depth -ge $DepthLimit) {
            $warnings.Add("Depth limit reached below a directory at depth $($current.Depth).")
            continue
        }

        try {
            $children = @(Get-ChildItem -LiteralPath $current.Path -Force -ErrorAction Stop | Sort-Object Name)
        }
        catch {
            $relativeDirectory = if ($current.Path -eq $Root) { '.' } else { Get-ContainedRelativePath -Root $Root -Path $current.Path }
            $warnings.Add("A directory could not be enumerated: $relativeDirectory")
            continue
        }

        foreach ($child in $children) {
            if ($rows.Count -ge $EntryLimit) { throw "Snapshot entry limit of $EntryLimit was exceeded." }
            $relativePath = Get-ContainedRelativePath -Root $Root -Path $child.FullName
            if (Test-IsProfileSaveContent -RelativePath $relativePath) {
                if ($child.PSIsContainer -and $relativePath.EndsWith('/saves', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $rows.Add([pscustomobject]@{
                        relativePath = $relativePath
                        kind = 'directory'
                        length = $null
                        lastWriteTimeUtc = $child.LastWriteTimeUtc.ToString('O')
                        attributes = [int]$child.Attributes
                        reparsePoint = (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
                        contentOmitted = $true
                    })
                }
                continue
            }

            $isReparsePoint = (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
            $rows.Add([pscustomobject]@{
                relativePath = $relativePath
                kind = if ($child.PSIsContainer) { 'directory' } else { 'file' }
                length = if ($child.PSIsContainer) { $null } else { [long]$child.Length }
                lastWriteTimeUtc = $child.LastWriteTimeUtc.ToString('O')
                attributes = [int]$child.Attributes
                reparsePoint = $isReparsePoint
                contentOmitted = $false
            })

            if ($child.PSIsContainer) {
                if ($isReparsePoint) {
                    $warnings.Add("A reparse-point directory was recorded but not traversed: $relativePath")
                }
                else {
                    $stack.Push([pscustomobject]@{ Path = $child.FullName; Depth = $current.Depth + 1 })
                }
            }
        }
    }

    return [pscustomobject]@{
        Rows = @($rows | Sort-Object relativePath)
        Warnings = @($warnings)
    }
}

function Get-SemanticFileHash {
    param(
        [Parameter(Mandatory)] [string]$Root,
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [long]$MaximumBytes
    )

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    $relativePath = Get-ContainedRelativePath -Root $Root -Path $item.FullName
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        return [pscustomobject]@{ relativePath = $relativePath; length = [long]$item.Length; sha256 = $null; status = 'reparse-point-refused' }
    }
    if ($item.Length -gt $MaximumBytes) {
        return [pscustomobject]@{ relativePath = $relativePath; length = [long]$item.Length; sha256 = $null; status = 'oversized' }
    }
    return [pscustomobject]@{
        relativePath = $relativePath
        length = [long]$item.Length
        sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        status = 'hashed'
    }
}

function Get-TextSha256 {
    param([Parameter(Mandatory)] [string]$Text)

    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
        return ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Write-AtomicUtf8Json {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Json
    )

    $outputDirectory = Split-Path -Parent $Path
    if ([string]::IsNullOrWhiteSpace($outputDirectory)) { throw 'Output path must have a parent directory.' }
    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
    $temporaryPath = Join-Path $outputDirectory ('.' + [System.IO.Path]::GetFileName($Path) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [System.IO.File]::WriteAllText($temporaryPath, $Json, [System.Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $Path) {
            [System.IO.File]::Move($temporaryPath, $Path, $true)
        }
        else {
            [System.IO.File]::Move($temporaryPath, $Path)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    }
}

function Get-ProfileSummary {
    param(
        [Parameter(Mandatory)] [string]$ProfileDirectory,
        [Parameter(Mandatory)] [string]$Root
    )

    $modList = Join-Path $ProfileDirectory 'modlist.txt'
    $pluginList = Join-Path $ProfileDirectory 'plugins.txt'
    $modCount = 0
    $separatorCount = 0
    $pluginCount = 0
    if (Test-Path -LiteralPath $modList -PathType Leaf) {
        foreach ($line in [System.IO.File]::ReadLines($modList)) {
            if ($line.Length -gt 1 -and '+-*'.Contains($line[0])) {
                $name = $line.Substring(1)
                if ($name.EndsWith('_separator', [System.StringComparison]::OrdinalIgnoreCase)) { $separatorCount++ }
                else { $modCount++ }
            }
        }
    }
    if (Test-Path -LiteralPath $pluginList -PathType Leaf) {
        foreach ($line in [System.IO.File]::ReadLines($pluginList)) {
            $trimmed = $line.Trim()
            if ($trimmed.Length -gt 0 -and -not $trimmed.StartsWith('#')) { $pluginCount++ }
        }
    }

    return [pscustomobject]@{
        name = (Split-Path -Leaf $ProfileDirectory)
        relativePath = Get-ContainedRelativePath -Root $Root -Path $ProfileDirectory
        modCount = $modCount
        separatorCount = $separatorCount
        pluginCount = $pluginCount
    }
}

$root = Get-NormalizedRoot -Path $InstallationRoot
$output = [System.IO.Path]::GetFullPath($OutputPath)
$rootPrefix = $root + [System.IO.Path]::DirectorySeparatorChar
if ($output.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Snapshot output must be outside the observed installation root.'
}

$tree = Get-SafeTreeObservation -Root $root -EntryLimit $MaximumEntries -DepthLimit $MaximumDepth
$semanticPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($name in $semanticRootFiles) {
    $candidate = Join-Path $root $name
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { [void]$semanticPaths.Add($candidate) }
}

$profiles = [System.Collections.Generic.List[object]]::new()
$profilesRoot = Join-Path $root 'profiles'
if (Test-Path -LiteralPath $profilesRoot -PathType Container) {
    $profileRootItem = Get-Item -LiteralPath $profilesRoot -Force
    if (($profileRootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) {
        foreach ($profile in @(Get-ChildItem -LiteralPath $profilesRoot -Directory -Force -ErrorAction Stop | Sort-Object Name)) {
            if (($profile.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            $profiles.Add((Get-ProfileSummary -ProfileDirectory $profile.FullName -Root $root))
            foreach ($name in $semanticProfileFiles) {
                $candidate = Join-Path $profile.FullName $name
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { [void]$semanticPaths.Add($candidate) }
            }
        }
    }
}

$modsRoot = Join-Path $root 'mods'
$modDirectoryCount = 0
if (Test-Path -LiteralPath $modsRoot -PathType Container) {
    $modsRootItem = Get-Item -LiteralPath $modsRoot -Force
    if (($modsRootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) {
        foreach ($modDirectory in @(Get-ChildItem -LiteralPath $modsRoot -Directory -Force -ErrorAction Stop | Sort-Object Name)) {
            $modDirectoryCount++
            if (($modDirectory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            $metaIni = Join-Path $modDirectory.FullName 'meta.ini'
            if (Test-Path -LiteralPath $metaIni -PathType Leaf) { [void]$semanticPaths.Add($metaIni) }
        }
    }
}

$maximumSemanticBytes = [long]$MaximumSemanticFileMiB * 1MB
$semanticFiles = @(
    $semanticPaths |
        ForEach-Object { Get-SemanticFileHash -Root $root -Path $_ -MaximumBytes $maximumSemanticBytes } |
        Sort-Object relativePath
)

$treeFingerprintRows = @(
    $tree.Rows | ForEach-Object {
        [pscustomobject]@{
            relativePath = $_.relativePath
            kind = $_.kind
            length = $_.length
            lastWriteTimeUtc = if ($_.kind -eq 'file') { $_.lastWriteTimeUtc } else { $null }
            attributes = $_.attributes
            reparsePoint = $_.reparsePoint
            contentOmitted = $_.contentOmitted
        }
    }
)
$treeJson = ConvertTo-Json -InputObject $treeFingerprintRows -Depth 4 -Compress
$structuralFingerprint = Get-TextSha256 -Text $treeJson
$semanticJson = ConvertTo-Json -InputObject @($semanticFiles) -Depth 4 -Compress
$semanticFingerprint = Get-TextSha256 -Text $semanticJson

$mo2Executable = Join-Path $root 'ModOrganizer.exe'
$mo2Version = $null
if (Test-Path -LiteralPath $mo2Executable -PathType Leaf) {
    $mo2Version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($mo2Executable).FileVersion
}
$selectedProfile = $null
$mo2Ini = Join-Path $root 'ModOrganizer.ini'
if (Test-Path -LiteralPath $mo2Ini -PathType Leaf) {
    foreach ($line in [System.IO.File]::ReadLines($mo2Ini)) {
        if ($line -match '^selected_profile\s*=\s*(.*)$') { $selectedProfile = $Matches[1].Trim(); break }
    }
}

$fileRows = @($tree.Rows | Where-Object kind -eq 'file')
$directoryRows = @($tree.Rows | Where-Object kind -eq 'directory')
$measuredBytes = ($fileRows | Measure-Object -Property length -Sum).Sum
if ($null -eq $measuredBytes) { $measuredBytes = 0 }
$snapshot = [ordered]@{
    schemaVersion = 1
    kind = 'grid-wabbajack-semantic-snapshot'
    observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    installationRoot = $root
    completeness = if ($tree.Warnings.Count -eq 0 -and -not ($semanticFiles.status -contains 'oversized') -and -not ($semanticFiles.status -contains 'reparse-point-refused')) { 'complete' } else { 'partial' }
    limits = [ordered]@{ maximumEntries = $MaximumEntries; maximumDepth = $MaximumDepth; maximumSemanticFileMiB = $MaximumSemanticFileMiB }
    mo2 = [ordered]@{ version = $mo2Version; portableEvidence = (Test-Path -LiteralPath $mo2Ini -PathType Leaf); selectedProfileEvidence = $selectedProfile }
    counts = [ordered]@{
        files = $fileRows.Count
        directories = $directoryRows.Count
        bytes = [long]$measuredBytes
        profiles = $profiles.Count
        modDirectories = $modDirectoryCount
        archives = @($fileRows | Where-Object { $_.relativePath -match '\.(?:bsa|ba2)$' }).Count
        downloads = @($fileRows | Where-Object { $_.relativePath.StartsWith('downloads/', [System.StringComparison]::OrdinalIgnoreCase) }).Count
    }
    profiles = @($profiles)
    semanticFiles = @($semanticFiles)
    semanticFingerprintSha256 = $semanticFingerprint
    structuralFingerprintSha256 = $structuralFingerprint
    tree = @($tree.Rows)
    warnings = @($tree.Warnings)
}

Write-AtomicUtf8Json -Path $output -Json (ConvertTo-Json -InputObject $snapshot -Depth 8)

Write-Host "Snapshot: $output"
Write-Host "Files: $($fileRows.Count); directories: $($directoryRows.Count); semantic files: $($semanticFiles.Count)"
Write-Host "Completeness: $($snapshot.completeness)"
