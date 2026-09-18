#requires -Version 5.1

<#
.SYNOPSIS
Ranks diagnostic hypotheses using an explicit game-independent evidence model.

.DESCRIPTION
This script does not invent confidence. Each candidate is scored from named,
bounded evidence values (0.0 to 1.0), symptom-specific weights, contradiction
penalties, evidence-completeness reporting, and verification caps.

Confidence means "strength of the collected evidence for this diagnosis." It
is not a statistical probability that the diagnosis is true.

Use -WriteTemplate to create a documented input file.
#>

[CmdletBinding(DefaultParameterSetName = 'Diagnose')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Diagnose', Position = 0)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$InputPath,

    [Parameter(ParameterSetName = 'Diagnose')]
    [string]$OutputPath,

    [Parameter(Mandatory, ParameterSetName = 'Template')]
    [string]$WriteTemplate,

    [Parameter(ParameterSetName = 'Diagnose')]
    [ValidateSet('Object', 'Json', 'Markdown')]
    [string]$Format = 'Markdown'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Values are evidence strength: 0 = disproven/absent, 0.5 = partial, 1 = direct.
# Positive weights total 100 for every symptom profile.
$profiles = @{
    MissingTexture = [ordered]@{
        controlledReproduction = 30
        runtimeAttribution     = 18
        assetResolutionFailure = 22
        winningOverride        = 10
        recordConflict         = 5
        requirementViolation   = 5
        spatialCorrelation     = 5
        versionMismatch        = 5
    }
    MissingMesh = [ordered]@{
        controlledReproduction = 30
        runtimeAttribution     = 18
        assetResolutionFailure = 22
        winningOverride        = 10
        recordConflict         = 5
        requirementViolation   = 5
        spatialCorrelation     = 5
        versionMismatch        = 5
    }
    PlacementConflict = [ordered]@{
        controlledReproduction = 30
        runtimeAttribution     = 18
        winningOverride        = 15
        recordConflict         = 15
        spatialCorrelation     = 10
        requirementViolation   = 5
        versionMismatch        = 5
        assetResolutionFailure = 2
    }
    Crash = [ordered]@{
        controlledReproduction = 25
        logSignature           = 25
        stackCorrelation       = 18
        runtimeAttribution     = 10
        winningOverride        = 7
        assetResolutionFailure = 5
        recordConflict         = 4
        requirementViolation   = 3
        versionMismatch        = 3
    }
    Behavior = [ordered]@{
        controlledReproduction = 32
        runtimeAttribution     = 18
        scriptLogCorrelation   = 15
        recordConflict         = 10
        winningOverride        = 8
        requirementViolation   = 7
        versionMismatch        = 5
        spatialCorrelation     = 5
    }
    Unknown = [ordered]@{
        controlledReproduction = 32
        runtimeAttribution     = 20
        winningOverride        = 10
        recordConflict         = 10
        assetResolutionFailure = 8
        requirementViolation   = 6
        spatialCorrelation     = 5
        versionMismatch        = 4
        logSignature           = 3
        stackCorrelation       = 2
    }
}

$penaltyWeights = [ordered]@{
    contradictoryReproduction = 45
    contraryRuntimeAttribution = 25
    strongerAlternative       = 15
    staleEvidence             = 10
    unverifiedAssumption      = 5
}

$definitions = [ordered]@{
    controlledReproduction = 'A controlled A/B change makes the symptom disappear and restoring the change makes it return.'
    runtimeAttribution = 'Runtime evidence identifies the candidate: selected reference, loaded DLL/module, active script, or owning plugin.'
    assetResolutionFailure = 'The effective asset, resource, script, behavior, or dependency cannot be resolved in the collected environment.'
    winningOverride = 'The candidate supplies the final winning record or file relevant to the symptom.'
    recordConflict = 'A deterministic native-record collector shows a material conflicting value or incompatible composition.'
    requirementViolation = 'A declared master, patch, installation option, or author-defined ordering requirement is violated.'
    spatialCorrelation = 'The affected reference is in the reported cell and near the reported position or selected object.'
    versionMismatch = 'Installed versions do not match the versions supported by the patch or generated output.'
    logSignature = 'A crash or application log names the candidate or a candidate-owned form/file with a specific failure signature.'
    stackCorrelation = 'The call stack or probable call stack repeatedly points to the candidate subsystem.'
    scriptLogCorrelation = 'Papyrus or native logs show a candidate-owned script failing at the time of the behavior.'
    contradictoryReproduction = 'A controlled test removes the candidate but the symptom remains, or enables it without producing the symptom.'
    contraryRuntimeAttribution = 'Runtime evidence attributes the affected object or failure to a different candidate.'
    strongerAlternative = 'Another candidate explains the same evidence more directly.'
    staleEvidence = 'Evidence was collected from a different profile, load order, generated-output state, or obsolete test run.'
    unverifiedAssumption = 'The diagnosis depends on a claim that has not been measured or inspected.'
}

function Write-InputTemplate {
    param([Parameter(Mandatory)][string]$Path)

    $template = [ordered]@{
        caseId = 'fixture-case-001'
        symptom = 'Describe the observed problem'
        symptomType = 'PlacementConflict'
        context = [ordered]@{
            environmentId = 'fixture-environment'
            profileId = 'fixture-profile'
            contextFingerprint = 'sha256:replace-with-current-context-fingerprint'
        }
        candidates = @(
            [ordered]@{
                name = 'Synthetic candidate'
                hypothesis = 'A synthetic candidate explains the reported fixture symptom.'
                evidence = [ordered]@{
                    controlledReproduction = [ordered]@{ value = 0.0; source = 'Not tested'; verificationStatus = 'Unverified' }
                    runtimeAttribution = [ordered]@{ value = 1.0; source = 'Deterministic fixture runtime observation'; verificationStatus = 'Verified' }
                    winningOverride = [ordered]@{ value = 1.0; source = 'Deterministic fixture winner observation'; verificationStatus = 'Verified' }
                    recordConflict = [ordered]@{ value = 0.8; source = 'Deterministic fixture record delta'; verificationStatus = 'Verified' }
                    spatialCorrelation = [ordered]@{ value = 1.0; source = 'Deterministic fixture location observation'; verificationStatus = 'Verified' }
                    requirementViolation = [ordered]@{ value = 0.0; source = 'Ordering verified'; verificationStatus = 'Verified' }
                    versionMismatch = [ordered]@{ value = 0.0; source = 'Versions match'; verificationStatus = 'Verified' }
                    assetResolutionFailure = [ordered]@{ value = 0.0; source = 'All referenced assets resolve'; verificationStatus = 'Verified' }
                    contradictoryReproduction = [ordered]@{ value = 1.0; source = 'Controlled fixture contradiction'; verificationStatus = 'Contradicted' }
                    contraryRuntimeAttribution = [ordered]@{ value = 0.5; source = 'Deterministic fixture owner observation'; verificationStatus = 'Collected' }
                    strongerAlternative = [ordered]@{ value = 0.0; source = 'Not collected'; verificationStatus = 'Unverified' }
                    staleEvidence = [ordered]@{ value = 0.0; source = 'Current fixture context'; verificationStatus = 'Verified' }
                    unverifiedAssumption = [ordered]@{ value = 0.2; source = 'Explicitly recorded unresolved assumption'; verificationStatus = 'Collected' }
                }
            }
        )
    }

    $json = $template | ConvertTo-Json -Depth 10
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
    Write-Host "Template written: $Path"
}

function Get-EvidenceValue {
    param(
        [Parameter(Mandatory)]$Evidence,
        [Parameter(Mandatory)][string]$Name
    )

    $property = $Evidence.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        return [pscustomobject]@{ Observed = $false; Value = 0.0; Source = 'Not supplied' }
    }

    $item = $property.Value
    $observed = $true
    if ($item -is [double] -or $item -is [decimal] -or $item -is [int] -or $item -is [long]) {
        $value = [double]$item
        $source = 'No source supplied'
    }
    else {
        $valueProperty = $item.PSObject.Properties['value']
        if ($null -eq $valueProperty) {
            throw "Evidence '$Name' must be a number or an object containing value and source."
        }
        $value = [double]$valueProperty.Value
        $sourceProperty = $item.PSObject.Properties['source']
        $source = if ($null -eq $sourceProperty) { 'No source supplied' } else { [string]$sourceProperty.Value }
        $observedProperty = $item.PSObject.Properties['observed']
        $collectedProperty = $item.PSObject.Properties['collected']
        $statusProperty = $item.PSObject.Properties['verificationStatus']
        if ($null -ne $observedProperty -and -not [bool]$observedProperty.Value) { $observed = $false }
        if ($null -ne $collectedProperty -and -not [bool]$collectedProperty.Value) { $observed = $false }
        if ($null -ne $statusProperty) {
            $status = [string]$statusProperty.Value
            if ($status -notin @('Collected', 'Verified', 'Contradicted', 'Stale')) { $observed = $false }
            if ($status -eq 'Stale' -and $Name -ne 'staleEvidence') { $observed = $false }
            if ($status -eq 'Contradicted' -and $Name -ne 'contradictoryReproduction') { $observed = $false }
        }
        # Backward-compatible protection for older fixture objects that used a
        # zero placeholder plus prose instead of an explicit collection flag.
        if ($null -eq $statusProperty -and $source -match '^(?i)not supplied|not collected|not tested|pending|unavailable|unknown$') { $observed = $false }
    }

    if ($value -lt 0.0 -or $value -gt 1.0) {
        throw "Evidence '$Name' must be between 0.0 and 1.0; received $value."
    }

    return [pscustomobject]@{ Observed = $observed; Value = if ($observed) { $value } else { 0.0 }; Source = $source }
}

