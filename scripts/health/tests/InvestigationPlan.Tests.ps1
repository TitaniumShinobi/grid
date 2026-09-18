$ErrorActionPreference = 'Stop'
$healthRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $healthRoot 'Grid.InvestigationPlan.ps1')

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }

$fixtureCapabilities = @(
    [pscustomobject][ordered]@{ capabilityId = 'grid.fixture.context.collect'; capabilityVersion = '1.0.0' }
    [pscustomobject][ordered]@{ capabilityId = 'grid.fixture.investigation.collect'; capabilityVersion = '2.0.0' }
)

# --- Plain request alone must produce an incomplete plan, never invented targets ---
$plan1 = New-GridInvestigationPlan -CaseId 'case-001' -GameId 'FixtureGame' -InstallationId 'C:\Fixture\Install' -ProfileId 'FixtureProfile' -OriginalRequest 'The synthetic structure is incomplete.' -CapabilityBindings $fixtureCapabilities
Assert-Equal 'NeedsEvidence' $plan1.status 'A plain-language-only request must produce a NeedsEvidence plan.'
Assert-Equal 0 @($plan1.candidatePlugins).Count 'Plain language must never invent candidate plugins.'
Assert-Equal 0 @($plan1.observedForms).Count 'Plain language must never invent observed forms.'
Assert-Equal 0 @($plan1.hypotheses).Count 'Plain language must never invent hypotheses.'
Assert-True (@($plan1.missingInputs) -contains 'candidatePlugins') 'Missing candidatePlugins must be reported.'
Assert-True (@($plan1.missingInputs) -contains 'observedForms') 'Missing observedForms must be reported.'
Assert-True (@($plan1.recommendedCollectors).Count -gt 0) 'An incomplete plan must recommend next collection steps.'
Assert-Equal 'The synthetic structure is incomplete.' $plan1.normalizedSymptoms[0] 'normalizedSymptoms must be a verbatim copy of the request, not a paraphrase.'
Write-Host 'PASS: a plain-language-only request produces an honest NeedsEvidence plan with no invented targets.'

