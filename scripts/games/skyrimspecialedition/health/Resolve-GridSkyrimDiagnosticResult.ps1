#requires -Version 5.1

<#
.SYNOPSIS
Resolves verified Skyrim evidence into Grid's deterministic diagnosis contract.
.DESCRIPTION
Only current, non-model evidence emitted by bounded collectors can identify an
affected plugin or role. A state-changing solution is exposed only when a
current evidence-bound remediation proposal exists. Missing proof produces the
same explicit unresolved fields and next investigation step every time.
#>
function Resolve-GridSkyrimDiagnosticResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][string]$ContextFingerprint,
        [Parameter(Mandatory)][string]$EvidenceFingerprint,
        [object[]]$Evidence = @(),
        $RemediationProposal,
        [object[]]$VerificationResults = @(),
        [string]$NextInvestigationStep = 'Continue investigation: collect the missing bounded record, reference, cell, or winning-override evidence.'
    )

    if (-not (Get-Command New-GridDiagnosticResult -ErrorAction SilentlyContinue)) {
        throw 'The shared Grid health module with New-GridDiagnosticResult must be imported before resolving a Skyrim diagnosis.'
    }
    $calculatedFingerprint = Get-GridEvidenceFingerprint -Evidence @($Evidence)
    if ($calculatedFingerprint -ne $EvidenceFingerprint) { throw 'EvidenceFingerprint does not match the supplied immutable evidence.' }

    $current = @($Evidence | Where-Object {
        $_.contextFingerprint -eq $ContextFingerprint -and
        $_.verificationStatus -in @('Collected', 'Verified', 'Contradicted') -and
        $_.sourceType -ne 'ModelReasoning'
    })
    $verified = @($current | Where-Object { $_.verificationStatus -in @('Collected', 'Verified') })
    $roleOrder = @('BaseOrMaster', 'WorldspaceOrCellEditor', 'PlacementProvider', 'OverrideProvider',
        'CompatibilityPatch', 'AssetProvider', 'AssetReplacer', 'GeneratedOutput', 'Dependency')
    $subjects = @{}
    foreach ($item in $verified) {
        if (-not $item.PSObject.Properties['native']) { continue }
        foreach ($subject in @($item.native.subjects)) {
            if (-not $subject -or [string]$subject.kind -ne 'Plugin' -or [string]::IsNullOrWhiteSpace([string]$subject.name)) { continue }
            $key = ([string]$subject.name).ToUpperInvariant()
            if (-not $subjects.ContainsKey($key)) {
                $subjects[$key] = [ordered]@{
                    name = [string]$subject.name
                    roles = New-Object Collections.Generic.List[string]
                    evidenceIds = New-Object Collections.Generic.List[string]
                }
            }
            if (-not $subjects[$key].evidenceIds.Contains([string]$item.evidenceId)) { $subjects[$key].evidenceIds.Add([string]$item.evidenceId) }
            foreach ($role in @($subject.roles)) {
                if ([string]$role -in $roleOrder -and -not $subjects[$key].roles.Contains([string]$role)) {
                    $subjects[$key].roles.Add([string]$role)
                }
            }
        }
    }

    [string[]]$subjectKeys = @($subjects.Keys)
    [Array]::Sort($subjectKeys, [StringComparer]::Ordinal)
    $orderedSubjects = @($subjectKeys | ForEach-Object { $subjects[$_] })
    $affectedItems = @($orderedSubjects | ForEach-Object {
        [pscustomobject][ordered]@{ subjectId = 'plugin:' + $_.name.ToUpperInvariant(); kind = 'Plugin'; name = $_.name }
    })
    $roleItems = @($orderedSubjects | ForEach-Object {
        $entry = $_
        [pscustomobject][ordered]@{
            subjectId = 'plugin:' + $entry.name.ToUpperInvariant()
            roles = @($roleOrder | Where-Object { $entry.roles.Contains($_) })
        }
    })
    [string[]]$subjectEvidenceIds = @($orderedSubjects | ForEach-Object { @($_.evidenceIds) } | Select-Object -Unique)
    [Array]::Sort($subjectEvidenceIds, [StringComparer]::Ordinal)

    $winnerRows = @($verified | Where-Object {
        $_.PSObject.Properties['native'] -and $_.native.state -eq 'Found' -and
        $_.native.operation -in @('TraceOverrides', 'InspectReferenceState', 'InspectContainingCell', 'ResolveWinningOverride') -and
        -not [string]::IsNullOrWhiteSpace([string]$_.native.winner)
    })
    if ($winnerRows.Count -gt 1) {
        [string[]]$winnerKeys = @($winnerRows | ForEach-Object {
            @([string]$_.native.plugin, [string]$_.native.formId, [string]$_.native.editorId,
                [string]$_.native.operation, [string]$_.native.winner) -join ([char]31)
        })
        [object[]]$winnerValues = @($winnerRows)
        Sort-GridDiagnosticParallelArrays -Keys $winnerKeys -Values $winnerValues
        $winnerRows = @($winnerValues)
    }

    $proposalIsCurrent = $false
    if ($RemediationProposal) {
        $proposalValidation = Test-GridRemediationProposal -Proposal $RemediationProposal -CurrentContextFingerprint $ContextFingerprint -CurrentEvidenceFingerprint $EvidenceFingerprint
        $proposalPluginTargets = @($RemediationProposal.targets | Where-Object { $_ -match '^pluginName=' } | ForEach-Object { $_.Substring('pluginName='.Length) })
        $allTargetsObserved = $proposalPluginTargets.Count -gt 0
        foreach ($target in $proposalPluginTargets) {
            if (@($affectedItems | Where-Object { $_.name -ieq $target }).Count -eq 0) { $allTargetsObserved = $false }
        }
        $proposalIsCurrent = $proposalValidation.IsValid -and -not $proposalValidation.IsStale -and
            @($RemediationProposal.supportingEvidenceIds).Count -gt 0 -and $allTargetsObserved
    }

    $unresolvedCollection = [pscustomobject][ordered]@{ status = 'Unresolved'; items = @(); evidenceIds = @() }
    $affectedField = if ($affectedItems.Count -gt 0) {
        [pscustomobject][ordered]@{ status = 'Resolved'; items = $affectedItems; evidenceIds = $subjectEvidenceIds }
    } else { $unresolvedCollection }
    $rolesField = if ($roleItems.Count -gt 0) {
        [pscustomobject][ordered]@{ status = 'Resolved'; items = $roleItems; evidenceIds = $subjectEvidenceIds }
    } else { [pscustomobject][ordered]@{ status = 'Unresolved'; items = @(); evidenceIds = @() } }

    if ($affectedItems.Count -eq 0 -or $winnerRows.Count -eq 0) {
        $state = 'NeedsEvidence'
        $finding = 'Insufficient evidence. Grid has not verified both the affected plugin relationship and the relevant winning record state.'
        $solution = $NextInvestigationStep
        $findingField = [pscustomobject][ordered]@{ status = 'Unresolved'; text = $finding; evidenceIds = @() }
        $solutionField = [pscustomobject][ordered]@{ status = 'Unresolved'; text = $solution; evidenceIds = @() }
    }
    elseif (-not $proposalIsCurrent) {
        $state = 'NeedsEvidence'
        $winnerSummary = @($winnerRows | ForEach-Object {
            $identity = if ($_.native.formId) { $_.native.formId } else { $_.native.editorId }
            "'$identity' resolves to '$($_.native.winner)'"
        } | Select-Object -Unique) -join '; '
        $finding = "Verified xEdit evidence established the current winning state: $winnerSummary. It does not yet prove a corrective action."
        $solution = $NextInvestigationStep
        $findingField = [pscustomobject][ordered]@{ status = 'Unresolved'; text = $finding; evidenceIds = @() }
        $solutionField = [pscustomobject][ordered]@{ status = 'Unresolved'; text = $solution; evidenceIds = @() }
    }
    else {
        $state = 'Diagnosed'
        $winnerSummary = @($winnerRows | ForEach-Object {
            $identity = if ($_.native.formId) { $_.native.formId } else { $_.native.editorId }
            "'$identity' resolves to '$($_.native.winner)'"
        } | Select-Object -Unique) -join '; '
        $finding = "Verified xEdit evidence established the implicated winning state: $winnerSummary."
        [string[]]$findingEvidenceIds = @($winnerRows | ForEach-Object evidenceId | Select-Object -Unique)
        [Array]::Sort($findingEvidenceIds, [StringComparer]::Ordinal)
        $findingField = [pscustomobject][ordered]@{ status = 'Resolved'; text = $finding; evidenceIds = $findingEvidenceIds }
        if ([string]$RemediationProposal.status -eq 'Unsupported') {
            $solution = 'An evidence-bound record patch specification is available for review, but Grid has no authorized writer for it and will not modify a plugin.'
            [string[]]$solutionEvidenceIds = @($RemediationProposal.supportingEvidenceIds)
            [Array]::Sort($solutionEvidenceIds, [StringComparer]::Ordinal)
            $solutionField = [pscustomobject][ordered]@{
                status = 'Unsupported'; text = $solution; evidenceIds = $solutionEvidenceIds; proposalId = [string]$RemediationProposal.proposalId
            }
        }
        else {
            [string[]]$orderedTargets = @($RemediationProposal.targets)
            [Array]::Sort($orderedTargets, [StringComparer]::Ordinal)
            $targetSummary = $orderedTargets -join ', '
            $verificationSummary = @($RemediationProposal.verificationSteps) -join '; '
            $solution = "Review and explicitly authorize the evidence-bound $($RemediationProposal.actionType) for $targetSummary. Verify by: $verificationSummary"
            [string[]]$solutionEvidenceIds = @($RemediationProposal.supportingEvidenceIds)
            [Array]::Sort($solutionEvidenceIds, [StringComparer]::Ordinal)
            $verifiedResult = @($VerificationResults | Where-Object {
                $_.proposalId -eq $RemediationProposal.proposalId -and $_.status -eq 'Verified'
            })
            if ($verifiedResult.Count -eq 1) {
                $solution = "Verified the evidence-bound $($RemediationProposal.actionType) for $targetSummary."
                $solutionField = [pscustomobject][ordered]@{
                    status = 'Verified'; text = $solution; evidenceIds = $solutionEvidenceIds
                    proposalId = [string]$RemediationProposal.proposalId; verificationId = [string]$verifiedResult[0].verificationId
                }
            }
            else {
                $solutionField = [pscustomobject][ordered]@{
                    status = 'Proposed'; text = $solution; evidenceIds = $solutionEvidenceIds; proposalId = [string]$RemediationProposal.proposalId
                }
            }
        }
    }

    $arguments = @{
        CaseId = $CaseId
        State = $state
        ContextFingerprint = $ContextFingerprint
        Evidence = @($Evidence)
        EvidenceFingerprint = $EvidenceFingerprint
        ResolverVersion = 'skyrim.xedit.v1'
        AffectedMods = $affectedField
        ModRoles = $rolesField
        Finding = $findingField
        Solution = $solutionField
        RemediationProposals = @($RemediationProposal | Where-Object { $null -ne $_ })
        VerificationResults = @($VerificationResults)
    }
    return New-GridDiagnosticResult @arguments
}
