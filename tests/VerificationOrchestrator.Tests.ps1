$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repoRoot = Split-Path -Parent $PSScriptRoot
$runner = Join-Path $repoRoot 'eng/Test-Grid.ps1'
. $runner

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "VerificationOrchestratorFailed: $Message" }
}
function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    if ($Expected -ne $Actual) { throw "VerificationOrchestratorFailed: $Message Expected '$Expected', got '$Actual'." }
}
function Assert-ThrowsContains {
    param([scriptblock]$Action, [string]$Needle, [string]$Message)
    try { & $Action; throw "VerificationOrchestratorFailed: $Message Expected exception." }
    catch {
        if ($_.Exception.Message -like 'VerificationOrchestratorFailed:*Expected exception.*') { throw }
        Assert-True ($_.Exception.Message.IndexOf($Needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) "$Message Actual: $($_.Exception.Message)"
    }
}

$manifestPath = Join-Path $repoRoot 'eng/verification.manifest.v1.json'
$manifest = Read-GridVerificationManifest -Path $manifestPath -RepositoryRoot $repoRoot

# Discovery/inventory: every checked-in shared-health and game PowerShell suite is registered exactly once.
$registeredPowerShell = @($manifest.suites | Where-Object { $_.kind -eq 'PowerShell' } | ForEach-Object { ([string]$_.path).Replace('\\','/') })
$expectedPowerShell = @('tests/ArchitectureConformance.Tests.ps1','tests/VerificationOrchestrator.Tests.ps1','tests/Provenance.Tests.ps1')
$expectedPowerShell += @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'scripts/health/tests') -Filter '*.Tests.ps1' -File | Sort-Object Name | ForEach-Object { 'scripts/health/tests/' + $_.Name })
$gamesRoot = Join-Path $repoRoot 'scripts/games'
$expectedPowerShell += @(Get-ChildItem -LiteralPath $gamesRoot -Directory | Sort-Object Name | ForEach-Object {
    $gameName = $_.Name
    $testsRoot = Join-Path $_.FullName 'tests'
    if (Test-Path -LiteralPath $testsRoot -PathType Container) {
        Get-ChildItem -LiteralPath $testsRoot -Filter '*.Tests.ps1' -File | Sort-Object Name | ForEach-Object {
            'scripts/games/' + $gameName + '/tests/' + $_.Name
        }
    }
})
Assert-Equal $expectedPowerShell.Count $registeredPowerShell.Count 'Manifest must register every expected PowerShell suite exactly once.'
foreach ($path in $expectedPowerShell) { Assert-True ($registeredPowerShell -contains $path) "Manifest is missing PowerShell suite '$path'." }

foreach ($project in @(
    'tests/Grid.Core.Tests/Grid.Core.Tests.csproj',
    'tests/Grid.Mo2.Tests/Grid.Mo2.Tests.csproj',
    'tests/Grid.DevLauncher.Tests/Grid.DevLauncher.Tests.csproj',
    'tests/Grid.App.UiTests/Grid.App.UiTests.csproj'
)) {
    Assert-True (@($manifest.suites | Where-Object { ([string]$_.path).Replace('\\','/') -eq $project }).Count -ge 1) "Manifest is missing executable project '$project'."
}

# Ordering is manifest-owned and stable even when fixture JSON is physically out of order.
$validFixturePath = Join-Path $repoRoot 'tests/fixtures/verification/manifest.valid.json'
$validFixture = Read-GridVerificationManifest -Path $validFixturePath -RepositoryRoot $repoRoot
$ordered = @(Get-GridSuitesForLane -Manifest $validFixture -Lane 'Portable')
Assert-Equal 'fixture-first' $ordered[0].id 'Lane selection must sort by manifest order.'
Assert-Equal 'fixture-second' $ordered[1].id 'Lane selection must preserve deterministic order.'

# Missing registered suite and duplicate ordering refuse before execution.
$missingFixture = Join-Path $repoRoot 'tests/fixtures/verification/manifest.missing-suite.json'
Assert-ThrowsContains { Read-GridVerificationManifest -Path $missingFixture -RepositoryRoot $repoRoot } 'Registered verification suite is missing' 'Missing registered suite must fail manifest validation.'
$invalidOrderFixture = Join-Path $repoRoot 'tests/fixtures/verification/manifest.invalid-order.json'
Assert-ThrowsContains { Read-GridVerificationManifest -Path $invalidOrderFixture -RepositoryRoot $repoRoot } 'order values must be unique' 'Duplicate ordering must fail manifest validation.'

