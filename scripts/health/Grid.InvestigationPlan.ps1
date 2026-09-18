<#
.SYNOPSIS
Defines and validates Grid's versioned, game-agnostic InvestigationPlan.
.DESCRIPTION
An InvestigationPlan is runtime case data, not production adapter source. It
is created only from what the user or an attached evidence source explicitly
stated -- Grid has no model capable of inferring FormIDs, plugin names, or
hypotheses from arbitrary prose, so plain language populates only the
`originalRequest` and `normalizedSymptoms` fields. Every other technical field
(candidatePlugins, observedForms, hypotheses, evidenceReferences) is populated
only when the caller supplies it explicitly, and every populated value gets a
provenance record naming its source and confidence. When required technical
inputs are absent, the plan is still created, but its `status` is
`NeedsEvidence` and `missingInputs`/`recommendedCollectors` explain what is
needed next. This plan is written only inside the case evidence directory.
#>

$script:GridInvestigationPlanSchemaVersion = 2
$script:GridInvestigationPlanSupportedSchemaVersions = @(1, 2)
$script:GridInvestigationPlanValidSources = @('UserStated', 'ToolCollected', 'EvidenceAttachment', 'ProductionManifest')
$script:GridInvestigationPlanValidStatuses = @('NeedsContext', 'NeedsEvidence', 'ReadyToCollect', 'Diagnosed', 'Failed')
$script:GridInvestigationPlanValidPurposes = @('Diagnosis', 'Baseline')
$script:GridBaselineRequiredGates = @(
    'InstallationBaseline', 'ProfileBaseline', 'PluginBaseline', 'AssetBaseline',
    'PriorCaseBaseline', 'SymptomEvidenceBaseline', 'RuntimeReferenceBaseline'
)

function New-GridProvenanceRecord {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Field,
        [Parameter(Mandatory)][ValidateSet('UserStated', 'ToolCollected', 'EvidenceAttachment', 'ProductionManifest')][string]$Source,
        [Parameter(Mandatory)][ValidateRange(0.0, 1.0)][double]$Confidence,
        [string]$Note = ''
    )
    [ordered]@{ field = $Field; source = $Source; confidence = $Confidence; note = $Note }
}

