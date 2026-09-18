#requires -Version 5.1

<#
.SYNOPSIS
Acquires one evidence-identified Skyrim artifact into Grid quarantine.
.DESCRIPTION
Validates an exact provider identity, streams one local, managed-inbox, or HTTPS
artifact through a resumable inbox record, and promotes verified bytes into a
content-addressed quarantine. Credentials are referenced only by an opaque
OS-protected handle and are never serialized into receipts.
#>

function Write-GridSkyrimAcquisitionJsonAtomic {
    param([Parameter(Mandatory)]$Value, [Parameter(Mandatory)][string]$LiteralPath)
    $path = [IO.Path]::GetFullPath($LiteralPath)
    $parent = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force -ErrorAction Stop | Out-Null
    }
    $temporary = Join-Path $parent ('.' + [IO.Path]::GetFileName($path) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 30), (New-Object Text.UTF8Encoding($false)))
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $backup = $path + '.replace-backup'
            [IO.File]::Replace($temporary, $path, $backup, $true)
            if (Test-Path -LiteralPath $backup -PathType Leaf) { Remove-Item -LiteralPath $backup -Force }
        }
        else { [IO.File]::Move($temporary, $path) }
    }
    finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force } }
    $path
}

function Test-GridSkyrimAcquisitionPath {
    param([Parameter(Mandatory)][string]$LiteralPath, [switch]$MustExist, [switch]$Leaf)
    if (-not [IO.Path]::IsPathRooted($LiteralPath)) { throw "AcquisitionPathRefused: path must be absolute: $LiteralPath" }
    $full = [IO.Path]::GetFullPath($LiteralPath).TrimEnd('\')
    if ($full.StartsWith('\\') -or $full.StartsWith('\\?\') -or $full.StartsWith('\\.\')) { throw "AcquisitionPathRefused: network and device paths are prohibited: $full" }
    $drive = New-Object IO.DriveInfo([IO.Path]::GetPathRoot($full))
    if ($drive.DriveType -notin @([IO.DriveType]::Fixed,[IO.DriveType]::Ram)) { throw "AcquisitionPathRefused: unsupported volume type $($drive.DriveType)." }
    if ($MustExist -and -not (Test-Path -LiteralPath $full)) { throw "AcquisitionPathMissing: $full" }
    if ($Leaf -and $MustExist -and -not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "AcquisitionPathRefused: expected one regular file: $full" }
    $cursorPath = $full
    while (-not (Test-Path -LiteralPath $cursorPath)) {
        $parent = Split-Path -Parent $cursorPath
        if (-not $parent -or $parent -ceq $cursorPath) { break }
        $cursorPath = $parent
    }
    if (Test-Path -LiteralPath $cursorPath) {
        $cursor = Get-Item -LiteralPath $cursorPath -Force
        while ($cursor) {
            if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "AcquisitionPathRefused: reparse point encountered at $($cursor.FullName)." }
            if ($cursor -is [IO.DirectoryInfo]) { $cursor = $cursor.Parent } else { $cursor = $cursor.Directory }
        }
    }
    $full
}

function ConvertTo-GridSkyrimRedactedValue {
    [CmdletBinding()]
    param([Parameter(ValueFromPipeline)]$InputObject)
    process {
        if ($null -eq $InputObject) { return $null }
        if ($InputObject -is [uri]) {
            $builder = New-Object UriBuilder($InputObject)
            if ($builder.UserName -or $builder.Password) { $builder.UserName = ''; $builder.Password = '' }
            if ($builder.Query -match '(?i)(token|key|secret|auth|password|session)') { $builder.Query = 'redacted=REDACTED' }
            return $builder.Uri.AbsoluteUri
        }
        if ($InputObject -is [string]) {
            $value = [string]$InputObject
            $value = [regex]::Replace($value, '(?i)(authorization|api[-_]?key|access[-_]?token|refresh[-_]?token|password|secret)\s*[:=]\s*[^\s&;]+', '$1=REDACTED')
            $value = [regex]::Replace($value, '(?i)(https?://)[^/@\s]+@', '$1')
            return $value
        }
        if ($InputObject -is [Collections.IDictionary]) {
            $output = [ordered]@{}
            foreach ($key in @($InputObject.Keys | Sort-Object)) {
                $name = [string]$key
                $output[$name] = if ($name -match '(?i)(authorization|credential|api.?key|token|password|secret|cookie)') { 'REDACTED' } else { ConvertTo-GridSkyrimRedactedValue $InputObject[$key] }
            }
            return [pscustomobject]$output
        }
        if ($InputObject -is [Collections.IEnumerable] -and $InputObject -isnot [string]) {
            return @($InputObject | ForEach-Object { ConvertTo-GridSkyrimRedactedValue $_ })
        }
        if ($InputObject -is [ValueType]) { return $InputObject }
        $properties = [ordered]@{}
        foreach ($property in @($InputObject.PSObject.Properties | Where-Object { $_.MemberType -in @('NoteProperty','Property') } | Sort-Object Name)) {
            $properties[$property.Name] = if ($property.Name -match '(?i)(authorization|credential|api.?key|token|password|secret|cookie)') { 'REDACTED' } else { ConvertTo-GridSkyrimRedactedValue $property.Value }
        }
        [pscustomobject]$properties
    }
}

function Test-GridSkyrimArtifactProviderIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Expected,
        [Parameter(Mandatory)]$Observed
    )
    $required = @('provider','gameId','modId','fileId','version','fileName','sizeBytes')
    $mismatches = New-Object Collections.Generic.List[object]
    foreach ($field in $required) {
        $expectedProperty = $Expected.PSObject.Properties[$field]
        $observedProperty = $Observed.PSObject.Properties[$field]
        if (-not $expectedProperty -or $null -eq $expectedProperty.Value -or [string]::IsNullOrWhiteSpace([string]$expectedProperty.Value)) {
            throw "ProviderIdentityInvalid: expected identity is missing '$field'."
        }
        if (-not $observedProperty -or $null -eq $observedProperty.Value) {
            $mismatches.Add([pscustomobject][ordered]@{ field = $field; expected = [string]$expectedProperty.Value; observed = $null })
            continue
        }
        $matches = if ($field -eq 'sizeBytes') {
            [long]$expectedProperty.Value -eq [long]$observedProperty.Value
        }
        else { [string]::Equals([string]$expectedProperty.Value, [string]$observedProperty.Value, [StringComparison]::OrdinalIgnoreCase) }
        if (-not $matches) { $mismatches.Add([pscustomobject][ordered]@{ field = $field; expected = [string]$expectedProperty.Value; observed = [string]$observedProperty.Value }) }
    }
    [pscustomobject][ordered]@{
        schemaVersion = 1
        status = if ($mismatches.Count -eq 0) { 'Verified' } else { 'Conflicting' }
        identity = [pscustomobject][ordered]@{
            provider = [string]$Expected.provider; gameId = [string]$Expected.gameId; modId = [string]$Expected.modId
            fileId = [string]$Expected.fileId; version = [string]$Expected.version; fileName = [string]$Expected.fileName
            sizeBytes = [long]$Expected.sizeBytes
        }
        mismatches = $mismatches.ToArray()
    }
}

