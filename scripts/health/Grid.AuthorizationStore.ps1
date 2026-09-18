#requires -Version 5.1
<#
.SYNOPSIS
Durable, one-use authorization grants with exclusive leases and semantic binding.
.DESCRIPTION
Authorization secrets are generated from cryptographically secure random bytes and are
returned only to the approving caller. The durable grant stores only a salted PBKDF2 proof.
Grant state transitions are serialized with an exclusive CreateNew lock and persist the
Issued -> Executing -> Consumed|Failed lifecycle. Historical v1 records may be read for
compatibility but can never be consumed as current authority.
#>
Set-StrictMode -Version Latest

$script:GridAuthorizationSchemaVersion = 2
$script:GridAuthorizationPbkdf2Iterations = 200000
$script:GridAuthorizationUtf8NoBom = New-Object Text.UTF8Encoding($false)

function ConvertTo-GridAuthorizationBase64Url {
    param([Parameter(Mandatory)][byte[]]$Bytes)
    [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
}

function New-GridAuthorizationRandomBytes {
    param([ValidateRange(16,128)][int]$Count = 32)
    $bytes = New-Object byte[] $Count
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $bytes
}

function New-GridAuthorizationSecret {
    [CmdletBinding()]
    param()
    ConvertTo-GridAuthorizationBase64Url -Bytes (New-GridAuthorizationRandomBytes -Count 32)
}

function New-GridAuthorizationSecretProof {
    param([Parameter(Mandatory)][string]$Secret)
    $salt = New-GridAuthorizationRandomBytes -Count 16
    $kdf = New-Object Security.Cryptography.Rfc2898DeriveBytes($Secret, $salt, $script:GridAuthorizationPbkdf2Iterations)
    try { $hash = $kdf.GetBytes(32) } finally { $kdf.Dispose() }
    [pscustomobject][ordered]@{
        algorithm = 'PBKDF2-HMAC-SHA1'
        iterations = $script:GridAuthorizationPbkdf2Iterations
        saltBase64 = [Convert]::ToBase64String($salt)
        hashBase64 = [Convert]::ToBase64String($hash)
    }
}

function Test-GridAuthorizationSecretProof {
    param([Parameter(Mandatory)][string]$Secret, [Parameter(Mandatory)]$Proof)
    if ([string]$Proof.algorithm -cne 'PBKDF2-HMAC-SHA1' -or [int]$Proof.iterations -lt 100000) { return $false }
    try { $salt = [Convert]::FromBase64String([string]$Proof.saltBase64); $expected = [Convert]::FromBase64String([string]$Proof.hashBase64) } catch { return $false }
    $kdf = New-Object Security.Cryptography.Rfc2898DeriveBytes($Secret, $salt, [int]$Proof.iterations)
    try { $actual = $kdf.GetBytes($expected.Length) } finally { $kdf.Dispose() }
    if ($actual.Length -ne $expected.Length) { return $false }
    $difference = 0
    for ($i=0; $i -lt $actual.Length; $i++) { $difference = $difference -bor ($actual[$i] -bxor $expected[$i]) }
    $difference -eq 0
}

function Get-GridAuthorizationStoreRoot {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$StoreRoot, [switch]$Ensure)
    $root = [IO.Path]::GetFullPath($StoreRoot)
    $path = Join-Path $root 'authorizations\v2'
    if ($Ensure -and -not (Test-Path -LiteralPath $path -PathType Container)) { New-Item -ItemType Directory -Path $path -Force -ErrorAction Stop | Out-Null }
    $path
}

function Assert-GridAuthorizationId {
    param([Parameter(Mandatory)][string]$Value, [Parameter(Mandatory)][string]$Name)
    if ($Value -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,159}$') { throw "AuthorizationIdentityInvalid: $Name '$Value' is invalid." }
}