# Configuration selection must be explicit for build/run/UI commands.
$coreSuite = $manifest.suites | Where-Object { $_.id -eq 'windows-core-tests' } | Select-Object -First 1
$coreDebug = Get-GridCommandSpec -Suite $coreSuite -RepositoryRoot $repoRoot -Configuration 'Debug'
$coreRelease = Get-GridCommandSpec -Suite $coreSuite -RepositoryRoot $repoRoot -Configuration 'Release'
Assert-True ($coreDebug.Identity -match '-c Debug') 'Debug executable-test command identity must name Debug.'
Assert-True ($coreRelease.Identity -match '-c Release') 'Release executable-test command identity must name Release.'

foreach ($suiteId in @('windows-core-tests','windows-mo2-tests','windows-devlauncher-tests')) {
    $suite = $manifest.suites | Where-Object { $_.id -eq $suiteId } | Select-Object -First 1
    $command = Get-GridCommandSpec -Suite $suite -RepositoryRoot $repoRoot -Configuration 'Debug'
    Assert-True ($command.Identity -match [regex]::Escape('-p:Platform=x64')) "$suiteId must execute the x64 output produced by windows-solution-build."
}

$uiSuite = $manifest.suites | Where-Object { $_.id -eq 'windows-ui-tests' } | Select-Object -First 1
$uiDebug = Get-GridCommandSpec -Suite $uiSuite -RepositoryRoot $repoRoot -Configuration 'Debug'
$uiRelease = Get-GridCommandSpec -Suite $uiSuite -RepositoryRoot $repoRoot -Configuration 'Release'
Assert-True ($uiDebug.Identity -match '--debug') 'Debug UI command must pass the explicit Debug selector.'
Assert-True ($uiRelease.Identity -match '--release') 'Release UI command must pass an explicit Release selector.'
Assert-True ($uiDebug.Environment.GRID_UI_EXECUTABLE -match '[\\/]Debug[\\/]') 'Debug UI target path must be configuration-specific.'
Assert-True ($uiRelease.Environment.GRID_UI_EXECUTABLE -match '[\\/]Release[\\/]') 'Release UI target path must be configuration-specific.'


# PowerShell 5.1 regression: Generic.List[object] must materialize without binder errors
# and must remain an object[] for both one-item and multi-item receipts.
$genericChecks = New-Object 'System.Collections.Generic.List[object]'
[void]$genericChecks.Add([pscustomobject]@{ id = 'one' })
$materializedChecks = ConvertTo-GridObjectArray -List $genericChecks
Assert-True ($materializedChecks -is [object[]]) 'Generic receipt checks must materialize as object[].'
Assert-Equal 1 @($materializedChecks).Count 'One-item receipt check lists must preserve cardinality.'
Assert-Equal 'one' $materializedChecks[0].id 'Materialized receipt checks must preserve values.'
[void]$genericChecks.Add([pscustomobject]@{ id = 'two' })
$materializedChecks = ConvertTo-GridObjectArray -List $genericChecks
Assert-Equal 2 @($materializedChecks).Count 'Multi-item receipt check lists must preserve cardinality.'
Assert-Equal 'two' $materializedChecks[1].id 'Materialized receipt checks must preserve order.'

# Receipt validation accepts canonical states and rejects invalid status/order.
$receiptFixture = Get-Content -LiteralPath (Join-Path $repoRoot 'tests/fixtures/verification/receipt.valid.json') -Raw | ConvertFrom-Json
$receiptValidation = Test-GridReceiptObject -Receipt $receiptFixture
Assert-True $receiptValidation.Valid ('Valid receipt fixture was rejected: ' + ($receiptValidation.Errors -join '; '))
$receiptFixture.checks[0].result = 'Unknown'
$badResult = Test-GridReceiptObject -Receipt $receiptFixture
Assert-True (-not $badResult.Valid) 'Receipt validation must reject unknown result states.'

# Schemas themselves must remain parseable JSON and declare v1 constants.
$manifestSchema = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/verification-manifest.v1.schema.json') -Raw | ConvertFrom-Json
$receiptSchema = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/verification-receipt.v1.schema.json') -Raw | ConvertFrom-Json
Assert-Equal 1 $manifestSchema.properties.schemaVersion.const 'Manifest schema must lock version 1.'
Assert-Equal 1 $receiptSchema.properties.schemaVersion.const 'Receipt schema must lock version 1.'

Write-Host 'PASS: GRID verification orchestrator manifest, ordering, refusal, configuration, and receipt checks passed.'
