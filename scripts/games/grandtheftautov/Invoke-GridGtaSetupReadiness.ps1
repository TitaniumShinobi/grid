#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$GameRoot,
    [string]$InstallationId,
    [ValidateSet('Foundation','ForeverTogether')][string]$Target = 'ForeverTogether',
    [switch]$BattleEyeDisabled,
    [switch]$MenyooInGameVerified,
    [switch]$AsJson
)
$ErrorActionPreference = 'Stop'
$modulePath = Join-Path $PSScriptRoot 'health\Grid.Health.GtaV.psm1'
Import-Module $modulePath -Force
$result = Invoke-GridGtaBaseline -GameRoot $GameRoot -InstallationId $InstallationId -Target $Target -BattleEyeDisabled:$BattleEyeDisabled -MenyooInGameVerified:$MenyooInGameVerified

if ($AsJson) { $result | ConvertTo-Json -Depth 12; return }
if ($result.Status -eq 'NeedsSelection') {
    Write-Host 'More than one GTA V installation was found. Rerun with -InstallationId and one of:'
    foreach ($installation in $result.InstallationSet.installations) {
        Write-Host ('  {0}  [{1}]  {2}' -f $installation.installationId, $installation.edition, $installation.rootPath)
    }
    return
}
if ($result.Status -in @('NoInstallations','SelectionNotFound')) {
    Write-Host ('GTA V setup: ' + $result.Status)
    return
}
Write-Host "GTA V $($result.Inventory.edition) setup: $($result.Status)"
foreach ($check in $result.Readiness.checks) { Write-Host ('[{0}] {1}' -f $check.state, $check.label) }
if ($result.Readiness.nextAction) {
    Write-Host ''
    Write-Host ('Next: ' + $result.Readiness.nextAction.instruction)
}
