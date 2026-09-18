$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent $PSScriptRoot
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $gameRoot '..\..\..'))
. (Join-Path $repositoryRoot 'scripts\health\Grid.CaseStore.ps1')
. (Join-Path $gameRoot 'health\collectors\Invoke-GridSkyrimBaseline.ps1')

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }
function Assert-Throws([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    try { & $Action; throw "Expected failure matching '$Pattern'." }
    catch { if ($_.Exception.Message -notmatch $Pattern) { throw "$Message Actual: $($_.Exception.Message)" } }
}

function New-FinalizationFixturePlan([string]$CaseId) {
    [pscustomobject][ordered]@{
        schemaVersion = 2; caseId = $CaseId; gameId = 'skyrimspecialedition'
        installationId = 'installation.fixture'; profileId = 'profile.fixture'
        originalRequest = 'Capture the exact fixture baseline.'; purpose = 'Baseline'
        requiredGates = @('InstallationBaseline','ProfileBaseline')
        capabilities = @([pscustomobject][ordered]@{ capabilityId = 'grid.game.skyrimspecialedition.baseline.collect'; capabilityVersion = '2.6.0' })
        normalizedSymptoms = @(); locations = @(); observedForms = @(); candidatePlugins = @(); providerSeeds = @()
        hypotheses = @(); evidenceReferences = @(); parentCaseIds = @(); evidenceIds = @(); collectorQueries = @()
        missingInputs = @(); status = 'ReadyToCollect'; recommendedCollectors = @()
    }
}

function New-FinalizationFixtureCase {
    param(
        [Parameter(Mandatory)][string]$StoreRoot,
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][string[]]$ProtectedPath,
        [Parameter(Mandatory)]$ResolvedContext,
        [ValidateSet('partial','authorizationRequired')][string]$TerminalStatus = 'partial'
    )
    $transaction = New-GridCaseStoreTransaction -StoreRoot $StoreRoot -CaseId $CaseId
    $runId = 'run-fixture'
    $plan = New-FinalizationFixturePlan -CaseId $CaseId
    $protected = @($ProtectedPath | ForEach-Object { Get-GridBaselineFileObservation -LiteralPath $_ })
    Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'case.json' -Value ([pscustomobject]@{schemaVersion=1;caseId=$CaseId;createdAt=[DateTimeOffset]::UtcNow.ToString('o');purpose='Baseline';status='Running'}) | Out-Null
    Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'investigation-plan.json' -Value $plan | Out-Null
    Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath "runs/$runId/resolved-roots.v1.json" -Value $ResolvedContext | Out-Null
    $summary = [pscustomobject][ordered]@{
        status=$TerminalStatus; installationId='installation.fixture'; profileId='profile.fixture'
        environmentFingerprint=('A' * 64); physicalFiles=1; completeHashes=if($TerminalStatus -eq 'partial'){1}else{0}; hashedBytes=7
        requiredAuthorizations=if($TerminalStatus -eq 'partial'){@()}else{@('C:\fixture')}
    }
    $rawPath = Join-Path $transaction.TransactionDirectory 'fixture.ndjson'
    [IO.File]::WriteAllText($rawPath, (([pscustomobject]@{schemaVersion=1;sequence=1;recordType='summary';payload=$summary} | ConvertTo-Json -Depth 10 -Compress) + "`n"), [Text.UTF8Encoding]::new($false))
    Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath "runs/$runId/raw/mo2-baseline-attempt-1.ndjson" -SourceLiteralPath $rawPath | Out-Null
    Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'snapshots/protected-after.v1.json' -Value ([pscustomobject]@{schemaVersion=1;caseId=$CaseId;installationId='installation.fixture';profileId='profile.fixture';observedAt=[DateTimeOffset]::UtcNow.ToString('o');files=@($protected)}) | Out-Null
    Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'audit/non-mutation.v1.json' -Value ([pscustomobject]@{schemaVersion=1;result='NoChangeObserved';changed=@();basis=@('fixture');limitation='fixture'}) | Out-Null
    Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'provenance/source-archive-inspections.v1.json' -Value ([pscustomobject]@{schemaVersion=1;status='NotCollectedWithoutHashedRecoveryArchive';recordFormat='ndjson';recordPath='provenance/source-archive-inspections.v1.ndjson';recordCount=0;completeCount=0}) | Out-Null
    Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'provenance/source-archive-inspections.v1.ndjson' -Text '' | Out-Null
    $run = [pscustomobject][ordered]@{
        schemaVersion=1;runId=$runId;caseId=$CaseId;state='Failed';startedAt=[DateTimeOffset]::UtcNow.ToString('o');completedAt=[DateTimeOffset]::UtcNow.ToString('o')
        planFingerprint=('B' * 64);resourcePolicyVersion='grid.baseline.resource-policy.v1';gates=@();sufficiency=[pscustomobject]@{status='InsufficientForRequestedDiagnosis'}
        checkpoints=@();primaryFailure=[pscustomobject]@{code='UnexpectedFailure';message='Post-collection finalizer fixture failure.'};secondaryFailures=@()
    }
    Seal-GridCaseStoreRun -Transaction $transaction -Run $run | Out-Null
    (Seal-GridDiagnosticCase -Transaction $transaction -SemanticBaselineFingerprint ('C' * 64) -Runs @($run)).CaseDirectory
}

