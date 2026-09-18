#requires -Version 5.1
function Test-GridGtaConnectedContext {
    [CmdletBinding()]
    param([string[]]$GameRoot)

    foreach ($candidate in @($GameRoot)) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        try { $root = [IO.Path]::GetFullPath($candidate) } catch { continue }
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        if ((Test-Path -LiteralPath (Join-Path $root 'GTA5.exe') -PathType Leaf) -or
            (Test-Path -LiteralPath (Join-Path $root 'GTA5_Enhanced.exe') -PathType Leaf)) { return $true }
    }
    return $false
}
