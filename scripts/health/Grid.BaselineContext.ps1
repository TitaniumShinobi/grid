#requires -Version 5.1
<#
.SYNOPSIS
Resolves the exact persisted MO2 installation and selected profile used by a
whole-profile Grid baseline.
#>

function Read-GridBoundedBaselineText {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$LiteralPath, [long]$MaximumBytes = 1MB)

    $item = Get-Item -LiteralPath $LiteralPath -ErrorAction Stop
    if (-not $item.PSIsContainer -and [long]$item.Length -le $MaximumBytes) {
        $stream = [IO.File]::Open($item.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8, $true)
            try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
        } finally { $stream.Dispose() }
    }
    if ($item.PSIsContainer) { throw "Expected a regular file: $($item.FullName)" }
    throw "Bounded read refused '$($item.FullName)' because it exceeds $MaximumBytes bytes."
}

function Resolve-GridPersistedMo2BaselineContext {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$DiagnosticStoreRoot,
        [string]$InstallationId,
        [string]$ProfileId
    )

    $root = [IO.Path]::GetFullPath($DiagnosticStoreRoot)
    $storePath = Join-Path $root 'connections\mo2-installations.v1.json'
    if (-not (Test-Path -LiteralPath $storePath -PathType Leaf)) {
        throw "InstallationContextUnresolved: persisted MO2 reference store is missing at the configured Grid data root. Connect exactly one Skyrim/MO2 installation in Grid and retry."
    }
    $storeText = Read-GridBoundedBaselineText -LiteralPath $storePath -MaximumBytes 1MB
    try { $document = $storeText | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "InstallationContextUnresolved: persisted MO2 reference store is malformed. $($_.Exception.Message)" }
    if ([int]$document.schemaVersion -ne 1) { throw "InstallationContextUnresolved: persisted MO2 reference store schemaVersion '$($document.schemaVersion)' is unsupported." }

    $valid = @($document.references | Where-Object {
        [int]$_.schemaVersion -eq 1 -and
        [string]$_.gameId -ceq 'game.skyrim-special-edition' -and
        [string]$_.adapterId -ceq 'adapter.mod-organizer-2' -and
        -not [string]::IsNullOrWhiteSpace([string]$_.installationId) -and
        [IO.Path]::IsPathRooted([string]$_.executablePath) -and
        [IO.Path]::IsPathRooted([string]$_.instanceDirectory)
    })
    $matched = @(if ([string]::IsNullOrWhiteSpace($InstallationId)) {
        $valid
    } else {
        $valid | Where-Object { [string]$_.installationId -ceq $InstallationId }
    })
    if ($matched.Count -eq 0) { throw 'InstallationContextUnresolved: no valid persisted MO2 reference matches the requested installation.' }
    if ($matched.Count -ne 1) { throw 'InstallationContextUnresolved: installationId is required because the persisted MO2 reference is not unique.' }

    $reference = $matched[0]
    $applicationPath = [IO.Path]::GetFullPath([string]$reference.executablePath)
    $instancePath = [IO.Path]::GetFullPath([string]$reference.instanceDirectory).TrimEnd('\')
    $configurationPath = Join-Path $instancePath 'ModOrganizer.ini'
    $configurationText = Read-GridBoundedBaselineText -LiteralPath $configurationPath -MaximumBytes 1MB
    $inGeneral = $false
    $selectedProfile = $null
    foreach ($line in @($configurationText -split "`r?`n")) {
        $trimmed = $line.Trim()
        if ($trimmed.StartsWith('[') -and $trimmed.EndsWith(']')) {
            $inGeneral = $trimmed.Substring(1, $trimmed.Length - 2) -ieq 'General'
            continue
        }
        if (-not $inGeneral) { continue }
        $separator = $trimmed.IndexOf('=')
        if ($separator -le 0 -or $trimmed.Substring(0, $separator).Trim() -ine 'selected_profile') { continue }
        $selectedProfile = $trimmed.Substring($separator + 1).Trim()
        if ($selectedProfile.StartsWith('@ByteArray(') -and $selectedProfile.EndsWith(')')) {
            $selectedProfile = $selectedProfile.Substring(11, $selectedProfile.Length - 12)
        }
        break
    }
    if ([string]::IsNullOrWhiteSpace($selectedProfile)) { throw 'InstallationContextUnresolved: General/selected_profile is absent from the bounded ModOrganizer.ini observation.' }
    $profilePath = Join-Path (Join-Path $instancePath 'profiles') $selectedProfile
    $profilePath = [IO.Path]::GetFullPath($profilePath).TrimEnd('\')
    $profileHashInput = ([string]$reference.id) + "`n" + $profilePath
    $profileBytes = [Text.Encoding]::UTF8.GetBytes($profileHashInput)
    $profileAlgorithm = [Security.Cryptography.SHA256]::Create()
    try { $stableProfileId = 'profile.mo2.' + ([BitConverter]::ToString($profileAlgorithm.ComputeHash($profileBytes))).Replace('-', '').ToLowerInvariant().Substring(0, 24) }
    finally { $profileAlgorithm.Dispose() }
    if (-not [string]::IsNullOrWhiteSpace($ProfileId) -and $stableProfileId -cne $ProfileId) {
        throw "InstallationContextUnresolved: requested stable profile '$ProfileId' does not match selected profile '$selectedProfile' ($stableProfileId)."
    }

    [pscustomobject][ordered]@{
        schemaVersion = 1
        installationId = [string]$reference.installationId
        profileId = $stableProfileId
        profileName = $selectedProfile
        profilePath = $profilePath
        applicationPath = $applicationPath
        instancePath = $instancePath
        referenceStorePath = $storePath
        referenceStoreSha256 = (Get-FileHash -LiteralPath $storePath -Algorithm SHA256).Hash.ToLowerInvariant()
        configurationPath = $configurationPath
        configurationSha256 = (Get-FileHash -LiteralPath $configurationPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
