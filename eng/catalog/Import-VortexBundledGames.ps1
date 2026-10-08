param(
    [Parameter(Mandatory=$true)]
    [string]$VortexRoot,

    [string]$OutputPath = (Join-Path $PSScriptRoot '..\..\config\catalog\vortex-bundled.v1.json')
)

$ErrorActionPreference = 'Stop'
$gamesRoot = Join-Path $VortexRoot 'extensions\games'
if (-not (Test-Path -LiteralPath $gamesRoot -PathType Container)) {
    throw "Vortex extensions/games not found: $gamesRoot"
}

$head = (& git -c "safe.directory=$VortexRoot" -C $VortexRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($head)) {
    throw "Unable to resolve Vortex Git HEAD."
}

$entries = @()
Get-ChildItem -LiteralPath $gamesRoot -Directory -Filter 'game-*' |
    Sort-Object Name |
    ForEach-Object {
        $packagePath = Join-Path $_.FullName 'package.json'
        if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) { return }

        $package = Get-Content -LiteralPath $packagePath -Raw | ConvertFrom-Json
        $displayName = $null
        if ($package.config -and $package.config.game) { $displayName = [string]$package.config.game }
        elseif ($package.description) { $displayName = [string]$package.description }
        else { $displayName = $_.Name }

        $canonical = $null
        $status = 'upstream-candidate'
        if ($_.Name -eq 'game-skyrimse') {
            $canonical = 'game.skyrim-special-edition'
            $status = 'grid-native-override'
        }

        $entries += [ordered]@{
            sourcePackage = $_.Name
            displayName = $displayName
            license = [string]$package.license
            sourcePath = "extensions/games/$($_.Name)/package.json"
            upstreamKey = $_.Name.Substring(5)
            gridCanonicalGameId = $canonical
            status = $status
        }
    }

if ($entries.Count -ne 86) {
    throw "Expected 86 bundled Vortex game packages at v2.6.3; observed $($entries.Count). Refusing to generate a misleading catalog."
}

$badLicense = @($entries | Where-Object { $_.license -ne 'GPL-3.0' })
if ($badLicense.Count -ne 0) {
    throw "Unexpected Vortex game-package license(s); provenance review required."
}

$result = [ordered]@{
    schemaVersion = 1
    kind = 'grid-vortex-bundled-catalog-foundation'
    source = [ordered]@{
        repository = 'Nexus-Mods/Vortex'
        tag = 'v2.6.3'
        commit = $head
        license = 'GPL-3.0'
    }
    semantics = [ordered]@{
        catalogMembershipDoesNotImplyInstallation = $true
        catalogMembershipDoesNotImplyConnection = $true
        gridNativeIdentityOverridesUpstream = $true
    }
    entries = @($entries | Sort-Object displayName, sourcePackage)
}

$parent = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Host "Generated $($entries.Count) bundled Vortex catalog candidates:"
Write-Host $OutputPath
