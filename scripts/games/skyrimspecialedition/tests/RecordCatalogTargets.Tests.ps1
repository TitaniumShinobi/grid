$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$gameRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $gameRoot 'health\collectors\Select-GridSkyrimRecordCatalogTargets.ps1')

function Assert-True([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', got '$Actual'." } }

$root = Join-Path ([IO.Path]::GetTempPath()) ('grid-record-catalog-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    $catalog = Join-Path $root 'plugin-record-catalog.v1.ndjson'
    $lines = @(
        [pscustomobject][ordered]@{recordSignature='SPEL';originPlugin='Skyrim.esm';localFormId=0x01D172;pluginName='Skyrim.esm';loadOrder=0;rawFormId=0x0001D172;recordOffset=120;recordFlags=0;editorId='Candlelight';dataSha256=('A'*64);isOverride=$false},
        [pscustomobject][ordered]@{recordSignature='SPEL';originPlugin='Skyrim.esm';localFormId=0x0433A5;pluginName='Lighting Patch.esp';loadOrder=22;rawFormId=0x000433A5;recordOffset=240;recordFlags=0;editorId='Magelight';dataSha256=('B'*64);isOverride=$true},
        [pscustomobject][ordered]@{recordSignature='ACHR';originPlugin='Immersive Encounters.esp';localFormId=0x2879A;pluginName='Immersive Encounters.esp';loadOrder=93;rawFormId=0x5D22879A;recordOffset=360;recordFlags=0;editorId='SetteWIBardF02';dataSha256=('C'*64);isOverride=$false},
        [pscustomobject][ordered]@{recordSignature='STAT';originPlugin='Skyrim.esm';localFormId=0x000123;pluginName='Skyrim.esm';loadOrder=0;rawFormId=0x00000123;recordOffset=480;recordFlags=0;dataSha256=('D'*64);isOverride=$false}
    ) | ForEach-Object { $_ | ConvertTo-Json -Compress }
    [IO.File]::WriteAllLines($catalog, [string[]]$lines, [Text.UTF8Encoding]::new($false))
    $sha = (Get-FileHash -LiteralPath $catalog -Algorithm SHA256).Hash

    $magic = Select-GridSkyrimRecordCatalogTargets -CatalogPath $catalog -CatalogStatus Complete -ExpectedSha256 $sha -ExpectedRecordCount 4 -RecordSignature SPEL -EditorIdContains light
    Assert-Equal Complete $magic.status 'Complete catalog query must complete.'
    Assert-Equal 2 @($magic.matches).Count 'Both light spell records must match.'
    Assert-Equal 2 @($magic.targets).Count 'Distinct canonical targets must be retained.'
    Assert-True (@($magic.targets | Where-Object canonicalKey -eq 'Skyrim.esm|01D172').Count -eq 1) 'Canonical target must be normalized.'

    $canonical = Select-GridSkyrimRecordCatalogTargets -CatalogPath $catalog -CatalogStatus Complete -ExpectedSha256 $sha -ExpectedRecordCount 4 -CanonicalKey 'skyrim.esm|433a5'
    Assert-Equal 1 @($canonical.matches).Count 'Canonical-key filtering must resolve one exact chain target.'
    Assert-Equal 'Magelight' $canonical.matches[0].editorId 'Canonical-key lookup must retain the matching record occurrence.'

    $bard = Select-GridSkyrimRecordCatalogTargets -CatalogPath $catalog -CatalogStatus Partial -ExpectedSha256 $sha -ExpectedRecordCount 4 -PluginName 'Immersive Encounters.esp' -RecordSignature ACHR
    Assert-Equal Partial $bard.status 'Positive matches from partial source remain explicitly partial.'
    Assert-Equal 'SetteWIBardF02' $bard.matches[0].editorId 'Exact plugin/signature filtering must retain the bard record.'
    Assert-Equal 1 @($bard.issues).Count 'Partial coverage warning must be explicit.'

    $withoutEditorId = Select-GridSkyrimRecordCatalogTargets -CatalogPath $catalog -CatalogStatus Complete -ExpectedSha256 $sha -ExpectedRecordCount 4 -CanonicalKey 'skyrim.esm|123'
    Assert-Equal 1 @($withoutEditorId.matches).Count 'Records without an optional Editor ID must remain queryable.'
    Assert-True ($null -eq $withoutEditorId.matches[0].editorId) 'An omitted Editor ID must normalize to null.'

    $overflow = Select-GridSkyrimRecordCatalogTargets -CatalogPath $catalog -CatalogStatus Complete -ExpectedSha256 $sha -ExpectedRecordCount 4 -RecordSignature SPEL -MaximumMatches 1
    Assert-Equal Refused $overflow.status 'Overflow must fail closed.'
    Assert-Equal 0 @($overflow.targets).Count 'Overflow must not return a partial target set.'

    $wrongDigestRejected = $false
    try { Select-GridSkyrimRecordCatalogTargets -CatalogPath $catalog -CatalogStatus Complete -ExpectedSha256 ('D'*64) -ExpectedRecordCount 4 -RecordSignature SPEL | Out-Null } catch { $wrongDigestRejected = $_.Exception.Message -like 'RecordCatalogInvalid:*digest*' }
    Assert-True $wrongDigestRejected 'Digest mismatch must be rejected.'

    $fullScanBounds = (Get-Command Select-GridSkyrimRecordCatalogTargets).Parameters
    Assert-Equal 10000000 $fullScanBounds.ExpectedRecordCount.Attributes.Where({ $_ -is [Management.Automation.ValidateRangeAttribute] })[0].MaxRange 'Target queries must accept the full-scan record bound.'
    Assert-Equal 10000000 $fullScanBounds.MaximumRecordsRead.Attributes.Where({ $_ -is [Management.Automation.ValidateRangeAttribute] })[0].MaxRange 'Target query read limits must accept the full-scan record bound.'
    Assert-Equal 8589934592 $fullScanBounds.MaximumCatalogBytes.Attributes.Where({ $_ -is [Management.Automation.ValidateRangeAttribute] })[0].MaxRange 'Target queries must accept bounded multi-gigabyte catalogs.'
    Write-Host 'PASS: bounded record-catalog queries produce exact root-cause targets and fail closed.'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
