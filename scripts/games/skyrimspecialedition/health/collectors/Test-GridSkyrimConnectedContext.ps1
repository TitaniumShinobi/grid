<#
.SYNOPSIS
Read-only probe: is there a connected, valid MO2 installation/profile?
.DESCRIPTION
Used only by Resolve-GridGameAdapter's routing (connected-context priority).
Contains no mod, location, or symptom identities -- it only checks that the
supplied Mo2Root/Profile resolve to a real, readable MO2 profile.
#>
function Test-GridSkyrimConnectedContext {
    [CmdletBinding()]
    param([string]$Mo2Root, [string]$Profile)
    if ([string]::IsNullOrWhiteSpace($Mo2Root) -or [string]::IsNullOrWhiteSpace($Profile)) { return $false }
    $configurationPath = Join-Path $Mo2Root 'ModOrganizer.ini'
    $profileDirectory = Join-Path (Join-Path $Mo2Root 'profiles') $Profile
    $pluginsPath = Join-Path $profileDirectory 'plugins.txt'
    return (Test-Path -LiteralPath $configurationPath -PathType Leaf) -and (Test-Path -LiteralPath $pluginsPath -PathType Leaf)
}
