function Get-GridMO2Context {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Mo2Root,
        [Parameter(Mandatory)][string]$Profile,
        [Parameter(Mandatory)][string]$CaseDirectory
    )

    if (-not [IO.Path]::IsPathRooted($Mo2Root)) { throw 'MO2 root must be an absolute path.' }
    if ([IO.Path]::GetFileName($Profile) -cne $Profile -or [string]::IsNullOrWhiteSpace($Profile) -or $Profile.IndexOfAny([char[]]@(0, 10, 13)) -ge 0) {
        throw 'Profile must be one immediate MO2 profile-directory name, not a path.'
    }
    $mo2RootPath = [IO.Path]::GetFullPath($Mo2Root).TrimEnd('\')
    $profilesRoot = [IO.Path]::GetFullPath((Join-Path $mo2RootPath 'profiles')).TrimEnd('\')
    $profileDirectory = [IO.Path]::GetFullPath((Join-Path $profilesRoot $Profile))
    if (-not $profileDirectory.StartsWith($profilesRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The requested profile resolves outside the MO2 profiles root.'
    }
    $pluginsPath = Join-Path $profileDirectory 'plugins.txt'
    $loadOrderPath = Join-Path $profileDirectory 'loadorder.txt'
    $modListPath = Join-Path $profileDirectory 'modlist.txt'

    foreach ($required in @($pluginsPath, $modListPath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Required MO2 profile file not found: $required"
        }
    }

    $plugins = @([IO.File]::ReadAllLines($pluginsPath))
    $mods = @([IO.File]::ReadAllLines($modListPath))
    $loadOrder = if (Test-Path -LiteralPath $loadOrderPath) { @([IO.File]::ReadAllLines($loadOrderPath)) } else { @() }

    $activePlugins = @($plugins | Where-Object { $_.TrimStart().StartsWith('*') } | ForEach-Object { $_.TrimStart().Substring(1).Trim() })
    $inactivePlugins = @($plugins | Where-Object {
        $line = $_.Trim()
        $line -and -not $line.StartsWith('#') -and -not $line.StartsWith('*')
    })
    $activeMods = @($mods | Where-Object { $_.StartsWith('+') } | ForEach-Object { $_.Substring(1).Trim() })
    $orderedLoadOrder = @($loadOrder | ForEach-Object {
        $line = $_.Trim()
        if ($line -and -not $line.StartsWith('#')) { $line }
    })

    $configurationPath = Join-Path $mo2RootPath 'ModOrganizer.ini'
    if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) {
        throw "Portable MO2 configuration not found: $configurationPath"
    }
    $settings = @{}
    $section = ''
    foreach ($line in [IO.File]::ReadAllLines($configurationPath)) {
        $trimmed = $line.Trim()
        if ($trimmed -match '^\[(.+)\]$') { $section = $matches[1]; continue }
        if ($trimmed -and -not $trimmed.StartsWith('#') -and -not $trimmed.StartsWith(';')) {
            $separator = $line.IndexOf('=')
            if ($separator -gt 0) {
                $key = ($section + '/' + $line.Substring(0, $separator).Trim())
                $settings[$key] = $line.Substring($separator + 1).Trim()
            }
        }
    }
    function ConvertFrom-Mo2IniValue([string]$Value) {
        if ($null -eq $Value) { return '' }
        $decoded = $Value.Trim()
        if ($decoded.StartsWith('@ByteArray(') -and $decoded.EndsWith(')')) { $decoded = $decoded.Substring(11, $decoded.Length - 12) }
        return $decoded.Replace('\\', '\')
    }
    $selectedProfile = ConvertFrom-Mo2IniValue ([string]$settings['General/selected_profile'])
    if ([string]::IsNullOrWhiteSpace($selectedProfile)) {
        throw 'MO2 selected_profile evidence is missing; Grid will not guess or use -p to change it.'
    }
    if ($selectedProfile -ne $Profile) {
        throw "Requested Grid profile '$Profile' is not the active MO2 profile '$selectedProfile'. The diagnostic will not use -p to change it."
    }

    function Resolve-Mo2SettingPath([string]$Value, [string]$DefaultPath) {
        if ([string]::IsNullOrWhiteSpace($Value)) { return [IO.Path]::GetFullPath($DefaultPath) }
        $decoded = (ConvertFrom-Mo2IniValue $Value).Replace('/', '\')
        if ($decoded -match '%(?!BASE_DIR%)[^%]+%') { throw "Unsupported MO2 path token in '$Value'." }
        $decoded = $decoded.Replace('%BASE_DIR%', $mo2RootPath)
        if (-not [IO.Path]::IsPathRooted($decoded)) { $decoded = Join-Path $mo2RootPath $decoded }
        return [IO.Path]::GetFullPath($decoded)
    }

    $baseRoot = Resolve-Mo2SettingPath ([string]$settings['Settings/base_directory']) $mo2RootPath
    $modsRoot = Resolve-Mo2SettingPath ([string]$settings['Settings/mod_directory']) (Join-Path $baseRoot 'mods')
    $overwriteRoot = Resolve-Mo2SettingPath ([string]$settings['Settings/overwrite_directory']) (Join-Path $baseRoot 'overwrite')
    $gamePathValue = [string]$settings['Settings/gamePath']
    if ([string]::IsNullOrWhiteSpace($gamePathValue)) { $gamePathValue = [string]$settings['General/gamePath'] }
    $gameRoot = if ([string]::IsNullOrWhiteSpace($gamePathValue)) { $null } else { Resolve-Mo2SettingPath $gamePathValue $gamePathValue }
    $gameDataRoot = if ($gameRoot) { Join-Path $gameRoot 'Data' } else { $null }

    $modInventory = New-Object Collections.Generic.List[object]
    foreach ($line in $mods) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#') -or $line.Length -lt 2) { continue }
        $marker = $line.Substring(0, 1)
        if ($marker -notin @('+', '-')) { continue }
        $modName = $line.Substring(1).Trim()
        if ([string]::IsNullOrWhiteSpace($modName) -or $modName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or $modName -in @('.', '..')) {
            throw "MO2 mod inventory contains an unsafe immediate provider name: $modName"
        }
        $modPath = [IO.Path]::GetFullPath((Join-Path $modsRoot $modName))
        if (-not $modPath.StartsWith($modsRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "MO2 mod inventory provider resolves outside the configured mods root: $modName"
        }
        $metadataPath = Join-Path $modPath 'meta.ini'
        $metadataSha256 = $null
        $metadataValues = @{}
        if (Test-Path -LiteralPath $metadataPath -PathType Leaf) {
            $metadataItem = Get-Item -LiteralPath $metadataPath -Force -ErrorAction Stop
            if (($metadataItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "MO2 metadata is a reparse point and requires separate authorization: $modName" }
            if ([long]$metadataItem.Length -gt 1MB) { throw "MO2 metadata exceeds 1 MiB: $modName" }
            $metadataSha256 = (Get-FileHash -LiteralPath $metadataPath -Algorithm SHA256).Hash
            $metadataSection = 'General'
            foreach ($metadataLine in [IO.File]::ReadAllLines($metadataPath)) {
                $trimmedMetadata = $metadataLine.Trim()
                if ([string]::IsNullOrWhiteSpace($trimmedMetadata) -or $trimmedMetadata.StartsWith('#') -or $trimmedMetadata.StartsWith(';')) { continue }
                if ($trimmedMetadata -match '^\[(.+)\]$') { $metadataSection = $matches[1].Trim(); continue }
                if ($metadataSection -ine 'General') { continue }
                $metadataSeparator = $metadataLine.IndexOf('=')
                if ($metadataSeparator -le 0) { continue }
                $metadataKey = $metadataLine.Substring(0, $metadataSeparator).Trim()
                if ($metadataKey -notin @('modid', 'version', 'installationFile') -or $metadataValues.ContainsKey($metadataKey)) { continue }
                $metadataValue = $metadataLine.Substring($metadataSeparator + 1).Trim()
                if ($metadataValue.StartsWith('@ByteArray(') -and $metadataValue.EndsWith(')')) { $metadataValue = $metadataValue.Substring(11, $metadataValue.Length - 12) }
                if (-not $metadataValue.StartsWith('@Variant(')) { $metadataValues[$metadataKey] = $metadataValue.Replace('\\', '\') }
            }
        }
        $nexusModId = $null
        $parsedNexusModId = [long]0
        if ($metadataValues.ContainsKey('modid') -and [long]::TryParse([string]$metadataValues['modid'], [ref]$parsedNexusModId) -and $parsedNexusModId -ge 0) { $nexusModId = $parsedNexusModId }
        $modInventory.Add([pscustomobject][ordered]@{
            name = $modName
            enabled = ($marker -ceq '+')
            nexusModId = $nexusModId
            version = if ($metadataValues.ContainsKey('version')) { [string]$metadataValues['version'] } else { $null }
            installationFile = if ($metadataValues.ContainsKey('installationFile')) { [string]$metadataValues['installationFile'] } else { $null }
            metadataSha256 = $metadataSha256
        })
    }

    $pluginPaths = @{}
    function Add-PluginProvider([string]$ProviderRoot) {
        if (-not $ProviderRoot -or -not (Test-Path -LiteralPath $ProviderRoot -PathType Container)) { return }
        Get-ChildItem -LiteralPath $ProviderRoot -File -ErrorAction Stop | Where-Object {
            $_.Extension -match '(?i)^\.(esm|esp|esl)$'
        } | ForEach-Object { $pluginPaths[$_.Name] = $_.FullName }
    }
    Add-PluginProvider $gameDataRoot
    for ($index = $activeMods.Count - 1; $index -ge 0; $index--) {
        $modName = $activeMods[$index]
        if ($modName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or $modName -in @('.', '..')) {
            throw "Active mod name is not safe to resolve: $modName"
        }
        $modPath = [IO.Path]::GetFullPath((Join-Path $modsRoot $modName))
        if (-not $modPath.StartsWith($modsRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Active mod resolves outside the configured mods root: $modName"
        }
        if (Test-Path -LiteralPath $modPath -PathType Container) {
            $attributes = (Get-Item -LiteralPath $modPath -Force -ErrorAction Stop).Attributes
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Active mod directory is a reparse point and requires separate authorization: $modName" }
        }
        Add-PluginProvider $modPath
    }
    Add-PluginProvider $overwriteRoot

    $fingerprintInput = [string]::Join("`n", @(
        "PROFILE=$Profile"
        '---PLUGINS---'
        $plugins
        '---LOADORDER---'
        $loadOrder
        '---MODLIST---'
        $mods
    ))
    $bytes = [Text.Encoding]::UTF8.GetBytes($fingerprintInput)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $fingerprint = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }

    $context = [pscustomobject]@{
        Collector = 'Get-GridMO2Context'
        CollectorVersion = 1
        CollectedAt = (Get-Date).ToString('o')
        Mo2Root = $mo2RootPath
        Profile = $Profile
        ProfileDirectory = $profileDirectory
        PluginsPath = $pluginsPath
        LoadOrderPath = $loadOrderPath
        ModListPath = $modListPath
        ActivePlugins = $activePlugins
        InactivePlugins = $inactivePlugins
        ActiveMods = $activeMods
        ModInventory = $modInventory.ToArray()
        LoadOrder = $orderedLoadOrder
        PluginPaths = $pluginPaths
        Mo2ConfigurationPath = $configurationPath
        SelectedProfile = $selectedProfile
        BaseRoot = $baseRoot
        ModsRoot = $modsRoot
        OverwriteRoot = $overwriteRoot
        GameDataRoot = $gameDataRoot
        Fingerprint = "sha256:$fingerprint"
    }

    $context | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $CaseDirectory 'mo2-context.json') -Encoding UTF8
    return $context
}
