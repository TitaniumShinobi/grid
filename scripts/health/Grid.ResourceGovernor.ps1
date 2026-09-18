Set-StrictMode -Version Latest

function New-GridBaselineResourceBudget {
    [CmdletBinding()]
    param(
        [ValidateSet(14400)][int]$MaximumWallClockSeconds = 14400,
        [ValidateSet(2000000L)][long]$MaximumFilesObserved = 2000000,
        [ValidateSet(10000000L)][long]$MaximumRecordCatalogEntries = 10000000,
        [ValidateSet(1099511627776L)][long]$MaximumBytesRead = 1099511627776,
        [ValidateSet(1099511627776L)][long]$MaximumBytesHashed = 1099511627776,
        [ValidateSet(268435456L)][long]$MaximumAttachmentBytes = 268435456,
        [ValidateRange(1, [int]::MaxValue)][int]$MaximumPriorCases = 32,
        [ValidateSet(4)][int]$MaximumConcurrentReads = 4,
        [ValidateSet(100000L)][long]$CheckpointIntervalFiles = 100000,
        [ValidateSet(1200)][int]$CheckpointIntervalSeconds = 1200,
        [ValidateSet(68719476736L)][long]$CheckpointIntervalHashedBytes = 68719476736,
        [ValidateRange(0, [long]::MaxValue)][long]$EstimatedTraversalEntries = 2000000,
        [ValidateRange(0, [long]::MaxValue)][long]$EstimatedHashBytes = 1099511627776,
        [ValidateRange(0, [long]::MaxValue)][long]$EstimatedCopiedEvidenceBytes = 0,
        [ValidateRange(0, [long]::MaxValue)][long]$CaseStoreVolumeBytes = 0,
        [ValidateRange(0, [long]::MaxValue)][long]$CaseStoreFreeBytes = 0,
        [ValidateNotNullOrEmpty()][string]$SourceDeviceClass = 'Unknown',
        [ValidateRange(1, [long]::MaxValue)][long]$MeasuredThroughputBytesPerSecond = 1,
        [ValidateRange(0, [long]::MaxValue)][long]$EffectiveMemoryBytes = 536870912,
        [ValidateSet('grid.baseline.resource-policy.v1')][string]$PolicyVersion = 'grid.baseline.resource-policy.v1'
    )

    if ([string]::IsNullOrWhiteSpace($PolicyVersion)) { throw 'PolicyVersion must not be empty.' }

    $oneTiB = 1099511627776L
    $sixteenGiB = 17179869184L
    $tenGiB = 10737418240L
    $fiveHundredTwelveMiB = 536870912L
    $twoHundredFiftySixMiB = 268435456L
    $storageReserve = [long][Math]::Max([double]$tenGiB, [Math]::Floor([double]$CaseStoreVolumeBytes * 0.15))
    $copyHeadroom = [Math]::Ceiling([double]$EstimatedCopiedEvidenceBytes * 1.25) + $twoHundredFiftySixMiB
    $copyAvailable = [Math]::Max(0.0, [double]$CaseStoreFreeBytes - [double]$storageReserve)
    $copiedAllocation = [long][Math]::Min([double]$sixteenGiB, [Math]::Min($copyHeadroom, $copyAvailable))
    $hashAllocation = [long][Math]::Min([double]$EstimatedHashBytes, [double]$oneTiB)
    $traversalAllocation = [long][Math]::Min([double]$EstimatedTraversalEntries, 2000000.0)
    $collectionSeconds = [Math]::Ceiling(([double]$hashAllocation / [Math]::Max([double]$MeasuredThroughputBytesPerSecond, 1.0)) * 1.5 + 900.0)
    $wallAllocation = [long][Math]::Min(14400.0, [Math]::Max(900.0, $collectionSeconds))
    $memoryAllocation = [long][Math]::Min([double]$fiveHundredTwelveMiB, [double]$EffectiveMemoryBytes)

    $effectiveLimits = [pscustomobject][ordered]@{
        maximumWallClockSeconds = [long][Math]::Min($MaximumWallClockSeconds, $wallAllocation)
        maximumFilesObserved = [long][Math]::Min($MaximumFilesObserved, $traversalAllocation)
        maximumEntries = [long][Math]::Min($MaximumFilesObserved, $traversalAllocation)
        maximumRecordCatalogEntries = $MaximumRecordCatalogEntries
        maximumBytesRead = $MaximumBytesRead
        maximumBytesHashed = [long][Math]::Min($MaximumBytesHashed, $hashAllocation)
        maximumAttachmentBytes = $MaximumAttachmentBytes
        maximumCopiedEvidenceBytes = $copiedAllocation
        maximumPriorCases = $MaximumPriorCases
        maximumConcurrentReads = $MaximumConcurrentReads
    }

    [pscustomobject][ordered]@{
        schemaVersion = 1
        policyVersion = $PolicyVersion
        createdAt = [DateTimeOffset]::UtcNow.ToString('o')
        limits = $effectiveLimits
        recordedLimits = [pscustomobject][ordered]@{
            maximumWallClockSeconds = $MaximumWallClockSeconds
            maximumFilesObserved = $MaximumFilesObserved
            maximumEntries = 2000000L
            maximumRecordCatalogEntries = $MaximumRecordCatalogEntries
            maximumBytesRead = $MaximumBytesRead
            maximumBytesHashed = $MaximumBytesHashed
            maximumAttachmentBytes = $MaximumAttachmentBytes
            maximumCaseMetadataBytes = $sixteenGiB
            minimumFreeDiskReserveBytes = $tenGiB
            minimumFreeDiskReservePercent = 15
            maximumWorkingMemoryBytes = $fiveHundredTwelveMiB
            maximumIdleSeconds = 300
            maximumRetries = 1
            retryDelaySeconds = 2
            maximumPriorCases = $MaximumPriorCases
            maximumConcurrentReads = $MaximumConcurrentReads
            remote = [pscustomobject][ordered]@{ maximumItems = 16; maximumItemBytes = 2097152; maximumAggregateBytes = 16777216; timeoutSeconds = 20; maximumRedirects = 5 }
            profile = [pscustomobject][ordered]@{ maximumFileBytes = 33554432; maximumRefreshBytes = 268435456 }
            configuration = [pscustomobject][ordered]@{ maximumFileBytes = 8388608; maximumLines = 65536; maximumEntries = 4096 }
            modMetadata = [pscustomobject][ordered]@{ maximumMetadataBytes = 2097152; maximumCategoriesBytes = 8388608; maximumRefreshBytes = 268435456 }
            parser = [pscustomobject][ordered]@{ maximumPluginHeaderBytes = 16777216; maximumArchiveIndexBytes = 67108864; maximumArchiveMembers = 1000000 }
            virtualData = [pscustomobject][ordered]@{ maximumDepth = 64; maximumSegmentLength = 255; maximumVirtualPathLength = 1024; maximumVirtualPaths = 2000000L; maximumProviderLinks = 8000000L }
            archive = [pscustomobject][ordered]@{ maximumArchives = 16384; maximumTotalMembers = 2000000L; maximumAggregateIndexBytes = 536870912 }
        }
        derivedBudgetInputs = [pscustomobject][ordered]@{
            estimatedTraversalEntries = $EstimatedTraversalEntries
            estimatedHashBytes = $EstimatedHashBytes
            estimatedCopiedEvidenceBytes = $EstimatedCopiedEvidenceBytes
            caseStoreVolumeBytes = $CaseStoreVolumeBytes
            caseStoreFreeBytes = $CaseStoreFreeBytes
            sourceDeviceClass = $SourceDeviceClass
            measuredThroughputBytesPerSecond = $MeasuredThroughputBytesPerSecond
            effectiveMemoryBytes = $EffectiveMemoryBytes
        }
        derivedAllocations = [pscustomobject][ordered]@{
            traversalEntries = $traversalAllocation
            recordCatalogEntries = $MaximumRecordCatalogEntries
            hashBytes = $hashAllocation
            copiedEvidenceBytes = $copiedAllocation
            storageReserveBytes = $storageReserve
            wallClockSeconds = $wallAllocation
            memoryBytes = $memoryAllocation
            concurrentMetadataReads = 4
            concurrentHashReads = 2
        }
        derivedBudgetFormulas = [pscustomobject][ordered]@{
            reserve = 'max(10GiB,floor(caseStoreVolumeBytes*0.15))'
            copied = 'min(16GiB,ceil(estimatedCopiedEvidenceBytes*1.25)+256MiB,max(0,caseStoreFreeBytes-storageReserveBytes))'
            hash = 'min(estimatedHashBytes,1TiB)'
            traversal = 'min(estimatedTraversalEntries,2000000)'
            recordCatalog = '10000000 streamed rows; bounded by the TES4 record-header ceiling'
            wall = 'min(14400,max(900,ceil(hashBytes/max(measuredThroughputBytesPerSecond,1))*1.5+900))'
            memory = 'min(512MiB,effectiveMemoryBytes)'
            metadataReaders = '4'
            hashReaders = '2'
        }
        checkpointPolicy = [pscustomobject][ordered]@{
            intervalFiles = $CheckpointIntervalFiles
            intervalSeconds = $CheckpointIntervalSeconds
            intervalHashedBytes = $CheckpointIntervalHashedBytes
        }
    }
}

