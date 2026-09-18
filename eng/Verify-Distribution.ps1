[CmdletBinding()]
param(
    [string]$PublishDirectory,
    [string]$InstallerPath,
    [switch]$ExerciseInstaller,
    [switch]$LaunchPublished,
    [switch]$LaunchInstalled,
    [string[]]$ReadOnlyExternalRoots = @()
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $PublishDirectory = Join-Path $repositoryRoot 'artifacts\publish\win-x64'
}
if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $InstallerPath = Join-Path $repositoryRoot 'artifacts\installer\GridSetup.exe'
}
$PublishDirectory = [System.IO.Path]::GetFullPath($PublishDirectory)
$InstallerPath = [System.IO.Path]::GetFullPath($InstallerPath)
$expectedInstallDirectory = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\Grid'))
$startMenuShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Grid.lnk'
$connectionStore = Join-Path $env:LOCALAPPDATA 'Grid'
$uninstallRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F9C0D7A-6A31-4C89-AB6C-4FEE8C6EA3E2}_is1'

$provenanceGate = Join-Path $repositoryRoot 'eng\provenance\Test-GridProvenance.ps1'
& $provenanceGate -Mode Distribution -RepositoryRoot $repositoryRoot

function Get-RelativeChildPath {
    param(
        [Parameter(Mandatory)] [string]$Root,
        [Parameter(Mandatory)] [string]$Path
    )

    $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith("$fullRoot\", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the expected root: '$fullPath'."
    }
    return $fullPath.Substring($fullRoot.Length + 1)
}

function Invoke-NativeHelpSmokeTest {
    param([Parameter(Mandatory)] [string]$ExecutablePath)

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $ExecutablePath
    $startInfo.Arguments = '--help'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Unable to start '$ExecutablePath'."
        }
        $standardOutput = $process.StandardOutput.ReadToEnd()
        $standardError = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = ($standardOutput + [Environment]::NewLine + $standardError)
        }
    }
    finally {
        $process.Dispose()
    }
}

