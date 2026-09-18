[CmdletBinding()]
param(
    [ValidateSet('Portable','WindowsSource','Distribution')]
    [string]$Lane = 'Portable',

    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug',

    [string]$ManifestPath,

    [string]$ArtifactsRoot,

    [switch]$ContinueOnFailure
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Get-GridRepositoryRoot {
    Split-Path -Parent $PSScriptRoot
}

function Resolve-GridPath {
    param([string]$Root, [string]$RelativePath)
    if ([System.IO.Path]::IsPathRooted($RelativePath)) { return [System.IO.Path]::GetFullPath($RelativePath) }
    [System.IO.Path]::GetFullPath((Join-Path $Root ($RelativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)))
}

function Test-GridIsWindows {
    if ($PSVersionTable.PSVersion.Major -le 5) { return $true }
    return [bool]$IsWindows
}

function Get-GridOsArchitecture {
    $property = [System.Runtime.InteropServices.RuntimeInformation].GetProperty('OSArchitecture')
    if ($null -ne $property) {
        $value = $property.GetValue($null, $null)
        if ($null -ne $value) { return $value.ToString() }
    }
    if (-not [string]::IsNullOrWhiteSpace($env:PROCESSOR_ARCHITECTURE)) { return $env:PROCESSOR_ARCHITECTURE }
    return 'Unknown'
}

function Read-GridVerificationManifest {
    param([Parameter(Mandatory=$true)][string]$Path, [Parameter(Mandatory=$true)][string]$RepositoryRoot)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Verification manifest is missing: $Path" }
    try { $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
    catch { throw "Verification manifest is not valid JSON: $($_.Exception.Message)" }

    if ($manifest.schemaVersion -ne 1 -or $manifest.manifestId -ne 'grid.verification.v1') {
        throw 'Verification manifest must use schemaVersion 1 and manifestId grid.verification.v1.'
    }
    if (@($manifest.lanes).Count -lt 3) { throw 'Verification manifest must declare Portable, WindowsSource, and Distribution lanes.' }
    foreach ($requiredLane in @('Portable','WindowsSource','Distribution')) {
        if (-not (@($manifest.lanes | ForEach-Object { $_.id }) -contains $requiredLane)) { throw "Verification manifest is missing lane '$requiredLane'." }
    }

    $suites = @($manifest.suites)
    if ($suites.Count -eq 0) { throw 'Verification manifest contains no suites.' }
    $ids = @($suites | ForEach-Object { [string]$_.id })
    if (@($ids | Sort-Object -Unique).Count -ne $ids.Count) { throw 'Verification manifest suite IDs must be unique.' }
    $orders = @($suites | ForEach-Object { [int]$_.order })
    if (@($orders | Sort-Object -Unique).Count -ne $orders.Count) { throw 'Verification manifest suite order values must be unique.' }

    $validKinds = @('PowerShell','DotNetBuild','DotNetRun','UiDotNetRun')
    $validPlatforms = @('Any','Windows')
    foreach ($suite in $suites) {
        if ([string]::IsNullOrWhiteSpace([string]$suite.id) -or [string]::IsNullOrWhiteSpace([string]$suite.path)) { throw 'Every registered suite requires id and path.' }
        if (-not ($validKinds -contains [string]$suite.kind)) { throw "Suite '$($suite.id)' has unsupported kind '$($suite.kind)'." }
        if (-not ($validPlatforms -contains [string]$suite.platform)) { throw "Suite '$($suite.id)' has unsupported platform '$($suite.platform)'." }
        if (-not (@('None','Required') -contains [string]$suite.configurationMode)) { throw "Suite '$($suite.id)' has invalid configurationMode." }
        $resolved = Resolve-GridPath -Root $RepositoryRoot -RelativePath ([string]$suite.path)
        $rootFull = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        if (-not $resolved.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { throw "Suite '$($suite.id)' resolves outside the repository." }
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "Registered verification suite is missing: $($suite.id) -> $($suite.path)" }
    }

    $manifest
}

function Get-GridSuitesForLane {
    param([Parameter(Mandatory=$true)]$Manifest, [Parameter(Mandatory=$true)][string]$Lane)
    @($Manifest.suites | Where-Object { @($_.lanes) -contains $Lane } | Sort-Object { [int]$_.order })
}

function Get-GridPowerShellExecutable {
    $current = $null
    try { $current = (Get-Process -Id $PID).Path } catch { }
    if (-not [string]::IsNullOrWhiteSpace($current) -and (Test-Path -LiteralPath $current -PathType Leaf)) { return $current }
    $candidate = Get-Command powershell.exe -ErrorAction SilentlyContinue
    if ($candidate) { return $candidate.Source }
    $candidate = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($candidate) { return $candidate.Source }
    throw 'Unable to resolve the current PowerShell executable.'
}

function Get-GridDotNetSdkVersion {
    try {
        $value = & dotnet --version 2>$null
        if ($LASTEXITCODE -eq 0) { return (($value | Select-Object -First 1) -as [string]) }
    } catch { }
    return $null
}

function Get-GridUiExecutablePath {
    param([string]$RepositoryRoot, [string]$Configuration)
    Join-Path $RepositoryRoot ("src/Grid.App/bin/x64/{0}/net9.0-windows10.0.19041.0/win-x64/Grid.exe" -f $Configuration)
}

function Get-GridCommandSpec {
    param($Suite, [string]$RepositoryRoot, [string]$Configuration)

    $path = Resolve-GridPath -Root $RepositoryRoot -RelativePath ([string]$Suite.path)
    switch ([string]$Suite.kind) {
        'PowerShell' {
            $exe = Get-GridPowerShellExecutable
            return [pscustomobject]@{ FileName=$exe; Arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$path); Environment=@{}; Identity=("PowerShell -File {0}" -f $Suite.path); ReceiptConfiguration='None' }
        }
        'DotNetBuild' {
            $args = @('build',$path,'-c',$Configuration) + @($Suite.arguments)
            return [pscustomobject]@{ FileName='dotnet'; Arguments=$args; Environment=@{}; Identity=("dotnet build {0} -c {1} {2}" -f $Suite.path,$Configuration,(@($Suite.arguments) -join ' ')).Trim(); ReceiptConfiguration=$Configuration }
        }
        'DotNetRun' {
            $args = @('run','--project',$path,'-c',$Configuration) + @($Suite.arguments)
            return [pscustomobject]@{ FileName='dotnet'; Arguments=$args; Environment=@{}; Identity=("dotnet run --project {0} -c {1} {2}" -f $Suite.path,$Configuration,(@($Suite.arguments) -join ' ')).Trim(); ReceiptConfiguration=$Configuration }
        }
        'UiDotNetRun' {
            $explicitUiArg = if ($Configuration -eq 'Debug') { '--debug' } else { '--release' }
            $args = @('run','--project',$path,'-c',$Configuration,'-p:Platform=x64') + @($Suite.arguments) + @('--',$explicitUiArg)
            $uiExecutable = Get-GridUiExecutablePath -RepositoryRoot $RepositoryRoot -Configuration $Configuration
            return [pscustomobject]@{ FileName='dotnet'; Arguments=$args; Environment=@{ GRID_UI_EXECUTABLE=$uiExecutable }; Identity=("dotnet run --project {0} -c {1} -p:Platform=x64 {2} -- {3}; GRID_UI_EXECUTABLE={4}" -f $Suite.path,$Configuration,(@($Suite.arguments) -join ' '),$explicitUiArg,("src/Grid.App/bin/x64/{0}/net9.0-windows10.0.19041.0/win-x64/Grid.exe" -f $Configuration)).Trim(); ReceiptConfiguration=$Configuration }
        }
        default { throw "Unsupported suite kind '$($Suite.kind)'." }
    }
}

function New-GridReceiptCheck {
    param($Suite, [string]$CommandIdentity, [string]$Configuration, [string]$StartedAtUtc, [long]$DurationMs, $ExitCode, [string]$Result, $LogPath)
    [ordered]@{
        id = [string]$Suite.id
        displayName = [string]$Suite.displayName
        order = [int]$Suite.order
        category = [string]$Suite.category
        commandIdentity = $CommandIdentity
        configuration = $Configuration
        startedAtUtc = $StartedAtUtc
        durationMs = [int64]$DurationMs
        exitCode = $ExitCode
        result = $Result
        logPath = $LogPath
    }
}

function Test-GridReceiptObject {
    param([Parameter(Mandatory=$true)]$Receipt)
    $errors = New-Object System.Collections.Generic.List[string]
    if ($Receipt.schemaVersion -ne 1) { $errors.Add('schemaVersion must be 1.') }
    if ($Receipt.manifestId -ne 'grid.verification.v1') { $errors.Add('manifestId is invalid.') }
    if (-not (@('Portable','WindowsSource','Distribution') -contains [string]$Receipt.lane)) { $errors.Add('lane is invalid.') }
    if (-not (@('Debug','Release') -contains [string]$Receipt.configuration)) { $errors.Add('configuration is invalid.') }
    if (-not (@('Passed','Failed') -contains [string]$Receipt.overallResult)) { $errors.Add('overallResult is invalid.') }
    $validResults = @('Passed','Failed','SkippedUnsupportedPlatform','NotRun')
    $lastOrder = -1
    foreach ($check in @($Receipt.checks)) {
        if (-not ($validResults -contains [string]$check.result)) { $errors.Add("check '$($check.id)' has invalid result.") }
        if ([int]$check.order -le $lastOrder) { $errors.Add('checks are not in strictly increasing manifest order.') }
        $lastOrder = [int]$check.order
        if ([string]::IsNullOrWhiteSpace([string]$check.commandIdentity)) { $errors.Add("check '$($check.id)' has no command identity.") }
    }
    [pscustomobject]@{ Valid=($errors.Count -eq 0); Errors=@($errors) }
}

function ConvertTo-GridCommandLineArgument {
    param([string]$Value)
    if ($null -eq $Value) { return '""' }
    if ($Value -notmatch '[\s"]') { return $Value }
    # Windows/.NET ProcessStartInfo quoting compatible with CommandLineToArgvW-style parsing.
    return '"' + ([regex]::Replace($Value, '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"'
}

function Invoke-GridExternalCommand {
    param([string]$FileName, [string[]]$Arguments, [hashtable]$Environment, [string]$LogPath, [string]$WorkingDirectory)

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FileName
    $psi.Arguments = (($Arguments | ForEach-Object { ConvertTo-GridCommandLineArgument ([string]$_) }) -join ' ')
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    foreach ($key in $Environment.Keys) { $psi.EnvironmentVariables[$key] = [string]$Environment[$key] }

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi
    [void]$process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.Result
    $stderr = $stderrTask.Result
    $body = if ([string]::IsNullOrEmpty($stderr)) { $stdout } else { $stdout + [Environment]::NewLine + $stderr }
    [System.IO.File]::WriteAllText($LogPath, $body, (New-Object System.Text.UTF8Encoding($false)))
    [pscustomobject]@{ ExitCode=$process.ExitCode; Output=$body }
}

function ConvertTo-GridObjectArray {
    param([Parameter(Mandatory=$true)]$List)
    # Windows PowerShell 5.1 can throw "Argument types do not match" when
    # array-subexpression or direct generic-list casts are used during function
    # return binding. Copy items into a plain object[] and return that array as
    # one pipeline object so the caller receives a stable array for 0, 1, or N items.
    $items = New-Object System.Collections.ArrayList
    foreach ($item in $List) { [void]$items.Add($item) }
    $array = [object[]]$items.ToArray([object])
    Write-Output -NoEnumerate $array
}

function Invoke-GridVerification {
    param([string]$Lane, [string]$Configuration, [string]$ManifestPath, [string]$ArtifactsRoot, [switch]$ContinueOnFailure)

    $repositoryRoot = Get-GridRepositoryRoot
    if ([string]::IsNullOrWhiteSpace($ManifestPath)) { $ManifestPath = Join-Path $repositoryRoot 'eng/verification.manifest.v1.json' }
    if ([string]::IsNullOrWhiteSpace($ArtifactsRoot)) { $ArtifactsRoot = Join-Path $repositoryRoot 'artifacts/verification' }

    $manifest = Read-GridVerificationManifest -Path $ManifestPath -RepositoryRoot $repositoryRoot
    $suites = @(Get-GridSuitesForLane -Manifest $manifest -Lane $Lane)
    if ($suites.Count -eq 0) { throw "Lane '$Lane' has no registered checks in verification manifest v1." }

    $started = [DateTimeOffset]::UtcNow
    $receiptId = '{0}-{1}-{2}' -f $started.ToString('yyyyMMddTHHmmssfffZ'),$Lane.ToLowerInvariant(),$Configuration.ToLowerInvariant()
    $runRoot = Join-Path $ArtifactsRoot $receiptId
    $logRoot = Join-Path $runRoot 'logs'
    [void](New-Item -ItemType Directory -Force -Path $logRoot)
    $receiptPath = Join-Path $runRoot 'verification-receipt.v1.json'
    $isWindowsPlatform = Test-GridIsWindows
    $checks = New-Object System.Collections.Generic.List[object]
    $failureSeen = $false

    Write-Host ("GRID verify: lane={0} configuration={1} checks={2}" -f $Lane,$Configuration,$suites.Count)
    foreach ($suite in $suites) {
        $spec = Get-GridCommandSpec -Suite $suite -RepositoryRoot $repositoryRoot -Configuration $Configuration
        $checkStart = [DateTimeOffset]::UtcNow
        $safeId = ([string]$suite.id -replace '[^A-Za-z0-9._-]','_')
        $logRelative = 'logs/{0:D3}-{1}.log' -f [int]$suite.order,$safeId
        $logPath = Join-Path $runRoot ($logRelative -replace '/', [System.IO.Path]::DirectorySeparatorChar)

        if ($failureSeen -and -not $ContinueOnFailure) {
            $checks.Add((New-GridReceiptCheck -Suite $suite -CommandIdentity $spec.Identity -Configuration $spec.ReceiptConfiguration -StartedAtUtc $checkStart.ToString('o') -DurationMs 0 -ExitCode $null -Result 'NotRun' -LogPath $null))
            continue
        }
        if ([string]$suite.platform -eq 'Windows' -and -not $isWindowsPlatform) {
            $checks.Add((New-GridReceiptCheck -Suite $suite -CommandIdentity $spec.Identity -Configuration $spec.ReceiptConfiguration -StartedAtUtc $checkStart.ToString('o') -DurationMs 0 -ExitCode $null -Result 'SkippedUnsupportedPlatform' -LogPath $null))
            Write-Host ("SKIP  {0}" -f $suite.id)
            continue
        }

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $exitCode = 1
        try {
            $commandResult = Invoke-GridExternalCommand -FileName $spec.FileName -Arguments $spec.Arguments -Environment $spec.Environment -LogPath $logPath -WorkingDirectory $repositoryRoot
            $exitCode = [int]$commandResult.ExitCode
        } catch {
            [System.IO.File]::WriteAllText($logPath, ($_ | Out-String), (New-Object System.Text.UTF8Encoding($false)))
            $exitCode = 1
        }
        $sw.Stop()
        $result = if ($exitCode -eq 0) { 'Passed' } else { 'Failed' }
        $checks.Add((New-GridReceiptCheck -Suite $suite -CommandIdentity $spec.Identity -Configuration $spec.ReceiptConfiguration -StartedAtUtc $checkStart.ToString('o') -DurationMs $sw.ElapsedMilliseconds -ExitCode $exitCode -Result $result -LogPath $logRelative))
        if ($result -eq 'Passed') { Write-Host ("PASS  {0} ({1} ms)" -f $suite.id,$sw.ElapsedMilliseconds) }
        else { Write-Host ("FAIL  {0} ({1} ms) -> {2}" -f $suite.id,$sw.ElapsedMilliseconds,$logRelative); $failureSeen = $true }
    }

    $finished = [DateTimeOffset]::UtcNow
    $overall = if (@($checks | Where-Object { $_.result -eq 'Failed' }).Count -gt 0) { 'Failed' } else { 'Passed' }
    $receiptChecks = ConvertTo-GridObjectArray -List $checks
    $receipt = [ordered]@{
        schemaVersion = 1
        receiptId = $receiptId
        manifestId = [string]$manifest.manifestId
        lane = $Lane
        configuration = $Configuration
        startedAtUtc = $started.ToString('o')
        finishedAtUtc = $finished.ToString('o')
        durationMs = [int64]($finished - $started).TotalMilliseconds
        platform = [ordered]@{
            os = [System.Environment]::OSVersion.VersionString
            isWindows = $isWindowsPlatform
            architecture = Get-GridOsArchitecture
        }
        dotnetSdk = Get-GridDotNetSdkVersion
        powerShellVersion = $PSVersionTable.PSVersion.ToString()
        overallResult = $overall
        checks = $receiptChecks
    }
    $validation = Test-GridReceiptObject -Receipt $receipt
    if (-not $validation.Valid) { throw ('Receipt validation failed: ' + ($validation.Errors -join '; ')) }
    $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $receiptPath -Encoding UTF8

    $passed = @($checks | Where-Object { $_.result -eq 'Passed' }).Count
    $failed = @($checks | Where-Object { $_.result -eq 'Failed' }).Count
    $skipped = @($checks | Where-Object { $_.result -eq 'SkippedUnsupportedPlatform' }).Count
    $notRun = @($checks | Where-Object { $_.result -eq 'NotRun' }).Count
    Write-Host ("GRID verify result={0} passed={1} failed={2} skipped={3} notRun={4}" -f $overall,$passed,$failed,$skipped,$notRun)
    Write-Host ("Receipt: {0}" -f $receiptPath)
    if ($overall -eq 'Failed') { return 1 }
    return 0
}

if ($MyInvocation.InvocationName -ne '.') {
    $exitCode = Invoke-GridVerification -Lane $Lane -Configuration $Configuration -ManifestPath $ManifestPath -ArtifactsRoot $ArtifactsRoot -ContinueOnFailure:$ContinueOnFailure
    exit $exitCode
}
