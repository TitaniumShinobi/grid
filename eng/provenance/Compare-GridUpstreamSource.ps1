[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$UpstreamRoot,
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9._-]+$')][string]$UpstreamName,
    [string]$RepositoryRoot,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'Grid.Provenance.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Get-GridProvenanceRepositoryRoot }
$RepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
$UpstreamRoot = [System.IO.Path]::GetFullPath($UpstreamRoot)
if (-not (Test-Path -LiteralPath $UpstreamRoot -PathType Container)) { throw "UpstreamRootMissing: $UpstreamRoot" }
if ($UpstreamRoot.StartsWith($RepositoryRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'UpstreamRootMustBeIndependent: Upstream source cannot be inside the Grid repository.' }
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $RepositoryRoot ("artifacts/provenance/upstream-{0}-comparison.v1.json" -f $UpstreamName.ToLowerInvariant()) }

$extensions = @('.cs','.cpp','.c','.h','.hpp','.ts','.tsx','.js','.jsx','.ps1','.psm1','.xaml','.xml','.json','.pas')
function Get-NormalizedTextHash([string]$Path) {
    $text = Get-Content -LiteralPath $Path -Raw
    $normalized = [regex]::Replace($text, '\s+', '').ToLowerInvariant()
    $bytes = [Text.Encoding]::UTF8.GetBytes($normalized)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
}

$upstreamIndex = @{}
Get-ChildItem -LiteralPath $UpstreamRoot -File -Recurse -Force | Where-Object { $extensions -contains $_.Extension.ToLowerInvariant() -and $_.Length -gt 0 } | ForEach-Object {
    $hash = Get-NormalizedTextHash $_.FullName
    if (-not $upstreamIndex.ContainsKey($hash)) { $upstreamIndex[$hash] = New-Object System.Collections.ArrayList }
    $relativeUpstreamPath = $_.FullName.Substring($UpstreamRoot.Length).TrimStart([char[]]@([char]'\',[char]'/')).Replace('\','/')
    [void]$upstreamIndex[$hash].Add($relativeUpstreamPath)
}
$matches = New-Object System.Collections.ArrayList
Get-GridGovernedSourceFiles -RepositoryRoot $RepositoryRoot | Where-Object { $extensions -contains ([IO.Path]::GetExtension($_.RelativePath).ToLowerInvariant()) -and $_.Length -gt 0 } | ForEach-Object {
    $hash = Get-NormalizedTextHash $_.FullName
    if ($upstreamIndex.ContainsKey($hash)) {
        [void]$matches.Add([ordered]@{ gridPath=$_.RelativePath; normalizedSha256=$hash; upstreamPaths=@($upstreamIndex[$hash]) })
    }
}
$result = [ordered]@{
    schemaVersion=1
    comparisonId='grid.upstream-exact-normalized-comparison.v1'
    generatedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
    upstreamName=$UpstreamName
    upstreamRoot=$UpstreamRoot
    method='Whitespace-insensitive, case-insensitive whole-file SHA-256. A non-match does not establish independent authorship.'
    matchCount=$matches.Count
    matches=@($matches)
}
Write-GridUtf8Json -InputObject $result -LiteralPath $OutputPath -Depth 8
Write-Host "PASS: Upstream comparison completed; exact normalized matches=$($matches.Count); report=$OutputPath"
