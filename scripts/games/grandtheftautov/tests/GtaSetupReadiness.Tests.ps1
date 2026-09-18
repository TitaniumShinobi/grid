#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $gameRoot 'health\Grid.Health.GtaV.psm1') -Force
function Assert-Grid([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

$fixture = Join-Path $env:TEMP ('grid-gta-readiness-' + [guid]::NewGuid().ToString('N'))
try {
    $legacyRoot = Join-Path $fixture 'Grand Theft Auto V'
    $enhancedRoot = Join-Path $fixture 'Grand Theft Auto V Enhanced'
    New-Item -ItemType Directory -Path (Join-Path $legacyRoot 'scripts'), $enhancedRoot -Force | Out-Null
    foreach ($name in @('GTA5.exe','dinput8.dll','OpenIV.asi','ScriptHookV.dll','Menyoo.asi','ScriptHookVDotNet.asi','ScriptHookVDotNet3.dll')) {
        Set-Content -LiteralPath (Join-Path $legacyRoot $name) -Value 'fixture'
    }
    Set-Content -LiteralPath (Join-Path $enhancedRoot 'GTA5_Enhanced.exe') -Value 'fixture'
    New-Item -ItemType Directory -Path (Join-Path $legacyRoot 'mods'), (Join-Path $legacyRoot 'menyooStuff') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $legacyRoot 'scripts\NativeUI.dll') -Value 'fixture'
    Set-Content -LiteralPath (Join-Path $legacyRoot 'scripts\iFruitAddon2.dll') -Value 'fixture'
    Set-Content -LiteralPath (Join-Path $legacyRoot 'ScriptHookVDotNet.ini') -Value 'ScriptTimeoutThreshold=60000'

    Assert-Grid (Test-GridGtaConnectedContext -GameRoot @($legacyRoot, $enhancedRoot)) 'Either recognized GTA executable must establish connected context.'
    $inventory = Get-GridGtaInstallationInventory -GameRoot $legacyRoot
    Assert-Grid ($inventory.edition -eq 'Legacy') 'GTA5.exe must resolve the fixture as Legacy.'
    Assert-Grid ($inventory.installationId -match '^gta-legacy-[a-f0-9]{16}$') 'Legacy inventory must expose a non-path stable installation ID.'
    Assert-Grid ($inventory.installationId -eq (Get-GridGtaInstallationInventory -GameRoot $legacyRoot).installationId) 'The same exact root and edition must retain its installation ID.'

    $unselectedSet = Get-GridGtaInstallationSet -CandidateRoots @($enhancedRoot, $legacyRoot, $legacyRoot)
    Assert-Grid ($unselectedSet.status -eq 'NeedsSelection') 'Two valid installations must require explicit selection.'
    Assert-Grid ($unselectedSet.installationCount -eq 2) 'Duplicate candidate roots must collapse without collapsing distinct editions.'
    $legacyId = @($unselectedSet.installations | Where-Object edition -eq 'Legacy')[0].installationId
    $selectedSet = Get-GridGtaInstallationSet -CandidateRoots @($legacyRoot, $enhancedRoot) -SelectedInstallationId $legacyId
    Assert-Grid ($selectedSet.status -eq 'Ready' -and $selectedSet.selectedInstallation.edition -eq 'Legacy') 'Exact InstallationId must select only Legacy.'
    $enhancedInventory = @($selectedSet.installations | Where-Object edition -eq 'Enhanced')[0]
    $enhancedScriptHook = @($enhancedInventory.components | Where-Object name -eq 'ScriptHookV')[0]
    Assert-Grid (-not [bool]$enhancedScriptHook.present) 'Enhanced inventory must not inherit Legacy files.'

    $before = Resolve-GridGtaSetupReadiness -Inventory $inventory -Target ForeverTogether -BattleEyeDisabled -MenyooInGameVerified
    Assert-Grid ($before.status -eq 'Incomplete') 'Missing Forever Together must keep readiness incomplete.'
    Assert-Grid ($before.nextAction.id -eq 'mod.forever-together') 'The first unmet step must be Forever Together when dependencies pass.'

    Set-Content -LiteralPath (Join-Path $legacyRoot 'scripts\Dealien_ForeverTogether.dll') -Value 'fixture'
    $readyInventory = Get-GridGtaInstallationInventory -GameRoot $legacyRoot
    $ready = Resolve-GridGtaSetupReadiness -Inventory $readyInventory -Target ForeverTogether -BattleEyeDisabled -MenyooInGameVerified
    Assert-Grid ($ready.status -eq 'Ready') 'A complete fixture with explicit confirmations must be ready.'
    Assert-Grid ($ready.installationId -eq $legacyId) 'Readiness must remain bound to the selected Legacy installation ID.'
    Assert-Grid (-not $ready.changedExternalState) 'Readiness evaluation must remain read-only.'
    $baselineSelection = Invoke-GridGtaBaseline -GameRoot @($legacyRoot, $enhancedRoot) -Target ForeverTogether -BattleEyeDisabled -MenyooInGameVerified
    Assert-Grid ($baselineSelection.Status -eq 'NeedsSelection') 'Baseline must stop before readiness when two installations lack explicit selection.'
    Write-Host 'PASS: GTA V setup readiness contracts passed.'
}
finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}
