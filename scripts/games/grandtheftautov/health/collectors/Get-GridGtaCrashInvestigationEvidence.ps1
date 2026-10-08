#requires -Version 5.1
Set-StrictMode -Version Latest

function Get-GridGtaBoundedFileObservation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][ValidateRange(1, 33554432)][long]$MaximumBytes,
        [Parameter(Mandatory)][string]$RootPath
    )
    $root = [IO.Path]::GetFullPath($RootPath).TrimEnd('\')
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -and -not $full.Equals($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'GtaEvidencePathEscaped: a diagnostic artifact escaped its authorized root.'
    }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { return $null }
    $before = Get-Item -LiteralPath $full -Force -ErrorAction Stop
    $hash = $null
    $observationStatus = if ([long]$before.Length -le $MaximumBytes) { 'Hashed' } else { 'MetadataOnlyOversize' }
    if ($observationStatus -eq 'Hashed') {
        $stream = [IO.File]::Open($full, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
        catch [UnauthorizedAccessException] { $observationStatus = 'UnavailableAccessDenied'; $hash = $null }
        catch [IO.IOException] { $observationStatus = 'UnavailableIo'; $hash = $null }
        finally { $sha.Dispose(); $stream.Dispose() }
    }
    $diagnosticLines = @()
    if ($observationStatus -eq 'Hashed' -and [IO.Path]::GetExtension($full) -in @('.log','.txt')) {
        try {
            $diagnosticLines = @(Get-Content -LiteralPath $full -ErrorAction Stop -TotalCount 2048 | ForEach-Object -Begin { $lineNumber = 0 } -Process {
                $lineNumber++
                $line = [string]$_
                if ($line -match '(?i)(token|authorization|cookie|password|secret|email|ticket|session)') { return }
                if ($line -match '^\s*=+[^=].*=+\s*$' -or $line -match '^\s*[A-Za-z][A-Za-z0-9 _/().-]{0,80}\s*:\s*\S' -or
                    $line -match '(?i)\b(error|exception|fatal|crash|fault|failed|warning)\b') {
                    [pscustomobject][ordered]@{ lineNumber = $lineNumber; text = if ($line.Length -gt 1024) { $line.Substring(0,1024) } else { $line } }
                }
            } | Select-Object -First 128)
        }
        catch [UnauthorizedAccessException] { $observationStatus = 'UnavailableAccessDenied'; $diagnosticLines = @() }
        catch [IO.IOException] { $observationStatus = 'UnavailableIo'; $diagnosticLines = @() }
    }
    $after = Get-Item -LiteralPath $full -Force -ErrorAction SilentlyContinue
    $stable = $null -ne $after -and [long]$before.Length -eq [long]$after.Length -and $before.LastWriteTimeUtc -eq $after.LastWriteTimeUtc
    [pscustomobject][ordered]@{
        path = $full
        name = $before.Name
        length = [long]$before.Length
        lastWriteTimeUtc = $before.LastWriteTimeUtc.ToString('o')
        sha256 = $hash
        diagnosticLines = $diagnosticLines
        status = if (-not $stable) { 'ChangedDuringRead' } else { $observationStatus }
        stable = [bool]$stable
    }
}

function Get-GridGtaDeclaredArtifactEvidence {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object[]]$Roots)
    $rootResults = @()
    foreach ($root in @($Roots)) {
        $files = @()
        if (Test-Path -LiteralPath $root.path -PathType Container) {
            foreach ($artifact in @($root.artifacts)) {
                if ([string]$artifact.kind -eq 'ProtectedDirectory') {
                    $files += [pscustomobject][ordered]@{
                        artifactId = [string]$artifact.artifactId; path = [string]$artifact.path
                        status = if (Test-Path -LiteralPath $artifact.path -PathType Container) { 'PresentProtected' } else { 'Absent' }
                        stable = $true; length = $null; lastWriteTimeUtc = $null; sha256 = $null
                    }
                    continue
                }
                $leaf = Split-Path -Leaf ([string]$artifact.path)
                $artifactParent = Split-Path -Parent ([string]$artifact.path)
                $matches = if ($leaf.Contains('*') -or $leaf.Contains('?')) {
                    @(Get-ChildItem -LiteralPath $artifactParent -File -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -like $leaf } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First ([int]$root.maxFiles))
                } elseif (Test-Path -LiteralPath $artifact.path -PathType Leaf) { @(Get-Item -LiteralPath $artifact.path -Force) } else { @() }
                foreach ($match in $matches) {
                    $maximumBytes = if ($null -eq $root.maxFileBytes) { 33554432 } else { [long]$root.maxFileBytes }
                    $observation = Get-GridGtaBoundedFileObservation -Path $match.FullName -MaximumBytes $maximumBytes -RootPath ([string]$root.path)
                    if ($null -ne $observation) {
                        $observation | Add-Member -NotePropertyName artifactId -NotePropertyValue ([string]$artifact.artifactId)
                        $files += $observation
                    }
                }
            }
        }
        $rootResults += [pscustomobject][ordered]@{
            rootId = [string]$root.rootId; role = [string]$root.role; path = [string]$root.path
            present = Test-Path -LiteralPath $root.path -PathType Container
            files = @($files)
        }
    }
    @($rootResults)
}