function Get-GridSkyrimProtectedCredentialHandleState {
    [CmdletBinding()]
    param(
        [string]$CredentialHandle,
        [scriptblock]$CredentialResolver
    )
    if ([string]::IsNullOrWhiteSpace($CredentialHandle)) {
        return [pscustomobject][ordered]@{ status = 'Unavailable'; handleId = $null; reason = 'NoProtectedCredentialHandleConfigured' }
    }
    if ($CredentialHandle -notmatch '^os-protected:[A-Za-z0-9._-]{1,128}$') { throw 'CredentialHandleInvalid: only an opaque OS-protected handle is accepted.' }
    if (-not $CredentialResolver) {
        return [pscustomobject][ordered]@{ status = 'Unavailable'; handleId = $CredentialHandle; reason = 'CredentialResolverUnavailable' }
    }
    try {
        $resolved = & $CredentialResolver $CredentialHandle
        $headers = if ($resolved -is [Collections.IDictionary]) { $resolved } elseif ($resolved -and $resolved.PSObject.Properties['headers']) { $resolved.headers } else { $null }
        if (-not $headers -or $headers.Count -eq 0) {
            return [pscustomobject][ordered]@{ status = 'Unavailable'; handleId = $CredentialHandle; reason = 'ProtectedCredentialUnavailable' }
        }
        foreach ($key in @($headers.Keys)) {
            if ([string]$key -notin @('Authorization','apikey')) { throw "CredentialResolverInvalid: header '$key' is not allowlisted." }
            if ([string]::IsNullOrWhiteSpace([string]$headers[$key])) { throw 'CredentialResolverInvalid: an authorization header is empty.' }
        }
        [pscustomobject][ordered]@{ status = 'Available'; handleId = $CredentialHandle; reason = $null; headers = $headers }
    }
    catch {
        [pscustomobject][ordered]@{ status = 'Unavailable'; handleId = $CredentialHandle; reason = 'ProtectedCredentialResolutionFailed' }
    }
}

