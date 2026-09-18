function Get-GridSkyrimRecordRelationshipFingerprint {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Parts)

    $text = @($Parts | ForEach-Object { [string]$_ }) -join "`n"
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)))).Replace('-', '')
    }
    finally { $sha.Dispose() }
}

function Select-GridSkyrimRecordRelationshipProtectedFiles {
    <#
    .SYNOPSIS
    Selects only state that determines the active plugin record graph.
    .DESCRIPTION
    MO2's application INI contains mutable window/session preferences and may be
    rewritten while MO2 is open. Record relationships are instead bound to the
    profile's mod activation, plugin activation, and load-order files. Every
    plugin source is independently SHA-256-bound by the collector request.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][object[]]$Files)

    $requiredNames = @('modlist.txt', 'plugins.txt', 'loadorder.txt')
    $selected = New-Object Collections.Generic.List[object]
    foreach ($name in $requiredNames) {
        $matches = @($Files | Where-Object { [IO.Path]::GetFileName([string]$_.path) -ieq $name })
        if ($matches.Count -ne 1) { throw "RecordRelationshipProtectedStateInvalid: expected exactly one $name, found $($matches.Count)." }
        $selected.Add($matches[0])
    }
    $parents = @($selected | ForEach-Object { [IO.Path]::GetFullPath((Split-Path -Parent ([string]$_.path))) } | Sort-Object -Unique)
    if ($parents.Count -ne 1) { throw 'RecordRelationshipProtectedStateInvalid: profile state files do not share one directory.' }
    @($selected | Sort-Object { [IO.Path]::GetFileName([string]$_.path) })
}

function Get-GridSkyrimRecordRelationshipProtectedState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$Files,
        [switch]$RequireExpectedHash
    )

    @($Files | Sort-Object path -Unique | ForEach-Object {
        $path = [IO.Path]::GetFullPath([string]$_.path)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "ProtectedInputMissing: $path" }
        $item = Get-Item -LiteralPath $path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "ProtectedInputReparsePoint: $path" }
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($RequireExpectedHash -and [string]::IsNullOrWhiteSpace([string]$_.sha256)) { throw "ProtectedExpectedHashMissing: $path" }
        if ($_.sha256 -and $hash -cne ([string]$_.sha256).ToUpperInvariant()) { throw "BaselineStale: protected hash changed: $path" }
        [pscustomobject][ordered]@{
            path = $item.FullName
            sizeBytes = [long]$item.Length
            lastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('o')
            sha256 = $hash
        }
    })
}

