#requires -Version 5.1
function Resolve-GridGtaSetupReadiness {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Inventory,
        [ValidateSet('Foundation','ForeverTogether')][string]$Target = 'ForeverTogether',
        [switch]$BattleEyeDisabled,
        [switch]$MenyooInGameVerified
    )

    $byName = @{}
    foreach ($component in @($Inventory.components)) { $byName[[string]$component.name] = $component }
    function Test-Present([string]$Name) { return $byName.ContainsKey($Name) -and [bool]$byName[$Name].present }
    function New-Check([string]$Id, [string]$Label, [string]$State, [string]$Instruction) {
        [pscustomobject]@{ id = $Id; label = $Label; state = $State; instruction = $Instruction }
    }

    $checks = @()
    $checks += New-Check 'game.edition' 'GTA V Legacy installation' $(if ($Inventory.edition -eq 'Legacy') {'Satisfied'} else {'Blocked'}) 'Select the Legacy folder containing GTA5.exe.'
    $checks += New-Check 'safety.battleye' 'BattlEye disabled for Story Mode' $(if ($BattleEyeDisabled) {'OperatorConfirmed'} else {'NeedsConfirmation'}) 'Disable BattlEye in Rockstar Launcher or use -nobattleye; never enter official GTA Online with this installation.'
    $checks += New-Check 'openiv.mods-folder' 'OpenIV mods folder' $(if ($Inventory.modsFolderPresent) {'Satisfied'} else {'Missing'}) 'Create the mods folder through OpenIV before installing archive replacements.'
    $checks += New-Check 'loader.asi' 'ASI Loader' $(if (Test-Present 'AsiLoader') {'Satisfied'} else {'Missing'}) 'Install ASI Loader from OpenIV ASI Manager.'
    $checks += New-Check 'loader.openiv' 'OpenIV.ASI' $(if (Test-Present 'OpenIvAsi') {'Satisfied'} else {'Missing'}) 'Install OpenIV.ASI from OpenIV ASI Manager.'
    $checks += New-Check 'runtime.scripthookv' 'Script Hook V' $(if (Test-Present 'ScriptHookV') {'Satisfied'} else {'Missing'}) 'Place the current ScriptHookV.dll beside GTA5.exe.'
    $menyooFiles = (Test-Present 'Menyoo') -and [bool]$Inventory.menyooDataPresent
    $checks += New-Check 'trainer.menyoo-files' 'Menyoo files' $(if ($menyooFiles) {'Satisfied'} else {'Missing'}) 'Place Menyoo.asi and menyooStuff beside GTA5.exe.'
    $checks += New-Check 'trainer.menyoo-test' 'Menyoo in-game test' $(if ($MenyooInGameVerified) {'OperatorConfirmed'} else {'NeedsConfirmation'}) 'Launch Story Mode and confirm that F8 opens Menyoo.'

    if ($Target -eq 'ForeverTogether') {
        $shvdn = (Test-Present 'ScriptHookVDotNetAsi') -and (Test-Present 'ScriptHookVDotNet3')
        $checks += New-Check 'runtime.shvdn3' 'ScriptHookVDotNet 3' $(if ($shvdn) {'Satisfied'} else {'Missing'}) 'Install ScriptHookVDotNet.asi and ScriptHookVDotNet3.dll in the GTA V root.'
        $checks += New-Check 'dependency.nativeui' 'NativeUI' $(if (Test-Present 'NativeUI') {'Satisfied'} else {'Missing'}) 'Place NativeUI.dll in the scripts folder.'
        $checks += New-Check 'dependency.ifruitaddon2' 'iFruitAddon2' $(if (Test-Present 'iFruitAddon2') {'Satisfied'} else {'Missing'}) 'Place iFruitAddon2.dll in the scripts folder.'
        $checks += New-Check 'config.script-timeout' 'Script timeout threshold' $(if ($Inventory.scriptTimeoutThreshold.status -eq 'Satisfied') {'Satisfied'} else {'MissingOrIncorrect'}) 'Set ScriptTimeoutThreshold=60000 in ScriptHookVDotNet.ini.'
        $checks += New-Check 'mod.forever-together' 'Forever Together' $(if (Test-Present 'ForeverTogether') {'Satisfied'} else {'Missing'}) 'After every dependency passes, place the Forever Together archive contents in the scripts folder.'
    }

    $firstAction = @($checks | Where-Object { $_.state -notin @('Satisfied','OperatorConfirmed') } | Select-Object -First 1)
    $status = if (@($checks | Where-Object state -eq 'Blocked').Count -gt 0) { 'Blocked' } elseif ($firstAction.Count -eq 0) { 'Ready' } else { 'Incomplete' }
    [pscustomobject]@{
        schemaVersion = 1; gameId = 'grandtheftautov'; installationId = [string]$Inventory.installationId; target = $Target; status = $status
        rootPath = [string]$Inventory.rootPath; edition = [string]$Inventory.edition
        checks = @($checks); nextAction = if ($firstAction.Count) { $firstAction[0] } else { $null }
        changedExternalState = $false
    }
}
