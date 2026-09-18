$ErrorActionPreference = 'Stop'
$healthRoot = Split-Path -Parent $PSScriptRoot
$scriptsRoot = Split-Path -Parent $healthRoot
Import-Module (Join-Path $healthRoot 'Grid.Health.psm1') -Force

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }

$recipes = @(Get-GridClassRecipeRegistry)
Assert-Equal 32 $recipes.Count 'The canonical Class registry must contain exactly 32 recipes.'
Assert-Equal 13 @($recipes | Where-Object coverage -eq 'Registered').Count 'Only implemented deterministic coverage may be registered.'
Assert-Equal 'grid.class.archives-metadata,grid.class.crash-freeze,grid.class.distribution-leveled-lists,grid.class.installation-integrity,grid.class.missing-mesh,grid.class.missing-texture,grid.class.npc-behavior,grid.class.outfits-bodies-physics,grid.class.plugins-dependencies,grid.class.record-conflicts,grid.class.scripts-save-state,grid.class.skse-compatibility,grid.class.world-objects' (@($recipes | Where-Object coverage -eq 'Registered' | ForEach-Object classId) -join ',') 'Registered coverage must include baseline, crash, distribution, plugin asset, SKSE load, scripted-reference, placed-actor, and explicit gameplay-capability slices.'
Write-Host 'PASS: all 32 Classes are present and coverage is honest.'

$envelope = New-GridRequestEnvelope -GameId 'SkyrimSpecialEdition' -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' `
    -ClassId 'grid.class.installation-integrity' -ClassRecipeVersion '1.1.0' -ModNames @('Fixture Two','Fixture One') -PlainText 'Verbatim fixture claim.'
Assert-True (Test-GridRequestEnvelope -Envelope $envelope).IsValid 'A canonical structured envelope must validate.'
Assert-Equal 2 @($envelope.selections.mods).Count 'Mod selections must be multi-value.'
Assert-Equal 0 @($envelope.selections.tools).Count 'No tool may be implied by a Class recipe.'
Assert-Equal 'Verbatim fixture claim.' $envelope.claims.text 'Claim text must remain verbatim.'
Assert-Equal 'grid.class.installation-integrity' $envelope.class.classId 'The explicit Class must remain unchanged.'
$tampered = $envelope | ConvertTo-Json -Depth 30 | ConvertFrom-Json
$tampered.claims.text = 'Changed after hashing.'
Assert-True (-not (Test-GridRequestEnvelope -Envelope $tampered).IsValid) 'Envelope content changes must invalidate the bound digest.'
$duplicateRejected = $false
try { New-GridRequestEnvelope -GameId 'SkyrimSpecialEdition' -ClassId 'grid.class.installation-integrity' -ClassRecipeVersion '1.1.0' -ModNames @('Fixture One','fixture one') | Out-Null }
catch { $duplicateRejected = $_.Exception.Message -match 'DuplicateSelection' }
Assert-True $duplicateRejected 'Case-insensitive duplicate selections must be rejected, not silently erased.'

$edited = New-GridRequestEnvelope -GameId 'SkyrimSpecialEdition' -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' `
    -ClassId 'grid.class.installation-integrity' -ClassRecipeVersion '1.1.0' -ModNames @('Fixture One') -PlainText 'This text mentions crashes and quests.'
Assert-Equal 'grid.class.installation-integrity' $edited.class.classId 'Plain-language edits must never reclassify the request.'
Assert-Equal 0 @($edited.selections.tools).Count 'Plain language must not invent tools.'
Write-Host 'PASS: Class, mods, and tools are structured input and never mined from text.'

$capabilityEnvelope = New-GridRequestEnvelope -GameId 'skyrimspecialedition' -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' `
    -ClassId 'grid.class.outfits-bodies-physics' -ClassRecipeVersion '1.1.0' -CapabilityIds @('grid.capability.equipment.multiple-rings') -PlainText 'Can this profile wear more than one ring?'
