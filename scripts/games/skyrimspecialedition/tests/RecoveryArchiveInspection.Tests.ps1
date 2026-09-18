$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent $PSScriptRoot
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $gameRoot '..\..\..'))
. (Join-Path $repositoryRoot 'scripts\health\Grid.CaseStore.ps1')
. (Join-Path $gameRoot 'health\collectors\Invoke-GridSkyrimBaseline.ps1')

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }

$tempRoot = Join-Path $env:TEMP ('grid-recovery-archive-tests-' + [guid]::NewGuid().ToString('N'))
$storeRoot = Join-Path $tempRoot 'store'
$stageRoot = Join-Path $tempRoot 'normalized'
$archivePath = Join-Path $tempRoot 'fixture.zip'
New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
try {
    Add-Type -AssemblyName System.IO.Compression
    $stream = [IO.File]::Open($archivePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $zip = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            $pluginEntry = $zip.CreateEntry('Fixture.esp')
            $pluginWriter = New-Object IO.StreamWriter($pluginEntry.Open(), ([Text.UTF8Encoding]::new($false)))
            try { $pluginWriter.Write('fixture plugin') } finally { $pluginWriter.Dispose() }
            $entry = $zip.CreateEntry('Data/scripts/fixture.pex')
            $writer = New-Object IO.StreamWriter($entry.Open(), ([Text.UTF8Encoding]::new($false)))
            try { $writer.Write('fixture') } finally { $writer.Dispose() }
            $meshEntry = $zip.CreateEntry('meshes/fixture/missing.nif')
            $meshWriter = New-Object IO.StreamWriter($meshEntry.Open(), ([Text.UTF8Encoding]::new($false)))
            try { $meshWriter.Write('fixture mesh') } finally { $meshWriter.Dispose() }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
    $sha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $pluginSha256 = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes('fixture plugin')))).Replace('-', '') }
    finally { $sha.Dispose() }
    $requirement = [pscustomobject]@{ pluginName='Fixture.esp'; sourceProvider='Fixture Mod'; requiredVirtualPath='scripts\fixture.pex' }
    $assetRequirement = [pscustomobject]@{ pluginName='Fixture.esp'; sourceProvider='Fixture Mod'; kind='Mesh'; requiredVirtualPath='meshes\fixture\missing.nif' }
    $normalized = [pscustomobject]@{
        StagingDirectory=$stageRoot
        PluginScriptDependencies=@($requirement)
        PluginAssetDependencies=@($assetRequirement)
        PluginRecords=@([pscustomobject]@{name='Fixture.esp';isEnabled=$true;observation=[pscustomobject]@{sourceProvider='Fixture Mod'}})
        FileHashRecords=@([pscustomobject]@{virtualPath='Fixture.esp';providerName='Fixture Mod';kind='Plugin';status='Complete';sha256=$pluginSha256})
        PluginMasterRecords=@()
        Metadata=@()
        SourceArchiveSidecars=@()
        SourceArchives=@([pscustomobject]@{modId='fixture-mod';modName='Fixture Mod';kind='InstallationArchive';state='Present';hashStatus='Complete';sha256=$sha256;canonicalPath=$archivePath;length=(Get-Item -LiteralPath $archivePath).Length})
    }
    $transaction = New-GridCaseStoreTransaction -StoreRoot $storeRoot -CaseId 'archive-inspection-fixture'
    $executable = Join-Path $repositoryRoot 'src\Grid.Diagnostics\bin\x64\Debug\net9.0-windows10.0.19041.0\Grid.Diagnostics.exe'
    $result = Invoke-GridSkyrimRecoveryArchiveInspections -Transaction $transaction -RunId 'fixture-run' -Normalized $normalized -DiagnosticsExecutable $executable -MaximumEntries 1000 -MaximumWallClockSeconds 30
    Assert-Equal 1 $result.CommandCount 'One unique archive must be inspected.'
    Assert-Equal 2 $result.Records[0].schemaVersion 'Archive evidence must identify the current matcher contract so stale results can be re-evaluated.'
    Assert-Equal 'Complete' $result.Records[0].status 'The bounded archive inspection must complete.'
    Assert-Equal 2 $result.Records[0].matchedRequiredFileCount 'The PEX and NIF requirements must both match.'
    Assert-True (@($result.Records[0].matches | Where-Object { $_.requiredVirtualPath -eq 'scripts\fixture.pex' -and $_.mappingRule -eq 'ExplicitDataDirectory' }).Count -eq 1) 'The explicit Data-relative PEX mapping rule must remain explicit.'
    Assert-True (@($result.Records[0].matches | Where-Object { $_.requiredVirtualPath -eq 'meshes\fixture\missing.nif' -and $_.mappingRule -eq 'ArchiveRoot' }).Count -eq 1) 'The archive-root NIF must be matched as a repair source candidate.'
    Assert-Equal 1 $result.Assessments.Count 'A valid candidate assessment must be retained even when Windows PowerShell exposes a blank redirected-process ExitCode.'
    Assert-True ([string]$result.Assessments[0].evidenceId -match '^archive-candidate\.[a-f0-9]{24}$') 'The retained assessment must carry a validated deterministic evidence identifier.'
    Assert-True (Test-Path -LiteralPath (Join-Path $transaction.CaseDirectory 'provenance\source-archive-inspections.v1.ndjson')) 'Compact inspection evidence must be written into the case transaction.'
    $cleanupPaths = @(Get-GridBaselineTransactionTemporaryPaths `
        -Bridge ([pscustomobject]@{
            Attempts = @([pscustomobject]@{ outputPath = 'C:\fixture\out.ndjson'; errorPath = 'C:\fixture\err.txt' })
            BuildPath = 'C:\fixture\build.txt'
        }) `
        -Normalized ([pscustomobject]@{ StagingDirectory = 'C:\fixture\normalized' }) `
        -ArchiveInspection ([pscustomobject]@{ TemporaryPaths = @('C:\fixture\requirements.ndjson', 'C:\fixture\archive.ndjson') }))
    Assert-Equal 6 $cleanupPaths.Count 'Every transaction-local temporary path must be flattened into its own cleanup entry.'
    Assert-True ('C:\fixture\requirements.ndjson' -in $cleanupPaths -and 'C:\fixture\archive.ndjson' -in $cleanupPaths) 'Archive-inspection temporary paths must remain separate exact paths.'
    Assert-True (@($cleanupPaths | Where-Object { $_ -match 'System\.Object\[\]' -or $_ -match '\.ndjson\s+[A-Za-z]:' }).Count -eq 0) 'Cleanup path flattening must never stringify a nested path array.'
    'Recovery archive inspection checks passed.'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