function Get-GridAuthorizationGrantDirectory {
    param([Parameter(Mandatory)][string]$StoreRoot, [Parameter(Mandatory)][string]$GrantId, [switch]$Ensure)
    Assert-GridAuthorizationId -Value $GrantId -Name GrantId
    $root = Get-GridAuthorizationStoreRoot -StoreRoot $StoreRoot -Ensure:$Ensure
    $path = Join-Path $root $GrantId
    if ($Ensure -and -not (Test-Path -LiteralPath $path -PathType Container)) { New-Item -ItemType Directory -Path $path -ErrorAction Stop | Out-Null }
    $path
}

function Write-GridAuthorizationJsonAtomic {
    param([Parameter(Mandatory)]$Value, [Parameter(Mandatory)][string]$LiteralPath)
    $directory = Split-Path -Parent $LiteralPath
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { New-Item -ItemType Directory -Path $directory -Force -ErrorAction Stop | Out-Null }
    $temporary = Join-Path $directory ('.grid-auth-{0}.tmp' -f ([Guid]::NewGuid().ToString('N')))
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 100 -Compress), $script:GridAuthorizationUtf8NoBom)
        Move-Item -LiteralPath $temporary -Destination $LiteralPath -Force -ErrorAction Stop
    }
    finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue } }
}

function Get-GridAuthorizationSemanticDigest {
    param([Parameter(Mandatory)]$SemanticBinding)
    Get-GridCanonicalJsonSha256 -InputObject $SemanticBinding
}

function Test-GridAuthorizationSemanticBinding {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Expected, [Parameter(Mandatory)]$Actual)
    (Get-GridAuthorizationSemanticDigest -SemanticBinding $Expected) -ceq (Get-GridAuthorizationSemanticDigest -SemanticBinding $Actual)
}

function New-GridAuthorizationGrant {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StoreRoot,
        [Parameter(Mandatory)][string]$ReviewId,
        [Parameter(Mandatory)][ValidateSet('Read','Mutation')][string]$AuthorityClass,
        [Parameter(Mandatory)]$SemanticBinding,
        [ValidateRange(1,1440)][int]$LifetimeMinutes = 15
    )
    foreach ($identity in @('actorId','sessionId','workspaceId','requestId','submissionId','envelopeSha256','planSha256','scopeSha256','proposalOrSpecificationId','proposalOrSpecificationSha256','normalizedInputSha256')) {
        if ($null -eq $SemanticBinding.PSObject.Properties[$identity] -or [string]::IsNullOrWhiteSpace([string]$SemanticBinding.$identity)) { throw "AuthorizationBindingInvalid: missing $identity." }
    }
    $secret = New-GridAuthorizationSecret
    $grantId = 'grant-' + [Guid]::NewGuid().ToString('N')
    $now = [DateTimeOffset]::UtcNow
    $grant = [pscustomobject][ordered]@{
        schemaVersion = 2
        grantId = $grantId
        reviewId = $ReviewId
        state = 'Issued'
        authorityClass = $AuthorityClass
        semanticBinding = $SemanticBinding
        semanticBindingSha256 = Get-GridAuthorizationSemanticDigest -SemanticBinding $SemanticBinding
        secretProof = New-GridAuthorizationSecretProof -Secret $secret
        issuedAt = $now.ToString('o')
        expiresAt = $now.AddMinutes($LifetimeMinutes).ToString('o')
        lease = $null
        consumption = $null
        failure = $null
    }
    $directory = Get-GridAuthorizationGrantDirectory -StoreRoot $StoreRoot -GrantId $grantId -Ensure
    $path = Join-Path $directory 'grant.v2.json'
    if (Test-Path -LiteralPath $path) { throw 'AuthorizationGrantCollision: generated grant already exists.' }
    Write-GridAuthorizationJsonAtomic -Value $grant -LiteralPath $path
    [pscustomobject][ordered]@{ Grant = $grant; AuthorizationSecret = $secret; GrantPath = $path }
}