$capabilityPlan = Resolve-GridRequestPlan -Envelope $capabilityEnvelope -ScriptsRoot $scriptsRoot
Assert-Equal 'ReadyToCollect' $capabilityPlan.status 'An explicit registered gameplay capability must be ready for deterministic profile assessment.'
Assert-Equal 'grid.capability.equipment.multiple-rings' $capabilityPlan.requestedCapabilityId 'The planner must preserve the exact structured gameplay capability.'
Assert-Equal 'GameplayCapabilityAssessment' $capabilityPlan.dispatch.purpose 'Gameplay coverage must route to the dedicated assessment collector.'
Assert-True (@($capabilityPlan.capabilityBindings | Where-Object capabilityId -eq 'grid.game.skyrimspecialedition.gameplay-capability.assess').Count -eq 1) 'Gameplay assessment must bind its exact registered collector.'
$missingCapabilityEnvelope = New-GridRequestEnvelope -GameId 'skyrimspecialedition' -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' `
    -ClassId 'grid.class.outfits-bodies-physics' -ClassRecipeVersion '1.1.0' -PlainText 'Preserve this request without guessing a capability.'
Assert-Equal 'NeedsEvidence' (Resolve-GridRequestPlan -Envelope $missingCapabilityEnvelope -ScriptsRoot $scriptsRoot).status 'A capability-aware Class must require an explicit structured capability selection.'
$wrongCapabilityEnvelope = New-GridRequestEnvelope -GameId 'skyrimspecialedition' -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' `
    -ClassId 'grid.class.outfits-bodies-physics' -ClassRecipeVersion '1.1.0' -CapabilityIds @('grid.capability.equipment.unknown') -PlainText 'Do not reinterpret this claim.'
Assert-Equal 'UnsupportedCoverage' (Resolve-GridRequestPlan -Envelope $wrongCapabilityEnvelope -ScriptsRoot $scriptsRoot).status 'An unregistered capability must fail closed.'
Write-Host 'PASS: gameplay capabilities are explicit structured selections with deterministic routing.'

$readyEnvelope = New-GridRequestEnvelope -GameId 'skyrimspecialedition' -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' `
    -ClassId 'grid.class.installation-integrity' -ClassRecipeVersion '1.1.0' -ModNames @('Fixture One','Fixture Two') -PlainText 'Verbatim fixture claim.'
$ready = Resolve-GridRequestPlan -Envelope $readyEnvelope -ScriptsRoot $scriptsRoot
Assert-Equal 'ReadyToCollect' $ready.status 'A contextualized Installation Integrity request with an exact provider must plan successfully.'
Assert-Equal 'Invoke-GridBaseline.ps1' $ready.dispatch.entryPoint 'Installation Integrity must route to the existing bounded baseline collector.'
Assert-True (-not $ready.dispatch.mutationAuthorized) 'Diagnosis planning must never authorize mutation.'
Assert-True (@($ready.capabilityBindings | Where-Object capabilityId -eq 'grid.game.skyrimspecialedition.baseline.collect').Count -eq 1) 'The exact baseline capability must be bound.'
Assert-Equal 3 @($ready.dispatch.requiredGates).Count 'The first vertical slice must request only the three gates its minimum input can satisfy.'

$wholeProfileEnvelope = New-GridRequestEnvelope -GameId 'skyrimspecialedition' -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' `
    -ClassId 'grid.class.installation-integrity' -ClassRecipeVersion '1.1.0' -PlainText 'Inspect the complete selected profile.'
$wholeProfile = Resolve-GridRequestPlan -Envelope $wholeProfileEnvelope -ScriptsRoot $scriptsRoot
Assert-Equal 'ReadyToCollect' $wholeProfile.status 'CleanHouse must admit the exact selected profile without requiring an arbitrary mod seed.'
Assert-Equal 0 @($wholeProfileEnvelope.selections.mods).Count 'Whole-profile scope must remain structurally distinct from selected mod filters.'
Assert-Equal 2 @($wholeProfile.dispatch.requiredGates).Count 'Whole-profile scope must not claim a provider-specific asset gate.'

