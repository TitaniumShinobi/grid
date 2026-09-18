#requires -Version 5.1
Set-StrictMode -Version Latest

$script:GridProblemLedgerStates = @(
    'Claimed', 'IntakeBound', 'NeedsContext', 'NeedsEvidence', 'Diagnosed',
    'SolutionProposed', 'Authorized', 'Applied', 'VerificationRequired',
    'Resolved', 'AccountedFor'
)

$script:GridProblemLedgerTransitions = @{
    Claimed              = @('IntakeBound','NeedsContext','NeedsEvidence','AccountedFor')
    IntakeBound           = @('NeedsContext','NeedsEvidence','Diagnosed','AccountedFor')
    NeedsContext          = @('IntakeBound','NeedsEvidence','Diagnosed','AccountedFor')
    NeedsEvidence         = @('Diagnosed','AccountedFor')
    Diagnosed             = @('SolutionProposed','NeedsEvidence','AccountedFor')
    SolutionProposed      = @('Authorized','NeedsEvidence','AccountedFor')
    Authorized            = @('Applied','NeedsEvidence')
    Applied               = @('VerificationRequired','Resolved','NeedsEvidence')
    VerificationRequired  = @('Resolved','NeedsEvidence')
    Resolved              = @('NeedsEvidence')
    AccountedFor          = @('NeedsEvidence')
}

$script:GridProblemLedgerAuthorities = @{
    IntakeBound          = @('RequestPlanner')
    NeedsContext         = @('RequestPlanner','Collector')
    NeedsEvidence        = @('Collector','Resolver','Verifier','Policy')
    Diagnosed            = @('Resolver')
    SolutionProposed     = @('Proposal')
    Authorized           = @('Authorization')
    Applied              = @('Executor')
    VerificationRequired = @('Executor','Verifier')
    Resolved             = @('Verifier')
    AccountedFor         = @('Policy')
}

function Assert-GridProblemLedgerText {
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$Name,
        [int]$MaximumLength = 4096
    )
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.Length -gt $MaximumLength -or
        $Value.IndexOfAny(@([char]0, [char]11, [char]12)) -ge 0) {
        throw "$Name must be nonempty, bounded text without unsupported control characters."
    }
}

function Assert-GridProblemLedgerFingerprint {
    param([Parameter(Mandatory)][string]$Value, [Parameter(Mandatory)][string]$Name)
    if ($Value -notmatch '^[A-Fa-f0-9]{64}$') { throw "$Name must be a SHA-256 fingerprint." }
}

function Get-GridProblemLedgerByteHash {
    param([Parameter(Mandatory)][string]$Value)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Get-GridProblemLedgerHash {
    param([Parameter(Mandatory)]$Ledger)
    $entries = @($Ledger.entries | Sort-Object issueId | ForEach-Object {
        [ordered]@{
            issueId = [string]$_.issueId
            claim = [string]$_.claim
            desiredOutcome = [string]$_.desiredOutcome
            state = [string]$_.state
            contextFingerprint = [string]$_.contextFingerprint
            classId = if ($_.classId) { [string]$_.classId } else { $null }
            capabilityIds = @($_.capabilityIds | ForEach-Object { [string]$_ } | Sort-Object -Unique)
            evidenceIds = @($_.evidenceIds | ForEach-Object { [string]$_ } | Sort-Object -Unique)
            proposalId = if ($_.proposalId) { [string]$_.proposalId } else { $null }
            authorizationId = if ($_.authorizationId) { [string]$_.authorizationId } else { $null }
            executionReceiptId = if ($_.executionReceiptId) { [string]$_.executionReceiptId } else { $null }
            verificationId = if ($_.verificationId) { [string]$_.verificationId } else { $null }
            blockers = @($_.blockers | ForEach-Object { [string]$_ } | Sort-Object -Unique)
            history = @($_.history | ForEach-Object {
                [ordered]@{
                    eventId = [string]$_.eventId
                    fromState = if ($_.fromState) { [string]$_.fromState } else { $null }
                    toState = [string]$_.toState
                    authorityType = [string]$_.authorityType
                    authorityId = [string]$_.authorityId
                    contextFingerprint = [string]$_.contextFingerprint
                    evidenceIds = @($_.evidenceIds | ForEach-Object { [string]$_ } | Sort-Object -Unique)
                }
            })
        }
    })
    $semantic = [ordered]@{
        schemaVersion = [int]$Ledger.schemaVersion
        ledgerId = [string]$Ledger.ledgerId
        gameId = [string]$Ledger.gameId
        installationId = [string]$Ledger.installationId
        profileId = [string]$Ledger.profileId
        currentContextFingerprint = [string]$Ledger.currentContextFingerprint
        entries = $entries
    }
    $json = $semantic | ConvertTo-Json -Depth 30 -Compress
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($json)))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Update-GridProblemLedgerFingerprint {
    param([Parameter(Mandatory)]$Ledger)
    $Ledger.ledgerFingerprint = Get-GridProblemLedgerHash -Ledger $Ledger
    $Ledger.updatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    $Ledger
}

function Assert-GridProblemLedgerIntegrity {
    param([Parameter(Mandatory)]$Ledger)
    if ($null -eq $Ledger.PSObject.Properties['ledgerFingerprint'] -or
        [string]$Ledger.ledgerFingerprint -cne (Get-GridProblemLedgerHash -Ledger $Ledger)) {
        throw 'ProblemLedgerIntegrityRefused: the ledger fingerprint does not match its semantic content.'
    }
}

function New-GridProblemLedger {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$GameId,
        [Parameter(Mandatory)][string]$InstallationId,
        [Parameter(Mandatory)][string]$ProfileId,
        [Parameter(Mandatory)][string]$ContextFingerprint,
        [string]$LedgerId = ('problem-ledger-' + [Guid]::NewGuid().ToString('N'))
    )
    foreach ($pair in @(@($GameId,'GameId',256), @($InstallationId,'InstallationId',512),
        @($ProfileId,'ProfileId',512), @($LedgerId,'LedgerId',512))) {
        Assert-GridProblemLedgerText -Value ([string]$pair[0]) -Name ([string]$pair[1]) -MaximumLength ([int]$pair[2])
    }
    Assert-GridProblemLedgerFingerprint -Value $ContextFingerprint -Name 'ContextFingerprint'
    $now = [DateTimeOffset]::UtcNow.ToString('o')
    $ledger = [pscustomobject][ordered]@{
        schemaVersion = 1
        ledgerId = $LedgerId
        gameId = $GameId
        installationId = $InstallationId
        profileId = $ProfileId
        currentContextFingerprint = $ContextFingerprint.ToUpperInvariant()
        createdAt = $now
        updatedAt = $now
        entries = @()
        ledgerFingerprint = $null
    }
    Update-GridProblemLedgerFingerprint -Ledger $ledger
}

