#requires -Version 5.1

<#!
.SYNOPSIS
Safely inspects or changes one plugin in a Mod Organizer 2 profile.

.DESCRIPTION
Grid's canonical low-level Skyrim plugin-state executor. It edits only the selected
profile's plugins.txt, creates a timestamped backup before every mutation,
verifies the result, and rolls back automatically if verification fails.
Mutation requires an already-authorized, current Grid remediation proposal;
normal callers use Invoke-GridAuthorizedPluginState.ps1.

No ESP/ESM/ESL, mesh, texture, archive, save, or mod file is modified.

.EXAMPLE
.\Set-GridPluginState.ps1 -PluginName 'FixturePatch.esp' -Action Status -Mo2Root 'C:\MO2' -Profile 'Fixture'

#>

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string]$PluginName,

    [Parameter(Position = 1)]
    [ValidateSet('Status', 'Enable', 'Disable', 'Restore')]
    [string]$Action = 'Status',

    [Parameter(Mandatory)]
    [string]$Mo2Root,

    [Parameter(Mandatory)]
    [string]$Profile,

    [string]$BackupPath,

    [switch]$Force,

    $AuthorizedProposal,

    [string]$CurrentContextFingerprint,

    [string]$CurrentEvidenceFingerprint,

    [switch]$PassThru
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($Mo2Root) -or -not [IO.Path]::IsPathRooted($Mo2Root)) { throw 'Mo2Root must be an existing absolute directory.' }
if ([IO.Path]::GetFileName($Profile) -cne $Profile -or [string]::IsNullOrWhiteSpace($Profile) -or $Profile.IndexOfAny([char[]]@(0, 10, 13)) -ge 0) {
    throw 'Profile must be one immediate MO2 profile-directory name, not a path.'
}
$canonicalMo2Root = [IO.Path]::GetFullPath($Mo2Root).TrimEnd('\')
if (-not (Test-Path -LiteralPath $canonicalMo2Root -PathType Container)) { throw "MO2 root not found: $canonicalMo2Root" }
$profilesRoot = [IO.Path]::GetFullPath((Join-Path $canonicalMo2Root 'profiles')).TrimEnd('\')
$profileDirectory = [IO.Path]::GetFullPath((Join-Path $profilesRoot $Profile))
if (-not $profileDirectory.StartsWith($profilesRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Resolved profile path escapes the MO2 profiles directory.' }
$pluginsFile = Join-Path $profileDirectory 'plugins.txt'
$safePluginName = $PluginName -replace '[^A-Za-z0-9._-]', '_'
$backupDirectory = Join-Path $profileDirectory "grid-backups\plugin-state\$safePluginName"

function Assert-AuthorizedMutation {
    if ($Action -eq 'Status') { return }
    if (-not $AuthorizedProposal -or [string]::IsNullOrWhiteSpace($CurrentContextFingerprint) -or [string]::IsNullOrWhiteSpace($CurrentEvidenceFingerprint)) {
        throw 'AuthorizedProposalRequired: mutation is reachable only through Grid proposal authorization.'
    }
    $gameRoot = Split-Path -Parent $PSScriptRoot
    $scriptsRoot = Split-Path -Parent (Split-Path -Parent $gameRoot)
    $gridHealthModulePath = [IO.Path]::GetFullPath((Join-Path $scriptsRoot 'health\Grid.Health.psm1'))
    $gridHealthModule = Get-Module | Where-Object {
        $_.Path -and [IO.Path]::GetFullPath([string]$_.Path) -eq $gridHealthModulePath
    } | Select-Object -First 1
    if ($null -eq $gridHealthModule) {
        Import-Module $gridHealthModulePath -ErrorAction Stop
    }
    $validation = Test-GridRemediationProposal -Proposal $AuthorizedProposal -CurrentContextFingerprint $CurrentContextFingerprint -CurrentEvidenceFingerprint $CurrentEvidenceFingerprint
    if (-not $validation.IsValid) { throw ($validation.Errors -join ' ') }
    if ($validation.IsStale) { throw 'StaleProposal: context or evidence changed after authorization.' }
    if ($AuthorizedProposal.status -ne 'Executing' -or $AuthorizedProposal.authorization.status -ne 'Authorized') { throw 'AuthorizedProposalRequired: proposal is not in its single executing transition.' }
    if ($AuthorizedProposal.actionType -ne 'PluginStateChange') { throw 'UnsupportedAction: proposal is not a PluginStateChange.' }
    if (@($AuthorizedProposal.targets) -notcontains "pluginName=$PluginName") { throw 'ProposalTargetMismatch: authorized plugin does not match this request.' }
    $expectedState = if ($Action -eq 'Enable') { 'Enabled' } elseif ($Action -eq 'Disable') { 'Disabled' } else { $null }
    if ($expectedState) {
        if (@($AuthorizedProposal.targets) -notcontains "desiredState=$expectedState") {
            throw 'ProposalTargetMismatch: authorized targets do not match this plugin-state request.'
        }
    }
}

function Assert-PluginsFile {
    if (-not (Test-Path -LiteralPath $pluginsFile -PathType Leaf)) {
        throw "MO2 plugins file not found: $pluginsFile`nPass the correct -Mo2Root and -Profile values."
    }
}

function Assert-SafePluginName {
    $extension = [IO.Path]::GetExtension($PluginName)
    if ($extension -notin @('.esp', '.esm', '.esl')) {
        throw "PluginName must end in .esp, .esm, or .esl: $PluginName"
    }
    if ([IO.Path]::GetFileName($PluginName) -cne $PluginName) {
        throw 'PluginName must be a filename, not a path.'
    }
}

function Assert-ApplicationsClosed {
    if ($Force) { return }

    $blockedNames = @(
        'ModOrganizer',
        'SkyrimSE',
        'skse64_loader',
        'SkyrimSELauncher',
        'SSEEdit',
        'xEdit'
    )

    $running = @(Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $blockedNames -contains $_.ProcessName } |
        Select-Object -ExpandProperty ProcessName -Unique)

    if ($running.Count -gt 0) {
        throw "Close MO2, Skyrim, and xEdit before changing plugins.txt. Running: $($running -join ', ')"
    }
}

function Read-PluginLines {
    Assert-PluginsFile
    return [IO.File]::ReadAllLines($pluginsFile)
}

function Get-PluginEntry {
    param([Parameter(Mandatory)][string[]]$Lines)

    $matches = @($Lines | Where-Object {
        $_.TrimStart().TrimStart('*').Trim() -ieq $PluginName
    })

    if ($matches.Count -eq 0) {
        return [pscustomobject]@{ State = 'Missing'; Line = $null }
    }
    if ($matches.Count -gt 1) {
        throw "Found duplicate entries for '$PluginName' in $pluginsFile"
    }

    $line = $matches[0].TrimStart()
    $state = if ($line.StartsWith('*')) { 'Enabled' } else { 'Disabled' }
    return [pscustomobject]@{ State = $state; Line = $matches[0] }
}

function New-GridBackup {
    New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $destination = Join-Path $backupDirectory "plugins-$stamp.txt"
    Copy-Item -LiteralPath $pluginsFile -Destination $destination -ErrorAction Stop
    return $destination
}

function Write-PluginLinesAtomically {
    param([Parameter(Mandatory)][string[]]$Lines)

    $temporaryFile = Join-Path $profileDirectory ('.grid-plugins-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllLines($temporaryFile, $Lines, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporaryFile -Destination $pluginsFile -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryFile) {
            Remove-Item -LiteralPath $temporaryFile -Force
        }
    }
}

function New-Result {
    param(
        [string]$State,
        [bool]$Changed,
        [string]$Backup,
        [string]$Message
    )

    [pscustomobject]@{
        Tool        = 'Set-GridPluginState'
        Action      = $Action
        PluginName  = $PluginName
        State       = $State
        Changed     = $Changed
        Mo2Root     = $Mo2Root
        Profile     = $Profile
        PluginsFile = $pluginsFile
        Backup      = $Backup
        Message     = $Message
        Timestamp   = (Get-Date).ToString('o')
    }
}

function Set-DesiredState {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Enabled', 'Disabled')]
        [string]$DesiredState
    )

    $lines = Read-PluginLines
    $current = Get-PluginEntry -Lines $lines

    if ($current.State -eq 'Missing') {
        throw "Plugin entry not found in $pluginsFile`: $PluginName"
    }
    if ($current.State -eq $DesiredState) {
        return New-Result -State $DesiredState -Changed $false -Backup $null `
            -Message "$PluginName is already $DesiredState."
    }

    if (-not $PSCmdlet.ShouldProcess("$PluginName in profile $Profile", "Set state to $DesiredState")) {
        return New-Result -State $current.State -Changed $false -Backup $null -Message 'No change made.'
    }
    Assert-ApplicationsClosed

    $backup = New-GridBackup
    $updated = foreach ($line in $lines) {
        if ($line.TrimStart().TrimStart('*').Trim() -ieq $PluginName) {
            if ($DesiredState -eq 'Enabled') { "*$PluginName" } else { $PluginName }
        }
        else {
            $line
        }
    }

    try {
        Write-PluginLinesAtomically -Lines $updated
        $verified = Get-PluginEntry -Lines (Read-PluginLines)
        if ($verified.State -ne $DesiredState) {
            throw "Verification returned '$($verified.State)' instead of '$DesiredState'."
        }
    }
    catch {
        Copy-Item -LiteralPath $backup -Destination $pluginsFile -Force
        throw "Plugin state change failed and plugins.txt was restored. $($_.Exception.Message)"
    }

    return New-Result -State $DesiredState -Changed $true -Backup $backup `
        -Message "$PluginName is now $DesiredState."
}

function Restore-GridBackup {
    Assert-ApplicationsClosed
    Assert-PluginsFile

    $source = $BackupPath
    if ([string]::IsNullOrWhiteSpace($source)) {
        $source = Get-ChildItem -LiteralPath $backupDirectory -Filter 'plugins-*.txt' -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }

    if ([string]::IsNullOrWhiteSpace($source) -or -not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "No Grid backup exists for '$PluginName'. Pass one with -BackupPath."
    }

    if (-not $PSCmdlet.ShouldProcess($pluginsFile, "Restore backup $source")) {
        $current = Get-PluginEntry -Lines (Read-PluginLines)
        return New-Result -State $current.State -Changed $false -Backup $null -Message 'No change made.'
    }

    $safetyBackup = New-GridBackup
    try {
        Copy-Item -LiteralPath $source -Destination $pluginsFile -Force
        $state = Get-PluginEntry -Lines (Read-PluginLines)
    }
    catch {
        Copy-Item -LiteralPath $safetyBackup -Destination $pluginsFile -Force
        throw "Restore failed; the pre-restore state was recovered. $($_.Exception.Message)"
    }

    return New-Result -State $state.State -Changed $true -Backup $safetyBackup `
        -Message "Restored $source."
}

Assert-SafePluginName
Assert-AuthorizedMutation

$result = switch ($Action) {
    'Status' {
        $entry = Get-PluginEntry -Lines (Read-PluginLines)
        New-Result -State $entry.State -Changed $false -Backup $null `
            -Message "${PluginName}: $($entry.State)"
    }
    'Enable'  { Set-DesiredState -DesiredState 'Enabled' }
    'Disable' { Set-DesiredState -DesiredState 'Disabled' }
    'Restore' { Restore-GridBackup }
}

if ($PassThru) {
    $result
}
else {
    Write-Host $result.Message
    Write-Host "Profile: $($result.Profile)"
    Write-Host "State: $($result.State)"
    if ($result.Backup) { Write-Host "Backup: $($result.Backup)" }
}
