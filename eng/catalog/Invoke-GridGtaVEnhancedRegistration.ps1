[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $GameRoot,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $FiveFuryWheel,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $UvExecutable,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $HistoricalLibrary,

    [Parameter(Mandatory = $false)]
    [ValidateNotNullOrEmpty()]
    [string] $RockstarCloudSnapshotBundle,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $ObservedAtUtc,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $ActorObservedAtUtc,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $SpatialObservedAtUtc,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-RequiredFile {
    param([string] $Path, [string] $Name)

    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
    if (-not (Test-Path -LiteralPath $resolved.Path -PathType Leaf)) {
        throw "$Name must be an existing file: $Path"
    }
    return $resolved.Path
}

function Resolve-RequiredDirectory {
    param([string] $Path, [string] $Name)

    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
    if (-not (Test-Path -LiteralPath $resolved.Path -PathType Container)) {
        throw "$Name must be an existing directory: $Path"
    }
    return $resolved.Path
}

function Invoke-CheckedProcess {
    param(
        [string] $FilePath,
        [string[]] $Arguments,
        [string] $WorkingDirectory
    )

    Push-Location -LiteralPath $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Process failed with exit code $LASTEXITCODE`: $FilePath $($Arguments -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
}

function Get-FileSha256 {
    param([string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-TextSha256 {
    param([string] $Value)

    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($hasher.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $hasher.Dispose()
    }
}

function Get-TreeManifest {
    param([string] $Root)

    $resolvedRoot = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\', '/')
    $rows = @()
    foreach ($file in Get-ChildItem -LiteralPath $resolvedRoot -Recurse -File | Sort-Object FullName) {
        $relative = $file.FullName.Substring($resolvedRoot.Length).TrimStart('\', '/').Replace('\', '/')
        $rows += [pscustomobject][ordered]@{
            path = $relative
            byteLength = $file.Length
            sha256 = Get-FileSha256 -Path $file.FullName
        }
    }
    return @($rows)
}

function Assert-EquivalentManifest {
    param(
        [object[]] $Left,
        [object[]] $Right,
        [string] $Name
    )

    $leftJson = ConvertTo-Json -InputObject @($Left) -Depth 8 -Compress
    $rightJson = ConvertTo-Json -InputObject @($Right) -Depth 8 -Compress
    if (-not [string]::Equals($leftJson, $rightJson, [StringComparison]::Ordinal)) {
        throw "$Name replay manifests differ."
    }
}

function Assert-ExactManifestPaths {
    param(
        [object[]] $Manifest,
        [string[]] $ExpectedPaths,
        [string] $Name
    )

    $actual = @($Manifest | ForEach-Object { [string]$_.path } | Sort-Object)
    $expected = @($ExpectedPaths | Sort-Object)
    $actualJson = ConvertTo-Json -InputObject $actual -Compress
    $expectedJson = ConvertTo-Json -InputObject $expected -Compress
    if (-not [string]::Equals($actualJson, $expectedJson, [StringComparison]::Ordinal)) {
        throw "$Name output paths do not exactly match the registered output contract."
    }
}

function Write-Utf8NoBomNewFile {
    param([string] $Path, [string] $Content)

    if (Test-Path -LiteralPath $Path) {
        throw "Refusing to overwrite output: $Path"
    }
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, $Content + "`n", $utf8NoBom)
}

$repositoryRoot = (& git rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repositoryRoot)) {
    throw 'The registration command must run from a Git worktree.'
}
$repositoryRoot = (Resolve-Path -LiteralPath $repositoryRoot).Path
$gameRootPath = Resolve-RequiredDirectory -Path $GameRoot -Name 'GameRoot'
$wheelPath = Resolve-RequiredFile -Path $FiveFuryWheel -Name 'FiveFuryWheel'
$uvPath = Resolve-RequiredFile -Path $UvExecutable -Name 'UvExecutable'
$historicalPath = Resolve-RequiredFile -Path $HistoricalLibrary -Name 'HistoricalLibrary'
$cloudBundlePath = $null
$cloudBundleManifest = @()
$cloudBundleDigest = $null
if (-not [string]::IsNullOrWhiteSpace($RockstarCloudSnapshotBundle)) {
    $cloudBundlePath = Resolve-RequiredDirectory -Path $RockstarCloudSnapshotBundle -Name 'RockstarCloudSnapshotBundle'
    $cloudBundleManifest = @(Get-TreeManifest -Root $cloudBundlePath)
    Assert-ExactManifestPaths -Manifest $cloudBundleManifest -ExpectedPaths @(
        'rockstar-cloud-job-schema.v1.json',
        'rockstar-cloud-job-snapshot-receipt.v1.json',
        'rockstar-cloud-job-snapshot.v1.json'
    ) -Name 'Rockstar cloud snapshot bundle'
    $cloudBundleCanonical = ConvertTo-Json -InputObject @($cloudBundleManifest) -Depth 8 -Compress
    $cloudBundleDigest = Get-TextSha256 -Value $cloudBundleCanonical
}

