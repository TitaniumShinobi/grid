[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$launcherProject = Join-Path $repositoryRoot 'src\Grid.DevLauncher\Grid.DevLauncher.csproj'
$launcherArtifacts = Join-Path $artifactsRoot 'root-launcher'
$publishDirectory = Join-Path $launcherArtifacts 'publish'
$recoveryDirectory = Join-Path $artifactsRoot 'recovery\root-launcher'
$rootExecutable = Join-Path $repositoryRoot 'Grid.exe'
$stagedExecutable = Join-Path $repositoryRoot '.Grid.exe.next'
$atomicBackup = Join-Path $recoveryDirectory (".replace-$PID-{0}.bak" -f [Guid]::NewGuid().ToString('N'))

function Assert-SafeArtifactDirectory {
    param([Parameter(Mandatory)] [string]$Path)

    $resolved = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $resolvedArtifacts = $artifactsRoot.TrimEnd('\')
    if (-not $resolved.StartsWith("$resolvedArtifacts\", [System.StringComparison]::OrdinalIgnoreCase) -or
        $resolved -eq $resolvedArtifacts -or
        $resolved -eq $repositoryRoot.TrimEnd('\')) {
        throw "Refusing to operate on an unexpected generated directory: $resolved"
    }

    $cursor = $resolved
    while ($cursor.StartsWith($resolvedArtifacts, [System.StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to operate through a reparse point: $cursor"
            }
        }
        if ($cursor -eq $resolvedArtifacts) { break }
        $cursor = Split-Path $cursor -Parent
    }
}

function Get-RootLauncherProcess {
    Get-Process -Name 'Grid' -ErrorAction SilentlyContinue | Where-Object {
        try {
            [System.IO.Path]::GetFullPath($_.Path).Equals(
                $rootExecutable,
                [System.StringComparison]::OrdinalIgnoreCase)
        }
        catch {
            $false
        }
    }
}

function Backup-RootExecutable {
    if (-not (Test-Path -LiteralPath $rootExecutable -PathType Leaf)) { return $null }

    Assert-SafeArtifactDirectory -Path $recoveryDirectory
    New-Item -ItemType Directory -Path $recoveryDirectory -Force | Out-Null
    $source = Get-Item -LiteralPath $rootExecutable
    $hash = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash
    $backupName = "Grid-$hash.exe"
    $backupPath = Join-Path $recoveryDirectory $backupName
    $manifestPath = Join-Path $recoveryDirectory "Grid-$hash.json"

    if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
        $existingHash = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash
        if ($existingHash -ne $hash) {
            throw "Recovery backup hash mismatch: $backupPath"
        }
    }
    else {
        Copy-Item -LiteralPath $source.FullName -Destination $backupPath
    }

    $manifest = [ordered]@{
        SchemaVersion = 1
        OriginalPath = $source.FullName
        BackupPath = $backupPath
        Length = $source.Length
        Sha256 = $hash
        OriginalLastWriteTimeUtc = $source.LastWriteTimeUtc.ToString('O')
        RecoveredAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8
    return [pscustomobject]@{ Path = $backupPath; Manifest = $manifestPath; Sha256 = $hash }
}

Assert-SafeArtifactDirectory -Path $launcherArtifacts
Assert-SafeArtifactDirectory -Path $publishDirectory
Assert-SafeArtifactDirectory -Path $recoveryDirectory

$runningRoot = @(Get-RootLauncherProcess)
if ($runningRoot.Count -gt 0) {
    $processList = ($runningRoot | ForEach-Object { "PID $($_.Id)" }) -join ', '
    throw "Close the repository-root Grid.exe before rebuilding the launcher ($processList). No process was stopped."
}

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

& dotnet publish $launcherProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --output $publishDirectory `
    /p:PublishSingleFile=true `
    /p:PublishTrimmed=false `
    /p:DebugSymbols=false `
    /p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw "Root launcher publish failed with exit code $LASTEXITCODE." }

$publishedExecutable = Join-Path $publishDirectory 'Grid.RootLauncher.exe'
if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
    throw "Published root launcher is missing: $publishedExecutable"
}

$backup = Backup-RootExecutable

if (Test-Path -LiteralPath $stagedExecutable) {
    Remove-Item -LiteralPath $stagedExecutable -Force
}
try {
    Copy-Item -LiteralPath $publishedExecutable -Destination $stagedExecutable
    $publishedHash = (Get-FileHash -LiteralPath $publishedExecutable -Algorithm SHA256).Hash
    $stagedHash = (Get-FileHash -LiteralPath $stagedExecutable -Algorithm SHA256).Hash
    if ($publishedHash -ne $stagedHash) {
        throw 'The staged root launcher does not match the published launcher.'
    }

    if (Test-Path -LiteralPath $rootExecutable -PathType Leaf) {
        [System.IO.File]::Replace($stagedExecutable, $rootExecutable, $atomicBackup, $true)
    }
    else {
        Move-Item -LiteralPath $stagedExecutable -Destination $rootExecutable
    }
}
finally {
    if (Test-Path -LiteralPath $stagedExecutable) {
        Remove-Item -LiteralPath $stagedExecutable -Force
    }
}

$rootHash = (Get-FileHash -LiteralPath $rootExecutable -Algorithm SHA256).Hash
if ($rootHash -ne (Get-FileHash -LiteralPath $publishedExecutable -Algorithm SHA256).Hash) {
    throw 'The repository-root Grid.exe does not match the published launcher.'
}
if (Test-Path -LiteralPath $atomicBackup -PathType Leaf) {
    Remove-Item -LiteralPath $atomicBackup -Force
}

Write-Host "Root launcher: $rootExecutable"
Write-Host "Debug target:  src\Grid.App\bin\x64\Debug\net9.0-windows10.0.19041.0\win-x64\Grid.exe"
if ($null -ne $backup) {
    Write-Host "Recovery copy: $($backup.Path)"
    Write-Host "Manifest:      $($backup.Manifest)"
}
