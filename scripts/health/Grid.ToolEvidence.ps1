#requires -Version 5.1
<#
.SYNOPSIS
Resolves registered tools and orchestrates bounded, read-only evidence runs.
.DESCRIPTION
Tool selection is explicit structured input. Capability dependencies determine
execution order. Each selected tool receives an independent receipt so one
failure cannot erase evidence collected by another tool.
#>

$script:GridToolDefinitionStates = @('Available','Unavailable','Misconfigured','UnsupportedForGame','UnsupportedForClass')
$script:GridToolRunStates = @('Collected','Unavailable','Failed')


function Get-GridToolEvidenceSha256 {
    [CmdletBinding()]
    param([object[]]$Evidence = @())

    # Windows PowerShell 5.1 emits no pipeline object for an empty array from
    # ConvertTo-GridCanonicalJsonValue, which would leave the canonical JSON
    # string null. Wrap the collection in a named object so zero/one/many
    # evidence items have one stable canonical shape before hashing.
    $items = [object[]]@($Evidence)
    $normalized = [pscustomobject][ordered]@{ count = [int]$items.Count; items = $items }
    Get-GridCanonicalJsonSha256 -InputObject $normalized
}

function Test-GridToolDefinition {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Definition)
    $errors = New-Object Collections.Generic.List[string]
    foreach ($name in @('schemaVersion','toolId','displayName','gameId','adapter','observationMode','rootCapabilityIds','supportedClassIds','limits')) {
        if ($null -eq $Definition.PSObject.Properties[$name]) { $errors.Add("Missing tool-definition field: $name") }
    }
    if ($errors.Count -gt 0) { return [pscustomobject]@{ IsValid = $false; Errors = @($errors) } }
    if ([int]$Definition.schemaVersion -ne 1) { $errors.Add('Unsupported tool-definition schemaVersion.') }
    if ([string]$Definition.toolId -notmatch '^grid\.tool\.[a-z0-9]+(?:[.-][a-z0-9]+)*$') { $errors.Add("Invalid toolId '$($Definition.toolId)'.") }
    if ([string]$Definition.gameId -notmatch '^[a-z0-9]+(?:[.-][a-z0-9]+)*$') { $errors.Add('Invalid gameId.') }
    if ([string]$Definition.adapter -notmatch '^[A-Za-z][A-Za-z0-9-]+$') { $errors.Add('Invalid adapter identity.') }
    if ([string]$Definition.observationMode -notin @('InProcessRead','ExistingOutputRead','ExplicitProcessLaunch')) { $errors.Add('Invalid observationMode.') }
    if (@($Definition.rootCapabilityIds).Count -eq 0) { $errors.Add('At least one rootCapabilityId is required.') }
    foreach ($id in @($Definition.rootCapabilityIds)) { if ([string]$id -notmatch '^grid\.[a-z0-9]+(?:[.-][a-z0-9]+)*$') { $errors.Add("Invalid rootCapabilityId '$id'.") } }
    foreach ($id in @($Definition.supportedClassIds)) { if ([string]$id -notmatch '^grid\.class\.[a-z0-9]+(?:[.-][a-z0-9]+)*$') { $errors.Add("Invalid supported classId '$id'.") } }
    foreach ($field in @('timeoutSeconds','maximumOutputBytes')) { if ([long]$Definition.limits.$field -lt 1) { $errors.Add("Tool limit '$field' must be positive.") } }
    [pscustomobject]@{ IsValid = ($errors.Count -eq 0); Errors = @($errors) }
}

function Get-GridToolRegistry {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ScriptsRoot)
    $definitions = New-Object Collections.Generic.List[object]
    $seen = @{}
    foreach ($path in @(Get-ChildItem -LiteralPath (Join-Path $ScriptsRoot 'games') -Filter 'tool-registry.v1.json' -File -Recurse -ErrorAction SilentlyContinue | Sort-Object FullName)) {
        try { $document = Get-Content -LiteralPath $path.FullName -Raw | ConvertFrom-Json -ErrorAction Stop }
        catch { throw "ToolRegistryUnreadable: '$($path.FullName)'. $($_.Exception.Message)" }
        if ([int]$document.schemaVersion -ne 1) { throw "ToolRegistryInvalid: '$($path.FullName)' has an unsupported schemaVersion." }
        foreach ($definition in @($document.tools)) {
            $validation = Test-GridToolDefinition -Definition $definition
            if (-not $validation.IsValid) { throw "ToolRegistryInvalid: '$($path.FullName)'. $($validation.Errors -join ' ')" }
            $key = ([string]$definition.toolId).ToLowerInvariant()
            if ($seen.ContainsKey($key)) { throw "DuplicateToolId: '$($definition.toolId)'." }
            $seen[$key] = $true
            $definition | Add-Member -NotePropertyName registryPath -NotePropertyValue $path.FullName -Force
            $definitions.Add($definition)
        }
    }
    @($definitions.ToArray() | Sort-Object toolId)
}

