#requires -Version 5.1
<# .SYNOPSIS Dispatches a sealed-baseline root-cause diagnosis to a manifest-bound game capability. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InputPath,
    [ValidateSet('skyrimspecialedition')][string]$Game = 'skyrimspecialedition',
    [string]$CaseStoreRoot,
    [switch]$PassThru
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Grid.Health.psm1') -Force -DisableNameChecking

try {
    if (-not (Test-Path -LiteralPath $InputPath -PathType Leaf)) { throw "RootCauseInputMissing: $InputPath" }
    $item = Get-Item -LiteralPath $InputPath -Force
    if ($item.Length -gt 1MB) { throw 'RootCauseInputTooLarge: the operator input exceeds 1 MiB.' }
    $input = Get-Content -LiteralPath $item.FullName -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    foreach ($name in @('caseId','baselineCaseDirectory','investigationPlan','collectorRequest')) {
        if ($null -eq $input.PSObject.Properties[$name]) { throw "RootCauseInputMalformed: missing '$name'." }
    }
    if ([string]::IsNullOrWhiteSpace($CaseStoreRoot)) { $CaseStoreRoot = Get-GridDiagnosticStoreRoot }
    $adapter = Resolve-GridGameAdapter -ScriptsRoot (Split-Path -Parent $PSScriptRoot) -Request 'root cause diagnosis' -Game $Game
    $manifest = Import-PowerShellDataFile -LiteralPath $adapter.ManifestPath
    $capabilityId = [string]$manifest.RootCauseCapabilityId
    if ([string]::IsNullOrWhiteSpace($capabilityId)) { throw "Adapter '$Game' does not declare RootCauseCapabilityId." }
    $registry = @(Get-GridCapabilityRegistry -ScriptsRoot ([string]$adapter.ScriptsRoot))
    $contract = Resolve-GridCapabilityContract -Registry $registry -CapabilityId $capabilityId
    $module = Import-Module -Name (Join-Path $adapter.AdapterRoot $adapter.Module) -Force -PassThru
    $command = Get-Command -Name ([string]$contract.implementation.entryPoint) -Module $module.Name -ErrorAction Stop
    $result = & $command -CaseId ([string]$input.caseId) -BaselineCaseDirectory ([string]$input.baselineCaseDirectory) `
        -InvestigationPlan $input.investigationPlan -CollectorRequest $input.collectorRequest -CaseStoreRoot $CaseStoreRoot -PassThru
}
catch {
    $result = [pscustomobject][ordered]@{
        schemaVersion = 1; status = 'RootCauseFailed'; caseId = if ($input -and $input.PSObject.Properties['caseId']) { [string]$input.caseId } else { '' }
        caseDirectory = $null; diagnosticResult = $null; patchSpecification = $null
        primaryFailure = [pscustomobject][ordered]@{ code = 'RootCauseDispatchFailed'; failedPrimitive = 'DispatchManifestBoundRootCauseCapability'; detail = $_.Exception.Message }
    }
}
if ($PassThru) { $result } else { $result | ConvertTo-Json -Depth 50 }
