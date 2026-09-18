#requires -Version 5.1
Set-StrictMode -Version Latest

function ConvertFrom-GridProviderKeyValueText {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$LiteralPath)
    $values=@{}
    foreach($line in @(Get-Content -LiteralPath $LiteralPath -ErrorAction Stop)){
        if($line -match '^\s*"([^"]+)"\s+"([^"]*)"\s*$'){$values[[string]$matches[1]]=[string]$matches[2]}
        elseif($line -match '^\s*([^;#][^=]+?)\s*=\s*(.*)\s*$'){$values[([string]$matches[1]).Trim()]=([string]$matches[2]).Trim()}
    }
    $values
}

function Get-GridRegisteredProviderExecutables {
    [CmdletBinding()]
    param($RegistryInventory)
    if($null-ne $RegistryInventory){return @($RegistryInventory)}
    $items=@();$locations=@(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    foreach($location in $locations){foreach($entry in @(Get-ItemProperty -Path $location -ErrorAction SilentlyContinue)){
        $displayNameProperty=$entry.PSObject.Properties['DisplayName']
        if($null-eq$displayNameProperty){continue}
        $name=[string]$displayNameProperty.Value;if($name-notmatch'(?i)Mod Organizer|Vortex'){continue};$provider=if($name-match'(?i)Vortex'){'vortex'}else{'mo2'};$paths=@()
        $installLocationProperty=$entry.PSObject.Properties['InstallLocation']
        if(($null -ne $installLocationProperty) -and (-not [string]::IsNullOrWhiteSpace([string]$installLocationProperty.Value))){$paths+=Join-Path ([string]$installLocationProperty.Value) $(if($provider-eq'vortex'){'Vortex.exe'}else{'ModOrganizer.exe'})}
        $displayIconProperty=$entry.PSObject.Properties['DisplayIcon']
        if(($null -ne $displayIconProperty) -and (-not [string]::IsNullOrWhiteSpace([string]$displayIconProperty.Value))){$paths+=(([string]$displayIconProperty.Value)-replace ',\d+$','').Trim('"')}
        foreach($path in @($paths|Sort-Object -Unique)){if(Test-Path -LiteralPath $path -PathType Leaf){$items+=[pscustomobject]@{providerId=$provider;executablePath=[IO.Path]::GetFullPath($path);source='WindowsRegistration'}}}
    }}
    @($items|Sort-Object providerId,executablePath -Unique)
}

function Get-GridRunningProviderProcesses {
    [CmdletBinding()]
    param($ProcessInventory)
    $inventory=if($null-ne $ProcessInventory){@($ProcessInventory)}else{@(Get-Process -ErrorAction SilentlyContinue|ForEach-Object{
        $path=$null;try{$path=$_.Path}catch{}
        [pscustomobject]@{processId=$_.Id;processName=$_.ProcessName;executablePath=$path}
    })}
    $recognized=@()
    foreach($item in $inventory){
        $name=[IO.Path]::GetFileNameWithoutExtension([string]$item.processName)
        $provider=switch -Regex ($name){'^steam$'{'steam';break}'^ModOrganizer$'{'mo2';break}'^Vortex$'{'vortex';break}default{$null}}
        if($provider){$recognized+=[pscustomobject]@{providerId=$provider;processId=[int]$item.processId;processName=[string]$item.processName;executablePath=[string]$item.executablePath;running=$true}}
    }
    @($recognized|Sort-Object providerId,processId)
}

function Get-GridSteamClientRoots {
    [CmdletBinding()]
    param([string[]]$CandidateRoot,$ProcessInventory)
    $roots=@($CandidateRoot)
    foreach($process in @(Get-GridRunningProviderProcesses -ProcessInventory $ProcessInventory|Where-Object providerId -eq 'steam')){
        if($process.executablePath){$roots+=Split-Path -Parent ([string]$process.executablePath)}
    }
    if(@($roots|Where-Object{$_}).Count-eq 0){
        foreach($location in @('HKCU:\Software\Valve\Steam','HKLM:\SOFTWARE\WOW6432Node\Valve\Steam','HKLM:\SOFTWARE\Valve\Steam')){
            try{$p=Get-ItemProperty -LiteralPath $location -ErrorAction Stop;foreach($n in @('SteamPath','InstallPath')){if($p.PSObject.Properties[$n]){$roots+=[string]$p.$n}}}catch{}
        }
    }
    @($roots|Where-Object{-not[string]::IsNullOrWhiteSpace([string]$_)}|ForEach-Object{try{[IO.Path]::GetFullPath([string]$_)}catch{}}|Where-Object{$_-and(Test-Path -LiteralPath $_ -PathType Container)}|Sort-Object -Unique)
}

function Get-GridSteamLibraryRoots {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$SteamRoot)
    $root=[IO.Path]::GetFullPath($SteamRoot);$libraries=@($root);$file=Join-Path $root 'steamapps\libraryfolders.vdf'
    if(Test-Path -LiteralPath $file -PathType Leaf){foreach($line in @(Get-Content -LiteralPath $file)){if($line-match '^\s*"path"\s+"([^"]+)"'){$candidate=([string]$matches[1]).Replace('\\','\');try{$libraries+=[IO.Path]::GetFullPath($candidate)}catch{}}}}
    @($libraries|Where-Object{Test-Path -LiteralPath (Join-Path $_ 'steamapps') -PathType Container}|Sort-Object -Unique)
}

function Get-GridSteamGameCandidates {
    [CmdletBinding()]
    param([string[]]$SteamRoot,[Parameter(Mandatory)][string]$GameDefinitionRoot,$ProcessInventory)
    $definitions=@()
    foreach($file in @(Get-ChildItem -LiteralPath ([IO.Path]::GetFullPath($GameDefinitionRoot)) -Filter 'discovery.steam.v1.json' -File -Recurse|Sort-Object FullName)){
        $d=Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json
        if([int]$d.schemaVersion-ne 1-or[string]$d.providerId-cne'steam'){throw "SteamDefinitionInvalid: $($file.FullName)"}
        foreach($a in @($d.applications)){$definitions+=[pscustomobject]@{game=$d;app=$a;path=$file.FullName}}
    }
    $clients=@(Get-GridSteamClientRoots -CandidateRoot $SteamRoot -ProcessInventory $ProcessInventory);$libraries=@();foreach($client in $clients){$libraries+=Get-GridSteamLibraryRoots -SteamRoot $client};$libraries=@($libraries|Sort-Object -Unique);$items=@()
    foreach($library in $libraries){foreach($entry in $definitions){
        $appId=[string]$entry.app.appId;$manifestPath=Join-Path $library "steamapps\appmanifest_$appId.acf";if(-not(Test-Path -LiteralPath $manifestPath -PathType Leaf)){continue}
        $manifest=ConvertFrom-GridProviderKeyValueText -LiteralPath $manifestPath
        if(-not$manifest.ContainsKey('appid')-or[string]$manifest.appid-cne$appId-or-not$manifest.ContainsKey('installdir')){$items+=[pscustomobject]@{providerId='steam';gameId=[string]$entry.game.gameId;edition=[string]$entry.app.edition;status='InvalidManifest';appId=$appId;manifestPath=$manifestPath;installRoot=$null;profiles=@();changedExternalState=$false};continue}
        $commonRoot = Join-Path $library 'steamapps\common'
        $install = [IO.Path]::GetFullPath((Join-Path $commonRoot ([string]$manifest['installdir'])))
        $required = @($entry.app.requiredFiles | ForEach-Object { Join-Path $install ([string]$_) })
        $conflicts = @($entry.app.conflictingFiles | ForEach-Object { Join-Path $install ([string]$_) })
        $missing=@($required|Where-Object{-not(Test-Path -LiteralPath $_ -PathType Leaf)});$present=@($conflicts|Where-Object{Test-Path -LiteralPath $_ -PathType Leaf});$status=if(-not(Test-Path -LiteralPath $install -PathType Container)){'InstallRootMissing'}elseif($missing.Count){'RequiredFilesMissing'}elseif($present.Count){'EditionAmbiguous'}else{'Verified'}
        $items+=[pscustomobject][ordered]@{schemaVersion=1;candidateKind='GameInstallation';providerId='steam';gameId=[string]$entry.game.gameId;gameDisplayName=[string]$entry.game.displayName;edition=[string]$entry.app.edition;status=$status;appId=$appId;manifestPath=$manifestPath;installRoot=$install;executablePath=if($required.Count){$required[0]}else{$null};profiles=@();missingRequiredFiles=$missing;conflictingFiles=$present;changedExternalState=$false}
    }}
    [pscustomobject]@{schemaVersion=1;providerId='steam';status=if(-not$clients.Count){'ProviderNotFound'}elseif(@($items|Where-Object status -eq 'Verified').Count){'CandidatesReadyForReview'}else{'NoVerifiedCandidates'};providerRoots=$clients;libraryRoots=$libraries;candidates=@($items);changedExternalState=$false}
}

function Get-GridModManagerCandidates {
    [CmdletBinding()]
    param([string[]]$Mo2Root,[string[]]$VortexRoot,[string[]]$Mo2InstanceRoot,[string[]]$VortexProfileRoot,$ProcessInventory,$RegistryInventory)
    $processes=@(Get-GridRunningProviderProcesses -ProcessInventory $ProcessInventory)
    if($PSBoundParameters.ContainsKey('RegistryInventory')){$registered=@(Get-GridRegisteredProviderExecutables -RegistryInventory $RegistryInventory)}else{$registered=@(Get-GridRegisteredProviderExecutables)}
    $candidates=@()

    $moExecutables=@($Mo2Root|Where-Object{$_}|ForEach-Object{Join-Path ([string]$_) 'ModOrganizer.exe'})
    if($env:LOCALAPPDATA){
        $defaultMo2Root=Join-Path $env:LOCALAPPDATA 'Programs\Mod Organizer 2'
        if(Test-Path -LiteralPath $defaultMo2Root -PathType Container){$moExecutables+=Join-Path $defaultMo2Root 'ModOrganizer.exe'}
    }
    foreach($p in @($processes|Where-Object{$_.providerId -eq 'mo2'})){if($p.executablePath){$moExecutables+=[string]$p.executablePath}}
    foreach($p in @($registered|Where-Object{$_.providerId -eq 'mo2'})){if($p.executablePath){$moExecutables+=[string]$p.executablePath}}
    foreach($exeValue in @($moExecutables|Where-Object{$_}|Sort-Object -Unique)){
        try{$exe=[IO.Path]::GetFullPath([string]$exeValue)}catch{continue}
        if(-not(Test-Path -LiteralPath $exe -PathType Leaf)){continue}
        $root=Split-Path -Parent $exe
        $instances=@($Mo2InstanceRoot)
        if($env:LOCALAPPDATA){
            $globalInstances=Join-Path $env:LOCALAPPDATA 'ModOrganizer'
            if(Test-Path -LiteralPath $globalInstances -PathType Container){$instances+=@(Get-ChildItem -LiteralPath $globalInstances -Directory|ForEach-Object FullName)}
        }
        if(Test-Path -LiteralPath (Join-Path $root 'ModOrganizer.ini') -PathType Leaf){$instances+=$root}
        $instances=@($instances|Where-Object{$_}|Sort-Object -Unique)
        if($instances.Count -eq 0){
            $candidates+=[pscustomobject][ordered]@{schemaVersion=1;candidateKind='ModManager';providerId='mo2';status='InstanceNotResolved';executablePath=$exe;applicationRoot=$root;instanceRoot=$null;profilesRoot=$null;profiles=@();running=@($processes|Where-Object{$_.providerId -eq 'mo2' -and $_.executablePath -ieq $exe}).Count -gt 0;changedExternalState=$false}
            continue
        }
        foreach($instanceValue in $instances){
            try{$instance=[IO.Path]::GetFullPath([string]$instanceValue)}catch{continue}
            $ini=Join-Path $instance 'ModOrganizer.ini';if(-not(Test-Path -LiteralPath $ini -PathType Leaf)){continue}
            $kv=ConvertFrom-GridProviderKeyValueText -LiteralPath $ini
            $profilesRoot=if($kv.ContainsKey('profiles_directory')){[Environment]::ExpandEnvironmentVariables([string]$kv['profiles_directory'])}else{Join-Path $instance 'profiles'}
            $profiles=if(Test-Path -LiteralPath $profilesRoot -PathType Container){@(Get-ChildItem -LiteralPath $profilesRoot -Directory|Sort-Object Name|ForEach-Object Name)}else{@()}
            $candidates+=[pscustomobject][ordered]@{schemaVersion=1;candidateKind='ModManager';providerId='mo2';status='Verified';executablePath=$exe;applicationRoot=$root;instanceRoot=$instance;profilesRoot=$profilesRoot;profiles=$profiles;running=@($processes|Where-Object{$_.providerId -eq 'mo2' -and $_.executablePath -ieq $exe}).Count -gt 0;changedExternalState=$false}
        }
    }

    $vortexExecutables=@($VortexRoot|Where-Object{$_}|ForEach-Object{Join-Path ([string]$_) 'Vortex.exe'})
    if($env:LOCALAPPDATA){
        $defaultVortexRoot=Join-Path $env:LOCALAPPDATA 'Programs\Vortex'
        if(Test-Path -LiteralPath $defaultVortexRoot -PathType Container){$vortexExecutables+=Join-Path $defaultVortexRoot 'Vortex.exe'}
    }
    foreach($p in @($processes|Where-Object{$_.providerId -eq 'vortex'})){if($p.executablePath){$vortexExecutables+=[string]$p.executablePath}}
    foreach($p in @($registered|Where-Object{$_.providerId -eq 'vortex'})){if($p.executablePath){$vortexExecutables+=[string]$p.executablePath}}
    foreach($exeValue in @($vortexExecutables|Where-Object{$_}|Sort-Object -Unique)){
        try{$exe=[IO.Path]::GetFullPath([string]$exeValue)}catch{continue}
        if(-not(Test-Path -LiteralPath $exe -PathType Leaf)){continue}
        $root=Split-Path -Parent $exe
        $profileRoots=@($VortexProfileRoot|Where-Object{$_}|Sort-Object -Unique)
        $profiles=@()
        foreach($profileRoot in $profileRoots){if(Test-Path -LiteralPath $profileRoot -PathType Container){$profiles+=@(Get-ChildItem -LiteralPath $profileRoot -Directory|ForEach-Object Name)}}
        $candidates+=[pscustomobject][ordered]@{schemaVersion=1;candidateKind='ModManager';providerId='vortex';status='Verified';executablePath=$exe;applicationRoot=$root;instanceRoot=$null;profilesRoot=$profileRoots;profiles=@($profiles|Sort-Object -Unique);running=@($processes|Where-Object{$_.providerId -eq 'vortex' -and $_.executablePath -ieq $exe}).Count -gt 0;profileFidelity=if($profiles.Count){'ObservedDirectoryNames'}else{'BridgeRequiredForAuthoritativeProfiles'};changedExternalState=$false}
    }
    @($candidates|Sort-Object providerId,applicationRoot,instanceRoot)
}

function Merge-GridGameInstallationCandidates {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Candidate)
    $groups=@{}
    foreach($item in @($Candidate)){if(-not$item.installRoot){continue};$key=([IO.Path]::GetFullPath([string]$item.installRoot).TrimEnd([char[]]@('\','/'))).ToLowerInvariant();if(-not$groups.ContainsKey($key)){$groups[$key]=@()};$groups[$key]+=$item}
    @($groups.GetEnumerator()|Sort-Object Name|ForEach-Object{$values=@($_.Value);[pscustomobject][ordered]@{schemaVersion=1;registrationStatus=if(@($values|Where-Object status -eq 'Verified').Count){'ReadyForReview'}else{'NeedsAttention'};installRoot=[string]$values[0].installRoot;gameId=[string]$values[0].gameId;edition=[string]$values[0].edition;providers=@($values|ForEach-Object providerId|Sort-Object -Unique);observations=$values;changedExternalState=$false}})
}
