#requires -Version 5.1

<#
.SYNOPSIS
Builds and executes one authorized Undo or Redo transition for a verified mod-chain repair.
.DESCRIPTION
Every transition is derived from the immutable repair specification and terminal repair
receipt. It verifies the exact current, rollback, and redo tree hashes before moving
anything, records intent before each rename, verifies the terminal state, and restores
the prior state if any operation fails.
#>

function Get-GridSkyrimModChainHistoryTransition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SpecificationPath,
        [Parameter(Mandatory)][string]$TransactionRoot,
        [Parameter(Mandatory)][ValidateSet('Undo','Redo')][string]$Direction
    )
    $specificationPathFull = [IO.Path]::GetFullPath($SpecificationPath)
    if (-not (Test-Path -LiteralPath $specificationPathFull -PathType Leaf)) { throw "Repair specification is missing: $specificationPathFull" }
    $specification = Get-Content -LiteralPath $specificationPathFull -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    if ((Get-GridRepairSpecificationHash -Specification $specification) -cne [string]$specification.specificationSha256) { throw 'SpecificationDigestMismatch: immutable repair specification has changed.' }
    $transactionRootFull = [IO.Path]::GetFullPath($TransactionRoot)
    $transactionId = 'transaction-' + [string]$specification.specificationSha256
    $transactionDirectory = Join-Path $transactionRootFull $transactionId
    $receiptPath = Join-Path $transactionDirectory 'receipt.v2.json'
    $journalPath = Join-Path $transactionDirectory 'journal.v1.ndjson'
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { throw 'RepairReceiptMissing: a verified terminal repair receipt is required.' }
    $receipt = Get-Content -LiteralPath $receiptPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    if ([int]$receipt.schemaVersion -ne 2 -or [string]$receipt.transactionId -cne $transactionId -or
        [string]$receipt.specificationSha256 -cne [string]$specification.specificationSha256 -or
        [string]$receipt.result.runLifecycle -cne 'Completed' -or [string]$receipt.result.repairOutcome -cne 'Applied') {
        throw 'RepairReceiptInvalid: only a completed verified repair can enter history.'
    }
    if (-not (Test-Path -LiteralPath $journalPath -PathType Leaf) -or (Get-GridRepairSha256 -LiteralPath $journalPath) -cne [string]$receipt.journalSha256) {
        throw 'RepairReceiptJournalMismatch: the original repair journal is missing or changed.'
    }
    $statePath = Join-Path $transactionDirectory 'history-state.v1.json'
    $state = if (Test-Path -LiteralPath $statePath -PathType Leaf) {
        Get-Content -LiteralPath $statePath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    } else {
        [pscustomobject][ordered]@{ schemaVersion = 1; transactionId = $transactionId; specificationSha256 = [string]$specification.specificationSha256; state = 'Applied'; revision = 0; lastReceiptPath = $receiptPath }
    }
    if ([int]$state.schemaVersion -ne 1 -or [string]$state.transactionId -cne $transactionId -or [string]$state.specificationSha256 -cne [string]$specification.specificationSha256) {
        throw 'RepairHistoryStateInvalid: persisted history identity differs from the repair.'
    }
    if ([string]$state.state -ceq 'Transitioning') {
        throw 'RepairHistoryTransitionIncomplete: a prior history transition did not reach a sealed terminal state; deterministic recovery is required before another transition.'
    }
    if ([int]$state.revision -gt 0) {
        $lastReceiptPath = [string]$state.lastReceiptPath
        if ([string]::IsNullOrWhiteSpace($lastReceiptPath) -or -not (Test-Path -LiteralPath $lastReceiptPath -PathType Leaf)) {
            throw 'RepairHistoryStateInvalid: the terminal history receipt is missing.'
        }
        $lastReceipt = Get-Content -LiteralPath $lastReceiptPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        if ([int]$lastReceipt.schemaVersion -ne 1 -or [string]$lastReceipt.status -cne 'Completed' -or
            [string]$lastReceipt.state -cne [string]$state.state -or [int]$lastReceipt.revision -ne [int]$state.revision) {
            throw 'RepairHistoryStateInvalid: the terminal history receipt does not match persisted history state.'
        }
    }
    $expectedState = if ($Direction -eq 'Undo') { 'Applied' } else { 'Undone' }
    if ([string]$state.state -cne $expectedState) { throw "RepairHistoryDirectionInvalid: $Direction requires state '$expectedState', observed '$($state.state)'." }
    $revision = [int]$state.revision + 1
    $targets = @($specification.operations | ForEach-Object { [IO.Path]::GetFullPath([string]$_.targetDirectory) } | Sort-Object -Unique)
    $normalizedInput = [pscustomobject][ordered]@{
        schemaVersion = 1; transactionId = $transactionId; specificationId = [string]$specification.specificationId
        specificationSha256 = [string]$specification.specificationSha256; direction = $Direction
        fromState = [string]$state.state; revision = $revision; targets = $targets
    }
    $digest = Get-GridCanonicalJsonSha256 -InputObject $normalizedInput
    [pscustomobject][ordered]@{
        schemaVersion = 1; transitionId = "history-$($specification.specificationId)-$($Direction.ToLowerInvariant())-$revision"
        transitionSha256 = $digest; normalizedInput = $normalizedInput; targets = $targets
        state = $state; statePath = $statePath; transactionDirectory = $transactionDirectory
        receipt = $receipt; receiptPath = $receiptPath; specification = $specification; specificationPath = $specificationPathFull
    }
}