function Get-Band {
    param([double]$Score)
    if ($Score -ge 90) { return 'Very High' }
    if ($Score -ge 75) { return 'High' }
    if ($Score -ge 55) { return 'Moderate' }
    if ($Score -ge 35) { return 'Low' }
    return 'Very Low'
}

function Measure-Candidate {
    param(
        [Parameter(Mandatory)]$Candidate,
        [Parameter(Mandatory)][string]$SymptomType
    )

    $weights = $profiles[$SymptomType]
    $breakdown = [System.Collections.Generic.List[object]]::new()
    $positiveScore = 0.0
    $observedPositiveWeight = 0.0
    $totalPositiveWeight = 0.0
    $directObserved = $false
    $directPositive = $false

    foreach ($name in $weights.Keys) {
        $weight = [double]$weights[$name]
        $totalPositiveWeight += $weight
        $e = Get-EvidenceValue -Evidence $Candidate.evidence -Name $name
        $points = $e.Value * $weight
        $positiveScore += $points
        if ($e.Observed) { $observedPositiveWeight += $weight }
        if ($name -in @('controlledReproduction', 'runtimeAttribution', 'logSignature', 'scriptLogCorrelation')) {
            if ($e.Observed) { $directObserved = $true }
            if ($e.Observed -and $e.Value -ge 0.75) { $directPositive = $true }
        }
        $breakdown.Add([pscustomobject]@{
            Parameter = $name
            Kind = 'Support'
            Definition = $definitions[$name]
            Weight = $weight
            Value = $e.Value
            Points = [math]::Round($points, 2)
            Observed = $e.Observed
            Source = $e.Source
        })
    }

    $penalty = 0.0
    $contradiction = 0.0
    foreach ($name in $penaltyWeights.Keys) {
        $weight = [double]$penaltyWeights[$name]
        $e = Get-EvidenceValue -Evidence $Candidate.evidence -Name $name
        $points = $e.Value * $weight
        $penalty += $points
        if ($name -eq 'contradictoryReproduction') { $contradiction = $e.Value }
        $breakdown.Add([pscustomobject]@{
            Parameter = $name
            Kind = 'Penalty'
            Definition = $definitions[$name]
            Weight = $weight
            Value = $e.Value
            Points = -[math]::Round($points, 2)
            Observed = $e.Observed
            Source = $e.Source
        })
    }

    $raw = [math]::Max(0.0, [math]::Min(100.0, $positiveScore - $penalty))
    $completeness = if ($totalPositiveWeight -eq 0) { 0 } else { 100.0 * $observedPositiveWeight / $totalPositiveWeight }
    $cap = 100.0
    $capReasons = [System.Collections.Generic.List[string]]::new()

    if (-not $directPositive) {
        $cap = [math]::Min($cap, 74.0)
        $capReasons.Add('No strong direct reproduction, runtime attribution, or diagnostic log evidence.')
    }
    if (-not $directObserved) {
        $cap = [math]::Min($cap, 59.0)
        $capReasons.Add('No direct evidence parameter was observed.')
    }
    if ($completeness -lt 40.0) {
        $cap = [math]::Min($cap, 54.0)
        $capReasons.Add('Less than 40% of the profile-weighted evidence was collected.')
    }
    if ($contradiction -ge 0.75) {
        $cap = [math]::Min($cap, 39.0)
        $capReasons.Add('A strong controlled contradiction exists.')
    }

    $final = [math]::Round([math]::Min($raw, $cap), 1)
    return [pscustomobject]@{
        Name = [string]$Candidate.name
        Hypothesis = [string]$Candidate.hypothesis
        SymptomType = $SymptomType
        Confidence = $final
        Rating = Get-Band -Score $final
        RawSupportMinusPenalty = [math]::Round($raw, 1)
        PositivePoints = [math]::Round($positiveScore, 1)
        PenaltyPoints = [math]::Round($penalty, 1)
        EvidenceCompleteness = [math]::Round($completeness, 1)
        AppliedCap = $cap
        CapReasons = @($capReasons)
        Breakdown = @($breakdown)
    }
}

