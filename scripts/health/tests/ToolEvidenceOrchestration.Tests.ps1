$ErrorActionPreference = 'Stop'
$healthRoot = Split-Path -Parent $PSScriptRoot
$scriptsRoot = Split-Path -Parent $healthRoot
Import-Module (Join-Path $healthRoot 'Grid.Health.psm1') -Force
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }
$registry = @(Get-GridToolRegistry -ScriptsRoot $scriptsRoot)
Assert-Equal 11 $registry.Count 'Skyrim must register MO2, LOOT, SSEEdit, and all eight selected after-run tool report families.'
Assert-Equal 'grid.tool.bodyslide,grid.tool.dyndolod,grid.tool.loot,grid.tool.mo2,grid.tool.sseedit,grid.tool.sseedit-report,grid.tool.synthesis,grid.tool.texgen,grid.tool.wrye-bash,grid.tool.xlodgen,grid.tool.zedit' (@($registry.toolId) -join ',') 'Tool IDs must be stable and sorted.'
Assert-Equal 'ExistingOutputRead' @($registry | Where-Object toolId -eq 'grid.tool.loot')[0].observationMode 'LOOT must remain an existing-output observer.'
Assert-True (@($registry|Where-Object{$_.adapter -eq 'SkyrimExistingToolReport' -and $_.observationMode -eq 'ExistingOutputRead'}).Count -eq 8) 'All new after-run tool families must remain read-only existing-output observers.'
$envelope = New-GridRequestEnvelope -GameId 'skyrimspecialedition' -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' -ClassId 'grid.class.installation-integrity' -ClassRecipeVersion '1.1.0' -ModNames @('Fixture One') -ToolIds @('grid.tool.loot','grid.tool.mo2')
$plan = Resolve-GridRequestPlan -Envelope $envelope -ScriptsRoot $scriptsRoot
Assert-Equal 'ReadyToCollect' $plan.status 'Registered compatible multi-tool selection must plan.'
Assert-Equal 'Invoke-GridToolEvidenceOrchestration' $plan.dispatch.entryPoint 'Tool-bearing requests must route to tool evidence orchestration.'

$bindingContext = [pscustomobject][ordered]@{
    workspaceId='workspace-fixture'; requestId=[string]$envelope.requestId; submissionId='submission-fixture'; envelopeSha256=[string]$envelope.envelopeSha256
    planSha256=(Get-GridCanonicalJsonSha256 -InputObject $plan); authorizationGrantId='grant-fixture'; authorizationBindingSha256=('A'*64)
    toolInputs=[pscustomobject]@{ 'grid.tool.mo2'=[pscustomobject]@{}; 'grid.tool.loot'=[pscustomobject]@{} }
}
$callOrder = New-Object Collections.Generic.List[string]
$successExecutor = { param($definition, $request) $callOrder.Add([string]$definition.toolId); [pscustomobject]@{ status = 'Collected'; evidence = @([pscustomobject]@{ source = [string]$definition.adapter }); stdout = ''; stderr = ''; exitCode = 0; reason = $null } }
$complete = Invoke-GridToolEvidenceOrchestration -Envelope $envelope -RequestPlan $plan -ScriptsRoot $scriptsRoot -Executor $successExecutor -BindingContext $bindingContext
Assert-Equal 'EvidenceComplete' $complete.terminalState 'All successful tools must produce EvidenceComplete.'
Assert-Equal 2 @($complete.toolReceipts).Count 'Every selected tool requires an independent receipt.'
Assert-Equal 'grid.tool.mo2,grid.tool.loot' (@($callOrder) -join ',') 'Capability dependencies must place MO2 context before LOOT observation.'
Assert-True (-not $complete.mutationAuthorized) 'Tool evidence orchestration must never authorize mutation.'
Assert-True (-not [string]::IsNullOrWhiteSpace([string]$complete.toolReceipts[0].evidenceSha256)) 'One-item evidence must have a stable digest.'
$manyExecutor = { param($definition, $request) [pscustomobject]@{ status = 'Collected'; evidence = @('first','second','third'); stdout = ''; stderr = ''; exitCode = 0; reason = $null } }
$many = Invoke-GridToolEvidenceOrchestration -Envelope $envelope -RequestPlan $plan -ScriptsRoot $scriptsRoot -Executor $manyExecutor -BindingContext $bindingContext
Assert-True (-not [string]::IsNullOrWhiteSpace([string]$many.toolReceipts[0].evidenceSha256)) 'Many-item evidence must have a stable digest.'
$partialExecutor = { param($definition, $request) if ($definition.toolId -eq 'grid.tool.loot') { throw 'synthetic LOOT failure' }; [pscustomobject]@{ status = 'Collected'; evidence = @('mo2'); stdout = 'ok'; stderr = ''; exitCode = 0; reason = $null } }
$partial = Invoke-GridToolEvidenceOrchestration -Envelope $envelope -RequestPlan $plan -ScriptsRoot $scriptsRoot -Executor $partialExecutor -BindingContext $bindingContext
Assert-Equal 'EvidencePartial' $partial.terminalState 'One failure must preserve other successful evidence.'
Assert-Equal 'Collected' @($partial.toolReceipts | Where-Object toolId -eq 'grid.tool.mo2')[0].status 'Successful evidence must survive another tool failure.'
Assert-Equal 'Failed' @($partial.toolReceipts | Where-Object toolId -eq 'grid.tool.loot')[0].status 'Failure must remain tool-local.'
$unavailable = Invoke-GridToolEvidenceOrchestration -Envelope $envelope -RequestPlan $plan -ScriptsRoot $scriptsRoot -Executor $successExecutor -BindingContext $bindingContext -AvailabilityResolver { param($definition, $request) [pscustomobject]@{ status = 'Unavailable'; reason = 'synthetic unavailable' } }
Assert-Equal 'EvidenceUnavailable' $unavailable.terminalState 'No available tools must report EvidenceUnavailable.'
Assert-True (@($unavailable.toolReceipts | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.evidenceSha256) }).Count -eq 0) 'Zero-item evidence must have a stable digest.'