function Invoke-GridGtaOptionalSharedCollector {
    param([Parameter(Mandatory)][string]$CommandName, [Parameter(Mandatory)][hashtable]$Arguments)
    $command = Get-Command -Name $CommandName -CommandType Function -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        return [pscustomobject][ordered]@{ status = 'Unavailable'; reason = "$CommandName is not loaded by the shared health runtime."; evidence = @() }
    }
    $bound = @{}
    foreach ($name in $Arguments.Keys) { if ($command.Parameters.ContainsKey($name)) { $bound[$name] = $Arguments[$name] } }
    try { return & $CommandName @bound }
    catch {
        return [pscustomobject][ordered]@{ status = 'Unavailable'; reason = $_.Exception.Message; evidence = @() }
    }
}

function Add-GridGtaNativeEvidence {
    param(
        [Parameter(Mandatory)][string]$Parameter,
        [Parameter(Mandatory)][string]$Claim,
        [Parameter(Mandatory)][string]$SourceType,
        [string]$SourceIdentifier,
        [Parameter(Mandatory)][string]$ContextFingerprint,
        [Parameter(Mandatory)][ValidateSet('Collected','Verified','Contradicted','Stale','Unverified','Unavailable')][string]$VerificationStatus,
        [Parameter(Mandatory)]$Native
    )
    $value = if ($VerificationStatus -in @('Collected','Verified')) { 1.0 } else { 0.0 }
    $item = New-GridEvidenceItem -Parameter $Parameter -Value $value -Claim $Claim -SourceType $SourceType `
        -SourceIdentifier $SourceIdentifier -ContextFingerprint $ContextFingerprint -VerificationStatus $VerificationStatus `
        -CollectorName 'Grid.GtaV.CrashInvestigation' -CollectorVersion '1.0.0'
    $item | Add-Member -NotePropertyName native -NotePropertyValue $Native
    $item
}

function Get-GridGtaCrashInvestigationEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Context,
        [hashtable]$KnownFolders,
        [datetime]$SinceUtc = ([datetime]::UtcNow.AddDays(-120)),
        $ApplicationFailureEvidence,
        $WindowsErrorReportingEvidence,
        $ProcessModuleEvidence
    )
    if ([string]$Context.status -cne 'Resolved' -or [string]::IsNullOrWhiteSpace([string]$Context.contextFingerprint)) {
        throw 'GtaCrashEvidenceContextInvalid: a resolved, fingerprinted GTA context is required.'
    }
    if ($null -eq $KnownFolders) {
        $KnownFolders = @{
            Documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
            LocalApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
            ProgramData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
        }
    }
    $inventory = Get-GridGtaInstallationInventory -GameRoot ([string]$Context.gameRoot)
    if ([string]$inventory.edition -cne [string]$Context.edition) {
        throw 'GtaCrashEvidenceEditionMismatch: current authorized filesystem evidence no longer matches the account registration.'
    }
    $currentExecutable = @($inventory.components | Where-Object name -eq 'GameExecutable')[0]
    if ($null -eq $currentExecutable -or -not [bool]$currentExecutable.present -or
        -not ([IO.Path]::GetFullPath([string]$currentExecutable.path)).Equals([IO.Path]::GetFullPath([string]$Context.executablePath), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'GtaCrashEvidenceExecutableMismatch: the authorized connected executable could not be revalidated.'
    }
    $executableName = Split-Path -Leaf ([string]$Context.executablePath)
    $commonArguments = @{
        ExecutablePath = [string]$Context.executablePath; ExecutableName = $executableName
        SinceUtc = $SinceUtc.ToUniversalTime(); ContextFingerprint = [string]$Context.contextFingerprint
    }
    if ($null -eq $ApplicationFailureEvidence) {
        $ApplicationFailureEvidence = Invoke-GridGtaOptionalSharedCollector -CommandName 'Get-GridWindowsApplicationFailureEvidence' -Arguments $commonArguments
    }
    if ($null -eq $WindowsErrorReportingEvidence) {
        $werArguments = @{} + $commonArguments
        if ($KnownFolders.ContainsKey('ProgramData')) {
            $werArguments.ArchiveRoot = @(
                (Join-Path ([string]$KnownFolders.ProgramData) 'Microsoft\Windows\WER\ReportArchive'),
                (Join-Path ([string]$KnownFolders.ProgramData) 'Microsoft\Windows\WER\ReportQueue')
            )
        }
        $WindowsErrorReportingEvidence = Invoke-GridGtaOptionalSharedCollector -CommandName 'Get-GridWindowsErrorReportingEvidence' -Arguments $werArguments
    }
    if ($null -eq $ProcessModuleEvidence) {
        $ProcessModuleEvidence = Invoke-GridGtaOptionalSharedCollector -CommandName 'Get-GridWindowsProcessModuleEvidence' -Arguments $commonArguments
    }

    $declaredArtifacts = @(Get-GridGtaDeclaredArtifactEvidence -Roots @($Context.userDataRoots))
    $evidence = @()
    $executableComponent = $currentExecutable
    $evidence += Add-GridGtaNativeEvidence -Parameter 'gta.installation.identity' `
        -Claim "The connected $($Context.edition) installation executable was observed at its account-registered path." `
        -SourceType 'FileSystemObservation' -SourceIdentifier ([string]$Context.executablePath) -ContextFingerprint ([string]$Context.contextFingerprint) `
        -VerificationStatus 'Verified' -Native ([pscustomobject][ordered]@{ gameId = $Context.gameId; installationId = $Context.installationId; profileId = $Context.profileId; edition = $Context.edition; executable = $executableComponent })
    if ($inventory.steam.present) {
        $evidence += Add-GridGtaNativeEvidence -Parameter 'gta.provider.manifest' -Claim 'The edition-specific Steam application manifest was observed and hashed.' `
            -SourceType 'ProviderManifest' -SourceIdentifier ([string]$inventory.steam.path) -ContextFingerprint ([string]$Context.contextFingerprint) `
            -VerificationStatus 'Verified' -Native $inventory.steam
    }
    foreach ($root in $declaredArtifacts) {
        $usable = @($root.files | Where-Object { $_.status -in @('Hashed','MetadataOnlyOversize','PresentProtected') -and $_.stable })
        $unavailable = @($root.files | Where-Object { $_.status -notin @('Hashed','MetadataOnlyOversize','PresentProtected') -or -not $_.stable })
        $verification = if (-not $root.present) { 'Unavailable' } elseif ($unavailable.Count -gt 0) { 'Unverified' } else { 'Collected' }
        $evidence += Add-GridGtaNativeEvidence -Parameter ('gta.userdata.' + $root.rootId) `
            -Claim $(if (-not $root.present) { "The declared $($root.rootId) root was not present." } else { "GRID observed $($usable.Count) stable bounded artifact(s) beneath $($root.rootId)." }) `
            -SourceType 'DeclaredGameUserDataRoot' -SourceIdentifier ([string]$root.path) -ContextFingerprint ([string]$Context.contextFingerprint) `
            -VerificationStatus $verification -Native $root
    }
    foreach ($source in @(
        @('gta.windows.application-failures','WindowsApplicationFailureEvidence',$ApplicationFailureEvidence),
        @('gta.windows.error-reporting','WindowsErrorReportingEvidence',$WindowsErrorReportingEvidence),
        @('gta.runtime.modules','WindowsProcessModuleEvidence',$ProcessModuleEvidence))) {
        $sourceObject = $source[2]
        $statusValue = if ($null -ne $sourceObject.PSObject.Properties['status']) { [string]$sourceObject.status } elseif ($null -ne $sourceObject.PSObject.Properties['Status']) { [string]$sourceObject.Status } else { 'Collected' }
        $verification = if ($statusValue -in @('Collected','Verified','Complete','Available')) { 'Collected' } elseif ($statusValue -in @('Stale','ChangedDuringRead')) { 'Stale' } else { 'Unavailable' }
        $evidence += Add-GridGtaNativeEvidence -Parameter ([string]$source[0]) -Claim $(if ($verification -eq 'Collected') { "$($source[1]) returned bounded evidence for the connected executable." } else { "$($source[1]) was unavailable; no negative inference was made." }) `
            -SourceType ([string]$source[1]) -SourceIdentifier $executableName -ContextFingerprint ([string]$Context.contextFingerprint) `
            -VerificationStatus $verification -Native $sourceObject
        foreach ($sharedItem in @($sourceObject.evidence)) {
            if ([string]$sharedItem.contextFingerprint -cne [string]$Context.contextFingerprint) {
                throw 'GtaCrashEvidenceContextMismatch: shared Windows evidence was collected for another context.'
            }
            $sharedValidation = Test-GridEvidenceItem -Evidence $sharedItem
            if (-not $sharedValidation.IsValid) { throw ('GtaCrashEvidenceInvalid: ' + ($sharedValidation.Errors -join ' ')) }
            $evidence += $sharedItem
        }
    }
    $verifiedCount = @($evidence | Where-Object verificationStatus -in @('Collected','Verified')).Count
    [pscustomobject][ordered]@{
        schemaVersion = 1
        gameId = [string]$Context.gameId
        installationId = [string]$Context.installationId
        profileId = [string]$Context.profileId
        edition = [string]$Context.edition
        contextFingerprint = [string]$Context.contextFingerprint
        collectedAtUtc = [datetime]::UtcNow.ToString('o')
        status = if ($verifiedCount -eq 0) { 'Unavailable' } elseif (@($evidence | Where-Object verificationStatus -in @('Unavailable','Stale','Unverified')).Count -gt 0) { 'Partial' } else { 'Collected' }
        sinceUtc = $SinceUtc.ToUniversalTime().ToString('o')
        inventory = $inventory
        declaredArtifacts = $declaredArtifacts
        windowsApplicationFailures = $ApplicationFailureEvidence
        windowsErrorReporting = $WindowsErrorReportingEvidence
        runtimeModules = $ProcessModuleEvidence
        evidence = @($evidence)
        changedExternalState = $false
    }
}
