$ErrorActionPreference = 'Stop'
$healthRoot = Split-Path -Parent $PSScriptRoot
$scriptsRoot = Split-Path -Parent $healthRoot
Import-Module (Join-Path $healthRoot 'Grid.Health.psm1') -Force

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }

$registry = @(Get-GridCapabilityRegistry -ScriptsRoot $scriptsRoot)
$now = [DateTimeOffset]::Parse('2026-09-14T12:00:00Z')
$base = @{
    GapId = 'gap.fixture.missing-capability'
    GameId = 'skyrimspecialedition'
    RequiredCapabilityId = 'grid.game.skyrimspecialedition.fixture.missing'
    RequestedOutcome = 'Provide the explicitly requested fixture behavior.'
    CapabilityRegistry = $registry
    AsOfUtc = $now
}

$existing = Resolve-GridCapabilityGap -GapId 'gap.fixture.existing' -GameId $base.GameId `
    -RequiredCapabilityId 'grid.health.capability.resolve' -RequestedOutcome $base.RequestedOutcome `
    -CapabilityRegistry $registry -AsOfUtc $now
Assert-Equal 'RegisteredCapabilityReady' $existing.status 'A registered semantic capability must route to exact reuse.'
Assert-True (-not $existing.mutationAuthorized) 'Gap resolution must never grant mutation authority.'

$composition = Resolve-GridCapabilityGap @base -CompositionCapabilityIds @('grid.health.capability.resolve','grid.health.script-capability.admit')
Assert-Equal 'CompositionReady' $composition.status 'An exact fully registered composition must route to a reviewable plan.'
Assert-True $composition.autoMayContinue 'AUTO may continue through inert composition planning.'

$candidate = [pscustomobject]@{
    candidateId='provider.fixture.current';compatibilityStatus='Compatible';availabilityStatus='Available'
    observedAtUtc='2026-09-14T10:00:00Z';expiresAtUtc='2026-09-15T10:00:00Z';evidenceIds=@('sha256:' + ('A' * 64))
}
$acquisition = Resolve-GridCapabilityGap @base -CommunityCandidates @($candidate)
Assert-Equal 'AcquisitionReady' $acquisition.status 'One fresh compatible evidenced provider candidate must route to acquisition review.'
Assert-True (-not $acquisition.autoMayContinue -and -not $acquisition.mutationAuthorized) 'AUTO must pause before provider acquisition authority.'

$secondCandidate = $candidate | Select-Object *
$secondCandidate.candidateId = 'provider.fixture.second'
$multiple = Resolve-GridCapabilityGap @base -CommunityCandidates @($candidate,$secondCandidate)
Assert-Equal 'CandidateSelectionRequired' $multiple.status 'AUTO must not invent a preference between multiple compatible candidates.'

$stale = $candidate | Select-Object *
$stale.expiresAtUtc = '2026-09-14T11:00:00Z'
$creationRequired = Resolve-GridCapabilityGap @base -CommunityCandidates @($stale)
Assert-Equal 'CreationProposalRequired' $creationRequired.status 'A stale community candidate must not become an acquisition route.'

$needsInput = Resolve-GridCapabilityGap @base -CommunityCandidates @($candidate) -MissingInputs @('exact runtime reference identity')
Assert-Equal 'NeedsStructuredInput' $needsInput.status 'Missing structured evidence must hold acquisition and creation planning.'

$admission = [pscustomobject]@{status='ReusableProposalReady';proposalSha256=('B' * 64)}
$proposal = Resolve-GridCapabilityGap @base -ScriptAdmission $admission
Assert-Equal 'CreationProposalReady' $proposal.status 'An admitted reusable CODE proposal must route to implementation review.'
Assert-True ($proposal.evidenceIds -contains ('sha256:' + ('B' * 64))) 'The CODE-admission proposal digest must remain bound as evidence.'

$repeat = Resolve-GridCapabilityGap @base -ScriptAdmission $admission
Assert-Equal $proposal.resolutionSha256 $repeat.resolutionSha256 'Equivalent gap evidence must produce a deterministic resolution fingerprint.'

Write-Host 'PASS: capability-gap routing deterministically chooses reuse, composition, acquisition, structured input, or admitted CODE creation without authority.'