$tempRoot = Join-Path $env:TEMP ('grid-baseline-finalization-tests-' + [guid]::NewGuid().ToString('N'))
$storeRoot = Join-Path $tempRoot 'store'
$protectedPath = Join-Path $tempRoot 'protected.txt'
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
try {
    [IO.File]::WriteAllText($protectedPath, 'fixture', [Text.UTF8Encoding]::new($false))
    $profilePath = Join-Path $tempRoot 'profile'
    New-Item -ItemType Directory -Path $profilePath -Force | Out-Null
    $pluginsPath = Join-Path $profilePath 'plugins.txt'
    [IO.File]::WriteAllText($pluginsPath, "*Fixture.esp`r`n", [Text.UTF8Encoding]::new($false))
    $resolved = [pscustomobject][ordered]@{
        installationId='installation.fixture';profileId='profile.fixture';profileName='Fixture Profile';profilePath=$profilePath
        applicationPath=(Join-Path $tempRoot 'ModOrganizer.exe');instancePath=$tempRoot;referenceStorePath=$protectedPath
        referenceStoreSha256=(Get-FileHash -LiteralPath $protectedPath -Algorithm SHA256).Hash;configurationPath=$protectedPath
        configurationSha256=(Get-FileHash -LiteralPath $protectedPath -Algorithm SHA256).Hash
    }

    $normalizationTransaction = New-GridCaseStoreTransaction -StoreRoot $storeRoot -CaseId 'baseline-streaming-memory-fixture'
    $normalizationOutput = Join-Path $normalizationTransaction.TransactionDirectory 'collector.ndjson'
    $normalizationRecords = @(
        [pscustomobject]@{schemaVersion=1;sequence=1;recordType='fileHash';payload=[pscustomobject]@{virtualPath='textures\fixture.dds';providerName='Fixture Provider';kind='physical';status='Complete';sha256=('D' * 64)}},
        [pscustomobject]@{schemaVersion=1;sequence=2;recordType='fileHash';payload=[pscustomobject]@{virtualPath='Fixture.esp';providerName='Fixture Provider';kind='physical';status='Complete';sha256=('E' * 64)}}
    )
    $catalogRecordCount = 25000
    $fixtureWriter = [IO.StreamWriter]::new($normalizationOutput, $false, [Text.UTF8Encoding]::new($false), 1MB)
    try {
        foreach ($record in $normalizationRecords) { $fixtureWriter.WriteLine(($record | ConvertTo-Json -Depth 10 -Compress)) }
        for ($index = 1; $index -le $catalogRecordCount; $index++) {
            $fixtureWriter.WriteLine('{"schemaVersion":1,"sequence":' + ($index + 2) + ',"recordType":"pluginRecordCatalog","payload":{"pluginName":"Fixture.esp","signature":"ARMO","formId":"00000001"}}')
        }
    }
    finally { $fixtureWriter.Dispose() }
    Assert-Throws {
        Write-GridBaselineNormalizedArtifacts -Transaction $normalizationTransaction -RunId 'run-over-limit' -OutputPath $normalizationOutput `
            -InvestigationPlan (New-FinalizationFixturePlan -CaseId 'baseline-streaming-memory-fixture') -ResolvedContext $resolved `
            -Summary ([pscustomobject]@{status='complete'}) -MaximumInputBytes 1
    } 'ResourceLimitExceeded: collector NDJSON is' 'Normalization must refuse an input that exceeds its recorded byte budget.'
    $normalizationClock = [Diagnostics.Stopwatch]::StartNew()
    $normalized = Write-GridBaselineNormalizedArtifacts -Transaction $normalizationTransaction -RunId 'run-streaming' -OutputPath $normalizationOutput `
        -InvestigationPlan (New-FinalizationFixturePlan -CaseId 'baseline-streaming-memory-fixture') -ResolvedContext $resolved `
        -Summary ([pscustomobject]@{status='complete'}) -MaximumInputBytes 33554432 -MaximumWallClockSeconds 30
    $normalizationClock.Stop()
    Assert-Equal 1 @($normalized.FileHashRecords).Count 'Normalization must retain only plugin hashes in working memory.'
    Assert-Equal 2 @(Get-Content -LiteralPath (Join-Path $normalizationTransaction.CaseDirectory 'inventory\files.v1.ndjson')).Count 'Normalization must still preserve the complete streamed file-hash inventory on disk.'
    Assert-Equal $catalogRecordCount $normalized.Counts.pluginRecordCatalog 'Normalization must count every streamed plugin-record catalog row.'
    Assert-Equal $catalogRecordCount @([IO.File]::ReadLines((Join-Path $normalizationTransaction.CaseDirectory 'inventory\plugin-record-catalog.v1.ndjson'))).Count 'Normalization must preserve every streamed plugin-record catalog payload.'
    Assert-True ($normalizationClock.Elapsed.TotalSeconds -lt 30) 'Bounded high-volume normalization exceeded its regression time budget.'

    $caseDirectory = New-FinalizationFixtureCase -StoreRoot $storeRoot -CaseId 'baseline-finalization-fixture' -ProtectedPath @($protectedPath,$pluginsPath) -ResolvedContext $resolved
    $currentPlan = New-FinalizationFixturePlan -CaseId 'baseline-successor-fixture'
    $result = Resolve-GridBaselineFinalizationArtifact -CaseStoreRoot $storeRoot -FinalizationCaseId 'baseline-finalization-fixture' `
        -InvestigationPlan $currentPlan -ResolvedContext $resolved -InstallationId 'installation.fixture' -ProfileId 'profile.fixture'
    Assert-Equal 'baseline-finalization-fixture' $result.CaseId 'A valid sealed post-collection failure must resolve.'
    Assert-Equal 'partial' $result.Summary.status 'The exact terminal native status must be preserved.'
    Assert-True ($result.ArtifactSha256 -match '^[A-F0-9]{64}$') 'The reused collector partition must remain digest-bound.'

    $stableHashCache = Resolve-GridBaselineHashCacheArtifact -CaseStoreRoot $storeRoot -HashCacheCaseId 'baseline-finalization-fixture' `
        -InvestigationPlan $currentPlan -ResolvedContext $resolved -InstallationId 'installation.fixture' -ProfileId 'profile.fixture'
    Assert-True (-not $stableHashCache.ConfigurationDriftAccepted) 'Unchanged MO2 configuration must not be recorded as accepted drift.'
    Assert-True (-not $stableHashCache.ProfileStateDriftAccepted) 'Unchanged profile state must not be recorded as accepted drift.'

    $collectorSource = Get-Content -Raw -LiteralPath (Join-Path $gameRoot 'health\collectors\Invoke-GridSkyrimBaseline.ps1')
    Assert-True ($collectorSource -match 'if \(\$finalization\)\s*\{\s*\$bridge\s*=') 'The finalization route must construct its bridge before the collector branch.'
    Assert-True ($collectorSource -match 'collectorRelaunched\s*=\s*\$false') 'Finalization lineage must explicitly record that the collector was not relaunched.'

    $mismatchPlan = New-FinalizationFixturePlan -CaseId 'baseline-mismatch-fixture'
    $mismatchPlan.originalRequest = 'Different request.'
    Assert-Throws { Resolve-GridBaselineFinalizationArtifact -CaseStoreRoot $storeRoot -FinalizationCaseId 'baseline-finalization-fixture' -InvestigationPlan $mismatchPlan -ResolvedContext $resolved -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' } 'FinalizationInvalid: source originalRequest does not match' 'Request drift must be refused.'

    [IO.File]::WriteAllText($protectedPath, 'changed', [Text.UTF8Encoding]::new($false))
    $changedResolved = $resolved.PSObject.Copy()
    $changedResolved.configurationSha256 = (Get-FileHash -LiteralPath $protectedPath -Algorithm SHA256).Hash
    $driftedHashCache = Resolve-GridBaselineHashCacheArtifact -CaseStoreRoot $storeRoot -HashCacheCaseId 'baseline-finalization-fixture' `
        -InvestigationPlan $currentPlan -ResolvedContext $changedResolved -InstallationId 'installation.fixture' -ProfileId 'profile.fixture'
    Assert-True $driftedHashCache.ConfigurationDriftAccepted 'Read-only hash-cache reuse must tolerate MO2 configuration byte drift after the same stable installation/profile resolves.'
    Assert-True (@($driftedHashCache.AcceptedReadOnlyDriftPaths) -contains $protectedPath) 'Accepted configuration drift must be recorded by exact path.'
    $otherConfigurationPath = Join-Path $tempRoot 'other.ini'
    [IO.File]::WriteAllText($otherConfigurationPath, 'changed', [Text.UTF8Encoding]::new($false))
    $otherResolved = $changedResolved.PSObject.Copy(); $otherResolved.configurationPath = $otherConfigurationPath
    Assert-Throws {
        Resolve-GridBaselineHashCacheArtifact -CaseStoreRoot $storeRoot -HashCacheCaseId 'baseline-finalization-fixture' `
            -InvestigationPlan $currentPlan -ResolvedContext $otherResolved -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' | Out-Null
    } 'BaselineStale: protected hash-cache input changed' 'Only the currently resolved MO2 configuration path may receive the read-only hash-cache drift exception.'
    [IO.File]::WriteAllText($protectedPath, 'fixture', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($pluginsPath, "# normalized by MO2`r`n*Fixture.esp`r`n", [Text.UTF8Encoding]::new($false))
    $profileDriftHashCache = Resolve-GridBaselineHashCacheArtifact -CaseStoreRoot $storeRoot -HashCacheCaseId 'baseline-finalization-fixture' `
        -InvestigationPlan $currentPlan -ResolvedContext $resolved -InstallationId 'installation.fixture' -ProfileId 'profile.fixture'
    Assert-True $profileDriftHashCache.ProfileStateDriftAccepted 'Read-only hash-cache reuse must tolerate a current canonical MO2 profile-order document rewrite.'
    Assert-True (@($profileDriftHashCache.AcceptedReadOnlyDriftPaths) -contains $pluginsPath) 'Accepted profile-state drift must be recorded by exact path.'
    $unrelatedProtectedPath = Join-Path $profilePath 'settings.ini'
    [IO.File]::WriteAllText($unrelatedProtectedPath, 'fixture', [Text.UTF8Encoding]::new($false))
    New-FinalizationFixtureCase -StoreRoot $storeRoot -CaseId 'baseline-unrelated-protected-fixture' -ProtectedPath @($protectedPath,$unrelatedProtectedPath) -ResolvedContext $resolved | Out-Null
    [IO.File]::WriteAllText($unrelatedProtectedPath, 'changed', [Text.UTF8Encoding]::new($false))
    Assert-Throws {
        Resolve-GridBaselineHashCacheArtifact -CaseStoreRoot $storeRoot -HashCacheCaseId 'baseline-unrelated-protected-fixture' `
            -InvestigationPlan $currentPlan -ResolvedContext $resolved -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' | Out-Null
    } 'BaselineStale: protected hash-cache input changed' 'Profile files outside the three canonical order documents must remain byte-for-byte protected.'
    [IO.File]::WriteAllText($pluginsPath, "*Fixture.esp`r`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($protectedPath, 'changed', [Text.UTF8Encoding]::new($false))
    Assert-Throws { Resolve-GridBaselineFinalizationArtifact -CaseStoreRoot $storeRoot -FinalizationCaseId 'baseline-finalization-fixture' -InvestigationPlan $currentPlan -ResolvedContext $resolved -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' } 'BaselineStale: protected finalization input changed' 'Changed protected state must be refused.'
    [IO.File]::WriteAllText($protectedPath, 'fixture', [Text.UTF8Encoding]::new($false))

    New-FinalizationFixtureCase -StoreRoot $storeRoot -CaseId 'baseline-no-terminal-fixture' -ProtectedPath $protectedPath -ResolvedContext $resolved -TerminalStatus authorizationRequired | Out-Null
    Assert-Throws { Resolve-GridBaselineFinalizationArtifact -CaseStoreRoot $storeRoot -FinalizationCaseId 'baseline-no-terminal-fixture' -InvestigationPlan $currentPlan -ResolvedContext $resolved -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' } 'FinalizationInvalid: source case has no unique terminal completed or partial collector partition' 'A nonterminal collector partition must be refused.'

    $tamperedRaw = Join-Path $caseDirectory 'runs\run-fixture\raw\mo2-baseline-attempt-1.ndjson'
    [IO.File]::AppendAllText($tamperedRaw, "tamper`n", [Text.UTF8Encoding]::new($false))
    Assert-Throws { Resolve-GridBaselineFinalizationArtifact -CaseStoreRoot $storeRoot -FinalizationCaseId 'baseline-finalization-fixture' -InvestigationPlan $currentPlan -ResolvedContext $resolved -InstallationId 'installation.fixture' -ProfileId 'profile.fixture' } 'FinalizationInvalid: source case seal failed' 'Tampered source evidence must be refused before reuse.'
    'Baseline finalization recovery checks passed.'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
