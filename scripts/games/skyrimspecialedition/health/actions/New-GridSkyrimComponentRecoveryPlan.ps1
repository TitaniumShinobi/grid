#requires -Version 5.1

<#
.SYNOPSIS
Builds an inert component-recovery plan for missing plugin-declared files.
.DESCRIPTION
The plan never assumes that the mod which wins a plugin file also owns every
file declared by that plugin. It joins MO2 components only through exact
provider metadata identity and treats installationFile values as claims until
an archive is independently observed, hashed, and inspected.
#>

function New-GridSkyrimComponentRecoveryPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ContextFingerprint,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Dependencies,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$ModMetadata,
        [AllowEmptyCollection()][object[]]$SourceArchives = @(),
        [AllowEmptyCollection()][object[]]$SourceArchiveSidecars = @(),
        [AllowEmptyCollection()][object[]]$ArchiveInspections = @(),
        [AllowEmptyCollection()][object[]]$ArchiveCandidateAssessments = @()
    )

    function Get-PropertyValue([AllowNull()][object]$Object, [string]$Name) {
        if ($null -eq $Object) { return $null }
        $property = $Object.PSObject.Properties[$Name]
        if ($property) { return $property.Value }
        $null
    }
    function Get-SafeName([AllowNull()][object]$Value) {
        $text = [string]$Value
        if ([string]::IsNullOrWhiteSpace($text) -or $text.Length -gt 512 -or
            $text.IndexOfAny([char[]]@(0, 9, 10, 13)) -ge 0) { return $null }
        $text
    }
    function Get-DependencyKind([AllowNull()][object]$Record) {
        $scriptName = [string](Get-PropertyValue $Record 'scriptName')
        if (-not [string]::IsNullOrWhiteSpace($scriptName)) { return 'PapyrusScript' }
        $kind = [string](Get-PropertyValue $Record 'kind')
        if ($kind -ieq 'Mesh') { return 'Mesh' }
        if ($kind -ieq 'Texture') { return 'Texture' }
        $null
    }
    function Get-IdentityKey([AllowNull()][object]$Record) {
        $repository = ([string](Get-PropertyValue $Record 'repository')).Trim().ToUpperInvariant()
        $game = ([string](Get-PropertyValue $Record 'nexusGameName')).Trim().ToUpperInvariant()
        $modIdValue = Get-PropertyValue $Record 'nexusModId'
        $modId = 0L
        if ([string]::IsNullOrWhiteSpace($repository) -or [string]::IsNullOrWhiteSpace($game) -or
            $null -eq $modIdValue -or -not [long]::TryParse([string]$modIdValue, [ref]$modId) -or $modId -le 0) { return $null }
        @($repository, $game, $modId) -join [char]31
    }
    function Get-ArchiveClaim([AllowNull()][object]$MetadataRecord, [object[]]$ArchiveRecords, [object[]]$SidecarRecords) {
        $claim = [string](Get-PropertyValue $MetadataRecord 'installationFile')
        if ([string]::IsNullOrWhiteSpace($claim)) {
            return [pscustomobject][ordered]@{
                status = 'NoArchiveClaim'; claimedLeafName = $null; locationClass = 'None'
                observationStatus = 'NotObserved'; sha256 = $null; sizeBytes = $null
                providerIdentityStatus = 'NotObserved'; providerIdentity = $null; sidecarSha256 = $null
            }
        }
        $trimmed = $claim.Trim()
        $isLeaf = -not [IO.Path]::IsPathRooted($trimmed) -and
            $trimmed.IndexOfAny([char[]]@('\','/')) -lt 0 -and
            $trimmed -notin @('.', '..') -and [IO.Path]::GetFileName($trimmed) -ceq $trimmed
        $leaf = try { [IO.Path]::GetFileName($trimmed) } catch { $null }
        if ([string]::IsNullOrWhiteSpace($leaf)) { $leaf = $null }
        $locationClass = if ($isLeaf) { 'Mo2DownloadsLeaf' } else { 'ExternalClaimAuthorizationRequired' }
        $modId = [string](Get-PropertyValue $MetadataRecord 'modId')
        $observations = @($ArchiveRecords | Where-Object {
            [string](Get-PropertyValue $_ 'modId') -ceq $modId -and
            [string](Get-PropertyValue $_ 'kind') -ieq 'InstallationArchive' -and
            (!$leaf -or [string](Get-PropertyValue $_ 'exactLeafName') -ieq $leaf)
        })
        $verified = @($observations | Where-Object {
            [string](Get-PropertyValue $_ 'state') -ieq 'Present' -and
            [string](Get-PropertyValue $_ 'hashStatus') -ieq 'Complete' -and
            [string](Get-PropertyValue $_ 'sha256') -match '^[A-Fa-f0-9]{64}$'
        })
        $observation = if ($verified.Count -eq 1) { $verified[0] } else { $null }
        $sidecars = @($SidecarRecords | Where-Object {
            [string](Get-PropertyValue $_ 'modId') -ceq $modId -and
            [string](Get-PropertyValue $_ 'kind') -ieq 'MetadataSidecar' -and
            (!$leaf -or [string](Get-PropertyValue $_ 'exactLeafName') -ieq ($leaf + '.meta'))
        })
        $verifiedSidecars = @($sidecars | Where-Object {
            [string](Get-PropertyValue $_ 'state') -ieq 'Present' -and
            [string](Get-PropertyValue $_ 'hashStatus') -ieq 'Complete' -and
            [string](Get-PropertyValue $_ 'sha256') -match '^[A-Fa-f0-9]{64}$'
        })
        $providerIdentity = $null
        $providerIdentityStatus = if ($verifiedSidecars.Count -eq 0) { 'NotObserved' } elseif ($verifiedSidecars.Count -gt 1) { 'Ambiguous' } else {
            $identity = Get-PropertyValue $verifiedSidecars[0] 'providerIdentity'
            $expectedRepository = [string](Get-PropertyValue $MetadataRecord 'repository')
            $expectedGame = [string](Get-PropertyValue $MetadataRecord 'nexusGameName')
            $expectedProviderModId = [string](Get-PropertyValue $MetadataRecord 'nexusModId')
            if ($identity -and [string](Get-PropertyValue $identity 'status') -eq 'ObservedComplete' -and
                [string](Get-PropertyValue $identity 'repository') -ieq $expectedRepository -and
                [string](Get-PropertyValue $identity 'gameName') -ieq $expectedGame -and
                [string](Get-PropertyValue $identity 'modId') -ceq $expectedProviderModId -and
                [string](Get-PropertyValue $identity 'archiveLeaf') -ieq $leaf) {
                $providerIdentity = [pscustomobject][ordered]@{
                    repository = [string](Get-PropertyValue $identity 'repository')
                    gameName = [string](Get-PropertyValue $identity 'gameName')
                    modId = Get-PropertyValue $identity 'modId'
                    fileId = Get-PropertyValue $identity 'fileId'
                    version = [string](Get-PropertyValue $identity 'version')
                    archiveLeaf = [string](Get-PropertyValue $identity 'archiveLeaf')
                }
                'Verified'
            }
            elseif ($identity) { 'Conflicting' } else { 'Incomplete' }
        }
        [pscustomobject][ordered]@{
            status = if ($observation) { 'LocallyHashedUnverifiedContents' } elseif ($observations.Count -gt 0) { 'ObservedButUnverified' } elseif ($isLeaf) { 'DownloadsLeafClaimUnobserved' } else { 'ExternalPathClaimAuthorizationRequired' }
            claimedLeafName = $leaf
            locationClass = $locationClass
            observationStatus = if ($observation) { 'HashVerified' } elseif ($observations.Count -gt 0) { [string](Get-PropertyValue $observations[0] 'state') } else { 'NotObserved' }
            sha256 = if ($observation) { ([string](Get-PropertyValue $observation 'sha256')).ToUpperInvariant() } else { $null }
            sizeBytes = if ($observation) { Get-PropertyValue $observation 'length' } else { $null }
            providerIdentityStatus = $providerIdentityStatus
            providerIdentity = $providerIdentity
            sidecarSha256 = if ($verifiedSidecars.Count -eq 1) { ([string](Get-PropertyValue $verifiedSidecars[0] 'sha256')).ToUpperInvariant() } else { $null }
        }
    }
    function Get-PlanHash([object]$Value) {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 30 -Compress))
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try { ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace('-', '') }
        finally { $algorithm.Dispose() }
    }

    $metadataByName = @{}
    foreach ($record in @($ModMetadata | Sort-Object { [string](Get-PropertyValue $_ 'name') })) {
        $name = Get-SafeName (Get-PropertyValue $record 'name')
        if (-not $name) { continue }
        $key = $name.ToUpperInvariant()
        if (-not $metadataByName.ContainsKey($key)) { $metadataByName[$key] = New-Object Collections.Generic.List[object] }
        $metadataByName[$key].Add($record)
    }
    $metadataByIdentity = @{}
    foreach ($record in @($ModMetadata)) {
        $identity = Get-IdentityKey $record
        if (-not $identity) { continue }
        if (-not $metadataByIdentity.ContainsKey($identity)) { $metadataByIdentity[$identity] = New-Object Collections.Generic.List[object] }
        $metadataByIdentity[$identity].Add($record)
    }

    $components = New-Object Collections.Generic.List[object]
    foreach ($group in @($Dependencies | Group-Object { ([string](Get-PropertyValue $_ 'pluginName')).ToUpperInvariant() } | Sort-Object Name)) {
        $records = @($group.Group | Where-Object {
            (Get-SafeName (Get-PropertyValue $_ 'pluginName')) -and
            (Get-DependencyKind $_) -and
            -not [string]::IsNullOrWhiteSpace([string](Get-PropertyValue $_ 'requiredVirtualPath'))
        } | Sort-Object { Get-DependencyKind $_ }, { [string](Get-PropertyValue $_ 'scriptName') }, { [string](Get-PropertyValue $_ 'requiredVirtualPath') })
        if ($records.Count -eq 0) { continue }
        $pluginName = Get-SafeName (Get-PropertyValue $records[0] 'pluginName')
        $providers = @($records | ForEach-Object { Get-SafeName (Get-PropertyValue $_ 'sourceProvider') } | Where-Object { $_ } | Sort-Object -Unique)
        $overrideProvider = if ($providers.Count -eq 1) { [string]$providers[0] } else { $null }
        $winnerMetadata = if ($overrideProvider -and $metadataByName.ContainsKey($overrideProvider.ToUpperInvariant())) { @($metadataByName[$overrideProvider.ToUpperInvariant()].ToArray()) } else { @() }
        $identityKeys = @($winnerMetadata | ForEach-Object { Get-IdentityKey $_ } | Where-Object { $_ } | Sort-Object -Unique)
        $lineage = if ($identityKeys.Count -eq 1) { @($metadataByIdentity[$identityKeys[0]].ToArray() | Sort-Object { [string](Get-PropertyValue $_ 'name') }) } else { @() }
        $lineageModIds = @($lineage | ForEach-Object { [string](Get-PropertyValue $_ 'modId') } | Where-Object { $_ } | Sort-Object -Unique)
        $lineageNames = @($lineage | ForEach-Object { [string](Get-PropertyValue $_ 'name') } | Where-Object { $_ } | Sort-Object -Unique)
        $lineageArchiveHashes = @($SourceArchives | Where-Object {
            [string](Get-PropertyValue $_ 'modId') -in $lineageModIds -and
            [string](Get-PropertyValue $_ 'kind') -ieq 'InstallationArchive' -and
            [string](Get-PropertyValue $_ 'state') -ieq 'Present' -and
            [string](Get-PropertyValue $_ 'hashStatus') -ieq 'Complete' -and
            [string](Get-PropertyValue $_ 'sha256') -match '^[A-Fa-f0-9]{64}$'
        } | ForEach-Object { ([string](Get-PropertyValue $_ 'sha256')).ToUpperInvariant() } | Sort-Object -Unique)
        $candidates = @($lineage | ForEach-Object {
            $archive = Get-ArchiveClaim -MetadataRecord $_ -ArchiveRecords $SourceArchives -SidecarRecords $SourceArchiveSidecars
            $contentInspection = $null
            if ([string]$archive.sha256 -match '^[A-Fa-f0-9]{64}$') {
                $observedInspections = @($ArchiveInspections | Where-Object {
                    [string](Get-PropertyValue $_ 'archiveSha256') -ieq [string]$archive.sha256
                })
                $completeInspections = @($observedInspections | Where-Object {
                    [string](Get-PropertyValue $_ 'status') -ceq 'Complete' -and
                    [string](Get-PropertyValue $_ 'entrySetSha256') -match '^[A-Fa-f0-9]{64}$' -and
                    [string](Get-PropertyValue $_ 'evidenceId') -match '^archive-content\.[a-f0-9]{24}$' -and
                    [long](Get-PropertyValue $_ 'archiveLength') -eq [long]$archive.sizeBytes
                })
                $selectedInspection = if ($completeInspections.Count -eq 1) { $completeInspections[0] } else { $null }
                # Wrap the complete conditional expression. PowerShell enumerates output
                # from an if assignment, so an inspection with exactly one match would
                # otherwise become a scalar string and fail under StrictMode at .Count.
                $matchedPaths = @(if ($selectedInspection) {
                    @((Get-PropertyValue $selectedInspection 'matches') | Where-Object {
                        [string](Get-PropertyValue $_ 'pluginName') -ieq $pluginName -and
                        [string](Get-PropertyValue $_ 'mappingRule') -in @('ArchiveRoot','ExplicitDataDirectory') -and
                        [string](Get-PropertyValue $_ 'sha256') -match '^[A-Fa-f0-9]{64}$'
                    } | ForEach-Object { ([string](Get-PropertyValue $_ 'requiredVirtualPath')).ToUpperInvariant() } | Sort-Object -Unique)
                } else { @() })
                $contentInspection = [pscustomobject][ordered]@{
                    status = if ($selectedInspection) { 'Verified' } elseif ($completeInspections.Count -gt 1) { 'Ambiguous' } elseif ($observedInspections.Count -gt 0) { 'RejectedOrIncomplete' } else { 'NotObserved' }
                    evidenceId = if ($selectedInspection) { [string](Get-PropertyValue $selectedInspection 'evidenceId') } else { $null }
                    entrySetSha256 = if ($selectedInspection) { ([string](Get-PropertyValue $selectedInspection 'entrySetSha256')).ToUpperInvariant() } else { $null }
                    inspectedEntryCount = if ($selectedInspection) { [int](Get-PropertyValue $selectedInspection 'inspectedEntryCount') } else { 0 }
                    fomodStatus = if ($selectedInspection) { [string](Get-PropertyValue $selectedInspection 'fomodStatus') } else { $null }
                    suppliedRequiredFileCount = [int]$matchedPaths.Count
                    requiredFileCount = [int]$records.Count
                    coverage = if (-not $selectedInspection) { 'Unverified' } elseif ($matchedPaths.Count -eq $records.Count) { 'Complete' } elseif ($matchedPaths.Count -gt 0) { 'Partial' } else { 'None' }
                }
            }
            $candidateModId = [string](Get-PropertyValue $_ 'modId')
            $candidateAssessments = @(if ([string]$archive.sha256 -match '^[A-Fa-f0-9]{64}$') {
                @($ArchiveCandidateAssessments | Where-Object {
                    [string](Get-PropertyValue $_ 'archiveSha256') -ieq [string](Get-PropertyValue $archive 'sha256') -and
                    [string](Get-PropertyValue $_ 'primaryPluginName') -ieq $pluginName -and
                    [string](Get-PropertyValue $_ 'evidenceId') -match '^archive-candidate\.[a-f0-9]{24}$' -and
                    [string](Get-PropertyValue $_ 'evidenceFingerprint') -match '^[A-Fa-f0-9]{64}$'
                })
            })
            $candidateAssessment = if ($candidateAssessments.Count -eq 1) { $candidateAssessments[0] } else { $null }
            $candidateRepository = [string](Get-PropertyValue $_ 'repository')
            $candidateGame = [string](Get-PropertyValue $_ 'nexusGameName')
            $candidateProviderModId = Get-PropertyValue $_ 'nexusModId'
            $nexusGameDomain = switch ($candidateGame.Trim().ToLowerInvariant()) {
                'skyrimse' { 'skyrimspecialedition' }
                'skyrim special edition' { 'skyrimspecialedition' }
                'skyrimspecialedition' { 'skyrimspecialedition' }
                default { $null }
            }
            $manualAcquisition = if ($candidateRepository -ieq 'Nexus' -and $nexusGameDomain -and
                $null -ne $candidateProviderModId -and [long]$candidateProviderModId -gt 0 -and
                -not [string]::IsNullOrWhiteSpace([string]$archive.claimedLeafName)) {
                [pscustomobject][ordered]@{
                    status = 'ReadyForUserInitiatedManagerDownload'
                    method = 'NexusFilesPageThenDownloadWithManager'
                    officialFilesUri = "https://www.nexusmods.com/$nexusGameDomain/mods/$($candidateProviderModId)?tab=files"
                    expectedArchiveLeaf = [string]$archive.claimedLeafName
                    completionEvidence = 'The authorized MO2 downloads directory must contain the exact archive leaf and its .meta sidecar; Grid then hashes both and verifies repository, game, mod ID, file ID, version, and leaf.'
                }
            } else { $null }
            [pscustomobject][ordered]@{
                modId = $candidateModId
                name = [string](Get-PropertyValue $_ 'name')
                enabled = [bool](Get-PropertyValue $_ 'enabled')
                version = [string](Get-PropertyValue $_ 'version')
                metadataSha256 = [string](Get-PropertyValue $_ 'metadataSha256')
                repository = [string](Get-PropertyValue $_ 'repository')
                nexusGameName = [string](Get-PropertyValue $_ 'nexusGameName')
                nexusModId = Get-PropertyValue $_ 'nexusModId'
                archiveClaim = $archive
                contentInspection = $contentInspection
                candidateAssessment = $candidateAssessment
                manualAcquisition = $manualAcquisition
            }
        })
        $componentArchiveCandidates = @($ArchiveCandidateAssessments | Where-Object {
            $assessment = $_
            $assessmentModIds = @((Get-PropertyValue $assessment 'sourceModIds'))
            $assessmentModNames = @((Get-PropertyValue $assessment 'sourceModNames'))
            [string](Get-PropertyValue $assessment 'primaryPluginName') -ieq $pluginName -and
            ([string](Get-PropertyValue $assessment 'archiveSha256')).ToUpperInvariant() -in $lineageArchiveHashes -and
            [string](Get-PropertyValue $assessment 'evidenceId') -match '^archive-candidate\.[a-f0-9]{24}$' -and
            [string](Get-PropertyValue $assessment 'evidenceFingerprint') -match '^[A-Fa-f0-9]{64}$' -and
            (@($assessmentModIds | Where-Object { [string]$_ -in $lineageModIds }).Count -gt 0 -or
                @($assessmentModNames | Where-Object { [string]$_ -in $lineageNames }).Count -gt 0)
        } | Group-Object { [string](Get-PropertyValue $_ 'evidenceId') } | ForEach-Object { $_.Group | Select-Object -First 1 } | ForEach-Object {
            $archiveLeaf = try { [IO.Path]::GetFileName([string](Get-PropertyValue $_ 'archivePath')) } catch { $null }
            $sourceModNames = @((Get-PropertyValue $_ 'sourceModNames'))
            [pscustomobject][ordered]@{
                archiveLeaf = $archiveLeaf
                providerName = @($sourceModNames | ForEach-Object { [string]$_ } | Where-Object { $_ } | Sort-Object -Unique) -join ', '
                archiveSha256 = ([string](Get-PropertyValue $_ 'archiveSha256')).ToUpperInvariant()
                primaryPluginName = [string](Get-PropertyValue $_ 'primaryPluginName')
                candidatePrimaryPluginEntryPath = [string](Get-PropertyValue $_ 'candidatePrimaryPluginEntryPath')
                candidatePrimaryArchiveEntryPath = [string](Get-PropertyValue $_ 'candidatePrimaryArchiveEntryPath')
                installedPrimaryArchiveSha256 = [string](Get-PropertyValue $_ 'installedPrimaryArchiveSha256')
                fomodSelectionEvidence = Get-PropertyValue $_ 'fomodSelectionEvidence'
                installedVersion = [string](Get-PropertyValue $_ 'installedVersion')
                candidateVersion = [string](Get-PropertyValue $_ 'candidateVersion')
                versionRelation = [string](Get-PropertyValue $_ 'versionRelation')
                payloadPairStatus = [string](Get-PropertyValue $_ 'payloadPairStatus')
                candidateRole = [string](Get-PropertyValue $_ 'candidateRole')
                mixingPolicy = [string](Get-PropertyValue $_ 'mixingPolicy')
                status = [string](Get-PropertyValue $_ 'status')
                compatibilityStatus = [string](Get-PropertyValue $_ 'compatibilityStatus')
                requiredEvidence = @((Get-PropertyValue $_ 'requiredEvidence'))
                dependentPlugins = @((Get-PropertyValue $_ 'dependentPlugins'))
                evidenceFingerprint = ([string](Get-PropertyValue $_ 'evidenceFingerprint')).ToUpperInvariant()
                evidenceId = [string](Get-PropertyValue $_ 'evidenceId')
                sourceModIds = @((Get-PropertyValue $_ 'sourceModIds'))
                sourceModNames = $sourceModNames
            }
        } | Sort-Object archiveLeaf,candidateRole,evidenceId)
        $hashedCandidates = @($candidates | Where-Object { [string]$_.archiveClaim.status -eq 'LocallyHashedUnverifiedContents' })
        $verifiedCandidates = @($hashedCandidates | Where-Object { [string]$_.contentInspection.status -eq 'Verified' })
        $requiredPathKeys = @($records | ForEach-Object { ([string](Get-PropertyValue $_ 'requiredVirtualPath')).ToUpperInvariant() } | Sort-Object -Unique)
        $suppliedPathKeys = @($verifiedCandidates | ForEach-Object {
            $evidenceId = [string]$_.contentInspection.evidenceId
            $inspection = @($ArchiveInspections | Where-Object { [string](Get-PropertyValue $_ 'evidenceId') -ceq $evidenceId })
            if ($inspection.Count -eq 1) {
                @((Get-PropertyValue $inspection[0] 'matches') | Where-Object {
                    [string](Get-PropertyValue $_ 'pluginName') -ieq $pluginName -and
                    [string](Get-PropertyValue $_ 'mappingRule') -in @('ArchiveRoot','ExplicitDataDirectory')
                } | ForEach-Object { ([string](Get-PropertyValue $_ 'requiredVirtualPath')).ToUpperInvariant() })
            }
        } | Sort-Object -Unique)
        $allRequiredFilesSupplied = $requiredPathKeys.Count -gt 0 -and @($requiredPathKeys | Where-Object { $_ -notin $suppliedPathKeys }).Count -eq 0
        $selectionCandidates = @($componentArchiveCandidates | Where-Object { [string]$_.status -eq 'SelectionRequired' })
        $compatibilityCandidates = @($componentArchiveCandidates | Where-Object {
            [string]$_.compatibilityStatus -eq 'RequiresDependentCompatibilityEvidence'
        })
        $exactRestorationCandidates = @($componentArchiveCandidates | Where-Object {
            [string]$_.candidateRole -eq 'ExactRestoration' -and [string]$_.status -eq 'Complete'
        })
        $state = if ($identityKeys.Count -ne 1 -or $candidates.Count -eq 0) {
            'ComponentLineageEvidenceRequired'
        } elseif ($hashedCandidates.Count -eq 0) {
            'AcquisitionEvidenceRequired'
        } elseif ($allRequiredFilesSupplied -and $exactRestorationCandidates.Count -gt 0) {
            'RepairSourceVerified'
        } elseif ($exactRestorationCandidates.Count -gt 0) {
            'ArchivePayloadVerificationRequired'
        } elseif ($selectionCandidates.Count -gt 0) {
            'CandidateSelectionRequired'
        } elseif ($compatibilityCandidates.Count -gt 0) {
            'CandidateCompatibilityEvidenceRequired'
        } elseif ($allRequiredFilesSupplied) {
            'RepairSourceVerified'
        } elseif ($verifiedCandidates.Count -gt 0) {
            'AdditionalSourceEvidenceRequired'
        } else {
            'ArchiveContentInspectionRequired'
        }
        $nextAction = switch ($state) {
            'ComponentLineageEvidenceRequired' { 'Resolve the component identity before selecting any repair source.' }
            'AcquisitionEvidenceRequired' { 'Acquire an exact archive with provider identity, size, and SHA-256 evidence, then inspect its contents in quarantine.' }
            'ArchiveContentInspectionRequired' { 'Inspect the hashed archive and prove which required component files it supplies before materializing a repair.' }
            'CandidateSelectionRequired' { 'Record the exact FOMOD selection vector, then reassess the selected plugin and archive payload before any repair.' }
            'CandidateCompatibilityEvidenceRequired' { 'Resolve every required dependent-plugin and winner-replacement compatibility gate before treating this version-changing archive as a repair source.' }
            'ArchivePayloadVerificationRequired' { 'The plugin and packaged asset archive match the installed version, but the packaged archive payload must be verified before any missing file is restored.' }
            'AdditionalSourceEvidenceRequired' { 'The inspected archive supplies only part of this component; verify another exact lineage archive for the remaining files.' }
            default { 'Build an inert file-level repair specification from the verified archive-content evidence.' }
        }
        $components.Add([pscustomobject][ordered]@{
            pluginName = $pluginName
            missingDependencyCount = [int]$records.Count
            requiredFileSetSha256 = Get-PlanHash @($records | ForEach-Object {
                [pscustomobject][ordered]@{
                    dependencyKind = Get-DependencyKind $_
                    sourceName = if ((Get-DependencyKind $_) -eq 'PapyrusScript') { [string](Get-PropertyValue $_ 'scriptName') } else { $null }
                    virtualPath = ([string](Get-PropertyValue $_ 'requiredVirtualPath')).ToUpperInvariant()
                    referenceCount = [int](Get-PropertyValue $_ 'referenceCount')
                }
            })
            totalReferenceCount = [long]($records | Measure-Object -Property referenceCount -Sum).Sum
            overrideProvider = $overrideProvider
            overrideProviderRole = if ($overrideProvider) { 'OverrideProvider' } else { $null }
            lineageStatus = if ($identityKeys.Count -eq 1) { 'ExactMetadataIdentity' } elseif ($identityKeys.Count -gt 1) { 'AmbiguousMetadataIdentity' } else { 'MetadataIdentityUnavailable' }
            lineageCandidates = $candidates
            archiveCandidates = $componentArchiveCandidates
            recoveryState = $state
            nextAction = $nextAction
            requiredFiles = @($records | Select-Object -First 12 | ForEach-Object {
                [pscustomobject][ordered]@{
                    dependencyKind = Get-DependencyKind $_
                    sourceName = if ((Get-DependencyKind $_) -eq 'PapyrusScript') { [string](Get-PropertyValue $_ 'scriptName') } else { $null }
                    virtualPath = [string](Get-PropertyValue $_ 'requiredVirtualPath')
                    referenceCount = [int](Get-PropertyValue $_ 'referenceCount')
                }
            })
            omittedRequiredFileCount = [Math]::Max(0, $records.Count - 12)
        })
    }

    $componentArray = $components.ToArray()
    $manualAcquisitionReadyCount = 0
    foreach ($component in @($componentArray | Where-Object { [string]$_.recoveryState -in @('AcquisitionEvidenceRequired', 'AdditionalSourceEvidenceRequired') })) {
        $routes = @($component.lineageCandidates | Where-Object {
            $null -ne $_.manualAcquisition -and [string]$_.archiveClaim.status -ne 'LocallyHashedUnverifiedContents'
        } | Group-Object { [string]$_.manualAcquisition.officialFilesUri + [char]31 + ([string]$_.manualAcquisition.expectedArchiveLeaf).ToUpperInvariant() } | ForEach-Object { $_.Group | Select-Object -First 1 })
        if ([string]$component.recoveryState -eq 'AcquisitionEvidenceRequired') {
            $winnerRoutes = @($routes | Where-Object { [string]$_.name -ieq [string]$component.overrideProvider })
            if ($winnerRoutes.Count -gt 0) { $routes = $winnerRoutes }
        }
        if ($routes.Count -eq 1) { $manualAcquisitionReadyCount++ }
    }
    $summary = [pscustomobject][ordered]@{
        affectedPluginCount = [int]$componentArray.Count
        missingDependencyCount = [long]($componentArray | Measure-Object -Property missingDependencyCount -Sum).Sum
        lineageEvidenceRequiredCount = [int]@($componentArray | Where-Object recoveryState -eq 'ComponentLineageEvidenceRequired').Count
        acquisitionEvidenceRequiredCount = [int]@($componentArray | Where-Object recoveryState -eq 'AcquisitionEvidenceRequired').Count
        archiveInspectionRequiredCount = [int]@($componentArray | Where-Object recoveryState -eq 'ArchiveContentInspectionRequired').Count
        candidateSelectionRequiredCount = [int]@($componentArray | Where-Object recoveryState -eq 'CandidateSelectionRequired').Count
        candidateCompatibilityEvidenceRequiredCount = [int]@($componentArray | Where-Object recoveryState -eq 'CandidateCompatibilityEvidenceRequired').Count
        archivePayloadVerificationRequiredCount = [int]@($componentArray | Where-Object recoveryState -eq 'ArchivePayloadVerificationRequired').Count
        exactRestorationCandidateCount = [int]@($componentArray | ForEach-Object { @($_.archiveCandidates) } | Group-Object evidenceId | ForEach-Object { $_.Group | Select-Object -First 1 } | Where-Object { [string]$_.candidateRole -eq 'ExactRestoration' }).Count
        versionChangingCandidateCount = [int]@($componentArray | ForEach-Object { @($_.archiveCandidates) } | Group-Object evidenceId | ForEach-Object { $_.Group | Select-Object -First 1 } | Where-Object { [string]$_.candidateRole -in @('CompleteUpdateCandidate','CompleteRollbackCandidate','CompleteAlternativeCandidate') }).Count
        additionalSourceEvidenceRequiredCount = [int]@($componentArray | Where-Object recoveryState -eq 'AdditionalSourceEvidenceRequired').Count
        manualAcquisitionReadyCount = [int]$manualAcquisitionReadyCount
        repairReadyCount = [int]@($componentArray | Where-Object recoveryState -eq 'RepairSourceVerified').Count
    }
    $status = if ($componentArray.Count -eq 0) { 'NotRequired' } elseif ($summary.lineageEvidenceRequiredCount -gt 0) { 'ComponentLineageEvidenceRequired' } elseif ($summary.acquisitionEvidenceRequiredCount -gt 0) { 'AcquisitionEvidenceRequired' } elseif ($summary.candidateSelectionRequiredCount -gt 0) { 'CandidateSelectionRequired' } elseif ($summary.candidateCompatibilityEvidenceRequiredCount -gt 0) { 'CandidateCompatibilityEvidenceRequired' } elseif ($summary.archivePayloadVerificationRequiredCount -gt 0) { 'ArchivePayloadVerificationRequired' } elseif ($summary.archiveInspectionRequiredCount -gt 0) { 'ArchiveContentInspectionRequired' } elseif ($summary.additionalSourceEvidenceRequiredCount -gt 0) { 'AdditionalSourceEvidenceRequired' } else { 'RepairSourcesVerified' }
    $unsigned = [pscustomobject][ordered]@{
        schemaVersion = 1; caseId = $CaseId; planKind = 'MissingPluginFileComponentRecovery'
        contextFingerprint = $ContextFingerprint.ToUpperInvariant(); status = $status; summary = $summary
        components = $componentArray
        invariants = @(
            'The selected live MO2 profile is the repair baseline; no separate or original modlist is required.',
            'The winning plugin provider is an OverrideProvider, not proof of missing-file ownership.',
            'Component lineage requires an exact repository, game, and provider mod ID match.',
            'An installationFile value is a claim until the exact archive is observed and hashed.',
            'A hashed archive is not a repair source until bounded content inspection proves the required scripts, meshes, or textures.',
            'Archive candidate classification compares exact hashes and package structure; it is immutable evidence, not a recommendation or repair authority.',
            'A version-changing plugin and asset pair requires dependent-plugin compatibility evidence, and an exact restoration still requires packaged payload verification.',
            'Archive content counts only when an inspected member maps unambiguously from the archive root or an explicit Data directory to the required virtual path.',
            'A Nexus download sidecar is retained only as redacted provider identity evidence and must match the component metadata and archive leaf.',
            'A manual acquisition link opens only the official Nexus Files page; the user still selects Download with Manager and Grid trusts nothing until local archive and sidecar evidence are verified.',
            'This plan performs no download, extraction, profile edit, load-order edit, or active-tree mutation.'
        )
    }
    $hash = Get-PlanHash $unsigned
    $unsigned | Add-Member -NotePropertyName planId -NotePropertyValue ('component-recovery-' + $hash.Substring(0, 24).ToLowerInvariant())
    $unsigned | Add-Member -NotePropertyName planSha256 -NotePropertyValue $hash
    $unsigned
}

