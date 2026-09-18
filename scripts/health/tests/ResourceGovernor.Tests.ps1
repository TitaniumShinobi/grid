[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'Grid.ResourceGovernor.ps1')

function Assert-GridResourceTest {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-GridResourceThrows {
    param([scriptblock]$Action, [string]$Message)
    $threw = $false
    try { & $Action } catch { $threw = $true }
    if (-not $threw) { throw $Message }
}

$budget = New-GridBaselineResourceBudget
Assert-GridResourceTest ($budget.schemaVersion -eq 1) 'Budget schemaVersion must be 1.'
Assert-GridResourceTest ($budget.policyVersion -eq 'grid.baseline.resource-policy.v1') 'Default policyVersion is incorrect.'
Assert-GridResourceTest ($budget.recordedLimits.maximumBytesRead -eq 1099511627776) 'Default byte-read limit changed unexpectedly.'
Assert-GridResourceTest ($budget.recordedLimits.maximumFilesObserved -eq 2000000) 'Default traversal limit changed unexpectedly.'
Assert-GridResourceTest ($budget.recordedLimits.maximumRecordCatalogEntries -eq 10000000) 'Default streamed record-catalog limit is incorrect.'
Assert-GridResourceTest ($budget.recordedLimits.remote.maximumRedirects -eq 5) 'Remote redirect limit changed unexpectedly.'
Assert-GridResourceTest ($budget.recordedLimits.profile.maximumFileBytes -eq 33554432) 'Profile file limit changed unexpectedly.'
Assert-GridResourceTest ($budget.recordedLimits.modMetadata.maximumMetadataBytes -eq 2097152) 'Mod metadata limit changed unexpectedly.'
Assert-GridResourceTest ($budget.checkpointPolicy.intervalHashedBytes -eq 68719476736) 'Hashed-byte partition boundary changed unexpectedly.'

$derived = New-GridBaselineResourceBudget -EstimatedTraversalEntries 3000000 -EstimatedHashBytes 2199023255552 `
    -EstimatedCopiedEvidenceBytes 1073741824 -CaseStoreVolumeBytes 107374182400 -CaseStoreFreeBytes 53687091200 `
    -SourceDeviceClass 'SyntheticSsd' -MeasuredThroughputBytesPerSecond 104857600 -EffectiveMemoryBytes 268435456
Assert-GridResourceTest ($derived.derivedBudgetInputs.estimatedTraversalEntries -eq 3000000) 'Caller traversal estimate was not preserved.'
Assert-GridResourceTest ($derived.derivedBudgetInputs.sourceDeviceClass -eq 'SyntheticSsd') 'Caller device class was not preserved.'
Assert-GridResourceTest ($derived.derivedAllocations.traversalEntries -eq 2000000) 'Traversal allocation formula is incorrect.'
Assert-GridResourceTest ($derived.derivedAllocations.recordCatalogEntries -eq 10000000) 'Record-catalog allocation is incorrect.'
Assert-GridResourceTest ($derived.derivedAllocations.hashBytes -eq 1099511627776) 'Hash allocation formula is incorrect.'
Assert-GridResourceTest ($derived.derivedAllocations.copiedEvidenceBytes -eq 1610612736) 'Copied-evidence allocation formula is incorrect.'
Assert-GridResourceTest ($derived.derivedAllocations.storageReserveBytes -eq 16106127360) 'Storage reserve formula is incorrect.'
Assert-GridResourceTest ($derived.derivedAllocations.wallClockSeconds -eq 14400) 'Wall-clock allocation formula is incorrect.'
Assert-GridResourceTest ($derived.derivedAllocations.memoryBytes -eq 268435456) 'Memory allocation formula is incorrect.'
Assert-GridResourceTest ($derived.derivedBudgetFormulas.hash -eq 'min(estimatedHashBytes,1TiB)') 'Formula audit strings were not retained.'

$usage = [pscustomobject][ordered]@{
    wallClockSeconds = 30
    filesObserved = 100
    bytesRead = 4096
    bytesHashed = 2048
    attachmentBytes = 0
    priorCases = 0
    concurrentReads = 1
    filesSinceCheckpoint = 10
    secondsSinceCheckpoint = 10
    bytesHashedSinceCheckpoint = 10
}

$within = Test-GridBaselineBudget -Budget $budget -Usage $usage
Assert-GridResourceTest $within.IsWithinBudget 'In-budget usage was rejected.'
Assert-GridResourceTest ($within.Status -eq 'WithinBudget') 'In-budget status is incorrect.'
Assert-GridResourceTest (-not $within.ShouldCheckpoint) 'A premature checkpoint was requested.'

$usage.filesSinceCheckpoint = $budget.checkpointPolicy.intervalFiles
$checkpoint = Test-GridBaselineBudget -Budget $budget -Usage $usage
Assert-GridResourceTest $checkpoint.ShouldCheckpoint 'File-count checkpoint boundary was not honored.'

$usage.filesSinceCheckpoint = 0
$usage.secondsSinceCheckpoint = $budget.checkpointPolicy.intervalSeconds
$checkpoint = Test-GridBaselineBudget -Budget $budget -Usage $usage
Assert-GridResourceTest $checkpoint.ShouldCheckpoint 'Time checkpoint boundary was not honored.'

$usage.secondsSinceCheckpoint = 0
$usage.bytesHashedSinceCheckpoint = $budget.checkpointPolicy.intervalHashedBytes
$checkpoint = Test-GridBaselineBudget -Budget $budget -Usage $usage
Assert-GridResourceTest $checkpoint.ShouldCheckpoint 'Hashed-byte checkpoint boundary was not honored.'

$usage.bytesHashedSinceCheckpoint = 0
$usage.bytesRead = [long]$budget.limits.maximumBytesRead + 1
$usage.concurrentReads = [long]$budget.limits.maximumConcurrentReads + 1
$exceeded = Test-GridBaselineBudget -Budget $budget -Usage $usage
Assert-GridResourceTest (-not $exceeded.IsWithinBudget) 'Over-budget usage was accepted.'
Assert-GridResourceTest ($exceeded.Status -eq 'BudgetExceeded') 'Over-budget status is incorrect.'
Assert-GridResourceTest ($exceeded.Exceeded.Count -eq 2) 'Expected two exceeded resources.'
Assert-GridResourceTest (@($exceeded.Exceeded.resource) -contains 'bytesRead') 'bytesRead exceedance was not reported.'
Assert-GridResourceTest (@($exceeded.Exceeded.resource) -contains 'concurrentReads') 'concurrentReads exceedance was not reported.'

$invalidUsage = $usage.PSObject.Copy()
$invalidUsage.bytesRead = -1
Assert-GridResourceThrows { Test-GridBaselineBudget -Budget $budget -Usage $invalidUsage } 'Negative usage was accepted.'

$missingUsage = [pscustomobject]@{ wallClockSeconds = 0 }
Assert-GridResourceThrows { Test-GridBaselineBudget -Budget $budget -Usage $missingUsage } 'Incomplete usage was accepted.'

Write-Host 'ResourceGovernor.Tests.ps1: PASS'
