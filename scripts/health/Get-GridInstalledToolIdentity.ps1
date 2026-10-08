#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $ExecutablePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolved = [System.IO.Path]::GetFullPath($ExecutablePath)
if (-not [System.IO.File]::Exists($resolved)) { throw "The selected executable does not exist." }
if (-not [System.StringComparer]::OrdinalIgnoreCase.Equals([System.IO.Path]::GetExtension($resolved), '.exe')) { throw "The selected file is not an executable." }

$item = Get-Item -LiteralPath $resolved -Force
$hash = Get-FileHash -LiteralPath $resolved -Algorithm SHA256
$version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($resolved)
$signature = Get-AuthenticodeSignature -LiteralPath $resolved
$observedAt = [DateTimeOffset]::UtcNow

$evidence = @(
    [ordered]@{ kind = 'FileHash'; sourceIdentifier = $resolved; claim = 'SHA-256 of the exact selected executable'; value = $hash.Hash.ToLowerInvariant() },
    [ordered]@{ kind = 'FileMetadata'; sourceIdentifier = $resolved; claim = 'Exact executable length'; value = [string]$item.Length },
    [ordered]@{ kind = 'FileMetadata'; sourceIdentifier = $resolved; claim = 'Exact executable last-write time'; value = $item.LastWriteTimeUtc.ToString('O') },
    [ordered]@{ kind = 'VersionResource'; sourceIdentifier = $resolved; claim = 'Executable product name'; value = [string]$version.ProductName },
    [ordered]@{ kind = 'VersionResource'; sourceIdentifier = $resolved; claim = 'Executable file version'; value = [string]$version.FileVersion },
    [ordered]@{ kind = 'Authenticode'; sourceIdentifier = $resolved; claim = 'Authenticode signature status'; value = [string]$signature.Status }
)

[ordered]@{
    schemaVersion = 1
    executablePath = $resolved
    sha256 = $hash.Hash.ToLowerInvariant()
    productName = if ([string]::IsNullOrWhiteSpace($version.ProductName)) { $null } else { $version.ProductName }
    companyName = if ([string]::IsNullOrWhiteSpace($version.CompanyName)) { $null } else { $version.CompanyName }
    fileVersion = if ([string]::IsNullOrWhiteSpace($version.FileVersion)) { $null } else { $version.FileVersion }
    productVersion = if ([string]::IsNullOrWhiteSpace($version.ProductVersion)) { $null } else { $version.ProductVersion }
    signatureStatus = [string]$signature.Status
    signatureSubject = if ($null -eq $signature.SignerCertificate) { $null } else { $signature.SignerCertificate.Subject }
    signatureThumbprint = if ($null -eq $signature.SignerCertificate) { $null } else { $signature.SignerCertificate.Thumbprint }
    observedAtUtc = $observedAt.ToString('O')
    evidence = $evidence
} | ConvertTo-Json -Depth 8 -Compress
