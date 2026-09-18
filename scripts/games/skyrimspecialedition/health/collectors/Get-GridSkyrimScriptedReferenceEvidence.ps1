#requires -Version 5.1

<#
.SYNOPSIS
Composes xEdit record evidence and exact MO2 PSC/PEX inspection into a bounded graph.
.DESCRIPTION
This is a read-only evidence composer. It does not infer runtime state from plugin
defaults. PEX instruction structure is observed statically; branch execution and
current save state remain explicit evidence gaps.
#>
function Get-GridSkyrimScriptedReferenceEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$XEditEvidence,
        [Parameter(Mandatory)][object[]]$AssetInspections,
        [Parameter(Mandatory)][string]$CaseDirectory,
        [Parameter(Mandatory)][string]$ContextFingerprint,
        [int]$MaximumNodes = 4096,
        [int]$MaximumEdges = 16384
    )
    if ($MaximumNodes -lt 1 -or $MaximumEdges -lt 1) { throw 'Graph limits must be positive.' }
    $nodes = @{}; $edges = New-Object Collections.Generic.List[object]
    $assessments = New-Object Collections.Generic.List[object]
    function AddNode([string]$id,[string]$kind,[string]$label,[string]$provider) {
        if ([string]::IsNullOrWhiteSpace($id)) { return }
        if (-not $nodes.ContainsKey($id)) {
            if ($nodes.Count -ge $MaximumNodes) { throw 'Scripted-reference node limit exceeded.' }
            $nodes[$id] = [ordered]@{ id=$id; kind=$kind; label=$label; provider=$provider }
        }
    }
    function AddEdge([string]$from,[string]$to,[string]$kind,[string]$evidenceId) {
        if (-not $from -or -not $to) { return }
        if ($edges.Count -ge $MaximumEdges) { throw 'Scripted-reference edge limit exceeded.' }
        $edges.Add([ordered]@{ from=$from; to=$to; kind=$kind; evidenceId=$evidenceId })
    }
    foreach ($item in @($XEditEvidence | Sort-Object id)) {
        $n = $item.native; if (-not $n -or [string]$n.state -ne 'Found') { continue }
        $record = 'record:' + [string]$n.formId
        AddNode $record 'PlacedRecord' ([string]$n.editorId) ([string]$n.winner)
        AddNode ('plugin:' + [string]$n.origin) 'Plugin' ([string]$n.origin) ([string]$n.origin)
        AddNode ('plugin:' + [string]$n.winner) 'Plugin' ([string]$n.winner) ([string]$n.winner)
        AddEdge $record ('plugin:' + [string]$n.winner) 'WinningOverride' ([string]$item.id)
        foreach ($pair in ([string]$n.detail -split ';')) {
            $separator=$pair.IndexOf('='); if($separator -lt 1){continue}
            $key=$pair.Substring(0,$separator); $value=$pair.Substring($separator+1)
            if ($key -in @('NAME','XTEL','XESP','Cell') -and $value) {
                $target = $key.ToLowerInvariant() + ':' + $value
                AddNode $target $key $value ([string]$n.winner)
                AddEdge $record $target $key ([string]$item.id)
            }
        }
    }
    foreach ($asset in @($AssetInspections | Sort-Object virtualPath)) {
        $assetId='asset:' + [string]$asset.virtualPath
        AddNode $assetId 'VirtualFile' ([string]$asset.virtualPath) ([string]$asset.provider.sourceName)
        if ($asset.papyrus) {
            foreach($script in @($asset.papyrus.scriptName | Where-Object { $_ })) {
                $scriptId='script:' + [string]$script; AddNode $scriptId 'PapyrusScript' $script ([string]$asset.provider.sourceName); AddEdge $assetId $scriptId 'Declares' ''
            }
            foreach($call in @($asset.papyrus.calls)) {
                $routine=if($call.PSObject.Properties['routine']){[string]$call.routine}else{''}
                $instruction=if($call.PSObject.Properties['instruction']){[string]$call.instruction}else{[string]$call.line}
                $callId='call:'+$routine+':'+[string]$call.receiver+'.'+[string]$call.method+'@'+$instruction
                AddNode $callId 'PapyrusCall' ([string]$call.method) ([string]$asset.provider.sourceName); AddEdge $assetId $callId 'ContainsCall' ''
                if($routine){$routineId='routine:'+$routine;AddNode $routineId 'PapyrusRoutine' $routine ([string]$asset.provider.sourceName);AddEdge $callId $routineId 'OccursIn' ''}
            }
            if($asset.papyrus.stateAnalysis){
                $analysis = $asset.papyrus.stateAnalysis
                $analysisStatus = [string]$analysis.status
                if ($analysisStatus -notin @('NoReferenceStateControlObserved','ReferenceStateControlObserved','LifecycleReapplicationObserved','SelectionReconciliationObserved')) {
                    throw "Scripted-reference assessment contains unsupported Papyrus state status '$analysisStatus'."
                }
                $assessments.Add([ordered]@{
                    virtualPath = [string]$asset.virtualPath
                    provider = [string]$asset.provider.sourceName
                    scriptName = [string]$asset.papyrus.scriptName
                    status = $analysisStatus
                    controlledReferences = @($analysis.controlledReferences | ForEach-Object { [string]$_ } | Sort-Object -Unique)
                    lifecycleRoutines = @($analysis.lifecycleRoutines | ForEach-Object { [string]$_ } | Sort-Object -Unique)
                    selectionVariables = @($analysis.selectionVariables | ForEach-Object { [string]$_ } | Sort-Object -Unique)
                    finding = [string]$analysis.finding
                    solution = [string]$analysis.solution
                    runtimeEvidenceRequired = $true
                })
                foreach($action in @($asset.papyrus.stateAnalysis.referenceStateActions)){
                    $actionId='reference-state-action:'+[string]$action.routine+':'+[string]$action.instruction
                    $targetId='papyrus-reference:'+[string]$action.target
                    AddNode $actionId 'ReferenceStateAction' ([string]$action.action) ([string]$asset.provider.sourceName)
                    AddNode $targetId 'PapyrusReference' ([string]$action.target) ([string]$asset.provider.sourceName)
                    AddEdge $assetId $actionId 'ContainsReferenceStateAction' '';AddEdge $actionId $targetId 'Targets' ''
                }
                foreach($access in @($asset.papyrus.stateAnalysis.persistenceAccesses)){
                    $accessId='persistence-access:'+[string]$access.routine+':'+[string]$access.instruction
                    AddNode $accessId 'PapyrusPersistenceAccess' ([string]$access.method) ([string]$asset.provider.sourceName)
                    AddEdge $assetId $accessId 'ContainsPersistenceAccess' ''
                }
                foreach($selector in @($asset.papyrus.stateAnalysis.selectionVariables)){
                    $selectorId='papyrus-selector:'+[string]$selector
                    AddNode $selectorId 'PapyrusSelectionVariable' ([string]$selector) ([string]$asset.provider.sourceName)
                    AddEdge $assetId $selectorId 'DeclaresSelectionVariable' ''
                }
            }
            foreach($assignment in @($asset.papyrus.assignments)){
                $assignmentId='papyrus-assignment:'+[string]$assignment.routine+':'+[string]$assignment.instruction+':'+[string]$assignment.target
                AddNode $assignmentId 'PapyrusAssignment' (([string]$assignment.target)+' = '+([string]$assignment.expression)) ([string]$asset.provider.sourceName)
                AddEdge $assetId $assignmentId 'ContainsAssignment' ''
            }
        }
    }
    [string[]]$nodeKeys=@($nodes.Keys); [Array]::Sort($nodeKeys,[StringComparer]::Ordinal)
    $result=[ordered]@{
        schemaVersion=1; contextFingerprint=$ContextFingerprint; status='Observed'
        limitations=@('Plugin flags are defaults, not proof of current save/runtime enabled state.','PEX instructions are static evidence; branch execution and persisted save values require current runtime/save evidence.')
        assessments=@($assessments|Sort-Object virtualPath,provider,scriptName)
        nodes=@($nodeKeys|ForEach-Object{[pscustomobject]$nodes[$_]})
        edges=@($edges|Sort-Object from,to,kind,evidenceId)
    }
    $path=Join-Path ([IO.Path]::GetFullPath($CaseDirectory)) 'scripted-reference-evidence.v1.json'
    $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding UTF8
    [pscustomobject]@{ Path=$path; Evidence=[pscustomobject]$result }
}
