$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $gameRoot 'health\actions\New-GridSkyrimComponentRecoveryPlan.ps1')

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }

$dependencies = @(
    [pscustomobject]@{ pluginName='3DNPC.esp'; sourceProvider='Interesting NPCs Update'; scriptName='BaseQuestScript'; requiredVirtualPath='scripts\BaseQuestScript.pex'; referenceCount=2 },
    [pscustomobject]@{ pluginName='3DNPC.esp'; sourceProvider='Interesting NPCs Update'; scriptName='UpdateQuestScript'; requiredVirtualPath='scripts\UpdateQuestScript.pex'; referenceCount=1 },
    [pscustomobject]@{ pluginName='3DNPC.esp'; sourceProvider='Interesting NPCs Update'; kind='Mesh'; requiredVirtualPath='meshes\3dnpc\missing.nif'; referenceCount=2 },
    [pscustomobject]@{ pluginName='Unknown.esp'; sourceProvider='Unknown Winner'; scriptName='UnknownScript'; requiredVirtualPath='scripts\UnknownScript.pex'; referenceCount=1 }
)
$metadata = @(
    [pscustomobject]@{ modId='mod-base'; name='Interesting NPCs Base'; enabled=$true; version='4.5'; metadataSha256=('A' * 64); repository='Nexus'; nexusGameName='SkyrimSE'; nexusModId=29194; installationFile='3DNPC-base.7z' },
    [pscustomobject]@{ modId='mod-update'; name='Interesting NPCs Update'; enabled=$true; version='4.54'; metadataSha256=('B' * 64); repository='Nexus'; nexusGameName='SkyrimSE'; nexusModId=29194; installationFile='D:\Old Library\3DNPC-update.7z' },
    [pscustomobject]@{ modId='mod-unknown'; name='Unknown Winner'; enabled=$true; version='1'; metadataSha256=('C' * 64); repository=''; nexusGameName=''; nexusModId=$null; installationFile='Unknown.7z' }
)
$archives = @(
    [pscustomobject]@{ modId='mod-base'; modName='Interesting NPCs Base'; kind='InstallationArchive'; exactLeafName='3DNPC-base.7z'; state='Present'; length=1234; hashStatus='Complete'; sha256=('D' * 64) }
)
$sidecars = @(
    [pscustomobject]@{ modId='mod-base'; modName='Interesting NPCs Base'; kind='MetadataSidecar'; exactLeafName='3DNPC-base.7z.meta'; state='Present'; hashStatus='Complete'; sha256=('F' * 64); providerIdentity=[pscustomobject]@{repository='Nexus';gameName='SkyrimSE';modId=29194;fileId=5678;version='4.5';archiveLeaf='3DNPC-base.7z';status='ObservedComplete'} }
)

$plan = New-GridSkyrimComponentRecoveryPlan -CaseId 'baseline-fixture' -ContextFingerprint ('E' * 64) -Dependencies $dependencies -ModMetadata $metadata -SourceArchives $archives -SourceArchiveSidecars $sidecars
Assert-Equal 'ComponentLineageEvidenceRequired' $plan.status 'An unresolved component must hold the whole plan before repair.'
Assert-Equal 2 $plan.summary.affectedPluginCount 'Dependencies must aggregate by consuming plugin.'
Assert-Equal 'MissingPluginFileComponentRecovery' $plan.planKind 'The plan must identify its generalized missing-component-file scope.'
Assert-Equal 4 $plan.summary.missingDependencyCount 'The plan must retain all distinct missing dependencies.'
Assert-Equal 0 $plan.summary.manualAcquisitionReadyCount 'A component with a locally hashed archive must be inspected before Grid requests another download.'
Assert-Equal 0 $plan.summary.repairReadyCount 'Hashed archives are not repair-ready without content inspection.'

