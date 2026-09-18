<#
.SYNOPSIS
Validates the sealed case lineage and bounded prerequisites for Skyrim runtime certification.
.DESCRIPTION
Provides three read-only operations: exact baseline-to-diagnosis-to-repair lineage resolution,
bounded enabled-plugin/header integrity validation, and materialization of a machine-readable
preflight description. It never creates a profile, launches a process, or changes MO2 state.
#>
Set-StrictMode -Version Latest

$script:GridSkyrimCertificationDigestPattern = '^[A-F0-9]{64}$'
$script:GridSkyrimCertificationGateNames = @(
    'InstallationIntegrity', 'CompatibilityIntegrity', 'AssetIntegrity', 'RecordIntegrity',
    'PluginIntegrity', 'ProfileIntegrity', 'RuntimeIntegrity', 'RollbackIntegrity'
)

function Get-GridSkyrimCertificationProperty {
    param([object]$InputObject, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $InputObject -or $null -eq $InputObject.PSObject.Properties[$Name]) { return $null }
    $InputObject.$Name
}

function Get-GridSkyrimCertificationSha256 {
    param([Parameter(Mandatory)][string]$LiteralPath)
    (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256 -ErrorAction Stop).Hash.ToUpperInvariant()
}

function Get-GridSkyrimCertificationFingerprint {
    param([Parameter(Mandatory)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Parts)
    $bytes = [Text.Encoding]::UTF8.GetBytes(($Parts -join "`n"))
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace('-', '') }
    finally { $algorithm.Dispose() }
}

function Assert-GridSkyrimCertificationDigest {
    param([Parameter(Mandatory)][string]$Value, [Parameter(Mandatory)][string]$Name)
    if ($Value.ToUpperInvariant() -cnotmatch $script:GridSkyrimCertificationDigestPattern) {
        throw "CertificationInvalid: $Name must be a SHA-256 digest."
    }
}

function Assert-GridSkyrimCertificationCaseId {
    param([Parameter(Mandatory)][string]$Value, [Parameter(Mandatory)][string]$Name)
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -in @('.', '..') -or
        $Value.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        $Value.Contains([IO.Path]::DirectorySeparatorChar) -or $Value.Contains([IO.Path]::AltDirectorySeparatorChar)) {
        throw "CertificationInvalid: $Name must be one safe case-store segment."
    }
}

function Read-GridSkyrimCertificationJson {
    param([Parameter(Mandatory)][string]$LiteralPath)
    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) { return $null }
    try { Get-Content -Raw -LiteralPath $LiteralPath -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "CertificationEvidenceUnreadable: '$LiteralPath': $($_.Exception.Message)" }
}

function Test-GridSkyrimProtectedSnapshotEquality {
    param([object]$Before, [object]$After)
    if ($null -eq $Before -or $null -eq $After) { return $false }
    $beforeFiles = @(Get-GridSkyrimCertificationProperty $Before 'files')
    $afterFiles = @(Get-GridSkyrimCertificationProperty $After 'files')
    if ($beforeFiles.Count -ne $afterFiles.Count) { return $false }
    $beforeMap = @{}
    foreach ($file in $beforeFiles) {
        $path = [string](Get-GridSkyrimCertificationProperty $file 'path')
        if ([string]::IsNullOrWhiteSpace($path) -or $beforeMap.ContainsKey($path)) { return $false }
        $beforeMap[$path] = $file
    }
    foreach ($file in $afterFiles) {
        $path = [string](Get-GridSkyrimCertificationProperty $file 'path')
        if (-not $beforeMap.ContainsKey($path)) { return $false }
        $old = $beforeMap[$path]
        foreach ($name in @('state', 'sizeBytes', 'sha256')) {
            if ([string](Get-GridSkyrimCertificationProperty $old $name) -cne [string](Get-GridSkyrimCertificationProperty $file $name)) { return $false }
        }
    }
    $true
}