function Test-GridBaselineBudget {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [psobject]$Budget,

        [Parameter(Mandatory)]
        [psobject]$Usage
    )

    $limitMap = [ordered]@{
        wallClockSeconds = 'maximumWallClockSeconds'
        filesObserved = 'maximumFilesObserved'
        bytesRead = 'maximumBytesRead'
        bytesHashed = 'maximumBytesHashed'
        attachmentBytes = 'maximumAttachmentBytes'
        priorCases = 'maximumPriorCases'
        concurrentReads = 'maximumConcurrentReads'
    }

    if ($null -eq $Budget.PSObject.Properties['limits'] -or
        $null -eq $Budget.PSObject.Properties['checkpointPolicy']) {
        throw 'Budget must contain limits and checkpointPolicy objects.'
    }

    $normalizedUsage = [ordered]@{}
    foreach ($usageName in @($limitMap.Keys) + @('filesSinceCheckpoint', 'secondsSinceCheckpoint')) {
        $property = $Usage.PSObject.Properties[$usageName]
        if ($null -eq $property) {
            throw "Usage is missing required property '$usageName'."
        }

        $parsed = 0L
        if (-not [long]::TryParse([string]$property.Value, [ref]$parsed) -or $parsed -lt 0) {
            throw "Usage property '$usageName' must be a non-negative integer."
        }
        $normalizedUsage[$usageName] = $parsed
    }

    $exceeded = @()
    foreach ($usageName in $limitMap.Keys) {
        $limitName = $limitMap[$usageName]
        $limitProperty = $Budget.limits.PSObject.Properties[$limitName]
        if ($null -eq $limitProperty) {
            throw "Budget limits are missing required property '$limitName'."
        }

        $limit = 0L
        if (-not [long]::TryParse([string]$limitProperty.Value, [ref]$limit) -or $limit -lt 1) {
            throw "Budget limit '$limitName' must be a positive integer."
        }

        if ($normalizedUsage[$usageName] -gt $limit) {
            $exceeded += [pscustomobject][ordered]@{
                resource = $usageName
                observed = $normalizedUsage[$usageName]
                limit = $limit
            }
        }
    }

    foreach ($checkpointName in @('intervalFiles', 'intervalSeconds', 'intervalHashedBytes')) {
        $property = $Budget.checkpointPolicy.PSObject.Properties[$checkpointName]
        if ($null -eq $property) {
            throw "Budget checkpointPolicy is missing required property '$checkpointName'."
        }
        $value = 0L
        if (-not [long]::TryParse([string]$property.Value, [ref]$value) -or $value -lt 1) {
            throw "Budget checkpoint policy '$checkpointName' must be a positive integer."
        }
    }

    $hashedSinceCheckpoint = 0L
    if ($null -ne $Usage.PSObject.Properties['bytesHashedSinceCheckpoint']) {
        if (-not [long]::TryParse([string]$Usage.bytesHashedSinceCheckpoint, [ref]$hashedSinceCheckpoint) -or $hashedSinceCheckpoint -lt 0) {
            throw "Usage property 'bytesHashedSinceCheckpoint' must be a non-negative integer."
        }
    }
    $normalizedUsage['bytesHashedSinceCheckpoint'] = $hashedSinceCheckpoint

    $shouldCheckpoint =
        ($normalizedUsage.filesSinceCheckpoint -ge [long]$Budget.checkpointPolicy.intervalFiles) -or
        ($normalizedUsage.secondsSinceCheckpoint -ge [long]$Budget.checkpointPolicy.intervalSeconds) -or
        ($normalizedUsage.bytesHashedSinceCheckpoint -ge [long]$Budget.checkpointPolicy.intervalHashedBytes)

    [pscustomobject][ordered]@{
        IsWithinBudget = ($exceeded.Count -eq 0)
        Status = if ($exceeded.Count -eq 0) { 'WithinBudget' } else { 'BudgetExceeded' }
        Exceeded = @($exceeded)
        ShouldCheckpoint = $shouldCheckpoint
        Usage = [pscustomobject]$normalizedUsage
    }
}
