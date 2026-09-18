#requires -Version 5.1
$ErrorActionPreference='Stop'
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
Import-Module (Join-Path $repoRoot 'scripts\health\Grid.Health.psm1') -Force
function Assert-Grid([bool]$Condition,[string]$Message){if(-not$Condition){throw $Message}}
$fixture=Join-Path $env:TEMP ('grid-provider-discovery-'+[guid]::NewGuid().ToString('N'))
$originalLocalAppData=$env:LOCALAPPDATA
try{
    $env:LOCALAPPDATA=Join-Path $fixture 'LocalAppData'
    $steam=Join-Path $fixture 'Steam';$library=Join-Path $fixture 'Library';$legacy=Join-Path $library 'steamapps\common\Grand Theft Auto V';$enhanced=Join-Path $steam 'steamapps\common\Grand Theft Auto V Enhanced'
    $mo2=Join-Path $fixture 'Tools\MO2';$moInstance=Join-Path $fixture 'MO2 Instances\UNDEFEATED';$vortex=Join-Path $fixture 'Apps\Vortex';$vProfiles=Join-Path $fixture 'VortexData\profiles'
    New-Item -ItemType Directory -Path (Join-Path $steam 'steamapps'),$legacy,$enhanced,$mo2,(Join-Path $moInstance 'profiles\Default'),(Join-Path $moInstance 'profiles\UNDEFEATED'),$vortex,(Join-Path $vProfiles 'gta-profile') -Force|Out-Null
    Set-Content -LiteralPath (Join-Path $mo2 'ModOrganizer.exe') -Value mo2;Set-Content -LiteralPath (Join-Path $vortex 'Vortex.exe') -Value vortex;Set-Content -LiteralPath (Join-Path $legacy 'GTA5.exe') -Value legacy;Set-Content -LiteralPath (Join-Path $enhanced 'GTA5_Enhanced.exe') -Value enhanced
    $libEscaped=$library.Replace('\','\\');Set-Content -LiteralPath (Join-Path $steam 'steamapps\libraryfolders.vdf') -Value @('"libraryfolders"','{',' "1"',' {',('  "path" "'+$libEscaped+'"'),' }','}')
    Set-Content -LiteralPath (Join-Path $library 'steamapps\appmanifest_271590.acf') -Value @('"AppState"','{',' "appid" "271590"',' "name" "Grand Theft Auto V Legacy"',' "installdir" "Grand Theft Auto V"','}')
    Set-Content -LiteralPath (Join-Path $steam 'steamapps\appmanifest_3240220.acf') -Value @('"AppState"','{',' "appid" "3240220"',' "name" "Grand Theft Auto V Enhanced"',' "installdir" "Grand Theft Auto V Enhanced"','}')
    $profileConfig='profiles_directory={0}' -f (Join-Path $moInstance 'profiles');Set-Content -LiteralPath (Join-Path $moInstance 'ModOrganizer.ini') -Value @('[Settings]',$profileConfig)
    $processes=@([pscustomobject]@{processId=101;processName='ModOrganizer';executablePath=(Join-Path $mo2 'ModOrganizer.exe')},[pscustomobject]@{processId=102;processName='Vortex';executablePath=(Join-Path $vortex 'Vortex.exe')})
    $steamResult=Get-GridSteamGameCandidates -SteamRoot $steam -GameDefinitionRoot (Join-Path $repoRoot 'scripts\games') -ProcessInventory $processes
    Assert-Grid ($steamResult.status -eq 'CandidatesReadyForReview') 'Steam candidates must be reviewable.';Assert-Grid ($steamResult.candidates.Count -eq 2) 'Both GTA editions must be found.'
    Assert-Grid (@($steamResult.candidates|Where-Object{$_.appId-eq'271590'-and$_.edition-eq'Legacy'-and$_.status-eq'Verified'}).Count-eq 1) 'Legacy identity must be exact.'
    $managers=@(Get-GridModManagerCandidates -Mo2Root $mo2 -Mo2InstanceRoot $moInstance -VortexRoot $vortex -VortexProfileRoot $vProfiles -ProcessInventory $processes -RegistryInventory @())
    Assert-Grid ($managers.Count -eq 2) 'MO2 and Vortex must both be found.';Assert-Grid (@($managers|Where-Object{$_.providerId-eq'mo2'-and$_.running}).Count-eq 1) 'Running MO2 must correlate by executable path.';Assert-Grid (@($managers|Where-Object{$_.providerId-eq'vortex'-and$_.running}).Count-eq 1) 'Running Vortex must correlate by executable path.'
    Assert-Grid (@($managers|Where-Object providerId -eq 'mo2')[0].profiles.Count-eq 2) 'MO2 profiles must enumerate.'
    $duplicate=[pscustomobject]@{providerId='vortex';gameId='grandtheftautov';edition='Legacy';status='Verified';installRoot=$legacy;changedExternalState=$false};$merged=@(Merge-GridGameInstallationCandidates -Candidate (@($steamResult.candidates)+@($duplicate)));Assert-Grid ($merged.Count-eq 2) 'Duplicate provider observations must merge by canonical game root.';Assert-Grid (@($merged|Where-Object edition -eq 'Legacy')[0].providers.Count-eq 2) 'Merged Legacy registration must retain both providers.'
    Assert-Grid ((-not [bool]$steamResult.changedExternalState) -and @($managers|Where-Object{[bool]$_.changedExternalState}).Count -eq 0) 'Discovery must remain read-only.'
    Write-Host 'PASS: Steam, MO2, Vortex, active runtime, GTA editions, profiles, and reconciliation contracts passed.'
}finally{$env:LOCALAPPDATA=$originalLocalAppData;Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue}
