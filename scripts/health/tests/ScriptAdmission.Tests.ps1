$ErrorActionPreference = 'Stop'
$healthRoot = Split-Path -Parent $PSScriptRoot
$scriptsRoot = Split-Path -Parent $healthRoot
Import-Module (Join-Path $healthRoot 'Grid.Health.psm1') -Force

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }

$registry = @(Get-GridCapabilityRegistry -ScriptsRoot $scriptsRoot)
$existing = [pscustomobject]@{
    requestedOperation = 'Resolve registered capability dependencies'; capabilityId = 'grid.health.capability.resolve'; requestedBy = 'Developer'
    publicCapability = $true; uniqueModFormat = $false; owner = [pscustomobject]@{ kind = 'SharedHealth'; gameId = $null; toolId = $null; modId = $null }
    reusedCapabilityIds = @(); proposedFiles = @(); exampleCases = @(); caseSpecificIdentifiers = @(); parameterContract = @{}; authority = @{}; terminalStates = @('Resolved')
}
$existingResult = Resolve-GridScriptCapabilityAdmission -Candidate $existing -CapabilityRegistry $registry
Assert-Equal 'ExistingCapability' $existingResult.status 'An existing semantic ID must resolve to reuse.'
Assert-True (-not $existingResult.mutationAuthorized) 'Admission must never grant mutation authority.'

$reusable = [pscustomobject]@{
    requestedOperation = 'Trace a missing world reference from parameterized evidence'; capabilityId = 'grid.game.skyrimspecialedition.world-reference.trace'; requestedBy = 'Grid'
    publicCapability = $true; uniqueModFormat = $false; owner = [pscustomobject]@{ kind = 'Game'; gameId = 'skyrimspecialedition'; toolId = $null; modId = $null }
    reusedCapabilityIds = @(); proposedFiles = @([pscustomobject]@{ path = 'scripts/games/skyrimspecialedition/health/Trace-GridWorldReference.ps1'; sourceText = 'param([string]$ReferenceIdentity)' })
    exampleCases = @([pscustomobject]@{ situationId = 'missing-door' },[pscustomobject]@{ situationId = 'missing-container' })
    caseSpecificIdentifiers = @(); parameterContract = @{ referenceIdentity = 'string' }; authority = @{ kind = 'ExternalRead' }; terminalStates = @('Resolved','NeedsEvidence','Failed')
}
$reusableResult = Resolve-GridScriptCapabilityAdmission -Candidate $reusable -CapabilityRegistry $registry
Assert-Equal 'ReusableProposalReady' $reusableResult.status 'Two parameterized situations may produce a reusable proposal.'
Assert-Equal 'scripts/games/skyrimspecialedition/' $reusableResult.canonicalDirectory 'Game-data behavior belongs in the exact game root.'

$caseData = [pscustomobject]@{
    requestedOperation = 'Fix one reported actor'; capabilityId = 'grid.game.skyrimspecialedition.actor.fix'; requestedBy = 'User'
    publicCapability = $true; uniqueModFormat = $false; owner = [pscustomobject]@{ kind = 'Game'; gameId = 'skyrimspecialedition'; toolId = $null; modId = $null }
    reusedCapabilityIds = @(); proposedFiles = @(); exampleCases = @([pscustomobject]@{ situationId = 'one-actor' })
    caseSpecificIdentifiers = @('actor-name','actor-form-id'); parameterContract = @{}; authority = @{}; terminalStates = @('Fixed')
}
$caseResult = Resolve-GridScriptCapabilityAdmission -Candidate $caseData -CapabilityRegistry $registry
Assert-Equal 'RuntimeCaseData' $caseResult.status 'Incident identities must remain runtime case data.'

$caseShaped = [pscustomobject]@{
    requestedOperation = 'Restore a parameterized hatch'; capabilityId = 'grid.game.skyrimspecialedition.hatch.restore'; requestedBy = 'Developer'
    publicCapability = $true; uniqueModFormat = $false; owner = [pscustomobject]@{ kind = 'Game'; gameId = 'skyrimspecialedition'; toolId = $null; modId = $null }
    reusedCapabilityIds = @(); proposedFiles = @([pscustomobject]@{ path = 'scripts/games/skyrimspecialedition/Fix-OneHatch.ps1'; sourceText = 'param()' })
    exampleCases = @([pscustomobject]@{ situationId = 'hatch-a' },[pscustomobject]@{ situationId = 'hatch-b' }); caseSpecificIdentifiers = @()
    parameterContract = @{}; authority = @{}; terminalStates = @('Fixed')
}
$rejected = Resolve-GridScriptCapabilityAdmission -Candidate $caseShaped -CapabilityRegistry $registry
Assert-Equal 'Rejected' $rejected.status 'Case-shaped production filenames must be rejected.'

$gap = [pscustomobject]@{
    gapId='gap.request.fixture-design'; gameId='skyrimspecialedition'; requiredCapabilityId='grid.game.skyrimspecialedition.world-placement.diagnose'
    requestedOutcome='Diagnose parameterized world-placement conflicts.'; status='CreationProposalRequired'; route='CreateCapabilityProposal'
}
$draft = New-GridScriptCapabilityProposalDraft -GapResolution $gap
$repeatDraft = New-GridScriptCapabilityProposalDraft -GapResolution $gap
Assert-Equal 'NeedsDesignEvidence' $draft.status 'A creation route must become an explicit inert design draft.'
Assert-Equal 'scripts/games/skyrimspecialedition/' $draft.canonicalDirectory 'The draft must route implementation to the canonical game owner.'
Assert-True (-not $draft.executable -and -not $draft.mutationAuthorized) 'A proposal draft must not execute or authorize mutation.'
Assert-Equal $draft.proposalDraftSha256 $repeatDraft.proposalDraftSha256 'Equivalent gap evidence must produce the same draft digest.'

Write-Host 'PASS: script admission deterministically resolves reuse, reusable proposals, runtime case data, and rejection without mutation.'
