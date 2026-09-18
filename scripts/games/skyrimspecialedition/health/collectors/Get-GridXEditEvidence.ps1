#requires -Version 5.1

<#
.SYNOPSIS
Normalizes a generic xEdit TSV report into verified Grid evidence.
#>
function Add-GridXEditEvidenceSubjectRole {
    param(
        [Parameter(Mandatory)][hashtable]$SubjectMap,
        [string]$Name,
        [Parameter(Mandatory)][string]$Role
    )
    if ([string]::IsNullOrWhiteSpace($Name)) { return }
    $key = $Name.ToUpperInvariant()
    if (-not $SubjectMap.ContainsKey($key)) {
        $SubjectMap[$key] = [ordered]@{ name = $Name; kind = 'Plugin'; roles = New-Object Collections.Generic.List[string] }
    }
    if (-not $SubjectMap[$key].roles.Contains($Role)) { $SubjectMap[$key].roles.Add($Role) }
}

function Get-GridXEditEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ReportPath,
        [Parameter(Mandatory)]$QueryPackage,
        [Parameter(Mandatory)][string]$CaseDirectory,
        [Parameter(Mandatory)][string]$ContextFingerprint
    )
    if ([string]$QueryPackage.ContextFingerprint -ne $ContextFingerprint) { throw 'The xEdit query package is stale for the current context fingerprint.' }
    $fullCase = [IO.Path]::GetFullPath($CaseDirectory).TrimEnd('\')
    $fullReport = [IO.Path]::GetFullPath($ReportPath)
    if (-not $fullReport.StartsWith($fullCase + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'The xEdit evidence report must remain beneath the selected case directory.' }
    if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) { throw "xEdit evidence report is missing: $ReportPath" }
    $maximumBytes = [int64]$QueryPackage.Limits.maximumOutputBytes
    if ((Get-Item -LiteralPath $ReportPath).Length -gt $maximumBytes) { throw "xEdit evidence exceeds the $maximumBytes-byte output limit." }
    $rows = @(Import-Csv -LiteralPath $ReportPath -Delimiter ([char]9))
    if ($rows.Count -gt [int]$QueryPackage.Limits.maximumOutputRows) { throw 'xEdit evidence exceeds the configured row limit.' }
    $known = @{}; foreach ($query in @($QueryPackage.Queries)) { $known[[string]$query.queryId] = $query }
    $seen = @{}
    $normalized = New-Object Collections.Generic.List[object]
    foreach ($row in $rows) {
        if (-not $known.ContainsKey([string]$row.QueryId)) { throw "xEdit returned an unknown query identity: $($row.QueryId)" }
        $expectedQuery = $known[[string]$row.QueryId]
        if ([string]$row.Operation -ne [string]$expectedQuery.operation -or [string]$row.Plugin -ne [string]$expectedQuery.plugin) {
            throw "xEdit evidence does not match the bound operation/plugin for query: $($row.QueryId)"
        }
        if ([string]$row.State -notin @('Found', 'NotFound')) { throw "xEdit returned an unsupported evidence state for query $($row.QueryId): $($row.State)" }
        if ([string]::IsNullOrWhiteSpace([string]$row.Claim)) { throw "xEdit returned an empty evidence claim for query: $($row.QueryId)" }
        $seen[[string]$row.QueryId] = $true
        if (-not (Get-Command New-GridEvidenceItem -ErrorAction SilentlyContinue)) { throw 'The shared Grid health module must be imported before normalizing xEdit evidence.' }
        $measuredValue = if ([string]$row.State -eq 'Found') { 1.0 } else { 0.0 }
        $itemArguments = @{
            Parameter = 'xedit.' + ([string]$row.Operation).ToLowerInvariant(); Value = $measuredValue
            Claim = [string]$row.Claim; SourceType = 'DeterministicScriptOutput'; SourceIdentifier = $ReportPath
            ContextFingerprint = $ContextFingerprint; VerificationStatus = 'Verified'
            CollectorName = 'Trace-GridReference'; CollectorVersion = '2.2.0'
        }
        $item = New-GridEvidenceItem @itemArguments
        $subjectMap = @{}
        if ([string]$row.State -eq 'Found') {
            switch ([string]$row.Operation) {
                { $_ -in @('FindRecordByFormId', 'FindRecordByEditorId', 'TraceOverrides', 'ResolveWinningOverride') } {
                    Add-GridXEditEvidenceSubjectRole -SubjectMap $subjectMap -Name ([string]$row.Origin) -Role 'BaseOrMaster'
                    Add-GridXEditEvidenceSubjectRole -SubjectMap $subjectMap -Name ([string]$row.Winner) -Role 'OverrideProvider'
                }
                'AuditEslEligibility' {
                    Add-GridXEditEvidenceSubjectRole -SubjectMap $subjectMap -Name ([string]$row.Plugin) -Role 'EslEligibilitySubject'
                }
                'FindReferencesToBase' {
                    Add-GridXEditEvidenceSubjectRole -SubjectMap $subjectMap -Name ([string]$row.Origin) -Role 'PlacementProvider'
                    Add-GridXEditEvidenceSubjectRole -SubjectMap $subjectMap -Name ([string]$row.Winner) -Role 'OverrideProvider'
                }
                'InspectReferenceState' {
                    Add-GridXEditEvidenceSubjectRole -SubjectMap $subjectMap -Name ([string]$row.Origin) -Role 'PlacementProvider'
                    Add-GridXEditEvidenceSubjectRole -SubjectMap $subjectMap -Name ([string]$row.Winner) -Role 'OverrideProvider'
                }
                'InspectContainingCell' {
                    Add-GridXEditEvidenceSubjectRole -SubjectMap $subjectMap -Name ([string]$row.Origin) -Role 'WorldspaceOrCellEditor'
                    Add-GridXEditEvidenceSubjectRole -SubjectMap $subjectMap -Name ([string]$row.Winner) -Role 'OverrideProvider'
                }
                { $_ -in @('InspectReferenceLinks', 'InspectVmad', 'TraceOverrideChain', 'InspectScriptedReference') } {
                    Add-GridXEditEvidenceSubjectRole -SubjectMap $subjectMap -Name ([string]$row.Origin) -Role 'PlacementProvider'
                    Add-GridXEditEvidenceSubjectRole -SubjectMap $subjectMap -Name ([string]$row.Winner) -Role 'WinningOverrideProvider'
                }
            }
        }
        [string[]]$subjectKeys = @($subjectMap.Keys)
        [Array]::Sort($subjectKeys, [StringComparer]::Ordinal)
        $subjects = @($subjectKeys | ForEach-Object {
            $subject = $subjectMap[$_]
            [pscustomobject][ordered]@{ name = $subject.name; kind = $subject.kind; roles = @($subject.roles) }
        })
        $item | Add-Member -NotePropertyName native -NotePropertyValue ([pscustomobject][ordered]@{
            queryId = $row.QueryId; operation = $row.Operation; plugin = $row.Plugin; formId = $row.FormId
            editorId = $row.EditorId; signature = $row.Signature; origin = $row.Origin; winner = $row.Winner
            state = $row.State; detail = $row.Detail; subjects = $subjects
        })
        $normalized.Add($item)
    }
    foreach ($queryId in $known.Keys) {
        if (-not $seen.ContainsKey($queryId)) { throw "xEdit did not return a measurement for query: $queryId" }
    }
    $path = Join-Path $CaseDirectory 'xedit-evidence.json'
    $result = [ordered]@{ schemaVersion = 1; contextFingerprint = $ContextFingerprint; reportPath = $ReportPath; rowCount = $rows.Count; evidence = $normalized.ToArray() }
    $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $path -Encoding UTF8
    [pscustomobject]@{ Path = $path; Evidence = $normalized.ToArray(); RowCount = $rows.Count }
}
