$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repoRoot = Split-Path -Parent $PSScriptRoot

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "ArchitectureConformanceFailed: $Message" }
}
function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    if ($Expected -ne $Actual) { throw "ArchitectureConformanceFailed: $Message Expected '$Expected', got '$Actual'." }
}
function Get-RepoText {
    param([string]$RelativePath)
    $path = Join-Path $repoRoot $RelativePath
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Required file '$RelativePath' is missing."
    Get-Content -LiteralPath $path -Raw
}
function Assert-Contains {
    param([string]$Text, [string]$Needle, [string]$Message)
    Assert-True ($Text.IndexOf($Needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) $Message
}

$requiredDocs = @(
    'README.md',
    'docs/README.md',
    'docs/architecture.md',
    'docs/CODEX_DIAGNOSTIC_SCRIPTING.md',
    'docs/capability-status.md',
    'docs/ui-authority-boundaries.md',
    'docs/decisions/0001-runtime-investigation-plans.md',
    'docs/decisions/0002-deterministic-diagnostic-results.md',
    'docs/decisions/0003-production-shell-workspace-and-assistant.md',
    'docs/decisions/0004-classes-capabilities-and-execution-authority.md',
    'docs/diagnostics/README.md',
    'docs/diagnostics/architecture.md',
    'docs/diagnostics/capability-contracts.md',
    'docs/diagnostics/investigation-plan.md',
    'docs/diagnostics/diagnostic-result-contract.md',
    'docs/diagnostics/migration.md',
    'docs/diagnostics/remediation-and-rollback.md',
    'docs/diagnostics/root-cause-diagnosis.md',
    'docs/diagnostics/verification.md'
)
foreach ($doc in $requiredDocs) { [void](Get-RepoText $doc) }

# Class registry: exactly 32 routing recipes, each one canonical class.v1.json.
$classRoot = Join-Path $repoRoot 'scripts/health/classes'
Assert-True (Test-Path -LiteralPath $classRoot -PathType Container) 'Canonical Class root is missing.'
$classRecipes = @(Get-ChildItem -LiteralPath $classRoot -Filter 'class.v1.json' -File -Recurse)
Assert-Equal 32 $classRecipes.Count 'Canonical Class registry must contain exactly 32 recipes.'
$classIds = @($classRecipes | ForEach-Object { (Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).classId })
$uniqueClassIds = @($classIds | Sort-Object -Unique)
Assert-Equal 32 $uniqueClassIds.Count 'Class IDs must be unique.'

$planner = Get-RepoText 'scripts/health/Grid.RequestPlanner.ps1'
Assert-Contains $planner 'Class recipes may not contain machine paths, plugin identities, or FormIDs.' 'Planner must reject concrete case data in Class recipes.'
Assert-Contains $planner "if (`$recipes.Count -ne 32)" 'Planner must enforce the 32-Class registry.'
$plannerTests = Get-RepoText 'scripts/health/tests/RequestPlanner.Tests.ps1'
Assert-Contains $plannerTests 'Plain-language edits must never reclassify the request.' 'Planner tests must protect structured Class selection from prose.'
Assert-Contains $plannerTests 'Plain language must not invent tools.' 'Planner tests must protect structured Tool selection from prose.'

# Four-field contract remains exact and ordered.
$resultDoc = Get-RepoText 'docs/diagnostics/diagnostic-result-contract.md'
$headings = @('Affected mod(s):','Mod role(s):','Finding:','Solution:')
$last = -1
foreach ($heading in $headings) {
    $index = $resultDoc.IndexOf($heading, [StringComparison]::Ordinal)
    Assert-True ($index -gt $last) "Four-field result must contain '$heading' in canonical order."
    $last = $index
}
Assert-Contains $resultDoc 'UNRESOLVED' 'Result contract must preserve unresolved semantics.'

# Locked UI baseline must be explicit.
$ui = Get-RepoText 'docs/ui-authority-boundaries.md'
Assert-Contains $ui 'no Home/Games/Workstation tab strip' 'Locked UI must prohibit the obsolete main-panel tab strip.'
Assert-Contains $ui 'circular game thumbnails' 'Locked UI must identify circular game thumbnail selection.'
Assert-Contains $ui 'Library, Search, and Activity are side-panel surfaces' 'Locked UI must keep Library/Search/Activity in the side panel.'
Assert-Contains $ui 'Chat History belongs to the assistant' 'Chat History must belong to the assistant.'
Assert-Contains $ui 'one assistant chat' 'Assistant must remain one chat.'
Assert-Contains $ui 'one composer' 'Assistant must remain one composer.'
Assert-Contains $ui 'GAME/GRID' 'Form-only GAME/GRID rule must be documented.'
Assert-Contains $ui 'not mined' 'Structured selections must not be mined from prose.'
Assert-Contains $ui 'not a diagnostic authority' 'Assistant must not receive diagnostic authority.'

# Current architecture must label obsolete implementation as dormant/superseded rather than current Production law.
$architecture = Get-RepoText 'docs/architecture.md'
Assert-Contains $architecture 'Dormant prototype' 'Architecture must define dormant prototype status.'
Assert-Contains $architecture 'Historical roadmap status' 'Architecture must contain a superseded-roadmap record.'
Assert-Contains $architecture '32 Class recipes' 'Architecture must describe Classes as routing categories.'
Assert-Contains $architecture 'routing and capability category' 'Architecture must describe Classes as routing/capability categories rather than monolithic scripts.'

# Shared/game ownership remains canonical.
$codex = Get-RepoText 'docs/CODEX_DIAGNOSTIC_SCRIPTING.md'
Assert-Contains $codex 'scripts/health/' 'Normative instructions must retain shared health ownership.'
Assert-Contains $codex 'scripts/games/<canonical-game>/' 'Normative instructions must retain game-native ownership.'
Assert-Contains $codex 'A Class recipe is a routing/capability category' 'Normative instructions must describe Class routing correctly.'

# Authorized wrapper/executor relationships are present both in repository and docs.
$pluginExecutor = Join-Path $repoRoot 'scripts/games/skyrimspecialedition/mo2/Set-GridPluginState.ps1'
$pluginWrapper = Join-Path $repoRoot 'scripts/games/skyrimspecialedition/health/actions/Invoke-GridAuthorizedPluginState.ps1'
$repairWrapper = Join-Path $repoRoot 'scripts/games/skyrimspecialedition/health/actions/Invoke-GridAuthorizedModChainRepair.ps1'
Assert-True (Test-Path -LiteralPath $pluginExecutor -PathType Leaf) 'Plugin-state executor is missing.'
Assert-True (Test-Path -LiteralPath $pluginWrapper -PathType Leaf) 'Authorized plugin-state wrapper is missing.'
Assert-True (Test-Path -LiteralPath $repairWrapper -PathType Leaf) 'Authorized mod-chain repair wrapper is missing.'
$remediation = Get-RepoText 'docs/diagnostics/remediation-and-rollback.md'
Assert-Contains $remediation 'Set-GridPluginState.ps1' 'Remediation docs must name the executor.'
Assert-Contains $remediation 'Invoke-GridAuthorizedPluginState.ps1' 'Remediation docs must name the authorized plugin wrapper.'
Assert-Contains $remediation 'Invoke-GridAuthorizedModChainRepair.ps1' 'Remediation docs must name the authorized repair wrapper.'

# Production authority must not be inferred from script existence.
$status = Get-RepoText 'docs/capability-status.md'
Assert-Contains $status 'AI target discovery/evidence/diagnosis' 'Capability matrix must expose the AI prohibition.'
Assert-Contains $status 'Automatic mining of Game/Mods/Tools/Class from prose' 'Capability matrix must expose the prose-mining prohibition.'
Assert-Contains $status 'Historical Home/game-rail/global History architecture' 'Capability matrix must record superseded shell claims.'


# Repository-owned verification orchestrator must remain checked in and documented.
$verificationFiles = @(
    'eng/Test-Grid.ps1',
    'eng/verification.manifest.v1.json',
    'eng/verification-manifest.v1.schema.json',
    'eng/verification-receipt.v1.schema.json',
    'tests/VerificationOrchestrator.Tests.ps1',
    'tests/fixtures/verification/manifest.valid.json',
    'tests/fixtures/verification/manifest.missing-suite.json',
    'tests/fixtures/verification/manifest.invalid-order.json',
    'tests/fixtures/verification/receipt.valid.json'
)
foreach ($file in $verificationFiles) {
    $path = Join-Path $repoRoot $file
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Required verification artifact '$file' is missing."
}
$verificationDoc = Get-RepoText 'docs/diagnostics/verification.md'
Assert-Contains $verificationDoc 'eng/Test-Grid.ps1' 'Verification documentation must name the authoritative runner.'
Assert-Contains $verificationDoc 'SkippedUnsupportedPlatform' 'Verification documentation must preserve platform-skip semantics.'
Assert-Contains $verificationDoc 'GRID_UI_EXECUTABLE' 'Verification documentation must describe configuration-specific UI binding.'

$developmentRefresh = Get-RepoText 'eng/Refresh-GridDevelopmentInstall.ps1'
Assert-Contains $developmentRefresh 'AuthorizeInstalledRefresh' 'Development installation refresh must require explicit authorization.'
Assert-Contains $developmentRefresh 'Programs\Grid' 'Development installation refresh must bind the fixed per-user application target.'
Assert-Contains $developmentRefresh 'Automatic rollback attempted' 'Development installation refresh must attempt rollback on failure.'
Assert-Contains $developmentRefresh 'Grid connection state was not modified' 'Development installation refresh must preserve external connection state.'
Assert-Contains $developmentRefresh 'DevelopmentInstallScriptStageLocked' 'Development installation refresh must bound and report transient staged-script locks.'
Assert-Contains $developmentRefresh '--configuration Release' 'Development installation refresh must match the installer Release runtime contract.'

# Licensing/provenance must remain explicit and fail closed for distribution.
$provenanceFiles = @(
    'legal/README.md',
    'legal/provenance.v1.json',
    'legal/asset-provenance.v1.json',
    'legal/third-party-components.v1.json',
    'legal/THIRD_PARTY_NOTICES.md',
    'legal/provenance-ledger.v1.schema.json',
    'eng/provenance/Grid.Provenance.ps1',
    'eng/provenance/Test-GridProvenance.ps1',
    'eng/provenance/New-GridProvenanceInventory.ps1',
    'eng/provenance/Compare-GridUpstreamSource.ps1',
    'eng/provenance/Export-GridAttorneyAudit.ps1',
    'tests/Provenance.Tests.ps1'
)
foreach ($file in $provenanceFiles) {
    Assert-True (Test-Path -LiteralPath (Join-Path $repoRoot $file) -PathType Leaf) "Required provenance artifact '$file' is missing."
}
$provenanceDoc = Get-RepoText 'legal/README.md'
Assert-Contains $provenanceDoc 'first formal Grid provenance baseline' 'Provenance documentation must disclose the absence of Git history.'
Assert-Contains $provenanceDoc 'distribution gate fails closed' 'Provenance documentation must retain distribution refusal semantics.'
$provenanceGate = Get-RepoText 'eng/provenance/Test-GridProvenance.ps1'
Assert-Contains $provenanceGate 'RepositoryLicenseMissing' 'Distribution gate must require an attorney-approved repository license.'
Assert-Contains $provenanceGate 'GPLBoundaryInvalid' 'Distribution gate must reject silent proprietary treatment of GPL-derived code.'

Write-Host 'PASS: GRID architecture/documentation conformance checks passed.'
