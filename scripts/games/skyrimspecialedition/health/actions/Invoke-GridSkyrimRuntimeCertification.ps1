#requires -Version 5.1
<#
.SYNOPSIS
Plans and evaluates bounded, isolated Skyrim runtime certification.
.DESCRIPTION
Provides deterministic disposable-profile planning, typed MO2 profile launch
arguments, owned-child selection, bounded screenshot/log auditing, and receipt
evaluation. This file never starts a process, injects input, captures a screen,
or changes a profile. A separate explicitly authorized operator must perform
those actions and return evidence to this evaluator.
#>
Set-StrictMode -Version Latest

function Get-GridSkyrimRuntimeSha256 {
    param([Parameter(Mandatory)][byte[]]$Bytes)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace('-', '') }
    finally { $algorithm.Dispose() }
}

function Get-GridSkyrimRuntimeFingerprint {
    param([Parameter(Mandatory)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Values)
    Get-GridSkyrimRuntimeSha256 -Bytes ([Text.Encoding]::UTF8.GetBytes(($Values -join "`n")))
}

function Assert-GridSkyrimRuntimeDigest {
    param([Parameter(Mandatory)][string]$Value, [Parameter(Mandatory)][string]$Name)
    if ($Value -cnotmatch '^[A-F0-9]{64}$') { throw "RuntimeCertificationInvalid: $Name must be an uppercase SHA-256 digest." }
}

function Get-GridSkyrimRuntimePropertyValue {
    param($InputObject, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $InputObject) { return $null }
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    $property.Value
}

function Assert-GridSkyrimRuntimeLeaf {
    param([Parameter(Mandatory)][string]$Value, [Parameter(Mandatory)][string]$Name)
    $hasControlCharacter = @($Value.ToCharArray() | Where-Object { [char]::IsControl([char]$_) }).Count -gt 0
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.Length -gt 128 -or $Value -in @('.', '..') -or
        $Value.IndexOfAny([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)) -ge 0 -or
        $hasControlCharacter) {
        throw "RuntimeCertificationInvalid: $Name must be one bounded directory/argument leaf."
    }
}

function Test-GridSkyrimRuntimeContainedPath {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Root, [switch]$ImmediateChild)
    try {
        $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
        $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    }
    catch { return $false }
    if ($ImmediateChild) { return ([IO.Path]::GetDirectoryName($full) -ieq $rootFull) }
    $full -ieq $rootFull -or $full.StartsWith($rootFull + '\', [StringComparison]::OrdinalIgnoreCase)
}

function New-GridSkyrimDisposableProfilePlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$')][string]$CaseId,
        [Parameter(Mandatory)][string]$SourceProfileDirectory,
        [Parameter(Mandatory)][string]$ProfilesRoot,
        [Parameter(Mandatory)][string]$ContextFingerprint,
        [Parameter(Mandatory)][string]$RepairFingerprint
    )
    Assert-GridSkyrimRuntimeDigest $ContextFingerprint ContextFingerprint
    Assert-GridSkyrimRuntimeDigest $RepairFingerprint RepairFingerprint
    $source = [IO.Path]::GetFullPath($SourceProfileDirectory).TrimEnd('\')
    $profiles = [IO.Path]::GetFullPath($ProfilesRoot).TrimEnd('\')
    if (-not (Test-GridSkyrimRuntimeContainedPath -Path $source -Root $profiles -ImmediateChild)) {
        throw 'RuntimeCertificationInvalid: the source profile must be one immediate child of the validated profiles root.'
    }
    $identity = Get-GridSkyrimRuntimeFingerprint @($CaseId, $source.ToLowerInvariant(), $ContextFingerprint, $RepairFingerprint)
    $name = 'Grid Runtime ' + $identity.Substring(0, 16).ToLowerInvariant()
    $target = Join-Path $profiles $name
    $allowlist = @('modlist.txt','plugins.txt','loadorder.txt','lockedorder.txt','settings.ini','skyrim.ini','skyrimprefs.ini','skyrimcustom.ini','initweaks.ini')
    $operations = New-Object Collections.Generic.List[object]
    foreach ($leaf in $allowlist) {
        $sourcePath = Join-Path $source $leaf
        $present = Test-Path -LiteralPath $sourcePath -PathType Leaf
        $sourceItem = if ($present) { Get-Item -LiteralPath $sourcePath -Force } else { $null }
        $operations.Add([pscustomobject][ordered]@{
            leafName = $leaf
            sourcePath = $sourcePath
            destinationPath = (Join-Path $target $leaf)
            sourceState = if ($present) { 'Present' } else { 'Absent' }
            sourceSizeBytes = if ($present) { [long]$sourceItem.Length } else { $null }
            sourceSha256 = if ($present) { (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash } else { $null }
        })
    }
    [pscustomobject][ordered]@{
        schemaVersion = 1; caseId = $CaseId; profileName = $name; sourceProfileDirectory = $source
        profilesRoot = $profiles; targetProfileDirectory = $target; contextFingerprint = $ContextFingerprint
        repairFingerprint = $RepairFingerprint; copyOperations = $operations.ToArray(); createEmptyDirectories = @((Join-Path $target 'saves'))
        prohibitedSourcePaths = @((Join-Path $source 'saves'))
        sourceMutationAllowed = $false; saveEnumerationAllowed = $false; requiresPhysicalBoundaryValidation = $true
        planFingerprint = (Get-GridSkyrimRuntimeFingerprint @($identity, ($operations | ForEach-Object { "$($_.leafName)|$($_.sourceState)|$($_.sourceSizeBytes)|$($_.sourceSha256)" })))
    }
}

function Test-GridSkyrimDisposableProfileMaterialization {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Plan)
    $issues = New-Object Collections.Generic.List[string]
    $isolation = Test-GridSkyrimDisposableProfileIsolation -Plan $Plan -RequireExistingRoots
    foreach ($issue in $isolation.issues) { $issues.Add([string]$issue) }
    $target = [string]$Plan.targetProfileDirectory
    if (-not (Test-Path -LiteralPath $target -PathType Container)) { $issues.Add('The disposable profile directory is absent.') }
    else {
        $targetItem = Get-Item -LiteralPath $target -Force
        if (($targetItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $issues.Add('The disposable profile is a reparse point.') }
        $allowed = @($Plan.copyOperations | ForEach-Object { [string]$_.leafName })
        foreach ($file in @(Get-ChildItem -LiteralPath $target -File -Force -ErrorAction SilentlyContinue)) {
            if ($file.Name -notin $allowed) { $issues.Add("Unexpected disposable-profile file: $($file.Name)") }
        }
        foreach ($directory in @(Get-ChildItem -LiteralPath $target -Directory -Force -ErrorAction SilentlyContinue)) {
            if ($directory.Name -ine 'saves') { $issues.Add("Unexpected disposable-profile directory: $($directory.Name)") }
            elseif (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $issues.Add('The disposable save root is a reparse point.') }
            elseif (@(Get-ChildItem -LiteralPath $directory.FullName -Force -ErrorAction SilentlyContinue).Count -ne 0) { $issues.Add('The disposable save root is not empty.') }
        }
        if (-not (Test-Path -LiteralPath (Join-Path $target 'saves') -PathType Container)) { $issues.Add('The isolated empty save root is absent.') }
    }
    foreach ($operation in @($Plan.copyOperations)) {
        $sourcePresent = Test-Path -LiteralPath ([string]$operation.sourcePath) -PathType Leaf
        $destinationPresent = Test-Path -LiteralPath ([string]$operation.destinationPath) -PathType Leaf
        if ([string]$operation.sourceState -eq 'Present') {
            if (-not $sourcePresent -or -not $destinationPresent) { $issues.Add("Required isolated profile file is absent: $($operation.leafName)"); continue }
            $sourceHash = (Get-FileHash -LiteralPath ([string]$operation.sourcePath) -Algorithm SHA256).Hash
            $destinationHash = (Get-FileHash -LiteralPath ([string]$operation.destinationPath) -Algorithm SHA256).Hash
            if ($sourceHash -ne [string]$operation.sourceSha256) { $issues.Add("Source profile drifted after planning: $($operation.leafName)") }
            if ($destinationHash -ne [string]$operation.sourceSha256) { $issues.Add("Disposable profile copy differs: $($operation.leafName)") }
        }
        elseif ($destinationPresent) { $issues.Add("An absent source file was fabricated in the disposable profile: $($operation.leafName)") }
    }
    $status = if ($issues.Count -eq 0) { 'Verified' } else { 'Refused' }
    [pscustomobject][ordered]@{
        status=$status; planFingerprint=[string]$Plan.planFingerprint; targetProfileDirectory=$target
        files=@($Plan.copyOperations | Where-Object sourceState -eq 'Present' | ForEach-Object { [pscustomobject][ordered]@{ leafName=[string]$_.leafName; sha256=[string]$_.sourceSha256 } })
        saveRootState=if ($status -eq 'Verified') { 'Empty' } else { 'Unverified' }; issues=$issues.ToArray()
        materializationFingerprint=(Get-GridSkyrimRuntimeFingerprint @([string]$Plan.planFingerprint,$status,(@($issues | Sort-Object) -join ';')))
    }
}

function Test-GridSkyrimDisposableProfileIsolation {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Plan, [switch]$RequireExistingRoots)
    $issues = New-Object Collections.Generic.List[string]
    if ([int]$Plan.schemaVersion -ne 1) { $issues.Add('Unsupported profile-plan schema version.') }
    if ([string]$Plan.sourceProfileDirectory -ieq [string]$Plan.targetProfileDirectory) { $issues.Add('Source and disposable profiles are identical.') }
    if (-not (Test-GridSkyrimRuntimeContainedPath -Path ([string]$Plan.sourceProfileDirectory) -Root ([string]$Plan.profilesRoot) -ImmediateChild)) { $issues.Add('Source profile escapes the validated profiles root.') }
    if (-not (Test-GridSkyrimRuntimeContainedPath -Path ([string]$Plan.targetProfileDirectory) -Root ([string]$Plan.profilesRoot) -ImmediateChild)) { $issues.Add('Disposable profile escapes the validated profiles root.') }
    $allowed = @('modlist.txt','plugins.txt','loadorder.txt','lockedorder.txt','settings.ini','skyrim.ini','skyrimprefs.ini','skyrimcustom.ini','initweaks.ini')
    foreach ($operation in @($Plan.copyOperations)) {
        if ([string]$operation.leafName -notin $allowed) { $issues.Add("Profile copy '$($operation.leafName)' is not allowlisted."); continue }
        if (-not (Test-GridSkyrimRuntimeContainedPath -Path ([string]$operation.sourcePath) -Root ([string]$Plan.sourceProfileDirectory)) -or
            -not (Test-GridSkyrimRuntimeContainedPath -Path ([string]$operation.destinationPath) -Root ([string]$Plan.targetProfileDirectory)) -or
            [string]$operation.sourcePath -match '(?i)[\\/]saves(?:[\\/]|$)' -or [string]$operation.destinationPath -match '(?i)[\\/]saves(?:[\\/]|$)') {
            $issues.Add("Profile copy '$($operation.leafName)' violates isolation.")
        }
    }
    if ($RequireExistingRoots) {
        foreach ($path in @([string]$Plan.profilesRoot, [string]$Plan.sourceProfileDirectory)) {
            if (-not (Test-Path -LiteralPath $path -PathType Container)) { $issues.Add("Required profile root is absent: $path"); continue }
            $cursor = Get-Item -LiteralPath $path -Force
            while ($cursor) {
                if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $issues.Add("Reparse boundary is not allowed: $($cursor.FullName)"); break }
                $cursor = $cursor.Parent
            }
        }
    }
    [pscustomobject][ordered]@{ status = if ($issues.Count -eq 0) { 'Valid' } else { 'Refused' }; issues = $issues.ToArray() }
}

