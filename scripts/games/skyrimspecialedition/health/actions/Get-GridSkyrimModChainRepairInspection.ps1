#requires -Version 5.1

<#
.SYNOPSIS
Validates a sealed Skyrim baseline and inspects exact evidence-bound repair inputs.
.DESCRIPTION
This collector is read-only. It never discovers targets by name and accepts only
the paths and evidence identifiers already present in a sealed baseline or plan.
#>

function Get-GridRepairSha256 {
    param([Parameter(Mandatory)][string]$LiteralPath)
    (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256 -ErrorAction Stop).Hash.ToUpperInvariant()
}

function Get-GridRootCausePatchSpecificationHash {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Specification)
    $unsigned = [pscustomobject][ordered]@{
        schemaVersion = [int]$Specification.schemaVersion; caseId = [string]$Specification.caseId
        status = [string]$Specification.status; solutionKind = [string]$Specification.solutionKind
        targetPluginName = $Specification.targetPluginName; records = @($Specification.records)
        assetActions = @($Specification.assetActions); evidenceFingerprint = [string]$Specification.evidenceFingerprint
        supportingEvidenceIds = @($Specification.supportingEvidenceIds); proposalId = [string]$Specification.proposalId
        writer = $Specification.writer; exclusions = @($Specification.exclusions)
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($unsigned | ConvertTo-Json -Depth 30 -Compress))
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace('-', '') } finally { $algorithm.Dispose() }
}

function Get-GridRepairTreeObservation {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$LiteralPath)
    $root = [IO.Path]::GetFullPath($LiteralPath).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Required tree is missing: $root" }
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $fileCount = 0L
    try {
        $pending = New-Object Collections.Generic.Stack[string]
        $pending.Push($root)
        while ($pending.Count -gt 0) {
            $entries = @(Get-ChildItem -LiteralPath $pending.Pop() -Force -ErrorAction Stop)
            foreach ($entry in $entries) {
                if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "ReparsePointRefused: $($entry.FullName)" }
            }
            foreach ($item in @($entries | Where-Object { -not $_.PSIsContainer } | Sort-Object Name)) {
                $relative = $item.FullName.Substring($root.Length + 1).Replace('\', '/')
                $beforeLength = [long]$item.Length
                $beforeWrite = $item.LastWriteTimeUtc.Ticks
                $sha = Get-GridRepairSha256 -LiteralPath $item.FullName
                $after = Get-Item -LiteralPath $item.FullName -Force -ErrorAction Stop
                if ($beforeLength -ne [long]$after.Length -or $beforeWrite -ne $after.LastWriteTimeUtc.Ticks) { throw "ChangedDuringRead: $($item.FullName)" }
                $record = [pscustomobject][ordered]@{ path = $relative; sizeBytes = $beforeLength; sha256 = $sha }
                $line = $record | ConvertTo-Json -Depth 5 -Compress
                $bytes = [Text.Encoding]::UTF8.GetBytes($(if ($fileCount -eq 0) { $line } else { "`n$line" }))
                [void]$algorithm.TransformBlock($bytes, 0, $bytes.Length, $bytes, 0)
                $fileCount = 1L + $fileCount
                if (($fileCount % 5000L) -eq 0) {
                    [GC]::Collect()
                    [GC]::WaitForPendingFinalizers()
                    [GC]::Collect()
                }
            }
            foreach ($directory in @($entries | Where-Object { $_.PSIsContainer } | Sort-Object Name -Descending)) {
                $pending.Push($directory.FullName)
            }
        }
        [void]$algorithm.TransformFinalBlock((New-Object byte[] 0), 0, 0)
        $treeHash = ([BitConverter]::ToString($algorithm.Hash)).Replace('-', '')
    }
    finally { $algorithm.Dispose() }
    [pscustomobject][ordered]@{ root = $root; treeSha256 = $treeHash; fileCount = $fileCount; files = @() }
}