function New-GridInvestigationPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][string]$GameId,
        [string]$InstallationId,
        [string]$ProfileId,
        [Parameter(Mandatory)][string]$OriginalRequest,
        [string]$StatedLocation,
        [string[]]$StatedPluginNames = @(),
        [string[]]$StatedProviderNames = @(),
        [string[]]$StatedFormIds = @(),
        [string[]]$StatedEditorIds = @(),
        [string[]]$EvidenceReferences = @(),
        [string[]]$ParentCaseIds = @(),
        [string[]]$EvidenceIds = @(),
        [object[]]$CollectorQueries = @(),
        [ValidateSet('Diagnosis', 'Baseline')][string]$Purpose = 'Diagnosis',
        [string[]]$RequiredGates = @(),
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][object[]]$CapabilityBindings
    )

    $now = (Get-Date).ToString('o')
    $provenance = New-Object Collections.Generic.List[object]

    $capabilities = New-Object Collections.Generic.List[object]
    $capabilityIds = @{}
    foreach ($binding in @($CapabilityBindings)) {
        $capabilityId = [string]$binding.capabilityId
        $capabilityVersion = [string]$binding.capabilityVersion
        if ([string]::IsNullOrWhiteSpace($capabilityId) -or [string]::IsNullOrWhiteSpace($capabilityVersion)) {
            throw 'Every capability binding requires capabilityId and capabilityVersion.'
        }
        $key = $capabilityId.ToLowerInvariant()
        if ($capabilityIds.ContainsKey($key)) { throw "Duplicate capability binding: $capabilityId" }
        $capabilityIds[$key] = $true
        $capabilities.Add([ordered]@{ capabilityId = $capabilityId; capabilityVersion = $capabilityVersion })
        $provenance.Add((New-GridProvenanceRecord -Field "capabilities[$($capabilities.Count - 1)]" -Source 'ProductionManifest' -Confidence 1.0 -Note "Resolved from the validated capability dependency graph."))
    }

    $resolvedGates = @()
    if ($Purpose -eq 'Baseline') {
        $resolvedGates = if (@($RequiredGates).Count -eq 0) { @($script:GridBaselineRequiredGates) } else { @($RequiredGates) }
        $seenGates = @{}
        foreach ($gate in $resolvedGates) {
            if ([string]::IsNullOrWhiteSpace([string]$gate) -or $gate -notin $script:GridBaselineRequiredGates) {
                throw "Unsupported baseline gate '$gate'."
            }
            if ($seenGates.ContainsKey($gate)) { throw "Duplicate baseline gate '$gate'." }
            $seenGates[$gate] = $true
        }
        $provenance.Add((New-GridProvenanceRecord -Field 'purpose' -Source 'ProductionManifest' -Confidence 1.0 -Note 'Selected by the reusable baseline entry point.'))
        foreach ($gate in $resolvedGates) {
            $index = [array]::IndexOf([object[]]$resolvedGates, $gate)
            $provenance.Add((New-GridProvenanceRecord -Field "requiredGates[$index]" -Source 'ProductionManifest' -Confidence 1.0 -Note 'Selected from the bounded baseline gate vocabulary.'))
        }
    }

    # Plain language is only ever copied verbatim; it is never mined for technical targets.
    $normalizedSymptoms = @($OriginalRequest)
    $provenance.Add((New-GridProvenanceRecord -Field 'normalizedSymptoms[0]' -Source 'UserStated' -Confidence 1.0 -Note 'Verbatim copy of the original request; not paraphrased or interpreted.'))

    $locations = @()
    if (-not [string]::IsNullOrWhiteSpace($StatedLocation)) {
        $locations = @($StatedLocation)
        $provenance.Add((New-GridProvenanceRecord -Field 'locations[0]' -Source 'UserStated' -Confidence 1.0 -Note 'Location explicitly stated by the caller.'))
    }

    $candidatePlugins = New-Object Collections.Generic.List[object]
    foreach ($name in @($StatedPluginNames | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)) {
        $candidatePlugins.Add([ordered]@{ name = $name; source = 'UserStated'; confidence = 1.0 })
        $provenance.Add((New-GridProvenanceRecord -Field "candidatePlugins[$($candidatePlugins.Count - 1)]" -Source 'UserStated' -Confidence 1.0 -Note "Plugin name '$name' was explicitly supplied by the caller."))
    }

    $observedForms = New-Object Collections.Generic.List[object]
    foreach ($formId in @($StatedFormIds | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)) {
        $observedForms.Add([ordered]@{ formId = $formId; editorId = $null; source = 'UserStated'; confidence = 1.0 })
        $provenance.Add((New-GridProvenanceRecord -Field "observedForms[$($observedForms.Count - 1)]" -Source 'UserStated' -Confidence 1.0 -Note "FormID '$formId' was explicitly supplied by the caller."))
    }
    foreach ($editorId in @($StatedEditorIds | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)) {
        $observedForms.Add([ordered]@{ formId = $null; editorId = $editorId; source = 'UserStated'; confidence = 1.0 })
        $provenance.Add((New-GridProvenanceRecord -Field "observedForms[$($observedForms.Count - 1)]" -Source 'UserStated' -Confidence 1.0 -Note "EditorID '$editorId' was explicitly supplied by the caller."))
    }

    $evidenceReferenceList = New-Object Collections.Generic.List[object]
    foreach ($reference in @($EvidenceReferences | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)) {
        $evidenceReferenceList.Add([ordered]@{ reference = $reference; source = 'EvidenceAttachment'; confidence = 1.0 })
        $provenance.Add((New-GridProvenanceRecord -Field "evidenceReferences[$($evidenceReferenceList.Count - 1)]" -Source 'EvidenceAttachment' -Confidence 1.0 -Note "Attached evidence reference supplied by the caller."))
    }

    # Grid never invents hypotheses; hypotheses only enter the plan if the caller states one explicitly (not exposed as a parameter yet -- reserved for future explicit input).
    $hypotheses = @()

    $missingContext = New-Object Collections.Generic.List[string]
    if ([string]::IsNullOrWhiteSpace($InstallationId)) { $missingContext.Add('installationId') }
    if ([string]::IsNullOrWhiteSpace($ProfileId)) { $missingContext.Add('profileId') }
    $missingInputs = New-Object Collections.Generic.List[string]
    if ($Purpose -eq 'Diagnosis') {
        if ($candidatePlugins.Count -eq 0) { $missingInputs.Add('candidatePlugins') }
        if ($observedForms.Count -eq 0) { $missingInputs.Add('observedForms') }
    }

    # A provider is an MO2 mod-directory identity, not a plugin identity. Keep
    # these seeds separate so a plugin filename is never treated as a mod name.
    $providerSeeds = New-Object Collections.Generic.List[object]
    foreach ($name in @($StatedProviderNames | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)) {
        $providerSeeds.Add([ordered]@{ name = $name; source = 'UserStated'; confidence = 1.0 })
        $provenance.Add((New-GridProvenanceRecord -Field "providerSeeds[$($providerSeeds.Count - 1)]" -Source 'UserStated' -Confidence 1.0 -Note "MO2 provider name '$name' was explicitly supplied by the caller."))
    }

    $parentCaseIdList = @($ParentCaseIds | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
    for ($index = 0; $index -lt $parentCaseIdList.Count; $index++) {
        $provenance.Add((New-GridProvenanceRecord -Field "parentCaseIds[$index]" -Source 'EvidenceAttachment' -Confidence 1.0 -Note 'Prior case identity explicitly supplied by the caller.'))
    }
    $evidenceIdList = @($EvidenceIds | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
    for ($index = 0; $index -lt $evidenceIdList.Count; $index++) {
        $provenance.Add((New-GridProvenanceRecord -Field "evidenceIds[$index]" -Source 'EvidenceAttachment' -Confidence 1.0 -Note 'Prior evidence identity explicitly supplied by the caller.'))
    }

    $recommendedCollectors = New-Object Collections.Generic.List[string]
    if ($missingContext.Count -gt 0) {
        $recommendedCollectors.Add('Supply an authoritative installation and profile context; Grid will not infer either from the prompt.')
    }
    if ($Purpose -eq 'Diagnosis' -and $missingInputs.Count -gt 0) {
        $recommendedCollectors.Add('Supply explicit candidate plugin names (e.g. -CandidatePlugin) identified by the user or prior tool evidence.')
        $recommendedCollectors.Add('Supply explicit observed FormIDs/EditorIDs (e.g. -ObservedFormId/-ObservedEditorId) captured from an in-game console, log, or screenshot.')
    }

    $status = if ($missingContext.Count -gt 0) { 'NeedsContext' } elseif ($missingInputs.Count -gt 0) { 'NeedsEvidence' } else { 'ReadyToCollect' }

    $plan = [ordered]@{
        schemaVersion = $script:GridInvestigationPlanSchemaVersion
        caseId = $CaseId
        gameId = $GameId
        installationId = $InstallationId
        profileId = $ProfileId
        originalRequest = $OriginalRequest
        purpose = $Purpose
        requiredGates = @($resolvedGates)
        capabilities = $capabilities.ToArray()
        normalizedSymptoms = $normalizedSymptoms
        locations = $locations
        observedForms = $observedForms.ToArray()
        candidatePlugins = $candidatePlugins.ToArray()
        providerSeeds = $providerSeeds.ToArray()
        hypotheses = @($hypotheses)
        evidenceReferences = $evidenceReferenceList.ToArray()
        parentCaseIds = @($parentCaseIdList)
        evidenceIds = @($evidenceIdList)
        collectorQueries = @($CollectorQueries)
        missingInputs = @($missingContext.ToArray()) + @($missingInputs.ToArray())
        status = $status
        recommendedCollectors = $recommendedCollectors.ToArray()
        provenance = $provenance.ToArray()
        createdAt = $now
        updatedAt = $now
    }
    return [pscustomobject]$plan
}

function Test-GridInvestigationPlanSchema {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Plan)

    $errors = New-Object Collections.Generic.List[string]
    $requiredFields = @(
        'schemaVersion', 'caseId', 'gameId', 'installationId', 'profileId', 'originalRequest',
        'normalizedSymptoms', 'locations', 'observedForms', 'candidatePlugins', 'hypotheses',
        'evidenceReferences', 'collectorQueries', 'missingInputs', 'status', 'provenance',
        'createdAt', 'updatedAt'
    )
    foreach ($field in $requiredFields) {
        if ($null -eq $Plan.PSObject.Properties[$field]) { $errors.Add("Missing required InvestigationPlan field: $field") }
    }
    if ($errors.Count -gt 0) { return [pscustomobject]@{ IsValid = $false; Errors = @($errors) } }

    if ([int]$Plan.schemaVersion -notin $script:GridInvestigationPlanSupportedSchemaVersions) {
        $errors.Add("Unsupported InvestigationPlan schemaVersion: $($Plan.schemaVersion). Expected one of: $($script:GridInvestigationPlanSupportedSchemaVersions -join ', ').")
    }
    if ([int]$Plan.schemaVersion -eq 2) {
        if ($null -eq $Plan.PSObject.Properties['providerSeeds']) {
            $errors.Add('InvestigationPlan v2 requires providerSeeds (which may be empty).')
        }
        if ($null -eq $Plan.PSObject.Properties['capabilities'] -or @($Plan.capabilities).Count -eq 0) {
            $errors.Add('InvestigationPlan v2 requires at least one capability binding.')
        }
        else {
            $seenCapabilities = @{}
            foreach ($binding in @($Plan.capabilities)) {
                $capabilityId = [string]$binding.capabilityId
                $capabilityVersion = [string]$binding.capabilityVersion
                if ($capabilityId -notmatch '^grid\.[a-z0-9]+(?:[.-][a-z0-9]+)*$' -or $capabilityVersion -notmatch '^\d+\.\d+\.\d+$') {
                    $errors.Add("Invalid capability binding '$capabilityId@$capabilityVersion'.")
                    continue
                }
                $key = $capabilityId.ToLowerInvariant()
                if ($seenCapabilities.ContainsKey($key)) { $errors.Add("Duplicate capability binding: $capabilityId") }
                $seenCapabilities[$key] = $true
            }
        }
        $purpose = if ($null -eq $Plan.PSObject.Properties['purpose'] -or [string]::IsNullOrWhiteSpace([string]$Plan.purpose)) { 'Diagnosis' } else { [string]$Plan.purpose }
        if ($purpose -notin $script:GridInvestigationPlanValidPurposes) {
            $errors.Add("Invalid InvestigationPlan purpose '$purpose'.")
        }
        if ($purpose -eq 'Baseline') {
            $baselineBindings = @($Plan.capabilities | Where-Object { [string]$_.capabilityId -match '^grid\.game\.[a-z0-9.-]+\.baseline\.collect$' })
            if ($baselineBindings.Count -ne 1) {
                $errors.Add('A baseline InvestigationPlan must bind exactly one game baseline collection capability.')
            }
            $gates = if ($null -eq $Plan.PSObject.Properties['requiredGates']) { @() } else { @($Plan.requiredGates) }
            if ($gates.Count -eq 0) { $errors.Add('A baseline InvestigationPlan requires at least one baseline gate.') }
            $seenGates = @{}
            foreach ($gate in $gates) {
                if ([string]$gate -notin $script:GridBaselineRequiredGates) { $errors.Add("Unsupported baseline gate '$gate'.") }
                elseif ($seenGates.ContainsKey([string]$gate)) { $errors.Add("Duplicate baseline gate '$gate'.") }
                else { $seenGates[[string]$gate] = $true }
            }
        }
    }
    if ($Plan.status -notin $script:GridInvestigationPlanValidStatuses) {
        $errors.Add("Invalid status '$($Plan.status)'. Expected one of: $($script:GridInvestigationPlanValidStatuses -join ', ').")
    }
    foreach ($entry in @($Plan.provenance)) {
        if ($null -eq $entry.field -or $null -eq $entry.source -or $null -eq $entry.confidence) {
            $errors.Add('Every provenance entry must include field, source, and confidence.')
            continue
        }
        if ([string]$entry.source -notin $script:GridInvestigationPlanValidSources) {
            $errors.Add("Provenance entry for '$($entry.field)' has an invalid source: $($entry.source).")
        }
        if ([double]$entry.confidence -lt 0.0 -or [double]$entry.confidence -gt 1.0) {
            $errors.Add("Provenance entry for '$($entry.field)' has a confidence outside 0.0-1.0: $($entry.confidence).")
        }
    }
    if (([string]::IsNullOrWhiteSpace([string]$Plan.installationId) -or [string]::IsNullOrWhiteSpace([string]$Plan.profileId)) -and $Plan.status -ne 'NeedsContext') {
        $errors.Add('A plan without installation and profile context must have status NeedsContext.')
    }
    $planPurpose = if ($null -eq $Plan.PSObject.Properties['purpose'] -or [string]::IsNullOrWhiteSpace([string]$Plan.purpose)) { 'Diagnosis' } else { [string]$Plan.purpose }
    if ($planPurpose -eq 'Diagnosis' -and -not [string]::IsNullOrWhiteSpace([string]$Plan.installationId) -and -not [string]::IsNullOrWhiteSpace([string]$Plan.profileId) -and @($Plan.candidatePlugins).Count -eq 0 -and @($Plan.observedForms).Count -eq 0 -and $Plan.status -ne 'NeedsEvidence') {
        $errors.Add('A contextualized plan with no candidate plugins and no observed forms must have status NeedsEvidence.')
    }

    return [pscustomobject]@{ IsValid = ($errors.Count -eq 0); Errors = @($errors) }
}

function Save-GridInvestigationPlan {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Plan, [Parameter(Mandatory)][string]$CaseDirectory)
    $validation = Test-GridInvestigationPlanSchema -Plan $Plan
    if (-not $validation.IsValid) { throw ($validation.Errors -join ' ') }
    $root = [IO.Path]::GetFullPath($CaseDirectory)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Case directory does not exist: $root" }
    $path = Join-Path $root 'investigation-plan.json'
    if (Get-Command Write-GridJsonAtomic -ErrorAction SilentlyContinue) {
        Write-GridJsonAtomic -InputObject $Plan -LiteralPath $path
    } else {
        [IO.File]::WriteAllText($path, ($Plan | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
    }
    $path
}