function New-GridSkyrimMo2ProfileLaunchInvocation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Mo2ExecutablePath,
        [Parameter(Mandatory)][ValidateSet('Portable','Global')][string]$InstanceKind,
        [Parameter(Mandatory)][string]$InstanceDirectory,
        [Parameter(Mandatory)][string]$ProfileName,
        [Parameter(Mandatory)][string]$ExecutableTitle,
        [Parameter(Mandatory)][string]$Mo2ExecutableIdentity,
        [int]$ExecutableSourceIndex = -1,
        [string]$ConfiguredExecutablePath,
        [string]$ConfiguredExecutableSha256
    )
    Assert-GridSkyrimRuntimeLeaf $ProfileName ProfileName
    Assert-GridSkyrimRuntimeLeaf $ExecutableTitle ExecutableTitle
    if ([IO.Path]::GetFileName($Mo2ExecutablePath) -ine 'ModOrganizer.exe') { throw 'RuntimeCertificationInvalid: the launch route must use ModOrganizer.exe.' }
    $arguments = New-Object Collections.Generic.List[string]
    if ($InstanceKind -eq 'Global') {
        $instanceLeaf = [IO.Path]::GetFileName(([IO.Path]::GetFullPath($InstanceDirectory).TrimEnd('\')))
        Assert-GridSkyrimRuntimeLeaf $instanceLeaf InstanceName
        $arguments.Add('-i'); $arguments.Add($instanceLeaf)
    }
    $arguments.Add('-p'); $arguments.Add($ProfileName); $arguments.Add('run'); $arguments.Add('-e'); $arguments.Add($ExecutableTitle)
    $fullExecutable = [IO.Path]::GetFullPath($Mo2ExecutablePath)
    $configuredPath = if ([string]::IsNullOrWhiteSpace($ConfiguredExecutablePath)) { $null } else { [IO.Path]::GetFullPath($ConfiguredExecutablePath) }
    if ($ExecutableSourceIndex -ge 0) {
        if ($null -eq $configuredPath -or $ConfiguredExecutableSha256 -cnotmatch '^[A-F0-9]{64}$') {
            throw 'RuntimeCertificationInvalid: a source-indexed executable requires its exact configured path and uppercase SHA-256.'
        }
    }
    [pscustomobject][ordered]@{
        executablePath = $fullExecutable; arguments = $arguments.ToArray(); profileName = $ProfileName
        executableTitle = $ExecutableTitle; instanceKind = $InstanceKind; executableIdentity = $Mo2ExecutableIdentity
        executableSourceIndex = $ExecutableSourceIndex; configuredExecutablePath = $configuredPath; configuredExecutableSha256 = $ConfiguredExecutableSha256
        routeFingerprint = (Get-GridSkyrimRuntimeFingerprint (@($fullExecutable, $Mo2ExecutableIdentity) + $arguments.ToArray()))
    }
}

function Assert-GridSkyrimRuntimeRecipe {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Recipe)
    foreach ($field in @('schemaVersion','recipeId','recipeVersion','gameId','route','navigation','oracle','captures','limits')) {
        if ($null -eq $Recipe.PSObject.Properties[$field]) { throw "RuntimeRecipeInvalid: missing '$field'." }
    }
    if ([int]$Recipe.schemaVersion -ne 1 -or [string]$Recipe.gameId -ne 'skyrimspecialedition') { throw 'RuntimeRecipeInvalid: unsupported schema or game.' }
    if ([string]$Recipe.route.status -notin @('Available','Unavailable')) { throw 'RuntimeRecipeInvalid: route status is invalid.' }
    $allowedActions = @('Wait','MoveForward','MoveBackward','StrafeLeft','StrafeRight','TurnLeft','TurnRight','LookUp','LookDown','Jump','ToggleHud','CloseMenu','CaptureScreenshot','ObserveRuntimeReference')
    $steps = @($Recipe.navigation)
    if ($steps.Count -gt [int]$Recipe.limits.maximumInputSteps -or $steps.Count -gt 256) { throw 'RuntimeRecipeInvalid: navigation exceeds the step ceiling.' }
    [long]$duration = 0
    foreach ($step in $steps) {
        if ([string]$step.action -notin $allowedActions) { throw "RuntimeRecipeInvalid: action '$($step.action)' is not allowlisted." }
        if ([int]$step.durationMilliseconds -lt 0 -or [int]$step.durationMilliseconds -gt 5000) { throw 'RuntimeRecipeInvalid: a step duration exceeds 5000 milliseconds.' }
        $duration += [int]$step.durationMilliseconds
    }
    if ($duration -gt ([long][int]$Recipe.limits.maximumRuntimeSeconds * 1000) -or [int]$Recipe.limits.maximumRuntimeSeconds -gt 900) { throw 'RuntimeRecipeInvalid: recipe exceeds the 15-minute runtime ceiling.' }
    if ([int]$Recipe.captures.maximumScreenshots -gt 6 -or [long]$Recipe.captures.maximumScreenshotBytes -gt 33554432 -or [long]$Recipe.captures.maximumAggregateLogBytes -gt 268435456) { throw 'RuntimeRecipeInvalid: capture limits exceed policy.' }
    if ([string]$Recipe.oracle.kind -notin @('DeterministicSnapshot','RuntimeReferenceProbe','Unavailable')) { throw 'RuntimeRecipeInvalid: oracle kind is invalid.' }
    if ($null -eq $Recipe.oracle.PSObject.Properties['requiredClaims']) { throw "RuntimeRecipeInvalid: oracle is missing 'requiredClaims'." }
    $requiredClaims = @($Recipe.oracle.requiredClaims)
    if ($requiredClaims.Count -lt 1 -or $requiredClaims.Count -gt 64) { throw 'RuntimeRecipeInvalid: oracle must declare between one and 64 required claims.' }
    $claimSet = @{}
    foreach ($claim in $requiredClaims) {
        $claimId = [string]$claim
        if ($claimId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') { throw "RuntimeRecipeInvalid: oracle claim '$claimId' is not a bounded identifier." }
        $key = $claimId.ToLowerInvariant()
        if ($claimSet.ContainsKey($key)) { throw "RuntimeRecipeInvalid: oracle claim '$claimId' is duplicated." }
        $claimSet[$key] = $true
    }
    $true
}

function Get-GridSkyrimRuntimeRecipeFingerprint {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Recipe)
    [void](Assert-GridSkyrimRuntimeRecipe $Recipe)
    Get-GridSkyrimRuntimeFingerprint @(
        [string]$Recipe.recipeId, [string]$Recipe.recipeVersion, [string]$Recipe.gameId,
        [string]$Recipe.route.status, [string]$Recipe.route.routeId,
        [string]$Recipe.oracle.kind, [string]$Recipe.oracle.collectorName, (@($Recipe.oracle.requiredClaims | Sort-Object) -join ';'),
        [string]$Recipe.captures.maximumScreenshots, [string]$Recipe.captures.maximumScreenshotBytes, [string]$Recipe.captures.maximumAggregateLogBytes,
        [string]$Recipe.limits.maximumRuntimeSeconds, [string]$Recipe.limits.maximumInputSteps,
        (@($Recipe.navigation | Sort-Object sequence | ForEach-Object { "$($_.sequence)|$($_.action)|$($_.durationMilliseconds)" }) -join ';')
    )
}

function Test-GridSkyrimTypedProfileInvocation {
    param([Parameter(Mandatory)]$Invocation, [Parameter(Mandatory)][string]$ExpectedProfileName)
    $arguments = @($Invocation.arguments)
    $valid = $false
    if ([string]$Invocation.instanceKind -eq 'Portable') {
        $valid = $arguments.Count -eq 5 -and $arguments[0] -ceq '-p' -and $arguments[1] -ceq $ExpectedProfileName -and $arguments[2] -ceq 'run' -and $arguments[3] -ceq '-e'
    }
    elseif ([string]$Invocation.instanceKind -eq 'Global') {
        $valid = $arguments.Count -eq 7 -and $arguments[0] -ceq '-i' -and -not [string]::IsNullOrWhiteSpace([string]$arguments[1]) -and $arguments[2] -ceq '-p' -and $arguments[3] -ceq $ExpectedProfileName -and $arguments[4] -ceq 'run' -and $arguments[5] -ceq '-e'
    }
    if (-not $valid -or [string]::IsNullOrWhiteSpace([string]$arguments[-1])) { return $false }
    $sourceIndex = Get-GridSkyrimRuntimePropertyValue $Invocation 'executableSourceIndex'
    if ($null -ne $sourceIndex -and [int]$sourceIndex -ge 0) {
        if ([string](Get-GridSkyrimRuntimePropertyValue $Invocation 'configuredExecutablePath') -eq '' -or
            [string](Get-GridSkyrimRuntimePropertyValue $Invocation 'configuredExecutableSha256') -cnotmatch '^[A-F0-9]{64}$') { return $false }
    }
    $true
}

function Resolve-GridSkyrimOwnedRuntimeProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$BeforeSnapshot,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$AfterSnapshot,
        [Parameter(Mandatory)][string]$ExpectedExecutablePath,
        [Parameter(Mandatory)][int]$ManagerProcessId,
        [Parameter(Mandatory)][datetime]$LaunchStartedUtc
    )
    $before = @{}
    foreach ($item in $BeforeSnapshot) { $before[([string]$item.processId + '|' + [string]$item.startTimeUtc)] = $true }
    $expected = [IO.Path]::GetFullPath($ExpectedExecutablePath)
    $matches = @($AfterSnapshot | Where-Object {
        $key = [string]$_.processId + '|' + [string]$_.startTimeUtc
        $pathMatch = $false; try { $pathMatch = [IO.Path]::GetFullPath([string]$_.executablePath) -ieq $expected } catch { }
        $startMatch = $false; try { $startMatch = ([datetime]$_.startTimeUtc).ToUniversalTime() -ge $LaunchStartedUtc.ToUniversalTime() } catch { }
        $ancestry = @($_.ancestorProcessIds | ForEach-Object { [int]$_ })
        (-not $before.ContainsKey($key)) -and $pathMatch -and $startMatch -and ($ancestry -contains $ManagerProcessId)
    } | Sort-Object { [datetime]$_.startTimeUtc }, { [int]$_.processId })
    if ($matches.Count -eq 0) { return [pscustomobject][ordered]@{ state='NotObserved'; process=$null; reason='No new exact executable with the recorded MO2 ancestor was observed.' } }
    if ($matches.Count -gt 1) { return [pscustomobject][ordered]@{ state='Ambiguous'; process=$null; reason="$($matches.Count) matching child processes were observed; ownership is not unique." } }
    $match = $matches[0]
    [pscustomobject][ordered]@{
        state='Owned'; process=[pscustomobject][ordered]@{ processId=[int]$match.processId; executablePath=$expected; startTimeUtc=([datetime]$match.startTimeUtc).ToUniversalTime().ToString('o'); managerProcessId=$ManagerProcessId }
        reason=$null
    }
}

