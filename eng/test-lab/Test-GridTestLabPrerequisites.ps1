#requires -Version 7.0

<#
.SYNOPSIS
Performs the bounded, read-only ASSOS artifact and Skyrim prerequisite gate.

.DESCRIPTION
The script never discovers a game installation. ExternalGameRoot is read only when it is
supplied together with AllowExternalGameRootReadOnly. ArtifactPath is pinned to the approved
ASSOS location, and artifact evidence plus output must be below the approved dated evidence
root.

ArtifactEvidencePath is a reviewed JSON document with schemaVersion 1, kind
"grid-test-lab-artifact-evidence", and these objects:

  list: { machineName: "ASSOS", version: "1.3.0" }
  artifact: { length: 15012480, sha256: "<64 hex>" }
  inspection: {
    archiveSourceDomains: ["host.example"],
    unexpectedExecutableOrigins: [],
    storageEstimateBytes: 123
  }
  requirements: {
    artifact: {
      gameVersion: "artifact-derived value",
      language: "artifact-derived value",
      gameVersionFile: "SkyrimSE.exe",
      gameFiles: [{ relativePath, role, length and/or sha256 }],
      machineComponents: [{ name, expected, observed, status }],
      ambiguities: []
    },
    documentation: { gameVersion, language }
  }

File roles are game-binary, required-source, or creation-content. Machine-component status is
passed, blocked, or not-observed. Unknown or conflicting requirements produce a typed blocker;
the script does not infer or remediate prerequisites.
#>