$threeDnpc = @($plan.components | Where-Object pluginName -eq '3DNPC.esp')[0]
Assert-Equal 'OverrideProvider' $threeDnpc.overrideProviderRole 'The plugin winner must not be mislabeled as an asset provider.'
Assert-True ([string]$threeDnpc.requiredFileSetSha256 -match '^[A-F0-9]{64}$') 'The plan must bind the complete required-file set even when display rows are truncated.'
Assert-Equal 'ExactMetadataIdentity' $threeDnpc.lineageStatus 'Matching repository, game, and mod ID must establish component lineage.'
Assert-Equal 2 @($threeDnpc.lineageCandidates).Count 'A split base/update installation must retain both exact identity candidates.'
Assert-True (@($threeDnpc.requiredFiles | Where-Object { $_.dependencyKind -eq 'Mesh' -and $_.virtualPath -eq 'meshes\3dnpc\missing.nif' }).Count -eq 1) 'The same lineage plan must retain an exact missing mesh requirement.'
Assert-Equal 'ArchiveContentInspectionRequired' $threeDnpc.recoveryState 'A hashed archive must still require bounded content proof.'
Assert-True (@($threeDnpc.lineageCandidates | Where-Object { $_.name -eq 'Interesting NPCs Base' -and $_.archiveClaim.providerIdentityStatus -eq 'Verified' -and $_.archiveClaim.providerIdentity.fileId -eq 5678 }).Count -eq 1) 'A hashed sidecar may establish only exact provider identity, not archive contents.'
Assert-True (@($threeDnpc.lineageCandidates | Where-Object { $_.name -eq 'Interesting NPCs Update' -and $_.archiveClaim.status -eq 'ExternalPathClaimAuthorizationRequired' }).Count -eq 1) 'An absolute legacy archive claim must require explicit external-read authority.'
Assert-True (@($threeDnpc.lineageCandidates | Where-Object { $_.name -eq 'Interesting NPCs Update' -and $_.manualAcquisition.officialFilesUri -eq 'https://www.nexusmods.com/skyrimspecialedition/mods/29194?tab=files' -and $_.manualAcquisition.expectedArchiveLeaf -eq '3DNPC-update.7z' }).Count -eq 1) 'Manual recovery must bind the official Files page and exact expected archive leaf.'

$acquisitionPlan = New-GridSkyrimComponentRecoveryPlan -CaseId 'baseline-fixture-acquisition' -ContextFingerprint ('E' * 64) -Dependencies $dependencies -ModMetadata $metadata -SourceArchives @() -SourceArchiveSidecars @()
$actionManifest = New-GridSkyrimComponentRecoveryActionManifest -Plan $acquisitionPlan -AffectedMods @('Interesting NPCs Update', 'Unknown Winner', 'Interesting NPCs Update')
Assert-Equal 1 $actionManifest.summary.exactArchiveAcquisitionCount 'The action manifest must request only the current override provider archive, not every archive sharing its Nexus mod identity.'
Assert-Equal 1 $actionManifest.summary.lineageEvidenceRequiredCount 'The action manifest must separate unresolved source identities from download requests.'
Assert-Equal 2 $actionManifest.summary.affectedModCount 'The action manifest must deduplicate and preserve every evidence-backed affected mod.'
Assert-Equal 0 $actionManifest.summary.provenUpdateRequiredCount 'Archive acquisition must not be mislabeled as a required update.'
Assert-Equal 0 $actionManifest.summary.provenReinstallationRequiredCount 'Archive acquisition must not be mislabeled as a required reinstall.'
Assert-Equal 0 $actionManifest.summary.plannedPatchChangeCount 'An inert recovery plan must not claim that Grid will create or modify a patch.'
Assert-True (@($actionManifest.manualAcquisitions | Where-Object { $_.expectedArchiveLeaf -eq '3DNPC-update.7z' }).Count -eq 1) 'The complete human action list must preserve the exact expected archive leaf.'
Assert-True (@($actionManifest.manualAcquisitions | Where-Object { $_.expectedArchiveLeaf -eq '3DNPC-base.7z' }).Count -eq 0) 'A same-page archive that is not the current override provider must not become a mandatory download.'
Assert-True (@($actionManifest.manualAcquisitions | Where-Object { $_.affectedPlugins -contains '3DNPC.esp' }).Count -eq 1) 'Each affected plugin must be assigned to at most one next archive acquisition.'
Assert-True (@($actionManifest.lineageRequirements | Where-Object { $_.pluginName -eq 'Unknown.esp' -and $_.status -eq 'SourceIdentityUnresolved' }).Count -eq 1) 'Unresolved component lineage must remain explicit and non-prescriptive.'
$reorderedActionManifest = New-GridSkyrimComponentRecoveryActionManifest -Plan $acquisitionPlan -AffectedMods @('Unknown Winner', 'Interesting NPCs Update')
Assert-Equal $actionManifest.manifestSha256 $reorderedActionManifest.manifestSha256 'Affected-mod input order and duplicates must not change the action manifest identity.'