function Read-GridAuthorizationGrant {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$StoreRoot, [Parameter(Mandatory)][string]$GrantId)
    $directory = Get-GridAuthorizationGrantDirectory -StoreRoot $StoreRoot -GrantId $GrantId
    $v2 = Join-Path $directory 'grant.v2.json'
    if (Test-Path -LiteralPath $v2 -PathType Leaf) { return (Get-Content -LiteralPath $v2 -Raw | ConvertFrom-Json -ErrorAction Stop) }
    $legacy = Join-Path $directory 'grant.v1.json'
    if (Test-Path -LiteralPath $legacy -PathType Leaf) {
        $record = Get-Content -LiteralPath $legacy -Raw | ConvertFrom-Json -ErrorAction Stop
        return [pscustomobject][ordered]@{ schemaVersion=1; grantId=$GrantId; state='HistoricalReadOnly'; legacyRecord=$record }
    }
    throw "AuthorizationGrantMissing: '$GrantId'."
}

function Test-GridAuthorizationGrantPreflight {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StoreRoot,
        [Parameter(Mandatory)][string]$GrantId,
        [Parameter(Mandatory)][string]$AuthorizationSecret,
        [Parameter(Mandatory)]$ExpectedSemanticBinding
    )
    $grant = Read-GridAuthorizationGrant -StoreRoot $StoreRoot -GrantId $GrantId
    if ([int]$grant.schemaVersion -ne 2) { throw 'HistoricalAuthorizationNotExecutable: historical grants cannot confer current authority.' }
    if (-not (Test-GridAuthorizationSecretProof -Secret $AuthorizationSecret -Proof $grant.secretProof)) { throw 'AuthorizationSecretInvalid: supplied secret does not match the grant.' }
    if (-not (Test-GridAuthorizationSemanticBinding -Expected $ExpectedSemanticBinding -Actual $grant.semanticBinding)) { throw 'AuthorizationBindingMismatch: current semantics differ from the grant.' }
    $grant
}

function Invoke-GridAuthorizationGrantLock {
    param([Parameter(Mandatory)][string]$GrantDirectory, [Parameter(Mandatory)][scriptblock]$Action)
    $lockPath = Join-Path $GrantDirectory 'transition.lock'
    $stream = $null
    try {
        try { $stream = New-Object IO.FileStream($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None, 1, [IO.FileOptions]::WriteThrough) }
        catch { throw 'AuthorizationConcurrentConsumer: an exclusive authorization transition is already in progress.' }
        & $Action
    }
    finally {
        if ($stream) { $stream.Dispose() }
        if (Test-Path -LiteralPath $lockPath -PathType Leaf) { Remove-Item -LiteralPath $lockPath -Force -ErrorAction SilentlyContinue }
    }
}