$transplanted = $complete.toolReceipts[0] | Select-Object *
$transplanted.workspaceId = 'workspace-other'
$transplanted.receiptSha256 = Get-GridCanonicalJsonSha256 -InputObject ($transplanted | Select-Object * -ExcludeProperty receiptSha256)
$transplantRefused=$false
try { Invoke-GridToolEvidenceOrchestration -Envelope $envelope -RequestPlan $plan -ScriptsRoot $scriptsRoot -Executor $successExecutor -BindingContext $bindingContext -ExistingReceipts @($transplanted) | Out-Null } catch { $transplantRefused=$_.Exception.Message -match 'CheckpointReceiptBindingMismatch' }
Assert-True $transplantRefused 'Receipt transplanted from another workspace must refuse resume.'
$alteredCapability = $complete.toolReceipts[0] | Select-Object *
$alteredCapability.capabilityVersion = '999.0.0'
$alteredCapability.receiptSha256 = Get-GridCanonicalJsonSha256 -InputObject ($alteredCapability | Select-Object * -ExcludeProperty receiptSha256)
$capabilityRefused=$false
try { Invoke-GridToolEvidenceOrchestration -Envelope $envelope -RequestPlan $plan -ScriptsRoot $scriptsRoot -Executor $successExecutor -BindingContext $bindingContext -ExistingReceipts @($alteredCapability) | Out-Null } catch { $capabilityRefused=$_.Exception.Message -match 'CheckpointReceiptBindingMismatch' }
Assert-True $capabilityRefused 'Receipt bound to another capability version must refuse resume.'
$alteredOutput = $complete.toolReceipts[0] | Select-Object *
$alteredOutput.stdout = 'transplanted output'
$alteredOutput.receiptSha256 = Get-GridCanonicalJsonSha256 -InputObject ($alteredOutput | Select-Object * -ExcludeProperty receiptSha256)
$outputRefused=$false
try { Invoke-GridToolEvidenceOrchestration -Envelope $envelope -RequestPlan $plan -ScriptsRoot $scriptsRoot -Executor $successExecutor -BindingContext $bindingContext -ExistingReceipts @($alteredOutput) | Out-Null } catch { $outputRefused=$_.Exception.Message -match 'CheckpointReceiptContentBindingMismatch' }
Assert-True $outputRefused 'Receipt with recomputed outer hash but altered output must refuse resume.'
$changedInputContext = $bindingContext | Select-Object *
$changedInputContext.toolInputs = [pscustomobject]@{ 'grid.tool.mo2'=[pscustomobject]@{changed=$true}; 'grid.tool.loot'=[pscustomobject]@{} }
$inputRefused=$false
try { Invoke-GridToolEvidenceOrchestration -Envelope $envelope -RequestPlan $plan -ScriptsRoot $scriptsRoot -Executor $successExecutor -BindingContext $changedInputContext -ExistingReceipts @($complete.toolReceipts[0]) | Out-Null } catch { $inputRefused=$_.Exception.Message -match 'CheckpointReceiptBindingMismatch' }
Assert-True $inputRefused 'Receipt with stale normalized input must refuse resume.'
$ssePlan = @(Resolve-GridToolEvidencePlan -ToolIds @('grid.tool.sseedit') -GameId 'skyrimspecialedition' -ClassId 'grid.class.installation-integrity' -ScriptsRoot $scriptsRoot)
Assert-Equal 'UnsupportedForClass' $ssePlan[0].availability 'SSEEdit must not run without a Class/query contract.'
$duplicateRejected = $false
try { Resolve-GridToolEvidencePlan -ToolIds @('grid.tool.mo2','GRID.TOOL.MO2') -GameId 'skyrimspecialedition' -ClassId 'grid.class.installation-integrity' -ScriptsRoot $scriptsRoot | Out-Null } catch { $duplicateRejected = $_.Exception.Message -match 'DuplicateSelection' }
Assert-True $duplicateRejected 'Duplicate tool selections must be rejected.'
Write-Host 'PASS: multi-tool evidence ordering, isolation, availability, and terminal states are deterministic.'
