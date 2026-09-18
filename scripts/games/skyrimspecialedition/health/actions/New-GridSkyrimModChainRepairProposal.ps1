#requires -Version 5.1

<#
.SYNOPSIS
Materializes an inert evidence-bound Skyrim mod-chain repair specification.
#>

function Get-GridRepairSpecificationLegacyHash {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Specification)
    $payload = [pscustomobject][ordered]@{
        schemaVersion = [int]$Specification.schemaVersion
        specificationId = [string]$Specification.specificationId
        caseId = [string]$Specification.caseId
        baseline = $Specification.baseline
        contextFingerprint = [string]$Specification.contextFingerprint
        evidenceFingerprint = [string]$Specification.evidenceFingerprint
        artifactIntegrity = @($Specification.artifactIntegrity)
        fomodDecisions = @($Specification.fomodDecisions)
        compatibilityMatrix = @($Specification.compatibilityMatrix)
        operations = @($Specification.operations)
        protectedState = @($Specification.protectedState)
        preconditions = @($Specification.preconditions)
        postconditions = @($Specification.postconditions)
        profilePreservation = @($Specification.profilePreservation)
        exclusions = @($Specification.exclusions)
        authorization = $Specification.authorization
    }
    foreach ($name in @('diagnosis','recordPatch','assetRepair')) {
        if ($Specification.PSObject.Properties[$name]) { $payload | Add-Member -NotePropertyName $name -NotePropertyValue $Specification.$name }
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Depth 30 -Compress))
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace('-', '') } finally { $algorithm.Dispose() }
}

function ConvertTo-GridRepairPortableValue {
    param($Value)
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [string]) { return ($Value | ConvertTo-Json -Compress) }
    if ($Value -is [bool]) { return $(if ($Value) { 'true' } else { 'false' }) }
    if ($Value -is [byte] -or $Value -is [sbyte] -or $Value -is [int16] -or $Value -is [uint16] -or
        $Value -is [int32] -or $Value -is [uint32] -or $Value -is [int64] -or $Value -is [uint64] -or
        $Value -is [single] -or $Value -is [double] -or $Value -is [decimal]) {
        return [Convert]::ToString($Value, [Globalization.CultureInfo]::InvariantCulture)
    }
    if ($Value -is [Collections.IDictionary]) {
        return '{' + (@($Value.Keys | ForEach-Object { [string]$_ } | Sort-Object | ForEach-Object {
            $key = $_
            ($key | ConvertTo-Json -Compress) + ':' + (ConvertTo-GridRepairPortableValue -Value $Value[$key])
        }) -join ',') + '}'
    }
    if ($Value -is [Collections.IEnumerable] -and -not ($Value -is [string])) {
        return '[' + (@($Value | ForEach-Object { ConvertTo-GridRepairPortableValue -Value $_ }) -join ',') + ']'
    }
    $properties = @($Value.PSObject.Properties | Where-Object MemberType -in @('NoteProperty','Property') | Sort-Object Name)
    return '{' + (@($properties | ForEach-Object {
        ($_.Name | ConvertTo-Json -Compress) + ':' + (ConvertTo-GridRepairPortableValue -Value $_.Value)
    }) -join ',') + '}'
}

function Get-GridRepairSpecificationPortableHash {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Specification)
    $payload = [ordered]@{}
    foreach ($name in @('schemaVersion','specificationHashAlgorithm','specificationId','caseId','baseline','contextFingerprint','evidenceFingerprint','artifactIntegrity','fomodDecisions','compatibilityMatrix','operations','protectedState','preconditions','postconditions','profilePreservation','exclusions','authorization','diagnosis','recordPatch','assetRepair')) {
        if ($Specification.PSObject.Properties[$name]) { $payload[$name] = $Specification.$name }
    }
    $canonical = ConvertTo-GridRepairPortableValue -Value $payload
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical)))).Replace('-', '') }
    finally { $algorithm.Dispose() }
}

