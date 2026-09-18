function Assert-GridSkyrimRootCauseFields {
    param([Parameter(Mandatory)]$Value, [Parameter(Mandatory)][string[]]$Required, [Parameter(Mandatory)][string]$Contract)
    foreach ($name in $Required) {
        if ($null -eq $Value.PSObject.Properties[$name]) { throw "$Contract missing required property '$name'." }
    }
}

function Assert-GridSkyrimRootCauseRequestContract {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Request)
    Assert-GridSkyrimRootCauseFields $Request @('schemaVersion','caseId','baselineManifestSha256','installationId','profileId','contextFingerprint','plugins','targets','cellScope','assetTargets','allowedSources','limits') 'RootCauseRequestContract'
    if ([int]$Request.schemaVersion -ne 1 -or [string]$Request.caseId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') { throw 'RootCauseRequestContract has an invalid schema version or case ID.' }
    foreach ($name in @('baselineManifestSha256','contextFingerprint')) { if ([string]$Request.$name -cnotmatch '^[A-F0-9]{64}$') { throw "RootCauseRequestContract has an invalid $name." } }
    if (@($Request.plugins).Count -gt 65536 -or @($Request.targets).Count -gt 4096 -or @($Request.assetTargets).Count -gt 4096 -or @($Request.allowedSources).Count -gt 2000000) { throw 'RootCauseRequestContract exceeds an array ceiling.' }
    if ($Request.cellScope) { Assert-GridSkyrimRootCauseFields $Request.cellScope @('worldspace','cells','spatialSeeds','radius','maximumCells','maximumReferences') 'RootCauseRequestContract.cellScope' }
    foreach ($source in @($Request.allowedSources)) {
        Assert-GridSkyrimRootCauseFields $source @('path','sha256') 'RootCauseRequestContract.allowedSources[]'
        if ([string]::IsNullOrWhiteSpace([string]$source.path) -or [string]$source.sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'RootCauseRequestContract contains an invalid allowed source.' }
    }
    Assert-GridSkyrimRootCauseFields $Request.limits @('maximumWallClockSeconds','maximumRecords','maximumAssets','maximumBytes','recordLimits','assetLimits') 'RootCauseRequestContract.limits'
    if ([int]$Request.limits.maximumWallClockSeconds -lt 1 -or [int]$Request.limits.maximumWallClockSeconds -gt 14400) { throw 'RootCauseRequestContract has an invalid wall-clock ceiling.' }
}

function Assert-GridSkyrimRootCauseResultContract {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Result)
    Assert-GridSkyrimRootCauseFields $Result @('schemaVersion','status','caseId','baselineManifestSha256','installationId','profileId','contextFingerprint','recordGraph','assetGraph','issues','usage','rawSources') 'RootCauseResultContract'
    if ([int]$Result.schemaVersion -ne 1 -or [string]$Result.status -notin @('Completed','Failed','Partial','Cancelled')) { throw 'RootCauseResultContract has an invalid schema version or status.' }
    foreach ($name in @('baselineManifestSha256','contextFingerprint')) { if ([string]$Result.$name -cnotmatch '^[A-F0-9]{64}$') { throw "RootCauseResultContract has an invalid $name." } }
    Assert-GridSkyrimRootCauseFields $Result.recordGraph @('status','plugins','chains','traversedKeys','cellScopeDiscoveries','discoveredKeys','recordHeadersExamined','bytesScanned','issues','semanticFingerprint') 'RootCauseResultContract.recordGraph'
    if ([string]$Result.recordGraph.status -notin @('Complete','Partial','Refused')) { throw 'RootCauseResultContract has an invalid record graph status.' }
    Assert-GridSkyrimRootCauseFields $Result.assetGraph @('status','inspectedBytes','targets','issues') 'RootCauseResultContract.assetGraph'
    Assert-GridSkyrimRootCauseFields $Result.usage @('recordsExamined','assetsExamined','bytesRead','pluginBytesScanned','assetBytesInspected','wallClockMilliseconds') 'RootCauseResultContract.usage'
    foreach ($name in @('recordsExamined','assetsExamined','bytesRead','pluginBytesScanned','assetBytesInspected','wallClockMilliseconds')) { if ([long]$Result.usage.$name -lt 0) { throw "RootCauseResultContract has negative usage '$name'." } }
    foreach ($source in @($Result.rawSources)) {
        Assert-GridSkyrimRootCauseFields $source @('sourceId','path','name','kind','sha256','sizeBytes','readability','beforeLastWriteTimeUtcTicks','afterLastWriteTimeUtcTicks') 'RootCauseResultContract.rawSources[]'
        if ([string]::IsNullOrWhiteSpace([string]$source.sourceId) -or [string]$source.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or [string]$source.readability -ne 'Readable' -or [long]$source.sizeBytes -lt 0) { throw 'RootCauseResultContract contains an invalid raw source.' }
    }
}

function Assert-GridSkyrimPatchSpecificationContract {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Specification)
    Assert-GridSkyrimRootCauseFields $Specification @('schemaVersion','caseId','status','solutionKind','targetPluginName','records','assetActions','evidenceFingerprint','supportingEvidenceIds','proposalId','writer','exclusions','specificationSha256') 'PatchSpecificationContract'
    if ([int]$Specification.schemaVersion -ne 1 -or [string]$Specification.status -ne 'Inert' -or [string]$Specification.solutionKind -notin @('RecordPatch','InstallationAssetRepair','MixedRecordAndAssetRepair')) { throw 'PatchSpecificationContract has an invalid version, status, or solution kind.' }
    if (@($Specification.records).Count -eq 0 -and @($Specification.assetActions).Count -eq 0) { throw 'PatchSpecificationContract contains no deterministic action.' }
    if (@($Specification.records).Count -gt 0 -and [string]$Specification.targetPluginName -notmatch '(?i)\.(esp|esm|esl)$') { throw 'PatchSpecificationContract record edits require a target plugin.' }
    foreach ($name in @('evidenceFingerprint','specificationSha256')) { if ([string]$Specification.$name -cnotmatch '^[A-F0-9]{64}$') { throw "PatchSpecificationContract has an invalid $name." } }
    foreach ($action in @($Specification.assetActions)) { Assert-GridSkyrimRootCauseFields $action @('virtualPath','action','providerName','expectedCurrentState','postcondition') 'PatchSpecificationContract.assetActions[]' }
}
