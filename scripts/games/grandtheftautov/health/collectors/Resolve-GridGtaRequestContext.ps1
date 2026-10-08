#requires -Version 5.1
Set-StrictMode -Version Latest
if ($null -eq (Get-Variable -Name GridGtaScriptsRoot -Scope Script -ErrorAction SilentlyContinue)) {
    $script:GridGtaScriptsRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
}

function Get-GridGtaObjectValue {
    param($InputObject, [Parameter(Mandatory)][string[]]$Names)
    if ($null -eq $InputObject) { return $null }
    foreach ($name in $Names) {
        $property = $InputObject.PSObject.Properties[$name]
        if ($null -ne $property) {
            $value = $property.Value
            if ($null -ne $value -and $null -ne $value.PSObject.Properties['Value']) { return $value.Value }
            return $value
        }
    }
    return $null
}

function Get-GridGtaSha256Text {
    param([Parameter(Mandatory)][string]$Value)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Resolve-GridGtaRequestContext {
    <#
    .SYNOPSIS
    Validates an account-scoped registration supplied by the shared request engine.
    .DESCRIPTION
    This function deliberately does not locate or open an account store. The caller
    must supply the registration selected from its authenticated account scope.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$ConnectedRegistration,
        [string]$RequestedGameId,
        [string]$RequestedInstallationId,
        [string]$RequestedProfileId,
        [string]$GameRoot,
        [hashtable]$KnownFolders,
        [ValidateRange(1, 3650)][int]$SinceDays = 120
    )

    $nestedRegistration = Get-GridGtaObjectValue $ConnectedRegistration @('registration','Registration')
    if ($null -ne $nestedRegistration) { $ConnectedRegistration = $nestedRegistration }

    $gameId = [string](Get-GridGtaObjectValue $ConnectedRegistration @('gameId','GameId'))
    $installationId = [string](Get-GridGtaObjectValue $ConnectedRegistration @('installationId','InstallationId'))
    $referenceId = [string](Get-GridGtaObjectValue $ConnectedRegistration @('referenceId','ReferenceId'))
    $edition = [string](Get-GridGtaObjectValue $ConnectedRegistration @('edition','Edition'))
    $registeredRootText = [string](Get-GridGtaObjectValue $ConnectedRegistration @('installRoot','InstallRoot'))
    $registeredExecutableText = [string](Get-GridGtaObjectValue $ConnectedRegistration @('executablePath','ExecutablePath'))
    if ([string]::IsNullOrWhiteSpace($gameId) -or [string]::IsNullOrWhiteSpace($installationId) -or
        [string]::IsNullOrWhiteSpace($registeredRootText) -or [string]::IsNullOrWhiteSpace($registeredExecutableText)) {
        throw 'GtaConnectedRegistrationInvalid: the account-owned registration is incomplete.'
    }

    $engineGameId = if ($gameId.StartsWith('game.', [StringComparison]::Ordinal)) { $gameId.Substring(5) } else { $gameId }
    $identity = switch -CaseSensitive ($engineGameId) {
        'grandtheftautov-enhanced' { [pscustomobject]@{ Edition = 'Enhanced'; Executable = 'GTA5_Enhanced.exe' } }
        'grandtheftautov-legacy' { [pscustomobject]@{ Edition = 'Legacy'; Executable = 'GTA5.exe' } }
        default { throw "GtaConnectedRegistrationGameUnsupported: '$gameId' is not a canonical GTA V identity." }
    }
    if ($edition -cne $identity.Edition) { throw 'GtaConnectedRegistrationEditionMismatch: the registered edition does not match its canonical game identity.' }
    if (-not [string]::IsNullOrWhiteSpace($RequestedGameId)) {
        $requestedEngineGameId = if ($RequestedGameId.StartsWith('game.', [StringComparison]::Ordinal)) { $RequestedGameId.Substring(5) } else { $RequestedGameId }
        if ($requestedEngineGameId -cne $engineGameId) { throw 'GtaConnectedRegistrationBindingMismatch: requested game identity does not match the account-owned registration.' }
    }
    if (-not [string]::IsNullOrWhiteSpace($RequestedInstallationId) -and $RequestedInstallationId -cne $installationId) {
        throw 'GtaConnectedRegistrationBindingMismatch: requested installation identity does not match the account-owned registration.'
    }

    $registeredRoot = [IO.Path]::GetFullPath($registeredRootText).TrimEnd([char[]]@('\','/'))
    $registeredExecutable = [IO.Path]::GetFullPath($registeredExecutableText)
    if ($registeredRoot.StartsWith('\\') -or $registeredRoot.StartsWith('\\?\') -or $registeredRoot.StartsWith('\\.\')) {
        throw 'GtaConnectedRegistrationInvalid: only an explicit local installation root is supported.'
    }
    $selectedRoot = if ([string]::IsNullOrWhiteSpace($GameRoot)) { $registeredRoot } else { [IO.Path]::GetFullPath($GameRoot).TrimEnd([char[]]@('\','/')) }
    if (-not $selectedRoot.Equals($registeredRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'GtaConnectedRegistrationRootMismatch: the selected root is not the registered installation.'
    }
    $expectedExecutable = [IO.Path]::GetFullPath((Join-Path $registeredRoot $identity.Executable))
    if (-not $registeredExecutable.Equals($expectedExecutable, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'GtaConnectedRegistrationExecutableMismatch: the registered executable is not the edition-specific executable beneath the installation root.'
    }
    $profileId = 'profile.grid.' + (Get-GridGtaSha256Text $installationId).ToLowerInvariant().Substring(0, 20)
    if (-not [string]::IsNullOrWhiteSpace($RequestedProfileId) -and $RequestedProfileId -cne $profileId) {
        throw 'GtaConnectedRegistrationBindingMismatch: requested profile identity does not match the account-owned default profile.'
    }
    $canonical = @($gameId, $installationId, $profileId, $referenceId, $identity.Edition, $registeredRoot.ToUpperInvariant(),
        $registeredExecutable.ToUpperInvariant()) -join "`n"

    if ($null -eq $KnownFolders) {
        $KnownFolders = @{
            Documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
            LocalApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
            ProgramData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
        }
    }
    $editionRootIds = if ($identity.Edition -eq 'Enhanced') { @('gtavEnhancedUserData','gtavEnhancedCrashDiagnostics') } else { @('gtavLegacyUserData','gtavLegacyDiagnostics') }
    $sharedRootIds = @('rockstarLauncherDiagnostics','rockstarLauncherLocalDiagnostics')
    $userDataDefinition = Resolve-GridGameUserDataRoots -ScriptsRoot $script:GridGtaScriptsRoot -GameId 'grandtheftautov' -KnownFolders $KnownFolders
    $selectedUserDataRoots = @($userDataDefinition.roots | Where-Object { [string]$_.rootId -in @($editionRootIds + $sharedRootIds) })
    $steamManifestPath = $null
    $commonRoot = Split-Path -Parent $registeredRoot
    if ((Split-Path -Leaf $commonRoot) -ieq 'common') {
        $steamAppsRoot = Split-Path -Parent $commonRoot
        $steamAppId = if ($identity.Edition -eq 'Enhanced') { '3240220' } else { '271590' }
        $steamManifestPath = Join-Path $steamAppsRoot ("appmanifest_{0}.acf" -f $steamAppId)
    }
    $programData = if ($KnownFolders.ContainsKey('ProgramData')) { [string]$KnownFolders.ProgramData } else { [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData) }
    $werArchivePath = Join-Path $programData 'Microsoft\Windows\WER\ReportArchive'
    $werQueuePath = Join-Path $programData 'Microsoft\Windows\WER\ReportQueue'
    $authorizedReadPaths = @($registeredRoot, $registeredExecutable, $steamManifestPath, $werArchivePath, $werQueuePath) + @($selectedUserDataRoots | ForEach-Object path)
    $authorizedReadPaths = @($authorizedReadPaths | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Sort-Object -Unique)
    $exactReadResources = @(
        [pscustomobject][ordered]@{
            resourceType = 'WindowsEventLog'; resourceId = 'Application'
            constraints = @('Provider=Application Error|Application Hang|Windows Error Reporting','EventIds=1000,1001,1002',('Executable=' + $identity.Executable),('SinceDays=' + $SinceDays))
        },
        [pscustomobject][ordered]@{
            resourceType = 'WindowsErrorReporting'; resourceId = 'ArchiveAndQueue'
            constraints = @(('Executable=' + $identity.Executable),('SinceDays=' + $SinceDays),'ReadOnly=true')
        }
    )

    [pscustomobject][ordered]@{
        schemaVersion = 1
        status = 'Resolved'
        adapterGameId = 'grandtheftautov'
        engineGameId = $engineGameId
        gameId = $gameId
        installationId = $installationId
        profileId = $profileId
        referenceId = $referenceId
        edition = $identity.Edition
        gameRoot = $registeredRoot
        executablePath = $registeredExecutable
        contextFingerprint = Get-GridGtaSha256Text $canonical
        inventory = $null
        expectedSteamManifestPath = $steamManifestPath
        userDataRoots = $selectedUserDataRoots
        authorizedReadPaths = $authorizedReadPaths
        exactReadResources = $exactReadResources
        changedExternalState = $false
    }
}
