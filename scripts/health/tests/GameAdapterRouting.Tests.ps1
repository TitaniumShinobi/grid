$ErrorActionPreference = 'Stop'
$healthRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $healthRoot 'Resolve-GridGameAdapter.ps1')

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }
function Assert-Throws([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    try { & $Action; throw "$Message Expected an exception matching '$Pattern'." }
    catch { if ($_.Exception.Message -notmatch $Pattern) { throw "$Message Unexpected exception: $($_.Exception.Message)" } }
}
function New-FixtureCapability([string]$CapabilityId, [string]$GameId, [string]$File, [string]$EntryPoint) {
    [ordered]@{
        capabilityId = $CapabilityId; capabilityVersion = '1.0.0'; ownerScope = @{ kind = 'Game'; gameId = $GameId }
        implementation = @{ files = @($File); entryPoint = $EntryPoint }
        inputSchema = @{ type = 'object' }; outputSchema = @{ type = 'object' }
        preconditions = @(); dependencies = @(); sideEffectClassification = 'RepositoryRead'
        requiredAuthority = @{ kind = 'RepositoryRead'; description = 'Synthetic repository-only fixture.' }
        rollback = @{ mode = 'NotRequired'; description = 'Synthetic fixture is read-only.' }
        terminalStates = @('Resolved','NotMatched')
    }
}

$tempRoot = Join-Path $env:TEMP ('grid-adapter-routing-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
try {
    # Two entirely synthetic fixture adapters -- no real game identity involved.
    foreach ($id in @('FixtureGameA', 'FixtureGameB')) {
        $gameRoot = Join-Path $tempRoot "games\$id"
        $adapterDirectory = Join-Path $gameRoot 'health'
        New-Item -ItemType Directory -Path $adapterDirectory -Force | Out-Null
        $connectedFlagPath = Join-Path $tempRoot "$id-connected.flag"
        Set-Content -LiteralPath (Join-Path $adapterDirectory 'ConnectedProbe.ps1') -Encoding UTF8 -Value @"
function Test-GridFixtureConnected {
    param(`$InstallRoot)
    return (Test-Path -LiteralPath '$connectedFlagPath' -PathType Leaf)
}
"@
        Set-Content -LiteralPath (Join-Path $adapterDirectory "$id.psm1") -Encoding UTF8 -Value @"
function Invoke-GridFixtureHealthAdapter { param(`$InvestigationPlan) [pscustomobject]@{ Status = 'Complete'; Plan = `$InvestigationPlan } }
Export-ModuleMember -Function Invoke-GridFixtureHealthAdapter
"@
        $lowerId = $id.ToLowerInvariant()
        $invocationId = "grid.game.$lowerId.investigation.collect"
        $contextId = "grid.game.$lowerId.context.probe"
        $capabilityManifest = [ordered]@{
            schemaVersion = 1; ownerScope = @{ kind = 'Game'; gameId = $id }; supportingFiles = @()
            capabilities = @(
                (New-FixtureCapability -CapabilityId $invocationId -GameId $id -File "scripts/games/$id/health/$id.psm1" -EntryPoint 'Invoke-GridFixtureHealthAdapter')
                (New-FixtureCapability -CapabilityId $contextId -GameId $id -File "scripts/games/$id/health/ConnectedProbe.ps1" -EntryPoint 'Test-GridFixtureConnected')
            )
        }
        $capabilityManifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $gameRoot 'capabilities.v1.json') -Encoding UTF8
        $lines = @"
@{
    SchemaVersion = 2
    Id = '$id'
    DisplayName = '$id'
    Module = '$id.psm1'
    InvocationFunction = 'Invoke-GridFixtureHealthAdapter'
    CapabilityManifest = '..\capabilities.v1.json'
    InvocationCapabilityId = '$invocationId'
    ConnectedContextCapabilityId = '$contextId'
    DefaultPipelineCapabilityIds = @('$invocationId')
    PromptMatchers = @('fixturekeyword$lowerId')
    ConnectedContextScript = 'ConnectedProbe.ps1'
    ConnectedContextFunction = 'Test-GridFixtureConnected'
}
"@
        Set-Content -LiteralPath (Join-Path $adapterDirectory 'adapter.psd1') -Encoding UTF8 -Value $lines
    }

    # --- Explicit -Game wins regardless of prompt text or connected context ---
    $resolved = Resolve-GridGameAdapter -ScriptsRoot $tempRoot -Request 'totally unrelated text' -Game 'FixtureGameB'
    Assert-Equal 'FixtureGameB' $resolved.Id 'An explicit -Game must be honored regardless of prompt content.'
    Write-Host 'PASS: explicit -Game routing wins over everything else.'

    # --- Connected context routes correctly even with no prompt keyword present ---
    Set-Content -LiteralPath (Join-Path $tempRoot 'FixtureGameA-connected.flag') -Value 'connected'
    $resolved2 = Resolve-GridGameAdapter -ScriptsRoot $tempRoot -Request 'a synthetic structure is incomplete' -Game 'Auto'
    Assert-Equal 'FixtureGameA' $resolved2.Id 'A connected installation must route correctly even without any prompt keyword.'
    Write-Host 'PASS: connected-profile routing succeeds without any prompt keyword.'
    Remove-Item -LiteralPath (Join-Path $tempRoot 'FixtureGameA-connected.flag') -Force

    # --- Prompt matcher fallback works when nothing is connected ---
    $resolved3 = Resolve-GridGameAdapter -ScriptsRoot $tempRoot -Request 'this mentions fixturekeywordfixturegameb explicitly' -Game 'Auto'
    Assert-Equal 'FixtureGameB' $resolved3.Id 'Prompt matcher fallback must resolve when no adapter is connected.'
    Write-Host 'PASS: prompt-matcher fallback resolves when nothing is connected.'

    # --- Ambiguous: both adapters connected simultaneously must refuse, not guess ---
    Set-Content -LiteralPath (Join-Path $tempRoot 'FixtureGameA-connected.flag') -Value 'connected'
    Set-Content -LiteralPath (Join-Path $tempRoot 'FixtureGameB-connected.flag') -Value 'connected'
    Assert-Throws { Resolve-GridGameAdapter -ScriptsRoot $tempRoot -Request 'no keyword here' -Game 'Auto' } 'AmbiguousGameContext' 'Two simultaneously connected adapters must refuse rather than guess.'
    Remove-Item -LiteralPath (Join-Path $tempRoot 'FixtureGameA-connected.flag'), (Join-Path $tempRoot 'FixtureGameB-connected.flag') -Force
    Write-Host 'PASS: simultaneous connected contexts refuse with AmbiguousGameContext instead of guessing.'

    # --- No signal at all must refuse, not guess ---
    Assert-Throws { Resolve-GridGameAdapter -ScriptsRoot $tempRoot -Request 'nothing recognizable at all' -Game 'Auto' } 'AmbiguousGameContext' 'No explicit game, connection, case, or keyword must refuse rather than guess.'
    Write-Host 'PASS: no routing signal at all refuses with AmbiguousGameContext instead of guessing.'

    # --- Existing case context resolves game continuation ---
    $caseFile = Join-Path $tempRoot 'case.json'
    @{ game = 'FixtureGameA' } | ConvertTo-Json | Set-Content -LiteralPath $caseFile -Encoding UTF8
    $resolved4 = Resolve-GridGameAdapter -ScriptsRoot $tempRoot -Request 'no keyword here' -Game 'Auto' -ExistingCasePath $caseFile
    Assert-Equal 'FixtureGameA' $resolved4.Id 'An existing case must resolve the game via case continuation.'
    Write-Host 'PASS: existing case context resolves game adapter continuation.'

    $command = Get-GridAdapterInvocationCommand -Adapter $resolved4
    Assert-Equal 'Invoke-GridFixtureHealthAdapter' $command.Name 'Manifest InvocationFunction must resolve the adapter entry point.'
    Assert-True $command.Parameters.ContainsKey('InvestigationPlan') 'Adapter entry point must expose its InvestigationPlan contract.'
    Write-Host 'PASS: manifest-driven adapter invocation resolves an InvestigationPlan-aware entry point.'

    $productionScriptsRoot = Split-Path -Parent $healthRoot
    $productionAdapters = @(Get-GridInstalledAdapters -ScriptsRoot $productionScriptsRoot)
    Assert-Equal 2 $productionAdapters.Count 'The production scripts/games tree must expose the GTA V and Skyrim adapters.'
    $gtaAdapter = @($productionAdapters | Where-Object Id -eq 'grandtheftautov')[0]
    $skyrimAdapter = @($productionAdapters | Where-Object Id -eq 'skyrimspecialedition')[0]
    Assert-True ($null -ne $gtaAdapter) 'The production GTA V adapter must be discovered from scripts/games.'
    Assert-True ($null -ne $skyrimAdapter) 'The production Skyrim adapter must be discovered from scripts/games.'
    Assert-True (@($gtaAdapter.CapabilityBindings).Count -gt 1) 'The GTA adapter must expose a dependency-closed capability pipeline.'
    Assert-Equal 'grid.game.grandtheftautov.baseline.collect' $gtaAdapter.BaselineCapabilityId 'The GTA adapter must bind its setup baseline capability.'
    $gtaBaselineCommand = Get-GridAdapterBaselineCommand -Adapter $gtaAdapter
    Assert-Equal 'Invoke-GridGtaBaseline' $gtaBaselineCommand.Name 'The GTA baseline capability contract must resolve its exported entry point.'
    Assert-True $gtaBaselineCommand.Parameters.ContainsKey('InvestigationPlan') 'The GTA baseline entry point must accept the bound InvestigationPlan.'
    Assert-True (@($skyrimAdapter.CapabilityBindings).Count -gt 1) 'The Skyrim adapter must expose a dependency-closed capability pipeline.'
    Assert-Equal 'grid.game.skyrimspecialedition.baseline.collect' $skyrimAdapter.BaselineCapabilityId 'The production adapter must bind the reusable Skyrim baseline capability.'
    Assert-True (@($skyrimAdapter.BaselineCapabilityBindings).Count -gt 1) 'The production adapter must expose the dependency-closed baseline pipeline.'
    $baselineCommand = Get-GridAdapterBaselineCommand -Adapter $skyrimAdapter
    Assert-Equal 'Invoke-GridSkyrimBaseline' $baselineCommand.Name 'The baseline capability contract must resolve its exported entry point.'
    Assert-True $baselineCommand.Parameters.ContainsKey('InvestigationPlan') 'The baseline entry point must accept the bound InvestigationPlan.'
    Write-Host 'PASS: production adapter discovery returns capability-bound GTA V and Skyrim adapters.'
}
finally { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