function Test-GridSkyrimModChainHistoryProcessGate {
    param([Parameter(Mandatory)]$Specification)
    $targetInstanceRoots = @($Specification.operations | ForEach-Object {
        $modsRoot = Split-Path -Parent ([IO.Path]::GetFullPath([string]$_.targetDirectory))
        [IO.Path]::GetFullPath((Split-Path -Parent $modsRoot)).TrimEnd('\') + '\'
    } | Sort-Object -Unique)
    $targetGameRoots = @()
    $installationPath = Join-Path ([string]$Specification.baseline.caseDirectory) 'installation\installation-baseline.v1.json'
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
    if ($running.Count) { throw ('ProcessGateRefused: close relevant applications before repair history mutation: ' + (@($running.ProcessName | Sort-Object -Unique) -join ', ')) }
}

function Invoke-GridAuthorizedModChainHistory {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory)][string]$SpecificationPath,
        [Parameter(Mandatory)][string]$TransactionRoot,
        [Parameter(Mandatory)][ValidateSet('Undo','Redo')][string]$Direction,
        [Parameter(Mandatory)][string]$AuthorizationGrantId,
        [Parameter(Mandatory)][string]$AuthorizationSecret,
        [Parameter(Mandatory)][string]$AuthorizationStoreRoot,
        [Parameter(Mandatory)]$SemanticBinding,
        [Parameter(Mandatory)][string]$CaseStoreRoot,
        [switch]$PassThru
    )
    $transition = Get-GridSkyrimModChainHistoryTransition -SpecificationPath $SpecificationPath -TransactionRoot $TransactionRoot -Direction $Direction
    $specification = $transition.specification
    if ([string]$SemanticBinding.proposalOrSpecificationId -cne [string]$transition.transitionId -or
        [string]$SemanticBinding.proposalOrSpecificationSha256 -cne [string]$transition.transitionSha256 -or
        [string]$SemanticBinding.normalizedInputSha256 -cne [string]$transition.transitionSha256) {
        throw 'AuthorizationBindingMismatch: repair history transition identity changed.'
    }
    if (@($SemanticBinding.capabilities | Where-Object { [string]$_.capabilityId -ceq 'grid.game.skyrimspecialedition.mod-chain-repair.history.execute' -and [string]$_.capabilityVersion -ceq '1.0.0' }).Count -ne 1) {
        throw 'AuthorizationBindingMismatch: repair history capability/version is not authorized.'
    }
    if ((@($SemanticBinding.targets | Sort-Object -Unique) -join "`n") -cne (@($transition.targets) -join "`n")) { throw 'AuthorizationBindingMismatch: exact history targets changed.' }
    Test-GridAuthorizationGrantPreflight -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -AuthorizationSecret $AuthorizationSecret -ExpectedSemanticBinding $SemanticBinding | Out-Null
    $seal = Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory ([string]$specification.baseline.caseDirectory)
    if (-not $seal.IsValid -or [string]$seal.Manifest.manifestSha256 -cne [string]$specification.baseline.manifestSha256) { throw 'BaselineSealInvalid: the bound baseline is absent, changed, or invalid.' }
    foreach ($protected in @($specification.protectedState)) {
        $path = Test-GridRepairPathBoundary -LiteralPath ([string]$protected.path) -Role ReadRoot -MustExist
        if ((Get-GridRepairSha256 -LiteralPath $path) -cne [string]$protected.sha256) { throw "ProtectedHashChanged: $path" }
    }

    $redoSuffix = '.grid-redo-' + ([string]$specification.specificationSha256).Substring(0, 16).ToLowerInvariant()
    foreach ($operation in @($specification.operations)) {
        $target = [IO.Path]::GetFullPath([string]$operation.targetDirectory)
        $rollback = [IO.Path]::GetFullPath([string]$operation.rollbackDirectory)
        $redo = $rollback + $redoSuffix
        [void](Test-GridRepairPathBoundary -LiteralPath $target -Role MutationRoot)
        [void](Test-GridRepairPathBoundary -LiteralPath $rollback -Role MutationRoot)
        [void](Test-GridRepairPathBoundary -LiteralPath $redo -Role MutationRoot)
        if ([IO.Path]::GetPathRoot($target) -ine [IO.Path]::GetPathRoot($rollback) -or [IO.Path]::GetPathRoot($target) -ine [IO.Path]::GetPathRoot($redo)) { throw 'CrossVolumeMutationRefused: history trees must share the target volume.' }
        $beforeState = if ($operation.PSObject.Properties['beforeTreeState']) { [string]$operation.beforeTreeState } else { 'Existing' }
        if ($Direction -eq 'Undo') {
            if (-not (Test-Path -LiteralPath $target -PathType Container) -or (Get-GridRepairTreeObservation -LiteralPath $target).treeSha256 -cne [string]$operation.expectedTreeSha256) { throw "RepairHistoryTargetDrift: $target" }
            if (Test-Path -LiteralPath $redo) { throw "RepairHistoryRedoCollision: $redo" }
            if ($beforeState -eq 'Existing' -and (-not (Test-Path -LiteralPath $rollback -PathType Container) -or (Get-GridRepairTreeObservation -LiteralPath $rollback).treeSha256 -cne [string]$operation.beforeTreeSha256)) { throw "RepairHistoryRollbackDrift: $rollback" }
        } else {
            if (-not (Test-Path -LiteralPath $redo -PathType Container) -or (Get-GridRepairTreeObservation -LiteralPath $redo).treeSha256 -cne [string]$operation.expectedTreeSha256) { throw "RepairHistoryRedoDrift: $redo" }
            if (Test-Path -LiteralPath $rollback) { throw "RepairHistoryRollbackCollision: $rollback" }
            if ($beforeState -eq 'Existing') {
                if (-not (Test-Path -LiteralPath $target -PathType Container) -or (Get-GridRepairTreeObservation -LiteralPath $target).treeSha256 -cne [string]$operation.beforeTreeSha256) { throw "RepairHistoryTargetDrift: $target" }
            } elseif (Test-Path -LiteralPath $target) { throw "RepairHistoryExpectedAbsent: $target" }
        }
    }
    Test-GridSkyrimModChainHistoryProcessGate -Specification $specification
    $lease = Enter-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -AuthorizationSecret $AuthorizationSecret -ExpectedSemanticBinding $SemanticBinding -ConsumerId ([string]$transition.transitionId)
    if ($WhatIfPreference -or -not $PSCmdlet.ShouldProcess(([string]$transition.transitionId), "$Direction exact repair trees and verify the resulting state")) {
        Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Consumed -Detail 'WhatIf/ShouldProcess declined; authorization consumed without mutation.' | Out-Null
        $result = [pscustomobject][ordered]@{ schemaVersion = 1; status = 'Planned'; direction = $Direction; transitionId = $transition.transitionId; state = $transition.state.state; revision = $transition.state.revision }
        if ($PassThru) { return $result } else { return ($result | ConvertTo-Json -Depth 20) }
    }

    $revision = [int]$transition.normalizedInput.revision
    $journalPath = Join-Path ([string]$transition.transactionDirectory) ("history-$revision.v1.ndjson")
    $receiptPath = Join-Path ([string]$transition.transactionDirectory) ("history-receipt-$revision.v1.json")
    if ((Test-Path -LiteralPath $journalPath) -or (Test-Path -LiteralPath $receiptPath)) { throw 'RepairHistoryRevisionCollision: transition artifacts already exist.' }
    $stream = New-Object IO.FileStream($journalPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read, 4096, [IO.FileOptions]::WriteThrough)
    $stream.Dispose()
    $sequence = 0; $previous = $null
    $priorState = $transition.state
    $intentState = [pscustomobject][ordered]@{
        schemaVersion=1; transactionId=$transition.normalizedInput.transactionId; specificationSha256=$transition.normalizedInput.specificationSha256
        state='Transitioning'; revision=$revision; direction=$Direction; previousState=[string]$priorState.state
        previousRevision=[int]$priorState.revision; pendingReceiptPath=$receiptPath; lastReceiptPath=[string]$priorState.lastReceiptPath
    }
    Write-GridJsonAtomic -InputObject $intentState -LiteralPath ([string]$transition.statePath)
    try {
        $record = Add-GridRepairJournalRecord -JournalPath $journalPath -TransactionId ([string]$transition.transitionId) -Sequence $sequence -Operation $Direction -State 'Authorized' -PreviousRecordSha256 $previous -Detail $transition.normalizedInput
        $previous = $record.recordSha256
        $orderedOperations = if ($Direction -eq 'Undo') { @($specification.operations | Sort-Object sequence -Descending) } else { @($specification.operations | Sort-Object sequence) }
        foreach ($operation in $orderedOperations) {
            $sequence++
            $target = [IO.Path]::GetFullPath([string]$operation.targetDirectory); $rollback = [IO.Path]::GetFullPath([string]$operation.rollbackDirectory); $redo = $rollback + $redoSuffix
            $beforeState = if ($operation.PSObject.Properties['beforeTreeState']) { [string]$operation.beforeTreeState } else { 'Existing' }
            $record = Add-GridRepairJournalRecord -JournalPath $journalPath -TransactionId ([string]$transition.transitionId) -Sequence $sequence -Operation $Direction -State 'Intent' -PreviousRecordSha256 $previous -Detail ([pscustomobject]@{ componentId=$operation.componentId; target=$target; rollback=$rollback; redo=$redo })
            $previous = $record.recordSha256
            if ($Direction -eq 'Undo') {
                Move-Item -LiteralPath $target -Destination $redo -ErrorAction Stop
                if ($beforeState -eq 'Existing') { Move-Item -LiteralPath $rollback -Destination $target -ErrorAction Stop }
            } else {
                if ($beforeState -eq 'Existing') { Move-Item -LiteralPath $target -Destination $rollback -ErrorAction Stop }
                Move-Item -LiteralPath $redo -Destination $target -ErrorAction Stop
            }
            $observed = if (Test-Path -LiteralPath $target -PathType Container) { (Get-GridRepairTreeObservation -LiteralPath $target).treeSha256 } else { ('0' * 64) }
            $expected = if ($Direction -eq 'Undo') { [string]$operation.beforeTreeSha256 } else { [string]$operation.expectedTreeSha256 }
            if ($observed -cne $expected) { throw "RepairHistoryPostconditionFailed: $($operation.componentId)" }
            $record = Add-GridRepairJournalRecord -JournalPath $journalPath -TransactionId ([string]$transition.transitionId) -Sequence $sequence -Operation $Direction -State 'Verified' -PreviousRecordSha256 $previous -Detail ([pscustomobject]@{ componentId=$operation.componentId; treeSha256=$observed })
            $previous = $record.recordSha256
        }
        foreach ($protected in @($specification.protectedState)) { if ((Get-GridRepairSha256 -LiteralPath ([string]$protected.path)) -cne [string]$protected.sha256) { throw "ProtectedPostconditionFailed: $($protected.path)" } }
    }
    catch {
        $failure = $_.Exception.Message; $recoveryFailed = $false
        foreach ($operation in @($specification.operations | Sort-Object sequence -Descending)) {
            try {
                $target = [IO.Path]::GetFullPath([string]$operation.targetDirectory); $rollback = [IO.Path]::GetFullPath([string]$operation.rollbackDirectory); $redo = $rollback + $redoSuffix
                $beforeState = if ($operation.PSObject.Properties['beforeTreeState']) { [string]$operation.beforeTreeState } else { 'Existing' }
                if ($Direction -eq 'Undo') {
                    if (Test-Path -LiteralPath $redo -PathType Container) {
                        if ($beforeState -eq 'Existing' -and (Test-Path -LiteralPath $target -PathType Container) -and -not (Test-Path -LiteralPath $rollback)) { Move-Item -LiteralPath $target -Destination $rollback -ErrorAction Stop }
                        if (-not (Test-Path -LiteralPath $target)) { Move-Item -LiteralPath $redo -Destination $target -ErrorAction Stop }
                    }
                } else {
                    if ((Test-Path -LiteralPath $target -PathType Container) -and -not (Test-Path -LiteralPath $redo)) {
                        $targetHash = (Get-GridRepairTreeObservation -LiteralPath $target).treeSha256
                        if ($targetHash -ceq [string]$operation.expectedTreeSha256) { Move-Item -LiteralPath $target -Destination $redo -ErrorAction Stop }
                    }
                    if ($beforeState -eq 'Existing' -and (Test-Path -LiteralPath $rollback -PathType Container) -and -not (Test-Path -LiteralPath $target)) { Move-Item -LiteralPath $rollback -Destination $target -ErrorAction Stop }
                }
            } catch { $recoveryFailed = $true; $failure += " Recovery failure: $($_.Exception.Message)" }
        }
        if (-not $recoveryFailed) { Write-GridJsonAtomic -InputObject $priorState -LiteralPath ([string]$transition.statePath) }
        Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Failed -Detail $failure | Out-Null
        if ($recoveryFailed) { throw "RepairHistoryIndeterminate: $failure" }
        throw "RepairHistoryRolledBack: $failure"
    }
    $newState = if ($Direction -eq 'Undo') { 'Undone' } else { 'Applied' }
    $result = [pscustomobject][ordered]@{ schemaVersion=1; status='Completed'; direction=$Direction; transitionId=$transition.transitionId; transitionSha256=$transition.transitionSha256; state=$newState; revision=$revision; journalSha256=(Get-GridRepairSha256 -LiteralPath $journalPath); completedAt=[DateTimeOffset]::UtcNow.ToString('o') }
    Write-GridJsonAtomic -InputObject $result -LiteralPath $receiptPath
    $stateRecord = [pscustomobject][ordered]@{ schemaVersion=1; transactionId=$transition.normalizedInput.transactionId; specificationSha256=$transition.normalizedInput.specificationSha256; state=$newState; revision=$revision; lastReceiptPath=$receiptPath }
    Write-GridJsonAtomic -InputObject $stateRecord -LiteralPath ([string]$transition.statePath)
    Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Consumed -Detail "$Direction completed and verified." | Out-Null
    if ($PassThru) { $result } else { $result | ConvertTo-Json -Depth 20 }
}