function Test-GridSkyrimCurrentProtectedSnapshot {
    param([Parameter(Mandatory)]$ExpectedSnapshot)
    $observations = New-Object Collections.Generic.List[object]
    $issues = New-Object Collections.Generic.List[string]
    foreach ($expected in @(Get-GridSkyrimCertificationProperty $ExpectedSnapshot 'files')) {
        $path = [string](Get-GridSkyrimCertificationProperty $expected 'path')
        $expectedState = [string](Get-GridSkyrimCertificationProperty $expected 'state')
        $observation = [ordered]@{ path=$path; state='Missing'; sizeBytes=$null; sha256=$null; changedDuringRead=$false }
        try {
            if ([string]::IsNullOrWhiteSpace($path) -or -not [IO.Path]::IsPathRooted($path)) { throw 'Protected path is not absolute.' }
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                $before = Get-Item -LiteralPath $path -Force -ErrorAction Stop
                if (($before.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Protected file is a reparse point.' }
                $hash = Get-GridSkyrimCertificationSha256 $before.FullName
                $after = Get-Item -LiteralPath $path -Force -ErrorAction Stop
                $changed = [long]$before.Length -ne [long]$after.Length -or $before.LastWriteTimeUtc.Ticks -ne $after.LastWriteTimeUtc.Ticks
                $observation.state='Readable'; $observation.sizeBytes=[long]$after.Length; $observation.sha256=$hash; $observation.changedDuringRead=$changed
            }
        } catch {
            $observation.state='Unreadable'; $observation.error=$_.Exception.Message
        }
        if ($observation.state -cne $expectedState -or
            [string]$observation.sizeBytes -cne [string](Get-GridSkyrimCertificationProperty $expected 'sizeBytes') -or
            [string]$observation.sha256 -cne [string](Get-GridSkyrimCertificationProperty $expected 'sha256') -or
            $observation.changedDuringRead) {
            $issues.Add("Protected state differs at '$path'.")
        }
        $observations.Add([pscustomobject]$observation)
    }
    $fingerprint = Get-GridSkyrimCertificationFingerprint @($observations | Sort-Object path | ForEach-Object { "$($_.path)|$($_.state)|$($_.sizeBytes)|$($_.sha256)|$($_.changedDuringRead)" })
    [pscustomobject][ordered]@{ status=if($issues.Count -eq 0){'Pass'}else{'Fail'}; observations=$observations.ToArray(); issues=$issues.ToArray(); semanticFingerprint=$fingerprint }
}

function Resolve-GridSkyrimCertificationEligibility {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseStoreRoot,
        [Parameter(Mandatory)][string]$BaselineCaseId,
        [Parameter(Mandatory)][string]$DiagnosisCaseId,
        [Parameter(Mandatory)][string]$RepairCaseId,
        [Parameter(Mandatory)][string]$ExpectedBaselineManifestSha256,
        [Parameter(Mandatory)][string]$ExpectedDiagnosisManifestFileSha256,
        [Parameter(Mandatory)][string]$ExpectedPatchSpecificationSha256,
        [Parameter(Mandatory)][string]$ExpectedAssetVirtualPath,
        [string]$SuccessorRepairCaseId,
        [string]$ExpectedSuccessorRepairManifestSha256
    )
    if (-not (Get-Command Test-GridDiagnosticCaseSeal -ErrorAction SilentlyContinue)) {
        throw 'CertificationInfrastructureUnavailable: import the shared Grid health module before resolving eligibility.'
    }
    foreach ($pair in @(
        @($BaselineCaseId, 'BaselineCaseId'), @($DiagnosisCaseId, 'DiagnosisCaseId'), @($RepairCaseId, 'RepairCaseId')
    )) { Assert-GridSkyrimCertificationCaseId -Value $pair[0] -Name $pair[1] }
    foreach ($pair in @(
        @($ExpectedBaselineManifestSha256, 'ExpectedBaselineManifestSha256'),
        @($ExpectedDiagnosisManifestFileSha256, 'ExpectedDiagnosisManifestFileSha256'),
        @($ExpectedPatchSpecificationSha256, 'ExpectedPatchSpecificationSha256')
    )) { Assert-GridSkyrimCertificationDigest -Value $pair[0] -Name $pair[1] }
    $hasSuccessorCase = -not [string]::IsNullOrWhiteSpace($SuccessorRepairCaseId)
    $hasSuccessorDigest = -not [string]::IsNullOrWhiteSpace($ExpectedSuccessorRepairManifestSha256)
    if ($hasSuccessorCase -xor $hasSuccessorDigest) {
        throw 'CertificationInvalid: SuccessorRepairCaseId and ExpectedSuccessorRepairManifestSha256 must be supplied together.'
    }
    if ($hasSuccessorCase) {
        Assert-GridSkyrimCertificationCaseId -Value $SuccessorRepairCaseId -Name 'SuccessorRepairCaseId'
        Assert-GridSkyrimCertificationDigest -Value $ExpectedSuccessorRepairManifestSha256 -Name 'ExpectedSuccessorRepairManifestSha256'
        if ($SuccessorRepairCaseId -ceq $RepairCaseId) {
            throw 'CertificationInvalid: the repair successor must be distinct from its sealed parent.'
        }
    }
    if ([string]::IsNullOrWhiteSpace($ExpectedAssetVirtualPath) -or [IO.Path]::IsPathRooted($ExpectedAssetVirtualPath) -or
        $ExpectedAssetVirtualPath -match '(^|[\\/])\.\.([\\/]|$)') {
        throw 'CertificationInvalid: ExpectedAssetVirtualPath must be one bounded virtual relative path.'
    }

    $store = [IO.Path]::GetFullPath($CaseStoreRoot)
    $casesRoot = Join-Path $store 'cases\v1'
    $directories = [ordered]@{
        baseline = Join-Path $casesRoot $BaselineCaseId
        diagnosis = Join-Path $casesRoot $DiagnosisCaseId
        repair = Join-Path $casesRoot $RepairCaseId
    }
    $issues = New-Object Collections.Generic.List[string]
    $preconditions = New-Object Collections.Generic.List[object]
    $lineage = [ordered]@{}
    $invalidLineage = $false

    foreach ($role in @('baseline', 'diagnosis', 'repair')) {
        $directory = [string]$directories[$role]
        $caseId = if ($role -eq 'baseline') { $BaselineCaseId } elseif ($role -eq 'diagnosis') { $DiagnosisCaseId } else { $RepairCaseId }
        $seal = Test-GridDiagnosticCaseSeal -StoreRoot $store -CaseDirectory $directory
        $manifestPath = Join-Path $directory 'case-manifest.v1.json'
        $fileHash = if (Test-Path -LiteralPath $manifestPath -PathType Leaf) { Get-GridSkyrimCertificationSha256 $manifestPath } else { $null }
        $manifestHash = if ($seal.Manifest) { [string](Get-GridSkyrimCertificationProperty $seal.Manifest 'manifestSha256') } else { $null }
        $lineage[$role] = [pscustomobject][ordered]@{
            caseId = $caseId; caseDirectory = $directory
            sealStatus = if ($seal.IsValid) { 'Verified' } else { 'Invalid' }
            manifestFileSha256 = $fileHash; manifestSha256 = $manifestHash
        }
        $detail = if ($seal.IsValid) { 'The sealed case manifest and every declared artifact verified.' } else { @($seal.Errors) -join '; ' }
        $preconditions.Add([pscustomobject][ordered]@{ name = ($role.Substring(0,1).ToUpperInvariant() + $role.Substring(1) + 'Seal'); status = if ($seal.IsValid) {'Pass'} else {'Fail'}; detail = $detail; evidenceSha256 = $fileHash })
        if (-not $seal.IsValid) { $invalidLineage = $true; $issues.Add("$role case seal is invalid: $detail") }
    }

    $repairEvidenceDirectory = [string]$directories.repair
    if ($hasSuccessorCase) {
        $successorDirectory = Join-Path $casesRoot $SuccessorRepairCaseId
        $successorSeal = Test-GridDiagnosticCaseSeal -StoreRoot $store -CaseDirectory $successorDirectory
        $successorManifestPath = Join-Path $successorDirectory 'case-manifest.v1.json'
        $successorFileHash = if (Test-Path -LiteralPath $successorManifestPath -PathType Leaf) { Get-GridSkyrimCertificationSha256 $successorManifestPath } else { $null }
        $successorManifestHash = if ($successorSeal.Manifest) { [string](Get-GridSkyrimCertificationProperty $successorSeal.Manifest 'manifestSha256') } else { $null }
        $lineage.repairSuccessor = [pscustomobject][ordered]@{
            caseId = $SuccessorRepairCaseId; caseDirectory = $successorDirectory
            sealStatus = if ($successorSeal.IsValid) { 'Verified' } else { 'Invalid' }
            manifestFileSha256 = $successorFileHash; manifestSha256 = $successorManifestHash
        }
        $successorDetail = if ($successorSeal.IsValid) { 'The sealed repair successor and every declared artifact verified.' } else { @($successorSeal.Errors) -join '; ' }
        $successorValid = $successorSeal.IsValid -and $successorManifestHash -ceq $ExpectedSuccessorRepairManifestSha256.ToUpperInvariant()
        $preconditions.Add([pscustomobject][ordered]@{
            name='RepairSuccessorSeal'; status=if ($successorValid) {'Pass'} else {'Fail'}
            detail=if ($successorValid) {$successorDetail} elseif (-not $successorSeal.IsValid) {$successorDetail} else {'The repair successor manifest digest does not match the requested descendant.'}
            evidenceSha256=$successorFileHash
        })
        if (-not $successorValid) {
            $invalidLineage = $true
            $issues.Add('The explicitly selected repair successor seal or manifest identity is invalid.')
        }

        $parentBinding = Read-GridSkyrimCertificationJson (Join-Path $successorDirectory 'repair\parent-repair-binding.v1.json')
        $parentBindingValid = $null -ne $parentBinding -and
            [int](Get-GridSkyrimCertificationProperty $parentBinding 'schemaVersion') -eq 1 -and
            [string](Get-GridSkyrimCertificationProperty $parentBinding 'parentRepairCaseId') -ceq $RepairCaseId -and
            [string](Get-GridSkyrimCertificationProperty $parentBinding 'parentRepairManifestSha256') -ceq [string]$lineage.repair.manifestSha256
        $preconditions.Add([pscustomobject][ordered]@{
            name='RepairSuccessorParentBinding'; status=if ($parentBindingValid) {'Pass'} else {'Fail'}
            detail=if ($parentBindingValid) {'The successor binds the exact sealed repair parent and internal manifest digest.'} else {'The successor does not bind the exact sealed repair parent and internal manifest digest.'}
            evidenceSha256=$successorManifestHash
        })
        if (-not $parentBindingValid) {
            $invalidLineage = $true
            $issues.Add('The explicitly selected repair successor has an invalid parent-repair binding.')
        }
        $repairEvidenceDirectory = $successorDirectory
    }

    if ([string]$lineage.baseline.manifestSha256 -cne $ExpectedBaselineManifestSha256.ToUpperInvariant()) {
        $invalidLineage = $true; $issues.Add('The baseline internal manifest fingerprint does not match the active lineage.')
    }
    if ([string]$lineage.diagnosis.manifestFileSha256 -cne $ExpectedDiagnosisManifestFileSha256.ToUpperInvariant()) {
        $invalidLineage = $true; $issues.Add('The diagnosis manifest file digest does not match the active lineage.')
    }

    $planPath = Join-Path $directories.diagnosis 'investigation-plan.json'
    $patchPath = Join-Path $directories.diagnosis 'patch\conflict-patch-specification.v1.json'
    $bindingPath = Join-Path $directories.repair 'repair\diagnosis-binding.v1.json'
    $plan = Read-GridSkyrimCertificationJson $planPath
    $patch = Read-GridSkyrimCertificationJson $patchPath
    $binding = Read-GridSkyrimCertificationJson $bindingPath
    $patchHash = if (Test-Path -LiteralPath $patchPath -PathType Leaf) { Get-GridSkyrimCertificationSha256 $patchPath } else { $null }
    $planParents = if ($plan) { @((Get-GridSkyrimCertificationProperty $plan 'parentCaseIds')) } else { @() }
    $planEvidence = if ($plan) { @((Get-GridSkyrimCertificationProperty $plan 'evidenceIds')) } else { @() }
    $expectedBaselineEvidence = 'baseline-manifest:' + $ExpectedBaselineManifestSha256.ToUpperInvariant()
    if ($null -eq $plan -or @($planParents | Where-Object { [string]$_ -ceq $BaselineCaseId }).Count -ne 1 -or
        @($planEvidence | Where-Object { [string]$_ -ceq $expectedBaselineEvidence }).Count -ne 1) {
        $invalidLineage = $true; $issues.Add('The diagnosis InvestigationPlan does not bind the exact baseline case and manifest.')
    }
    if ($null -eq $patch -or $patchHash -cne $ExpectedPatchSpecificationSha256.ToUpperInvariant() -or
        [string](Get-GridSkyrimCertificationProperty $patch 'solutionKind') -cne 'InstallationAssetRepair' -or
        $null -ne (Get-GridSkyrimCertificationProperty $patch 'targetPluginName') -or @((Get-GridSkyrimCertificationProperty $patch 'records')).Count -ne 0) {
        $invalidLineage = $true; $issues.Add('The diagnosis patch specification is not the exact asset-only specification.')
    }
    $recordPatch = Get-GridSkyrimCertificationProperty $binding 'recordPatch'
    if ($null -eq $binding -or
        [string](Get-GridSkyrimCertificationProperty $binding 'diagnosisManifestSha256') -cne $ExpectedDiagnosisManifestFileSha256.ToUpperInvariant() -or
        [string](Get-GridSkyrimCertificationProperty $binding 'diagnosisPatchSpecificationSha256') -cne $ExpectedPatchSpecificationSha256.ToUpperInvariant() -or
        [string](Get-GridSkyrimCertificationProperty $binding 'solutionKind') -cne 'InstallationAssetRepair' -or
        [string](Get-GridSkyrimCertificationProperty $binding 'requiredAsset') -cne $ExpectedAssetVirtualPath -or
        $null -eq $recordPatch -or [bool](Get-GridSkyrimCertificationProperty $recordPatch 'required') -or
        [string](Get-GridSkyrimCertificationProperty $recordPatch 'status') -cne 'NotApplicable' -or
        @((Get-GridSkyrimCertificationProperty $recordPatch 'records')).Count -ne 0) {
        $invalidLineage = $true; $issues.Add('The repair case does not bind the exact diagnosis and asset-only action.')
    }
    $preconditions.Add([pscustomobject][ordered]@{ name='ExactLineage'; status=if ($invalidLineage) {'Fail'} else {'Pass'}; detail=if ($invalidLineage) {'One or more exact lineage bindings failed.'} else {'Baseline, diagnosis, repair, and asset-only specification identities match.'}; evidenceSha256=$patchHash })

    $acquisition = Read-GridSkyrimCertificationJson (Join-Path $repairEvidenceDirectory 'acquisition\artifact-acquisition-record.v1.json')
    $pendingAction = Read-GridSkyrimCertificationJson (Join-Path $repairEvidenceDirectory 'acquisition\user-acquisition-action.v1.json')
    $repairSpecificationPath = Join-Path $repairEvidenceDirectory 'repair\repair-specification.v1.json'
    $repairSpecification = Read-GridSkyrimCertificationJson $repairSpecificationPath
    $transactionReceipt = Read-GridSkyrimCertificationJson (Join-Path $repairEvidenceDirectory 'repair\repair-transaction-receipt.v1.json')
    $staticReceipt = Read-GridSkyrimCertificationJson (Join-Path $repairEvidenceDirectory 'repair\static-validation.v1.json')
    $virtualWinner = Read-GridSkyrimCertificationJson (Join-Path $repairEvidenceDirectory 'repair\virtual-winner.v1.json')
    $rollback = Read-GridSkyrimCertificationJson (Join-Path $repairEvidenceDirectory 'repair\rollback-manifest.v1.json')
    $protectedBefore = Read-GridSkyrimCertificationJson (Join-Path $repairEvidenceDirectory 'snapshots\protected-before.v1.json')
    $protectedAfter = Read-GridSkyrimCertificationJson (Join-Path $repairEvidenceDirectory 'snapshots\protected-after.v1.json')

    $acquisitionPassed = $null -ne $acquisition -and [string](Get-GridSkyrimCertificationProperty $acquisition 'state') -eq 'Verified' -and
        [string](Get-GridSkyrimCertificationProperty (Get-GridSkyrimCertificationProperty $acquisition 'artifact') 'sha256') -match $script:GridSkyrimCertificationDigestPattern
    $pending = $null -ne $pendingAction -and [string](Get-GridSkyrimCertificationProperty $pendingAction 'state') -eq 'PendingUserAcquisition'
    $preconditions.Add([pscustomobject][ordered]@{ name='ArtifactAcquisition'; status=if ($acquisitionPassed) {'Pass'} elseif ($pending) {'Pending'} else {'NotStarted'}; detail=if ($acquisitionPassed) {'The exact artifact acquisition receipt is verified.'} elseif ($pending) {'The exact managed acquisition is pending and resumable.'} else {'No verified exact artifact acquisition receipt exists.'}; evidenceSha256=if ($acquisitionPassed) {[string]$acquisition.artifact.sha256} else {$null} })

    $specificationReady = $null -ne $repairSpecification
    $transactionSummary = if ($transactionReceipt) { Get-GridSkyrimCertificationProperty (Get-GridSkyrimCertificationProperty $transactionReceipt 'result') 'summary' } else { $null }
    $transactionPassed = $null -ne $transactionReceipt -and [string]$transactionSummary -eq 'InputsRepairedAndVerified'
    $journalHash = if ($transactionReceipt) { [string](Get-GridSkyrimCertificationProperty $transactionReceipt 'journalSha256') } else { $null }
    $journalPassed = $transactionPassed -and $journalHash -match $script:GridSkyrimCertificationDigestPattern
    $staticPassed = $null -ne $staticReceipt -and [string](Get-GridSkyrimCertificationProperty $staticReceipt 'status') -eq 'Pass'
    $winnerPassed = $null -ne $virtualWinner -and [string](Get-GridSkyrimCertificationProperty $virtualWinner 'status') -eq 'Pass'
    $protectedSealPassed = Test-GridSkyrimProtectedSnapshotEquality -Before $protectedBefore -After $protectedAfter
    $currentProtected = if ($null -ne $protectedBefore) { Test-GridSkyrimCurrentProtectedSnapshot -ExpectedSnapshot $protectedBefore } else { [pscustomobject][ordered]@{status='Fail';observations=@();issues=@('Protected snapshot is missing.');semanticFingerprint=(Get-GridSkyrimCertificationFingerprint @('missing-protected-snapshot'))} }
    $protectedPassed = $protectedSealPassed -and [string]$currentProtected.status -eq 'Pass'
    $rollbackEntries = if ($rollback -and $rollback.PSObject.Properties['entries']) { @($rollback.entries) } else { @() }
    $recoverableRollbackEntries = @($rollbackEntries | Where-Object {
        $null -ne $_ -and
        -not [string]::IsNullOrWhiteSpace([string](Get-GridSkyrimCertificationProperty $_ 'componentId')) -and
        [IO.Path]::IsPathRooted([string](Get-GridSkyrimCertificationProperty $_ 'originalPath')) -and
        [IO.Path]::IsPathRooted([string](Get-GridSkyrimCertificationProperty $_ 'rollbackPath')) -and
        [string](Get-GridSkyrimCertificationProperty $_ 'treeSha256') -match $script:GridSkyrimCertificationDigestPattern
    })
    $rollbackPassed = [bool](
        $null -ne $rollback -and
        [string](Get-GridSkyrimCertificationProperty $rollback 'status') -in @('Available', 'Verified') -and
        $recoverableRollbackEntries.Count -gt 0
    )
    foreach ($condition in @(
        @('RepairTransaction', $transactionPassed, 'A committed InputsRepairedAndVerified transaction receipt is required.'),
        @('StaticValidation', $staticPassed, 'The asset static-validation receipt must pass.'),
        @('VirtualWinner', $winnerPassed, 'The repaired asset must have a verified intended virtual winner.'),
        @('ProtectedState', $protectedPassed, 'Protected before/after content identities must match.'),
        @('JournalIntegrity', $journalPassed, 'The committed transaction must bind a valid journal digest.'),
        @('RollbackPackage', $rollbackPassed, 'A non-empty Available or Verified rollback manifest is required.')
    )) { $preconditions.Add([pscustomobject][ordered]@{ name=[string]$condition[0]; status=if ([bool]$condition[1]) {'Pass'} else {'NotStarted'}; detail=if ([bool]$condition[1]) {'Verified.'} else {[string]$condition[2]}; evidenceSha256=if ([string]$condition[0] -eq 'JournalIntegrity' -and $journalPassed) {$journalHash} elseif ([string]$condition[0] -eq 'ProtectedState') {[string]$currentProtected.semanticFingerprint} else {$null} }) }

    $eligibility = 'PreconditionsNotMet'; $recovery = 'ResumeAvailable'; $blocking = 'ArtifactAcquisition'
    if ($invalidLineage) { $eligibility='InvalidLineage'; $recovery='ConfigurationRequired'; $blocking='ExactLineage' }
    elseif (-not $acquisitionPassed) { $blocking='ArtifactAcquisition' }
    elseif (-not $transactionPassed) {
        if ($specificationReady) { $eligibility='ApprovalRequired'; $recovery='AuthorizationRequired'; $blocking='RepairTransactionAuthorization' }
        else { $blocking='RepairSpecification' }
    }
    elseif (-not $staticPassed) { $blocking='StaticValidation' }
    elseif (-not $winnerPassed) { $blocking='VirtualWinner' }
    elseif (-not $protectedPassed) { $blocking='ProtectedState' }
    elseif (-not $journalPassed) { $blocking='JournalIntegrity' }
    elseif (-not $rollbackPassed) { $blocking='RollbackPackage' }
    else { $eligibility='Eligible'; $recovery='None'; $blocking=$null }

    $gates = @($script:GridSkyrimCertificationGateNames | ForEach-Object { [pscustomobject][ordered]@{ name=$_; status='NotStarted'; detail=if ($_ -eq 'RuntimeIntegrity') {'Runtime launch is prohibited until certification eligibility is established and explicit launch authority is supplied.'} else {'Independent gate collection has not started.'} } })
    $semanticParts = @(
        $BaselineCaseId, $DiagnosisCaseId, $RepairCaseId, $ExpectedBaselineManifestSha256.ToUpperInvariant(),
        $ExpectedDiagnosisManifestFileSha256.ToUpperInvariant(), $ExpectedPatchSpecificationSha256.ToUpperInvariant(),
        [string]$SuccessorRepairCaseId, $(if ($hasSuccessorCase) { $ExpectedSuccessorRepairManifestSha256.ToUpperInvariant() } else { '' }),
        $ExpectedAssetVirtualPath, $eligibility, $recovery, [string]$blocking, [string]$currentProtected.semanticFingerprint,
        (@($preconditions | ForEach-Object { "$($_.name)|$($_.status)|$($_.evidenceSha256)" }) -join ';'),
        (@($issues | Sort-Object) -join ';')
    )
    [pscustomobject][ordered]@{
        schemaVersion=1; lineage=[pscustomobject]$lineage; eligibility=$eligibility
        recoveryDisposition=$recovery; blockingPrimitive=$blocking
        preconditions=$preconditions.ToArray(); gates=$gates
        runtimeValidation='NotStarted'; readyToPlay='NotEvaluated'; protectedState=$currentProtected; issues=$issues.ToArray()
        semanticFingerprint=Get-GridSkyrimCertificationFingerprint $semanticParts
    }
}

