function Resolve-GridSkyrimRootCause {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$CaseId, [Parameter(Mandatory)]$CollectorResult)
    if ([string]$CollectorResult.status -ne 'Completed') { throw "RootCauseCollectionIncomplete: $($CollectorResult.status)" }
    if (@($CollectorResult.issues | Where-Object { [string]$_.severity -in @('Error','Contradiction') }).Count -gt 0) { throw 'RootCauseEvidenceContradictory: blocking collector issues remain.' }
    $graph = $CollectorResult.recordGraph
    if ($graph -and $graph.PSObject.Properties['chains']) {
        $derived = New-Object Collections.Generic.List[object]
        $chains = @($graph.chains)
        foreach ($asset in @($CollectorResult.assetGraph.targets | Where-Object { [string]$_.status -eq 'Missing' })) {
            $assetVirtual = ([string]$asset.virtualPath).Replace('/','\').TrimStart('\')
            if (-not $assetVirtual.StartsWith('meshes\', [StringComparison]::OrdinalIgnoreCase)) { $assetVirtual = 'meshes\' + $assetVirtual }
            $baseChains = @($chains | Where-Object {
                if (-not $_.winner -or [string]::IsNullOrWhiteSpace([string]$_.winner.modelPath)) { return $false }
                $modelVirtual = ([string]$_.winner.modelPath).Replace('/','\').TrimStart('\')
                if (-not $modelVirtual.StartsWith('meshes\', [StringComparison]::OrdinalIgnoreCase)) { $modelVirtual = 'meshes\' + $modelVirtual }
                $modelVirtual -ieq $assetVirtual
            })
            foreach ($baseChain in $baseChains) {
                $references = @($chains | Where-Object {
                    $_.winner -and [string]$_.winner.signature -eq 'REFR' -and $_.winner.baseObject -and
                    [string]$_.winner.baseObject.originPlugin -ieq [string]$baseChain.target.originPlugin -and
                    [uint32]$_.winner.baseObject.localFormId -eq [uint32]$baseChain.target.localFormId
                } | Sort-Object { [string]$_.target.originPlugin }, { [uint32]$_.target.localFormId })
                if ($references.Count -eq 0) { continue }
                $reference = $references[0].winner; $base = $baseChain.winner
                $providerName = if ($asset.provider -and -not [string]::IsNullOrWhiteSpace([string]$asset.provider.sourceName)) { [string]$asset.provider.sourceName } else { [string]$base.pluginName }
                $subjects = @(
                    [pscustomobject][ordered]@{ subjectId=('plugin:' + [string]$reference.pluginName); kind='Plugin'; name=[string]$reference.pluginName; authoritativeOrder=$reference.loadOrder; roles=@('PlacementProvider') },
                    [pscustomobject][ordered]@{ subjectId=('plugin:' + [string]$base.pluginName); kind='Plugin'; name=[string]$base.pluginName; authoritativeOrder=$base.loadOrder; roles=@('OverrideProvider','CompatibilityPatch') },
                    [pscustomobject][ordered]@{ subjectId=('mod:' + $providerName); kind='Mod'; name=$providerName; authoritativeOrder=$null; roles=@('AssetProvider','Dependency') }
                )
                $referenceIds = @($references | ForEach-Object { '{0}:{1:X8}' -f [string]$_.target.originPlugin, [uint32]$_.target.localFormId })
                $derived.Add([pscustomobject][ordered]@{
                    recordId = ($referenceIds -join ','); status='RootCauseProven'; evidenceSha256=[string]$reference.pluginRawSha256
                    winningPlugin=[string]$base.pluginName; patchTargetPlugin=$null
                    cause=[pscustomobject][ordered]@{ code='Missing'; detail=("The winning placements use base object {0}:{1:X8} ({2}), whose required model {3} is absent from the resolved virtual data; {4} adjacent reference(s) therefore render without that geometry." -f [string]$baseChain.target.originPlugin,[uint32]$baseChain.target.localFormId,[string]$base.editorId,$assetVirtual,$references.Count) }
                    subjects=$subjects; requiredChanges=@(); requiredAssetActions=@([pscustomobject][ordered]@{
                        virtualPath=$assetVirtual; action='RestoreVerifiedAssetPayload'; providerName=$providerName
                        expectedCurrentState='Missing'; postcondition='The exact virtual NIF resolves from the intended provider and passes bounded structural validation.'
                    })
                })
            }
        }
        $proven = @($derived.ToArray())
    }
    else { $proven = @($graph | Where-Object { [string]$_.status -eq 'RootCauseProven' }) }
    if ($proven.Count -eq 0) { throw 'RootCauseNotProven: deterministic collection did not prove a causal record chain.' }
    $rawHashes = @($CollectorResult.rawSources | ForEach-Object { ([string]$_.sha256).ToUpperInvariant() })
    $evidence = @(); $affected = @(); $roles = @()
    foreach ($node in @($proven | Sort-Object recordId)) {
        if ([string]$node.evidenceSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $rawHashes -notcontains ([string]$node.evidenceSha256).ToUpperInvariant()) { throw "RootCauseEvidenceUnbound: $($node.recordId)" }
        if ([string]::IsNullOrWhiteSpace([string]$node.winningPlugin) -or [string]::IsNullOrWhiteSpace([string]$node.cause.code)) { throw "RootCauseNodeMalformed: $($node.recordId)" }
        $item = New-GridEvidenceItem -Parameter 'rootCauseProof' -Value 1.0 -Claim ([string]$node.cause.detail) -SourceType 'DeterministicCollector' `
            -SourceIdentifier ([string]$node.recordId) -ContextFingerprint ([string]$CollectorResult.contextFingerprint) -VerificationStatus Verified `
            -CollectorName 'Grid.Diagnostics root-cause-collect' -CollectorVersion '1.0.0'
        $item | Add-Member -NotePropertyName native -NotePropertyValue ([pscustomobject][ordered]@{ operation = 'ResolveWinningOverride'; state = 'Found'; winner = [string]$node.winningPlugin; sha256 = ([string]$node.evidenceSha256).ToUpperInvariant() })
        $evidence += $item
        foreach ($subject in @($node.subjects)) {
            if ([string]::IsNullOrWhiteSpace([string]$subject.name)) { continue }
            $id = if ($subject.PSObject.Properties['subjectId'] -and $subject.subjectId) { [string]$subject.subjectId } else { ([string]$subject.kind).ToLowerInvariant() + ':' + [string]$subject.name }
            $kind = if ([string]$subject.kind -eq 'Mod') { 'Mod' } else { 'Plugin' }
            $order = if ($subject.PSObject.Properties['authoritativeOrder'] -and $null -ne $subject.authoritativeOrder) { [int]$subject.authoritativeOrder } else { $null }
            $affected += [pscustomobject][ordered]@{ subjectId = $id; kind = $kind; name = [string]$subject.name; authoritativeOrder = $order }
            $allowedRoles = @($subject.roles | Where-Object { $_ -in @('BaseOrMaster','WorldspaceOrCellEditor','PlacementProvider','OverrideProvider','CompatibilityPatch','AssetProvider','AssetReplacer','GeneratedOutput','Dependency') } | Sort-Object -Unique)
            if ($allowedRoles.Count -eq 0) { throw "RootCauseSubjectRolesMissing: $id" }
            $roles += [pscustomobject][ordered]@{ subjectId = $id; roles = $allowedRoles }
        }
    }
    $affected = @($affected | Sort-Object subjectId -Unique); $roles = @($roles | Sort-Object subjectId -Unique)
    if ($affected.Count -eq 0) { throw 'RootCauseSubjectsMissing.' }
    $fingerprint = Get-GridEvidenceFingerprint -Evidence $evidence
    $patch = New-GridSkyrimConflictPatchSpecification -CaseId $CaseId -ContextFingerprint ([string]$CollectorResult.contextFingerprint) -EvidenceFingerprint $fingerprint -Evidence $evidence -RecordGraph $proven
    $ids = @($evidence | ForEach-Object evidenceId)
    $causeCodes = @($proven | ForEach-Object { [string]$_.cause.code } | Sort-Object -Unique)
    $findingText = (@($proven | ForEach-Object { [string]$_.cause.detail } | Sort-Object -Unique) -join ' ')
    $solutionText = if (@($proven | Where-Object { $_.PSObject.Properties['requiredAssetActions'] -and @($_.requiredAssetActions).Count -gt 0 }).Count -gt 0) {
        'Restore the evidence-identified provider asset payload in a separate transactional installation repair, then recollect the exact record and virtual-asset graph; this workflow made no active-state change.'
    } else { 'Apply the exact inert conflict-patch specification through a separately tested and authorized writer, then recollect the affected record and asset graph.' }
    $diagnostic = New-GridDiagnosticResult -CaseId $CaseId -State Diagnosed -ContextFingerprint ([string]$CollectorResult.contextFingerprint) -Evidence $evidence -EvidenceFingerprint $fingerprint `
        -AffectedMods ([pscustomobject]@{ status='Resolved'; items=$affected; evidenceIds=$ids }) -ModRoles ([pscustomobject]@{ status='Resolved'; items=$roles; evidenceIds=$ids }) `
        -Finding ([pscustomobject]@{ status='Resolved'; text=$findingText; evidenceIds=$ids }) `
        -Solution ([pscustomobject]@{ status='Unsupported'; text=$solutionText; evidenceIds=$ids; proposalId=$patch.Proposal.proposalId }) `
        -ResolverVersion 'skyrimspecialedition.root-cause.v1' -RemediationProposals @($patch.Proposal)
    [pscustomobject][ordered]@{ Evidence = $evidence; DiagnosticResult = $diagnostic; PatchSpecification = $patch.Specification; RemediationProposal = $patch.Proposal }
}