function Test-GridRepairPathBoundary {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$LiteralPath,
        [Parameter(Mandatory)][ValidateSet('ReadRoot','MutationRoot')][string]$Role,
        [switch]$MustExist
    )
    if (-not [IO.Path]::IsPathRooted($LiteralPath)) { throw "$Role path must be absolute: $LiteralPath" }
    $full = [IO.Path]::GetFullPath($LiteralPath).TrimEnd('\')
    if ($full.StartsWith('\\') -or $full.StartsWith('\\?\') -or $full.StartsWith('\\.\')) { throw "NetworkOrDevicePathRefused: $full" }
    $root = [IO.Path]::GetPathRoot($full)
    $drive = New-Object IO.DriveInfo($root)
    if ($drive.DriveType -notin @([IO.DriveType]::Fixed, [IO.DriveType]::Ram)) { throw "UnsupportedVolumeType: $($drive.DriveType) for $full" }
    if ($MustExist -and -not (Test-Path -LiteralPath $full)) { throw "Required path is missing: $full" }
    $existingAncestor = $full
    while (-not (Test-Path -LiteralPath $existingAncestor)) {
        $parent = Split-Path -Parent $existingAncestor
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $existingAncestor) { throw "No existing path ancestor is available: $full" }
        $existingAncestor = $parent
    }
    $cursor = Get-Item -LiteralPath $existingAncestor -Force
    while ($cursor) {
        if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "ReparsePointRefused: $($cursor.FullName)" }
        if ($cursor -is [IO.DirectoryInfo]) { $cursor = $cursor.Parent } else { $cursor = $cursor.Directory }
    }
    $full
}

