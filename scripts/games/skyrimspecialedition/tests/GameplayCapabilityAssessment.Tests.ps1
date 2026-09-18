#requires -Version 5.1

$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent $PSScriptRoot
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $gameRoot)
Import-Module (Join-Path $scriptsRoot 'health\Grid.Health.psm1') -Force
Import-Module (Join-Path $gameRoot 'health\Grid.Health.Skyrim.psm1') -Force

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }
function Assert-Throws([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    try { & $Action; throw "$Message Expected '$Pattern'." }
    catch { if ($_.Exception.Message -notmatch $Pattern) { throw "$Message Unexpected: $($_.Exception.Message)" } }
}

$capabilityId = 'grid.capability.equipment.multiple-rings'
$profileEvidenceId = 'sha256:' + ('F' * 64)
$currentTime = [DateTimeOffset]::Parse('2026-09-12T00:00:00Z')
$baseInventory = @(
    [pscustomobject]@{ name='Immersive Jewelry 1.06a'; enabled=$true; nexusModId=5336; version='1.06a'; installationFile='Immersive Jewelry 1.06a-5336-1-06a.7z'; metadataSha256=('A' * 64) },
    [pscustomobject]@{ name='Address Library All in One'; enabled=$true; nexusModId=32444; version='11'; installationFile='Address Library.7z'; metadataSha256=('B' * 64) },
    [pscustomobject]@{ name='SkyUI'; enabled=$true; nexusModId=12604; version='5.2'; installationFile='SkyUI.7z'; metadataSha256=('C' * 64) },
    [pscustomobject]@{ name='MCM Helper'; enabled=$true; nexusModId=53000; version='1.5'; installationFile='MCM Helper.7z'; metadataSha256=('D' * 64) }
)

$partial = Get-GridSkyrimGameplayCapabilityAssessment -CapabilityId $capabilityId -ModInventory $baseInventory -ProfileEvidenceId $profileEvidenceId -AsOfUtc $currentTime
Assert-Equal 'Partial' $partial.installedStatus 'An enabled update-only Immersive Jewelry row must retain installed but incomplete coverage.'
Assert-Equal 1 @($partial.installedProviders).Count 'The installed provider must be preserved instead of reported absent.'
Assert-True ([string]$partial.installedProviders[0].status -match 'main payload') 'The missing required main payload evidence must be named.'
Assert-Equal 'Current' $partial.discoveryStatus 'Unexpired curated source evidence must be current.'
Assert-Equal 1 @($partial.communityCandidates).Count 'Current discovery must retain the cataloged candidate.'
Assert-Equal 'Unresolved' $partial.communityCandidates[0].compatibilityStatus 'Undocumented coexistence with active Immersive Jewelry must not be guessed compatible.'
Assert-True ([string]$partial.communityCandidates[0].compatibilityDetail -match 'coexistence') 'The unresolved interaction must be explicit.'
Assert-True (@($partial.evidenceIds | Where-Object { $_ -eq $profileEvidenceId }).Count -eq 1) 'Assessment must bind the current profile evidence identity.'

$envelope = [pscustomobject]@{ requestId='multiple-rings-fixture' }
$projected = New-GridCapabilityAssessmentResult -Envelope $envelope -Assessment $partial
Assert-Equal 'CapabilityAssessment' $projected.resultKind 'Gameplay coverage must project as an assessment, not an unsupported error.'
Assert-Equal 'EvidenceComplete' $projected.terminalState 'Current installed and discovery evidence must be useful even while coexistence is unresolved.'
Assert-True (-not $projected.repairState.applyEnabled) 'A community candidate must never authorize installation.'

$complete = Get-GridSkyrimGameplayCapabilityAssessment -CapabilityId $capabilityId -ModInventory $baseInventory -ProfileEvidenceId $profileEvidenceId -AsOfUtc $currentTime -ComponentEvidence @(
    [pscustomobject]@{ providerId='nexus.skyrimspecialedition.5336'; componentId='main'; status='Verified'; evidenceIds=@('sha256:' + ('E' * 64)) }
)
Assert-Equal 'Satisfied' $complete.installedStatus 'Independent main-component evidence plus the update archive must satisfy installed coverage.'

$withoutJewelry = @($baseInventory | Where-Object { $_.nexusModId -ne 5336 })
$compatible = Get-GridSkyrimGameplayCapabilityAssessment -CapabilityId $capabilityId -ModInventory $withoutJewelry -ProfileEvidenceId $profileEvidenceId -AsOfUtc $currentTime
Assert-Equal 'Absent' $compatible.installedStatus 'A profile with no matching installed provider must be explicitly absent.'
Assert-Equal 'Compatible' $compatible.communityCandidates[0].compatibilityStatus 'Satisfied declared requirements and no cataloged blocker must resolve compatible.'

$withoutAddressLibrary = @($withoutJewelry | Where-Object { $_.nexusModId -ne 32444 })
$needsDependency = Get-GridSkyrimGameplayCapabilityAssessment -CapabilityId $capabilityId -ModInventory $withoutAddressLibrary -ProfileEvidenceId $profileEvidenceId -AsOfUtc $currentTime
Assert-Equal 'PatchRequired' $needsDependency.communityCandidates[0].compatibilityStatus 'A missing declared runtime dependency must not be called compatible.'
Assert-True (@($needsDependency.communityCandidates[0].requiredPatches) -contains 'Address Library for SKSE Plugins') 'The exact missing support package must be named.'

$oldTng = @($withoutJewelry + [pscustomobject]@{ name='The New Gentleman'; enabled=$true; version='4.0.0'; installationFile='TNG.7z' })
$incompatible = Get-GridSkyrimGameplayCapabilityAssessment -CapabilityId $capabilityId -ModInventory $oldTng -ProfileEvidenceId $profileEvidenceId -AsOfUtc $currentTime
Assert-Equal 'Incompatible' $incompatible.communityCandidates[0].compatibilityStatus 'A documented below-minimum TNG version must resolve incompatible.'

$stale = Get-GridSkyrimGameplayCapabilityAssessment -CapabilityId $capabilityId -ModInventory $withoutJewelry -ProfileEvidenceId $profileEvidenceId -AsOfUtc ([DateTimeOffset]::Parse('2026-10-12T00:00:00Z'))
Assert-Equal 'Stale' $stale.discoveryStatus 'Expired curated observations must fail closed as stale.'
Assert-Equal 0 @($stale.communityCandidates).Count 'Stale discovery must never retain recommendations.'

$providerObservationUnsigned = [pscustomobject][ordered]@{
    schemaVersion=1; capabilityId=$capabilityId; status='Observed'; observedAtUtc='2026-10-12T00:00:00Z'; expiresAtUtc='2026-10-13T00:00:00Z'
    candidates=@([pscustomobject][ordered]@{
        candidateId='nexus.skyrimspecialedition.180015'; provider='Nexus Mods'; gameId='skyrimspecialedition'; modId=180015
        uri='https://www.nexusmods.com/skyrimspecialedition/mods/180015'; status='Observed'; version='0.4.6'; updatedAtUtc='2026-06-22T08:08:00Z'
        evidenceId=('sha256:' + ('1' * 64)); detail=$null
    }); credentialHandle='os-protected:nexus-fixture'; mutationPerformed=$false
}
$providerObservation = $providerObservationUnsigned | Select-Object *
$providerObservation | Add-Member -NotePropertyName observationSha256 -NotePropertyValue (Get-GridCanonicalJsonSha256 -InputObject $providerObservationUnsigned)
$refreshedStale = Get-GridSkyrimGameplayCapabilityAssessment -CapabilityId $capabilityId -ModInventory $withoutJewelry -ProfileEvidenceId $profileEvidenceId `
    -AsOfUtc ([DateTimeOffset]::Parse('2026-10-12T12:00:00Z')) -CommunityObservation $providerObservation
Assert-Equal 'Current' $refreshedStale.discoveryStatus 'A current exact provider observation must retain provider existence after catalog expiry.'
Assert-Equal 1 @($refreshedStale.communityCandidates).Count 'A current exact provider observation must retain the observed candidate.'
Assert-Equal 'Unresolved' $refreshedStale.communityCandidates[0].compatibilityStatus 'Current provider existence must not refresh stale compatibility claims.'
Assert-True ([string]$refreshedStale.communityCandidates[0].compatibilityDetail -match 'stale') 'Stale compatibility knowledge must be explicit.'

$changedVersionUnsigned = $providerObservationUnsigned | Select-Object *
$changedVersionUnsigned.candidates = @($providerObservationUnsigned.candidates | ForEach-Object { $_ | Select-Object * })
$changedVersionUnsigned.candidates[0].version = '0.5.0'
$changedVersionUnsigned.observedAtUtc = '2026-09-12T00:00:00Z'
$changedVersionUnsigned.expiresAtUtc = '2026-09-13T00:00:00Z'
$changedVersion = $changedVersionUnsigned | Select-Object *
$changedVersion | Add-Member -NotePropertyName observationSha256 -NotePropertyValue (Get-GridCanonicalJsonSha256 -InputObject $changedVersionUnsigned)
$changed = Get-GridSkyrimGameplayCapabilityAssessment -CapabilityId $capabilityId -ModInventory $withoutJewelry -ProfileEvidenceId $profileEvidenceId `
    -AsOfUtc ([DateTimeOffset]::Parse('2026-09-12T12:00:00Z')) -CommunityObservation $changedVersion
Assert-Equal '0.5.0' $changed.communityCandidates[0].version 'The current Nexus version must replace the catalog snapshot version in presentation.'
Assert-Equal 'Unresolved' $changed.communityCandidates[0].compatibilityStatus 'A newer provider version must invalidate reviewed compatibility instead of inheriting it.'
Assert-True ([string]$changed.communityCandidates[0].compatibilityDetail -match 'changed release') 'The version-review requirement must be explicit.'

$nearName = @([pscustomobject]@{ name='Immersive Jewelry Compatibility Patch'; enabled=$true; version='1'; installationFile='patch.7z' })
$notFalsePositive = Get-GridSkyrimGameplayCapabilityAssessment -CapabilityId $capabilityId -ModInventory $nearName -ProfileEvidenceId $profileEvidenceId -AsOfUtc $currentTime
Assert-Equal 'Absent' $notFalsePositive.installedStatus 'Partial-name matching must not invent an installed provider.'
Assert-Throws { Get-GridSkyrimGameplayCapabilityAssessment -CapabilityId 'grid.capability.equipment.unknown' -ModInventory @() -ProfileEvidenceId $profileEvidenceId -AsOfUtc $currentTime | Out-Null } 'GameplayCapabilityNotCataloged' 'Unknown explicit capability IDs must fail closed.'

'Skyrim gameplay capability assessment checks passed.'
