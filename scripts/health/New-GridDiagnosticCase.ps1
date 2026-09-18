<#
.SYNOPSIS
Creates and updates a versioned Grid diagnostic case.
.DESCRIPTION
The case is the shared, game-independent audit envelope. Files are written
atomically and only beneath the explicitly selected case directory.
#>

$script:GridCaseSchemaVersion = 1
$script:GridCaseStatuses = @('NeedsContext', 'NeedsEvidence', 'ReadyToCollect', 'Collecting', 'Diagnosed', 'Failed', 'Incomplete')

function Write-GridJsonAtomic {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$InputObject,
        [Parameter(Mandatory)][string]$LiteralPath,
        [ValidateRange(2, 100)][int]$Depth = 30
    )
    $parent = Split-Path -Parent $LiteralPath
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $temporaryPath = Join-Path $parent ('.' + [IO.Path]::GetFileName($LiteralPath) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    $backupPath = $null
    try {
        $json = $InputObject | ConvertTo-Json -Depth $Depth
        [IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $LiteralPath -PathType Leaf) {
            $backupPath = Join-Path $parent ('.' + [IO.Path]::GetFileName($LiteralPath) + '.' + [guid]::NewGuid().ToString('N') + '.bak')
            [IO.File]::Replace($temporaryPath, $LiteralPath, $backupPath)
            Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
        }
        else {
            [IO.File]::Move($temporaryPath, $LiteralPath)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
        if ($backupPath -and (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
            Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function New-GridDiagnosticCase {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$RawPrompt,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$OutputRoot,
        [string]$CaseId,
        [string]$GameId,
        [string[]]$ReportedSymptoms = @(),
        [string[]]$Constraints = @('Use read-only collectors before proposing mutations.')
    )
    if ([string]::IsNullOrWhiteSpace($CaseId)) {
        $CaseId = 'grid-health-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + ([guid]::NewGuid().ToString('N').Substring(0, 8))
    }
    if ($CaseId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') { throw "Invalid diagnostic CaseId '$CaseId'." }
    $root = [IO.Path]::GetFullPath($OutputRoot)
    $caseDirectory = [IO.Path]::GetFullPath((Join-Path $root $CaseId))
    if (-not $caseDirectory.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The diagnostic case directory must remain beneath OutputRoot.'
    }
    New-Item -ItemType Directory -Path $caseDirectory -Force | Out-Null
    $now = (Get-Date).ToUniversalTime().ToString('o')
    $case = [pscustomobject][ordered]@{
        schemaVersion = $script:GridCaseSchemaVersion
        caseId = $CaseId
        rawPrompt = $RawPrompt
        game = if ($GameId) { $GameId } else { $null }
        createdAt = $now
        updatedAt = $now
        status = if ($GameId) { 'NeedsContext' } else { 'NeedsContext' }
        reportedSymptoms = @($ReportedSymptoms)
        requestedOutcome = $null
        constraints = @($Constraints)
        context = [pscustomobject][ordered]@{}
        investigationPlanPath = $null
        diagnosticResultPath = $null
        evidence = @()
        hypotheses = @()
        tests = @()
        proposals = @()
        verification = @()
        decision = $null
        history = @([pscustomobject][ordered]@{ sequence = 1; at = $now; event = 'CaseCreated'; status = 'NeedsContext'; detail = 'Diagnostic case created from the raw request.' })
    }
    $path = Join-Path $caseDirectory 'case.json'
    Write-GridJsonAtomic -InputObject $case -LiteralPath $path
    [pscustomobject]@{ Case = $case; CaseDirectory = $caseDirectory; CasePath = $path }
}

function Set-GridDiagnosticCaseStatus {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CasePath,
        [Parameter(Mandatory)][ValidateSet('NeedsContext', 'NeedsEvidence', 'ReadyToCollect', 'Collecting', 'Diagnosed', 'Failed', 'Incomplete')][string]$Status,
        [Parameter(Mandatory)][string]$Event,
        [string]$Detail = ''
    )
    $case = Get-Content -LiteralPath $CasePath -Raw | ConvertFrom-Json
    $now = (Get-Date).ToUniversalTime().ToString('o')
    $history = @($case.history)
    $history += [pscustomobject][ordered]@{ sequence = $history.Count + 1; at = $now; event = $Event; status = $Status; detail = $Detail }
    $case.status = $Status
    $case.updatedAt = $now
    $case.history = $history
    Write-GridJsonAtomic -InputObject $case -LiteralPath $CasePath
    $case
}