$parsedObservation = [DateTimeOffset]::MinValue
$observationStyle = [Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal
if ($ObservedAtUtc -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$' -or
    -not [DateTimeOffset]::TryParse(
        $ObservedAtUtc,
        [Globalization.CultureInfo]::InvariantCulture,
        $observationStyle,
        [ref] $parsedObservation)) {
    throw 'ObservedAtUtc must be an exact RFC 3339 UTC timestamp ending in Z.'
}
$parsedActorObservation = [DateTimeOffset]::MinValue
if ($ActorObservedAtUtc -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$' -or
    -not [DateTimeOffset]::TryParse(
        $ActorObservedAtUtc,
        [Globalization.CultureInfo]::InvariantCulture,
        $observationStyle,
        [ref] $parsedActorObservation)) {
    throw 'ActorObservedAtUtc must be an exact RFC 3339 UTC timestamp ending in Z.'
}
$parsedSpatialObservation = [DateTimeOffset]::MinValue
if ($SpatialObservedAtUtc -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$' -or
    -not [DateTimeOffset]::TryParse(
        $SpatialObservedAtUtc,
        [Globalization.CultureInfo]::InvariantCulture,
        $observationStyle,
        [ref] $parsedSpatialObservation)) {
    throw 'SpatialObservedAtUtc must be an exact RFC 3339 UTC timestamp ending in Z.'
}

$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) {
    if ((Get-ChildItem -LiteralPath $outputPath -Force | Select-Object -First 1) -ne $null) {
        throw 'OutputDirectory must be absent or empty.'
    }
}
else {
    [void](New-Item -ItemType Directory -Path $outputPath)
}

$acquisitionScript = Join-Path $repositoryRoot 'scripts\games\grandtheftautov\catalog\gta_v_enhanced_acquire.py'
$actorAcquisitionScript = Join-Path $repositoryRoot 'scripts\games\grandtheftautov\catalog\gta_v_enhanced_actor_acquire.py'
$spatialAcquisitionScript = Join-Path $repositoryRoot 'scripts\games\grandtheftautov\catalog\gta_v_enhanced_spatial_acquire.py'
$sourceManifest = Join-Path $repositoryRoot 'scripts\games\grandtheftautov\catalog\gta_v_enhanced_registration_sources.v5.json'
$actorSourceManifest = Join-Path $repositoryRoot 'scripts\games\grandtheftautov\catalog\gta_v_enhanced_actor_source_families.v1.json'
$spatialSourceManifest = Join-Path $repositoryRoot 'scripts\games\grandtheftautov\catalog\gta_v_enhanced_spatial_source_families.v1.json'
$canaryProject = Join-Path $repositoryRoot 'eng\catalog\Grid.GtaVEnhanced.Canary\Grid.GtaVEnhanced.Canary.csproj'
[void](Resolve-RequiredFile -Path $acquisitionScript -Name 'AcquisitionScript')
[void](Resolve-RequiredFile -Path $actorAcquisitionScript -Name 'ActorAcquisitionScript')
[void](Resolve-RequiredFile -Path $spatialAcquisitionScript -Name 'SpatialAcquisitionScript')
[void](Resolve-RequiredFile -Path $sourceManifest -Name 'SourceFamilyManifest')
[void](Resolve-RequiredFile -Path $actorSourceManifest -Name 'ActorSourceFamilyManifest')
[void](Resolve-RequiredFile -Path $spatialSourceManifest -Name 'SpatialSourceFamilyManifest')
[void](Resolve-RequiredFile -Path $canaryProject -Name 'CanaryProject')

$lockPath = Join-Path $repositoryRoot 'scripts\games\grandtheftautov\catalog\fivefury.lock.v1.json'
$lock = Get-Content -Raw -LiteralPath $lockPath | ConvertFrom-Json
if ((Get-FileSha256 -Path $wheelPath) -ne [string]$lock.windowsX64WheelSha256) {
    throw 'The supplied FiveFury wheel digest does not match the checked-in lock.'
}

