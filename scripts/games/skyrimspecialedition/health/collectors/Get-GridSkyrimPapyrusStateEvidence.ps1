#requires -Version 5.1

<#
.SYNOPSIS
Inspects one exact Skyrim PEX or PSC and records bounded reference-state evidence.
.DESCRIPTION
This read-only collector launches only the repository-owned Grid.Diagnostics
binary. It does not decompile with a third-party tool, inspect a save, change a
script, or infer that a statically observed branch executed at runtime.
#>
function Get-GridSkyrimPapyrusStateEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ScriptPath,
        [Parameter(Mandatory)][string]$CaseDirectory,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ContextFingerprint,
        [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedSha256,
        [ValidateSet('PEX','PSC')][string]$InputFormat,
        [string]$DiagnosticsExecutable,
        [ValidateRange(1,300)][int]$TimeoutSeconds = 30
    )

    $source = [IO.Path]::GetFullPath($ScriptPath)
    $extension = [IO.Path]::GetExtension($source)
    if ([string]::IsNullOrWhiteSpace($InputFormat) -and $extension -notin @('.pex','.psc')) { throw 'PapyrusStateEvidenceInvalid: ScriptPath must be one exact .pex or .psc file unless InputFormat identifies a content-addressed blob.' }
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw 'PapyrusStateEvidenceMissing: the exact script is absent.' }
    $item = Get-Item -LiteralPath $source -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'PapyrusStateEvidenceRefused: reparse-point scripts are not accepted.' }
    if ([long]$item.Length -le 0 -or [long]$item.Length -gt 32MB) { throw 'PapyrusStateEvidenceRefused: the exact script must be non-empty and no larger than 32 MiB.' }

    if ([string]::IsNullOrWhiteSpace($DiagnosticsExecutable)) {
        $repositoryRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))))
        $candidates = @(
            (Join-Path $repositoryRoot 'bin\Grid.Diagnostics.exe'),
            (Join-Path $repositoryRoot 'src\Grid.Diagnostics\bin\x64\Debug\net9.0-windows10.0.19041.0\Grid.Diagnostics.exe'),
            (Join-Path $repositoryRoot 'src\Grid.Diagnostics\bin\Debug\net9.0-windows10.0.19041.0\Grid.Diagnostics.exe')
        )
        $DiagnosticsExecutable = @($candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1)
    }
    if ([string]::IsNullOrWhiteSpace([string]$DiagnosticsExecutable)) { throw 'PapyrusStateEvidenceUnavailable: build Grid.Diagnostics for x64 Debug or provide its exact executable path.' }
    $executable = [IO.Path]::GetFullPath([string]$DiagnosticsExecutable)
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'PapyrusStateEvidenceUnavailable: the exact Grid.Diagnostics executable is absent.' }

    $caseRoot = [IO.Path]::GetFullPath($CaseDirectory)
    if (-not (Test-Path -LiteralPath $caseRoot -PathType Container)) { New-Item -ItemType Directory -Path $caseRoot -Force | Out-Null }
    $stdout = Join-Path $caseRoot 'papyrus-state-collector.stdout.json'
    $stderr = Join-Path $caseRoot 'papyrus-state-collector.stderr.txt'
    $arguments = 'papyrus-inspect --input "{0}"' -f $source.Replace('"','\"')
    if (-not [string]::IsNullOrWhiteSpace($InputFormat)) { $arguments += ' --format "' + $InputFormat.ToLowerInvariant() + '"' }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256)) { $arguments += ' --expected-sha256 "' + $ExpectedSha256.ToUpperInvariant() + '"' }
    $process = Start-Process -FilePath $executable -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $ownedPid = $process.Id
    $ownedStart = $process.StartTime.ToUniversalTime()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $current = Get-Process -Id $ownedPid -ErrorAction SilentlyContinue
        if ($current -and [IO.Path]::GetFullPath($current.Path) -ieq $executable -and $current.StartTime.ToUniversalTime() -eq $ownedStart) {
            Stop-Process -Id $ownedPid -ErrorAction Stop
            throw "PapyrusStateEvidenceTimeout: exceeded $TimeoutSeconds seconds; closed only revalidated case-owned PID $ownedPid."
        }
        throw "PapyrusStateEvidenceTimeoutOwnershipMismatch: PID $ownedPid was not controlled."
    }
    $process.WaitForExit(); $process.Refresh()
    $exitCode = [int]$process.ExitCode
    if (-not (Test-Path -LiteralPath $stdout -PathType Leaf)) { throw 'PapyrusStateEvidenceProtocolInvalid: the collector produced no JSON.' }
    $outputItem = Get-Item -LiteralPath $stdout -Force
    if ([long]$outputItem.Length -gt 32MB) { throw 'PapyrusStateEvidenceProtocolInvalid: collector JSON exceeds 32 MiB.' }
    try { $observed = Get-Content -LiteralPath $stdout -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "PapyrusStateEvidenceProtocolInvalid: $($_.Exception.Message)" }
    foreach ($field in @('schemaVersion','status','sourcePath','sizeBytes','sha256','collectedAtUtc','inspection')) {
        if ($null -eq $observed.PSObject.Properties[$field]) { throw "PapyrusStateEvidenceProtocolInvalid: missing '$field'." }
    }
    if ([int]$observed.schemaVersion -ne 1 -or [string]$observed.sourcePath -cne $source -or [string]$observed.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
        throw 'PapyrusStateEvidenceProtocolInvalid: source identity or schema binding is invalid.'
    }
    if ($exitCode -ne 0 -or [string]$observed.status -ne 'Complete') {
        throw "PapyrusStateEvidenceFailed: collector status '$($observed.status)' with exit code $exitCode."
    }
    $evidence = [pscustomobject][ordered]@{
        schemaVersion = 1
        status = 'Observed'
        contextFingerprint = $ContextFingerprint.ToUpperInvariant()
        source = [pscustomobject][ordered]@{
            path = $source
            sizeBytes = [long]$observed.sizeBytes
            sha256 = ([string]$observed.sha256).ToUpperInvariant()
            collectedAtUtc = [string]$observed.collectedAtUtc
            beforeLastWriteTimeUtcTicks = [long]$observed.beforeLastWriteTimeUtcTicks
            afterLastWriteTimeUtcTicks = [long]$observed.afterLastWriteTimeUtcTicks
        }
        inspection = $observed.inspection
        limitations = @(
            'Static PEX/PSC inspection does not prove which branch executed in the current save.',
            'A lasting repair still requires current selector value and script attachment/provider evidence.'
        )
    }
    $evidencePath = Join-Path $caseRoot 'papyrus-state-evidence.v1.json'
    $evidence | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $evidencePath -Encoding UTF8
    [pscustomobject][ordered]@{
        Status = 'Observed'
        Path = $evidencePath
        Evidence = $evidence
        ProcessId = $ownedPid
        ProcessPath = $executable
        ProcessStartUtc = $ownedStart.ToString('o')
        StandardErrorPath = $stderr
    }
}
