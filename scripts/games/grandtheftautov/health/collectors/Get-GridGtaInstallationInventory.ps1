#requires -Version 5.1
function Get-GridGtaInstallationId {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RootPath, [Parameter(Mandatory)][string]$Edition)

    $canonical = ([IO.Path]::GetFullPath($RootPath).TrimEnd([char[]]@('\','/')).ToLowerInvariant() + '|' + $Edition.ToLowerInvariant())
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $digest = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical)) }
    finally { $sha.Dispose() }
    $shortHash = (-join ($digest | Select-Object -First 8 | ForEach-Object { $_.ToString('x2') }))
    return ('gta-{0}-{1}' -f $Edition.ToLowerInvariant(), $shortHash)
}

function Get-GridGtaInstallationInventory {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$GameRoot)

    $root = [IO.Path]::GetFullPath($GameRoot)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "GtaInstallationNotFound: '$root' is not a directory."
    }

    function Get-ObservedFile([string]$Name, [string[]]$Candidates) {
        $matches = @($Candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -Unique)
        $path = if ($matches.Count -gt 0) { [IO.Path]::GetFullPath($matches[0]) } else { $null }
        $version = $null
        if ($path) {
            try { $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion } catch { $version = $null }
        }
        [pscustomobject]@{ name = $Name; present = [bool]$path; path = $path; version = $version; matches = @($matches) }
    }

    $legacyPath = Join-Path $root 'GTA5.exe'
    $enhancedPath = Join-Path $root 'GTA5_Enhanced.exe'
    $hasLegacy = Test-Path -LiteralPath $legacyPath -PathType Leaf
    $hasEnhanced = Test-Path -LiteralPath $enhancedPath -PathType Leaf
    $edition = if ($hasLegacy -and -not $hasEnhanced) { 'Legacy' } elseif ($hasEnhanced -and -not $hasLegacy) { 'Enhanced' } else { 'Unknown' }
    $scripts = Join-Path $root 'scripts'

    $components = @(
        Get-ObservedFile 'GameExecutable' @($legacyPath, $enhancedPath)
        Get-ObservedFile 'AsiLoader' @((Join-Path $root 'dinput8.dll'))
        Get-ObservedFile 'OpenIvAsi' @((Join-Path $root 'OpenIV.asi'))
        Get-ObservedFile 'ScriptHookV' @((Join-Path $root 'ScriptHookV.dll'))
        Get-ObservedFile 'Menyoo' @((Join-Path $root 'Menyoo.asi'))
        Get-ObservedFile 'ScriptHookVDotNetAsi' @((Join-Path $root 'ScriptHookVDotNet.asi'))
        Get-ObservedFile 'ScriptHookVDotNet3' @((Join-Path $root 'ScriptHookVDotNet3.dll'))
        Get-ObservedFile 'NativeUI' @((Join-Path $scripts 'NativeUI.dll'), (Join-Path $root 'NativeUI.dll'))
        Get-ObservedFile 'iFruitAddon2' @((Join-Path $scripts 'iFruitAddon2.dll'), (Join-Path $root 'iFruitAddon2.dll'))
    )

    $foreverMatches = @()
    if (Test-Path -LiteralPath $scripts -PathType Container) {
        $foreverMatches = @(Get-ChildItem -LiteralPath $scripts -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '(?i)forever.?together|dealien_forevertogether' } |
            Sort-Object FullName | ForEach-Object FullName)
    }
    $components += [pscustomobject]@{
        name = 'ForeverTogether'; present = ($foreverMatches.Count -gt 0)
        path = if ($foreverMatches.Count -gt 0) { $foreverMatches[0] } else { $null }
        version = $null; matches = @($foreverMatches)
    }

    $timeout = [pscustomobject]@{ status = 'Missing'; path = $null; value = $null }
    $iniPath = Join-Path $root 'ScriptHookVDotNet.ini'
    if (Test-Path -LiteralPath $iniPath -PathType Leaf) {
        $timeout.path = $iniPath
        $line = @(Get-Content -LiteralPath $iniPath -ErrorAction Stop | Where-Object { $_ -match '^\s*ScriptTimeoutThreshold\s*=' } | Select-Object -Last 1)
        if ($line.Count -eq 1) {
            $valueText = ($line[0] -split '=', 2)[1].Trim()
            $value = 0
            if ([int]::TryParse($valueText, [ref]$value)) {
                $timeout.value = $value
                $timeout.status = if ($value -eq 60000) { 'Satisfied' } else { 'Incorrect' }
            } else { $timeout.status = 'Invalid' }
        } else { $timeout.status = 'Unset' }
    }

    [pscustomobject]@{
        schemaVersion = 1
        gameId = 'grandtheftautov'
        installationId = Get-GridGtaInstallationId -RootPath $root -Edition $edition
        observedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        rootPath = $root
        edition = $edition
        modsFolderPresent = Test-Path -LiteralPath (Join-Path $root 'mods') -PathType Container
        scriptsFolderPresent = Test-Path -LiteralPath $scripts -PathType Container
        menyooDataPresent = Test-Path -LiteralPath (Join-Path $root 'menyooStuff') -PathType Container
        components = @($components)
        scriptTimeoutThreshold = $timeout
    }
}