function Add-GridSkyrimArtifactToQuarantine {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Local')][string]$SourceLiteralPath,
        [Parameter(Mandatory, ParameterSetName = 'Remote')][uri]$SourceUri,
        [Parameter(Mandatory)][string]$QuarantineRoot,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedSha256,
        [Parameter(Mandatory)][long]$ExpectedSizeBytes,
        [Parameter(Mandatory)][string[]]$IdentityEvidenceIds,
        [Parameter(Mandatory, ParameterSetName = 'Remote')][string[]]$AllowedProviderHosts,
        [ValidateRange(1, 5)][int]$MaximumRedirects = 5,
        [ValidateRange(1, 120)][int]$TimeoutSeconds = 20,
        [ValidateRange(1, 2147483647)][long]$MaximumBytes = 268435456,
        [Parameter(DontShow, ParameterSetName = 'Remote')][switch]$AllowHttpForTesting,
        [switch]$PassThru
    )
    if (@($IdentityEvidenceIds).Count -eq 0) { throw 'Artifact quarantine requires baseline identity evidence.' }
    $quarantine = Test-GridRepairPathBoundary -LiteralPath $QuarantineRoot -Role MutationRoot
    if ($ExpectedSizeBytes -lt 0 -or $ExpectedSizeBytes -gt $MaximumBytes) { throw 'ArtifactSizeRefused: evidence-bound size exceeds the configured bound.' }
    if (-not (Test-Path -LiteralPath $quarantine -PathType Container) -and $PSCmdlet.ShouldProcess($quarantine, 'Create quarantine root')) {
        New-Item -ItemType Directory -Path $quarantine -ErrorAction Stop | Out-Null
    }
    $incoming = Join-Path $quarantine ('incoming-' + [guid]::NewGuid().ToString('N'))
    $requestedUri = $null
    $finalUri = $null
    $redirects = @()
    $headers = [ordered]@{}
    $source = $null
    if ($PSCmdlet.ParameterSetName -eq 'Local') {
        $source = Test-GridRepairPathBoundary -LiteralPath $SourceLiteralPath -Role ReadRoot -MustExist
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw 'Artifact source must be a regular file.' }
        $item = Get-Item -LiteralPath $source
        if ([long]$item.Length -ne $ExpectedSizeBytes) { throw 'ArtifactSizeMismatch: local artifact does not match evidence-bound size.' }
        if ($PSCmdlet.ShouldProcess($incoming, 'Copy evidence-bound local artifact into quarantine')) {
            $input = [IO.File]::Open($source, 'Open', 'Read', 'Read')
            $output = [IO.File]::Open($incoming, 'CreateNew', 'Write', 'None')
            try { $input.CopyTo($output, 65536); $output.Flush() } finally { $output.Dispose(); $input.Dispose() }
        }
    }
    else {
        $requestedUri = $SourceUri.AbsoluteUri
        $current = $SourceUri
        for ($hop = 0; $hop -le $MaximumRedirects; $hop++) {
            if ($current.Scheme -cne 'https' -and -not ($AllowHttpForTesting -and $current.Scheme -ceq 'http')) { throw 'NetworkPolicyRefused: only unauthenticated HTTPS is allowed.' }
            if ([string]::IsNullOrWhiteSpace($current.Host) -or $current.UserInfo) { throw 'NetworkPolicyRefused: credentials and ambiguous hosts are prohibited.' }
            if ($current.Host -notin $AllowedProviderHosts) { throw "ProviderNotAllowed: $($current.Host)" }
            $request = [Net.HttpWebRequest]::Create($current)
            $request.Method = 'GET'; $request.AllowAutoRedirect = $false; $request.Timeout = $TimeoutSeconds * 1000
            $request.ReadWriteTimeout = $TimeoutSeconds * 1000; $request.MaximumResponseHeadersLength = 64
            $request.Credentials = $null; $request.UseDefaultCredentials = $false; $request.PreAuthenticate = $false
            $response = $request.GetResponse()
            try {
                $code = [int]$response.StatusCode
                if ($code -in @(301, 302, 303, 307, 308)) {
                    if ($hop -eq $MaximumRedirects) { throw 'RedirectLimitExceeded: artifact acquisition exceeded the bounded redirect count.' }
                    $location = [string]$response.Headers['Location']
                    if ([string]::IsNullOrWhiteSpace($location)) { throw 'RedirectInvalid: Location is missing.' }
                    $next = [uri]::new($current, $location)
                    $redirects += [pscustomobject][ordered]@{ from = $current.AbsoluteUri; to = $next.AbsoluteUri; status = $code }
                    $current = $next
                    continue
                }
                if ($code -lt 200 -or $code -ge 300) { throw "ArtifactRequestFailed: HTTP $code" }
                $finalUri = $current.AbsoluteUri
                foreach ($key in @('Content-Type','Content-Length','ETag','Last-Modified')) { if ($response.Headers[$key]) { $headers[$key] = [string]$response.Headers[$key] } }
                if ($response.ContentLength -gt $MaximumBytes -or ($response.ContentLength -ge 0 -and $response.ContentLength -ne $ExpectedSizeBytes)) { throw 'ArtifactSizeMismatch: response size differs from evidence.' }
                if ($PSCmdlet.ShouldProcess($incoming, 'Acquire exact evidence-bound artifact into quarantine')) {
                    $input = $response.GetResponseStream()
                    $output = [IO.File]::Open($incoming, 'CreateNew', 'Write', 'None')
                    $buffer = New-Object byte[] 65536
                    $total = 0L
                    try {
                        while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                            $total += $read
                            if ($total -gt $MaximumBytes) { throw 'ArtifactSizeRefused: response exceeded the configured byte limit.' }
                            $output.Write($buffer, 0, $read)
                        }
                        $output.Flush()
                    } finally { $output.Dispose(); $input.Dispose() }
                    if ($total -ne $ExpectedSizeBytes) { throw 'ArtifactSizeMismatch: downloaded bytes differ from evidence.' }
                }
                break
            } finally { $response.Dispose() }
        }
    }
    if (-not (Test-Path -LiteralPath $incoming -PathType Leaf)) {
        $result = [pscustomobject][ordered]@{ schemaVersion = 1; artifactId = 'whatif'; path = $incoming; status = 'IdentityUnresolved'; sizeBytes = $ExpectedSizeBytes; sha256 = $null; identityEvidenceIds = @($IdentityEvidenceIds | Sort-Object -Unique); observedAt = [DateTimeOffset]::UtcNow.ToString('o') }
        if ($PassThru) { return $result } else { return ($result | ConvertTo-Json -Depth 8) }
    }
    $actual = Get-GridRepairSha256 -LiteralPath $incoming
    $matches = $actual -ceq $ExpectedSha256.ToUpperInvariant()
    $destination = Join-Path (Join-Path $quarantine $actual.Substring(0, 2)) $actual
    if (-not $matches) {
        $rejectedRoot = Join-Path $quarantine 'rejected'
        if (-not (Test-Path -LiteralPath $rejectedRoot)) { New-Item -ItemType Directory -Path $rejectedRoot -ErrorAction Stop | Out-Null }
        $destination = Join-Path $rejectedRoot ([guid]::NewGuid().ToString('N') + '.artifact')
        Move-Item -LiteralPath $incoming -Destination $destination -ErrorAction Stop
        $status = 'Rejected'
    }
    elseif (Test-Path -LiteralPath $destination -PathType Leaf) {
        if ((Get-GridRepairSha256 -LiteralPath $destination) -cne $actual) { throw 'QuarantineCollision: existing artifact has an invalid digest.' }
        Remove-Item -LiteralPath $incoming -Force -ErrorAction Stop
        $status = 'Verified'
    }
    else {
        $parent = Split-Path -Parent $destination
        if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Path $parent -ErrorAction Stop | Out-Null }
        Move-Item -LiteralPath $incoming -Destination $destination -ErrorAction Stop
        if ((Get-GridRepairSha256 -LiteralPath $destination) -cne $actual) { throw 'QuarantineVerificationFailed: copied artifact digest differs.' }
        $status = 'Verified'
    }
    $result = [pscustomobject][ordered]@{ schemaVersion = 1; artifactId = "sha256:$actual"; path = $destination; status = $status; sizeBytes = [long](Get-Item -LiteralPath $destination).Length; sha256 = $actual; identityEvidenceIds = @($IdentityEvidenceIds | Sort-Object -Unique); observedAt = [DateTimeOffset]::UtcNow.ToString('o'); provenance = [pscustomobject][ordered]@{ requestedUri = $requestedUri; finalUri = $finalUri; redirects = @($redirects); responseHeaders = [pscustomobject]$headers; localSourcePath = $source } }
    Write-GridJsonAtomic -InputObject $result -LiteralPath ($destination + '.receipt.v1.json')
    if ($PassThru) { $result } else { $result | ConvertTo-Json -Depth 6 }
}

