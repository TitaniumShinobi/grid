[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$BeforePath,
    [Parameter(Mandatory)] [string]$AfterPath,
    [string]$OutputPath,
    [switch]$FailOnSemanticChange
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Read-Snapshot {
    param([Parameter(Mandatory)] [string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
    if ($item.Length -gt 256MB) { throw "Snapshot exceeds the 256 MiB comparison limit: '$fullPath'." }
    $snapshot = Get-Content -LiteralPath $fullPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($snapshot.schemaVersion -ne 1 -or $snapshot.kind -ne 'grid-wabbajack-semantic-snapshot') {
        throw "Unsupported snapshot schema in '$fullPath'."
    }
    return $snapshot
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

function New-Index {
    param(
        [AllowEmptyCollection()] [object[]]$Rows,
        [Parameter(Mandatory)] [string]$KeyProperty
    )

    $index = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($row in $Rows) {
        $key = [string]$row.$KeyProperty
        if ($index.ContainsKey($key)) { throw "Duplicate '$KeyProperty' key in snapshot: '$key'." }
        $index.Add($key, $row)
    }
    return $index
}

function Get-Changes {
    param(
        [Parameter(Mandatory)] $BeforeIndex,
        [Parameter(Mandatory)] $AfterIndex,
        [Parameter(Mandatory)] [string[]]$ComparedProperties
    )

    $changes = [System.Collections.Generic.List[object]]::new()
    $keys = @($BeforeIndex.Keys + $AfterIndex.Keys | Sort-Object -Unique)
    foreach ($key in $keys) {
        $beforeExists = $BeforeIndex.ContainsKey($key)
        $afterExists = $AfterIndex.ContainsKey($key)
        if (-not $beforeExists) { $changes.Add([pscustomobject]@{ key = $key; change = 'added'; fields = @() }); continue }
        if (-not $afterExists) { $changes.Add([pscustomobject]@{ key = $key; change = 'removed'; fields = @() }); continue }
        $changedFields = @(
            foreach ($property in $ComparedProperties) {
                if ($property -eq 'lastWriteTimeUtc' -and $BeforeIndex[$key].kind -eq 'directory') { continue }
                $beforeValue = ConvertTo-Json -InputObject $BeforeIndex[$key].$property -Compress -Depth 4
                $afterValue = ConvertTo-Json -InputObject $AfterIndex[$key].$property -Compress -Depth 4
                if ($beforeValue -cne $afterValue) { $property }
            }
        )
        if ($changedFields.Count -gt 0) { $changes.Add([pscustomobject]@{ key = $key; change = 'modified'; fields = $changedFields }) }
    }
    return @($changes)
}

$before = Read-Snapshot -Path $BeforePath
$after = Read-Snapshot -Path $AfterPath
if (-not [string]::Equals($before.installationRoot, $after.installationRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Snapshots refer to different installation roots.'
}

$beforeTree = New-Index -Rows @($before.tree) -KeyProperty 'relativePath'
$afterTree = New-Index -Rows @($after.tree) -KeyProperty 'relativePath'
$treeChanges = @(Get-Changes -BeforeIndex $beforeTree -AfterIndex $afterTree -ComparedProperties @('kind', 'length', 'lastWriteTimeUtc', 'attributes', 'reparsePoint', 'contentOmitted'))
$beforeSemantic = New-Index -Rows @($before.semanticFiles) -KeyProperty 'relativePath'
$afterSemantic = New-Index -Rows @($after.semanticFiles) -KeyProperty 'relativePath'
$semanticChanges = @(Get-Changes -BeforeIndex $beforeSemantic -AfterIndex $afterSemantic -ComparedProperties @('length', 'sha256', 'status'))

$result = [ordered]@{
    schemaVersion = 1
    kind = 'grid-wabbajack-semantic-comparison'
    comparedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    installationRoot = $before.installationRoot
    beforeObservedAtUtc = $before.observedAtUtc
    afterObservedAtUtc = $after.observedAtUtc
    semanticState = if ($semanticChanges.Count -eq 0) { 'no-change-observed' } else { 'changed' }
    structuralState = if ($treeChanges.Count -eq 0) { 'no-change-observed' } else { 'changed' }
    beforeCompleteness = $before.completeness
    afterCompleteness = $after.completeness
    beforeSemanticFingerprintSha256 = $before.semanticFingerprintSha256
    afterSemanticFingerprintSha256 = $after.semanticFingerprintSha256
    beforeStructuralFingerprintSha256 = $before.structuralFingerprintSha256
    afterStructuralFingerprintSha256 = $after.structuralFingerprintSha256
    semanticChanges = @($semanticChanges)
    structuralChanges = @($treeChanges)
}

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $output = [System.IO.Path]::GetFullPath($OutputPath)
    $installationPrefix = ([System.IO.Path]::GetFullPath([string]$before.installationRoot).TrimEnd('\') + '\')
    if ($output.StartsWith($installationPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Comparison output must be outside the observed installation root.'
    }
    Write-AtomicUtf8Json -Path $output -Json (ConvertTo-Json -InputObject $result -Depth 8)
    Write-Host "Comparison: $output"
}

Write-Host "Semantic changes: $($semanticChanges.Count); structural changes: $($treeChanges.Count)"
if ($FailOnSemanticChange -and $semanticChanges.Count -gt 0) { throw 'Semantic evidence changed.' }
$result