$stale = New-GridRequestEnvelope -GameId 'skyrimspecialedition' -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' `
    -ClassId 'grid.class.installation-integrity' -ClassRecipeVersion '9.9.9' -ModNames @('Fixture One')
$stalePlan = Resolve-GridRequestPlan -Envelope $stale -ScriptsRoot $scriptsRoot
Assert-Equal 'UnsupportedCoverage' $stalePlan.status 'A stale Class recipe version must never execute under changed semantics.'

$needsContextEnvelope = New-GridRequestEnvelope -GameId 'skyrimspecialedition' -ClassId 'grid.class.installation-integrity' -ClassRecipeVersion '1.1.0' -ModNames @('Fixture One')
$needsContext = Resolve-GridRequestPlan -Envelope $needsContextEnvelope -ScriptsRoot $scriptsRoot
Assert-Equal 'NeedsContext' $needsContext.status 'Missing installation/profile context must fail closed.'

$unsupportedClassEnvelope = New-GridRequestEnvelope -GameId 'skyrimspecialedition' -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' -ClassId 'grid.class.quests-aliases' -ClassRecipeVersion '1.0.0' -ModNames @('Fixture One')
$unsupportedClass = Resolve-GridRequestPlan -Envelope $unsupportedClassEnvelope -ScriptsRoot $scriptsRoot
Assert-Equal 'UnsupportedCoverage' $unsupportedClass.status 'Unimplemented Classes must report UnsupportedCoverage.'

$toolEnvelope = New-GridRequestEnvelope -GameId 'skyrimspecialedition' -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' -ClassId 'grid.class.installation-integrity' -ClassRecipeVersion '1.1.0' -ModNames @('Fixture One') -ToolIds @('grid.tool.loot','grid.tool.mo2')
$toolPlan = Resolve-GridRequestPlan -Envelope $toolEnvelope -ScriptsRoot $scriptsRoot
Assert-Equal 'ReadyToCollect' $toolPlan.status 'Registered compatible tools must produce a tool-evidence plan.'
Assert-Equal 'Invoke-GridToolEvidenceOrchestration' $toolPlan.dispatch.entryPoint 'Selected tools must route through the tool orchestrator.'
$unsupportedToolEnvelope = New-GridRequestEnvelope -GameId 'skyrimspecialedition' -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' -ClassId 'grid.class.installation-integrity' -ClassRecipeVersion '1.1.0' -ModNames @('Fixture One') -ToolIds @('grid.tool.sseedit')
$unsupportedTool = Resolve-GridRequestPlan -Envelope $unsupportedToolEnvelope -ScriptsRoot $scriptsRoot
Assert-Equal 'UnsupportedCoverage' $unsupportedTool.status 'A tool without Class coverage must fail closed.'
Write-Host 'PASS: planner resolves supported coverage and refuses missing context, unsupported Classes, and unsupported tools.'

$crashEnvelope = New-GridRequestEnvelope -GameId 'skyrimspecialedition' -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' `
    -ClassId 'grid.class.crash-freeze' -ClassRecipeVersion '1.1.0' -ToolIds @('grid.tool.mo2') -PlainText 'Preserved crash claim.'
$crashPlan = Resolve-GridRequestPlan -Envelope $crashEnvelope -ScriptsRoot $scriptsRoot
Assert-Equal 'ReadyToCollect' $crashPlan.status 'Crash & Freeze must route the current bounded CrashLogger and MO2-provider baseline.'
Assert-True (@($crashPlan.capabilityBindings | Where-Object capabilityId -eq 'grid.game.skyrimspecialedition.baseline.collect').Count -eq 1) 'Crash & Freeze must bind the baseline crash collector.'

$distributionEnvelope = New-GridRequestEnvelope -GameId 'skyrimspecialedition' -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' `
    -ClassId 'grid.class.distribution-leveled-lists' -ClassRecipeVersion '1.1.0' -ToolIds @('grid.tool.mo2') -PlainText 'Preserved distribution claim.'