function Resolve-GridToolEvidencePlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$ToolIds,
        [Parameter(Mandatory)][string]$GameId,
        [Parameter(Mandatory)][string]$ClassId,
        [Parameter(Mandatory)][string]$ScriptsRoot
    )
    $selected = @(Get-GridCanonicalStringArray -Value $ToolIds -MaximumCount 64 | ForEach-Object { $_.ToLowerInvariant() })
    $tools = @(Get-GridToolRegistry -ScriptsRoot $ScriptsRoot)
    $capabilities = @(Get-GridCapabilityRegistry -ScriptsRoot $ScriptsRoot)
    $rows = New-Object Collections.Generic.List[object]
    foreach ($id in $selected) {
        $match = @($tools | Where-Object { [string]$_.toolId -ieq $id })
        if ($match.Count -ne 1) {
            $rows.Add([pscustomobject][ordered]@{ toolId = $id; availability = 'Unavailable'; reason = 'Tool is not present in the validated registry.'; order = [int]::MaxValue; definition = $null })
            continue
        }
        $definition = $match[0]
        $availability = 'Available'; $reason = $null; $order = [int]::MaxValue
        if ([string]$definition.gameId -ine $GameId) { $availability = 'UnsupportedForGame'; $reason = "Tool does not support game '$GameId'." }
        elseif ([string]$ClassId -notin @($definition.supportedClassIds)) { $availability = 'UnsupportedForClass'; $reason = "Tool is not registered for Class '$ClassId'." }
        else {
            try {
                $closure = @(Get-GridCapabilityDependencyClosure -Registry $capabilities -CapabilityId @($definition.rootCapabilityIds))
                $order = [Array]::IndexOf(@($capabilities | ForEach-Object capabilityId), [string]$definition.rootCapabilityIds[0])
                # Dependency closure is already topologically ordered. The root's
                # dependency depth provides a stable cross-tool execution rank.
                $order = $closure.Count
            } catch { $availability = 'Misconfigured'; $reason = $_.Exception.Message }
        }
        $rows.Add([pscustomobject][ordered]@{ toolId = [string]$definition.toolId; availability = $availability; reason = $reason; order = $order; definition = $definition })
    }
    @($rows.ToArray() | Sort-Object order, toolId)
}