function New-GridSkyrimUserAcquisitionAction {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]$ProviderIdentity,
        [Parameter(Mandatory)][uri]$OfficialUri,
        [Parameter(Mandatory)][string]$ManagedInboxRoot,
        [Parameter(Mandatory)][string]$OutputPath,
        [Parameter(Mandatory)][string[]]$IdentityEvidenceIds,
        [switch]$PassThru
    )
    if ($OfficialUri.Scheme -cne 'https' -or $OfficialUri.UserInfo) { throw 'NetworkPolicyRefused: user acquisition must use credential-free HTTPS.' }
    if (@($IdentityEvidenceIds).Count -eq 0) { throw 'ProviderIdentityUnbound: acquisition requires sealed identity evidence.' }
    $actionId = 'acquire-' + [guid]::NewGuid().ToString('N')
    $inbox = Join-Path ([IO.Path]::GetFullPath($ManagedInboxRoot)) $actionId
    $random = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($random)
    $resumeToken = ([Convert]::ToBase64String($random)).TrimEnd('=').Replace('+','-').Replace('/','_')
    $action = [pscustomobject][ordered]@{
        schemaVersion = 1; actionId = $actionId; state = 'PendingUserAcquisition'
        providerIdentity = $ProviderIdentity; officialUri = $OfficialUri.AbsoluteUri; managedInbox = $inbox
        expectedFileName = [string]$ProviderIdentity.fileName
        expectedSizeBytes = if ($ProviderIdentity.PSObject.Properties['sizeBytes'] -and $null -ne $ProviderIdentity.sizeBytes -and [long]$ProviderIdentity.sizeBytes -gt 0) { [long]$ProviderIdentity.sizeBytes } else { $null }
        identityEvidenceIds = @($IdentityEvidenceIds | Sort-Object -Unique); resumeToken = $resumeToken
        createdAt = [DateTimeOffset]::UtcNow.ToString('o'); completedAt = $null
    }
    if ($PSCmdlet.ShouldProcess($OutputPath, 'Persist resumable user acquisition action')) {
        if (-not (Test-Path -LiteralPath $inbox -PathType Container)) { New-Item -ItemType Directory -Path $inbox -Force -ErrorAction Stop | Out-Null }
        Write-GridSkyrimAcquisitionJsonAtomic -Value $action -LiteralPath $OutputPath | Out-Null
    }
    if ($PassThru) { $action } else { $action | ConvertTo-Json -Depth 12 }
}

function Resolve-GridSkyrimArtifactAcquisitionOutcome {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('Verified','Conflicting','Unavailable')][string]$ProviderIdentityStatus,
        [Parameter(Mandatory)][object[]]$SourceObservations,
        [switch]$AuthenticationRequired,
        [switch]$PendingUserAction
    )
    if ($ProviderIdentityStatus -eq 'Conflicting') { return [pscustomobject][ordered]@{ status = 'ProviderIdentityConflict'; recoveryDisposition = 'ConfigurationRequired' } }
    if ($AuthenticationRequired) { return [pscustomobject][ordered]@{ status = 'AuthenticationRequired'; recoveryDisposition = 'UserActionRequired' } }
    if ($PendingUserAction) { return [pscustomobject][ordered]@{ status = 'PendingUserAcquisition'; recoveryDisposition = 'ResumeAvailable' } }
    $observations = @($SourceObservations)
    if ($ProviderIdentityStatus -eq 'Verified' -and $observations.Count -gt 0 -and @($observations | Where-Object { [string]$_.status -notin @('TerminalUnavailable','ExactNotFound') }).Count -eq 0) {
        return [pscustomobject][ordered]@{ status = 'ExactArtifactUnavailable'; recoveryDisposition = 'ExternalDependencyRequired' }
    }
    [pscustomobject][ordered]@{ status = 'AcquisitionIncomplete'; recoveryDisposition = 'ResumeAvailable' }
}

