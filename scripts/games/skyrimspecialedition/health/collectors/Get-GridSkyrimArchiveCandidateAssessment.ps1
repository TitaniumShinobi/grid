#requires -Version 5.1

<#
.SYNOPSIS
Classifies one exact archive against current installed plugin evidence.
.DESCRIPTION
Launches only Grid.Diagnostics repair-archive-classify with structured values.
The result is read-only evidence. It does not select installer options, create a
repair proposal, recommend an update, or mutate MO2.
#>

function Get-GridSkyrimArchiveCandidateAssessment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$DiagnosticsExecutable,
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedArchiveSha256,
        [Parameter(Mandatory)][string]$PrimaryPluginName,
        [string]$PrimaryPluginEntry,
        [string]$PrimaryArchiveEntry,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$InstalledPluginSha256,
        [string]$InstalledVersion,
        [string]$CandidateVersion,
        [AllowEmptyCollection()][object[]]$DependentPlugins = @()
    )

    $executable = [IO.Path]::GetFullPath($DiagnosticsExecutable)
    $archive = [IO.Path]::GetFullPath($ArchivePath)
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf) -or
        [IO.Path]::GetFileName($executable) -ine 'Grid.Diagnostics.exe') {
        throw 'ArchiveCandidateClassifierUnavailable: DiagnosticsExecutable must name Grid.Diagnostics.exe.'
    }
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        throw 'ArchiveCandidateUnavailable: the exact structured archive path is not a file.'
    }

    $arguments = New-Object Collections.Generic.List[string]
    foreach ($value in @(
        'repair-archive-classify', '--archive', $archive,
        '--expected-sha256', $ExpectedArchiveSha256,
        '--primary-plugin-name', $PrimaryPluginName,
        '--installed-plugin-sha256', $InstalledPluginSha256
    )) { $arguments.Add([string]$value) }
    if (-not [string]::IsNullOrWhiteSpace($PrimaryPluginEntry)) {
        $arguments.Add('--primary-plugin-entry'); $arguments.Add($PrimaryPluginEntry.Trim())
    }
    if (-not [string]::IsNullOrWhiteSpace($PrimaryArchiveEntry)) {
        $arguments.Add('--primary-archive-entry'); $arguments.Add($PrimaryArchiveEntry.Trim())
    }
    if (-not [string]::IsNullOrWhiteSpace($InstalledVersion)) {
        $arguments.Add('--installed-version'); $arguments.Add($InstalledVersion.Trim())
    }
    if (-not [string]::IsNullOrWhiteSpace($CandidateVersion)) {
        $arguments.Add('--candidate-version'); $arguments.Add($CandidateVersion.Trim())
    }

    $seen = @{}
    foreach ($dependent in @($DependentPlugins | Sort-Object { [string]$_.name })) {
        $name = [string]$dependent.name
        $sha256 = [string]$dependent.sha256
        if ([string]::IsNullOrWhiteSpace($name) -or $name.IndexOfAny([char[]]@(0,9,10,13,'=')) -ge 0 -or
            $sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $seen.ContainsKey($name.ToUpperInvariant())) {
            throw 'ArchiveCandidateDependentInvalid: dependent plugins require unique names and exact SHA-256 evidence.'
        }
        $seen[$name.ToUpperInvariant()] = $true
        $arguments.Add('--dependent'); $arguments.Add($name + '=' + $sha256)
        $candidateEntry = [string]$dependent.candidateEntryPath
        if (-not [string]::IsNullOrWhiteSpace($candidateEntry)) {
            if ($candidateEntry.IndexOfAny([char[]]@(0,9,10,13,'=')) -ge 0) {
                throw 'ArchiveCandidateBundledEntryInvalid: archive-entry evidence contains an invalid character.'
            }
            $arguments.Add('--bundled'); $arguments.Add($name + '=' + $candidateEntry)
        }
    }

    $json = & $executable @arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -notin @(0,3) -or [string]::IsNullOrWhiteSpace(($json -join ''))) {
        throw "ArchiveCandidateClassifierFailed: Grid.Diagnostics exited with code $exitCode."
    }
    try { $result = ($json -join [Environment]::NewLine) | ConvertFrom-Json -ErrorAction Stop }
    catch { throw 'ArchiveCandidateClassifierInvalidOutput: Grid.Diagnostics did not return valid JSON.' }
    if ([string]$result.status -notin @('Complete','SelectionRequired','Rejected') -or
        [string]$result.evidenceId -notmatch '^archive-candidate\.[a-f0-9]{24}$') {
        throw 'ArchiveCandidateClassifierInvalidOutput: terminal status or evidence identity is invalid.'
    }
    $result
}
