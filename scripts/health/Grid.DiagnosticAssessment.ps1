#requires -Version 5.1
<#
.SYNOPSIS
Defines Grid's deterministic epistemic assessment contract.
.DESCRIPTION
An assessment separates direct observations, deterministic derivations,
supported hypotheses, insufficient evidence, and proven causes. Derived,
hypothesis, and causal assertions require a registered non-model rule. A cause
cannot be proven unless every registered proof obligation is satisfied by
current collected or verified evidence and no contradictory evidence is bound.
#>

$script:GridDiagnosticAssessmentSchemaVersion = 1
$script:GridDiagnosticAssessmentPolicyVersion = 'diagnostic-assessment.v1'
$script:GridDiagnosticAssertionClasses = @('ObservedFact','DerivedFact','SupportedHypothesis','InsufficientEvidence','ProvenCause')
$script:GridDiagnosticAssessmentStates = @('EvidenceAssessed','InsufficientEvidence','CauseProven')
$script:GridDiagnosticRuleKinds = @('DeterministicDerivation','HypothesisSupport','CausalProof')

function Get-GridAssessmentObjectValue {
    param($Object, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Object) { return $null }
    if ($Object -is [Collections.IDictionary]) {
        if ($Object.Contains($Name)) { return $Object[$Name] }
        return $null
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($property) { return $property.Value }
    $null
}

function Get-GridDiagnosticAssessmentFingerprint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Assessment,
        [string]$PolicyVersion = $script:GridDiagnosticAssessmentPolicyVersion
    )
    $semantic = [ordered]@{
        policyVersion = $PolicyVersion
        caseId = [string]$Assessment.caseId
        contextFingerprint = [string]$Assessment.contextFingerprint
        evidenceFingerprint = [string]$Assessment.evidenceFingerprint
        state = [string]$Assessment.state
        assessor = $Assessment.assessor
        assertions = @($Assessment.assertions)
        nextEvidenceDecision = $Assessment.nextEvidenceDecision
    }
    $canonical = ConvertTo-GridCanonicalEvidenceValue -Value $semantic
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical)))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Test-GridDiagnosticAssessment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Assessment,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Evidence,
        [AllowEmptyCollection()][object[]]$RegisteredRules = @()
    )
    $errors = New-Object Collections.Generic.List[string]
    foreach ($field in @('schemaVersion','assessmentId','caseId','state','contextFingerprint','evidenceFingerprint','policyVersion','assessor','assertions','nextEvidenceDecision','assessmentFingerprint','createdAt')) {
        if ($null -eq $Assessment.PSObject.Properties[$field]) { $errors.Add("Missing diagnostic assessment field: $field") }
    }
    if ($errors.Count -gt 0) { return [pscustomobject]@{ IsValid=$false; Errors=$errors.ToArray() } }
    if ([int]$Assessment.schemaVersion -ne $script:GridDiagnosticAssessmentSchemaVersion) { $errors.Add('Unsupported diagnostic assessment schemaVersion.') }
    if ([string]$Assessment.state -notin $script:GridDiagnosticAssessmentStates) { $errors.Add('Invalid diagnostic assessment state.') }
    if ([string]::IsNullOrWhiteSpace([string]$Assessment.caseId) -or [string]::IsNullOrWhiteSpace([string]$Assessment.contextFingerprint)) {
        $errors.Add('Diagnostic assessment requires caseId and contextFingerprint.')
    }
    if ([string](Get-GridAssessmentObjectValue $Assessment.assessor 'kind') -ne 'DeterministicRuleEngine') {
        $errors.Add('Diagnostic assessment authority must be DeterministicRuleEngine.')
    }
    if ([string]::IsNullOrWhiteSpace([string](Get-GridAssessmentObjectValue $Assessment.assessor 'name')) -or
        [string]::IsNullOrWhiteSpace([string](Get-GridAssessmentObjectValue $Assessment.assessor 'version'))) {
        $errors.Add('Diagnostic assessment requires a named, versioned deterministic assessor.')
    }

    $knownEvidence = @{}
    foreach ($item in @($Evidence)) {
        $validation = Test-GridEvidenceItem -Evidence $item
        if (-not $validation.IsValid) { $errors.Add(($validation.Errors -join ' ')); continue }
        $knownEvidence[[string]$item.evidenceId] = $item
    }
    $expectedEvidenceFingerprint = Get-GridEvidenceFingerprint -Evidence $Evidence
    if ([string]$Assessment.evidenceFingerprint -ne $expectedEvidenceFingerprint) { $errors.Add('Diagnostic assessment evidenceFingerprint does not match supplied evidence.') }

    $rules = @{}
    foreach ($rule in @($RegisteredRules)) {
        $ruleId = [string](Get-GridAssessmentObjectValue $rule 'ruleId')
        $version = [string](Get-GridAssessmentObjectValue $rule 'version')
        $kind = [string](Get-GridAssessmentObjectValue $rule 'kind')
        $source = [string](Get-GridAssessmentObjectValue $rule 'source')
        if ($ruleId -notmatch '^grid\.[a-z0-9]+(?:[.-][a-z0-9]+)*$' -or $version -notmatch '^\d+\.\d+\.\d+$' -or
            $kind -notin $script:GridDiagnosticRuleKinds -or $source -ne 'ProductionManifest') {
            $errors.Add("Invalid registered deterministic rule '$ruleId'.")
            continue
        }
        if ($rules.ContainsKey($ruleId)) { $errors.Add("Duplicate registered deterministic rule '$ruleId'.") }
        else { $rules[$ruleId] = $rule }
    }

    $assertionIds = @{}
    $hasInsufficient = $false
    $hasProvenCause = $false
    foreach ($assertion in @($Assessment.assertions)) {
        $assertionId = [string](Get-GridAssessmentObjectValue $assertion 'assertionId')
        $classification = [string](Get-GridAssessmentObjectValue $assertion 'classification')
        $statement = [string](Get-GridAssessmentObjectValue $assertion 'statement')
        if ([string]::IsNullOrWhiteSpace($assertionId) -or $assertionIds.ContainsKey($assertionId)) { $errors.Add("Invalid or duplicate assertionId '$assertionId'.") }
        else { $assertionIds[$assertionId] = $true }
        if ($classification -notin $script:GridDiagnosticAssertionClasses) { $errors.Add("Invalid assertion classification '$classification'."); continue }
        if ([string]::IsNullOrWhiteSpace($statement) -or $statement.Length -gt 4096 -or $statement.IndexOfAny([char[]]@(0,10,13)) -ge 0) {
            $errors.Add("Assertion '$assertionId' requires bounded single-line text.")
        }
        $evidenceIds = @((Get-GridAssessmentObjectValue $assertion 'evidenceIds') | ForEach-Object { [string]$_ } | Select-Object -Unique)
        $contradictingIds = @((Get-GridAssessmentObjectValue $assertion 'contradictingEvidenceIds') | ForEach-Object { [string]$_ } | Select-Object -Unique)
        foreach ($id in @($evidenceIds + $contradictingIds)) {
            if (-not $knownEvidence.ContainsKey($id)) { $errors.Add("Assertion '$assertionId' references unknown evidenceId '$id'."); continue }
            $item = $knownEvidence[$id]
            if ([string]$item.contextFingerprint -ne [string]$Assessment.contextFingerprint) { $errors.Add("Assertion '$assertionId' references evidence from another context.") }
            if ([string]$item.sourceType -eq 'ModelReasoning') { $errors.Add("Assertion '$assertionId' cannot use model reasoning as diagnostic authority.") }
        }
        foreach ($id in $evidenceIds) {
            if ($knownEvidence.ContainsKey($id) -and [string]$knownEvidence[$id].verificationStatus -notin @('Collected','Verified')) {
                $errors.Add("Assertion '$assertionId' uses evidence that is not current collected or verified evidence.")
            }
        }

        $ruleId = [string](Get-GridAssessmentObjectValue $assertion 'ruleId')
        switch ($classification) {
            'ObservedFact' {
                if ($evidenceIds.Count -eq 0) { $errors.Add("ObservedFact '$assertionId' requires direct evidence.") }
                if (-not [string]::IsNullOrWhiteSpace($ruleId) -and $ruleId -ne 'grid.assessment.direct-observation.v1') {
                    $errors.Add("ObservedFact '$assertionId' may only use the reserved direct-observation rule.")
                }
            }
            'InsufficientEvidence' {
                $hasInsufficient = $true
                $missing = @((Get-GridAssessmentObjectValue $assertion 'missingInputs') | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
                if ($missing.Count -eq 0) { $errors.Add("InsufficientEvidence '$assertionId' requires explicit missingInputs.") }
                if (-not [string]::IsNullOrWhiteSpace($ruleId)) { $errors.Add("InsufficientEvidence '$assertionId' cannot assert a derivation rule.") }
            }
            default {
                if ($evidenceIds.Count -eq 0) { $errors.Add("$classification '$assertionId' requires supporting evidence.") }
                if ([string]::IsNullOrWhiteSpace($ruleId) -or -not $rules.ContainsKey($ruleId)) {
                    $errors.Add("$classification '$assertionId' requires a registered deterministic rule.")
                    continue
                }
                $rule = $rules[$ruleId]
                $ruleVersion = [string](Get-GridAssessmentObjectValue $assertion 'ruleVersion')
                if ($ruleVersion -ne [string](Get-GridAssessmentObjectValue $rule 'version')) {
                    $errors.Add("Assertion '$assertionId' does not bind the registered version of rule '$ruleId'.")
                }
                $requiredKind = switch ($classification) {
                    'DerivedFact' { 'DeterministicDerivation' }
                    'SupportedHypothesis' { 'HypothesisSupport' }
                    'ProvenCause' { 'CausalProof' }
                }
                if ([string](Get-GridAssessmentObjectValue $rule 'kind') -ne $requiredKind) {
                    $errors.Add("Rule '$ruleId' cannot produce $classification.")
                }
                $parameters = @($evidenceIds | Where-Object { $knownEvidence.ContainsKey($_) } | ForEach-Object { [string]$knownEvidence[$_].parameter } | Select-Object -Unique)
                foreach ($parameter in @((Get-GridAssessmentObjectValue $rule 'requiredEvidenceParameters'))) {
                    if ([string]$parameter -notin $parameters) { $errors.Add("Assertion '$assertionId' does not satisfy required evidence parameter '$parameter'.") }
                }
                if ($classification -eq 'ProvenCause') {
                    $hasProvenCause = $true
                    if ($contradictingIds.Count -gt 0) { $errors.Add("ProvenCause '$assertionId' cannot retain contradictory evidence.") }
                    $satisfied = @((Get-GridAssessmentObjectValue $assertion 'satisfiedProofObligationIds') | ForEach-Object { [string]$_ } | Select-Object -Unique)
                    $obligations = @((Get-GridAssessmentObjectValue $rule 'proofObligations'))
                    if ($obligations.Count -eq 0) { $errors.Add("Causal proof rule '$ruleId' must declare proof obligations.") }
                    $knownObligations = @()
                    foreach ($obligation in $obligations) {
                        $obligationId = [string](Get-GridAssessmentObjectValue $obligation 'obligationId')
                        if (-not [string]::IsNullOrWhiteSpace($obligationId)) { $knownObligations += $obligationId }
                        $obligationParameters = @((Get-GridAssessmentObjectValue $obligation 'requiredEvidenceParameters') | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
                        if ([string]::IsNullOrWhiteSpace($obligationId) -or $obligationParameters.Count -eq 0) {
                            $errors.Add("Causal proof rule '$ruleId' contains an invalid proof obligation.")
                            continue
                        }
                        foreach ($parameter in $obligationParameters) {
                            if ($parameter -notin $parameters) { $errors.Add("Proof obligation '$obligationId' lacks required evidence parameter '$parameter'.") }
                        }
                        if ($obligationId -notin $satisfied) {
                            $errors.Add("ProvenCause '$assertionId' has not satisfied proof obligation '$obligationId'.")
                        }
                    }
                    foreach ($obligationId in $satisfied) {
                        if ($obligationId -notin $knownObligations) { $errors.Add("ProvenCause '$assertionId' names unknown proof obligation '$obligationId'.") }
                    }
                }
            }
        }
    }
    if (@($Assessment.assertions).Count -eq 0) { $errors.Add('Diagnostic assessment requires at least one assertion.') }
    if ($hasProvenCause -and $hasInsufficient) { $errors.Add('A diagnostic assessment cannot simultaneously claim ProvenCause and InsufficientEvidence.') }
    $expectedState = if ($hasProvenCause) { 'CauseProven' } elseif ($hasInsufficient) { 'InsufficientEvidence' } else { 'EvidenceAssessed' }
    if ([string]$Assessment.state -ne $expectedState) { $errors.Add("Diagnostic assessment state must be '$expectedState' for its assertions.") }

    $decisionStatus = [string](Get-GridAssessmentObjectValue $Assessment.nextEvidenceDecision 'status')
    if ($decisionStatus -notin @('NotRequired','Required')) { $errors.Add('nextEvidenceDecision.status must be NotRequired or Required.') }
    if ($hasInsufficient -and $decisionStatus -ne 'Required') { $errors.Add('Insufficient evidence requires a next bounded evidence decision.') }
    if ($decisionStatus -eq 'Required') {
        $capabilityId = [string](Get-GridAssessmentObjectValue $Assessment.nextEvidenceDecision 'collectorCapabilityId')
        $reason = [string](Get-GridAssessmentObjectValue $Assessment.nextEvidenceDecision 'reason')
        $requiredInputs = @((Get-GridAssessmentObjectValue $Assessment.nextEvidenceDecision 'requiredInputs'))
        if ($capabilityId -notmatch '^grid\.[a-z0-9]+(?:[.-][a-z0-9]+)*$' -or [string]::IsNullOrWhiteSpace($reason) -or $requiredInputs.Count -eq 0) {
            $errors.Add('A required next evidence decision needs collectorCapabilityId, reason, and requiredInputs.')
        }
    }
    $expectedFingerprint = Get-GridDiagnosticAssessmentFingerprint -Assessment $Assessment -PolicyVersion ([string]$Assessment.policyVersion)
    if ([string]$Assessment.assessmentFingerprint -ne $expectedFingerprint) { $errors.Add('Diagnostic assessment fingerprint does not match its semantic content.') }
    [pscustomobject]@{ IsValid=($errors.Count -eq 0); Errors=$errors.ToArray() }
}

function New-GridDiagnosticAssessment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$CaseId,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ContextFingerprint,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Evidence,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][object[]]$Assertions,
        [AllowEmptyCollection()][object[]]$RegisteredRules = @(),
        [Parameter(Mandatory)]$NextEvidenceDecision,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$AssessorName,
        [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$AssessorVersion,
        [datetime]$CreatedAt = [datetime]::UtcNow
    )
    $hasCause = @($Assertions | Where-Object classification -eq 'ProvenCause').Count -gt 0
    $hasInsufficient = @($Assertions | Where-Object classification -eq 'InsufficientEvidence').Count -gt 0
    $assessment = [pscustomobject][ordered]@{
        schemaVersion = $script:GridDiagnosticAssessmentSchemaVersion
        assessmentId = 'assessment-' + [guid]::NewGuid().ToString('N')
        caseId = $CaseId
        state = if ($hasCause) { 'CauseProven' } elseif ($hasInsufficient) { 'InsufficientEvidence' } else { 'EvidenceAssessed' }
        contextFingerprint = $ContextFingerprint
        evidenceFingerprint = Get-GridEvidenceFingerprint -Evidence $Evidence
        policyVersion = $script:GridDiagnosticAssessmentPolicyVersion
        assessor = [pscustomobject][ordered]@{ kind='DeterministicRuleEngine'; name=$AssessorName; version=$AssessorVersion }
        assertions = @($Assertions | Sort-Object { [string]$_.assertionId })
        nextEvidenceDecision = $NextEvidenceDecision
        assessmentFingerprint = ''
        createdAt = $CreatedAt.ToUniversalTime().ToString('o')
    }
    $assessment.assessmentFingerprint = Get-GridDiagnosticAssessmentFingerprint -Assessment $assessment
    $validation = Test-GridDiagnosticAssessment -Assessment $assessment -Evidence $Evidence -RegisteredRules $RegisteredRules
    if (-not $validation.IsValid) { throw ($validation.Errors -join ' ') }
    $assessment
}
