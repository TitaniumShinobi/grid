#requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$healthRoot = Split-Path -Parent $PSScriptRoot
$scriptsRoot = Split-Path -Parent $healthRoot
. (Join-Path $healthRoot 'Grid.RequestPlanner.ps1')
. (Join-Path $healthRoot 'Grid.RequestExecution.ps1')
. (Join-Path $healthRoot 'Grid.ConnectedGameContext.ps1')
. (Join-Path $scriptsRoot 'games\skyrimspecialedition\health\collectors\Resolve-GridSkyrimRequestToolInputs.ps1')

function Assert-Grid([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-GridThrows([scriptblock]$Action, [string]$Pattern) {
    try { & $Action; throw 'Expected the operation to fail.' }
    catch { if ($_.Exception.Message -eq 'Expected the operation to fail.' -or $_.Exception.Message -notmatch $Pattern) { throw } }
}

$fixture = Join-Path ([IO.Path]::GetTempPath()) ('grid-authorization-preparation-' + [guid]::NewGuid().ToString('N'))
try {
    $account = Join-Path $fixture 'account'
    $connections = Join-Path $account 'connections'
    New-Item -ItemType Directory -Path $connections -Force | Out-Null

    $missingMo2Root = Join-Path $fixture 'external-missing-mo2'
    $mo2ReferenceId = 'reference.mo2.fixture'
    $profilePath = Join-Path (Join-Path $missingMo2Root 'profiles') 'Fixture Profile'
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($mo2ReferenceId + "`n" + $profilePath)
        $profileId = 'profile.mo2.' + ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','').ToLowerInvariant().Substring(0,24)
    } finally { $sha.Dispose() }
    $mo2Store = [pscustomobject][ordered]@{
        schemaVersion = 1
        references = @([pscustomobject][ordered]@{
            id = $mo2ReferenceId
            installationId = 'installation.mo2.fixture'
            gameId = 'game.skyrim-special-edition'
            adapterId = 'adapter.mod-organizer-2'
            instanceDirectory = $missingMo2Root
            gameDirectory = (Join-Path $fixture 'external-missing-game')
        })
    }
    [IO.File]::WriteAllText((Join-Path $connections 'mo2-installations.v1.json'), ($mo2Store | ConvertTo-Json -Depth 10))
    $preparedMo2 = Resolve-GridSkyrimRequestToolInputs -GridDataRoot $account -InstallationId 'installation.mo2.fixture' -ProfileId $profileId -ToolIds @('grid.tool.mo2') -PrepareAuthorizationScope
    Assert-Grid ([bool]$preparedMo2['grid.tool.mo2'].deferredResolution) 'MO2 authorization preparation must defer manager-owned configuration reads.'
    Assert-Grid (@($preparedMo2['grid.tool.mo2'].authorizedReadPaths) -contains [IO.Path]::GetFullPath($missingMo2Root).TrimEnd('\')) 'MO2 review must still disclose the persisted external root.'
    Assert-GridThrows {
        Resolve-GridSkyrimRequestToolInputs -GridDataRoot $account -InstallationId 'installation.mo2.fixture' -ProfileId $profileId -ToolIds @('grid.tool.mo2') | Out-Null
    } 'ModOrganizer.ini|Could not find'

    $missingGameRoot = Join-Path $fixture 'external-missing-gta'
    $installationId = 'installation.gta.fixture'
    $registeredProfileId = Get-GridRegisteredProfileId -InstallationId $installationId
    $gameStore = [pscustomobject][ordered]@{
        schemaVersion = 1
        registrations = @([pscustomobject][ordered]@{
            schemaVersion = 1
            referenceId = [pscustomobject]@{ value = 'reference.gta.fixture' }
            installationId = [pscustomobject]@{ value = $installationId }
            gameId = [pscustomobject]@{ value = 'game.grandtheftautov-enhanced' }
            adapterId = [pscustomobject]@{ value = 'adapter.provider-discovery' }
            displayName = 'GTA V Enhanced'
            edition = 'Enhanced'
            providerId = 'steam'
            installRoot = $missingGameRoot
            executablePath = (Join-Path $missingGameRoot 'GTA5_Enhanced.exe')
            managerProviderIds = @()
            registeredAtUtc = '2026-09-23T00:00:00Z'
        })
    }
    [IO.File]::WriteAllText((Join-Path $connections 'game-installations.v1.json'), ($gameStore | ConvertTo-Json -Depth 10))
    $preparedGame = Resolve-GridConnectedGameContext -GridDataRoot $account -CatalogGameId 'game.grandtheftautov-enhanced' -InstallationId $installationId -ProfileId $registeredProfileId -PrepareAuthorizationScope
    Assert-Grid ([bool]$preparedGame.externalValidationDeferred) 'Game authorization preparation must defer external installation validation.'
    Assert-GridThrows {
        Resolve-GridConnectedGameContext -GridDataRoot $account -CatalogGameId 'game.grandtheftautov-enhanced' -InstallationId $installationId -ProfileId $registeredProfileId | Out-Null
    } 'unavailable'
}
finally {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}

'Authorization preparation tests passed.'
