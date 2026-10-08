#requires -Version 5.1
<#
.SYNOPSIS
Shared helpers for bounded, provenance-bearing Windows evidence collectors.
.DESCRIPTION
This file contains no collection entry point. It normalizes exact executable
identity, hashes immutable source payloads, and materializes the common result
shape used by the Windows Event Log, WER, and running-process collectors.
#>

$script:GridWindowsEvidenceSchemaVersion = 1
$script:GridWindowsEvidenceCollectorVersion = '1.0.0'

function Get-GridWindowsObjectValue {
    param($InputObject, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $InputObject) { return $null }
    if ($InputObject -is [Collections.IDictionary]) {
        foreach ($key in @($InputObject.Keys)) {
            if ([string]$key -ieq $Name) { return $InputObject[$key] }
        }
        return $null
    }
    $property = @($InputObject.PSObject.Properties | Where-Object Name -ieq $Name | Select-Object -First 1)
    if ($property.Count -eq 1) { return $property[0].Value }
    $null
}

function Get-GridWindowsSha256Text {
    param([AllowEmptyString()][string]$Text)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Resolve-GridWindowsExecutableIdentity {
    [CmdletBinding()]
    param(
        [string]$ExecutablePath,
        [string]$ExecutableName
    )
    if ([string]::IsNullOrWhiteSpace($ExecutablePath) -and [string]::IsNullOrWhiteSpace($ExecutableName)) {
        throw 'ExecutablePath or ExecutableName is required.'
    }
    $canonicalPath = $null
    if (-not [string]::IsNullOrWhiteSpace($ExecutablePath)) {
        if ($ExecutablePath.IndexOfAny([char[]]@(0, 10, 13)) -ge 0) { throw 'ExecutablePath contains prohibited control characters.' }
        $canonicalPath = [IO.Path]::GetFullPath($ExecutablePath)
    }
    $resolvedName = if (-not [string]::IsNullOrWhiteSpace($ExecutableName)) {
        [IO.Path]::GetFileName($ExecutableName)
    }
    elseif ($canonicalPath) { [IO.Path]::GetFileName($canonicalPath) }
    else { '' }
    if ([string]::IsNullOrWhiteSpace($resolvedName) -or $resolvedName -ne [IO.Path]::GetFileName($resolvedName) -or
        $resolvedName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
        throw 'ExecutableName must be a bounded file name, not a path.'
    }
    [pscustomobject][ordered]@{
        executableName = $resolvedName
        executablePath = $canonicalPath
    }
}

function Assert-GridWindowsCollectionBounds {
    param(
        [Parameter(Mandatory)][datetime]$SinceUtc,
        [Parameter(Mandatory)][datetime]$UntilUtc,
        [Parameter(Mandatory)][int]$MaximumItems,
        [int]$MaximumRangeDays = 366
    )
    $start = $SinceUtc.ToUniversalTime()
    $end = $UntilUtc.ToUniversalTime()
    if ($start -gt $end) { throw 'SinceUtc must not be later than UntilUtc.' }
    if (($end - $start).TotalDays -gt $MaximumRangeDays) { throw "The collection range cannot exceed $MaximumRangeDays days." }
    if ($MaximumItems -lt 1 -or $MaximumItems -gt 4096) { throw 'The collection item limit must be within 1-4096.' }
}

function ConvertFrom-GridWindowsEventRecord {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Record)
    $values = [ordered]@{}
    $provided = Get-GridWindowsObjectValue $Record 'EventData'
    if ($provided -is [Collections.IDictionary]) {
        foreach ($key in @($provided.Keys | Select-Object -First 64)) {
            $boundedKey = ([string]$key)
            if ($boundedKey.Length -gt 128) { $boundedKey = $boundedKey.Substring(0, 128) }
            $boundedValue = [string]$provided[$key]
            if ($boundedValue.Length -gt 4096) { $boundedValue = $boundedValue.Substring(0, 4096) }
            $values[$boundedKey] = $boundedValue
        }
    }
    elseif ($null -ne $provided) {
        foreach ($property in @($provided.PSObject.Properties | Select-Object -First 64)) {
            $boundedKey = [string]$property.Name
            if ($boundedKey.Length -gt 128) { $boundedKey = $boundedKey.Substring(0, 128) }
            $boundedValue = [string]$property.Value
            if ($boundedValue.Length -gt 4096) { $boundedValue = $boundedValue.Substring(0, 4096) }
            $values[$boundedKey] = $boundedValue
        }
    }

    $xmlText = $null
    if ($values.Count -eq 0 -and $Record.PSObject.Methods['ToXml']) {
        try {
            $xmlText = [string]$Record.ToXml()
            [xml]$xml = $xmlText
            $position = 0
            foreach ($node in @($xml.Event.EventData.Data | Select-Object -First 64)) {
                $name = if ($node.Name) { [string]$node.Name } else { "Value$position" }
                if ($name.Length -gt 128) { $name = $name.Substring(0, 128) }
                $boundedValue = [string]$node.'#text'
                if ($boundedValue.Length -gt 4096) { $boundedValue = $boundedValue.Substring(0, 4096) }
                $values[$name] = $boundedValue
                $position++
            }
        }
        catch { $xmlText = $null }
    }

    [pscustomobject][ordered]@{
        id = [int](Get-GridWindowsObjectValue $Record 'Id')
        providerName = [string](Get-GridWindowsObjectValue $Record 'ProviderName')
        recordId = [string](Get-GridWindowsObjectValue $Record 'RecordId')
        timeCreated = Get-GridWindowsObjectValue $Record 'TimeCreated'
        eventData = $values
        sourceSha256 = if ($xmlText) { Get-GridWindowsSha256Text $xmlText } else { Get-GridWindowsSha256Text ($Record | ConvertTo-Json -Depth 8 -Compress) }
    }
}

