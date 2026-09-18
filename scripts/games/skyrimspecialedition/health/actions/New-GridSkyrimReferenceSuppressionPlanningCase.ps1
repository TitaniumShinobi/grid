#requires -Version 5.1

function New-GridSkyrimReferenceSuppressionPlanningCase {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseStoreRoot,
        [Parameter(Mandatory)][string]$EvidenceCaseDirectory,
        [Parameter(Mandatory)][string]$PlanningCaseId,
        [Parameter(Mandatory)][string]$ContextFingerprint,
        [Parameter(Mandatory)][object[]]$TargetReferences,
        [Parameter(Mandatory)][string]$PatchPluginName,
        [Parameter(Mandatory)][string]$Intent,
        [switch]$PassThru
    )
    $transaction = New-GridCaseStoreTransaction -StoreRoot $CaseStoreRoot -CaseId $PlanningCaseId
    try {
        $evidenceDirectory = [IO.Path]::GetFullPath($EvidenceCaseDirectory).TrimEnd('\','/')
        $evidenceSeal = Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory $evidenceDirectory
        if (-not $evidenceSeal.IsValid) { throw ('ReferenceSuppressionEvidenceCaseInvalid: ' + (@($evidenceSeal.Errors) -join ' ')) }
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'case.json' -Value ([pscustomobject][ordered]@{
            schemaVersion=1;caseId=$PlanningCaseId;purpose='ReferenceSuppressionPlanning';status='Planning'
        }) | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'request/parent-evidence-binding.v1.json' -Value ([pscustomobject][ordered]@{
            schemaVersion=1;caseId=[string]$evidenceSeal.Manifest.caseId;caseDirectory=$evidenceDirectory
            manifestSha256=[string]$evidenceSeal.Manifest.manifestSha256
        }) | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'request/user-intent.v1.json' -Value ([pscustomobject][ordered]@{
            schemaVersion=1;authorityType='User';intent=$Intent;targets=@($TargetReferences);requestedAction='ProposeOnly'
        }) | Out-Null
        $specificationPath = Join-Path $transaction.CaseDirectory 'repair\reference-suppression-specification.v1.json'
        $planned = New-GridSkyrimReferenceSuppressionProposal -EvidenceCaseDirectory $evidenceDirectory -CaseStoreRoot $CaseStoreRoot `
            -CaseId $PlanningCaseId -ContextFingerprint $ContextFingerprint -TargetReferences $TargetReferences `
            -PatchPluginName $PatchPluginName -Intent $Intent -OutputPath $specificationPath -PassThru
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'repair/remediation-proposal.v1.json' -Value $planned.remediationProposal | Out-Null
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'case.json' -Value ([pscustomobject][ordered]@{
            schemaVersion=1;caseId=$PlanningCaseId;purpose='ReferenceSuppressionPlanning';status='AwaitingAuthorization'
        }) | Out-Null
        $now=[DateTimeOffset]::UtcNow.ToString('o')
        $run=[pscustomobject][ordered]@{
            schemaVersion=1;runId=('run-'+[guid]::NewGuid().ToString('N'));caseId=$PlanningCaseId;state='Completed'
            startedAt=$now;completedAt=$now;planFingerprint=[string]$planned.specification.specificationSha256
            resourcePolicyVersion='grid.reference-suppression-planning.v1';gates=@()
            sufficiency=[pscustomobject]@{schemaVersion=1;status='SufficientForRepairProposal'};checkpoints=@();primaryFailure=$null;secondaryFailures=@()
        }
        Seal-GridCaseStoreRun -Transaction $transaction -Run $run | Out-Null
        $sealed=Seal-GridDiagnosticCase -Transaction $transaction -SemanticBaselineFingerprint ([string]$planned.specification.specificationSha256) -Runs @($run)
        $result=[pscustomobject][ordered]@{
            schemaVersion=1;status='AwaitingAuthorization';caseId=$PlanningCaseId;caseDirectory=$sealed.CaseDirectory
            manifestSha256=[string]$sealed.Manifest.manifestSha256;specification=$planned.specification
            remediationProposal=$planned.remediationProposal;changedExternalState=$false
        }
    } catch {
        if ($transaction -and [string]$transaction.State -eq 'Open') { $transaction.State='Abandoned' }
        throw
    }
    if ($PassThru) { $result } else { $result | ConvertTo-Json -Depth 40 }
}
