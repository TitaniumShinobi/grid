[CmdletBinding()]
param([string]$RepositoryRoot, [string]$OutputPath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'Grid.Provenance.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Get-GridProvenanceRepositoryRoot }
$RepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $RepositoryRoot 'artifacts/provenance/repository-inventory.v1.json' }

$ledger = Read-GridProvenanceJson -LiteralPath (Join-Path $RepositoryRoot 'legal/provenance.v1.json')
$files = @((Get-GridGovernedSourceFiles -RepositoryRoot $RepositoryRoot) + (Get-GridGovernedAssetFiles -RepositoryRoot $RepositoryRoot) | Sort-Object RelativePath | ForEach-Object {
    $matches = @(Get-GridPathLedgerMatches -RelativePath $_.RelativePath -Ledger $ledger)
    [ordered]@{
        path = $_.RelativePath
        length = [int64]$_.Length
        sha256 = Get-GridSha256 -LiteralPath $_.FullName
        lastWriteTimeUtc = $_.LastWriteTimeUtc.ToString('o')
        provenanceEntries = @($matches | ForEach-Object { [string]$_.id })
    }
})

$inventory = [ordered]@{
    schemaVersion = 1
    inventoryId = 'grid.repository-provenance-inventory.v1'
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    repositoryRoot = $RepositoryRoot
    gitHistory = Get-GridGitHistoryState -RepositoryRoot $RepositoryRoot
    fileCount = $files.Count
    files = $files
}
Write-GridUtf8Json -InputObject $inventory -LiteralPath $OutputPath -Depth 10
Write-Host "PASS: Wrote Grid provenance inventory for $($files.Count) files to $OutputPath"
