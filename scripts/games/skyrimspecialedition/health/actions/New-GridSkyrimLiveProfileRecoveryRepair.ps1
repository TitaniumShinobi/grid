#requires -Version 5.1

<#
.SYNOPSIS
Materializes a staged, reversible repair for verified missing files in the live MO2 profile.
.DESCRIPTION
The selected live profile is authoritative. The function consumes one sealed
component-recovery case, rechecks the current enabled mod/plugin selections,
and prepares complete replacement trees outside the active mods directory.
Only files that are still absent from the live loose-file view and whose exact
archive members were sealed and hashed are added. The function never changes
the active profile or mod tree; it emits a normal one-use-authorized repair
specification and a sealed RepairPlanning case.
#>

function New-GridSkyrimLiveProfileRecoveryRepair {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseStoreRoot,
        [Parameter(Mandatory)][string]$RecoveryCaseDirectory,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$AuthorizedReadPaths,
        [Parameter(Mandatory)][string]$DiagnosticsExecutable,
        [switch]$PassThru
    )

    $ErrorActionPreference = 'Stop'
    Set-StrictMode -Version Latest
    foreach ($command in @(
        'Test-GridDiagnosticCaseSeal', 'Get-GridCanonicalJsonSha256',
        'New-GridCaseStoreTransaction', 'Write-GridCaseStoreArtifact',
        'Seal-GridCaseStoreRun', 'Seal-GridDiagnosticCase',
        'Get-GridRepairTreeObservation', 'Copy-GridRepairTree',
        'New-GridSkyrimModChainRepairProposal', 'Write-GridJsonAtomic',
        'ConvertTo-GridNativeArgument')) {
        if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
            throw "LiveProfileRecoveryPrerequisiteMissing: $command"
        }
    }

    function Get-PropertyValue([AllowNull()][object]$Object, [string]$Name) {
        if ($null -eq $Object) { return $null }
        $property = $Object.PSObject.Properties[$Name]
        if ($property) { return $property.Value }
        $null
    }
    function Test-WithinAuthorizedRead([string]$LiteralPath) {
        $path = [IO.Path]::GetFullPath($LiteralPath).TrimEnd('\')
        foreach ($candidate in @($AuthorizedReadPaths)) {
            if ([string]::IsNullOrWhiteSpace([string]$candidate)) { continue }
            $root = [IO.Path]::GetFullPath([string]$candidate).TrimEnd('\')
            if ($path -ieq $root -or $path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { return $true }
        }
        $false
    }
    function Assert-SafeLiveFile([string]$LiteralPath) {
        $path = [IO.Path]::GetFullPath($LiteralPath)
        if (-not (Test-WithinAuthorizedRead $path)) { throw "LiveProfileRecoveryReadUnauthorized: $path" }
        $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "LiveProfileRecoveryReparsePointRefused: $path" }
        $path
    }
    function Read-SealedJson([string]$RelativePath) {
        $normalized = $RelativePath.Replace('\','/')
        if (@($seal.Manifest.artifacts | Where-Object { [string]$_.path -ceq $normalized }).Count -ne 1) {
            throw "LiveProfileRecoveryArtifactMissing: $normalized"
        }
        Get-Content -LiteralPath (Join-Path $caseDirectory $RelativePath) -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    }
    function Read-SealedNdjson([string]$RelativePath) {
        $normalized = $RelativePath.Replace('\','/')
        if (@($seal.Manifest.artifacts | Where-Object { [string]$_.path -ceq $normalized }).Count -ne 1) {
            throw "LiveProfileRecoveryArtifactMissing: $normalized"
        }
        @($values = Get-Content -LiteralPath (Join-Path $caseDirectory $RelativePath) -ReadCount 1 -ErrorAction Stop |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
            ForEach-Object { [string]$_ | ConvertFrom-Json -ErrorAction Stop }; $values)
    }
    function Get-SafeSegment([string]$Value) {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Value)
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try { $hash = ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant() }
        finally { $algorithm.Dispose() }
        $hash.Substring(0, 24)
    }
    function Get-EnabledNames([string]$LiteralPath, [char]$Prefix) {
        $names = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($line in @(Get-Content -LiteralPath $LiteralPath -ErrorAction Stop)) {
            $text = ([string]$line).Trim()
            if ($text.Length -gt 1 -and $text[0] -eq $Prefix) { [void]$names.Add($text.Substring(1).Trim()) }
        }
        return (, $names)
    }
    function Test-LiveLoosePathPresent([string]$RelativePath, [Collections.Generic.HashSet[string]]$EnabledMods, [string]$ModsRoot, [string]$GameDataRoot) {
        if (-not [string]::IsNullOrWhiteSpace($GameDataRoot) -and (Test-Path -LiteralPath (Join-Path $GameDataRoot $RelativePath) -PathType Leaf)) { return $true }
        foreach ($modName in $EnabledMods) {
            if (Test-Path -LiteralPath (Join-Path (Join-Path $ModsRoot $modName) $RelativePath) -PathType Leaf) { return $true }
        }
        $false
    }
    function Invoke-ArchiveExtraction([string]$ArchivePath, [string]$ArchiveSha256, [string]$Destination) {
        $stdout = Join-Path (Split-Path -Parent $Destination) ((Split-Path -Leaf $Destination) + '.stdout.json')
        $stderr = Join-Path (Split-Path -Parent $Destination) ((Split-Path -Leaf $Destination) + '.stderr.txt')
        $arguments = @('repair-archive-inspect','--archive',$ArchivePath,'--expected-sha256',$ArchiveSha256,'--mode','full','--staging',$Destination)
        $argumentLine = @($arguments | ForEach-Object { ConvertTo-GridNativeArgument -Value ([string]$_) }) -join ' '
        $process = Start-Process -FilePath $diagnosticsPath -ArgumentList $argumentLine -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr -ErrorAction Stop
        if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $stdout -PathType Leaf)) {
            $detail = if (Test-Path -LiteralPath $stderr -PathType Leaf) { (@(Get-Content -LiteralPath $stderr | Select-Object -First 8) -join ' ').Trim() } else { '' }
            throw "LiveProfileRecoveryArchiveExtractionFailed: $detail"
        }
        $result = Get-Content -LiteralPath $stdout -Raw | ConvertFrom-Json -ErrorAction Stop
        if ([string]$result.status -cne 'Extracted' -or [string]$result.archiveSha256 -ine $ArchiveSha256) {
            throw 'LiveProfileRecoveryArchiveExtractionFailed: extracted archive identity was not verified.'
        }
        $result
    }

    $root = Get-GridDiagnosticStoreRoot -Root $CaseStoreRoot -Ensure
    $caseDirectory = [IO.Path]::GetFullPath($RecoveryCaseDirectory)
    $seal = Test-GridDiagnosticCaseSeal -StoreRoot $root -CaseDirectory $caseDirectory
    if (-not $seal.IsValid) { throw ('LiveProfileRecoverySealInvalid: ' + ($seal.Errors -join '; ')) }
    $plan = Read-SealedJson 'repair\component-recovery-plan.v1.json'
    $validation = Test-GridSkyrimComponentRecoveryPlan -Plan $plan -ExpectedCaseId ([string]$seal.Manifest.caseId)
    if (-not $validation.IsValid) { throw ('LiveProfileRecoveryPlanInvalid: ' + ($validation.Errors -join '; ')) }

    $readyComponents = @($plan.components | Where-Object recoveryState -eq 'RepairSourceVerified' | Sort-Object pluginName)
    if ($readyComponents.Count -eq 0) {
        $result = [pscustomobject][ordered]@{status='EvidenceRequired';planningCaseId=$null;planningCaseDirectory=$null;specification=$null;preparedComponentCount=0;detail='No component has complete sealed source evidence yet.'}
        if ($PassThru) { return $result }
        return ($result | ConvertTo-Json -Depth 20)
    }

    # Grid.Diagnostics is trusted application code, not live MO2 evidence. It
    # therefore must exist and must not be a reparse point, but it is not
    # required to sit inside the user's exact MO2 read authorization roots.
    $diagnosticsPath = [IO.Path]::GetFullPath($DiagnosticsExecutable)
    $diagnosticsItem = Get-Item -LiteralPath $diagnosticsPath -Force -ErrorAction Stop
    if (($diagnosticsItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "LiveProfileRecoveryDiagnosticsReparsePointRefused: $diagnosticsPath"
    }
    $installation = Read-SealedJson 'installation\installation-baseline.v1.json'
    $metadataDocument = Read-SealedJson 'provenance\mod-metadata.v1.json'
    $pluginInventory = @(Read-SealedNdjson 'inventory\plugins.v1.ndjson')
    $dependencies = @(Read-SealedNdjson 'inventory\plugin-script-dependencies.v1.ndjson') + @(Read-SealedNdjson 'inventory\plugin-asset-dependencies.v1.ndjson')
    $inspections = @(Read-SealedNdjson 'provenance\source-archive-inspections.v1.ndjson')
    $sourceArchives = @(Read-SealedNdjson 'provenance\source-archives.v1.ndjson')

    $modsRoot = [string]@($installation.roots | Where-Object label -eq 'Mods directory' | Select-Object -First 1).path
    $profilesRoot = [string]@($installation.roots | Where-Object label -eq 'Profiles directory' | Select-Object -First 1).path
    $instanceIni = [string]@($installation.roots | Where-Object label -eq 'Instance configuration' | Select-Object -First 1).path
    $gameDataRoot = [string]@($installation.roots | Where-Object label -eq 'Skyrim Data directory' | Select-Object -First 1).path
    foreach ($requiredRoot in @($modsRoot,$profilesRoot,$instanceIni)) {
        if ([string]::IsNullOrWhiteSpace($requiredRoot) -or -not (Test-WithinAuthorizedRead $requiredRoot)) {
            throw "LiveProfileRecoveryReadUnauthorized: $requiredRoot"
        }
    }
    $modsRoot = [IO.Path]::GetFullPath($modsRoot).TrimEnd('\')
    $profile = @($installation.profiles | Where-Object { [string]$_.profileId -ceq [string]$installation.profileId })
    if ($profile.Count -ne 1) { throw 'LiveProfileRecoveryProfileAmbiguous: the sealed selected profile cannot be resolved.' }
    $profileRoot = [IO.Path]::GetFullPath((Join-Path $profilesRoot ([string]$profile[0].name))).TrimEnd('\')
    if (-not (Test-WithinAuthorizedRead $profileRoot)) { throw "LiveProfileRecoveryReadUnauthorized: $profileRoot" }

    $sourceByName = @{}
    foreach ($source in @($installation.profileSources)) { $sourceByName[[string]$source.name] = $source }
    foreach ($required in @('modlist.txt','plugins.txt')) {
        if (-not $sourceByName.ContainsKey($required)) { throw "LiveProfileRecoveryProfileInputMissing: $required" }
    }
    $modlistPath = Assert-SafeLiveFile ([string]$sourceByName['modlist.txt'].path)
    $pluginsPath = Assert-SafeLiveFile ([string]$sourceByName['plugins.txt'].path)
    $enabledMods = Get-EnabledNames -LiteralPath $modlistPath -Prefix '+'
    $enabledPlugins = Get-EnabledNames -LiteralPath $pluginsPath -Prefix '*'
    $sealedEnabledMods = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($record in @($metadataDocument.records | Where-Object { [bool](Get-PropertyValue $_ 'enabled') })) {
        $name = [string](Get-PropertyValue $record 'name')
        if (-not [string]::IsNullOrWhiteSpace($name)) { [void]$sealedEnabledMods.Add($name) }
    }
    $sealedEnabledPlugins = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($record in @($pluginInventory | Where-Object { [bool](Get-PropertyValue $_ 'isEnabled') })) {
        $name = [string](Get-PropertyValue $record 'name')
        if (-not [string]::IsNullOrWhiteSpace($name)) { [void]$sealedEnabledPlugins.Add($name) }
    }
    if (-not $enabledMods.SetEquals($sealedEnabledMods) -or -not $enabledPlugins.SetEquals($sealedEnabledPlugins)) {
        throw 'LiveProfileRecoverySelectionChanged: the current enabled mod or plugin selection differs from the sealed dependency evidence.'
    }

    $protectedState = New-Object Collections.Generic.List[object]
    $protectedCandidates = @($instanceIni) + @($installation.profileSources | Where-Object { [string]$_.availability -eq 'Read' -and -not [string]::IsNullOrWhiteSpace([string]$_.path) } | ForEach-Object { [string]$_.path })
    foreach ($pathValue in @($protectedCandidates | Sort-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $pathValue -PathType Leaf)) { continue }
        $path = Assert-SafeLiveFile $pathValue
        $protectedState.Add([pscustomobject][ordered]@{path=$path;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()})
    }

    $metadataByName = @{}
    foreach ($record in @($metadataDocument.records)) {
        $name = [string](Get-PropertyValue $record 'name')
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        $key = $name.ToUpperInvariant()
        if (-not $metadataByName.ContainsKey($key)) { $metadataByName[$key] = New-Object Collections.Generic.List[object] }
        $metadataByName[$key].Add($record)
    }
    $inspectionByEvidence = @{}
    foreach ($inspection in $inspections) {
        $id = [string](Get-PropertyValue $inspection 'evidenceId')
        if (-not [string]::IsNullOrWhiteSpace($id)) { $inspectionByEvidence[$id] = $inspection }
    }
    $archiveByIdentity = @{}
    foreach ($archive in $sourceArchives) {
        $sha = ([string](Get-PropertyValue $archive 'sha256')).ToUpperInvariant()
        $path = [string](Get-PropertyValue $archive 'canonicalPath')
        if ($sha -match '^[A-F0-9]{64}$' -and -not [string]::IsNullOrWhiteSpace($path)) { $archiveByIdentity[$sha + '|' + ([IO.Path]::GetFullPath($path)).ToUpperInvariant()] = $archive }
    }

    $providerPlans = @{}
    $skipped = New-Object Collections.Generic.List[object]
    foreach ($component in $readyComponents) {
        $pluginName = [string]$component.pluginName
        $providerName = [string]$component.overrideProvider
        if (-not $enabledPlugins.Contains($pluginName)) { $skipped.Add([pscustomobject]@{pluginName=$pluginName;reason='PluginNotEnabledNow'}); continue }
        if (-not $enabledMods.Contains($providerName)) { $skipped.Add([pscustomobject]@{pluginName=$pluginName;reason='ProviderNotEnabledNow'}); continue }
        [object[]]$metadataMatches = @()
        if ($metadataByName.ContainsKey($providerName.ToUpperInvariant())) {
            $metadataMatches = @($metadataByName[$providerName.ToUpperInvariant()].ToArray())
        }
        if ($metadataMatches.Count -ne 1) { $skipped.Add([pscustomobject]@{pluginName=$pluginName;reason='ProviderPathAmbiguous'}); continue }
        $targetValue = [string](Get-PropertyValue $metadataMatches[0] 'path')
        if ([string]::IsNullOrWhiteSpace($targetValue)) { $targetValue = [string](Get-PropertyValue $metadataMatches[0] 'canonicalPath') }
        if ([string]::IsNullOrWhiteSpace($targetValue)) { $skipped.Add([pscustomobject]@{pluginName=$pluginName;reason='ProviderTreeUnavailable'}); continue }
        $targetDirectory = [IO.Path]::GetFullPath($targetValue).TrimEnd('\')
        if ((Split-Path -Parent $targetDirectory) -ine $modsRoot -or -not (Test-WithinAuthorizedRead $targetDirectory) -or -not (Test-Path -LiteralPath $targetDirectory -PathType Container)) {
            $skipped.Add([pscustomobject]@{pluginName=$pluginName;reason='ProviderTreeUnavailable'}); continue
        }

        $requirements = @($dependencies | Where-Object { [string](Get-PropertyValue $_ 'pluginName') -ieq $pluginName } |
            Group-Object { ([string](Get-PropertyValue $_ 'requiredVirtualPath')).ToUpperInvariant() } | ForEach-Object { $_.Group | Select-Object -First 1 } |
            Sort-Object { [string](Get-PropertyValue $_ 'requiredVirtualPath') })
        $matchRows = New-Object Collections.Generic.List[object]
        foreach ($candidate in @($component.lineageCandidates | Where-Object { [string]$_.contentInspection.status -eq 'Verified' })) {
            $evidenceId = [string]$candidate.contentInspection.evidenceId
            if (-not $inspectionByEvidence.ContainsKey($evidenceId)) { continue }
            $inspection = $inspectionByEvidence[$evidenceId]
            $archivePath = Assert-SafeLiveFile ([string]$inspection.archivePath)
            $archiveSha = ([string]$inspection.archiveSha256).ToUpperInvariant()
            if (-not $archiveByIdentity.ContainsKey($archiveSha + '|' + $archivePath.ToUpperInvariant())) { throw 'LiveProfileRecoveryArchiveUnbound: an inspected archive is absent from sealed source provenance.' }
            if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToUpperInvariant() -cne $archiveSha) { throw "LiveProfileRecoveryArchiveChanged: $archivePath" }
            foreach ($match in @($inspection.matches | Where-Object { [string]$_.pluginName -ieq $pluginName -and [string]$_.mappingRule -in @('ArchiveRoot','ExplicitDataDirectory') })) {
                $matchRows.Add([pscustomobject][ordered]@{pluginName=$pluginName;providerName=$providerName;requiredVirtualPath=[string]$match.requiredVirtualPath;archivePath=$archivePath;archiveSha256=$archiveSha;archiveEntryPath=[string]$match.normalizedArchivePath;fileSha256=([string]$match.sha256).ToUpperInvariant();evidenceId=$evidenceId;mappingRule=[string]$match.mappingRule})
            }
        }
        $selected = New-Object Collections.Generic.List[object]
        foreach ($requirement in $requirements) {
            $relative = [string](Get-PropertyValue $requirement 'requiredVirtualPath')
            if (Test-LiveLoosePathPresent -RelativePath $relative -EnabledMods $enabledMods -ModsRoot $modsRoot -GameDataRoot $gameDataRoot) { continue }
            $matches = @($matchRows | Where-Object { [string]$_.requiredVirtualPath -ieq $relative } | Sort-Object archiveSha256,archiveEntryPath)
            $hashes = @($matches.fileSha256 | Sort-Object -Unique)
            if ($matches.Count -eq 0 -or $hashes.Count -ne 1) { $selected.Clear(); break }
            $selected.Add($matches[0])
        }
        if ($selected.Count -eq 0) { $skipped.Add([pscustomobject]@{pluginName=$pluginName;reason='NoStillMissingVerifiedFiles'}); continue }
        $providerKey = $providerName.ToUpperInvariant()
        if (-not $providerPlans.ContainsKey($providerKey)) {
            $providerPlans[$providerKey] = [pscustomobject][ordered]@{providerName=$providerName;targetDirectory=$targetDirectory;matches=(New-Object Collections.Generic.List[object]);plugins=(New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase))}
        }
        if ([string]$providerPlans[$providerKey].targetDirectory -ine $targetDirectory) { throw 'LiveProfileRecoveryProviderAmbiguous: one provider name resolves to multiple target trees.' }
        [void]$providerPlans[$providerKey].plugins.Add($pluginName)
        foreach ($match in $selected) { $providerPlans[$providerKey].matches.Add($match) }
    }

    if ($providerPlans.Count -eq 0) {
        $result = [pscustomobject][ordered]@{status='NoCurrentRepairRequired';planningCaseId=$null;planningCaseDirectory=$null;specification=$null;preparedComponentCount=0;skipped=$skipped.ToArray();detail='Verified source files are no longer missing from the current enabled live profile, or their current provider is unavailable.'}
        if ($PassThru) { return $result }
        return ($result | ConvertTo-Json -Depth 20)
    }

    $preparedRoot = Join-Path $root ('prepared-repairs\' + ([string]$plan.planSha256).ToLowerInvariant())
    if (-not (Test-Path -LiteralPath $preparedRoot -PathType Container)) { New-Item -ItemType Directory -Path $preparedRoot -Force -ErrorAction Stop | Out-Null }
    $instanceRoot = Split-Path -Parent $modsRoot
    $operations = New-Object Collections.Generic.List[object]
    $artifacts = New-Object Collections.Generic.List[object]
    $fomodDecisions = New-Object Collections.Generic.List[object]
    $sequence = 0
    foreach ($providerPlan in @($providerPlans.Values | Sort-Object providerName)) {
        $sequence++
        $providerSegment = Get-SafeSegment ([string]$providerPlan.providerName)
        $workRoot = Join-Path $preparedRoot $providerSegment
        $sourceDirectory = Join-Path $workRoot 'source'
        $manifestPath = Join-Path $workRoot 'prepared-source.v1.json'
        if (Test-Path -LiteralPath $workRoot) { throw "LiveProfileRecoveryPreparationExists: $workRoot" }
        New-Item -ItemType Directory -Path $workRoot -ErrorAction Stop | Out-Null
        Copy-GridRepairTree -Source ([string]$providerPlan.targetDirectory) -Destination $sourceDirectory

        $uniqueMatches = @($providerPlan.matches | Group-Object { ([string]$_.requiredVirtualPath).ToUpperInvariant() } | ForEach-Object {
            $group = @($_.Group | Sort-Object archiveSha256,archiveEntryPath)
            if (@($group.fileSha256 | Sort-Object -Unique).Count -ne 1) { throw "LiveProfileRecoveryPayloadConflict: $($_.Name)" }
            $group[0]
        } | Sort-Object requiredVirtualPath)
        $extractions = @{}
        foreach ($archiveGroup in @($uniqueMatches | Group-Object archiveSha256 | Sort-Object Name)) {
            $match = @($archiveGroup.Group)[0]
            $extractRoot = Join-Path $workRoot ('archive-' + ([string]$match.archiveSha256).Substring(0,24).ToLowerInvariant())
            [void](Invoke-ArchiveExtraction -ArchivePath ([string]$match.archivePath) -ArchiveSha256 ([string]$match.archiveSha256) -Destination $extractRoot)
            $extractions[[string]$match.archiveSha256] = $extractRoot
        }
        $expectedFiles = New-Object Collections.Generic.List[object]
        $mappings = New-Object Collections.Generic.List[object]
        $evidenceIds = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        foreach ($match in $uniqueMatches) {
            $relative = ConvertTo-GridRepairRelativePath ([string]$match.requiredVirtualPath)
            $entry = ConvertTo-GridRepairRelativePath ([string]$match.archiveEntryPath)
            $extracted = Join-Path ([string]$extractions[[string]$match.archiveSha256]) $entry
            if (-not (Test-Path -LiteralPath $extracted -PathType Leaf) -or (Get-FileHash -LiteralPath $extracted -Algorithm SHA256).Hash.ToUpperInvariant() -cne [string]$match.fileSha256) {
                throw "LiveProfileRecoveryExtractedFileInvalid: $relative"
            }
            $destination = Join-Path $sourceDirectory $relative
            if (Test-Path -LiteralPath $destination) { throw "LiveProfileRecoveryWouldOverwriteCurrentFile: $relative" }
            $parent = Split-Path -Parent $destination
            if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Path $parent -Force -ErrorAction Stop | Out-Null }
            Copy-Item -LiteralPath $extracted -Destination $destination -ErrorAction Stop
            if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToUpperInvariant() -cne [string]$match.fileSha256) { throw "LiveProfileRecoveryPreparedFileInvalid: $relative" }
            $expectedFiles.Add([pscustomobject][ordered]@{path=$relative;sha256=[string]$match.fileSha256})
            $mappings.Add([pscustomobject][ordered]@{source=$relative;destination=$relative})
            [void]$evidenceIds.Add([string]$match.evidenceId)
            [void]$evidenceIds.Add('sha256:' + [string]$match.archiveSha256)
        }
        $preparedTree = Get-GridRepairTreeObservation -LiteralPath $sourceDirectory
        $sourceManifest = [pscustomobject][ordered]@{schemaVersion=1;recoveryCaseId=[string]$seal.Manifest.caseId;recoveryManifestSha256=[string]$seal.Manifest.manifestSha256;recoveryPlanId=[string]$plan.planId;recoveryPlanSha256=[string]$plan.planSha256;providerName=[string]$providerPlan.providerName;targetDirectory=[string]$providerPlan.targetDirectory;preparedTreeSha256=[string]$preparedTree.treeSha256;restoredFiles=$expectedFiles.ToArray();sourceArchives=@($uniqueMatches | Select-Object archivePath,archiveSha256 | Sort-Object archiveSha256 -Unique);evidenceIds=@($evidenceIds | Sort-Object)}
        Write-GridJsonAtomic -InputObject $sourceManifest -LiteralPath $manifestPath
        $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToUpperInvariant()
        $artifacts.Add([pscustomobject][ordered]@{schemaVersion=1;artifactId=('prepared-source:'+$providerSegment);path=$manifestPath;status='Verified';sizeBytes=(Get-Item -LiteralPath $manifestPath).Length;sha256=$manifestHash;identityEvidenceIds=@($evidenceIds | Sort-Object);observedAt=[DateTimeOffset]::UtcNow.ToString('o')})
        $fomodDecisions.Add([pscustomobject][ordered]@{schemaVersion=1;artifactSha256=$manifestHash;moduleConfigSha256=$null;status='Deterministic';selections=@();fileMappings=$mappings.ToArray()})
        $before = Get-GridRepairTreeObservation -LiteralPath ([string]$providerPlan.targetDirectory)
        # Keep each mutation destination an immediate child of the existing
        # instance root. Planning creates neither path; Apply can therefore
        # stage and preserve with atomic same-volume moves without unjournaled
        # parent-directory mutations.
        $stagingDirectory = Join-Path $instanceRoot ('.grid-repair-staging-' + ([string]$plan.planId) + '-' + $providerSegment)
        $rollbackDirectory = Join-Path $instanceRoot ('.grid-repair-rollback-' + ([string]$plan.planId) + '-' + $providerSegment)
        $operations.Add([pscustomobject][ordered]@{sequence=$sequence;componentId=[string]$providerPlan.providerName;repairStrategy='TransactionalOwningModReinstall';beforeTreeState='Existing';sourceDirectory=$sourceDirectory;targetDirectory=[string]$providerPlan.targetDirectory;stagingDirectory=$stagingDirectory;rollbackDirectory=$rollbackDirectory;beforeTreeSha256=[string]$before.treeSha256;expectedTreeSha256=[string]$preparedTree.treeSha256;baselineEvidenceIds=@($evidenceIds | Sort-Object);artifactSha256=$manifestHash;sourceToDestinationMappings=$mappings.ToArray();expectedFiles=$expectedFiles.ToArray()})
    }

    $protectedArray = $protectedState.ToArray()
    $operationArray = $operations.ToArray()
    $artifactArray = $artifacts.ToArray()
    $fomodArray = $fomodDecisions.ToArray()
    $operationEvidence = @($operationArray | ForEach-Object {[pscustomobject]@{componentId=$_.componentId;beforeTreeSha256=$_.beforeTreeSha256;expectedTreeSha256=$_.expectedTreeSha256;artifactSha256=$_.artifactSha256}})
    $inspection = [pscustomobject][ordered]@{status='InputsVerified';baselineCaseDirectory=$caseDirectory;baselineCaseId=[string]$seal.Manifest.caseId;baselineManifestSha256=[string]$seal.Manifest.manifestSha256;semanticBaselineFingerprint=[string]$seal.Manifest.semanticBaselineFingerprint;protectedState=$protectedArray}
    $evidenceFingerprint = Get-GridCanonicalJsonSha256 -InputObject ([pscustomobject][ordered]@{recoveryManifestSha256=[string]$seal.Manifest.manifestSha256;recoveryPlanSha256=[string]$plan.planSha256;operations=$operationEvidence;protectedState=$protectedArray})
    $draftPath = Join-Path $preparedRoot 'repair-specification.v1.json'
    $compatibility = @([pscustomobject][ordered]@{schemaVersion=1;components=@($operationArray.componentId | Sort-Object -Unique);requirements=@('The current enabled mod selection matches the sealed dependency baseline.','Every restored file is still absent from the current enabled loose-file view.','Every source member and prepared destination has the sealed SHA-256 digest.');status='VerifiedCompatible'})
    $proposal = New-GridSkyrimModChainRepairProposal -CaseId ('live-recovery-' + ([string]$plan.planId)) -Inspection $inspection -ContextFingerprint ([string]$plan.contextFingerprint) -EvidenceFingerprint $evidenceFingerprint -ModsRoot $modsRoot -Operations $operationArray -ProtectedState $protectedArray -ArtifactIntegrity $artifactArray -FomodDecisions $fomodArray -CompatibilityMatrix $compatibility -Preconditions @('The live target trees, enabled mod list, protected profile files, prepared trees, and exact source manifests remain byte-identical to review.') -Postconditions @('Every prepared replacement tree is promoted atomically, every restored file digest verifies, and every protected profile file remains unchanged.') -ProfilePreservation $protectedArray -Exclusions @('No profile activation, plugin-order, mod-order, record, or configuration change.','No file already supplied by the current enabled loose-file view is overwritten.') -OutputPath $draftPath -PassThru

    $planningCaseId = 'repair-planning-' + ([string]$proposal.specification.specificationSha256).Substring(0,24).ToLowerInvariant() + '-v1'
    $planningDirectory = Join-Path (Join-Path $root 'cases\v1') $planningCaseId
    if (Test-Path -LiteralPath $planningDirectory -PathType Container) {
        $existingSeal = Test-GridDiagnosticCaseSeal -StoreRoot $root -CaseDirectory $planningDirectory
        if (-not $existingSeal.IsValid) { throw ('LiveProfileRecoveryPlanningCaseInvalid: ' + ($existingSeal.Errors -join '; ')) }
        $result = [pscustomobject][ordered]@{status='RepairPlanned';planningCaseId=$planningCaseId;planningCaseDirectory=$planningDirectory;specification=$proposal.specification;preparedComponentCount=$operations.Count;skipped=$skipped.ToArray();reused=$true;detail='An exact live-profile repair is staged and awaits one-use mutation authorization.'}
        if ($PassThru) { return $result }
        return ($result | ConvertTo-Json -Depth 40)
    }
    $transaction = New-GridCaseStoreTransaction -StoreRoot $root -CaseId $planningCaseId
    try {
        $created = [DateTimeOffset]::UtcNow.ToString('o')
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'case.json' -Value ([pscustomobject][ordered]@{schemaVersion=1;caseId=$planningCaseId;purpose='RepairPlanning';status='Completed';createdAt=$created}) | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'repair\repair-specification.v1.json' -SourceLiteralPath $draftPath | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'repair\live-profile-recovery-binding.v1.json' -Value ([pscustomobject][ordered]@{schemaVersion=1;recoveryCaseId=[string]$seal.Manifest.caseId;recoveryManifestSha256=[string]$seal.Manifest.manifestSha256;recoveryPlanId=[string]$plan.planId;recoveryPlanSha256=[string]$plan.planSha256;liveProfileIsAuthoritative=$true;originalModlistRequired=$false;preparedProviders=@($operations.componentId);skipped=$skipped.ToArray()}) | Out-Null
        $run = [pscustomobject][ordered]@{schemaVersion=1;runId=('run-'+$planningCaseId);caseId=$planningCaseId;state='Completed';startedAt=$created;completedAt=[DateTimeOffset]::UtcNow.ToString('o');planFingerprint=[string]$proposal.specification.specificationSha256;resourcePolicyVersion='grid.live-profile-recovery-planning.v1';gates=@([pscustomobject]@{gateId='LiveProfileCurrent';status='Pass';evidenceIds=@([string]$plan.planId)},[pscustomobject]@{gateId='ExactSourcePayloads';status='Pass';evidenceIds=@($artifacts.identityEvidenceIds | Sort-Object -Unique)});sufficiency=[pscustomobject]@{schemaVersion=1;status='SufficientForRequestedDiagnosis'};checkpoints=@();primaryFailure=$null;secondaryFailures=@()}
        Seal-GridCaseStoreRun -Transaction $transaction -Run $run | Out-Null
        $sealedPlanning = Seal-GridDiagnosticCase -Transaction $transaction -SemanticBaselineFingerprint ([string]$proposal.specification.specificationSha256) -Runs @($run)
        $result = [pscustomobject][ordered]@{status='RepairPlanned';planningCaseId=$planningCaseId;planningCaseDirectory=$sealedPlanning.CaseDirectory;specification=$proposal.specification;preparedComponentCount=$operations.Count;skipped=$skipped.ToArray();reused=$false;detail='An exact live-profile repair is staged and awaits one-use mutation authorization.'}
    }
    catch {
        if ($transaction.State -eq 'Open' -and (Test-Path -LiteralPath $transaction.TransactionDirectory)) { Remove-Item -LiteralPath $transaction.TransactionDirectory -Recurse -Force -ErrorAction SilentlyContinue }
        throw
    }
    if ($PassThru) { $result } else { $result | ConvertTo-Json -Depth 40 }
}
