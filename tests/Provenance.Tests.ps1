$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repoRoot = Split-Path -Parent $PSScriptRoot
$gate = Join-Path $repoRoot 'eng/provenance/Test-GridProvenance.ps1'
. $gate

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "ProvenanceTestsFailed: $Message" }
}
function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    if ($Expected -ne $Actual) { throw "ProvenanceTestsFailed: $Message Expected '$Expected', got '$Actual'." }
}

$source = Test-GridProvenanceState -Mode Source -RepositoryRoot $repoRoot
Assert-Equal 'Passed' $source.status ('Source gate must pass: ' + (@($source.violations | ForEach-Object { "$($_.code):$($_.path)" }) -join '; '))
Assert-True ($source.sourceCount -gt 0) 'Source gate must inventory governed source.'
Assert-Equal 3 $source.assetCount 'Asset inventory must cover all three baseline assets.'

$distribution = Test-GridProvenanceState -Mode Distribution -RepositoryRoot $repoRoot
Assert-Equal 'Failed' $distribution.status 'Distribution must remain blocked before attorney review and final licensing.'
$licenseViolationCount = @($distribution.violations | Where-Object { $_.code -eq 'RepositoryLicenseMissing' }).Count
$assetViolationCount = @($distribution.violations | Where-Object { $_.code -eq 'AssetNotClearedForDistribution' }).Count
$dependencyViolationCount = @($distribution.violations | Where-Object { $_.code -eq 'DependencyNotClearedForDistribution' }).Count
Assert-True ($licenseViolationCount -eq 1) 'Distribution gate must require an approved repository license.'
Assert-True ($assetViolationCount -ge 2) 'Unreviewed design assets must block distribution.'
Assert-True ($dependencyViolationCount -ge 2) 'Unreviewed dependency licenses must block distribution.'

$buildDistribution = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/Build-Distribution.ps1') -Raw
$verifyDistribution = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/Verify-Distribution.ps1') -Raw
Assert-True ($buildDistribution -match 'Test-GridProvenance\.ps1' -and $buildDistribution -match '\-Mode Distribution') 'Distribution build must invoke the strict provenance gate before packaging.'
Assert-True ($verifyDistribution -match 'Test-GridProvenance\.ps1' -and $verifyDistribution -match '\-Mode Distribution') 'Distribution verification must invoke the strict provenance gate.'

$ledger = Read-GridProvenanceJson -LiteralPath (Join-Path $repoRoot 'legal/provenance.v1.json')
$mo2 = @($ledger.entries | Where-Object { $_.id -eq 'grid.mo2.interoperability' })
Assert-Equal 1 $mo2.Count 'MO2 interoperability must have one canonical provenance entry.'
Assert-Equal 'InteroperabilityImplementation' $mo2[0].classification 'MO2 compatibility must be classified distinctly from GPL derivation pending review.'
Assert-Equal 'ReviewRequired' $mo2[0].reviewState 'MO2 compatibility implementation must remain queued for file-level review.'

$pattern = ConvertTo-GridGlobRegex -Pattern 'src/Grid.Core/**'
Assert-True ('src/Grid.Core/Models/GridModels.cs' -match $pattern) 'Recursive provenance patterns must include descendants.'
Assert-True (-not ('src/Grid.Mo2/Models/Mo2Models.cs' -match $pattern)) 'Provenance patterns must not cross component boundaries.'

Write-Host 'PASS: GRID provenance coverage, classification, and distribution-refusal contracts passed.'
