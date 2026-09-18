#requires -Version 5.1

<#
.SYNOPSIS
Executes one exact authorized mod-chain repair specification.
.DESCRIPTION
Stages complete replacement trees outside the active mod tree, preserves each
original in rollback storage, promotes by same-volume rename, verifies all
postconditions, and reverses committed operations when verification fails.
#>

function Add-GridRepairJournalRecord {
    param(
        [Parameter(Mandatory)][string]$JournalPath,
        [Parameter(Mandatory)][string]$TransactionId,
        [Parameter(Mandatory)][int]$Sequence,
        [Parameter(Mandatory)][string]$Operation,
        [Parameter(Mandatory)][string]$State,
        [string]$PreviousRecordSha256,
        $Detail
    )
    $unsigned = [pscustomobject][ordered]@{ schemaVersion = 1; transactionId = $TransactionId; sequence = $Sequence; operation = $Operation; state = $State; previousRecordSha256 = if ($PreviousRecordSha256) { $PreviousRecordSha256 } else { $null }; recordedAt = [DateTimeOffset]::UtcNow.ToString('o'); detail = $Detail }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($unsigned | ConvertTo-Json -Depth 20 -Compress))
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $hash = ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace('-', '') } finally { $algorithm.Dispose() }
    $record = [pscustomobject][ordered]@{ schemaVersion = 1; transactionId = $TransactionId; sequence = $Sequence; operation = $Operation; state = $State; previousRecordSha256 = $unsigned.previousRecordSha256; recordedAt = $unsigned.recordedAt; detail = $Detail; recordSha256 = $hash }
    $line = ($record | ConvertTo-Json -Depth 20 -Compress) + [Environment]::NewLine
    $stream = New-Object IO.FileStream($JournalPath, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read, 4096, [IO.FileOptions]::WriteThrough)
    try { $data = [Text.Encoding]::UTF8.GetBytes($line); $stream.Write($data, 0, $data.Length); $stream.Flush() } finally { $stream.Dispose() }
    $record
}

function Copy-GridRepairTree {
    param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Destination)
    if (Test-Path -LiteralPath $Destination) { throw "Transaction destination already exists: $Destination" }
    New-Item -ItemType Directory -Path $Destination -ErrorAction Stop | Out-Null
    foreach ($directory in @(Get-ChildItem -LiteralPath $Source -Directory -Recurse -Force -ErrorAction Stop | Sort-Object FullName)) {
        if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "ReparsePointRefused: $($directory.FullName)" }
        $relative = $directory.FullName.Substring($Source.TrimEnd('\').Length + 1)
        New-Item -ItemType Directory -Path (Join-Path $Destination $relative) -ErrorAction Stop | Out-Null
    }
    foreach ($file in @(Get-ChildItem -LiteralPath $Source -File -Recurse -Force -ErrorAction Stop | Sort-Object FullName)) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "ReparsePointRefused: $($file.FullName)" }
        $relative = $file.FullName.Substring($Source.TrimEnd('\').Length + 1)
        $target = Join-Path $Destination $relative
        $parent = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -ErrorAction Stop | Out-Null }
        $input = [IO.File]::Open($file.FullName, 'Open', 'Read', 'Read')
        $output = [IO.File]::Open($target, 'CreateNew', 'Write', 'None')
        try { $input.CopyTo($output, 65536); $output.Flush() } finally { $output.Dispose(); $input.Dispose() }
        [IO.File]::SetLastWriteTimeUtc($target, $file.LastWriteTimeUtc)
    }
}

