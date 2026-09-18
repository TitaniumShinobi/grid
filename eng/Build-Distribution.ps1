[CmdletBinding()]
param(
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',

    [ValidatePattern('^win-x64$')]
    [string]$RuntimeIdentifier = 'win-x64',

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.0',

    [string]$InnoCompilerPath,

    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repositoryRoot 'Grid.sln'
$projectPath = Join-Path $repositoryRoot 'src\Grid.App\Grid.App.csproj'
$diagnosticsProjectPath = Join-Path $repositoryRoot 'src\Grid.Diagnostics\Grid.Diagnostics.csproj'
$publishDirectory = Join-Path $repositoryRoot "artifacts\publish\$RuntimeIdentifier"
$diagnosticsPublishDirectory = Join-Path $publishDirectory 'RequestEngine\bin'
$installerDirectory = Join-Path $repositoryRoot 'artifacts\installer'
$installerDefinition = Join-Path $repositoryRoot 'installer\Grid.iss'

function Assert-SafeGeneratedDirectory {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$ExpectedLeaf
    )

    $resolvedRoot = [System.IO.Path]::GetFullPath($repositoryRoot).TrimEnd('\')
    $resolvedArtifacts = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')).TrimEnd('\')
    $resolvedPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')

    foreach ($candidate in @($resolvedArtifacts, (Split-Path $resolvedPath -Parent), $resolvedPath)) {
        if (Test-Path -LiteralPath $candidate) {
            $item = Get-Item -LiteralPath $candidate -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to operate through a reparse point: $candidate"
            }
        }
    }

    if (-not $resolvedPath.StartsWith("$resolvedArtifacts\", [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($resolvedPath) -ne $ExpectedLeaf -or
        $resolvedPath -eq $resolvedRoot -or
        $resolvedPath -eq $resolvedArtifacts) {
        throw "Refusing to operate on unexpected generated directory: $resolvedPath"
    }
}

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

function Resolve-InnoCompiler {
    param([string]$RequestedPath)

    $candidates = @(
        $RequestedPath,
        $env:INNO_SETUP_ISCC,
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    $compiler = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ($null -eq $compiler) {
        throw 'Official Inno Setup 6.7.3 compiler not found. Pass -InnoCompilerPath or set INNO_SETUP_ISCC.'
    }

    $compiler = [System.IO.Path]::GetFullPath($compiler)
    # ISCC intentionally carries 0.0.0.0 resource metadata. The signed sibling
    # uninstaller records the installed Inno Setup product version.
    $installationEvidence = Join-Path (Split-Path $compiler -Parent) 'unins000.exe'
    if (-not (Test-Path -LiteralPath $installationEvidence -PathType Leaf)) {
        throw "Inno Setup installation version evidence is missing beside '$compiler'."
    }
    $productVersion = (Get-Item -LiteralPath $installationEvidence).VersionInfo.ProductVersion.Trim()
    if ($productVersion -notmatch '^6\.7\.3(?:\.|$)') {
        throw "Inno Setup 6.7.3 is required; found '$productVersion' at '$compiler'."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $compiler
    $versionSignature = Get-AuthenticodeSignature -LiteralPath $installationEvidence
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $versionSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate -or
        $null -eq $versionSignature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch 'CN=Pyrsys B\.V\.(?:,|$)' -or
        $versionSignature.SignerCertificate.Subject -notmatch 'CN=Pyrsys B\.V\.(?:,|$)') {
        throw "Inno Setup compiler signature is not the expected Pyrsys B.V. publisher signature: '$compiler'."
    }

    return $compiler
}

# Commercial/package output is a stricter boundary than source development.
# Refuse before restore, deletion, publish, or installer work when provenance,
# asset, dependency, or repository-license review is incomplete.
$provenanceGate = Join-Path $repositoryRoot 'eng\provenance\Test-GridProvenance.ps1'
& $provenanceGate -Mode Distribution -RepositoryRoot $repositoryRoot

Assert-SafeGeneratedDirectory -Path $publishDirectory -ExpectedLeaf $RuntimeIdentifier
Assert-SafeGeneratedDirectory -Path $installerDirectory -ExpectedLeaf 'installer'

if (-not $NoRestore) {
    & dotnet restore $solutionPath
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }

    # Resolve the exact supported self-contained runtime pack declared by the publish profile.
    & dotnet restore $projectPath `
        --runtime $RuntimeIdentifier `
        /p:Platform=x64 `
        /p:PublishProfile=win-x64
    if ($LASTEXITCODE -ne 0) { throw "publish-profile restore failed with exit code $LASTEXITCODE." }

    & dotnet restore $diagnosticsProjectPath `
        --runtime $RuntimeIdentifier `
        /p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw "diagnostics restore failed with exit code $LASTEXITCODE." }
}

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $installerDirectory) {
    Remove-Item -LiteralPath $installerDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDirectory, $installerDirectory -Force | Out-Null

& dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --no-restore `
    --output $publishDirectory `
    /p:Platform=x64 `
    /p:PublishProfile=win-x64 `
    /p:Version=$Version `
    /p:WindowsAppSDKSelfContained=true `
    /p:PublishSingleFile=false `
    /p:PublishTrimmed=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

# The in-app request engine must work without a source checkout or machine-wide
# .NET runtime. Replace the framework-dependent build copy with one self-contained
# single-file collector owned by this exact distribution.
if (Test-Path -LiteralPath $diagnosticsPublishDirectory) {
    Remove-Item -LiteralPath $diagnosticsPublishDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $diagnosticsPublishDirectory -Force | Out-Null
& dotnet publish $diagnosticsProjectPath `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --no-restore `
    --output $diagnosticsPublishDirectory `
    /p:Platform=x64 `
    /p:Version=$Version `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:PublishTrimmed=false `
    /p:DebugSymbols=false `
    /p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw "diagnostics publish failed with exit code $LASTEXITCODE." }

# MSBuild's incremental Content pipeline can retain an older copy of a modified
# request-engine script in the intermediate output even when PublishDir itself
# was recreated. The distribution boundary must contain the exact repository
# scripts that were reviewed and tested in this invocation, so overwrite every
# script from the authoritative source tree and verify each byte digest before
# producing the publish manifest or installer.
$sourceScriptsDirectory = Join-Path $repositoryRoot 'scripts'
$publishedScriptsDirectory = Join-Path $publishDirectory 'RequestEngine\scripts'
New-Item -ItemType Directory -Path $publishedScriptsDirectory -Force | Out-Null
foreach ($sourceScript in @(Get-ChildItem -LiteralPath $sourceScriptsDirectory -File -Recurse | Sort-Object FullName)) {
    $relativeScriptPath = Get-RelativeChildPath -Root $sourceScriptsDirectory -Path $sourceScript.FullName
    $publishedScriptPath = Join-Path $publishedScriptsDirectory $relativeScriptPath
    $publishedScriptParent = Split-Path -Parent $publishedScriptPath
    if (-not (Test-Path -LiteralPath $publishedScriptParent -PathType Container)) {
        New-Item -ItemType Directory -Path $publishedScriptParent -Force | Out-Null
    }
    Copy-Item -LiteralPath $sourceScript.FullName -Destination $publishedScriptPath -Force
    if ((Get-FileHash -LiteralPath $publishedScriptPath -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath $sourceScript.FullName -Algorithm SHA256).Hash) {
        throw "Published request-engine script differs from source: '$relativeScriptPath'."
    }
}

$requiredPayload = @(
    'Grid.exe',
    'Grid.Core.dll',
    'Grid.Mo2.dll',
    'App.xbf',
    'MainWindow.xbf',
    'Grid.pri',
    'coreclr.dll',
    'hostfxr.dll',
    'Microsoft.WindowsAppRuntime.Bootstrap.dll',
    'Microsoft.ui.xaml.dll',
    'RequestEngine\bin\Grid.Diagnostics.exe',
    'Assets\TemporaryIdentity\GridTemporary.ico'
)
foreach ($relativePath in $requiredPayload) {
    $candidate = Join-Path $publishDirectory $relativePath
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Publish output is incomplete; missing '$relativePath'."
    }
}

$diagnosticsExecutable = Join-Path $publishDirectory 'RequestEngine\bin\Grid.Diagnostics.exe'
$diagnosticsSmoke = Invoke-NativeHelpSmokeTest -ExecutablePath $diagnosticsExecutable
if ($diagnosticsSmoke.ExitCode -ne 0 -or $diagnosticsSmoke.Output -notmatch 'mo2-baseline') {
    throw 'The self-contained Grid.Diagnostics collector failed its distribution smoke test.'
}

$publishManifest = Get-ChildItem -LiteralPath $publishDirectory -File -Recurse |
    Sort-Object FullName |
    ForEach-Object {
        [pscustomobject]@{
            Path = Get-RelativeChildPath -Root $publishDirectory -Path $_.FullName
            Length = $_.Length
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    }
$publishManifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $publishDirectory 'publish-manifest.json') -Encoding utf8

$compilerPath = Resolve-InnoCompiler -RequestedPath $InnoCompilerPath
& $compilerPath "/DSourceDir=$publishDirectory" "/DOutputDir=$installerDirectory" "/DAppVersion=$Version" $installerDefinition
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }

$setupPath = Join-Path $installerDirectory 'GridSetup.exe'
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "Installer was not produced at '$setupPath'."
}

Write-Host "Published Grid: $publishDirectory"
Write-Host "Installer:      $setupPath"
Write-Warning 'These Stage 1 artifacts are unsigned. Clean-machine compatibility remains unverified.'
