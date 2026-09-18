$ErrorActionPreference = 'Stop'
$healthRoot = Split-Path -Parent $PSScriptRoot
$entryPoint = Join-Path $healthRoot 'Invoke-GridBaseline.ps1'

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }

$tempRoot = Join-Path $env:TEMP ('grid-baseline-dispatch-tests-' + [guid]::NewGuid().ToString('N'))
$storeRoot = Join-Path $tempRoot 'store'
$instanceRoot = Join-Path $tempRoot 'instance'
$applicationRoot = Join-Path $tempRoot 'application'
New-Item -ItemType Directory -Path (Join-Path $storeRoot 'connections'), $instanceRoot, $applicationRoot -Force | Out-Null
try {
    $executablePath = Join-Path $applicationRoot 'ModOrganizer.exe'
    Set-Content -LiteralPath $executablePath -Value 'synthetic executable identity only' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $instanceRoot 'ModOrganizer.ini') -Value "[General]`nselected_profile=Fixture Profile`n" -Encoding UTF8
    $reference = [ordered]@{
        schemaVersion = 1
        id = 'reference.fixture'
        installationId = 'installation.fixture'
        gameId = 'game.skyrim-special-edition'
        adapterId = 'adapter.mod-organizer-2'
        displayName = 'Fixture'
        instanceKind = 'Portable'
        executablePath = $executablePath
        instanceDirectory = $instanceRoot
    }
    [ordered]@{ schemaVersion = 1; references = @($reference) } | ConvertTo-Json -Depth 10 | `
        Set-Content -LiteralPath (Join-Path $storeRoot 'connections\mo2-installations.v1.json') -Encoding UTF8
    $profilePath = [IO.Path]::GetFullPath((Join-Path (Join-Path $instanceRoot 'profiles') 'Fixture Profile')).TrimEnd('\')
    $profileHash = [Security.Cryptography.SHA256]::Create()
    try {
        $profileId = 'profile.mo2.' + ([BitConverter]::ToString($profileHash.ComputeHash([Text.Encoding]::UTF8.GetBytes("reference.fixture`n$profilePath")))).Replace('-', '').ToLowerInvariant().Substring(0, 24)
    } finally { $profileHash.Dispose() }

    $result = & $entryPoint -Request 'Capture a synthetic baseline.' -Game skyrimspecialedition `
        -InstallationId 'installation.fixture' -ProfileId 'Different Profile' -CaseStoreRoot $storeRoot -PassThru
    Assert-Equal 'Failed' $result.Status 'A requested profile that differs from selected_profile must fail before collection.'
    Assert-Equal 'InstallationContextUnresolved' $result.PrimaryFailure.code ("The mismatch must use the stable installation-context primitive. Detail: " + $result.PrimaryFailure.detail)
    Assert-Equal 'ConfigurationRequired' $result.RecoveryDisposition 'Missing or mismatched Grid-owned installation context requires configuration, not an external artifact.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $storeRoot 'cases'))) 'Context failure must not create a case transaction or invoke the collector.'
    . (Join-Path $healthRoot '..\games\skyrimspecialedition\health\collectors\Invoke-GridSkyrimBaseline.ps1')
    $sparseMetadata = '{"name":"Sparse fixture"}' | ConvertFrom-Json
    Assert-Equal $null (Get-GridBaselineOptionalProperty -InputObject $sparseMetadata -Name 'version') 'Omitted optional collector metadata must normalize to null under StrictMode.'
    Assert-Equal 'Sparse fixture' (Get-GridBaselineOptionalProperty -InputObject $sparseMetadata -Name 'name') 'Present collector metadata must remain intact.'
    Write-Host 'PASS: baseline dispatch resolves one bounded persisted reference and refuses profile drift before collection.'

    $collectorResult = & $entryPoint -Request 'Capture a synthetic baseline.' -Game skyrimspecialedition `
        -InstallationId 'installation.fixture' -ProfileId $profileId -CaseStoreRoot $storeRoot -PassThru
    Assert-Equal 'Failed' $collectorResult.Status 'An invalid synthetic executable must fail deterministically at the repository-owned collector boundary.'
    Assert-True ($null -ne $collectorResult.PrimaryFailure) 'Collector failure must expose a stable primaryFailure.'
    Assert-True (-not [string]::IsNullOrWhiteSpace([string]$collectorResult.CaseDirectory)) ('A started collection must preserve a sealed failed case. Detail: ' + $collectorResult.PrimaryFailure.detail)
    . (Join-Path $healthRoot 'Grid.CaseStore.ps1')
    $seal = Test-GridDiagnosticCaseSeal -StoreRoot $storeRoot -CaseDirectory $collectorResult.CaseDirectory
    Assert-True $seal.IsValid ('The failed synthetic collection must be sealed: ' + ($seal.Errors -join '; '))
    Write-Host 'PASS: repository-owned collector failure is preserved as a sealed, tamper-evident case.'

    $predecessorCaseId = Split-Path -Leaf ([string]$collectorResult.CaseDirectory)
    Assert-True (-not [string]::IsNullOrWhiteSpace($predecessorCaseId)) 'The sealed predecessor directory must expose an exact case ID leaf.'
    Assert-Equal ([string]$collectorResult.CaseId) $predecessorCaseId 'The collector result case ID must match its sealed case-directory identity.'
    $resumeResult = & $entryPoint -Request 'Capture a synthetic baseline.' -Game skyrimspecialedition `
        -InstallationId 'installation.fixture' -ProfileId $profileId -CaseStoreRoot $storeRoot `
        -PredecessorCaseId $predecessorCaseId -PassThru
    $resumeCode = if ($resumeResult.PrimaryFailure) { [string]$resumeResult.PrimaryFailure.code } else { '' }
    $resumeDetail = if ($resumeResult.PrimaryFailure -and $resumeResult.PrimaryFailure.PSObject.Properties['detail']) { [string]$resumeResult.PrimaryFailure.detail } else { [string]$resumeResult.Detail }
    Assert-Equal 'ResumeInvalid' $resumeCode 'A predecessor that failed before protected snapshots and a retained collector partition must not be treated as resumable.'
    Assert-True ($resumeDetail -match 'protected-after snapshot is missing|no unique PausedAtCheckpoint run|no unique retained collector NDJSON partition') ('Resume refusal must identify the exact missing resumable primitive. Detail: ' + $resumeDetail)
    Assert-True ([string]::IsNullOrWhiteSpace([string]$resumeResult.CaseDirectory)) 'Resume validation failure must not create or reopen a case directory.'
    Write-Host 'PASS: sealed failures without a verified checkpoint partition are refused as non-resumable.'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