$ambiguousWinnerMetadata = @($metadata) + @(
    [pscustomobject]@{ modId='mod-update-duplicate'; name='Interesting NPCs Update'; enabled=$true; version='4.54'; metadataSha256=('7' * 64); repository='Nexus'; nexusGameName='SkyrimSE'; nexusModId=29194; installationFile='3DNPC-update-alternate.7z' }
)
$ambiguousWinnerPlan = New-GridSkyrimComponentRecoveryPlan -CaseId 'baseline-fixture-ambiguous-winner' -ContextFingerprint ('E' * 64) -Dependencies $dependencies -ModMetadata $ambiguousWinnerMetadata -SourceArchives @() -SourceArchiveSidecars @()
$ambiguousWinnerManifest = New-GridSkyrimComponentRecoveryActionManifest -Plan $ambiguousWinnerPlan -AffectedMods @('Interesting NPCs Update', 'Unknown Winner')
Assert-Equal 0 $ambiguousWinnerPlan.summary.manualAcquisitionReadyCount 'Multiple current-provider archive claims must not be reported as one ready manual acquisition.'
Assert-Equal 0 $ambiguousWinnerManifest.summary.exactArchiveAcquisitionCount 'Multiple current-provider archive claims must not become mandatory downloads.'
Assert-True (@($ambiguousWinnerManifest.lineageRequirements | Where-Object { $_.pluginName -eq '3DNPC.esp' -and $_.status -eq 'ArchiveIdentityAmbiguous' }).Count -eq 1) 'Ambiguous current-provider archives must remain an explicit non-download source-resolution requirement.'

$inspections = @(
    [pscustomobject]@{
        schemaVersion=1;status='Complete';archiveLength=1234;archiveSha256=('D' * 64);entrySetSha256=('1' * 64)
        inspectedEntryCount=25;evidenceId='archive-content.1234567890abcdef12345678';fomodStatus='Absent'
        matches=@(
            [pscustomobject]@{pluginName='3DNPC.esp';requiredVirtualPath='scripts\BaseQuestScript.pex';mappingRule='ArchiveRoot';sha256=('2' * 64)},
            [pscustomobject]@{pluginName='3DNPC.esp';requiredVirtualPath='scripts\UpdateQuestScript.pex';mappingRule='ExplicitDataDirectory';sha256=('3' * 64)},
            [pscustomobject]@{pluginName='3DNPC.esp';requiredVirtualPath='meshes\3dnpc\missing.nif';mappingRule='ArchiveRoot';sha256=('4' * 64)}
        )
    }
)
$singleMatchInspection = @(
    [pscustomobject]@{
        schemaVersion=1;status='Complete';archiveLength=1234;archiveSha256=('D' * 64);entrySetSha256=('A' * 64)
        inspectedEntryCount=1;evidenceId='archive-content.aaaaaaaaaaaaaaaaaaaaaaaa';fomodStatus='Absent'
        matches=@(
            [pscustomobject]@{pluginName='3DNPC.esp';requiredVirtualPath='scripts\BaseQuestScript.pex';mappingRule='ArchiveRoot';sha256=('B' * 64)}
        )
    }
)
$singleMatchPlan = New-GridSkyrimComponentRecoveryPlan -CaseId 'baseline-fixture-single-match' -ContextFingerprint ('E' * 64) -Dependencies $dependencies -ModMetadata $metadata -SourceArchives $archives -SourceArchiveSidecars $sidecars -ArchiveInspections $singleMatchInspection
$singleMatchThreeDnpc = @($singleMatchPlan.components | Where-Object pluginName -eq '3DNPC.esp')[0]
Assert-Equal 'AdditionalSourceEvidenceRequired' $singleMatchThreeDnpc.recoveryState 'A single verified archive match must remain a one-element collection and report partial coverage.'
Assert-True (@($singleMatchThreeDnpc.lineageCandidates | Where-Object { $_.name -eq 'Interesting NPCs Base' -and $_.contentInspection.suppliedRequiredFileCount -eq 1 -and $_.contentInspection.coverage -eq 'Partial' }).Count -eq 1) 'One matching required file must be counted without a StrictMode scalar-property failure.'

