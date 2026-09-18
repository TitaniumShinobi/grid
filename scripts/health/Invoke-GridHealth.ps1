#requires -Version 5.1
<#
.SYNOPSIS
Creates a truthful diagnostic case and dispatches a ready InvestigationPlan.
.DESCRIPTION
Routing is manifest-driven. Plain language is preserved but never mined for
plugins, records, hypotheses, or fixes. Incomplete requests return an honest
case state without invoking a native collector.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][ValidateNotNullOrEmpty()][string]$Request,
    [string]$Game = 'Auto',
    [string]$InstallationId,
    [string]$ProfileId,
    [string]$StatedLocation,
    [Alias('CandidatePlugin')][string[]]$StatedPluginNames = @(),
    [Alias('Mod','Provider')][string[]]$StatedProviderNames = @(),
    [Alias('ObservedFormId')][string[]]$StatedFormIds = @(),
    [Alias('ObservedEditorId')][string[]]$StatedEditorIds = @(),
    [string[]]$EvidenceReferences = @(),
    [object[]]$CollectorQueries = @(),
    [hashtable]$ConnectedContextParameters = @{},
    [hashtable]$AdapterParameters = @{},
    [string]$ExistingCasePath,
    [string]$OutputRoot = (Join-Path $env:TEMP 'Grid\health'),
    [switch]$PassThru
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'Grid.Health.psm1') -Force -DisableNameChecking
$scriptsRoot = Split-Path -Parent $PSScriptRoot
$contextProbe = @{}
foreach ($key in $ConnectedContextParameters.Keys) { $contextProbe[$key] = $ConnectedContextParameters[$key] }

