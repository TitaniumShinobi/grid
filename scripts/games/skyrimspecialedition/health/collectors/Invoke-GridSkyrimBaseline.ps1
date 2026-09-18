#requires -Version 5.1
<#
.SYNOPSIS
Collects a bounded Skyrim/MO2 baseline through Grid.Diagnostics.
.DESCRIPTION
This adapter entry point validates a Baseline-purpose InvestigationPlan,
creates a case-store transaction, records the resolved roots before traversal,
and invokes Grid.Diagnostics in read-only mo2-baseline mode. It never launches
MO2, xEdit, or the game and never changes profile or load-order state.
#>

function ConvertTo-GridNativeArgument {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Value)
    if ($Value -notmatch '[\s"]') { return $Value }
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    '"' + $escaped + '"'
}

function Get-GridBaselineByteSha256 {
    param([Parameter(Mandatory)][byte[]]$Bytes)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace('-', '') }
    finally { $algorithm.Dispose() }
}

function Get-GridBaselineOptionalProperty {
    param(
        [AllowNull()][object]$InputObject,
        [Parameter(Mandatory)][string]$Name
    )
    if ($null -eq $InputObject) { return $null }
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    $property.Value
}

function Get-GridBaselineTransactionTemporaryPaths {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Bridge,
        $Normalized,
        $ArchiveInspection
    )

    $paths = New-Object Collections.Generic.List[string]
    function Add-TemporaryPathValues([object[]]$Values) {
        foreach ($value in @($Values)) {
            if ($null -eq $value) { continue }
            $text = [string]$value
            if (-not [string]::IsNullOrWhiteSpace($text)) { $paths.Add($text) }
        }
    }

    foreach ($attempt in @($Bridge.Attempts)) {
        Add-TemporaryPathValues @($attempt.outputPath, $attempt.errorPath)
    }
    Add-TemporaryPathValues @($Bridge.BuildPath)
    if ($Normalized) { Add-TemporaryPathValues @($Normalized.StagingDirectory) }
    if ($ArchiveInspection) { Add-TemporaryPathValues @($ArchiveInspection.TemporaryPaths) }
    @($paths.ToArray() | Select-Object -Unique)
}

function Get-GridBaselineTes4RecordMatches {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$PluginPath,
        [Parameter(Mandatory)][uint32[]]$LocalFormIds
    )

    $item = Get-Item -LiteralPath $PluginPath -Force -ErrorAction Stop
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "RuntimeReferenceEvidenceRefused: plugin is not a regular non-reparse file: $PluginPath"
    }
    $targets = @{}
    foreach ($id in $LocalFormIds) { $targets[[uint32]$id] = $true }
    $matches = New-Object Collections.Generic.List[object]
    $stream = [IO.File]::Open($item.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $reader = New-Object IO.BinaryReader($stream, [Text.Encoding]::ASCII, $true)
        try {
            $ranges = New-Object Collections.Generic.Queue[object]
            $ranges.Enqueue([pscustomobject]@{ Start = 0L; End = [long]$stream.Length })
            while ($ranges.Count -gt 0) {
                $range = $ranges.Dequeue()
                $position = [long]$range.Start
                while ($position + 24L -le [long]$range.End) {
                    $stream.Position = $position
                    $signature = [Text.Encoding]::ASCII.GetString($reader.ReadBytes(4))
                    $dataSize = [uint32]$reader.ReadUInt32()
                    if ($signature -ceq 'GRUP') {
                        if ($dataSize -lt 24 -or $position + $dataSize -gt [long]$range.End) {
                            throw "RuntimeReferenceEvidenceMalformed: invalid TES4 group at offset $position in $PluginPath"
                        }
                        $ranges.Enqueue([pscustomobject]@{ Start = $position + 24L; End = $position + [long]$dataSize })
                        $position += [long]$dataSize
                        continue
                    }
                    $flags = [uint32]$reader.ReadUInt32()
                    $formId = [uint32]$reader.ReadUInt32()
                    [void]$reader.ReadBytes(8)
                    if ($dataSize -gt [long]$range.End - $position - 24L) {
                        throw "RuntimeReferenceEvidenceMalformed: invalid TES4 record at offset $position in $PluginPath"
                    }
                    $localFormId = [uint32]($formId -band 0x00FFFFFF)
                    if ($targets.ContainsKey($localFormId)) {
                        $baseObjectFormId = $null
                        $editorId = $null
                        $compressed = ($flags -band 0x00040000) -ne 0
                        if (-not $compressed) {
                            $data = $reader.ReadBytes([int]$dataSize)
                            $cursor = 0
                            $extendedSize = $null
                            while ($cursor + 6 -le $data.Length) {
                                $subrecord = [Text.Encoding]::ASCII.GetString($data, $cursor, 4)
                                $shortSize = [BitConverter]::ToUInt16($data, $cursor + 4)
                                $cursor += 6
                                $size = if ($null -ne $extendedSize) { $value = [uint32]$extendedSize; $extendedSize = $null; $value } else { [uint32]$shortSize }
                                if ([long]$cursor + [long]$size -gt $data.Length) { break }
                                if ($subrecord -ceq 'XXXX' -and $size -eq 4) {
                                    $extendedSize = [BitConverter]::ToUInt32($data, $cursor)
                                    $cursor += 4
                                    continue
                                }
                                if ($subrecord -ceq 'NAME' -and $size -eq 4) { $baseObjectFormId = [uint32][BitConverter]::ToUInt32($data, $cursor) }
                                if ($subrecord -ceq 'EDID') {
                                    $editorId = [Text.Encoding]::GetEncoding(1252).GetString($data, $cursor, [int]$size).Trim([char]0)
                                }
                                $cursor += [int]$size
                            }
                        }
                        $matches.Add([pscustomobject][ordered]@{
                            signature = $signature; rawFormId = $formId; localFormId = $localFormId; flags = $flags
                            compressed = $compressed; offset = $position; dataSize = [long]$dataSize
                            baseObjectFormId = $baseObjectFormId; editorId = $editorId
                        })
                    }
                    $position += 24L + [long]$dataSize
                }
                if ($position -ne [long]$range.End) {
                    throw "RuntimeReferenceEvidenceMalformed: trailing TES4 bytes at offset $position in $PluginPath"
                }
            }
        }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
    @($matches.ToArray())
}

function Get-GridSkyrimBaselineRuntimeReferenceEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$InvestigationPlan,
        [Parameter(Mandatory)][object[]]$PluginRecords,
        [Parameter(Mandatory)][object[]]$ResolvedRoots
    )

    $statedForms = @($InvestigationPlan.observedForms | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.formId) })
    $statedEditorIds = @($InvestigationPlan.observedForms | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.editorId) } | ForEach-Object { [string]$_.editorId } | Sort-Object -Unique)
    if ($statedForms.Count -eq 0 -or $statedEditorIds.Count -eq 0) {
        return [pscustomobject][ordered]@{ schemaVersion = 1; status = 'Incomplete'; evidenceId = $null; records = @(); issues = @('Both explicit FormIDs and EditorIDs are required for bounded installed-record verification.') }
    }
    $localIds = @($statedForms | ForEach-Object {
        $text = ([string]$_.formId).Trim()
        if ($text -notmatch '^(?i)(?:0x)?([0-9a-f]{8})$') { throw "PlanInvalid: unsupported FormID '$text'." }
        [uint32]([Convert]::ToUInt32($matches[1], 16) -band 0x00FFFFFF)
    } | Sort-Object -Unique)
    $modsRoots = @($ResolvedRoots | Where-Object { [string]$_.label -ceq 'Mods directory' })
    $dataRoots = @($ResolvedRoots | Where-Object { [string]$_.label -ceq 'Skyrim Data directory' })
    if ($modsRoots.Count -ne 1 -or $dataRoots.Count -ne 1) {
        return [pscustomobject][ordered]@{ schemaVersion = 1; status = 'Incomplete'; evidenceId = $null; records = @(); issues = @('One validated Mods root and Skyrim Data root are required.') }
    }
    $modsRoot = [IO.Path]::GetFullPath([string]$modsRoots[0].path).TrimEnd('\')
    $dataRoot = [IO.Path]::GetFullPath([string]$dataRoots[0].path).TrimEnd('\')
    $plugins = New-Object Collections.Generic.List[object]
    foreach ($plugin in @($PluginRecords | Where-Object { $_.isEnabled -eq $true -and $null -ne $_.loadOrder } | Sort-Object loadOrder)) {
        $name = [string]$plugin.name
        $provider = [string]$plugin.observation.sourceProvider
        if ([IO.Path]::GetFileName($name) -cne $name) { continue }
        $path = if ($provider -ceq 'Base game Data') { Join-Path $dataRoot $name } else {
            if ([IO.Path]::GetFileName($provider) -cne $provider) { continue }
            Join-Path (Join-Path $modsRoot $provider) $name
        }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $masters = if (Get-Command Get-GridTes4MasterNames -ErrorAction SilentlyContinue) { @(Get-GridTes4MasterNames -PluginPath $path) } else { @() }
        $plugins.Add([pscustomobject][ordered]@{
            name = $name; loadOrder = [int]$plugin.loadOrder; light = $plugin.observation.hasLightFlag -eq $true
            provider = $provider; path = [IO.Path]::GetFullPath($path); masters = $masters
            sha256 = [string]$plugin.observation.fingerprint
        })
    }
    $byName = @{}
    foreach ($plugin in $plugins) { $byName[[string]$plugin.name] = $plugin }
    $matchesByOrigin = @{}
    $issues = New-Object Collections.Generic.List[string]
    foreach ($plugin in $plugins) {
        try { $recordMatches = @(Get-GridBaselineTes4RecordMatches -PluginPath $plugin.path -LocalFormIds $localIds) }
        catch { $issues.Add($_.Exception.Message); continue }
        foreach ($record in $recordMatches) {
            if ($record.signature -cne 'REFR' -or $record.compressed -or $null -eq $record.baseObjectFormId) { continue }
            $originIndex = [int]([uint32]$record.rawFormId -shr 24)
            $origin = if ($originIndex -lt @($plugin.masters).Count) { [string]$plugin.masters[$originIndex] } elseif ($originIndex -eq @($plugin.masters).Count) { [string]$plugin.name } else { $null }
            if ([string]::IsNullOrWhiteSpace($origin)) { continue }
            $baseIndex = [int]([uint32]$record.baseObjectFormId -shr 24)
            $basePluginName = if ($baseIndex -lt @($plugin.masters).Count) { [string]$plugin.masters[$baseIndex] } elseif ($baseIndex -eq @($plugin.masters).Count) { [string]$plugin.name } else { $null }
            if ([string]::IsNullOrWhiteSpace($basePluginName) -or -not $byName.ContainsKey($basePluginName)) { continue }
            $basePlugin = $byName[$basePluginName]
            $baseLocalId = [uint32]([uint32]$record.baseObjectFormId -band 0x00FFFFFF)
            $baseMatches = @(Get-GridBaselineTes4RecordMatches -PluginPath $basePlugin.path -LocalFormIds @($baseLocalId) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.editorId) })
            if ($baseMatches.Count -ne 1) { continue }
            $key = $origin.ToLowerInvariant() + '|' + ('{0:X6}' -f [uint32]$record.localFormId)
            if (-not $matchesByOrigin.ContainsKey($key)) { $matchesByOrigin[$key] = New-Object Collections.Generic.List[object] }
            $matchesByOrigin[$key].Add([pscustomobject][ordered]@{
                containingPlugin = $plugin.name; loadOrder = $plugin.loadOrder; originPlugin = $origin
                localFormId = [uint32]$record.localFormId; rawFormId = ('{0:X8}' -f [uint32]$record.rawFormId)
                basePlugin = $basePluginName; baseObjectFormId = ('{0:X8}' -f [uint32]$record.baseObjectFormId)
                editorId = [string]$baseMatches[0].editorId; recordOffset = [long]$record.offset
                pluginSha256 = $plugin.sha256; basePluginSha256 = $basePlugin.sha256
            })
        }
    }
    $verified = New-Object Collections.Generic.List[object]
    foreach ($localId in $localIds) {
        $candidates = @($matchesByOrigin.Values | ForEach-Object { $_.ToArray() } | Where-Object { [uint32]$_.localFormId -eq [uint32]$localId -and [string]$_.editorId -in $statedEditorIds })
        if ($candidates.Count -eq 0) { continue }
        $winner = $candidates | Sort-Object loadOrder | Select-Object -Last 1
        $originPlugin = if ($byName.ContainsKey([string]$winner.originPlugin)) { $byName[[string]$winner.originPlugin] } else { $null }
        $fullSlot = if ($originPlugin) { @($plugins | Where-Object { -not $_.light -and $_.loadOrder -lt $originPlugin.loadOrder }).Count } else { $null }
        $stated = @($statedForms | Where-Object { ([Convert]::ToUInt32((([string]$_.formId) -replace '^(?i)0x',''),16) -band 0x00FFFFFF) -eq $localId } | ForEach-Object { [string]$_.formId })
        $verified.Add([pscustomobject][ordered]@{
            statedFormIds = $stated; stableLocalFormId = ('{0:X6}' -f [uint32]$localId)
            currentFormId = if ($null -ne $fullSlot -and $fullSlot -lt 0xFE) { '{0:X2}{1:X6}' -f @($fullSlot, [uint32]$localId) } else { $null }
            originPlugin = $winner.originPlugin; winningPlugin = $winner.containingPlugin; editorId = $winner.editorId
            basePlugin = $winner.basePlugin; baseObjectFormId = $winner.baseObjectFormId
            recordOffset = $winner.recordOffset; pluginSha256 = $winner.pluginSha256; basePluginSha256 = $winner.basePluginSha256
        })
    }
    $foundEditors = @($verified | ForEach-Object { [string]$_.editorId } | Sort-Object -Unique)
    $complete = $verified.Count -eq $localIds.Count -and @($statedEditorIds | Where-Object { $_ -notin $foundEditors }).Count -eq 0
    $payload = [pscustomobject][ordered]@{ schemaVersion = 1; status = if ($complete) { 'Verified' } else { 'Incomplete' }; records = $verified.ToArray(); issues = $issues.ToArray() }
    $canonical = $payload | ConvertTo-Json -Depth 20 -Compress
    $payload | Add-Member -NotePropertyName evidenceId -NotePropertyValue ('runtime-reference.' + (Get-GridBaselineByteSha256 -Bytes ([Text.Encoding]::UTF8.GetBytes($canonical))).Substring(0, 24).ToLowerInvariant())
    $payload
}

