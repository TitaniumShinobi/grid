#requires -Version 5.1

<#!
.SYNOPSIS
Atomically applies one already-authorized MO2 plugin activation batch.
.DESCRIPTION
This subordinate executor is reachable only through
Invoke-GridAuthorizedPluginStateBatch.ps1. It writes only plugins.txt, keeps a
byte-exact before/after image for Undo/Redo, and restores the before image if
any postcondition fails.
#>

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]$AuthorizedSpecification,
    [Parameter(Mandatory)][string]$CaseStoreRoot,
    [Parameter(Mandatory)][string]$TransactionRoot,
    [switch]$PassThru
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$gameRoot = Split-Path -Parent $PSScriptRoot
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $gameRoot)
Import-Module (Join-Path $scriptsRoot 'health\Grid.Health.psm1') -Force -DisableNameChecking
. (Join-Path $PSScriptRoot 'Grid.PluginStateBatch.ps1')

function Test-GridPluginStateBatchProcessesClosed {
    param([Parameter(Mandatory)]$Specification)
    $blocked = @('ModOrganizer','SkyrimSE','skse64_loader','SkyrimSELauncher','SSEEdit','xEdit')
    $mo2Root = ([string]$Specification.paths.mo2Root).TrimEnd('\','/')
    $gameRoot = (Split-Path -Parent ([string]$Specification.paths.gameDataRoot)).TrimEnd('\','/')
    $running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        if ($blocked -notcontains $_.ProcessName) { return $false }
        try { $path = [string]$_.Path } catch { $path = '' }
        [string]::IsNullOrWhiteSpace($path) -or $path.StartsWith($mo2Root + '\',[StringComparison]::OrdinalIgnoreCase) -or
            $path.StartsWith($gameRoot + '\',[StringComparison]::OrdinalIgnoreCase)
    } | Select-Object -ExpandProperty ProcessName -Unique)
    if ($running.Count) { throw ('PluginBatchProcessGateRefused: close the target MO2, Skyrim, and xEdit processes first. Running: ' + (@($running | Sort-Object) -join ', ')) }
}

function Write-GridPluginStateBatchBytesAtomic {
    param([Parameter(Mandatory)][string]$LiteralPath, [Parameter(Mandatory)][byte[]]$Bytes)
    $directory = Split-Path -Parent $LiteralPath
    $temporary = Join-Path $directory ('.grid-plugin-batch-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $stream = New-Object IO.FileStream($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
        try { $stream.Write($Bytes, 0, $Bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        Move-Item -LiteralPath $temporary -Destination $LiteralPath -Force -ErrorAction Stop
    }
    finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue } }
}

$spec = $AuthorizedSpecification
if ([int]$spec.schemaVersion -ne 1 -or [string]$spec.status -ne 'AwaitingAuthorization' -or
    -not $spec.PSObject.Properties['executionStatus'] -or [string]$spec.executionStatus -ne 'Executing') {
    throw 'PluginBatchAuthorizationRequired: specification is not in its one authorized execution transition.'
}
$calculated = Get-GridPluginStateBatchSpecificationHash -Specification $spec
if ([string]$spec.specificationSha256 -cne $calculated) { throw 'PluginBatchSpecificationDigestMismatch: specification changed after review.' }
$seal = Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory ([string]$spec.baseline.caseDirectory)
if (-not $seal.IsValid -or [string]$seal.Manifest.manifestSha256 -cne [string]$spec.baseline.manifestSha256) { throw 'PluginBatchBaselineSealInvalid: sealed evidence changed or is unavailable.' }

$fresh = Get-GridPluginStateBatchInspection -Mo2Root ([string]$spec.paths.mo2Root) -Profile ([string]$spec.paths.profile) `
    -ModsRoot ([string]$spec.paths.modsRoot) -GameDataRoot ([string]$spec.paths.gameDataRoot) -RequestedPlugins @($spec.requestedPlugins) `
    -MaximumFullPlugins ([int]$spec.slotUsage.fullMaximum) -MaximumLightPlugins ([int]$spec.slotUsage.lightMaximum)
[void](Test-GridPluginStateBatchInspectionEquivalent -Expected $spec -Actual $fresh)
Test-GridPluginStateBatchProcessesClosed -Specification $spec
if ($WhatIfPreference -or -not $PSCmdlet.ShouldProcess(([string]$spec.paths.pluginsFile), "Atomically enable $(@($spec.operations).Count) dependency-closed plugins")) {
    $planned = [pscustomobject][ordered]@{ schemaVersion=1; status='Planned'; specificationId=[string]$spec.specificationId; changed=$false; pluginsFile=[string]$spec.paths.pluginsFile }
    if ($PassThru) { return $planned } else { return ($planned | ConvertTo-Json -Depth 12) }
}

$transactionDirectory = [IO.Path]::GetFullPath((Join-Path $TransactionRoot ('plugin-batch-' + [string]$spec.specificationSha256)))
if (Test-Path -LiteralPath $transactionDirectory) { throw "PluginBatchTransactionCollision: $transactionDirectory" }
New-Item -ItemType Directory -Path $transactionDirectory -ErrorAction Stop | Out-Null
$beforePath = Join-Path $transactionDirectory 'plugins.before.bin'
$afterPath = Join-Path $transactionDirectory 'plugins.after.bin'
$intentPath = Join-Path $transactionDirectory 'intent.v1.json'
$receiptPath = Join-Path $transactionDirectory 'receipt.v1.json'
$statePath = Join-Path $transactionDirectory 'history-state.v1.json'
$pluginsFile = [string]$spec.paths.pluginsFile
$beforeDocument = Read-GridPluginStateBatchTextFile -LiteralPath $pluginsFile
if ([string]$beforeDocument.sha256 -cne [string]$spec.protectedState.pluginsFileSha256) { throw 'PluginBatchCurrentStateChanged: plugins.txt changed before backup.' }
[IO.File]::WriteAllBytes($beforePath, [byte[]]$beforeDocument.bytes)
if ((Get-GridPluginStateBatchSha256File $beforePath) -cne [string]$spec.protectedState.pluginsFileSha256) { throw 'PluginBatchBackupVerificationFailed: byte-exact backup digest differs.' }
$updatedText = Set-GridPluginStateBatchText -Text $beforeDocument.text -Operations @($spec.operations)
$afterBytes = ConvertTo-GridPluginStateBatchBytes -Text $updatedText -Encoding ([string]$beforeDocument.encoding)
$afterSha = Get-GridPluginStateBatchSha256Bytes -Bytes $afterBytes
if ($afterSha -cne [string]$spec.protectedState.expectedAfterPluginsFileSha256) { throw 'PluginBatchAfterImageMismatch: recomputed bytes differ from the reviewed specification.' }
[IO.File]::WriteAllBytes($afterPath, [byte[]]$afterBytes)
Write-GridJsonAtomic -InputObject ([pscustomobject][ordered]@{
    schemaVersion=1; state='Intent'; specificationId=[string]$spec.specificationId; specificationSha256=[string]$spec.specificationSha256
    pluginsFile=$pluginsFile; beforeSha256=[string]$spec.protectedState.pluginsFileSha256; afterSha256=$afterSha; recordedAt=[DateTimeOffset]::UtcNow.ToString('o')
}) -LiteralPath $intentPath

try {
    Test-GridPluginStateBatchProcessesClosed -Specification $spec
    if ((Get-GridPluginStateBatchSha256File $pluginsFile) -cne [string]$spec.protectedState.pluginsFileSha256) { throw 'PluginBatchCurrentStateChanged: plugins.txt drifted immediately before commit.' }
    foreach ($pair in @(
        @([string]$spec.paths.modListFile,[string]$spec.protectedState.modListFileSha256),
        @([string]$spec.paths.loadOrderFile,[string]$spec.protectedState.loadOrderFileSha256)
    )) { if ((Get-GridPluginStateBatchSha256File ([string]$pair[0])) -cne [string]$pair[1]) { throw "PluginBatchCurrentStateChanged: $($pair[0])" } }
    $manifest = Get-GridPluginStateBatchCreationManifest -LiteralPath ([string]$spec.paths.creationManifestFile)
    if ([string]$manifest.status -cne [string]$spec.protectedState.creationManifestStatus -or
        [string]$manifest.fingerprint -cne [string]$spec.protectedState.creationManifestFingerprint) {
        throw 'PluginBatchCurrentStateChanged: Skyrim.ccc'
    }
    foreach ($operation in @($spec.operations)) {
        if ((Get-GridPluginStateBatchSha256File ([string]$operation.pluginPath)) -cne [string]$operation.pluginSha256) {
            throw "PluginBatchCurrentStateChanged: winning plugin changed: $($operation.pluginName)"
        }
    }
    Write-GridPluginStateBatchBytesAtomic -LiteralPath $pluginsFile -Bytes $afterBytes
    if ((Get-GridPluginStateBatchSha256File $pluginsFile) -cne $afterSha) { throw 'PluginBatchPostconditionFailed: plugins.txt digest does not match the reviewed after image.' }
    $verified = Read-GridPluginStateBatchTextFile -LiteralPath $pluginsFile
    $entries = @(Get-GridPluginStateBatchEntries -Text $verified.text)
    foreach ($operation in @($spec.operations)) {
        $match = @($entries | Where-Object { [string]$_.name -ieq [string]$operation.pluginName })
        if ($match.Count -ne 1 -or [string]$match[0].state -ne 'Enabled') { throw "PluginBatchPostconditionFailed: $($operation.pluginName) was not enabled." }
    }
}
catch {
    $failure = $_.Exception.Message
    try {
        Write-GridPluginStateBatchBytesAtomic -LiteralPath $pluginsFile -Bytes ([byte[]]$beforeDocument.bytes)
        if ((Get-GridPluginStateBatchSha256File $pluginsFile) -cne [string]$spec.protectedState.pluginsFileSha256) { throw 'rollback digest mismatch' }
    }
    catch { throw "PluginBatchIndeterminate: $failure Rollback failed: $($_.Exception.Message)" }
    throw "PluginBatchRolledBack: $failure"
}

$receipt = [pscustomobject][ordered]@{
    schemaVersion=1; status='Verified'; transactionId=('plugin-batch-' + [string]$spec.specificationSha256); specificationId=[string]$spec.specificationId
    specificationSha256=[string]$spec.specificationSha256; pluginsFile=$pluginsFile; changed=$true
    beforeSha256=[string]$spec.protectedState.pluginsFileSha256; afterSha256=$afterSha; backupPath=$beforePath; redoImagePath=$afterPath
    operations=@($spec.operations | ForEach-Object { [pscustomobject][ordered]@{ sequence=[int]$_.sequence; pluginName=[string]$_.pluginName; beforeState=[string]$_.beforeState; afterState='Enabled' } })
    completedAt=[DateTimeOffset]::UtcNow.ToString('o')
}
Write-GridJsonAtomic -InputObject $receipt -LiteralPath $receiptPath
Write-GridJsonAtomic -InputObject ([pscustomobject][ordered]@{
    schemaVersion=1; transactionId=[string]$receipt.transactionId; specificationSha256=[string]$spec.specificationSha256
    state='Applied'; revision=0; currentPluginsFileSha256=$afterSha; lastReceiptPath=$receiptPath
}) -LiteralPath $statePath
if ($PassThru) { $receipt } else { $receipt | ConvertTo-Json -Depth 20 }
