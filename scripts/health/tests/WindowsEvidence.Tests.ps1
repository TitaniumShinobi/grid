$ErrorActionPreference = 'Stop'
$healthRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $healthRoot 'Grid.Health.psm1') -Force

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }

$context = 'sha256:windows-evidence-fixture'
$since = [datetime]'2026-01-01T00:00:00Z'
$until = [datetime]'2026-02-01T00:00:00Z'
$events = @(
    [pscustomobject]@{ Id=1000; ProviderName='Application Error'; RecordId=101; TimeCreated=[datetime]'2026-01-10T01:02:03Z'; EventData=[ordered]@{ AppName='FixtureGame.exe'; FaultingModuleName='fixture.dll'; ExceptionCode='c0000005' } },
    [pscustomobject]@{ Id=1002; ProviderName='Application Hang'; RecordId=102; TimeCreated=[datetime]'2026-01-11T01:02:03Z'; EventData=[ordered]@{ AppName='OtherGame.exe' } }
)
$application = Get-GridWindowsApplicationFailureEvidence -ExecutableName FixtureGame.exe -SinceUtc $since -UntilUtc $until -ContextFingerprint $context -EventRecords $events
Assert-Equal Complete $application.status 'A bounded fixture event query must complete.'
Assert-Equal 1 @($application.evidence).Count 'Only the exact executable event may be retained.'
Assert-Equal windowsApplicationFailureEvent $application.evidence[0].parameter 'Application evidence must use the registered parameter.'
Assert-Equal Collected $application.evidence[0].verificationStatus 'A stable event record must be collected evidence.'
Assert-Equal 101 $application.evidence[0].native.recordId 'The immutable Windows record identity must be retained.'
Assert-True ($application.evidence[0].native.sourceSha256 -match '^[A-F0-9]{64}$') 'The source record must have a content fingerprint.'

$absence = Get-GridWindowsApplicationFailureEvidence -ExecutableName MissingGame.exe -SinceUtc $since -UntilUtc $until -ContextFingerprint $context -EventRecords $events
Assert-Equal 0 $absence.evidence[0].value 'A bounded exact query with no match must record observed absence, not fabricate an event.'

$tempRoot = Join-Path $env:TEMP ('grid-wer-fixture-' + [guid]::NewGuid().ToString('N'))
try {
    $reportDirectory = Join-Path $tempRoot 'AppCrash_FixtureGame_01234567'
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    @(
        'Version=1',
        'EventType=APPCRASH',
        'AppName=FixtureGame.exe',
        'AppPath=C:\Fixture\FixtureGame.exe',
        'ReportIdentifier=fixture-report-id'
    ) | Set-Content -LiteralPath (Join-Path $reportDirectory 'Report.wer') -Encoding UTF8
    (Get-Item -LiteralPath $reportDirectory).LastWriteTimeUtc = [datetime]'2026-01-12T00:00:00Z'
    $werEvents = @([pscustomobject]@{ Id=1001; ProviderName='Windows Error Reporting'; RecordId=201; TimeCreated=[datetime]'2026-01-12T00:00:01Z'; EventData=[ordered]@{ AppName='FixtureGame.exe'; ReportId='fixture-report-id' } })
    $wer = Get-GridWindowsErrorReportingEvidence -ExecutableName FixtureGame.exe -SinceUtc $since -UntilUtc $until -ContextFingerprint $context -ArchiveRoot $tempRoot -EventRecords $werEvents
    Assert-Equal Complete $wer.status 'Readable fixture WER sources must complete.'
    Assert-Equal 1 @($wer.evidence | Where-Object parameter -eq 'windowsErrorReportingEvent').Count 'The exact WER event must be retained.'
    $reportEvidence = @($wer.evidence | Where-Object parameter -eq 'windowsErrorReportingArchive')
    Assert-Equal 1 $reportEvidence.Count 'The exact retained report must be retained.'
    Assert-True $reportEvidence[0].native.identityConfirmed 'The report identity must come from Report.wer content.'
    Assert-Equal Readable $reportEvidence[0].native.readabilityStatus 'Stable reports must be explicitly readable.'
    Assert-True ($reportEvidence[0].native.reportSha256 -match '^[A-F0-9]{64}$') 'Readable reports must have a content fingerprint.'
}
finally { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }

$processFixture = [pscustomobject]@{
    Id = 301
    Path = 'C:\Fixture\FixtureGame.exe'
    StartTime = [datetime]'2026-01-10T00:00:00Z'
    Modules = @(
        [pscustomobject]@{ ModuleName='FixtureGame.exe'; FileName='C:\Fixture\FixtureGame.exe'; FileVersionInfo=[pscustomobject]@{FileVersion='1.2.3.4';ProductVersion='1.2.3.4'} },
        [pscustomobject]@{ ModuleName='fixture.dll'; FileName='C:\Fixture\fixture.dll'; FileVersionInfo=[pscustomobject]@{FileVersion='2.0.0.0';ProductVersion='2.0.0.0'} }
    )
}
$modules = Get-GridWindowsProcessModuleEvidence -ExecutablePath 'C:\Fixture\FixtureGame.exe' -ContextFingerprint $context -ProcessRecords @($processFixture)
Assert-Equal Complete $modules.status 'A supplied stable exact-path process observation must complete.'
Assert-Equal 2 $modules.evidence[0].native.moduleCount 'The bounded module membership must be preserved.'
Assert-True (-not $modules.scope.startsProcess -and -not $modules.scope.controlsProcess) 'The module collector contract must explicitly prohibit process launch and control.'

$noProcess = Get-GridWindowsProcessModuleEvidence -ExecutablePath 'C:\Fixture\FixtureGame.exe' -ContextFingerprint $context -ProcessRecords @()
Assert-Equal 0 $noProcess.evidence[0].value 'An absent exact process must be recorded without starting it.'
Assert-Equal Collected $noProcess.evidence[0].verificationStatus 'Observed process absence is collected evidence, not an error.'

Write-Host 'PASS: bounded Windows event, WER, and already-running process-module evidence contracts.'
