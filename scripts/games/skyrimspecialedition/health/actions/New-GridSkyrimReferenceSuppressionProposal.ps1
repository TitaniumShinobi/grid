#requires -Version 5.1

function Get-GridSkyrimReferenceSuppressionSpecificationHash {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Specification)
    $schemaVersion = [int]$Specification.schemaVersion
    if ($schemaVersion -notin @(1,2)) { throw 'ReferenceSuppressionSpecificationVersionUnsupported.' }
    $semantic = [ordered]@{
        schemaVersion = $schemaVersion
        specificationId = [string]$Specification.specificationId
        caseId = [string]$Specification.caseId
        contextFingerprint = [string]$Specification.contextFingerprint
        evidenceCase = $Specification.evidenceCase
        recordGraph = $Specification.recordGraph
        patchPluginName = [string]$Specification.patchPluginName
        intent = [string]$Specification.intent
        records = @($Specification.records)
        authorization = $Specification.authorization
        exclusions = @($Specification.exclusions)
        status = [string]$Specification.status
    }
    if ($schemaVersion -ge 2) { $semantic.Insert(5, 'context', $Specification.context) }
    Get-GridCanonicalJsonSha256 -InputObject ([pscustomobject]$semantic)
}

function New-GridSkyrimReferenceSuppressionProposal {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$EvidenceCaseDirectory,
        [Parameter(Mandatory)][string]$CaseStoreRoot,
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][string]$ContextFingerprint,
        [Parameter(Mandatory)][object[]]$TargetReferences,
        [Parameter(Mandatory)][string]$PatchPluginName,
        [Parameter(Mandatory)][string]$Intent,
        [Parameter(Mandatory)][string]$OutputPath,
        [switch]$PassThru
    )
    if (-not (Get-Command Get-GridCanonicalJsonSha256 -ErrorAction SilentlyContinue)) {
        throw 'ReferenceSuppressionHealthModuleRequired: import Grid.Health.psm1 before planning.'
    }
    if ($CaseId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') { throw 'ReferenceSuppressionCaseIdInvalid.' }
    if ($ContextFingerprint -notmatch '^[A-Fa-f0-9]{64}$') { throw 'ReferenceSuppressionContextFingerprintInvalid.' }
    if ($PatchPluginName -notmatch '^[^\\/:*?"<>|]{1,240}\.esp$') { throw 'ReferenceSuppressionPatchNameInvalid: a safe .esp filename is required.' }
    if ([string]::IsNullOrWhiteSpace($Intent) -or $Intent.Length -gt 2048) { throw 'ReferenceSuppressionIntentInvalid.' }
    if ($TargetReferences.Count -lt 1 -or $TargetReferences.Count -gt 256) { throw 'ReferenceSuppressionTargetCountInvalid: 1..256 exact references are required.' }

    $sealedCase = [IO.Path]::GetFullPath($EvidenceCaseDirectory).TrimEnd('\','/')
    $seal = Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory $sealedCase
    if (-not $seal.IsValid) { throw ('ReferenceSuppressionEvidenceCaseInvalid: ' + (@($seal.Errors) -join ' ')) }
    $graphPath = Join-Path $sealedCase 'evidence\plugin-record-graph.v1.json'
    if (-not (Test-Path -LiteralPath $graphPath -PathType Leaf)) { throw 'ReferenceSuppressionRecordGraphMissing.' }
    $graph = Get-Content -LiteralPath $graphPath -Raw | ConvertFrom-Json -ErrorAction Stop
    if ([string]$graph.status -ne 'Complete') { throw "ReferenceSuppressionRecordGraphIncomplete: $($graph.status)" }
    $graphHash = (Get-FileHash -LiteralPath $graphPath -Algorithm SHA256).Hash.ToUpperInvariant()
    $planPath = Join-Path $sealedCase 'investigation-plan.json'
    if (-not (Test-Path -LiteralPath $planPath -PathType Leaf)) { throw 'ReferenceSuppressionInvestigationPlanMissing.' }
    $plan = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json -ErrorAction Stop
    if ([string]::IsNullOrWhiteSpace([string]$plan.installationId) -or [string]::IsNullOrWhiteSpace([string]$plan.profileId)) {
        throw 'ReferenceSuppressionContextIdentityMissing.'
    }
    $baseline = if ($plan.PSObject.Properties['baseline']) { $plan.baseline } else { $null }
    if (-not $baseline -or [string]$baseline.manifestSha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'ReferenceSuppressionBaselineBindingMissing.' }

    $records = New-Object Collections.Generic.List[object]
    $seen = @{}
    foreach ($target in $TargetReferences) {
        $origin = [string]$target.originPlugin
        $localId = if ($target.localFormId -is [string]) {
            $text = ([string]$target.localFormId).Trim()
            if ($text -notmatch '^(?:0x)?[A-Fa-f0-9]{1,6}$') { throw "ReferenceSuppressionTargetInvalid: '$text' is not a local FormID." }
            [Convert]::ToUInt32(($text -replace '^0x',''), 16)
        } else { [uint32]$target.localFormId }
        if ([string]::IsNullOrWhiteSpace($origin) -or $origin -notmatch '(?i)\.(esp|esm|esl)$') { throw 'ReferenceSuppressionTargetInvalid: originPlugin is required.' }
        $key = ('{0}:{1:X6}' -f $origin, $localId)
        if ($seen.ContainsKey($key.ToUpperInvariant())) { throw "ReferenceSuppressionTargetDuplicate: $key" }
        $seen[$key.ToUpperInvariant()] = $true
        $matches = @($graph.chains | Where-Object {
            [string]$_.target.originPlugin -ieq $origin -and [uint32]$_.target.localFormId -eq $localId
        })
        if ($matches.Count -ne 1) { throw "ReferenceSuppressionTargetUnresolved: $key" }
        $winner = $matches[0].winner
        if (-not $winner -or [string]$winner.signature -notin @('REFR','ACHR')) { throw "ReferenceSuppressionTargetTypeRefused: $key" }
        if (-not [bool]$winner.isEnabled -or [bool]$winner.isDeleted -or [bool]$winner.isInitiallyDisabled) { throw "ReferenceSuppressionTargetNotActive: $key" }
        if ($winner.enableParent -or @($winner.linkedReferences).Count -gt 0) { throw "ReferenceSuppressionLinkedTargetRefused: $key" }
        if ([string]$winner.pluginRawSha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw "ReferenceSuppressionSourceHashMissing: $key" }
        $pluginSources = @($graph.plugins | Where-Object { [string]$_.name -ieq [string]$winner.pluginName })
        if ($pluginSources.Count -ne 1 -or [string]$pluginSources[0].rawSha256 -ine [string]$winner.pluginRawSha256 -or
            [string]::IsNullOrWhiteSpace([string]$pluginSources[0].canonicalPath)) {
            throw "ReferenceSuppressionSourceBindingInvalid: $key"
        }
        $records.Add([pscustomobject][ordered]@{
            originPlugin = $origin
            localFormId = [uint32]$localId
            displayFormId = ('{0}:{1:X6}' -f $origin, $localId)
            signature = [string]$winner.signature
            winningPlugin = [string]$winner.pluginName
            winningPluginSha256 = ([string]$winner.pluginRawSha256).ToUpperInvariant()
            winningPluginPath = [IO.Path]::GetFullPath([string]$pluginSources[0].canonicalPath)
            expectedRecordFlags = [uint32]$winner.recordFlags
            expectedInitiallyDisabled = $false
            action = 'SetInitiallyDisabled'
            requestedInitiallyDisabled = $true
            baseObject = $winner.baseObject
            worldspace = $winner.worldspace
            cell = $winner.cell
            placement = [string]$winner.placement
            transform = $winner.transform
            scale = $winner.scale
        })
    }
    $orderedRecords = @($records.ToArray() | Sort-Object originPlugin, localFormId)
    $seed = [pscustomobject][ordered]@{
        evidenceManifestSha256 = [string]$seal.Manifest.manifestSha256
        installationId = [string]$plan.installationId
        profileId = [string]$plan.profileId
        baselineManifestSha256 = ([string]$baseline.manifestSha256).ToUpperInvariant()
        recordGraphSha256 = $graphHash
        contextFingerprint = $ContextFingerprint.ToUpperInvariant()
        patchPluginName = $PatchPluginName
        intent = $Intent
        records = $orderedRecords
    }
    $seedHash = Get-GridCanonicalJsonSha256 -InputObject $seed
    $specificationId = 'reference-suppression-' + $seedHash.Substring(0,24).ToLowerInvariant()
    $evidenceId = 'evidence-record-graph-' + $graphHash.Substring(0,24).ToLowerInvariant()
    $specification = [pscustomobject][ordered]@{
        schemaVersion = 2
        specificationId = $specificationId
        specificationSha256 = $null
        caseId = $CaseId
        contextFingerprint = $ContextFingerprint.ToUpperInvariant()
        evidenceCase = [pscustomobject][ordered]@{ directory=$sealedCase; manifestSha256=[string]$seal.Manifest.manifestSha256 }
        context = [pscustomobject][ordered]@{
            installationId=[string]$plan.installationId;profileId=[string]$plan.profileId
            baselineCaseDirectory=[IO.Path]::GetFullPath([string]$baseline.caseDirectory)
            baselineManifestSha256=([string]$baseline.manifestSha256).ToUpperInvariant()
        }
        recordGraph = [pscustomobject][ordered]@{ path=$graphPath; sha256=$graphHash; evidenceId=$evidenceId }
        patchPluginName = $PatchPluginName
        intent = $Intent
        records = $orderedRecords
        authorization = [pscustomobject][ordered]@{
            requirement = 'Fresh explicit durable one-use authorization bound to this exact patch name, source hashes, reference set, and requested Initially Disabled flags.'
            status = 'Required'
        }
        exclusions = @(
            'No source plugin is modified.',
            'No reference is deleted.',
            'No transform, base object, enable parent, linked reference, navmesh, asset, save, or game file is changed.',
            'No plugin or mod is enabled and no load order is changed by this proposal.'
        )
        status = 'AwaitingAuthorization'
    }
    $specification.specificationSha256 = Get-GridSkyrimReferenceSuppressionSpecificationHash -Specification $specification
    Write-GridJsonAtomic -InputObject $specification -LiteralPath ([IO.Path]::GetFullPath($OutputPath))

    $proposal = New-GridRemediationProposal -CaseId $CaseId -ContextFingerprint $ContextFingerprint.ToUpperInvariant() `
        -EvidenceFingerprint $graphHash -ActionType 'ReferenceSuppressionPatch' `
        -Targets @($orderedRecords | ForEach-Object { 'record=' + $_.displayFormId }) `
        -SupportingEvidenceIds @($evidenceId) `
        -ExpectedEffects @('A dedicated compatibility plugin overrides only the reviewed placed references with the Initially Disabled flag.') `
        -Exclusions @($specification.exclusions) `
        -AuthorizationRequirement ([string]$specification.authorization.requirement) `
        -VerificationSteps @('Recollect the exact record graph and verify the patch is the winner.', 'Launch an isolated runtime verification and confirm the unwanted placements are absent.', 'Verify no adjacent placement or navmesh regression.') `
        -BackupRequirement 'Preserve a byte-exact copy of any pre-existing patch with the same name before replacement.' `
        -RollbackProcedure 'Disable/remove only the generated patch or restore the verified prior patch image.'
    $result = [pscustomobject][ordered]@{
        schemaVersion=1; status='AwaitingAuthorization'; specification=$specification
        specificationPath=[IO.Path]::GetFullPath($OutputPath); remediationProposal=$proposal
        authorizationRequired=$true; changedExternalState=$false
    }
    if ($PassThru) { $result } else { $result | ConvertTo-Json -Depth 40 }
}