$inspectedPlan = New-GridSkyrimComponentRecoveryPlan -CaseId 'baseline-fixture-inspected' -ContextFingerprint ('E' * 64) -Dependencies $dependencies -ModMetadata $metadata -SourceArchives $archives -SourceArchiveSidecars $sidecars -ArchiveInspections $inspections
$inspectedThreeDnpc = @($inspectedPlan.components | Where-Object pluginName -eq '3DNPC.esp')[0]
Assert-Equal 'RepairSourceVerified' $inspectedThreeDnpc.recoveryState 'Complete direct content coverage must advance only the evidenced component.'
Assert-Equal 1 $inspectedPlan.summary.repairReadyCount 'Repair readiness must be counted per fully covered component.'
Assert-True (@($inspectedThreeDnpc.lineageCandidates | Where-Object { $_.name -eq 'Interesting NPCs Base' -and $_.contentInspection.status -eq 'Verified' -and $_.contentInspection.coverage -eq 'Complete' }).Count -eq 1) 'Archive content evidence must bind to the exact hashed candidate and report component coverage.'

$exactAssessment = [pscustomobject]@{
    schemaVersion=1;status='Complete';archiveSha256=('D' * 64);primaryPluginName='3DNPC.esp'
    candidateRole='ExactRestoration';compatibilityStatus='NotRequiredForExactRestoration';requiredEvidence=@()
    evidenceFingerprint=('5' * 64);evidenceId='archive-candidate.1234567890abcdef12345678';sourceModIds=@('mod-base')
}
$exactPlan = New-GridSkyrimComponentRecoveryPlan -CaseId 'baseline-fixture-exact' -ContextFingerprint ('E' * 64) -Dependencies $dependencies -ModMetadata $metadata -SourceArchives $archives -SourceArchiveSidecars $sidecars -ArchiveCandidateAssessments @($exactAssessment)
$exactThreeDnpc = @($exactPlan.components | Where-Object pluginName -eq '3DNPC.esp')[0]
Assert-Equal 'ArchivePayloadVerificationRequired' $exactThreeDnpc.recoveryState 'An exact plugin/archive pair must still prove its packaged payload before restoration.'
Assert-Equal 1 $exactPlan.summary.exactRestorationCandidateCount 'Exact restoration evidence must be visible in the recovery summary.'

$updateAssessment = [pscustomobject]@{
    schemaVersion=1;status='Complete';archiveSha256=('D' * 64);primaryPluginName='3DNPC.esp'
    candidateRole='CompleteUpdateCandidate';compatibilityStatus='RequiresDependentCompatibilityEvidence'
    requiredEvidence=@('DependentPluginCompatibilityMatrix');evidenceFingerprint=('6' * 64)
    evidenceId='archive-candidate.abcdef1234567890abcdef12';sourceModIds=@('mod-base')
}
$updatePlan = New-GridSkyrimComponentRecoveryPlan -CaseId 'baseline-fixture-update' -ContextFingerprint ('E' * 64) -Dependencies $dependencies -ModMetadata $metadata -SourceArchives $archives -SourceArchiveSidecars $sidecars -ArchiveInspections $inspections -ArchiveCandidateAssessments @($updateAssessment)
$updateThreeDnpc = @($updatePlan.components | Where-Object pluginName -eq '3DNPC.esp')[0]
Assert-Equal 'CandidateCompatibilityEvidenceRequired' $updateThreeDnpc.recoveryState 'A version-changing archive must not become repair-ready from file coverage alone.'
Assert-Equal 1 $updatePlan.summary.versionChangingCandidateCount 'Version-changing archive evidence must be visible in the recovery summary.'
Assert-Equal 0 $updatePlan.summary.repairReadyCount 'A compatibility-gated update must remain inert.'

