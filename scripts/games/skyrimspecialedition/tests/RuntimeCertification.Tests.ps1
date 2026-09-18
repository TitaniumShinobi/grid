$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $gameRoot 'health\actions\Invoke-GridSkyrimRuntimeCertification.ps1')
. (Join-Path $gameRoot 'health\actions\Invoke-GridSkyrimRuntimeCertificationSession.ps1')

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }
function Assert-Throws([scriptblock]$Action, [string]$Pattern, [string]$Message) { try { & $Action; throw "$Message Expected an exception." } catch { if ($_.Exception.Message -notmatch $Pattern) { throw "$Message Unexpected: $($_.Exception.Message)" } } }

$root = Join-Path $env:TEMP ('grid-runtime-cert-' + [guid]::NewGuid().ToString('N'))
$profiles = Join-Path $root 'profiles'; $source = Join-Path $profiles 'Source'; $captures = Join-Path $root 'captures'; $logs = Join-Path $root 'logs'
foreach ($directory in @($source,$captures,$logs)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
try {
    [IO.File]::WriteAllText((Join-Path $source 'modlist.txt'), '+Fixture', (New-Object Text.UTF8Encoding($false)))
    $context = 'A' * 64; $repair = 'B' * 64
    $plan = New-GridSkyrimDisposableProfilePlan -CaseId case-runtime -SourceProfileDirectory $source -ProfilesRoot $profiles -ContextFingerprint $context -RepairFingerprint $repair
    Assert-Equal 'Valid' (Test-GridSkyrimDisposableProfileIsolation -Plan $plan).status 'A bounded immediate-child profile plan must be valid.'
    Assert-True (-not (Test-Path -LiteralPath $plan.targetProfileDirectory)) 'Planning must not create the disposable profile.'
    Assert-True (@($plan.copyOperations | Where-Object { $_.leafName -match 'save' }).Count -eq 0) 'Saves must not enter the profile copy plan.'
    New-Item -ItemType Directory -Path $plan.targetProfileDirectory,(Join-Path $plan.targetProfileDirectory 'saves') -Force | Out-Null
    foreach ($operation in @($plan.copyOperations | Where-Object sourceState -eq 'Present')) { Copy-Item -LiteralPath $operation.sourcePath -Destination $operation.destinationPath }
    $materialization=Test-GridSkyrimDisposableProfileMaterialization -Plan $plan
    Assert-Equal 'Verified' $materialization.status 'Exact copied profile bytes and empty isolated saves must verify.'

    $invocation = New-GridSkyrimMo2ProfileLaunchInvocation -Mo2ExecutablePath (Join-Path $root 'ModOrganizer.exe') -InstanceKind Portable -InstanceDirectory $root -ProfileName $plan.profileName -ExecutableTitle SKSE -Mo2ExecutableIdentity ('C' * 64)
    Assert-Equal '-p' $invocation.arguments[0] 'Portable launch must select the disposable profile explicitly.'
    Assert-Equal 'run' $invocation.arguments[2] 'MO2 run must remain a typed argument.'
    $global = New-GridSkyrimMo2ProfileLaunchInvocation -Mo2ExecutablePath (Join-Path $root 'ModOrganizer.exe') -InstanceKind Global -InstanceDirectory (Join-Path $root 'Global Fixture') -ProfileName $plan.profileName -ExecutableTitle SKSE -Mo2ExecutableIdentity ('C' * 64)
    Assert-Equal '-i' $global.arguments[0] 'Global launch must select the instance before the profile.'
    Assert-Throws { New-GridSkyrimMo2ProfileLaunchInvocation -Mo2ExecutablePath (Join-Path $root 'ModOrganizer.exe') -InstanceKind Portable -InstanceDirectory $root -ProfileName '..' -ExecutableTitle SKSE -Mo2ExecutableIdentity x } 'profile' 'Traversal profile names must be refused.'
    Assert-Throws { New-GridSkyrimMo2ProfileLaunchInvocation -Mo2ExecutablePath (Join-Path $root 'ModOrganizer.exe') -InstanceKind Portable -InstanceDirectory $root -ProfileName $plan.profileName -ExecutableTitle SKSE -Mo2ExecutableIdentity ('C' * 64) -ExecutableSourceIndex 17 } 'source-indexed' 'A source index without the configured executable identity must be refused.'
    Assert-True (Test-GridSkyrimRuntimeSessionConsoleCommand 'coc FixtureExterior') 'A bounded ephemeral cell route must be accepted.'
    Assert-True (Test-GridSkyrimRuntimeSessionConsoleCommand 'prid 00ABCDEF') 'A bounded reference selection must be accepted.'
    Assert-True (-not (Test-GridSkyrimRuntimeSessionConsoleCommand 'disable')) 'Persistent reference mutation must be refused.'
    Assert-True (-not (Test-GridSkyrimRuntimeSessionConsoleCommand 'player.additem 1 1')) 'Inventory mutation must be refused.'

    $recipe = [pscustomobject][ordered]@{
        schemaVersion=1; recipeId='fixture.route'; recipeVersion='1.0.0'; gameId='skyrimspecialedition'
        route=[pscustomobject]@{ status='Available'; routeId='fixture' }
        navigation=@([pscustomobject]@{ sequence=1; action='Wait'; durationMilliseconds=100 },[pscustomobject]@{ sequence=2; action='CaptureScreenshot'; durationMilliseconds=0 })
        oracle=[pscustomobject]@{ kind='RuntimeReferenceProbe'; collectorName='FixtureOracle'; requiredClaims=@('greenhouse.structure','black-plane.absent','adjacent-area.regression') }
        captures=[pscustomobject]@{ maximumScreenshots=6; maximumScreenshotBytes=33554432; maximumAggregateLogBytes=268435456 }
        limits=[pscustomobject]@{ maximumRuntimeSeconds=900; maximumInputSteps=256 }
    }
    Assert-True (Assert-GridSkyrimRuntimeRecipe $recipe) 'Allowlisted bounded recipe must validate.'
    $unmaterialized = Invoke-GridSkyrimRuntimeCertification -CaseId case-runtime -Recipe $recipe -ProfilePlan $plan
    Assert-Equal 'PreconditionsNotMet' $unmaterialized.status 'Absent lineage and independent gates must prevent certification eligibility.'
    Assert-Equal 'PreconditionsNotMet' $unmaterialized.certificationEligibility 'Missing prelaunch evidence must be explicit.'
    Assert-Equal 'NotStarted' $unmaterialized.runtimeValidation 'Runtime evidence must not be evaluated before eligibility.'
    Assert-Equal 'NotEvaluated' $unmaterialized.readyToPlay 'ReadyToPlay must remain tri-state before eligibility.'
    Assert-Equal 8 @($unmaterialized.gates).Count 'Every result must contain all eight independent gates.'
    $hostile = $recipe | ConvertTo-Json -Depth 10 | ConvertFrom-Json; $hostile.navigation[0].action='OpenConsole'
    Assert-Throws { Assert-GridSkyrimRuntimeRecipe $hostile } 'not allowlisted' 'Console-style arbitrary actions must be refused.'

    $before=@([pscustomobject]@{ processId=10; executablePath=(Join-Path $root 'SkyrimSE.exe'); startTimeUtc='2026-01-01T00:00:00Z'; ancestorProcessIds=@(1) })
    $after=@($before[0],[pscustomobject]@{ processId=20; executablePath=(Join-Path $root 'SkyrimSE.exe'); startTimeUtc='2026-01-01T00:01:00Z'; ancestorProcessIds=@(44,1) })
    $owned=Resolve-GridSkyrimOwnedRuntimeProcess -BeforeSnapshot $before -AfterSnapshot $after -ExpectedExecutablePath (Join-Path $root 'SkyrimSE.exe') -ManagerProcessId 44 -LaunchStartedUtc ([datetime]'2026-01-01T00:00:30Z')
    Assert-Equal 'Owned' $owned.state 'One new exact child with the manager ancestor must bind ownership.'
    $ambiguous=Resolve-GridSkyrimOwnedRuntimeProcess -BeforeSnapshot @() -AfterSnapshot @($after[1],([pscustomobject]@{ processId=21; executablePath=(Join-Path $root 'SkyrimSE.exe'); startTimeUtc='2026-01-01T00:01:01Z'; ancestorProcessIds=@(44) })) -ExpectedExecutablePath (Join-Path $root 'SkyrimSE.exe') -ManagerProcessId 44 -LaunchStartedUtc ([datetime]'2026-01-01T00:00:30Z')
    Assert-Equal 'Ambiguous' $ambiguous.state 'Multiple new exact children must fail closed.'

    [IO.File]::WriteAllBytes((Join-Path $captures 'one.png'), [byte[]](1,2,3)); [IO.File]::WriteAllText((Join-Path $logs 'runtime.log'),'fixture')
    $recipeFingerprint=Get-GridSkyrimRuntimeRecipeFingerprint $recipe
    $screens=Get-GridSkyrimRuntimeFileAudit -Kind Screenshot -LiteralPath @((Join-Path $captures 'one.png')) -AllowedRoot $captures -RecipeFingerprint $recipeFingerprint -ContextFingerprint $context -RepairFingerprint $repair
    $logAudit=Get-GridSkyrimRuntimeFileAudit -Kind Log -LiteralPath @((Join-Path $logs 'runtime.log')) -AllowedRoot $logs -RecipeFingerprint $recipeFingerprint -ContextFingerprint $context -RepairFingerprint $repair -MaximumFiles 32 -MaximumFileBytes 268435456
    Assert-Equal 'Complete' $screens.status 'Exact bounded screenshot audit must hash the fixture.'
    Assert-Throws { Get-GridSkyrimRuntimeFileAudit -Kind Screenshot -LiteralPath @((Join-Path $root 'outside.png')) -AllowedRoot $captures -RecipeFingerprint $recipeFingerprint -ContextFingerprint $context -RepairFingerprint $repair } 'escapes' 'Capture paths outside the output root must be refused.'

    $lineage=[pscustomobject]@{ status='Verified'; contextFingerprint=$context; repairFingerprint=$repair; baselineManifestSha256=('1' * 64); diagnosisManifestSha256=('2' * 64); repairSpecificationSha256=('3' * 64); repairReceiptSha256=('4' * 64) }
    $gateNames=@('InstallationIntegrity','CompatibilityIntegrity','AssetIntegrity','RecordIntegrity','PluginIntegrity','ProfileIntegrity','RollbackIntegrity')
    $gates=@($gateNames | ForEach-Object { [pscustomobject]@{ name=$_; status='Pass'; evidenceSha256=('E' * 64); contextFingerprint=$context; repairFingerprint=$repair; issue=$null } })
    $rollback=[pscustomobject]@{ status='Verified'; contextFingerprint=$context; repairFingerprint=$repair; receiptSha256=('5' * 64); journalSha256=('6' * 64); rollbackManifestSha256=('7' * 64); priorTreeSha256=('8' * 64) }

    $needs = Invoke-GridSkyrimRuntimeCertification -CaseId case-runtime -Recipe $recipe -ProfilePlan $plan -LineageBinding $lineage -PrelaunchGates $gates -RollbackReceipt $rollback -ProfileIsolationReceipt $materialization -LaunchInvocation $invocation -OwnershipObservation $owned -ScreenshotAudit $screens -LogAudit $logAudit
    Assert-Equal 'NeedsRuntimeVerification' $needs.status 'No oracle must produce NeedsRuntimeVerification.'
    Assert-True (-not $needs.readyToPlay) 'No oracle must never set ReadyToPlay.'
    $oracle=[pscustomobject]@{ status='Passed'; recipeFingerprint=$recipeFingerprint; contextFingerprint=$context; repairFingerprint=$repair; collectorName='FixtureOracle'; observations=@(
        [pscustomobject]@{ claimId='greenhouse.structure'; status='Verified'; evidenceSha256=('A' * 64) },
        [pscustomobject]@{ claimId='black-plane.absent'; status='Verified'; evidenceSha256=('B' * 64) },
        [pscustomobject]@{ claimId='adjacent-area.regression'; status='Verified'; evidenceSha256=('C' * 64) }
    ) }
    $certified=Invoke-GridSkyrimRuntimeCertification -CaseId case-runtime -Recipe $recipe -ProfilePlan $plan -LineageBinding $lineage -PrelaunchGates $gates -RollbackReceipt $rollback -ProfileIsolationReceipt $materialization -LaunchInvocation $invocation -OwnershipObservation $owned -OracleResult $oracle -ScreenshotAudit $screens -LogAudit $logAudit
    Assert-Equal 'Certified' $certified.status 'Matching deterministic oracle receipt must certify the synthetic case.'
    Assert-True $certified.readyToPlay 'Only the complete bound evidence path may set ReadyToPlay.'
    Assert-Equal 'Eligible' $certified.certificationEligibility 'All seven prelaunch gates must establish eligibility.'
    Assert-Equal 'Pass' $certified.runtimeValidation 'Exact oracle claim coverage plus screenshot and log evidence must pass runtime integrity.'
    Assert-Equal 8 @($certified.gates).Count 'Certified output must retain eight independent gate records.'

    $partialOracle=$oracle | ConvertTo-Json -Depth 10 | ConvertFrom-Json; $partialOracle.observations=@($partialOracle.observations | Select-Object -First 2)
    $partial=Invoke-GridSkyrimRuntimeCertification -CaseId case-runtime -Recipe $recipe -ProfilePlan $plan -LineageBinding $lineage -PrelaunchGates $gates -RollbackReceipt $rollback -ProfileIsolationReceipt $materialization -LaunchInvocation $invocation -OwnershipObservation $owned -OracleResult $partialOracle -ScreenshotAudit $screens -LogAudit $logAudit
    Assert-Equal 'NeedsRuntimeVerification' $partial.status 'Partial deterministic-oracle coverage must remain inconclusive.'
    Assert-True (-not $partial.readyToPlay) 'Partial claim coverage must never set ReadyToPlay.'

    $missingRollback=Invoke-GridSkyrimRuntimeCertification -CaseId case-runtime -Recipe $recipe -ProfilePlan $plan -LineageBinding $lineage -PrelaunchGates $gates -ProfileIsolationReceipt $materialization -LaunchInvocation $invocation -OwnershipObservation $owned -OracleResult $oracle -ScreenshotAudit $screens -LogAudit $logAudit
    Assert-Equal 'PreconditionsNotMet' $missingRollback.status 'A missing rollback receipt must prevent runtime evaluation.'
    Assert-Equal 'NotEvaluated' $missingRollback.readyToPlay 'ReadyToPlay must remain unevaluated when rollback is unavailable.'

    $failedGates=@($gates | ForEach-Object { $_ | Select-Object * }); @($failedGates | Where-Object name -eq 'AssetIntegrity')[0].status='Fail'
    $failed=Invoke-GridSkyrimRuntimeCertification -CaseId case-runtime -Recipe $recipe -ProfilePlan $plan -LineageBinding $lineage -PrelaunchGates $failedGates -RollbackReceipt $rollback
    Assert-Equal 'NotReadyToPlay' $failed.status 'A failed independent prelaunch gate must be a final negative result.'
    Assert-True ($failed.readyToPlay -eq $false) 'A failed gate must produce ReadyToPlay false.'

    $staleScreens=$screens | Select-Object *; $staleScreens.repairFingerprint=('9' * 64)
    $stale=Invoke-GridSkyrimRuntimeCertification -CaseId case-runtime -Recipe $recipe -ProfilePlan $plan -LineageBinding $lineage -PrelaunchGates $gates -RollbackReceipt $rollback -ProfileIsolationReceipt $materialization -LaunchInvocation $invocation -OwnershipObservation $owned -OracleResult $oracle -ScreenshotAudit $staleScreens -LogAudit $logAudit
    Assert-Equal 'NeedsRuntimeVerification' $stale.status 'Stale screenshot evidence must fail closed.'
    Assert-True (-not $stale.readyToPlay) 'Stale screenshot evidence must never set ReadyToPlay.'
    $sessionEvidence=Join-Path $root 'session-evidence'; New-Item -ItemType Directory -Path $sessionEvidence -Force | Out-Null
    $preserved=Move-GridSkyrimDisposableProfileToEvidence -ProfilePlan $plan -SessionRoot $sessionEvidence -Confirm:$false
    Assert-Equal 'Preserved' $preserved.status 'A verified disposable profile must be preserved after shutdown.'
    Assert-True (-not (Test-Path -LiteralPath $plan.targetProfileDirectory)) 'The disposable profile must be removed from MO2 after evidence preservation.'
    Assert-True (Test-Path -LiteralPath (Join-Path $sessionEvidence 'disposable-profile\modlist.txt') -PathType Leaf) 'Exact disposable profile bytes must remain in certification evidence.'
    Write-Host 'PASS: isolated runtime certification planning and fail-closed evidence evaluation.'
}
finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