$runRoots = @(
    (Join-Path $outputPath 'replay-1'),
    (Join-Path $outputPath 'replay-2')
)
$toolRoot = Split-Path -Parent (Split-Path -Parent $uvPath)
$pinnedOfflineCache = Join-Path $toolRoot 'uv-cache'
if (Test-Path -LiteralPath $pinnedOfflineCache -PathType Container) {
    $env:UV_CACHE_DIR = $pinnedOfflineCache
}
else {
    $env:UV_CACHE_DIR = Join-Path $outputPath '.uv-cache'
}
foreach ($runRoot in $runRoots) {
    [void](New-Item -ItemType Directory -Path $runRoot)
    $acquisitionOutput = Join-Path $runRoot 'acquisition'
    Invoke-CheckedProcess -FilePath $uvPath -WorkingDirectory $repositoryRoot -Arguments @(
        'run', '--offline', '--python', '3.11', '--with', $wheelPath,
        'python', $acquisitionScript, 'acquire',
        '--game-root', $gameRootPath,
        '--fivefury-wheel', $wheelPath,
        '--output', $acquisitionOutput,
        '--observed-at-utc', $ObservedAtUtc
    )
    Invoke-CheckedProcess -FilePath $uvPath -WorkingDirectory $repositoryRoot -Arguments @(
        'run', '--offline', '--python', '3.11', '--with', $wheelPath,
        'python', $acquisitionScript, 'verify',
        '--game-root', $gameRootPath,
        '--fivefury-wheel', $wheelPath,
        '--receipt', (Join-Path $acquisitionOutput 'gta-v-enhanced-acquisition-receipt.v2.json'),
        '--observed-at-utc', $ObservedAtUtc
    )
    $actorAcquisitionOutput = Join-Path $runRoot 'actor-acquisition'
    Invoke-CheckedProcess -FilePath $uvPath -WorkingDirectory $repositoryRoot -Arguments @(
        'run', '--offline', '--python', '3.11', '--with', $wheelPath,
        'python', $actorAcquisitionScript, 'acquire',
        '--game-root', $gameRootPath,
        '--fivefury-wheel', $wheelPath,
        '--output', $actorAcquisitionOutput,
        '--observed-at-utc', $ActorObservedAtUtc
    )
    Invoke-CheckedProcess -FilePath $uvPath -WorkingDirectory $repositoryRoot -Arguments @(
        'run', '--offline', '--python', '3.11', '--with', $wheelPath,
        'python', $actorAcquisitionScript, 'verify',
        '--game-root', $gameRootPath,
        '--fivefury-wheel', $wheelPath,
        '--receipt', (Join-Path $actorAcquisitionOutput 'gta-v-enhanced-actor-acquisition-receipt.v1.json')
    )
    $spatialAcquisitionOutput = Join-Path $runRoot 'spatial-acquisition'
    Invoke-CheckedProcess -FilePath $uvPath -WorkingDirectory $repositoryRoot -Arguments @(
        'run', '--offline', '--python', '3.11', '--with', $wheelPath,
        'python', $spatialAcquisitionScript, 'acquire',
        '--game-root', $gameRootPath,
        '--fivefury-wheel', $wheelPath,
        '--output', $spatialAcquisitionOutput,
        '--observed-at-utc', $SpatialObservedAtUtc
    )
    Invoke-CheckedProcess -FilePath $uvPath -WorkingDirectory $repositoryRoot -Arguments @(
        'run', '--offline', '--python', '3.11', '--with', $wheelPath,
        'python', $spatialAcquisitionScript, 'verify',
        '--game-root', $gameRootPath,
        '--fivefury-wheel', $wheelPath,
        '--receipt', (Join-Path $spatialAcquisitionOutput 'gta-v-enhanced-spatial-acquisition-receipt.v1.json')
    )
}

Invoke-CheckedProcess -FilePath 'dotnet' -WorkingDirectory $repositoryRoot -Arguments @(
    'build', $canaryProject, '-c', 'Debug', '-p:Platform=x64'
)

