$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Get-GridProvenanceRepositoryRoot {
    [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
}

function Read-GridProvenanceJson {
    param([Parameter(Mandatory=$true)][string]$LiteralPath)
    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) {
        throw "ProvenanceRequiredFileMissing: $LiteralPath"
    }
    try { Get-Content -LiteralPath $LiteralPath -Raw | ConvertFrom-Json }
    catch { throw "ProvenanceJsonInvalid: '$LiteralPath'. $($_.Exception.Message)" }
}

function ConvertTo-GridNormalizedRelativePath {
    param([Parameter(Mandatory=$true)][string]$RepositoryRoot, [Parameter(Mandatory=$true)][string]$LiteralPath)
    $root = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $full = [System.IO.Path]::GetFullPath($LiteralPath)
    if (-not $full.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "ProvenancePathOutsideRepository: $LiteralPath"
    }
    $full.Substring($root.Length + 1).Replace('\','/')
}

function ConvertTo-GridGlobRegex {
    param([Parameter(Mandatory=$true)][string]$Pattern)
    $normalized = $Pattern.Replace('\','/')
    $escaped = [regex]::Escape($normalized)
    $escaped = $escaped.Replace('\*\*', '.*').Replace('\*', '[^/]*').Replace('\?', '[^/]')
    '^' + $escaped + '$'
}

function Test-GridPathMatchesPattern {
    param([Parameter(Mandatory=$true)][string]$RelativePath, [Parameter(Mandatory=$true)][string]$Pattern)
    $RelativePath.Replace('\','/') -match (ConvertTo-GridGlobRegex -Pattern $Pattern)
}

function Test-GridIgnoredRepositoryPath {
    param([Parameter(Mandatory=$true)][string]$RelativePath)
    $path = $RelativePath.Replace('\','/')
    foreach ($pattern in @(
        '.git/**', '.vs/**', 'artifacts/**', '**/bin/**', '**/obj/**', '**/node_modules/**',
        'tmp/**', 'Grid.exe', 'debug.log', '*.user', '*.suo', '*.env', '**/*.env'
    )) {
        if (Test-GridPathMatchesPattern -RelativePath $path -Pattern $pattern) { return $true }
    }
    return $false
}

function Get-GridGovernedSourceFiles {
    param([Parameter(Mandatory=$true)][string]$RepositoryRoot)
    $extensions = @('.cs','.xaml','.ps1','.psm1','.psd1','.pas','.json','.xml','.props','.targets','.csproj','.sln','.iss','.md')
    @(Get-ChildItem -LiteralPath $RepositoryRoot -File -Recurse -Force | ForEach-Object {
        $relative = ConvertTo-GridNormalizedRelativePath -RepositoryRoot $RepositoryRoot -LiteralPath $_.FullName
        if (-not (Test-GridIgnoredRepositoryPath -RelativePath $relative) -and $extensions -contains $_.Extension.ToLowerInvariant()) {
            [pscustomobject]@{ FullName=$_.FullName; RelativePath=$relative; Length=$_.Length; LastWriteTimeUtc=$_.LastWriteTimeUtc }
        }
    } | Sort-Object RelativePath)
}

function Get-GridGovernedAssetFiles {
    param([Parameter(Mandatory=$true)][string]$RepositoryRoot)
    $extensions = @('.png','.jpg','.jpeg','.gif','.ico','.svg','.bmp','.webp','.woff','.woff2','.ttf','.otf')
    @(Get-ChildItem -LiteralPath $RepositoryRoot -File -Recurse -Force | ForEach-Object {
        $relative = ConvertTo-GridNormalizedRelativePath -RepositoryRoot $RepositoryRoot -LiteralPath $_.FullName
        if (-not (Test-GridIgnoredRepositoryPath -RelativePath $relative) -and $extensions -contains $_.Extension.ToLowerInvariant()) {
            [pscustomobject]@{ FullName=$_.FullName; RelativePath=$relative; Length=$_.Length; LastWriteTimeUtc=$_.LastWriteTimeUtc }
        }
    } | Sort-Object RelativePath)
}

function Get-GridPathLedgerMatches {
    param([Parameter(Mandatory=$true)][string]$RelativePath, [Parameter(Mandatory=$true)]$Ledger)
    @($Ledger.entries | Where-Object {
        $entry = $_
        $matched = $false
        foreach ($pattern in @($entry.paths)) {
            if (Test-GridPathMatchesPattern -RelativePath $RelativePath -Pattern ([string]$pattern)) {
                $matched = $true
                break
            }
        }
        $matched
    })
}

function Get-GridGitHistoryState {
    param([Parameter(Mandatory=$true)][string]$RepositoryRoot)
    $gitDirectory = Join-Path $RepositoryRoot '.git'
    if (-not (Test-Path -LiteralPath $gitDirectory)) { return 'Unavailable' }
    try {
        & git -C $RepositoryRoot rev-parse --verify HEAD 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { return 'Available' }
    } catch { }
    'Unavailable'
}

function Get-GridSha256 {
    param([Parameter(Mandatory=$true)][string]$LiteralPath)
    (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-GridUtf8Json {
    param([Parameter(Mandatory=$true)]$InputObject, [Parameter(Mandatory=$true)][string]$LiteralPath, [int]$Depth=12)
    $parent = Split-Path -Parent $LiteralPath
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { [void](New-Item -ItemType Directory -Force -Path $parent) }
    $json = $InputObject | ConvertTo-Json -Depth $Depth
    [System.IO.File]::WriteAllText($LiteralPath, $json + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
}
