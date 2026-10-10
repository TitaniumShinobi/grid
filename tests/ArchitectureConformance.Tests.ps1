param(
    [switch]$InspectionContract
)

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

if (-not $InspectionContract) {

$requiredDocs = @(
    'README.md',
    'docs/README.md',
    'docs/architecture.md',
    'docs/game-registration-inspection-v1.md',
    'src/Grid.Core/Contracts/game-registration-inspection.v1.json',
    'src/Grid.Core/Contracts/canonical-registration-checklist.v1.json',
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

}

# LIF-8 inspection checklist is a sibling contract, not the 98-node DIF mold, and is not engine-loaded.
$docsIndex = Get-RepoText 'docs/README.md'
Assert-Contains $docsIndex 'Game Registration Inspection Checklist v1' 'Docs index must name the inspection checklist.'
Assert-Contains $docsIndex 'Proposed metadata-first inspection checklist' 'Docs index must mark the inspection checklist as Proposed.'
Assert-Contains $docsIndex 'not the frozen 98-node DIF projection mold' 'Docs index must keep the inspection checklist distinct from the DIF mold.'

$gridMd = Get-RepoText 'GRID.md'
Assert-Contains $gridMd 'Registration is metadata-first, not DIF-first.' 'GRID.md must retain metadata-first registration authority.'
Assert-Contains $gridMd 'Register once. Resolve once. Use everywhere.' 'GRID.md must retain the register-once rule.'
Assert-Contains $gridMd 'FILE VERIFIED' 'GRID.md must retain FILE VERIFIED evidence tags.'
Assert-Contains $gridMd 'REFERENCE VERIFIED' 'GRID.md must retain REFERENCE VERIFIED evidence tags.'
Assert-Contains $gridMd 'UNRESOLVED' 'GRID.md must retain UNRESOLVED evidence tags.'

$coreProject = Get-RepoText 'src/Grid.Core/Grid.Core.csproj'
Assert-Contains $coreProject 'Contracts\canonical-registration-checklist.v1.json' 'Core must embed the frozen DIF mold checklist.'
Assert-True ($coreProject -notmatch 'game-registration-inspection\.v1\.json') 'Inspection checklist must not be an EmbeddedResource loaded by Core.'

$mold = Get-RepoText 'src/Grid.Core/Contracts/canonical-registration-checklist.v1.json' | ConvertFrom-Json
Assert-Equal '1' ([string]$mold.version) 'DIF mold checklist version must remain 1.'
Assert-Equal 98 @($mold.nodes).Count 'DIF mold must remain exactly 98 authored nodes.'
$moldSelectors = @($mold.nodes | Where-Object { -not $_.parentId } | ForEach-Object { [string]$_.id })
Assert-Equal 'Tool Mod Location MissionQuest Item Actor' ($moldSelectors -join ' ') 'DIF mold selector roots must remain the six authored identities.'

$inspectionJson = Get-RepoText 'src/Grid.Core/Contracts/game-registration-inspection.v1.json'
$inspection = $inspectionJson | ConvertFrom-Json
Assert-Equal 1 ([int]$inspection.schemaVersion) 'Inspection checklist schemaVersion must be 1.'
Assert-Equal 'grid.game-registration-inspection.v1' ([string]$inspection.contractId) 'Inspection contractId must be stable.'
Assert-Equal 'Proposed' ([string]$inspection.status) 'Inspection checklist remains Proposed until product publication is separately decided.'
Assert-Equal 'NOT_PUBLISHED' ([string]$inspection.authority.publicationState) 'Inspection contract must preserve NOT_PUBLISHED.'
Assert-Equal $false ([bool]$inspection.loadedByCanonicalRegistrationChecklist) 'Inspection checklist must declare it is not engine-loaded.'
Assert-Equal 98 ([int]$inspection.authority.difMold.requiredNodeCount) 'Inspection contract must freeze the DIF mold at 98 nodes without modifying it.'
Assert-Equal 21 @($inspection.categories).Count 'Inspection checklist must enumerate exactly 21 categories.'
$expectedIds = @(1..21 | ForEach-Object { 'GR-{0:D2}' -f $_ })
$actualIds = @($inspection.categories | ForEach-Object { [string]$_.id })
Assert-Equal ($expectedIds -join ' ') ($actualIds -join ' ') 'Inspection category ids must be GR-01 through GR-21 in order.'
foreach ($category in @($inspection.categories)) {
    Assert-True (-not [string]::IsNullOrWhiteSpace([string]$category.name)) "Category $($category.id) must have a name."
    Assert-True (-not [string]::IsNullOrWhiteSpace([string]$category.inspect)) "Category $($category.id) must declare what GRID inspects."
    Assert-True (@('Implemented', 'Partial', 'Missing') -contains [string]$category.coverage) "Category $($category.id) coverage must use the v1 vocabulary."
    Assert-True (@($category.grounding).Count -ge 1) "Category $($category.id) must cite repository grounding."
}
$coverageValues = @($inspection.categories | ForEach-Object { [string]$_.coverage } | Sort-Object -Unique)
Assert-True ($coverageValues -contains 'Partial') 'Inspection checklist must distinguish Partial coverage from Implemented.'
Assert-True ($coverageValues -contains 'Missing') 'Inspection checklist must keep Missing categories visible.'
Assert-True ($coverageValues -notcontains 'Implemented') 'No GR category may claim Implemented inspection completeness in v1.'
Assert-Contains ([string]$inspection.coverageRule) 'game-agnostic completeness' 'Coverage rule must keep game-specific coverage distinct from game-agnostic completeness.'
Assert-Contains $inspectionJson 'Preserve ambiguity instead of guessing' 'Inspection evidence rules must preserve GRID.md ambiguity.'
$gr09 = @($inspection.categories | Where-Object { [string]$_.id -eq 'GR-09' })[0]
Assert-Contains ([string]$gr09.v1Default) 'It does not mean string-table completeness.' 'GR-09 Partial must not be read as string-table completeness.'
$gr19 = @($inspection.categories | Where-Object { [string]$_.id -eq 'GR-19' })[0]
Assert-Contains ([string]$gr19.inspect) 'RegistrationApplicability remains GameId plus optional ProfileId' 'GR-19 must distinguish implemented applicability from inspection facts.'
Assert-Contains ([string]$gr19.v1Default) 'Do not widen RegistrationApplicability in v1.' 'GR-19 must not widen Contract 2 applicability fields.'
Assert-Equal 3 @($inspection.escalations).Count 'Inspection checklist must retain the three product-intent escalations only.'
foreach ($escalation in @($inspection.escalations)) {
    Assert-Equal 'No' ([string]$escalation.v1Recommendation) "Escalation $($escalation.id) v1 recommendation must remain No."
}
$candidateIsNot = @($inspection.authority.candidateVerificationIsNot | ForEach-Object { [string]$_ })
foreach ($forbidden in @('live publication', 'KnowledgeRebuild catalog import', 'Workstation Refresh publish', 'populated live DIF')) {
    Assert-True ($candidateIsNot -contains $forbidden) "Candidate verification must remain distinct from '$forbidden'."
}
$inspectionDoc = Get-RepoText 'docs/game-registration-inspection-v1.md'
Assert-Contains $inspectionDoc 'game-registration-inspection.v1.json' 'Human checklist must point at the machine-readable contract.'
Assert-Contains $inspectionDoc 'canonical-registration-checklist.v1.json' 'Human checklist must name the DIF mold it is not.'
Assert-Contains $inspectionDoc 'NOT_PUBLISHED' 'Human checklist must preserve the NOT_PUBLISHED boundary.'
Assert-Contains $inspectionDoc 'GR-01 through GR-21' 'Human checklist must cover the 21 inspection categories.'
Assert-Contains $inspectionDoc 'game-agnostic completeness' 'Human checklist must keep game-specific coverage distinct from game-agnostic completeness.'
Assert-Contains $inspectionDoc 'Preserve ambiguity' 'Human checklist must preserve GRID.md ambiguity.'
Assert-Contains $inspectionDoc 'Proposed metadata-first' 'Human checklist must remain Proposed, not a frozen live contract.'
Assert-Contains $inspectionDoc '--game-registration-inspection' 'Human checklist must name the independent Core inspection selector.'
Assert-Contains $inspectionDoc '-InspectionContract' 'Human checklist must name the independent ArchitectureConformance inspection selector.'
$coreTestsEntry = Get-RepoText 'tests/Grid.Core.Tests/Program.cs'
Assert-Contains $coreTestsEntry '--game-registration-inspection' 'Core tests must expose an independent LIF-8 inspection selector.'

Write-Host 'PASS: GRID architecture/documentation conformance checks passed.'
