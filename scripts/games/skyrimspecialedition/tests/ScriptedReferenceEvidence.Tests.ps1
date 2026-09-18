$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'health\collectors\Get-GridSkyrimScriptedReferenceEvidence.ps1')
$case=Join-Path ([IO.Path]::GetTempPath()) ('grid-scripted-reference-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $case | Out-Null
try {
  $x=[pscustomobject]@{ id='e1'; native=[pscustomobject]@{ state='Found'; formId='01000001'; editorId='FixtureDoor'; origin='Fixture.esp'; winner='FixtureOverride.esp'; detail='NAME=Door:01000002;XTEL=Reference:01000003;XESP=' } }
  $a=[pscustomobject]@{ virtualPath='scripts/fixture.psc'; provider=[pscustomobject]@{ sourceName='FixtureMod' }; papyrus=[pscustomobject]@{
      scriptName='FixtureScript'
      calls=@([pscustomobject]@{receiver='HatchAlternate';method='Enable';routine='OnGameReload';instruction=3;line=7})
      stateAnalysis=[pscustomobject]@{
        status='SelectionReconciliationObserved'
        controlledReferences=@('HatchOriginal','HatchAlternate')
        lifecycleRoutines=@('OnGameReload')
        referenceStateActions=@([pscustomobject]@{target='HatchAlternate';action='Enable';routine='OnGameReload';instruction=3})
        persistenceAccesses=@([pscustomobject]@{receiver='StorageUtil';method='GetIntValue';routine='OnGameReload';instruction=5})
        selectionVariables=@('SelectedInterior')
        finding='A persisted selection is reapplied during a game lifecycle callback.'
        solution='Verify the persisted selector against current save and runtime evidence.'
      }
      assignments=@([pscustomobject]@{target='SelectedInterior';expression='index';routine='OnOptionMenuAccept';instruction=7})
    } }
  $r=Get-GridSkyrimScriptedReferenceEvidence -XEditEvidence @($x) -AssetInspections @($a) -CaseDirectory $case -ContextFingerprint 'fixture'
  if(-not(Test-Path -LiteralPath $r.Path)){throw 'Evidence file was not written.'}
  if(@($r.Evidence.nodes|Where-Object kind -eq 'XTEL').Count -ne 1){throw 'XTEL graph node missing.'}
  if(@($r.Evidence.nodes|Where-Object kind -eq 'PapyrusCall').Count -ne 1){throw 'Papyrus call graph node missing.'}
  if(@($r.Evidence.nodes|Where-Object kind -eq 'PapyrusRoutine').Count -ne 1){throw 'Papyrus routine graph node missing.'}
  if(@($r.Evidence.nodes|Where-Object kind -eq 'ReferenceStateAction').Count -ne 1){throw 'Reference-state action graph node missing.'}
  if(@($r.Evidence.nodes|Where-Object kind -eq 'PapyrusPersistenceAccess').Count -ne 1){throw 'Papyrus persistence graph node missing.'}
  if(@($r.Evidence.nodes|Where-Object kind -eq 'PapyrusSelectionVariable').Count -ne 1){throw 'Papyrus selector graph node missing.'}
  if(@($r.Evidence.nodes|Where-Object kind -eq 'PapyrusAssignment').Count -ne 1){throw 'Papyrus assignment graph node missing.'}
  if(@($r.Evidence.assessments).Count -ne 1){throw 'Papyrus state assessment missing.'}
  if([string]$r.Evidence.assessments[0].status -ne 'SelectionReconciliationObserved'){throw 'Papyrus state assessment status drifted.'}
  if(-not [bool]$r.Evidence.assessments[0].runtimeEvidenceRequired){throw 'Papyrus assessment must retain its runtime evidence gap.'}
  if(@($r.Evidence.limitations).Count -lt 2){throw 'Evidence limitations missing.'}
  'Scripted-reference evidence checks passed.'
} finally { Remove-Item -LiteralPath $case -Recurse -Force -ErrorAction SilentlyContinue }
