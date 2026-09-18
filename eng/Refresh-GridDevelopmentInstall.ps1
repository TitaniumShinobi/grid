[CmdletBinding()]
param(
    [switch]$AuthorizeInstalledRefresh,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version 2.0

if (-not $AuthorizeInstalledRefresh) {
    throw 'DevelopmentInstallAuthorizationRequired: pass -AuthorizeInstalledRefresh after reviewing the fixed target and rollback behavior.'
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$stageRoot = Join-Path $artifactsRoot 'development-install\win-x64'
$appProject = Join-Path $repositoryRoot 'src\Grid.App\Grid.App.csproj'
$diagnosticsProject = Join-Path $repositoryRoot 'src\Grid.Diagnostics\Grid.Diagnostics.csproj'
$diagnosticsStage = Join-Path $stageRoot 'RequestEngine\bin'
$installRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\Grid'))
$expectedInstallRoot = [IO.Path]::GetFullPath("$env:LOCALAPPDATA\Programs\Grid")
$shortcutPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Grid.lnk'
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$rollbackRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "Programs\Grid.rollback-$stamp"))
$installedExecutable = Join-Path $installRoot 'Grid.exe'

if (-not [string]::Equals($installRoot,$expectedInstallRoot,[StringComparison]::OrdinalIgnoreCase)) {
    throw "DevelopmentInstallTargetInvalid: '$installRoot'."
}
foreach ($path in @($repositoryRoot,$stageRoot,$installRoot,$rollbackRoot)) {
    if (Test-Path -LiteralPath $path) {
        $item = Get-Item -LiteralPath $path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "DevelopmentInstallReparsePointRefused: '$path'."
        }
    }
}

$running = @(Get-Process -Name Grid -ErrorAction SilentlyContinue | Where-Object {
    try { [IO.Path]::GetFullPath($_.Path).Equals($installedExecutable,[StringComparison]::OrdinalIgnoreCase) }
    catch { $false }
})
if ($running.Count -gt 0) {
    throw ('DevelopmentInstallInUse: close the Start-menu Grid application first. PID(s): ' + (($running | ForEach-Object Id) -join ', '))
}

if (-not $NoRestore) {
    & dotnet restore $appProject --runtime win-x64 /p:Platform=x64 /p:PublishProfile=win-x64
    if ($LASTEXITCODE -ne 0) { throw "DevelopmentInstallRestoreFailed: Grid.App exit code $LASTEXITCODE." }
    & dotnet restore $diagnosticsProject --runtime win-x64 /p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw "DevelopmentInstallRestoreFailed: Grid.Diagnostics exit code $LASTEXITCODE." }
}

if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Path $stageRoot,$diagnosticsStage -Force | Out-Null

& dotnet publish $appProject --configuration Release --runtime win-x64 --self-contained true --no-restore --output $stageRoot `
    /p:Platform=x64 /p:PublishProfile=win-x64 /p:WindowsAppSDKSelfContained=true /p:PublishSingleFile=false /p:PublishTrimmed=false
if ($LASTEXITCODE -ne 0) { throw "DevelopmentInstallPublishFailed: Grid.App exit code $LASTEXITCODE." }

& dotnet publish $diagnosticsProject --configuration Release --runtime win-x64 --self-contained true --no-restore --output $diagnosticsStage `
    /p:Platform=x64 /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:PublishTrimmed=false /p:DebugSymbols=false /p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw "DevelopmentInstallPublishFailed: Grid.Diagnostics exit code $LASTEXITCODE." }

$publishedScripts = Join-Path $stageRoot 'RequestEngine\scripts'
$nextScripts = Join-Path $stageRoot 'RequestEngine\scripts.next'
if (Test-Path -LiteralPath $nextScripts) { Remove-Item -LiteralPath $nextScripts -Recurse -Force }
New-Item -ItemType Directory -Path $nextScripts -Force | Out-Null
Copy-Item -Path (Join-Path $repositoryRoot 'scripts\*') -Destination $nextScripts -Recurse -Force

$scriptsSwapped = $false
for ($attempt = 1; $attempt -le 10 -and -not $scriptsSwapped; $attempt++) {
    try {
        if (Test-Path -LiteralPath $publishedScripts) {
            Remove-Item -LiteralPath $publishedScripts -Recurse -Force
        }
        Move-Item -LiteralPath $nextScripts -Destination $publishedScripts
        $scriptsSwapped = $true
    }
    catch {
        if ($attempt -eq 10) {
            throw "DevelopmentInstallScriptStageLocked: unable to replace the staged RequestEngine scripts after $attempt attempts. $($_.Exception.Message)"
        }
        Start-Sleep -Milliseconds 500
    }
}

foreach ($required in @('Grid.exe','Grid.Core.dll','Grid.Mo2.dll','Grid.pri','RequestEngine\bin\Grid.Diagnostics.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $stageRoot $required) -PathType Leaf)) {
        throw "DevelopmentInstallPayloadIncomplete: '$required'."
    }
}

$movedExisting = $false
try {
    if (Test-Path -LiteralPath $installRoot) {
        Move-Item -LiteralPath $installRoot -Destination $rollbackRoot
        $movedExisting = $true
    }
    New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $stageRoot '*') -Destination $installRoot -Recurse -Force
    if (-not (Test-Path -LiteralPath $installedExecutable -PathType Leaf)) {
        throw 'DevelopmentInstallVerificationFailed: installed Grid.exe is missing.'
    }

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $installedExecutable
    $shortcut.WorkingDirectory = $installRoot
    $shortcut.IconLocation = "$installedExecutable,0"
    $shortcut.Save()
    $verifiedShortcut = $shell.CreateShortcut($shortcutPath)
    if (-not [IO.Path]::GetFullPath($verifiedShortcut.TargetPath).Equals($installedExecutable,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'DevelopmentInstallVerificationFailed: Start-menu shortcut target differs from installed Grid.exe.'
    }
}
catch {
    $failure = $_
    if (Test-Path -LiteralPath $installRoot) { Remove-Item -LiteralPath $installRoot -Recurse -Force }
    if ($movedExisting -and (Test-Path -LiteralPath $rollbackRoot)) {
        Move-Item -LiteralPath $rollbackRoot -Destination $installRoot
    }
    throw "DevelopmentInstallFailed: $($failure.Exception.Message) Automatic rollback attempted."
}

Write-Host "Installed development Grid: $installedExecutable"
Write-Host "Start-menu shortcut:       $shortcutPath"
if ($movedExisting) { Write-Host "Rollback installation:     $rollbackRoot" }
Write-Host 'Grid connection state was not modified.'
