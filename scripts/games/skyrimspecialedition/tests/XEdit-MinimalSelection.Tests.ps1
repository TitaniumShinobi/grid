$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent $PSScriptRoot
$collectorRoot = Join-Path $gameRoot 'health\collectors'
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $gameRoot)
Import-Module (Join-Path $scriptsRoot 'health\Grid.Health.psm1') -Force
. (Join-Path $collectorRoot 'Get-GridMO2Context.ps1')
. (Join-Path $collectorRoot 'New-GridXEditPluginSelection.ps1')
. (Join-Path $collectorRoot 'Invoke-GridSkyrimBaseline.ps1')
. (Join-Path $collectorRoot 'New-GridXEditQuery.ps1')
. (Join-Path $collectorRoot 'Get-GridXEditLoaderProgress.ps1')
. (Join-Path $collectorRoot 'Start-GridXEditProbe.ps1')

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }
function Assert-Throws([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    try { & $Action; throw "$Message Expected an exception matching '$Pattern'." }
    catch { if ($_.Exception.Message -notmatch $Pattern) { throw "$Message Unexpected exception: $($_.Exception.Message)" } }
}
function Write-TestPlugin([string]$Path, [string[]]$Masters) {
    $dataStream = New-Object IO.MemoryStream
    $dataWriter = New-Object IO.BinaryWriter($dataStream, [Text.Encoding]::ASCII, $true)
    try {
        foreach ($master in $Masters) {
            $bytes = [Text.Encoding]::GetEncoding(28591).GetBytes($master + [char]0)
            $dataWriter.Write([Text.Encoding]::ASCII.GetBytes('MAST')); $dataWriter.Write([uint16]$bytes.Length); $dataWriter.Write($bytes)
        }
        $dataWriter.Flush(); $data = $dataStream.ToArray()
    } finally { $dataWriter.Dispose(); $dataStream.Dispose() }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $writer = New-Object IO.BinaryWriter($stream, [Text.Encoding]::ASCII, $false)
    try { $writer.Write([Text.Encoding]::ASCII.GetBytes('TES4')); $writer.Write([uint32]$data.Length); $writer.Write((New-Object byte[] 16)); $writer.Write($data) }
    finally { $writer.Dispose() }
}

$tempRoot = Join-Path $env:TEMP ('grid xedit selection tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
try {
    $recordFixture = Join-Path $tempRoot 'RuntimeReferenceFixture.esp'
    $stream = [IO.File]::Open($recordFixture, 'CreateNew', 'Write', 'None')
    try {
        $writer = New-Object IO.BinaryWriter($stream, [Text.Encoding]::ASCII, $true)
        try {
            $writer.Write([Text.Encoding]::ASCII.GetBytes('TES4')); $writer.Write([uint32]0); $writer.Write((New-Object byte[] 16))
            $writer.Write([Text.Encoding]::ASCII.GetBytes('GRUP')); $writer.Write([uint32]58); $writer.Write((New-Object byte[] 16))
            $writer.Write([Text.Encoding]::ASCII.GetBytes('REFR')); $writer.Write([uint32]10); $writer.Write([uint32]0)
            $writer.Write([uint32]0x01000042); $writer.Write((New-Object byte[] 8))
            $writer.Write([Text.Encoding]::ASCII.GetBytes('NAME')); $writer.Write([uint16]4); $writer.Write([uint32]0x00001234)
        }
        finally { $writer.Dispose() }
    }
    finally { $stream.Dispose() }
    $recordMatch = @(Get-GridBaselineTes4RecordMatches -PluginPath $recordFixture -LocalFormIds @([uint32]0x42))
    Assert-Equal 1 $recordMatch.Count 'Bounded installed-record verification must resolve one exact local FormID.'
    Assert-Equal 'REFR' $recordMatch[0].signature 'Installed-record evidence must retain the TES4 record signature.'
    Assert-Equal ([uint32]0x1234) ([uint32]$recordMatch[0].baseObjectFormId) 'Installed-record evidence must resolve the REFR base object.'

    Assert-Throws { Get-GridMO2Context -Mo2Root $tempRoot -Profile '..\outside' -CaseDirectory $tempRoot } 'immediate MO2 profile-directory name' 'Profile traversal must refuse before profile evidence is read.'
    $names = @('Skyrim.esm', 'Update.esm', 'FixtureBase.esp', 'FixtureFeature.esp', 'Unrelated.esp')
    $paths = @{}; foreach ($name in $names) { $paths[$name] = Join-Path $tempRoot $name }
    Write-TestPlugin $paths['Skyrim.esm'] @()
    Write-TestPlugin $paths['Update.esm'] @('Skyrim.esm')
    Write-TestPlugin $paths['FixtureBase.esp'] @('Skyrim.esm', 'Update.esm')
    Write-TestPlugin $paths['FixtureFeature.esp'] @('FixtureBase.esp')
    Write-TestPlugin $paths['Unrelated.esp'] @('Skyrim.esm')

    $closure = Resolve-GridPluginDependencyClosure -Targets @('FixtureFeature.esp') -PluginPaths $paths -LoadOrder $names -ActivePlugins $names
    Assert-Equal 'Skyrim.esm|Update.esm|FixtureBase.esp|FixtureFeature.esp' ([string]::Join('|', $closure.Plugins)) 'Recursive closure must preserve profile order.'
    Write-Host 'PASS: recursive master closure preserves active profile order.'

    $missing = @{} + $paths; $missing.Remove('Update.esm')
    Assert-Throws { Resolve-GridPluginDependencyClosure -Targets @('FixtureBase.esp') -PluginPaths $missing -LoadOrder $names -ActivePlugins $names } 'Required master.*Update\.esm.*could not be found' 'Missing master must refuse.'
    Assert-Throws { Resolve-GridPluginDependencyClosure -Targets @('FixtureFeature.esp') -PluginPaths $paths -LoadOrder $names -ActivePlugins @('Skyrim.esm', 'Update.esm', 'FixtureBase.esp') } 'inactive or absent' 'Inactive target must refuse.'
    Write-Host 'PASS: missing masters and inactive targets refuse collection.'

    $caseDirectory = Join-Path $tempRoot 'case'
    $context = [pscustomobject]@{ ActivePlugins = $names; InactivePlugins = @(); LoadOrder = $names; PluginPaths = $paths; Fingerprint = 'sha256:fixture' }
    $selection = New-GridXEditPluginSelection -Context $context -CaseDirectory $caseDirectory -Targets @('FixtureFeature.esp')
    Assert-Equal 4 @($selection.Plugins).Count 'Selection must contain only target dependency closure.'
    Assert-True (-not (@($selection.Plugins) -contains 'Unrelated.esp')) 'Unrelated active plugin must not enter selection.'
    Assert-True (@($selection.Plugins).Count -lt $names.Count) 'Generated list must not equal the full profile.'
    Write-Host 'PASS: case-local selection excludes unrelated active plugins.'

    $query = New-GridXEditQuery -Queries @([pscustomobject]@{ operation = 'FindRecordByFormId'; plugin = 'FixtureFeature.esp'; formId = '01000ABC'; editorId = '' }) -CaseDirectory $caseDirectory -ContextFingerprint $context.Fingerprint
    Assert-True (Test-Path -LiteralPath $query.JsonPath) 'Structured query JSON must be case-local.'
    Assert-True (Test-Path -LiteralPath $query.TransportPath) 'Pascal transport must be case-local.'
    Assert-Throws { New-GridXEditQuery -Queries @([pscustomobject]@{ operation = 'SetRecord'; plugin = 'FixtureFeature.esp'; formId = '01000ABC' }) -CaseDirectory $caseDirectory -ContextFingerprint $context.Fingerprint } 'Unsupported or mutating' 'Mutating query must refuse.'
    $scriptedQuery = New-GridXEditQuery -Queries @('InspectReferenceLinks','InspectVmad','TraceOverrideChain','InspectScriptedReference' | ForEach-Object { [pscustomobject]@{ operation=$_; plugin='FixtureFeature.esp'; formId='01000ABC'; editorId='' } }) -CaseDirectory (Join-Path $caseDirectory 'scripted') -ContextFingerprint $context.Fingerprint
    Assert-Equal 4 @($scriptedQuery.Queries).Count 'Scripted-reference query vocabulary must be accepted.'
    Assert-GridXEditQuerySelectionBinding -Context $context -QueryPackage $query -Selection $selection
    $staleQuery = [pscustomobject]@{ ContextFingerprint = 'sha256:stale'; Queries = $query.Queries }
    Assert-Throws { Assert-GridXEditQuerySelectionBinding -Context $context -QueryPackage $staleQuery -Selection $selection } 'stale' 'Stale query packages must refuse before launch.'
    $outsideQuery = [pscustomobject]@{ ContextFingerprint = $context.Fingerprint; Queries = @([pscustomobject]@{ plugin = 'Unrelated.esp' }) }
    Assert-Throws { Assert-GridXEditQuerySelectionBinding -Context $context -QueryPackage $outsideQuery -Selection $selection } 'outside the selected' 'Queries outside the target/master closure must refuse before launch.'
    Write-Host 'PASS: bounded query builder accepts only the read-only vocabulary.'

    $ini = Join-Path $tempRoot 'ModOrganizer.ini'
    $x86 = Join-Path $tempRoot 'SSEEdit.exe'; [IO.File]::WriteAllBytes($x86, [byte[]]@(0))
    Set-Content -LiteralPath $ini -Encoding UTF8 -Value @('[customExecutables]', 'size=1', '1\title=SSEEdit', "1\binary=$x86")
    Assert-Throws { Get-GridMo2ConfiguredExecutable -ConfigurationPath $ini -Title 'SSEEdit' } 'requires SSEEdit64\.exe' 'x86 name must refuse.'
    $x64 = Join-Path $tempRoot 'SSEEdit64.exe'; [IO.File]::WriteAllBytes($x64, [byte[]]@(0))
    Set-Content -LiteralPath $ini -Encoding UTF8 -Value @('[customExecutables]', 'size=1', '1\title=SSEEdit', "1\binary=$x64")
    Assert-Equal $x64 (Get-GridMo2ConfiguredExecutable -ConfigurationPath $ini -Title 'SSEEdit').Binary 'SSEEdit64 entry must resolve.'
    $outputPath = Join-Path $caseDirectory 'xedit-evidence.tsv'
    $statusPath = Join-Path $caseDirectory 'xedit-status.tsv'
    $invocation = New-GridMo2XEditInvocation -ExecutableTitle 'SSEEdit' -PluginsPath $selection.PluginsPath -QueryPath $query.TransportPath -OutputPath $outputPath -StatusPath $statusPath
    Assert-True ($invocation.XEditArguments -match '^-P:".+plugins\.txt" -autoload -autoexit -script:"Trace-GridReference\.pas" ') 'Invocation must bind autoload and native auto-exit to a case-local -P list and generic script.'
    $quotedQuery = '-gridquery:' + [char]34 + $query.TransportPath + [char]34
    $quotedOutput = '-gridoutput:' + [char]34 + $outputPath + [char]34
    $quotedStatus = '-gridstatus:' + [char]34 + $statusPath + [char]34
    Assert-True ($invocation.XEditArguments.Contains($quotedQuery)) 'Invocation must carry the query path across MO2 forwarding.'
    Assert-True ($invocation.XEditArguments.Contains($quotedOutput) -and $invocation.XEditArguments.Contains($quotedStatus)) 'Invocation must carry output/status paths across MO2 forwarding.'
    Assert-True (-not $invocation.ArgumentString.Contains(' -p ')) 'Invocation must not mutate selected profile.'
    Assert-Throws { New-GridMo2XEditInvocation -ExecutableTitle 'SSEEdit' -PluginsPath $selection.PluginsPath -QueryPath 'relative.tsv' -OutputPath $outputPath -StatusPath $statusPath } 'must be absolute' 'Relative forwarding paths must refuse.'
    Assert-Throws { New-GridMo2XEditInvocation -ExecutableTitle 'SSEEdit' -PluginsPath $selection.PluginsPath -QueryPath (Join-Path $tempRoot 'outside.tsv') -OutputPath $outputPath -StatusPath $statusPath } 'same case directory' 'Paths outside the case must refuse.'
    Write-Host "PASS: SSEEdit64 enforcement and safe invocation: $($invocation.ArgumentString)"

    $argumentRecorder = Join-Path $tempRoot 'GridArgumentRecorder.exe'
    $argumentOutput = Join-Path $tempRoot 'received arguments.txt'
    $argumentRecorderSource = @'
using System;
using System.IO;
public static class GridArgumentRecorder
{
    public static void Main(string[] args)
    {
        File.WriteAllLines(Environment.GetEnvironmentVariable("GRID_ARGUMENT_OUTPUT"), args);
    }
}
'@
    Add-Type -TypeDefinition $argumentRecorderSource -Language CSharp -OutputAssembly $argumentRecorder -OutputType ConsoleApplication
    $env:GRID_ARGUMENT_OUTPUT = $argumentOutput
    try {
        $argumentProcess = Start-GridProcessWithSerializedArguments -FilePath $argumentRecorder -ArgumentString $invocation.ArgumentString -WorkingDirectory $tempRoot
        Assert-True ($argumentProcess.WaitForExit(10000)) 'Synthetic argument recorder must exit within ten seconds.'
        Assert-Equal 0 $argumentProcess.ExitCode 'Synthetic argument recorder must exit successfully.'
        $receivedArguments = @([IO.File]::ReadAllLines($argumentOutput))
    }
    finally { Remove-Item Env:\GRID_ARGUMENT_OUTPUT -ErrorAction SilentlyContinue }
    Assert-Equal 5 $receivedArguments.Count 'MO2 launch must receive exactly five arguments.'
    Assert-Equal 'run' $receivedArguments[0] 'MO2 argument 1 must select run.'
    Assert-Equal '-e' $receivedArguments[1] 'MO2 argument 2 must introduce the executable title.'
    Assert-Equal 'SSEEdit' $receivedArguments[2] 'MO2 argument 3 must be the configured executable title.'
    Assert-Equal '-a' $receivedArguments[3] 'MO2 argument 4 must introduce one forwarded payload.'
    Assert-Equal $invocation.XEditArguments $receivedArguments[4] 'MO2 argument 5 must preserve the complete xEdit payload as one argument.'
    Assert-True ($receivedArguments[4].Contains('-script:"Trace-GridReference.pas"')) 'The forwarded payload must preserve the explicit Pascal script switch.'
    foreach ($requiredSwitch in @('-P:', '-autoload', '-autoexit', '-gridquery:', '-gridoutput:', '-gridstatus:', '-gridmaxtraversal:', '-gridmaxrows:', '-gridmaxbytes:')) {
        Assert-True ($receivedArguments[4].Contains($requiredSwitch)) "The forwarded payload must preserve $requiredSwitch."
    }
    Write-Host 'PASS: the real process boundary receives five exact MO2 arguments and one intact xEdit payload.'

    $launchBoundary = [datetime]::UtcNow
    $managerProcessId = 9001
    $staleExact = [pscustomobject]@{ Id = 101; Path = $x64; StartTime = $launchBoundary.AddMinutes(-1); AncestorProcessIds = @($managerProcessId) }
    $recentWrong = [pscustomobject]@{ Id = 102; Path = (Join-Path $tempRoot 'Other\SSEEdit64.exe'); StartTime = $launchBoundary.AddSeconds(1); AncestorProcessIds = @($managerProcessId) }
    $recentExact = [pscustomobject]@{ Id = 103; Path = $x64; StartTime = $launchBoundary.AddSeconds(1); AncestorProcessIds = @($managerProcessId) }
    $secondRecentExact = [pscustomobject]@{ Id = 104; Path = $x64; StartTime = $launchBoundary.AddSeconds(2); AncestorProcessIds = @($managerProcessId) }
    $unrelatedRecentExact = [pscustomobject]@{ Id = 105; Path = $x64; StartTime = $launchBoundary.AddSeconds(1); AncestorProcessIds = @(8001) }
    Assert-Equal 'NotObserved' (Resolve-GridXEditLaunchProcess -Candidates @($staleExact) -ConfiguredExecutable $x64 -LaunchBoundaryUtc $launchBoundary -ManagerProcessId $managerProcessId).Status 'A stale same-path SSEEdit process must not bind.'
    Assert-Equal 'NotObserved' (Resolve-GridXEditLaunchProcess -Candidates @($recentWrong) -ConfiguredExecutable $x64 -LaunchBoundaryUtc $launchBoundary -ManagerProcessId $managerProcessId).Status 'A recent wrong-path SSEEdit process must not bind.'
    Assert-Equal 'NotObserved' (Resolve-GridXEditLaunchProcess -Candidates @($unrelatedRecentExact) -ConfiguredExecutable $x64 -LaunchBoundaryUtc $launchBoundary -ManagerProcessId $managerProcessId).Status 'A recent same-path process outside the launched MO2 ancestry must not bind.'
    $prelaunchKey = (Get-GridXEditProcessIdentity -Process $recentExact).IdentityKey
    Assert-Equal 'NotObserved' (Resolve-GridXEditLaunchProcess -Candidates @($recentExact) -ConfiguredExecutable $x64 -LaunchBoundaryUtc $launchBoundary -ManagerProcessId $managerProcessId -PrelaunchIdentityKeys @($prelaunchKey)).Status 'A process present in the prelaunch snapshot must not bind.'
    $bound = Resolve-GridXEditLaunchProcess -Candidates @($staleExact, $recentWrong, $unrelatedRecentExact, $recentExact) -ConfiguredExecutable $x64 -LaunchBoundaryUtc $launchBoundary -ManagerProcessId $managerProcessId
    Assert-Equal 'Bound' $bound.Status 'Exactly one recent configured SSEEdit process must bind.'
    Assert-Equal 103 $bound.Identity.ProcessId 'The resolver must bind only the recent configured process.'
    Assert-Equal 'Ambiguous' (Resolve-GridXEditLaunchProcess -Candidates @($recentExact, $secondRecentExact) -ConfiguredExecutable $x64 -LaunchBoundaryUtc $launchBoundary -ManagerProcessId $managerProcessId).Status 'Multiple recent configured child processes must fail closed as ambiguous.'
    $readyGate = Resolve-GridXEditLaunchProcessGate -Processes @([pscustomobject]@{ Id=201; ProcessName='explorer' })
    Assert-Equal 'Ready' $readyGate.status 'Unrelated processes must not block a bounded xEdit launch.'
    $closedGate = Resolve-GridXEditLaunchProcessGate -Processes @(
        [pscustomobject]@{ Id=3104; ProcessName='SkyrimSE' },
        [pscustomobject]@{ Id=9492; ProcessName='ModOrganizer' },
        [pscustomobject]@{ Id=811; ProcessName='SSEEdit64' }
    )
    Assert-Equal 'NeedsProcessClosure' $closedGate.status 'Any live MO2, Skyrim, SKSE, launcher, or xEdit process must block the collection before launch.'
    Assert-Equal 3 @($closedGate.blockingProcesses).Count 'The process gate must preserve every exact blocker for the UI prompt.'
    Assert-True (-not $closedGate.mutationAuthorized) 'A process-closure prompt must never grant mutation authority.'
    $ownership = @{ ProcessId = 103; ExecutablePath = $x64; StartTime = $recentExact.StartTime }
    Assert-True (Test-GridXEditProcessOwnershipMatch -Ownership $ownership -Process $recentExact) 'Pinned ownership must match the same PID, path, and start time.'
    Assert-True (-not (Test-GridXEditProcessOwnershipMatch -Ownership $ownership -Process $secondRecentExact)) 'Pinned ownership must reject a different PID.'
    $launchReport = Join-Path $tempRoot 'current-launch-report.tsv'
    Set-Content -LiteralPath $launchReport -Value 'fixture' -Encoding UTF8
    (Get-Item -LiteralPath $launchReport).LastWriteTimeUtc = $launchBoundary.AddSeconds(1)
    Assert-True (Test-GridXEditCurrentLaunchReport -ReportPath $launchReport -Ownership $ownership -LaunchBoundaryUtc $launchBoundary) 'A current report is accepted only after process ownership is bound.'
    Assert-True (-not (Test-GridXEditCurrentLaunchReport -ReportPath $launchReport -Ownership $null -LaunchBoundaryUtc $launchBoundary)) 'A report without current-launch ownership must be rejected.'
    (Get-Item -LiteralPath $launchReport).LastWriteTimeUtc = $launchBoundary.AddSeconds(-1)
    Assert-True (-not (Test-GridXEditCurrentLaunchReport -ReportPath $launchReport -Ownership $ownership -LaunchBoundaryUtc $launchBoundary)) 'A stale report must be rejected.'
    $probeSource = Get-Content -Raw -LiteralPath (Join-Path $collectorRoot 'Start-GridXEditProbe.ps1')
    $pollLoop = $probeSource.IndexOf('while ((Get-Date) -lt $deadline)')
    $terminalReportCheck = $probeSource.IndexOf('if (Test-GridXEditCurrentLaunchReport -ReportPath $reportPath -Ownership $xeditOwnership -LaunchBoundaryUtc $launchBoundaryUtc)', $pollLoop)
    $processAncestryRefresh = $probeSource.IndexOf('$resolution = Resolve-GridXEditLaunchProcess', $pollLoop)
    $loaderTelemetrySample = $probeSource.IndexOf('$sample = Get-GridXEditLoaderProgressSample @sampleArgs')
    Assert-True ($terminalReportCheck -gt $pollLoop -and $terminalReportCheck -lt $processAncestryRefresh -and $terminalReportCheck -lt $loaderTelemetrySample) 'A current ownership-bound report must be accepted before refreshing xEdit ancestry or window telemetry.'
    Write-Host 'PASS: xEdit process binding requires launch ancestry and rejects stale, prelaunch, wrong-path, unrelated, and ambiguous candidates.'

    $profilePlugins = Join-Path $tempRoot 'profile-plugins.txt'; Set-Content $profilePlugins '*FixtureFeature.esp'
    $before = (Get-FileHash $profilePlugins -Algorithm SHA256).Hash
    [void](New-GridXEditPluginSelection -Context $context -CaseDirectory (Join-Path $tempRoot 'second-case') -Targets @('FixtureFeature.esp'))
    Assert-Equal $before (Get-FileHash $profilePlugins -Algorithm SHA256).Hash 'Selection must not mutate MO2 profile state.'
    Write-Host 'PASS: selection leaves profile evidence unchanged.'

    $logRoot = Join-Path $tempRoot 'logs'; $logCase = Join-Path $tempRoot 'log-case'
    New-Item -ItemType Directory -Path $logRoot, $logCase | Out-Null
    Set-Content -LiteralPath (Join-Path $logRoot 'SSEEdit.log') -Value 'Fatal: EOutOfMemory while loading modules' -Encoding UTF8
    $captured = @(Copy-GridXEditLaunchLogs -Roots @($logRoot) -CaseDirectory $logCase -StartedAt (Get-Date).AddMinutes(-1))
    Assert-Equal 1 $captured.Count 'Early-exit evidence must capture a recent xEdit log.'
    Assert-Equal 'ModuleLoadingOutOfMemory' (Get-GridXEditFailureKind -LogPaths $captured -CollectorStage '' -Default 'XEditEarlyExit') 'OOM must be distinguished from a generic exit.'
    Set-Content -LiteralPath (Join-Path $logRoot 'SSEEdit.log') -Value 'Error in unit Trace: undeclared identifier' -Encoding UTF8
    $scriptCase = Join-Path $tempRoot 'script-log-case'; New-Item -ItemType Directory -Path $scriptCase | Out-Null
    $captured = @(Copy-GridXEditLaunchLogs -Roots @($logRoot) -CaseDirectory $scriptCase -StartedAt (Get-Date).AddMinutes(-1))
    Assert-Equal 'ScriptFailure' (Get-GridXEditFailureKind -LogPaths $captured -CollectorStage '' -Default 'XEditEarlyExit') 'Script failures must be classified.'
    Write-Host 'PASS: early-exit logs are captured and classified.'
}
finally { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }

