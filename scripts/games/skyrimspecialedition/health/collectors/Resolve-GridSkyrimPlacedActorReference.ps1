#requires -Version 5.1

<#
.SYNOPSIS
Resolves exact placed-actor references from verified FindReferencesToBase evidence.
.DESCRIPTION
This is a deterministic presentation boundary over the existing read-only xEdit
collector. It never treats an NPC_ base FormID as an ACHR reference, never
selects among multiple placements, and never emits a placeatme instruction.
#>
function Resolve-GridSkyrimPlacedActorReference {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$Evidence,
        [Parameter(Mandatory)]$QueryPackage,
        [Parameter(Mandatory)][string]$ContextFingerprint,
        [Parameter(Mandatory)][string]$CaseDirectory
    )

    if ([string]$QueryPackage.ContextFingerprint -cne $ContextFingerprint) {
        throw 'PlacedActorReferenceContextMismatch: the query package is stale for the current context.'
    }

    $fullCase = [IO.Path]::GetFullPath($CaseDirectory)
    if (-not (Test-Path -LiteralPath $fullCase -PathType Container)) {
        throw "PlacedActorReferenceCaseMissing: $fullCase"
    }

    $results = New-Object Collections.Generic.List[object]
    foreach ($query in @($QueryPackage.Queries | Where-Object { [string]$_.operation -ceq 'FindReferencesToBase' })) {
        $queryEvidence = @($Evidence | Where-Object {
            [string]$_.verificationStatus -ceq 'Verified' -and
            [string]$_.contextFingerprint -ceq $ContextFingerprint -and
            [string]$_.native.queryId -ceq [string]$query.queryId -and
            [string]$_.native.operation -ceq 'FindReferencesToBase'
        })

        $allQueryRows = @($Evidence | Where-Object {
            [string]$_.native.queryId -ceq [string]$query.queryId -and
            [string]$_.native.operation -ceq 'FindReferencesToBase'
        })
        if ($allQueryRows.Count -ne $queryEvidence.Count) {
            throw "PlacedActorReferenceEvidenceUnverified: query '$($query.queryId)' contains stale or unverified evidence."
        }

        $candidateMap = @{}
        foreach ($item in @($queryEvidence | Where-Object {
            [string]$_.native.state -ceq 'Found' -and [string]$_.native.signature -ceq 'ACHR'
        })) {
            $referenceFormId = ([string]$item.native.formId).ToUpperInvariant()
            if ($referenceFormId -notmatch '^[0-9A-F]{8}$') {
                throw "PlacedActorReferenceInvalidFormId: query '$($query.queryId)' returned '$referenceFormId'."
            }
            if (-not $candidateMap.ContainsKey($referenceFormId)) {
                $candidateMap[$referenceFormId] = [pscustomobject][ordered]@{
                    referenceFormId = $referenceFormId
                    editorId = [string]$item.native.editorId
                    signature = 'ACHR'
                    origin = [string]$item.native.origin
                    winner = [string]$item.native.winner
                }
            }
        }
        $candidates = @($candidateMap.Keys | Sort-Object | ForEach-Object { $candidateMap[$_] })

        $status = if ($candidates.Count -eq 1) { 'Resolved' } elseif ($candidates.Count -gt 1) { 'Ambiguous' } else { 'NotFound' }
        $result = [ordered]@{
            queryId = [string]$query.queryId
            status = $status
            baseSelector = [pscustomobject][ordered]@{
                plugin = [string]$query.plugin
                formId = [string]$query.formId
                editorId = [string]$query.editorId
            }
            candidates = $candidates
            selectedReference = $null
            console = $null
        }
        if ($status -eq 'Resolved') {
            $selected = $candidates[0]
            $result.selectedReference = $selected
            $result.console = [pscustomobject][ordered]@{
                diagnosticCommands = @(
                    'prid ' + $selected.referenceFormId
                    'getdead'
                    'getdisabled'
                )
                recoveryPolicy = [pscustomobject][ordered]@{
                    aliveAndEnabled = @('moveto player', 'evp')
                    disabled = @('enable', 'moveto player', 'evp')
                    dead = @('resurrect 1', 'enable', 'moveto player', 'resetai', 'evp')
                }
                warnings = @(
                    'Use the resolved ACHR reference FormID with prid, not the NPC_ base FormID.'
                    'Do not use player.placeatme with the NPC_ base FormID; it creates a duplicate actor.'
                    'Do not use recycleactor as a first-line recovery; it destructively resets the actor.'
                )
            }
        }
        $results.Add([pscustomobject]$result)
    }

    $overallStatus = if ($results.Count -eq 0) { 'NotApplicable' }
        elseif (@($results | Where-Object status -eq 'Ambiguous').Count -gt 0) { 'Ambiguous' }
        elseif (@($results | Where-Object status -eq 'Resolved').Count -eq $results.Count) { 'Resolved' }
        else { 'Incomplete' }

    $document = [pscustomobject][ordered]@{
        schemaVersion = 1
        status = $overallStatus
        contextFingerprint = $ContextFingerprint
        results = $results.ToArray()
        mutationPerformed = $false
    }
    $path = Join-Path $fullCase 'placed-actor-reference.json'
    $document | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding UTF8
    [pscustomobject][ordered]@{ Path = $path; Result = $document }
}
