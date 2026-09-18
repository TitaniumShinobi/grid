[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$OutputPath,
    [switch]$ExcludeGitHistory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'Grid.Provenance.ps1')
. (Join-Path $PSScriptRoot 'Test-GridProvenance.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Get-GridProvenanceRepositoryRoot }
$RepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path ([Environment]::GetFolderPath('UserProfile')) ("Downloads/grid-attorney-audit-{0}.zip" -f [DateTime]::UtcNow.ToString('yyyy-MM-dd'))
}
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
if ($OutputPath.StartsWith($RepositoryRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'AttorneyExportPathInvalid: Output ZIP must be outside the repository.'
}

$sourceGate = Test-GridProvenanceState -Mode Source -RepositoryRoot $RepositoryRoot
if ($sourceGate.status -ne 'Passed') {
    $details = @($sourceGate.violations | ForEach-Object { "$($_.code):$($_.path)" }) -join '; '
    throw "AttorneyExportProvenanceGateFailed: $details"
}

$gitState = Get-GridGitHistoryState -RepositoryRoot $RepositoryRoot
$includeGit = ($gitState -eq 'Available' -and -not $ExcludeGitHistory)
$excluded = New-Object System.Collections.ArrayList
$included = New-Object System.Collections.ArrayList
$secretFindings = New-Object System.Collections.ArrayList

function Test-GridAttorneyExportExcluded([string]$RelativePath) {
    foreach ($pattern in @(
        '.vs/**','artifacts/**','**/bin/**','**/obj/**','**/node_modules/**','tmp/**',
        'Grid.exe','debug.log','*.user','*.suo','*.env','**/*.env','*.pfx','**/*.pfx','*.p12','**/*.p12',
        '*.pem','**/*.pem','id_rsa','**/id_rsa','id_ed25519','**/id_ed25519','**/credentials.json',
        '**/secrets.json','**/appsettings.*.json'
    )) { if (Test-GridPathMatchesPattern -RelativePath $RelativePath -Pattern $pattern) { return $true } }
    if ($RelativePath -like '.git/*' -and -not $includeGit) { return $true }
    return $false
}

$candidateFiles = @(Get-ChildItem -LiteralPath $RepositoryRoot -File -Recurse -Force | Sort-Object FullName)
foreach ($file in $candidateFiles) {
    $relative = ConvertTo-GridNormalizedRelativePath -RepositoryRoot $RepositoryRoot -LiteralPath $file.FullName
    if (Test-GridAttorneyExportExcluded $relative) { [void]$excluded.Add($relative); continue }
    if ($file.Length -le 5242880 -and @('.cs','.xaml','.ps1','.psm1','.psd1','.json','.xml','.md','.txt','.yml','.yaml','.props','.targets','.csproj','.sln','.iss','.pas') -contains $file.Extension.ToLowerInvariant()) {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($rule in @(
            @{ Name='PrivateKey'; Pattern='-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----' },
            @{ Name='GitHubToken'; Pattern='\bgh[pousr]_[A-Za-z0-9]{30,}\b' },
            @{ Name='OpenAiStyleSecret'; Pattern='\bsk-[A-Za-z0-9_-]{32,}\b' }
        )) {
            if ($text -match $rule.Pattern) { [void]$secretFindings.Add([pscustomobject]@{ path=$relative; rule=$rule.Name }) }
        }
    }
    [void]$included.Add([pscustomobject]@{ FullName=$file.FullName; RelativePath=$relative; Length=$file.Length; Sha256=(Get-GridSha256 -LiteralPath $file.FullName) })
}
if ($secretFindings.Count -gt 0) {
    $details = @($secretFindings | ForEach-Object { "$($_.rule):$($_.path)" }) -join '; '
    throw "AttorneyExportSecretScanFailed: $details"
}

$parent = Split-Path -Parent $OutputPath
if (-not (Test-Path -LiteralPath $parent -PathType Container)) { [void](New-Item -ItemType Directory -Force -Path $parent) }
if (Test-Path -LiteralPath $OutputPath) { throw "AttorneyExportAlreadyExists: $OutputPath" }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stream = [IO.File]::Open($OutputPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($file in @($included)) {
            $entry = $archive.CreateEntry(('grid/' + $file.RelativePath), [IO.Compression.CompressionLevel]::Optimal)
            $entryStream = $entry.Open()
            $input = [IO.File]::OpenRead($file.FullName)
            try { $input.CopyTo($entryStream) } finally { $input.Dispose(); $entryStream.Dispose() }
        }
        $baselineLimitation = if ($gitState -eq 'Unavailable') { 'No Git commits or branches were available; the working snapshot is the first formal provenance baseline.' } else { $null }
        $manifest = [ordered]@{
            schemaVersion=1
            exportId='grid.attorney-audit-export.v1'
            generatedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
            gitHistoryAvailable=($gitState -eq 'Available')
            gitHistoryIncluded=$includeGit
            baselineLimitation=$baselineLimitation
            includedFileCount=$included.Count
            excludedPaths=@($excluded)
            files=@($included | ForEach-Object { [ordered]@{ path=('grid/' + $_.RelativePath); length=[int64]$_.Length; sha256=$_.Sha256 } })
        }
        $entry = $archive.CreateEntry('attorney-export-manifest.v1.json', [IO.Compression.CompressionLevel]::Optimal)
        $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
        try { $writer.Write(($manifest | ConvertTo-Json -Depth 8)) } finally { $writer.Dispose() }
    } finally { $archive.Dispose() }
} catch {
    $stream.Dispose()
    if (Test-Path -LiteralPath $OutputPath) { Remove-Item -LiteralPath $OutputPath -Force }
    throw
} finally { if ($null -ne $stream) { $stream.Dispose() } }
Write-Host "PASS: Created sanitized Grid attorney audit export at $OutputPath"
Write-Host "Git history available=$($gitState -eq 'Available'); included=$includeGit; files=$($included.Count)"
