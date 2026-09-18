#requires -Version 5.1

<#
.SYNOPSIS
Read-only primitives for an evidence-bound TES4 ESL-header mutation.
#>
Set-StrictMode -Version Latest

function Get-GridEslFlagSpecificationHash {
    param([Parameter(Mandatory)]$Specification)
    if (-not (Get-Command Get-GridCanonicalJsonSha256 -ErrorAction SilentlyContinue)) { throw 'EslFlagHealthModuleRequired: import Grid.Health.psm1 first.' }
    $payload = [pscustomobject][ordered]@{
        schemaVersion=[int]$Specification.schemaVersion; specificationId=[string]$Specification.specificationId; caseId=[string]$Specification.caseId
        contextFingerprint=[string]$Specification.contextFingerprint; evidenceCase=$Specification.evidenceCase; eligibilityEvidence=$Specification.eligibilityEvidence
        paths=$Specification.paths; plugin=$Specification.plugin; protectedState=$Specification.protectedState
        authorization=$Specification.authorization; exclusions=@($Specification.exclusions)
    }
    Get-GridCanonicalJsonSha256 -InputObject $payload
}

function Get-GridEslFlagAfterBytes {
    param([Parameter(Mandatory)][byte[]]$BeforeBytes)
    if ($BeforeBytes.Length -lt 24 -or [Text.Encoding]::ASCII.GetString($BeforeBytes,0,4) -cne 'TES4') { throw 'EslFlagTes4Malformed: missing or truncated TES4 header.' }
    $flags = [BitConverter]::ToUInt32($BeforeBytes,8)
    if (($flags -band 0x200) -ne 0) { throw 'EslFlagAlreadySet: the TES4 ESL flag is already present.' }
    $after = New-Object byte[] $BeforeBytes.Length
    [Array]::Copy($BeforeBytes,$after,$BeforeBytes.Length)
    [Array]::Copy([BitConverter]::GetBytes([uint32]($flags -bor 0x200)),0,$after,8,4)
    $after
}

function Get-GridEslFlagInspection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Mo2Root,
        [Parameter(Mandatory)][string]$Profile,
        [string]$ModsRoot,
        [Parameter(Mandatory)][string]$GameDataRoot,
        [Parameter(Mandatory)][string]$PluginName
    )
    Assert-GridPluginStateBatchLeafName -Name $PluginName
    if ([IO.Path]::GetExtension($PluginName) -ine '.esp') { throw 'EslFlagPluginUnsupported: only an .esp can receive a header-only ESL flag.' }
    $paths = Get-GridPluginStateBatchProfilePaths -Mo2Root $Mo2Root -Profile $Profile -ModsRoot $ModsRoot -GameDataRoot $GameDataRoot
    $modList = Read-GridPluginStateBatchTextFile -LiteralPath $paths.modListFile
    $providers = Get-GridPluginStateBatchProviderMap -Paths $paths -ModListText $modList.text
    if (-not $providers.ContainsKey($PluginName)) { throw "EslFlagProviderMissing: no winning file resolves for '$PluginName'." }
    $provider = $providers[$PluginName]
    $header = Get-GridPluginStateBatchTes4Header -PluginPath ([string]$provider.path)
    if ($header.hasLightFlag) { throw 'EslFlagAlreadySet: the winning plugin is already light flagged.' }
    $beforeBytes = [IO.File]::ReadAllBytes([string]$provider.path)
    $afterBytes = Get-GridEslFlagAfterBytes -BeforeBytes $beforeBytes
    [pscustomobject][ordered]@{
        paths=$paths
        plugin=[pscustomobject][ordered]@{
            name=$PluginName; path=[string]$provider.path; provider=[string]$provider.provider; sizeBytes=[long]$beforeBytes.Length
            beforeSha256=(Get-GridPluginStateBatchSha256Bytes -Bytes $beforeBytes)
            expectedAfterSha256=(Get-GridPluginStateBatchSha256Bytes -Bytes $afterBytes)
            beforeFlags=[uint32][BitConverter]::ToUInt32($beforeBytes,8)
            expectedAfterFlags=[uint32][BitConverter]::ToUInt32($afterBytes,8)
        }
        protectedState=[pscustomobject][ordered]@{
            modListFileSha256=[string]$modList.sha256
            pluginsFileSha256=(Get-GridPluginStateBatchSha256File -LiteralPath $paths.pluginsFile)
            loadOrderFileSha256=(Get-GridPluginStateBatchSha256File -LiteralPath $paths.loadOrderFile)
        }
    }
}

function Test-GridEslFlagInspectionEquivalent {
    param([Parameter(Mandatory)]$Expected,[Parameter(Mandatory)]$Actual)
    foreach($name in @('name','path','provider','sizeBytes','beforeSha256','expectedAfterSha256','beforeFlags','expectedAfterFlags')) {
        if ([string]$Expected.plugin.$name -cne [string]$Actual.plugin.$name) { throw "EslFlagCurrentStateChanged: plugin.$name" }
    }
    foreach($name in @('modListFileSha256','pluginsFileSha256','loadOrderFileSha256')) {
        if ([string]$Expected.protectedState.$name -cne [string]$Actual.protectedState.$name) { throw "EslFlagCurrentStateChanged: protectedState.$name" }
    }
    $true
}
