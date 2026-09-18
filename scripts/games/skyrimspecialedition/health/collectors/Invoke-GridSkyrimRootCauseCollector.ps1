function Invoke-GridSkyrimRootCauseCollector {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RequestPath,
        [Parameter(Mandatory)][string]$OutputPath,
        [Parameter(Mandatory)][ValidateRange(1, 14400)][int]$TimeoutSeconds,
        [string]$DiagnosticsExecutable
    )
    $request = [IO.Path]::GetFullPath($RequestPath)
    $output = [IO.Path]::GetFullPath($OutputPath)
    if (-not (Test-Path -LiteralPath $request -PathType Leaf)) { throw 'RootCauseCollectorRequestMissing.' }
    if ([string]::IsNullOrWhiteSpace($DiagnosticsExecutable)) {
        $repositoryRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))))
        $DiagnosticsExecutable = Join-Path $repositoryRoot 'src\Grid.Diagnostics\bin\x64\Debug\net9.0-windows10.0.19041.0\Grid.Diagnostics.exe'
    }
    $executable = [IO.Path]::GetFullPath($DiagnosticsExecutable)
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "RootCauseCollectorUnavailable: $executable" }
    $parent = Split-Path -Parent $output
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $stderr = Join-Path $parent 'root-cause-collector.stderr.txt'
    $stdout = Join-Path $parent 'root-cause-collector.stdout.txt'
    $arguments = 'root-cause-collect --request "{0}" --output "{1}"' -f $request.Replace('"','\"'), $output.Replace('"','\"')
    $process = Start-Process -FilePath $executable -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $ownedPid = $process.Id; $ownedStart = $process.StartTime.ToUniversalTime(); $ownedPath = $executable
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $current = Get-Process -Id $ownedPid -ErrorAction SilentlyContinue
        if ($current -and [IO.Path]::GetFullPath($current.Path) -ieq $ownedPath -and $current.StartTime.ToUniversalTime() -eq $ownedStart) {
            Stop-Process -Id $ownedPid -ErrorAction Stop
            throw "RootCauseCollectorTimeout: exceeded $TimeoutSeconds seconds; closed only revalidated case-owned PID $ownedPid."
        }
        throw "RootCauseCollectorTimeoutOwnershipMismatch: PID $ownedPid was not controlled."
    }
    # Windows PowerShell can leave ExitCode unset after the timed overload when
    # redirected streams are still completing. The parameterless wait drains
    # those streams and Refresh makes the terminal exit code observable.
    $process.WaitForExit()
    $process.Refresh()
    $exitCode = [int]$process.ExitCode
    if ($exitCode -ne 0) { throw "RootCauseCollectorFailed: exit code $exitCode." }
    if (-not (Test-Path -LiteralPath $output -PathType Leaf)) { throw 'RootCauseCollectorProtocolUnsupported: the fixed command did not create --output.' }
    $outputItem = Get-Item -LiteralPath $output -Force
    if ($outputItem.Length -gt 64MB) { throw 'RootCauseCollectorResultTooLarge: output exceeds 64 MiB.' }
    $result = Get-Content -LiteralPath $output -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    foreach ($name in @('schemaVersion','status','caseId','baselineManifestSha256','recordGraph','assetGraph','issues','usage','rawSources')) {
        if ($null -eq $result.PSObject.Properties[$name]) { throw "RootCauseCollectorResultMalformed: missing '$name'." }
    }
    if ([int]$result.schemaVersion -ne 1 -or [string]$result.status -notin @('Completed','Failed','Partial','Cancelled')) { throw 'RootCauseCollectorResultMalformed: unsupported schemaVersion or status.' }
    foreach ($source in @($result.rawSources)) {
        if ([string]$source.sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'RootCauseCollectorResultMalformed: every raw source requires SHA-256 evidence.' }
    }
    [pscustomobject][ordered]@{ Result = $result; OutputPath = $output; StandardOutputPath = $stdout; StandardErrorPath = $stderr; ExitCode = $exitCode; ProcessId = $ownedPid; ProcessPath = $ownedPath; ProcessStartUtc = $ownedStart.ToString('o') }
}
