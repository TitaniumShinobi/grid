#requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$healthRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $healthRoot 'Grid.RequestPlanner.ps1')
. (Join-Path $healthRoot 'Grid.RequestExecution.ps1')
. (Join-Path $healthRoot 'Grid.ConnectedGameContext.ps1')

function Assert-Grid([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-GridThrows([scriptblock]$Action, [string]$Pattern) {
    try { & $Action; throw 'Expected the operation to fail.' }
    catch { if ($_.Exception.Message -eq 'Expected the operation to fail.' -or $_.Exception.Message -notmatch $Pattern) { throw } }
}

$fixture = Join-Path ([IO.Path]::GetTempPath()) ('grid-connected-context-' + [guid]::NewGuid().ToString('N'))
try {
    $account = Join-Path $fixture 'accounts\v1\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
    $connections = Join-Path $account 'connections'
    $gameRoot = Join-Path $fixture 'game'
    New-Item -ItemType Directory -Path $connections,$gameRoot -Force | Out-Null
    $executable = Join-Path $gameRoot 'GTA5_Enhanced.exe'
    [IO.File]::WriteAllBytes($executable, [byte[]](1,2,3))
    $installationId = 'installation.game.fixture'
    $profileId = Get-GridRegisteredProfileId -InstallationId $installationId
    $store = [pscustomobject][ordered]@{
        schemaVersion = 1
        registrations = @([pscustomobject][ordered]@{
            schemaVersion = 1
            referenceId = [pscustomobject]@{ value = 'reference.game.fixture' }
            installationId = [pscustomobject]@{ value = $installationId }
            gameId = [pscustomobject]@{ value = 'game.grandtheftautov-enhanced' }
            adapterId = [pscustomobject]@{ value = 'adapter.provider-discovery' }
            displayName = 'GTA V Enhanced'
            edition = 'Enhanced'
            providerId = 'steam'
            installRoot = $gameRoot
            executablePath = $executable
            managerProviderIds = @('vortex')
            registeredAtUtc = '2026-09-23T00:00:00Z'
        })
    }
    [IO.File]::WriteAllText((Join-Path $connections 'game-installations.v1.json'), ($store | ConvertTo-Json -Depth 10))

    $resolved = Resolve-GridConnectedGameContext -GridDataRoot $account -CatalogGameId 'game.grandtheftautov-enhanced' -InstallationId $installationId -ProfileId $profileId
    Assert-Grid ($resolved.catalogGameId -ceq 'game.grandtheftautov-enhanced') 'Catalog identity must be retained.'
    Assert-Grid ($resolved.adapterGameId -ceq 'grandtheftautov') 'Enhanced must map to the shared GTA adapter identity.'
    Assert-Grid ($resolved.edition -ceq 'Enhanced') 'Edition evidence must be retained.'
    Assert-Grid ($resolved.installRoot -ceq [IO.Path]::GetFullPath($gameRoot).TrimEnd('\')) 'The exact registered root must be returned.'
    Assert-Grid ($resolved.contextFingerprint -match '^[A-F0-9]{64}$') 'Context must carry a deterministic fingerprint.'

    Assert-GridThrows {
        Resolve-GridConnectedGameContext -GridDataRoot $account -CatalogGameId 'game.grandtheftautov-legacy' -InstallationId $installationId -ProfileId $profileId
    } 'absent or ambiguous'
    Assert-GridThrows {
        Resolve-GridConnectedGameContext -GridDataRoot $account -CatalogGameId 'game.grandtheftautov-enhanced' -InstallationId $installationId -ProfileId 'profile.grid.wrong'
    } 'not the deterministic profile'

    $globalRoot = Join-Path $fixture 'global'
    New-Item -ItemType Directory -Path (Join-Path $globalRoot 'connections') -Force | Out-Null
    Assert-GridThrows {
        Resolve-GridConnectedGameContext -GridDataRoot $globalRoot -CatalogGameId 'game.grandtheftautov-enhanced' -InstallationId $installationId -ProfileId $profileId
    } 'store is missing'
}
finally {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}

'Connected game context tests passed.'
