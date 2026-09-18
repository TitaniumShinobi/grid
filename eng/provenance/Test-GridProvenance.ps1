[CmdletBinding()]
param(
    [ValidateSet('Source','Distribution')]
    [string]$Mode = 'Source',
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'Grid.Provenance.ps1')

function Add-GridProvenanceViolation {
    param([System.Collections.ArrayList]$Violations, [string]$Code, [string]$Path, [string]$Message)
    [void]$Violations.Add([pscustomobject]@{ code=$Code; path=$Path; message=$Message })
}

function Test-GridExactProperties {
    param([Parameter(Mandatory=$true)]$Object, [Parameter(Mandatory=$true)][string[]]$Allowed)
    $actual = @($Object.PSObject.Properties | ForEach-Object { $_.Name })
    foreach ($name in $actual) {
        if (-not ($Allowed -contains $name)) { return $false }
    }
    return $true
}

function Test-GridProvenanceState {
    param([ValidateSet('Source','Distribution')][string]$Mode='Source', [string]$RepositoryRoot)
    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Get-GridProvenanceRepositoryRoot }
    $RepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
    $violations = New-Object System.Collections.ArrayList

    $ledgerPath = Join-Path $RepositoryRoot 'legal/provenance.v1.json'
    $assetPath = Join-Path $RepositoryRoot 'legal/asset-provenance.v1.json'
    $componentPath = Join-Path $RepositoryRoot 'legal/third-party-components.v1.json'
    $schemaPath = Join-Path $RepositoryRoot 'legal/provenance-ledger.v1.schema.json'
    foreach ($required in @($ledgerPath,$assetPath,$componentPath,$schemaPath,(Join-Path $RepositoryRoot 'legal/README.md'),(Join-Path $RepositoryRoot 'legal/THIRD_PARTY_NOTICES.md'))) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            Add-GridProvenanceViolation $violations 'RequiredFileMissing' $required 'A canonical provenance artifact is missing.'
        }
    }
    if ($violations.Count -gt 0) { return [pscustomobject]@{ status='Failed'; mode=$Mode; sourceCount=0; assetCount=0; violations=@($violations) } }

    $ledger = Read-GridProvenanceJson -LiteralPath $ledgerPath
    $assets = Read-GridProvenanceJson -LiteralPath $assetPath
    $components = Read-GridProvenanceJson -LiteralPath $componentPath
    $schema = Read-GridProvenanceJson -LiteralPath $schemaPath
    if ($ledger.schemaVersion -ne 1 -or $ledger.ledgerId -ne 'grid.provenance.v1') { Add-GridProvenanceViolation $violations 'LedgerIdentityInvalid' 'legal/provenance.v1.json' 'Expected Grid provenance ledger v1.' }
    if ($assets.schemaVersion -ne 1 -or $assets.ledgerId -ne 'grid.asset-provenance.v1') { Add-GridProvenanceViolation $violations 'AssetLedgerIdentityInvalid' 'legal/asset-provenance.v1.json' 'Expected Grid asset provenance ledger v1.' }
    if ($components.schemaVersion -ne 1 -or $components.ledgerId -ne 'grid.third-party-components.v1') { Add-GridProvenanceViolation $violations 'ComponentLedgerIdentityInvalid' 'legal/third-party-components.v1.json' 'Expected Grid third-party component ledger v1.' }
    if ($schema.properties.ledgerId.const -ne 'grid.provenance.v1') { Add-GridProvenanceViolation $violations 'SchemaIdentityInvalid' 'legal/provenance-ledger.v1.schema.json' 'Schema must lock the canonical ledger identity.' }
    if (-not (Test-GridExactProperties -Object $ledger -Allowed @('schemaVersion','ledgerId','baseline','entries'))) { Add-GridProvenanceViolation $violations 'LedgerAdditionalProperty' 'legal/provenance.v1.json' 'Ledger root contains a property outside the closed v1 contract.' }
    if (-not (Test-GridExactProperties -Object $ledger.baseline -Allowed @('capturedOn','gitHistory','statement'))) { Add-GridProvenanceViolation $violations 'BaselineAdditionalProperty' 'legal/provenance.v1.json' 'Baseline contains a property outside the closed v1 contract.' }

    $entryIds = @($ledger.entries | ForEach-Object { [string]$_.id })
    if (@($entryIds | Sort-Object -Unique).Count -ne $entryIds.Count) { Add-GridProvenanceViolation $violations 'DuplicateEntryId' 'legal/provenance.v1.json' 'Provenance entry IDs must be unique.' }
    $validClassifications = @('OriginalGridWork','BehaviorOrWorkflowInspiration','InteroperabilityImplementation','MITDerivedCode','GPLDerivedCode','ThirdPartyDependency','CopiedOrLicensedAsset','UnclearProvenance')
    foreach ($entry in @($ledger.entries)) {
        if (-not (Test-GridExactProperties -Object $entry -Allowed @('id','paths','classification','reviewState','distributionDisposition','upstream','evidence','requiredAction'))) { Add-GridProvenanceViolation $violations 'EntryAdditionalProperty' ([string]$entry.id) 'Entry contains a property outside the closed v1 contract.' }
        if (-not ($validClassifications -contains [string]$entry.classification)) { Add-GridProvenanceViolation $violations 'ClassificationInvalid' ([string]$entry.id) 'Unknown provenance classification.' }
        if (@($entry.paths).Count -eq 0 -or @($entry.evidence).Count -eq 0 -or [string]::IsNullOrWhiteSpace([string]$entry.requiredAction)) { Add-GridProvenanceViolation $violations 'EntryIncomplete' ([string]$entry.id) 'Every entry requires paths, evidence, and required action.' }
    }

    $sourceFiles = @(Get-GridGovernedSourceFiles -RepositoryRoot $RepositoryRoot)
    foreach ($file in $sourceFiles) {
        $matches = @(Get-GridPathLedgerMatches -RelativePath $file.RelativePath -Ledger $ledger)
        if ($matches.Count -eq 0) { Add-GridProvenanceViolation $violations 'SourceUnclassified' $file.RelativePath 'Governed source/documentation has no provenance entry.' }
        elseif ($matches.Count -gt 1) { Add-GridProvenanceViolation $violations 'SourceClassificationAmbiguous' $file.RelativePath ('Matches multiple provenance entries: ' + (@($matches | ForEach-Object { $_.id }) -join ', ')) }
    }

    $assetFiles = @(Get-GridGovernedAssetFiles -RepositoryRoot $RepositoryRoot)
    $assetPaths = @($assets.assets | ForEach-Object { [string]$_.path })
    if (@($assetPaths | Sort-Object -Unique).Count -ne $assetPaths.Count) { Add-GridProvenanceViolation $violations 'DuplicateAssetPath' 'legal/asset-provenance.v1.json' 'Asset paths must be unique.' }
    foreach ($file in $assetFiles) {
        if (-not ($assetPaths -contains $file.RelativePath)) { Add-GridProvenanceViolation $violations 'AssetUnclassified' $file.RelativePath 'Governed asset has no exact provenance record.' }
    }
    foreach ($record in @($assets.assets)) {
        if (-not (Test-Path -LiteralPath (Join-Path $RepositoryRoot ([string]$record.path)) -PathType Leaf)) { Add-GridProvenanceViolation $violations 'AssetRecordStale' ([string]$record.path) 'Asset record points to a missing file.' }
    }

    $projectText = @(Get-ChildItem -LiteralPath $RepositoryRoot -Filter '*.csproj' -File -Recurse | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join [Environment]::NewLine
    $declaredComponents = @($components.components | ForEach-Object { [string]$_.name })
    $packageMatches = [regex]::Matches($projectText, '<PackageReference\s+Include="([^"]+)"')
    foreach ($match in $packageMatches) {
        $name = $match.Groups[1].Value
        if (-not ($declaredComponents -contains $name)) { Add-GridProvenanceViolation $violations 'DependencyUnclassified' $name 'PackageReference is absent from the third-party component ledger.' }
    }

    $actualGitHistory = Get-GridGitHistoryState -RepositoryRoot $RepositoryRoot
    if ([string]$ledger.baseline.gitHistory -ne $actualGitHistory) { Add-GridProvenanceViolation $violations 'GitHistoryStateMismatch' 'legal/provenance.v1.json' "Ledger says '$($ledger.baseline.gitHistory)' but repository state is '$actualGitHistory'." }

    if ($Mode -eq 'Distribution') {
        foreach ($entry in @($ledger.entries)) {
            if ([string]$entry.reviewState -ne 'Reviewed' -or -not (@('Approved','NoticeRequired','SeparateComponent') -contains [string]$entry.distributionDisposition)) {
                Add-GridProvenanceViolation $violations 'SourceNotClearedForDistribution' ([string]$entry.id) 'Source entry is not fully reviewed and dispositioned for distribution.'
            }
            if ([string]$entry.classification -eq 'GPLDerivedCode' -and [string]$entry.distributionDisposition -eq 'Approved') {
                Add-GridProvenanceViolation $violations 'GPLBoundaryInvalid' ([string]$entry.id) 'GPL-derived code cannot be silently approved under the proprietary Grid application boundary.'
            }
        }
        foreach ($asset in @($assets.assets)) {
            if ([string]$asset.reviewState -ne 'Reviewed' -or [string]$asset.distributionDisposition -ne 'Approved') { Add-GridProvenanceViolation $violations 'AssetNotClearedForDistribution' ([string]$asset.path) 'Asset is not reviewed and approved.' }
        }
        foreach ($component in @($components.components)) {
            if ([string]$component.reviewState -ne 'Reviewed' -or -not (@('Approved','NoticeRequired','SeparateComponent') -contains [string]$component.distributionDisposition)) { Add-GridProvenanceViolation $violations 'DependencyNotClearedForDistribution' ([string]$component.name) 'Dependency license disposition is incomplete.' }
        }
        if (-not (Test-Path -LiteralPath (Join-Path $RepositoryRoot 'LICENSE') -PathType Leaf)) { Add-GridProvenanceViolation $violations 'RepositoryLicenseMissing' 'LICENSE' 'Attorney-approved repository license is required for distribution.' }
    }

    $status = if ($violations.Count -eq 0) { 'Passed' } else { 'Failed' }
    [pscustomobject]@{ status=$status; mode=$Mode; sourceCount=$sourceFiles.Count; assetCount=$assetFiles.Count; violations=@($violations) }
}

if ($MyInvocation.InvocationName -ne '.') {
    $result = Test-GridProvenanceState -Mode $Mode -RepositoryRoot $RepositoryRoot
    if ($result.status -eq 'Passed') {
        Write-Host ("PASS: GRID provenance {0} gate covered {1} source/document files and {2} assets." -f $Mode,$result.sourceCount,$result.assetCount)
    } else {
        foreach ($violation in @($result.violations)) { Write-Error ("{0}: {1} - {2}" -f $violation.code,$violation.path,$violation.message) -ErrorAction Continue }
        throw ("GridProvenanceFailed: mode={0}; violations={1}" -f $Mode,@($result.violations).Count)
    }
}
