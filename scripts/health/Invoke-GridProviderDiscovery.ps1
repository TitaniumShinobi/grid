#requires -Version 5.1
[CmdletBinding()]
param(
    [string[]]$SteamRoot,
    [string[]]$Mo2Root,
    [string[]]$Mo2InstanceRoot,
    [string[]]$VortexRoot,
    [string[]]$VortexProfileRoot,
    [string]$OutputPath
)
$ErrorActionPreference='Stop'
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
Import-Module (Join-Path $PSScriptRoot 'Grid.Health.psm1') -Force
$steam=Get-GridSteamGameCandidates -SteamRoot $SteamRoot -GameDefinitionRoot (Join-Path $repoRoot 'scripts\games')
$managers=@(Get-GridModManagerCandidates -Mo2Root $Mo2Root -Mo2InstanceRoot $Mo2InstanceRoot -VortexRoot $VortexRoot -VortexProfileRoot $VortexProfileRoot)
$registrations=@(Merge-GridGameInstallationCandidates -Candidate $steam.candidates)
$result=[pscustomobject][ordered]@{schemaVersion=1;status='ReviewRequired';observedAtUtc=(Get-Date).ToUniversalTime().ToString('o');runningProviders=@(Get-GridRunningProviderProcesses);stores=@($steam);modManagers=$managers;registrationCandidates=$registrations;changedExternalState=$false}
if($OutputPath){Write-GridJsonAtomic -InputObject $result -LiteralPath ([IO.Path]::GetFullPath($OutputPath))}
$result|ConvertTo-Json -Depth 30