function Enter-GridAuthorizationLease {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StoreRoot,
        [Parameter(Mandatory)][string]$GrantId,
        [Parameter(Mandatory)][string]$AuthorizationSecret,
        [Parameter(Mandatory)]$ExpectedSemanticBinding,
        [Parameter(Mandatory)][string]$ConsumerId,
        [ValidateRange(1,60)][int]$LeaseMinutes = 10
    )
    Assert-GridAuthorizationId -Value $ConsumerId -Name ConsumerId
    $directory = Get-GridAuthorizationGrantDirectory -StoreRoot $StoreRoot -GrantId $GrantId
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw "AuthorizationGrantMissing: '$GrantId'." }
    $result = Invoke-GridAuthorizationGrantLock -GrantDirectory $directory -Action {
        $grantPath = Join-Path $directory 'grant.v2.json'
        if (-not (Test-Path -LiteralPath $grantPath -PathType Leaf)) { throw 'HistoricalAuthorizationNotExecutable: historical grants are read-compatible only.' }
        $grant = Get-Content -LiteralPath $grantPath -Raw | ConvertFrom-Json -ErrorAction Stop
        if ([int]$grant.schemaVersion -ne 2) { throw 'AuthorizationGrantVersionUnsupported.' }
        if ([string]$grant.grantId -cne $GrantId) { throw 'AuthorizationGrantIdentityMismatch: persisted grant identity differs from its store path.' }
        switch ([string]$grant.state) {
            'Executing' { throw 'AuthorizationConcurrentConsumer: grant already has an executing lease.' }
            'Consumed' { throw 'AuthorizationReplayRefused: grant has already been consumed.' }
            'Failed' { throw 'AuthorizationReplayRefused: failed grants are terminal.' }
            'Issued' { }
            default { throw "AuthorizationStateInvalid: '$($grant.state)'." }
        }
        if ([DateTimeOffset]::Parse([string]$grant.expiresAt) -le [DateTimeOffset]::UtcNow) { throw 'AuthorizationExpired: grant is expired.' }
        if (-not (Test-GridAuthorizationSecretProof -Secret $AuthorizationSecret -Proof $grant.secretProof)) { throw 'AuthorizationSecretInvalid: supplied secret does not match the persisted protected proof.' }
        if (-not (Test-GridAuthorizationSemanticBinding -Expected $ExpectedSemanticBinding -Actual $grant.semanticBinding)) { throw 'AuthorizationBindingMismatch: actor, session, workspace, request, plan, scope, target, capability, or input identity changed.' }
        $now = [DateTimeOffset]::UtcNow
        $lease = [pscustomobject][ordered]@{
            schemaVersion = 1; leaseId = 'lease-' + [Guid]::NewGuid().ToString('N'); grantId = $GrantId; state = 'Executing'
            consumerId = $ConsumerId; semanticBindingSha256 = [string]$grant.semanticBindingSha256
            acquiredAt = $now.ToString('o'); expiresAt = $now.AddMinutes($LeaseMinutes).ToString('o'); completedAt = $null
        }
        Write-GridAuthorizationJsonAtomic -Value $lease -LiteralPath (Join-Path $directory 'lease.v1.json')
        $grant.state = 'Executing'; $grant.lease = [pscustomobject][ordered]@{ leaseId=$lease.leaseId; consumerId=$ConsumerId; acquiredAt=$lease.acquiredAt; expiresAt=$lease.expiresAt }
        Write-GridAuthorizationJsonAtomic -Value $grant -LiteralPath $grantPath
        [pscustomobject][ordered]@{ Grant=$grant; Lease=$lease }
    }
    $result
}

function Complete-GridAuthorizationLease {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StoreRoot,
        [Parameter(Mandatory)][string]$GrantId,
        [Parameter(Mandatory)][string]$LeaseId,
        [Parameter(Mandatory)][ValidateSet('Consumed','Failed')][string]$Result,
        [string]$Detail = ''
    )
    $directory = Get-GridAuthorizationGrantDirectory -StoreRoot $StoreRoot -GrantId $GrantId
    $record = Invoke-GridAuthorizationGrantLock -GrantDirectory $directory -Action {
        $grantPath = Join-Path $directory 'grant.v2.json'
        $grant = Get-Content -LiteralPath $grantPath -Raw | ConvertFrom-Json -ErrorAction Stop
        if ([string]$grant.state -cne 'Executing' -or $null -eq $grant.lease -or [string]$grant.lease.leaseId -cne $LeaseId) { throw 'AuthorizationLeaseMismatch: only the current exclusive lease can finish consumption.' }
        $now = [DateTimeOffset]::UtcNow.ToString('o')
        if ($Result -eq 'Consumed') {
            $record = [pscustomobject][ordered]@{ schemaVersion=1; consumptionId='consumption-' + [Guid]::NewGuid().ToString('N'); grantId=$GrantId; leaseId=$LeaseId; state='Consumed'; semanticBindingSha256=[string]$grant.semanticBindingSha256; completedAt=$now; detail=$Detail }
            Write-GridAuthorizationJsonAtomic -Value $record -LiteralPath (Join-Path $directory 'consumption.v1.json')
            $grant.consumption = $record; $grant.failure = $null
        }
        else {
            $record = [pscustomobject][ordered]@{ schemaVersion=1; failureId='failure-' + [Guid]::NewGuid().ToString('N'); grantId=$GrantId; leaseId=$LeaseId; state='Failed'; semanticBindingSha256=[string]$grant.semanticBindingSha256; failedAt=$now; detail=$Detail }
            Write-GridAuthorizationJsonAtomic -Value $record -LiteralPath (Join-Path $directory 'failure.v1.json')
            $grant.failure = $record; $grant.consumption = $null
        }
        $leasePath = Join-Path $directory 'lease.v1.json'
        $lease = if (Test-Path -LiteralPath $leasePath -PathType Leaf) { Get-Content -LiteralPath $leasePath -Raw | ConvertFrom-Json } else { $null }
        if ($lease) { $lease.state = $Result; $lease.completedAt = $now; Write-GridAuthorizationJsonAtomic -Value $lease -LiteralPath $leasePath }
        $grant.state = $Result; $grant.lease = [pscustomobject][ordered]@{ leaseId=$LeaseId; consumerId=[string]$grant.lease.consumerId; acquiredAt=[string]$grant.lease.acquiredAt; expiresAt=[string]$grant.lease.expiresAt; completedAt=$now }
        Write-GridAuthorizationJsonAtomic -Value $grant -LiteralPath $grantPath
        $record
    }
    $record
}