function Get-GridSkyrimModChainRepairInspection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseStoreRoot,
        [Parameter(Mandatory)][string]$BaselineCaseDirectory,
        [string]$DiagnosisCaseDirectory,
        [object[]]$Components = @(),
        [object[]]$CurrentProtectedState = @(),
        [switch]$PassThru
    )
    if (-not (Get-Command Test-GridDiagnosticCaseSeal -ErrorAction SilentlyContinue)) { throw 'The shared Grid health module must be imported.' }
    $seal = Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory $BaselineCaseDirectory
    if (-not $seal.IsValid) { throw ('BaselineSealInvalid: ' + ($seal.Errors -join '; ')) }
    $failures = New-Object Collections.Generic.List[object]
    $diagnosisBinding = $null

    if (-not [string]::IsNullOrWhiteSpace($DiagnosisCaseDirectory)) {
        $diagnosisSeal = Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory $DiagnosisCaseDirectory
        if (-not $diagnosisSeal.IsValid) { throw ('DiagnosisSealInvalid: ' + ($diagnosisSeal.Errors -join '; ')) }
        $diagnosisRoot = [IO.Path]::GetFullPath($DiagnosisCaseDirectory)
        $diagnosisCasePath = Join-Path $diagnosisRoot 'case.json'
        $patchPath = Join-Path $diagnosisRoot 'patch\conflict-patch-specification.v1.json'
        $requestPath = Join-Path $diagnosisRoot 'collector\root-cause-request.v1.json'
        if (-not (Test-Path -LiteralPath $diagnosisCasePath -PathType Leaf) -or -not (Test-Path -LiteralPath $patchPath -PathType Leaf) -or -not (Test-Path -LiteralPath $requestPath -PathType Leaf)) { throw 'DiagnosisArtifactMissing: sealed diagnosis lacks case.json, its collector request, or its patch specification.' }
        $diagnosisCase = Get-Content -LiteralPath $diagnosisCasePath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        $diagnosisRequest = Get-Content -LiteralPath $requestPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        $patchSpecification = Get-Content -LiteralPath $patchPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        if ([string]$diagnosisCase.status -cne 'RootCauseResolved') { throw 'DiagnosisNotResolved: repair planning requires RootCauseResolved.' }
        $baselineManifestFileSha256 = Get-GridRepairSha256 -LiteralPath (Join-Path ([IO.Path]::GetFullPath($BaselineCaseDirectory)) 'case-manifest.v1.json')
        if ([string]$diagnosisRequest.baselineManifestSha256 -cne $baselineManifestFileSha256 -or [string]$diagnosisRequest.caseId -cne [string]$diagnosisSeal.Manifest.caseId -or [string]$patchSpecification.caseId -cne [string]$diagnosisSeal.Manifest.caseId) { throw 'DiagnosisBaselineMismatch: diagnosis is not bound to this exact baseline and case identity.' }
        if ([string]$patchSpecification.solutionKind -cne 'InstallationAssetRepair' -or $null -ne $patchSpecification.targetPluginName -or @($patchSpecification.records).Count -ne 0 -or @($patchSpecification.assetActions).Count -eq 0) {
            throw 'DiagnosisRepairUnsupported: this workflow accepts only an asset-only diagnosis with no target plugin or record edits.'
        }
        if ((Get-GridRootCausePatchSpecificationHash -Specification $patchSpecification) -cne [string]$patchSpecification.specificationSha256) { throw 'DiagnosisSpecificationDigestMismatch: sealed patch specification content is inconsistent.' }
        $diagnosisBinding = [pscustomobject][ordered]@{
            caseDirectory = $diagnosisRoot; caseId = [string]$diagnosisSeal.Manifest.caseId
            manifestSha256 = [string]$diagnosisSeal.Manifest.manifestSha256
            manifestFileSha256 = Get-GridRepairSha256 -LiteralPath (Join-Path $diagnosisRoot 'case-manifest.v1.json')
            specificationSha256 = [string]$patchSpecification.specificationSha256
            specificationFileSha256 = Get-GridRepairSha256 -LiteralPath $patchPath
            evidenceFingerprint = [string]$patchSpecification.evidenceFingerprint
            solutionKind = 'InstallationAssetRepair'; assetActions = @($patchSpecification.assetActions | Sort-Object virtualPath, providerName)
        }
    }

    function Read-CaseJson([string]$RelativePath) {
        $path = Join-Path ([IO.Path]::GetFullPath($BaselineCaseDirectory)) $RelativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "BaselineArtifactMissing: $RelativePath" }
        Get-Content -LiteralPath $path -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    }
    function Read-CaseNdjson([string]$RelativePath) {
        $path = Join-Path ([IO.Path]::GetFullPath($BaselineCaseDirectory)) $RelativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "BaselineArtifactMissing: $RelativePath" }
        @($result = Get-Content -LiteralPath $path -ReadCount 1 -ErrorAction Stop | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_ | ConvertFrom-Json -ErrorAction Stop }; $result)
    }
    function Get-CaseVirtualProviderSummary([string]$RelativePath, [string[]]$ProviderNames) {
        $path = Join-Path ([IO.Path]::GetFullPath($BaselineCaseDirectory)) $RelativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "BaselineArtifactMissing: $RelativePath" }
        $artifact = @($seal.Manifest.artifacts | Where-Object { [string]$_.path -ceq $RelativePath.Replace('\','/') })
        if ($artifact.Count -ne 1) { throw "BaselineArtifactMissing: sealed manifest entry for $RelativePath" }
        @($ProviderNames | Sort-Object -Unique | ForEach-Object {
            [pscustomobject][ordered]@{
                providerName = [string]$_; verification = 'SealedArtifactDigest'
                inventoryPath = $RelativePath.Replace('\','/'); inventorySha256 = [string]$artifact[0].sha256
            }
        })
    }
    function Value-Of($Value) {
        if ($null -eq $Value) { return $null }
        if ($Value -is [string]) { return [string]$Value }
        foreach ($name in @('value','Value','name','Name')) {
            $property = $Value.PSObject.Properties[$name]
            if ($property -and $null -ne $property.Value) { return [string]$property.Value }
        }
        [string]$Value
    }
    function Optional-Of($Value, [string]$Name) {
        if ($null -eq $Value) { return $null }
        $property = $Value.PSObject.Properties[$Name]
        if ($null -eq $property) { return $null }
        $property.Value
    }
    function New-InspectionFailure([string]$Code, [string]$Primitive, [string[]]$Resources, [string]$Expected, [string]$Observed, [bool]$Trustworthy = $true) {
        [pscustomobject][ordered]@{
            schemaVersion = 1; code = $Code; failedPrimitive = $Primitive; affectedResources = @($Resources)
            expectedState = $Expected; observedState = $Observed; lastVerifiedJournalEntry = $null; currentHashes = @()
            boundedRecoveryAttempted = @('Validated the exact sealed baseline artifact once')
            recoveryChoices = @('Refresh only the stale or incomplete baseline partition and reseal the baseline')
            externalStateTrustworthy = $Trustworthy
        }
    }

    $plan = Read-CaseJson 'investigation-plan.json'
    if ([int]$plan.schemaVersion -ne 2 -or [string]$plan.purpose -ne 'Baseline' -or [string]$plan.caseId -cne [string]$seal.Manifest.caseId) {
        throw 'BaselinePlanInvalid: the sealed case does not contain its matching Baseline InvestigationPlan v2.'
    }
    $requiredCapabilities = @('grid.game.skyrimspecialedition.baseline.collect', 'grid.game.skyrimspecialedition.mod-chain-repair.inspect')
    $registry = @(Get-GridCapabilityRegistry -ScriptsRoot $scriptsRoot)
    foreach ($capabilityId in $requiredCapabilities) {
        $binding = @($plan.capabilities | Where-Object { [string]$_.capabilityId -ceq $capabilityId })
        if ($binding.Count -ne 1) { throw "BaselineCapabilityIncompatible: plan does not bind $capabilityId exactly once." }
        $contract = Resolve-GridCapabilityContract -Registry $registry -CapabilityId $capabilityId
        if ([string]$binding[0].capabilityVersion -cne [string]$contract.capabilityVersion) { throw "BaselineCapabilityIncompatible: $capabilityId version is stale." }
    }
    $runs = @($seal.Manifest.runs | ForEach-Object { Read-CaseJson ([string]$_.path) })
    $run = @($runs | Where-Object { [string]$_.state -eq 'Completed' } | Sort-Object { [DateTimeOffset]$_.completedAt } -Descending | Select-Object -First 1)
    if ($run.Count -ne 1) { throw 'BaselineIncomplete: the sealed case has no completed collection run.' }
    if ([string]$run[0].sufficiency.status -ne 'SufficientForRequestedDiagnosis') {
        $failures.Add((New-InspectionFailure 'BaselineInsufficient' 'ValidateBaselineSufficiency' @($BaselineCaseDirectory) 'SufficientForRequestedDiagnosis' ([string]$run[0].sufficiency.status)))
    }
    $acceptableGateStates = @('Complete','CompleteWithUnavailableOptionalEvidence','NotApplicable')
    foreach ($requiredGate in @($plan.requiredGates | Sort-Object -Unique)) {
        $gate = @($run[0].gates | Where-Object { [string]$_.gate -ceq [string]$requiredGate })
        if ($gate.Count -ne 1 -or [string]$gate[0].status -notin $acceptableGateStates) {
            $observed = if ($gate.Count -eq 1) { [string]$gate[0].status } else { "count=$($gate.Count)" }
            $failures.Add((New-InspectionFailure 'BaselineGateIncomplete' 'ValidateRequiredEvidenceGate' @([string]$requiredGate) ($acceptableGateStates -join '|') $observed))
        }
    }

    $installation = Read-CaseJson 'installation/installation-baseline.v1.json'
    if ([string]$installation.installationId -cne [string]$plan.installationId -or [string]$installation.profileId -cne [string]$plan.profileId) {
        throw 'BaselineContextMismatch: installation/profile identity differs from the sealed InvestigationPlan.'
    }
    $protectedBefore = Read-CaseJson 'snapshots/protected-before.v1.json'
    $protectedAfter = Read-CaseJson 'snapshots/protected-after.v1.json'
    if ([string]$protectedBefore.installationId -cne [string]$plan.installationId -or [string]$protectedAfter.profileId -cne [string]$plan.profileId) {
        throw 'BaselineContextMismatch: protected snapshots do not match the sealed InvestigationPlan.'
    }
    $afterByPath = @{}
    foreach ($file in @($protectedAfter.files)) { $afterByPath[[string]$file.path] = $file }
    $protectedEvidence = @($protectedBefore.files | Sort-Object path)
    foreach ($protected in $protectedEvidence) {
        try {
            $path = Test-GridRepairPathBoundary -LiteralPath ([string]$protected.path) -Role ReadRoot -MustExist
            $actual = Get-GridRepairSha256 -LiteralPath $path
            $after = $afterByPath[[string]$protected.path]
            if ($null -eq $after -or [string]$after.sha256 -cne [string]$protected.sha256 -or $actual -cne ([string]$protected.sha256).ToUpperInvariant()) { throw "ProtectedHashChanged: $path" }
        }
        catch { $failures.Add((New-InspectionFailure 'BaselineStale' 'RehashProtectedState' @([string]$protected.path) ([string]$protected.sha256) $_.Exception.Message $false)) }
    }

    # Caller-supplied hashes may only tighten the sealed snapshot; they cannot replace it.
    foreach ($protected in @($CurrentProtectedState | Sort-Object path)) {
        $sealedProtected = @($protectedEvidence | Where-Object { [string]$_.path -ieq [string]$protected.path })
        if ($sealedProtected.Count -ne 1 -or [string]$sealedProtected[0].sha256 -cne ([string]$protected.sha256).ToUpperInvariant()) {
            throw 'ProtectedStateNotEvidenceBound: caller state is absent from or differs from the sealed snapshot.'
        }
    }

    $mods = @(Read-CaseNdjson 'inventory/mods.v1.ndjson')
    $plugins = @(Read-CaseNdjson 'inventory/plugins.v1.ndjson')
    $masters = @(Read-CaseNdjson 'inventory/plugin-dependencies.v1.ndjson')
    $archives = @(Read-CaseNdjson 'inventory/archives.v1.ndjson')
    $sourceArtifacts = @((Read-CaseNdjson 'provenance/source-archives.v1.ndjson') + (Read-CaseNdjson 'provenance/source-archive-sidecars.v1.ndjson'))
    $metadata = Read-CaseJson 'provenance/mod-metadata.v1.json'

    $seedNames = New-Object Collections.Generic.List[string]
    $planProviderSeeds = if ($plan.PSObject.Properties['providerSeeds']) { @($plan.providerSeeds) } else { @() }
    foreach ($seed in $planProviderSeeds) {
        if ($seed -is [string]) { if (-not [string]::IsNullOrWhiteSpace($seed)) { $seedNames.Add([string]$seed) }; continue }
        foreach ($name in @('name','modName','pluginName','providerName','id')) {
            $property = $seed.PSObject.Properties[$name]
            if ($property -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)) { $seedNames.Add([string]$property.Value) }
        }
    }
    if ($seedNames.Count -eq 0) {
        foreach ($candidate in @($plan.candidatePlugins)) {
            $property = $candidate.PSObject.Properties['name']
            if ($property -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)) { $seedNames.Add([string]$property.Value) }
        }
    }
    if ($seedNames.Count -eq 0) { $failures.Add((New-InspectionFailure 'BaselineInsufficient' 'ResolveEvidenceBoundComponentClosure' @('investigation-plan.json') 'At least one explicit provider seed' 'No provider seeds were recorded.')) }

    $pluginNames = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $providerNames = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($seed in $seedNames) { [void]$pluginNames.Add($seed); [void]$providerNames.Add($seed) }
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($entry in $plugins) {
            $plugin = $entry.plugin
            $name = [string]$plugin.name
            $sourceProvider = if ($plugin.observation) { [string]$plugin.observation.sourceProvider } else { '' }
            if ($pluginNames.Contains($name) -or $providerNames.Contains($sourceProvider)) {
                if ($pluginNames.Add($name)) { $changed = $true }
                if ($sourceProvider -and $providerNames.Add($sourceProvider)) { $changed = $true }
            }
        }
        foreach ($dependency in $masters) {
            $pluginName = [string]$dependency.pluginName
            $masterName = if ($dependency.master) { [string]$dependency.master.name } else { [string]$dependency.masterName }
            if ($pluginNames.Contains($pluginName) -and $masterName -and $pluginNames.Add($masterName)) { $changed = $true }
        }
    }
    # The complete virtual inventory can exceed a gigabyte. The case-seal check
    # above already rehashed it, so bind exact provider names to that verified
    # artifact digest without materializing or rescanning every profile row.
    $virtualProviders = @(Get-CaseVirtualProviderSummary 'inventory/virtual-winners.v1.ndjson' @($providerNames))

    $derivedComponents = @()
    foreach ($mod in @($mods | Sort-Object name)) {
        $modId = Value-Of $mod.modId
        if (-not ($providerNames.Contains([string]$mod.name) -or $providerNames.Contains($modId))) { continue }
        if ([string]::IsNullOrWhiteSpace([string]$mod.canonicalPath)) {
            $failures.Add((New-InspectionFailure 'RequiredInputUnreadable' 'ResolveInstalledProviderTree' @([string]$mod.name) 'One canonical installed directory' 'The sealed mod record has no canonicalPath.'))
            continue
        }
        $derivedComponents += [pscustomobject][ordered]@{
            componentId = if ($modId) { $modId } else { [string]$mod.name }; version = [string](Optional-Of $mod 'version')
            installedDirectory = [string]$mod.canonicalPath
            evidenceIds = @("baseline:$($seal.Manifest.manifestSha256):mod:$modId")
        }
    }
    if ($seedNames.Count -gt 0 -and $derivedComponents.Count -eq 0) {
        $failures.Add((New-InspectionFailure 'BaselineInsufficient' 'ResolveEvidenceBoundComponentClosure' @($seedNames.ToArray()) 'At least one sealed provider record matching the explicit seeds' 'No provider component was resolved.'))
    }
    if (@($Components).Count -gt 0) {
        foreach ($component in $Components) {
            $match = @($derivedComponents | Where-Object { [string]$_.componentId -ieq [string]$component.componentId })
            if ($match.Count -ne 1 -or [string]$match[0].installedDirectory -ine [string]$component.installedDirectory) { throw 'ComponentNotEvidenceBound: caller component is outside the sealed provider closure.' }
        }
    }
    $observations = @()
    foreach ($component in @($derivedComponents | Sort-Object componentId)) {
        try {
            if (@($component.evidenceIds).Count -eq 0) { throw 'Component lacks baseline evidence IDs.' }
            $tree = Get-GridRepairTreeObservation -LiteralPath ([string]$component.installedDirectory)
            $observations += [pscustomobject][ordered]@{ componentId = [string]$component.componentId; version = [string]$component.version; installedDirectory = $tree.root; treeSha256 = $tree.treeSha256; fileCount = $tree.fileCount; evidenceIds = @($component.evidenceIds | Sort-Object -Unique); status = 'Verified' }
        }
        catch {
            $observations += [pscustomobject][ordered]@{ componentId = [string]$component.componentId; version = [string]$component.version; installedDirectory = [string]$component.installedDirectory; treeSha256 = $null; fileCount = 0; evidenceIds = @($component.evidenceIds); status = 'Unreadable' }
            $failures.Add((New-InspectionFailure 'RequiredInputUnreadable' 'ObserveInstalledTree' @([string]$component.installedDirectory) 'Readable evidence-bound tree' $_.Exception.Message))
        }
    }
    $stale = @($failures | Where-Object { $_.code -eq 'BaselineStale' }).Count -gt 0
    $insufficient = @($failures | Where-Object { $_.code -in @('BaselineInsufficient','BaselineGateIncomplete','RequiredInputUnreadable') }).Count -gt 0
    $result = [pscustomobject][ordered]@{
        schemaVersion = 1; baselineCaseId = [string]$seal.Manifest.caseId; baselineManifestSha256 = [string]$seal.Manifest.manifestSha256
        baselineCaseDirectory = [IO.Path]::GetFullPath($BaselineCaseDirectory)
        semanticBaselineFingerprint = [string]$seal.Manifest.semanticBaselineFingerprint
        installationId = [string]$plan.installationId; profileId = [string]$plan.profileId
        planFingerprint = [string]$run[0].planFingerprint
        status = if ($stale) { 'BaselineStale' } elseif ($insufficient -or $failures.Count) { 'InsufficientForRequestedDiagnosis' } else { 'InputsVerified' }
        components = @($observations); protectedState = @($protectedEvidence | ForEach-Object { [pscustomobject][ordered]@{ path = [string]$_.path; sha256 = [string]$_.sha256 } })
        evidence = [pscustomobject][ordered]@{
            providerSeeds = @($seedNames | Sort-Object -Unique); pluginNames = @($pluginNames | Sort-Object)
            modMetadata = @($metadata.records | Where-Object { $providerNames.Contains([string]$_.name) } | Sort-Object name)
            pluginDependencies = @($masters | Where-Object { $pluginNames.Contains([string]$_.pluginName) } | Sort-Object pluginName)
            archives = @($archives | Where-Object { $providerNames.Contains([string]$_.archive.sourceProvider) } | Sort-Object { $_.archive.name })
            sourceArtifacts = @($sourceArtifacts | Where-Object { $providerNames.Contains([string]$_.modName) -or $providerNames.Contains((Value-Of $_.modId)) } | Sort-Object canonicalPath)
            virtualProviders = @($virtualProviders | Sort-Object providerName)
        }
        gates = @($run[0].gates); sufficiency = $run[0].sufficiency
        failures = @($failures.ToArray()); observedAt = [DateTimeOffset]::UtcNow.ToString('o')
    }
    if ($null -ne $diagnosisBinding) { $result | Add-Member -NotePropertyName diagnosis -NotePropertyValue $diagnosisBinding }
    if ($PassThru) { $result } else { $result | ConvertTo-Json -Depth 12 }
}
