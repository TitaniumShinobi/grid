#requires -Version 5.1
Set-StrictMode -Version Latest

function Get-GridGtaEvidenceCount {
    param($Source)
    if ($null -ne $Source -and $null -ne $Source.PSObject.Properties['evidence']) {
        return @($Source.evidence | Where-Object { [double]$_.value -gt 0 -and [string]$_.verificationStatus -in @('Collected','Verified') }).Count
    }
    foreach ($name in @('eventCount','reportCount','recordCount','count','EventCount','ReportCount','RecordCount','Count')) {
        if ($null -ne $Source -and $null -ne $Source.PSObject.Properties[$name]) { return [int]$Source.$name }
    }
    foreach ($name in @('events','reports','records','Events','Reports','Records')) {
        if ($null -ne $Source -and $null -ne $Source.PSObject.Properties[$name]) { return @($Source.$name).Count }
    }
    return 0
}

function Resolve-GridGtaCrashAssessment {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$EvidencePackage, [Parameter(Mandatory)][string]$CaseId)
    if ([int]$EvidencePackage.schemaVersion -ne 1 -or [string]::IsNullOrWhiteSpace([string]$EvidencePackage.contextFingerprint)) {
        throw 'GtaCrashAssessmentEvidenceInvalid: a versioned, fingerprinted GTA evidence package is required.'
    }
    $evidenceByParameter = @{}
    foreach ($item in @($EvidencePackage.evidence)) { $evidenceByParameter[[string]$item.parameter] = $item }
    $assertions = @()
    $identityEvidence = $evidenceByParameter['gta.installation.identity']
    $assertions += [pscustomobject][ordered]@{
        assertionId = 'gta.observed.connected-installation'; classification = 'ObservedFact'
        statement = "Evidence was collected for the exact account-connected $($EvidencePackage.edition) installation."
        evidenceIds = @([string]$identityEvidence.evidenceId); ruleId = 'grid.assessment.direct-observation.v1'; contradictingEvidenceIds = @()
    }
    $applicationCount = Get-GridGtaEvidenceCount $EvidencePackage.windowsApplicationFailures
    $werCount = Get-GridGtaEvidenceCount $EvidencePackage.windowsErrorReporting
    $crashArtifacts = @($EvidencePackage.declaredArtifacts | Where-Object rootId -like '*CrashDiagnostics' | ForEach-Object files | Where-Object status -in @('Hashed','MetadataOnlyOversize'))
    $historicalIds = @('gta.windows.application-failures','gta.windows.error-reporting') | ForEach-Object { if ($evidenceByParameter.ContainsKey($_)) { [string]$evidenceByParameter[$_].evidenceId } }
    $crashRootEvidence = @($EvidencePackage.evidence | Where-Object parameter -like 'gta.userdata.*CrashDiagnostics')
    $historicalIds += @($crashRootEvidence | ForEach-Object evidenceId)
    if ($applicationCount -gt 0 -or $werCount -gt 0 -or $crashArtifacts.Count -gt 0) {
        $directHistoricalIds = @($EvidencePackage.evidence | Where-Object {
            [string]$_.parameter -in @('windowsApplicationFailureEvent','windowsErrorReportingEvent','windowsErrorReportingArchive') -and
            [double]$_.value -gt 0 -and [string]$_.verificationStatus -in @('Collected','Verified')
        } | ForEach-Object evidenceId)
        $directHistoricalIds += @($crashRootEvidence | Where-Object verificationStatus -in @('Collected','Verified') | ForEach-Object evidenceId)
        $assertions += [pscustomobject][ordered]@{
            assertionId = 'gta.observed.historical-failure-evidence-available'; classification = 'ObservedFact'
            statement = 'Historical failure evidence exists for the connected executable and can be investigated without first reproducing the failure.'
            evidenceIds = @($directHistoricalIds | Sort-Object -Unique); ruleId = 'grid.assessment.direct-observation.v1'; contradictingEvidenceIds = @()
        }
    }
    $assertions += [pscustomobject][ordered]@{
        assertionId = 'gta.insufficient.report-correlation'; classification = 'InsufficientEvidence'
        statement = 'No collected evidence deterministically binds a retained failure record to the user-reported gameplay moment.'
        evidenceIds = @(); ruleId = $null; contradictingEvidenceIds = @(); missingInputs = @('A deterministic execution identifier or bounded time correlation for the reported failure')
    }
    $assertions += [pscustomobject][ordered]@{
        assertionId = 'gta.insufficient.cause'; classification = 'InsufficientEvidence'
        statement = 'The collected evidence does not prove a cause. File, module, configuration, or software presence alone is not causal evidence.'
        evidenceIds = @(); ruleId = $null; contradictingEvidenceIds = @(); missingInputs = @('Evidence satisfying a registered causal proof obligation')
    }
    $nextObservation = if ($applicationCount -gt 0 -or $werCount -gt 0 -or $crashArtifacts.Count -gt 0) {
        'Correlate retained event, WER, crash-context, dump, launcher, and configuration timestamps for one exact historical execution interval; do not infer cause from component presence.'
    } else {
        'Existing historical sources are insufficient. Request one explicitly authorized reproduction only after defining the exact additional event and artifact capture scope.'
    }
    $nextEvidenceDecision = [pscustomobject][ordered]@{
        status = 'Required'; collectorCapabilityId = 'grid.game.grandtheftautov.crash-evidence.collect'
        reason = $nextObservation; requiredInputs = @('Exact connected context','Bounded historical execution interval')
    }
    $assessmentContract = New-GridDiagnosticAssessment -CaseId $CaseId -ContextFingerprint ([string]$EvidencePackage.contextFingerprint) `
        -Evidence @($EvidencePackage.evidence) -Assertions @($assertions) -RegisteredRules @() -NextEvidenceDecision $nextEvidenceDecision `
        -AssessorName 'Grid.GtaV.CrashAssessment' -AssessorVersion '1.0.0'
    $diagnosticResult = New-GridUnresolvedDiagnosticResult -CaseId $CaseId -State 'NeedsEvidence' `
        -ContextFingerprint ([string]$EvidencePackage.contextFingerprint) -Evidence @($EvidencePackage.evidence) `
        -Finding 'Available evidence does not yet prove which component or condition caused the reported crash.' -NextStep $nextObservation
    [pscustomobject][ordered]@{
        schemaVersion = 1; assessmentId = [string]$assessmentContract.assessmentId
        caseId = $CaseId; gameId = [string]$EvidencePackage.gameId; installationId = [string]$EvidencePackage.installationId
        profileId = [string]$EvidencePackage.profileId; contextFingerprint = [string]$EvidencePackage.contextFingerprint
        state = [string]$assessmentContract.state; assertions = @($assessmentContract.assertions); nextObservation = $nextObservation
        assessmentContract = $assessmentContract
        diagnosticResult = $diagnosticResult; createdAtUtc = [datetime]::UtcNow.ToString('o'); changedExternalState = $false
    }
}