function Complete-GridSkyrimManagedAcquisition {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string]$UserActionPath,
        [Parameter(Mandatory)][string]$QuarantineRoot,
        [Parameter(Mandatory)][string]$AcquisitionRecordPath,
        [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedSha256,
        [ValidateRange(1, 1073741824)][long]$MaximumBytes = 1073741824,
        [switch]$PassThru
    )
    $actionPath = Test-GridSkyrimAcquisitionPath -LiteralPath $UserActionPath -MustExist -Leaf
    $action = Get-Content -LiteralPath $actionPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    foreach ($field in @('schemaVersion','actionId','state','providerIdentity','managedInbox','expectedFileName','expectedSizeBytes','identityEvidenceIds','resumeToken')) {
        if ($null -eq $action.PSObject.Properties[$field]) { throw "UserAcquisitionActionInvalid: missing '$field'." }
    }
    if ([int]$action.schemaVersion -ne 1 -or [string]$action.state -ne 'PendingUserAcquisition') { throw 'UserAcquisitionActionInvalid: only a pending v1 action can be resumed.' }
    if ([string]$action.resumeToken -notmatch '^[A-Za-z0-9_-]{40,64}$') { throw 'UserAcquisitionActionInvalid: resume token is malformed.' }
    $inbox = Test-GridSkyrimAcquisitionPath -LiteralPath ([string]$action.managedInbox)
    if (-not (Test-Path -LiteralPath $inbox -PathType Container)) {
        $pending = [pscustomobject][ordered]@{
            state = 'PendingUserAcquisition'; recoveryDisposition = 'ResumeAvailable'
            actionId = [string]$action.actionId; managedInbox = $inbox
            expectedFileName = [string]$action.expectedFileName
            detail = 'The exact managed inbox does not exist yet; no artifact bytes were observed.'
        }
        if ($PassThru) { return $pending }
        return ($pending | ConvertTo-Json -Depth 10)
    }
    $expectedLeaf = [string]$action.expectedFileName
    if ([IO.Path]::GetFileName($expectedLeaf) -cne $expectedLeaf) { throw 'UserAcquisitionActionInvalid: expected file name is not one leaf.' }
    $candidates = @(Get-ChildItem -LiteralPath $inbox -File -Force -ErrorAction Stop | Where-Object { $_.Name -ieq $expectedLeaf })
    if ($candidates.Count -eq 0) {
        $pending = [pscustomobject][ordered]@{
            state = 'PendingUserAcquisition'; recoveryDisposition = 'ResumeAvailable'
            actionId = [string]$action.actionId; managedInbox = $inbox
            expectedFileName = $expectedLeaf
            detail = 'The exact expected archive is absent from the managed inbox.'
        }
        if ($PassThru) { return $pending }
        return ($pending | ConvertTo-Json -Depth 10)
    }
    if ($candidates.Count -ne 1) { throw 'ManagedInboxAmbiguous: multiple case-colliding expected archives were found.' }
    $item = $candidates[0]
    if ([long]$item.Length -le 0 -or [long]$item.Length -gt $MaximumBytes) { throw 'ArtifactSizeRefused: acquired bytes are outside the evidence-derived ceiling.' }
    $provider = $action.providerIdentity
    foreach ($field in @('provider','gameId','modId','fileId','version','fileName')) {
        if ($null -eq $provider.PSObject.Properties[$field] -or [string]::IsNullOrWhiteSpace([string]$provider.$field)) { throw "UserAcquisitionActionInvalid: provider identity is missing '$field'." }
    }
    if ([string]$provider.fileName -cne $expectedLeaf) { throw 'UserAcquisitionActionInvalid: provider identity leaf differs from the action leaf.' }
    $actionSize = if ($null -ne $action.PSObject.Properties['expectedSizeBytes'] -and $null -ne $action.expectedSizeBytes) { [long]$action.expectedSizeBytes } else { 0L }
    $providerSize = if ($null -ne $provider.PSObject.Properties['sizeBytes'] -and $null -ne $provider.sizeBytes) { [long]$provider.sizeBytes } else { 0L }
    if ($actionSize -lt 0 -or $providerSize -lt 0 -or ($actionSize -gt 0 -and $providerSize -gt 0 -and $actionSize -ne $providerSize)) {
        throw 'UserAcquisitionActionInvalid: sealed exact-size fields are inconsistent.'
    }
    $sealedSize = if ($actionSize -gt 0) { $actionSize } else { $providerSize }
    if ($sealedSize -gt 0 -and [long]$item.Length -ne $sealedSize) { throw 'ArtifactSizeMismatch: managed-inbox bytes differ from the sealed exact size.' }
    $boundIdentity = [pscustomobject][ordered]@{
        provider = [string]$provider.provider; gameId = [string]$provider.gameId; modId = [string]$provider.modId
        fileId = [string]$provider.fileId; version = [string]$provider.version; fileName = $expectedLeaf; sizeBytes = [long]$item.Length
    }
    $arguments = @{
        SourceLiteralPath = $item.FullName; ExpectedProviderIdentity = $boundIdentity; ObservedProviderIdentity = $boundIdentity
        IdentityEvidenceIds = @($action.identityEvidenceIds); InboxRoot = $inbox; QuarantineRoot = $QuarantineRoot
        AcquisitionRecordPath = $AcquisitionRecordPath; MaximumBytes = $MaximumBytes; Confirm = $false; PassThru = $true
    }
    if ($ExpectedSha256) { $arguments.ExpectedSha256 = $ExpectedSha256 }
    if ($WhatIfPreference) { $arguments.WhatIf = $true }
    $result = Invoke-GridSkyrimArtifactAcquisition @arguments
    $result | Add-Member -NotePropertyName predecessorActionId -NotePropertyValue ([string]$action.actionId) -Force
    $result | Add-Member -NotePropertyName predecessorResumeTokenSha256 -NotePropertyValue (Get-GridSkyrimAcquisitionStringSha256 -Value ([string]$action.resumeToken)) -Force
    if ($PSCmdlet.ShouldProcess($AcquisitionRecordPath, 'Bind acquisition receipt to predecessor user action')) {
        Write-GridSkyrimAcquisitionJsonAtomic -Value $result -LiteralPath $AcquisitionRecordPath | Out-Null
    }
    if ($PassThru) { $result } else { $result | ConvertTo-Json -Depth 30 }
}