function Get-GridSkyrimCertificationTes4Masters {
    param([Parameter(Mandatory)][string]$PluginPath, [Parameter(Mandatory)][int]$MaximumHeaderBytes)
    $stream = [IO.File]::Open($PluginPath, 'Open', 'Read', 'ReadWrite')
    try {
        if ($stream.Length -lt 24) { throw 'TES4 header is truncated.' }
        $reader = New-Object IO.BinaryReader($stream, [Text.Encoding]::ASCII, $true)
        try {
            if ([Text.Encoding]::ASCII.GetString($reader.ReadBytes(4)) -cne 'TES4') { throw 'File does not begin with a TES4 record.' }
            $dataSize = $reader.ReadUInt32(); [void]$reader.ReadBytes(16)
            if ($dataSize -gt $MaximumHeaderBytes -or (24L + [long]$dataSize) -gt $stream.Length) { throw 'TES4 header is truncated or exceeds the bound.' }
            $data = $reader.ReadBytes([int]$dataSize)
            if ($data.Length -ne [int]$dataSize) { throw 'TES4 header read was incomplete.' }
        } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
    $masters = New-Object Collections.Generic.List[string]; $position=0; $extended=$null; $latin1=[Text.Encoding]::GetEncoding(28591)
    while ($position -lt $data.Length) {
        if (($data.Length-$position) -lt 6) { throw 'TES4 subrecord header is truncated.' }
        $signature=[Text.Encoding]::ASCII.GetString($data,$position,4); $short=[BitConverter]::ToUInt16($data,$position+4); $position+=6
        $size=if ($null -ne $extended) {[uint32]$extended} else {[uint32]$short}; $extended=$null
        if ($size -gt [int]::MaxValue -or ([long]$position+[long]$size) -gt $data.Length) { throw 'TES4 subrecord extends beyond the header.' }
        if ($signature -eq 'XXXX') { if ($size -ne 4) { throw 'TES4 XXXX subrecord is malformed.' }; $extended=[BitConverter]::ToUInt32($data,$position); $position+=4; continue }
        if ($signature -eq 'MAST') {
            $bytes=New-Object byte[] ([int]$size); [Array]::Copy($data,$position,$bytes,0,[int]$size); $nullIndex=[Array]::IndexOf($bytes,[byte]0)
            $length=if ($nullIndex -ge 0) {$nullIndex} else {$bytes.Length}; $name=$latin1.GetString($bytes,0,$length).Trim()
            if ([string]::IsNullOrWhiteSpace($name) -or [IO.Path]::GetFileName($name) -cne $name) { throw 'TES4 MAST contains an invalid plugin identity.' }
            $masters.Add($name)
        }
        $position += [int]$size
    }
    if ($null -ne $extended) { throw 'TES4 XXXX subrecord has no following subrecord.' }
    @($masters)
}

function Test-GridSkyrimEnabledPluginIntegrity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$PluginsFilePath,
        [Parameter(Mandatory)][string[]]$PluginRoots,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$ImplicitMasterNames,
        [ValidateRange(1, 2048)][int]$MaximumEnabledPlugins=2048,
        [ValidateRange(24, 16777216)][int]$MaximumHeaderBytes=16777216
    )
    $issues=New-Object Collections.Generic.List[string]; $observations=New-Object Collections.Generic.List[object]
    if (-not (Test-Path -LiteralPath $PluginsFilePath -PathType Leaf)) { throw 'PluginIntegrityInvalid: plugins.txt is missing.' }
    $profileItem=Get-Item -LiteralPath $PluginsFilePath -Force
    if ($profileItem.Length -gt 33554432) { throw 'PluginIntegrityBudgetExceeded: plugins.txt exceeds 32 MiB.' }
    $roots=@()
    foreach ($root in $PluginRoots) {
        $full=[IO.Path]::GetFullPath($root)
        if (-not (Test-Path -LiteralPath $full -PathType Container)) { throw "PluginIntegrityInvalid: plugin root is missing: $full" }
        if (((Get-Item -LiteralPath $full -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "PluginIntegrityBoundaryRefused: plugin root is a reparse point: $full" }
        $roots += $full.TrimEnd('\','/')
    }
    $implicit=@{}; foreach($name in $ImplicitMasterNames){ if ([IO.Path]::GetFileName($name) -cne $name -or $name -notmatch '(?i)\.(esm|esl|esp)$') { throw "PluginIntegrityInvalid: invalid implicit master identity '$name'." }; $implicit[$name]=$true }
    $enabled=New-Object Collections.Generic.List[string]; $seen=@{}
    foreach($line in [IO.File]::ReadAllLines([IO.Path]::GetFullPath($PluginsFilePath))) {
        if (-not $line.StartsWith('*')) { continue }; $name=$line.Substring(1).Trim()
        if ([IO.Path]::GetFileName($name) -cne $name -or $name -notmatch '(?i)\.(esm|esl|esp)$') { $issues.Add("Invalid enabled plugin row: $name"); continue }
        if ($seen.ContainsKey($name)) { $issues.Add("Duplicate or case-colliding enabled plugin row: $name"); continue }; $seen[$name]=$true; $enabled.Add($name)
        if ($enabled.Count -gt $MaximumEnabledPlugins) { throw "PluginIntegrityBudgetExceeded: enabled plugin count exceeds $MaximumEnabledPlugins." }
    }
    $allRequired=@($enabled.ToArray()) + @($implicit.Keys)
    $resolved=@{}
    foreach($name in @($allRequired | Sort-Object -Unique)) {
        $providers=@(); foreach($root in $roots){$candidate=Join-Path $root $name; if(Test-Path -LiteralPath $candidate -PathType Leaf){$providers += [IO.Path]::GetFullPath($candidate)}}
        if($providers.Count -eq 0){$issues.Add("Required plugin file is absent: $name"); continue}; $resolved[$name]=$providers[-1]
    }
    foreach($name in @($enabled.ToArray() | Sort-Object)) {
        if(-not $resolved.ContainsKey($name)){continue}; $path=[string]$resolved[$name]
        try {
            $item=Get-Item -LiteralPath $path -Force -ErrorAction Stop; if($item.Length -eq 0){throw 'Plugin file is zero length.'}
            $masters=@(Get-GridSkyrimCertificationTes4Masters -PluginPath $path -MaximumHeaderBytes $MaximumHeaderBytes)
            foreach($master in $masters){if(-not $seen.ContainsKey($master) -and -not $implicit.ContainsKey($master)){$issues.Add("Enabled plugin '$name' requires inactive master '$master'.")}; if(-not $resolved.ContainsKey($master)){$issues.Add("Enabled plugin '$name' requires missing master '$master'.")}}
            $observations.Add([pscustomobject][ordered]@{name=$name;path=$path;status='Readable';sizeBytes=[long]$item.Length;sha256=Get-GridSkyrimCertificationSha256 $path;masters=@($masters);shadowedProviders=@()})
        } catch {$issues.Add("Enabled plugin '$name' is unreadable or malformed: $($_.Exception.Message)"); $observations.Add([pscustomobject][ordered]@{name=$name;path=$path;status='Unreadable';sizeBytes=$null;sha256=$null;masters=@();shadowedProviders=@()})}
    }
    $parts=@($observations|Sort-Object name|ForEach-Object{"$($_.name)|$($_.status)|$($_.sha256)|$(@($_.masters)-join ',')"}) + @($implicit.Keys|Sort-Object|ForEach-Object{"implicit|$_|$($resolved[$_])"}) + @($issues|Sort-Object)
    [pscustomobject][ordered]@{schemaVersion=1;status=if($issues.Count -eq 0){'Pass'}else{'Fail'};enabledPluginCount=$enabled.Count;plugins=$observations.ToArray();implicitMasters=@($implicit.Keys|Sort-Object);issues=$issues.ToArray();semanticFingerprint=Get-GridSkyrimCertificationFingerprint $parts}
}

function New-GridSkyrimCertificationPreflight {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Eligibility,
        [Parameter(Mandatory)][string]$Mo2ExecutablePath,
        [Parameter(Mandatory)][string]$SkseExecutableTitle,
        [Parameter(Mandatory)][string]$ExpectedGameExecutablePath,
        [Parameter(Mandatory)][string]$DisposableProfilesRoot,
        [Parameter(Mandatory)][string]$EvidenceCaseDirectory,
        [Parameter(Mandatory)][string]$RollbackDirectory,
        [string[]]$AdditionalProtectedPaths = @()
    )
    foreach($path in @($Mo2ExecutablePath,$ExpectedGameExecutablePath,$DisposableProfilesRoot,$EvidenceCaseDirectory,$RollbackDirectory)+@($AdditionalProtectedPaths)){if(-not [IO.Path]::IsPathRooted($path)){throw 'CertificationPreflightInvalid: every filesystem path must be absolute.'}}
    if([IO.Path]::GetFileName($Mo2ExecutablePath) -ine 'ModOrganizer.exe'){throw 'CertificationPreflightInvalid: the manager process must be ModOrganizer.exe.'}
    if([string]::IsNullOrWhiteSpace($SkseExecutableTitle)){throw 'CertificationPreflightInvalid: SKSE executable title is required.'}
    $eligibilityFingerprint=[string](Get-GridSkyrimCertificationProperty $Eligibility 'semanticFingerprint')
    Assert-GridSkyrimCertificationDigest -Value $eligibilityFingerprint -Name 'Eligibility.semanticFingerprint'
    $ready=[string](Get-GridSkyrimCertificationProperty $Eligibility 'eligibility') -eq 'Eligible'
    $profileName='Grid-Cert-'+$eligibilityFingerprint.Substring(0,12).ToLowerInvariant(); $profilePath=Join-Path ([IO.Path]::GetFullPath($DisposableProfilesRoot)) $profileName
    $paths=[pscustomobject][ordered]@{disposableProfile=$profilePath;emptySaveRoot=Join-Path $profilePath 'saves';evidenceCase=[IO.Path]::GetFullPath($EvidenceCaseDirectory);screenshots=Join-Path ([IO.Path]::GetFullPath($EvidenceCaseDirectory)) 'runtime\screenshots';logs=Join-Path ([IO.Path]::GetFullPath($EvidenceCaseDirectory)) 'runtime\logs';rollback=[IO.Path]::GetFullPath($RollbackDirectory)}
    $issues=if($ready){@()}else{@("Certification eligibility is '$([string]$Eligibility.eligibility)' at '$([string]$Eligibility.blockingPrimitive)'.")}
    $criteria = [ordered]@{
        InstallationIntegrity='Selected MO2, Skyrim, SKSE, roots, executable identities, and repaired installation tree all verify.'
        CompatibilityIntegrity='The exact evidence-bound provider chain, versions, installer choices, archive identities, and compatibility evidence all verify.'
        AssetIntegrity='The repaired NIF is the sole intended winner and its NIF and every referenced DDS provider validate.'
        RecordIntegrity='The bounded diagnosed CELL/reference graph is unchanged by the asset-only repair.'
        PluginIntegrity='Every enabled plugin header parses and every explicit or implicit master resolves without missing, zero, unreadable, or truncated files.'
        ProfileIntegrity='Protected hashes, enabled mod directories, ordering, encoding, and repair placement verify without unrelated changes.'
        RuntimeIntegrity='Bound geometry, collision, reference, screenshot, adjacent-area, log, and process evidence satisfies every declared runtime oracle claim.'
        RollbackIntegrity='The bound journal, rollback manifest, preserved prior tree, hashes, and reverse operation specification verify.'
    }
    $preflightGates = @($Eligibility.gates | ForEach-Object { [pscustomobject][ordered]@{ name=[string]$_.name; status=[string]$_.status; detail=[string]$_.detail; passCriteria=[string]$criteria[[string]$_.name] } })
    $semantic=Get-GridSkyrimCertificationFingerprint @($eligibilityFingerprint,[IO.Path]::GetFullPath($Mo2ExecutablePath),$SkseExecutableTitle,[IO.Path]::GetFullPath($ExpectedGameExecutablePath),$profilePath,[string]$Eligibility.eligibility)
    [pscustomobject][ordered]@{
        schemaVersion=1;status=if($ready){'Ready'}else{'Blocked'};eligibilityFingerprint=$eligibilityFingerprint
        processes=@([pscustomobject][ordered]@{role='Manager';executablePath=[IO.Path]::GetFullPath($Mo2ExecutablePath)},[pscustomobject][ordered]@{role='Game';executablePath=[IO.Path]::GetFullPath($ExpectedGameExecutablePath)},[pscustomobject][ordered]@{role='LauncherEntry';title=$SkseExecutableTitle},[pscustomobject][ordered]@{role='CaptureInputHelper';status='NotConfigured';executablePath=$null})
        paths=$paths;writablePaths=@($profilePath,$paths.emptySaveRoot,$paths.evidenceCase,$paths.screenshots,$paths.logs)
        protectedPaths=@(@($paths.rollback)+@($AdditionalProtectedPaths | ForEach-Object {[IO.Path]::GetFullPath($_)}) | Sort-Object -Unique);gates=$preflightGates
        limits=[pscustomobject][ordered]@{maximumRuntimeSeconds=900;maximumInputSteps=256;maximumScreenshots=6;maximumScreenshotBytes=33554432;maximumAggregateLogBytes=268435456}
        closePolicy='Close only revalidated Grid-owned processes; normal close precedes separately authorized force-close.'
        rollbackBehavior='Runtime certification never activates rollback; a failed repair-scope postcondition delegates to the bound repair journal.'
        issues=@($issues);semanticFingerprint=$semantic
    }
}