function Invoke-GridSkyrimRecordRelationshipInvestigation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][string]$BaselineCaseDirectory,
        [Parameter(Mandatory)]$CollectorRequest,
        [Parameter(Mandatory)][string]$CaseStoreRoot,
        [string]$DiagnosticsExecutable,
        [switch]$PassThru
    )

    $transaction = $null
    $started = [DateTimeOffset]::UtcNow
    try {
        if ($CaseId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') { throw 'RecordRelationshipCaseIdInvalid.' }
        $baseline = [IO.Path]::GetFullPath($BaselineCaseDirectory)
        $seal = Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory $baseline
        if (-not $seal.IsValid) { throw ('BaselineSealInvalid: ' + (@($seal.Errors) -join ' ')) }

        $baselinePlanPath = Join-Path $baseline 'investigation-plan.json'
        $baselineManifestPath = Join-Path $baseline 'case-manifest.v1.json'
        $baselineNonMutationPath = Join-Path $baseline 'audit\non-mutation.v1.json'
        $baselineProtectedPath = Join-Path $baseline 'snapshots\protected-after.v1.json'
        foreach ($required in @($baselinePlanPath, $baselineManifestPath, $baselineNonMutationPath, $baselineProtectedPath)) {
            if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "BaselineArtifactMissing: $required" }
        }

        $baselinePlan = Get-Content -LiteralPath $baselinePlanPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $baselineProtected = Get-Content -LiteralPath $baselineProtectedPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $protectedFiles = @(Select-GridSkyrimRecordRelationshipProtectedFiles -Files @($baselineProtected.files))
        $protectedBefore = @(Get-GridSkyrimRecordRelationshipProtectedState -Files $protectedFiles -RequireExpectedHash)

        $manifestHash = (Get-FileHash -LiteralPath $baselineManifestPath -Algorithm SHA256).Hash
        $contextFingerprint = Get-GridSkyrimRecordRelationshipFingerprint @(
            [string]$baselinePlan.installationId,
            [string]$baselinePlan.profileId,
            $manifestHash
        )
        $limits = if ($CollectorRequest.PSObject.Properties['limits']) { $CollectorRequest.limits } else { [pscustomobject]@{} }
        $maximumSeconds = if ($limits.PSObject.Properties['maximumWallClockSeconds']) { [int]$limits.maximumWallClockSeconds } else { 1800 }
        if ($maximumSeconds -lt 1 -or $maximumSeconds -gt 14400) { throw 'RootCauseBudgetInvalid: wall clock must be 1..14400 seconds.' }

        $request = [pscustomobject][ordered]@{
            schemaVersion = 1
            caseId = $CaseId
            baselineManifestSha256 = $manifestHash
            installationId = [string]$baselinePlan.installationId
            profileId = [string]$baselinePlan.profileId
            contextFingerprint = $contextFingerprint
            plugins = @($CollectorRequest.plugins)
            targets = @($CollectorRequest.targets)
            cellScope = $CollectorRequest.cellScope
            assetTargets = @($CollectorRequest.assetTargets)
            allowedSources = @($CollectorRequest.allowedSources)
            limits = $limits
        }
        Assert-GridSkyrimRootCauseRequestContract -Request $request

        $transaction = New-GridCaseStoreTransaction -StoreRoot $CaseStoreRoot -CaseId $CaseId
        $runId = 'run-' + [guid]::NewGuid().ToString('N')
        $plan = [pscustomobject][ordered]@{
            schemaVersion = 1
            caseId = $CaseId
            purpose = 'RecordRelationshipInvestigation'
            installationId = [string]$baselinePlan.installationId
            profileId = [string]$baselinePlan.profileId
            baseline = [pscustomobject][ordered]@{ caseDirectory = $baseline; manifestSha256 = $manifestHash }
            capability = [pscustomobject][ordered]@{ capabilityId = 'grid.game.skyrimspecialedition.record-relationship.investigate'; capabilityVersion = '1.0.0' }
            requestedTargets = @($request.targets)
        }
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'case.json' -Value ([pscustomobject][ordered]@{ schemaVersion=1; caseId=$CaseId; purpose='RecordRelationshipInvestigation'; status='Running' }) | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'investigation-plan.json' -Value $plan | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'collector/root-cause-request.v1.json' -Value $request | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'snapshots/protected-before.v1.json' -Value ([pscustomobject]@{ schemaVersion=1; files=$protectedBefore }) | Out-Null

        $requestPath = Join-Path $transaction.CaseDirectory 'collector\root-cause-request.v1.json'
        $resultPath = Join-Path $transaction.CaseDirectory 'collector\root-cause-result.v1.json'
        $collectorArguments = @{ RequestPath=$requestPath; OutputPath=$resultPath; TimeoutSeconds=$maximumSeconds }
        if (-not [string]::IsNullOrWhiteSpace($DiagnosticsExecutable)) { $collectorArguments.DiagnosticsExecutable = $DiagnosticsExecutable }
        $collector = Invoke-GridSkyrimRootCauseCollector @collectorArguments
        Assert-GridSkyrimRootCauseResultContract -Result $collector.Result
        if ([string]$collector.Result.status -notin @('Completed','Partial')) { throw "RootCauseCollectorIncomplete: $($collector.Result.status)" }
        if ([string]$collector.Result.caseId -cne $CaseId -or
            [string]$collector.Result.baselineManifestSha256 -cne $manifestHash -or
            [string]$collector.Result.installationId -cne [string]$baselinePlan.installationId -or
            [string]$collector.Result.profileId -cne [string]$baselinePlan.profileId -or
            [string]$collector.Result.contextFingerprint -cne $contextFingerprint) {
            throw 'RootCauseCollectorBindingMismatch.'
        }
        foreach ($source in @($collector.Result.rawSources)) {
            $matches = @($request.allowedSources | Where-Object {
                [IO.Path]::GetFullPath([string]$_.path) -ieq [IO.Path]::GetFullPath([string]$source.path) -and
                [string]$_.sha256 -ieq [string]$source.sha256
            })
            if ($matches.Count -ne 1) { throw "RootCauseRawSourceUnbound: $($source.path)" }
        }
        foreach ($pair in @(@('maximumRecords','recordsExamined'), @('maximumAssets','assetsExamined'), @('maximumBytes','bytesRead'))) {
            if ($limits.PSObject.Properties[$pair[0]] -and [long]$collector.Result.usage.($pair[1]) -gt [long]$limits.($pair[0])) {
                throw "RootCauseBudgetExceeded: $($pair[1])"
            }
        }

        $protectedAfter = @(Get-GridSkyrimRecordRelationshipProtectedState -Files $protectedBefore -RequireExpectedHash)
        if ($protectedBefore.Count -ne $protectedAfter.Count) { throw 'ProtectedStateChangedDuringRootCauseCollection: file count changed.' }
        for ($index = 0; $index -lt $protectedBefore.Count; $index++) {
            if ($protectedBefore[$index].path -cne $protectedAfter[$index].path -or
                $protectedBefore[$index].sha256 -cne $protectedAfter[$index].sha256 -or
                $protectedBefore[$index].sizeBytes -ne $protectedAfter[$index].sizeBytes) {
                throw "ProtectedStateChangedDuringRootCauseCollection: $($protectedBefore[$index].path)"
            }
        }

        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'collector/root-cause-result.v1.json' -Value $collector.Result | Out-Null
        if (Test-Path -LiteralPath $collector.StandardOutputPath -PathType Leaf) { Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'collector/root-cause-collector.stdout.txt' -SourceLiteralPath $collector.StandardOutputPath | Out-Null }
        if (Test-Path -LiteralPath $collector.StandardErrorPath -PathType Leaf) { Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'collector/root-cause-collector.stderr.txt' -SourceLiteralPath $collector.StandardErrorPath | Out-Null }
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'evidence/plugin-record-graph.v1.json' -Value $collector.Result.recordGraph | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'evidence/asset-graph.v1.json' -Value $collector.Result.assetGraph | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'evidence/collector-issues.v1.json' -Value ([pscustomobject]@{ schemaVersion=1; issues=@($collector.Result.issues) }) | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'snapshots/protected-after.v1.json' -Value ([pscustomobject]@{ schemaVersion=1; files=$protectedAfter }) | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'audit/command-audit.v1.json' -Value ([pscustomobject]@{ schemaVersion=1; command='Grid.Diagnostics root-cause-collect'; processId=$collector.ProcessId; processPath=$collector.ProcessPath; processStartUtc=$collector.ProcessStartUtc; requestPath='collector/root-cause-request.v1.json'; outputPath='collector/root-cause-result.v1.json' }) | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'audit/non-mutation.v1.json' -Value ([pscustomobject][ordered]@{ schemaVersion=1; status='NoChangeObserved'; collectorCommand='Grid.Diagnostics root-cause-collect'; caseStoreWrites=@($transaction.CaseDirectory); externalMutationTargets=@(); externalProcesses=@('Grid.Diagnostics'); toolOrGameLaunches=@(); protectedFileCount=$protectedAfter.Count; baselineNonMutationSha256=(Get-FileHash -LiteralPath $baselineNonMutationPath -Algorithm SHA256).Hash }) | Out-Null

        $semanticFingerprint = Get-GridSkyrimRecordRelationshipFingerprint @(
            $contextFingerprint,
            [string]$collector.Result.recordGraph.semanticFingerprint,
            (@($collector.Result.rawSources | Sort-Object sourceId | ForEach-Object { "$($_.sourceId)|$($_.sha256)" }) -join ';')
        )
        $run = [pscustomobject][ordered]@{
            schemaVersion=1; runId=$runId; caseId=$CaseId; state='Completed'; startedAt=$started.ToString('o'); completedAt=[DateTimeOffset]::UtcNow.ToString('o')
            planFingerprint=$semanticFingerprint; resourcePolicyVersion='grid.record-relationship.v1'; gates=@(); sufficiency=[pscustomobject]@{ schemaVersion=1; status='EvidenceCollectedForNextDeterministicStep' }
            checkpoints=@(); primaryFailure=$null; secondaryFailures=@()
        }
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'case.json' -Value ([pscustomobject][ordered]@{ schemaVersion=1; caseId=$CaseId; purpose='RecordRelationshipInvestigation'; status='RelationshipEvidenceCollected' }) | Out-Null
        Seal-GridCaseStoreRun -Transaction $transaction -Run $run | Out-Null
        $sealed = Seal-GridDiagnosticCase -Transaction $transaction -SemanticBaselineFingerprint $semanticFingerprint -Runs @($run)
        $result = [pscustomobject][ordered]@{ schemaVersion=1; status='RelationshipEvidenceCollected'; caseId=$CaseId; caseDirectory=$sealed.CaseDirectory; manifestSha256=$sealed.Manifest.manifestSha256; contextFingerprint=$contextFingerprint; semanticFingerprint=$semanticFingerprint; recordGraph=$collector.Result.recordGraph; assetGraph=$collector.Result.assetGraph; issues=@($collector.Result.issues); usage=$collector.Result.usage; primaryFailure=$null }
    }
    catch {
        $message = $_.Exception.Message
        $primitive = if ($message -match '^Baseline|^Protected') { 'ValidateSealedBaselineAndProtectedState' } elseif ($message -match '^RootCause') { 'InvokeAndValidateFixedRootCauseCollector' } else { 'SealRecordRelationshipCase' }
        $failure = [pscustomobject][ordered]@{
            code='RecordRelationshipInvestigationFailed'; failedPrimitive=$primitive; detail=$message
            expectedState='Current sealed baseline, bounded SHA-256-bound collector evidence, and unchanged protected state'
            observedState=$message
            boundedRecoveryAttempted=@('Validated exact baseline seal and protected state','Validated collector protocol, source bindings, and resource use','Sealed available failure evidence')
            recovery=@('Correct only the failed primitive and invoke a successor evidence case','Do not infer missing evidence or mutate active state')
        }
        $caseDirectory = $null
        if ($transaction -and [string]$transaction.State -eq 'Open') {
            try {
                Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'case.json' -Value ([pscustomobject][ordered]@{ schemaVersion=1; caseId=$CaseId; purpose='RecordRelationshipInvestigation'; status='RelationshipEvidenceFailed' }) | Out-Null
                Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'audit/failure.v1.json' -Value $failure | Out-Null
                $failureFingerprint = Get-GridSkyrimRecordRelationshipFingerprint @($CaseId, $primitive, $message)
                $failedRun = [pscustomobject][ordered]@{ schemaVersion=1; runId=('run-'+[guid]::NewGuid().ToString('N')); caseId=$CaseId; state='Failed'; startedAt=$started.ToString('o'); completedAt=[DateTimeOffset]::UtcNow.ToString('o'); planFingerprint=$failureFingerprint; resourcePolicyVersion='grid.record-relationship.v1'; gates=@(); sufficiency=[pscustomobject]@{ schemaVersion=1; status='InsufficientForRequestedInvestigation' }; checkpoints=@(); primaryFailure=$failure; secondaryFailures=@() }
                Seal-GridCaseStoreRun -Transaction $transaction -Run $failedRun | Out-Null
                $sealedFailure = Seal-GridDiagnosticCase -Transaction $transaction -SemanticBaselineFingerprint $failureFingerprint -Runs @($failedRun)
                $caseDirectory = $sealedFailure.CaseDirectory
            }
            catch { $caseDirectory = $null }
        }
        $result = [pscustomobject][ordered]@{ schemaVersion=1; status='RelationshipEvidenceFailed'; caseId=$CaseId; caseDirectory=$caseDirectory; recordGraph=$null; assetGraph=$null; issues=@(); usage=$null; primaryFailure=$failure }
    }

    if ($PassThru) { $result } else { $result | ConvertTo-Json -Depth 100 }
}
