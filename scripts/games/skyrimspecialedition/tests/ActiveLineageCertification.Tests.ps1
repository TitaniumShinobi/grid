$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$gameRoot = Split-Path -Parent $PSScriptRoot
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $gameRoot)
. (Join-Path $scriptsRoot 'health\Grid.CaseStore.ps1')
. (Join-Path $gameRoot 'health\actions\Resolve-GridSkyrimCertificationEligibility.ps1')

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." }
}

function Write-TestTes4Plugin {
    param(
        [Parameter(Mandatory)][string]$LiteralPath,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Masters
    )
    $dataStream = New-Object IO.MemoryStream
    $dataWriter = New-Object IO.BinaryWriter($dataStream, [Text.Encoding]::ASCII, $true)
    try {
        foreach ($master in $Masters) {
            $bytes = [Text.Encoding]::GetEncoding(28591).GetBytes($master + [char]0)
            $dataWriter.Write([Text.Encoding]::ASCII.GetBytes('MAST'))
            $dataWriter.Write([uint16]$bytes.Length)
            $dataWriter.Write($bytes)
        }
        $dataWriter.Flush()
        $data = $dataStream.ToArray()
    }
    finally {
        $dataWriter.Dispose()
        $dataStream.Dispose()
    }
    $stream = [IO.File]::Open($LiteralPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $writer = New-Object IO.BinaryWriter($stream, [Text.Encoding]::ASCII, $false)
    try {
        $writer.Write([Text.Encoding]::ASCII.GetBytes('TES4'))
        $writer.Write([uint32]$data.Length)
        $writer.Write((New-Object byte[] 16))
        $writer.Write($data)
    }
    finally { $writer.Dispose() }
}

function New-TestSealedCase {
    param(
        [Parameter(Mandatory)][string]$StoreRoot,
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][string]$Purpose,
        [Parameter(Mandatory)][string]$Status,
        [Parameter(Mandatory)][hashtable]$Artifacts
    )
    $transaction = New-GridCaseStoreTransaction -StoreRoot $StoreRoot -CaseId $CaseId
    Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'case.json' -Value ([pscustomobject][ordered]@{
        schemaVersion = 1
        caseId = $CaseId
        purpose = $Purpose
        status = $Status
    }) | Out-Null
    foreach ($relativePath in @($Artifacts.Keys | Sort-Object)) {
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath $relativePath -Value $Artifacts[$relativePath] | Out-Null
    }
    $now = [DateTimeOffset]::UtcNow.ToString('o')
    $run = [pscustomobject][ordered]@{
        schemaVersion = 1; runId = 'run-fixture'; caseId = $CaseId; state = 'Completed'
        startedAt = $now; completedAt = $now; planFingerprint = ('F' * 64)
        resourcePolicyVersion = 'fixture'; gates = @(); sufficiency = [pscustomobject]@{ status = 'NotEvaluated' }
        checkpoints = @(); primaryFailure = $null; secondaryFailures = @()
    }
    Seal-GridCaseStoreRun -Transaction $transaction -Run $run | Out-Null
    Seal-GridDiagnosticCase -Transaction $transaction -SemanticBaselineFingerprint ('A' * 64) -Runs @($run)
}

function Get-TestCaseFileHash {
    param([Parameter(Mandatory)]$Seal, [Parameter(Mandatory)][string]$RelativePath)
    (Get-FileHash -LiteralPath (Join-Path $Seal.CaseDirectory $RelativePath) -Algorithm SHA256).Hash
}