[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Run')] [string]$ArtifactPath,
    [Parameter(Mandatory, ParameterSetName = 'Run')] [Alias('RequirementsPath', 'ArtifactInspectionPath')] [string]$ArtifactEvidencePath,
    [Parameter(Mandatory, ParameterSetName = 'Run')] [Alias('ReportPath')] [string]$OutputPath,
    [Parameter(ParameterSetName = 'Run')] [Alias('GamePath')] [string]$ExternalGameRoot,
    [Parameter(ParameterSetName = 'Run')] [string]$ObservedGameLanguage,
    [Parameter(ParameterSetName = 'Run')] [Alias('AllowExternalGamePath')] [switch]$AllowExternalGameRootReadOnly,
    [Parameter(ParameterSetName = 'Run')] [ValidateRange(1, 4096)] [int]$MaximumRequiredFiles = 1024,
    [Parameter(ParameterSetName = 'Run')] [ValidateRange(1, 16384)] [int]$MaximumEvidenceKiB = 8192,
    [Parameter(ParameterSetName = 'Run')] [ValidateRange(1, 65536)] [int]$MaximumSingleFileMiB = 8192,
    [Parameter(ParameterSetName = 'Run')] [ValidateRange(1, 262144)] [int]$MaximumTotalHashMiB = 16384,
    [Parameter(Mandatory, ParameterSetName = 'SelfTest')] [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$approved = [ordered]@{
    LabRoot = 'C:\Grid-Test-Lab'
    ArtifactPath = 'C:\Grid-Test-Lab\downloads\assos\1.3.0\ASSOS.wabbajack'
    EvidenceRoot = 'C:\Grid-Test-Lab\evidence\assos-1.3.0-20260826'
    ListMachineName = 'ASSOS'
    ListVersion = '1.3.0'
    ArtifactLength = [long]15012480
    MaximumApprovedStorageBytes = [long](50GB)
}

function Test-HasControlCharacter {
    param([Parameter(Mandatory)] [string]$Value)
    return $Value.IndexOfAny([char[]](0..31 + 127)) -ge 0
}

function Assert-SafeAbsoluteWindowsPath {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Role,
        [switch]$PermitMissing
    )

    if ([string]::IsNullOrWhiteSpace($Path)) { throw "$Role path is empty." }
    if (Test-HasControlCharacter -Value $Path) { throw "$Role path contains a control character." }
    if ($Path.Contains('/')) { throw "$Role path must use Windows separators only: '$Path'." }
    if ($Path.StartsWith('\\', [StringComparison]::Ordinal) -or
        $Path.StartsWith('\\?\', [StringComparison]::Ordinal) -or
        $Path.StartsWith('\\.\', [StringComparison]::Ordinal)) {
        throw "$Role path must not be a UNC or device path: '$Path'."
    }
    if ($Path -notmatch '^[A-Za-z]:\\') { throw "$Role path must be an absolute drive path: '$Path'." }
    if ($Path.Length -le 3) { throw "$Role path must not be a volume root: '$Path'." }
    if ($Path.Substring(2).Contains(':')) { throw "$Role path must not contain an alternate data stream: '$Path'." }
    if ($Path.IndexOfAny([char[]]'*?') -ge 0) { throw "$Role path must not contain wildcard characters: '$Path'." }

    $segments = @($Path.Substring(3).Split('\'))
    foreach ($segment in $segments) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment -in @('.', '..')) {
            throw "$Role path contains an empty or traversal segment: '$Path'."
        }
        if ($segment.EndsWith(' ', [StringComparison]::Ordinal) -or $segment.EndsWith('.', [StringComparison]::Ordinal)) {
            throw "$Role path contains a segment with an ambiguous trailing character: '$Path'."
        }
        $baseName = $segment.Split('.')[0]
        if ($baseName -match '^(?i:con|prn|aux|nul|com[1-9]|lpt[1-9])$') {
            throw "$Role path contains a reserved device name: '$Path'."
        }
    }

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $fullPath.Equals($Path.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Role path is not canonical: '$Path'."
    }

    $current = $fullPath.Substring(0, 3).TrimEnd('\')
    foreach ($segment in $segments) {
        $current = Join-Path $current $segment
        if (-not (Test-Path -LiteralPath $current)) { break }
        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Role path crosses a reparse point: '$current'."
        }
    }

    if (-not $PermitMissing -and -not (Test-Path -LiteralPath $fullPath)) {
        throw "$Role path does not exist: '$fullPath'."
    }
    return $fullPath
}

function Assert-SafeRelativePath {
    param([Parameter(Mandatory)] [string]$Path, [Parameter(Mandatory)] [string]$Role)
    if ([string]::IsNullOrWhiteSpace($Path) -or (Test-HasControlCharacter -Value $Path)) {
        throw "$Role relative path is empty or contains a control character."
    }
    if ($Path.Length -gt 32767) { throw "$Role exceeds the Windows path-length limit." }
    if ([IO.Path]::IsPathRooted($Path) -or $Path.StartsWith('\\') -or $Path.Contains('/') -or
        $Path.Contains(':') -or $Path.IndexOfAny([char[]]'*?') -ge 0) {
        throw "$Role must be a literal relative Windows path: '$Path'."
    }
    $segments = @($Path.Split('\'))
    foreach ($segment in $segments) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment -in @('.', '..') -or
            $segment.EndsWith(' ', [StringComparison]::Ordinal) -or $segment.EndsWith('.', [StringComparison]::Ordinal)) {
            throw "$Role contains an unsafe segment: '$Path'."
        }
        if ($segment.Split('.')[0] -match '^(?i:con|prn|aux|nul|com[1-9]|lpt[1-9])$') {
            throw "$Role contains a reserved device name: '$Path'."
        }
    }
    return ($segments -join '\')
}

function Test-IsDescendant {
    param([Parameter(Mandatory)] [string]$Parent, [Parameter(Mandatory)] [string]$Child)
    $prefix = $Parent.TrimEnd('\') + '\'
    return $Child.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NotExcludedPath {
    param([Parameter(Mandatory)] [string]$Path, [Parameter(Mandatory)] [string]$Role)
    if ($Path.StartsWith('D:\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Role must never access D:."
    }
    $excluded = @('C:\Wabbajack')
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if (-not [string]::IsNullOrWhiteSpace($localAppData)) { $excluded += (Join-Path $localAppData 'Grid') }
    foreach ($root in $excluded) {
        if ($Path.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or (Test-IsDescendant -Parent $root -Child $Path)) {
            throw "$Role is inside an excluded root: '$root'."
        }
    }
}

function Get-RequiredProperty {
    param([Parameter(Mandatory)] $Object, [Parameter(Mandatory)] [string]$Name, [Parameter(Mandatory)] [string]$Context)
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { throw "$Context is missing required property '$Name'." }
    return $property.Value
}

function Get-OptionalProperty {
    param($Object, [Parameter(Mandatory)] [string]$Name, $Default = $null)
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return $Default }
    return $property.Value
}

function Assert-SafeText {
    param([AllowNull()] [AllowEmptyString()] [string]$Value, [Parameter(Mandatory)] [string]$Role, [int]$MaximumLength = 2048)
    if ($null -ne $Value -and ($Value.Length -gt $MaximumLength -or
        ($Value.Length -gt 0 -and (Test-HasControlCharacter -Value $Value)))) {
        throw "$Role contains prohibited text."
    }
    return $Value
}

function Read-BoundedJson {
    param([Parameter(Mandatory)] [string]$Path, [Parameter(Mandatory)] [long]$MaximumBytes)
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "JSON evidence must be a regular file: '$Path'."
    }
    if ($item.Length -gt $MaximumBytes) { throw "JSON evidence exceeds the $MaximumBytes-byte limit: '$Path'." }
    return [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false, $true)) | ConvertFrom-Json
}

function Add-Check {
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [Collections.Generic.List[object]]$Checks,
        [Parameter(Mandatory)] [string]$Code,
        [Parameter(Mandatory)] [ValidateSet('passed', 'blocked', 'not-observed')] [string]$Status,
        [Parameter(Mandatory)] [string]$Source,
        [AllowNull()] [string]$Expected,
        [AllowNull()] [string]$Observed,
        [Parameter(Mandatory)] [string]$Summary
    )
    $Checks.Add([pscustomobject][ordered]@{
        code = $Code
        status = $Status
        source = $Source
        expected = Assert-SafeText -Value $Expected -Role "$Code expected"
        observed = Assert-SafeText -Value $Observed -Role "$Code observed"
        summary = Assert-SafeText -Value $Summary -Role "$Code summary"
    })
}

function Get-CanonicalUtcTimestamp {
    return [DateTime]::UtcNow.ToString(
        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
        [Globalization.CultureInfo]::InvariantCulture
    )
}

function New-Blocker {
    param(
        [Parameter(Mandatory)] [ValidateSet('metadata', 'prerequisites', 'artifact')] [string]$Phase,
        [Parameter(Mandatory)] [string]$Code,
        [Parameter(Mandatory)] [string]$Summary,
        [AllowNull()] [string]$Expected,
        [AllowNull()] [string]$Observed
    )
    return [ordered]@{
        phase = $Phase
        code = $Code
        summary = Assert-SafeText -Value $Summary -Role 'blocker summary'
        expected = Assert-SafeText -Value $Expected -Role 'blocker expected'
        observed = Assert-SafeText -Value $Observed -Role 'blocker observed'
        requiresAdditionalApproval = $true
        stoppedAtUtc = Get-CanonicalUtcTimestamp
    }
}

function Set-FirstBlocker {
    param([ref]$Blocker, [Parameter(Mandatory)] $Value)
    if ($null -eq $Blocker.Value) { $Blocker.Value = $Value }
}

function Get-SafeOriginHost {
    param([Parameter(Mandatory)] [string]$Origin)
    if (Test-HasControlCharacter -Value $Origin) { throw 'An executable origin contains a control character.' }
    $uri = $null
    if (-not [Uri]::TryCreate($Origin, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https' -or [string]::IsNullOrWhiteSpace($uri.DnsSafeHost)) {
        throw 'Executable origins must be absolute HTTPS URLs.'
    }
    return $uri.DnsSafeHost.ToLowerInvariant()
}

function Test-VersionMatch {
    param([Parameter(Mandatory)] [string]$Expected, [Parameter(Mandatory)] [string]$Observed)
    if ($Expected.Equals($Observed, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    $expectedVersion = $null
    $observedVersion = $null
    if ([Version]::TryParse($Expected, [ref]$expectedVersion) -and [Version]::TryParse($Observed, [ref]$observedVersion)) {
        $parts = $Expected.Split('.').Count
        $expectedParts = @($expectedVersion.Major, $expectedVersion.Minor, $expectedVersion.Build, $expectedVersion.Revision)
        $observedParts = @($observedVersion.Major, $observedVersion.Minor, $observedVersion.Build, $observedVersion.Revision)
        for ($index = 0; $index -lt $parts; $index++) {
            if ($expectedParts[$index] -ne $observedParts[$index]) { return $false }
        }
        return $true
    }
    return $false
}

function Write-AtomicUtf8Json {
    param([Parameter(Mandatory)] [string]$Path, [Parameter(Mandatory)] [string]$Json)
    $directory = Split-Path -Parent $Path
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    [void](Assert-SafeAbsoluteWindowsPath -Path $directory -Role 'output directory')
    if (Test-Path -LiteralPath $Path) {
        $existing = Get-Item -LiteralPath $Path -Force
        if ($existing.PSIsContainer -or ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Output target is not a regular file: '$Path'."
        }
    }
    $temporaryPath = Join-Path $directory ('.' + [IO.Path]::GetFileName($Path) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Json)
        $stream = [IO.File]::Open($temporaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        if (Test-Path -LiteralPath $Path) { [IO.File]::Move($temporaryPath, $Path, $true) }
        else { [IO.File]::Move($temporaryPath, $Path) }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    }
}

function Invoke-PrerequisiteValidation {
    param(
        [Parameter(Mandatory)] [string]$ResolvedArtifactPath,
        [Parameter(Mandatory)] [string]$ResolvedEvidencePath,
        [Parameter(Mandatory)] [string]$ResolvedOutputPath,
        [Parameter(Mandatory)] [string]$EvidenceRoot,
        [AllowNull()] [string]$ResolvedGameRoot,
        [AllowNull()] [string]$GameLanguage,
        [Parameter(Mandatory)] [long]$EvidenceByteLimit,
        [Parameter(Mandatory)] [int]$RequiredFileLimit,
        [Parameter(Mandatory)] [long]$SingleFileByteLimit,
        [Parameter(Mandatory)] [long]$TotalHashByteLimit
    )

    if (-not (Test-IsDescendant -Parent $EvidenceRoot -Child $ResolvedEvidencePath) -or
        -not (Test-IsDescendant -Parent $EvidenceRoot -Child $ResolvedOutputPath)) {
        throw 'Validation evidence and output escaped the supplied evidence root.'
    }
    $checks = [Collections.Generic.List[object]]::new()
    $warnings = [Collections.Generic.List[string]]::new()
    $blocker = $null
    $runtimeVersion = 'not-observed'
    $runtimeLanguage = if ([string]::IsNullOrWhiteSpace($GameLanguage)) { 'not-observed' } else { $GameLanguage }
    $creationStatus = 'not-observed'
    $artifactEvidenceRead = $false
    $externalGameEvidenceRead = $false
    $artifactResult = [ordered]@{ path = $ResolvedArtifactPath; exists = $false; length = $null; sha256 = $null }
    $domains = @()
    $evidence = $null

    if (-not (Test-Path -LiteralPath $ResolvedArtifactPath -PathType Leaf)) {
        Add-Check -Checks $checks -Code 'artifact-not-found' -Status blocked -Source artifact -Expected $approved.ArtifactPath -Observed $null -Summary 'The approved ASSOS artifact is absent.'
        Set-FirstBlocker -Blocker ([ref]$blocker) -Value (New-Blocker -Phase artifact -Code 'artifact-not-found' -Summary 'The approved ASSOS artifact must be downloaded and inspected before payload work.' -Expected $approved.ArtifactPath -Observed $null)
    }
    else {
        $artifactItem = Get-Item -LiteralPath $ResolvedArtifactPath -Force
        if ($artifactItem.PSIsContainer -or ($artifactItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The artifact must be a regular, non-reparse-point file.'
        }
        $artifactResult.exists = $true
        $artifactResult.length = [long]$artifactItem.Length
        $sizeStatus = if ($artifactItem.Length -eq $approved.ArtifactLength) { 'passed' } else { 'blocked' }
        Add-Check -Checks $checks -Code 'artifact-size' -Status $sizeStatus -Source official-metadata -Expected ([string]$approved.ArtifactLength) -Observed ([string]$artifactItem.Length) -Summary 'Compared the local list artifact length with official metadata.'
        if ($sizeStatus -eq 'blocked') {
            Set-FirstBlocker -Blocker ([ref]$blocker) -Value (New-Blocker -Phase artifact -Code 'artifact-size-mismatch' -Summary 'The local artifact length does not match official ASSOS 1.3.0 metadata.' -Expected ([string]$approved.ArtifactLength) -Observed ([string]$artifactItem.Length))
        }
        else {
            # Hash only the exact, metadata-sized artifact; a substituted oversized file is never streamed.
            $artifactResult.sha256 = (Get-FileHash -LiteralPath $ResolvedArtifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }

    if (-not (Test-Path -LiteralPath $ResolvedEvidencePath -PathType Leaf)) {
        Add-Check -Checks $checks -Code 'artifact-evidence-not-found' -Status blocked -Source artifact -Expected $ResolvedEvidencePath -Observed $null -Summary 'The reviewed artifact-evidence JSON is absent.'
        Set-FirstBlocker -Blocker ([ref]$blocker) -Value (New-Blocker -Phase artifact -Code 'artifact-evidence-not-found' -Summary 'Reviewed artifact evidence is required before prerequisite approval.' -Expected $ResolvedEvidencePath -Observed $null)
    }
    else {
        $evidence = Read-BoundedJson -Path $ResolvedEvidencePath -MaximumBytes $EvidenceByteLimit
        $artifactEvidenceRead = $true
        if ((Get-RequiredProperty $evidence schemaVersion evidence) -ne 1 -or (Get-RequiredProperty $evidence kind evidence) -ne 'grid-test-lab-artifact-evidence') {
            throw 'Unsupported artifact-evidence schema.'
        }
        $list = Get-RequiredProperty $evidence list evidence
        $machineName = [string](Get-RequiredProperty $list machineName 'evidence.list')
        $listVersion = [string](Get-RequiredProperty $list version 'evidence.list')
        $identityStatus = if ($machineName -ceq $approved.ListMachineName -and $listVersion -ceq $approved.ListVersion) { 'passed' } else { 'blocked' }
        Add-Check -Checks $checks -Code 'list-identity' -Status $identityStatus -Source artifact -Expected "$($approved.ListMachineName) $($approved.ListVersion)" -Observed "$machineName $listVersion" -Summary 'Validated the artifact-derived list identity.'
        if ($identityStatus -eq 'blocked') {
            Set-FirstBlocker -Blocker ([ref]$blocker) -Value (New-Blocker -Phase artifact -Code 'artifact-identity-mismatch' -Summary 'Artifact evidence identifies a different list or version.' -Expected "$($approved.ListMachineName) $($approved.ListVersion)" -Observed "$machineName $listVersion")
        }

        $declaredArtifact = Get-RequiredProperty $evidence artifact evidence
        $declaredLength = [long](Get-RequiredProperty $declaredArtifact length 'evidence.artifact')
        $declaredHash = [string](Get-RequiredProperty $declaredArtifact sha256 'evidence.artifact')
        if ($declaredHash -notmatch '^[a-fA-F0-9]{64}$') { throw 'evidence.artifact.sha256 is not a SHA-256 value.' }
        if ($declaredLength -ne $approved.ArtifactLength) {
            Add-Check -Checks $checks -Code 'evidence-artifact-size' -Status blocked -Source official-metadata -Expected ([string]$approved.ArtifactLength) -Observed ([string]$declaredLength) -Summary 'Artifact evidence records the wrong official artifact length.'
            Set-FirstBlocker -Blocker ([ref]$blocker) -Value (New-Blocker -Phase artifact -Code 'evidence-artifact-size-mismatch' -Summary 'Reviewed artifact evidence conflicts with official metadata.' -Expected ([string]$approved.ArtifactLength) -Observed ([string]$declaredLength))
        }
        if ($null -ne $artifactResult.sha256) {
            $hashStatus = if ($artifactResult.sha256 -ceq $declaredHash.ToLowerInvariant() -and $artifactResult.length -eq $declaredLength) { 'passed' } else { 'blocked' }
            Add-Check -Checks $checks -Code 'artifact-hash' -Status $hashStatus -Source artifact -Expected "$($declaredHash.ToLowerInvariant())/$declaredLength" -Observed "$($artifactResult.sha256)/$($artifactResult.length)" -Summary 'Matched the local artifact to the reviewed artifact evidence.'
            if ($hashStatus -eq 'blocked') {
                Set-FirstBlocker -Blocker ([ref]$blocker) -Value (New-Blocker -Phase artifact -Code 'artifact-hash-mismatch' -Summary 'The local artifact does not match the hash-recorded artifact evidence.' -Expected "$($declaredHash.ToLowerInvariant())/$declaredLength" -Observed "$($artifactResult.sha256)/$($artifactResult.length)")
            }
        }

        $inspection = Get-RequiredProperty $evidence inspection evidence
        $domainValues = @(Get-RequiredProperty $inspection archiveSourceDomains 'evidence.inspection')
        if ($domainValues.Count -gt 4096) { throw 'Artifact evidence exceeds the 4096-domain limit.' }
        $domainSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($domainValue in $domainValues) {
            $domain = [string]$domainValue
            if ($domain.Length -gt 253 -or $domain -notmatch '^[A-Za-z0-9.-]+$') { throw "Invalid archive source domain: '$domain'." }
            [void]$domainSet.Add($domain.ToLowerInvariant())
        }
        $domains = @($domainSet | Sort-Object)
        if ($domains.Count -eq 0) {
            Add-Check -Checks $checks -Code 'archive-origins-not-observed' -Status blocked -Source artifact -Expected 'at least one inspected origin domain' -Observed $null -Summary 'No archive source domains were recorded.'
            Set-FirstBlocker -Blocker ([ref]$blocker) -Value (New-Blocker -Phase artifact -Code 'archive-origins-not-observed' -Summary 'The embedded download manifest must be inspected before payload work.' -Expected 'at least one inspected origin domain' -Observed $null)
        }

        $unexpectedOrigins = @((Get-OptionalProperty $inspection unexpectedExecutableOrigins @()))
        if ($unexpectedOrigins.Count -gt 128) { throw 'Artifact evidence exceeds the 128 unexpected-origin limit.' }
        foreach ($origin in $unexpectedOrigins) {
            $host = Get-SafeOriginHost -Origin ([string]$origin)
            Add-Check -Checks $checks -Code 'unexpected-executable-origin' -Status blocked -Source artifact -Expected 'none' -Observed $host -Summary 'Artifact inspection recorded an unexpected executable origin.'
            Set-FirstBlocker -Blocker ([ref]$blocker) -Value (New-Blocker -Phase artifact -Code 'unexpected-executable-origin' -Summary 'Unexpected executable origins require review and new approval.' -Expected 'none' -Observed $host)
        }

        $storageEstimate = [long](Get-RequiredProperty $inspection storageEstimateBytes 'evidence.inspection')
        $storageStatus = if ($storageEstimate -ge 0 -and $storageEstimate -le $approved.MaximumApprovedStorageBytes) { 'passed' } else { 'blocked' }
        Add-Check -Checks $checks -Code 'storage-estimate' -Status $storageStatus -Source artifact -Expected "0-$($approved.MaximumApprovedStorageBytes)" -Observed ([string]$storageEstimate) -Summary 'Compared artifact-derived storage with the approved 50 GiB reservation.'
        if ($storageStatus -eq 'blocked') {
            Set-FirstBlocker -Blocker ([ref]$blocker) -Value (New-Blocker -Phase artifact -Code 'storage-reapproval-required' -Summary 'Artifact-derived storage materially exceeds the approved reservation.' -Expected "at most $($approved.MaximumApprovedStorageBytes) bytes" -Observed "$storageEstimate bytes")
        }

        $requirements = Get-RequiredProperty $evidence requirements evidence
        $artifactRequirements = Get-RequiredProperty $requirements artifact 'evidence.requirements'
        $expectedVersion = Assert-SafeText -Value ([string](Get-OptionalProperty $artifactRequirements gameVersion '')) -Role 'artifact game version' -MaximumLength 64
        $expectedLanguage = Assert-SafeText -Value ([string](Get-OptionalProperty $artifactRequirements language '')) -Role 'artifact game language' -MaximumLength 64
        $versionFileRelative = [string](Get-OptionalProperty $artifactRequirements gameVersionFile 'SkyrimSE.exe')
        $ambiguities = @((Get-OptionalProperty $artifactRequirements ambiguities @()))
        if ($ambiguities.Count -gt 128) { throw 'Artifact evidence exceeds the 128-ambiguity limit.' }
        if ([string]::IsNullOrWhiteSpace($expectedVersion) -or [string]::IsNullOrWhiteSpace($expectedLanguage) -or $ambiguities.Count -gt 0) {
            $observedAmbiguity = if ($ambiguities.Count -gt 0) { [string]$ambiguities[0] } else { 'game version or language absent' }
            Add-Check -Checks $checks -Code 'artifact-requirements-ambiguous' -Status blocked -Source artifact -Expected 'unambiguous game version and language' -Observed (Assert-SafeText $observedAmbiguity 'requirement ambiguity') -Summary 'Artifact requirements are missing or ambiguous.'
            Set-FirstBlocker -Blocker ([ref]$blocker) -Value (New-Blocker -Phase prerequisites -Code 'artifact-requirements-ambiguous' -Summary 'Artifact-derived prerequisites must be resolved without assumptions.' -Expected 'unambiguous game version and language' -Observed (Assert-SafeText $observedAmbiguity 'requirement ambiguity'))
        }

        $documentation = Get-OptionalProperty $requirements documentation $null
        if ($null -ne $documentation) {
            $documentationVersion = [string](Get-OptionalProperty $documentation gameVersion '')
            $documentationLanguage = [string](Get-OptionalProperty $documentation language '')
            if ((-not [string]::IsNullOrWhiteSpace($documentationVersion) -and $documentationVersion -cne $expectedVersion) -or
                (-not [string]::IsNullOrWhiteSpace($documentationLanguage) -and $documentationLanguage -cne $expectedLanguage)) {
                Add-Check -Checks $checks -Code 'documentation-conflict' -Status blocked -Source official-documentation -Expected "$expectedVersion/$expectedLanguage" -Observed "$documentationVersion/$documentationLanguage" -Summary 'Matching documentation conflicts with artifact-derived requirements.'
                Set-FirstBlocker -Blocker ([ref]$blocker) -Value (New-Blocker -Phase prerequisites -Code 'documentation-conflict' -Summary 'Artifact and matching official documentation disagree.' -Expected "$expectedVersion/$expectedLanguage" -Observed "$documentationVersion/$documentationLanguage")
            }
        }

        if ($null -eq $ResolvedGameRoot) {
            Add-Check -Checks $checks -Code 'game-root-not-observed' -Status blocked -Source local-observation -Expected 'an explicitly approved read-only game root' -Observed $null -Summary 'No external game root was supplied.'
            Set-FirstBlocker -Blocker ([ref]$blocker) -Value (New-Blocker -Phase prerequisites -Code 'game-root-not-observed' -Summary 'Local game prerequisites were not observed.' -Expected 'explicit read-only game root opt-in' -Observed $null)
        }
        else {
            $externalGameEvidenceRead = $true
            $versionRelative = Assert-SafeRelativePath -Path $versionFileRelative -Role 'game version file'
            $versionPath = Join-Path $ResolvedGameRoot $versionRelative
            if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf)) {
                Add-Check -Checks $checks -Code 'game-version-file-missing' -Status blocked -Source local-observation -Expected $versionRelative -Observed $null -Summary 'The artifact-declared game version file is absent.'
                Set-FirstBlocker -Blocker ([ref]$blocker) -Value (New-Blocker -Phase prerequisites -Code 'game-version-file-missing' -Summary 'The local game cannot satisfy the artifact version check.' -Expected $versionRelative -Observed $null)
            }
            else {
                [void](Assert-SafeAbsoluteWindowsPath -Path $versionPath -Role 'game version file')
                $runtimeVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($versionPath).FileVersion
                if ([string]::IsNullOrWhiteSpace($runtimeVersion)) { $runtimeVersion = 'not-observed' }
                $runtimeVersion = Assert-SafeText -Value $runtimeVersion -Role 'observed game version' -MaximumLength 64
                $versionStatus = if (-not [string]::IsNullOrWhiteSpace($expectedVersion) -and (Test-VersionMatch -Expected $expectedVersion -Observed $runtimeVersion)) { 'passed' } else { 'blocked' }
                Add-Check -Checks $checks -Code 'game-version' -Status $versionStatus -Source local-observation -Expected $expectedVersion -Observed $runtimeVersion -Summary 'Compared the local game executable version with artifact-derived requirements.'
                if ($versionStatus -eq 'blocked') {
                    Set-FirstBlocker -Blocker ([ref]$blocker) -Value (New-Blocker -Phase prerequisites -Code 'game-version-mismatch' -Summary 'Changing the installed game version is outside this task.' -Expected $expectedVersion -Observed $runtimeVersion)
                }
            }

            $requiredFiles = @((Get-OptionalProperty $artifactRequirements gameFiles @()))
            if ($requiredFiles.Count -gt $RequiredFileLimit) { throw "Artifact evidence exceeds the $RequiredFileLimit required-file limit." }
            $totalHashedBytes = [long]0
            $creationRequirements = 0
            $creationMatches = 0
            foreach ($requiredFile in $requiredFiles) {
                $relativePath = Assert-SafeRelativePath -Path ([string](Get-RequiredProperty $requiredFile relativePath 'gameFiles item')) -Role 'required game file'
                $role = [string](Get-RequiredProperty $requiredFile role 'gameFiles item')
                if ($role -notin @('game-binary', 'required-source', 'creation-content')) { throw "Unsupported game-file role: '$role'." }
                if ($role -eq 'creation-content') { $creationRequirements++ }
                $filePath = Join-Path $ResolvedGameRoot $relativePath
                if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
                    Add-Check -Checks $checks -Code 'required-game-file-missing' -Status blocked -Source local-observation -Expected $relativePath -Observed $null -Summary "A required $role file is absent."
                    Set-FirstBlocker -Blocker ([ref]$blocker) -Value (New-Blocker -Phase prerequisites -Code 'required-game-file-missing' -Summary 'Downloading or replacing game content is outside this task.' -Expected $relativePath -Observed $null)
                    continue
                }
                [void](Assert-SafeAbsoluteWindowsPath -Path $filePath -Role 'required game file')
                $fileItem = Get-Item -LiteralPath $filePath -Force
                if ($fileItem.PSIsContainer -or ($fileItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Required game file is not a regular file: '$relativePath'." }
                $expectedLengthValue = Get-OptionalProperty $requiredFile length $null
                $expectedHashValue = [string](Get-OptionalProperty $requiredFile sha256 '')
                if ($null -eq $expectedLengthValue -and [string]::IsNullOrWhiteSpace($expectedHashValue)) {
                    throw "Required game file '$relativePath' has neither a length nor a SHA-256 requirement."
                }
                $match = $true
                $observedParts = [Collections.Generic.List[string]]::new()
                $observedParts.Add("length=$($fileItem.Length)")
                if ($null -ne $expectedLengthValue -and $fileItem.Length -ne [long]$expectedLengthValue) { $match = $false }
                if (-not [string]::IsNullOrWhiteSpace($expectedHashValue)) {
                    if ($expectedHashValue -notmatch '^[a-fA-F0-9]{64}$') { throw "Invalid SHA-256 requirement for '$relativePath'." }
                    if ($fileItem.Length -gt $SingleFileByteLimit -or $totalHashedBytes + $fileItem.Length -gt $TotalHashByteLimit) {
                        throw "Hash limits would be exceeded by '$relativePath'."
                    }
                    $observedHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
                    $totalHashedBytes += $fileItem.Length
                    $observedParts.Add("sha256=$observedHash")
                    if ($observedHash -cne $expectedHashValue.ToLowerInvariant()) { $match = $false }
                }
                $expectedText = "length=$expectedLengthValue;sha256=$($expectedHashValue.ToLowerInvariant())"
                Add-Check -Checks $checks -Code 'required-game-file' -Status $(if ($match) { 'passed' } else { 'blocked' }) -Source local-observation -Expected $expectedText -Observed ($observedParts -join ';') -Summary "Compared required $role evidence: $relativePath"
                if ($match -and $role -eq 'creation-content') { $creationMatches++ }
                if (-not $match) {
                    Set-FirstBlocker -Blocker ([ref]$blocker) -Value (New-Blocker -Phase prerequisites -Code 'required-game-file-mismatch' -Summary 'Replacing or downloading game content is outside this task.' -Expected "$relativePath $expectedText" -Observed ($observedParts -join ';'))
                }
            }
            if ($creationRequirements -gt 0) { $creationStatus = if ($creationMatches -eq $creationRequirements) { 'present' } else { 'incomplete' } }
        }

        if ([string]::IsNullOrWhiteSpace($GameLanguage)) {
            Add-Check -Checks $checks -Code 'game-language-not-observed' -Status blocked -Source local-observation -Expected $expectedLanguage -Observed $null -Summary 'No read-only game-language observation was supplied.'
            Set-FirstBlocker -Blocker ([ref]$blocker) -Value (New-Blocker -Phase prerequisites -Code 'game-language-not-observed' -Summary 'Game language must be observed without changing Steam or game state.' -Expected $expectedLanguage -Observed $null)
        }
        elseif (-not $GameLanguage.Equals($expectedLanguage, [StringComparison]::OrdinalIgnoreCase)) {
            Add-Check -Checks $checks -Code 'game-language' -Status blocked -Source local-observation -Expected $expectedLanguage -Observed $GameLanguage -Summary 'Local game language does not match the artifact requirement.'
            Set-FirstBlocker -Blocker ([ref]$blocker) -Value (New-Blocker -Phase prerequisites -Code 'game-language-mismatch' -Summary 'Changing game language is outside this task.' -Expected $expectedLanguage -Observed $GameLanguage)
        }
        else { Add-Check -Checks $checks -Code 'game-language' -Status passed -Source local-observation -Expected $expectedLanguage -Observed $GameLanguage -Summary 'Local game language matches the artifact requirement.' }

        $machineComponents = @((Get-OptionalProperty $artifactRequirements machineComponents @()))
        if ($machineComponents.Count -gt 128) { throw 'Artifact evidence exceeds the 128-component limit.' }
        foreach ($component in $machineComponents) {
            $name = Assert-SafeText -Value ([string](Get-RequiredProperty $component name 'machineComponents item')) -Role 'component name' -MaximumLength 256
            $expected = Assert-SafeText -Value ([string](Get-RequiredProperty $component expected 'machineComponents item')) -Role 'component expected'
            $status = [string](Get-RequiredProperty $component status 'machineComponents item')
            $observed = Assert-SafeText -Value ([string](Get-OptionalProperty $component observed 'not-observed')) -Role 'component observed'
            if ($status -notin @('passed', 'blocked', 'not-observed')) { throw "Invalid machine-component status for '$name'." }
            Add-Check -Checks $checks -Code 'machine-component' -Status $status -Source local-observation -Expected "${name}: $expected" -Observed $observed -Summary 'Recorded a bounded, reviewed machine-prerequisite observation.'
            if ($status -ne 'passed') {
                Set-FirstBlocker -Blocker ([ref]$blocker) -Value (New-Blocker -Phase prerequisites -Code 'machine-component-not-satisfied' -Summary 'Installing or changing machine prerequisites is outside this task.' -Expected "${name}: $expected" -Observed $observed)
            }
        }
    }

    $runtimeNotes = [Collections.Generic.List[string]]::new()
    if ($artifactEvidenceRead) {
        $runtimeNotes.Add('Requirements were read from the hash-recorded artifact-evidence file; no runtime version was assumed.')
    }
    else {
        $runtimeNotes.Add('Artifact-derived requirements were not observed because the reviewed artifact-evidence file was absent.')
    }
    if ($externalGameEvidenceRead) {
        $runtimeNotes.Add('External game evidence was read only from explicitly listed files under the opted-in root.')
    }
    elseif ($null -eq $ResolvedGameRoot) {
        $runtimeNotes.Add('No external game root was supplied or read.')
    }
    else {
        $runtimeNotes.Add('The supplied external game root was not read because reviewed artifact evidence was unavailable.')
    }
    $runtime = [ordered]@{
        gameVersion = $runtimeVersion
        language = $runtimeLanguage
        prerequisiteStatus = if ($null -eq $blocker) { 'passed-read-only' } else { 'blocked' }
        creationContentStatus = $creationStatus
        notes = @($runtimeNotes)
    }
    $report = [ordered]@{
        schemaVersion = 1
        kind = 'grid-test-lab-prerequisite-report'
        observedAtUtc = Get-CanonicalUtcTimestamp
        artifact = $artifactResult
        artifactEvidencePath = $ResolvedEvidencePath
        externalGameRoot = $ResolvedGameRoot
        archiveSourceDomains = @($domains)
        runtime = $runtime
        checks = @($checks)
        warnings = @($warnings)
    }
    if ($null -ne $blocker) { $report.blocker = $blocker }
    Write-AtomicUtf8Json -Path $ResolvedOutputPath -Json (ConvertTo-Json -InputObject $report -Depth 10)
    return [pscustomobject]$report
}

function Invoke-SelfTest {
    $testRoot = Join-Path ([IO.Path]::GetTempPath()) ('grid-prerequisite-selftest-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    try {
        $safe = Assert-SafeAbsoluteWindowsPath -Path $testRoot -Role 'self-test root'
        $rejections = 0
        foreach ($unsafe in @('D:\forbidden', 'C:\safe\..\escape', '\\server\share\file', 'C:\safe\CON\file', 'C:\safe\file:stream')) {
            try { [void](Assert-SafeAbsoluteWindowsPath -Path $unsafe -Role 'self-test unsafe' -PermitMissing) }
            catch { $rejections++ }
        }
        # D: is rejected by the exclusion guard, while the parser accepts ordinary drive paths.
        if ($rejections -ne 4) { throw "Path-defense self-test rejected $rejections of 4 parser-invalid paths." }
        try { Assert-NotExcludedPath -Path 'D:\forbidden' -Role 'self-test unsafe'; throw 'D: exclusion self-test failed.' } catch { if ($_.Exception.Message -eq 'D: exclusion self-test failed.') { throw } }
        $output = Join-Path $safe 'atomic.json'
        Write-AtomicUtf8Json -Path $output -Json '{"pass":true}'
        $parsed = Read-BoundedJson -Path $output -MaximumBytes 1024
        if ($parsed.pass -ne $true) { throw 'Atomic JSON self-test failed.' }
        Write-AtomicUtf8Json -Path $output -Json '{"pass":false,"replacement":true}'
        $replaced = Read-BoundedJson -Path $output -MaximumBytes 1024
        if ($replaced.pass -ne $false -or $replaced.replacement -ne $true) {
            throw 'Atomic JSON existing-output replacement self-test failed.'
        }
        $relative = Assert-SafeRelativePath -Path 'Data\Skyrim.esm' -Role 'self-test relative path'
        if ($relative -cne 'Data\Skyrim.esm') { throw 'Relative-path self-test failed.' }
        try { [void](Assert-SafeRelativePath -Path '..\escape' -Role 'self-test unsafe relative'); throw 'Traversal self-test failed.' } catch { if ($_.Exception.Message -eq 'Traversal self-test failed.') { throw } }
        $artifact = Join-Path $safe 'ASSOS.wabbajack'
        $artifactStream = [IO.File]::Open($artifact, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $artifactStream.SetLength($approved.ArtifactLength) } finally { $artifactStream.Dispose() }
        $artifactHash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()

        $gameRoot = Join-Path $safe 'game'
        [IO.Directory]::CreateDirectory($gameRoot) | Out-Null
        $pwshSource = (Get-Process -Id $PID).Path
        $versionFile = Join-Path $gameRoot 'Game.exe'
        Copy-Item -LiteralPath $pwshSource -Destination $versionFile
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($versionFile).FileVersion
        [IO.File]::WriteAllText((Join-Path $gameRoot 'required.bin'), 'required', [Text.UTF8Encoding]::new($false))
        $requiredItem = Get-Item -LiteralPath (Join-Path $gameRoot 'required.bin')

        $evidencePath = Join-Path $safe 'artifact-evidence.json'
        $evidenceObject = [ordered]@{
            schemaVersion = 1
            kind = 'grid-test-lab-artifact-evidence'
            list = [ordered]@{ machineName = 'ASSOS'; version = '1.3.0' }
            artifact = [ordered]@{ length = $approved.ArtifactLength; sha256 = $artifactHash }
            inspection = [ordered]@{
                archiveSourceDomains = @('example.invalid')
                unexpectedExecutableOrigins = @()
                storageEstimateBytes = 1GB
            }
            requirements = [ordered]@{
                artifact = [ordered]@{
                    gameVersion = $version
                    language = 'English'
                    gameVersionFile = 'Game.exe'
                    gameFiles = @([ordered]@{ relativePath = 'required.bin'; role = 'required-source'; length = [long]$requiredItem.Length })
                    machineComponents = @()
                    ambiguities = @()
                }
                documentation = [ordered]@{ gameVersion = $version; language = 'English' }
            }
        }
        Write-AtomicUtf8Json -Path $evidencePath -Json (ConvertTo-Json -InputObject $evidenceObject -Depth 8)
        $reportPath = Join-Path $safe 'report.json'
        $invokeParameters = @{
            ResolvedArtifactPath = $artifact
            ResolvedEvidencePath = $evidencePath
            ResolvedOutputPath = $reportPath
            EvidenceRoot = $safe
            ResolvedGameRoot = $gameRoot
            GameLanguage = 'English'
            EvidenceByteLimit = 1MB
            RequiredFileLimit = 10
            SingleFileByteLimit = 1MB
            TotalHashByteLimit = 1MB
        }
        $report = Invoke-PrerequisiteValidation @invokeParameters
        if ($report.runtime.prerequisiteStatus -ne 'passed-read-only' -or $null -ne $report.PSObject.Properties['blocker']) {
            throw 'End-to-end pass-path self-test failed.'
        }
        if ($report.observedAtUtc -notmatch 'Z$') { throw 'Report UTC timestamp is not canonical Z form.' }
        $readBack = Read-BoundedJson -Path $reportPath -MaximumBytes 1MB
        if ($readBack.runtime.prerequisiteStatus -ne 'passed-read-only') { throw 'Report serialization self-test failed.' }

        $blockedParameters = $invokeParameters.Clone()
        $blockedParameters.ResolvedArtifactPath = Join-Path $safe 'missing.wabbajack'
        $blockedParameters.ResolvedEvidencePath = Join-Path $safe 'missing-evidence.json'
        $blockedParameters.ResolvedOutputPath = Join-Path $safe 'blocked-report.json'
        $blockedReport = Invoke-PrerequisiteValidation @blockedParameters
        if ($blockedReport.runtime.prerequisiteStatus -ne 'blocked' -or $blockedReport.blocker.code -ne 'artifact-not-found') {
            throw 'End-to-end typed-blocker self-test failed.'
        }
        if ($blockedReport.observedAtUtc -notmatch 'Z$' -or $blockedReport.blocker.stoppedAtUtc -notmatch 'Z$') {
            throw 'Blocked-report UTC timestamps are not canonical Z form.'
        }
        $blockedNotes = @($blockedReport.runtime.notes)
        if ($blockedNotes -contains 'Requirements were read from the hash-recorded artifact-evidence file; no runtime version was assumed.' -or
            $blockedNotes -contains 'External game evidence was read only from explicitly listed files under the opted-in root.') {
            throw 'Missing-input report made an observation that did not occur.'
        }
        if ($blockedNotes -notcontains 'Artifact-derived requirements were not observed because the reviewed artifact-evidence file was absent.' -or
            $blockedNotes -notcontains 'The supplied external game root was not read because reviewed artifact evidence was unavailable.') {
            throw 'Missing-input report omitted its conditional non-observation notes.'
        }

        [pscustomobject]@{ passed = $true; temporaryRoot = $safe; tests = 15 }
    }
    finally {
        if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
    }
}

if ($PSCmdlet.ParameterSetName -eq 'SelfTest') {
    Invoke-SelfTest
    return
}

$resolvedArtifact = Assert-SafeAbsoluteWindowsPath -Path $ArtifactPath -Role 'artifact' -PermitMissing
if (-not $resolvedArtifact.Equals($approved.ArtifactPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw "ArtifactPath must be the approved literal path '$($approved.ArtifactPath)'."
}
$resolvedEvidenceRoot = Assert-SafeAbsoluteWindowsPath -Path $approved.EvidenceRoot -Role 'evidence root' -PermitMissing
$resolvedEvidence = Assert-SafeAbsoluteWindowsPath -Path $ArtifactEvidencePath -Role 'artifact evidence' -PermitMissing
$resolvedOutput = Assert-SafeAbsoluteWindowsPath -Path $OutputPath -Role 'output' -PermitMissing
foreach ($candidate in @($resolvedEvidence, $resolvedOutput)) {
    if (-not (Test-IsDescendant -Parent $resolvedEvidenceRoot -Child $candidate)) {
        throw "Evidence inputs and outputs must be descendants of '$resolvedEvidenceRoot'."
    }
}
if ($resolvedEvidence.Equals($resolvedOutput, [StringComparison]::OrdinalIgnoreCase)) { throw 'ArtifactEvidencePath and OutputPath must differ.' }
if (-not [string]::IsNullOrWhiteSpace($ObservedGameLanguage)) {
    $ObservedGameLanguage = Assert-SafeText -Value $ObservedGameLanguage -Role 'observed game language' -MaximumLength 64
}

$resolvedGame = $null
if (-not [string]::IsNullOrWhiteSpace($ExternalGameRoot)) {
    if (-not $AllowExternalGameRootReadOnly) { throw 'ExternalGameRoot requires explicit -AllowExternalGameRootReadOnly opt-in.' }
    $resolvedGame = Assert-SafeAbsoluteWindowsPath -Path $ExternalGameRoot -Role 'external game root'
    Assert-NotExcludedPath -Path $resolvedGame -Role 'external game root'
    $gameItem = Get-Item -LiteralPath $resolvedGame -Force
    if (-not $gameItem.PSIsContainer) { throw 'ExternalGameRoot must be a directory.' }
}
elseif ($AllowExternalGameRootReadOnly) { throw '-AllowExternalGameRootReadOnly requires ExternalGameRoot.' }

$result = Invoke-PrerequisiteValidation `
    -ResolvedArtifactPath $resolvedArtifact `
    -ResolvedEvidencePath $resolvedEvidence `
    -ResolvedOutputPath $resolvedOutput `
    -EvidenceRoot $resolvedEvidenceRoot `
    -ResolvedGameRoot $resolvedGame `
    -GameLanguage $ObservedGameLanguage `
    -EvidenceByteLimit ([long]$MaximumEvidenceKiB * 1KB) `
    -RequiredFileLimit $MaximumRequiredFiles `
    -SingleFileByteLimit ([long]$MaximumSingleFileMiB * 1MB) `
    -TotalHashByteLimit ([long]$MaximumTotalHashMiB * 1MB)

Write-Host "Prerequisite report: $resolvedOutput"
Write-Host "Status: $($result.runtime.prerequisiteStatus)"
$result