function Get-GridSkyrimRuntimeFileAudit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('Screenshot','Log')][string]$Kind,
        [Parameter(Mandatory)][string[]]$LiteralPath,
        [Parameter(Mandatory)][string]$AllowedRoot,
        [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$RecipeFingerprint,
        [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$ContextFingerprint,
        [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$RepairFingerprint,
        [ValidateRange(1, 32)][int]$MaximumFiles = 6,
        [ValidateRange(1, 268435456)][long]$MaximumFileBytes = 33554432,
        [ValidateRange(1, 268435456)][long]$MaximumAggregateBytes = 268435456
    )
    if ($LiteralPath.Count -gt $MaximumFiles) { throw "RuntimeCertificationBudgetExceeded: $Kind count exceeds $MaximumFiles." }
    $observations = New-Object Collections.Generic.List[object]; [long]$aggregate = 0
    foreach ($path in @($LiteralPath | Sort-Object -Unique)) {
        $full = [IO.Path]::GetFullPath($path)
        if (-not (Test-GridSkyrimRuntimeContainedPath -Path $full -Root $AllowedRoot)) { throw "RuntimeCertificationBoundaryRefused: $Kind path escapes its allowed root." }
        try {
            $item = Get-Item -LiteralPath $full -Force -ErrorAction Stop
            if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Only an exact ordinary file is permitted.' }
            if ($item.Length -gt $MaximumFileBytes) { throw "File exceeds the $MaximumFileBytes-byte ceiling." }
            $aggregate += $item.Length
            if ($aggregate -gt $MaximumAggregateBytes) { throw "Aggregate exceeds the $MaximumAggregateBytes-byte ceiling." }
            $observations.Add([pscustomobject][ordered]@{ path=$full; state='Readable'; sizeBytes=[long]$item.Length; lastWriteTimeUtc=$item.LastWriteTimeUtc.ToString('o'); sha256=(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash })
        }
        catch { $observations.Add([pscustomobject][ordered]@{ path=$full; state='Unreadable'; sizeBytes=$null; lastWriteTimeUtc=$null; sha256=$null; error=$_.Exception.Message }) }
    }
    [pscustomobject][ordered]@{
        kind=$Kind; recipeFingerprint=$RecipeFingerprint; contextFingerprint=$ContextFingerprint; repairFingerprint=$RepairFingerprint
        files=$observations.ToArray(); aggregateBytes=$aggregate
        status=if ($observations.Count -gt 0 -and @($observations | Where-Object state -ne 'Readable').Count -eq 0) { 'Complete' } else { 'Incomplete' }
    }
}

function Resolve-GridSkyrimRuntimePrelaunchGates {
    param(
        [AllowNull()][object[]]$GateEvidence,
        [Parameter(Mandatory)][string]$ContextFingerprint,
        [Parameter(Mandatory)][string]$RepairFingerprint,
        $RollbackReceipt
    )
    $required = @('InstallationIntegrity','CompatibilityIntegrity','AssetIntegrity','RecordIntegrity','PluginIntegrity','ProfileIntegrity','RollbackIntegrity')
    $results = New-Object Collections.Generic.List[object]
    $seen = @{}
    foreach ($item in @($GateEvidence)) {
        $name = [string](Get-GridSkyrimRuntimePropertyValue $item name)
        $key = $name.ToLowerInvariant()
        if ($name -notin $required) { continue }
        if ($seen.ContainsKey($key)) {
            $existing = @($results | Where-Object name -eq $name)[0]
            $existing.status='Fail'; $existing.evidenceSha256=$null; $existing.issue='Gate evidence is duplicated.'
            continue
        }
        $seen[$key] = $true
        $status = [string](Get-GridSkyrimRuntimePropertyValue $item status)
        $issue = $null
        if ($status -notin @('Pass','Fail','NotEvaluated')) { $status='Fail'; $issue='Gate status is invalid.' }
        elseif ([string](Get-GridSkyrimRuntimePropertyValue $item contextFingerprint) -ne $ContextFingerprint -or [string](Get-GridSkyrimRuntimePropertyValue $item repairFingerprint) -ne $RepairFingerprint) { $status='Fail'; $issue='Gate evidence is bound to a different context or repair.' }
        elseif ($status -in @('Pass','Fail') -and [string](Get-GridSkyrimRuntimePropertyValue $item evidenceSha256) -cnotmatch '^[A-F0-9]{64}$') { $status='Fail'; $issue='Gate evidence digest is absent or malformed.' }
        $itemDigest = [string](Get-GridSkyrimRuntimePropertyValue $item evidenceSha256)
        $results.Add([pscustomobject][ordered]@{ name=$name; status=$status; evidenceSha256=if ($itemDigest -match '^[A-F0-9]{64}$') { $itemDigest } else { $null }; issue=if ($issue) { $issue } elseif ($item.PSObject.Properties['issue']) { [string]$item.issue } else { $null } })
    }
    foreach ($name in $required) {
        if (-not $seen.ContainsKey($name.ToLowerInvariant())) { $results.Add([pscustomobject][ordered]@{ name=$name; status='NotEvaluated'; evidenceSha256=$null; issue='Required independent gate evidence is absent.' }) }
    }
    $rollback = @($results | Where-Object name -eq 'RollbackIntegrity')[0]
    if ($rollback.status -eq 'Pass') {
        if (-not $RollbackReceipt) { $rollback.status='NotEvaluated'; $rollback.evidenceSha256=$null; $rollback.issue='A verified rollback receipt is required.' }
        elseif ([string](Get-GridSkyrimRuntimePropertyValue $RollbackReceipt status) -ne 'Verified' -or [string](Get-GridSkyrimRuntimePropertyValue $RollbackReceipt contextFingerprint) -ne $ContextFingerprint -or [string](Get-GridSkyrimRuntimePropertyValue $RollbackReceipt repairFingerprint) -ne $RepairFingerprint) { $rollback.status='Fail'; $rollback.issue='Rollback receipt status, context binding, or repair binding is invalid.' }
        else {
            foreach ($field in @('receiptSha256','journalSha256','rollbackManifestSha256','priorTreeSha256')) {
                if ([string](Get-GridSkyrimRuntimePropertyValue $RollbackReceipt $field) -cnotmatch '^[A-F0-9]{64}$') { $rollback.status='Fail'; $rollback.issue="Rollback receipt is missing valid '$field'."; break }
            }
        }
    }
    @($results | Sort-Object { [array]::IndexOf($required, [string]$_.name) })
}

function Test-GridSkyrimRuntimeBoundAudit {
    param($Audit, [string]$Kind, [string]$RecipeFingerprint, [string]$ContextFingerprint, [string]$RepairFingerprint)
    $files = @(Get-GridSkyrimRuntimePropertyValue $Audit files)
    if (-not $Audit -or [string](Get-GridSkyrimRuntimePropertyValue $Audit kind) -ne $Kind -or [string](Get-GridSkyrimRuntimePropertyValue $Audit status) -ne 'Complete' -or $files.Count -lt 1) { return $false }
    if ([string](Get-GridSkyrimRuntimePropertyValue $Audit recipeFingerprint) -ne $RecipeFingerprint -or [string](Get-GridSkyrimRuntimePropertyValue $Audit contextFingerprint) -ne $ContextFingerprint -or [string](Get-GridSkyrimRuntimePropertyValue $Audit repairFingerprint) -ne $RepairFingerprint) { return $false }
    @($files | Where-Object { [string](Get-GridSkyrimRuntimePropertyValue $_ state) -ne 'Readable' -or [string](Get-GridSkyrimRuntimePropertyValue $_ sha256) -cnotmatch '^[A-F0-9]{64}$' }).Count -eq 0
}

function Invoke-GridSkyrimRuntimeCertification {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)]$Recipe,
        [Parameter(Mandatory)]$ProfilePlan,
        $LineageBinding,
        [AllowNull()][object[]]$PrelaunchGates,
        $RollbackReceipt,
        $ProfileIsolationReceipt,
        $LaunchInvocation,
        $OwnershipObservation,
        $OracleResult,
        $ScreenshotAudit,
        $LogAudit
    )
    [void](Assert-GridSkyrimRuntimeRecipe $Recipe)
    $isolation = Test-GridSkyrimDisposableProfileIsolation -Plan $ProfilePlan
    $issues = New-Object Collections.Generic.List[string]
    $recipeFingerprint = Get-GridSkyrimRuntimeRecipeFingerprint $Recipe
    $gates = @(Resolve-GridSkyrimRuntimePrelaunchGates -GateEvidence $PrelaunchGates -ContextFingerprint ([string]$ProfilePlan.contextFingerprint) -RepairFingerprint ([string]$ProfilePlan.repairFingerprint) -RollbackReceipt $RollbackReceipt)
    $lineageValid = $true
    if (-not $LineageBinding -or [string](Get-GridSkyrimRuntimePropertyValue $LineageBinding status) -ne 'Verified' -or [string](Get-GridSkyrimRuntimePropertyValue $LineageBinding contextFingerprint) -ne [string]$ProfilePlan.contextFingerprint -or [string](Get-GridSkyrimRuntimePropertyValue $LineageBinding repairFingerprint) -ne [string]$ProfilePlan.repairFingerprint) { $lineageValid=$false; $issues.Add('A verified active-lineage binding is required.') }
    else {
        foreach ($field in @('baselineManifestSha256','diagnosisManifestSha256','repairSpecificationSha256','repairReceiptSha256')) {
            if ([string](Get-GridSkyrimRuntimePropertyValue $LineageBinding $field) -cnotmatch '^[A-F0-9]{64}$') { $lineageValid=$false; $issues.Add("Active-lineage binding is missing valid '$field'.") }
        }
    }
    $failedPrelaunch = @($gates | Where-Object status -eq 'Fail').Count -gt 0
    $incompletePrelaunch = @($gates | Where-Object status -ne 'Pass').Count -gt 0
    $eligibility = if ($lineageValid -and -not $incompletePrelaunch) { 'Eligible' } else { 'PreconditionsNotMet' }
    $runtimeValidation = 'NotStarted'; $status = 'PreconditionsNotMet'; $ready = 'NotEvaluated'
    if ($failedPrelaunch) { $status='NotReadyToPlay'; $ready=$false; $issues.Add('One or more independent prelaunch gates failed.') }
    elseif (-not $lineageValid -or $incompletePrelaunch) { $issues.Add('Runtime certification cannot start until every independent prelaunch gate passes.') }
    elseif ($isolation.status -ne 'Valid') { $status='Failed'; $ready=$false; $runtimeValidation='Fail'; foreach ($issue in $isolation.issues) { $issues.Add([string]$issue) } }
    elseif (-not $ProfileIsolationReceipt -or [string]$ProfileIsolationReceipt.status -ne 'Verified' -or [string]$ProfileIsolationReceipt.planFingerprint -ne [string]$ProfilePlan.planFingerprint -or [string]$ProfileIsolationReceipt.saveRootState -ne 'Empty') { $status='NeedsRuntimeVerification'; $ready=$false; $runtimeValidation='NeedsRuntimeVerification'; $issues.Add('The disposable profile has not been independently materialized and verified with an empty save root.') }
    elseif ([string]$Recipe.route.status -ne 'Available') { $status='NeedsRuntimeVerification'; $ready=$false; $runtimeValidation='NeedsRuntimeVerification'; $issues.Add('No validated navigation route is available.') }
    elseif (-not $LaunchInvocation) { $status='NeedsRuntimeVerification'; $ready=$false; $runtimeValidation='NeedsRuntimeVerification'; $issues.Add('No typed MO2 disposable-profile invocation was materialized.') }
    elseif (-not (Test-GridSkyrimTypedProfileInvocation -Invocation $LaunchInvocation -ExpectedProfileName ([string]$ProfilePlan.profileName))) { $status='Failed'; $ready=$false; $runtimeValidation='Fail'; $issues.Add('The launch invocation is not the exact typed disposable-profile route.') }
    elseif (-not $OwnershipObservation -or [string]$OwnershipObservation.state -eq 'NotObserved') { $status='NeedsRuntimeVerification'; $ready=$false; $runtimeValidation='NeedsRuntimeVerification'; $issues.Add('No exact Grid-owned Skyrim child process was observed.') }
    elseif ([string]$OwnershipObservation.state -ne 'Owned') { $status='Failed'; $ready=$false; $runtimeValidation='Fail'; $issues.Add('Runtime process ownership is ambiguous or invalid.') }
    elseif ([string]$Recipe.oracle.kind -eq 'Unavailable' -or -not $OracleResult) { $status='NeedsRuntimeVerification'; $ready=$false; $runtimeValidation='NeedsRuntimeVerification'; $issues.Add('No deterministic visual/runtime oracle receipt is available.') }
    else {
        foreach ($field in @('status','recipeFingerprint','contextFingerprint','repairFingerprint','collectorName','observations')) {
            if ($null -eq $OracleResult.PSObject.Properties[$field]) { $status='Failed'; $ready=$false; $runtimeValidation='Fail'; $issues.Add("Oracle receipt is missing '$field'.") }
        }
        if ($status -ne 'Failed') {
            $expectedRecipe = Get-GridSkyrimRuntimeRecipeFingerprint $Recipe
            if ([string]$OracleResult.recipeFingerprint -ne $expectedRecipe -or [string]$OracleResult.contextFingerprint -ne [string]$ProfilePlan.contextFingerprint -or [string]$OracleResult.repairFingerprint -ne [string]$ProfilePlan.repairFingerprint) { $status='Failed'; $ready=$false; $runtimeValidation='Fail'; $issues.Add('Oracle receipt fingerprints do not match the recipe, context, and repair.') }
            elseif ([string]$OracleResult.collectorName -ne [string]$Recipe.oracle.collectorName) { $status='Failed'; $ready=$false; $runtimeValidation='Fail'; $issues.Add('Oracle receipt came from an unselected collector.') }
            else {
                $observations = @($OracleResult.observations)
                $observedClaims = @($observations | ForEach-Object { [string](Get-GridSkyrimRuntimePropertyValue $_ claimId) })
                $missingClaims = @($Recipe.oracle.requiredClaims | Where-Object { [string]$_ -notin $observedClaims })
                $duplicateClaims = @($observedClaims | Group-Object | Where-Object Count -ne 1)
                $invalidObservations = @($observations | Where-Object { [string](Get-GridSkyrimRuntimePropertyValue $_ claimId) -notin @($Recipe.oracle.requiredClaims) -or [string](Get-GridSkyrimRuntimePropertyValue $_ status) -ne 'Verified' -or [string](Get-GridSkyrimRuntimePropertyValue $_ evidenceSha256) -cnotmatch '^[A-F0-9]{64}$' })
                if ([string]$OracleResult.status -eq 'Passed' -and $missingClaims.Count -eq 0 -and $duplicateClaims.Count -eq 0 -and $invalidObservations.Count -eq 0) { $status='Certified'; $ready=$true; $runtimeValidation='Pass' }
                elseif ([string]$OracleResult.status -eq 'Failed') { $status='NotReadyToPlay'; $ready=$false; $runtimeValidation='Fail'; $issues.Add('The deterministic runtime oracle reported failure.') }
                else { $status='NeedsRuntimeVerification'; $ready=$false; $runtimeValidation='NeedsRuntimeVerification'; $issues.Add('The deterministic runtime oracle did not provide exact verified coverage for every required claim.') }
            }
        }
    }
    if ($eligibility -eq 'Eligible') {
        if (-not (Test-GridSkyrimRuntimeBoundAudit -Audit $ScreenshotAudit -Kind Screenshot -RecipeFingerprint $recipeFingerprint -ContextFingerprint ([string]$ProfilePlan.contextFingerprint) -RepairFingerprint ([string]$ProfilePlan.repairFingerprint))) { $ready=$false; if ($status -eq 'Certified') { $status='NeedsRuntimeVerification' }; if ($runtimeValidation -ne 'Fail') { $runtimeValidation='NeedsRuntimeVerification' }; $issues.Add('Bound screenshot evidence is absent, incomplete, or stale.') }
        if (-not (Test-GridSkyrimRuntimeBoundAudit -Audit $LogAudit -Kind Log -RecipeFingerprint $recipeFingerprint -ContextFingerprint ([string]$ProfilePlan.contextFingerprint) -RepairFingerprint ([string]$ProfilePlan.repairFingerprint))) { $ready=$false; if ($status -eq 'Certified') { $status='NeedsRuntimeVerification' }; if ($runtimeValidation -ne 'Fail') { $runtimeValidation='NeedsRuntimeVerification' }; $issues.Add('Bound runtime log evidence is absent, incomplete, or stale.') }
    }
    $evidenceDigests = @()
    if ($OracleResult) { $evidenceDigests += @((Get-GridSkyrimRuntimePropertyValue $OracleResult observations) | ForEach-Object { [string](Get-GridSkyrimRuntimePropertyValue $_ evidenceSha256) }) }
    if ($ScreenshotAudit) { $evidenceDigests += @((Get-GridSkyrimRuntimePropertyValue $ScreenshotAudit files) | ForEach-Object { [string](Get-GridSkyrimRuntimePropertyValue $_ sha256) }) }
    if ($LogAudit) { $evidenceDigests += @((Get-GridSkyrimRuntimePropertyValue $LogAudit files) | ForEach-Object { [string](Get-GridSkyrimRuntimePropertyValue $_ sha256) }) }
    $ownershipIdentity = if ($OwnershipObservation -and $OwnershipObservation.process) { "$($OwnershipObservation.process.processId)|$($OwnershipObservation.process.executablePath)|$($OwnershipObservation.process.startTimeUtc)" } else { '' }
    $materializationFingerprint = if ($ProfileIsolationReceipt -and $ProfileIsolationReceipt.PSObject.Properties['materializationFingerprint']) { [string]$ProfileIsolationReceipt.materializationFingerprint } else { '' }
    $runtimeGate = [pscustomobject][ordered]@{ name='RuntimeIntegrity'; status=$runtimeValidation; evidenceSha256=if ($runtimeValidation -eq 'Pass') { Get-GridSkyrimRuntimeFingerprint @($recipeFingerprint,(@($evidenceDigests | Sort-Object) -join ';')) } else { $null }; issue=if ($runtimeValidation -eq 'Pass') { $null } else { (@($issues | Sort-Object) -join '; ') } }
    $allGates = @($gates) + @($runtimeGate)
    $semantic = Get-GridSkyrimRuntimeFingerprint @($CaseId,[string]$Recipe.recipeId,[string]$ProfilePlan.planFingerprint,$materializationFingerprint,$eligibility,$runtimeValidation,$status,[string]$ready,$ownershipIdentity,(@($allGates | ForEach-Object { "$($_.name)|$($_.status)|$($_.evidenceSha256)" }) -join ';'),(@($evidenceDigests | Sort-Object) -join ';'),(@($issues | Sort-Object) -join ';'))
    [pscustomobject][ordered]@{
        schemaVersion=1; caseId=$CaseId; lifecycle='Completed'; certificationEligibility=$eligibility; runtimeValidation=$runtimeValidation; status=$status; readyToPlay=$ready
        profilePlanFingerprint=[string]$ProfilePlan.planFingerprint; routeFingerprint=if ($LaunchInvocation) { [string]$LaunchInvocation.routeFingerprint } else { $null }
        lineage=$LineageBinding; gates=$allGates; rollback=$RollbackReceipt; profileIsolation=$ProfileIsolationReceipt; ownership=$OwnershipObservation; oracle=$OracleResult; screenshots=$ScreenshotAudit; logs=$LogAudit
        issues=$issues.ToArray(); semanticFingerprint=$semantic
    }
}