function New-TestDiagnosisCase {
    param(
        [Parameter(Mandatory)][string]$StoreRoot,
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][string]$BaselineCaseId,
        [Parameter(Mandatory)][string]$BaselineManifestSha256,
        [Parameter(Mandatory)][string]$AssetVirtualPath
    )
    $patch = [pscustomobject][ordered]@{
        schemaVersion = 1
        caseId = $CaseId
        status = 'Inert'
        solutionKind = 'InstallationAssetRepair'
        targetPluginName = $null
        records = @()
        assetActions = @([pscustomobject][ordered]@{
            virtualPath = $AssetVirtualPath
            action = 'RestoreVerifiedAssetPayload'
        })
    }
    New-TestSealedCase -StoreRoot $StoreRoot -CaseId $CaseId -Purpose RootCauseDiagnosis -Status RootCauseResolved -Artifacts @{
        'investigation-plan.json' = [pscustomobject][ordered]@{
            schemaVersion = 2
            purpose = 'Diagnosis'
            parentCaseIds = @($BaselineCaseId)
            evidenceIds = @("baseline-manifest:$BaselineManifestSha256")
        }
        'patch/conflict-patch-specification.v1.json' = $patch
    }
}

function New-TestRepairArtifacts {
    param(
        [Parameter(Mandatory)][string]$DiagnosisManifestFileSha256,
        [Parameter(Mandatory)][string]$PatchSpecificationSha256,
        [Parameter(Mandatory)][string]$AssetVirtualPath,
        [Parameter(Mandatory)][ValidateSet('Pending','Approval','NoRollback','EmptyRollback','Eligible')][string]$State
    )
    $artifactSha = 'B' * 64
    $journalSha = 'C' * 64
    $protected = [pscustomobject][ordered]@{
        schemaVersion = 1
        files = @([pscustomobject][ordered]@{ path = $script:ActiveLineageProtectedFixturePath; state = 'Readable'; sizeBytes = $script:ActiveLineageProtectedFixtureSize; sha256 = $script:ActiveLineageProtectedFixtureSha256 })
    }
    $artifacts = @{
        'repair/diagnosis-binding.v1.json' = [pscustomobject][ordered]@{
            schemaVersion = 1
            diagnosisManifestSha256 = $DiagnosisManifestFileSha256
            diagnosisPatchSpecificationSha256 = $PatchSpecificationSha256
            solutionKind = 'InstallationAssetRepair'
            recordPatch = [pscustomobject][ordered]@{ required = $false; status = 'NotApplicable'; records = @() }
            requiredAsset = $AssetVirtualPath
        }
    }
    if ($State -eq 'Pending') {
        $artifacts['acquisition/user-acquisition-action.v1.json'] = [pscustomobject][ordered]@{
            schemaVersion = 1
            actionId = 'fixture-acquisition'
            state = 'PendingUserAcquisition'
        }
        return $artifacts
    }
    $artifacts['acquisition/artifact-acquisition-record.v1.json'] = [pscustomobject][ordered]@{
        schemaVersion = 1
        state = 'Verified'
        artifact = [pscustomobject][ordered]@{ sha256 = $artifactSha }
    }
    $artifacts['repair/repair-specification.v1.json'] = [pscustomobject][ordered]@{
        schemaVersion = 1
        status = 'AwaitingAuthorization'
        authorization = [pscustomobject][ordered]@{ status = 'Required' }
    }
    if ($State -eq 'Approval') { return $artifacts }
    $artifacts['repair/repair-transaction-receipt.v1.json'] = [pscustomobject][ordered]@{
        schemaVersion = 1
        result = [pscustomobject][ordered]@{ summary = 'InputsRepairedAndVerified' }
        journalSha256 = $journalSha
    }
    $artifacts['repair/static-validation.v1.json'] = [pscustomobject][ordered]@{ schemaVersion = 1; status = 'Pass' }
    $artifacts['repair/virtual-winner.v1.json'] = [pscustomobject][ordered]@{ schemaVersion = 1; status = 'Pass'; virtualPath = $AssetVirtualPath; sha256 = $artifactSha }
    $artifacts['snapshots/protected-before.v1.json'] = $protected
    $artifacts['snapshots/protected-after.v1.json'] = $protected
    if ($State -in @('EmptyRollback','Eligible')) {
        $rollbackManifest = [pscustomobject][ordered]@{
            schemaVersion = 1
            status = 'Available'
            journalSha256 = $journalSha
            treeSha256 = ('E' * 64)
            entries = [object[]]@()
        }
        if ($State -eq 'Eligible') { $rollbackManifest.entries = [object[]]@([pscustomobject][ordered]@{ componentId = 'fixture.component'; originalPath = 'C:\fixture\target'; rollbackPath = 'C:\fixture\rollback'; treeSha256 = ('E' * 64) }) }
        $artifacts['repair/rollback-manifest.v1.json'] = $rollbackManifest
    }
    $artifacts
}