function Assert-GridRepairOperationPostconditions {
    param([Parameter(Mandatory)]$Operation, [Parameter(Mandatory)][string]$Root, [switch]$ValidateProvider)
    foreach ($expected in @($Operation.expectedFiles)) {
        $relative = ConvertTo-GridRepairRelativePath ([string]$expected.path)
        $path = Join-Path $Root $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "ExpectedFileMissing: $relative" }
        if ((Get-GridRepairSha256 -LiteralPath $path) -cne ([string]$expected.sha256).ToUpperInvariant()) { throw "ExpectedFileHashMismatch: $relative" }
    }
    if ($Operation.PSObject.Properties['virtualAssetPostconditions']) {
        foreach ($expected in @($Operation.virtualAssetPostconditions)) {
            $relative = ConvertTo-GridRepairRelativePath ([string]$expected.virtualPath)
            if ($ValidateProvider -and [IO.Path]::GetFullPath([string]$expected.expectedProviderDirectory).TrimEnd('\') -ine [IO.Path]::GetFullPath($Root).TrimEnd('\')) { throw "VirtualProviderMismatch: $relative" }
            if ($expected.PSObject.Properties['archiveRelativePath']) {
                $archiveRelative = ConvertTo-GridRepairRelativePath ([string]$expected.archiveRelativePath)
                $archiveMember = ConvertTo-GridRepairRelativePath ([string]$expected.archiveMemberPath)
                if ($archiveMember -ine $relative -or [string]$expected.validationBasis -cne 'ExactContainerDigest' -or [string]$expected.assetEvidenceSha256 -notmatch '^[A-F0-9]{64}$' -or [string]$expected.requiredSha256 -notmatch '^[A-F0-9]{64}$') { throw "VirtualAssetMemberPostconditionInvalid: $relative" }
                $path = Join-Path $Root $archiveRelative
                if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-GridRepairSha256 -LiteralPath $path) -cne ([string]$expected.archiveSha256).ToUpperInvariant()) { throw "VirtualAssetContainerPostconditionFailed: $relative" }
            }
            else {
                $path = Join-Path $Root $relative
                if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-GridRepairSha256 -LiteralPath $path) -cne ([string]$expected.requiredSha256).ToUpperInvariant()) { throw "VirtualAssetPostconditionFailed: $relative" }
                if ([string]$expected.validationKind -eq 'Nif' -and -not (Test-GridRepairNifHeader -LiteralPath $path)) { throw "NifValidationFailed: $relative" }
            }
        }
    }
}

function New-GridRepairResult {
    param(
        [string]$RunLifecycle, [string]$RepairOutcome, [string]$MutationState,
        [string]$RollbackState, [string]$RecoveryDisposition, [AllowNull()]$Summary,
        [object[]]$Failures = @()
    )
    [pscustomobject][ordered]@{ schemaVersion = 1; runLifecycle = $RunLifecycle; repairOutcome = $RepairOutcome; mutationState = $MutationState; rollbackState = $RollbackState; recoveryDisposition = $RecoveryDisposition; summary = $Summary; failures = @($Failures) }
}