function Get-GridRepairSpecificationHash {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Specification)
    if (-not $Specification.PSObject.Properties['specificationHashAlgorithm']) {
        return Get-GridRepairSpecificationLegacyHash -Specification $Specification
    }
    if ([string]$Specification.specificationHashAlgorithm -cne 'grid.repair-specification.portable-v1') {
        throw "RepairSpecificationHashAlgorithmUnsupported: $($Specification.specificationHashAlgorithm)"
    }
    Get-GridRepairSpecificationPortableHash -Specification $Specification
}

function ConvertTo-GridRepairRelativePath {
    param([Parameter(Mandatory)][string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or [IO.Path]::IsPathRooted($Path) -or $Path -match '(^|[\/])\.\.([\/]|$)') { throw "RepairPathInvalid: '$Path' must be relative and traversal-free." }
    $Path.Replace('/', '\').TrimStart('\')
}

function Test-GridRepairNifHeader {
    param([Parameter(Mandatory)][string]$LiteralPath)
    $stream = [IO.File]::Open($LiteralPath, 'Open', 'Read', 'Read')
    try {
        if ($stream.Length -lt 32) { return $false }
        $buffer = New-Object byte[] ([Math]::Min(64L, $stream.Length))
        [void]$stream.Read($buffer, 0, $buffer.Length)
        $header = [Text.Encoding]::ASCII.GetString($buffer)
        $header.StartsWith('Gamebryo File Format') -or $header.StartsWith('NetImmerse File Format')
    }
    finally { $stream.Dispose() }
}

function New-GridSkyrimModChainRepairProposal {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)]$Inspection,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ContextFingerprint,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$EvidenceFingerprint,
        [string]$ModsRoot,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Operations,
        [object[]]$ProtectedState = @(),
        [object[]]$ArtifactIntegrity = @(),
        [object[]]$FomodDecisions = @(),
        [object[]]$CompatibilityMatrix = @(),
        [string[]]$Preconditions = @(),
        [string[]]$Postconditions = @(),
        [object[]]$ProfilePreservation = @(),
        [string[]]$Exclusions = @(),
        [Parameter(Mandatory)][string]$OutputPath,
        [switch]$PassThru
    )
    if ([string]$Inspection.status -ne 'InputsVerified') { throw 'BaselineNotReady: inspection must be InputsVerified before a repair proposal can be created.' }
    if ([string]$Inspection.baselineManifestSha256 -notmatch '^[A-F0-9]{64}$') { throw 'BaselineNotReady: manifest digest is absent or invalid.' }
    if (@($Operations).Count -gt 0 -and [string]::IsNullOrWhiteSpace($ModsRoot)) { throw 'Repair operations require the evidence-bound mods root.' }
    $mods = if ([string]::IsNullOrWhiteSpace($ModsRoot)) { $null } else { (Test-GridRepairPathBoundary -LiteralPath $ModsRoot -Role ReadRoot -MustExist).TrimEnd('\') }
    foreach ($artifact in @($ArtifactIntegrity)) {
        if ([string]$artifact.status -ne 'Verified' -or [string]$artifact.sha256 -notmatch '^[A-F0-9]{64}$' -or @($artifact.identityEvidenceIds).Count -eq 0) {
            throw 'ArtifactEvidenceInsufficient: every repair artifact must have a verified digest and baseline identity evidence.'
        }
        if ($Inspection.PSObject.Properties['diagnosis']) {
            $provenance = $artifact.PSObject.Properties['provenance']
            if (-not $provenance -or [string]::IsNullOrWhiteSpace([string]$provenance.Value.providerName) -or [string]::IsNullOrWhiteSpace([string]$provenance.Value.sourceIdentity)) {
                throw 'ArtifactProviderIdentityMissing: diagnosis-bound repair artifacts require providerName and sourceIdentity provenance.'
            }
        }
    }
    foreach ($decision in @($FomodDecisions)) {
        if ([string]$decision.status -notin @('Deterministic','NotPresent')) { throw 'FomodSelectionAmbiguous: unsupported or contradictory installer logic cannot produce a repair specification.' }
    }
    foreach ($matrix in @($CompatibilityMatrix)) {
        if ([string]$matrix.status -ne 'VerifiedCompatible') { throw 'CompatibilityEvidenceInsufficient: repair components are not proven mutually compatible.' }
    }
    if (@($Operations).Count -gt 0) {
        if (@($ArtifactIntegrity).Count -eq 0) { throw 'ArtifactEvidenceInsufficient: a nonempty repair requires at least one verified source artifact.' }
        if (@($FomodDecisions).Count -eq 0) { throw 'FomodEvidenceInsufficient: every repair artifact requires a deterministic or NotPresent installer decision.' }
        if (@($CompatibilityMatrix).Count -eq 0) { throw 'CompatibilityEvidenceInsufficient: a nonempty repair requires a verified compatibility matrix.' }
        if (@($Preconditions).Count -eq 0 -or @($Postconditions).Count -eq 0) { throw 'RepairConditionIncomplete: nonempty repair requires explicit preconditions and postconditions.' }
    }
    $normalized = @()
    $sequences = @{}
    foreach ($operation in @($Operations | Sort-Object { [int]$_.sequence })) {
        $sequence = [int]$operation.sequence
        if ($sequence -lt 1 -or $sequences.ContainsKey($sequence)) { throw 'Repair operations require unique positive sequence values.' }
        $sequences[$sequence] = $true
        $target = [IO.Path]::GetFullPath([string]$operation.targetDirectory).TrimEnd('\')
        $repairStrategy = if ($operation.PSObject.Properties['repairStrategy']) { [string]$operation.repairStrategy } else { 'TransactionalOwningModReinstall' }
        if ($repairStrategy -notin @('TransactionalOwningModReinstall','EvidenceBoundOverlay')) { throw "RepairStrategyUnsupported: $repairStrategy" }
        $beforeTreeState = if ($operation.PSObject.Properties['beforeTreeState']) { [string]$operation.beforeTreeState } else { 'Existing' }
        if ($beforeTreeState -notin @('Existing','Absent')) { throw "BeforeTreeStateUnsupported: $beforeTreeState" }
        if ($repairStrategy -eq 'TransactionalOwningModReinstall' -and $beforeTreeState -ne 'Existing') { throw 'RepairStrategyInvalid: an owning-mod reinstall requires an existing target tree.' }
        if ($repairStrategy -eq 'EvidenceBoundOverlay' -and $beforeTreeState -ne 'Absent') { throw 'RepairStrategyInvalid: an overlay must target an absent mod tree.' }
        $source = Test-GridRepairPathBoundary -LiteralPath ([string]$operation.sourceDirectory) -Role ReadRoot -MustExist
        $staging = Test-GridRepairPathBoundary -LiteralPath ([string]$operation.stagingDirectory) -Role MutationRoot
        $rollback = Test-GridRepairPathBoundary -LiteralPath ([string]$operation.rollbackDirectory) -Role MutationRoot
        if ((Split-Path -Parent $target) -ine $mods) { throw "Target must be an immediate child of the authorized mods root: $target" }
        foreach ($outside in @($source, $staging, $rollback)) {
            if ($outside -ieq $mods -or $outside.StartsWith($mods + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Staging, source, and rollback trees must remain outside the active mods root: $outside" }
        }
        if ($source -ieq $staging -or $source -ieq $rollback -or $staging -ieq $rollback) { throw 'Source, staging, and rollback directories must be distinct.' }
        if (Test-Path -LiteralPath $staging) { throw "Staging destination already exists: $staging" }
        if (Test-Path -LiteralPath $rollback) { throw "Rollback destination already exists: $rollback" }
        if ($beforeTreeState -eq 'Existing' -and -not (Test-Path -LiteralPath $target -PathType Container)) { throw "Target tree is missing: $target" }
        if ($beforeTreeState -eq 'Absent' -and (Test-Path -LiteralPath $target)) { throw "Overlay target must be absent: $target" }
        $before = if ($beforeTreeState -eq 'Existing') { Get-GridRepairTreeObservation -LiteralPath $target } else { [pscustomobject]@{ treeSha256 = ('0' * 64) } }
        $expected = Get-GridRepairTreeObservation -LiteralPath $source
        if ($operation.PSObject.Properties['beforeTreeSha256'] -and [string]$operation.beforeTreeSha256 -and $before.treeSha256 -cne ([string]$operation.beforeTreeSha256).ToUpperInvariant()) { throw "BeforeTreeHashMismatch: $target" }
        if ($operation.PSObject.Properties['expectedTreeSha256'] -and [string]$operation.expectedTreeSha256 -and $expected.treeSha256 -cne ([string]$operation.expectedTreeSha256).ToUpperInvariant()) { throw "ExpectedTreeHashMismatch: $source" }
        $evidenceIds = @($operation.baselineEvidenceIds | Sort-Object -Unique)
        if ($evidenceIds.Count -eq 0) { throw "Operation '$sequence' lacks baseline evidence IDs." }
        $artifactSha256 = ([string]$operation.artifactSha256).ToUpperInvariant()
        $artifact = @($ArtifactIntegrity | Where-Object { [string]$_.sha256 -ceq $artifactSha256 })
        if ($artifact.Count -ne 1) { throw "Operation '$sequence' does not bind exactly one verified artifact digest." }
        $installer = @($FomodDecisions | Where-Object { [string]$_.artifactSha256 -ceq $artifactSha256 })
        if ($installer.Count -ne 1) { throw "Operation '$sequence' does not bind exactly one installer decision." }
        $mappings = @($operation.sourceToDestinationMappings | Sort-Object destination, source)
        $expectedFiles = @($operation.expectedFiles | Sort-Object path)
        if ($mappings.Count -eq 0 -or $expectedFiles.Count -eq 0) { throw "Operation '$sequence' lacks deterministic file mappings or expected-file postconditions." }
        $mappingKeys = @($mappings | ForEach-Object { (ConvertTo-GridRepairRelativePath ([string]$_.source)).ToLowerInvariant() + '|' + (ConvertTo-GridRepairRelativePath ([string]$_.destination)).ToLowerInvariant() })
        $installerKeys = @($installer[0].fileMappings | ForEach-Object { (ConvertTo-GridRepairRelativePath ([string]$_.source)).ToLowerInvariant() + '|' + (ConvertTo-GridRepairRelativePath ([string]$_.destination)).ToLowerInvariant() })
        if (@(Compare-Object $mappingKeys $installerKeys).Count -ne 0) { throw "SourceMappingMismatch: operation '$sequence' differs from its deterministic installer mapping." }
        foreach ($expectedFile in $expectedFiles) {
            $relative = ConvertTo-GridRepairRelativePath ([string]$expectedFile.path)
            $literal = Join-Path $source $relative
            if (-not (Test-Path -LiteralPath $literal -PathType Leaf)) { throw "ExpectedFileMissing: $relative" }
            if ((Get-GridRepairSha256 -LiteralPath $literal) -cne ([string]$expectedFile.sha256).ToUpperInvariant()) { throw "ExpectedFileHashMismatch: $relative" }
            if ($relative.EndsWith('.nif', [StringComparison]::OrdinalIgnoreCase) -and -not (Test-GridRepairNifHeader -LiteralPath $literal)) { throw "NifValidationFailed: $relative" }
        }
        $virtualPostconditions = @()
        if ($operation.PSObject.Properties['virtualAssetPostconditions']) {
            foreach ($postcondition in @($operation.virtualAssetPostconditions | Sort-Object virtualPath)) {
                $relative = ConvertTo-GridRepairRelativePath ([string]$postcondition.virtualPath)
                if ($postcondition.PSObject.Properties['archiveRelativePath']) {
                    $archiveRelative = ConvertTo-GridRepairRelativePath ([string]$postcondition.archiveRelativePath)
                    $archiveMember = ConvertTo-GridRepairRelativePath ([string]$postcondition.archiveMemberPath)
                    $matchingExpected = @($expectedFiles | Where-Object { (ConvertTo-GridRepairRelativePath ([string]$_.path)) -ieq $archiveRelative })
                    if ($matchingExpected.Count -ne 1 -or [string]$matchingExpected[0].sha256 -cne ([string]$postcondition.archiveSha256).ToUpperInvariant()) { throw "VirtualAssetContainerPostconditionUnbound: $relative" }
                    if ($archiveMember -ine $relative -or [string]$postcondition.requiredSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or [string]$postcondition.assetEvidenceSha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw "VirtualAssetMemberPostconditionInvalid: $relative" }
                    $virtualPostconditions += [pscustomobject][ordered]@{
                        virtualPath = $relative; expectedProviderDirectory = $target
                        requiredSha256 = ([string]$postcondition.requiredSha256).ToUpperInvariant(); validationKind = [string]$postcondition.validationKind
                        validationBasis = 'ExactContainerDigest'; archiveRelativePath = $archiveRelative; archiveSha256 = ([string]$postcondition.archiveSha256).ToUpperInvariant()
                        archiveMemberPath = $archiveMember; assetEvidenceSha256 = ([string]$postcondition.assetEvidenceSha256).ToUpperInvariant()
                    }
                }
                else {
                    $matchingExpected = @($expectedFiles | Where-Object { (ConvertTo-GridRepairRelativePath ([string]$_.path)) -ieq $relative })
                    if ($matchingExpected.Count -ne 1 -or [string]$matchingExpected[0].sha256 -cne ([string]$postcondition.requiredSha256).ToUpperInvariant()) { throw "VirtualAssetPostconditionUnbound: $relative" }
                    $virtualPostconditions += [pscustomobject][ordered]@{ virtualPath = $relative; expectedProviderDirectory = $target; requiredSha256 = ([string]$postcondition.requiredSha256).ToUpperInvariant(); validationKind = [string]$postcondition.validationKind; validationBasis = 'LooseFileDigest' }
                }
            }
        }
        $normalized += [pscustomobject][ordered]@{
            sequence = $sequence; componentId = [string]$operation.componentId; sourceDirectory = $source; targetDirectory = $target
            stagingDirectory = $staging; rollbackDirectory = $rollback; beforeTreeSha256 = $before.treeSha256
            expectedTreeSha256 = $expected.treeSha256; commitStrategy = 'SameVolumeAtomicRename'; repairStrategy = $repairStrategy; beforeTreeState = $beforeTreeState; baselineEvidenceIds = $evidenceIds
            artifactSha256 = $artifactSha256; installerDecisionStatus = [string]$installer[0].status
            sourceToDestinationMappings = $mappings; expectedFiles = $expectedFiles; virtualAssetPostconditions = @($virtualPostconditions)
        }
    }
    if ($Inspection.PSObject.Properties['diagnosis']) {
        $requiredPaths = @($Inspection.diagnosis.assetActions | ForEach-Object { (ConvertTo-GridRepairRelativePath ([string]$_.virtualPath)).ToLowerInvariant() } | Sort-Object -Unique)
        $boundPaths = @($normalized.virtualAssetPostconditions | ForEach-Object { ([string]$_.virtualPath).ToLowerInvariant() } | Sort-Object -Unique)
        if (@(Compare-Object $requiredPaths $boundPaths).Count -ne 0) { throw 'DiagnosisAssetActionUnbound: every proved asset action must have exactly one executable virtual-file postcondition.' }
        $requiredProviders = @($Inspection.diagnosis.assetActions.providerName | ForEach-Object { ([string]$_).ToLowerInvariant() } | Sort-Object -Unique)
        $artifactProviders = @($ArtifactIntegrity | ForEach-Object { ([string]$_.provenance.providerName).ToLowerInvariant() } | Sort-Object -Unique)
        foreach ($provider in $requiredProviders) { if ($provider -notin $artifactProviders) { throw "ArtifactProviderIdentityMismatch: no verified artifact is bound to proved provider '$provider'." } }
    }
    if (@($ProtectedState).Count -eq 0 -and $Inspection.PSObject.Properties['protectedState']) { $ProtectedState = @($Inspection.protectedState) }
    $protected = @($ProtectedState | Sort-Object path | ForEach-Object { [pscustomobject][ordered]@{ path = [IO.Path]::GetFullPath([string]$_.path); sha256 = ([string]$_.sha256).ToUpperInvariant() } })
    if (@($ProfilePreservation).Count -eq 0) { $ProfilePreservation = @($protected) }
    $zeroOperation = @($normalized).Count -eq 0
    $specification = [pscustomobject][ordered]@{
        schemaVersion = 1; specificationHashAlgorithm = 'grid.repair-specification.portable-v1'; specificationId = 'repair-' + ('0' * 32); caseId = $CaseId
        baseline = [pscustomobject][ordered]@{ caseDirectory = [string]$Inspection.baselineCaseDirectory; caseId = [string]$Inspection.baselineCaseId; manifestSha256 = [string]$Inspection.baselineManifestSha256; semanticBaselineFingerprint = [string]$Inspection.semanticBaselineFingerprint }
        contextFingerprint = $ContextFingerprint.ToUpperInvariant(); evidenceFingerprint = $EvidenceFingerprint.ToUpperInvariant()
        artifactIntegrity = @($ArtifactIntegrity | Sort-Object artifactId)
        fomodDecisions = @($FomodDecisions | Sort-Object artifactSha256)
        compatibilityMatrix = @($CompatibilityMatrix)
        operations = @($normalized); protectedState = $protected
        preconditions = @($Preconditions | Sort-Object -Unique); postconditions = @($Postconditions | Sort-Object -Unique)
        profilePreservation = @($ProfilePreservation | Sort-Object path)
        exclusions = @($Exclusions | Sort-Object -Unique)
        authorization = [pscustomobject][ordered]@{ status = if ($zeroOperation) { 'NotRequired' } else { 'Required' }; requirement = if ($zeroOperation) { 'NoMutation' } else { 'ExactOneUseSpecificationAuthorization' } }
        specificationSha256 = ''
    }
    if ($Inspection.PSObject.Properties['diagnosis']) {
        $specification | Add-Member -NotePropertyName diagnosis -NotePropertyValue ([pscustomobject][ordered]@{ caseDirectory = [string]$Inspection.diagnosis.caseDirectory; caseId = [string]$Inspection.diagnosis.caseId; manifestSha256 = [string]$Inspection.diagnosis.manifestSha256; manifestFileSha256 = [string]$Inspection.diagnosis.manifestFileSha256; specificationSha256 = [string]$Inspection.diagnosis.specificationSha256; specificationFileSha256 = [string]$Inspection.diagnosis.specificationFileSha256; evidenceFingerprint = [string]$Inspection.diagnosis.evidenceFingerprint })
        $specification | Add-Member -NotePropertyName recordPatch -NotePropertyValue ([pscustomobject][ordered]@{ required = $false; status = 'NotApplicable'; targetPluginName = $null; records = @() })
        $specification | Add-Member -NotePropertyName assetRepair -NotePropertyValue @($Inspection.diagnosis.assetActions)
    }
    if ([string]::IsNullOrWhiteSpace([string]$specification.baseline.caseDirectory)) { throw 'Inspection must retain the sealed baseline case directory.' }
    $identitySeed = Get-GridRepairSpecificationHash -Specification $specification
    $specification.specificationId = 'repair-' + $identitySeed.Substring(0, 32).ToLowerInvariant()
    $specification.specificationSha256 = Get-GridRepairSpecificationHash -Specification $specification
    Write-GridJsonAtomic -InputObject $specification -LiteralPath ([IO.Path]::GetFullPath($OutputPath))
    $result = [pscustomobject][ordered]@{ status = if ($zeroOperation) { 'InputsVerified' } else { 'AwaitingAuthorization' }; specification = $specification; specificationPath = [IO.Path]::GetFullPath($OutputPath); authorizationRequired = (-not $zeroOperation) }
    if ($PassThru) { $result } else { $result | ConvertTo-Json -Depth 30 }
}
