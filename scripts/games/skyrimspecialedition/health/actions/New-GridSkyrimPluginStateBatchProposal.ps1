#requires -Version 5.1

<#!
.SYNOPSIS
Builds an inert, sealed-evidence-bound MO2 plugin activation batch.
.DESCRIPTION
The planner accepts exact plugin identities, proves that each requested plugin
is a disabled SPID source in the sealed baseline, recomputes its complete TES4
master closure from current winning files, and binds the exact before/after
plugins.txt digests. It never mutates the MO2 profile.
#>

function New-GridSkyrimPluginStateBatchProposal {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseStoreRoot,
        [Parameter(Mandatory)][string]$BaselineCaseDirectory,
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ContextFingerprint,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$EvidenceFingerprint,
        [Parameter(Mandatory)][string]$Mo2Root,
        [Parameter(Mandatory)][string]$Profile,
        [string]$ModsRoot,
        [Parameter(Mandatory)][string]$GameDataRoot,
        [Parameter(Mandatory)][string[]]$RequestedPlugins,
        [ValidateRange(1,254)][int]$MaximumFullPlugins = 254,
        [ValidateRange(1,4096)][int]$MaximumLightPlugins = 4096,
        [Parameter(Mandatory)][string]$OutputPath,
        [switch]$PassThru
    )
    $ErrorActionPreference = 'Stop'
    if (-not (Get-Command Test-GridDiagnosticCaseSeal -ErrorAction SilentlyContinue)) {
        throw 'PluginBatchHealthModuleRequired: import Grid.Health.psm1 before planning.'
    }
    $gameRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    . (Join-Path $gameRoot 'mo2\Grid.PluginStateBatch.ps1')
    $seal = Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory $BaselineCaseDirectory
    if (-not $seal.IsValid) { throw ('PluginBatchBaselineSealInvalid: ' + (@($seal.Errors) -join ' ')) }
    if (@($seal.Manifest.runs | Where-Object state -eq 'Completed').Count -eq 0) { throw 'PluginBatchBaselineIncomplete: no completed sealed run exists.' }

    $sealedCaseDirectory = [IO.Path]::GetFullPath($BaselineCaseDirectory)
    $protectedPath = Join-Path $sealedCaseDirectory 'snapshots\protected-after.v1.json'
    $spidPath = Join-Path $sealedCaseDirectory 'inventory\spid-source-issues.v1.ndjson'
    if (-not (Test-Path -LiteralPath $protectedPath -PathType Leaf) -or -not (Test-Path -LiteralPath $spidPath -PathType Leaf)) {
        throw 'PluginBatchBaselineEvidenceMissing: protected-state or SPID-source evidence is absent.'
    }
    $inspection = Get-GridPluginStateBatchInspection -Mo2Root $Mo2Root -Profile $Profile -ModsRoot $ModsRoot -GameDataRoot $GameDataRoot `
        -RequestedPlugins $RequestedPlugins -MaximumFullPlugins $MaximumFullPlugins -MaximumLightPlugins $MaximumLightPlugins
    $snapshot = Get-Content -LiteralPath $protectedPath -Raw | ConvertFrom-Json -ErrorAction Stop
    $expectedProtected = New-Object 'Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @($snapshot.files)) { $expectedProtected[[IO.Path]::GetFullPath([string]$file.path)] = ([string]$file.sha256).ToUpperInvariant() }
    foreach ($pair in @(
        @([string]$inspection.paths.pluginsFile, [string]$inspection.protectedState.pluginsFileSha256),
        @([string]$inspection.paths.modListFile, [string]$inspection.protectedState.modListFileSha256),
        @([string]$inspection.paths.loadOrderFile, [string]$inspection.protectedState.loadOrderFileSha256)
    )) {
        $path = [IO.Path]::GetFullPath([string]$pair[0]); $current = ([string]$pair[1]).ToUpperInvariant()
        if (-not $expectedProtected.ContainsKey($path) -or $expectedProtected[$path] -cne $current) {
            throw "PluginBatchBaselineStale: protected profile source changed since the sealed baseline: $path"
        }
    }

    $issues = New-Object Collections.Generic.List[object]
    foreach ($line in [IO.File]::ReadLines($spidPath)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $issue = $line | ConvertFrom-Json -ErrorAction Stop
        if ([string]$issue.status -ieq 'disabled' -and @($RequestedPlugins | Where-Object { [string]$_ -ieq [string]$issue.pluginName }).Count -gt 0) { $issues.Add($issue) }
    }
    $support = New-Object Collections.Generic.List[string]
    foreach ($requested in @($inspection.requestedPlugins)) {
        $matches = @($issues | Where-Object { [string]$_.pluginName -ieq [string]$requested })
        if ($matches.Count -ne 1) { throw "PluginBatchEvidenceInsufficient: '$requested' is not exactly one disabled SPID source in the sealed baseline." }
        $support.Add('spid-source:' + (Get-GridCanonicalJsonSha256 -InputObject $matches[0]))
    }
    $support.Add('baseline-manifest:' + ([string]$seal.Manifest.manifestSha256).ToUpperInvariant())
    foreach ($operation in @($inspection.operations)) { $support.Add('tes4-header:' + [string]$operation.pluginSha256) }
    $supportingEvidenceIds = @($support | Sort-Object -Unique)

    $normalizedInspection = [pscustomobject][ordered]@{
        paths = $inspection.paths; requestedPlugins = @($inspection.requestedPlugins); dependencyClosure = @($inspection.dependencyClosure)
        operations = @($inspection.operations); protectedState = $inspection.protectedState; slotUsage = $inspection.slotUsage
    }
    $identitySeed = [pscustomobject][ordered]@{
        schemaVersion = 1; caseId = $CaseId; baselineManifestSha256 = ([string]$seal.Manifest.manifestSha256).ToUpperInvariant()
        contextFingerprint = $ContextFingerprint.ToUpperInvariant(); evidenceFingerprint = $EvidenceFingerprint.ToUpperInvariant()
        inspection = $normalizedInspection; supportingEvidenceIds = $supportingEvidenceIds
    }
    $specificationId = 'plugin-batch-' + (Get-GridCanonicalJsonSha256 -InputObject $identitySeed).Substring(0, 24).ToLowerInvariant()
    $specification = [pscustomobject][ordered]@{
        schemaVersion = 1; specificationId = $specificationId; specificationSha256 = $null; caseId = $CaseId
        baseline = [pscustomobject][ordered]@{ caseDirectory = $sealedCaseDirectory; manifestSha256 = ([string]$seal.Manifest.manifestSha256).ToUpperInvariant() }
        contextFingerprint = $ContextFingerprint.ToUpperInvariant(); evidenceFingerprint = $EvidenceFingerprint.ToUpperInvariant()
        supportingEvidenceIds = $supportingEvidenceIds; paths = $inspection.paths; requestedPlugins = @($inspection.requestedPlugins)
        dependencyClosure = @($inspection.dependencyClosure); operations = @($inspection.operations)
        protectedState = $inspection.protectedState; slotUsage = $inspection.slotUsage
        authorization = [pscustomobject][ordered]@{ requirement = 'Fresh explicit durable one-use authorization bound to this exact batch.'; status = 'Required' }
        exclusions = @('No plugin binary, mod directory, loadorder.txt, modlist.txt, save, or game file mutation.')
        status = 'AwaitingAuthorization'; createdAt = [DateTimeOffset]::UtcNow.ToString('o')
    }
    $specification.specificationSha256 = Get-GridPluginStateBatchSpecificationHash -Specification $specification
    $fullOutput = [IO.Path]::GetFullPath($OutputPath)
    Write-GridJsonAtomic -InputObject $specification -LiteralPath $fullOutput
    $result = [pscustomobject][ordered]@{
        schemaVersion = 1; status = 'AwaitingAuthorization'; specification = $specification; specificationPath = $fullOutput
        authorizationRequired = $true; changedExternalState = $false
    }
    if ($PassThru) { $result } else { $result | ConvertTo-Json -Depth 30 }
}
