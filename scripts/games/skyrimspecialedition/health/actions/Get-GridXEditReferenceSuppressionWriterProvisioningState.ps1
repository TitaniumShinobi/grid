#requires -Version 5.1

function Get-GridXEditReferenceSuppressionWriterProvisioningState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ConfigurationPath,
        [string]$ExecutableTitle = 'SSEEdit',
        [string]$GridDataRoot
    )
    try {
        foreach ($required in @('Get-GridMo2ConfiguredExecutable','Get-GridPeMachine','Get-GridProvisioningDataRoot','Get-GridStablePathId')) {
            if (-not (Get-Command $required -ErrorAction SilentlyContinue)) { throw "$required is unavailable." }
        }
        $definition = Get-GridMo2ConfiguredExecutable -ConfigurationPath $ConfigurationPath -Title $ExecutableTitle
        if ((Get-GridPeMachine -LiteralPath $definition.Binary) -ne 0x8664) { throw 'Configured SSEEdit executable is not an x64 PE image (Machine 0x8664).' }
        $adapterRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
        $source = [IO.Path]::GetFullPath((Join-Path $adapterRoot 'sseedit\Write-GridReferenceSuppression.pas'))
        $manifestFile = [IO.Path]::GetFullPath((Join-Path $adapterRoot 'sseedit\Write-GridReferenceSuppression.manifest.json'))
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Bundled writer is missing: $source" }
        if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) { throw "Writer manifest is missing: $manifestFile" }
        $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json -ErrorAction Stop
        if ([int]$manifest.schemaVersion -ne 1 -or [string]$manifest.writerId -cne 'Grid.ReferenceSuppression' -or
            [string]$manifest.writerVersion -notmatch '^\d+\.\d+\.\d+$' -or [string]$manifest.fileName -cne 'Write-GridReferenceSuppression.pas') {
            throw 'Writer manifest identity is unsupported.'
        }
        $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($sourceHash -cne ([string]$manifest.sha256).ToUpperInvariant()) { throw 'Bundled writer hash does not match its versioned manifest.' }
        $editScripts = Join-Path (Split-Path -Parent $definition.Binary) 'Edit Scripts'
        if (-not (Test-Path -LiteralPath $editScripts -PathType Container)) { throw "xEdit Edit Scripts directory is unavailable: $editScripts" }
        if (((Get-Item -LiteralPath $editScripts).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'xEdit Edit Scripts directory is a reparse point and is not supported for provisioning.' }
        $destination = Join-Path $editScripts ([string]$manifest.fileName)
        $installedHash = if (Test-Path -LiteralPath $destination -PathType Leaf) { (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToUpperInvariant() } else { $null }
        $dataRoot = Get-GridProvisioningDataRoot -GridDataRoot $GridDataRoot
        $installationId = Get-GridStablePathId -Path $definition.Binary
        $receiptPath = Join-Path $dataRoot "diagnostics\tool-provisioning\xedit\reference-suppression-writer\receipts\$installationId.v1.json"
        $receipt = $null
        $receiptStatus = 'Absent'
        if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
            try {
                $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -ErrorAction Stop
                if ([int]$receipt.schemaVersion -ne 1 -or [string]$receipt.writerId -cne 'Grid.ReferenceSuppression' -or
                    [string]$receipt.writerVersion -notmatch '^\d+\.\d+\.\d+$' -or
                    [string]$receipt.installedSha256 -cne [string]$receipt.sourceSha256 -or
                    [string]$receipt.executableSha256 -cne (Get-FileHash -LiteralPath $definition.Binary -Algorithm SHA256).Hash.ToUpperInvariant() -or
                    [string]$receipt.destinationPath -ine $destination) { throw 'Unsupported receipt.' }
                $receiptStatus = if ([string]$receipt.writerVersion -ceq [string]$manifest.writerVersion) { 'Valid' } else { 'Historic' }
            } catch { $receiptStatus = 'Corrupt'; $receipt = $null }
        }
        $state = if (-not $installedHash) { 'Missing' }
            elseif ($installedHash -ceq $sourceHash) { 'Ready' }
            elseif ($receiptStatus -in @('Valid','Historic') -and $installedHash -ceq ([string]$receipt.installedSha256).ToUpperInvariant()) { 'Outdated' }
            else { 'ModifiedExternally' }
        [pscustomobject][ordered]@{
            State=$state;Detail=$null;WriterId=[string]$manifest.writerId;WriterVersion=[string]$manifest.writerVersion
            SourcePath=$source;ManifestPath=$manifestFile;SourceSha256=$sourceHash;DestinationPath=$destination;InstalledSha256=$installedHash
            ExecutablePath=$definition.Binary;ExecutableSha256=(Get-FileHash -LiteralPath $definition.Binary -Algorithm SHA256).Hash.ToUpperInvariant()
            PeMachine='0x8664';ConfigurationPath=[IO.Path]::GetFullPath($ConfigurationPath);ExecutableTitle=$ExecutableTitle
            ReceiptPath=$receiptPath;ReceiptStatus=$receiptStatus;Receipt=$receipt;GridDataRoot=$dataRoot;InstallationId=$installationId
        }
    } catch {
        [pscustomobject][ordered]@{State='Unavailable';Detail=$_.Exception.Message;WriterId='Grid.ReferenceSuppression';WriterVersion=$null;SourcePath=$null;ManifestPath=$null;SourceSha256=$null;DestinationPath=$null;InstalledSha256=$null;ExecutablePath=$null;ExecutableSha256=$null;PeMachine=$null;ConfigurationPath=$ConfigurationPath;ExecutableTitle=$ExecutableTitle;ReceiptPath=$null;ReceiptStatus='Unavailable';Receipt=$null;GridDataRoot=$GridDataRoot;InstallationId=$null}
    }
}
