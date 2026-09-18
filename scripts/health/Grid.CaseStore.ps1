Set-StrictMode -Version Latest

$script:GridCaseStoreVersion = 'grid.case-store.v1'
$script:GridUtf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Get-GridCaseStoreSha256 {
    param([Parameter(Mandatory)][string]$LiteralPath)
    (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256 -ErrorAction Stop).Hash.ToUpperInvariant()
}

function Get-GridCaseStoreByteSha256 {
    param([Parameter(Mandatory)][byte[]]$Bytes)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        ([BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace('-', '')
    }
    finally {
        $algorithm.Dispose()
    }
}

function ConvertTo-GridCaseStoreJson {
    param([Parameter(Mandatory)][object]$Value)
    $Value | ConvertTo-Json -Depth 100 -Compress
}

function Write-GridCaseStoreTextAtomic {
    param(
        [Parameter(Mandatory)][string]$LiteralPath,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text
    )
    $directory = Split-Path -Parent $LiteralPath
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force -ErrorAction Stop | Out-Null
    }
    $temporaryPath = Join-Path $directory ('.grid-write-{0}.tmp' -f ([Guid]::NewGuid().ToString('N')))
    try {
        [IO.File]::WriteAllText($temporaryPath, $Text, $script:GridUtf8NoBom)
        Move-Item -LiteralPath $temporaryPath -Destination $LiteralPath -Force -ErrorAction Stop
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Assert-GridCaseStoreSegment {
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$Name
    )
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -in @('.', '..') -or
        $Value.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        $Value.Contains([IO.Path]::DirectorySeparatorChar) -or
        $Value.Contains([IO.Path]::AltDirectorySeparatorChar)) {
        throw "$Name is not a safe path segment."
    }
}

function Resolve-GridCaseStoreRelativePath {
    param(
        [Parameter(Mandatory)][string]$BaseDirectory,
        [Parameter(Mandatory)][string]$RelativePath
    )
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) {
        throw 'Artifact path must be a non-empty relative path.'
    }
    $normalized = $RelativePath.Replace('\', '/')
    $parts = @($normalized -split '/')
    if ($parts.Count -eq 0 -or $parts -contains '' -or $parts -contains '.' -or $parts -contains '..') {
        throw "Artifact path '$RelativePath' is not safe."
    }
    foreach ($part in $parts) {
        if ($part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
            throw "Artifact path '$RelativePath' contains an invalid segment."
        }
    }
    $baseFull = [IO.Path]::GetFullPath($BaseDirectory).TrimEnd('\', '/')
    $candidate = [IO.Path]::GetFullPath((Join-Path $baseFull ($parts -join [IO.Path]::DirectorySeparatorChar)))
    $prefix = $baseFull + [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Artifact path '$RelativePath' escapes the case directory."
    }
    [pscustomobject]@{ FullPath = $candidate; RelativePath = ($parts -join '/') }
}

function Assert-GridCaseStoreTransaction {
    param([Parameter(Mandatory)][psobject]$Transaction)
    foreach ($name in @('StoreRoot', 'CaseId', 'TransactionDirectory', 'CaseDirectory', 'State')) {
        if ($null -eq $Transaction.PSObject.Properties[$name]) {
            throw "Transaction is missing '$name'."
        }
    }
    if ($Transaction.State -ne 'Open') { throw 'Transaction is not open.' }
    if (-not (Test-Path -LiteralPath $Transaction.CaseDirectory -PathType Container)) {
        throw 'Transaction case directory no longer exists.'
    }
}

function Get-GridDiagnosticStoreRoot {
    [CmdletBinding()]
    param(
        [string]$Root,
        [switch]$Ensure
    )
    if ([string]::IsNullOrWhiteSpace($Root)) {
        if (-not [string]::IsNullOrWhiteSpace($env:GRID_DATA_ROOT)) {
            $Root = $env:GRID_DATA_ROOT
        }
        elseif (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
            $Root = Join-Path $env:LOCALAPPDATA 'Grid'
        }
        else {
            throw 'Neither GRID_DATA_ROOT nor LOCALAPPDATA is available; provide an explicit diagnostic store root.'
        }
    }
    $resolved = [IO.Path]::GetFullPath($Root)
    if ($Ensure -and -not (Test-Path -LiteralPath $resolved -PathType Container)) {
        New-Item -ItemType Directory -Path $resolved -Force -ErrorAction Stop | Out-Null
    }
    $resolved
}

function New-GridCaseStoreTransaction {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StoreRoot,
        [Parameter(Mandatory)][string]$CaseId
    )
    Assert-GridCaseStoreSegment -Value $CaseId -Name 'CaseId'
    $root = Get-GridDiagnosticStoreRoot -Root $StoreRoot -Ensure
    foreach ($name in @('cases\v1', 'transactions', 'blobs\sha256')) {
        $directory = Join-Path $root $name
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
            New-Item -ItemType Directory -Path $directory -Force -ErrorAction Stop | Out-Null
        }
    }
    $target = Join-Path (Join-Path $root 'cases\v1') $CaseId
    if (Test-Path -LiteralPath $target) { throw "Case '$CaseId' already exists." }
    $transactionId = [Guid]::NewGuid().ToString('N')
    $transactionDirectory = Join-Path (Join-Path $root 'transactions') $transactionId
    $caseDirectory = Join-Path $transactionDirectory 'case'
    New-Item -ItemType Directory -Path $caseDirectory -Force -ErrorAction Stop | Out-Null
    [pscustomobject][ordered]@{
        TransactionId = $transactionId
        StoreRoot = $root
        CaseId = $CaseId
        TransactionDirectory = $transactionDirectory
        CaseDirectory = $caseDirectory
        State = 'Open'
        CreatedAt = [DateTimeOffset]::UtcNow.ToString('o')
        Artifacts = @()
        Blobs = @()
    }
}