function Test-GridAuthorizationHistoricalRecord {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Record)
    if ($null -eq $Record.PSObject.Properties['schemaVersion']) { return [pscustomobject]@{ IsCompatible=$false; Executable=$false; Reason='Missing schemaVersion.' } }
    if ([int]$Record.schemaVersion -eq 1) { return [pscustomobject]@{ IsCompatible=$true; Executable=$false; Reason='Historical v1 record is readable but cannot confer current execution authority.' } }
    [pscustomobject]@{ IsCompatible=([int]$Record.schemaVersion -eq 2); Executable=([int]$Record.schemaVersion -eq 2); Reason=$null }
}

function New-GridAuthorizationSemanticBinding {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ActorId,
        [Parameter(Mandatory)][string]$SessionId,
        [Parameter(Mandatory)][string]$WorkspaceId,
        [Parameter(Mandatory)][string]$RequestId,
        [Parameter(Mandatory)][string]$SubmissionId,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$EnvelopeSha256,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$PlanSha256,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ScopeSha256,
        [Parameter(Mandatory)][string]$ProposalOrSpecificationId,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ProposalOrSpecificationSha256,
        [Parameter(Mandatory)][object[]]$Capabilities,
        [Parameter(Mandatory)][string[]]$Targets,
        [Parameter(Mandatory)]$NormalizedInput
    )
    foreach ($pair in @(@('ActorId',$ActorId),@('SessionId',$SessionId),@('WorkspaceId',$WorkspaceId),@('RequestId',$RequestId),@('SubmissionId',$SubmissionId),@('ProposalOrSpecificationId',$ProposalOrSpecificationId))) { Assert-GridAuthorizationId -Value ([string]$pair[1]) -Name ([string]$pair[0]) }
    if (@($Capabilities).Count -eq 0) { throw 'AuthorizationBindingInvalid: at least one capability/version binding is required.' }
    [pscustomobject][ordered]@{
        schemaVersion=1; actorId=$ActorId; sessionId=$SessionId; workspaceId=$WorkspaceId; requestId=$RequestId; submissionId=$SubmissionId
        envelopeSha256=$EnvelopeSha256.ToUpperInvariant(); planSha256=$PlanSha256.ToUpperInvariant(); scopeSha256=$ScopeSha256.ToUpperInvariant()
        proposalOrSpecificationId=$ProposalOrSpecificationId; proposalOrSpecificationSha256=$ProposalOrSpecificationSha256.ToUpperInvariant()
        capabilities=@($Capabilities | ForEach-Object { [pscustomobject][ordered]@{ capabilityId=[string]$_.capabilityId; capabilityVersion=[string]$_.capabilityVersion; adapterId=if($_.PSObject.Properties['adapterId']){[string]$_.adapterId}else{$null}; adapterVersion=if($_.PSObject.Properties['adapterVersion']){[string]$_.adapterVersion}else{$null} } } | Sort-Object capabilityId,capabilityVersion,adapterId)
        targets=@($Targets | ForEach-Object { [string]$_ } | Sort-Object -Unique)
        normalizedInputSha256=Get-GridCanonicalJsonSha256 -InputObject $NormalizedInput
    }
}