function Invoke-GridMo2BaselineCollection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)]$InvestigationPlan,
        [Parameter(Mandatory)][string]$TransactionDirectory,
        [Parameter(Mandatory)][string]$InstallationId,
        [Parameter(Mandatory)][string]$ProfileId,
        [Parameter(Mandatory)][string]$ProfileName,
        [string[]]$ParentCaseIds = @(),
        [string[]]$EvidenceIds = @(),
        [Parameter(Mandatory)][string[]]$RequiredGates,
        [Parameter(Mandatory)]$ResourceBudget,
        [Parameter(Mandatory)][string]$ResourcePolicyVersion,
        [Parameter(Mandatory)][string]$ApplicationPath,
        [Parameter(Mandatory)][string]$InstancePath,
        [string[]]$ExplicitProviderSeeds = @(),
        [string[]]$ExplicitPluginSeeds = @(),
        [string]$ResumeOutputPath
    )

    $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..\..'))
    $projectPath = Join-Path $repoRoot 'src\Grid.Diagnostics\Grid.Diagnostics.csproj'
    $sourceExecutablePath = Join-Path $repoRoot 'src\Grid.Diagnostics\bin\x64\Debug\net9.0-windows10.0.19041.0\Grid.Diagnostics.exe'
    $bundledExecutablePath = Join-Path $repoRoot 'bin\Grid.Diagnostics.exe'
    $executablePath = if (Test-Path -LiteralPath $bundledExecutablePath -PathType Leaf) {
        [IO.Path]::GetFullPath($bundledExecutablePath)
    } else {
        [IO.Path]::GetFullPath($sourceExecutablePath)
    }
    $buildPath = Join-Path $TransactionDirectory 'grid-diagnostics-build.txt'

    $usingBundledCollector = Test-Path -LiteralPath $bundledExecutablePath -PathType Leaf
    if (-not $usingBundledCollector -and -not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        return [pscustomobject][ordered]@{
            Status = 'Failed'; ExitCode = $null; OutputPath = $null; ErrorPath = $null; BuildPath = $null; Summary = $null; ExecutablePath = $null
            PrimaryFailure = [pscustomobject][ordered]@{ code = 'CollectorUnavailable'; detail = 'Neither the bundled Grid.Diagnostics executable nor its source project is available; no external read was attempted.' }
        }
    }
    if (-not $usingBundledCollector) {
        $collectorSourceRoots = @(
            (Join-Path $repoRoot 'src\Grid.Diagnostics'),
            (Join-Path $repoRoot 'src\Grid.Mo2'),
            (Join-Path $repoRoot 'src\Grid.Core')
        )
        $latestCollectorSourceWrite = @(
            $collectorSourceRoots | ForEach-Object {
                Get-ChildItem -LiteralPath $_ -Recurse -File -ErrorAction Stop | Where-Object {
                    $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]' -and $_.Extension -in @('.cs', '.csproj', '.json')
                }
            }
            Get-Item -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -ErrorAction Stop
        ) | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        $collectorExecutable = if (Test-Path -LiteralPath $executablePath -PathType Leaf) { Get-Item -LiteralPath $executablePath -ErrorAction Stop } else { $null }
        $collectorBuildRequired = $null -eq $collectorExecutable -or $latestCollectorSourceWrite.LastWriteTimeUtc -gt $collectorExecutable.LastWriteTimeUtc
        if ($collectorBuildRequired) {
            $buildOutput = @(& dotnet build $projectPath -c Debug -p:Platform=x64 --nologo 2>&1 | ForEach-Object { [string]$_ })
            [IO.File]::WriteAllLines($buildPath, $buildOutput, [Text.UTF8Encoding]::new($false))
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
                return [pscustomobject][ordered]@{
                    Status = 'Failed'; ExitCode = $LASTEXITCODE; OutputPath = $null; ErrorPath = $buildPath; BuildPath = $buildPath; Summary = $null; ExecutablePath = $null
                    PrimaryFailure = [pscustomobject][ordered]@{ code = 'CollectorUnavailable'; detail = 'Grid.Diagnostics could not be built; no external read was attempted.' }
                }
            }
        }
    }

    $maximumEntries = [long]$ResourceBudget.limits.maximumFilesObserved
    $maximumRecordCatalogEntries = [long]$ResourceBudget.limits.maximumRecordCatalogEntries
    $maximumHashBytes = [long]$ResourceBudget.limits.maximumBytesHashed
    $maximumFileBytes = 0
    $checkpointInterval = [long]$ResourceBudget.checkpointPolicy.intervalFiles
    $authorizations = @($ApplicationPath, $InstancePath)
    $attempts = @()
    $summary = $null
    $process = $null
    $outputPath = $null
    $errorPath = $null
    $maximumAuthorizationPasses = 8
    for ($attempt = 1; $attempt -le $maximumAuthorizationPasses; $attempt++) {
        $outputPath = Join-Path $TransactionDirectory ("mo2-baseline-attempt-$attempt.ndjson")
        $errorPath = Join-Path $TransactionDirectory ("mo2-baseline-attempt-$attempt.stderr.txt")
        $nativeArguments = @(
            'mo2-baseline', '--application', $ApplicationPath, '--instance', $InstancePath,
            '--profile', $ProfileName, '--format', 'ndjson',
            '--max-entries', ([string]$maximumEntries),
            '--max-record-catalog-entries', ([string]$maximumRecordCatalogEntries),
            '--max-total-hash-bytes', ([string]$maximumHashBytes),
            '--max-file-bytes', ([string]$maximumFileBytes),
            '--checkpoint-interval', ([string]$checkpointInterval)
        )
        foreach ($authorization in @($authorizations | Select-Object -Unique)) { $nativeArguments += @('--authorize', [string]$authorization) }
        foreach ($providerName in @($ExplicitProviderSeeds | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)) {
            $nativeArguments += @('--provider', [string]$providerName)
        }
        foreach ($pluginName in @($ExplicitPluginSeeds | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)) {
            $nativeArguments += @('--plugin', [string]$pluginName)
        }
        if (-not [string]::IsNullOrWhiteSpace($ResumeOutputPath)) { $nativeArguments += @('--resume', $ResumeOutputPath) }
        $argumentLine = (@($nativeArguments | ForEach-Object { ConvertTo-GridNativeArgument -Value ([string]$_) }) -join ' ')
        try {
            $process = Start-Process -FilePath $executablePath -ArgumentList $argumentLine -NoNewWindow -PassThru `
                -RedirectStandardOutput $outputPath -RedirectStandardError $errorPath -ErrorAction Stop
            $timeoutMilliseconds = [int64]$ResourceBudget.limits.maximumWallClockSeconds * 1000L
            if (-not $process.WaitForExit([int][Math]::Min($timeoutMilliseconds, [int]::MaxValue))) {
                $process.Kill()
                $process.WaitForExit()
                throw 'The repository-owned collector exceeded its recorded wall-clock budget.'
            }
        }
        catch {
            $attempts += [pscustomobject][ordered]@{ sequence = $attempt; outputPath = $outputPath; errorPath = $errorPath; exitCode = if ($process -and $process.HasExited) { [int]$process.ExitCode } else { $null }; summary = $null }
            return [pscustomobject][ordered]@{
                Status = 'Failed'; ExitCode = $null; OutputPath = $outputPath; ErrorPath = $errorPath; BuildPath = $null; Summary = $null; Attempts = @($attempts); ExecutablePath = $executablePath
                PrimaryFailure = [pscustomobject][ordered]@{ code = 'CollectorUnavailable'; detail = $_.Exception.Message }
            }
        }
        $summary = $null
        if (Test-Path -LiteralPath $outputPath -PathType Leaf) {
            $last = Get-Content -LiteralPath $outputPath -Tail 1 -ErrorAction SilentlyContinue
            if ($last) {
                try {
                    $envelope = [string]$last | ConvertFrom-Json -ErrorAction Stop
                    if ([string]$envelope.recordType -eq 'summary') { $summary = $envelope.payload }
                } catch { }
            }
        }
        $attempts += [pscustomobject][ordered]@{ sequence = $attempt; outputPath = $outputPath; errorPath = $errorPath; exitCode = [int]$process.ExitCode; summary = $summary }
        if ($summary -and [string]$summary.status -ieq 'AuthorizationRequired' -and @($summary.requiredAuthorizations).Count -gt 0) {
            $newAuthorizations = @($summary.requiredAuthorizations | ForEach-Object { [IO.Path]::GetFullPath([string]$_) } | Where-Object { $_ -notin $authorizations })
            if ($newAuthorizations.Count -gt 0 -and $attempt -lt $maximumAuthorizationPasses) {
                $authorizations += $newAuthorizations
                continue
            }
        }
        break
    }
    $nativeStatus = if ($summary) { [string]$summary.status } else { '' }
    $mappedStatus = switch ($nativeStatus.ToLowerInvariant()) {
        'completed' { 'Completed' }
        'partial' { 'Completed' }
        'authorizationrequired' { 'PausedAtCheckpoint' }
        'canceled' { 'Cancelled' }
        default { 'Failed' }
    }
    $failure = $null
    if ($mappedStatus -eq 'Failed') {
        $failureCode = if ($nativeStatus -eq 'ContextUnavailable') { 'InstallationContextUnresolved' } else { 'CollectorFailed' }
        $failure = [pscustomobject][ordered]@{ code = $failureCode; detail = "Grid.Diagnostics ended with status '$nativeStatus' and exit code $($process.ExitCode)." }
    }
    [pscustomobject][ordered]@{
        Status = $mappedStatus
        ExitCode = [int]$process.ExitCode
        OutputPath = $outputPath
        ErrorPath = $errorPath
        BuildPath = if (Test-Path -LiteralPath $buildPath -PathType Leaf) { $buildPath } else { $null }
        Summary = $summary
        Attempts = @($attempts)
        Authorizations = @($authorizations | Select-Object -Unique)
        ExecutablePath = $executablePath
        PrimaryFailure = $failure
    }
}

function Get-GridBaselineFileObservation {
    param([Parameter(Mandatory)][string]$LiteralPath, [string]$ExpectedSha256)
    $observedAt = [DateTimeOffset]::UtcNow.ToString('o')
    try {
        $item = Get-Item -LiteralPath $LiteralPath -Force -ErrorAction Stop
        if ($item.PSIsContainer) { throw 'NotAFile' }
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'ReparsePointRefused' }
        $beforeLength = [long]$item.Length
        $beforeWrite = $item.LastWriteTimeUtc.ToString('o')
        $hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256 -ErrorAction Stop).Hash.ToUpperInvariant()
        $after = Get-Item -LiteralPath $item.FullName -Force -ErrorAction Stop
        $stable = $beforeLength -eq [long]$after.Length -and $beforeWrite -eq $after.LastWriteTimeUtc.ToString('o')
        [pscustomobject][ordered]@{
            path = $item.FullName; readability = 'Readable'; sizeBytes = $beforeLength; lastWriteTimeUtc = $beforeWrite
            sha256 = $hash; hashStatus = if ($stable) { 'Hashed' } else { 'ChangedDuringRead' }
            expectedSha256 = if ($ExpectedSha256) { $ExpectedSha256.ToUpperInvariant() } else { $null }
            matchesExpected = if ($ExpectedSha256) { $stable -and $hash -eq $ExpectedSha256.ToUpperInvariant() } else { $null }
            observedAt = $observedAt; error = $null
        }
    }
    catch {
        [pscustomobject][ordered]@{
            path = [IO.Path]::GetFullPath($LiteralPath); readability = if ($_.Exception.Message -eq 'NotAFile') { 'NotAFile' } elseif ($_.Exception.Message -eq 'ReparsePointRefused') { 'ReparsePointRefused' } elseif (-not (Test-Path -LiteralPath $LiteralPath)) { 'Missing' } else { 'IoError' }
            sizeBytes = $null; lastWriteTimeUtc = $null; sha256 = $null; hashStatus = 'Failed'
            expectedSha256 = if ($ExpectedSha256) { $ExpectedSha256.ToUpperInvariant() } else { $null }; matchesExpected = $false
            observedAt = $observedAt; error = $_.Exception.Message
        }
    }
}

function Resolve-GridBaselineResumeArtifact {
    param(
        [Parameter(Mandatory)][string]$CaseStoreRoot,
        [Parameter(Mandatory)][string]$PredecessorCaseId,
        [Parameter(Mandatory)]$InvestigationPlan,
        [Parameter(Mandatory)][string]$InstallationId,
        [Parameter(Mandatory)][string]$ProfileId
    )
    if ([IO.Path]::GetFileName($PredecessorCaseId) -cne $PredecessorCaseId) { throw 'ResumeInvalid: predecessor case ID is not a safe exact segment.' }
    $caseDirectory = Join-Path (Join-Path $CaseStoreRoot 'cases\v1') $PredecessorCaseId
    $seal = Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory $caseDirectory
    if (-not $seal.IsValid) { throw ('ResumeInvalid: predecessor case seal failed: ' + ($seal.Errors -join '; ')) }
    $predecessorPlan = Get-Content -Raw -LiteralPath (Join-Path $caseDirectory 'investigation-plan.json') | ConvertFrom-Json -ErrorAction Stop
    foreach ($field in @('installationId','profileId','originalRequest')) {
        if ([string]$predecessorPlan.$field -cne [string]$InvestigationPlan.$field) { throw "ResumeInvalid: predecessor $field does not match." }
    }
    foreach ($field in @('candidatePlugins','providerSeeds','requiredGates')) {
        if ((@($predecessorPlan.$field) | ConvertTo-Json -Depth 20 -Compress) -cne (@($InvestigationPlan.$field) | ConvertTo-Json -Depth 20 -Compress)) {
            throw "ResumeInvalid: predecessor $field binding does not match."
        }
    }
    foreach ($priorCapability in @($predecessorPlan.capabilities)) {
        $current = @($InvestigationPlan.capabilities | Where-Object {
            [string]$_.capabilityId -ceq [string]$priorCapability.capabilityId -and
            [string]$_.capabilityVersion -ceq [string]$priorCapability.capabilityVersion
        })
        if ($current.Count -ne 1) {
            throw "ResumeInvalid: predecessor capability binding '$($priorCapability.capabilityId)' is absent or version-changed."
        }
    }
    $protectedPath = Join-Path $caseDirectory 'snapshots\protected-after.v1.json'
    if (-not (Test-Path -LiteralPath $protectedPath -PathType Leaf)) { throw 'ResumeInvalid: predecessor protected-after snapshot is missing.' }
    $protected = Get-Content -Raw -LiteralPath $protectedPath | ConvertFrom-Json -ErrorAction Stop
    foreach ($prior in @($protected.files)) {
        $current = Get-GridBaselineFileObservation -LiteralPath ([string]$prior.path) -ExpectedSha256 ([string]$prior.sha256)
        if ($prior.readability -eq 'Missing' -and $current.readability -eq 'Missing') { continue }
        if ($current.matchesExpected -ne $true -or $current.hashStatus -ne 'Hashed') { throw "BaselineStale: protected predecessor input changed: $($prior.path)" }
    }
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $caseDirectory 'case-manifest.v1.json') | ConvertFrom-Json -ErrorAction Stop
    $resumableRuns = @($manifest.runs | Where-Object { [string]$_.state -eq 'PausedAtCheckpoint' })
    if ($resumableRuns.Count -ne 1) { throw 'ResumeInvalid: predecessor has no unique PausedAtCheckpoint run and is not resumable.' }
    $raw = @($manifest.artifacts | Where-Object { [string]$_.path -like 'runs/*/raw/mo2-baseline-attempt-*.ndjson' } | Sort-Object path | Select-Object -Last 1)
    if ($raw.Count -ne 1) { throw 'ResumeInvalid: predecessor has no unique retained collector NDJSON partition.' }
    $rawPath = Join-Path $caseDirectory ([string]$raw[0].path).Replace('/', '\')
    if ((Get-FileHash -LiteralPath $rawPath -Algorithm SHA256).Hash -cne [string]$raw[0].sha256) { throw 'ResumeInvalid: predecessor NDJSON digest mismatch.' }
    $rawPath
}

function Resolve-GridBaselineHashCacheArtifact {
    param(
        [Parameter(Mandatory)][string]$CaseStoreRoot,
        [Parameter(Mandatory)][string]$HashCacheCaseId,
        [Parameter(Mandatory)]$InvestigationPlan,
        [Parameter(Mandatory)]$ResolvedContext,
        [Parameter(Mandatory)][string]$InstallationId,
        [Parameter(Mandatory)][string]$ProfileId
    )
    if ([IO.Path]::GetFileName($HashCacheCaseId) -cne $HashCacheCaseId) { throw 'HashCacheInvalid: source case ID is not a safe exact segment.' }
    $caseDirectory = Join-Path (Join-Path $CaseStoreRoot 'cases\v1') $HashCacheCaseId
    $seal = Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory $caseDirectory
    if (-not $seal.IsValid) { throw ('HashCacheInvalid: source case seal failed: ' + ($seal.Errors -join '; ')) }
    $priorPlan = Get-Content -Raw -LiteralPath (Join-Path $caseDirectory 'investigation-plan.json') | ConvertFrom-Json -ErrorAction Stop
    if ([string]$priorPlan.installationId -cne $InstallationId -or [string]$priorPlan.profileId -cne $ProfileId -or
        [string]$InvestigationPlan.installationId -cne $InstallationId -or [string]$InvestigationPlan.profileId -cne $ProfileId) {
        throw 'HashCacheInvalid: source case installation/profile binding does not match.'
    }
    $priorBinding = @($priorPlan.capabilities | Where-Object { [string]$_.capabilityId -ceq 'grid.game.skyrimspecialedition.baseline.collect' })
    $currentBinding = @($InvestigationPlan.capabilities | Where-Object { [string]$_.capabilityId -ceq 'grid.game.skyrimspecialedition.baseline.collect' })
    # Diagnostic reducers and additional read-only inventory records do not
    # invalidate completed per-file hashes. Reuse remains bound to the exact
    # source case, protected inputs, raw artifact digest, and file stamps.
    $cacheCompatibleVersions = @('1.2.0', '1.3.0', '1.4.0', '1.5.0', '1.6.0', '1.7.0', '1.8.0', '1.9.0', '2.0.0', '2.1.0', '2.2.0', '2.3.0', '2.4.0', '2.5.0', '2.6.0')
    if ($priorBinding.Count -ne 1 -or $currentBinding.Count -ne 1 -or
        [string]$currentBinding[0].capabilityVersion -cne '2.6.0' -or
        [string]$priorBinding[0].capabilityVersion -notin $cacheCompatibleVersions) {
        throw 'HashCacheInvalid: baseline hash-record capability binding is absent or incompatible.'
    }
    $protectedPath = Join-Path $caseDirectory 'snapshots\protected-after.v1.json'
    if (-not (Test-Path -LiteralPath $protectedPath -PathType Leaf)) { throw 'HashCacheInvalid: source protected-after snapshot is missing.' }
    $protected = Get-Content -Raw -LiteralPath $protectedPath | ConvertFrom-Json -ErrorAction Stop
    $configurationPath = [IO.Path]::GetFullPath([string]$ResolvedContext.configurationPath)
    $profilePath = [IO.Path]::GetFullPath([string]$ResolvedContext.profilePath).TrimEnd('\')
    $refreshableProfilePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($leaf in @('plugins.txt','loadorder.txt','modlist.txt')) {
        [void]$refreshableProfilePaths.Add([IO.Path]::GetFullPath((Join-Path $profilePath $leaf)))
    }
    $configurationDriftAccepted = $false
    $profileStateDriftAccepted = $false
    $acceptedReadOnlyDriftPaths = New-Object Collections.Generic.List[string]
    foreach ($prior in @($protected.files)) {
        $current = Get-GridBaselineFileObservation -LiteralPath ([string]$prior.path) -ExpectedSha256 ([string]$prior.sha256)
        if ($prior.readability -eq 'Missing' -and $current.readability -eq 'Missing') { continue }
        if ($current.matchesExpected -ne $true -or $current.hashStatus -ne 'Hashed') {
            $priorPath = [IO.Path]::GetFullPath([string]$prior.path)
            # MO2 rewrites ModOrganizer.ini and may normalize its three canonical
            # profile-order documents during the NXM download flow that this
            # refresh asks the user to perform. The successor collector always
            # reparses those current documents and rebuilds the complete profile,
            # plugin, and load-order evidence. The predecessor artifact contributes
            # only per-file content hashes, each accepted only for an identical
            # current canonical path, length, and timestamp. Provider metadata,
            # Grid connection inputs, and every other protected file remain exact.
            if ($priorPath.Equals($configurationPath, [StringComparison]::OrdinalIgnoreCase) -and $current.hashStatus -eq 'Hashed') {
                $configurationDriftAccepted = $true
                $acceptedReadOnlyDriftPaths.Add($priorPath)
                continue
            }
            if ($refreshableProfilePaths.Contains($priorPath) -and $current.hashStatus -eq 'Hashed') {
                $profileStateDriftAccepted = $true
                $acceptedReadOnlyDriftPaths.Add($priorPath)
                continue
            }
            throw "BaselineStale: protected hash-cache input changed: $($prior.path)"
        }
    }
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $caseDirectory 'case-manifest.v1.json') | ConvertFrom-Json -ErrorAction Stop
    $raw = @($manifest.artifacts | Where-Object { [string]$_.path -like 'runs/*/raw/mo2-baseline-attempt-*.ndjson' } | Sort-Object path | Select-Object -Last 1)
    if ($raw.Count -ne 1) { throw 'HashCacheInvalid: source case has no unique final collector NDJSON partition.' }
    $rawPath = Join-Path $caseDirectory ([string]$raw[0].path).Replace('/', '\')
    if ((Get-FileHash -LiteralPath $rawPath -Algorithm SHA256).Hash -cne [string]$raw[0].sha256) { throw 'HashCacheInvalid: source NDJSON digest mismatch.' }
    $last = Get-Content -LiteralPath $rawPath -Tail 1 -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    if ([string]$last.recordType -cne 'summary' -or [string]$last.payload.status -notin @('completed','partial') -or
        [string]$last.payload.installationId -cne $InstallationId -or [string]$last.payload.profileId -cne $ProfileId -or
        [long]$last.payload.completeHashes -le 0) {
        throw 'HashCacheInvalid: final collector partition has no reusable completed-hash summary.'
    }
    [pscustomobject][ordered]@{
        Path = $rawPath
        CaseId = $HashCacheCaseId
        ManifestSha256 = [string]$manifest.manifestSha256
        ArtifactSha256 = [string]$raw[0].sha256
        CompleteHashes = [long]$last.payload.completeHashes
        ConfigurationDriftAccepted = $configurationDriftAccepted
        ProfileStateDriftAccepted = $profileStateDriftAccepted
        AcceptedReadOnlyDriftPaths = $acceptedReadOnlyDriftPaths.ToArray()
    }
}

function Resolve-GridBaselineFinalizationArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseStoreRoot,
        [Parameter(Mandatory)][string]$FinalizationCaseId,
        [Parameter(Mandatory)]$InvestigationPlan,
        [Parameter(Mandatory)]$ResolvedContext,
        [Parameter(Mandatory)][string]$InstallationId,
        [Parameter(Mandatory)][string]$ProfileId
    )

    if ([IO.Path]::GetFileName($FinalizationCaseId) -cne $FinalizationCaseId) {
        throw 'FinalizationInvalid: source case ID is not a safe exact segment.'
    }
    $caseDirectory = Join-Path (Join-Path $CaseStoreRoot 'cases\v1') $FinalizationCaseId
    $seal = Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory $caseDirectory
    if (-not $seal.IsValid) { throw ('FinalizationInvalid: source case seal failed: ' + ($seal.Errors -join '; ')) }
    $manifest = $seal.Manifest
    $sourcePlan = Get-Content -Raw -LiteralPath (Join-Path $caseDirectory 'investigation-plan.json') | ConvertFrom-Json -ErrorAction Stop
    foreach ($field in @('gameId','installationId','profileId','originalRequest','purpose','status')) {
        if ([string]$sourcePlan.$field -cne [string]$InvestigationPlan.$field) {
            throw "FinalizationInvalid: source $field does not match."
        }
    }
    foreach ($field in @(
        'requiredGates','capabilities','normalizedSymptoms','locations','observedForms','candidatePlugins',
        'providerSeeds','hypotheses','evidenceReferences','parentCaseIds','evidenceIds','collectorQueries',
        'missingInputs','recommendedCollectors'
    )) {
        if ((@($sourcePlan.$field) | ConvertTo-Json -Depth 30 -Compress) -cne (@($InvestigationPlan.$field) | ConvertTo-Json -Depth 30 -Compress)) {
            throw "FinalizationInvalid: source $field binding does not match."
        }
    }

    $resolvedEntries = @($manifest.artifacts | Where-Object { [string]$_.path -like 'runs/*/resolved-roots.v1.json' })
    if ($resolvedEntries.Count -ne 1) { throw 'FinalizationInvalid: source case has no unique resolved-context artifact.' }
    $sourceResolvedPath = Join-Path $caseDirectory ([string]$resolvedEntries[0].path).Replace('/', '\')
    $sourceResolved = Get-Content -Raw -LiteralPath $sourceResolvedPath | ConvertFrom-Json -ErrorAction Stop
    foreach ($field in @(
        'installationId','profileId','profileName','profilePath','applicationPath','instancePath',
        'referenceStorePath','referenceStoreSha256','configurationPath','configurationSha256'
    )) {
        if ([string]$sourceResolved.$field -cne [string]$ResolvedContext.$field) {
            throw "FinalizationInvalid: source resolved context field '$field' does not match."
        }
    }
    if ([string]$sourceResolved.installationId -cne $InstallationId -or [string]$sourceResolved.profileId -cne $ProfileId) {
        throw 'FinalizationInvalid: source case installation/profile binding does not match.'
    }

    $failedRuns = @($manifest.runs | Where-Object { [string]$_.state -eq 'Failed' })
    if ($failedRuns.Count -ne 1) { throw 'FinalizationInvalid: source case has no unique failed run.' }
    $runPath = Join-Path $caseDirectory ([string]$failedRuns[0].path).Replace('/', '\')
    $failedRun = Get-Content -Raw -LiteralPath $runPath | ConvertFrom-Json -ErrorAction Stop
    if ($null -eq $failedRun.primaryFailure) { throw 'FinalizationInvalid: source failed run has no recorded primary failure.' }

    $auditPath = Join-Path $caseDirectory 'audit\non-mutation.v1.json'
    if (-not (Test-Path -LiteralPath $auditPath -PathType Leaf)) { throw 'FinalizationInvalid: source non-mutation audit is missing.' }
    $audit = Get-Content -Raw -LiteralPath $auditPath | ConvertFrom-Json -ErrorAction Stop
    if ([string]$audit.result -cne 'NoChangeObserved' -or @($audit.changed).Count -ne 0) {
        throw 'FinalizationInvalid: source collection did not establish an unchanged protected state.'
    }
    $protectedPath = Join-Path $caseDirectory 'snapshots\protected-after.v1.json'
    if (-not (Test-Path -LiteralPath $protectedPath -PathType Leaf)) { throw 'FinalizationInvalid: source protected-after snapshot is missing.' }
    $protected = Get-Content -Raw -LiteralPath $protectedPath | ConvertFrom-Json -ErrorAction Stop
    if ([string]$protected.installationId -cne $InstallationId -or [string]$protected.profileId -cne $ProfileId) {
        throw 'FinalizationInvalid: source protected state has the wrong installation/profile binding.'
    }
    foreach ($prior in @($protected.files)) {
        $current = Get-GridBaselineFileObservation -LiteralPath ([string]$prior.path) -ExpectedSha256 ([string]$prior.sha256)
        if ($prior.readability -eq 'Missing' -and $current.readability -eq 'Missing') { continue }
        if ($current.matchesExpected -ne $true -or $current.hashStatus -ne 'Hashed') {
            throw "BaselineStale: protected finalization input changed: $($prior.path)"
        }
    }

    $terminalPartitions = New-Object Collections.Generic.List[object]
    foreach ($entry in @($manifest.artifacts | Where-Object { [string]$_.path -like 'runs/*/raw/mo2-baseline-attempt-*.ndjson' } | Sort-Object path)) {
        $path = Join-Path $caseDirectory ([string]$entry.path).Replace('/', '\')
        $lastLine = Get-Content -LiteralPath $path -Tail 1 -ErrorAction Stop
        if ([string]::IsNullOrWhiteSpace([string]$lastLine)) { continue }
        try { $last = [string]$lastLine | ConvertFrom-Json -ErrorAction Stop } catch { continue }
        if ([string]$last.recordType -ceq 'summary' -and [string]$last.payload.status -in @('completed','partial')) {
            $terminalPartitions.Add([pscustomobject][ordered]@{ Entry = $entry; Path = $path; Summary = $last.payload })
        }
    }
    if ($terminalPartitions.Count -ne 1) {
        throw 'FinalizationInvalid: source case has no unique terminal completed or partial collector partition.'
    }
    $terminal = $terminalPartitions[0]
    $remainingAuthorizations = @($terminal.Summary.requiredAuthorizations | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    if ([string]$terminal.Summary.installationId -cne $InstallationId -or [string]$terminal.Summary.profileId -cne $ProfileId -or
        [long]$terminal.Summary.completeHashes -le 0 -or $remainingAuthorizations.Count -ne 0) {
        throw 'FinalizationInvalid: terminal collector summary is not reusable for this exact context.'
    }
    if ((Get-FileHash -LiteralPath $terminal.Path -Algorithm SHA256).Hash -cne [string]$terminal.Entry.sha256) {
        throw 'FinalizationInvalid: terminal collector partition digest mismatch.'
    }
    foreach ($relative in @(
        'provenance/source-archive-inspections.v1.json',
        'provenance/source-archive-inspections.v1.ndjson'
    )) {
        if (@($manifest.artifacts | Where-Object { [string]$_.path -ceq $relative }).Count -ne 1) {
            throw "FinalizationInvalid: source post-collection artifact '$relative' is missing."
        }
    }
    [pscustomobject][ordered]@{
        Path = [string]$terminal.Path
        Summary = $terminal.Summary
        CaseId = $FinalizationCaseId
        CaseDirectory = $caseDirectory
        Manifest = $manifest
        ManifestSha256 = [string]$manifest.manifestSha256
        ArtifactSha256 = [string]$terminal.Entry.sha256
        ArtifactPath = [string]$terminal.Entry.path
        FailedRunId = [string]$failedRun.runId
        FailedRunPrimaryFailure = $failedRun.primaryFailure
        ResolvedContextSha256 = [string]$resolvedEntries[0].sha256
    }
}

function Import-GridBaselineFinalizationArchiveInspection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Transaction,
        [Parameter(Mandatory)]$Finalization
    )
    $recordsRelative = 'provenance/source-archive-inspections.v1.ndjson'
    $summaryRelative = 'provenance/source-archive-inspections.v1.json'
    $assessmentsRelative = 'provenance/source-archive-candidate-assessments.v1.ndjson'
    $assessmentSummaryRelative = 'provenance/source-archive-candidate-assessments.v1.json'
    $recordsPath = Join-Path ([string]$Finalization.CaseDirectory) $recordsRelative.Replace('/', '\')
    $summaryPath = Join-Path ([string]$Finalization.CaseDirectory) $summaryRelative.Replace('/', '\')
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath $recordsRelative -SourceLiteralPath $recordsPath | Out-Null
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath $summaryRelative -SourceLiteralPath $summaryPath | Out-Null
    $assessmentsPath = Join-Path ([string]$Finalization.CaseDirectory) $assessmentsRelative.Replace('/', '\')
    $assessmentSummaryPath = Join-Path ([string]$Finalization.CaseDirectory) $assessmentSummaryRelative.Replace('/', '\')
    $records = New-Object Collections.Generic.List[object]
    Get-Content -LiteralPath $recordsPath -ReadCount 1 -ErrorAction Stop | ForEach-Object {
        if (-not [string]::IsNullOrWhiteSpace([string]$_)) { $records.Add(([string]$_ | ConvertFrom-Json -ErrorAction Stop)) }
    }
    if ($records.Count -gt 512) { throw 'FinalizationInvalid: source archive-inspection evidence exceeds its bounded record limit.' }
    $assessments = New-Object Collections.Generic.List[object]
    $temporaryPaths = New-Object Collections.Generic.List[string]
    $assessmentManifestCount = @($Finalization.Manifest.artifacts | Where-Object {
        [string]$_.path -in @($assessmentsRelative, $assessmentSummaryRelative)
    }).Count
    if ($assessmentManifestCount -notin @(0, 2)) {
        throw 'FinalizationInvalid: source archive-candidate assessment evidence is only partially sealed.'
    }
    $assessmentArtifactsPresent = $assessmentManifestCount -eq 2
    if ($assessmentArtifactsPresent) {
        if (-not (Test-Path -LiteralPath $assessmentsPath -PathType Leaf) -or -not (Test-Path -LiteralPath $assessmentSummaryPath -PathType Leaf)) {
            throw 'FinalizationInvalid: sealed source archive-candidate assessment evidence is unavailable.'
        }
        Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath $assessmentsRelative -SourceLiteralPath $assessmentsPath | Out-Null
        Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath $assessmentSummaryRelative -SourceLiteralPath $assessmentSummaryPath | Out-Null
        Get-Content -LiteralPath $assessmentsPath -ReadCount 1 -ErrorAction Stop | ForEach-Object {
            if (-not [string]::IsNullOrWhiteSpace([string]$_)) { $assessments.Add(([string]$_ | ConvertFrom-Json -ErrorAction Stop)) }
        }
    }
    else {
        $emptyAssessmentsPath = Join-Path $Transaction.TransactionDirectory ('legacy-empty-archive-candidate-assessments-' + [guid]::NewGuid().ToString('N') + '.ndjson')
        [IO.File]::WriteAllText($emptyAssessmentsPath, '', [Text.UTF8Encoding]::new($false))
        $temporaryPaths.Add($emptyAssessmentsPath)
        Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath $assessmentsRelative -SourceLiteralPath $emptyAssessmentsPath | Out-Null
        Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath $assessmentSummaryRelative -Value ([pscustomobject][ordered]@{
            schemaVersion = 1; status = 'NotPresentInLegacyFinalizationSource'; recordFormat = 'ndjson'
            recordPath = $assessmentsRelative; recordCount = 0; exactRestorationCount = 0; updateCandidateCount = 0
            note = 'The finalized source predates archive-candidate assessment evidence; no classification was inferred.'
        }) | Out-Null
    }
    if ($assessments.Count -gt 512) { throw 'FinalizationInvalid: source archive-candidate evidence exceeds its bounded record limit.' }
    [pscustomobject][ordered]@{
        Records = $records.ToArray(); Assessments = $assessments.ToArray(); TemporaryPaths = $temporaryPaths.ToArray()
        CommandCount = 0; AssessmentCommandCount = 0; FomodReconciliationCommandCount = 0; Reused = $true
    }
}

function Write-GridBaselineNormalizedArtifacts {
    param(
        [Parameter(Mandatory)]$Transaction,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)][string]$OutputPath,
        [Parameter(Mandatory)]$InvestigationPlan,
        [Parameter(Mandatory)]$ResolvedContext,
        $Summary,
        $FinalizationSource,
        [ValidateRange(1L, [long]::MaxValue)][long]$MaximumInputBytes = 17179869184L,
        [ValidateRange(1, 3600)][int]$MaximumWallClockSeconds = 1800
    )
    $inputFile = Get-Item -LiteralPath $OutputPath -ErrorAction Stop
    if ($inputFile.Length -gt $MaximumInputBytes) {
        throw "ResourceLimitExceeded: collector NDJSON is $($inputFile.Length) bytes; the normalization limit is $MaximumInputBytes bytes."
    }
    $staging = Join-Path $Transaction.TransactionDirectory ('normalized-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging -Force -ErrorAction Stop | Out-Null
    $map = [ordered]@{
        mod = 'inventory/mods.v1.ndjson'; plugin = 'inventory/plugins.v1.ndjson'
        pluginMaster = 'inventory/plugin-dependencies.v1.ndjson'; fileHash = 'inventory/files.v1.ndjson'
        pluginScriptDependency = 'inventory/plugin-script-dependencies.v1.ndjson'
        pluginAssetDependency = 'inventory/plugin-asset-dependencies.v1.ndjson'
        pluginRecordConflict = 'inventory/plugin-record-conflicts.v1.ndjson'
        pluginRecordProvenance = 'inventory/plugin-record-provenance.v1.ndjson'
        pluginRecordCatalog = 'inventory/plugin-record-catalog.v1.ndjson'
        spidSourceIssue = 'inventory/spid-source-issues.v1.ndjson'
        archive = 'inventory/archives.v1.ndjson'; virtualProvider = 'inventory/virtual-winners.v1.ndjson'
        sourceArchive = 'provenance/source-archives.v1.ndjson'; sourceArchiveSidecar = 'provenance/source-archive-sidecars.v1.ndjson'
    }
    $writers = @{}
    foreach ($key in $map.Keys) {
        $path = Join-Path $staging ($key + '.ndjson')
        $writers[$key] = [IO.StreamWriter]::new($path, $false, ([Text.UTF8Encoding]::new($false)), 1MB)
        $writers[$key].NewLine = "`n"
    }
    $roots = New-Object Collections.Generic.List[object]
    $profiles = New-Object Collections.Generic.List[object]
    $profileSources = New-Object Collections.Generic.List[object]
    $issues = New-Object Collections.Generic.List[object]
    $metadata = New-Object Collections.Generic.List[object]
    $checkpoints = New-Object Collections.Generic.List[object]
    $pluginNames = New-Object Collections.Generic.List[string]
    $pluginRecords = New-Object Collections.Generic.List[object]
    $pluginMasterRecords = New-Object Collections.Generic.List[object]
    $fileHashRecords = New-Object Collections.Generic.List[object]
    $pluginScriptDependencies = New-Object Collections.Generic.List[object]
    $pluginAssetDependencies = New-Object Collections.Generic.List[object]
    $spidSourceIssues = New-Object Collections.Generic.List[object]
    $sourceArchives = New-Object Collections.Generic.List[object]
    $sourceArchiveSidecars = New-Object Collections.Generic.List[object]
    $fileProviders = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $virtualProviderNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $diagnosticOutputs = New-Object Collections.Generic.List[object]
    $pluginScriptInspection = $null
    $pluginAssetInspection = $null
    $spidInspection = $null
    $counts = @{}
    foreach ($key in $map.Keys) { $counts[$key] = 0L }
    $objectTypes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($objectType in @(
        'resolvedRoot','profile','profileSource','gateIssue','pluginScriptInspection','pluginAssetInspection','spidInspection',
        'pluginScriptDependency','pluginAssetDependency','spidSourceIssue','sourceArchive','sourceArchiveSidecar','checkpoint',
        'plugin','pluginMaster','fileHash','mod'
    )) { [void]$objectTypes.Add($objectType) }
    $requestedProviderNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($providerSeed in @($InvestigationPlan.providerSeeds)) {
        $providerSeedName = [string](Get-GridBaselineOptionalProperty -InputObject $providerSeed -Name 'name')
        if (-not [string]::IsNullOrWhiteSpace($providerSeedName)) { [void]$requestedProviderNames.Add($providerSeedName) }
    }
    $recordTypeMarker = '"recordType":"'
    $payloadMarker = ',"payload":'
    $normalizationClock = [Diagnostics.Stopwatch]::StartNew()
    $recordsRead = 0L
    $reader = $null
    try {
        $reader = [IO.StreamReader]::new(
            $OutputPath,
            [Text.UTF8Encoding]::new($false, $true),
            $true,
            1MB)
        while (($line = $reader.ReadLine()) -ne $null) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $recordsRead++
            if (($recordsRead % 10000L) -eq 0L -and $normalizationClock.Elapsed.TotalSeconds -gt $MaximumWallClockSeconds) {
                throw "ResourceLimitExceeded: collector normalization exceeded $MaximumWallClockSeconds seconds after $recordsRead records."
            }
            $typeStart = $line.IndexOf($recordTypeMarker, [StringComparison]::Ordinal)
            if ($typeStart -lt 0 -or $line.Length -lt 2 -or $line[0] -ne '{' -or $line[$line.Length - 1] -ne '}') {
                throw "Malformed collector NDJSON at record ${recordsRead}: the deterministic envelope is invalid."
            }
            $typeStart += $recordTypeMarker.Length
            $typeEnd = $line.IndexOf('"', $typeStart)
            $payloadMarkerStart = $line.IndexOf($payloadMarker, $typeEnd, [StringComparison]::Ordinal)
            if ($typeEnd -le $typeStart -or $payloadMarkerStart -lt 0) {
                throw "Malformed collector NDJSON at record ${recordsRead}: recordType or payload is unavailable."
            }
            $type = $line.Substring($typeStart, $typeEnd - $typeStart)
            $payloadStart = $payloadMarkerStart + $payloadMarker.Length
            $payloadLength = $line.Length - $payloadStart - 1
            if ($payloadLength -le 0) {
                throw "Malformed collector NDJSON at record ${recordsRead}: payload is empty."
            }
            $payloadJson = $line.Substring($payloadStart, $payloadLength)
            if ($writers.ContainsKey($type)) {
                # Grid.Diagnostics already emitted canonical compact JSON. Preserve
                # the payload bytes instead of round-tripping millions of records
                # through Windows PowerShell's object serializer.
                $writers[$type].WriteLine($payloadJson)
                $counts[$type] = 1L + [long]$counts[$type]
            }
            if (-not $objectTypes.Contains($type) -and -not ($type -eq 'virtualProvider' -and $requestedProviderNames.Count -gt 0)) {
                continue
            }
            try { $payload = $payloadJson | ConvertFrom-Json -ErrorAction Stop } catch { throw "Malformed collector payload at record ${recordsRead}: $($_.Exception.Message)" }
            switch ($type) {
                'resolvedRoot' { $roots.Add($payload) }
                'profile' { $profiles.Add($payload) }
                'profileSource' { $profileSources.Add($payload) }
                'gateIssue' { $issues.Add($payload) }
                'pluginScriptInspection' { $pluginScriptInspection = $payload }
                'pluginAssetInspection' { $pluginAssetInspection = $payload }
                'spidInspection' { $spidInspection = $payload }
                'pluginScriptDependency' { $pluginScriptDependencies.Add($payload) }
                'pluginAssetDependency' { $pluginAssetDependencies.Add($payload) }
                'spidSourceIssue' { $spidSourceIssues.Add($payload) }
                'sourceArchive' { $sourceArchives.Add($payload) }
                'sourceArchiveSidecar' { $sourceArchiveSidecars.Add($payload) }
                'checkpoint' { $checkpoints.Add($payload) }
                'plugin' {
                    $plugin = Get-GridBaselineOptionalProperty -InputObject $payload -Name 'plugin'
                    $pluginName = Get-GridBaselineOptionalProperty -InputObject $plugin -Name 'name'
                    if ($pluginName) { $pluginNames.Add([string]$pluginName) }
                    if ($plugin) { $pluginRecords.Add($plugin) }
                }
                'pluginMaster' { $pluginMasterRecords.Add($payload) }
                'fileHash' {
                    # The complete file-hash inventory is already streamed to its
                    # sealed NDJSON artifact above.  Keep only plugin and paired
                    # BSA/BA2 payloads in
                    # memory: archive-candidate classification is the sole
                    # in-process consumer and does not need loose-file hashes.
                    # Retaining every file-hash object made large MO2 profiles grow
                    # the PowerShell wrapper well beyond its 512 MiB policy while
                    # performing a second, unnecessary in-memory inventory.
                    $fileHashVirtualPath = [string](Get-GridBaselineOptionalProperty -InputObject $payload -Name 'virtualPath')
                    if ($fileHashVirtualPath -match '(?i)(?:^|\\)[^\\]+\.(?:esp|esm|esl|bsa|ba2)$') {
                        $fileHashRecords.Add($payload)
                    }
                    $providerName = Get-GridBaselineOptionalProperty -InputObject $payload -Name 'providerName'
                    if ($providerName) { [void]$fileProviders.Add([string]$providerName) }
                    if ([string](Get-GridBaselineOptionalProperty -InputObject $payload -Name 'kind') -ieq 'diagnosticOutput') {
                        $hashStatus = [string](Get-GridBaselineOptionalProperty -InputObject $payload -Name 'status')
                        $canonicalPath = [string](Get-GridBaselineOptionalProperty -InputObject $payload -Name 'canonicalPath')
                        $expectedHash = [string](Get-GridBaselineOptionalProperty -InputObject $payload -Name 'sha256')
                        if ($hashStatus -ieq 'complete' -and -not [string]::IsNullOrWhiteSpace($canonicalPath) -and -not [string]::IsNullOrWhiteSpace($expectedHash)) {
                            if ($FinalizationSource) {
                                $sourceBlob = @($FinalizationSource.Manifest.blobs | Where-Object { [string]$_.sha256 -ieq $expectedHash })
                                if ($sourceBlob.Count -ne 1) { throw "FinalizationInvalid: sealed diagnostic blob '$expectedHash' is unavailable." }
                                $blob = [pscustomobject][ordered]@{ sha256 = ([string]$sourceBlob[0].sha256).ToUpperInvariant(); sizeBytes = [long]$sourceBlob[0].sizeBytes }
                                if (@($Transaction.Blobs | Where-Object { [string]$_.sha256 -eq [string]$blob.sha256 }).Count -eq 0) {
                                    $Transaction.Blobs = @($Transaction.Blobs) + @($blob)
                                }
                            }
                            else {
                                $blob = Add-GridCaseStoreBlob -Transaction $Transaction -LiteralPath $canonicalPath
                                if ([string]$blob.sha256 -ine $expectedHash) {
                                    throw "Diagnostic evidence changed after collection: $canonicalPath"
                                }
                            }
                            $payload | Add-Member -MemberType NoteProperty -Name blobSha256 -Value ([string]$blob.sha256) -Force
                        }
                        $diagnosticOutputs.Add($payload)
                    }
                }
                'virtualProvider' {
                    $provider = Get-GridBaselineOptionalProperty -InputObject $payload -Name 'provider'
                    $sourceName = Get-GridBaselineOptionalProperty -InputObject $provider -Name 'sourceName'
                    if ($sourceName -and $requestedProviderNames.Contains([string]$sourceName)) { [void]$virtualProviderNames.Add([string]$sourceName) }
                }
                'mod' {
                    $metadata.Add([pscustomobject][ordered]@{
                        modId = Get-GridBaselineOptionalProperty $payload 'modId'; name = Get-GridBaselineOptionalProperty $payload 'name'; enabled = Get-GridBaselineOptionalProperty $payload 'enabled'
                        path = Get-GridBaselineOptionalProperty $payload 'canonicalPath'; metadataSha256 = Get-GridBaselineOptionalProperty $payload 'metadataSha256'
                        metadataAvailability = Get-GridBaselineOptionalProperty $payload 'metadataAvailability'; version = Get-GridBaselineOptionalProperty $payload 'version'
                        newestVersion = Get-GridBaselineOptionalProperty $payload 'newestVersion'; ignoredVersion = Get-GridBaselineOptionalProperty $payload 'ignoredVersion'
                        categoryIds = @(Get-GridBaselineOptionalProperty $payload 'categoryIds'); categoryNames = @(Get-GridBaselineOptionalProperty $payload 'categoryNames')
                        nexusGameName = Get-GridBaselineOptionalProperty $payload 'nexusGameName'; nexusModId = Get-GridBaselineOptionalProperty $payload 'nexusModId'
                        installationFile = Get-GridBaselineOptionalProperty $payload 'installationFile'; notes = Get-GridBaselineOptionalProperty $payload 'notes'
                        comments = Get-GridBaselineOptionalProperty $payload 'comments'; repository = Get-GridBaselineOptionalProperty $payload 'repository'
                        providerUpdatedAtUtc = Get-GridBaselineOptionalProperty $payload 'providerUpdatedAtUtc'; providerStatus = Get-GridBaselineOptionalProperty $payload 'providerStatus'
                        rawValues = @(Get-GridBaselineOptionalProperty $payload 'rawValues'); warningCount = Get-GridBaselineOptionalProperty $payload 'warningCount'
                    })
                }
            }
        }
    }
    finally {
        if ($reader) { $reader.Dispose() }
        foreach ($writer in $writers.Values) { $writer.Dispose() }
    }
    foreach ($key in $map.Keys) {
        Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath $map[$key] -SourceLiteralPath (Join-Path $staging ($key + '.ndjson')) | Out-Null
    }
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath 'installation/installation-baseline.v1.json' -Value ([pscustomobject][ordered]@{
        schemaVersion = 1; installationId = $ResolvedContext.installationId; profileId = $ResolvedContext.profileId
        roots = $roots.ToArray(); profiles = $profiles.ToArray(); profileSources = $profileSources.ToArray(); summary = $Summary; issues = $issues.ToArray()
    }) | Out-Null
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath 'inventory/plugin-dependencies.v1.json' -Value ([pscustomobject][ordered]@{
        schemaVersion = 1; recordFormat = 'ndjson'; recordPath = 'inventory/plugin-dependencies.v1.ndjson'; recordCount = [long]$counts.pluginMaster
    }) | Out-Null
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath 'inventory/plugin-script-dependencies.v1.json' -Value ([pscustomobject][ordered]@{
        schemaVersion = 1
        status = if ($pluginScriptInspection) { [string](Get-GridBaselineOptionalProperty $pluginScriptInspection 'status') } else { 'Unavailable' }
        pluginsScanned = if ($pluginScriptInspection) { [long](Get-GridBaselineOptionalProperty $pluginScriptInspection 'pluginsScanned') } else { 0L }
        recordHeadersExamined = if ($pluginScriptInspection) { [long](Get-GridBaselineOptionalProperty $pluginScriptInspection 'recordHeadersExamined') } else { 0L }
        bytesScanned = if ($pluginScriptInspection) { [long](Get-GridBaselineOptionalProperty $pluginScriptInspection 'bytesScanned') } else { 0L }
        scriptReferences = if ($pluginScriptInspection) { [long](Get-GridBaselineOptionalProperty $pluginScriptInspection 'scriptReferences') } else { 0L }
        recordConflictGroups = if ($pluginScriptInspection) { [long](Get-GridBaselineOptionalProperty $pluginScriptInspection 'recordConflictGroups') } else { 0L }
        recordConflictRecords = if ($pluginScriptInspection) { [long](Get-GridBaselineOptionalProperty $pluginScriptInspection 'recordConflictRecords') } else { 0L }
        recordProvenanceGroups = if ($pluginScriptInspection) { [long](Get-GridBaselineOptionalProperty $pluginScriptInspection 'recordProvenanceGroups') } else { 0L }
        recordProvenanceRecords = if ($pluginScriptInspection) { [long](Get-GridBaselineOptionalProperty $pluginScriptInspection 'recordProvenanceRecords') } else { 0L }
        recordCatalogEntries = if ($pluginScriptInspection) { [long](Get-GridBaselineOptionalProperty $pluginScriptInspection 'recordCatalogEntries') } else { 0L }
        semanticFingerprint = if ($pluginScriptInspection) { [string](Get-GridBaselineOptionalProperty $pluginScriptInspection 'semanticFingerprint') } else { $null }
        recordFormat = 'ndjson'; recordPath = 'inventory/plugin-script-dependencies.v1.ndjson'; recordCount = [long]$counts.pluginScriptDependency
        note = 'Records are emitted only when the virtual Data observation is complete; each proves that an enabled plugin attaches a Papyrus script for which no PEX provider exists.'
    }) | Out-Null
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath 'inventory/plugin-record-conflicts.v1.json' -Value ([pscustomobject][ordered]@{
        schemaVersion = 1
        status = if ($pluginScriptInspection) { [string](Get-GridBaselineOptionalProperty $pluginScriptInspection 'status') } else { 'Unavailable' }
        semanticFingerprint = if ($pluginScriptInspection) { [string](Get-GridBaselineOptionalProperty $pluginScriptInspection 'semanticFingerprint') } else { $null }
        recordFormat = 'ndjson'; recordPath = 'inventory/plugin-record-conflicts.v1.ndjson'; recordCount = [long]$counts.pluginRecordConflict
        coveredSignatures = @('REFR','ACHR','NPC_','QUST','PACK','SCEN','DIAL','INFO','DOOR','FURN','SPEL','MGEF','ARMO','ALCH','INGR','COBJ','KYWD','OTFT','LVLI','FLST','FACT','RACE','CELL','WRLD','LIGH','STAT','MSTT','TXST','LTEX')
        note = 'Each row aggregates an exact load-order transition between two enabled plugins for one record signature. Sampled canonical FormIDs, record offsets, flags, and before/after data hashes identify bounded follow-up targets; a transition is a conflict candidate, not proof of a defect.'
    }) | Out-Null
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath 'inventory/plugin-record-provenance.v1.json' -Value ([pscustomobject][ordered]@{
        schemaVersion = 1
        status = if ($pluginScriptInspection) { [string](Get-GridBaselineOptionalProperty $pluginScriptInspection 'status') } else { 'Unavailable' }
        semanticFingerprint = if ($pluginScriptInspection) { [string](Get-GridBaselineOptionalProperty $pluginScriptInspection 'semanticFingerprint') } else { $null }
        recordFormat = 'ndjson'; recordPath = 'inventory/plugin-record-provenance.v1.ndjson'; recordCount = [long]$counts.pluginRecordProvenance
        coveredSignatures = @('REFR','ACHR','NPC_','QUST','PACK','SCEN','DIAL','INFO','DOOR','FURN','SPEL','MGEF','ARMO','ALCH','INGR','COBJ','KYWD','OTFT','LVLI','FLST','FACT','RACE','CELL','WRLD','LIGH','STAT','MSTT','TXST','LTEX')
        note = 'Each row inventories one enabled plugin and record signature, separating new records from overrides and retaining bounded canonical FormID, EditorID, flags, offset, and data-hash samples. Provenance identifies ownership and follow-up targets; it is not by itself a defect finding.'
    }) | Out-Null
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath 'inventory/plugin-record-catalog.v1.json' -Value ([pscustomobject][ordered]@{
        schemaVersion = 1
        status = if ($pluginScriptInspection) { [string](Get-GridBaselineOptionalProperty $pluginScriptInspection 'status') } else { 'Unavailable' }
        semanticFingerprint = if ($pluginScriptInspection) { [string](Get-GridBaselineOptionalProperty $pluginScriptInspection 'semanticFingerprint') } else { $null }
        recordFormat = 'ndjson'; recordPath = 'inventory/plugin-record-catalog.v1.ndjson'; recordCount = [long]$counts.pluginRecordCatalog
        coveredSignatures = @('REFR','ACHR','NPC_','QUST','PACK','SCEN','DIAL','INFO','DOOR','FURN','SPEL','MGEF','ARMO','ALCH','INGR','COBJ','KYWD','OTFT','LVLI','FLST','FACT','RACE','CELL','WRLD','LIGH','STAT','MSTT','TXST','LTEX')
        note = 'Each row identifies one retained diagnostic TES4 record by canonical FormID, defining or overriding plugin, established load order, EditorID, flags, exact offset, and data hash. Status Partial means the configured catalog bound or another observation gate prevented completeness; omitted records remain unknown.'
    }) | Out-Null
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath 'inventory/plugin-asset-dependencies.v1.json' -Value ([pscustomobject][ordered]@{
        schemaVersion = 1
        status = if ($pluginAssetInspection) { [string](Get-GridBaselineOptionalProperty $pluginAssetInspection 'status') } else { 'Unavailable' }
        directRecordStatus = if ($pluginAssetInspection -and (Get-GridBaselineOptionalProperty $pluginAssetInspection 'directRecordStatus')) { [string](Get-GridBaselineOptionalProperty $pluginAssetInspection 'directRecordStatus') } elseif ($pluginAssetInspection) { [string](Get-GridBaselineOptionalProperty $pluginAssetInspection 'status') } else { 'Unavailable' }
        nifTextureStatus = if ($pluginAssetInspection -and (Get-GridBaselineOptionalProperty $pluginAssetInspection 'nifTextureStatus')) { [string](Get-GridBaselineOptionalProperty $pluginAssetInspection 'nifTextureStatus') } elseif ($pluginAssetInspection) { [string](Get-GridBaselineOptionalProperty $pluginAssetInspection 'status') } else { 'Unavailable' }
        pluginsScanned = if ($pluginAssetInspection) { [long](Get-GridBaselineOptionalProperty $pluginAssetInspection 'pluginsScanned') } else { 0L }
        recordHeadersExamined = if ($pluginAssetInspection) { [long](Get-GridBaselineOptionalProperty $pluginAssetInspection 'recordHeadersExamined') } else { 0L }
        bytesScanned = if ($pluginAssetInspection) { [long](Get-GridBaselineOptionalProperty $pluginAssetInspection 'bytesScanned') } else { 0L }
        assetReferences = if ($pluginAssetInspection) { [long](Get-GridBaselineOptionalProperty $pluginAssetInspection 'assetReferences') } else { 0L }
        nifMeshesInspected = if ($pluginAssetInspection) { [long](Get-GridBaselineOptionalProperty $pluginAssetInspection 'nifMeshesInspected') } else { 0L }
        embeddedTextureReferences = if ($pluginAssetInspection) { [long](Get-GridBaselineOptionalProperty $pluginAssetInspection 'embeddedTextureReferences') } else { 0L }
        issueCount = if ($pluginAssetInspection) { [long](Get-GridBaselineOptionalProperty $pluginAssetInspection 'issueCount') } else { 0L }
        semanticFingerprint = if ($pluginAssetInspection) { [string](Get-GridBaselineOptionalProperty $pluginAssetInspection 'semanticFingerprint') } else { $null }
        recordFormat = 'ndjson'; recordPath = 'inventory/plugin-asset-dependencies.v1.ndjson'; recordCount = [long]$counts.pluginAssetDependency
        note = 'Records are emitted only when the virtual Data observation is complete; each independently proves that an effective winning plugin record directly declares a missing NIF/DDS path or reaches a missing DDS path through an established winning NIF provider. Partial component status limits completeness but does not invalidate emitted records.'
    }) | Out-Null
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath 'inventory/spid-distributions.v1.json' -Value ([pscustomobject][ordered]@{
        schemaVersion = 1
        status = if ($spidInspection) { [string](Get-GridBaselineOptionalProperty $spidInspection 'status') } else { 'Unavailable' }
        documentsScanned = if ($spidInspection) { [long](Get-GridBaselineOptionalProperty $spidInspection 'documentsScanned') } else { 0L }
        linesScanned = if ($spidInspection) { [long](Get-GridBaselineOptionalProperty $spidInspection 'linesScanned') } else { 0L }
        rulesScanned = if ($spidInspection) { [long](Get-GridBaselineOptionalProperty $spidInspection 'rulesScanned') } else { 0L }
        sourcePluginReferences = if ($spidInspection) { [long](Get-GridBaselineOptionalProperty $spidInspection 'sourcePluginReferences') } else { 0L }
        disabledSourceReferences = if ($spidInspection) { [long](Get-GridBaselineOptionalProperty $spidInspection 'disabledSourceReferences') } else { 0L }
        missingSourceReferences = if ($spidInspection) { [long](Get-GridBaselineOptionalProperty $spidInspection 'missingSourceReferences') } else { 0L }
        issueCount = if ($spidInspection) { [long](Get-GridBaselineOptionalProperty $spidInspection 'issueCount') } else { 0L }
        semanticFingerprint = if ($spidInspection) { [string](Get-GridBaselineOptionalProperty $spidInspection 'semanticFingerprint') } else { $null }
        recordFormat = 'ndjson'; recordPath = 'inventory/spid-source-issues.v1.ndjson'; recordCount = [long]$counts.spidSourceIssue
        note = 'Only the distributed source-form field is classified as blocking when its plugin is disabled or missing. Optional filter-field plugin references are not promoted to errors.'
    }) | Out-Null
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath 'provenance/mod-metadata.v1.json' -Value ([pscustomobject][ordered]@{
        schemaVersion = 1; records = @($metadata.ToArray() | Sort-Object name)
    }) | Out-Null
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath 'provenance/source-archives.v1.json' -Value ([pscustomobject][ordered]@{
        schemaVersion = 1; status = if (([long]$counts.sourceArchive + [long]$counts.sourceArchiveSidecar) -gt 0) { 'Collected' } else { 'NotCollectedWithoutExactRelevantLeaf' }
        archiveRecordFormat = 'ndjson'; archiveRecordPath = 'provenance/source-archives.v1.ndjson'; archiveRecordCount = [long]$counts.sourceArchive
        sidecarRecordFormat = 'ndjson'; sidecarRecordPath = 'provenance/source-archive-sidecars.v1.ndjson'; sidecarRecordCount = [long]$counts.sourceArchiveSidecar
        note = 'Only exact immediate installationFile leaves and sidecars declared by graph-linked provider metadata are inspected; the relationship remains a metadata claim until independently verified.'
    }) | Out-Null
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath 'provenance/remote-documents.v1.json' -Value ([pscustomobject][ordered]@{
        schemaVersion = 1; status = 'Unavailable'; records = @(); note = 'No locally evidenced canonical documentation URL was required or fetched.'
    }) | Out-Null
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath 'diagnostics/skse-crash-logs.v1.json' -Value ([pscustomobject][ordered]@{
        schemaVersion = 1; rootRole = 'GameOwnedDiagnosticOutput'; patterns = @('crash-*.log','skse*.log'); recursion = 'ImmediateChildrenOnly'
        maximumItems = 256; records = @($diagnosticOutputs.ToArray() | Sort-Object virtualPath)
        note = 'Hash-matched raw logs are retained as content-addressed blobs. Global Skyrim INIs and saves remain excluded when MO2 local settings are active.'
    }) | Out-Null
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath 'prior-evidence/index.v1.json' -Value ([pscustomobject][ordered]@{
        schemaVersion = 1; parentCaseIds = @($InvestigationPlan.parentCaseIds); evidenceIds = @($InvestigationPlan.evidenceIds)
        records = @(); status = if (@($InvestigationPlan.parentCaseIds).Count + @($InvestigationPlan.evidenceIds).Count -eq 0) { 'NotApplicable' } else { 'ReferencedEvidenceUnavailable' }
    }) | Out-Null
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath 'attachments/manifest.v1.json' -Value ([pscustomobject][ordered]@{
        schemaVersion = 1; records = @(); status = if (@($InvestigationPlan.evidenceReferences).Count -eq 0) { 'NotApplicable' } else { 'ReferencedEvidenceUnavailable' }
    }) | Out-Null
    [pscustomobject][ordered]@{
        Roots = $roots.ToArray(); Profiles = $profiles.ToArray(); ProfileSources = $profileSources.ToArray()
        Issues = $issues.ToArray(); Metadata = $metadata.ToArray(); Checkpoints = $checkpoints.ToArray(); Counts = $counts
        PluginNames = @($pluginNames.ToArray() | Sort-Object -Unique)
        PluginRecords = $pluginRecords.ToArray()
        PluginMasterRecords = $pluginMasterRecords.ToArray()
        FileHashRecords = $fileHashRecords.ToArray()
        PluginScriptInspection = $pluginScriptInspection
        PluginScriptDependencies = $pluginScriptDependencies.ToArray()
        PluginAssetInspection = $pluginAssetInspection
        PluginAssetDependencies = $pluginAssetDependencies.ToArray()
        SpidInspection = $spidInspection
        SpidSourceIssues = $spidSourceIssues.ToArray()
        SourceArchives = $sourceArchives.ToArray()
        SourceArchiveSidecars = $sourceArchiveSidecars.ToArray()
        DiagnosticOutputs = $diagnosticOutputs.ToArray()
        FileProviders = @($fileProviders | Sort-Object)
        VirtualProviderNames = @($virtualProviderNames | Sort-Object)
        StagingDirectory = $staging
    }
}

