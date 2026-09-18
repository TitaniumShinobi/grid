function Invoke-GridSkyrimRootCauseDiagnosis {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][string]$BaselineCaseDirectory,
        [Parameter(Mandatory)]$InvestigationPlan,
        [Parameter(Mandatory)]$CollectorRequest,
        [Parameter(Mandatory)][string]$CaseStoreRoot,
        [switch]$PassThru
    )
    $transaction = $null; $run = $null; $started = [DateTimeOffset]::UtcNow; $failure = $null
    try {
        if ($CaseId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') { throw 'RootCauseCaseIdInvalid.' }
        $baseline = [IO.Path]::GetFullPath($BaselineCaseDirectory)
        $seal = Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory $baseline
        if (-not $seal.IsValid) { throw ('BaselineSealInvalid: ' + ($seal.Errors -join ' ')) }
        if ([int]$InvestigationPlan.schemaVersion -ne 2 -or [string]$InvestigationPlan.purpose -ne 'Diagnosis') { throw 'RootCausePlanInvalid: a v2 Diagnosis plan is required.' }
        $binding = @($InvestigationPlan.capabilities | Where-Object { [string]$_.capabilityId -eq 'grid.game.skyrimspecialedition.root-cause.resolve' })
        if ($binding.Count -ne 1 -or [string]$binding[0].capabilityVersion -ne '1.0.0') { throw 'RootCausePlanCapabilityMissingOrStale.' }
        $baselinePlanPath = Join-Path $baseline 'investigation-plan.json'
        if (-not (Test-Path -LiteralPath $baselinePlanPath -PathType Leaf)) { throw 'BaselinePlanMissing.' }
        $baselinePlan = Get-Content -LiteralPath $baselinePlanPath -Raw | ConvertFrom-Json
        foreach ($name in @('installationId','profileId')) {
            if ([string]$InvestigationPlan.$name -ne [string]$baselinePlan.$name) { throw "RootCauseContextMismatch: $name" }
        }
        $nonMutationPath = Join-Path $baseline 'audit\non-mutation.v1.json'
        if (-not (Test-Path -LiteralPath $nonMutationPath -PathType Leaf)) { throw 'BaselineNonMutationEvidenceMissing.' }
        $protectedPath = Join-Path $baseline 'snapshots\protected-after.v1.json'
        if (-not (Test-Path -LiteralPath $protectedPath -PathType Leaf)) { throw 'BaselineProtectedSnapshotMissing.' }
        $protectedSnapshot = Get-Content -LiteralPath $protectedPath -Raw | ConvertFrom-Json
        $protectedFiles = @($protectedSnapshot.files)
        $profileFiles = @($protectedFiles | Where-Object { [IO.Path]::GetFileName([string]$_.path) -ieq 'settings.ini' })
        if ($profileFiles.Count -eq 1) {
            $iniTweaks = Join-Path (Split-Path -Parent ([string]$profileFiles[0].path)) 'initweaks.ini'
            if (Test-Path -LiteralPath $iniTweaks -PathType Leaf) { $protectedFiles += [pscustomobject]@{ path=$iniTweaks; sha256=(Get-FileHash -LiteralPath $iniTweaks -Algorithm SHA256).Hash } }
        }
        $protectedBefore = @($protectedFiles | Sort-Object path -Unique | ForEach-Object {
            if (-not (Test-Path -LiteralPath ([string]$_.path) -PathType Leaf)) { throw "ProtectedInputMissing: $($_.path)" }
            $currentHash = (Get-FileHash -LiteralPath ([string]$_.path) -Algorithm SHA256).Hash
            if ($_.sha256 -and $currentHash -cne [string]$_.sha256) { throw "BaselineStale: protected hash changed: $($_.path)" }
            $i=Get-Item -LiteralPath ([string]$_.path); [pscustomobject][ordered]@{ path=$i.FullName; sizeBytes=[long]$i.Length; lastWriteTimeUtc=$i.LastWriteTimeUtc.ToString('o'); sha256=$currentHash }
        })
        $transaction = New-GridCaseStoreTransaction -StoreRoot $CaseStoreRoot -CaseId $CaseId
        $runId = 'run-' + [guid]::NewGuid().ToString('N')
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'case.json' -Value ([pscustomobject][ordered]@{ schemaVersion=1; caseId=$CaseId; purpose='RootCauseDiagnosis'; status='Running' }) | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'investigation-plan.json' -Value $InvestigationPlan | Out-Null
        $manifestPath = Join-Path $baseline 'case-manifest.v1.json'; $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
        $limits = if ($CollectorRequest.PSObject.Properties['limits']) { $CollectorRequest.limits } else { [pscustomobject]@{} }
        $maximumSeconds = if ($limits.PSObject.Properties['maximumWallClockSeconds']) { [int]$limits.maximumWallClockSeconds } else { 1800 }
        if ($maximumSeconds -lt 1 -or $maximumSeconds -gt 14400) { throw 'RootCauseBudgetInvalid: wall clock must be 1..14400 seconds.' }
        $contextSource = ([string]$InvestigationPlan.installationId) + '|' + ([string]$InvestigationPlan.profileId) + '|' + $manifestHash
        $sha = [Security.Cryptography.SHA256]::Create(); try { $contextFingerprint=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($contextSource)))).Replace('-','') } finally { $sha.Dispose() }
        $request = [pscustomobject][ordered]@{
            schemaVersion=1; caseId=$CaseId; baselineManifestSha256=$manifestHash; installationId=[string]$InvestigationPlan.installationId
            profileId=[string]$InvestigationPlan.profileId; contextFingerprint=$contextFingerprint
            plugins=@($CollectorRequest.plugins); targets=@($CollectorRequest.targets); cellScope=$CollectorRequest.cellScope
            assetTargets=@($CollectorRequest.assetTargets); allowedSources=@($CollectorRequest.allowedSources); limits=$limits
        }
        Assert-GridSkyrimRootCauseRequestContract -Request $request
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'collector/root-cause-request.v1.json' -Value $request | Out-Null
        $requestPath = Join-Path $transaction.CaseDirectory 'collector\root-cause-request.v1.json'; $resultPath = Join-Path $transaction.CaseDirectory 'collector\root-cause-result.v1.json'
        $collector = Invoke-GridSkyrimRootCauseCollector -RequestPath $requestPath -OutputPath $resultPath -TimeoutSeconds $maximumSeconds
        Assert-GridSkyrimRootCauseResultContract -Result $collector.Result
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'collector/root-cause-result.v1.json' -Value $collector.Result | Out-Null
        if (Test-Path -LiteralPath $collector.StandardOutputPath -PathType Leaf) { Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'collector/root-cause-collector.stdout.txt' -SourceLiteralPath $collector.StandardOutputPath | Out-Null }
        if (Test-Path -LiteralPath $collector.StandardErrorPath -PathType Leaf) { Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'collector/root-cause-collector.stderr.txt' -SourceLiteralPath $collector.StandardErrorPath | Out-Null }
        if ([string]$collector.Result.caseId -ne $CaseId -or [string]$collector.Result.baselineManifestSha256 -cne $manifestHash -or [string]$collector.Result.contextFingerprint -cne $contextFingerprint) { throw 'RootCauseCollectorBindingMismatch.' }
        foreach ($source in @($collector.Result.rawSources)) {
            $match = @($request.allowedSources | Where-Object { [IO.Path]::GetFullPath([string]$_.path) -ieq [IO.Path]::GetFullPath([string]$source.path) -and [string]$_.sha256 -ieq [string]$source.sha256 })
            if ($match.Count -ne 1) { throw "RootCauseRawSourceUnbound: $($source.path)" }
        }
        foreach ($pair in @(@('maximumRecords','recordsExamined'),@('maximumAssets','assetsExamined'),@('maximumBytes','bytesRead'))) {
            if ($limits.PSObject.Properties[$pair[0]] -and [long]$collector.Result.usage.($pair[1]) -gt [long]$limits.($pair[0])) { throw "RootCauseBudgetExceeded: $($pair[1])" }
        }
        $protectedAfter = @($protectedBefore | ForEach-Object { $i=Get-Item -LiteralPath $_.path; [pscustomobject][ordered]@{ path=$i.FullName; sizeBytes=[long]$i.Length; lastWriteTimeUtc=$i.LastWriteTimeUtc.ToString('o'); sha256=(Get-FileHash -LiteralPath $i.FullName -Algorithm SHA256).Hash } })
        for ($index=0; $index -lt $protectedBefore.Count; $index++) { if ($protectedBefore[$index].sha256 -cne $protectedAfter[$index].sha256 -or $protectedBefore[$index].sizeBytes -ne $protectedAfter[$index].sizeBytes) { throw "ProtectedStateChangedDuringRootCauseCollection: $($protectedBefore[$index].path)" } }
        $resolved = Resolve-GridSkyrimRootCause -CaseId $CaseId -CollectorResult $collector.Result
        Assert-GridSkyrimPatchSpecificationContract -Specification $resolved.PatchSpecification
        $chains = @($collector.Result.recordGraph.chains)
        $enableNodes = @($chains | Where-Object {
            $_.winner -and ($_.winner.enableParent -or @($_.winner.linkedReferences).Count -gt 0 -or $_.winner.isInitiallyDisabled -or $_.winner.isDeleted)
        } | ForEach-Object { [pscustomobject][ordered]@{
            target=$_.target; winnerPlugin=$_.winner.pluginName; enableParent=$_.winner.enableParent
            linkedReferences=@($_.winner.linkedReferences); initiallyDisabled=[bool]$_.winner.isInitiallyDisabled; deleted=[bool]$_.winner.isDeleted
        } })
        $inactiveChains = @($chains | Where-Object { @($_.records | Where-Object { -not $_.isEnabled }).Count -gt 0 })
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'evidence/plugin-record-graph.v1.json' -Value $collector.Result.recordGraph | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'evidence/cell-scope.v1.json' -Value ([pscustomobject][ordered]@{ schemaVersion=1; status='Complete'; cells=@($collector.Result.recordGraph.cellScopeDiscoveries); limits=$request.cellScope }) | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'evidence/override-chains.v1.json' -Value ([pscustomobject][ordered]@{ schemaVersion=1; status='Complete'; chains=$chains }) | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'evidence/enable-state-graph.v1.json' -Value ([pscustomobject][ordered]@{ schemaVersion=1; status='Complete'; nodes=$enableNodes }) | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'evidence/inactive-comparison.v1.json' -Value ([pscustomobject][ordered]@{ schemaVersion=1; status='ComparativeOnly'; chains=$inactiveChains }) | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'evidence/asset-graph.v1.json' -Value $collector.Result.assetGraph | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'evidence/generated-output.v1.json' -Value ([pscustomobject][ordered]@{ schemaVersion=1; status='NotApplicable'; reason='No exact affected record or virtual asset target resolved to a generated-output provider.' }) | Out-Null
        if ($CollectorRequest.PSObject.Properties['remoteDocuments']) {
            Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'provenance/remote-documents.v1.json' -Value ([pscustomobject][ordered]@{ schemaVersion=1; documents=@($CollectorRequest.remoteDocuments) }) | Out-Null
        }
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'evidence/root-cause-evidence.v1.json' -Value $resolved.Evidence | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'diagnosis/diagnostic-result.v1.json' -Value $resolved.DiagnosticResult | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'diagnosis/diagnostic-result.txt' -Text (ConvertTo-GridDiagnosticResultText -DiagnosticResult $resolved.DiagnosticResult -Evidence $resolved.Evidence -RemediationProposals @($resolved.RemediationProposal)) | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'patch/conflict-patch-specification.v1.json' -Value $resolved.PatchSpecification | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'patch/remediation-proposal.v1.json' -Value $resolved.RemediationProposal | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'snapshots/protected-before.v1.json' -Value ([pscustomobject]@{ schemaVersion=1; files=$protectedBefore }) | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'snapshots/protected-after.v1.json' -Value ([pscustomobject]@{ schemaVersion=1; files=$protectedAfter }) | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'audit/command-audit.v1.json' -Value ([pscustomobject]@{ schemaVersion=1; command='Grid.Diagnostics root-cause-collect'; processId=$collector.ProcessId; processPath=$collector.ProcessPath; processStartUtc=$collector.ProcessStartUtc; requestPath='collector/root-cause-request.v1.json'; outputPath='collector/root-cause-result.v1.json' }) | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'audit/non-mutation.v1.json' -Value ([pscustomobject][ordered]@{ schemaVersion=1; status='NoChangeObserved'; collectorCommand='Grid.Diagnostics root-cause-collect'; caseStoreWrites=@($transaction.CaseDirectory); externalMutationTargets=@(); externalProcesses=@('Grid.Diagnostics'); toolOrGameLaunches=@(); protectedFileCount=$protectedAfter.Count; baselineNonMutationSha256=(Get-FileHash -LiteralPath $nonMutationPath -Algorithm SHA256).Hash }) | Out-Null
        $run = [pscustomobject][ordered]@{ schemaVersion=1; runId=$runId; caseId=$CaseId; state='Completed'; startedAt=$started.ToString('o'); completedAt=[DateTimeOffset]::UtcNow.ToString('o'); planFingerprint=$resolved.DiagnosticResult.resultFingerprint; resourcePolicyVersion='grid.root-cause.v1'; gates=@(); sufficiency=[pscustomobject]@{ schemaVersion=1; status='SufficientForRequestedDiagnosis' }; checkpoints=@(); primaryFailure=$null; secondaryFailures=@() }
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'case.json' -Value ([pscustomobject][ordered]@{ schemaVersion=1; caseId=$CaseId; purpose='RootCauseDiagnosis'; status='RootCauseResolved' }) | Out-Null
        Seal-GridCaseStoreRun -Transaction $transaction -Run $run | Out-Null
        $sealed = Seal-GridDiagnosticCase -Transaction $transaction -SemanticBaselineFingerprint $resolved.DiagnosticResult.resultFingerprint -Runs @($run)
        $result = [pscustomobject][ordered]@{ schemaVersion=1; status='RootCauseResolved'; caseId=$CaseId; caseDirectory=$sealed.CaseDirectory; manifestSha256=$sealed.Manifest.manifestSha256; diagnosticResult=$resolved.DiagnosticResult; patchSpecification=$resolved.PatchSpecification; remediationProposal=$resolved.RemediationProposal; primaryFailure=$null }
    }
    catch {
        $message = $_.Exception.Message
        $primitive = if ($message -match '^Baseline|^Protected') { 'ValidateSealedBaselineAndProtectedState' } elseif ($message -match '^RootCauseCollector|^RootCauseRawSource|^RootCauseBudget') { 'InvokeAndValidateFixedRootCauseCollector' } elseif ($message -match '^RootCause|^Patch') { 'ResolveEvidenceAndMaterializeInertPatchSpecification' } else { 'SealRootCauseCase' }
        $failure = [pscustomobject][ordered]@{ code='RootCauseFailed'; failedPrimitive=$primitive; detail=$message; expectedState='Current sealed baseline, bounded SHA-256-bound collector result, four-field diagnosis, and inert patch specification'; observedState=$message; boundedRecoveryAttempted=@('Validated exact baseline seal and capability binding','Validated collector protocol, context, resource use, and source hashes','Sealed available failure evidence'); recovery=@('Correct only the failed primitive and invoke a successor case','Do not infer missing evidence or mutate active state') }
        if ($transaction -and [string]$transaction.State -eq 'Open') {
            try {
                Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'case.json' -Value ([pscustomobject][ordered]@{ schemaVersion=1; caseId=$CaseId; purpose='RootCauseDiagnosis'; status='RootCauseFailed' }) | Out-Null
                Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'audit/failure.v1.json' -Value $failure | Out-Null
                $failedRun = [pscustomobject][ordered]@{ schemaVersion=1; runId=('run-'+[guid]::NewGuid().ToString('N')); caseId=$CaseId; state='Failed'; startedAt=$started.ToString('o'); completedAt=[DateTimeOffset]::UtcNow.ToString('o'); planFingerprint=('0'*64); resourcePolicyVersion='grid.root-cause.v1'; gates=@(); sufficiency=[pscustomobject]@{ schemaVersion=1; status='InsufficientForRequestedDiagnosis' }; checkpoints=@(); primaryFailure=$failure; secondaryFailures=@() }
                Seal-GridCaseStoreRun -Transaction $transaction -Run $failedRun | Out-Null
                $sealed = Seal-GridDiagnosticCase -Transaction $transaction -SemanticBaselineFingerprint ('0'*64) -Runs @($failedRun)
                $caseDirectory = $sealed.CaseDirectory
            } catch { $caseDirectory = $null }
        } else { $caseDirectory = $null }
        $result = [pscustomobject][ordered]@{ schemaVersion=1; status='RootCauseFailed'; caseId=$CaseId; caseDirectory=$caseDirectory; diagnosticResult=$null; patchSpecification=$null; remediationProposal=$null; primaryFailure=$failure }
    }
    if ($PassThru) { $result } else { $result | ConvertTo-Json -Depth 50 }
}