function Get-GridSkyrimComponentRecoveryPlanFromCase {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseStoreRoot,
        [Parameter(Mandatory)][string]$CaseDirectory
    )
    if (-not (Get-Command Test-GridDiagnosticCaseSeal -ErrorAction SilentlyContinue)) {
        throw 'The shared Grid health module must be imported before reviewing a component recovery plan.'
    }
    $root = [IO.Path]::GetFullPath($CaseStoreRoot)
    $case = [IO.Path]::GetFullPath($CaseDirectory)
    $seal = Test-GridDiagnosticCaseSeal -StoreRoot $root -CaseDirectory $case
    if (-not $seal.IsValid) { throw ('BaselineSealInvalid: ' + ($seal.Errors -join '; ')) }

    function Read-SealedJson([string]$RelativePath) {
        $normalized = $RelativePath.Replace('\','/')
        if (@($seal.Manifest.artifacts | Where-Object { [string]$_.path -ceq $normalized }).Count -ne 1) {
            throw "BaselineArtifactMissing: $normalized"
        }
        $path = Join-Path $case $RelativePath
        Get-Content -Raw -LiteralPath $path -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    }
    function Read-SealedNdjson([string]$RelativePath) {
        $normalized = $RelativePath.Replace('\','/')
        if (@($seal.Manifest.artifacts | Where-Object { [string]$_.path -ceq $normalized }).Count -ne 1) {
            throw "BaselineArtifactMissing: $normalized"
        }
        $path = Join-Path $case $RelativePath
        @($values = Get-Content -LiteralPath $path -ReadCount 1 -ErrorAction Stop |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
            ForEach-Object { [string]$_ | ConvertFrom-Json -ErrorAction Stop }; $values)
    }

    $inspection = Read-SealedJson 'inventory\plugin-script-dependencies.v1.json'
    $assetInspection = Read-SealedJson 'inventory\plugin-asset-dependencies.v1.json'
    if ([string]$inspection.status -ine 'complete' -or [string]$assetInspection.status -ine 'complete') {
        throw "BaselineInspectionIncomplete: attached-script status is '$($inspection.status)' and plugin-asset status is '$($assetInspection.status)'."
    }
    $installation = Read-SealedJson 'installation\installation-baseline.v1.json'
    $contextFingerprint = [string]$installation.summary.environmentFingerprint
    if ($contextFingerprint -notmatch '^[A-Fa-f0-9]{64}$') {
        throw 'BaselineContextInvalid: environment fingerprint is absent or invalid.'
    }
    $metadata = Read-SealedJson 'provenance\mod-metadata.v1.json'
    $dependencies = @(Read-SealedNdjson 'inventory\plugin-script-dependencies.v1.ndjson') + @(Read-SealedNdjson 'inventory\plugin-asset-dependencies.v1.ndjson')
    $archives = Read-SealedNdjson 'provenance\source-archives.v1.ndjson'
    $sidecars = Read-SealedNdjson 'provenance\source-archive-sidecars.v1.ndjson'
    $inspectionPath = 'provenance\source-archive-inspections.v1.ndjson'
    $inspections = if (@($seal.Manifest.artifacts | Where-Object { [string]$_.path -ceq $inspectionPath.Replace('\','/') }).Count -eq 1) { Read-SealedNdjson $inspectionPath } else { @() }
    $assessmentPath = 'provenance\source-archive-candidate-assessments.v1.ndjson'
    $assessments = if (@($seal.Manifest.artifacts | Where-Object { [string]$_.path -ceq $assessmentPath.Replace('\','/') }).Count -eq 1) { Read-SealedNdjson $assessmentPath } else { @() }
    New-GridSkyrimComponentRecoveryPlan -CaseId ([string]$seal.Manifest.caseId) `
        -ContextFingerprint $contextFingerprint -Dependencies @($dependencies) `
        -ModMetadata @($metadata.records) -SourceArchives @($archives) -SourceArchiveSidecars @($sidecars) `
        -ArchiveInspections @($inspections) -ArchiveCandidateAssessments @($assessments)
}

function Test-GridSkyrimComponentRecoveryPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Plan,
        [Parameter(Mandatory)][string]$ExpectedCaseId
    )
    $errors=@()
    if([int]$Plan.schemaVersion-ne1){$errors+='Unsupported component recovery plan schemaVersion.'}
    if([string]$Plan.caseId-cne$ExpectedCaseId){$errors+='Component recovery plan caseId does not match its sealed case.'}
    if([string]$Plan.planKind-cne'MissingPluginFileComponentRecovery'){$errors+='Component recovery plan kind is invalid.'}
    if([string]$Plan.contextFingerprint-notmatch'^[A-Fa-f0-9]{64}$'){$errors+='Component recovery context fingerprint is invalid.'}
    if([string]$Plan.planSha256-notmatch'^[A-Fa-f0-9]{64}$'){$errors+='Component recovery plan SHA-256 is invalid.'}
    if($null-eq$Plan.summary-or$null-eq$Plan.components-or$null-eq$Plan.invariants){$errors+='Component recovery plan content is incomplete.'}
    if($errors.Count-eq0){
        $unsigned=[pscustomobject][ordered]@{
            schemaVersion=[int]$Plan.schemaVersion;caseId=[string]$Plan.caseId;planKind=[string]$Plan.planKind
            contextFingerprint=([string]$Plan.contextFingerprint).ToUpperInvariant();status=[string]$Plan.status;summary=$Plan.summary
            components=@($Plan.components);invariants=@($Plan.invariants)
        }
        $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($unsigned|ConvertTo-Json -Depth 30 -Compress))
        $algorithm=[Security.Cryptography.SHA256]::Create()
        try{$expectedHash=([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace('-','')}finally{$algorithm.Dispose()}
        if([string]$Plan.planSha256-cne$expectedHash){$errors+='Component recovery plan SHA-256 does not match its content.'}
        if([string]$Plan.planId-cne('component-recovery-'+$expectedHash.Substring(0,24).ToLowerInvariant())){$errors+='Component recovery plan ID does not match its content.'}
    }
    [pscustomobject][ordered]@{IsValid=($errors.Count-eq0);Errors=@($errors)}
}

function Read-GridSkyrimSealedComponentRecoveryPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseStoreRoot,
        [Parameter(Mandatory)][string]$CaseDirectory
    )
    if(-not(Get-Command Test-GridDiagnosticCaseSeal -ErrorAction SilentlyContinue)){throw 'The shared Grid health module must be imported before reviewing a component recovery plan.'}
    $root=[IO.Path]::GetFullPath($CaseStoreRoot);$case=[IO.Path]::GetFullPath($CaseDirectory)
    $seal=Test-GridDiagnosticCaseSeal -StoreRoot $root -CaseDirectory $case
    if(-not$seal.IsValid){throw ('BaselineSealInvalid: '+($seal.Errors-join'; '))}
    $relative='repair/component-recovery-plan.v1.json'
    if(@($seal.Manifest.artifacts|Where-Object{[string]$_.path-ceq$relative}).Count-ne1){throw "BaselineArtifactMissing: $relative"}
    $plan=Get-Content -LiteralPath (Join-Path $case 'repair\component-recovery-plan.v1.json') -Raw|ConvertFrom-Json -ErrorAction Stop
    $validation=Test-GridSkyrimComponentRecoveryPlan -Plan $plan -ExpectedCaseId ([string]$seal.Manifest.caseId)
    if(-not$validation.IsValid){throw ('ComponentRecoveryPlanInvalid: '+($validation.Errors-join'; '))}
    $plan
}

function New-GridSkyrimComponentRecoveryActionManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Plan,
        [AllowEmptyCollection()][string[]]$AffectedMods = @()
    )

    $validation = Test-GridSkyrimComponentRecoveryPlan -Plan $Plan -ExpectedCaseId ([string]$Plan.caseId)
    if (-not $validation.IsValid) {
        throw ('ComponentRecoveryPlanInvalid: ' + ($validation.Errors -join '; '))
    }

    function Get-PropertyValue([AllowNull()][object]$Object, [string]$Name) {
        if ($null -eq $Object) { return $null }
        $property = $Object.PSObject.Properties[$Name]
        if ($property) { return $property.Value }
        $null
    }
    function Get-ManifestHash([object]$Value) {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 40 -Compress))
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try { ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace('-', '') }
        finally { $algorithm.Dispose() }
    }

    $routeRows = [Collections.Generic.List[object]]::new()
    $sourceResolutionRows = [Collections.Generic.List[object]]::new()
    foreach ($component in @($Plan.components | Sort-Object pluginName)) {
        if ([string]$component.recoveryState -notin @('AcquisitionEvidenceRequired', 'AdditionalSourceEvidenceRequired')) { continue }
        $availableRoutes = @($component.lineageCandidates | ForEach-Object {
            $candidate = $_
            $manual = Get-PropertyValue $candidate 'manualAcquisition'
            if ($null -eq $manual -or [string](Get-PropertyValue $candidate.archiveClaim 'status') -eq 'LocallyHashedUnverifiedContents') { return }
            $uri = [string](Get-PropertyValue $manual 'officialFilesUri')
            $leaf = [string](Get-PropertyValue $manual 'expectedArchiveLeaf')
            if ([string]::IsNullOrWhiteSpace($uri) -or [string]::IsNullOrWhiteSpace($leaf)) { return }
            [pscustomobject][ordered]@{
                candidate = $candidate
                manual = $manual
                routeKey = $uri + [char]31 + $leaf.ToUpperInvariant()
                officialFilesUri = $uri
                expectedArchiveLeaf = $leaf
            }
        } | Group-Object routeKey | ForEach-Object { $_.Group | Select-Object -First 1 })

        $candidateRoutes = @(if ([string]$component.recoveryState -eq 'AcquisitionEvidenceRequired') {
            $winnerRoutes = @($availableRoutes | Where-Object { [string](Get-PropertyValue $_.candidate 'name') -ieq [string]$component.overrideProvider })
            if ($winnerRoutes.Count -gt 0) { $winnerRoutes } elseif ($availableRoutes.Count -eq 1) { $availableRoutes } else { @() }
        } else {
            $availableRoutes
        })
        if ($candidateRoutes.Count -ne 1) {
            $sourceResolutionRows.Add([pscustomobject][ordered]@{
                pluginName = [string]$component.pluginName
                currentOverrideProvider = [string]$component.overrideProvider
                missingDependencyCount = [int]$component.missingDependencyCount
                requiredFileExamples = @($component.requiredFiles | ForEach-Object { [string]$_.virtualPath } | Where-Object { $_ })
                omittedRequiredFileCount = [int]$component.omittedRequiredFileCount
                status = if ($candidateRoutes.Count -gt 1 -or $availableRoutes.Count -gt 1) { 'ArchiveIdentityAmbiguous' } else { 'SourceIdentityUnresolved' }
                nextAction = if ($candidateRoutes.Count -gt 1 -or $availableRoutes.Count -gt 1) {
                    "Grid found $($availableRoutes.Count) unverified archive claims for this component and must prove which single archive is the next evidence source before requesting a download."
                } else {
                    'Resolve the component source identity before requesting an archive download.'
                }
            })
            continue
        }

        $route = $candidateRoutes[0]
        $candidate = $route.candidate
        $manual = $route.manual
        $uri = [string]$route.officialFilesUri
        $leaf = [string]$route.expectedArchiveLeaf
            $routeRows.Add([pscustomobject][ordered]@{
                routeKey = [string]$route.routeKey
                pluginName = [string]$component.pluginName
                missingDependencyCount = [int]$component.missingDependencyCount
                modName = [string]$candidate.name
                installedVersionClaim = [string]$candidate.version
                officialFilesUri = $uri
                expectedArchiveLeaf = $leaf
                completionEvidence = [string](Get-PropertyValue $manual 'completionEvidence')
            })
    }

    $manualAcquisitions = @($routeRows | Group-Object routeKey | ForEach-Object {
        $entries = @($_.Group)
        $pluginImpacts = @($entries | Group-Object pluginName | ForEach-Object { $_.Group | Select-Object -First 1 })
        $uri = [string]$entries[0].officialFilesUri
        $leaf = [string]$entries[0].expectedArchiveLeaf
        $routeHash = Get-ManifestHash ([pscustomobject][ordered]@{ officialFilesUri = $uri; expectedArchiveLeaf = $leaf })
        [pscustomobject][ordered]@{
            actionId = 'source-acquisition-' + $routeHash.Substring(0, 24).ToLowerInvariant()
            modNames = @($entries | ForEach-Object { [string]$_.modName } | Where-Object { $_ } | Sort-Object -Unique)
            installedVersionClaims = @($entries | ForEach-Object { [string]$_.installedVersionClaim } | Where-Object { $_ } | Sort-Object -Unique)
            expectedArchiveLeaf = $leaf
            officialFilesUri = $uri
            affectedPlugins = @($pluginImpacts | ForEach-Object { [string]$_.pluginName } | Sort-Object -Unique)
            missingDependencyCount = [long](($pluginImpacts | Measure-Object -Property missingDependencyCount -Sum).Sum)
            instruction = 'Recover the missing bytes from the official Files page by choosing Download with Manager for the exact expected archive. This does not replace or recreate the live modlist. Do not install it yet.'
            completionEvidence = [string]$entries[0].completionEvidence
        }
    } | Sort-Object @{ Expression = 'missingDependencyCount'; Descending = $true }, expectedArchiveLeaf, officialFilesUri)

    $lineageRequirements = @(@($Plan.components | Where-Object { [string]$_.recoveryState -eq 'ComponentLineageEvidenceRequired' } | Sort-Object pluginName | ForEach-Object {
        [pscustomobject][ordered]@{
            pluginName = [string]$_.pluginName
            currentOverrideProvider = [string]$_.overrideProvider
            missingDependencyCount = [int]$_.missingDependencyCount
            requiredFileExamples = @($_.requiredFiles | ForEach-Object { [string]$_.virtualPath } | Where-Object { $_ })
            omittedRequiredFileCount = [int]$_.omittedRequiredFileCount
            status = 'SourceIdentityUnresolved'
            nextAction = [string]$_.nextAction
        }
    }) + @($sourceResolutionRows) | Sort-Object pluginName)

    $archiveCandidates = @($Plan.components | ForEach-Object {
        $component = $_
        @($component.archiveCandidates) | ForEach-Object {
            [pscustomobject][ordered]@{
                pluginName = [string]$component.pluginName
                providerName = [string]$_.providerName
                archiveLeaf = [string]$_.archiveLeaf
                candidateRole = [string]$_.candidateRole
                compatibilityStatus = [string]$_.compatibilityStatus
                evidenceId = [string]$_.evidenceId
            }
        }
    } | Where-Object { $_.evidenceId } | Group-Object evidenceId | ForEach-Object { $_.Group | Select-Object -First 1 })
    $versionChangingCandidates = @($archiveCandidates | Where-Object { $_.candidateRole -in @('CompleteUpdateCandidate', 'CompleteRollbackCandidate', 'CompleteAlternativeCandidate') } | Sort-Object pluginName, providerName, archiveLeaf)
    $exactRestorationCandidates = @($archiveCandidates | Where-Object { $_.candidateRole -eq 'ExactRestoration' } | Sort-Object pluginName, providerName, archiveLeaf)
    $affectedModList = @($AffectedMods | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
    $affectedPluginList = @($Plan.components | ForEach-Object { [string]$_.pluginName } | Where-Object { $_ } | Sort-Object -Unique)

    $unsigned = [pscustomobject][ordered]@{
        schemaVersion = 1
        planId = [string]$Plan.planId
        planSha256 = [string]$Plan.planSha256
        status = [string]$Plan.status
        summary = [pscustomobject][ordered]@{
            affectedModCount = [int]$affectedModList.Count
            affectedPluginCount = [int]$affectedPluginList.Count
            missingDependencyCount = [long]$Plan.summary.missingDependencyCount
            exactArchiveAcquisitionCount = [int]$manualAcquisitions.Count
            lineageEvidenceRequiredCount = [int]$lineageRequirements.Count
            versionChangingCandidateCount = [int]$versionChangingCandidates.Count
            exactRestorationCandidateCount = [int]$exactRestorationCandidates.Count
            provenUpdateRequiredCount = 0
            provenReinstallationRequiredCount = 0
            plannedPatchChangeCount = 0
            repairReadyCount = [int]$Plan.summary.repairReadyCount
            mutationAuthorized = $false
        }
        manualAcquisitions = $manualAcquisitions
        lineageRequirements = $lineageRequirements
        versionChangingCandidates = $versionChangingCandidates
        exactRestorationCandidates = $exactRestorationCandidates
        affectedMods = $affectedModList
        affectedPlugins = $affectedPluginList
        gridFollowUpActions = @(
            'The selected live MO2 profile and its current ordering are the repair baseline; no separate or original modlist is required.',
            'After MO2 receives an exact archive and its .meta sidecar, Grid will hash and verify both against the bound provider identity.',
            'Grid will inspect newly received archives and continue the sealed recovery plan without recapturing the whole profile.',
            'Grid will prepare a separate exact staged repair, verification plan, and rollback before requesting mutation authorization.'
        )
        plannedPatchChanges = @()
        classifications = @(
            'Archive acquisition is evidence gathering; it is not proof that an update or reinstall is required.',
            'No mod update, reinstall, patch creation, profile edit, or load-order change is authorized by this manifest.',
            'Lineage requirements are unresolved source identities, not download recommendations.'
        )
    }
    $manifestHash = Get-ManifestHash $unsigned
    $unsigned | Add-Member -NotePropertyName manifestId -NotePropertyValue ('recovery-actions-' + $manifestHash.Substring(0, 24).ToLowerInvariant())
    $unsigned | Add-Member -NotePropertyName manifestSha256 -NotePropertyValue $manifestHash
    $unsigned
}
