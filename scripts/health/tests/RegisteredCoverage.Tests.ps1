$ErrorActionPreference = 'Stop'
$healthRoot = Split-Path -Parent $PSScriptRoot

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }

$result = & (Join-Path $healthRoot 'Invoke-GridRegisteredCoverage.ps1') -PassThru
$plan = $result.ExecutionPlan

Assert-Equal 'Preview' $result.Status 'Coverage preview must remain read-only.'
Assert-True (-not $result.Executed) 'Preview must not start a collector.'
Assert-Equal 'SingleWholeProfileBaseline' $plan.mode 'Registered coverage must expose the one-run whole-profile mode.'
Assert-Equal 'grid.class.installation-integrity' $plan.primaryClassId 'CleanHouse Repair must be the sole whole-profile entry point.'
Assert-Equal 1 $plan.plannedRunCount 'Whole-profile coverage must schedule exactly one baseline run.'
Assert-Equal 8 @($plan.coveredClassIds).Count 'The shared baseline must cover all eight baseline-backed Classes without rerunning collection.'
Assert-True ('grid.class.crash-freeze' -in @($plan.coveredClassIds)) 'Crash coverage must reuse the CleanHouse baseline.'
Assert-True ('grid.class.missing-mesh' -in @($plan.coveredClassIds)) 'Missing-mesh coverage must reuse the CleanHouse baseline.'
Assert-True ('grid.class.missing-texture' -in @($plan.coveredClassIds)) 'Missing-texture coverage must reuse the CleanHouse baseline.'
Assert-True ('grid.class.record-conflicts' -in @($plan.coveredClassIds)) 'Whole-profile record-conflict provenance must reuse the CleanHouse baseline.'
Assert-Equal 5 @($plan.deferredClassIds).Count 'Target- or capability-dependent registered Classes must remain explicit and deferred.'
Assert-True ('grid.class.npc-behavior' -in @($plan.deferredClassIds)) 'NPC behavior must not run without an exact actor target.'
Assert-True ('grid.class.world-objects' -in @($plan.deferredClassIds)) 'World-object inspection must not invent a target.'
Assert-True ('grid.class.outfits-bodies-physics' -in @($plan.deferredClassIds)) 'Gameplay capability assessment must not infer its explicit capability selection.'

$toolSelectionRejected = $false
try {
    & (Join-Path $healthRoot 'Invoke-GridRegisteredCoverage.ps1') -InstallationId 'fixture' -ProfileId 'fixture' -ToolIds 'grid.tool.mo2' -Execute -PassThru | Out-Null
} catch {
    $toolSelectionRejected = $_.Exception.Message -match '^CoverageExecutionScopeInvalid:'
}
Assert-True $toolSelectionRejected 'The one-pass command must reject tool reads that have no displayed authorization review.'

$tempRoot = Join-Path $env:TEMP ('grid-registered-coverage-tests-' + [guid]::NewGuid().ToString('N'))
$storeRoot = Join-Path $tempRoot 'store'
$instanceRoot = Join-Path $tempRoot 'instance'
New-Item -ItemType Directory -Path (Join-Path $storeRoot 'connections'), $instanceRoot -Force | Out-Null
try {
    $applicationPath = Join-Path $tempRoot 'missing-ModOrganizer.exe'
    Set-Content -LiteralPath (Join-Path $instanceRoot 'ModOrganizer.ini') -Value "[General]`nselected_profile=Fixture Profile`n" -Encoding UTF8
    $reference = [ordered]@{
        schemaVersion = 1
        id = 'reference.fixture'
        installationId = 'installation.fixture'
        gameId = 'game.skyrim-special-edition'
        adapterId = 'adapter.mod-organizer-2'
        displayName = 'Fixture'
        instanceKind = 'Portable'
        executablePath = $applicationPath
        instanceDirectory = $instanceRoot
    }
    [ordered]@{ schemaVersion = 1; references = @($reference) } | ConvertTo-Json -Depth 10 | `
        Set-Content -LiteralPath (Join-Path $storeRoot 'connections\mo2-installations.v1.json') -Encoding UTF8

    $implicit = & (Join-Path $healthRoot 'Invoke-GridRegisteredCoverage.ps1') -CaseStoreRoot $storeRoot -Execute -PassThru
    Assert-Equal 'Incomplete' $implicit.Status 'A collector failure after implicit context resolution must be reported as incomplete.'
    Assert-Equal 1 @($implicit.Runs).Count 'Implicit context resolution must still schedule exactly one Class request.'
    Assert-Equal 'ReadyToCollect' ([string]$implicit.Runs[0].PlannerStatus) 'One unique persisted installation and selected profile must satisfy the Class planner.'
    Assert-True ([bool]$implicit.Runs[0].Executed) 'The coverage wrapper must reach the collector boundary without caller-supplied IDs.'
    Assert-True ([string]$implicit.Runs[0].Status -ne 'NeedsContext') 'Standalone coverage must not stop at NeedsContext when Grid has one exact persisted selection.'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'PASS: registered coverage plans one CleanHouse baseline and defers target-dependent collectors.'
