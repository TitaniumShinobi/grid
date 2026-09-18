#requires -Version 5.1

<#
.SYNOPSIS
Refreshes component-recovery download evidence without rescanning the profile.
.DESCRIPTION
Consumes one sealed component-recovery baseline, observes only the exact archive
and sidecar leaves already named by that baseline, and seals a lightweight
successor.  Installed mods, plugins, the active profile, and game Data are not
enumerated or changed.
#>

function Get-GridSkyrimRecoveryArchiveIdentityKey {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Record,
        [Parameter(Mandatory)][string]$PathProperty,
        [Parameter(Mandatory)][string]$HashProperty
    )

    $pathMember = $Record.PSObject.Properties[$PathProperty]
    $hashMember = $Record.PSObject.Properties[$HashProperty]
    if ($null -eq $pathMember -or $null -eq $hashMember) { return $null }
    $pathValue = [string]$pathMember.Value
    $hashValue = [string]$hashMember.Value
    if ([string]::IsNullOrWhiteSpace($pathValue) -or $hashValue -notmatch '^[A-Fa-f0-9]{64}$') { return $null }
    try { $fullPath = [IO.Path]::GetFullPath($pathValue) } catch { return $null }
    $fullPath.ToUpperInvariant() + '|' + $hashValue.ToUpperInvariant()
}

function Select-GridSkyrimRecoveryArchivesForInspection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Archives,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$PriorInspections,
        [AllowEmptyCollection()][object[]]$PriorAssessments = @(),
        [ValidateRange(1, 2147483647)][int]$InspectionContractVersion = 2,
        [ValidateRange(1, 2147483647)][int]$AssessmentContractVersion = 2
    )

    $selected = New-Object Collections.Generic.List[object]
    $replacementKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($archive in @($Archives)) {
        $state = $archive.PSObject.Properties['state']
        $hashStatus = $archive.PSObject.Properties['hashStatus']
        if ($null -eq $state -or $null -eq $hashStatus -or
            [string]$state.Value -ine 'present' -or [string]$hashStatus.Value -ine 'complete') { continue }
        $key = Get-GridSkyrimRecoveryArchiveIdentityKey -Record $archive -PathProperty canonicalPath -HashProperty sha256
        if ([string]::IsNullOrWhiteSpace($key)) { continue }
        $hasCurrentInspection = @($PriorInspections | Where-Object {
            $schema = $_.PSObject.Properties['schemaVersion']
            $null -ne $schema -and [int]$schema.Value -eq $InspectionContractVersion -and
            (Get-GridSkyrimRecoveryArchiveIdentityKey -Record $_ -PathProperty archivePath -HashProperty archiveSha256) -ieq $key
        }).Count -gt 0
        $hasCurrentAssessment = @($PriorAssessments | Where-Object {
            $schema = $_.PSObject.Properties['schemaVersion']
            $null -ne $schema -and [int]$schema.Value -eq $AssessmentContractVersion -and
            (Get-GridSkyrimRecoveryArchiveIdentityKey -Record $_ -PathProperty archivePath -HashProperty archiveSha256) -ieq $key
        }).Count -gt 0
        if (-not $hasCurrentInspection -or -not $hasCurrentAssessment) {
            $selected.Add($archive)
            $replacementKeys.Add($key) | Out-Null
        }
    }
    [pscustomobject][ordered]@{
        Archives = $selected.ToArray()
        ReplacementKeys = @($replacementKeys | Sort-Object)
        InspectionContractVersion = $InspectionContractVersion
        AssessmentContractVersion = $AssessmentContractVersion
    }
}

