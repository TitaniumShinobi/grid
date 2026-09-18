#requires -Version 5.1
function Get-GridGtaInstallationSet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$CandidateRoots,
        [string]$SelectedInstallationId
    )

    $seen = @{}
    # Use native PowerShell arrays here. Windows PowerShell 5.1 has unreliable
    # dynamic binding around generic List[object] values in PSCustomObjects.
    $installations = @()
    $rejected = @()
    foreach ($candidate in @($CandidateRoots)) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        try { $root = [IO.Path]::GetFullPath($candidate) }
        catch {
            $rejected += [pscustomobject]@{ suppliedPath = $candidate; reason = 'InvalidPath' }
            continue
        }
        $rootKey = $root.ToLowerInvariant()
        if ($seen.ContainsKey($rootKey)) { continue }
        $seen[$rootKey] = $true
        if (-not (Test-GridGtaConnectedContext -GameRoot $root)) {
            $rejected += [pscustomobject]@{ suppliedPath = $root; reason = 'NoRecognizedExecutable' }
            continue
        }
        $installations += Get-GridGtaInstallationInventory -GameRoot $root
    }

    $ordered = @($installations | Sort-Object edition, rootPath)
    $selected = $null
    $status = 'Ready'
    if ($ordered.Count -eq 0) { $status = 'NoInstallations' }
    elseif ([string]::IsNullOrWhiteSpace($SelectedInstallationId)) {
        if ($ordered.Count -eq 1) { $selected = $ordered[0] }
        else { $status = 'NeedsSelection' }
    }
    else {
        $matches = @($ordered | Where-Object { [string]$_.installationId -ceq $SelectedInstallationId })
        if ($matches.Count -eq 1) { $selected = $matches[0] }
        else { $status = 'SelectionNotFound' }
    }

    [pscustomobject]@{
        schemaVersion = 1; gameId = 'grandtheftautov'; status = $status
        installationCount = $ordered.Count; installations = $ordered
        selectedInstallationId = if ($selected) { [string]$selected.installationId } else { $null }
        selectedInstallation = $selected; rejectedCandidates = @($rejected)
        changedExternalState = $false
    }
}
