$ErrorActionPreference = 'Stop'
$module = Join-Path (Split-Path -Parent $PSScriptRoot) 'health\Grid.Health.Skyrim.psm1'
Import-Module $module -Force
$hash = 'A' * 64
$collector = [pscustomobject]@{
    status='Completed'; contextFingerprint=$hash; issues=@(); rawSources=@([pscustomobject]@{sha256=$hash})
    assetGraph=@(); usage=[pscustomobject]@{}
    recordGraph=@([pscustomobject]@{
        recordId='synthetic-record'; status='RootCauseProven'; evidenceSha256=$hash; winningPlugin='SyntheticWinner.esp'; patchTargetPlugin='SyntheticPatch.esp'
        cause=[pscustomobject]@{code='WinningOverrideMismatch';detail='The synthetic winner differs from the required value.'}
        subjects=@([pscustomobject]@{subjectId='plugin:SyntheticWinner.esp';kind='Plugin';name='SyntheticWinner.esp';authoritativeOrder=1;roles=@('OverrideProvider')})
        requiredChanges=@([pscustomobject]@{fieldPath='Record.Flags';sourcePlugin='SyntheticSource.esm';beforeValue='A';afterValue='B'})
    })
}
$resolved = Resolve-GridSkyrimRootCause -CaseId 'synthetic-root-cause' -CollectorResult $collector
if ($resolved.DiagnosticResult.state -ne 'Diagnosed') { throw 'Expected a four-field Diagnosed result.' }
if ($resolved.PatchSpecification.status -ne 'Inert' -or $resolved.RemediationProposal.status -ne 'Unsupported') { throw 'Patch output must remain inert and unsupported.' }
$collector.recordGraph[0].evidenceSha256 = 'B' * 64
try { Resolve-GridSkyrimRootCause -CaseId 'synthetic-unbound' -CollectorResult $collector | Out-Null; throw 'Expected unbound evidence refusal.' }
catch { if ($_.Exception.Message -notmatch 'RootCauseEvidenceUnbound') { throw } }
'Root-cause diagnosis tests passed.'
