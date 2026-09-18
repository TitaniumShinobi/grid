$ErrorActionPreference = 'Stop'
$collector = Join-Path (Split-Path -Parent $PSScriptRoot) 'health\collectors\Invoke-GridSkyrimRecoverySourceRefresh.ps1'
. $collector

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." }
}

$shaA = 'A' * 64
$shaB = 'B' * 64
$archive = [pscustomobject]@{
    state = 'Present'; hashStatus = 'Complete'; canonicalPath = 'C:\fixtures\source.7z'; sha256 = $shaA
}
$oldRejected = [pscustomobject]@{
    schemaVersion = 1; status = 'Rejected'; archivePath = 'c:\FIXTURES\source.7z'; archiveSha256 = $shaA.ToLowerInvariant()
}
$current = [pscustomobject]@{
    schemaVersion = 2; status = 'Complete'; archivePath = 'C:\fixtures\source.7z'; archiveSha256 = $shaA
}
$currentAssessment = [pscustomobject]@{
    schemaVersion = 2; status = 'Complete'; archivePath = 'c:\FIXTURES\source.7z'; archiveSha256 = $shaA.ToLowerInvariant()
}

$stale = Select-GridSkyrimRecoveryArchivesForInspection -Archives @($archive) -PriorInspections @($oldRejected)
Assert-Equal 1 @($stale.Archives).Count 'A byte-identical archive with old inspection evidence must be selected again.'
Assert-Equal 1 @($stale.ReplacementKeys).Count 'Stale evidence replacement must bind one exact path/hash identity.'

$missingAssessment = Select-GridSkyrimRecoveryArchivesForInspection -Archives @($archive) -PriorInspections @($oldRejected, $current)
Assert-Equal 1 @($missingAssessment.Archives).Count 'A current inspection without current candidate assessment evidence must be selected again.'

$settled = Select-GridSkyrimRecoveryArchivesForInspection -Archives @($archive) -PriorInspections @($oldRejected, $current) -PriorAssessments @($currentAssessment)
Assert-Equal 0 @($settled.Archives).Count 'Current inspection and assessment evidence for the exact archive identity must prevent a retry loop.'

$changed = $archive | Select-Object *
$changed.sha256 = $shaB
$changedSelection = Select-GridSkyrimRecoveryArchivesForInspection -Archives @($changed) -PriorInspections @($current) -PriorAssessments @($currentAssessment)
Assert-Equal 1 @($changedSelection.Archives).Count 'Changed archive bytes must require a new inspection.'

$missing = $archive | Select-Object *
$missing.state = 'Missing'
$missingSelection = Select-GridSkyrimRecoveryArchivesForInspection -Archives @($missing) -PriorInspections @()
Assert-Equal 0 @($missingSelection.Archives).Count 'An absent source must never be selected for inspection.'

'Recovery source refresh checks passed.'