$distributionPlan = Resolve-GridRequestPlan -Envelope $distributionEnvelope -ScriptsRoot $scriptsRoot
Assert-Equal 'ReadyToCollect' $distributionPlan.status 'Distribution & Leveled Lists must route whole-profile SPID source validation.'
Assert-True (@($distributionPlan.capabilityBindings | Where-Object capabilityId -eq 'grid.game.skyrimspecialedition.baseline.collect').Count -eq 1) 'Distribution & Leveled Lists must bind the baseline SPID collector.'
Write-Host 'PASS: crash and distribution Classes route only their implemented read-only baseline evidence.'

$skseEnvelope = New-GridRequestEnvelope -GameId 'skyrimspecialedition' -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' `
    -ClassId 'grid.class.skse-compatibility' -ClassRecipeVersion '1.1.0' -ToolIds @('grid.tool.mo2') -PlainText 'Preserved SKSE compatibility claim.'
$sksePlan = Resolve-GridRequestPlan -Envelope $skseEnvelope -ScriptsRoot $scriptsRoot
Assert-Equal 'ReadyToCollect' $sksePlan.status 'SKSE Compatibility must route the bounded native plugin-manager log and current MO2 winner baseline.'
Assert-True (@($sksePlan.capabilityBindings | Where-Object capabilityId -eq 'grid.game.skyrimspecialedition.baseline.collect').Count -eq 1) 'SKSE Compatibility must bind the baseline native-load collector.'
Write-Host 'PASS: SKSE Compatibility routes only implemented read-only SKSE log and MO2-provider evidence.'

$missingMeshEnvelope = New-GridRequestEnvelope -GameId 'skyrimspecialedition' -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' `
    -ClassId 'grid.class.missing-mesh' -ClassRecipeVersion '1.1.0' -ToolIds @('grid.tool.mo2') -PlainText 'Preserved missing mesh claim.'
$missingMeshPlan = Resolve-GridRequestPlan -Envelope $missingMeshEnvelope -ScriptsRoot $scriptsRoot
Assert-Equal 'ReadyToCollect' $missingMeshPlan.status 'Missing Mesh must route whole-profile plugin-declared NIF validation.'
Assert-True (@($missingMeshPlan.capabilityBindings | Where-Object capabilityId -eq 'grid.game.skyrimspecialedition.baseline.collect').Count -eq 1) 'Missing Mesh must bind the baseline asset collector.'
$missingTextureEnvelope = New-GridRequestEnvelope -GameId 'skyrimspecialedition' -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' `
    -ClassId 'grid.class.missing-texture' -ClassRecipeVersion '1.1.0' -ToolIds @('grid.tool.mo2') -PlainText 'Preserved missing texture claim.'
$missingTexturePlan = Resolve-GridRequestPlan -Envelope $missingTextureEnvelope -ScriptsRoot $scriptsRoot
Assert-Equal 'ReadyToCollect' $missingTexturePlan.status 'Missing Texture must route whole-profile plugin-declared DDS validation.'
Assert-True (@($missingTexturePlan.capabilityBindings | Where-Object capabilityId -eq 'grid.game.skyrimspecialedition.baseline.collect').Count -eq 1) 'Missing Texture must bind the baseline asset collector.'
Write-Host 'PASS: Missing Mesh and Missing Texture route exact read-only plugin asset dependency evidence.'

$recordConflictEnvelope = New-GridRequestEnvelope -GameId 'skyrimspecialedition' -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' `
    -ClassId 'grid.class.record-conflicts' -ClassRecipeVersion '1.2.0' -PlainText 'Preserved whole-profile conflict claim.'