function Add-GridCaseStoreBlob {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][psobject]$Transaction,
        [Parameter(Mandatory)][string]$LiteralPath
    )
    Assert-GridCaseStoreTransaction -Transaction $Transaction
    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) { throw "Blob source '$LiteralPath' is not a file." }
    $item = Get-Item -LiteralPath $LiteralPath -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse-point blob sources are refused.' }
    $hash = Get-GridCaseStoreSha256 -LiteralPath $item.FullName
    $prefixDirectory = Join-Path (Join-Path $Transaction.StoreRoot 'blobs\sha256') $hash.Substring(0, 2)
    $destination = Join-Path $prefixDirectory $hash
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        if ((Get-GridCaseStoreSha256 -LiteralPath $destination) -ne $hash) { throw "Blob collision detected for '$hash'." }
    }
    else {
        if (-not (Test-Path -LiteralPath $prefixDirectory -PathType Container)) {
            New-Item -ItemType Directory -Path $prefixDirectory -Force -ErrorAction Stop | Out-Null
        }
        $temporary = Join-Path $prefixDirectory ('.grid-blob-{0}.tmp' -f ([Guid]::NewGuid().ToString('N')))
        try {
            Copy-Item -LiteralPath $item.FullName -Destination $temporary -ErrorAction Stop
            if ((Get-GridCaseStoreSha256 -LiteralPath $temporary) -ne $hash) { throw 'Blob changed while it was copied.' }
            Move-Item -LiteralPath $temporary -Destination $destination -ErrorAction Stop
        }
        finally {
            if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
        }
    }
    $record = [pscustomobject][ordered]@{ sha256 = $hash; sizeBytes = [long]$item.Length }
    if (@($Transaction.Blobs | Where-Object { $_.sha256 -eq $hash }).Count -eq 0) {
        $Transaction.Blobs = @($Transaction.Blobs) + @($record)
    }
    $record
}

function Write-GridCaseStoreArtifact {
    [CmdletBinding(DefaultParameterSetName = 'Json')]
    param(
        [Parameter(Mandatory)][psobject]$Transaction,
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory, ParameterSetName = 'Json')][object]$Value,
        [Parameter(Mandatory, ParameterSetName = 'Text')][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory, ParameterSetName = 'File')][string]$SourceLiteralPath
    )
    Assert-GridCaseStoreTransaction -Transaction $Transaction
    $resolved = Resolve-GridCaseStoreRelativePath -BaseDirectory $Transaction.CaseDirectory -RelativePath $RelativePath
    if ($resolved.RelativePath -ieq 'case-manifest.v1.json') { throw 'case-manifest.v1.json is reserved for case sealing.' }
    $parent = Split-Path -Parent $resolved.FullPath
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Path $parent -Force -ErrorAction Stop | Out-Null }
    switch ($PSCmdlet.ParameterSetName) {
        'Json' { Write-GridCaseStoreTextAtomic -LiteralPath $resolved.FullPath -Text (ConvertTo-GridCaseStoreJson -Value $Value) }
        'Text' { Write-GridCaseStoreTextAtomic -LiteralPath $resolved.FullPath -Text $Text }
        'File' {
            if (-not (Test-Path -LiteralPath $SourceLiteralPath -PathType Leaf)) { throw "Artifact source '$SourceLiteralPath' is not a file." }
            $temporary = Join-Path $parent ('.grid-artifact-{0}.tmp' -f ([Guid]::NewGuid().ToString('N')))
            try {
                Copy-Item -LiteralPath $SourceLiteralPath -Destination $temporary -ErrorAction Stop
                Move-Item -LiteralPath $temporary -Destination $resolved.FullPath -Force -ErrorAction Stop
            }
            finally {
                if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
            }
        }
    }
    $item = Get-Item -LiteralPath $resolved.FullPath -ErrorAction Stop
    $record = [pscustomobject][ordered]@{
        path = $resolved.RelativePath
        sizeBytes = [long]$item.Length
        sha256 = Get-GridCaseStoreSha256 -LiteralPath $item.FullName
    }
    $Transaction.Artifacts = @($Transaction.Artifacts | Where-Object { $_.path -ine $record.path }) + @($record)
    $record
}

function New-GridCaseStoreCheckpoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][psobject]$Transaction,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)][ValidateRange(0, [int]::MaxValue)][int]$Sequence,
        [Parameter(Mandatory)][psobject]$ResourceUsage,
        [Parameter(Mandatory)][object[]]$Gates
    )
    Assert-GridCaseStoreSegment -Value $RunId -Name 'RunId'
    $artifactFingerprintSource = (@($Transaction.Artifacts | Sort-Object path | ForEach-Object { '{0}|{1}|{2}' -f $_.path, $_.sizeBytes, $_.sha256 }) -join "`n")
    $fingerprint = Get-GridCaseStoreByteSha256 -Bytes $script:GridUtf8NoBom.GetBytes($artifactFingerprintSource)
    $checkpoint = [pscustomobject][ordered]@{
        schemaVersion = 1
        checkpointId = '{0}-{1:d8}' -f $RunId, $Sequence
        runId = $RunId
        caseId = $Transaction.CaseId
        sequence = $Sequence
        state = 'Sealed'
        createdAt = [DateTimeOffset]::UtcNow.ToString('o')
        artifactCount = @($Transaction.Artifacts).Count
        artifactFingerprint = $fingerprint
        resourceUsage = $ResourceUsage
        gates = @($Gates)
    }
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath ('runs/{0}/checkpoints/{1:d8}.json' -f $RunId, $Sequence) -Value $checkpoint | Out-Null
    $checkpoint
}

function Seal-GridCaseStoreRun {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][psobject]$Transaction,
        [Parameter(Mandatory)][psobject]$Run
    )
    $required = @('schemaVersion', 'runId', 'caseId', 'state', 'startedAt', 'completedAt', 'planFingerprint', 'resourcePolicyVersion', 'gates', 'sufficiency', 'checkpoints', 'primaryFailure', 'secondaryFailures')
    foreach ($name in $required) {
        if ($null -eq $Run.PSObject.Properties[$name]) { throw "Run is missing required property '$name'." }
    }
    if ($Run.caseId -ne $Transaction.CaseId) { throw 'Run caseId does not match the transaction caseId.' }
    Assert-GridCaseStoreSegment -Value ([string]$Run.runId) -Name 'RunId'
    if ([string]$Run.state -notin @('Planned', 'Running', 'PausedAtCheckpoint', 'Completed', 'Failed', 'Cancelled')) { throw "Unsupported run state '$($Run.state)'." }
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath ('runs/{0}/collection-run.v1.json' -f $Run.runId) -Value $Run | Out-Null
    $Run
}

function Get-GridCaseManifestLegacyHash {
    param([Parameter(Mandatory)][psobject]$Manifest)
    $unsigned = [pscustomobject][ordered]@{
        schemaVersion = $Manifest.schemaVersion
        caseId = $Manifest.caseId
        storeVersion = $Manifest.storeVersion
        sealedAt = $Manifest.sealedAt
        semanticBaselineFingerprint = $Manifest.semanticBaselineFingerprint
        artifacts = @($Manifest.artifacts)
        blobs = @($Manifest.blobs)
        runs = @($Manifest.runs)
    }
    Get-GridCaseStoreByteSha256 -Bytes $script:GridUtf8NoBom.GetBytes((ConvertTo-GridCaseStoreJson -Value $unsigned))
}

function ConvertTo-GridCaseManifestPortableText {
    param([Parameter(Mandatory)][psobject]$Manifest)
    $lines = [Collections.Generic.List[string]]::new()
    function Add-GridManifestHashField([string]$Name, $Value) {
        $text = if ($Value -is [DateTimeOffset]) { $Value.ToString('o', [Globalization.CultureInfo]::InvariantCulture) }
            elseif ($Value -is [DateTime]) { $Value.ToString('o', [Globalization.CultureInfo]::InvariantCulture) }
            elseif ($null -eq $Value) { $null }
            else { [string]$Value }
        $encoded = if ($null -eq $text) { '-' } else {
            's' + [Convert]::ToBase64String($script:GridUtf8NoBom.GetBytes($text))
        }
        [void]$lines.Add($Name + '|' + $encoded)
    }
    Add-GridManifestHashField 'manifestHashAlgorithm' ([string]$Manifest.manifestHashAlgorithm)
    Add-GridManifestHashField 'schemaVersion' ([int]$Manifest.schemaVersion)
    Add-GridManifestHashField 'caseId' ([string]$Manifest.caseId)
    Add-GridManifestHashField 'storeVersion' ([string]$Manifest.storeVersion)
    $sealedAt = $Manifest.sealedAt
    $sealedUtcTicks = if ($sealedAt -is [DateTimeOffset]) { $sealedAt.UtcDateTime.Ticks }
        elseif ($sealedAt -is [DateTime]) { $sealedAt.ToUniversalTime().Ticks }
        else { [DateTimeOffset]::Parse([string]$sealedAt, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).UtcDateTime.Ticks }
    Add-GridManifestHashField 'sealedAtUtcTicks' ([long]$sealedUtcTicks)
    Add-GridManifestHashField 'semanticBaselineFingerprint' ([string]$Manifest.semanticBaselineFingerprint)
    $artifacts = @($Manifest.artifacts)
    Add-GridManifestHashField 'artifactCount' $artifacts.Count
    foreach ($artifact in $artifacts) {
        Add-GridManifestHashField 'artifact.path' ([string]$artifact.path)
        Add-GridManifestHashField 'artifact.sizeBytes' ([long]$artifact.sizeBytes)
        Add-GridManifestHashField 'artifact.sha256' ([string]$artifact.sha256)
    }
    $blobs = @($Manifest.blobs)
    Add-GridManifestHashField 'blobCount' $blobs.Count
    foreach ($blob in $blobs) {
        Add-GridManifestHashField 'blob.sha256' ([string]$blob.sha256)
        Add-GridManifestHashField 'blob.sizeBytes' ([long]$blob.sizeBytes)
    }
    $runs = @($Manifest.runs)
    Add-GridManifestHashField 'runCount' $runs.Count
    foreach ($run in $runs) {
        Add-GridManifestHashField 'run.runId' ([string]$run.runId)
        Add-GridManifestHashField 'run.state' ([string]$run.state)
        Add-GridManifestHashField 'run.path' ([string]$run.path)
        Add-GridManifestHashField 'run.sha256' ([string]$run.sha256)
    }
    [string]::Join("`n", $lines)
}

