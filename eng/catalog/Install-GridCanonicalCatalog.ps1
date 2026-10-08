[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$SourceCatalogPath,
    [Parameter(Mandatory)][ValidatePattern('^grid\.catalog-package\.v[0-9]+\.sha256\.[0-9a-f]{64}$')][string]$PackageId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedSha256,
    [switch]$AllowCandidate,
    [string]$GridDataRoot = (Join-Path $env:LOCALAPPDATA 'Grid')
)

$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($SourceCatalogPath)
if (-not [IO.File]::Exists($source)) { throw "CanonicalCatalogSourceMissing: $source" }
$actualSha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToUpperInvariant()
if ($actualSha256 -cne $ExpectedSha256.ToUpperInvariant()) {
    throw "CanonicalCatalogDigestMismatch: expected $($ExpectedSha256.ToUpperInvariant()); observed $actualSha256"
}

$baseRoot = [IO.Path]::GetFullPath($GridDataRoot)
$catalogRoot = [IO.Path]::GetFullPath((Join-Path $baseRoot 'catalogs'))
if (-not $catalogRoot.StartsWith($baseRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'CanonicalCatalogTargetEscapedGridDataRoot'
}
$destination = Join-Path $catalogRoot 'shared-canonical-library.v5.json'
$bindingPath = Join-Path $catalogRoot 'canonical-runtime-binding.v1.json'
$operation = "Install verified catalog $actualSha256 and bind package $PackageId"
if (-not $PSCmdlet.ShouldProcess($catalogRoot, $operation)) { return }

[IO.Directory]::CreateDirectory($catalogRoot) | Out-Null
$catalogTemporary = Join-Path $catalogRoot ('.shared-canonical-library.' + [Guid]::NewGuid().ToString('N') + '.tmp')
$bindingTemporary = Join-Path $catalogRoot ('.canonical-runtime-binding.' + [Guid]::NewGuid().ToString('N') + '.tmp')
$catalogBackup = $null
$bindingBackup = $null
try {
    [IO.File]::Copy($source, $catalogTemporary, $false)
    $copiedSha256 = (Get-FileHash -LiteralPath $catalogTemporary -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($copiedSha256 -cne $actualSha256) { throw 'CanonicalCatalogCopyDigestMismatch' }

    $binding = [ordered]@{
        schemaVersion = 1
        catalogStoreFileName = 'shared-canonical-library.v5.json'
        allowCandidatePackages = [bool]$AllowCandidate
        packageId = $PackageId
    }
    $bindingJson = $binding | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText($bindingTemporary, $bindingJson + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

    if ([IO.File]::Exists($destination)) {
        $catalogBackup = $destination + '.rollback-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
        [IO.File]::Replace($catalogTemporary, $destination, $catalogBackup, $true)
    } else {
        [IO.File]::Move($catalogTemporary, $destination)
    }
    if ([IO.File]::Exists($bindingPath)) {
        $bindingBackup = $bindingPath + '.rollback-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
        [IO.File]::Replace($bindingTemporary, $bindingPath, $bindingBackup, $true)
    } else {
        [IO.File]::Move($bindingTemporary, $bindingPath)
    }

    $installedSha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($installedSha256 -cne $actualSha256) { throw 'InstalledCanonicalCatalogDigestMismatch' }
    [pscustomobject][ordered]@{
        schemaVersion = 1
        status = 'Installed'
        catalogPath = $destination
        catalogSha256 = $installedSha256
        bindingPath = $bindingPath
        packageId = $PackageId
        allowCandidatePackages = [bool]$AllowCandidate
        catalogRollbackPath = $catalogBackup
        bindingRollbackPath = $bindingBackup
        changedAuthenticationState = $false
        changedCanonicalKnowledge = $false
    }
} finally {
    if ([IO.File]::Exists($catalogTemporary)) { [IO.File]::Delete($catalogTemporary) }
    if ([IO.File]::Exists($bindingTemporary)) { [IO.File]::Delete($bindingTemporary) }
}
