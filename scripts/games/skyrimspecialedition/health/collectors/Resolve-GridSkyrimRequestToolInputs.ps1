#requires -Version 5.1
<# .SYNOPSIS Resolves exact read scopes from one persisted Grid MO2 reference. #>
function Resolve-GridSkyrimRequestToolInputs {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$GridDataRoot, [Parameter(Mandatory)][string]$InstallationId, [Parameter(Mandatory)][string]$ProfileId, [Parameter(Mandatory)][string[]]$ToolIds)
    $storePath = Join-Path ([IO.Path]::GetFullPath($GridDataRoot)) 'connections\mo2-installations.v1.json'
    if (-not (Test-Path -LiteralPath $storePath -PathType Leaf)) { throw 'InstallationContextUnresolved: persisted MO2 reference store is missing.' }
    $storeItem = Get-Item -LiteralPath $storePath -ErrorAction Stop
    if ([long]$storeItem.Length -gt 1MB) { throw 'InstallationContextUnresolved: reference store exceeds 1 MiB.' }
    $store = Get-Content -LiteralPath $storePath -Raw | ConvertFrom-Json -ErrorAction Stop
    $references = @($store.references | Where-Object { [string]$_.installationId -ceq $InstallationId -and [string]$_.gameId -ceq 'game.skyrim-special-edition' -and [string]$_.adapterId -ceq 'adapter.mod-organizer-2' })
    if ($references.Count -ne 1) { throw 'InstallationContextUnresolved: the requested persisted MO2 installation is absent or ambiguous.' }
    $instanceRoot = [IO.Path]::GetFullPath([string]$references[0].instanceDirectory).TrimEnd('\')
    $iniPath = Join-Path $instanceRoot 'ModOrganizer.ini'
    $iniItem = Get-Item -LiteralPath $iniPath -ErrorAction Stop
    if ([long]$iniItem.Length -gt 1MB) { throw 'InstallationContextUnresolved: ModOrganizer.ini exceeds 1 MiB.' }
    $settings = @{}; $section = ''
    foreach ($line in [IO.File]::ReadAllLines($iniPath)) {
        $trimmed = $line.Trim()
        if ($trimmed -match '^\[(.+)\]$') { $section = $matches[1]; continue }
        $separator = $line.IndexOf('=')
        if ($separator -gt 0) { $settings[$section + '/' + $line.Substring(0,$separator).Trim()] = $line.Substring($separator + 1).Trim() }
    }
    function Decode([string]$value) { if ($null -eq $value) { return '' }; $v=$value.Trim(); if($v.StartsWith('@ByteArray(')-and $v.EndsWith(')')){$v=$v.Substring(11,$v.Length-12)}; $v.Replace('\\','\') }
    function ResolveSetting([string]$value,[string]$default) { $v=Decode $value; if([string]::IsNullOrWhiteSpace($v)){$v=$default}; $v=$v.Replace('%BASE_DIR%',$instanceRoot).Replace('/','\'); if(-not [IO.Path]::IsPathRooted($v)){$v=Join-Path $instanceRoot $v}; [IO.Path]::GetFullPath($v).TrimEnd('\') }
    $profileName = Decode ([string]$settings['General/selected_profile'])
    if ([string]::IsNullOrWhiteSpace($profileName)) { throw 'InstallationContextUnresolved: selected_profile is absent.' }
    $profileHashBytes = [Text.Encoding]::UTF8.GetBytes(([string]$references[0].id + "`n" + (Join-Path (Join-Path $instanceRoot 'profiles') $profileName)))
    $sha=[Security.Cryptography.SHA256]::Create(); try{$stable='profile.mo2.'+([BitConverter]::ToString($sha.ComputeHash($profileHashBytes))).Replace('-','').ToLowerInvariant().Substring(0,24)}finally{$sha.Dispose()}
    if ($stable -cne $ProfileId) { throw "InstallationContextUnresolved: selected profile '$profileName' no longer matches '$ProfileId'." }
    $baseRoot=ResolveSetting ([string]$settings['Settings/base_directory']) $instanceRoot
    $modsRoot=ResolveSetting ([string]$settings['Settings/mod_directory']) (Join-Path $baseRoot 'mods')
    $downloadsRoot=ResolveSetting ([string]$settings['Settings/download_directory']) (Join-Path $baseRoot 'downloads')
    $overwriteRoot=ResolveSetting ([string]$settings['Settings/overwrite_directory']) (Join-Path $baseRoot 'overwrite')
    $gameValue=[string]$settings['Settings/gamePath']; if([string]::IsNullOrWhiteSpace($gameValue)){$gameValue=[string]$settings['General/gamePath']}
    $gameData=if([string]::IsNullOrWhiteSpace($gameValue)){$null}else{Join-Path (ResolveSetting $gameValue $gameValue) 'Data'}
    $profileRoot=[IO.Path]::GetFullPath((Join-Path (Join-Path $instanceRoot 'profiles') $profileName))
    $result=@{}
    if ('grid.tool.mo2' -in $ToolIds) { $result['grid.tool.mo2']=[pscustomobject][ordered]@{ mo2Root=$instanceRoot; configurationPath=$iniPath; profileName=$profileName; profileRoot=$profileRoot; modsRoot=$modsRoot; downloadsRoot=$downloadsRoot; overwriteRoot=$overwriteRoot; gameDataRoot=$gameData; authorizedReadPaths=@(@($instanceRoot,$profileRoot,$modsRoot,$downloadsRoot,$overwriteRoot,$gameData) | Where-Object { $_ }) } }
    if ('grid.tool.sseedit' -in $ToolIds) { $result['grid.tool.sseedit']=[pscustomobject][ordered]@{ mo2Root=$instanceRoot; configurationPath=$iniPath; profileName=$profileName; profileRoot=$profileRoot; modsRoot=$modsRoot; downloadsRoot=$downloadsRoot; overwriteRoot=$overwriteRoot; gameDataRoot=$gameData; authorizedReadPaths=@(@($instanceRoot,$profileRoot,$modsRoot,$downloadsRoot,$overwriteRoot,$gameData) | Where-Object { $_ }); operations=@('FindReferencesToBase','InspectReferenceLinks','InspectVmad','TraceOverrideChain','InspectScriptedReference') } }
    if ('grid.tool.loot' -in $ToolIds) {
        $lootRoot = if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'LOOT\Skyrim Special Edition' } else { $null }
        $candidates = if ($lootRoot -and (Test-Path -LiteralPath $lootRoot -PathType Container)) { @(Get-ChildItem -LiteralPath $lootRoot -File -ErrorAction SilentlyContinue | Where-Object { $_.Length -le 16MB -and $_.Extension -in @('.json','.yaml','.yml','.log','.txt') } | Sort-Object FullName | Select-Object -First 64 | ForEach-Object FullName) } else { @() }
        $result['grid.tool.loot']=[pscustomobject][ordered]@{ candidatePaths=@($candidates); authorizedReadPaths=@($candidates) }
    }
    $afterRunFamilies=[ordered]@{
        'grid.tool.texgen'='(?i)texgen';'grid.tool.xlodgen'='(?i)xlodgen|x-lodgen'
        'grid.tool.dyndolod'='(?i)dyndolod';'grid.tool.bodyslide'='(?i)bodyslide'
        'grid.tool.zedit'='(?i)zedit|zmerge';'grid.tool.wrye-bash'='(?i)wrye\s*bash|bash\s*patch'
        'grid.tool.synthesis'='(?i)synthesis';'grid.tool.sseedit-report'='(?i)sseedit'
    }
    if(@($ToolIds|Where-Object{$_ -in @($afterRunFamilies.Keys)}).Count){
        $configured=@{}
        foreach($key in @($settings.Keys|Where-Object{$_ -match '^customExecutables/(\d+)\\(title|binary)$'})){
            $null=$key -match '^customExecutables/(\d+)\\(title|binary)$';$index=[string]$matches[1];$field=[string]$matches[2]
            if(-not$configured.ContainsKey($index)){$configured[$index]=@{}}
            $configured[$index][$field]=Decode ([string]$settings[$key])
        }
        foreach($toolId in @($afterRunFamilies.Keys|Where-Object{$_ -in $ToolIds})){
            $paths=New-Object Collections.Generic.List[string]
            foreach($entry in @($configured.Values|Where-Object{[string]$_.title -match [string]$afterRunFamilies[$toolId]})){
                $binary=[string]$entry.binary
                if(-not[IO.Path]::IsPathRooted($binary)){continue}
                $toolRoot=[IO.Path]::GetFullPath((Split-Path -Parent $binary))
                foreach($root in @($toolRoot,(Join-Path $toolRoot 'Logs'))){
                    if(-not(Test-Path -LiteralPath $root -PathType Container)){continue}
                    if(((Get-Item -LiteralPath $root -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0){continue}
                    foreach($file in @(Get-ChildItem -LiteralPath $root -File -ErrorAction SilentlyContinue|Where-Object{$_.Length -le 16MB -and $_.Extension -in @('.log','.txt','.json','.xml') -and ($_.Attributes-band[IO.FileAttributes]::ReparsePoint)-eq0}|Sort-Object LastWriteTimeUtc -Descending|Select-Object -First 8)){$paths.Add($file.FullName)}
                }
            }
            $candidates=@($paths.ToArray()|Sort-Object -Unique|Select-Object -First 16)
            $result[$toolId]=[pscustomobject][ordered]@{candidatePaths=$candidates;authorizedReadPaths=$candidates;profileName=$profileName;contextStatus='UnboundToToolRun'}
        }
    }
    $result
}