function Add-GridProblemClaim {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Ledger,
        [Parameter(Mandatory)][string]$Claim,
        [Parameter(Mandatory)][string]$DesiredOutcome,
        [string]$IssueId = ('problem-' + [Guid]::NewGuid().ToString('N')),
        [string]$AuthorityId = 'user'
    )
    Assert-GridProblemLedgerIntegrity -Ledger $Ledger
    Assert-GridProblemLedgerText -Value $IssueId -Name 'IssueId' -MaximumLength 512
    Assert-GridProblemLedgerText -Value $Claim -Name 'Claim'
    Assert-GridProblemLedgerText -Value $DesiredOutcome -Name 'DesiredOutcome'
    Assert-GridProblemLedgerText -Value $AuthorityId -Name 'AuthorityId' -MaximumLength 512
    if (@($Ledger.entries | Where-Object { [string]$_.issueId -ceq $IssueId }).Count -ne 0) {
        throw "ProblemLedgerIssueDuplicate: '$IssueId' already exists."
    }
    $now = [DateTimeOffset]::UtcNow.ToString('o')
    $entry = [pscustomobject][ordered]@{
        issueId = $IssueId
        claim = $Claim
        desiredOutcome = $DesiredOutcome
        state = 'Claimed'
        contextFingerprint = [string]$Ledger.currentContextFingerprint
        classId = $null
        capabilityIds = @()
        evidenceIds = @()
        proposalId = $null
        authorizationId = $null
        executionReceiptId = $null
        verificationId = $null
        blockers = @()
        createdAt = $now
        updatedAt = $now
        history = @([pscustomobject][ordered]@{
            eventId = 'problem-event-' + [Guid]::NewGuid().ToString('N')
            fromState = $null
            toState = 'Claimed'
            authorityType = 'User'
            authorityId = $AuthorityId
            contextFingerprint = [string]$Ledger.currentContextFingerprint
            evidenceIds = @()
            recordedAt = $now
        })
    }
    $Ledger.entries = @($Ledger.entries) + @($entry)
    Update-GridProblemLedgerFingerprint -Ledger $Ledger
}

function Set-GridProblemState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Ledger,
        [Parameter(Mandatory)][string]$IssueId,
        [Parameter(Mandatory)][ValidateSet('IntakeBound','NeedsContext','NeedsEvidence','Diagnosed','SolutionProposed','Authorized','Applied','VerificationRequired','Resolved','AccountedFor')][string]$State,
        [Parameter(Mandatory)][ValidateSet('RequestPlanner','Collector','Resolver','Proposal','Authorization','Executor','Verifier','Policy')][string]$AuthorityType,
        [Parameter(Mandatory)][string]$AuthorityId,
        [Parameter(Mandatory)][string]$ContextFingerprint,
        [string[]]$EvidenceIds = @(),
        [string]$ClassId,
        [string[]]$CapabilityIds = @(),
        [string]$ProposalId,
        [string]$AuthorizationId,
        [string]$ExecutionReceiptId,
        [string]$VerificationId,
        [string[]]$Blockers = @()
    )
    Assert-GridProblemLedgerIntegrity -Ledger $Ledger
    Assert-GridProblemLedgerText -Value $AuthorityId -Name 'AuthorityId' -MaximumLength 512
    Assert-GridProblemLedgerFingerprint -Value $ContextFingerprint -Name 'ContextFingerprint'
    $entries = @($Ledger.entries | Where-Object { [string]$_.issueId -ceq $IssueId })
    if ($entries.Count -ne 1) { throw "ProblemLedgerIssueUnavailable: expected one exact issue '$IssueId'." }
    $entry = $entries[0]
    $from = [string]$entry.state
    if ($State -notin @($script:GridProblemLedgerTransitions[$from])) {
        throw "ProblemLedgerTransitionRefused: '$from' cannot transition to '$State'."
    }
    if ($AuthorityType -notin @($script:GridProblemLedgerAuthorities[$State])) {
        throw "ProblemLedgerAuthorityRefused: '$AuthorityType' cannot establish '$State'."
    }
    $normalizedEvidence = @($EvidenceIds | ForEach-Object { [string]$_ } | Where-Object { $_ } | Sort-Object -Unique)
    if ($State -in @('Diagnosed','SolutionProposed','Authorized','Applied','VerificationRequired','Resolved','AccountedFor') -and $normalizedEvidence.Count -eq 0) {
        throw "ProblemLedgerEvidenceRequired: '$State' requires at least one evidence identity."
    }
    if ($State -eq 'SolutionProposed' -and [string]::IsNullOrWhiteSpace($ProposalId)) { throw 'ProblemLedgerProposalRequired: SolutionProposed requires ProposalId.' }
    if ($State -eq 'Authorized' -and [string]::IsNullOrWhiteSpace($AuthorizationId)) { throw 'ProblemLedgerAuthorizationRequired: Authorized requires AuthorizationId.' }
    if ($State -eq 'Applied' -and [string]::IsNullOrWhiteSpace($ExecutionReceiptId)) { throw 'ProblemLedgerExecutionReceiptRequired: Applied requires ExecutionReceiptId.' }
    if ($State -eq 'Resolved' -and [string]::IsNullOrWhiteSpace($VerificationId)) { throw 'ProblemLedgerVerificationRequired: Resolved requires VerificationId.' }
    if ($State -eq 'AccountedFor' -and @($Blockers | Where-Object { $_ }).Count -eq 0) { throw 'ProblemLedgerAccountingReasonRequired: AccountedFor requires a bounded reason.' }

    $now = [DateTimeOffset]::UtcNow.ToString('o')
    $entry.state = $State
    $entry.contextFingerprint = $ContextFingerprint.ToUpperInvariant()
    if ($ClassId) { $entry.classId = $ClassId }
    $entry.capabilityIds = @(@($entry.capabilityIds) + @($CapabilityIds) | ForEach-Object { [string]$_ } | Where-Object { $_ } | Sort-Object -Unique)
    $entry.evidenceIds = @(@($entry.evidenceIds) + $normalizedEvidence | Sort-Object -Unique)
    if ($ProposalId) { $entry.proposalId = $ProposalId }
    if ($AuthorizationId) { $entry.authorizationId = $AuthorizationId }
    if ($ExecutionReceiptId) { $entry.executionReceiptId = $ExecutionReceiptId }
    if ($VerificationId) { $entry.verificationId = $VerificationId }
    $entry.blockers = @($Blockers | ForEach-Object { [string]$_ } | Where-Object { $_ } | Sort-Object -Unique)
    $entry.updatedAt = $now
    $entry.history = @($entry.history) + @([pscustomobject][ordered]@{
        eventId = 'problem-event-' + [Guid]::NewGuid().ToString('N')
        fromState = $from
        toState = $State
        authorityType = $AuthorityType
        authorityId = $AuthorityId
        contextFingerprint = $ContextFingerprint.ToUpperInvariant()
        evidenceIds = $normalizedEvidence
        recordedAt = $now
    })
    Update-GridProblemLedgerFingerprint -Ledger $Ledger
}

function Update-GridProblemLedgerContext {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Ledger,
        [Parameter(Mandatory)][string]$ContextFingerprint,
        [Parameter(Mandatory)][string]$DriftEvidenceId,
        [string]$AuthorityId = 'grid.context-drift.v1'
    )
    Assert-GridProblemLedgerIntegrity -Ledger $Ledger
    Assert-GridProblemLedgerFingerprint -Value $ContextFingerprint -Name 'ContextFingerprint'
    $next = $ContextFingerprint.ToUpperInvariant()
    if ([string]$Ledger.currentContextFingerprint -ceq $next) { return $Ledger }
    foreach ($entry in @($Ledger.entries)) {
        if ([string]$entry.contextFingerprint -ceq $next -or [string]$entry.state -eq 'Claimed') { continue }
        if ([string]$entry.state -eq 'NeedsEvidence') {
            $now = [DateTimeOffset]::UtcNow.ToString('o')
            $entry.contextFingerprint = $next
            $entry.evidenceIds = @(@($entry.evidenceIds) + @($DriftEvidenceId) | ForEach-Object { [string]$_ } | Where-Object { $_ } | Sort-Object -Unique)
            $entry.blockers = @(@($entry.blockers) + @('The selected game/profile context changed; prior evidence must be revalidated.') | Sort-Object -Unique)
            $entry.updatedAt = $now
            $entry.history = @($entry.history) + @([pscustomobject][ordered]@{
                eventId = 'problem-event-' + [Guid]::NewGuid().ToString('N')
                fromState = 'NeedsEvidence'
                toState = 'NeedsEvidence'
                authorityType = 'Verifier'
                authorityId = $AuthorityId
                contextFingerprint = $next
                evidenceIds = @($DriftEvidenceId)
                recordedAt = $now
            })
            Update-GridProblemLedgerFingerprint -Ledger $Ledger | Out-Null
            continue
        }
        Set-GridProblemState -Ledger $Ledger -IssueId ([string]$entry.issueId) -State NeedsEvidence `
            -AuthorityType Verifier -AuthorityId $AuthorityId -ContextFingerprint $next `
            -EvidenceIds @($DriftEvidenceId) -Blockers @('The selected game/profile context changed; prior terminal evidence must be revalidated.') | Out-Null
    }
    $Ledger.currentContextFingerprint = $next
    Update-GridProblemLedgerFingerprint -Ledger $Ledger
}

