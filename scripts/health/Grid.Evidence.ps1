<# .SYNOPSIS Defines normalized, provenance-bearing diagnostic evidence. #>
$script:GridEvidenceSchemaVersion = 1
$script:GridEvidenceVerificationStatuses = @('Collected', 'Verified', 'Contradicted', 'Stale', 'Unverified', 'Unavailable')

function New-GridEvidenceItem {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Parameter,
        [Parameter(Mandatory)][ValidateRange(0.0, 1.0)][double]$Value,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Claim,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$SourceType,
        [string]$SourceIdentifier,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ContextFingerprint,
        [Parameter(Mandatory)][ValidateSet('Collected', 'Verified', 'Contradicted', 'Stale', 'Unverified', 'Unavailable')][string]$VerificationStatus,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$CollectorName,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$CollectorVersion,
        [datetime]$CollectedAt = [datetime]::UtcNow
    )
    [pscustomobject][ordered]@{
        schemaVersion = $script:GridEvidenceSchemaVersion
        evidenceId = 'evidence-' + [guid]::NewGuid().ToString('N')
        parameter = $Parameter
        value = $Value
        claim = $Claim
        sourceType = $SourceType
        sourceIdentifier = if ($SourceIdentifier) { $SourceIdentifier } else { $null }
        collectedAt = $CollectedAt.ToUniversalTime().ToString('o')
        contextFingerprint = $ContextFingerprint
        verificationStatus = $VerificationStatus
        collector = [pscustomobject][ordered]@{ name = $CollectorName; version = $CollectorVersion }
    }
}

function Test-GridEvidenceItem {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Evidence)
    $errors = New-Object Collections.Generic.List[string]
    foreach ($name in @('schemaVersion','evidenceId','parameter','value','claim','sourceType','collectedAt','contextFingerprint','verificationStatus','collector')) {
        if ($null -eq $Evidence.PSObject.Properties[$name]) { $errors.Add("Missing evidence field: $name") }
    }
    if ($errors.Count -eq 0) {
        if ([int]$Evidence.schemaVersion -ne $script:GridEvidenceSchemaVersion) { $errors.Add('Unsupported evidence schemaVersion.') }
        if ([double]$Evidence.value -lt 0 -or [double]$Evidence.value -gt 1) { $errors.Add('Evidence value must be within 0.0-1.0.') }
        if ([string]$Evidence.verificationStatus -notin $script:GridEvidenceVerificationStatuses) { $errors.Add('Invalid evidence verificationStatus.') }
    }
    [pscustomobject]@{ IsValid = ($errors.Count -eq 0); Errors = @($errors) }
}

function Add-GridCaseEvidence {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$CasePath, [Parameter(Mandatory)]$Evidence)
    $validation = Test-GridEvidenceItem -Evidence $Evidence
    if (-not $validation.IsValid) { throw ($validation.Errors -join ' ') }
    $case = Get-Content -LiteralPath $CasePath -Raw | ConvertFrom-Json
    $case.evidence = @($case.evidence) + @($Evidence)
    $case.updatedAt = (Get-Date).ToUniversalTime().ToString('o')
    Write-GridJsonAtomic -InputObject $case -LiteralPath $CasePath
    $case
}

function Get-GridEvidenceFingerprint {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Evidence)
    $canonical = @($Evidence | Sort-Object evidenceId | ForEach-Object {
        '{0}|{1}|{2:R}|{3}|{4}' -f $_.evidenceId, $_.parameter, [double]$_.value, $_.verificationStatus, $_.contextFingerprint
    }) -join "`n"
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical)))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function ConvertTo-GridCanonicalEvidenceValue {
    param($Value)
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [string]) { return ($Value | ConvertTo-Json -Compress) }
    if ($Value -is [bool]) { return $(if ($Value) { 'true' } else { 'false' }) }
    if ($Value -is [byte] -or $Value -is [int16] -or $Value -is [int32] -or $Value -is [int64] -or
        $Value -is [single] -or $Value -is [double] -or $Value -is [decimal]) {
        return [Convert]::ToString($Value, [Globalization.CultureInfo]::InvariantCulture)
    }
    if ($Value -is [Collections.IDictionary]) {
        return '{' + (@($Value.Keys | ForEach-Object { [string]$_ } | Sort-Object | ForEach-Object {
            $key = $_
            ($key | ConvertTo-Json -Compress) + ':' + (ConvertTo-GridCanonicalEvidenceValue -Value $Value[$key])
        }) -join ',') + '}'
    }
    if ($Value -is [Collections.IEnumerable] -and -not ($Value -is [string])) {
        return '[' + (@($Value | ForEach-Object { ConvertTo-GridCanonicalEvidenceValue -Value $_ }) -join ',') + ']'
    }
    $properties = @($Value.PSObject.Properties | Where-Object MemberType -in @('NoteProperty','Property') | Sort-Object Name)
    return '{' + (@($properties | ForEach-Object {
        ($_.Name | ConvertTo-Json -Compress) + ':' + (ConvertTo-GridCanonicalEvidenceValue -Value $_.Value)
    }) -join ',') + '}'
}

function Get-GridSemanticEvidenceFingerprint {
    <#
    .SYNOPSIS
    Fingerprints the meaning of evidence independently of run IDs, paths, and timestamps.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Evidence)

    [string[]]$records = @($Evidence | ForEach-Object {
        $validation = Test-GridEvidenceItem -Evidence $_
        if (-not $validation.IsValid) { throw ($validation.Errors -join ' ') }
        $semantic = [ordered]@{
            parameter = [string]$_.parameter
            value = [double]$_.value
            claim = [string]$_.claim
            sourceType = [string]$_.sourceType
            contextFingerprint = [string]$_.contextFingerprint
            verificationStatus = [string]$_.verificationStatus
            collector = [ordered]@{ name = [string]$_.collector.name; version = [string]$_.collector.version }
            native = if ($null -ne $_.PSObject.Properties['native']) { $_.native } else { $null }
        }
        ConvertTo-GridCanonicalEvidenceValue -Value $semantic
    })
    [Array]::Sort($records, [StringComparer]::Ordinal)
    $canonical = $records -join "`n"
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical)))).Replace('-', '') }
    finally { $sha.Dispose() }
}