function Test-GridWindowsEventExecutableMatch {
    param(
        [Parameter(Mandatory)]$NormalizedRecord,
        [Parameter(Mandatory)][string]$ExecutableName
    )
    $identityFields = @(
        'AppName', 'ApplicationName', 'FaultingApplicationName', 'HungApplicationName',
        'ProcessName', 'ExecutableName', 'P1', 'Value0'
    )
    foreach ($field in $identityFields) {
        $candidate = Get-GridWindowsObjectValue $NormalizedRecord.eventData $field
        if (-not [string]::IsNullOrWhiteSpace([string]$candidate) -and [IO.Path]::GetFileName([string]$candidate) -ieq $ExecutableName) { return $true }
    }
    $false
}

function New-GridWindowsCollectorEvidence {
    param(
        [Parameter(Mandatory)][string]$Parameter,
        [Parameter(Mandatory)][ValidateRange(0.0, 1.0)][double]$Value,
        [Parameter(Mandatory)][string]$Claim,
        [Parameter(Mandatory)][string]$SourceType,
        [string]$SourceIdentifier,
        [Parameter(Mandatory)][string]$ContextFingerprint,
        [Parameter(Mandatory)][ValidateSet('Collected','Verified','Contradicted','Stale','Unverified','Unavailable')][string]$VerificationStatus,
        [Parameter(Mandatory)][string]$CollectorName,
        [Parameter(Mandatory)]$Native,
        [datetime]$CollectedAt = [datetime]::UtcNow
    )
    $item = New-GridEvidenceItem -Parameter $Parameter -Value $Value -Claim $Claim -SourceType $SourceType `
        -SourceIdentifier $SourceIdentifier -ContextFingerprint $ContextFingerprint -VerificationStatus $VerificationStatus `
        -CollectorName $CollectorName -CollectorVersion $script:GridWindowsEvidenceCollectorVersion -CollectedAt $CollectedAt
    $item | Add-Member -NotePropertyName native -NotePropertyValue $Native
    $item
}

function New-GridWindowsEvidenceCollectionResult {
    param(
        [Parameter(Mandatory)][string]$CollectorName,
        [Parameter(Mandatory)][ValidateSet('Complete','Partial','Unavailable')][string]$Status,
        [Parameter(Mandatory)]$Scope,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Evidence,
        [string[]]$Warnings = @(),
        [datetime]$StartedAt = [datetime]::UtcNow,
        [datetime]$CompletedAt = [datetime]::UtcNow
    )
    [pscustomobject][ordered]@{
        schemaVersion = $script:GridWindowsEvidenceSchemaVersion
        collectionId = 'collection-' + [guid]::NewGuid().ToString('N')
        collector = [pscustomobject][ordered]@{ name = $CollectorName; version = $script:GridWindowsEvidenceCollectorVersion }
        status = $Status
        scope = $Scope
        evidence = @($Evidence)
        warnings = @($Warnings)
        startedAt = $StartedAt.ToUniversalTime().ToString('o')
        completedAt = $CompletedAt.ToUniversalTime().ToString('o')
    }
}