function Test-GridProblemLedger {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Ledger)
    $errors = New-Object Collections.Generic.List[string]
    foreach ($name in @('schemaVersion','ledgerId','gameId','installationId','profileId','currentContextFingerprint','createdAt','updatedAt','entries','ledgerFingerprint')) {
        if ($null -eq $Ledger.PSObject.Properties[$name]) { $errors.Add("Missing ledger field: $name") }
    }
    if ($errors.Count -eq 0) {
        if ([int]$Ledger.schemaVersion -ne 1) { $errors.Add('Unsupported problem-ledger schemaVersion.') }
        if ([string]$Ledger.currentContextFingerprint -notmatch '^[A-F0-9]{64}$') { $errors.Add('Invalid currentContextFingerprint.') }
        $ids = @($Ledger.entries | ForEach-Object { [string]$_.issueId })
        if (@($ids | Sort-Object -Unique).Count -ne $ids.Count) { $errors.Add('Problem issue identities must be unique.') }
        foreach ($entry in @($Ledger.entries)) {
            if ([string]$entry.state -notin $script:GridProblemLedgerStates) { $errors.Add("Invalid state for '$($entry.issueId)'.") }
            if (@($entry.history).Count -eq 0) { $errors.Add("Missing history for '$($entry.issueId)'.") }
            else {
                $prior = $null
                foreach ($event in @($entry.history)) {
                    if ([string]::IsNullOrWhiteSpace([string]$event.eventId)) { $errors.Add("Missing history event identity for '$($entry.issueId)'.") }
                    if ([string]$event.contextFingerprint -notmatch '^[A-F0-9]{64}$') { $errors.Add("Invalid history context fingerprint for '$($entry.issueId)'.") }
                    if ($null -eq $prior) {
                        if ($null -ne $event.fromState -or [string]$event.toState -ne 'Claimed' -or [string]$event.authorityType -ne 'User') {
                            $errors.Add("Invalid claim origin event for '$($entry.issueId)'.")
                        }
                    }
                    else {
                        if ([string]$event.fromState -ne [string]$prior.toState) { $errors.Add("Broken history chain for '$($entry.issueId)'.") }
                        $isEvidenceRefresh = ([string]$event.fromState -eq 'NeedsEvidence' -and [string]$event.toState -eq 'NeedsEvidence' -and [string]$event.authorityType -eq 'Verifier')
                        if (-not $isEvidenceRefresh -and [string]$event.toState -notin @($script:GridProblemLedgerTransitions[[string]$event.fromState])) {
                            $errors.Add("Invalid history transition for '$($entry.issueId)'.")
                        }
                        if (-not $isEvidenceRefresh -and [string]$event.authorityType -notin @($script:GridProblemLedgerAuthorities[[string]$event.toState])) {
                            $errors.Add("Invalid history authority for '$($entry.issueId)'.")
                        }
                    }
                    $prior = $event
                }
                if ([string]$prior.toState -ne [string]$entry.state) { $errors.Add("History terminal state mismatch for '$($entry.issueId)'.") }
            }
        }
        if ([string]$Ledger.ledgerFingerprint -cne (Get-GridProblemLedgerHash -Ledger $Ledger)) { $errors.Add('Problem ledger fingerprint mismatch.') }
    }
    [pscustomobject]@{ IsValid = ($errors.Count -eq 0); Errors = @($errors) }
}

