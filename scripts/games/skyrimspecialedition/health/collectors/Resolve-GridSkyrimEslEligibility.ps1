#requires -Version 5.1

<#
.SYNOPSIS
Reduces one bounded xEdit ESL audit row into a typed eligibility result.
.DESCRIPTION
Accepts only current verified AuditEslEligibility evidence for one exact plugin.
This reducer is read-only and never changes a plugin header or compacts FormIDs.
#>
function Resolve-GridSkyrimEslEligibility {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$Evidence,
        [Parameter(Mandatory)][string]$PluginName,
        [Parameter(Mandatory)][string]$ContextFingerprint,
        [Parameter(Mandatory)][string]$CaseDirectory,
        [string]$OutputPath
    )

    if ([IO.Path]::GetFileName($PluginName) -cne $PluginName -or [IO.Path]::GetExtension($PluginName) -notin @('.esp','.esm','.esl')) {
        throw "EslEligibilityPluginInvalid: '$PluginName' must be one plugin filename."
    }
    $matches = @($Evidence | Where-Object {
        [string]$_.verificationStatus -ceq 'Verified' -and
        [string]$_.contextFingerprint -ceq $ContextFingerprint -and
        [string]$_.native.operation -ceq 'AuditEslEligibility' -and
        [string]$_.native.plugin -ieq $PluginName
    })
    if ($matches.Count -ne 1) { throw "EslEligibilityEvidenceAmbiguous: expected exactly one current verified audit for '$PluginName'." }
    $evidenceItem = $matches[0]
    $pairs = @{}
    foreach ($part in ([string]$evidenceItem.native.detail -split ';')) {
        if ([string]::IsNullOrWhiteSpace($part)) { continue }
        $separator = $part.IndexOf('=')
        if ($separator -lt 1) { throw 'EslEligibilityEvidenceMalformed: detail contains an invalid field.' }
        $name = $part.Substring(0, $separator)
        if ($pairs.ContainsKey($name)) { throw "EslEligibilityEvidenceMalformed: duplicate '$name' field." }
        $pairs[$name] = $part.Substring($separator + 1)
    }
    foreach ($required in @('Eligibility','NewRecordCount','MaximumObjectId','HasNewCell','HasEsmFlag')) {
        if (-not $pairs.ContainsKey($required)) { throw "EslEligibilityEvidenceMalformed: missing '$required' field." }
    }
    $eligibility = [string]$pairs.Eligibility
    if ($eligibility -notin @('AlreadyLight','HeaderFlagOnly','CompactionRequired','IneligibleTooManyNewRecords')) {
        throw "EslEligibilityEvidenceMalformed: unsupported classification '$eligibility'."
    }
    [uint32]$newRecordCount = 0
    if (-not [uint32]::TryParse([string]$pairs.NewRecordCount, [ref]$newRecordCount)) { throw 'EslEligibilityEvidenceMalformed: NewRecordCount is invalid.' }
    [uint32]$maximumObjectId = 0
    if (-not [uint32]::TryParse([string]$pairs.MaximumObjectId, [Globalization.NumberStyles]::HexNumber, [Globalization.CultureInfo]::InvariantCulture, [ref]$maximumObjectId)) { throw 'EslEligibilityEvidenceMalformed: MaximumObjectId is invalid.' }
    if ([string]$pairs.HasNewCell -notin @('true','false') -or [string]$pairs.HasEsmFlag -notin @('true','false')) {
        throw 'EslEligibilityEvidenceMalformed: boolean fields are invalid.'
    }
    $warning = if ($pairs.ContainsKey('Warning')) { [string]$pairs.Warning } else { '' }
    if ($warning -notin @('','EsmWithNewCellEngineRisk')) { throw "EslEligibilityEvidenceMalformed: unsupported warning '$warning'." }
    $status = switch ($eligibility) {
        'AlreadyLight' { 'AlreadyLight' }
        'HeaderFlagOnly' { if ($warning) { 'UnsafeEngineRisk' } else { 'EligibleWithoutCompaction' } }
        'CompactionRequired' { 'CompactionRequired' }
        default { 'Ineligible' }
    }
    $result = [pscustomobject][ordered]@{
        schemaVersion = 1
        status = $status
        pluginName = $PluginName
        classification = $eligibility
        newRecordCount = [long]$newRecordCount
        maximumObjectId = ('{0:X6}' -f $maximumObjectId)
        hasNewCell = ([string]$pairs.HasNewCell -ceq 'true')
        hasEsmFlag = ([string]$pairs.HasEsmFlag -ceq 'true')
        warning = if ($warning) { $warning } else { $null }
        contextFingerprint = $ContextFingerprint
        supportingEvidenceIds = @([string]$evidenceItem.evidenceId)
        observedAt = [string]$evidenceItem.collectedAt
        mutationAuthorized = $false
    }
    $fullCase = [IO.Path]::GetFullPath($CaseDirectory)
    if (-not (Test-Path -LiteralPath $fullCase -PathType Container)) { New-Item -ItemType Directory -Path $fullCase -Force | Out-Null }
    $path = if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        Join-Path $fullCase 'xedit-esl-eligibility.v1.json'
    }
    else {
        $candidate = [IO.Path]::GetFullPath($OutputPath)
        if (-not $candidate.StartsWith($fullCase.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'EslEligibilityOutputBoundaryRefused: output must remain beneath the case directory.'
        }
        $candidate
    }
    $parent = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $path -Encoding UTF8
    [pscustomobject][ordered]@{ Path = $path; Result = $result }
}