function Invoke-GridSkyrimRecoverySourceRefresh {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ParentCaseId,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedParentManifestSha256,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedRecoveryPlanSha256,
        [Parameter(Mandatory)][string]$CaseStoreRoot,
        [Parameter(Mandatory)][string]$InstallationId,
        [Parameter(Mandatory)][string]$ProfileId,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$AuthorizedReadPaths,
        [string]$DiagnosticsExecutable,
        [ValidateRange(1, 86400)][int]$MaximumWallClockSeconds = 900
    )

    $ErrorActionPreference = 'Stop'
    Set-StrictMode -Version Latest
    $scriptsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
    Import-Module (Join-Path $scriptsRoot 'health\Grid.Health.psm1') -DisableNameChecking
    . (Join-Path $PSScriptRoot 'Invoke-GridSkyrimBaseline.ps1')
    . (Join-Path $PSScriptRoot 'Resolve-GridSkyrimRequestToolInputs.ps1')
    $actionsRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'actions'
    . (Join-Path $actionsRoot 'New-GridSkyrimComponentRecoveryPlan.ps1')
    . (Join-Path $actionsRoot 'Get-GridSkyrimModChainRepairInspection.ps1')
    . (Join-Path $actionsRoot 'Invoke-GridAuthorizedModChainRepair.ps1')
    . (Join-Path $actionsRoot 'New-GridSkyrimModChainRepairProposal.ps1')
    . (Join-Path $actionsRoot 'New-GridSkyrimLiveProfileRecoveryRepair.ps1')

    function Read-SealedJson([string]$RelativePath) {
        $normalized = $RelativePath.Replace('\','/')
        if (@($parentSeal.Manifest.artifacts | Where-Object { [string]$_.path -ceq $normalized }).Count -ne 1) {
            throw "RecoveryRefreshArtifactMissing: $normalized"
        }
        Get-Content -Raw -LiteralPath (Join-Path $parentDirectory $RelativePath) -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    }
    function Read-SealedNdjson([string]$RelativePath) {
        $normalized = $RelativePath.Replace('\','/')
        if (@($parentSeal.Manifest.artifacts | Where-Object { [string]$_.path -ceq $normalized }).Count -ne 1) { return @() }
        @($values = Get-Content -LiteralPath (Join-Path $parentDirectory $RelativePath) -ReadCount 1 -ErrorAction Stop |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
            ForEach-Object { [string]$_ | ConvertFrom-Json -ErrorAction Stop }; $values)
    }
    function Copy-SealedArtifact([string]$RelativePath) {
        $normalized = $RelativePath.Replace('\','/')
        if (@($parentSeal.Manifest.artifacts | Where-Object { [string]$_.path -ceq $normalized }).Count -ne 1) {
            throw "RecoveryRefreshArtifactMissing: $normalized"
        }
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath $RelativePath -SourceLiteralPath (Join-Path $parentDirectory $RelativePath) | Out-Null
    }
    function Write-NdjsonArtifact([string]$RelativePath, [object[]]$Records) {
        $temporary = Join-Path $transaction.TransactionDirectory ([guid]::NewGuid().ToString('N') + '.ndjson')
        $writer = [IO.StreamWriter]::new($temporary, $false, [Text.UTF8Encoding]::new($false))
        try { foreach ($record in @($Records)) { $writer.WriteLine(($record | ConvertTo-Json -Depth 40 -Compress)) } }
        finally { $writer.Dispose() }
        try { Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath $RelativePath -SourceLiteralPath $temporary | Out-Null }
        finally { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
    }
    function Get-GeneralIniValues([string]$LiteralPath) {
        $values = @{}; $section = ''
        foreach ($line in @(Get-Content -LiteralPath $LiteralPath -ErrorAction Stop)) {
            $text = [string]$line
            if ($text -match '^\s*\[([^]]+)\]\s*$') { $section = $matches[1]; continue }
            if ($section -ieq 'General' -and $text -match '^\s*([^=]+?)\s*=\s*(.*)\s*$') {
                $values[$matches[1].Trim().ToLowerInvariant()] = $matches[2].Trim().Trim('"')
            }
        }
        $values
    }
    function Resolve-DiagnosticsExecutable {
        if (-not [string]::IsNullOrWhiteSpace([string]$DiagnosticsExecutable)) {
            return [IO.Path]::GetFullPath($DiagnosticsExecutable)
        }
        $repositoryRoot = Split-Path -Parent $scriptsRoot
        $candidate = @(
            (Join-Path $repositoryRoot 'Grid.Diagnostics.exe'),
            (Join-Path $repositoryRoot 'bin\Grid.Diagnostics.exe'),
            (Join-Path $repositoryRoot 'src\Grid.Diagnostics\bin\x64\Debug\net9.0-windows10.0.19041.0\Grid.Diagnostics.exe')
        ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace([string]$candidate)) {
            throw 'CollectorUnavailable: Grid.Diagnostics is unavailable for archive inspection or repair preparation.'
        }
        [IO.Path]::GetFullPath([string]$candidate)
    }
    function Observe-ExactLeaf($Source, [bool]$IsSidecar) {
        $copy = $Source | ConvertTo-Json -Depth 30 -Compress | ConvertFrom-Json -ErrorAction Stop
        $leaf = [string]$copy.exactLeafName
        if ([string]::IsNullOrWhiteSpace($leaf) -or [IO.Path]::GetFileName($leaf) -cne $leaf) {
            throw "RecoveryRefreshScopeInvalid: '$leaf' is not one safe exact source leaf."
        }
        $path = [IO.Path]::GetFullPath((Join-Path $downloadsRoot $leaf))
        if ([IO.Path]::GetFullPath((Split-Path -Parent $path)).TrimEnd('\') -ine $downloadsRoot.TrimEnd('\')) {
            throw "RecoveryRefreshScopeInvalid: '$leaf' is not an exact immediate child of the authorized downloads directory."
        }
        $copy | Add-Member -NotePropertyName canonicalPath -NotePropertyValue $path -Force
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            foreach ($property in @('length','lastWriteTimeUtcTicks','hashStatus','sha256','providerIdentity')) { $copy.PSObject.Properties.Remove($property) }
            $copy.state = 'missing'
            return $copy
        }
        $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "RecoveryRefreshReparsePointRefused: $leaf" }
        $beforeLength = [long]$item.Length; $beforeTicks = [long]$item.LastWriteTimeUtc.Ticks
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256 -ErrorAction Stop).Hash.ToUpperInvariant()
        $after = Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if ([long]$after.Length -ne $beforeLength -or [long]$after.LastWriteTimeUtc.Ticks -ne $beforeTicks) { throw "RecoveryRefreshInputChanged: $leaf" }
        $copy.state = 'present'
        foreach ($pair in @(@('length',$beforeLength),@('lastWriteTimeUtcTicks',$beforeTicks),@('hashStatus','complete'),@('sha256',$hash))) {
            $copy | Add-Member -NotePropertyName $pair[0] -NotePropertyValue $pair[1] -Force
        }
        if ($IsSidecar) {
            $ini = Get-GeneralIniValues -LiteralPath $path
            $modId = 0L; $fileId = 0L
            $identity = if ($ini.ContainsKey('repository') -and $ini.ContainsKey('gamename') -and
                $ini.ContainsKey('modid') -and [long]::TryParse([string]$ini['modid'], [ref]$modId) -and $modId -gt 0 -and
                $ini.ContainsKey('fileid') -and [long]::TryParse([string]$ini['fileid'], [ref]$fileId) -and $fileId -gt 0 -and
                $ini.ContainsKey('version')) {
                [pscustomobject][ordered]@{repository=[string]$ini['repository'];gameName=[string]$ini['gamename'];modId=$modId;fileId=$fileId;version=[string]$ini['version'];archiveLeaf=$leaf.Substring(0,$leaf.Length-5);status='ObservedComplete'}
            } else { [pscustomobject][ordered]@{status='ObservedIncomplete'} }
            $copy | Add-Member -NotePropertyName providerIdentity -NotePropertyValue $identity -Force
        }
        $copy
    }

    $transaction = $null
    $caseId = 'baseline-refresh-' + [guid]::NewGuid().ToString('N')
    $runId = 'run-' + [guid]::NewGuid().ToString('N')
    $startedAt = [DateTimeOffset]::UtcNow.ToString('o')
    try {
        if ([IO.Path]::GetFileName($ParentCaseId) -cne $ParentCaseId) { throw 'RecoveryRefreshParentInvalid: parent case ID is not one safe segment.' }
        $root = Get-GridDiagnosticStoreRoot -Root $CaseStoreRoot -Ensure
        $parentDirectory = Join-Path (Join-Path $root 'cases\v1') $ParentCaseId
        $parentSeal = Test-GridDiagnosticCaseSeal -StoreRoot $root -CaseDirectory $parentDirectory
        if (-not $parentSeal.IsValid) { throw ('RecoveryRefreshParentInvalid: ' + ($parentSeal.Errors -join '; ')) }
        if ([string]$parentSeal.Manifest.manifestSha256 -cne $ExpectedParentManifestSha256.ToUpperInvariant()) { throw 'RecoveryRefreshParentStale: parent manifest changed.' }
        $parentPlan = Read-SealedJson 'investigation-plan.json'
        $recoveryPlan = Read-SealedJson 'repair\component-recovery-plan.v1.json'
        if ([string]$recoveryPlan.planSha256 -cne $ExpectedRecoveryPlanSha256.ToUpperInvariant()) { throw 'RecoveryRefreshPlanStale: recovery plan changed.' }
        if ([string]$parentPlan.installationId -cne $InstallationId -or [string]$parentPlan.profileId -cne $ProfileId) { throw 'RecoveryRefreshContextMismatch: installation or profile differs from the sealed baseline.' }

        # Recovery refreshes must carry the complete sealed physical-file ledger
        # forward. Older refresh builds retained only plugin/BSA rows, which is
        # sufficient for payload pairing but cannot prove an empty optional
        # FOMOD selection. Walk the sealed refresh lineage back to its baseline
        # source and reuse that immutable ledger without loading it into memory.
        $fileInventorySourceDirectory = $parentDirectory
        $fileInventorySourceSeal = $parentSeal
        for ($lineageDepth = 0; $lineageDepth -lt 32; $lineageDepth++) {
            $caseEntry = @($fileInventorySourceSeal.Manifest.artifacts | Where-Object { [string]$_.path -ceq 'case.json' })
            if ($caseEntry.Count -ne 1) { throw 'RecoveryRefreshLineageInvalid: a lineage case omits case.json.' }
            $caseDocument = Get-Content -Raw -LiteralPath (Join-Path $fileInventorySourceDirectory 'case.json') -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
            if ([string]$caseDocument.purpose -cne 'RecoverySourceRefresh') { break }
            $lineageEntries = @($fileInventorySourceSeal.Manifest.artifacts | Where-Object { [string]$_.path -match '^runs/[^/]+/recovery-source-lineage\.v1\.json$' })
            if ($lineageEntries.Count -ne 1) { throw 'RecoveryRefreshLineageInvalid: a refresh case does not identify exactly one sealed parent.' }
            $lineageDocument = Get-Content -Raw -LiteralPath (Join-Path $fileInventorySourceDirectory ([string]$lineageEntries[0].path).Replace('/','\')) -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
            $ancestorCaseId = [string]$lineageDocument.parentCaseId
            if ([IO.Path]::GetFileName($ancestorCaseId) -cne $ancestorCaseId) { throw 'RecoveryRefreshLineageInvalid: an ancestor case ID is unsafe.' }
            $ancestorDirectory = Join-Path (Join-Path $root 'cases\v1') $ancestorCaseId
            $ancestorSeal = Test-GridDiagnosticCaseSeal -StoreRoot $root -CaseDirectory $ancestorDirectory
            if (-not $ancestorSeal.IsValid -or [string]$ancestorSeal.Manifest.manifestSha256 -cne ([string]$lineageDocument.parentManifestSha256).ToUpperInvariant()) {
                throw 'RecoveryRefreshLineageInvalid: an ancestor case seal does not match its child.'
            }
            $ancestorPlan = Get-Content -Raw -LiteralPath (Join-Path $ancestorDirectory 'investigation-plan.json') -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
            if ([string]$ancestorPlan.installationId -cne $InstallationId -or [string]$ancestorPlan.profileId -cne $ProfileId) {
                throw 'RecoveryRefreshContextMismatch: an inventory ancestor belongs to another installation or profile.'
            }
            $fileInventorySourceDirectory = $ancestorDirectory
            $fileInventorySourceSeal = $ancestorSeal
        }
        if ([string]$caseDocument.purpose -ceq 'RecoverySourceRefresh') { throw 'RecoveryRefreshLineageInvalid: refresh lineage exceeds 32 sealed cases.' }
        $fileInventoryEntries = @($fileInventorySourceSeal.Manifest.artifacts | Where-Object { [string]$_.path -ceq 'inventory/files.v1.ndjson' })
        if ($fileInventoryEntries.Count -ne 1) { throw 'RecoveryRefreshLineageInvalid: the baseline source omits its complete file ledger.' }
        $fileInventorySourcePath = Join-Path $fileInventorySourceDirectory 'inventory\files.v1.ndjson'

        $sourceArchives = @(Read-SealedNdjson 'provenance\source-archives.v1.ndjson')
        $sourceSidecars = @(Read-SealedNdjson 'provenance\source-archive-sidecars.v1.ndjson')
        $oldInspections = @(Read-SealedNdjson 'provenance\source-archive-inspections.v1.ndjson')
        $oldAssessments = @(Read-SealedNdjson 'provenance\source-archive-candidate-assessments.v1.ndjson')
        $liveInputs = Resolve-GridSkyrimRequestToolInputs -GridDataRoot $root -InstallationId $InstallationId -ProfileId $ProfileId -ToolIds @('grid.tool.mo2')
        $liveMo2 = $liveInputs['grid.tool.mo2']
        if ($null -eq $liveMo2) { throw 'RecoveryRefreshScopeInvalid: the current MO2 context did not resolve.' }
        $livePaths = @($liveMo2.authorizedReadPaths | ForEach-Object { [IO.Path]::GetFullPath([string]$_).TrimEnd('\') })
        $downloadsCandidates = @($livePaths | Where-Object { [IO.Path]::GetFileName([string]$_) -ieq 'downloads' } | Sort-Object -Unique)
        if ($downloadsCandidates.Count -ne 1) { throw 'RecoveryRefreshScopeInvalid: the current MO2 context does not identify one downloads directory.' }
        $downloadsRoot = [string]$downloadsCandidates[0]
        $authorized = @($AuthorizedReadPaths | ForEach-Object { [IO.Path]::GetFullPath([string]$_).TrimEnd('\') })
        if ($downloadsRoot -notin $authorized) { throw 'RecoveryRefreshAuthorizationInvalid: downloads directory is absent from the exact authorized read scope.' }

        $transaction = New-GridCaseStoreTransaction -StoreRoot $root -CaseId $caseId
        $refreshedArchives = New-Object Collections.Generic.List[object]
        $newArchives = New-Object Collections.Generic.List[object]
        foreach ($archive in $sourceArchives) {
            $observed = Observe-ExactLeaf $archive $false; $refreshedArchives.Add($observed)
            $wasSameVerifiedSource = [string]$archive.state -ieq 'present' -and [string]$archive.hashStatus -ieq 'complete' -and
                [string]$archive.sha256 -ieq [string]$observed.sha256 -and
                [IO.Path]::GetFullPath([string]$archive.canonicalPath) -ieq [string]$observed.canonicalPath
            if (-not $wasSameVerifiedSource -and [string]$observed.state -ieq 'present' -and [string]$observed.hashStatus -ieq 'complete') { $newArchives.Add($observed) }
        }
        # A source can be byte-identical while Grid's bounded inspection contract
        # has advanced.  Re-evaluate evidence created by an older matcher instead
        # of retaining a stale rejection forever.  This is deliberately keyed by
        # canonical path + SHA-256 and never broadens the authorized source set.
        $inspectionContractVersion = 2
        $assessmentContractVersion = 2
        $inspectionSelection = Select-GridSkyrimRecoveryArchivesForInspection -Archives $refreshedArchives.ToArray() -PriorInspections $oldInspections -PriorAssessments $oldAssessments -InspectionContractVersion $inspectionContractVersion -AssessmentContractVersion $assessmentContractVersion
        $archivesToInspect = @($inspectionSelection.Archives)
        $replacementKeys = [Collections.Generic.HashSet[string]]::new([string[]]$inspectionSelection.ReplacementKeys, [StringComparer]::OrdinalIgnoreCase)
        $refreshedSidecars = New-Object Collections.Generic.List[object]
        foreach ($sidecar in $sourceSidecars) { $refreshedSidecars.Add((Observe-ExactLeaf $sidecar $true)) }

        foreach ($relative in @('installation\installation-baseline.v1.json','inventory\plugin-script-dependencies.v1.json','inventory\plugin-script-dependencies.v1.ndjson','inventory\plugin-asset-dependencies.v1.json','inventory\plugin-asset-dependencies.v1.ndjson','provenance\mod-metadata.v1.json')) { Copy-SealedArtifact $relative }
        # Retain only the small plugin/archive identity subset needed to classify a
        # later archive.  The parent's complete virtual-file inventory can be
        # tens of megabytes; loose-file hashes are not needed here.
        # Baseline inventory preserves the collector's canonical plugin payload
        # envelope ("plugin": { ... }).  Older refresh fixtures wrote the inner
        # plugin object directly.  Normalize both sealed representations here so
        # archive classification, installer-variant reconciliation, and
        # live-profile selection checks consume one stable shape.
        $pluginRecords = @(Read-SealedNdjson 'inventory\plugins.v1.ndjson' | ForEach-Object {
            $innerPlugin = Get-GridBaselineOptionalProperty -InputObject $_ -Name 'plugin'
            if ($null -ne $innerPlugin) { $innerPlugin } else { $_ }
        })
        $masterRecords = @(Read-SealedNdjson 'inventory\plugin-dependencies.v1.ndjson')
        $fileHashRecords = @(Get-Content -LiteralPath $fileInventorySourcePath -ReadCount 1 -ErrorAction Stop | ForEach-Object {
            if ([string]::IsNullOrWhiteSpace([string]$_)) { return }
            $fileRecord = [string]$_ | ConvertFrom-Json -ErrorAction Stop
            if ([string]$fileRecord.virtualPath -match '(?i)(?:^|\\)[^\\]+\.(?:esp|esm|esl|bsa|ba2)$') { $fileRecord }
        })
        Write-NdjsonArtifact 'inventory\plugins.v1.ndjson' $pluginRecords
        Write-NdjsonArtifact 'inventory\plugin-dependencies.v1.ndjson' $masterRecords
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'inventory\files.v1.ndjson' -SourceLiteralPath $fileInventorySourcePath | Out-Null
        Write-NdjsonArtifact 'provenance\source-archives.v1.ndjson' $refreshedArchives.ToArray()
        Write-NdjsonArtifact 'provenance\source-archive-sidecars.v1.ndjson' $refreshedSidecars.ToArray()
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'provenance\source-archives.v1.json' -Value ([pscustomobject][ordered]@{schemaVersion=1;status='Collected';archiveRecordFormat='ndjson';archiveRecordPath='provenance/source-archives.v1.ndjson';archiveRecordCount=$refreshedArchives.Count;sidecarRecordFormat='ndjson';sidecarRecordPath='provenance/source-archive-sidecars.v1.ndjson';sidecarRecordCount=$refreshedSidecars.Count;note='Exact source leaves were refreshed without enumerating the installed profile.'}) | Out-Null

        $dependencies = @(Read-SealedNdjson 'inventory\plugin-script-dependencies.v1.ndjson') + @(Read-SealedNdjson 'inventory\plugin-asset-dependencies.v1.ndjson')
        $metadataDocument = Read-SealedJson 'provenance\mod-metadata.v1.json'
        $newInspection = $null
        if ($archivesToInspect.Count -gt 0) {
            $DiagnosticsExecutable = Resolve-DiagnosticsExecutable
            $normalized = [pscustomobject]@{PluginScriptDependencies=@($dependencies | Where-Object { $_.PSObject.Properties['scriptName'] });PluginAssetDependencies=@($dependencies | Where-Object { -not $_.PSObject.Properties['scriptName'] });SourceArchives=$archivesToInspect;SourceArchiveSidecars=$refreshedSidecars.ToArray();PluginRecords=$pluginRecords;PluginMasterRecords=$masterRecords;FileHashRecords=$fileHashRecords;Metadata=@($metadataDocument.records)}
            $newInspection = Invoke-GridSkyrimRecoveryArchiveInspections -Transaction $transaction -RunId $runId -Normalized $normalized -DiagnosticsExecutable ([IO.Path]::GetFullPath($DiagnosticsExecutable)) -MaximumEntries 1000000 -MaximumWallClockSeconds $MaximumWallClockSeconds
        }
        $retainedInspections = @($oldInspections | Where-Object {
            $key = Get-GridSkyrimRecoveryArchiveIdentityKey -Record $_ -PathProperty archivePath -HashProperty archiveSha256
            [string]::IsNullOrWhiteSpace($key) -or -not $replacementKeys.Contains($key)
        })
        $retainedAssessments = @($oldAssessments | Where-Object {
            $key = Get-GridSkyrimRecoveryArchiveIdentityKey -Record $_ -PathProperty archivePath -HashProperty archiveSha256
            [string]::IsNullOrWhiteSpace($key) -or -not $replacementKeys.Contains($key)
        })
        $inspections = @($retainedInspections) + @($(if ($newInspection) { $newInspection.Records } else { @() }))
        $assessments = @($retainedAssessments) + @($(if ($newInspection) { $newInspection.Assessments } else { @() }))
        Write-NdjsonArtifact 'provenance\source-archive-inspections.v1.ndjson' $inspections
        Write-NdjsonArtifact 'provenance\source-archive-candidate-assessments.v1.ndjson' $assessments
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'provenance\source-archive-inspections.v1.json' -Value ([pscustomobject][ordered]@{schemaVersion=1;status=if($inspections.Count){'Complete'}else{'NotCollectedWithoutHashedRecoveryArchive'};recordFormat='ndjson';recordPath='provenance/source-archive-inspections.v1.ndjson';recordCount=$inspections.Count;completeCount=@($inspections|Where-Object status -eq 'Complete').Count}) | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'provenance\source-archive-candidate-assessments.v1.json' -Value ([pscustomobject][ordered]@{schemaVersion=1;status=if($assessments.Count){'Complete'}else{'NoClassifiableCandidate'};recordFormat='ndjson';recordPath='provenance/source-archive-candidate-assessments.v1.ndjson';recordCount=$assessments.Count}) | Out-Null

        $installation = Read-SealedJson 'installation\installation-baseline.v1.json'
        $contextFingerprint = [string]$installation.summary.environmentFingerprint
        $successorPlan = New-GridSkyrimComponentRecoveryPlan -CaseId $caseId -ContextFingerprint $contextFingerprint -Dependencies $dependencies -ModMetadata @($metadataDocument.records) -SourceArchives $refreshedArchives.ToArray() -SourceArchiveSidecars $refreshedSidecars.ToArray() -ArchiveInspections $inspections -ArchiveCandidateAssessments $assessments
        $planCopy = $parentPlan | ConvertTo-Json -Depth 100 -Compress | ConvertFrom-Json
        $planCopy.caseId = $caseId; $planCopy.originalRequest = "Refresh exact downloaded recovery sources for $ParentCaseId."
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'case.json' -Value ([pscustomobject][ordered]@{schemaVersion=1;caseId=$caseId;createdAt=$startedAt;purpose='RecoverySourceRefresh';status='Completed'}) | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'investigation-plan.json' -Value $planCopy | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'repair\component-recovery-plan.v1.json' -Value $successorPlan | Out-Null
        $reinspectedArchiveCount = [int]$archivesToInspect.Count - [int]$newArchives.Count
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath "runs\$runId\recovery-source-lineage.v1.json" -Value ([pscustomobject][ordered]@{schemaVersion=1;parentCaseId=$ParentCaseId;parentManifestSha256=$ExpectedParentManifestSha256.ToUpperInvariant();parentRecoveryPlanSha256=$ExpectedRecoveryPlanSha256.ToUpperInvariant();downloadsRoot=$downloadsRoot;profileRescanned=$false;newArchiveCount=$newArchives.Count;inspectedArchiveCount=$archivesToInspect.Count;reinspectedArchiveCount=$reinspectedArchiveCount;inspectionContractVersion=$inspectionContractVersion;assessmentContractVersion=$assessmentContractVersion}) | Out-Null
        $evidence = New-GridEvidenceItem -Parameter 'recoverySourceRefresh' -Value 1 -Claim ("Observed {0} newly available and re-evaluated {1} existing exact recovery archive source(s) without rescanning the installed profile." -f $newArchives.Count,$reinspectedArchiveCount) -SourceType 'ExactFileObservation' -ContextFingerprint $contextFingerprint -VerificationStatus Collected -CollectorName 'Invoke-GridSkyrimRecoverySourceRefresh' -CollectorVersion '1.1.0'
        $diagnostic = New-GridUnresolvedDiagnosticResult -CaseId $caseId -State NeedsEvidence -ContextFingerprint $contextFingerprint -Evidence @($evidence) -Finding ("Recovery source verification completed; {0} new and {1} previously inspected archive source(s) were evaluated." -f $newArchives.Count,$reinspectedArchiveCount) -NextStep 'Review the refreshed recovery plan before authorizing any repair.'
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'evidence\recovery-source-refresh.v1.json' -Value ([pscustomobject][ordered]@{schemaVersion=1;records=@($evidence)}) | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'diagnosis\diagnostic-result.v1.json' -Value $diagnostic | Out-Null
        $completedAt = [DateTimeOffset]::UtcNow.ToString('o')
        $run = [pscustomobject][ordered]@{schemaVersion=1;runId=$runId;caseId=$caseId;state='Completed';startedAt=$startedAt;completedAt=$completedAt;planFingerprint=$ExpectedRecoveryPlanSha256.ToUpperInvariant();resourcePolicyVersion='grid.recovery-source-refresh.v1';gates=@();sufficiency=[pscustomobject]@{status='CompleteForExactRecoverySourceRefresh'};checkpoints=@();primaryFailure=$null;secondaryFailures=@()}
        Seal-GridCaseStoreRun -Transaction $transaction -Run $run | Out-Null
        $semantic = Get-GridCanonicalJsonSha256 -InputObject ([pscustomobject][ordered]@{parentManifestSha256=$ExpectedParentManifestSha256.ToUpperInvariant();recoveryPlanSha256=[string]$successorPlan.planSha256;newArchiveCount=$newArchives.Count;inspectedArchiveCount=$archivesToInspect.Count;inspectionContractVersion=$inspectionContractVersion})
        $sealed = Seal-GridDiagnosticCase -Transaction $transaction -SemanticBaselineFingerprint $semantic -Runs @($run)
        $repairPlanning = if ([int]$successorPlan.summary.repairReadyCount -gt 0) {
            if ([string]::IsNullOrWhiteSpace([string]$DiagnosticsExecutable)) { $DiagnosticsExecutable = Resolve-DiagnosticsExecutable }
            New-GridSkyrimLiveProfileRecoveryRepair -CaseStoreRoot $root -RecoveryCaseDirectory $sealed.CaseDirectory -AuthorizedReadPaths $AuthorizedReadPaths -DiagnosticsExecutable $DiagnosticsExecutable -PassThru
        }
        else {
            [pscustomobject][ordered]@{status='EvidenceRequired';planningCaseId=$null;planningCaseDirectory=$null;specification=$null;preparedComponentCount=0;detail='No component has complete sealed source evidence yet.'}
        }
        [pscustomobject][ordered]@{Tool='Grid.Health.Skyrim.RecoverySourceRefresh';Status='Completed';TerminalState='Diagnosed';CaseId=$caseId;CaseDirectory=$sealed.CaseDirectory;Manifest=$sealed.Manifest;DiagnosticResult=$diagnostic;Evidence=@($evidence);RepairPlanning=$repairPlanning;RepairPlanningCaseId=[string]$repairPlanning.planningCaseId;Summary=[pscustomobject][ordered]@{profileRescanned=$false;newArchiveCount=$newArchives.Count;inspectedArchiveCount=$archivesToInspect.Count;reinspectedArchiveCount=$reinspectedArchiveCount;archiveClaimCount=$refreshedArchives.Count;recoveryStatus=[string]$successorPlan.status;repairReadyCount=[int]$successorPlan.summary.repairReadyCount;preparedComponentCount=[int]$repairPlanning.preparedComponentCount};PrimaryFailure=$null;Detail=if([string]$repairPlanning.status -eq 'RepairPlanned'){'Exact recovery sources were verified and one inert live-profile repair was staged for review. The MO2 profile was not changed.'}else{'Exact recovery download leaves were refreshed without rescanning or changing the MO2 profile.'}}
    }
    catch {
        if ($null -ne $transaction -and $transaction.State -eq 'Open' -and
            (Test-Path -LiteralPath $transaction.TransactionDirectory -PathType Container)) {
            Remove-Item -LiteralPath $transaction.TransactionDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
        [pscustomobject][ordered]@{Tool='Grid.Health.Skyrim.RecoverySourceRefresh';Status='Failed';TerminalState='EvidenceFailed';CaseId=$null;CaseDirectory=$null;PrimaryFailure=[pscustomobject][ordered]@{code=if($_.Exception.Message -match '^([A-Za-z]+(?:[A-Za-z]+)?):'){$matches[1]}else{'UnexpectedFailure'};detail=$_.Exception.Message;location=[string]$_.ScriptStackTrace};Detail='Recovery source refresh failed without changing the MO2 profile.'}
    }
}