function Get-GridSkyrimAcquisitionStringSha256 {
    param([Parameter(Mandatory)][string]$Value)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-', '') }
    finally { $algorithm.Dispose() }
}

function Invoke-GridSkyrimArtifactAcquisition {
    [CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'Local')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Local')][string]$SourceLiteralPath,
        [Parameter(Mandatory, ParameterSetName = 'ManagedInbox')][string]$ManagedInbox,
        [Parameter(Mandatory, ParameterSetName = 'Remote')][uri]$SourceUri,
        [Parameter(Mandatory)]$ExpectedProviderIdentity,
        [Parameter(Mandatory)]$ObservedProviderIdentity,
        [Parameter(Mandatory)][string[]]$IdentityEvidenceIds,
        [Parameter(Mandatory)][string]$InboxRoot,
        [Parameter(Mandatory)][string]$QuarantineRoot,
        [Parameter(Mandatory)][string]$AcquisitionRecordPath,
        [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedSha256,
        [ValidateRange(1, 1073741824)][long]$MaximumBytes = 1073741824,
        [Parameter(Mandatory, ParameterSetName = 'Remote')][string[]]$AllowedProviderHosts,
        [Parameter(ParameterSetName = 'Remote')][string]$CredentialHandle,
        [Parameter(ParameterSetName = 'Remote')][scriptblock]$CredentialResolver,
        [Parameter(ParameterSetName = 'Remote')][scriptblock]$HttpResponseFactory,
        [ValidateRange(1,5)][int]$MaximumRedirects = 5,
        [ValidateRange(0,1)][int]$MaximumTransientRetries = 1,
        [ValidateRange(1,120)][int]$TimeoutSeconds = 20,
        [switch]$PassThru
    )
    $identity = Test-GridSkyrimArtifactProviderIdentity -Expected $ExpectedProviderIdentity -Observed $ObservedProviderIdentity
    if ($identity.status -ne 'Verified') { throw 'ProviderIdentityConflict: observed provider metadata does not match the sealed expected identity.' }
    if (@($IdentityEvidenceIds).Count -eq 0) { throw 'ProviderIdentityUnbound: acquisition requires sealed identity evidence.' }
    $expectedSize = [long]$ExpectedProviderIdentity.sizeBytes
    if ($expectedSize -le 0 -or $expectedSize -gt $MaximumBytes) { throw 'ArtifactSizeRefused: evidence size is outside the bounded acquisition policy.' }
    if ([IO.Path]::GetFileName([string]$ExpectedProviderIdentity.fileName) -cne [string]$ExpectedProviderIdentity.fileName) { throw 'ProviderIdentityInvalid: fileName must be one exact leaf name.' }
    $inbox = Test-GridSkyrimAcquisitionPath -LiteralPath $InboxRoot
    $quarantine = Test-GridSkyrimAcquisitionPath -LiteralPath $QuarantineRoot
    foreach ($root in @($inbox,$quarantine)) {
        if (-not (Test-Path -LiteralPath $root -PathType Container) -and $PSCmdlet.ShouldProcess($root, 'Create acquisition directory')) { New-Item -ItemType Directory -Path $root -Force -ErrorAction Stop | Out-Null }
    }
    $actionId = 'artifact-' + ([string]$ExpectedProviderIdentity.provider + '|' + [string]$ExpectedProviderIdentity.gameId + '|' + [string]$ExpectedProviderIdentity.modId + '|' + [string]$ExpectedProviderIdentity.fileId)
    $actionHashBytes = [Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($actionId))
    $stableId = ([BitConverter]::ToString($actionHashBytes)).Replace('-','').Substring(0,24).ToLowerInvariant()
    $partPath = Join-Path $inbox ($stableId + '.part')
    $statePath = Join-Path $inbox ($stableId + '.resume.v1.json')
    $sourcePath = $null
    $requestedUri = $null
    $finalUri = $null
    $validators = [ordered]@{ etag = $null; lastModified = $null }
    $redirects = New-Object Collections.Generic.List[object]
    $resumeOffset = if (Test-Path -LiteralPath $partPath -PathType Leaf) { [long](Get-Item -LiteralPath $partPath).Length } else { 0L }
    if ($resumeOffset -gt $expectedSize) { throw 'ResumeStateInvalid: partial artifact exceeds the evidence-bound size.' }

    if ($PSCmdlet.ParameterSetName -eq 'ManagedInbox') {
        $managed = Test-GridSkyrimAcquisitionPath -LiteralPath $ManagedInbox -MustExist
        if (-not (Test-Path -LiteralPath $managed -PathType Container)) { throw 'PendingUserAcquisition: managed inbox has not been populated.' }
        $candidates = @(Get-ChildItem -LiteralPath $managed -File -Force -ErrorAction Stop | Where-Object { $_.Name -ieq [string]$ExpectedProviderIdentity.fileName })
        if ($candidates.Count -eq 0) { throw 'PendingUserAcquisition: exact expected leaf is absent from the managed inbox.' }
        if ($candidates.Count -ne 1) { throw 'ManagedInboxAmbiguous: multiple case-colliding expected leaves were found.' }
        $sourcePath = Test-GridSkyrimAcquisitionPath -LiteralPath $candidates[0].FullName -MustExist -Leaf
    }
    elseif ($PSCmdlet.ParameterSetName -eq 'Local') { $sourcePath = Test-GridSkyrimAcquisitionPath -LiteralPath $SourceLiteralPath -MustExist -Leaf }

    if ($sourcePath) {
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw 'ExactArtifactSourceNotFound: the exact authorized local source is absent.' }
        $sourceItem = Get-Item -LiteralPath $sourcePath -Force
        if ([long]$sourceItem.Length -ne $expectedSize) { throw 'ArtifactSizeMismatch: source differs from provider identity.' }
        if ($PSCmdlet.ShouldProcess($partPath, 'Stream exact artifact into managed inbox')) {
            $input = [IO.File]::Open($sourcePath, 'Open', 'Read', 'Read')
            $output = [IO.File]::Open($partPath, 'OpenOrCreate', 'Write', 'None')
            try {
                if ($resumeOffset -gt 0) { $input.Position = $resumeOffset; $output.Position = $resumeOffset }
                $buffer = New-Object byte[] 65536
                while (($read = $input.Read($buffer,0,$buffer.Length)) -gt 0) { $output.Write($buffer,0,$read) }
                $output.Flush()
            } finally { $output.Dispose(); $input.Dispose() }
        }
    }
    else {
        $requestedUri = $SourceUri.AbsoluteUri
        $credential = Get-GridSkyrimProtectedCredentialHandleState -CredentialHandle $CredentialHandle -CredentialResolver $CredentialResolver
        if ($CredentialHandle -and $credential.status -ne 'Available') { throw 'AuthenticationRequired: the OS-protected credential handle is unavailable.' }
        $current = $SourceUri
        $transientRetries = 0
        for ($hop = 0; $hop -le $MaximumRedirects; $hop++) {
            if ($current.Scheme -cne 'https' -or $current.UserInfo -or $current.Host -notin $AllowedProviderHosts) { throw 'NetworkPolicyRefused: remote source is not an allowlisted credential-free HTTPS URI.' }
            $requestHeaders = [ordered]@{}
            if ($credential.status -eq 'Available') { foreach ($key in $credential.headers.Keys) { $requestHeaders[[string]$key] = [string]$credential.headers[$key] } }
            if ($resumeOffset -gt 0 -and (Test-Path -LiteralPath $statePath -PathType Leaf)) {
                $oldState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
                if (-not $oldState.validators.etag -and -not $oldState.validators.lastModified) { throw 'ResumeValidatorUnavailable: partial HTTPS acquisition has no strong validator.' }
                $requestHeaders['Range'] = "bytes=$resumeOffset-"
                $requestHeaders['If-Range'] = if ($oldState.validators.etag) { [string]$oldState.validators.etag } else { [string]$oldState.validators.lastModified }
            }
            try {
                if ($HttpResponseFactory) { $response = & $HttpResponseFactory $current $requestHeaders $TimeoutSeconds }
                else {
                    $request = [Net.HttpWebRequest]::Create($current); $request.Method = 'GET'; $request.AllowAutoRedirect = $false
                    $request.Timeout = $TimeoutSeconds * 1000; $request.ReadWriteTimeout = $TimeoutSeconds * 1000
                    foreach ($key in $requestHeaders.Keys) {
                        if ($key -eq 'Range') { $request.AddRange($resumeOffset) }
                        elseif ($key -eq 'If-Range') { $request.Headers['If-Range'] = $requestHeaders[$key] }
                        else { $request.Headers[$key] = $requestHeaders[$key] }
                    }
                    $native = $request.GetResponse()
                    $response = [pscustomobject]@{ StatusCode = [int]$native.StatusCode; Headers = $native.Headers; Stream = $native.GetResponseStream(); NativeResponse = $native }
                }
            }
            catch [Net.WebException] {
                $native = $_.Exception.Response
                if ($native) {
                    $response = [pscustomobject]@{ StatusCode = [int]$native.StatusCode; Headers = $native.Headers; Stream = $native.GetResponseStream(); NativeResponse = $native }
                }
                elseif ($transientRetries -lt $MaximumTransientRetries) { $transientRetries++; $hop--; continue }
                else { throw }
            }
            catch {
                if ($transientRetries -lt $MaximumTransientRetries) { $transientRetries++; $hop--; continue }
                throw
            }
            try {
                $code = [int]$response.StatusCode
                if ($code -in @(301,302,303,307,308)) {
                    if ($hop -eq $MaximumRedirects) { throw 'RedirectLimitExceeded: acquisition exceeded the redirect bound.' }
                    $location = [string]$response.Headers['Location']; if (-not $location) { throw 'RedirectInvalid: response omitted Location.' }
                    $next = [uri]::new($current,$location); $redirects.Add([pscustomobject]@{ from=$current.AbsoluteUri; to=$next.AbsoluteUri; status=$code }); $current=$next; continue
                }
                if ($code -in @(401,403)) { throw 'AuthenticationRequired: provider rejected the protected credential.' }
                if ($code -eq 404) { throw 'ExactArtifactSourceNotFound: this provider endpoint reports the exact sealed file identity is absent.' }
                if ($code -lt 200 -or $code -ge 300) { throw "ArtifactRequestFailed: HTTP $code" }
                if ($resumeOffset -gt 0 -and $code -ne 206) { throw 'ResumeValidatorMismatch: provider did not honor the bounded resume request.' }
                $validators.etag = [string]$response.Headers['ETag']; $validators.lastModified = [string]$response.Headers['Last-Modified']
                if ($resumeOffset -gt 0) {
                    $oldState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
                    if (($oldState.validators.etag -and $validators.etag -cne [string]$oldState.validators.etag) -or ($oldState.validators.lastModified -and $validators.lastModified -cne [string]$oldState.validators.lastModified)) { throw 'ResumeValidatorMismatch: provider validator changed.' }
                }
                $finalUri = $current.AbsoluteUri
                if ($PSCmdlet.ShouldProcess($partPath, 'Stream HTTPS artifact into managed inbox')) {
                    $output = [IO.File]::Open($partPath, 'OpenOrCreate', 'Write', 'None'); $output.Position = $resumeOffset
                    $buffer = New-Object byte[] 65536; $total = $resumeOffset
                    try {
                        while (($read = $response.Stream.Read($buffer,0,$buffer.Length)) -gt 0) {
                            $total += $read; if ($total -gt $MaximumBytes -or $total -gt $expectedSize) { throw 'ArtifactSizeRefused: response exceeded the evidence-bound limit.' }
                            $output.Write($buffer,0,$read)
                            if (($total % 4194304) -lt 65536) {
                                $resume = [pscustomobject][ordered]@{ schemaVersion=1; state='Downloading'; providerIdentity=$identity.identity; partPath=$partPath; downloadedBytes=$total; expectedSizeBytes=$expectedSize; validators=[pscustomobject]$validators; requestedUri=$requestedUri; finalUri=$finalUri; updatedAt=[DateTimeOffset]::UtcNow.ToString('o') }
                                Write-GridSkyrimAcquisitionJsonAtomic -Value (ConvertTo-GridSkyrimRedactedValue $resume) -LiteralPath $statePath | Out-Null
                            }
                        }
                        $output.Flush()
                    } finally { $output.Dispose() }
                }
                if (Test-Path -LiteralPath $partPath -PathType Leaf) {
                    $currentBytes = [long](Get-Item -LiteralPath $partPath -Force).Length
                    $resume = [pscustomobject][ordered]@{ schemaVersion=1; state=if ($currentBytes -eq $expectedSize) {'Downloaded'} else {'Downloading'}; providerIdentity=$identity.identity; partPath=$partPath; downloadedBytes=$currentBytes; expectedSizeBytes=$expectedSize; validators=[pscustomobject]$validators; requestedUri=$requestedUri; finalUri=$finalUri; updatedAt=[DateTimeOffset]::UtcNow.ToString('o') }
                    Write-GridSkyrimAcquisitionJsonAtomic -Value (ConvertTo-GridSkyrimRedactedValue $resume) -LiteralPath $statePath | Out-Null
                }
                break
            }
            finally {
                if ($response.Stream) { $response.Stream.Dispose() }
                if ($response.PSObject.Properties['NativeResponse'] -and $response.NativeResponse) { $response.NativeResponse.Dispose() }
            }
        }
    }
    if (-not (Test-Path -LiteralPath $partPath -PathType Leaf)) {
        $whatIf = [pscustomobject][ordered]@{ schemaVersion=1; acquisitionId=$stableId; state='Planned'; providerIdentity=$identity.identity; artifact=$null; recoveryDisposition='ResumeAvailable' }
        if ($PassThru) { return $whatIf } else { return ($whatIf | ConvertTo-Json -Depth 12) }
    }
    $part = Get-Item -LiteralPath $partPath -Force
    if ([long]$part.Length -ne $expectedSize) { throw 'AcquisitionInterrupted: partial bytes are retained for bounded resume.' }
    $sha256 = (Get-FileHash -LiteralPath $partPath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($ExpectedSha256 -and $sha256 -cne $ExpectedSha256.ToUpperInvariant()) { throw 'ArtifactHashMismatch: bytes remain outside verified quarantine.' }
    $destinationDirectory = Join-Path $quarantine $sha256.Substring(0,2)
    $destination = Join-Path $destinationDirectory $sha256
    if ($PSCmdlet.ShouldProcess($destination, 'Promote verified artifact to immutable quarantine')) {
        if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) { New-Item -ItemType Directory -Path $destinationDirectory -Force -ErrorAction Stop | Out-Null }
        if (Test-Path -LiteralPath $destination -PathType Leaf) {
            if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -cne $sha256) { throw 'QuarantineCollision: existing object failed digest verification.' }
            Remove-Item -LiteralPath $partPath -Force
        }
        else { [IO.File]::Move($partPath,$destination) }
    }
    $receipt = [pscustomobject][ordered]@{
        schemaVersion=1; acquisitionId=$stableId; state='Verified'; providerIdentity=$identity.identity
        artifact=[pscustomobject][ordered]@{ artifactId="sha256:$sha256"; path=$destination; sizeBytes=$expectedSize; sha256=$sha256 }
        identityEvidenceIds=@($IdentityEvidenceIds | Sort-Object -Unique)
        provenance=[pscustomobject][ordered]@{ sourceKind=$PSCmdlet.ParameterSetName; requestedUri=$requestedUri; finalUri=$finalUri; redirects=$redirects.ToArray(); validators=[pscustomobject]$validators }
        recoveryDisposition='None'; completedAt=[DateTimeOffset]::UtcNow.ToString('o')
    }
    $safeReceipt = ConvertTo-GridSkyrimRedactedValue $receipt
    if ($PSCmdlet.ShouldProcess($AcquisitionRecordPath, 'Write secret-free acquisition receipt')) { Write-GridSkyrimAcquisitionJsonAtomic -Value $safeReceipt -LiteralPath $AcquisitionRecordPath | Out-Null }
    if ($PassThru) { $safeReceipt } else { $safeReceipt | ConvertTo-Json -Depth 20 }
}