$fixture = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'fixtures\active-lineage-certification.cases.v1.json') | ConvertFrom-Json
$root = Join-Path $env:TEMP ('grid-active-lineage-cert-' + [guid]::NewGuid().ToString('N'))
$store = Join-Path $root 'store'
$assetVirtualPath = 'meshes\Fixture\Architecture\fixture-door.nif'
New-Item -ItemType Directory -Path $root -Force | Out-Null
$script:ActiveLineageProtectedFixturePath = Join-Path $root 'protected-plugins.txt'
[IO.File]::WriteAllText($script:ActiveLineageProtectedFixturePath, 'fixture', (New-Object Text.UTF8Encoding($false)))
$script:ActiveLineageProtectedFixtureSize = (Get-Item -LiteralPath $script:ActiveLineageProtectedFixturePath).Length
$script:ActiveLineageProtectedFixtureSha256 = (Get-FileHash -LiteralPath $script:ActiveLineageProtectedFixturePath -Algorithm SHA256).Hash

try {
    $baseline = New-TestSealedCase -StoreRoot $store -CaseId baseline-fixture -Purpose Baseline -Status BaselineComplete -Artifacts @{}
    $diagnosis = New-TestDiagnosisCase -StoreRoot $store -CaseId diagnosis-fixture -BaselineCaseId baseline-fixture -BaselineManifestSha256 $baseline.Manifest.manifestSha256 -AssetVirtualPath $assetVirtualPath
    $alternateDiagnosis = New-TestDiagnosisCase -StoreRoot $store -CaseId diagnosis-substitute -BaselineCaseId baseline-fixture -BaselineManifestSha256 $baseline.Manifest.manifestSha256 -AssetVirtualPath $assetVirtualPath
    $diagnosisManifestFileHash = Get-TestCaseFileHash -Seal $diagnosis -RelativePath 'case-manifest.v1.json'
    $diagnosisPatchHash = Get-TestCaseFileHash -Seal $diagnosis -RelativePath 'patch\conflict-patch-specification.v1.json'

    $repairPending = New-TestSealedCase -StoreRoot $store -CaseId repair-pending -Purpose EvidenceBoundAssetRepair -Status PausedAtAcquisition -Artifacts (New-TestRepairArtifacts -DiagnosisManifestFileSha256 $diagnosisManifestFileHash -PatchSpecificationSha256 $diagnosisPatchHash -AssetVirtualPath $assetVirtualPath -State Pending)
    $pending = Resolve-GridSkyrimCertificationEligibility -CaseStoreRoot $store -BaselineCaseId baseline-fixture -DiagnosisCaseId diagnosis-fixture -RepairCaseId repair-pending `
        -ExpectedBaselineManifestSha256 $baseline.Manifest.manifestSha256 -ExpectedDiagnosisManifestFileSha256 $diagnosisManifestFileHash -ExpectedPatchSpecificationSha256 $diagnosisPatchHash -ExpectedAssetVirtualPath $assetVirtualPath
    $expectedPending = @($fixture.lineageCases | Where-Object name -eq 'pending-acquisition')[0]
    Assert-Equal $expectedPending.expectedEligibility $pending.eligibility 'A pending exact artifact must block certification without declaring external unavailability.'
    Assert-Equal $expectedPending.expectedRecoveryDisposition $pending.recoveryDisposition 'Pending acquisition must remain resumable.'
    Assert-Equal $expectedPending.expectedBlockingPrimitive $pending.blockingPrimitive 'The exact acquisition primitive must be reported.'
    Assert-Equal $expectedPending.expectedRuntimeValidation $pending.runtimeValidation 'Runtime must not start before static repair eligibility.'
    Assert-Equal $expectedPending.expectedReadyToPlay $pending.readyToPlay 'ReadyToPlay is not evaluated before eligibility.'
    Assert-Equal 'InstallationIntegrity|CompatibilityIntegrity|AssetIntegrity|RecordIntegrity|PluginIntegrity|ProfileIntegrity|RuntimeIntegrity|RollbackIntegrity' ([string]::Join('|', @($pending.gates | ForEach-Object { $_.name }))) 'Eligibility must expose all eight independent certification gates in canonical order.'
    Assert-True (@($pending.gates | Where-Object status -ne 'NotStarted').Count -eq 0) 'Every certification gate must remain NotStarted while acquisition is pending.'
    Assert-True (@($pending.preconditions | Where-Object { $_.name -eq 'ExactLineage' -and $_.status -eq 'Pass' }).Count -eq 1) 'Pending acquisition must not erase proof that the exact lineage already passed.'
    $pendingReplay = Resolve-GridSkyrimCertificationEligibility -CaseStoreRoot $store -BaselineCaseId baseline-fixture -DiagnosisCaseId diagnosis-fixture -RepairCaseId repair-pending `
        -ExpectedBaselineManifestSha256 $baseline.Manifest.manifestSha256 -ExpectedDiagnosisManifestFileSha256 $diagnosisManifestFileHash -ExpectedPatchSpecificationSha256 $diagnosisPatchHash -ExpectedAssetVirtualPath $assetVirtualPath
    Assert-Equal $pending.semanticFingerprint $pendingReplay.semanticFingerprint 'The same sealed lineage must produce the same eligibility fingerprint.'

    $successorArtifacts = New-TestRepairArtifacts -DiagnosisManifestFileSha256 $diagnosisManifestFileHash -PatchSpecificationSha256 $diagnosisPatchHash -AssetVirtualPath $assetVirtualPath -State Eligible
    $successorArtifacts['repair/parent-repair-binding.v1.json'] = [pscustomobject][ordered]@{
        schemaVersion=1; parentRepairCaseId='repair-pending'; parentRepairManifestSha256=[string]$repairPending.Manifest.manifestSha256
    }
    $repairSuccessor = New-TestSealedCase -StoreRoot $store -CaseId repair-pending-successor -Purpose EvidenceBoundAssetRepair -Status Completed -Artifacts $successorArtifacts
    $successorEligible = Resolve-GridSkyrimCertificationEligibility -CaseStoreRoot $store -BaselineCaseId baseline-fixture -DiagnosisCaseId diagnosis-fixture -RepairCaseId repair-pending `
        -SuccessorRepairCaseId repair-pending-successor -ExpectedSuccessorRepairManifestSha256 $repairSuccessor.Manifest.manifestSha256 `
        -ExpectedBaselineManifestSha256 $baseline.Manifest.manifestSha256 -ExpectedDiagnosisManifestFileSha256 $diagnosisManifestFileHash -ExpectedPatchSpecificationSha256 $diagnosisPatchHash -ExpectedAssetVirtualPath $assetVirtualPath
    Assert-Equal 'Eligible' $successorEligible.eligibility 'A sealed, exact parent-bound repair successor must supply the post-repair receipts.'
    Assert-Equal 'Verified' $successorEligible.lineage.repairSuccessor.sealStatus 'The successor seal must be explicit in lineage evidence.'

    $wrongBindingArtifacts = New-TestRepairArtifacts -DiagnosisManifestFileSha256 $diagnosisManifestFileHash -PatchSpecificationSha256 $diagnosisPatchHash -AssetVirtualPath $assetVirtualPath -State Eligible
    $wrongBindingArtifacts['repair/parent-repair-binding.v1.json'] = [pscustomobject][ordered]@{ schemaVersion=1; parentRepairCaseId='repair-other'; parentRepairManifestSha256=[string]$repairPending.Manifest.manifestSha256 }
    $wrongBinding = New-TestSealedCase -StoreRoot $store -CaseId repair-wrong-parent -Purpose EvidenceBoundAssetRepair -Status Completed -Artifacts $wrongBindingArtifacts
    $wrongParent = Resolve-GridSkyrimCertificationEligibility -CaseStoreRoot $store -BaselineCaseId baseline-fixture -DiagnosisCaseId diagnosis-fixture -RepairCaseId repair-pending `
        -SuccessorRepairCaseId repair-wrong-parent -ExpectedSuccessorRepairManifestSha256 $wrongBinding.Manifest.manifestSha256 `
        -ExpectedBaselineManifestSha256 $baseline.Manifest.manifestSha256 -ExpectedDiagnosisManifestFileSha256 $diagnosisManifestFileHash -ExpectedPatchSpecificationSha256 $diagnosisPatchHash -ExpectedAssetVirtualPath $assetVirtualPath
    Assert-Equal 'InvalidLineage' $wrongParent.eligibility 'A successor bound to another repair parent must be rejected.'

    $missingEvidenceArtifacts = @{ 'repair/parent-repair-binding.v1.json' = [pscustomobject][ordered]@{ schemaVersion=1; parentRepairCaseId='repair-pending'; parentRepairManifestSha256=[string]$repairPending.Manifest.manifestSha256 } }
    $missingEvidence = New-TestSealedCase -StoreRoot $store -CaseId repair-missing-successor-evidence -Purpose EvidenceBoundAssetRepair -Status Completed -Artifacts $missingEvidenceArtifacts
    $noFallback = Resolve-GridSkyrimCertificationEligibility -CaseStoreRoot $store -BaselineCaseId baseline-fixture -DiagnosisCaseId diagnosis-fixture -RepairCaseId repair-pending `
        -SuccessorRepairCaseId repair-missing-successor-evidence -ExpectedSuccessorRepairManifestSha256 $missingEvidence.Manifest.manifestSha256 `
        -ExpectedBaselineManifestSha256 $baseline.Manifest.manifestSha256 -ExpectedDiagnosisManifestFileSha256 $diagnosisManifestFileHash -ExpectedPatchSpecificationSha256 $diagnosisPatchHash -ExpectedAssetVirtualPath $assetVirtualPath
    Assert-True ($noFallback.eligibility -ne 'Eligible') 'A successor missing receipts must never fall back to artifacts in its parent.'
    Assert-Equal 'ArtifactAcquisition' $noFallback.blockingPrimitive 'Missing successor acquisition evidence must remain the exact blocking primitive.'
    try {
        Resolve-GridSkyrimCertificationEligibility -CaseStoreRoot $store -BaselineCaseId baseline-fixture -DiagnosisCaseId diagnosis-fixture -RepairCaseId repair-pending -SuccessorRepairCaseId repair-pending-successor `
            -ExpectedBaselineManifestSha256 $baseline.Manifest.manifestSha256 -ExpectedDiagnosisManifestFileSha256 $diagnosisManifestFileHash -ExpectedPatchSpecificationSha256 $diagnosisPatchHash -ExpectedAssetVirtualPath $assetVirtualPath | Out-Null
        throw 'A partial successor parameter pair should have failed.'
    } catch { Assert-True ($_.Exception.Message -match 'must be supplied together') 'A partial successor parameter pair must fail explicitly.' }

    $preflightEvidence = Join-Path $root 'must-not-create\evidence'
    $preflightRollback = Join-Path $root 'must-not-create\rollback'
    $preflightProfiles = Join-Path $root 'must-not-create\profiles'
    $preflight = New-GridSkyrimCertificationPreflight -Eligibility $pending -Mo2ExecutablePath (Join-Path $root 'external\ModOrganizer.exe') -SkseExecutableTitle SKSE `
        -ExpectedGameExecutablePath (Join-Path $root 'external\SkyrimSE.exe') -DisposableProfilesRoot $preflightProfiles -EvidenceCaseDirectory $preflightEvidence -RollbackDirectory $preflightRollback
    Assert-Equal 'Blocked' $preflight.status 'Preflight must refuse process launch and disposable-state creation before eligibility.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'must-not-create'))) 'Eligibility and preflight evaluation must perform no external or case writes.'
    Assert-True (@($preflight.gates | Where-Object status -ne 'NotStarted').Count -eq 0) 'Blocked preflight must not infer one gate from another.'
    Write-Host 'PASS: pending acquisition remains resumable with Runtime NotStarted, ReadyToPlay NotEvaluated, and no writes or launches.'

    $repairApproval = New-TestSealedCase -StoreRoot $store -CaseId repair-approval -Purpose RepairPlanning -Status Completed -Artifacts (New-TestRepairArtifacts -DiagnosisManifestFileSha256 $diagnosisManifestFileHash -PatchSpecificationSha256 $diagnosisPatchHash -AssetVirtualPath $assetVirtualPath -State Approval)
    $approval = Resolve-GridSkyrimCertificationEligibility -CaseStoreRoot $store -BaselineCaseId baseline-fixture -DiagnosisCaseId diagnosis-fixture -RepairCaseId repair-approval `
        -ExpectedBaselineManifestSha256 $baseline.Manifest.manifestSha256 -ExpectedDiagnosisManifestFileSha256 $diagnosisManifestFileHash -ExpectedPatchSpecificationSha256 $diagnosisPatchHash -ExpectedAssetVirtualPath $assetVirtualPath
    Assert-Equal 'ApprovalRequired' $approval.eligibility 'A verified artifact and inert specification cannot substitute for exact mutation authorization and a receipt.'
    Assert-Equal 'AuthorizationRequired' $approval.recoveryDisposition 'Missing transaction authority must remain a distinct recovery disposition.'
    Assert-Equal 'NotStarted' $approval.runtimeValidation 'Missing authorization must prevent runtime launch.'

    $repairNoRollback = New-TestSealedCase -StoreRoot $store -CaseId repair-no-rollback -Purpose EvidenceBoundAssetRepair -Status Completed -Artifacts (New-TestRepairArtifacts -DiagnosisManifestFileSha256 $diagnosisManifestFileHash -PatchSpecificationSha256 $diagnosisPatchHash -AssetVirtualPath $assetVirtualPath -State NoRollback)
    $noRollback = Resolve-GridSkyrimCertificationEligibility -CaseStoreRoot $store -BaselineCaseId baseline-fixture -DiagnosisCaseId diagnosis-fixture -RepairCaseId repair-no-rollback `
        -ExpectedBaselineManifestSha256 $baseline.Manifest.manifestSha256 -ExpectedDiagnosisManifestFileSha256 $diagnosisManifestFileHash -ExpectedPatchSpecificationSha256 $diagnosisPatchHash -ExpectedAssetVirtualPath $assetVirtualPath
    Assert-True ($noRollback.eligibility -ne 'Eligible') 'A repair receipt without an exact rollback package must not become certification-eligible.'
    Assert-True (@($noRollback.gates | Where-Object { $_.name -eq 'RollbackIntegrity' -and $_.status -eq 'Pass' }).Count -eq 0) 'RollbackIntegrity cannot pass without its independent rollback evidence.'

    $repairEmptyRollback = New-TestSealedCase -StoreRoot $store -CaseId repair-empty-rollback -Purpose EvidenceBoundAssetRepair -Status Completed -Artifacts (New-TestRepairArtifacts -DiagnosisManifestFileSha256 $diagnosisManifestFileHash -PatchSpecificationSha256 $diagnosisPatchHash -AssetVirtualPath $assetVirtualPath -State EmptyRollback)
    $emptyRollback = Resolve-GridSkyrimCertificationEligibility -CaseStoreRoot $store -BaselineCaseId baseline-fixture -DiagnosisCaseId diagnosis-fixture -RepairCaseId repair-empty-rollback `
        -ExpectedBaselineManifestSha256 $baseline.Manifest.manifestSha256 -ExpectedDiagnosisManifestFileSha256 $diagnosisManifestFileHash -ExpectedPatchSpecificationSha256 $diagnosisPatchHash -ExpectedAssetVirtualPath $assetVirtualPath
    Assert-True ($emptyRollback.eligibility -ne 'Eligible') "A rollback manifest with no recoverable entries must not become certification-eligible. Actual: $($emptyRollback.eligibility), blocking: $($emptyRollback.blockingPrimitive)."

    $repairEligible = New-TestSealedCase -StoreRoot $store -CaseId repair-eligible -Purpose EvidenceBoundAssetRepair -Status Completed -Artifacts (New-TestRepairArtifacts -DiagnosisManifestFileSha256 $diagnosisManifestFileHash -PatchSpecificationSha256 $diagnosisPatchHash -AssetVirtualPath $assetVirtualPath -State Eligible)
    $eligible = Resolve-GridSkyrimCertificationEligibility -CaseStoreRoot $store -BaselineCaseId baseline-fixture -DiagnosisCaseId diagnosis-fixture -RepairCaseId repair-eligible `
        -ExpectedBaselineManifestSha256 $baseline.Manifest.manifestSha256 -ExpectedDiagnosisManifestFileSha256 $diagnosisManifestFileHash -ExpectedPatchSpecificationSha256 $diagnosisPatchHash -ExpectedAssetVirtualPath $assetVirtualPath
    Assert-Equal 'Eligible' $eligible.eligibility 'Only the complete sealed repair, static, winner, protected-state, and rollback evidence set may become eligible.'
    Assert-Equal 'NotStarted' $eligible.runtimeValidation 'Eligibility alone must not claim runtime validation.'
    Assert-Equal 'NotEvaluated' $eligible.readyToPlay 'Eligibility alone must not claim ReadyToPlay.'

    $alternateManifestFileHash = Get-TestCaseFileHash -Seal $alternateDiagnosis -RelativePath 'case-manifest.v1.json'
    $alternatePatchHash = Get-TestCaseFileHash -Seal $alternateDiagnosis -RelativePath 'patch\conflict-patch-specification.v1.json'
    $substituted = Resolve-GridSkyrimCertificationEligibility -CaseStoreRoot $store -BaselineCaseId baseline-fixture -DiagnosisCaseId diagnosis-substitute -RepairCaseId repair-eligible `
        -ExpectedBaselineManifestSha256 $baseline.Manifest.manifestSha256 -ExpectedDiagnosisManifestFileSha256 $alternateManifestFileHash -ExpectedPatchSpecificationSha256 $alternatePatchHash -ExpectedAssetVirtualPath $assetVirtualPath
    Assert-Equal 'InvalidLineage' $substituted.eligibility 'A different sealed diagnosis must not be substituted into a repair bound to the active diagnosis.'
    Assert-Equal 'NotStarted' $substituted.runtimeValidation 'Lineage substitution must fail before runtime.'
    Write-Host 'PASS: sealed lineage, authorization, static repair, and rollback preconditions fail closed independently.'

    $pluginRoot = Join-Path $root 'plugins'
    New-Item -ItemType Directory -Path $pluginRoot -Force | Out-Null
    Write-TestTes4Plugin -LiteralPath (Join-Path $pluginRoot 'Skyrim.esm') -Masters @()
    Write-TestTes4Plugin -LiteralPath (Join-Path $pluginRoot 'ccGridFixture.esl') -Masters @('Skyrim.esm')
    Write-TestTes4Plugin -LiteralPath (Join-Path $pluginRoot 'FixtureDependency.esm') -Masters @('Skyrim.esm')
    Write-TestTes4Plugin -LiteralPath (Join-Path $pluginRoot 'FixtureFeature.esp') -Masters @('Skyrim.esm','ccGridFixture.esl')
    $pluginsFile = Join-Path $root 'plugins.txt'
    [IO.File]::WriteAllText($pluginsFile, "*FixtureFeature.esp`r`n", (New-Object Text.UTF8Encoding($false)))
    $pluginPass = Test-GridSkyrimEnabledPluginIntegrity -PluginsFilePath $pluginsFile -PluginRoots @($pluginRoot) -ImplicitMasterNames @('Skyrim.esm','ccGridFixture.esl') -MaximumEnabledPlugins 2048
    Assert-Equal 'Pass' $pluginPass.status 'A physically present implicit Creation Club master need not have an explicit enabled profile row.'
    Assert-True (@($pluginPass.implicitMasters | Where-Object { [string]$_ -ieq 'ccGridFixture.esl' }).Count -eq 1) 'The implicit master must be preserved as explicit evidence.'
    $pluginReplay = Test-GridSkyrimEnabledPluginIntegrity -PluginsFilePath $pluginsFile -PluginRoots @($pluginRoot) -ImplicitMasterNames @('ccGridFixture.esl','Skyrim.esm') -MaximumEnabledPlugins 2048
    Assert-Equal $pluginPass.semanticFingerprint $pluginReplay.semanticFingerprint 'Implicit-master input ordering must not alter the semantic plugin-integrity fingerprint.'

    Remove-Item -LiteralPath (Join-Path $pluginRoot 'ccGridFixture.esl') -Force
    $missingImplicit = Test-GridSkyrimEnabledPluginIntegrity -PluginsFilePath $pluginsFile -PluginRoots @($pluginRoot) -ImplicitMasterNames @('Skyrim.esm','ccGridFixture.esl')
    Assert-Equal 'Fail' $missingImplicit.status 'Implicit activation cannot excuse a physically absent required master.'
    Write-TestTes4Plugin -LiteralPath (Join-Path $pluginRoot 'ccGridFixture.esl') -Masters @('Skyrim.esm')

    Write-TestTes4Plugin -LiteralPath (Join-Path $pluginRoot 'FixtureFeature.esp') -Masters @('Skyrim.esm','FixtureDependency.esm')
    $inactiveMaster = Test-GridSkyrimEnabledPluginIntegrity -PluginsFilePath $pluginsFile -PluginRoots @($pluginRoot) -ImplicitMasterNames @('Skyrim.esm')
    Assert-Equal 'Fail' $inactiveMaster.status 'A present non-implicit master must still be enabled explicitly.'

    [IO.File]::WriteAllBytes((Join-Path $pluginRoot 'FixtureFeature.esp'), [byte[]](0,0,0,0))
    $truncated = Test-GridSkyrimEnabledPluginIntegrity -PluginsFilePath $pluginsFile -PluginRoots @($pluginRoot) -ImplicitMasterNames @('Skyrim.esm')
    Assert-Equal 'Fail' $truncated.status 'A truncated enabled plugin must fail PluginIntegrity without xEdit.'
    [IO.File]::WriteAllBytes((Join-Path $pluginRoot 'FixtureFeature.esp'), [byte[]]@())
    $zero = Test-GridSkyrimEnabledPluginIntegrity -PluginsFilePath $pluginsFile -PluginRoots @($pluginRoot) -ImplicitMasterNames @('Skyrim.esm')
    Assert-Equal 'Fail' $zero.status 'A zero-length enabled plugin must fail PluginIntegrity.'
    Write-Host 'PASS: enabled-plugin integrity handles implicit masters and rejects missing, inactive, truncated, and zero-length inputs.'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
