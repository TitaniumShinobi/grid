#requires -Version 5.1

<#
.SYNOPSIS
Creates a bounded, read-only xEdit query package for one Grid case.
.DESCRIPTION
Accepts only the documented query vocabulary. Values are treated as data and
are written beneath the case directory as JSON plus a tab-delimited transport
for the bundled Pascal collector. No xEdit source or MO2 state is modified.
#>
function Get-GridXEditQueryValue {
    param([Parameter(Mandatory)]$Query, [Parameter(Mandatory)][string]$Name)
    if ($Query -is [Collections.IDictionary]) {
        if ($Query.Contains($Name)) { return [string]$Query[$Name] }
        return ''
    }
    $property = $Query.PSObject.Properties[$Name]
    if ($property) { return [string]$property.Value }
    return ''
}

function New-GridXEditQuery {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$Queries,
        [Parameter(Mandatory)][string]$CaseDirectory,
        [Parameter(Mandatory)][string]$ContextFingerprint,
        [int]$MaximumQueries = 64,
        [int]$MaximumPlugins = 256,
        [int]$MaximumTraversalRecords = 250000,
        [int]$MaximumOutputRows = 100000,
        [int]$MaximumOutputBytes = 33554432
    )

    $allowed = @('AuditEslEligibility', 'FindRecordByFormId', 'FindRecordByEditorId', 'TraceOverrides',
        'FindReferencesToBase', 'InspectReferenceState', 'InspectContainingCell',
        'ResolveWinningOverride', 'InspectReferenceLinks', 'InspectVmad',
        'TraceOverrideChain', 'InspectScriptedReference')
    $items = @($Queries)
    if ($items.Count -eq 0) { throw 'At least one explicit xEdit query is required.' }
    if ($items.Count -gt $MaximumQueries) { throw "Query count exceeds the $MaximumQueries-query safety limit." }
    foreach ($limit in @($MaximumPlugins, $MaximumTraversalRecords, $MaximumOutputRows, $MaximumOutputBytes)) {
        if ($limit -lt 1) { throw 'All xEdit query safety limits must be positive.' }
    }

    $normalized = New-Object Collections.Generic.List[object]
    $index = 0
    foreach ($query in $items) {
        $index++
        $operation = Get-GridXEditQueryValue -Query $query -Name operation
        if ($operation -notin $allowed) { throw "Unsupported or mutating xEdit query operation: $operation" }
        $plugin = Get-GridXEditQueryValue -Query $query -Name plugin
        $formId = Get-GridXEditQueryValue -Query $query -Name formId
        $editorId = Get-GridXEditQueryValue -Query $query -Name editorId
        foreach ($value in @($plugin, $formId, $editorId)) {
            if ($value.Length -gt 512 -or $value.IndexOfAny([char[]]@(0, 9, 10, 13)) -ge 0) {
                throw 'xEdit query values cannot contain control characters or exceed 512 characters.'
            }
        }
        if ($plugin -and ([IO.Path]::GetFileName($plugin) -cne $plugin -or [IO.Path]::GetExtension($plugin) -notin @('.esp', '.esm', '.esl'))) {
            throw "Query plugin must be a plugin filename, not a path: $plugin"
        }
        if ($formId -and $formId -notmatch '^(?i)(0x)?[0-9a-f]{8}$') { throw "Invalid eight-digit FormID: $formId" }
        if ($editorId -and $editorId -notmatch '^[A-Za-z0-9_:.\-]{1,256}$') { throw "Invalid EditorID: $editorId" }
        if ($operation -eq 'AuditEslEligibility') {
            if (-not $plugin) { throw 'AuditEslEligibility requires one exact plugin filename.' }
            if ($formId -or $editorId) { throw 'AuditEslEligibility accepts a plugin filename only.' }
        }
        elseif (-not $formId -and -not $editorId) { throw "Query $index requires a FormID or EditorID." }
        if ($operation -eq 'FindRecordByFormId' -and -not $formId) { throw 'FindRecordByFormId requires formId.' }
        if ($operation -eq 'FindRecordByEditorId' -and -not $editorId) { throw 'FindRecordByEditorId requires editorId.' }

        $normalized.Add([ordered]@{
            queryId = ('query-{0:D3}' -f $index)
            operation = $operation
            plugin = $plugin
            formId = $formId
            editorId = $editorId
        })
    }

    $fullCase = [IO.Path]::GetFullPath($CaseDirectory)
    if (-not (Test-Path -LiteralPath $fullCase -PathType Container)) { New-Item -ItemType Directory -Path $fullCase -Force | Out-Null }
    $directory = Join-Path $fullCase 'xedit-query'
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $jsonPath = Join-Path $directory 'query.json'
    $transportPath = Join-Path $directory 'query.tsv'
    $package = [ordered]@{
        schemaVersion = 1
        collector = 'Trace-GridReference'
        collectorVersion = '2.2.0'
        contextFingerprint = $ContextFingerprint
        createdAt = (Get-Date).ToString('o')
        limits = [ordered]@{ maximumPlugins = $MaximumPlugins; maximumTraversalRecords = $MaximumTraversalRecords; maximumOutputRows = $MaximumOutputRows; maximumOutputBytes = $MaximumOutputBytes }
        queries = $normalized.ToArray()
    }
    $package | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
    @('QueryId' + [char]9 + 'Operation' + [char]9 + 'Plugin' + [char]9 + 'FormId' + [char]9 + 'EditorId') + @($normalized | ForEach-Object {
        @($_.queryId, $_.operation, $_.plugin, $_.formId, $_.editorId) -join [char]9
    }) | Set-Content -LiteralPath $transportPath -Encoding UTF8

    [pscustomobject]@{ JsonPath = $jsonPath; TransportPath = $transportPath; Queries = $normalized.ToArray(); Limits = $package.limits; ContextFingerprint = $ContextFingerprint; CollectorVersion = $package.collectorVersion }
}