# --- Explicit fields populate the plan with correct provenance ---
$plan2 = New-GridInvestigationPlan -CaseId 'case-002' -GameId 'FixtureGame' -InstallationId 'C:\Fixture\Install' -ProfileId 'FixtureProfile' `
    -OriginalRequest 'Fixture symptom description.' -StatedLocation 'Fixture Location' `
    -StatedPluginNames @('Fixture.Main.esp') -StatedProviderNames @('Fixture Provider') -StatedFormIds @('0x000F13C4') -StatedEditorIds @('FixtureEditorId') `
    -EvidenceReferences @('C:\Fixture\screenshot.png') -CapabilityBindings $fixtureCapabilities
Assert-Equal 2 $plan2.schemaVersion 'New InvestigationPlans must use schema v2.'
Assert-Equal 'grid.fixture.context.collect' $plan2.capabilities[0].capabilityId 'Capability dependency order must be preserved in the runtime plan.'
Assert-Equal 'ReadyToCollect' $plan2.status 'A plan with explicit context, candidate plugins, and observed forms must be ReadyToCollect.'
Assert-Equal 0 @($plan2.missingInputs).Count 'A ReadyToCollect plan must have no missing inputs.'
Assert-Equal 'Fixture.Main.esp' $plan2.candidatePlugins[0].name 'Explicit plugin name must be recorded verbatim.'
Assert-Equal 'UserStated' $plan2.candidatePlugins[0].source 'Explicit plugin name source must be UserStated.'
Assert-Equal 'Fixture Provider' $plan2.providerSeeds[0].name 'Explicit provider seed must remain distinct from plugin names.'
Assert-Equal 'Fixture Location' $plan2.locations[0] 'Explicit location must be recorded verbatim.'
Assert-True (@($plan2.observedForms | Where-Object { $_.formId -eq '0x000F13C4' }).Count -gt 0) 'Explicit FormID must be recorded as an observed form.'
Assert-True (@($plan2.observedForms | Where-Object { $_.editorId -eq 'FixtureEditorId' }).Count -gt 0) 'Explicit EditorID must be recorded as an observed form.'
Assert-Equal 'C:\Fixture\screenshot.png' $plan2.evidenceReferences[0].reference 'Evidence reference must be recorded verbatim.'
Assert-Equal 'EvidenceAttachment' $plan2.evidenceReferences[0].source 'Evidence reference source must be EvidenceAttachment.'
Write-Host 'PASS: explicit plugin/FormID/EditorID/location/evidence fields populate the plan with correct provenance.'

# --- Provenance covers every populated technical field ---
$provenanceFields = @($plan2.provenance | ForEach-Object { $_.field })
Assert-True ($provenanceFields -contains 'candidatePlugins[0]') 'Provenance must exist for candidatePlugins[0].'
Assert-True ($provenanceFields -contains 'providerSeeds[0]') 'Provenance must exist for providerSeeds[0].'
Assert-True ($provenanceFields -contains 'evidenceReferences[0]') 'Provenance must exist for evidenceReferences[0].'
Assert-True ($provenanceFields -contains 'locations[0]') 'Provenance must exist for locations[0].'
Assert-True ($provenanceFields -contains 'capabilities[0]') 'Manifest provenance must exist for every capability binding.'
Write-Host 'PASS: provenance is recorded for every populated technical field.'

# --- Schema validator accepts a well-formed plan and rejects malformed ones ---
$validation1 = Test-GridInvestigationPlanSchema -Plan $plan2
Assert-True $validation1.IsValid 'A well-formed plan must pass schema validation.'

$malformed = [pscustomobject]@{ schemaVersion = 999; caseId = 'x' }
$validation2 = Test-GridInvestigationPlanSchema -Plan $malformed
Assert-True (-not $validation2.IsValid) 'A plan missing required fields must fail schema validation.'
Assert-True (@($validation2.Errors).Count -gt 0) 'Validation errors must be reported for a malformed plan.'
Write-Host 'PASS: the schema validator accepts well-formed plans and rejects malformed ones.'

$badStatusPlan = $plan1.PSObject.Copy()
$badStatusPlan.status = 'InventedStatus'
$validation3 = Test-GridInvestigationPlanSchema -Plan $badStatusPlan
Assert-True (-not $validation3.IsValid) 'An invalid status value must fail schema validation.'
Write-Host 'PASS: an invalid status value fails schema validation.'

$legacyPlan = $plan2 | ConvertTo-Json -Depth 30 | ConvertFrom-Json
$legacyPlan.schemaVersion = 1
$legacyPlan.PSObject.Properties.Remove('capabilities')
$legacyValidation = Test-GridInvestigationPlanSchema -Plan $legacyPlan
Assert-True $legacyValidation.IsValid 'InvestigationPlan v1 must remain readable for historical replay.'
Write-Host 'PASS: InvestigationPlan v1 remains valid for replay while new plans use v2.'

# --- Missing hierarchy context blocks collection before technical evidence ---
$plan3 = New-GridInvestigationPlan -CaseId 'case-003' -GameId 'FixtureGame' -OriginalRequest 'Fixture symptom.' -StatedPluginNames @('Fixture.Main.esp') -StatedFormIds @('0x00001234') -CapabilityBindings $fixtureCapabilities
Assert-Equal 'NeedsContext' $plan3.status 'Missing installation/profile context must produce NeedsContext.'
Assert-True (@($plan3.missingInputs) -contains 'installationId') 'Missing installationId must be explicit.'
Assert-True (@($plan3.missingInputs) -contains 'profileId') 'Missing profileId must be explicit.'
Write-Host 'PASS: missing hierarchy context blocks collection without guessing.'

# --- Baseline plans are ready from exact context and gates; they do not invent xEdit targets ---
$baselineCapabilities = @(
    [pscustomobject][ordered]@{ capabilityId = 'grid.health.capability.resolve'; capabilityVersion = '1.0.0' }
    [pscustomobject][ordered]@{ capabilityId = 'grid.game.fixturegame.baseline.collect'; capabilityVersion = '1.0.0' }
)
$baselinePlan = New-GridInvestigationPlan -CaseId 'case-baseline-001' -GameId 'fixturegame' `
    -InstallationId 'fixture-installation-id' -ProfileId 'FixtureProfile' -OriginalRequest 'Capture a reproducible baseline.' `
    -Purpose Baseline -CapabilityBindings $baselineCapabilities
Assert-Equal 'Baseline' $baselinePlan.purpose 'The reusable baseline entry point must record an explicit plan purpose.'
Assert-Equal 'ReadyToCollect' $baselinePlan.status 'Exact installation/profile context is sufficient for baseline collection readiness.'
Assert-Equal 0 @($baselinePlan.candidatePlugins).Count 'Baseline readiness must not invent or require candidate plugins.'
Assert-Equal 0 @($baselinePlan.observedForms).Count 'Baseline readiness must not invent or require FormIDs/EditorIDs.'
Assert-Equal 7 @($baselinePlan.requiredGates).Count 'Omitting RequiredGates must select the seven bounded baseline gates.'
Assert-Equal 'InstallationBaseline' $baselinePlan.requiredGates[0] 'Baseline gate order must be deterministic.'
Assert-Equal 'RuntimeReferenceBaseline' $baselinePlan.requiredGates[6] 'Baseline gate order must be deterministic.'
Assert-True (Test-GridInvestigationPlanSchema -Plan $baselinePlan).IsValid 'A contextualized baseline v2 plan must validate.'

$baselineNeedsContext = New-GridInvestigationPlan -CaseId 'case-baseline-002' -GameId 'fixturegame' `
    -OriginalRequest 'Capture a reproducible baseline.' -Purpose Baseline -CapabilityBindings $baselineCapabilities
Assert-Equal 'NeedsContext' $baselineNeedsContext.status 'A baseline plan without exact installation/profile context must stop at NeedsContext.'

$baselineWrongBinding = $baselinePlan | ConvertTo-Json -Depth 30 | ConvertFrom-Json
$baselineWrongBinding.capabilities = @($fixtureCapabilities)
Assert-True (-not (Test-GridInvestigationPlanSchema -Plan $baselineWrongBinding).IsValid) 'A baseline plan without exactly one game baseline capability must fail validation.'
Write-Host 'PASS: baseline-purpose readiness is context- and capability-bound without xEdit targets.'
