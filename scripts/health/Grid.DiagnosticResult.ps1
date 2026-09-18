#requires -Version 5.1
<#
.SYNOPSIS
Defines Grid's deterministic four-field diagnostic result contract.
.DESCRIPTION
The public result contains exactly affectedMods, modRoles, finding, and
solution. Audit bindings remain in the surrounding envelope and are never
rendered as additional user-facing fields.
#>
$script:GridDiagnosticResultSchemaVersion = 1
$script:GridDiagnosticResultPolicyVersion = 'diagnostic-result.v1'
$script:GridDiagnosticResultStates = @('NeedsContext','NeedsEvidence','ReadyToCollect','Diagnosed','Failed')
$script:GridDiagnosticFieldStatuses = @('Resolved','Unresolved')
$script:GridDiagnosticSolutionStatuses = @('Verified','Proposed','Unsupported','Unresolved')
$script:GridDiagnosticSubjectKinds = @('Mod','Plugin')
$script:GridDiagnosticRoles = @(
    'BaseOrMaster','WorldspaceOrCellEditor','PlacementProvider','OverrideProvider',
    'CompatibilityPatch','AssetProvider','AssetReplacer','GeneratedOutput','Dependency',
    'RuntimeModuleProvider'
)

function Get-GridDiagnosticObjectValue {
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

function Test-GridDiagnosticPublicText {
    param([string]$Text, [string]$FieldName)
    if ([string]::IsNullOrWhiteSpace($Text) -or $Text.Length -gt 4096) { return "$FieldName text must contain 1-4096 characters." }
    if ($Text.IndexOfAny([char[]]@(0, 9, 10, 13)) -ge 0) { return "$FieldName text cannot contain control characters or additional lines." }
    if ($Text -match '(?i)(?:^|[\s(])(?:[a-z]:\\|\\\\|(?:file|https?)://)') { return "$FieldName text cannot expose an absolute path or URI." }
    if ($Text -match '(?i)\b(?:api[_-]?key|access[_-]?token|refresh[_-]?token|password|secret)\s*[:=]') { return "$FieldName text cannot expose secret-bearing material." }
    if ($Text -match '(?i)\b(probably|maybe|perhaps|possibly|likely)\b|\bI\s+(think|believe|suspect)\b|\bconfidence\b') { return "$FieldName text contains speculative, personal, or confidence language prohibited by the public contract." }
    $null
}

function ConvertTo-GridDiagnosticRenderedText {
    param([AllowEmptyString()][string]$Text)
    # The public report is Markdown, so render evidence-derived labels as inert
    # single-line text rather than allowing them to create markup or headings.
    $value = $Text.Replace('\', '\\')
    foreach ($character in @('`','*','_','{','}','[',']','(',')','#','+','!','|','>','<')) {
        $value = $value.Replace($character, '\' + $character)
    }
    $value
}

function Sort-GridDiagnosticParallelArrays {
    param([Parameter(Mandatory)][string[]]$Keys, [Parameter(Mandatory)][object[]]$Values)
    if ($Keys.Count -ne $Values.Count) { throw 'Parallel diagnostic sort arrays must have the same length.' }
    for ($left = 0; $left -lt $Keys.Count - 1; $left++) {
        for ($right = $left + 1; $right -lt $Keys.Count; $right++) {
            if ([StringComparer]::Ordinal.Compare($Keys[$left], $Keys[$right]) -gt 0) {
                $key = $Keys[$left]; $Keys[$left] = $Keys[$right]; $Keys[$right] = $key
                $value = $Values[$left]; $Values[$left] = $Values[$right]; $Values[$right] = $value
            }
        }
    }
}

function Get-GridDiagnosticResultFingerprint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Result,
        [Parameter(Mandatory)][AllowEmptyString()][string]$ContextFingerprint,
        [Parameter(Mandatory)][string]$SemanticEvidenceFingerprint,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ResolverVersion,
        [string]$PolicyVersion = $script:GridDiagnosticResultPolicyVersion
    )
    [string[]]$affectedItems = @($Result.affectedMods.items | ForEach-Object {
        ConvertTo-GridCanonicalEvidenceValue -Value ([ordered]@{
            subjectId = [string]$_.subjectId; kind = [string]$_.kind; name = [string]$_.name
            authoritativeOrder = Get-GridDiagnosticObjectValue $_ authoritativeOrder
        })
    })
    [Array]::Sort($affectedItems, [StringComparer]::Ordinal)
    [string[]]$roleItems = @($Result.modRoles.items | ForEach-Object {
        [string[]]$roles = @($_.roles | ForEach-Object { [string]$_ })
        [Array]::Sort($roles, [StringComparer]::Ordinal)
        ConvertTo-GridCanonicalEvidenceValue -Value ([ordered]@{ subjectId = [string]$_.subjectId; roles = $roles })
    })
    [Array]::Sort($roleItems, [StringComparer]::Ordinal)
    $public = [ordered]@{
        affectedMods = [ordered]@{
            status = [string](Get-GridDiagnosticObjectValue $Result.affectedMods status)
            items = $affectedItems
        }
        modRoles = [ordered]@{
            status = [string](Get-GridDiagnosticObjectValue $Result.modRoles status)
            items = $roleItems
        }
        finding = [ordered]@{ status = [string]$Result.finding.status; text = [string]$Result.finding.text }
        solution = [ordered]@{ status = [string]$Result.solution.status; text = [string]$Result.solution.text }
    }
    $canonical = @($ResolverVersion, $PolicyVersion, $ContextFingerprint, $SemanticEvidenceFingerprint,
        (ConvertTo-GridCanonicalEvidenceValue -Value $public)) -join "`n"
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical)))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Test-GridDiagnosticResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$DiagnosticResult,
        [object[]]$Evidence = @(),
        [object[]]$RemediationProposals = @(),
        [object[]]$VerificationResults = @()
    )
    $errors = New-Object Collections.Generic.List[string]
    foreach ($field in @('schemaVersion','caseId','state','contextFingerprint','evidenceFingerprint','semanticEvidenceFingerprint','resolverVersion','policyVersion','result','resultFingerprint','createdAt')) {
        if ($null -eq $DiagnosticResult.PSObject.Properties[$field]) { $errors.Add("Missing diagnostic result field: $field") }
    }
    if ($errors.Count -gt 0) { return [pscustomobject]@{ IsValid = $false; Errors = @($errors) } }
    if ([int]$DiagnosticResult.schemaVersion -ne $script:GridDiagnosticResultSchemaVersion) { $errors.Add('Unsupported diagnostic result schemaVersion.') }
    if ([string]$DiagnosticResult.state -notin $script:GridDiagnosticResultStates) { $errors.Add('Invalid diagnostic result state.') }
    if ([string]::IsNullOrWhiteSpace([string]$DiagnosticResult.resolverVersion) -or ([string]$DiagnosticResult.resolverVersion).Length -gt 128) { $errors.Add('resolverVersion must contain 1-128 characters.') }
    $publicProperties = @($DiagnosticResult.result.PSObject.Properties.Name)
    foreach ($field in @('affectedMods','modRoles','finding','solution')) {
        if ($field -notin $publicProperties) { $errors.Add("Missing public diagnostic field: $field") }
    }
    foreach ($field in $publicProperties) {
        if ($field -notin @('affectedMods','modRoles','finding','solution')) { $errors.Add("Unexpected public diagnostic field: $field") }
    }
    if ($errors.Count -gt 0) { return [pscustomobject]@{ IsValid = $false; Errors = @($errors) } }

    $knownEvidence = @{}; foreach ($item in @($Evidence)) { $knownEvidence[[string]$item.evidenceId] = $item }
    $expectedRunFingerprint = Get-GridEvidenceFingerprint -Evidence $Evidence
    $expectedSemanticFingerprint = Get-GridSemanticEvidenceFingerprint -Evidence $Evidence
    if ($DiagnosticResult.evidenceFingerprint -ne $expectedRunFingerprint) { $errors.Add('Diagnostic result evidenceFingerprint does not match the supplied immutable evidence.') }
    if ($DiagnosticResult.semanticEvidenceFingerprint -ne $expectedSemanticFingerprint) { $errors.Add('Diagnostic result semanticEvidenceFingerprint does not match the supplied semantic evidence.') }
    foreach ($fieldName in @('affectedMods','modRoles','finding')) {
        $field = $DiagnosticResult.result.$fieldName
        if ([string]$field.status -notin $script:GridDiagnosticFieldStatuses) { $errors.Add("Invalid status for $fieldName."); continue }
        $ids = @($field.evidenceIds)
        if ($field.status -eq 'Resolved' -and $ids.Count -eq 0) { $errors.Add("Resolved field $fieldName requires evidenceIds.") }
        foreach ($id in $ids) {
            if (-not $knownEvidence.ContainsKey([string]$id)) { $errors.Add("Field $fieldName references unknown evidenceId '$id'."); continue }
            $item = $knownEvidence[[string]$id]
            if ($item.contextFingerprint -ne $DiagnosticResult.contextFingerprint -or
                $item.verificationStatus -notin @('Collected','Verified') -or $item.sourceType -eq 'ModelReasoning') {
                $errors.Add("Field $fieldName references evidence that cannot resolve a public result.")
            }
        }
    }
    $solution = $DiagnosticResult.result.solution
    if ([string]$solution.status -notin $script:GridDiagnosticSolutionStatuses) { $errors.Add('Invalid status for solution.') }
    foreach ($id in @($solution.evidenceIds)) {
        if (-not $knownEvidence.ContainsKey([string]$id)) { $errors.Add("Field solution references unknown evidenceId '$id'."); continue }
        $item = $knownEvidence[[string]$id]
        if ($item.contextFingerprint -ne $DiagnosticResult.contextFingerprint -or
            $item.verificationStatus -notin @('Collected','Verified') -or $item.sourceType -eq 'ModelReasoning') {
            $errors.Add('Field solution references evidence that cannot resolve a public result.')
        }
    }
    if ($solution.status -ne 'Unresolved' -and @($solution.evidenceIds).Count -eq 0) { $errors.Add("Solution status '$($solution.status)' requires evidenceIds.") }
    foreach ($item in @($DiagnosticResult.result.affectedMods.items)) {
        if ([string]$item.kind -notin $script:GridDiagnosticSubjectKinds) { $errors.Add("Invalid affected subject kind '$($item.kind)'.") }
        if ([string]::IsNullOrWhiteSpace([string]$item.subjectId) -or [string]::IsNullOrWhiteSpace([string]$item.name)) { $errors.Add('Affected subjects require subjectId and name.') }
        if (([string]$item.subjectId).Length -gt 512 -or ([string]$item.name).Length -gt 512 -or
            ([string]$item.subjectId).IndexOfAny([char[]]@(0,9,10,13)) -ge 0 -or ([string]$item.name).IndexOfAny([char[]]@(0,9,10,13,'\','/')) -ge 0) {
            $errors.Add('Affected subject identities must be bounded single-line values and names cannot be paths.')
        }
        $order = Get-GridDiagnosticObjectValue $item authoritativeOrder
        if ($null -ne $order -and ([int64]$order -lt 0 -or [double]$order -ne [math]::Floor([double]$order))) { $errors.Add('authoritativeOrder must be a non-negative integer when supplied.') }
    }
    if ($DiagnosticResult.result.affectedMods.status -eq 'Resolved' -and @($DiagnosticResult.result.affectedMods.items).Count -eq 0) { $errors.Add('Resolved affectedMods requires at least one subject.') }
    $subjects = @{}; foreach ($item in @($DiagnosticResult.result.affectedMods.items)) { $subjects[[string]$item.subjectId] = $true }
    foreach ($item in @($DiagnosticResult.result.modRoles.items)) {
        if (-not $subjects.ContainsKey([string]$item.subjectId)) { $errors.Add("Role references unknown subject '$($item.subjectId)'.") }
        foreach ($role in @($item.roles)) { if ([string]$role -notin $script:GridDiagnosticRoles) { $errors.Add("Invalid diagnostic role '$role'.") } }
        if (@($item.roles).Count -eq 0) { $errors.Add("Resolved role subject '$($item.subjectId)' requires at least one role.") }
    }
    if ($DiagnosticResult.result.modRoles.status -eq 'Resolved' -and @($DiagnosticResult.result.modRoles.items).Count -eq 0) { $errors.Add('Resolved modRoles requires at least one role assignment.') }
    if ($DiagnosticResult.result.affectedMods.status -eq 'Unresolved' -and @($DiagnosticResult.result.affectedMods.items).Count -ne 0) { $errors.Add('Unresolved affectedMods cannot contain asserted subjects.') }
    if ($DiagnosticResult.result.modRoles.status -eq 'Unresolved' -and @($DiagnosticResult.result.modRoles.items).Count -ne 0) { $errors.Add('Unresolved modRoles cannot contain asserted roles.') }
    foreach ($fieldName in @('finding','solution')) {
        $text = [string]$DiagnosticResult.result.$fieldName.text
        $publicTextError = Test-GridDiagnosticPublicText -Text $text -FieldName $fieldName
        if ($publicTextError) { $errors.Add($publicTextError) }
    }
    if ($solution.status -in @('Proposed','Unsupported','Verified') -and [string]::IsNullOrWhiteSpace([string]$solution.proposalId)) {
        $errors.Add("Solution status '$($solution.status)' requires a bound proposalId.")
    }
    $boundProposal = $null
    if ($solution.status -in @('Proposed','Unsupported','Verified') -and -not [string]::IsNullOrWhiteSpace([string]$solution.proposalId)) {
        $proposals = @($RemediationProposals | Where-Object proposalId -eq $solution.proposalId)
        if ($proposals.Count -ne 1) { $errors.Add("Solution status '$($solution.status)' requires exactly one supplied remediation proposal with the bound proposalId.") }
        else {
            $boundProposal = $proposals[0]
            $proposalValidation = Test-GridRemediationProposal -Proposal $boundProposal -CurrentContextFingerprint $DiagnosticResult.contextFingerprint -CurrentEvidenceFingerprint $DiagnosticResult.evidenceFingerprint
            if (-not $proposalValidation.IsValid -or $proposalValidation.IsStale) { $errors.Add('The solution remediation proposal is invalid or stale for the current result.') }
            if ($solution.status -eq 'Unsupported' -and $boundProposal.status -ne 'Unsupported') { $errors.Add('An Unsupported solution requires an Unsupported remediation proposal.') }
            if ($solution.status -in @('Proposed','Verified') -and $boundProposal.status -eq 'Unsupported') { $errors.Add("A $($solution.status) solution cannot bind an Unsupported remediation proposal.") }
        }
    }
    if ($solution.status -eq 'Verified') {
        if ([string]::IsNullOrWhiteSpace([string]$solution.verificationId)) { $errors.Add('A Verified solution requires a bound verificationId.') }
        else {
            $verification = @($VerificationResults | Where-Object verificationId -eq $solution.verificationId)
            if ($verification.Count -ne 1 -or $verification[0].status -ne 'Verified' -or $verification[0].proposalId -ne $solution.proposalId) {
                $errors.Add('A Verified solution requires one successful verification bound to the same proposal.')
            }
        }
    }
    $expectedFingerprint = Get-GridDiagnosticResultFingerprint -Result $DiagnosticResult.result -ContextFingerprint $DiagnosticResult.contextFingerprint -SemanticEvidenceFingerprint $DiagnosticResult.semanticEvidenceFingerprint -ResolverVersion $DiagnosticResult.resolverVersion -PolicyVersion $DiagnosticResult.policyVersion
    if ($expectedFingerprint -ne $DiagnosticResult.resultFingerprint) { $errors.Add('Diagnostic result fingerprint does not match its public semantic content.') }
    if ($DiagnosticResult.state -eq 'Diagnosed' -and ($DiagnosticResult.result.affectedMods.status -ne 'Resolved' -or $DiagnosticResult.result.modRoles.status -ne 'Resolved' -or $DiagnosticResult.result.finding.status -ne 'Resolved')) {
        $errors.Add('Diagnosed requires resolved affectedMods, modRoles, and finding fields.')
    }
    [pscustomobject]@{ IsValid = ($errors.Count -eq 0); Errors = @($errors) }
}

function New-GridDiagnosticResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][ValidateSet('NeedsContext','NeedsEvidence','ReadyToCollect','Diagnosed','Failed')][string]$State,
        [AllowEmptyString()][string]$ContextFingerprint = '',
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Evidence,
        [Parameter(Mandatory)]$AffectedMods,
        [Parameter(Mandatory)]$ModRoles,
        [Parameter(Mandatory)]$Finding,
        [Parameter(Mandatory)]$Solution,
        [string]$EvidenceFingerprint,
        [ValidateNotNullOrEmpty()][string]$ResolverVersion = 'shared.unresolved.v1',
        [object[]]$RemediationProposals = @(),
        [object[]]$VerificationResults = @(),
        [datetime]$CreatedAt = [datetime]::UtcNow
    )
    if (-not $EvidenceFingerprint) { $EvidenceFingerprint = Get-GridEvidenceFingerprint -Evidence $Evidence }
    $semanticFingerprint = Get-GridSemanticEvidenceFingerprint -Evidence $Evidence
    [object[]]$affectedValues = @($AffectedMods.items | ForEach-Object {
        $order = Get-GridDiagnosticObjectValue $_ authoritativeOrder
        [pscustomobject][ordered]@{
            subjectId = [string]$_.subjectId; kind = [string]$_.kind; name = [string]$_.name
            authoritativeOrder = $order
        }
    })
    [string[]]$affectedKeys = @($affectedValues | ForEach-Object {
        $orderKey = if ($null -eq $_.authoritativeOrder) { '~~~~~~~~~~~~' } else { '{0:D12}' -f [int64]$_.authoritativeOrder }
        $orderKey + [char]31 + ([string]$_.subjectId).ToUpperInvariant()
    })
    if ($affectedValues.Count -gt 1) { Sort-GridDiagnosticParallelArrays -Keys $affectedKeys -Values $affectedValues }
    $subjectOrder = @{}; for ($index = 0; $index -lt $affectedValues.Count; $index++) { $subjectOrder[[string]$affectedValues[$index].subjectId] = $index }
    [object[]]$roleValues = @($ModRoles.items | ForEach-Object {
        [string[]]$roles = @($_.roles | ForEach-Object { [string]$_ } | Select-Object -Unique)
        [string[]]$roleKeys = @($roles | ForEach-Object { '{0:D4}' -f [Array]::IndexOf($script:GridDiagnosticRoles, $_) })
        if ($roles.Count -gt 1) { Sort-GridDiagnosticParallelArrays -Keys $roleKeys -Values $roles }
        [pscustomobject][ordered]@{ subjectId = [string]$_.subjectId; roles = $roles }
    })
    [string[]]$roleItemKeys = @($roleValues | ForEach-Object {
        $order = if ($subjectOrder.ContainsKey([string]$_.subjectId)) { [int]$subjectOrder[[string]$_.subjectId] } else { [int]::MaxValue }
        ('{0:D12}' -f $order) + [char]31 + ([string]$_.subjectId).ToUpperInvariant()
    })
    if ($roleValues.Count -gt 1) { Sort-GridDiagnosticParallelArrays -Keys $roleItemKeys -Values $roleValues }
    function Get-SortedGridDiagnosticEvidenceIds($Field) {
        [string[]]$ids = @($Field.evidenceIds | ForEach-Object { [string]$_ } | Select-Object -Unique)
        [Array]::Sort($ids, [StringComparer]::Ordinal)
        $ids
    }
    $canonicalSolution = [ordered]@{
        status = [string]$Solution.status; text = [string]$Solution.text
        evidenceIds = @(Get-SortedGridDiagnosticEvidenceIds $Solution)
    }
    foreach ($propertyName in @('proposalId','verificationId')) {
        $propertyValue = Get-GridDiagnosticObjectValue $Solution $propertyName
        if (-not [string]::IsNullOrWhiteSpace([string]$propertyValue)) { $canonicalSolution[$propertyName] = [string]$propertyValue }
    }
    $public = [pscustomobject][ordered]@{
        affectedMods = [pscustomobject][ordered]@{ status = [string]$AffectedMods.status; items = $affectedValues; evidenceIds = @(Get-SortedGridDiagnosticEvidenceIds $AffectedMods) }
        modRoles = [pscustomobject][ordered]@{ status = [string]$ModRoles.status; items = $roleValues; evidenceIds = @(Get-SortedGridDiagnosticEvidenceIds $ModRoles) }
        finding = [pscustomobject][ordered]@{ status = [string]$Finding.status; text = [string]$Finding.text; evidenceIds = @(Get-SortedGridDiagnosticEvidenceIds $Finding) }
        solution = [pscustomobject]$canonicalSolution
    }
    $resultFingerprint = Get-GridDiagnosticResultFingerprint -Result $public -ContextFingerprint $ContextFingerprint -SemanticEvidenceFingerprint $semanticFingerprint -ResolverVersion $ResolverVersion
    $result = [pscustomobject][ordered]@{
        schemaVersion = $script:GridDiagnosticResultSchemaVersion
        caseId = $CaseId
        state = $State
        contextFingerprint = $ContextFingerprint
        evidenceFingerprint = $EvidenceFingerprint
        semanticEvidenceFingerprint = $semanticFingerprint
        resolverVersion = $ResolverVersion
        policyVersion = $script:GridDiagnosticResultPolicyVersion
        result = $public
        resultFingerprint = $resultFingerprint
        createdAt = $CreatedAt.ToUniversalTime().ToString('o')
    }
    $validation = Test-GridDiagnosticResult -DiagnosticResult $result -Evidence $Evidence -RemediationProposals $RemediationProposals -VerificationResults $VerificationResults
    if (-not $validation.IsValid) { throw ($validation.Errors -join ' ') }
    $result
}

function New-GridUnresolvedDiagnosticResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][ValidateSet('NeedsContext','NeedsEvidence','ReadyToCollect','Failed')][string]$State,
        [AllowEmptyString()][string]$ContextFingerprint = '',
        [object[]]$Evidence = @(),
        [Parameter(Mandatory)][string]$Finding,
        [Parameter(Mandatory)][string]$NextStep
    )
    $empty = [pscustomobject][ordered]@{ status = 'Unresolved'; items = @(); evidenceIds = @() }
    New-GridDiagnosticResult -CaseId $CaseId -State $State -ContextFingerprint $ContextFingerprint -Evidence $Evidence `
        -AffectedMods $empty -ModRoles ([pscustomobject][ordered]@{ status = 'Unresolved'; items = @(); evidenceIds = @() }) `
        -Finding ([pscustomobject][ordered]@{ status = 'Unresolved'; text = $Finding; evidenceIds = @() }) `
        -Solution ([pscustomobject][ordered]@{ status = 'Unresolved'; text = $NextStep; evidenceIds = @() })
}

function ConvertTo-GridDiagnosticResultText {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$DiagnosticResult, [object[]]$Evidence = @(), [object[]]$RemediationProposals = @(), [object[]]$VerificationResults = @())
    $validation = Test-GridDiagnosticResult -DiagnosticResult $DiagnosticResult -Evidence $Evidence -RemediationProposals $RemediationProposals -VerificationResults $VerificationResults
    if (-not $validation.IsValid) { throw ($validation.Errors -join ' ') }
    $affected = if ($DiagnosticResult.result.affectedMods.status -eq 'Resolved') {
        [string[]]$lines = @($DiagnosticResult.result.affectedMods.items | ForEach-Object { "$(ConvertTo-GridDiagnosticRenderedText ([string]$_.kind)): $(ConvertTo-GridDiagnosticRenderedText ([string]$_.name))" })
        $lines -join [Environment]::NewLine
    } else { 'UNRESOLVED' }
    $rolesBySubject = @{}; foreach ($subject in @($DiagnosticResult.result.affectedMods.items)) { $rolesBySubject[[string]$subject.subjectId] = [string]$subject.name }
    $roles = if ($DiagnosticResult.result.modRoles.status -eq 'Resolved') {
        [string[]]$lines = @($DiagnosticResult.result.modRoles.items | ForEach-Object {
            [string[]]$itemRoles = @($_.roles | ForEach-Object { [string]$_ } | Select-Object -Unique)
            [Array]::Sort($itemRoles, [StringComparer]::Ordinal)
            "$(ConvertTo-GridDiagnosticRenderedText $rolesBySubject[[string]$_.subjectId]): $($itemRoles -join ', ')"
        })
        $lines -join [Environment]::NewLine
    } else { 'UNRESOLVED' }
    @(
        'Affected mod(s):', $affected, '',
        'Mod role(s):', $roles, '',
        'Finding:', (ConvertTo-GridDiagnosticRenderedText ([string]$DiagnosticResult.result.finding.text)), '',
        'Solution:', (ConvertTo-GridDiagnosticRenderedText ([string]$DiagnosticResult.result.solution.text))
    ) -join [Environment]::NewLine
}

function Save-GridDiagnosticResult {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$DiagnosticResult, [Parameter(Mandatory)][string]$CaseDirectory, [object[]]$Evidence = @(), [object[]]$RemediationProposals = @(), [object[]]$VerificationResults = @())
    $validation = Test-GridDiagnosticResult -DiagnosticResult $DiagnosticResult -Evidence $Evidence -RemediationProposals $RemediationProposals -VerificationResults $VerificationResults
    if (-not $validation.IsValid) { throw ($validation.Errors -join ' ') }
    $root = [IO.Path]::GetFullPath($CaseDirectory)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Case directory does not exist: $root" }
    $path = Join-Path $root 'diagnostic-result.json'
    Write-GridJsonAtomic -InputObject $DiagnosticResult -LiteralPath $path
    $path
}
