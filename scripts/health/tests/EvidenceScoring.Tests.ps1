$ErrorActionPreference = 'Stop'
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }
function Get-NamedCandidate($Report, [string]$Name) {
    $matches = @($Report.Candidates | Where-Object Name -eq $Name)
    if ($matches.Count -ne 1) { throw "Expected exactly one candidate named '$Name'; found $($matches.Count)." }
    return $matches[0]
}
$healthRoot = Split-Path -Parent $PSScriptRoot
$engine = Join-Path $healthRoot 'Invoke-GridDiagnosis.ps1'
$root = Join-Path $env:TEMP ('grid-evidence-scoring-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $inputPath = Join-Path $root 'input.json'
    $data = [ordered]@{
        caseId = 'fixture-score'; symptom = 'Synthetic symptom'; symptomType = 'PlacementConflict'; context = @{}
        candidates = @([ordered]@{
            name = 'Synthetic candidate'; hypothesis = 'Synthetic hypothesis.'
            evidence = [ordered]@{
                controlledReproduction = [ordered]@{ value = 0.0; source = 'Not tested'; verificationStatus = 'Unverified' }
                runtimeAttribution = [ordered]@{ value = 0.0; source = 'Not collected'; collected = $false }
                winningOverride = [ordered]@{ value = 0.0; source = 'Verified absence'; verificationStatus = 'Verified' }
                staleEvidence = [ordered]@{ value = 0.5; source = 'Context fingerprint mismatch'; verificationStatus = 'Stale' }
            }
        })
    }
    [IO.File]::WriteAllText($inputPath, ($data | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    $report = & $engine -InputPath $inputPath -Format Object
    $candidate = $report.Candidates[0]
    Assert-Equal 15.0 $candidate.EvidenceCompleteness 'Only the verified zero winningOverride (weight 15) must count as collected.'
    Assert-Equal 54.0 $candidate.AppliedCap 'Low completeness must apply the stricter cap after uncollected direct placeholders are excluded.'
    Assert-Equal 5.0 $candidate.PenaltyPoints 'Verified stale evidence must apply its configured penalty.'
    if (@($candidate.CapReasons | Where-Object { $_ -match 'No direct evidence parameter' }).Count -ne 1) { throw 'Uncollected direct placeholders did not trigger the no-direct-evidence cap reason.' }
    $controlled = @($candidate.Breakdown | Where-Object Parameter -eq 'controlledReproduction')[0]
    if ($controlled.Observed) { throw 'Unverified zero placeholder was incorrectly counted as observed.' }
    Write-Host 'PASS: uncollected zero placeholders do not inflate completeness or direct-evidence caps.'

    $data.candidates = @(
        [ordered]@{ name = 'Zulu'; hypothesis = 'Synthetic tie.'; evidence = [ordered]@{ winningOverride = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' } } }
        [ordered]@{ name = 'Alpha'; hypothesis = 'Synthetic tie.'; evidence = [ordered]@{ winningOverride = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' } } }
    )
    [IO.File]::WriteAllText($inputPath, ($data | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    $tie = & $engine -InputPath $inputPath -Format Object
    Assert-Equal 'Alpha' $tie.Candidates[0].Name 'Equal scores must use deterministic ordinal candidate ordering.'

    $data.candidates = @(
        [ordered]@{
            name = 'Cap 74'; hypothesis = 'Synthetic strong-direct-evidence cap fixture.'
            evidence = [ordered]@{
                controlledReproduction = [ordered]@{ value = 0.7; source = 'Fixture'; verificationStatus = 'Verified' }
                runtimeAttribution = [ordered]@{ value = 0.7; source = 'Fixture'; verificationStatus = 'Verified' }
                winningOverride = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
                recordConflict = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
                spatialCorrelation = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
                requirementViolation = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
                versionMismatch = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
                assetResolutionFailure = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            }
        }
        [ordered]@{
            name = 'Cap 59'; hypothesis = 'Synthetic no-direct-evidence cap fixture.'
            evidence = [ordered]@{
                winningOverride = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
                recordConflict = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
                spatialCorrelation = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
                requirementViolation = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
                versionMismatch = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
                assetResolutionFailure = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            }
        }
        [ordered]@{
            name = 'Cap 54'; hypothesis = 'Synthetic low-completeness cap fixture.'
            evidence = [ordered]@{
                controlledReproduction = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            }
        }
        [ordered]@{
            name = 'Cap 39'; hypothesis = 'Synthetic controlled-contradiction cap fixture.'
            evidence = [ordered]@{
                controlledReproduction = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
                runtimeAttribution = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
                winningOverride = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
                recordConflict = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
                spatialCorrelation = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
                requirementViolation = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
                versionMismatch = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
                assetResolutionFailure = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
                contradictoryReproduction = [ordered]@{ value = 0.75; source = 'Fixture'; verificationStatus = 'Contradicted' }
            }
        }
    )
    [IO.File]::WriteAllText($inputPath, ($data | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    $caps = & $engine -InputPath $inputPath -Format Object

    $cap74 = Get-NamedCandidate -Report $caps -Name 'Cap 74'
    Assert-Equal 74.0 $cap74.AppliedCap 'Weak direct evidence must select the 74% cap.'
    Assert-Equal 74.0 $cap74.Confidence 'The 74% cap must bind support above the ceiling.'

    $cap59 = Get-NamedCandidate -Report $caps -Name 'Cap 59'
    Assert-Equal 59.0 $cap59.AppliedCap 'No observed direct parameter must select the 59% cap.'
    Assert-Equal 52.0 $cap59.Confidence 'Non-direct PlacementConflict evidence totals 52%, below the 59% ceiling.'

    $cap54 = Get-NamedCandidate -Report $caps -Name 'Cap 54'
    Assert-Equal 54.0 $cap54.AppliedCap 'Less than 40% weighted completeness must select the 54% cap.'
    Assert-Equal 30.0 $cap54.Confidence 'The single collected 30-point parameter remains below the 54% ceiling.'

    $cap39 = Get-NamedCandidate -Report $caps -Name 'Cap 39'
    Assert-Equal 39.0 $cap39.AppliedCap 'A 0.75 controlled contradiction must select the 39% cap.'
    Assert-Equal 39.0 $cap39.Confidence 'The 39% contradiction cap must bind support above the ceiling.'
    Write-Host 'PASS: confidence hard caps select and bind at their deterministic thresholds.'

    $data.candidates = @(
        [ordered]@{ name = 'Rating 90.0'; hypothesis = 'Synthetic rating boundary.'; evidence = [ordered]@{
            controlledReproduction = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            runtimeAttribution = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            winningOverride = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            recordConflict = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            spatialCorrelation = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            requirementViolation = [ordered]@{ value = 0.4; source = 'Fixture'; verificationStatus = 'Verified' }
        } }
        [ordered]@{ name = 'Rating 89.9'; hypothesis = 'Synthetic rating boundary.'; evidence = [ordered]@{
            controlledReproduction = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            runtimeAttribution = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            winningOverride = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            recordConflict = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            spatialCorrelation = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            requirementViolation = [ordered]@{ value = 0.38; source = 'Fixture'; verificationStatus = 'Verified' }
        } }
        [ordered]@{ name = 'Rating 75.0'; hypothesis = 'Synthetic rating boundary.'; evidence = [ordered]@{
            controlledReproduction = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            runtimeAttribution = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            winningOverride = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            recordConflict = [ordered]@{ value = 0.8; source = 'Fixture'; verificationStatus = 'Verified' }
        } }
        [ordered]@{ name = 'Rating 74.9'; hypothesis = 'Synthetic rating boundary.'; evidence = [ordered]@{
            controlledReproduction = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            runtimeAttribution = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            winningOverride = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            recordConflict = [ordered]@{ value = 0.793333333333333; source = 'Fixture'; verificationStatus = 'Verified' }
        } }
        [ordered]@{ name = 'Rating 55.0'; hypothesis = 'Synthetic rating boundary.'; evidence = [ordered]@{
            controlledReproduction = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            runtimeAttribution = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            requirementViolation = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            assetResolutionFailure = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
        } }
        [ordered]@{ name = 'Rating 54.9'; hypothesis = 'Synthetic rating boundary.'; evidence = [ordered]@{
            controlledReproduction = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            runtimeAttribution = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            requirementViolation = [ordered]@{ value = 0.98; source = 'Fixture'; verificationStatus = 'Verified' }
            assetResolutionFailure = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
        } }
        [ordered]@{ name = 'Rating 35.0'; hypothesis = 'Synthetic rating boundary.'; evidence = [ordered]@{
            controlledReproduction = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
            runtimeAttribution = [ordered]@{ value = 0.0; source = 'Verified absence'; verificationStatus = 'Verified' }
            requirementViolation = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
        } }
        [ordered]@{ name = 'Rating 34.9'; hypothesis = 'Synthetic rating boundary.'; evidence = [ordered]@{
            controlledReproduction = [ordered]@{ value = 0.996666666666667; source = 'Fixture'; verificationStatus = 'Verified' }
            runtimeAttribution = [ordered]@{ value = 0.0; source = 'Verified absence'; verificationStatus = 'Verified' }
            requirementViolation = [ordered]@{ value = 1.0; source = 'Fixture'; verificationStatus = 'Verified' }
        } }
        [ordered]@{ name = 'Rating 0.0'; hypothesis = 'Synthetic rating boundary.'; evidence = [ordered]@{
            controlledReproduction = [ordered]@{ value = 0.0; source = 'Verified absence'; verificationStatus = 'Verified' }
            runtimeAttribution = [ordered]@{ value = 0.0; source = 'Verified absence'; verificationStatus = 'Verified' }
            winningOverride = [ordered]@{ value = 0.0; source = 'Verified absence'; verificationStatus = 'Verified' }
        } }
    )
    [IO.File]::WriteAllText($inputPath, ($data | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    $ratings = & $engine -InputPath $inputPath -Format Object
    $ratingExpectations = [ordered]@{
        'Rating 90.0' = @('90', 'Very High')
        'Rating 89.9' = @('89.9', 'High')
        'Rating 75.0' = @('75', 'High')
        'Rating 74.9' = @('74.9', 'Moderate')
        'Rating 55.0' = @('55', 'Moderate')
        'Rating 54.9' = @('54.9', 'Low')
        'Rating 35.0' = @('35', 'Low')
        'Rating 34.9' = @('34.9', 'Very Low')
        'Rating 0.0' = @('0', 'Very Low')
    }
    foreach ($name in $ratingExpectations.Keys) {
        $candidate = Get-NamedCandidate -Report $ratings -Name $name
        Assert-Equal ([double]$ratingExpectations[$name][0]) $candidate.Confidence "$name confidence boundary failed."
        Assert-Equal $ratingExpectations[$name][1] $candidate.Rating "$name rating boundary failed."
    }
    Write-Host 'PASS: all confidence rating boundaries classify deterministically.'
}
finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