$alternateArchive = [pscustomobject]@{ modId='mod-base'; modName='Interesting NPCs Base'; kind='InstallationArchive'; exactLeafName='3DNPC-5.0.7z'; state='Present'; length=2345; hashStatus='Complete'; sha256=('8' * 64) }
$alternateUpdateAssessment = $updateAssessment.PSObject.Copy()
$alternateUpdateAssessment.archiveSha256 = ('8' * 64)
$alternateUpdateAssessment.evidenceId = 'archive-candidate.bbbbbbbbbbbbbbbbbbbbbbbb'
$alternateUpdateAssessment.evidenceFingerprint = ('9' * 64)
$combinedPlan = New-GridSkyrimComponentRecoveryPlan -CaseId 'baseline-fixture-combined' -ContextFingerprint ('E' * 64) -Dependencies $dependencies -ModMetadata $metadata -SourceArchives @($archives + $alternateArchive) -SourceArchiveSidecars $sidecars -ArchiveInspections $inspections -ArchiveCandidateAssessments @($exactAssessment,$alternateUpdateAssessment)
$combinedThreeDnpc = @($combinedPlan.components | Where-Object pluginName -eq '3DNPC.esp')[0]
Assert-Equal 2 @($combinedThreeDnpc.archiveCandidates).Count 'Every exact-identity downloaded version must remain visible on the affected component.'
Assert-Equal 'RepairSourceVerified' $combinedThreeDnpc.recoveryState 'A separately verified exact restoration must not be blocked merely because a newer optional candidate is also present.'

$selectionAssessment = $updateAssessment.PSObject.Copy()
$selectionAssessment.status = 'SelectionRequired'
$selectionAssessment.evidenceId = 'archive-candidate.aaaaaaaaaaaaaaaaaaaaaaaa'
$selectionAssessment.evidenceFingerprint = ('7' * 64)
$selectionPlan = New-GridSkyrimComponentRecoveryPlan -CaseId 'baseline-fixture-selection' -ContextFingerprint ('E' * 64) -Dependencies $dependencies -ModMetadata $metadata -SourceArchives $archives -SourceArchiveSidecars $sidecars -ArchiveInspections $inspections -ArchiveCandidateAssessments @($selectionAssessment)
$selectionThreeDnpc = @($selectionPlan.components | Where-Object pluginName -eq '3DNPC.esp')[0]
Assert-Equal 'CandidateSelectionRequired' $selectionThreeDnpc.recoveryState 'A FOMOD candidate must preserve its exact selection gate ahead of compatibility review.'

$unknown = @($plan.components | Where-Object pluginName -eq 'Unknown.esp')[0]
Assert-Equal 'ComponentLineageEvidenceRequired' $unknown.recoveryState 'A provider name alone must not establish missing-file ownership.'
Assert-True ([string]$plan.planSha256 -match '^[A-F0-9]{64}$') 'The plan must have a deterministic SHA-256 binding.'
Assert-True (@($plan.invariants | Where-Object { $_ -match 'live MO2 profile.*no separate or original modlist' }).Count -eq 1) 'Recovery must bind the selected live profile as its source of truth instead of requiring an original modlist.'
$planValidation=Test-GridSkyrimComponentRecoveryPlan -Plan $plan -ExpectedCaseId 'baseline-fixture'
Assert-True $planValidation.IsValid ('A generated component recovery plan must pass its public content binding: '+($planValidation.Errors-join'; '))
$tamperedPlan=$plan|ConvertTo-Json -Depth 30|ConvertFrom-Json
$tamperedPlan.summary.missingDependencyCount=999
Assert-True (-not(Test-GridSkyrimComponentRecoveryPlan -Plan $tamperedPlan -ExpectedCaseId 'baseline-fixture').IsValid) 'A changed recovery summary must fail the sealed plan content binding.'

$again = New-GridSkyrimComponentRecoveryPlan -CaseId 'baseline-fixture' -ContextFingerprint ('E' * 64) -Dependencies @($dependencies[3],$dependencies[2],$dependencies[1],$dependencies[0]) -ModMetadata @($metadata[2],$metadata[1],$metadata[0]) -SourceArchives $archives -SourceArchiveSidecars $sidecars
Assert-Equal $plan.planSha256 $again.planSha256 'Input ordering must not change the recovery plan identity.'

$clean = New-GridSkyrimComponentRecoveryPlan -CaseId 'baseline-clean' -ContextFingerprint ('F' * 64) -Dependencies @() -ModMetadata @() -SourceArchives @()
Assert-Equal 'NotRequired' $clean.status 'A profile with no missing component files needs no component recovery plan.'

'Component recovery plan checks passed.'