function Get-GridSkyrimWinningDllProviderMap {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Normalized)

    $cached = Get-GridBaselineOptionalProperty -InputObject $Normalized -Name 'WinningDllProviders'
    if ($null -ne $cached) { return $cached }

    $dllProviders = @{}
    $virtualProviderPath = Join-Path ([string]$Normalized.StagingDirectory) 'virtualProvider.ndjson'
    if (-not (Test-Path -LiteralPath $virtualProviderPath -PathType Leaf)) {
        $Normalized | Add-Member -MemberType NoteProperty -Name WinningDllProviders -Value $dllProviders -Force
        return $dllProviders
    }
    $virtualReader = $null
    try {
        $virtualReader = [IO.StreamReader]::new($virtualProviderPath, [Text.UTF8Encoding]::new($false, $true), $true, 1MB)
        while (($virtualLine = $virtualReader.ReadLine()) -ne $null) {
            # The virtual inventory may contain millions of rows. Only SKSE DLL/EXE
            # winners can contribute to this map, so reject every other row before
            # invoking Windows PowerShell's comparatively expensive JSON parser.
            if ($virtualLine.IndexOf('SKSE\\Plugins\\', [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
                ($virtualLine.IndexOf('.dll"', [StringComparison]::OrdinalIgnoreCase) -lt 0 -and
                 $virtualLine.IndexOf('.exe"', [StringComparison]::OrdinalIgnoreCase) -lt 0)) { continue }
            $record = $virtualLine | ConvertFrom-Json -ErrorAction Stop
            $provider = Get-GridBaselineOptionalProperty $record 'provider'
            $virtualPath = [string](Get-GridBaselineOptionalProperty $record 'virtualPath')
            if ($provider -and (Get-GridBaselineOptionalProperty $provider 'isWinner') -eq $true -and
                $virtualPath -match '^(?i)SKSE\\Plugins\\([^\\]+\.(?:dll|exe))$') {
                $dllProviders[$matches[1].ToUpperInvariant()] = [pscustomobject][ordered]@{
                    virtualPath = $virtualPath
                    providerName = [string](Get-GridBaselineOptionalProperty $provider 'sourceName')
                    providerId = [string](Get-GridBaselineOptionalProperty (Get-GridBaselineOptionalProperty $provider 'id') 'value')
                    modId = [string](Get-GridBaselineOptionalProperty (Get-GridBaselineOptionalProperty $provider 'modId') 'value')
                    providerFingerprint = [string](Get-GridBaselineOptionalProperty $provider 'fingerprint')
                }
            }
        }
    }
    finally {
        if ($virtualReader) { $virtualReader.Dispose() }
    }
    $Normalized | Add-Member -MemberType NoteProperty -Name WinningDllProviders -Value $dllProviders -Force
    $dllProviders
}

function New-GridSkyrimCrashSignatureSummary {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Transaction,
        [Parameter(Mandatory)]$Normalized
    )

    $dllProviders = Get-GridSkyrimWinningDllProviderMap -Normalized $Normalized

    $records = New-Object Collections.Generic.List[object]
    foreach ($diagnostic in @($Normalized.DiagnosticOutputs | Sort-Object lastWriteTimeUtcTicks, virtualPath)) {
        $blobSha256 = ([string](Get-GridBaselineOptionalProperty $diagnostic 'blobSha256')).ToUpperInvariant()
        if ($blobSha256 -notmatch '^[A-F0-9]{64}$') { continue }
        $blobPath = Join-Path (Join-Path (Join-Path $Transaction.StoreRoot 'blobs\sha256') $blobSha256.Substring(0,2)) $blobSha256
        if (-not (Test-Path -LiteralPath $blobPath -PathType Leaf)) { continue }
        $item = Get-Item -LiteralPath $blobPath -Force -ErrorAction Stop
        if ([long]$item.Length -gt 8MB -or (Get-FileHash -LiteralPath $blobPath -Algorithm SHA256).Hash -cne $blobSha256) { continue }
        $gameVersion = $null; $loggerVersion = $null; $exceptionType = $null; $exceptionModule = $null
        $probableModules = New-Object Collections.Generic.List[string]
        $reader = New-Object IO.StreamReader($blobPath, [Text.UTF8Encoding]::new($false, $true), $true, 65536)
        try {
            $lineNumber = 0; $inProbableStack = $false
            while (($line = $reader.ReadLine()) -ne $null -and $lineNumber -lt 2000) {
                $lineNumber++
                if ($lineNumber -eq 1 -and $line -match '^Skyrim SSE\s+(.+)$') { $gameVersion = $matches[1].Trim() }
                elseif ($lineNumber -le 6 -and $line -match '^CrashLoggerSSE\s+(.+)$') { $loggerVersion = $matches[1].Trim() }
                if (-not $exceptionType -and $line -match '^Unhandled exception\s+"([^"]+)".*?\s([^\s+]+\.(?:dll|exe))\+[0-9A-Fa-f]+') {
                    $exceptionType = $matches[1]; $exceptionModule = $matches[2]
                }
                if ($line -ceq 'PROBABLE CALL STACK:') { $inProbableStack = $true; continue }
                if ($inProbableStack) {
                    if ([string]::IsNullOrWhiteSpace($line) -or $line -ceq 'REGISTERS:') { $inProbableStack = $false; continue }
                    if ($line -match '^\s*\[\s*\d+\].*?\s([^\s+]+\.(?:dll|exe))\+[0-9A-Fa-f]+') {
                        if ($probableModules.Count -lt 32) { $probableModules.Add($matches[1]) }
                    }
                }
            }
        } finally { $reader.Dispose() }
        if ([string]::IsNullOrWhiteSpace($exceptionType) -or [string]::IsNullOrWhiteSpace($exceptionModule)) { continue }
        $provider = if ($dllProviders.ContainsKey($exceptionModule.ToUpperInvariant())) { $dllProviders[$exceptionModule.ToUpperInvariant()] } else { $null }
        $records.Add([pscustomobject][ordered]@{
            crashEvidenceId = 'crash-log.' + $blobSha256.Substring(0,24).ToLowerInvariant()
            virtualPath = [string](Get-GridBaselineOptionalProperty $diagnostic 'virtualPath')
            blobSha256 = $blobSha256; sizeBytes = [long]$item.Length
            lastWriteTimeUtcTicks = [long](Get-GridBaselineOptionalProperty $diagnostic 'lastWriteTimeUtcTicks')
            gameVersion = $gameVersion; loggerVersion = $loggerVersion; exceptionType = $exceptionType
            exceptionModule = $exceptionModule; exceptionAddressBasis = 'CrashLoggerUnhandledExceptionAddress'
            winningProvider = $provider; probableStackModules = @($probableModules.ToArray())
        })
    }
    $clusters = @($records | Group-Object { ([string]$_.exceptionModule).ToUpperInvariant() + '|' + ([string]$_.exceptionType).ToUpperInvariant() } | ForEach-Object {
        $ordered = @($_.Group | Sort-Object lastWriteTimeUtcTicks)
        $latest = $ordered[-1]
        [pscustomobject][ordered]@{
            exceptionModule = [string]$latest.exceptionModule; exceptionType = [string]$latest.exceptionType
            crashCount = $ordered.Count; latestCrashEvidenceId = [string]$latest.crashEvidenceId
            latestLogVirtualPath = [string]$latest.virtualPath; winningProvider = $latest.winningProvider
            evidenceStrength = 'DirectExceptionAddressModule'
            limitation = 'The exception address proves where execution failed, not by itself why the module received invalid state.'
        }
    } | Sort-Object @{Expression='crashCount';Descending=$true},exceptionModule,exceptionType)
    $summary = [pscustomobject][ordered]@{
        schemaVersion = 1; status = if (@($Normalized.DiagnosticOutputs).Count -eq 0) { 'NotObserved' } elseif ($records.Count -eq 0) { 'UnsupportedOrUnparsed' } else { 'Complete' }
        observedLogCount = @($Normalized.DiagnosticOutputs).Count; parsedCrashCount = $records.Count
        clusterCount = $clusters.Count; clusters = $clusters; records = $records.ToArray()
        note = 'Crash clusters are exact CrashLogger exception-address observations correlated only to the current winning SKSE plugin provider; causal attribution remains unresolved without corroboration.'
    }
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath 'diagnostics/crash-signatures.v1.json' -Value $summary | Out-Null
    $summary
}