foreach ($runRoot in $runRoots) {
    $acquisitionOutput = Join-Path $runRoot 'acquisition'
    $receipt = Join-Path $acquisitionOutput 'gta-v-enhanced-acquisition-receipt.v2.json'
    $canaryOutput = Join-Path $runRoot 'registration'
    $canaryArguments = @(
        'run', '--project', $canaryProject, '-c', 'Debug', '-p:Platform=x64', '--no-build', '--',
        '--game-root', $gameRootPath,
        '--acquisition-receipt', $receipt,
        '--fivefury-wheel', $wheelPath,
        '--uv-executable', $uvPath,
        '--historical-library', $historicalPath,
        '--actor-acquisition-receipt', (Join-Path $runRoot 'actor-acquisition\gta-v-enhanced-actor-acquisition-receipt.v1.json'),
        '--actor-corpus-index', (Join-Path $runRoot 'actor-acquisition\actor-corpus-index.v1.json'),
        '--spatial-acquisition-receipt', (Join-Path $runRoot 'spatial-acquisition\gta-v-enhanced-spatial-acquisition-receipt.v1.json'),
        '--spatial-corpus-index', (Join-Path $runRoot 'spatial-acquisition\spatial-corpus-index.v1.json'),
        '--output', $canaryOutput
    )
    if ($cloudBundlePath -ne $null) {
        $canaryArguments += @('--rockstar-cloud-snapshot-bundle', $cloudBundlePath)
    }
    Invoke-CheckedProcess -FilePath 'dotnet' -WorkingDirectory $repositoryRoot -Arguments $canaryArguments
}

$acquisition1 = Get-TreeManifest -Root (Join-Path $runRoots[0] 'acquisition')
$acquisition2 = Get-TreeManifest -Root (Join-Path $runRoots[1] 'acquisition')
Assert-EquivalentManifest -Left $acquisition1 -Right $acquisition2 -Name 'Acquisition'
$actorAcquisition1 = Get-TreeManifest -Root (Join-Path $runRoots[0] 'actor-acquisition')
$actorAcquisition2 = Get-TreeManifest -Root (Join-Path $runRoots[1] 'actor-acquisition')
Assert-EquivalentManifest -Left $actorAcquisition1 -Right $actorAcquisition2 -Name 'Actor acquisition'
$spatialAcquisition1 = Get-TreeManifest -Root (Join-Path $runRoots[0] 'spatial-acquisition')
$spatialAcquisition2 = Get-TreeManifest -Root (Join-Path $runRoots[1] 'spatial-acquisition')
Assert-EquivalentManifest -Left $spatialAcquisition1 -Right $spatialAcquisition2 -Name 'Spatial acquisition'

$requiredOutputs = @(
    'canary-package.v7.json',
    'canary-report.v3.json',
    'registration-coverage.v1.json',
    'registration-source-index.v1.json',
    'shared-canonical-library.v5.json'
)
if ($cloudBundlePath -ne $null) {
    $requiredOutputs += 'registration-reference-index.v1.json'
}
$registration1 = Get-TreeManifest -Root (Join-Path $runRoots[0] 'registration')
$registration2 = Get-TreeManifest -Root (Join-Path $runRoots[1] 'registration')
Assert-ExactManifestPaths -Manifest $registration1 -ExpectedPaths $requiredOutputs -Name 'Registration replay 1'
Assert-ExactManifestPaths -Manifest $registration2 -ExpectedPaths $requiredOutputs -Name 'Registration replay 2'
Assert-EquivalentManifest -Left $registration1 -Right $registration2 -Name 'Registration'

$comparisons = @()
foreach ($name in $requiredOutputs) {
    $leftPath = Join-Path (Join-Path $runRoots[0] 'registration') $name
    $rightPath = Join-Path (Join-Path $runRoots[1] 'registration') $name
    [void](Resolve-RequiredFile -Path $leftPath -Name $name)
    [void](Resolve-RequiredFile -Path $rightPath -Name $name)
    $leftHash = Get-FileSha256 -Path $leftPath
    $rightHash = Get-FileSha256 -Path $rightPath
    if (-not [string]::Equals($leftHash, $rightHash, [StringComparison]::Ordinal)) {
        throw "$name is not byte-identical across deterministic replays."
    }
    $comparisons += [pscustomobject][ordered]@{
        artifact = $name
        sha256 = $leftHash
        byteLength = (Get-Item -LiteralPath $leftPath).Length
    }
}