function Convert-ReportToMarkdown {
    param([Parameter(Mandatory)]$Report)

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# Grid diagnosis: $($Report.CaseId)")
    $lines.Add('')
    $lines.Add("**Symptom:** $($Report.Symptom)")
    $lines.Add('')
    $lines.Add("**Type:** $($Report.SymptomType)")
    $lines.Add('')
    $lines.Add('Confidence measures evidence strength, not statistical probability.')
    $lines.Add('')
    $lines.Add('| Rank | Candidate | Confidence | Rating | Completeness | Penalty |')
    $lines.Add('|---:|---|---:|---|---:|---:|')
    $rank = 1
    foreach ($candidate in $Report.Candidates) {
        $lines.Add("| $rank | $($candidate.Name) | $($candidate.Confidence)% | $($candidate.Rating) | $($candidate.EvidenceCompleteness)% | $($candidate.PenaltyPoints) |")
        $rank++
    }

    foreach ($candidate in $Report.Candidates) {
        $lines.Add('')
        $lines.Add("## $($candidate.Name) - $($candidate.Confidence)% $($candidate.Rating)")
        $lines.Add('')
        $lines.Add($candidate.Hypothesis)
        $lines.Add('')
        $lines.Add('| Parameter | Kind | Weight | Value | Points | Observed | Source |')
        $lines.Add('|---|---|---:|---:|---:|---|---|')
        foreach ($item in $candidate.Breakdown) {
            $source = ([string]$item.Source).Replace('|', '\|')
            $lines.Add("| $($item.Parameter) | $($item.Kind) | $($item.Weight) | $($item.Value) | $($item.Points) | $($item.Observed) | $source |")
        }
        if ($candidate.CapReasons.Count -gt 0) {
            $lines.Add('')
            $lines.Add("Applied confidence cap: $($candidate.AppliedCap)%")
            foreach ($reason in $candidate.CapReasons) { $lines.Add("- $reason") }
        }
    }

    return $lines -join [Environment]::NewLine
}