function Get-GridCaseManifestPortableHash {
    param([Parameter(Mandatory)][psobject]$Manifest)
    Get-GridCaseStoreByteSha256 -Bytes $script:GridUtf8NoBom.GetBytes((ConvertTo-GridCaseManifestPortableText -Manifest $Manifest))
}

function Get-GridCaseManifestHash {
    param([Parameter(Mandatory)][psobject]$Manifest)
    if ($Manifest.PSObject.Properties['manifestHashAlgorithm']) {
        if ([string]$Manifest.manifestHashAlgorithm -cne 'grid.case-manifest.portable-v1') {
            throw "Unsupported manifestHashAlgorithm '$($Manifest.manifestHashAlgorithm)'."
        }
        return Get-GridCaseManifestPortableHash -Manifest $Manifest
    }
    Get-GridCaseManifestLegacyHash -Manifest $Manifest
}

function Seal-GridDiagnosticCase {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][psobject]$Transaction,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$SemanticBaselineFingerprint,
        [Parameter(Mandatory)][object[]]$Runs
    )
    Assert-GridCaseStoreTransaction -Transaction $Transaction
    $artifacts = @()
    Get-ChildItem -LiteralPath $Transaction.CaseDirectory -File -Recurse -Force | ForEach-Object {
        $relative = $_.FullName.Substring($Transaction.CaseDirectory.TrimEnd('\').Length + 1).Replace('\', '/')
        if ($relative -ine 'case-manifest.v1.json') {
            $artifacts += [pscustomobject][ordered]@{ path = $relative; sizeBytes = [long]$_.Length; sha256 = Get-GridCaseStoreSha256 -LiteralPath $_.FullName }
        }
    }
    $artifacts = @($artifacts | Sort-Object path)
    $blobs = @($Transaction.Blobs | Sort-Object sha256 -Unique)
    $runRefs = @()
    foreach ($run in @($Runs | Sort-Object runId)) {
        $relative = 'runs/{0}/collection-run.v1.json' -f $run.runId
        $artifact = @($artifacts | Where-Object { $_.path -eq $relative })
        if ($artifact.Count -ne 1) { throw "Sealed run artifact '$relative' is missing." }
        $runRefs += [pscustomobject][ordered]@{ runId = [string]$run.runId; state = [string]$run.state; path = $relative; sha256 = $artifact[0].sha256 }
    }
    $manifest = [pscustomobject][ordered]@{
        schemaVersion = 1
        manifestHashAlgorithm = 'grid.case-manifest.portable-v1'
        caseId = $Transaction.CaseId
        storeVersion = $script:GridCaseStoreVersion
        sealedAt = [DateTimeOffset]::UtcNow.ToString('o')
        semanticBaselineFingerprint = $SemanticBaselineFingerprint.ToUpperInvariant()
        artifacts = $artifacts
        blobs = $blobs
        runs = $runRefs
        manifestSha256 = ''
    }
    $manifest.manifestSha256 = Get-GridCaseManifestHash -Manifest $manifest
    Write-GridCaseStoreTextAtomic -LiteralPath (Join-Path $Transaction.CaseDirectory 'case-manifest.v1.json') -Text (ConvertTo-GridCaseStoreJson -Value $manifest)
    $target = Join-Path (Join-Path $Transaction.StoreRoot 'cases\v1') $Transaction.CaseId
    if (Test-Path -LiteralPath $target) { throw "Case '$($Transaction.CaseId)' already exists." }
    Move-Item -LiteralPath $Transaction.CaseDirectory -Destination $target -ErrorAction Stop
    $Transaction.State = 'Sealed'
    if (Test-Path -LiteralPath $Transaction.TransactionDirectory -PathType Container) {
        Remove-Item -LiteralPath $Transaction.TransactionDirectory -Recurse -Force -ErrorAction Stop
    }
    [pscustomobject][ordered]@{ CaseDirectory = $target; Manifest = $manifest }
}

function Test-GridDiagnosticCaseSeal {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StoreRoot,
        [Parameter(Mandatory)][string]$CaseDirectory
    )
    $errors = @()
    $manifest = $null
    try {
        $root = Get-GridDiagnosticStoreRoot -Root $StoreRoot
        $caseFull = [IO.Path]::GetFullPath($CaseDirectory)
        $manifestPath = Join-Path $caseFull 'case-manifest.v1.json'
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'case-manifest.v1.json is missing.' }
        $manifest = Get-Content -Raw -LiteralPath $manifestPath -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        if ($manifest.storeVersion -ne $script:GridCaseStoreVersion) { $errors += 'Unsupported storeVersion.' }
        if ([string]$manifest.caseId -ne (Split-Path -Leaf $caseFull)) { $errors += 'Manifest caseId does not match the case directory.' }
        if ([string]$manifest.semanticBaselineFingerprint -notmatch '^[A-F0-9]{64}$') { $errors += 'Semantic baseline fingerprint is invalid.' }
        if ([string]$manifest.manifestSha256 -notmatch '^[A-F0-9]{64}$') { $errors += 'Manifest fingerprint is invalid.' }
        if ((Get-GridCaseManifestHash -Manifest $manifest) -ne $manifest.manifestSha256) { $errors += 'Manifest fingerprint does not match.' }
        $declared = @{}
        foreach ($artifact in @($manifest.artifacts)) {
            try {
                $resolved = Resolve-GridCaseStoreRelativePath -BaseDirectory $caseFull -RelativePath ([string]$artifact.path)
                if ($declared.ContainsKey($resolved.RelativePath.ToLowerInvariant())) { $errors += "Duplicate artifact '$($artifact.path)'."; continue }
                $declared[$resolved.RelativePath.ToLowerInvariant()] = $true
                if (-not (Test-Path -LiteralPath $resolved.FullPath -PathType Leaf)) { $errors += "Artifact '$($artifact.path)' is missing."; continue }
                $item = Get-Item -LiteralPath $resolved.FullPath -ErrorAction Stop
                if ([long]$item.Length -ne [long]$artifact.sizeBytes) { $errors += "Artifact '$($artifact.path)' size does not match." }
                if ((Get-GridCaseStoreSha256 -LiteralPath $resolved.FullPath) -ne $artifact.sha256) { $errors += "Artifact '$($artifact.path)' hash does not match." }
            }
            catch { $errors += "Artifact '$($artifact.path)' is invalid: $($_.Exception.Message)" }
        }
        Get-ChildItem -LiteralPath $caseFull -File -Recurse -Force | ForEach-Object {
            $relative = $_.FullName.Substring($caseFull.TrimEnd('\').Length + 1).Replace('\', '/')
            if ($relative -ine 'case-manifest.v1.json' -and -not $declared.ContainsKey($relative.ToLowerInvariant())) { $errors += "Undeclared artifact '$relative'." }
        }
        $declaredBlobs = @{}
        foreach ($blob in @($manifest.blobs)) {
            if ([string]$blob.sha256 -notmatch '^[A-F0-9]{64}$') { $errors += "Blob hash '$($blob.sha256)' is invalid."; continue }
            if ($declaredBlobs.ContainsKey([string]$blob.sha256)) { $errors += "Duplicate blob '$($blob.sha256)'."; continue }
            $declaredBlobs[[string]$blob.sha256] = $true
            $blobPath = Join-Path (Join-Path (Join-Path $root 'blobs\sha256') $blob.sha256.Substring(0, 2)) $blob.sha256
            if (-not (Test-Path -LiteralPath $blobPath -PathType Leaf)) { $errors += "Blob '$($blob.sha256)' is missing."; continue }
            $item = Get-Item -LiteralPath $blobPath
            if ([long]$item.Length -ne [long]$blob.sizeBytes) { $errors += "Blob '$($blob.sha256)' size does not match." }
            if ((Get-GridCaseStoreSha256 -LiteralPath $blobPath) -ne $blob.sha256) { $errors += "Blob '$($blob.sha256)' hash does not match." }
        }
        $declaredRuns = @{}
        foreach ($runReference in @($manifest.runs)) {
            $runId = [string]$runReference.runId
            if ($declaredRuns.ContainsKey($runId.ToLowerInvariant())) { $errors += "Duplicate run '$runId'."; continue }
            $declaredRuns[$runId.ToLowerInvariant()] = $true
            $runPath = [string]$runReference.path
            $artifactKey = $runPath.ToLowerInvariant()
            if (-not $declared.ContainsKey($artifactKey)) { $errors += "Run '$runId' references an undeclared artifact."; continue }
            $runArtifact = @($manifest.artifacts | Where-Object { ([string]$_.path).ToLowerInvariant() -eq $artifactKey })[0]
            if ([string]$runArtifact.sha256 -ne [string]$runReference.sha256) { $errors += "Run '$runId' reference hash does not match its artifact." }
            try {
                $runFullPath = (Resolve-GridCaseStoreRelativePath -BaseDirectory $caseFull -RelativePath $runPath).FullPath
                $run = Get-Content -Raw -LiteralPath $runFullPath -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
                if ([string]$run.runId -ne $runId -or [string]$run.caseId -ne [string]$manifest.caseId -or [string]$run.state -ne [string]$runReference.state) {
                    $errors += "Run '$runId' reference does not match the sealed run artifact."
                }
            }
            catch { $errors += "Run '$runId' artifact is invalid: $($_.Exception.Message)" }
        }
    }
    catch { $errors += $_.Exception.Message }
    [pscustomobject][ordered]@{ IsValid = ($errors.Count -eq 0); Errors = @($errors); Manifest = $manifest }
}

function Export-GridDiagnosticCase {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StoreRoot,
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][string]$DestinationLiteralPath
    )
    Assert-GridCaseStoreSegment -Value $CaseId -Name 'CaseId'
    if (Test-Path -LiteralPath $DestinationLiteralPath) { throw "Export destination '$DestinationLiteralPath' already exists." }
    $root = Get-GridDiagnosticStoreRoot -Root $StoreRoot
    $caseDirectory = Join-Path (Join-Path $root 'cases\v1') $CaseId
    $seal = Test-GridDiagnosticCaseSeal -StoreRoot $root -CaseDirectory $caseDirectory
    if (-not $seal.IsValid) { throw "Case seal validation failed: $($seal.Errors -join '; ')" }
    Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
    $files = @()
    Get-ChildItem -LiteralPath $caseDirectory -File -Recurse -Force | ForEach-Object {
        $relative = $_.FullName.Substring($caseDirectory.TrimEnd('\').Length + 1).Replace('\', '/')
        $files += [pscustomobject][ordered]@{ archivePath = "case/$CaseId/$relative"; sourcePath = $_.FullName; sizeBytes = [long]$_.Length; sha256 = Get-GridCaseStoreSha256 -LiteralPath $_.FullName }
    }
    foreach ($blob in @($seal.Manifest.blobs)) {
        $source = Join-Path (Join-Path (Join-Path $root 'blobs\sha256') $blob.sha256.Substring(0, 2)) $blob.sha256
        $files += [pscustomobject][ordered]@{ archivePath = "blobs/sha256/$($blob.sha256)"; sourcePath = $source; sizeBytes = [long]$blob.sizeBytes; sha256 = [string]$blob.sha256 }
    }
    $files = @($files | Sort-Object archivePath)
    $descriptor = [pscustomobject][ordered]@{
        schemaVersion = 1
        caseId = $CaseId
        storeVersion = $script:GridCaseStoreVersion
        manifestSha256 = [string]$seal.Manifest.manifestSha256
        files = @($files | ForEach-Object { [pscustomobject][ordered]@{ path = $_.archivePath; sizeBytes = $_.sizeBytes; sha256 = $_.sha256 } })
    }
    $parent = Split-Path -Parent ([IO.Path]::GetFullPath($DestinationLiteralPath))
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Path $parent -Force -ErrorAction Stop | Out-Null }
    $stream = [IO.File]::Open($DestinationLiteralPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            $descriptorEntry = $archive.CreateEntry('case-export.v1.json', [IO.Compression.CompressionLevel]::NoCompression)
            $descriptorEntry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $writer = New-Object IO.StreamWriter($descriptorEntry.Open(), $script:GridUtf8NoBom)
            try { $writer.Write((ConvertTo-GridCaseStoreJson -Value $descriptor)) } finally { $writer.Dispose() }
            foreach ($file in $files) {
                $entry = $archive.CreateEntry($file.archivePath, [IO.Compression.CompressionLevel]::NoCompression)
                $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $input = [IO.File]::OpenRead($file.sourcePath)
                $output = $entry.Open()
                try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
    Get-Item -LiteralPath $DestinationLiteralPath
}

function Import-GridDiagnosticCase {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StoreRoot,
        [Parameter(Mandatory)][string]$ArchiveLiteralPath
    )
    if (-not (Test-Path -LiteralPath $ArchiveLiteralPath -PathType Leaf)) { throw "Import archive '$ArchiveLiteralPath' is missing." }
    $root = Get-GridDiagnosticStoreRoot -Root $StoreRoot -Ensure
    Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
    $stream = [IO.File]::OpenRead((Resolve-Path -LiteralPath $ArchiveLiteralPath).Path)
    $stageRoot = Join-Path (Join-Path $root 'imports') ([Guid]::NewGuid().ToString('N'))
    try {
        $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Read, $false)
        try {
            $entries = @{}
            foreach ($entry in $archive.Entries) {
                if ([string]::IsNullOrWhiteSpace($entry.FullName) -or $entry.FullName.Contains('\') -or $entry.FullName.StartsWith('/') -or ($entry.FullName -split '/') -contains '..') { throw "Unsafe archive entry '$($entry.FullName)'." }
                $key = $entry.FullName.ToLowerInvariant()
                if ($entries.ContainsKey($key)) { throw "Duplicate archive entry '$($entry.FullName)'." }
                $entries[$key] = $entry
            }
            if (-not $entries.ContainsKey('case-export.v1.json')) { throw 'case-export.v1.json is missing.' }
            $reader = New-Object IO.StreamReader($entries['case-export.v1.json'].Open(), [Text.Encoding]::UTF8, $true)
            try { $descriptor = $reader.ReadToEnd() | ConvertFrom-Json -ErrorAction Stop } finally { $reader.Dispose() }
            Assert-GridCaseStoreSegment -Value ([string]$descriptor.caseId) -Name 'CaseId'
            if ($descriptor.schemaVersion -ne 1 -or $descriptor.storeVersion -ne $script:GridCaseStoreVersion) { throw 'Unsupported case export descriptor.' }
            $targetCase = Join-Path (Join-Path $root 'cases\v1') $descriptor.caseId
            if (Test-Path -LiteralPath $targetCase) { throw "Case '$($descriptor.caseId)' already exists." }
            $declared = @{}
            foreach ($file in @($descriptor.files)) {
                $key = ([string]$file.path).ToLowerInvariant()
                if ($declared.ContainsKey($key)) { throw "Duplicate descriptor path '$($file.path)'." }
                $declared[$key] = $true
                if (-not $entries.ContainsKey($key)) { throw "Archive entry '$($file.path)' is missing." }
                $entry = $entries[$key]
                if ([long]$entry.Length -ne [long]$file.sizeBytes) { throw "Archive entry '$($file.path)' has the wrong size." }
                $entryStream = $entry.Open()
                $memory = New-Object IO.MemoryStream
                try { $entryStream.CopyTo($memory); $bytes = $memory.ToArray() } finally { $memory.Dispose(); $entryStream.Dispose() }
                if ((Get-GridCaseStoreByteSha256 -Bytes $bytes) -ne $file.sha256) { throw "Archive entry '$($file.path)' has the wrong hash." }
            }
            foreach ($key in $entries.Keys) {
                if ($key -ne 'case-export.v1.json' -and -not $declared.ContainsKey($key)) { throw "Undeclared archive entry '$key'." }
            }
            New-Item -ItemType Directory -Path $stageRoot -Force -ErrorAction Stop | Out-Null
            foreach ($file in @($descriptor.files | Sort-Object path)) {
                $path = [string]$file.path
                if ($path.StartsWith("case/$($descriptor.caseId)/", [StringComparison]::Ordinal)) {
                    $relative = $path.Substring(("case/$($descriptor.caseId)/").Length)
                    $base = Join-Path (Join-Path $stageRoot 'cases\v1') $descriptor.caseId
                    $destination = (Resolve-GridCaseStoreRelativePath -BaseDirectory $base -RelativePath $relative).FullPath
                }
                elseif ($path -match '^blobs/sha256/([A-F0-9]{64})$') {
                    $hash = $Matches[1]
                    $destination = Join-Path (Join-Path (Join-Path $stageRoot 'blobs\sha256') $hash.Substring(0, 2)) $hash
                }
                else { throw "Unsupported archive path '$path'." }
                $destinationParent = Split-Path -Parent $destination
                if (-not (Test-Path -LiteralPath $destinationParent -PathType Container)) { New-Item -ItemType Directory -Path $destinationParent -Force -ErrorAction Stop | Out-Null }
                $input = $entries[$path.ToLowerInvariant()].Open()
                $output = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
            }
            $stageCase = Join-Path (Join-Path $stageRoot 'cases\v1') $descriptor.caseId
            $seal = Test-GridDiagnosticCaseSeal -StoreRoot $stageRoot -CaseDirectory $stageCase
            if (-not $seal.IsValid -or $seal.Manifest.manifestSha256 -ne $descriptor.manifestSha256) { throw "Imported case seal validation failed: $($seal.Errors -join '; ')" }
            foreach ($blob in @($seal.Manifest.blobs)) {
                $source = Join-Path (Join-Path (Join-Path $stageRoot 'blobs\sha256') $blob.sha256.Substring(0, 2)) $blob.sha256
                $destination = Join-Path (Join-Path (Join-Path $root 'blobs\sha256') $blob.sha256.Substring(0, 2)) $blob.sha256
                if (Test-Path -LiteralPath $destination -PathType Leaf) {
                    if ((Get-GridCaseStoreSha256 -LiteralPath $destination) -ne $blob.sha256) { throw "Blob collision detected for '$($blob.sha256)'." }
                }
                else {
                    $destinationParent = Split-Path -Parent $destination
                    if (-not (Test-Path -LiteralPath $destinationParent -PathType Container)) { New-Item -ItemType Directory -Path $destinationParent -Force -ErrorAction Stop | Out-Null }
                    Copy-Item -LiteralPath $source -Destination $destination -ErrorAction Stop
                }
            }
            $casesDirectory = Join-Path $root 'cases\v1'
            if (-not (Test-Path -LiteralPath $casesDirectory -PathType Container)) { New-Item -ItemType Directory -Path $casesDirectory -Force -ErrorAction Stop | Out-Null }
            Move-Item -LiteralPath $stageCase -Destination $targetCase -ErrorAction Stop
            [pscustomobject][ordered]@{ CaseId = [string]$descriptor.caseId; CaseDirectory = $targetCase; Manifest = $seal.Manifest }
        }
        finally { $archive.Dispose() }
    }
    finally {
        $stream.Dispose()
        if (Test-Path -LiteralPath $stageRoot -PathType Container) { Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

function Remove-GridDiagnosticCase {
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory)][string]$StoreRoot,
        [Parameter(Mandatory)][string]$CaseId
    )
    Assert-GridCaseStoreSegment -Value $CaseId -Name 'CaseId'
    $root = Get-GridDiagnosticStoreRoot -Root $StoreRoot
    $caseDirectory = Join-Path (Join-Path $root 'cases\v1') $CaseId
    if (-not (Test-Path -LiteralPath $caseDirectory -PathType Container)) { return $false }
    $targetSeal = Test-GridDiagnosticCaseSeal -StoreRoot $root -CaseDirectory $caseDirectory
    if (-not $targetSeal.IsValid) { throw "Case '$CaseId' is not safely sealed; deletion and blob collection were refused." }
    $referencedByOthers = @{}
    $casesRoot = Join-Path $root 'cases\v1'
    foreach ($otherCase in @(Get-ChildItem -LiteralPath $casesRoot -Directory -Force -ErrorAction Stop | Where-Object { $_.Name -ine $CaseId })) {
        $otherSeal = Test-GridDiagnosticCaseSeal -StoreRoot $root -CaseDirectory $otherCase.FullName
        if (-not $otherSeal.IsValid) { throw "Case '$($otherCase.Name)' is not safely sealed; blob reference validation failed." }
        foreach ($blob in @($otherSeal.Manifest.blobs)) { $referencedByOthers[[string]$blob.sha256] = $true }
    }
    if ($PSCmdlet.ShouldProcess($caseDirectory, 'Remove sealed diagnostic case and its now-unreferenced blobs')) {
        Remove-Item -LiteralPath $caseDirectory -Recurse -Force -ErrorAction Stop
        foreach ($blob in @($targetSeal.Manifest.blobs)) {
            $hash = [string]$blob.sha256
            if ($referencedByOthers.ContainsKey($hash)) { continue }
            $blobPath = Join-Path (Join-Path (Join-Path $root 'blobs\sha256') $hash.Substring(0, 2)) $hash
            if (Test-Path -LiteralPath $blobPath -PathType Leaf) {
                if ((Get-GridCaseStoreSha256 -LiteralPath $blobPath) -ne $hash) { throw "Blob '$hash' changed before garbage collection." }
                Remove-Item -LiteralPath $blobPath -Force -ErrorAction Stop
            }
        }
        return $true
    }
    $false
}

function Test-GridDiagnosticCaseSemanticIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StoreRoot,
        [Parameter(Mandatory)][string]$CaseDirectory,
        [Parameter(Mandatory)][hashtable]$ExpectedArtifactSha256,
        [string]$ExpectedSemanticBaselineFingerprint
    )
    $seal = Test-GridDiagnosticCaseSeal -StoreRoot $StoreRoot -CaseDirectory $CaseDirectory
    $errors = New-Object Collections.Generic.List[string]
    if (-not $seal.IsValid) {
        foreach ($error in @($seal.Errors)) { $errors.Add([string]$error) }
        return [pscustomobject][ordered]@{ IsValid = $false; Errors = @($errors.ToArray()); Manifest = $seal.Manifest }
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSemanticBaselineFingerprint) -and
        [string]$seal.Manifest.semanticBaselineFingerprint -cne $ExpectedSemanticBaselineFingerprint) {
        $errors.Add('SemanticBaselineFingerprintMismatch')
    }
    foreach ($relativePath in @($ExpectedArtifactSha256.Keys | Sort-Object)) {
        $expected = ([string]$ExpectedArtifactSha256[$relativePath]).ToUpperInvariant()
        if ($expected -notmatch '^[A-F0-9]{64}$') { throw "Expected artifact digest for '$relativePath' is invalid." }
        $resolved = Resolve-GridCaseStoreRelativePath -BaseDirectory $CaseDirectory -RelativePath ([string]$relativePath)
        if (-not (Test-Path -LiteralPath $resolved.FullPath -PathType Leaf)) {
            $errors.Add("ArtifactMissing:$relativePath")
            continue
        }
        $actual = Get-GridCaseStoreSha256 -LiteralPath $resolved.FullPath
        if ($actual -cne $expected) { $errors.Add("ArtifactDigestMismatch:$relativePath") }
    }
    [pscustomobject][ordered]@{ IsValid = ($errors.Count -eq 0); Errors = @($errors.ToArray()); Manifest = $seal.Manifest }
}