function Invoke-GridAuthorizedModChainRepair {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory)][string]$SpecificationPath,
        [Parameter(Mandatory)][string]$AuthorizationGrantId,
        [Parameter(Mandatory)][string]$AuthorizationSecret,
        [Parameter(Mandatory)][string]$AuthorizationStoreRoot,
        [Parameter(Mandatory)]$SemanticBinding,
        [Parameter(Mandatory)][string]$CaseStoreRoot,
        [Parameter(Mandatory)][string]$TransactionRoot,
        [switch]$PassThru
    )
    $specificationPathFull = [IO.Path]::GetFullPath($SpecificationPath)
    if (-not (Test-Path -LiteralPath $specificationPathFull -PathType Leaf)) { throw "Repair specification is missing: $specificationPathFull" }
    $specification = Get-Content -LiteralPath $specificationPathFull -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    $calculated = Get-GridRepairSpecificationHash -Specification $specification
    if ($calculated -cne [string]$specification.specificationSha256) { throw 'SpecificationDigestMismatch: immutable repair specification has changed.' }
    if ([string]$SemanticBinding.proposalOrSpecificationId -cne [string]$specification.specificationId -or [string]$SemanticBinding.proposalOrSpecificationSha256 -cne [string]$specification.specificationSha256) { throw 'AuthorizationBindingMismatch: repair specification identity changed.' }
    if ([string]$SemanticBinding.normalizedInputSha256 -cne (Get-GridCanonicalJsonSha256 -InputObject $specification)) { throw 'AuthorizationBindingMismatch: normalized repair specification changed.' }
    if (@($SemanticBinding.capabilities | Where-Object { [string]$_.capabilityId -ceq 'grid.game.skyrimspecialedition.mod-chain-repair.execute' -and [string]$_.capabilityVersion -ceq '2.0.0' }).Count -ne 1) { throw 'AuthorizationBindingMismatch: mod-chain repair capability/version is not authorized.' }
    $expectedTargets=@($specification.operations | ForEach-Object { [string]$_.targetDirectory } | Sort-Object -Unique); $boundTargets=@($SemanticBinding.targets | Sort-Object -Unique); if (($expectedTargets-join "`n") -cne ($boundTargets-join "`n")) { throw 'AuthorizationBindingMismatch: exact repair targets changed.' }
    Test-GridAuthorizationGrantPreflight -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -AuthorizationSecret $AuthorizationSecret -ExpectedSemanticBinding $SemanticBinding | Out-Null
    $seal = Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory ([string]$specification.baseline.caseDirectory)
    if (-not $seal.IsValid -or [string]$seal.Manifest.manifestSha256 -cne [string]$specification.baseline.manifestSha256) { throw 'BaselineSealInvalid: the bound baseline is absent, changed, or invalid.' }
    if ($specification.PSObject.Properties['diagnosis']) {
        $diagnosisSeal = Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory ([string]$specification.diagnosis.caseDirectory)
        if (-not $diagnosisSeal.IsValid -or [string]$diagnosisSeal.Manifest.manifestSha256 -cne [string]$specification.diagnosis.manifestSha256) { throw 'DiagnosisSealInvalid: the bound diagnosis is absent, changed, or invalid.' }
        if ((Get-GridRepairSha256 -LiteralPath (Join-Path ([string]$specification.diagnosis.caseDirectory) 'case-manifest.v1.json')) -cne [string]$specification.diagnosis.manifestFileSha256) { throw 'DiagnosisManifestDigestMismatch: bound manifest bytes changed.' }
        $patchPath = Join-Path ([string]$specification.diagnosis.caseDirectory) 'patch\conflict-patch-specification.v1.json'
        if (-not (Test-Path -LiteralPath $patchPath -PathType Leaf)) { throw 'DiagnosisArtifactMissing: bound patch specification is absent.' }
        if ((Get-GridRepairSha256 -LiteralPath $patchPath) -cne [string]$specification.diagnosis.specificationFileSha256) { throw 'DiagnosisSpecificationFileDigestMismatch: bound patch bytes changed.' }
        $diagnosisPatch = Get-Content -LiteralPath $patchPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        if ((Get-GridRootCausePatchSpecificationHash -Specification $diagnosisPatch) -cne [string]$specification.diagnosis.specificationSha256 -or [string]$diagnosisPatch.solutionKind -cne 'InstallationAssetRepair' -or @($diagnosisPatch.records).Count -ne 0) { throw 'DiagnosisSpecificationMismatch: repair authority is not bound to the sealed asset-only diagnosis.' }
        if (-not $specification.PSObject.Properties['recordPatch'] -or $specification.recordPatch.required -or [string]$specification.recordPatch.status -cne 'NotApplicable' -or @($specification.recordPatch.records).Count -ne 0) { throw 'UnsupportedRecordPatch: diagnosis-bound asset repair cannot execute record edits.' }
    }
    foreach ($protected in @($specification.protectedState)) {
        $path = Test-GridRepairPathBoundary -LiteralPath ([string]$protected.path) -Role ReadRoot -MustExist
        if ((Get-GridRepairSha256 -LiteralPath $path) -cne [string]$protected.sha256) { throw "ProtectedHashChanged: $path" }
    }
    foreach ($artifact in @($specification.artifactIntegrity)) {
        if ([string]$artifact.status -cne 'Verified') { throw "ArtifactNotVerified: $($artifact.artifactId)" }
        $artifactPath = Test-GridRepairPathBoundary -LiteralPath ([string]$artifact.path) -Role ReadRoot -MustExist
        if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf) -or (Get-GridRepairSha256 -LiteralPath $artifactPath) -cne [string]$artifact.sha256) { throw "ArtifactHashChanged: $($artifact.artifactId)" }
    }
    $targetInstanceRoots = @($specification.operations | ForEach-Object {
        $modsRoot = Split-Path -Parent ([IO.Path]::GetFullPath([string]$_.targetDirectory))
        [IO.Path]::GetFullPath((Split-Path -Parent $modsRoot)).TrimEnd('\') + '\'
    } | Sort-Object -Unique)
    $targetGameRoots = @()
    $installationPath = Join-Path ([string]$specification.baseline.caseDirectory) 'installation\installation-baseline.v1.json'
    if (Test-Path -LiteralPath $installationPath -PathType Leaf) {
        $installation = Get-Content -LiteralPath $installationPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        $targetGameRoots = @($installation.roots | Where-Object { [string]$_.label -eq 'Skyrim Data directory' } | ForEach-Object {
            [IO.Path]::GetFullPath((Split-Path -Parent ([string]$_.path))).TrimEnd('\') + '\'
        } | Sort-Object -Unique)
    }
    $running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        if ($_.ProcessName -notin @('ModOrganizer', 'SkyrimSE', 'SkyrimSELauncher', 'SSEEdit', 'SSEEdit64', 'xEdit')) { return $false }
        $processPath = try { [string]$_.Path } catch { '' }
        if ([string]::IsNullOrWhiteSpace($processPath)) { return $true }
        $fullProcessPath = [IO.Path]::GetFullPath($processPath)
        if ($_.ProcessName -eq 'ModOrganizer') {
            return @($targetInstanceRoots | Where-Object { $fullProcessPath.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
        }
        if ($_.ProcessName -in @('SSEEdit','SSEEdit64','xEdit')) { return $true }
        if ($targetGameRoots.Count -eq 0) { return $true }
        @($targetGameRoots | Where-Object { $fullProcessPath.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
    })
    if ($running.Count) { throw ('ProcessGateRefused: close relevant applications before repair: ' + (@($running.ProcessName | Sort-Object -Unique) -join ', ')) }

    $transactionRootFull = Test-GridRepairPathBoundary -LiteralPath $TransactionRoot -Role MutationRoot
    $transactionId = 'transaction-' + [string]$specification.specificationSha256
    $directory = Join-Path $transactionRootFull $transactionId
    $receiptPath = Join-Path $directory 'receipt.v2.json'
    $historicalReceiptPath = Join-Path $directory 'receipt.v1.json'
    $journalPath = Join-Path $directory 'journal.v1.ndjson'
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf) -and (Test-Path -LiteralPath $historicalReceiptPath -PathType Leaf)) { throw 'HistoricalReceiptNotResumable: historical repair receipt remains readable but cannot satisfy the current semantic binding contract.' }
    if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
        $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
        if ([int]$receipt.schemaVersion -ne 2) { throw 'HistoricalReceiptNotResumable: historical repair receipts remain readable but cannot satisfy the current semantic binding contract.' }
        $expectedBindingSha = Get-GridAuthorizationSemanticDigest -SemanticBinding $SemanticBinding
        foreach ($pair in @(
            @('workspaceId',[string]$SemanticBinding.workspaceId), @('requestId',[string]$SemanticBinding.requestId), @('submissionId',[string]$SemanticBinding.submissionId),
            @('envelopeSha256',[string]$SemanticBinding.envelopeSha256), @('planSha256',[string]$SemanticBinding.planSha256), @('authorizationGrantId',$AuthorizationGrantId),
            @('authorizationBindingSha256',$expectedBindingSha), @('capabilityId','grid.game.skyrimspecialedition.mod-chain-repair.execute'), @('capabilityVersion','2.0.0'),
            @('adapterId','SkyrimSpecialEdition'), @('adapterVersion','1'), @('normalizedInputSha256',[string]$SemanticBinding.normalizedInputSha256),
            @('specificationId',[string]$specification.specificationId), @('specificationSha256',[string]$specification.specificationSha256), @('baselineManifestSha256',[string]$specification.baseline.manifestSha256))) {
            if ([string]$receipt.($pair[0]) -cne [string]$pair[1]) { throw "TerminalReceiptCollision: existing receipt $($pair[0]) differs from the current semantic identity." }
        }
        if (-not (Test-Path -LiteralPath $journalPath -PathType Leaf) -or (Get-GridRepairSha256 -LiteralPath $journalPath) -cne [string]$receipt.journalSha256) { throw 'TerminalReceiptJournalMismatch: existing transaction journal changed.' }
        $expectedOutputSha = Get-GridCanonicalJsonSha256 -InputObject $receipt.result
        $expectedEvidenceSha = Get-GridCanonicalJsonSha256 -InputObject ([pscustomobject][ordered]@{baseline=[string]$specification.baseline.manifestSha256;diagnosis=if($specification.PSObject.Properties['diagnosis']){[string]$specification.diagnosis.manifestSha256}else{$null}})
        if ([string]$receipt.outputSha256 -cne $expectedOutputSha -or [string]$receipt.evidenceSha256 -cne $expectedEvidenceSha) { throw 'TerminalReceiptContentBindingMismatch: existing repair output or evidence digest is invalid.' }
        if ($PassThru) { return $receipt } else { return ($receipt | ConvertTo-Json -Depth 30) }
    }

    foreach ($operation in @($specification.operations)) {
        [void](Test-GridRepairPathBoundary -LiteralPath ([string]$operation.sourceDirectory) -Role ReadRoot -MustExist)
        $beforeTreeState = if ($operation.PSObject.Properties['beforeTreeState']) { [string]$operation.beforeTreeState } else { 'Existing' }
        if ($beforeTreeState -eq 'Existing') { [void](Test-GridRepairPathBoundary -LiteralPath ([string]$operation.targetDirectory) -Role ReadRoot -MustExist) }
        elseif ($beforeTreeState -eq 'Absent') {
            [void](Test-GridRepairPathBoundary -LiteralPath ([string]$operation.targetDirectory) -Role MutationRoot)
            if (Test-Path -LiteralPath ([string]$operation.targetDirectory)) { throw "StaleRepairSpecification: overlay target is no longer absent: $($operation.targetDirectory)" }
        }
        else { throw "BeforeTreeStateUnsupported: $beforeTreeState" }
        foreach ($pathName in @('stagingDirectory','rollbackDirectory')) { [void](Test-GridRepairPathBoundary -LiteralPath ([string]$operation.$pathName) -Role MutationRoot) }
        $targetVolume = [IO.Path]::GetPathRoot([string]$operation.targetDirectory)
        foreach ($pathName in @('stagingDirectory','rollbackDirectory')) {
            if ([IO.Path]::GetPathRoot([string]$operation.$pathName) -ine $targetVolume) { throw "CrossVolumeMutationRefused: $pathName must share the target volume." }
        }
        $before = if ($beforeTreeState -eq 'Existing') { Get-GridRepairTreeObservation -LiteralPath ([string]$operation.targetDirectory) } else { [pscustomobject]@{ treeSha256 = ('0' * 64) } }
        $source = Get-GridRepairTreeObservation -LiteralPath ([string]$operation.sourceDirectory)
        if ($before.treeSha256 -cne [string]$operation.beforeTreeSha256 -or $source.treeSha256 -cne [string]$operation.expectedTreeSha256) { throw 'StaleRepairSpecification: target or staged source changed after approval.' }
        Assert-GridRepairOperationPostconditions -Operation $operation -Root ([string]$operation.sourceDirectory)
    }
    $lease = Enter-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -AuthorizationSecret $AuthorizationSecret -ExpectedSemanticBinding $SemanticBinding -ConsumerId ('mod-chain-repair-' + [string]$specification.specificationId)
    if ($WhatIfPreference -or -not $PSCmdlet.ShouldProcess(([string]$specification.specificationId), 'Stage, preserve, promote, verify, and recover exact mod replacement trees')) {
        $whatIfResult = New-GridRepairResult -RunLifecycle Planned -RepairOutcome NotAttempted -MutationState NotStarted -RollbackState NotRequired -RecoveryDisposition None -Summary $null
        Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Consumed -Detail 'WhatIf/ShouldProcess declined; authorization consumed without mutation.' | Out-Null
        if ($PassThru) { return $whatIfResult } else { return ($whatIfResult | ConvertTo-Json -Depth 20) }
    }

    if (-not (Test-Path -LiteralPath $transactionRootFull -PathType Container)) { New-Item -ItemType Directory -Path $transactionRootFull -ErrorAction Stop | Out-Null }
    New-Item -ItemType Directory -Path $directory -ErrorAction Stop | Out-Null
    $create = New-Object IO.FileStream($journalPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read, 4096, [IO.FileOptions]::WriteThrough)
    $create.Dispose()
    $sequence = 0
    $previous = $null
    $committed = New-Object Collections.Generic.List[object]
    $preserved = New-Object Collections.Generic.List[object]
    $failure = $null
    try {
        $record = Add-GridRepairJournalRecord -JournalPath $journalPath -TransactionId $transactionId -Sequence $sequence -Operation 'Authorize' -State 'Running' -PreviousRecordSha256 $previous -Detail ([pscustomobject]@{ specificationSha256 = $specification.specificationSha256; baselineManifestSha256 = $specification.baseline.manifestSha256 })
        $previous = $record.recordSha256
        foreach ($operation in @($specification.operations | Sort-Object sequence)) {
            $sequence++
            $record = Add-GridRepairJournalRecord -JournalPath $journalPath -TransactionId $transactionId -Sequence $sequence -Operation 'Stage' -State 'Intent' -PreviousRecordSha256 $previous -Detail ([pscustomobject]@{ componentId = $operation.componentId; source = $operation.sourceDirectory; destination = $operation.stagingDirectory; expectedTreeSha256 = $operation.expectedTreeSha256 })
            $previous = $record.recordSha256
            Copy-GridRepairTree -Source ([string]$operation.sourceDirectory) -Destination ([string]$operation.stagingDirectory)
            $staged = Get-GridRepairTreeObservation -LiteralPath ([string]$operation.stagingDirectory)
            if ($staged.treeSha256 -cne [string]$operation.expectedTreeSha256) { throw "StagingVerificationFailed: $($operation.componentId)" }
            $stagedManifest = [pscustomobject][ordered]@{ schemaVersion = 1; specificationId = [string]$specification.specificationId; componentId = [string]$operation.componentId; root = [string]$operation.stagingDirectory; treeSha256 = $staged.treeSha256; files = @($staged.files); verifiedAt = [DateTimeOffset]::UtcNow.ToString('o') }
            Write-GridJsonAtomic -InputObject $stagedManifest -LiteralPath (Join-Path $directory ('staged-{0:d8}.v1.json' -f [int]$operation.sequence))
            $record = Add-GridRepairJournalRecord -JournalPath $journalPath -TransactionId $transactionId -Sequence $sequence -Operation 'Stage' -State 'Staged' -PreviousRecordSha256 $previous -Detail ([pscustomobject]@{ componentId = $operation.componentId; treeSha256 = $staged.treeSha256 })
            $previous = $record.recordSha256

            $sequence++
            $beforeTreeState = if ($operation.PSObject.Properties['beforeTreeState']) { [string]$operation.beforeTreeState } else { 'Existing' }
            $record = Add-GridRepairJournalRecord -JournalPath $journalPath -TransactionId $transactionId -Sequence $sequence -Operation 'PreserveOriginal' -State 'Intent' -PreviousRecordSha256 $previous -Detail ([pscustomobject]@{ componentId = $operation.componentId; source = $operation.targetDirectory; destination = $operation.rollbackDirectory; beforeTreeState = $beforeTreeState; beforeTreeSha256 = $operation.beforeTreeSha256 })
            $previous = $record.recordSha256
            if ($beforeTreeState -eq 'Existing') {
                Move-Item -LiteralPath ([string]$operation.targetDirectory) -Destination ([string]$operation.rollbackDirectory) -ErrorAction Stop
                $preserved.Add($operation)
                $backup = Get-GridRepairTreeObservation -LiteralPath ([string]$operation.rollbackDirectory)
                if ($backup.treeSha256 -cne [string]$operation.beforeTreeSha256) { throw "RollbackBackupVerificationFailed: $($operation.componentId)" }
                $rollbackManifest = [pscustomobject][ordered]@{ schemaVersion = 1; specificationId = [string]$specification.specificationId; status = 'Available'; entries = @([pscustomobject][ordered]@{ componentId = [string]$operation.componentId; originalPath = [string]$operation.targetDirectory; rollbackPath = [string]$operation.rollbackDirectory; treeSha256 = $backup.treeSha256 }) }
                Write-GridJsonAtomic -InputObject $rollbackManifest -LiteralPath (Join-Path $directory ('rollback-{0:d8}.v1.json' -f [int]$operation.sequence))
                $record = Add-GridRepairJournalRecord -JournalPath $journalPath -TransactionId $transactionId -Sequence $sequence -Operation 'PreserveOriginal' -State 'Available' -PreviousRecordSha256 $previous -Detail ([pscustomobject]@{ componentId = $operation.componentId; treeSha256 = $backup.treeSha256 })
                $previous = $record.recordSha256
            }
            else {
                $record = Add-GridRepairJournalRecord -JournalPath $journalPath -TransactionId $transactionId -Sequence $sequence -Operation 'PreserveOriginal' -State 'NotRequired' -PreviousRecordSha256 $previous -Detail ([pscustomobject]@{ componentId = $operation.componentId; beforeTreeState = 'Absent' })
                $previous = $record.recordSha256
            }

            $sequence++
            $record = Add-GridRepairJournalRecord -JournalPath $journalPath -TransactionId $transactionId -Sequence $sequence -Operation 'PromoteAndVerify' -State 'Intent' -PreviousRecordSha256 $previous -Detail ([pscustomobject]@{ componentId = $operation.componentId; source = $operation.stagingDirectory; destination = $operation.targetDirectory; expectedTreeSha256 = $operation.expectedTreeSha256 })
            $previous = $record.recordSha256
            Move-Item -LiteralPath ([string]$operation.stagingDirectory) -Destination ([string]$operation.targetDirectory) -ErrorAction Stop
            $committed.Add($operation)
            $promoted = Get-GridRepairTreeObservation -LiteralPath ([string]$operation.targetDirectory)
            if ($promoted.treeSha256 -cne [string]$operation.expectedTreeSha256) { throw "PostconditionFailed: $($operation.componentId)" }
            Assert-GridRepairOperationPostconditions -Operation $operation -Root ([string]$operation.targetDirectory) -ValidateProvider
            $record = Add-GridRepairJournalRecord -JournalPath $journalPath -TransactionId $transactionId -Sequence $sequence -Operation 'PromoteAndVerify' -State 'Committed' -PreviousRecordSha256 $previous -Detail ([pscustomobject]@{ componentId = $operation.componentId; treeSha256 = $promoted.treeSha256 })
            $previous = $record.recordSha256
        }
        foreach ($protected in @($specification.protectedState)) {
            if ((Get-GridRepairSha256 -LiteralPath ([string]$protected.path)) -cne [string]$protected.sha256) { throw "ProtectedPostconditionFailed: $($protected.path)" }
        }
        $sequence++
        $record = Add-GridRepairJournalRecord -JournalPath $journalPath -TransactionId $transactionId -Sequence $sequence -Operation 'Finalize' -State 'Completed' -PreviousRecordSha256 $previous -Detail ([pscustomobject]@{ protectedStateVerified = $true; operationCount = @($specification.operations).Count })
        $previous = $record.recordSha256
        $result = New-GridRepairResult -RunLifecycle Completed -RepairOutcome Applied -MutationState Committed -RollbackState Available -RecoveryDisposition None -Summary InputsRepairedAndVerified
    }
    catch {
        $primary = $_.Exception.Message
        $rollbackFailed = $false
        $recoveryOperations = @(@($committed) + @($preserved) | Group-Object sequence | ForEach-Object { $_.Group[0] } | Sort-Object sequence -Descending)
        foreach ($operation in $recoveryOperations) {
            try {
                $sequence++
                $failedPath = ([string]$operation.targetDirectory) + '.failed-' + $transactionId
                $record = Add-GridRepairJournalRecord -JournalPath $journalPath -TransactionId $transactionId -Sequence $sequence -Operation 'Rollback' -State 'Intent' -PreviousRecordSha256 $previous -Detail ([pscustomobject]@{ componentId = $operation.componentId; target = $operation.targetDirectory; rollback = $operation.rollbackDirectory; failedTreePath = $failedPath })
                $previous = $record.recordSha256
                if (Test-Path -LiteralPath ([string]$operation.targetDirectory)) {
                    if (Test-Path -LiteralPath $failedPath) { throw "Failed-tree quarantine already exists: $failedPath" }
                    Move-Item -LiteralPath ([string]$operation.targetDirectory) -Destination $failedPath -ErrorAction Stop
                }
                $beforeTreeState = if ($operation.PSObject.Properties['beforeTreeState']) { [string]$operation.beforeTreeState } else { 'Existing' }
                if ($beforeTreeState -eq 'Existing') {
                    if (-not (Test-Path -LiteralPath ([string]$operation.rollbackDirectory) -PathType Container)) { throw 'Rollback tree is unavailable.' }
                    Move-Item -LiteralPath ([string]$operation.rollbackDirectory) -Destination ([string]$operation.targetDirectory) -ErrorAction Stop
                    $restored = Get-GridRepairTreeObservation -LiteralPath ([string]$operation.targetDirectory)
                    if ($restored.treeSha256 -cne [string]$operation.beforeTreeSha256) { throw 'Restored tree digest differs from the approved before state.' }
                    $restoredHash = $restored.treeSha256
                }
                else {
                    if (Test-Path -LiteralPath ([string]$operation.targetDirectory)) { throw 'Overlay rollback failed to restore the absent target state.' }
                    $restoredHash = ('0' * 64)
                }
                $record = Add-GridRepairJournalRecord -JournalPath $journalPath -TransactionId $transactionId -Sequence $sequence -Operation 'Rollback' -State 'Verified' -PreviousRecordSha256 $previous -Detail ([pscustomobject]@{ componentId = $operation.componentId; treeSha256 = $restoredHash; failedTreePath = $failedPath })
                $previous = $record.recordSha256
            }
            catch { $rollbackFailed = $true; $primary += \" Rollback failure: $($_.Exception.Message)\" }
        }
        $failure = [pscustomobject][ordered]@{ schemaVersion = 1; code = if ($rollbackFailed) { 'RollbackFailed' } else { 'TransactionFailed' }; failedPrimitive = 'StageCommitVerify'; affectedResources = @($specification.operations.targetDirectory); expectedState = 'All exact postconditions verified'; observedState = $primary; lastVerifiedJournalEntry = $previous; currentHashes = @(); boundedRecoveryAttempted = @('Reverse-order rollback of committed operations'); recoveryChoices = if ($rollbackFailed) { @('Use the journal and preserved rollback trees for bounded manual recovery') } else { @('Correct the failed input and materialize a new specification') }; externalStateTrustworthy = (-not $rollbackFailed) }
        if ($rollbackFailed) {
            $result = New-GridRepairResult -RunLifecycle Failed -RepairOutcome Indeterminate -MutationState PartiallyCommitted -RollbackState Failed -RecoveryDisposition ManualRecoveryRequired -Summary ManualRecoveryRequired -Failures @($failure)
        } else {
            $result = New-GridRepairResult -RunLifecycle Failed -RepairOutcome NotApplied -MutationState PartiallyCommitted -RollbackState Verified -RecoveryDisposition None -Summary RepairRolledBack -Failures @($failure)
        }
    }
    $journalHash = Get-GridRepairSha256 -LiteralPath $journalPath
    $receipt = [pscustomobject][ordered]@{ schemaVersion = 2; transactionId = $transactionId; workspaceId=[string]$SemanticBinding.workspaceId; requestId=[string]$SemanticBinding.requestId; submissionId=[string]$SemanticBinding.submissionId; envelopeSha256=[string]$SemanticBinding.envelopeSha256; planSha256=[string]$SemanticBinding.planSha256; authorizationGrantId=$AuthorizationGrantId; authorizationBindingSha256=(Get-GridAuthorizationSemanticDigest -SemanticBinding $SemanticBinding); capabilityId='grid.game.skyrimspecialedition.mod-chain-repair.execute'; capabilityVersion='2.0.0'; adapterId='SkyrimSpecialEdition'; adapterVersion='1'; normalizedInputSha256=[string]$SemanticBinding.normalizedInputSha256; specificationId = [string]$specification.specificationId; specificationSha256 = [string]$specification.specificationSha256; baselineManifestSha256 = [string]$specification.baseline.manifestSha256; journalSha256 = $journalHash; outputSha256=(Get-GridCanonicalJsonSha256 -InputObject $result); evidenceSha256=(Get-GridCanonicalJsonSha256 -InputObject ([pscustomobject][ordered]@{baseline=[string]$specification.baseline.manifestSha256;diagnosis=if($specification.PSObject.Properties['diagnosis']){[string]$specification.diagnosis.manifestSha256}else{$null}})); result = $result; completedAt = [DateTimeOffset]::UtcNow.ToString('o') }
    Write-GridJsonAtomic -InputObject $receipt -LiteralPath $receiptPath
    $terminalGrantResult = if ([string]$result.runLifecycle -eq 'Completed') { 'Consumed' } else { 'Failed' }
    try { Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result $terminalGrantResult -Detail ([string]$result.summary) | Out-Null } catch { if ($terminalGrantResult -eq 'Consumed') { throw } }
    if ($PassThru) { $receipt } else { $receipt | ConvertTo-Json -Depth 30 }
}
