#requires -Version 5.1
<#
.SYNOPSIS
Resolves whole-profile missing Papyrus and plugin-declared asset dependencies into Grid's four-field diagnosis.
.DESCRIPTION
Consumes only the native baseline's bounded plugin-to-virtual-Data comparisons.
It does not infer a repair source, download anything, or mutate the MO2 profile.
#>
function Resolve-GridSkyrimBaselineIntegrityDiagnosis {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][string]$ContextFingerprint,
        [Parameter(Mandatory)]$Inspection,
        [AllowEmptyCollection()][object[]]$Dependencies = @(),
        $AssetInspection,
        [AllowEmptyCollection()][object[]]$AssetDependencies = @(),
        $SpidInspection,
        [AllowEmptyCollection()][object[]]$SpidSourceIssues = @(),
        $CrashSignatures,
        $SksePluginLoad
    )

    if (-not (Get-Command New-GridDiagnosticResult -ErrorAction SilentlyContinue)) {
        throw 'The shared Grid health module must be imported before baseline integrity diagnosis.'
    }
    if ([string]::IsNullOrWhiteSpace($ContextFingerprint)) {
        throw 'BaselineIntegrityContextUnavailable: the resolved environment fingerprint is missing.'
    }

    function Get-StableEvidenceId([string]$Value) {
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try {
            $digest = ([BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-', '').ToLowerInvariant()
            'evidence-' + $digest.Substring(0, 32)
        }
        finally { $algorithm.Dispose() }
    }
    function Get-SafeIdentity([object]$Value) {
        $text = [string]$Value
        if ([string]::IsNullOrWhiteSpace($text) -or $text.IndexOfAny([char[]]@(0,9,10,13,'\','/')) -ge 0 -or $text.Length -gt 512) { return $null }
        $text
    }
    function Get-SafeVirtualPath([object]$Value) {
        $text = [string]$Value
        if ([string]::IsNullOrWhiteSpace($text) -or $text.Length -gt 1024 -or $text.IndexOfAny([char[]]@(0,9,10,13,':')) -ge 0 -or $text.StartsWith('\') -or $text.StartsWith('/')) { return $null }
        $segments = @($text -split '[\\/]' | Where-Object { $_.Length -gt 0 })
        if ($segments.Count -eq 0 -or @($segments | Where-Object { $_ -in @('.','..') }).Count -gt 0) { return $null }
        ($segments -join '\')
    }

    $ordered = @($Dependencies | Sort-Object @{ Expression = { [string]$_.pluginName } }, @{ Expression = { [string]$_.scriptName } })
    $evidence = New-Object Collections.Generic.List[object]
    $pluginGroups = @($ordered | Group-Object { ([string]$_.pluginName).ToUpperInvariant() })
    foreach ($pluginGroup in $pluginGroups) {
        $validDependencies = @($pluginGroup.Group | Where-Object {
            (Get-SafeIdentity $_.pluginName) -and
            -not [string]::IsNullOrWhiteSpace([string]$_.scriptName) -and
            -not [string]::IsNullOrWhiteSpace([string]$_.requiredVirtualPath)
        })
        if ($validDependencies.Count -eq 0) { continue }
        $pluginName = Get-SafeIdentity $validDependencies[0].pluginName
        $sourceProviders = @($validDependencies | ForEach-Object { Get-SafeIdentity $_.sourceProvider } | Where-Object { $_ } | Sort-Object -Unique)
        $sourceProvider = if ($sourceProviders.Count -eq 1) { [string]$sourceProviders[0] } else { $null }
        $totalReferences = [long]($validDependencies | Measure-Object -Property referenceCount -Sum).Sum
        $dependencySemantic = @($validDependencies | ForEach-Object {
            @([string]$_.scriptName, [string]$_.requiredVirtualPath, [int]$_.referenceCount) -join [char]30
        }) -join [char]29
        $semantic = @($ContextFingerprint, $pluginName, $sourceProvider, $dependencySemantic) -join [char]31
        $subjects = @([pscustomobject][ordered]@{ kind = 'Plugin'; name = $pluginName; roles = @('Dependency') })
        if ($sourceProvider) {
            # This is the MO2 provider that wins the consuming plugin file. It is
            # not proof that the same mod folder should contain the missing PEX.
            $subjects += [pscustomobject][ordered]@{ kind = 'Mod'; name = $sourceProvider; roles = @('OverrideProvider') }
        }
        $evidence.Add([pscustomobject][ordered]@{
            schemaVersion = 1
            evidenceId = Get-StableEvidenceId $semantic
            parameter = 'missingAttachedPapyrusDependency'
            value = 1.0
            claim = "Enabled plugin '$pluginName' attaches $($validDependencies.Count) distinct Papyrus script dependencies for which the resolved virtual Data tree has no PEX provider."
            sourceType = 'Mo2ResolvedVirtualData'
            sourceIdentifier = [string]$Inspection.semanticFingerprint
            collectedAt = [DateTimeOffset]::UtcNow.ToString('o')
            contextFingerprint = $ContextFingerprint
            verificationStatus = 'Verified'
            collector = [pscustomobject][ordered]@{ name = 'Grid.Mo2.PluginScriptDependencyInspector'; version = '1.1.0' }
            native = [pscustomobject][ordered]@{
                pluginName = $pluginName; pluginSourceProvider = $sourceProvider
                missingDependencyCount = [int]$validDependencies.Count; totalReferenceCount = $totalReferences
                samples = @($validDependencies | Select-Object -First 12 | ForEach-Object {
                    [pscustomobject][ordered]@{
                        scriptName = [string]$_.scriptName
                        requiredVirtualPath = [string]$_.requiredVirtualPath
                        referenceCount = [int]$_.referenceCount
                    }
                })
                subjects = $subjects
            }
        })
    }

    $orderedAssets = @($AssetDependencies | Sort-Object `
        @{ Expression = { [string]$_.pluginName } }, `
        @{ Expression = { [string]$_.kind } }, `
        @{ Expression = { [string]$_.requiredVirtualPath } })
    $assetGroups = @($orderedAssets | Group-Object {
        (([string]$_.pluginName).ToUpperInvariant()) + [char]31 + (([string]$_.kind).ToUpperInvariant())
    })
    foreach ($assetGroup in $assetGroups) {
        $validAssets = @($assetGroup.Group | Where-Object {
            (Get-SafeIdentity $_.pluginName) -and
            (Get-SafeVirtualPath $_.requiredVirtualPath) -and
            [string]$_.kind -in @('Mesh','Texture') -and
            [int]$_.referenceCount -gt 0
        })
        if ($validAssets.Count -eq 0) { continue }
        $pluginName = Get-SafeIdentity $validAssets[0].pluginName
        $kind = [string]$validAssets[0].kind
        $sourceProviders = @($validAssets | ForEach-Object { Get-SafeIdentity $_.sourceProvider } | Where-Object { $_ } | Sort-Object -Unique)
        $sourceProvider = if ($sourceProviders.Count -eq 1) { [string]$sourceProviders[0] } else { $null }
        $totalReferences = [long]($validAssets | Measure-Object -Property referenceCount -Sum).Sum
        $assetSemantic = @($validAssets | ForEach-Object {
            @((Get-SafeVirtualPath $_.requiredVirtualPath), [int]$_.referenceCount) -join [char]30
        }) -join [char]29
        $semantic = @($ContextFingerprint, 'PluginAsset', $pluginName, $sourceProvider, $kind, $assetSemantic) -join [char]31
        $subjects = @([pscustomobject][ordered]@{ kind = 'Plugin'; name = $pluginName; roles = @('Dependency') })
        if ($sourceProvider) {
            # This provider wins the consuming plugin. It does not prove ownership
            # of every asset declared by that plugin.
            $subjects += [pscustomobject][ordered]@{ kind = 'Mod'; name = $sourceProvider; roles = @('OverrideProvider') }
        }
        $noun = if ($kind -eq 'Mesh') { 'mesh' } else { 'texture' }
        $parameter = if ($kind -eq 'Mesh') { 'missingPluginMeshDependency' } else { 'missingPluginTextureDependency' }
        $evidence.Add([pscustomobject][ordered]@{
            schemaVersion = 1
            evidenceId = Get-StableEvidenceId $semantic
            parameter = $parameter
            value = 1.0
            claim = "Effective winning records consumed by enabled plugin '$pluginName' require $($validAssets.Count) distinct $noun path(s) for which the resolved virtual Data tree has no provider."
            sourceType = 'Mo2ResolvedVirtualData'
            sourceIdentifier = [string]$AssetInspection.semanticFingerprint
            collectedAt = [DateTimeOffset]::UtcNow.ToString('o')
            contextFingerprint = $ContextFingerprint
            verificationStatus = 'Verified'
            collector = [pscustomobject][ordered]@{ name = 'Grid.Mo2.PluginAssetDependencyInspector'; version = '2.2.0' }
            native = [pscustomobject][ordered]@{
                pluginName = $pluginName; pluginSourceProvider = $sourceProvider; assetKind = $kind
                missingDependencyCount = [int]$validAssets.Count; totalReferenceCount = $totalReferences
                samples = @($validAssets | Select-Object -First 12 | ForEach-Object {
                    [pscustomobject][ordered]@{
                        requiredVirtualPath = Get-SafeVirtualPath $_.requiredVirtualPath
                        referenceCount = [int]$_.referenceCount
                        recordSamples = @($_.samples | Select-Object -First 4 | ForEach-Object {
                            [pscustomobject][ordered]@{
                                recordSignature = Get-SafeIdentity $_.recordSignature
                                rawFormId = [string]$_.rawFormId
                                subrecordSignature = Get-SafeIdentity $_.subrecordSignature
                                discoveredThroughVirtualPath = if ($_.PSObject.Properties['discoveredThroughVirtualPath']) { Get-SafeVirtualPath $_.discoveredThroughVirtualPath } else { $null }
                            }
                        })
                    }
                })
                subjects = $subjects
            }
        })
    }

    foreach ($spid in @($SpidSourceIssues | Sort-Object @{ Expression = { [string]$_.virtualPath } }, @{ Expression = { [string]$_.pluginName } })) {
        $virtualPath = Get-SafeVirtualPath $spid.virtualPath
        $providerName = Get-SafeIdentity $spid.providerName
        $pluginName = Get-SafeIdentity $spid.pluginName
        $spidStatus = [string]$spid.status
        $ruleCount = [int]$spid.ruleCount
        if (-not $virtualPath -or -not $providerName -or -not $pluginName -or
            $spidStatus -notin @('Disabled','Missing') -or $ruleCount -lt 1) { continue }
        $semantic = @($ContextFingerprint, 'SPID', $virtualPath, $providerName, $pluginName, $spidStatus, $ruleCount) -join [char]31
        $parameter = if ($spidStatus -eq 'Disabled') { 'spidSourcePluginDisabled' } else { 'spidSourcePluginMissing' }
        $stateText = $spidStatus.ToLowerInvariant()
        $evidence.Add([pscustomobject][ordered]@{
            schemaVersion = 1
            evidenceId = Get-StableEvidenceId $semantic
            parameter = $parameter
            value = 1.0
            claim = "Winning SPID configuration '$virtualPath' from '$providerName' has $ruleCount rule(s) that distribute source forms from $stateText plugin '$pluginName'."
            sourceType = 'Mo2ResolvedSpidDistribution'
            sourceIdentifier = if ($SpidInspection -and $SpidInspection.PSObject.Properties['semanticFingerprint']) { [string]$SpidInspection.semanticFingerprint } else { $virtualPath }
            collectedAt = [DateTimeOffset]::UtcNow.ToString('o')
            contextFingerprint = $ContextFingerprint
            verificationStatus = 'Verified'
            collector = [pscustomobject][ordered]@{ name = 'Grid.Mo2.SpidDistributionInspector'; version = '1.0.0' }
            native = [pscustomobject][ordered]@{
                virtualPath = $virtualPath; providerName = $providerName; pluginName = $pluginName
                pluginStatus = $spidStatus; ruleCount = $ruleCount; sampleLines = @($spid.sampleLines)
                subjects = @(
                    [pscustomobject][ordered]@{ kind = 'Mod'; name = $providerName; roles = @('AssetProvider') },
                    [pscustomobject][ordered]@{ kind = 'Plugin'; name = $pluginName; roles = @('Dependency') }
                )
            }
        })
    }

    $crashClusters = @()
    if ($CrashSignatures -and [string]$CrashSignatures.status -eq 'Complete') {
        $crashClusters = @($CrashSignatures.clusters | Sort-Object `
            @{ Expression = { [string]$_.exceptionModule } }, `
            @{ Expression = { [string]$_.exceptionType } } | Where-Object {
            (Get-SafeIdentity $_.exceptionModule) -and
            (Get-SafeIdentity $_.exceptionType) -and
            [int]$_.crashCount -gt 0 -and
            $_.winningProvider -and
            (Get-SafeIdentity $_.winningProvider.providerName)
        })
        foreach ($cluster in $crashClusters) {
            $moduleName = Get-SafeIdentity $cluster.exceptionModule
            $exceptionType = Get-SafeIdentity $cluster.exceptionType
            $providerName = Get-SafeIdentity $cluster.winningProvider.providerName
            $crashCount = [int]$cluster.crashCount
            $sourceIdentifier = Get-SafeIdentity $cluster.latestCrashEvidenceId
            if (-not $sourceIdentifier) { $sourceIdentifier = $moduleName }
            $semantic = @($ContextFingerprint, 'CrashLogger', $moduleName, $exceptionType, $providerName, $crashCount) -join [char]31
            $evidence.Add([pscustomobject][ordered]@{
                schemaVersion = 1
                evidenceId = Get-StableEvidenceId $semantic
                parameter = 'crashExceptionAddressModule'
                value = 0.8
                claim = "CrashLogger recorded $crashCount crash(es) whose unhandled $exceptionType address was inside '$moduleName'; the current winning MO2 provider is '$providerName'. This locates failing execution but does not by itself prove why the module received invalid state."
                sourceType = 'SkseCrashLogger'
                sourceIdentifier = $sourceIdentifier
                collectedAt = [DateTimeOffset]::UtcNow.ToString('o')
                contextFingerprint = $ContextFingerprint
                verificationStatus = 'Verified'
                collector = [pscustomobject][ordered]@{ name = 'Grid.Skyrim.CrashSignatureSummary'; version = '1.1.0' }
                native = [pscustomobject][ordered]@{
                    exceptionModule = $moduleName; exceptionType = $exceptionType; crashCount = $crashCount
                    evidenceStrength = [string]$cluster.evidenceStrength
                    limitation = [string]$cluster.limitation
                    subjects = @(
                        [pscustomobject][ordered]@{ kind = 'Mod'; name = $providerName; roles = @('RuntimeModuleProvider') }
                    )
                }
            })
        }
    }

    $skseRejected = @()
    if ($SksePluginLoad -and [string]$SksePluginLoad.status -eq 'Complete') {
        $skseRejected = @($SksePluginLoad.rejected | Sort-Object `
            @{ Expression = { [string]$_.dllName } }, `
            @{ Expression = { [int]$_.lineNumber } } | Where-Object {
            (Get-SafeIdentity $_.dllName) -and
            (Get-SafeIdentity $_.errorText) -and
            $_.winningProvider -and
            (Get-SafeIdentity $_.winningProvider.providerName)
        })
        foreach ($rejected in $skseRejected) {
            $moduleName = Get-SafeIdentity $rejected.dllName
            $providerName = Get-SafeIdentity $rejected.winningProvider.providerName
            $errorText = Get-SafeIdentity $rejected.errorText
            $pluginName = Get-SafeIdentity $rejected.pluginName
            $sourceIdentifier = Get-SafeIdentity $rejected.evidenceId
            if (-not $sourceIdentifier) { $sourceIdentifier = $moduleName }
            $semantic = @($ContextFingerprint, 'SKSEPluginManager', $moduleName, $providerName, $pluginName, $errorText, [int]$rejected.errorCode) -join [char]31
            $evidence.Add([pscustomobject][ordered]@{
                schemaVersion = 1
                evidenceId = Get-StableEvidenceId $semantic
                parameter = 'skseNativePluginRejected'
                value = 1.0
                claim = "SKSE recorded that native plugin '$moduleName' from the current winning MO2 provider '$providerName' was rejected: $errorText."
                sourceType = 'SksePluginManagerLog'
                sourceIdentifier = $sourceIdentifier
                collectedAt = [DateTimeOffset]::UtcNow.ToString('o')
                contextFingerprint = $ContextFingerprint
                verificationStatus = 'Verified'
                collector = [pscustomobject][ordered]@{ name = 'Grid.Skyrim.SksePluginLoadSummary'; version = '1.0.0' }
                native = [pscustomobject][ordered]@{
                    dllName = $moduleName; pluginName = $pluginName; pluginVersionHex = [string]$rejected.pluginVersionHex
                    errorText = $errorText; errorCode = [int]$rejected.errorCode; logLine = [int]$rejected.lineNumber
                    limitation = 'The log proves the recorded SKSE load result; rerun Skyrim through the selected MO2 profile to refresh it before repair if the log predates the current profile state.'
                    subjects = @(
                        [pscustomobject][ordered]@{ kind = 'Mod'; name = $providerName; roles = @('RuntimeModuleProvider') }
                    )
                }
            })
        }
    }

    $coverageLimitations = New-Object Collections.Generic.List[string]
    if ([string]$Inspection.status -ne 'Complete') {
        $coverageLimitations.Add('attached-script coverage is incomplete')
    }
    if ($AssetInspection) {
        $directStatus = if ($AssetInspection.PSObject.Properties['directRecordStatus']) { [string]$AssetInspection.directRecordStatus } else { [string]$AssetInspection.status }
        $nifStatus = if ($AssetInspection.PSObject.Properties['nifTextureStatus']) { [string]$AssetInspection.nifTextureStatus } else { [string]$AssetInspection.status }
        if ($directStatus -ne 'Complete') { $coverageLimitations.Add('direct plugin mesh/texture coverage is incomplete') }
        if ($nifStatus -ne 'Complete') { $coverageLimitations.Add('winning-NIF embedded-texture coverage is incomplete') }
    }
    if ($SpidInspection -and [string]$SpidInspection.status -ne 'Complete') {
        $coverageLimitations.Add('winning SPID distribution coverage is incomplete')
    }
    if ($coverageLimitations.Count -gt 0 -and $evidence.Count -eq 0) {
        $result = New-GridUnresolvedDiagnosticResult -CaseId $CaseId -State NeedsEvidence -ContextFingerprint $ContextFingerprint -Evidence @() `
            -Finding ('Insufficient evidence. ' + ($coverageLimitations.ToArray() -join '; ') + '; omitted items remain unknown and cannot be treated as healthy.') `
            -NextStep 'Resume the bounded baseline scan from its verified checkpoint before proposing a repair for an unobserved item.'
        return [pscustomobject][ordered]@{ Evidence = @(); DiagnosticResult = $result }
    }
    if ($evidence.Count -eq 0) {
        $result = New-GridUnresolvedDiagnosticResult -CaseId $CaseId -State NeedsEvidence -ContextFingerprint $ContextFingerprint -Evidence @() `
            -Finding 'The completed plugin dependency scans found no missing attached PEX, declared NIF, or declared DDS provider among enabled plugins; these rules do not explain the reported failures.' `
            -NextStep 'Continue the whole-profile diagnosis with the next evidence rule for records, configuration, generated outputs, or runtime state.'
        return [pscustomobject][ordered]@{ Evidence = @(); DiagnosticResult = $result }
    }

    $subjectMap = [ordered]@{}
    foreach ($item in $evidence) {
        foreach ($subject in @($item.native.subjects)) {
            $key = ([string]$subject.kind) + ':' + ([string]$subject.name).ToUpperInvariant()
            if (-not $subjectMap.Contains($key)) {
                $subjectMap[$key] = [pscustomobject][ordered]@{
                    subjectId = $key; kind = [string]$subject.kind; name = [string]$subject.name
                    roles = New-Object Collections.Generic.List[string]
                    evidenceIds = New-Object Collections.Generic.List[string]
                }
            }
            foreach ($role in @($subject.roles)) {
                if (-not $subjectMap[$key].roles.Contains([string]$role)) { $subjectMap[$key].roles.Add([string]$role) }
            }
            if (-not $subjectMap[$key].evidenceIds.Contains([string]$item.evidenceId)) { $subjectMap[$key].evidenceIds.Add([string]$item.evidenceId) }
        }
    }
    $affected = @($subjectMap.Values | ForEach-Object {
        [pscustomobject][ordered]@{ subjectId = $_.subjectId; kind = $_.kind; name = $_.name }
    })
    $roles = @($subjectMap.Values | ForEach-Object {
        [pscustomobject][ordered]@{ subjectId = $_.subjectId; roles = $_.roles.ToArray() }
    })
    $ids = @($evidence | ForEach-Object { [string]$_.evidenceId } | Sort-Object -Unique)
    $preview = @($ordered | Select-Object -First 8 | ForEach-Object { "'$([string]$_.pluginName)' requires '$([string]$_.requiredVirtualPath)'" })
    $assetPreview = @($orderedAssets | Select-Object -First 8 | ForEach-Object { "'$([string]$_.pluginName)' requires $(([string]$_.kind).ToLowerInvariant()) '$([string]$_.requiredVirtualPath)'" })
    $preview += $assetPreview
    $spidPreview = @($SpidSourceIssues | Sort-Object virtualPath,pluginName | Select-Object -First 8 | ForEach-Object { "'$([string]$_.virtualPath)' has $([int]$_.ruleCount) rule(s) targeting $(([string]$_.status).ToLowerInvariant()) '$([string]$_.pluginName)'" })
    $preview += $spidPreview
    $crashPreview = @($crashClusters | Select-Object -First 8 | ForEach-Object { "$([int]$_.crashCount) crash(es) stopped in '$([string]$_.exceptionModule)' from '$([string]$_.winningProvider.providerName)' with '$([string]$_.exceptionType)'" })
    $preview += $crashPreview
    $sksePreview = @($skseRejected | Select-Object -First 8 | ForEach-Object { "SKSE rejected '$([string]$_.dllName)' from '$([string]$_.winningProvider.providerName)': $([string]$_.errorText)" })
    $preview += $sksePreview
    $totalRecords = $ordered.Count + $orderedAssets.Count + @($SpidSourceIssues).Count + $crashClusters.Count + $skseRejected.Count
    $remaining = $totalRecords - $preview.Count
    $findingText = "The resolved profile has $($ordered.Count) missing attached Papyrus dependency record(s), $($orderedAssets.Count) missing plugin asset dependency record(s), $(@($SpidSourceIssues).Count) SPID source-plugin defect group(s), $($crashClusters.Count) directly located crash cluster(s), and $($skseRejected.Count) SKSE-rejected native plugin record(s): " + ($preview -join '; ') + '.'
    if ($crashClusters.Count -gt 0) { $findingText += ' A crash address identifies the module executing at failure, not the originating cause.' }
    if ($remaining -gt 0) { $findingText += " The sealed evidence contains $remaining additional record(s)." }
    if ($coverageLimitations.Count -gt 0) {
        $findingText += ' Coverage remains partial: ' + ($coverageLimitations.ToArray() -join '; ') + '. Emitted findings are independently verified; unobserved items remain unknown.'
    }
    $solutionSteps = New-Object Collections.Generic.List[string]
    if ($ordered.Count -gt 0) { $solutionSteps.Add('Restore each missing PEX only from an exact verified archive in the consuming plugin''s proven component lineage.') }
    if ($orderedAssets.Count -gt 0) { $solutionSteps.Add('Restore each missing NIF or DDS only from exact verified component lineage or correct the declaring record when the path itself is proven wrong; do not synthesize placeholder assets.') }
    if (@($SpidSourceIssues).Count -gt 0) { $solutionSteps.Add('For each SPID source-plugin defect, either restore or enable the exact intended plugin after validating its dependency closure, or disable the orphaned winning distribution file; do not rewrite optional filter references.') }
    if ($crashClusters.Count -gt 0) { $solutionSteps.Add('For each directly located crash module, verify its exact DLL version and Skyrim or SKSE compatibility, then collect a repeatable same-context stack or controlled reproduction before authorizing removal, replacement, or configuration repair.') }
    if ($skseRejected.Count -gt 0) { $solutionSteps.Add('For each SKSE-rejected native plugin, refresh skse64.log through the selected MO2 profile, then replace, reconfigure, or remove only the exact winning provider according to the recorded rejection reason and verify that the replacement loads.') }
    if ($coverageLimitations.Count -gt 0) { $solutionSteps.Add('Resume incomplete collectors before making any whole-profile healthy or complete claim; this does not block repair of the independently verified findings above.') }
    $solutionSteps.Add('Rerun the baseline before authorizing any repair.')
    $solutionText = $solutionSteps.ToArray() -join ' '

    $evidenceArray = $evidence.ToArray()
    $result = New-GridDiagnosticResult -CaseId $CaseId -State Diagnosed -ContextFingerprint $ContextFingerprint -Evidence $evidenceArray `
        -AffectedMods ([pscustomobject][ordered]@{ status = 'Resolved'; items = $affected; evidenceIds = $ids }) `
        -ModRoles ([pscustomobject][ordered]@{ status = 'Resolved'; items = $roles; evidenceIds = $ids }) `
        -Finding ([pscustomobject][ordered]@{ status = 'Resolved'; text = $findingText; evidenceIds = $ids }) `
        -Solution ([pscustomobject][ordered]@{ status = 'Unresolved'; text = $solutionText; evidenceIds = @() }) `
        -ResolverVersion 'skyrim.baseline-integrity.v7'
    [pscustomobject][ordered]@{ Evidence = $evidenceArray; DiagnosticResult = $result }
}