if ($PSCmdlet.ParameterSetName -eq 'Template') {
    Write-InputTemplate -Path $WriteTemplate
    return
}

$inputData = Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json
$validTypes = @($profiles.Keys)
if ($inputData.symptomType -notin $validTypes) {
    throw "Unknown symptomType '$($inputData.symptomType)'. Valid values: $($validTypes -join ', ')"
}
if ($null -eq $inputData.candidates -or @($inputData.candidates).Count -eq 0) {
    throw 'Input must contain at least one diagnosis candidate.'
}

$candidateNames = @{}
$measured = foreach ($candidate in @($inputData.candidates)) {
    if ([string]::IsNullOrWhiteSpace([string]$candidate.name)) { throw 'Every candidate requires a name.' }
    $candidateKey = ([string]$candidate.name).ToUpperInvariant()
    if ($candidateNames.ContainsKey($candidateKey)) { throw "Duplicate diagnosis candidate name '$($candidate.name)' is not allowed." }
    $candidateNames[$candidateKey] = $true
    if ($null -eq $candidate.evidence) { throw "Candidate '$($candidate.name)' requires an evidence object." }
    Measure-Candidate -Candidate $candidate -SymptomType ([string]$inputData.symptomType)
}

$ranked = @($measured | Sort-Object `
    @{ Expression = 'Confidence'; Descending = $true },
    @{ Expression = 'EvidenceCompleteness'; Descending = $true },
    @{ Expression = 'Name'; Descending = $false })
$report = [pscustomobject]@{
    SchemaVersion = 1
    Tool = 'Invoke-GridDiagnosis'
    CaseId = [string]$inputData.caseId
    Symptom = [string]$inputData.symptom
    SymptomType = [string]$inputData.symptomType
    Context = $inputData.context
    GeneratedAt = (Get-Date).ToUniversalTime().ToString('o')
    ConfidenceDefinition = 'Evidence strength after symptom-specific weighting, explicit contradiction penalties, completeness reporting, and verification caps; not statistical probability.'
    RatingBands = [ordered]@{ VeryHigh = '90-100'; High = '75-89.9'; Moderate = '55-74.9'; Low = '35-54.9'; VeryLow = '0-34.9' }
    ParameterDefinitions = $definitions
    Candidates = $ranked
}

$rendered = switch ($Format) {
    'Object'   { $report }
    'Json'     { $report | ConvertTo-Json -Depth 12 }
    'Markdown' { Convert-ReportToMarkdown -Report $report }
}

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    if ($Format -eq 'Object') {
        $rendered | Export-Clixml -LiteralPath $OutputPath
    }
    else {
        [IO.File]::WriteAllText($OutputPath, [string]$rendered, [Text.UTF8Encoding]::new($false))
    }
    Write-Host "Diagnosis report written: $OutputPath"
}
else {
    $rendered
}