function New-GridSkyrimSksePluginLoadSummary {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Transaction,
        [Parameter(Mandatory)]$Normalized
    )

    $dllProviders = Get-GridSkyrimWinningDllProviderMap -Normalized $Normalized
    $records = New-Object Collections.Generic.List[object]
    $observedLogs = 0
    foreach ($diagnostic in @($Normalized.DiagnosticOutputs | Sort-Object lastWriteTimeUtcTicks, virtualPath)) {
        $virtualPath = [string](Get-GridBaselineOptionalProperty $diagnostic 'virtualPath')
        if ($virtualPath -notmatch '^(?i)diagnostics/skse/skse(?:64)?\.log$') { continue }
        $observedLogs++
        $blobSha256 = ([string](Get-GridBaselineOptionalProperty $diagnostic 'blobSha256')).ToUpperInvariant()
        if ($blobSha256 -notmatch '^[A-F0-9]{64}$') { continue }
        $blobPath = Join-Path (Join-Path (Join-Path $Transaction.StoreRoot 'blobs\sha256') $blobSha256.Substring(0,2)) $blobSha256
        if (-not (Test-Path -LiteralPath $blobPath -PathType Leaf)) { continue }
        $item = Get-Item -LiteralPath $blobPath -Force -ErrorAction Stop
        if ([long]$item.Length -gt 8MB -or (Get-FileHash -LiteralPath $blobPath -Algorithm SHA256).Hash -cne $blobSha256) { continue }
        $reader = New-Object IO.StreamReader($blobPath, [Text.UTF8Encoding]::new($false, $true), $true, 65536)
        try {
            $lineNumber = 0
            while (($line = $reader.ReadLine()) -ne $null -and $lineNumber -lt 100000) {
                $lineNumber++
                $dllName = $null; $dataVersion = $null; $pluginName = $null; $pluginVersion = $null
                $loadStatus = $null; $errorText = $null; $errorCode = $null; $handle = $null
                if ($line -match '^\s*(?:\[[^\]]+\]\s*)?plugin\s+([^\s]+\.dll)\s+\(([0-9A-Fa-f]{8})\s+(.+?)\s+([0-9A-Fa-f]{8})\)\s+loaded correctly\s+\(handle\s+(-?\d+)\)\s*$') {
                    $dllName = $matches[1]; $dataVersion = $matches[2].ToUpperInvariant(); $pluginName = $matches[3]
                    $pluginVersion = $matches[4].ToUpperInvariant(); $handle = [int]$matches[5]; $loadStatus = 'Loaded'
                }
                elseif ($line -match '^\s*(?:\[[^\]]+\]\s*)?plugin\s+([^\s]+\.dll)\s+\(([0-9A-Fa-f]{8})\s+(.+?)\s+([0-9A-Fa-f]{8})\)\s+(.+?)\s+(-?\d+)\s+\(handle\s+(-?\d+)\)\s*$') {
                    $dllName = $matches[1]; $dataVersion = $matches[2].ToUpperInvariant(); $pluginName = $matches[3]
                    $pluginVersion = $matches[4].ToUpperInvariant(); $errorText = $matches[5].Trim(); $errorCode = [int]$matches[6]
                    $handle = [int]$matches[7]; $loadStatus = 'Rejected'
                }
                if (-not $loadStatus) { continue }
                $provider = if ($dllProviders.ContainsKey($dllName.ToUpperInvariant())) { $dllProviders[$dllName.ToUpperInvariant()] } else { $null }
                $records.Add([pscustomobject][ordered]@{
                    evidenceId = 'skse-log.' + $blobSha256.Substring(0,16).ToLowerInvariant() + '.' + $lineNumber
                    virtualPath = $virtualPath; blobSha256 = $blobSha256
                    lastWriteTimeUtcTicks = [long](Get-GridBaselineOptionalProperty $diagnostic 'lastWriteTimeUtcTicks')
                    lineNumber = $lineNumber; dllName = $dllName; dataVersionHex = $dataVersion
                    pluginName = $pluginName; pluginVersionHex = $pluginVersion; loadStatus = $loadStatus
                    errorText = $errorText; errorCode = $errorCode; handle = $handle; winningProvider = $provider
                })
            }
        } finally { $reader.Dispose() }
    }
    $ordered = @($records.ToArray() | Sort-Object lastWriteTimeUtcTicks,lineNumber,dllName)
    $rejected = @($ordered | Where-Object loadStatus -eq 'Rejected')
    $summary = [pscustomobject][ordered]@{
        schemaVersion = 1
        status = if ($observedLogs -eq 0) { 'NotObserved' } elseif ($ordered.Count -eq 0) { 'UnsupportedOrUnparsed' } else { 'Complete' }
        observedLogCount = $observedLogs; parsedPluginCount = $ordered.Count; loadedCount = @($ordered | Where-Object loadStatus -eq 'Loaded').Count
        rejectedCount = $rejected.Count; rejected = $rejected; records = $ordered
        note = 'Records are exact SKSE plugin-manager terminal load reports from a bounded skse64.log, correlated to the current winning MO2 DLL provider. A prior log is not proof of a current launch until its timestamp is refreshed.'
    }
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath 'diagnostics/skse-plugin-load.v1.json' -Value $summary | Out-Null
    $summary
}