function Invoke-GridToolEvidenceOrchestration {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Envelope,
        [Parameter(Mandatory)]$RequestPlan,
        [Parameter(Mandatory)][string]$ScriptsRoot,
        [Parameter(Mandatory)][scriptblock]$Executor,
        [scriptblock]$AvailabilityResolver,
        [object[]]$ExistingReceipts = @(),
        [Parameter(Mandatory)]$BindingContext,
        [scriptblock]$ReceiptSink
    )
    $validation = Test-GridRequestEnvelope -Envelope $Envelope
    foreach ($field in @('workspaceId','requestId','submissionId','envelopeSha256','planSha256','authorizationGrantId','authorizationBindingSha256','toolInputs')) {
        if ($null -eq $BindingContext.PSObject.Properties[$field]) { throw "ToolBindingContextInvalid: missing $field." }
    }
    if ([string]$BindingContext.requestId -cne [string]$Envelope.requestId -or [string]$BindingContext.envelopeSha256 -cne [string]$Envelope.envelopeSha256) { throw 'ToolBindingContextMismatch: request or envelope changed.' }
    if ([string]$BindingContext.planSha256 -cne (Get-GridCanonicalJsonSha256 -InputObject $RequestPlan)) { throw 'ToolBindingContextMismatch: request plan changed.' }
    if (-not $validation.IsValid) { throw ('RequestEnvelopeInvalid: ' + ($validation.Errors -join ' ')) }
    if ([string]$RequestPlan.requestId -cne [string]$Envelope.requestId) { throw 'ToolRunPlanMismatch: request plan is not bound to the envelope.' }
    $toolIds = @($Envelope.selections.tools | ForEach-Object { [string]$_.toolId })
    $planned = @(Resolve-GridToolEvidencePlan -ToolIds $toolIds -GameId ([string]$Envelope.context.gameId) -ClassId ([string]$Envelope.class.classId) -ScriptsRoot $ScriptsRoot)
    $receipts = New-Object Collections.Generic.List[object]
    foreach ($item in $planned) {
        $capabilityRegistry = @(Get-GridCapabilityRegistry -ScriptsRoot $ScriptsRoot)
        $rootCapabilityId = if ($item.definition -and @($item.definition.rootCapabilityIds).Count -gt 0) { [string]$item.definition.rootCapabilityIds[0] } else { 'grid.health.tool-evidence.orchestrate' }
        $capabilityMatch = @($capabilityRegistry | Where-Object { [string]$_.capabilityId -ceq $rootCapabilityId })
        $capabilityVersion = if ($capabilityMatch.Count -eq 1) { [string]$capabilityMatch[0].capabilityVersion } else { 'unknown' }
        $adapterId = if ($item.definition) { [string]$item.definition.adapter } else { $null }
        $adapterVersion = if ($item.definition) { [string]$item.definition.schemaVersion } else { $null }
        $toolInput = [pscustomobject]@{}
        if ($BindingContext.toolInputs -is [Collections.IDictionary]) { if ($BindingContext.toolInputs.Contains([string]$item.toolId)) { $toolInput = $BindingContext.toolInputs[[string]$item.toolId] } }
        else { $toolInputProperty = $BindingContext.toolInputs.PSObject.Properties[[string]$item.toolId]; if ($null -ne $toolInputProperty) { $toolInput = $toolInputProperty.Value } }
        $normalizedInputSha256 = Get-GridCanonicalJsonSha256 -InputObject $toolInput
        $existing = @($ExistingReceipts | Where-Object { [string]$_.toolId -ceq [string]$item.toolId })
        if ($existing.Count -gt 1) { throw "DuplicateCheckpointReceipt: '$($item.toolId)'." }
        if ($existing.Count -eq 1) {
            $checkpoint = $existing[0]
            if ([int]$checkpoint.schemaVersion -ne 2) { throw "CheckpointReceiptLegacyNotResumable: '$($item.toolId)' historical receipts are readable but cannot authorize resume." }
            $unsignedCheckpoint = $checkpoint | Select-Object * -ExcludeProperty receiptSha256
            if ([string]$checkpoint.receiptSha256 -cne (Get-GridCanonicalJsonSha256 -InputObject $unsignedCheckpoint)) { throw "CheckpointReceiptInvalid: '$($item.toolId)' hash mismatch." }
            foreach ($pair in @(
                @('workspaceId',[string]$BindingContext.workspaceId), @('requestId',[string]$BindingContext.requestId), @('submissionId',[string]$BindingContext.submissionId),
                @('envelopeSha256',[string]$BindingContext.envelopeSha256), @('planSha256',[string]$BindingContext.planSha256), @('authorizationGrantId',[string]$BindingContext.authorizationGrantId),
                @('authorizationBindingSha256',[string]$BindingContext.authorizationBindingSha256), @('capabilityId',$rootCapabilityId), @('capabilityVersion',$capabilityVersion),
                @('adapterId',$adapterId), @('adapterVersion',$adapterVersion), @('normalizedInputSha256',$normalizedInputSha256))) {
                if ([string]$checkpoint.($pair[0]) -cne [string]$pair[1]) { throw "CheckpointReceiptBindingMismatch: '$($item.toolId)' $($pair[0]) differs from the current workspace or executable contract." }
            }
            $checkpointOutputSha256 = Get-GridCanonicalJsonSha256 -InputObject ([pscustomobject][ordered]@{ status=[string]$checkpoint.status; exitCode=$checkpoint.exitCode; reason=$checkpoint.reason; stdout=[string]$checkpoint.stdout; stderr=[string]$checkpoint.stderr })
            $checkpointEvidenceSha256 = Get-GridToolEvidenceSha256 -Evidence @($checkpoint.evidence)
            if ([string]$checkpoint.outputSha256 -cne $checkpointOutputSha256 -or [string]$checkpoint.evidenceSha256 -cne $checkpointEvidenceSha256) { throw "CheckpointReceiptContentBindingMismatch: '$($item.toolId)' output or evidence digest is invalid." }
            $receipts.Add($checkpoint)
            if ($ReceiptSink) { & $ReceiptSink $checkpoint | Out-Null }
            continue
        }
        $started = [DateTimeOffset]::UtcNow
        $availability = [string]$item.availability; $reason = [string]$item.reason
        if ($availability -eq 'Available' -and $AvailabilityResolver) {
            try {
                $resolved = & $AvailabilityResolver $item.definition $Envelope
                if ([string]$resolved.status -notin $script:GridToolDefinitionStates) { throw "Unsupported availability status '$($resolved.status)'." }
                $availability = [string]$resolved.status; $reason = [string]$resolved.reason
            } catch { $availability = 'Misconfigured'; $reason = $_.Exception.Message }
        }
        $status = 'Unavailable'; $evidence = @(); $stdout = ''; $stderr = ''; $exitCode = $null
        if ($availability -eq 'Available') {
            try {
                $result = & $Executor $item.definition $Envelope
                $status = [string]$result.status
                if ($status -notin $script:GridToolRunStates) { throw "Unsupported tool result '$status'." }
                $evidence = @($result.evidence); $stdout = [string]$result.stdout; $stderr = [string]$result.stderr; $exitCode = $result.exitCode; $reason = [string]$result.reason
                $outputBytes = [Text.Encoding]::UTF8.GetByteCount($stdout) + [Text.Encoding]::UTF8.GetByteCount($stderr) +
                    [Text.Encoding]::UTF8.GetByteCount((@($evidence) | ConvertTo-Json -Depth 30 -Compress))
                if ($outputBytes -gt [long]$item.definition.limits.maximumOutputBytes) {
                    throw "ToolOutputLimitExceeded: '$($item.toolId)' exceeded $($item.definition.limits.maximumOutputBytes) bytes."
                }
                if (([DateTimeOffset]::UtcNow - $started).TotalSeconds -gt [int]$item.definition.limits.timeoutSeconds) {
                    throw "ToolTimeout: '$($item.toolId)' exceeded $($item.definition.limits.timeoutSeconds) seconds."
                }
            } catch {
                # A failed bound check must not leak the rejected payload into
                # the public receipt. Preserve only the bounded failure fact.
                $status = 'Failed'
                $evidence = @()
                $stdout = ''
                $exitCode = $null
                $stderr = $_.Exception.Message
                $reason = $_.Exception.Message
            }
        }
        $completed = [DateTimeOffset]::UtcNow
        $evidenceSha256 = Get-GridToolEvidenceSha256 -Evidence @($evidence)
        $outputSha256 = Get-GridCanonicalJsonSha256 -InputObject ([pscustomobject][ordered]@{ status=$status; exitCode=$exitCode; reason=$reason; stdout=$stdout; stderr=$stderr })
        $receipt = [pscustomobject][ordered]@{
            schemaVersion = 2; toolId = [string]$item.toolId; availability = $availability; status = $status
            workspaceId = [string]$BindingContext.workspaceId; requestId = [string]$BindingContext.requestId; submissionId = [string]$BindingContext.submissionId
            envelopeSha256 = [string]$BindingContext.envelopeSha256; planSha256 = [string]$BindingContext.planSha256
            authorizationGrantId = [string]$BindingContext.authorizationGrantId; authorizationBindingSha256 = [string]$BindingContext.authorizationBindingSha256
            capabilityId = $rootCapabilityId; capabilityVersion = $capabilityVersion; adapterId = $adapterId; adapterVersion = $adapterVersion
            normalizedInputSha256 = $normalizedInputSha256; outputSha256 = $outputSha256; evidenceSha256 = $evidenceSha256
            startedAt = $started.ToString('o'); completedAt = $completed.ToString('o'); durationMilliseconds = [long]($completed - $started).TotalMilliseconds
            exitCode = $exitCode; reason = $reason; stdout = $stdout; stderr = $stderr; evidence = @($evidence)
            receiptSha256 = ''
        }
        $receipt.receiptSha256 = Get-GridCanonicalJsonSha256 -InputObject ($receipt | Select-Object * -ExcludeProperty receiptSha256)
        $receipts.Add($receipt)
        if ($ReceiptSink) { & $ReceiptSink $receipt | Out-Null }
    }
    $collected = @($receipts | Where-Object status -eq 'Collected').Count
    $failed = @($receipts | Where-Object status -eq 'Failed').Count
    $terminal = if ($receipts.Count -eq 0) { 'EvidenceUnavailable' } elseif ($collected -eq $receipts.Count) { 'EvidenceComplete' } elseif ($collected -gt 0) { 'EvidencePartial' } elseif ($failed -gt 0) { 'EvidenceFailed' } else { 'EvidenceUnavailable' }
    $runUnsigned = [pscustomobject][ordered]@{
        schemaVersion = 2; workspaceId = [string]$BindingContext.workspaceId; requestId = [string]$Envelope.requestId; submissionId = [string]$BindingContext.submissionId
        envelopeSha256 = [string]$BindingContext.envelopeSha256; planSha256 = [string]$BindingContext.planSha256; authorizationGrantId = [string]$BindingContext.authorizationGrantId
        authorizationBindingSha256 = [string]$BindingContext.authorizationBindingSha256; terminalState = $terminal; mutationAuthorized = $false; receiptSha256s = @($receipts | ForEach-Object receiptSha256)
    }
    [pscustomobject][ordered]@{
        schemaVersion = 2; workspaceId = $runUnsigned.workspaceId; requestId = $runUnsigned.requestId; submissionId = $runUnsigned.submissionId
        envelopeSha256 = $runUnsigned.envelopeSha256; planSha256 = $runUnsigned.planSha256; authorizationGrantId = $runUnsigned.authorizationGrantId; authorizationBindingSha256 = $runUnsigned.authorizationBindingSha256
        terminalState = $terminal; mutationAuthorized = $false; toolReceipts = $receipts.ToArray(); runSha256 = Get-GridCanonicalJsonSha256 -InputObject $runUnsigned
    }
}
