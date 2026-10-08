#requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$gameRoot = Split-Path -Parent $PSScriptRoot
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $gameRoot)
. (Join-Path $scriptsRoot 'health\Grid.Evidence.ps1')
Import-Module (Join-Path $gameRoot 'health\Grid.Health.GtaV.psm1') -Force
function Assert-Grid([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-GridThrows([scriptblock]$Action, [string]$Pattern) {
    try { & $Action; throw 'Expected the operation to fail.' }
    catch { if ($_.Exception.Message -eq 'Expected the operation to fail.' -or $_.Exception.Message -notmatch $Pattern) { throw } }
}
function Get-FixtureHash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

$fixture = Join-Path $env:TEMP ('grid-gta-crash-' + [guid]::NewGuid().ToString('N'))
try {
    $installRoot = Join-Path $fixture 'SteamLibrary\steamapps\common\Grand Theft Auto V Enhanced'
    $documents = Join-Path $fixture 'Documents'
    $localData = Join-Path $fixture 'LocalAppData'
    $programData = Join-Path $fixture 'ProgramData'
    $caseDirectory = Join-Path $fixture 'case'
    New-Item -ItemType Directory -Path $installRoot, $documents, $localData, $programData, $caseDirectory -Force | Out-Null
    $executable = Join-Path $installRoot 'GTA5_Enhanced.exe'
    Set-Content -LiteralPath $executable -Value 'enhanced executable fixture' -Encoding UTF8
    $manifest = Join-Path $fixture 'SteamLibrary\steamapps\appmanifest_3240220.acf'
    @' 
"AppState"
{
  "appid" "3240220"
  "StateFlags" "4"
  "buildid" "fixture-build"
}
'@ | Set-Content -LiteralPath $manifest -Encoding UTF8
    $enhancedUser = Join-Path $documents 'Rockstar Games\GTAV Enhanced'
    $enhancedCrashes = Join-Path $localData 'Rockstar Games\GTAV Enhanced\CrashLogs'
    $launcher = Join-Path $documents 'Rockstar Games\Launcher'
    New-Item -ItemType Directory -Path (Join-Path $enhancedUser 'Profiles'), $enhancedCrashes, $launcher -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $enhancedUser 'settings.xml') -Value '<settings />' -Encoding UTF8
    @('============ SYSTEM INFO ============','Game State : Game Running','Current Mission : None','Password: excluded fixture value') | Set-Content -LiteralPath (Join-Path $enhancedCrashes 'crashcontext.log') -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $enhancedCrashes 'fixture.dmp') -Value 'fixture dump metadata' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $launcher 'launcher.log') -Value 'fixture launcher log' -Encoding UTF8

    $registration = [pscustomobject][ordered]@{
        schemaVersion = 1; referenceId = 'reference.game.fixture'; installationId = 'installation.game.fixture'
        gameId = 'game.grandtheftautov-enhanced'; adapterId = 'adapter.provider-discovery'; displayName = 'GTA V Enhanced'
        edition = 'Enhanced'; providerId = 'steam'; installRoot = $installRoot; executablePath = $executable
        managerProviderIds = @('vortex'); registeredAtUtc = [datetime]::UtcNow.ToString('o')
    }
    $knownFolders = @{ Documents=$documents; LocalApplicationData=$localData; ProgramData=$programData }
    $context = Resolve-GridGtaRequestContext -ConnectedRegistration $registration -RequestedGameId 'grandtheftautov-enhanced' `
        -RequestedInstallationId 'installation.game.fixture' -KnownFolders $knownFolders
    Assert-Grid ($context.status -eq 'Resolved' -and $context.edition -eq 'Enhanced') 'The exact Enhanced account registration must resolve.'
    Assert-Grid ($context.gameId -eq 'game.grandtheftautov-enhanced' -and $context.engineGameId -eq 'grandtheftautov-enhanced') 'Catalog and engine identities must both be retained.'
    Assert-Grid ($context.profileId -match '^profile\.grid\.[a-f0-9]{20}$') 'The account-owned default profile identity must be deterministic.'
    Assert-Grid (@($context.userDataRoots | Where-Object rootId -eq 'gtavEnhancedCrashDiagnostics').Count -eq 1) 'Enhanced crash diagnostics must be selected.'
    Assert-Grid (@($context.userDataRoots | Where-Object rootId -like 'gtavLegacy*').Count -eq 0) 'Legacy roots must not leak into Enhanced evidence scope.'
    Assert-Grid (@($context.exactReadResources | Where-Object resourceType -eq 'WindowsEventLog').Count -eq 1) 'Logical event-log scope must be explicit.'
    Assert-Grid ($context.authorizedReadPaths -contains (Join-Path $programData 'Microsoft\Windows\WER\ReportArchive')) 'WER archive must be an exact authorized filesystem path.'
    Assert-GridThrows { Resolve-GridGtaRequestContext -ConnectedRegistration ($registration.PSObject.Copy()) -RequestedGameId 'grandtheftautov-legacy' -KnownFolders $knownFolders } 'BindingMismatch'
    $absentRegistration = $registration.PSObject.Copy()
    $absentRegistration.installRoot = Join-Path $fixture 'not-present\Grand Theft Auto V Enhanced'
    $absentRegistration.executablePath = Join-Path $absentRegistration.installRoot 'GTA5_Enhanced.exe'
    $scopeOnly = Resolve-GridGtaRequestContext -ConnectedRegistration $absentRegistration -KnownFolders $knownFolders
    Assert-Grid ($scopeOnly.status -eq 'Resolved' -and -not (Test-Path -LiteralPath $scopeOnly.gameRoot)) 'Pre-authorization scope resolution must not require or read external files.'
    Assert-GridThrows { Get-GridGtaCrashInvestigationEvidence -Context $scopeOnly -KnownFolders $knownFolders } 'GtaInstallationNotFound'

    $applicationItem = New-GridEvidenceItem -Parameter 'windowsApplicationFailureEvent' -Value 1 -Claim 'A fixture Application Error was recorded for the exact executable.' `
        -SourceType 'WindowsEventLog' -SourceIdentifier 'windows-event://Application/fixture' -ContextFingerprint $context.contextFingerprint `
        -VerificationStatus Collected -CollectorName 'Grid.Windows.ApplicationFailure' -CollectorVersion '1.0.0'
    $applicationItem | Add-Member native ([pscustomobject]@{ eventKind='ApplicationError'; executableName='GTA5_Enhanced.exe'; recordId='fixture' })
    $applicationEvidence = [pscustomobject]@{ status='Complete'; evidence=@($applicationItem); warnings=@() }
    $werZero = New-GridEvidenceItem -Parameter 'windowsErrorReportingEvidence' -Value 0 -Claim 'No fixture WER record was present.' `
        -SourceType 'WindowsEventLogQuery' -SourceIdentifier 'windows-event://Application/wer-fixture' -ContextFingerprint $context.contextFingerprint `
        -VerificationStatus Collected -CollectorName 'Grid.Windows.ErrorReporting' -CollectorVersion '1.0.0'
    $werEvidence = [pscustomobject]@{ status='Complete'; evidence=@($werZero); warnings=@() }
    $moduleZero = New-GridEvidenceItem -Parameter 'runningProcessModules' -Value 0 -Claim 'No exact fixture process was running.' `
        -SourceType 'WindowsProcessObservation' -SourceIdentifier 'windows-process-query://fixture' -ContextFingerprint $context.contextFingerprint `
        -VerificationStatus Collected -CollectorName 'Grid.Windows.ProcessModules' -CollectorVersion '1.0.0'
    $moduleEvidence = [pscustomobject]@{ status='Complete'; evidence=@($moduleZero); warnings=@() }

    $protectedFile = Join-Path $enhancedUser 'Profiles\protected-save.fixture'
    Set-Content -LiteralPath $protectedFile -Value 'must remain unread and unchanged' -Encoding UTF8
    $beforeHashes = @{}
    foreach ($path in @($executable,$manifest,(Join-Path $enhancedUser 'settings.xml'),(Join-Path $enhancedCrashes 'crashcontext.log'),(Join-Path $enhancedCrashes 'fixture.dmp'),(Join-Path $launcher 'launcher.log'),$protectedFile)) { $beforeHashes[$path] = Get-FixtureHash $path }
    $result = Invoke-GridGtaCrashInvestigation -Request 'A user-reported repeatable crash.' -CaseId 'case.fixture' -CaseDirectory $caseDirectory `
        -ResolvedContext $context -KnownFolders $knownFolders -ApplicationFailureEvidence $applicationEvidence `
        -WindowsErrorReportingEvidence $werEvidence -ProcessModuleEvidence $moduleEvidence
    Assert-Grid ($result.Status -eq 'Completed' -and $result.TerminalState -eq 'EvidencePartial') 'Historical evidence must complete with a bounded unresolved result, not a diagnosis.'
    Assert-Grid (-not $result.ChangedExternalState) 'Crash investigation must remain read-only.'
    Assert-Grid (@($result.Evidence | Where-Object parameter -eq 'windowsApplicationFailureEvent').Count -eq 1) 'Shared event evidence must retain its original receipt-bearing identity.'
    Assert-Grid (@($result.Assessment.assertions | Where-Object classification -eq 'ProvenCause').Count -eq 0) 'Presence evidence must never become a proven cause.'
    Assert-Grid (@($result.Assessment.assertions | Where-Object classification -eq 'SupportedHypothesis').Count -eq 0) 'No unsupported hypothesis may be created.'
    Assert-Grid ($result.DiagnosticResult.result.finding.status -eq 'Unresolved') 'The public finding must remain unresolved.'
    $crashObservation = @($result.EvidencePackage.declaredArtifacts | Where-Object rootId -eq 'gtavEnhancedCrashDiagnostics' | ForEach-Object files | Where-Object name -eq 'crashcontext.log')[0]
    Assert-Grid (@($crashObservation.diagnosticLines).Count -ge 2) 'Bounded GTA log parsing must retain structural diagnostic records.'
    Assert-Grid (-not ((@($crashObservation.diagnosticLines.text) -join "`n") -match 'excluded fixture value')) 'Sensitive-looking log fields must not enter evidence.'
    Assert-Grid (@(Get-ChildItem -LiteralPath $caseDirectory -Force).Count -eq 0) 'The adapter callback must not create or seal a second case.'
    foreach ($path in $beforeHashes.Keys) { Assert-Grid ((Get-FixtureHash $path) -eq $beforeHashes[$path]) "Read-only investigation changed '$path'." }
    Assert-Grid (-not (@($result.Evidence | ConvertTo-Json -Depth 20) -match 'protected-save\.fixture')) 'Protected profile/save contents must never enter evidence.'

    Write-Host 'PASS: GTA V crash investigation contracts passed.'
}
finally {
    Remove-Module Grid.Health.GtaV -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