$adapter = Resolve-GridGameAdapter -ScriptsRoot $scriptsRoot -Request $Request -Game $Game -ConnectedContextParameters $contextProbe -ExistingCasePath $ExistingCasePath
$caseEnvelope = New-GridDiagnosticCase -RawPrompt $Request -OutputRoot $OutputRoot -GameId $adapter.Id
$caseDirectory = $caseEnvelope.CaseDirectory
$casePath = $caseEnvelope.CasePath
$resolvedInstallation = if ($InstallationId) { $InstallationId } else { $null }
$resolvedProfile = if ($ProfileId) { $ProfileId } else { $null }
$plan = New-GridInvestigationPlan -CaseId $caseEnvelope.Case.caseId -GameId $adapter.Id -InstallationId $resolvedInstallation -ProfileId $resolvedProfile `
    -OriginalRequest $Request -StatedLocation $StatedLocation -StatedPluginNames $StatedPluginNames -StatedProviderNames $StatedProviderNames -StatedFormIds $StatedFormIds `
    -StatedEditorIds $StatedEditorIds -EvidenceReferences $EvidenceReferences -CollectorQueries $CollectorQueries `
    -CapabilityBindings $adapter.CapabilityBindings
$planPath = Save-GridInvestigationPlan -Plan $plan -CaseDirectory $caseDirectory
$caseData = Get-Content -LiteralPath $casePath -Raw | ConvertFrom-Json
$caseData.game = $adapter.Id
$caseData.investigationPlanPath = [IO.Path]::GetFileName($planPath)
$caseData.context = [pscustomobject][ordered]@{ installationId = $resolvedInstallation; profileId = $resolvedProfile }
Write-GridJsonAtomic -InputObject $caseData -LiteralPath $casePath

function Save-GridPublicDiagnosticResult {
    param([Parameter(Mandatory)]$DiagnosticResult, [object[]]$Evidence = @(), [object[]]$RemediationProposals = @(), [object[]]$VerificationResults = @())
    $resultPath = Save-GridDiagnosticResult -DiagnosticResult $DiagnosticResult -CaseDirectory $caseDirectory -Evidence $Evidence -RemediationProposals $RemediationProposals -VerificationResults $VerificationResults
    $text = ConvertTo-GridDiagnosticResultText -DiagnosticResult $DiagnosticResult -Evidence $Evidence -RemediationProposals $RemediationProposals -VerificationResults $VerificationResults
    $reportPath = Join-Path $caseDirectory 'diagnosis.md'
    [IO.File]::WriteAllText($reportPath, $text, [Text.UTF8Encoding]::new($false))
    $caseData = Get-Content -LiteralPath $casePath -Raw | ConvertFrom-Json
    $caseData.diagnosticResultPath = [IO.Path]::GetFileName($resultPath)
    $caseData.decision = $DiagnosticResult.result
    Write-GridJsonAtomic -InputObject $caseData -LiteralPath $casePath
    [pscustomobject]@{ ResultPath = $resultPath; ReportPath = $reportPath }
}

$result = $null
if ($plan.status -ne 'ReadyToCollect') {
    $detail = if ($plan.status -eq 'NeedsContext') { 'Grid needs an explicit installation and profile context before collection. No adapter collector was invoked.' } else { 'Grid needs explicit candidate plugin and record evidence before collection. No technical targets were inferred from the request.' }
    Set-GridDiagnosticCaseStatus -CasePath $casePath -Status $plan.status -Event 'InvestigationPlanIncomplete' -Detail $detail | Out-Null
    $next = if (@($plan.recommendedCollectors).Count -gt 0) { 'Continue investigation: ' + [string]$plan.recommendedCollectors[0] } else { 'Continue investigation by collecting the missing authoritative context and evidence.' }
    $diagnosticResult = New-GridUnresolvedDiagnosticResult -CaseId $caseEnvelope.Case.caseId -State $plan.status -Finding ('Insufficient evidence. ' + $detail) -NextStep $next
    $saved = Save-GridPublicDiagnosticResult -DiagnosticResult $diagnosticResult
    $result = [pscustomobject]@{ Tool = 'Invoke-GridHealth'; CaseId = $caseEnvelope.Case.caseId; Status = $plan.status; Game = $adapter.Id; CaseDirectory = $caseDirectory; CasePath = $casePath; InvestigationPlanPath = $planPath; DiagnosticResultPath = $saved.ResultPath; DiagnosticResult = $diagnosticResult; ReportPath = $saved.ReportPath; MissingInputs = @($plan.missingInputs) }
}
else {
    Set-GridDiagnosticCaseStatus -CasePath $casePath -Status 'Collecting' -Event 'CollectionStarted' -Detail "Invoking adapter '$($adapter.Id)' with an explicit InvestigationPlan." | Out-Null
    try {
        $command = Get-GridAdapterInvocationCommand -Adapter $adapter
        $arguments = @{}
        $candidates = [ordered]@{ Request = $Request; CaseId = $caseEnvelope.Case.caseId; CaseDirectory = $caseDirectory; InvestigationPlan = $plan }
        foreach ($name in $candidates.Keys) {
            $value = $candidates[$name]
            if ($command.Parameters.ContainsKey($name) -and $null -ne $value -and (-not ($value -is [string]) -or -not [string]::IsNullOrWhiteSpace($value))) { $arguments[$name] = $value }
        }
        foreach ($name in $ConnectedContextParameters.Keys) {
            if ($command.Parameters.ContainsKey($name) -and -not $arguments.ContainsKey($name)) { $arguments[$name] = $ConnectedContextParameters[$name] }
        }
        foreach ($name in $AdapterParameters.Keys) {
            if (-not $command.Parameters.ContainsKey($name)) { throw "Adapter '$($adapter.Id)' does not accept parameter '$name'." }
            $arguments[$name] = $AdapterParameters[$name]
        }
        $adapterResult = & $command @arguments
        $adapterStatus = [string]$adapterResult.Status
        $knownAdapterStatuses = @('NeedsContext','NeedsEvidence','ReadyToCollect','Incomplete','Unavailable','Failed','Diagnosed')
        if ($adapterStatus -notin $knownAdapterStatuses) { throw "Adapter returned an unsupported status '$adapterStatus'." }
        $finalStatus = switch ($adapterStatus) {
            'NeedsContext' { 'NeedsContext' }
            'NeedsEvidence' { 'NeedsEvidence' }
            'ReadyToCollect' { 'ReadyToCollect' }
            'Incomplete' { 'NeedsEvidence' }
            'Unavailable' { 'NeedsEvidence' }
            'Failed' { 'Failed' }
            'Diagnosed' { 'Diagnosed' }
        }
        $adapterEvidence = if ($null -ne $adapterResult.PSObject.Properties['Evidence']) { @($adapterResult.Evidence) } else { @() }
        $adapterRemediationProposals = if ($null -ne $adapterResult.PSObject.Properties['RemediationProposals']) { @($adapterResult.RemediationProposals) } else { @() }
        $adapterVerificationResults = if ($null -ne $adapterResult.PSObject.Properties['VerificationResults']) { @($adapterResult.VerificationResults) } else { @() }
        if ($null -ne $adapterResult.PSObject.Properties['DiagnosticResult']) {
            $diagnosticResult = $adapterResult.DiagnosticResult
            $validation = Test-GridDiagnosticResult -DiagnosticResult $diagnosticResult -Evidence $adapterEvidence -RemediationProposals $adapterRemediationProposals -VerificationResults $adapterVerificationResults
            if (-not $validation.IsValid) { throw ('Adapter returned an invalid DiagnosticResult: ' + ($validation.Errors -join ' ')) }
            $finalStatus = [string]$diagnosticResult.state
        }
        else {
            if ($finalStatus -eq 'Diagnosed') { $finalStatus = 'NeedsEvidence' }
            $collectorDetail = if ($adapterResult.PSObject.Properties['CollectorReason'] -and $adapterResult.CollectorReason) { [string]$adapterResult.CollectorReason } else { "Adapter collection ended with status '$adapterStatus' without a proven deterministic result." }
            $diagnosticResult = New-GridUnresolvedDiagnosticResult -CaseId $caseEnvelope.Case.caseId -State $finalStatus -Evidence $adapterEvidence -Finding ('Insufficient evidence. ' + $collectorDetail) -NextStep 'Continue investigation by collecting the evidence required to identify the affected subject, its role, the causal finding, and a supported solution.'
        }
        $plan.status = $finalStatus
        $plan.updatedAt = (Get-Date).ToUniversalTime().ToString('o')
        Save-GridInvestigationPlan -Plan $plan -CaseDirectory $caseDirectory | Out-Null
        Set-GridDiagnosticCaseStatus -CasePath $casePath -Status $finalStatus -Event 'CollectionCompleted' -Detail "Adapter returned '$adapterStatus'." | Out-Null
        $saved = Save-GridPublicDiagnosticResult -DiagnosticResult $diagnosticResult -Evidence $adapterEvidence -RemediationProposals $adapterRemediationProposals -VerificationResults $adapterVerificationResults
        $result = [pscustomobject]@{ Tool = 'Invoke-GridHealth'; CaseId = $caseEnvelope.Case.caseId; Status = $finalStatus; AdapterStatus = $adapterStatus; Game = $adapter.Id; CaseDirectory = $caseDirectory; CasePath = $casePath; InvestigationPlanPath = $planPath; DiagnosticResultPath = $saved.ResultPath; DiagnosticResult = $diagnosticResult; ReportPath = $saved.ReportPath; AdapterResult = $adapterResult }
    }
    catch {
        $message = $_.Exception.Message
        $plan.status = 'Failed'
        $plan.updatedAt = (Get-Date).ToUniversalTime().ToString('o')
        Save-GridInvestigationPlan -Plan $plan -CaseDirectory $caseDirectory | Out-Null
        Set-GridDiagnosticCaseStatus -CasePath $casePath -Status 'Failed' -Event 'CollectionFailed' -Detail $message | Out-Null
        $diagnosticResult = New-GridUnresolvedDiagnosticResult -CaseId $caseEnvelope.Case.caseId -State Failed -Finding ('Evidence collection failed. ' + $message) -NextStep 'Continue investigation after resolving the reported collector or validation failure.'
        $saved = Save-GridPublicDiagnosticResult -DiagnosticResult $diagnosticResult
        $result = [pscustomobject]@{ Tool = 'Invoke-GridHealth'; CaseId = $caseEnvelope.Case.caseId; Status = 'Failed'; Game = $adapter.Id; CaseDirectory = $caseDirectory; CasePath = $casePath; InvestigationPlanPath = $planPath; DiagnosticResultPath = $saved.ResultPath; DiagnosticResult = $diagnosticResult; ReportPath = $saved.ReportPath; Error = $message }
    }
}

if ($PassThru) { $result } else { Get-Content -LiteralPath $result.ReportPath -Raw; Write-Host "`nEvidence package: $caseDirectory" }