function Invoke-GridSkyrimRecoveryArchiveInspections {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Transaction,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)]$Normalized,
        [Parameter(Mandatory)][string]$DiagnosticsExecutable,
        [Parameter(Mandatory)][long]$MaximumEntries,
        [Parameter(Mandatory)][ValidateRange(1, 86400)][int]$MaximumWallClockSeconds
    )

    $records = New-Object Collections.Generic.List[object]
    $assessments = New-Object Collections.Generic.List[object]
    $temporaryPaths = New-Object Collections.Generic.List[string]
    $requirements = @(Get-GridBaselineOptionalProperty $Normalized 'PluginScriptDependencies') + @(Get-GridBaselineOptionalProperty $Normalized 'PluginAssetDependencies')
    $requirementsPath = Join-Path $Transaction.TransactionDirectory ('component-requirements-' + [guid]::NewGuid().ToString('N') + '.ndjson')
    $temporaryPaths.Add($requirementsPath)
    $requirementsWriter = New-Object IO.StreamWriter($requirementsPath, $false, ([Text.UTF8Encoding]::new($false)))
    try {
        foreach ($requirement in $requirements) {
            $requirementsWriter.WriteLine(($requirement | ConvertTo-Json -Depth 30 -Compress))
        }
    }
    finally { $requirementsWriter.Dispose() }
    if (-not (Test-Path -LiteralPath $requirementsPath -PathType Leaf)) {
        throw 'ArchiveInspectionRequirementsMissing: normalized component dependency evidence is unavailable.'
    }
    $archiveGroups = @($Normalized.SourceArchives | Where-Object {
        [string](Get-GridBaselineOptionalProperty $_ 'kind') -ieq 'InstallationArchive' -and
        [string](Get-GridBaselineOptionalProperty $_ 'state') -ieq 'Present' -and
        [string](Get-GridBaselineOptionalProperty $_ 'hashStatus') -ieq 'Complete' -and
        [string](Get-GridBaselineOptionalProperty $_ 'sha256') -match '^[A-Fa-f0-9]{64}$' -and
        -not [string]::IsNullOrWhiteSpace([string](Get-GridBaselineOptionalProperty $_ 'canonicalPath'))
    } | Group-Object {
        ([string](Get-GridBaselineOptionalProperty $_ 'canonicalPath')).ToUpperInvariant() + '|' +
        ([string](Get-GridBaselineOptionalProperty $_ 'sha256')).ToUpperInvariant()
    } | Sort-Object Name)
    if ($archiveGroups.Count -gt 512) { throw 'ArchiveInspectionLimitExceeded: more than 512 unique recovery archives were observed.' }

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $sequence = 0
    $assessmentCommandCount = 0
    $fomodReconciliationCommandCount = 0
    foreach ($archiveGroup in $archiveGroups) {
        $sequence++
        $archive = @($archiveGroup.Group)[0]
        $archivePath = [IO.Path]::GetFullPath([string](Get-GridBaselineOptionalProperty $archive 'canonicalPath'))
        $expectedSha256 = ([string](Get-GridBaselineOptionalProperty $archive 'sha256')).ToUpperInvariant()
        $outputPath = Join-Path $Transaction.TransactionDirectory ("archive-match-$sequence.json")
        $errorPath = Join-Path $Transaction.TransactionDirectory ("archive-match-$sequence.stderr.txt")
        $temporaryPaths.Add($outputPath); $temporaryPaths.Add($errorPath)
        $nativeArguments = @(
            'repair-archive-match', '--archive', $archivePath, '--expected-sha256', $expectedSha256,
            '--requirements', $requirementsPath, '--max-entries', ([string]$MaximumEntries)
        )
        $argumentLine = (@($nativeArguments | ForEach-Object { ConvertTo-GridNativeArgument -Value ([string]$_) }) -join ' ')
        $process = Start-Process -FilePath $DiagnosticsExecutable -ArgumentList $argumentLine -NoNewWindow -PassThru `
            -RedirectStandardOutput $outputPath -RedirectStandardError $errorPath -ErrorAction Stop
        $remainingMilliseconds = [long]$MaximumWallClockSeconds * 1000L - $stopwatch.ElapsedMilliseconds
        if ($remainingMilliseconds -le 0 -or -not $process.WaitForExit([int][Math]::Min($remainingMilliseconds, [int]::MaxValue))) {
            try { $process.Kill() } catch { }
            try { $process.WaitForExit() } catch { }
            throw 'ArchiveInspectionTimeout: recovery archive inspection exceeded the sealed wall-clock budget.'
        }
        $process.WaitForExit()
        $record = $null
        if (Test-Path -LiteralPath $outputPath -PathType Leaf) {
            try { $record = Get-Content -Raw -LiteralPath $outputPath -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop } catch { }
        }
        if ($null -eq $record) {
            $safeError = if (Test-Path -LiteralPath $errorPath -PathType Leaf) {
                (@(Get-Content -LiteralPath $errorPath -ErrorAction SilentlyContinue | Select-Object -First 8) -join ' ').Trim()
            } else { '' }
            if ($safeError.Length -gt 1024) { $safeError = $safeError.Substring(0, 1024) }
            $record = [pscustomobject][ordered]@{
                schemaVersion = 1; status = 'CollectorFailed'; archivePath = $archivePath
                archiveLength = Get-GridBaselineOptionalProperty $archive 'length'; archiveSha256 = $expectedSha256
                format = $null; inspectedEntryCount = 0; entrySetSha256 = $null
                requiredFileCount = $requirements.Count; matchedRequiredFileCount = 0
                ambiguousRequiredFileCount = 0; matches = @(); fomodStatus = 'Unsupported'
                issues = @([pscustomobject][ordered]@{ code = 'mo2.repair.archive.matcher_failed'; detail = $(if ($safeError) { $safeError } else { "Grid.Diagnostics exited with code $($process.ExitCode)." }); entryPath = $null })
                evidenceId = $null
            }
        }
        $record | Add-Member -NotePropertyName sourceModIds -NotePropertyValue @($archiveGroup.Group | ForEach-Object { [string](Get-GridBaselineOptionalProperty $_ 'modId') } | Sort-Object -Unique) -Force
        $record | Add-Member -NotePropertyName sourceModNames -NotePropertyValue @($archiveGroup.Group | ForEach-Object { [string](Get-GridBaselineOptionalProperty $_ 'modName') } | Sort-Object -Unique) -Force
        $records.Add($record)

        $sourceModIds = @($archiveGroup.Group | ForEach-Object { [string](Get-GridBaselineOptionalProperty $_ 'modId') } | Where-Object { $_ } | Sort-Object -Unique)
        $sourceModNames = @($archiveGroup.Group | ForEach-Object { [string](Get-GridBaselineOptionalProperty $_ 'modName') } | Where-Object { $_ } | Sort-Object -Unique)
        $primaryGroups = @($requirements | Where-Object {
            $provider = [string](Get-GridBaselineOptionalProperty $_ 'sourceProvider')
            -not [string]::IsNullOrWhiteSpace($provider) -and $provider -in $sourceModNames
        } | Group-Object { [string](Get-GridBaselineOptionalProperty $_ 'pluginName') } | Sort-Object Name)
        foreach ($primaryGroup in $primaryGroups) {
            if ($assessments.Count -ge 512) { throw 'ArchiveCandidateAssessmentLimitExceeded: more than 512 archive/plugin candidate pairs were observed.' }
            $primaryPluginName = [string]$primaryGroup.Name
            if ([string]::IsNullOrWhiteSpace($primaryPluginName)) { continue }
            $pluginRecords = @($Normalized.PluginRecords | Where-Object { [string]$_.name -ieq $primaryPluginName -and $_.isEnabled -eq $true })
            if ($pluginRecords.Count -ne 1) { continue }
            $primaryProvider = [string]$pluginRecords[0].observation.sourceProvider
            $primaryHashes = @($Normalized.FileHashRecords | Where-Object {
                [string](Get-GridBaselineOptionalProperty $_ 'virtualPath') -ieq $primaryPluginName -and
                [string](Get-GridBaselineOptionalProperty $_ 'providerName') -ieq $primaryProvider -and
                [string](Get-GridBaselineOptionalProperty $_ 'status') -ieq 'Complete' -and
                [string](Get-GridBaselineOptionalProperty $_ 'sha256') -match '^[A-Fa-f0-9]{64}$'
            })
            if ($primaryHashes.Count -ne 1) { continue }
            $primaryArchiveStem = [IO.Path]::GetFileNameWithoutExtension($primaryPluginName)
            $installedArchiveHashes = @($Normalized.FileHashRecords | Where-Object {
                $virtualPath = [string](Get-GridBaselineOptionalProperty $_ 'virtualPath')
                [string](Get-GridBaselineOptionalProperty $_ 'providerName') -in $sourceModNames -and
                [string](Get-GridBaselineOptionalProperty $_ 'kind') -ieq 'Archive' -and
                [string](Get-GridBaselineOptionalProperty $_ 'status') -ieq 'Complete' -and
                [string](Get-GridBaselineOptionalProperty $_ 'sha256') -match '^[A-Fa-f0-9]{64}$' -and
                [IO.Path]::GetExtension($virtualPath) -match '(?i)^\.(?:bsa|ba2)$' -and
                [IO.Path]::GetFileNameWithoutExtension($virtualPath) -ieq $primaryArchiveStem
            } | ForEach-Object { ([string](Get-GridBaselineOptionalProperty $_ 'sha256')).ToUpperInvariant() } | Sort-Object -Unique)
            $installedArchiveHash = if ($installedArchiveHashes.Count -eq 1) { [string]$installedArchiveHashes[0] } else { $null }

            $fomodReconciliation = $null
            if ([string](Get-GridBaselineOptionalProperty $record 'fomodStatus') -eq 'SelectionRequired') {
                $reconciliationOutputPath = Join-Path $Transaction.TransactionDirectory ("fomod-reconciliation-$sequence-$($assessments.Count + 1).json")
                $reconciliationErrorPath = Join-Path $Transaction.TransactionDirectory ("fomod-reconciliation-$sequence-$($assessments.Count + 1).stderr.txt")
                $temporaryPaths.Add($reconciliationOutputPath); $temporaryPaths.Add($reconciliationErrorPath)
                $installedFilesPath = Join-Path $Transaction.CaseDirectory 'inventory\files.v1.ndjson'
                if (Test-Path -LiteralPath $installedFilesPath -PathType Leaf) {
                    $reconciliationArguments = New-Object Collections.Generic.List[string]
                    foreach ($value in @(
                        'repair-fomod-reconcile', '--archive', $archivePath, '--expected-sha256', $expectedSha256,
                        '--installed-files', $installedFilesPath, '--max-entries', ([string]$MaximumEntries)
                    )) { $reconciliationArguments.Add([string]$value) }
                    foreach ($providerName in $sourceModNames) {
                        $reconciliationArguments.Add('--provider'); $reconciliationArguments.Add([string]$providerName)
                    }
                    if (-not [string]::IsNullOrWhiteSpace($installedArchiveHash)) {
                        $reconciliationArguments.Add('--installed-archive-sha256'); $reconciliationArguments.Add($installedArchiveHash)
                    }
                    $reconciliationArgumentLine = (@($reconciliationArguments | ForEach-Object { ConvertTo-GridNativeArgument -Value ([string]$_) }) -join ' ')
                    $fomodReconciliationCommandCount++
                    $reconciliationProcess = Start-Process -FilePath $DiagnosticsExecutable -ArgumentList $reconciliationArgumentLine -NoNewWindow -PassThru `
                        -RedirectStandardOutput $reconciliationOutputPath -RedirectStandardError $reconciliationErrorPath -ErrorAction Stop
                    $remainingMilliseconds = [long]$MaximumWallClockSeconds * 1000L - $stopwatch.ElapsedMilliseconds
                    if ($remainingMilliseconds -le 0 -or -not $reconciliationProcess.WaitForExit([int][Math]::Min($remainingMilliseconds, [int]::MaxValue))) {
                        try { $reconciliationProcess.Kill() } catch { }
                        try { $reconciliationProcess.WaitForExit() } catch { }
                        throw 'FomodReconciliationTimeout: installed FOMOD selection reconciliation exceeded the sealed wall-clock budget.'
                    }
                    $reconciliationProcess.WaitForExit()
                    try { $fomodReconciliation = Get-Content -Raw -LiteralPath $reconciliationOutputPath -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop } catch { }
                    if ($null -ne $fomodReconciliation -and [string](Get-GridBaselineOptionalProperty $fomodReconciliation 'evidenceId') -notmatch '^fomod-selection\.[a-f0-9]{24}$') {
                        $fomodReconciliation = $null
                    }
                }
            }

            $metadata = @($Normalized.Metadata | Where-Object { [string]$_.name -ieq $primaryProvider })
            $installedVersion = if ($metadata.Count -eq 1) { [string](Get-GridBaselineOptionalProperty $metadata[0] 'version') } else { $null }
            $archiveLeaf = [string](Get-GridBaselineOptionalProperty $archive 'exactLeafName')
            $candidateVersions = @($Normalized.SourceArchiveSidecars | Where-Object {
                [string](Get-GridBaselineOptionalProperty $_ 'modId') -in $sourceModIds -and
                [string](Get-GridBaselineOptionalProperty $_ 'exactLeafName') -ieq ($archiveLeaf + '.meta')
            } | ForEach-Object {
                $identity = Get-GridBaselineOptionalProperty $_ 'providerIdentity'
                if ($identity -and [string](Get-GridBaselineOptionalProperty $identity 'status') -eq 'ObservedComplete') {
                    [string](Get-GridBaselineOptionalProperty $identity 'version')
                }
            } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
            $candidateVersion = if ($candidateVersions.Count -eq 1) { [string]$candidateVersions[0] } else { $null }

            $dependentValues = New-Object Collections.Generic.List[string]
            foreach ($masterRecord in @($Normalized.PluginMasterRecords | Where-Object {
                [string]$_.master.name -ieq $primaryPluginName
            } | Sort-Object pluginName)) {
                $dependentName = [string]$masterRecord.pluginName
                $dependentPlugin = @($Normalized.PluginRecords | Where-Object { [string]$_.name -ieq $dependentName -and $_.isEnabled -eq $true })
                if ($dependentPlugin.Count -ne 1) { continue }
                $dependentProvider = [string]$dependentPlugin[0].observation.sourceProvider
                $dependentHash = @($Normalized.FileHashRecords | Where-Object {
                    [string](Get-GridBaselineOptionalProperty $_ 'virtualPath') -ieq $dependentName -and
                    [string](Get-GridBaselineOptionalProperty $_ 'providerName') -ieq $dependentProvider -and
                    [string](Get-GridBaselineOptionalProperty $_ 'status') -ieq 'Complete' -and
                    [string](Get-GridBaselineOptionalProperty $_ 'sha256') -match '^[A-Fa-f0-9]{64}$'
                })
                if ($dependentHash.Count -eq 1) {
                    $dependentValues.Add($dependentName + '=' + ([string](Get-GridBaselineOptionalProperty $dependentHash[0] 'sha256')).ToUpperInvariant())
                }
            }

            $assessmentSequence = $assessments.Count + 1
            $assessmentOutputPath = Join-Path $Transaction.TransactionDirectory ("archive-candidate-$assessmentSequence.json")
            $assessmentErrorPath = Join-Path $Transaction.TransactionDirectory ("archive-candidate-$assessmentSequence.stderr.txt")
            $temporaryPaths.Add($assessmentOutputPath); $temporaryPaths.Add($assessmentErrorPath)
            $assessmentArguments = New-Object Collections.Generic.List[string]
            foreach ($value in @(
                'repair-archive-classify', '--archive', $archivePath, '--expected-sha256', $expectedSha256,
                '--primary-plugin-name', $primaryPluginName, '--installed-plugin-sha256',
                ([string](Get-GridBaselineOptionalProperty $primaryHashes[0] 'sha256')).ToUpperInvariant(),
                '--max-entries', ([string]$MaximumEntries)
            )) { $assessmentArguments.Add([string]$value) }
            if (-not [string]::IsNullOrWhiteSpace($installedVersion)) { $assessmentArguments.Add('--installed-version'); $assessmentArguments.Add($installedVersion) }
            if (-not [string]::IsNullOrWhiteSpace($candidateVersion)) { $assessmentArguments.Add('--candidate-version'); $assessmentArguments.Add($candidateVersion) }
            if (-not [string]::IsNullOrWhiteSpace($installedArchiveHash)) { $assessmentArguments.Add('--installed-archive-sha256'); $assessmentArguments.Add($installedArchiveHash) }
            if ($null -ne $fomodReconciliation -and [string](Get-GridBaselineOptionalProperty $fomodReconciliation 'status') -eq 'Complete') {
                foreach ($selection in @($fomodReconciliation.selectionVector)) {
                    $selectedNames = @($selection.pluginNames)
                    if ($selectedNames.Count -eq 0) {
                        $assessmentArguments.Add('--select'); $assessmentArguments.Add(([string]$selection.groupName + '='))
                    }
                    else {
                        foreach ($selectedName in $selectedNames) {
                            $assessmentArguments.Add('--select'); $assessmentArguments.Add(([string]$selection.groupName + '=' + [string]$selectedName))
                        }
                    }
                }
            }
            foreach ($dependentValue in $dependentValues) { $assessmentArguments.Add('--dependent'); $assessmentArguments.Add($dependentValue) }
            $assessmentArgumentLine = (@($assessmentArguments | ForEach-Object { ConvertTo-GridNativeArgument -Value ([string]$_) }) -join ' ')
            $assessmentCommandCount++
            $assessmentProcess = Start-Process -FilePath $DiagnosticsExecutable -ArgumentList $assessmentArgumentLine -NoNewWindow -PassThru `
                -RedirectStandardOutput $assessmentOutputPath -RedirectStandardError $assessmentErrorPath -ErrorAction Stop
            $remainingMilliseconds = [long]$MaximumWallClockSeconds * 1000L - $stopwatch.ElapsedMilliseconds
            if ($remainingMilliseconds -le 0 -or -not $assessmentProcess.WaitForExit([int][Math]::Min($remainingMilliseconds, [int]::MaxValue))) {
                try { $assessmentProcess.Kill() } catch { }
                try { $assessmentProcess.WaitForExit() } catch { }
                throw 'ArchiveCandidateAssessmentTimeout: recovery archive assessment exceeded the sealed wall-clock budget.'
            }
            $assessmentProcess.WaitForExit()
            try { $assessment = Get-Content -Raw -LiteralPath $assessmentOutputPath -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop } catch { continue }
            if ([string](Get-GridBaselineOptionalProperty $assessment 'evidenceId') -notmatch '^archive-candidate\.[a-f0-9]{24}$') { continue }
            $assessment | Add-Member -NotePropertyName sourceModIds -NotePropertyValue $sourceModIds -Force
            $assessment | Add-Member -NotePropertyName sourceModNames -NotePropertyValue $sourceModNames -Force
            $assessment | Add-Member -NotePropertyName fomodSelectionEvidence -NotePropertyValue $fomodReconciliation -Force
            $assessments.Add($assessment)
        }
    }

    $ndjsonPath = Join-Path $Transaction.TransactionDirectory ('archive-inspections-' + [guid]::NewGuid().ToString('N') + '.ndjson')
    $temporaryPaths.Add($ndjsonPath)
    $writer = New-Object IO.StreamWriter($ndjsonPath, $false, ([Text.UTF8Encoding]::new($false)))
    try { foreach ($record in $records) { $writer.WriteLine(($record | ConvertTo-Json -Depth 30 -Compress)) } }
    finally { $writer.Dispose() }
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath 'provenance/source-archive-inspections.v1.ndjson' -SourceLiteralPath $ndjsonPath | Out-Null
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath 'provenance/source-archive-inspections.v1.json' -Value ([pscustomobject][ordered]@{
        schemaVersion = 1; status = if ($archiveGroups.Count -eq 0) { 'NotCollectedWithoutHashedRecoveryArchive' } elseif (@($records | Where-Object status -eq 'Complete').Count -eq $archiveGroups.Count) { 'Complete' } else { 'Partial' }
        recordFormat = 'ndjson'; recordPath = 'provenance/source-archive-inspections.v1.ndjson'; recordCount = $records.Count
        completeCount = @($records | Where-Object status -eq 'Complete').Count
        note = 'Each record is a bounded read-only archive inspection reduced to exact archive-root or explicit Data-directory matches against the complete missing script, mesh, and texture requirement set.'
    }) | Out-Null
    $assessmentNdjsonPath = Join-Path $Transaction.TransactionDirectory ('archive-candidate-assessments-' + [guid]::NewGuid().ToString('N') + '.ndjson')
    $temporaryPaths.Add($assessmentNdjsonPath)
    $assessmentWriter = New-Object IO.StreamWriter($assessmentNdjsonPath, $false, ([Text.UTF8Encoding]::new($false)))
    try { foreach ($assessment in $assessments) { $assessmentWriter.WriteLine(($assessment | ConvertTo-Json -Depth 30 -Compress)) } }
    finally { $assessmentWriter.Dispose() }
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath 'provenance/source-archive-candidate-assessments.v1.ndjson' -SourceLiteralPath $assessmentNdjsonPath | Out-Null
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath 'provenance/source-archive-candidate-assessments.v1.json' -Value ([pscustomobject][ordered]@{
        schemaVersion = 1; status = if ($archiveGroups.Count -eq 0) { 'NotCollectedWithoutHashedRecoveryArchive' } elseif ($assessments.Count -eq 0) { 'NoClassifiableCandidate' } else { 'Complete' }
        recordFormat = 'ndjson'; recordPath = 'provenance/source-archive-candidate-assessments.v1.ndjson'; recordCount = $assessments.Count
        exactRestorationCount = @($assessments | Where-Object candidateRole -eq 'ExactRestoration').Count
        updateCandidateCount = @($assessments | Where-Object candidateRole -eq 'CompleteUpdateCandidate').Count
        note = 'Each record classifies one exact hashed archive/plugin pair against current installed plugin and dependent-plugin hashes. Classification is evidence only and grants no repair authority.'
    }) | Out-Null
    [pscustomobject][ordered]@{ Records = $records.ToArray(); Assessments = $assessments.ToArray(); TemporaryPaths = $temporaryPaths.ToArray(); CommandCount = $archiveGroups.Count; AssessmentCommandCount = $assessmentCommandCount; FomodReconciliationCommandCount = $fomodReconciliationCommandCount }
}

function Invoke-GridSkyrimBaseline {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)]$InvestigationPlan,
        [Parameter(Mandatory)][string]$CaseStoreRoot,
        [Parameter(Mandatory)][string]$InstallationId,
        [Parameter(Mandatory)][string]$ProfileId,
        [Parameter(Mandatory)][string]$ProfileName,
        [string[]]$ParentCaseIds = @(),
        [string[]]$EvidenceIds = @(),
        [string]$ResourcePolicyVersion = 'grid.baseline.resource-policy.v1',
        [Parameter(Mandatory)][string]$ApplicationPath,
        [Parameter(Mandatory)][string]$InstancePath,
        [Parameter(Mandatory)]$ResolvedContext,
        [string]$PredecessorCaseId,
        [string]$HashCacheCaseId,
        [string]$FinalizationCaseId,
        [switch]$PassThru
    )

    $transaction = $null
    $runId = 'run-' + [guid]::NewGuid().ToString('N')
    $startedAt = [DateTimeOffset]::UtcNow.ToString('o')
    $stage = 'ValidatePlan'
    try {
        $validation = Test-GridInvestigationPlanSchema -Plan $InvestigationPlan
        if (-not $validation.IsValid) { throw ('PlanInvalid: ' + ($validation.Errors -join ' ')) }
        if ([string]$InvestigationPlan.purpose -ne 'Baseline' -or [string]$InvestigationPlan.status -ne 'ReadyToCollect') {
            throw 'PlanInvalid: the Skyrim baseline collector requires a ReadyToCollect Baseline-purpose plan.'
        }
        if ([string]$InvestigationPlan.caseId -cne $CaseId -or [string]$InvestigationPlan.installationId -cne $InstallationId -or [string]$InvestigationPlan.profileId -cne $ProfileId) {
            throw 'PlanInvalid: case, installation, or profile does not match the bound InvestigationPlan.'
        }
        $binding = @($InvestigationPlan.capabilities | Where-Object { [string]$_.capabilityId -ceq 'grid.game.skyrimspecialedition.baseline.collect' })
        if ($binding.Count -ne 1 -or [string]$binding[0].capabilityVersion -cne '2.6.0') {
            throw 'PlanInvalid: the exact Skyrim baseline capability version is not bound.'
        }

        $stage = 'LoadCaseStore'
        $caseStorePath = Join-Path $scriptsRoot 'health\Grid.CaseStore.ps1'
        $governorPath = Join-Path $scriptsRoot 'health\Grid.ResourceGovernor.ps1'
        if (-not (Test-Path -LiteralPath $caseStorePath -PathType Leaf) -or -not (Test-Path -LiteralPath $governorPath -PathType Leaf)) {
            throw 'CollectorUnavailable: case-store or resource-governor support is unavailable.'
        }
        foreach ($requiredCommand in @('New-GridCaseStoreTransaction', 'Write-GridCaseStoreArtifact', 'New-GridCaseStoreCheckpoint', 'Seal-GridCaseStoreRun', 'Seal-GridDiagnosticCase', 'New-GridBaselineResourceBudget')) {
            if (-not (Get-Command $requiredCommand -ErrorAction SilentlyContinue)) { throw "CollectorUnavailable: required command '$requiredCommand' is unavailable." }
        }
        $stage = 'CreateTransaction'
        $continuationModes = @(
            @($PredecessorCaseId, $HashCacheCaseId, $FinalizationCaseId) |
                Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
        )
        if ($continuationModes.Count -gt 1) {
            throw 'PlanInvalid: predecessor resume, completed hash-cache reuse, and post-collection finalization are mutually exclusive.'
        }
        $resumeOutputPath = $null
        $hashCache = $null
        $finalization = $null
        if (-not [string]::IsNullOrWhiteSpace($PredecessorCaseId)) {
            $resumeOutputPath = Resolve-GridBaselineResumeArtifact -CaseStoreRoot $CaseStoreRoot -PredecessorCaseId $PredecessorCaseId `
                -InvestigationPlan $InvestigationPlan -InstallationId $InstallationId -ProfileId $ProfileId
        }
        elseif (-not [string]::IsNullOrWhiteSpace($HashCacheCaseId)) {
            $hashCache = Resolve-GridBaselineHashCacheArtifact -CaseStoreRoot $CaseStoreRoot -HashCacheCaseId $HashCacheCaseId `
                -InvestigationPlan $InvestigationPlan -ResolvedContext $ResolvedContext -InstallationId $InstallationId -ProfileId $ProfileId
            $resumeOutputPath = [string]$hashCache.Path
        }
        elseif (-not [string]::IsNullOrWhiteSpace($FinalizationCaseId)) {
            $finalization = Resolve-GridBaselineFinalizationArtifact -CaseStoreRoot $CaseStoreRoot -FinalizationCaseId $FinalizationCaseId `
                -InvestigationPlan $InvestigationPlan -ResolvedContext $ResolvedContext -InstallationId $InstallationId -ProfileId $ProfileId
        }
        $storeRootPath = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($CaseStoreRoot))
        $storeDrive = Get-PSDrive -Name $storeRootPath.TrimEnd('\').TrimEnd(':') -ErrorAction SilentlyContinue
        $storeFree = if ($storeDrive) { [long]$storeDrive.Free } else { 0L }
        $storeVolume = if ($storeDrive) { [long]$storeDrive.Free + [long]$storeDrive.Used } else { 0L }
        $budget = New-GridBaselineResourceBudget -PolicyVersion $ResourcePolicyVersion -CaseStoreVolumeBytes $storeVolume `
            -CaseStoreFreeBytes $storeFree -EstimatedCopiedEvidenceBytes 268435456
        if ($storeFree -gt 0 -and $storeFree -le [long]$budget.derivedAllocations.storageReserveBytes) {
            throw 'InsufficientStorage: the case-store volume cannot retain the required free-space reserve.'
        }
        $transaction = New-GridCaseStoreTransaction -StoreRoot $CaseStoreRoot -CaseId $CaseId
        $stage = 'WritePlanArtifacts'
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'case.json' -Value ([pscustomobject][ordered]@{ schemaVersion = 1; caseId = $CaseId; createdAt = $startedAt; purpose = 'Baseline'; status = 'Running' }) | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'investigation-plan.json' -Value $InvestigationPlan | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath ("runs/$runId/resolved-roots.v1.json") -Value $ResolvedContext | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath ("runs/$runId/resource-budget.v1.json") -Value $budget | Out-Null
        if (-not [string]::IsNullOrWhiteSpace($PredecessorCaseId)) {
            $predecessorDirectory = Join-Path (Join-Path $CaseStoreRoot 'cases\v1') $PredecessorCaseId
            $predecessorManifest = Get-Content -Raw -LiteralPath (Join-Path $predecessorDirectory 'case-manifest.v1.json') | ConvertFrom-Json -ErrorAction Stop
            Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath ("runs/$runId/resume-lineage.v1.json") -Value ([pscustomobject][ordered]@{
                schemaVersion = 1
                predecessorCaseId = $PredecessorCaseId
                predecessorManifestSha256 = [string]$predecessorManifest.manifestSha256
                reusedCollectorOutputSha256 = (Get-FileHash -LiteralPath $resumeOutputPath -Algorithm SHA256).Hash
                validation = 'Seal, plan, context, capability, protected state, source stamps, and collector-output digest verified before successor creation.'
            }) | Out-Null
        }
        elseif ($hashCache) {
            Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath ("runs/$runId/hash-cache-lineage.v1.json") -Value ([pscustomobject][ordered]@{
                schemaVersion = 1
                sourceCaseId = [string]$hashCache.CaseId
                sourceManifestSha256 = [string]$hashCache.ManifestSha256
                reusedCollectorOutputSha256 = [string]$hashCache.ArtifactSha256
                reusableCompletedHashCount = [long]$hashCache.CompleteHashes
                configurationDriftAccepted = [bool]$hashCache.ConfigurationDriftAccepted
                profileStateDriftAccepted = [bool]$hashCache.ProfileStateDriftAccepted
                acceptedReadOnlyDriftPaths = @($hashCache.AcceptedReadOnlyDriftPaths)
                validation = 'Seal, installation/profile binding, capability version, protected provider/connection state, source artifact digest, and terminal collector summary were verified. MO2 configuration and canonical profile-order document drift are accepted only for a read-only successor that reparses the current profile; each predecessor hash is reused only when current path, length, and timestamp still match.'
            }) | Out-Null
        }
        elseif ($finalization) {
            Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath ("runs/$runId/finalization-lineage.v1.json") -Value ([pscustomobject][ordered]@{
                schemaVersion = 1
                sourceCaseId = [string]$finalization.CaseId
                sourceRunId = [string]$finalization.FailedRunId
                sourceManifestSha256 = [string]$finalization.ManifestSha256
                reusedCollectorArtifactPath = [string]$finalization.ArtifactPath
                reusedCollectorOutputSha256 = [string]$finalization.ArtifactSha256
                sourceResolvedContextSha256 = [string]$finalization.ResolvedContextSha256
                reason = 'PostCollectionFinalization'
                collectorRelaunched = $false
                validation = 'Seal, failed-run lineage, complete plan/capability binding, exact resolved context, unchanged protected state, non-mutation audit, source artifact digest, terminal summary, and sealed diagnostic blobs were verified before successor creation.'
            }) | Out-Null
        }
        $protectedBefore = @(
            Get-GridBaselineFileObservation -LiteralPath ([string]$ResolvedContext.referenceStorePath) -ExpectedSha256 ([string]$ResolvedContext.referenceStoreSha256)
            Get-GridBaselineFileObservation -LiteralPath ([string]$ResolvedContext.configurationPath) -ExpectedSha256 ([string]$ResolvedContext.configurationSha256)
        )
        $categoriesPath = Join-Path $InstancePath 'categories.dat'
        if (Test-Path -LiteralPath $categoriesPath -PathType Leaf) { $protectedBefore += Get-GridBaselineFileObservation -LiteralPath $categoriesPath }
        $profileDirectory = [IO.Path]::GetFullPath((Join-Path (Join-Path $InstancePath 'profiles') $ProfileName))
        foreach ($profileLeaf in @('modlist.txt','plugins.txt','loadorder.txt','settings.ini','skyrim.ini','skyrimprefs.ini','skyrimcustom.ini')) {
            $profileSourcePath = Join-Path $profileDirectory $profileLeaf
            if (Test-Path -LiteralPath $profileSourcePath -PathType Leaf) {
                $protectedBefore += Get-GridBaselineFileObservation -LiteralPath $profileSourcePath
            }
        }
        # Provider seeds are exact MO2 mod-directory identities, separate from
        # plugin filenames. Capture their metadata before the native collector.
        foreach ($providerSeed in @($InvestigationPlan.providerSeeds | ForEach-Object { [string]$_.name } | Where-Object { $_ })) {
            if ([IO.Path]::GetFileName($providerSeed) -cne $providerSeed) { throw "PlanInvalid: provider seed '$providerSeed' is not an exact directory leaf." }
            $seedMetadata = Join-Path (Join-Path (Join-Path $InstancePath 'mods') $providerSeed) 'meta.ini'
            if (Test-Path -LiteralPath $seedMetadata -PathType Leaf) {
                $protectedBefore += Get-GridBaselineFileObservation -LiteralPath $seedMetadata
            }
        }

        $stage = 'InvokeCollector'
        if ($finalization) {
            $bridge = [pscustomobject][ordered]@{
                Status = 'Completed'; ExitCode = 0; OutputPath = [string]$finalization.Path; ErrorPath = $null; BuildPath = $null
                Summary = $finalization.Summary; Attempts = @(); Authorizations = @(); ExecutablePath = $null; PrimaryFailure = $null
            }
        }
        else {
            $bridge = Invoke-GridMo2BaselineCollection -CaseId $CaseId -InvestigationPlan $InvestigationPlan `
                -TransactionDirectory $transaction.TransactionDirectory -InstallationId $InstallationId -ProfileId $ProfileId `
                -ProfileName $ProfileName `
                -ParentCaseIds $ParentCaseIds -EvidenceIds $EvidenceIds -RequiredGates @($InvestigationPlan.requiredGates) `
                -ResourceBudget $budget -ResourcePolicyVersion $ResourcePolicyVersion -ApplicationPath $ApplicationPath -InstancePath $InstancePath `
                -ExplicitProviderSeeds @($InvestigationPlan.providerSeeds | ForEach-Object { [string]$_.name }) `
                -ExplicitPluginSeeds @($InvestigationPlan.candidatePlugins | ForEach-Object { [string]$_.name }) -ResumeOutputPath $resumeOutputPath
        }
        $stage = 'ImportCollectorArtifacts'
        $collectorOutputHash = if ($bridge.OutputPath -and (Test-Path -LiteralPath $bridge.OutputPath -PathType Leaf)) {
            (Get-FileHash -LiteralPath $bridge.OutputPath -Algorithm SHA256).Hash
        } else { 'NO-COLLECTOR-OUTPUT' }
        foreach ($attempt in @($bridge.Attempts)) {
            if ($attempt.outputPath -and (Test-Path -LiteralPath $attempt.outputPath -PathType Leaf)) {
                Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath ("runs/$runId/raw/mo2-baseline-attempt-$($attempt.sequence).ndjson") -SourceLiteralPath $attempt.outputPath | Out-Null
            }
            if ($attempt.errorPath -and (Test-Path -LiteralPath $attempt.errorPath -PathType Leaf)) {
                Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath ("runs/$runId/raw/mo2-baseline-attempt-$($attempt.sequence).stderr.txt") -SourceLiteralPath $attempt.errorPath | Out-Null
            }
        }
        if ($bridge.BuildPath -and (Test-Path -LiteralPath $bridge.BuildPath -PathType Leaf)) {
            Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath ("runs/$runId/collector-build.txt") -SourceLiteralPath $bridge.BuildPath | Out-Null
        }
        $normalized = $null
        $archiveInspection = $null
        $crashSignatures = $null
        $sksePluginLoad = $null
        if ($bridge.OutputPath -and (Test-Path -LiteralPath $bridge.OutputPath -PathType Leaf)) {
            $normalized = Write-GridBaselineNormalizedArtifacts -Transaction $transaction -RunId $runId -OutputPath $bridge.OutputPath `
                -InvestigationPlan $InvestigationPlan -ResolvedContext $ResolvedContext -Summary $bridge.Summary -FinalizationSource $finalization `
                -MaximumInputBytes ([long]$budget.recordedLimits.maximumCaseMetadataBytes) `
                -MaximumWallClockSeconds ([Math]::Min(1800, [int]$budget.limits.maximumWallClockSeconds))
            $crashSignatures = New-GridSkyrimCrashSignatureSummary -Transaction $transaction -Normalized $normalized
            $sksePluginLoad = New-GridSkyrimSksePluginLoadSummary -Transaction $transaction -Normalized $normalized
            if ($finalization) {
                $archiveInspection = Import-GridBaselineFinalizationArchiveInspection -Transaction $transaction -Finalization $finalization
            }
            else {
                $diagnosticsExecutable = [string]$bridge.ExecutablePath
                if ([string]::IsNullOrWhiteSpace($diagnosticsExecutable) -or -not (Test-Path -LiteralPath $diagnosticsExecutable -PathType Leaf)) {
                    throw 'CollectorUnavailable: the exact Grid.Diagnostics executable used for baseline collection is unavailable for archive inspection.'
                }
                $archiveInspection = Invoke-GridSkyrimRecoveryArchiveInspections -Transaction $transaction -RunId $runId `
                    -Normalized $normalized -DiagnosticsExecutable $diagnosticsExecutable `
                    -MaximumEntries ([long]$budget.limits.maximumFilesObserved) `
                    -MaximumWallClockSeconds ([int]$budget.limits.maximumWallClockSeconds)
            }
            # Do not backfill the before snapshot from observations made during
            # collection. Any graph-expanded metadata not known at preflight is
            # intentionally excluded from the before/after non-mutation claim.
        }
        $protectedBefore = @($protectedBefore | Sort-Object path -Unique)
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'snapshots/protected-before.v1.json' -Value ([pscustomobject][ordered]@{
            schemaVersion = 1; caseId = $CaseId; installationId = $InstallationId; profileId = $ProfileId; observedAt = $startedAt; files = $protectedBefore
        }) | Out-Null
        $protectedAfter = @($protectedBefore | ForEach-Object { Get-GridBaselineFileObservation -LiteralPath ([string]$_.path) -ExpectedSha256 ([string]$_.sha256) })
        $drift = @($protectedAfter | Where-Object { $_.matchesExpected -ne $true -or $_.hashStatus -ne 'Hashed' })
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'snapshots/protected-after.v1.json' -Value ([pscustomobject][ordered]@{
            schemaVersion = 1; caseId = $CaseId; installationId = $InstallationId; profileId = $ProfileId; observedAt = [DateTimeOffset]::UtcNow.ToString('o'); files = $protectedAfter
        }) | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'audit/non-mutation.v1.json' -Value ([pscustomobject][ordered]@{
            schemaVersion = 1; result = if ($drift.Count -eq 0) { 'NoChangeObserved' } else { 'DriftObserved' }; changed = $drift
            basis = @('read-only collector call graph', 'protected before/after SHA-256 and stamps', 'no MO2/xEdit/game launch', 'case-store-only writes')
            limitation = 'This does not prove another process could not change and restore bytes between observations.'
        }) | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'audit/command-audit.v1.json' -Value ([pscustomobject][ordered]@{
            schemaVersion = 1; commands = @(
                [pscustomobject][ordered]@{ executable = 'Grid.Diagnostics'; command = 'mo2-baseline'; attempts = @($bridge.Attempts).Count; reusedSealedOutput = [bool]$finalization; externalWrites = $false; gameOrManagerLaunch = $false }
                [pscustomobject][ordered]@{ executable = 'Grid.Diagnostics'; command = 'repair-archive-match'; attempts = $(if ($archiveInspection) { [int]$archiveInspection.CommandCount } else { 0 }); reusedSealedOutput = [bool]($finalization -and $archiveInspection); externalWrites = $false; gameOrManagerLaunch = $false }
                [pscustomobject][ordered]@{ executable = 'Grid.Diagnostics'; command = 'repair-archive-classify'; attempts = $(if ($archiveInspection) { [int]$archiveInspection.AssessmentCommandCount } else { 0 }); reusedSealedOutput = [bool]($finalization -and $archiveInspection); externalWrites = $false; gameOrManagerLaunch = $false }
                [pscustomobject][ordered]@{ executable = 'Grid.Diagnostics'; command = 'repair-fomod-reconcile'; attempts = $(if ($archiveInspection) { [int]$archiveInspection.FomodReconciliationCommandCount } else { 0 }); reusedSealedOutput = [bool]($finalization -and $archiveInspection); externalWrites = $false; gameOrManagerLaunch = $false }
            )
        }) | Out-Null
        foreach ($temporaryPath in @(Get-GridBaselineTransactionTemporaryPaths -Bridge $bridge -Normalized $normalized -ArchiveInspection $archiveInspection)) {
            $temporaryFull = [IO.Path]::GetFullPath([string]$temporaryPath)
            $transactionPrefix = [IO.Path]::GetFullPath($transaction.TransactionDirectory).TrimEnd('\') + '\'
            if (-not $temporaryFull.StartsWith($transactionPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'CaseWriteFailed: collector staging output escaped the case-store transaction.'
            }
            if (Test-Path -LiteralPath $temporaryFull -PathType Leaf) { Remove-Item -LiteralPath $temporaryFull -Force -ErrorAction Stop }
            elseif (Test-Path -LiteralPath $temporaryFull -PathType Container) { Remove-Item -LiteralPath $temporaryFull -Recurse -Force -ErrorAction Stop }
        }
        $stage = 'SealRun'
        $candidatePluginNames = @($InvestigationPlan.candidatePlugins | ForEach-Object { [string]$_.name } | Where-Object { $_ } | Sort-Object -Unique)
        $providerSeedNames = @($InvestigationPlan.providerSeeds | ForEach-Object { [string]$_.name } | Where-Object { $_ } | Sort-Object -Unique)
        $observedPluginNames = @($(if ($normalized) { $normalized.PluginNames } else { @() }))
        $observedMetadataNames = @($(if ($normalized) { $normalized.Metadata | ForEach-Object { [string]$_.name } } else { @() }))
        $observedFileProviders = @($(if ($normalized) { $normalized.FileProviders } else { @() }))
        $observedVirtualProviders = @($(if ($normalized) { $normalized.VirtualProviderNames } else { @() }))
        $missingCandidatePlugins = @($candidatePluginNames | Where-Object { $_ -notin $observedPluginNames })
        $missingProviders = @($providerSeedNames | Where-Object { $_ -notin $observedMetadataNames })
        $scopedAssetProviders = @($providerSeedNames | Where-Object { $_ -in $observedFileProviders -or $_ -in $observedVirtualProviders })
        $runtimeEvidence = if ($bridge.Status -eq 'Completed' -and $normalized) {
            Get-GridSkyrimBaselineRuntimeReferenceEvidence -InvestigationPlan $InvestigationPlan -PluginRecords @($normalized.PluginRecords) -ResolvedRoots @($normalized.Roots)
        } else {
            [pscustomobject][ordered]@{ schemaVersion = 1; status = 'Incomplete'; evidenceId = $null; records = @(); issues = @('The baseline collector did not complete.') }
        }
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'evidence/runtime-references.v1.json' -Value $runtimeEvidence | Out-Null
        $gateStatuses = @{
            InstallationBaseline = if ($bridge.Status -eq 'Completed' -and $bridge.Summary.installationId) { 'Complete' } else { 'IncompleteRequiredEvidence' }
            ProfileBaseline = if ($bridge.Status -eq 'Completed' -and $bridge.Summary.profileId -and $drift.Count -eq 0) { 'Complete' } else { 'IncompleteRequiredEvidence' }
            PluginBaseline = if ($bridge.Status -eq 'Completed' -and $candidatePluginNames.Count -gt 0 -and $missingCandidatePlugins.Count -eq 0) { 'Complete' } else { 'IncompleteRequiredEvidence' }
            AssetBaseline = if ($bridge.Status -eq 'Completed' -and $providerSeedNames.Count -gt 0 -and $missingProviders.Count -eq 0 -and $scopedAssetProviders.Count -gt 0) { 'Complete' } else { 'IncompleteRequiredEvidence' }
            PriorCaseBaseline = if (@($ParentCaseIds).Count + @($EvidenceIds).Count -eq 0) { 'NotApplicable' } else { 'IncompleteRequiredEvidence' }
            SymptomEvidenceBaseline = if (@($InvestigationPlan.locations).Count -gt 0) { 'CompleteWithUnavailableOptionalEvidence' } else { 'IncompleteRequiredEvidence' }
            RuntimeReferenceBaseline = if ([string]$runtimeEvidence.status -eq 'Verified') { 'Complete' } else { 'IncompleteRequiredEvidence' }
        }
        $gates = @($InvestigationPlan.requiredGates | ForEach-Object {
            $gateName = [string]$_
            $unavailable = switch ($gateName) {
                'PluginBaseline' { @($missingCandidatePlugins | ForEach-Object { "Explicit candidate plugin not observed: $_" }) }
                'AssetBaseline' {
                    @($missingProviders | ForEach-Object { "Explicit provider seed not observed: $_" }) +
                    $(if ($scopedAssetProviders.Count -eq 0) { @('No hashed file or virtual-provider evidence was scoped to an explicit provider seed.') } else { @() })
                }
                'RuntimeReferenceBaseline' { if ([string]$runtimeEvidence.status -eq 'Verified') { @() } else { @($runtimeEvidence.issues) + @('User-stated FormIDs/EditorIDs could not all be verified against bounded current installed-record evidence.') } }
                'PriorCaseBaseline' { if (@($ParentCaseIds).Count + @($EvidenceIds).Count -eq 0) { @() } else { @('Linked prior evidence was not resolved into the sealed baseline.') } }
                default { @() }
            }
            [pscustomobject][ordered]@{ schemaVersion = 1; gate = $gateName; status = [string]$gateStatuses[$gateName]; required = $true; evidenceIds = if ($gateName -eq 'RuntimeReferenceBaseline' -and $runtimeEvidence.evidenceId) { @([string]$runtimeEvidence.evidenceId) } else { @() }; unavailableReasons = @($unavailable); contradictions = @(); updatedAt = [DateTimeOffset]::UtcNow.ToString('o') }
        })
        foreach ($gate in $gates) { Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath ("gates/$($gate.gate).v1.json") -Value $gate | Out-Null }
        $incompleteGates = @($gates | Where-Object { $_.status -notin @('Complete', 'CompleteWithUnavailableOptionalEvidence', 'NotApplicable') } | ForEach-Object { $_.gate })
        $sufficiency = [pscustomobject][ordered]@{
            schemaVersion = 1; caseId = $CaseId; status = if ($incompleteGates.Count -eq 0) { 'SufficientForRequestedDiagnosis' } else { 'InsufficientForRequestedDiagnosis' }
            evaluatedAt = [DateTimeOffset]::UtcNow.ToString('o'); requiredGates = @($InvestigationPlan.requiredGates); incompleteGates = $incompleteGates
            contradictions = @(); nextRequiredEvidence = @($incompleteGates | ForEach-Object { "Complete evidence gate $_." })
        }
        $usage = [pscustomobject][ordered]@{ wallClockSeconds = 0; filesObserved = if ($bridge.Summary) { [long]$bridge.Summary.physicalFiles } else { 0 }; bytesRead = if ($bridge.Summary) { [long]$bridge.Summary.hashedBytes } else { 0 }; bytesHashed = if ($bridge.Summary) { [long]$bridge.Summary.hashedBytes } else { 0 }; attachmentBytes = 0; priorCases = @($ParentCaseIds).Count; concurrentReads = 1; filesSinceCheckpoint = 0; secondsSinceCheckpoint = 0; bytesHashedSinceCheckpoint = 0 }
        if ([long]$usage.filesObserved -gt [long]$budget.limits.maximumFilesObserved -or [long]$usage.bytesHashed -gt [long]$budget.limits.maximumBytesHashed) {
            throw 'BudgetExceeded: collector resource usage exceeded the sealed resource budget.'
        }
        $checkpointRefs = New-Object Collections.Generic.List[object]
        $nativeCheckpoints = @($(if ($normalized) { $normalized.Checkpoints } else { @() }))
        if ($nativeCheckpoints.Count -eq 0) { $nativeCheckpoints = @([pscustomobject]@{ stage = 'terminal'; completed = [long]$usage.filesObserved; total = [long]$usage.filesObserved; hashedBytes = [long]$usage.bytesHashed }) }
        $checkpointSequence = 0
        foreach ($nativeCheckpoint in $nativeCheckpoints) {
            $checkpointSequence++
            $checkpointUsage = [pscustomobject][ordered]@{
                wallClockSeconds = 0; filesObserved = [long]$nativeCheckpoint.completed
                bytesRead = [long]$nativeCheckpoint.hashedBytes; bytesHashed = [long]$nativeCheckpoint.hashedBytes
                attachmentBytes = 0; priorCases = @($ParentCaseIds).Count; concurrentReads = 1
                filesSinceCheckpoint = 0; secondsSinceCheckpoint = 0; bytesHashedSinceCheckpoint = 0
                cursor = [pscustomobject][ordered]@{ stage = [string]$nativeCheckpoint.stage; completed = [long]$nativeCheckpoint.completed; total = $nativeCheckpoint.total }
            }
            $checkpoint = New-GridCaseStoreCheckpoint -Transaction $transaction -RunId $runId -Sequence $checkpointSequence -ResourceUsage $checkpointUsage -Gates $gates
            $checkpointPath = 'runs/{0}/checkpoints/{1:d8}.json' -f $runId, $checkpointSequence
            $checkpointArtifact = @($transaction.Artifacts | Where-Object { $_.path -eq $checkpointPath })[0]
            $checkpointRefs.Add([pscustomobject][ordered]@{ checkpointId = $checkpoint.checkpointId; state = $checkpoint.state; path = $checkpointArtifact.path; sha256 = $checkpointArtifact.sha256 })
        }
        $primaryFailure = if ($bridge.PrimaryFailure) { [pscustomobject][ordered]@{ code = [string]$bridge.PrimaryFailure.code; message = [string]$bridge.PrimaryFailure.detail; at = [DateTimeOffset]::UtcNow.ToString('o') } } else { $null }
        $run = [pscustomobject][ordered]@{
            schemaVersion = 1; runId = $runId; caseId = $CaseId; state = [string]$bridge.Status
            startedAt = $startedAt; completedAt = [DateTimeOffset]::UtcNow.ToString('o')
            planFingerprint = (Get-GridBaselineByteSha256 -Bytes ([Text.UTF8Encoding]::new($false)).GetBytes(($InvestigationPlan | ConvertTo-Json -Depth 30 -Compress)))
            resourcePolicyVersion = $ResourcePolicyVersion; gates = $gates; sufficiency = $sufficiency
            checkpoints = $checkpointRefs.ToArray()
            primaryFailure = $primaryFailure; secondaryFailures = @()
        }
        Seal-GridCaseStoreRun -Transaction $transaction -Run $run | Out-Null
        $semanticSource = ($InvestigationPlan | ConvertTo-Json -Depth 30 -Compress) + '|' + $collectorOutputHash
        $semanticFingerprint = Get-GridBaselineByteSha256 -Bytes ([Text.UTF8Encoding]::new($false)).GetBytes($semanticSource)
        $integrityDiagnosis = $null
        if ($normalized -and $normalized.PluginScriptInspection -and $normalized.PluginAssetInspection -and $bridge.Summary.environmentFingerprint) {
            $integrityDiagnosis = Resolve-GridSkyrimBaselineIntegrityDiagnosis -CaseId $CaseId `
                -ContextFingerprint ([string]$bridge.Summary.environmentFingerprint) `
                -Inspection $normalized.PluginScriptInspection `
                -Dependencies @($normalized.PluginScriptDependencies) `
                -AssetInspection $normalized.PluginAssetInspection `
                -AssetDependencies @($normalized.PluginAssetDependencies) `
                -SpidInspection $normalized.SpidInspection `
                -SpidSourceIssues @($normalized.SpidSourceIssues) `
                -CrashSignatures $crashSignatures `
                -SksePluginLoad $sksePluginLoad
            $componentRecoveryDependencies = @($normalized.PluginScriptDependencies) + @($normalized.PluginAssetDependencies)
            $componentRecovery = New-GridSkyrimComponentRecoveryPlan -CaseId $CaseId `
                -ContextFingerprint ([string]$bridge.Summary.environmentFingerprint) `
                -Dependencies $componentRecoveryDependencies `
                -ModMetadata @($normalized.Metadata) `
                -SourceArchives @($normalized.SourceArchives) `
                -SourceArchiveSidecars @($normalized.SourceArchiveSidecars) `
                -ArchiveInspections @($(if ($archiveInspection) { $archiveInspection.Records } else { @() })) `
                -ArchiveCandidateAssessments @($(if ($archiveInspection) { $archiveInspection.Assessments } else { @() }))
            Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'evidence/baseline-integrity-evidence.v1.json' -Value ([pscustomobject][ordered]@{
                schemaVersion = 1; records = @($integrityDiagnosis.Evidence)
            }) | Out-Null
            Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'evidence/plugin-script-dependency-evidence.v1.json' -Value ([pscustomobject][ordered]@{
                schemaVersion = 1; records = @($integrityDiagnosis.Evidence | Where-Object parameter -eq 'missingAttachedPapyrusDependency')
                note = 'Compatibility view containing only attached-Papyrus dependency evidence; the full baseline-integrity evidence set is stored separately.'
            }) | Out-Null
            Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'diagnosis/diagnostic-result.v1.json' -Value $integrityDiagnosis.DiagnosticResult | Out-Null
            Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'repair/component-recovery-plan.v1.json' -Value $componentRecovery | Out-Null
        }
        $stage = 'SealCase'
        $sealed = Seal-GridDiagnosticCase -Transaction $transaction -SemanticBaselineFingerprint $semanticFingerprint -Runs @($run)
        $result = [pscustomobject][ordered]@{
            Tool = 'Grid.Health.Skyrim.Baseline'; Status = [string]$bridge.Status; TerminalState = if ($bridge.Status -eq 'Completed') { 'BaselineComplete' } else { 'BaselineFailed' }; CaseId = $CaseId
            CaseDirectory = $sealed.CaseDirectory; Manifest = $sealed.Manifest; Summary = $bridge.Summary
            Gates = $gates; Sufficiency = $sufficiency; NonMutation = if ($drift.Count -eq 0) { 'NoChangeObserved' } else { 'DriftObserved' }
            DiagnosticResult = if ($integrityDiagnosis) { $integrityDiagnosis.DiagnosticResult } else { $null }
            Evidence = if ($integrityDiagnosis) { @($integrityDiagnosis.Evidence) } else { @() }
            PrimaryFailure = $primaryFailure; Detail = if ($finalization) { 'The sealed collector output was finalized without relaunching Grid.Diagnostics, MO2, xEdit, or the game.' } else { 'The bounded Grid.Diagnostics baseline collector completed without launching MO2, xEdit, or the game.' }
        }
    }
    catch {
        $message = $_.Exception.Message + " (stage: $stage; at: $($_.InvocationInfo.ScriptLineNumber))"
        $code = if ($message -match '^([A-Za-z]+(?:[A-Za-z]+)?):') { $matches[1] } else { 'UnexpectedFailure' }
        $sealedFailure = $null
        if ($transaction -and [string]$transaction.State -eq 'Open' -and (Get-Command Seal-GridCaseStoreRun -ErrorAction SilentlyContinue)) {
            try {
                $failure = [pscustomobject][ordered]@{ code = $code; message = $message; at = [DateTimeOffset]::UtcNow.ToString('o') }
                $failedGates = @($InvestigationPlan.requiredGates | ForEach-Object { [pscustomobject][ordered]@{
                    schemaVersion = 1; gate = [string]$_; status = 'IncompleteRequiredEvidence'; required = $true
                    evidenceIds = @(); unavailableReasons = @($message); contradictions = @(); updatedAt = [DateTimeOffset]::UtcNow.ToString('o')
                } })
                $failedSufficiency = [pscustomobject][ordered]@{
                    schemaVersion = 1; caseId = $CaseId; status = 'InsufficientForRequestedDiagnosis'; evaluatedAt = [DateTimeOffset]::UtcNow.ToString('o')
                    requiredGates = @($InvestigationPlan.requiredGates); incompleteGates = @($InvestigationPlan.requiredGates); contradictions = @(); nextRequiredEvidence = @('Retry the exact failed primitive after applying the recorded observational recovery guidance.')
                }
                $failureRun = [pscustomobject][ordered]@{
                    schemaVersion = 1; runId = $runId; caseId = $CaseId; state = 'Failed'
                    startedAt = $startedAt; completedAt = [DateTimeOffset]::UtcNow.ToString('o')
                    planFingerprint = (Get-GridBaselineByteSha256 -Bytes ([Text.UTF8Encoding]::new($false)).GetBytes(($InvestigationPlan | ConvertTo-Json -Depth 30 -Compress)))
                    resourcePolicyVersion = $ResourcePolicyVersion; gates = $failedGates; sufficiency = $failedSufficiency
                    checkpoints = @(); primaryFailure = $failure; secondaryFailures = @()
                }
                Seal-GridCaseStoreRun -Transaction $transaction -Run $failureRun | Out-Null
                $failureFingerprint = Get-GridBaselineByteSha256 -Bytes ([Text.UTF8Encoding]::new($false)).GetBytes(("FAILED|$CaseId|$code|" + $failureRun.planFingerprint))
                $sealedFailure = Seal-GridDiagnosticCase -Transaction $transaction -SemanticBaselineFingerprint $failureFingerprint -Runs @($failureRun)
            } catch { }
        }
        $result = [pscustomobject][ordered]@{
            Tool = 'Grid.Health.Skyrim.Baseline'; Status = 'Failed'; TerminalState = 'BaselineFailed'; CaseId = $CaseId
            CaseDirectory = if ($sealedFailure) { $sealedFailure.CaseDirectory } else { $null }
            PrimaryFailure = [pscustomobject][ordered]@{ code = $code; detail = $message }
            Detail = 'Baseline collection failed without launching MO2, xEdit, or the game.'
        }
    }
    if ($PassThru) { $result } else { $result }
}