function Write-GridProblemLedgerArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][psobject]$Transaction,
        [Parameter(Mandatory)]$Ledger,
        [string]$RelativePath = 'diagnosis/problem-ledger.v1.json'
    )
    $validation = Test-GridProblemLedger -Ledger $Ledger
    if (-not $validation.IsValid) {
        throw "ProblemLedgerArtifactRefused: $($validation.Errors -join ' ')"
    }
    Write-GridCaseStoreArtifact -Transaction $Transaction -RelativePath $RelativePath -Value $Ledger
}

function New-GridRequestProblemLedger {
    <#
    .SYNOPSIS
    Projects one structured request and its deterministic result into a durable problem ledger.
    .DESCRIPTION
    Plain text is preserved as a claim and is never parsed into a target. A parent
    ledger may be supplied for a successor case; its sealed history is cloned and
    context drift reopens dependent entries before the new result is projected.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Envelope,
        [Parameter(Mandatory)]$RequestPlan,
        $InvestigationIntake,
        $Result,
        $ParentLedger,
        [string]$ContextFingerprint,
        [string[]]$AdditionalEvidenceIds = @()
    )
    $gameId = [string]$Envelope.context.gameId
    $installationId = [string]$Envelope.context.installationId
    $profileId = [string]$Envelope.context.profileId
    foreach ($field in @(@($gameId,'gameId'),@($installationId,'installationId'),@($profileId,'profileId'))) {
        if ([string]::IsNullOrWhiteSpace([string]$field[0])) { throw "ProblemLedgerRequestInvalid: '$($field[1])' is required." }
    }
    if ([string]::IsNullOrWhiteSpace($ContextFingerprint)) {
        $contextJson = [pscustomobject][ordered]@{ gameId=$gameId; installationId=$installationId; profileId=$profileId } | ConvertTo-Json -Compress
        $ContextFingerprint = Get-GridProblemLedgerByteHash -Value $contextJson
    }
    if ($ContextFingerprint -match '^sha256:([A-Fa-f0-9]{64})$') { $ContextFingerprint = $matches[1] }
    Assert-GridProblemLedgerFingerprint -Value $ContextFingerprint -Name 'ContextFingerprint'
    $context = $ContextFingerprint.ToUpperInvariant()

    $ledger = if ($null -ne $ParentLedger) {
        $clone = $ParentLedger | ConvertTo-Json -Depth 100 | ConvertFrom-Json
        $validation = Test-GridProblemLedger -Ledger $clone
        if (-not $validation.IsValid) { throw "ProblemLedgerParentInvalid: $($validation.Errors -join ' ')" }
        if ([string]$clone.currentContextFingerprint -cne $context) {
            $driftId = 'context-drift.' + $context.Substring(0,24).ToLowerInvariant()
            Update-GridProblemLedgerContext -Ledger $clone -ContextFingerprint $context -DriftEvidenceId $driftId | Out-Null
        }
        $clone
    }
    else {
        New-GridProblemLedger -GameId $gameId -InstallationId $installationId -ProfileId $profileId -ContextFingerprint $context
    }

    $claim = if ($null -ne $InvestigationIntake -and $InvestigationIntake.PSObject.Properties['problem'] -and
        -not [string]::IsNullOrWhiteSpace([string]$InvestigationIntake.problem)) { [string]$InvestigationIntake.problem }
        else { [string]$Envelope.claims.text }
    if ([string]::IsNullOrWhiteSpace($claim)) { return $ledger }
    $desired = if ($null -ne $InvestigationIntake -and $InvestigationIntake.PSObject.Properties['desiredOutcome'] -and
        -not [string]::IsNullOrWhiteSpace([string]$InvestigationIntake.desiredOutcome)) { [string]$InvestigationIntake.desiredOutcome }
        elseif ($null -ne $InvestigationIntake -and $InvestigationIntake.PSObject.Properties['expectedBehavior'] -and
        -not [string]::IsNullOrWhiteSpace([string]$InvestigationIntake.expectedBehavior)) { [string]$InvestigationIntake.expectedBehavior }
        else { 'Produce an evidence-backed result for the preserved claim without unauthorized mutation.' }
    $issueHash = Get-GridProblemLedgerByteHash -Value ((@($gameId,$claim,$desired) | ForEach-Object { [string]$_ }) -join "`n")
    $issueId = 'problem.' + $issueHash.Substring(0,24).ToLowerInvariant()
    $entries = @($ledger.entries | Where-Object { [string]$_.issueId -ceq $issueId })
    if ($entries.Count -eq 0) {
        Add-GridProblemClaim -Ledger $ledger -IssueId $issueId -Claim $claim -DesiredOutcome $desired -AuthorityId 'structured-intake' | Out-Null
        $capabilityIds = @($RequestPlan.capabilityBindings | ForEach-Object { [string]$_.capabilityId } | Where-Object { $_ } | Sort-Object -Unique)
        Set-GridProblemState -Ledger $ledger -IssueId $issueId -State IntakeBound -AuthorityType RequestPlanner `
            -AuthorityId ([string]$Envelope.requestId) -ContextFingerprint $context -ClassId ([string]$Envelope.class.classId) -CapabilityIds $capabilityIds | Out-Null
        $entries = @($ledger.entries | Where-Object { [string]$_.issueId -ceq $issueId })
    }
    if ($entries.Count -ne 1) { throw "ProblemLedgerIssueProjectionAmbiguous: '$issueId'." }

    $entry = $entries[0]
    $resultEvidence = @($AdditionalEvidenceIds)
    if ($null -ne $Result -and $Result.PSObject.Properties['evidenceIds']) { $resultEvidence += @($Result.evidenceIds) }
    $resultEvidence = @($resultEvidence | ForEach-Object { [string]$_ } | Where-Object { $_ } | Sort-Object -Unique)
    $isDiagnosis = $null -ne $Result -and [string]$Result.resultKind -eq 'Diagnostic' -and
        [string]$Result.terminalState -eq 'Diagnosed' -and $resultEvidence.Count -gt 0
    if ($isDiagnosis -and [string]$entry.state -in @('IntakeBound','NeedsContext','NeedsEvidence')) {
        Set-GridProblemState -Ledger $ledger -IssueId $issueId -State Diagnosed -AuthorityType Resolver `
            -AuthorityId ('request-result.' + [string]$Envelope.requestId) -ContextFingerprint $context -EvidenceIds $resultEvidence | Out-Null
    }
    elseif (-not $isDiagnosis -and [string]$entry.state -in @('IntakeBound','NeedsContext')) {
        Set-GridProblemState -Ledger $ledger -IssueId $issueId -State NeedsEvidence -AuthorityType Collector `
            -AuthorityId ('request-result.' + [string]$Envelope.requestId) -ContextFingerprint $context -EvidenceIds $resultEvidence | Out-Null
    }
    $ledger
}

function New-GridProblemLedgerCase {
    <#
    .SYNOPSIS
    Seals user-supplied problem claims as a case without claiming diagnosis.
    .DESCRIPTION
    This is an intake operation. It writes no game or mod-manager state. A
    Completed run means only that the bounded claims were recorded; every entry
    remains Claimed until a structured request and deterministic evidence bind it.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StoreRoot,
        [Parameter(Mandatory)][string]$GameId,
        [Parameter(Mandatory)][string]$InstallationId,
        [Parameter(Mandatory)][string]$ProfileId,
        [Parameter(Mandatory)][string]$ContextFingerprint,
        [Parameter(Mandatory)][ValidateCount(1,1024)][object[]]$Claims,
        [string]$CaseId = ('problem-intake-' + [Guid]::NewGuid().ToString('N')),
        [string]$AuthorityId = 'user'
    )
    $transaction = $null
    try {
        $transaction = New-GridCaseStoreTransaction -StoreRoot $StoreRoot -CaseId $CaseId
        $ledger = New-GridProblemLedger -GameId $GameId -InstallationId $InstallationId -ProfileId $ProfileId -ContextFingerprint $ContextFingerprint
        $normalizedClaims = New-Object Collections.Generic.List[object]
        foreach ($item in @($Claims)) {
            if ($null -eq $item -or $null -eq $item.PSObject.Properties['claim'] -or $null -eq $item.PSObject.Properties['desiredOutcome']) {
                throw 'ProblemLedgerIntakeInvalid: each item requires claim and desiredOutcome.'
            }
            $claim = [string]$item.claim
            $desired = [string]$item.desiredOutcome
            $issueId = if ($item.PSObject.Properties['issueId'] -and -not [string]::IsNullOrWhiteSpace([string]$item.issueId)) {
                [string]$item.issueId
            }
            else {
                $identity = Get-GridProblemLedgerByteHash -Value ((@($GameId,$claim,$desired) | ForEach-Object { [string]$_ }) -join "`n")
                'problem.' + $identity.Substring(0,24).ToLowerInvariant()
            }
            Add-GridProblemClaim -Ledger $ledger -IssueId $issueId -Claim $claim -DesiredOutcome $desired -AuthorityId $AuthorityId | Out-Null
            $normalizedClaims.Add([pscustomobject][ordered]@{ issueId=$issueId; claim=$claim; desiredOutcome=$desired })
        }
        $intake = [pscustomobject][ordered]@{
            schemaVersion=1; gameId=$GameId; installationId=$InstallationId; profileId=$ProfileId
            contextFingerprint=$ContextFingerprint.ToUpperInvariant(); authorityId=$AuthorityId; claims=@($normalizedClaims.ToArray())
        }
        Write-GridCaseStoreArtifact -Transaction $transaction -RelativePath 'request\problem-ledger-intake.v1.json' -Value $intake | Out-Null
        Write-GridProblemLedgerArtifact -Transaction $transaction -Ledger $ledger | Out-Null
        $now = [DateTimeOffset]::UtcNow.ToString('o')
        $run = [pscustomobject][ordered]@{
            schemaVersion=1; runId='problem-intake'; caseId=$CaseId; state='Completed'; startedAt=$now; completedAt=$now
            planFingerprint=$ledger.ledgerFingerprint; resourcePolicyVersion='grid.problem-ledger-intake.v1'; gates=@()
            sufficiency=[pscustomobject]@{status='ClaimsRecorded';completionAssertsDiagnosis=$false}
            checkpoints=@(); primaryFailure=$null; secondaryFailures=@()
        }
        Seal-GridCaseStoreRun -Transaction $transaction -Run $run | Out-Null
        $sealed = Seal-GridDiagnosticCase -Transaction $transaction -SemanticBaselineFingerprint $ContextFingerprint -Runs @($run)
        [pscustomobject][ordered]@{ Status='ClaimsRecorded'; CaseId=$CaseId; CaseDirectory=$sealed.CaseDirectory; Manifest=$sealed.Manifest; Ledger=$ledger }
    }
    catch {
        if ($transaction -and [string]$transaction.State -eq 'Open' -and (Test-Path -LiteralPath $transaction.TransactionDirectory -PathType Container)) {
            Remove-Item -LiteralPath $transaction.TransactionDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}
