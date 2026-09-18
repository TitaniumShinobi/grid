Set-StrictMode -Version Latest

function Select-GridSkyrimRecordCatalogTargets {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CatalogPath,
        [Parameter(Mandatory)][ValidateSet('Complete','Partial')][string]$CatalogStatus,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedSha256,
        [Parameter(Mandatory)][ValidateRange(0, 10000000)][long]$ExpectedRecordCount,
        [string[]]$RecordSignature = @(),
        [string[]]$OriginPlugin = @(),
        [string[]]$PluginName = @(),
        [string[]]$EditorId = @(),
        [string[]]$EditorIdContains = @(),
        [string[]]$CanonicalKey = @(),
        [ValidateRange(1, 100000)][int]$MaximumMatches = 10000,
        [ValidateRange(1, 10000000)][int]$MaximumRecordsRead = 10000000,
        [ValidateRange(1, 8589934592)][long]$MaximumCatalogBytes = 8589934592
    )

    $fullPath = [IO.Path]::GetFullPath($CatalogPath)
    $info = [IO.FileInfo]::new($fullPath)
    if (-not $info.Exists -or ($info.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'RecordCatalogInvalid: the catalog must be a present non-reparse file.'
    }
    if ($info.Length -gt $MaximumCatalogBytes) { throw 'RecordCatalogLimit: catalog bytes exceed the configured bound.' }
    $actualSha = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256 -ErrorAction Stop).Hash
    if ($actualSha -cne $ExpectedSha256.ToUpperInvariant()) { throw 'RecordCatalogInvalid: catalog digest mismatch.' }

    $signatures = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($value in @($RecordSignature)) {
        $normalized = ([string]$value).Trim().ToUpperInvariant()
        if ($normalized -notmatch '^[A-Z0-9_]{4}$') { throw "RecordCatalogQueryInvalid: invalid record signature '$value'." }
        [void]$signatures.Add($normalized)
    }
    $origins = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($value in @($OriginPlugin)) {
        if ([string]::IsNullOrWhiteSpace($value) -or [IO.Path]::GetFileName($value) -cne $value) { throw 'RecordCatalogQueryInvalid: origin plugin names must be exact leaf names.' }
        [void]$origins.Add($value)
    }
    $plugins = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($value in @($PluginName)) {
        if ([string]::IsNullOrWhiteSpace($value) -or [IO.Path]::GetFileName($value) -cne $value) { throw 'RecordCatalogQueryInvalid: plugin names must be exact leaf names.' }
        [void]$plugins.Add($value)
    }
    $editorIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($value in @($EditorId)) {
        if ([string]::IsNullOrWhiteSpace($value) -or $value.Length -gt 512 -or $value.IndexOfAny([char[]]@(0,10,13)) -ge 0) { throw 'RecordCatalogQueryInvalid: EditorIDs must be bounded non-empty text.' }
        [void]$editorIds.Add($value)
    }
    $editorContains = @($EditorIdContains | ForEach-Object {
        if ([string]::IsNullOrWhiteSpace($_) -or $_.Length -gt 128 -or $_.IndexOfAny([char[]]@(0,10,13)) -ge 0) { throw 'RecordCatalogQueryInvalid: EditorID fragments must be bounded non-empty text.' }
        [string]$_
    })
    $keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($value in @($CanonicalKey)) {
        if ([string]$value -notmatch '^([^|]+)\|([A-Fa-f0-9]{1,6})$' -or [IO.Path]::GetFileName($Matches[1]) -cne $Matches[1]) {
            throw "RecordCatalogQueryInvalid: canonical key '$value' must be OriginPlugin|LocalFormIdHex."
        }
        [void]$keys.Add(('{0}|{1:X6}' -f $Matches[1], [Convert]::ToUInt32($Matches[2], 16)))
    }
    if ($signatures.Count -eq 0 -and $origins.Count -eq 0 -and $plugins.Count -eq 0 -and
        $editorIds.Count -eq 0 -and $editorContains.Count -eq 0 -and $keys.Count -eq 0) {
        throw 'RecordCatalogQueryInvalid: at least one structured filter is required.'
    }

    $selectedRecords = [Collections.Generic.List[object]]::new()
    $targets = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    $recordsRead = 0L
    $reader = [IO.StreamReader]::new($fullPath, [Text.UTF8Encoding]::new($false, $true), $true, 1MB)
    try {
        while (($line = $reader.ReadLine()) -ne $null) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $recordsRead++
            if ($recordsRead -gt $MaximumRecordsRead) { throw 'RecordCatalogLimit: record count exceeds the configured read bound.' }
            try { $record = $line | ConvertFrom-Json -ErrorAction Stop } catch { throw "RecordCatalogInvalid: malformed NDJSON at retained record $recordsRead." }
            foreach ($required in @('recordSignature','originPlugin','localFormId','pluginName','loadOrder','rawFormId','recordOffset','recordFlags','dataSha256','isOverride')) {
                if (-not $record.PSObject.Properties[$required]) { throw "RecordCatalogInvalid: record $recordsRead omits '$required'." }
            }
            $signature = [string]$record.recordSignature
            $origin = [string]$record.originPlugin
            $plugin = [string]$record.pluginName
            $local = [uint32]$record.localFormId
            $editorProperty = $record.PSObject.Properties['editorId']
            $editor = if ($null -eq $editorProperty -or $null -eq $editorProperty.Value) { $null } else { [string]$editorProperty.Value }
            $key = '{0}|{1:X6}' -f $origin, $local
            $selected = ($signatures.Count -eq 0 -or $signatures.Contains($signature)) -and
                ($origins.Count -eq 0 -or $origins.Contains($origin)) -and
                ($plugins.Count -eq 0 -or $plugins.Contains($plugin)) -and
                ($editorIds.Count -eq 0 -or ($null -ne $editor -and $editorIds.Contains($editor))) -and
                ($keys.Count -eq 0 -or $keys.Contains($key))
            if ($selected -and $editorContains.Count -gt 0) {
                $selected = $null -ne $editor -and @($editorContains | Where-Object { $editor.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count -gt 0
            }
            if (-not $selected) { continue }
            if ($selectedRecords.Count -ge $MaximumMatches) {
                return [pscustomobject][ordered]@{
                    schemaVersion = 1; status = 'Refused'; sourceCoverage = $CatalogStatus
                    sourceSha256 = $actualSha; recordsRead = $recordsRead; matches = @(); targets = @()
                    issues = @("The structured query exceeded MaximumMatches=$MaximumMatches; no partial target set was returned.")
                }
            }
            $normalized = [pscustomobject][ordered]@{
                recordSignature = $signature; originPlugin = $origin; localFormId = $local
                canonicalKey = $key; pluginName = $plugin; loadOrder = if ($null -eq $record.loadOrder) { $null } else { [int]$record.loadOrder }
                rawFormId = [uint32]$record.rawFormId; recordOffset = [long]$record.recordOffset
                recordFlags = [uint32]$record.recordFlags; editorId = $editor
                dataSha256 = ([string]$record.dataSha256).ToUpperInvariant(); isOverride = [bool]$record.isOverride
            }
            if ($normalized.dataSha256 -notmatch '^[A-F0-9]{64}$') { throw "RecordCatalogInvalid: record $recordsRead has an invalid data digest." }
            $selectedRecords.Add($normalized)
            if (-not $targets.ContainsKey($key)) {
                $target = [pscustomobject][ordered]@{ originPlugin = $origin; localFormId = $local; canonicalKey = $key }
                $targets.Add($key, $target)
            }
        }
    }
    finally { $reader.Dispose() }
    if ($recordsRead -ne $ExpectedRecordCount) { throw "RecordCatalogInvalid: expected $ExpectedRecordCount records but read $recordsRead." }

    $orderedMatches = @($selectedRecords | Sort-Object recordSignature, originPlugin, localFormId, loadOrder, pluginName, recordOffset)
    $orderedTargets = @($targets.Values | Sort-Object originPlugin, localFormId)
    $status = if ($CatalogStatus -eq 'Partial') { 'Partial' } elseif ($orderedMatches.Count -eq 0) { 'NoMatch' } else { 'Complete' }
    [pscustomobject][ordered]@{
        schemaVersion = 1; status = $status; sourceCoverage = $CatalogStatus
        sourceSha256 = $actualSha; recordsRead = $recordsRead
        matches = $orderedMatches; targets = $orderedTargets
        issues = if ($CatalogStatus -eq 'Partial') { @('The source catalog is partial; retained positive matches are valid but absence is not established.') } else { @() }
    }
}