$recordConflictPlan = Resolve-GridRequestPlan -Envelope $recordConflictEnvelope -ScriptsRoot $scriptsRoot
Assert-Equal 'ReadyToCollect' $recordConflictPlan.status 'Record Conflicts must begin with the whole-profile conflict provenance index without an invented FormID.'
Assert-True (@($recordConflictPlan.capabilityBindings | Where-Object capabilityId -eq 'grid.game.skyrimspecialedition.baseline.collect').Count -eq 1) 'Record Conflicts must bind the one-run baseline collector.'
Assert-True (-not $recordConflictPlan.dispatch.mutationAuthorized) 'Whole-profile conflict indexing must remain read-only.'
Write-Host 'PASS: Record Conflicts begins with one-run whole-profile provenance and produces bounded targets for deeper inspection.'

$npcEnvelope = New-GridRequestEnvelope -GameId 'skyrimspecialedition' -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' `
    -ClassId 'grid.class.npc-behavior' -ClassRecipeVersion '1.1.0' -ModNames @('Fixture NPC Provider') -ToolIds @('grid.tool.sseedit')
$npcPlan = Resolve-GridRequestPlan -Envelope $npcEnvelope -ScriptsRoot $scriptsRoot
Assert-Equal 'NeedsEvidence' $npcPlan.status 'NPC Behavior must require an explicit observed FormID or EditorID in addition to provider and SSEEdit selections.'
Assert-True (@($npcPlan.capabilityBindings | Where-Object capabilityId -eq 'grid.game.skyrimspecialedition.placed-actor-reference.resolve').Count -eq 1) 'NPC Behavior must bind deterministic base-to-ACHR resolution.'
Assert-True (@($npcPlan.missingInputs) -contains 'evidenceGroup1(observedForms)') 'NPC Behavior must fail closed until the structured observed form is supplied.'
Write-Host 'PASS: NPC Behavior binds placed-actor resolution and fails closed until exact form evidence is supplied.'


$pluginEnvelope = New-GridRequestEnvelope -GameId 'skyrimspecialedition' -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' `
    -ClassId 'grid.class.plugins-dependencies' -ClassRecipeVersion '1.0.0' -ModNames @('Dependency Subject') -ToolIds @('grid.tool.mo2','grid.tool.loot') -PlainText 'Check plugin dependencies.'
$pluginPlan = Resolve-GridRequestPlan -Envelope $pluginEnvelope -ScriptsRoot $scriptsRoot
Assert-Equal 'ReadyToCollect' $pluginPlan.status 'Plugins & Dependencies must admit a structured provider with registered MO2/LOOT evidence.'
Assert-Equal 'Invoke-GridToolEvidenceOrchestration' $pluginPlan.dispatch.entryPoint 'Plugins & Dependencies must use selected deterministic tool evidence when tools are selected.'
Assert-True (-not $pluginPlan.dispatch.mutationAuthorized) 'Plugins & Dependencies collection must remain read-only.'
Assert-True (@($pluginPlan.capabilityBindings | Where-Object capabilityId -eq 'grid.game.skyrimspecialedition.baseline.collect').Count -eq 1) 'Plugins & Dependencies must bind the reusable Skyrim baseline capability.'
$pluginNoProvider = New-GridRequestEnvelope -GameId 'skyrimspecialedition' -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' `
    -ClassId 'grid.class.plugins-dependencies' -ClassRecipeVersion '1.0.0' -ToolIds @('grid.tool.loot')
$pluginNoProviderPlan = Resolve-GridRequestPlan -Envelope $pluginNoProvider -ScriptsRoot $scriptsRoot
Assert-Equal 'NeedsEvidence' $pluginNoProviderPlan.status 'Plugins & Dependencies must not invent a provider from tool output or prose.'
Write-Host 'PASS: Plugins & Dependencies is a bounded read-only deterministic evidence slice.'

$coverage = @(Get-GridRegisteredCoverage -ScriptsRoot $scriptsRoot)
Assert-Equal 32 $coverage.Count 'Coverage reporting must include every Class.'
Assert-Equal 13 @($coverage | Where-Object coverage -eq 'Registered').Count 'Coverage reporting must expose the thirteen implemented deterministic slices.'
Write-Host 'PASS: registered coverage is complete, bounded, and truthful.'
