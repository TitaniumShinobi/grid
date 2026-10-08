#requires -Version 5.1
<#
.SYNOPSIS
Resolves one exact account-owned game registration for diagnostic collection.
.DESCRIPTION
Reads only the account-scoped game installation store supplied by the caller.
It does not fall back to legacy/global records, discover installations, or
persist state. Catalog game identity remains distinct from the health-adapter
identity returned separately by Resolve-GridRequestGameId.
#>
Set-StrictMode -Version Latest

function Get-GridRegisteredProfileId {
    [CmdletBinding()]
    param([Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$InstallationId)

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($InstallationId)
        $hex = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
        'profile.grid.' + $hex.Substring(0, 20)
    }
    finally { $sha.Dispose() }
}

function Resolve-GridConnectedGameContext {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$GridDataRoot,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$CatalogGameId,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$InstallationId,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ProfileId,
        [switch]$PrepareAuthorizationScope
    )

    $root = [IO.Path]::GetFullPath($GridDataRoot).TrimEnd('\')
    $storePath = Join-Path $root 'connections\game-installations.v1.json'
    if (-not (Test-Path -LiteralPath $storePath -PathType Leaf)) {
        throw 'InstallationContextUnresolved: the account-owned game installation store is missing.'
    }
    $storeItem = Get-Item -LiteralPath $storePath -Force -ErrorAction Stop
    if (($storeItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'InstallationContextUnresolved: the account-owned game installation store may not be a reparse point.'
    }
    if ([long]$storeItem.Length -gt 1MB) {
        throw 'InstallationContextUnresolved: the account-owned game installation store exceeds 1 MiB.'
    }
    try { $store = Get-Content -LiteralPath $storePath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "InstallationContextUnresolved: the account-owned game installation store is unreadable or malformed: $($_.Exception.Message)" }
    if ([int]$store.schemaVersion -ne 1) {
        throw 'InstallationContextUnresolved: the account-owned game installation store has an unsupported schema.'
    }

    $matches = @($store.registrations | Where-Object {
        [string]$_.installationId.value -ceq $InstallationId -and
        [string]$_.gameId.value -ceq $CatalogGameId
    })
    if ($matches.Count -ne 1) {
        throw 'InstallationContextUnresolved: the requested account-owned game registration is absent or ambiguous.'
    }
    $registration = $matches[0]
    $expectedProfileId = Get-GridRegisteredProfileId -InstallationId $InstallationId
    if ($expectedProfileId -cne $ProfileId) {
        throw 'InstallationContextUnresolved: the requested profile is not the deterministic profile owned by this registration.'
    }

    $installRoot = [IO.Path]::GetFullPath([string]$registration.installRoot).TrimEnd('\')
    $executablePath = [IO.Path]::GetFullPath([string]$registration.executablePath)
    $rootPrefix = $installRoot + '\'
    if (-not $executablePath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'InstallationContextUnresolved: the registered executable escapes the registered installation root.'
    }
    if (-not $PrepareAuthorizationScope) {
        if (-not (Test-Path -LiteralPath $installRoot -PathType Container) -or
            -not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
            throw 'InstallationContextUnresolved: the exact registered installation or executable is unavailable.'
        }
        $rootItem = Get-Item -LiteralPath $installRoot -Force -ErrorAction Stop
        $executableItem = Get-Item -LiteralPath $executablePath -Force -ErrorAction Stop
        if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            ($executableItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'InstallationContextUnresolved: registered diagnostic roots may not be reparse points.'
        }
    }

    $unsigned = [pscustomobject][ordered]@{
        schemaVersion = 1
        gameId = $CatalogGameId
        catalogGameId = $CatalogGameId
        adapterGameId = Resolve-GridRequestAdapterGameId -GameId $CatalogGameId
        referenceId = [string]$registration.referenceId.value
        installationId = $InstallationId
        profileId = $ProfileId
        adapterId = [string]$registration.adapterId.value
        displayName = [string]$registration.displayName
        edition = [string]$registration.edition
        providerId = [string]$registration.providerId
        installRoot = $installRoot
        executablePath = $executablePath
        registrationStorePath = $storePath
        registeredAtUtc = [string]$registration.registeredAtUtc
        managerProviderIds = @($registration.managerProviderIds | ForEach-Object { [string]$_ } | Sort-Object -Unique)
        externalValidationDeferred = [bool]$PrepareAuthorizationScope
    }
    $unsigned | Add-Member -NotePropertyName contextFingerprint -NotePropertyValue (Get-GridCanonicalJsonSha256 -InputObject $unsigned)
    $unsigned
}