$report1 = Get-Content -Raw -LiteralPath (Join-Path (Join-Path $runRoots[0] 'registration') 'canary-report.v3.json') | ConvertFrom-Json
$report2 = Get-Content -Raw -LiteralPath (Join-Path (Join-Path $runRoots[1] 'registration') 'canary-report.v3.json') | ConvertFrom-Json
foreach ($property in @('packageId', 'catalogRevisionId', 'payloadDigest', 'sharedLibraryRevision')) {
    if (-not [string]::Equals([string]$report1.$property, [string]$report2.$property, [StringComparison]::Ordinal)) {
        throw "Registration identity $property differs across deterministic replays."
    }
}
if (-not $report1.structurallyValid -or -not $report2.structurallyValid -or
    -not [string]::Equals([string]$report1.validationStatus, 'Candidate', [StringComparison]::Ordinal)) {
    throw 'A replay did not produce a structurally valid Candidate package.'
}
if ([int]$report1.counts.actors -ne 1120 -or
    [int]$report1.counts.actorNpcClassifications -ne 1117 -or
    [int]$report1.counts.actorPlayerCharacterClassifications -ne 3 -or
    [int]$report1.counts.actorDlcOrganizationalValues -ne 451 -or
    [int]$report1.counts.correlations -ne 2) {
    throw 'The mounted Actor registration did not satisfy the exact reviewed coverage closure.'
}
if ([int]$report1.counts.locations -ne 2353 -or
    [int]$report1.counts.locationNativeTypeAssertions -ne 2353 -or
    [int]$report1.counts.locationSemanticClassifications -ne 3200 -or
    [int]$report1.counts.locationCoverageReports -ne 1 -or
    [int]$report1.counts.locationCoverageSourceFamilies -ne 5 -or
    [int]$report1.counts.locationContainedByRelationships -ne 1116 -or
    [int]$report1.counts.locationInstanceOfRelationships -ne 847 -or
    [int]$report1.counts.locationConnectsToRelationships -ne 1821 -or
    [int]$report1.counts.locationResolvedInstanceOfRelationships -ne 418 -or
    [int]$report1.counts.locationUnresolvedInstanceOfRelationships -ne 429) {
    throw 'The mounted spatial Location registration did not satisfy the exact reviewed coverage closure.'
}

$qcsProperties = [ordered]@{
    schemaVersion = 1
    status = 'PASS'
    gameId = [string]$report1.gameId
    exactGameVersion = [string]$report1.gameBuild
    observedAtUtc = $ObservedAtUtc
    actorObservedAtUtc = $ActorObservedAtUtc
    spatialObservedAtUtc = $SpatialObservedAtUtc
    packageId = [string]$report1.packageId
    catalogRevisionId = [string]$report1.catalogRevisionId
    payloadDigest = [string]$report1.payloadDigest
    sharedLibraryRevision = [long]$report1.sharedLibraryRevision
    structurallyValid = [bool]$report1.structurallyValid
    validationStatus = [string]$report1.validationStatus
    acquisitionFiles = @($acquisition1)
    actorAcquisitionFiles = @($actorAcquisition1)
    spatialAcquisitionFiles = @($spatialAcquisition1)
    actorCoverage = [pscustomobject][ordered]@{
        records = [int]$report1.counts.actors
        npcClassifications = [int]$report1.counts.actorNpcClassifications
        playerCharacterClassifications = [int]$report1.counts.actorPlayerCharacterClassifications
        dlcOrganizationalValues = [int]$report1.counts.actorDlcOrganizationalValues
        gen9ResidentCorrelations = [int]$report1.counts.correlations
    }
    spatialCoverage = [pscustomobject][ordered]@{
        records = [int]$report1.counts.locations
        nativeTypeAssertions = [int]$report1.counts.locationNativeTypeAssertions
        semanticClassifications = [int]$report1.counts.locationSemanticClassifications
        sourceFamilies = [int]$report1.counts.locationCoverageSourceFamilies
        containedBy = [int]$report1.counts.locationContainedByRelationships
        instanceOf = [int]$report1.counts.locationInstanceOfRelationships
        resolvedInstanceOf = [int]$report1.counts.locationResolvedInstanceOfRelationships
        unresolvedInstanceOf = [int]$report1.counts.locationUnresolvedInstanceOfRelationships
        connectsTo = [int]$report1.counts.locationConnectsToRelationships
    }
}
if ($cloudBundlePath -ne $null) {
    $qcsProperties.referenceSnapshotBundle = [pscustomobject][ordered]@{
        contentSha256 = $cloudBundleDigest
        files = @($cloudBundleManifest)
    }
}
$qcsProperties.deterministicOutputs = @($comparisons)
$qcs = [pscustomobject]$qcsProperties
$qcsJson = ConvertTo-Json -InputObject $qcs -Depth 8
Write-Utf8NoBomNewFile -Path (Join-Path $outputPath 'registration-qcs.v1.json') -Content $qcsJson

Write-Host "RegistrationStatus=PASS"
Write-Host "PackageId=$($report1.packageId)"
Write-Host "CatalogRevisionId=$($report1.catalogRevisionId)"
Write-Host "PayloadDigest=$($report1.payloadDigest)"
Write-Host "SharedLibraryRevision=$($report1.sharedLibraryRevision)"
Write-Host "QcsReport=$(Join-Path $outputPath 'registration-qcs.v1.json')"