function Invoke-PackagedPowerShellCollectorSmokeTest {
    param(
        [Parameter(Mandatory)] [string]$CollectorPath,
        [Parameter(Mandatory)] [string]$WorkingDirectory
    )

    $escapedCollectorPath = $CollectorPath.Replace("'", "''")
    $command = @"
`$ErrorActionPreference = 'Stop'
. '$escapedCollectorPath'
if (-not ('Grid.SkyrimSave.CreatedActorReader' -as [type])) { throw 'The packaged Skyrim save collector type was not compiled.' }
"@
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $startInfo.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encodedCommand"
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw "Unable to start packaged collector smoke test for '$CollectorPath'." }
        $standardOutput = $process.StandardOutput.ReadToEnd()
        $standardError = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "The packaged Skyrim save collector does not compile from Grid's publish directory. $standardOutput $standardError"
        }
    }
    finally {
        $process.Dispose()
    }
}

function Get-ReadOnlyManifest {
    param([Parameter(Mandatory)] [string]$Root)

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return @()
    }

    $normalizedRoot = [System.IO.Path]::GetFullPath($Root)
    return @(
        Get-ChildItem -LiteralPath $normalizedRoot -Force -Recurse -ErrorAction Stop |
            Sort-Object FullName |
            ForEach-Object {
                [pscustomobject]@{
                    Path = Get-RelativeChildPath -Root $normalizedRoot -Path $_.FullName
                    IsDirectory = $_.PSIsContainer
                    Length = if ($_.PSIsContainer) { $null } else { $_.Length }
                    LastWriteTimeUtc = $_.LastWriteTimeUtc.Ticks
                    Attributes = [int]$_.Attributes
                    Sha256 = if ($_.PSIsContainer) { $null } else { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
                }
            }
    )
}

function Get-ReadOnlyExternalSemanticManifest {
    param([Parameter(Mandatory)] [string]$Root)

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return @()
    }

    $normalizedRoot = [System.IO.Path]::GetFullPath($Root)
    $semanticRelativePaths = [System.Collections.Generic.List[string]]::new()
    foreach ($name in @('ModOrganizer.ini', 'portable.txt', 'categories.dat')) {
        if (Test-Path -LiteralPath (Join-Path $normalizedRoot $name) -PathType Leaf) {
            $semanticRelativePaths.Add($name)
        }
    }

    $profilesRoot = Join-Path $normalizedRoot 'profiles'
    if (Test-Path -LiteralPath $profilesRoot -PathType Container) {
        $profileDirectories = @(Get-ChildItem -LiteralPath $profilesRoot -Directory -Force -ErrorAction Stop | Sort-Object Name)
        if ($profileDirectories.Count -gt 4096) {
            throw "External semantic manifest profile limit exceeded under '$profilesRoot'."
        }
        foreach ($profileDirectory in $profileDirectories) {
            if (($profileDirectory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "External semantic verification refuses a profile-directory reparse point: '$($profileDirectory.FullName)'."
            }
            foreach ($name in @('modlist.txt', 'plugins.txt', 'loadorder.txt', 'archives.txt', 'settings.ini', 'Skyrim.ini', 'SkyrimPrefs.ini', 'SkyrimCustom.ini')) {
                $candidate = Join-Path $profileDirectory.FullName $name
                if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                    $semanticRelativePaths.Add((Get-RelativeChildPath -Root $normalizedRoot -Path $candidate))
                }
            }
        }
    }

    return @(
        $semanticRelativePaths |
            Sort-Object -Unique |
            ForEach-Object {
                $fullPath = Join-Path $normalizedRoot $_
                $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
                if ($item.Length -gt 32MB) {
                    throw "External semantic file exceeds the 32 MiB verification limit: '$fullPath'."
                }
                [pscustomobject]@{
                    Path = $_
                    Length = $item.Length
                    LastWriteTimeUtc = $item.LastWriteTimeUtc.Ticks
                    Attributes = [int]$item.Attributes
                    Sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
                }
            }
    )
}

function Compare-Manifests {
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]]$Before,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]]$After,
        [Parameter(Mandatory)] [string]$Description
    )

    $beforeJson = ConvertTo-Json -InputObject $Before -Depth 4 -Compress
    $afterJson = ConvertTo-Json -InputObject $After -Depth 4 -Compress
    if ($beforeJson -cne $afterJson) {
        $beforeByPath = @{}
        foreach ($entry in $Before) { $beforeByPath[[string]$entry.Path] = $entry }
        $afterByPath = @{}
        foreach ($entry in $After) { $afterByPath[[string]$entry.Path] = $entry }
        $changes = New-Object Collections.Generic.List[string]
        foreach ($path in @($beforeByPath.Keys + $afterByPath.Keys | Sort-Object -Unique)) {
            if (-not $beforeByPath.ContainsKey($path)) { $changes.Add("added '$path'"); continue }
            if (-not $afterByPath.ContainsKey($path)) { $changes.Add("removed '$path'"); continue }
            $beforeEntry = ConvertTo-Json -InputObject $beforeByPath[$path] -Depth 4 -Compress
            $afterEntry = ConvertTo-Json -InputObject $afterByPath[$path] -Depth 4 -Compress
            if ($beforeEntry -cne $afterEntry) { $changes.Add("changed '$path'") }
        }
        $preview = @($changes | Select-Object -First 8) -join '; '
        throw "$Description changed during distribution verification: $preview."
    }
}

function Stop-VerifiedGridProcess {
    param(
        [Parameter(Mandatory)] [System.Diagnostics.Process]$Process,
        [Parameter(Mandatory)] [string]$ExpectedExecutable
    )

    if ($Process.HasExited) { return }
    $actualPath = $Process.MainModule.FileName
    if (-not [string]::Equals([System.IO.Path]::GetFullPath($actualPath), [System.IO.Path]::GetFullPath($ExpectedExecutable), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to close unexpected process '$actualPath'."
    }
    [void]$Process.CloseMainWindow()
    if (-not $Process.WaitForExit(5000)) {
        throw 'Installed Grid did not close normally; verification will not force-terminate it.'
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory 'Grid.exe') -PathType Leaf)) {
    throw "Published Grid.exe is missing from '$PublishDirectory'."
}
if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) {
    throw "GridSetup.exe is missing at '$InstallerPath'."
}

$requiredPayload = @(
    'coreclr.dll',
    'hostfxr.dll',
    'Microsoft.WindowsAppRuntime.Bootstrap.dll',
    'Microsoft.ui.xaml.dll',
    'RequestEngine\bin\Grid.Diagnostics.exe'
)
foreach ($name in $requiredPayload) {
    if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory $name) -PathType Leaf)) {
        throw "Self-contained publish evidence is incomplete; missing '$name'."
    }
}

$diagnosticsExecutable = Join-Path $PublishDirectory 'RequestEngine\bin\Grid.Diagnostics.exe'
$diagnosticsSmoke = Invoke-NativeHelpSmokeTest -ExecutablePath $diagnosticsExecutable
if ($diagnosticsSmoke.ExitCode -ne 0 -or $diagnosticsSmoke.Output -notmatch 'mo2-baseline') {
    throw 'The published self-contained Grid.Diagnostics collector is not runnable.'
}
$saveCollectorPath = Join-Path $PublishDirectory 'RequestEngine\scripts\games\skyrimspecialedition\health\collectors\Get-GridSkyrimSaveCreatedActorEvidence.ps1'
if (-not (Test-Path -LiteralPath $saveCollectorPath -PathType Leaf)) {
    throw "The packaged Skyrim save collector is missing at '$saveCollectorPath'."
}
Invoke-PackagedPowerShellCollectorSmokeTest -CollectorPath $saveCollectorPath -WorkingDirectory $PublishDirectory

$externalBefore = @{}
foreach ($root in $ReadOnlyExternalRoots) {
    $fullRoot = [System.IO.Path]::GetFullPath($root)
    $externalBefore[$fullRoot] = @(Get-ReadOnlyExternalSemanticManifest -Root $fullRoot)
}
$storeBefore = @(Get-ReadOnlyManifest -Root $connectionStore)

if ($LaunchPublished) {
    $publishedExecutable = Join-Path $PublishDirectory 'Grid.exe'
    $testDataRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("grid-published-verification-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $testDataRoot | Out-Null
    $previousDataRoot = $env:GRID_DATA_ROOT
    try {
        $env:GRID_DATA_ROOT = $testDataRoot
        $publishedProcess = Start-Process -FilePath $publishedExecutable -WorkingDirectory $PublishDirectory -PassThru
        Start-Sleep -Seconds 3
        if ($publishedProcess.HasExited) { throw "Published Grid.exe exited with code $($publishedProcess.ExitCode)." }
        Stop-VerifiedGridProcess -Process $publishedProcess -ExpectedExecutable $publishedExecutable
    }
    finally {
        $env:GRID_DATA_ROOT = $previousDataRoot
        if (Test-Path -LiteralPath $testDataRoot) {
            Remove-Item -LiteralPath $testDataRoot -Recurse -Force
        }
    }
}

if ($ExerciseInstaller) {
    if (Test-Path -LiteralPath $expectedInstallDirectory) {
        throw "Refusing installer exercise because the expected install directory already exists: '$expectedInstallDirectory'."
    }

    $install = Start-Process -FilePath $InstallerPath -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -PassThru -Wait
    if ($install.ExitCode -ne 0) { throw "Installer exited with code $($install.ExitCode)." }

    $installedExecutable = Join-Path $expectedInstallDirectory 'Grid.exe'
    if (-not (Test-Path -LiteralPath $installedExecutable -PathType Leaf)) {
        throw "Installed executable is missing at '$installedExecutable'."
    }
    if (-not (Test-Path -LiteralPath $startMenuShortcut -PathType Leaf)) {
        throw "Start menu shortcut is missing at '$startMenuShortcut'."
    }
    if (-not (Test-Path -LiteralPath $uninstallRegistryPath)) {
        throw "Grid is not registered in the current user's Installed Apps registry view."
    }

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($startMenuShortcut)
    if (-not [string]::Equals([System.IO.Path]::GetFullPath($shortcut.TargetPath), $installedExecutable, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Start menu shortcut targets '$($shortcut.TargetPath)' instead of installed Grid.exe."
    }
    if ([string]::IsNullOrWhiteSpace($shortcut.IconLocation) -or
        -not $shortcut.IconLocation.StartsWith($installedExecutable, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Start menu shortcut does not use installed Grid.exe as its icon source."
    }

    # Re-run the same fixed-AppId installer to exercise an in-place upgrade.
    $upgrade = Start-Process -FilePath $InstallerPath -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -PassThru -Wait
    if ($upgrade.ExitCode -ne 0) { throw "Upgrade installer exited with code $($upgrade.ExitCode)." }
    Compare-Manifests -Before $storeBefore -After @(Get-ReadOnlyManifest -Root $connectionStore) -Description 'Grid connection store during upgrade'

    if ($LaunchInstalled) {
        $firstLaunch = Start-Process -FilePath $startMenuShortcut -PassThru
        Start-Sleep -Seconds 3
        if ($firstLaunch.HasExited) { throw "Grid exited during its first Start menu launch with code $($firstLaunch.ExitCode)." }
        Stop-VerifiedGridProcess -Process $firstLaunch -ExpectedExecutable $installedExecutable

        $secondLaunch = Start-Process -FilePath $startMenuShortcut -PassThru
        Start-Sleep -Seconds 3
        if ($secondLaunch.HasExited) { throw "Grid exited during its second Start menu launch with code $($secondLaunch.ExitCode)." }
        Stop-VerifiedGridProcess -Process $secondLaunch -ExpectedExecutable $installedExecutable
    }

    $uninstaller = Join-Path $expectedInstallDirectory 'unins000.exe'
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        throw "Uninstaller is missing at '$uninstaller'."
    }
    $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -PassThru -Wait
    if ($uninstall.ExitCode -ne 0) { throw "Uninstaller exited with code $($uninstall.ExitCode)." }
    if (Test-Path -LiteralPath $installedExecutable) { throw 'Grid.exe remains after uninstall.' }
    if (Test-Path -LiteralPath $startMenuShortcut) { throw 'Grid Start menu shortcut remains after uninstall.' }
    if (Test-Path -LiteralPath $uninstallRegistryPath) { throw 'Grid remains registered in Installed Apps after uninstall.' }
}

$storeAfter = @(Get-ReadOnlyManifest -Root $connectionStore)
Compare-Manifests -Before $storeBefore -After $storeAfter -Description 'Grid connection store'
foreach ($entry in $externalBefore.GetEnumerator()) {
    $after = @(Get-ReadOnlyExternalSemanticManifest -Root $entry.Key)
    Compare-Manifests -Before $entry.Value -After $after -Description "External root '$($entry.Key)'"
}

Write-Host "Verified published executable: $(Join-Path $PublishDirectory 'Grid.exe')"
Write-Host "Verified installer:            $InstallerPath"
if ($ExerciseInstaller) {
    Write-Host 'Per-user install, Start shortcut, and uninstall verification passed.'
}
Write-Warning 'This local verification does not establish clean-machine compatibility or code-signing trust.'
