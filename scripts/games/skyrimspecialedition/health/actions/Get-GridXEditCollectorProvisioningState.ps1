#requires -Version 5.1

function Get-GridProvisioningDataRoot {
    [CmdletBinding()]
    param([string]$GridDataRoot)
    if (-not [string]::IsNullOrWhiteSpace($GridDataRoot)) { return [IO.Path]::GetFullPath($GridDataRoot) }
    if (-not [string]::IsNullOrWhiteSpace($env:GRID_DATA_ROOT)) { return [IO.Path]::GetFullPath($env:GRID_DATA_ROOT) }
    return [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Grid'))
}

function Get-GridStablePathId {
    param([Parameter(Mandatory)][string]$Path)
    $canonical = [IO.Path]::GetFullPath($Path).TrimEnd('\').ToUpperInvariant()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical)))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-GridPeMachine {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$LiteralPath)
    $stream = [IO.File]::Open($LiteralPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        if ($stream.Length -lt 64) { throw 'Executable is too small to contain a valid PE header.' }
        $reader = New-Object IO.BinaryReader($stream, [Text.Encoding]::ASCII, $true)
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'Executable does not contain an MZ header.' }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadUInt32()
        if ($peOffset -gt ($stream.Length - 6)) { throw 'Executable contains an invalid PE header offset.' }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw 'Executable does not contain a PE signature.' }
        return $reader.ReadUInt16()
    }
    finally { $stream.Dispose() }
}

function Get-GridXEditCollectorProvisioningState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ConfigurationPath,
        [string]$ExecutableTitle = 'SSEEdit',
        [string]$GridDataRoot
    )
    try {
        if (-not (Get-Command Get-GridMo2ConfiguredExecutable -ErrorAction SilentlyContinue)) { throw 'Get-GridMo2ConfiguredExecutable is unavailable.' }
        $definition = Get-GridMo2ConfiguredExecutable -ConfigurationPath $ConfigurationPath -Title $ExecutableTitle
        if ((Get-GridPeMachine -LiteralPath $definition.Binary) -ne 0x8664) { throw 'Configured SSEEdit executable is not an x64 PE image (Machine 0x8664).' }
        $adapterRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
        # The production provisioning boundary is intentionally bound to these
        # adapter-owned files. Callers cannot substitute arbitrary Pascal code
        # and a self-consistent manifest for Grid's reviewed collector bundle.
        $source = [IO.Path]::GetFullPath((Join-Path $adapterRoot 'sseedit\Trace-GridReference.pas'))
        $manifestFile = [IO.Path]::GetFullPath((Join-Path $adapterRoot 'sseedit\Trace-GridReference.manifest.json'))
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Bundled collector is missing: $source" }
        if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) { throw "Collector manifest is missing: $manifestFile" }
        $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
        if ([int]$manifest.schemaVersion -ne 1 -or [string]$manifest.collectorId -ne 'Grid.TraceReference' -or [string]$manifest.fileName -ne 'Trace-GridReference.pas') { throw 'Collector manifest identity is unsupported.' }
        $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($sourceHash -cne ([string]$manifest.sha256).ToUpperInvariant()) { throw 'Bundled collector hash does not match its versioned manifest.' }
        $editScripts = Join-Path (Split-Path -Parent $definition.Binary) 'Edit Scripts'
        if (-not (Test-Path -LiteralPath $editScripts -PathType Container)) { throw "xEdit Edit Scripts directory is unavailable: $editScripts" }
        $attributes = (Get-Item -LiteralPath $editScripts).Attributes
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'xEdit Edit Scripts directory is a reparse point and is not supported for provisioning.' }
        $destination = Join-Path $editScripts ([string]$manifest.fileName)
        $installedHash = if (Test-Path -LiteralPath $destination -PathType Leaf) { (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToUpperInvariant() } else { $null }
        $dataRoot = Get-GridProvisioningDataRoot -GridDataRoot $GridDataRoot
        $installationId = Get-GridStablePathId -Path $definition.Binary
        $receiptDirectory = Join-Path $dataRoot 'diagnostics\tool-provisioning\xedit\receipts'
        $receiptPath = Join-Path $receiptDirectory ($installationId + '.v1.json')
        $receipt = $null
        $receiptStatus = 'Absent'
        if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
            try {
                $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
                if ([int]$receipt.schemaVersion -ne 1 -or
                    [string]$receipt.collectorId -ne 'Grid.TraceReference' -or
                    [string]$receipt.collectorVersion -notmatch '^\d+\.\d+\.\d+$' -or
                    [string]::IsNullOrWhiteSpace([string]$receipt.installedSha256) -or
                    [string]$receipt.installedSha256 -cne [string]$receipt.sourceSha256 -or
                    [string]$receipt.executableSha256 -cne (Get-FileHash -LiteralPath $definition.Binary -Algorithm SHA256).Hash.ToUpperInvariant() -or
                    [string]$receipt.destinationPath -ine $destination) { throw 'Unsupported receipt.' }
                $receiptStatus = if ([string]$receipt.collectorVersion -ceq [string]$manifest.collectorVersion) { 'Valid' } else { 'Historic' }
            } catch { $receiptStatus = 'Corrupt'; $receipt = $null }
        }
        $state = if (-not $installedHash) { 'Missing' }
            elseif ($installedHash -ceq $sourceHash) { 'Ready' }
            elseif ($receiptStatus -in @('Valid','Historic') -and $installedHash -ceq ([string]$receipt.installedSha256).ToUpperInvariant() -and [string]$receipt.destinationPath -ieq $destination) { 'Outdated' }
            else { 'ModifiedExternally' }
        [pscustomobject][ordered]@{
            State = $state; Detail = $null; CollectorId = [string]$manifest.collectorId; CollectorVersion = [string]$manifest.collectorVersion
            SourcePath = $source; ManifestPath = $manifestFile; SourceSha256 = $sourceHash; DestinationPath = $destination; InstalledSha256 = $installedHash
            ExecutablePath = $definition.Binary; ExecutableSha256 = (Get-FileHash -LiteralPath $definition.Binary -Algorithm SHA256).Hash.ToUpperInvariant()
            PeMachine = '0x8664'; ConfigurationPath = [IO.Path]::GetFullPath($ConfigurationPath); ExecutableTitle = $ExecutableTitle
            ReceiptPath = $receiptPath; ReceiptStatus = $receiptStatus; Receipt = $receipt; GridDataRoot = $dataRoot; InstallationId = $installationId
        }
    }
    catch {
        [pscustomobject][ordered]@{ State = 'Unavailable'; Detail = $_.Exception.Message; CollectorId = 'Grid.TraceReference'; CollectorVersion = $null; SourcePath = $null; ManifestPath = $null; SourceSha256 = $null; DestinationPath = $null; InstalledSha256 = $null; ExecutablePath = $null; ExecutableSha256 = $null; PeMachine = $null; ConfigurationPath = $ConfigurationPath; ExecutableTitle = $ExecutableTitle; ReceiptPath = $null; ReceiptStatus = 'Unavailable'; Receipt = $null; GridDataRoot = $GridDataRoot; InstallationId = $null }
    }
}
