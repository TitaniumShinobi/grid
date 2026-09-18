#requires -Version 5.1
<#
.SYNOPSIS
Compiles structured Grid intake into a deterministic capability plan.
.DESCRIPTION
The request envelope is a public, evidence-safe boundary. It preserves exact
game, profile, mod-provider, tool, Class, and claim selections without mining
plain language for technical targets. Class recipes may select only capability
roots already present in validated production manifests.
#>

$script:GridRequestEnvelopeSchemaVersion = 1
$script:GridClassRecipeSchemaVersion = 1
$script:GridRequestProvenanceSources = @('UserSelected', 'ContextInherited', 'DetectedRunning', 'ProductionManifest')
$script:GridClassCoverageStates = @('Registered', 'Unsupported')
$script:GridClassPipelineStages = @('diagnose', 'propose', 'execute', 'verify')
$script:GridClassEvidenceFields = @('providerSeeds', 'candidatePlugins', 'observedForms', 'locations', 'claims.text', 'evidenceReferences')

function Get-GridCanonicalStringArray {
    [CmdletBinding()]
    param([string[]]$Value = @(), [ValidateRange(0, 65536)][int]$MaximumCount = 4096)

    $values = @($Value | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | ForEach-Object { ([string]$_).Trim() })
    if ($values.Count -gt $MaximumCount) { throw "SelectionLimitExceeded: maximum $MaximumCount values." }
    $seen = @{}
    foreach ($item in $values) {
        if ($item.Length -gt 512) { throw 'SelectionValueTooLong: selection values may not exceed 512 characters.' }
        $key = $item.ToLowerInvariant()
        if ($seen.ContainsKey($key)) { throw "DuplicateSelection: '$item'." }
        $seen[$key] = $true
    }
    @($values | Sort-Object)
}

function Get-GridCanonicalJsonSha256 {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$InputObject)

    $json = ConvertTo-GridCanonicalJsonValue -Value $InputObject | ConvertTo-Json -Depth 50 -Compress
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($json)))).Replace('-', '') }
    finally { $algorithm.Dispose() }
}

function Get-GridRequestEnvelopePortableDigest {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$UnsignedEnvelope)
    $encoding = [Text.UTF8Encoding]::new($false)
    $fields = [Collections.Generic.List[string]]::new()
    function Add-GridRequestDigestField([string]$Name, $Value) {
        $text = if ($null -eq $Value) { $null } else { [string]$Value }
        $encoded = if ($null -eq $text) { '-' } else { 's' + [Convert]::ToBase64String($encoding.GetBytes($text)) }
        [void]$fields.Add($Name + '|' + $encoded)
    }
    Add-GridRequestDigestField 'digestAlgorithm' 'grid.request-envelope.portable-v1'
    Add-GridRequestDigestField 'schemaVersion' ([int]$UnsignedEnvelope.schemaVersion)
    Add-GridRequestDigestField 'mode' ([string]$UnsignedEnvelope.mode)
    Add-GridRequestDigestField 'context.gameId' ([string]$UnsignedEnvelope.context.gameId)
    Add-GridRequestDigestField 'context.installationId' $UnsignedEnvelope.context.installationId
    Add-GridRequestDigestField 'context.profileId' $UnsignedEnvelope.context.profileId
    Add-GridRequestDigestField 'class.classId' ([string]$UnsignedEnvelope.class.classId)
    Add-GridRequestDigestField 'class.recipeVersion' ([string]$UnsignedEnvelope.class.recipeVersion)
    $mods = @($UnsignedEnvelope.selections.mods)
    Add-GridRequestDigestField 'mods.count' $mods.Count
    foreach ($mod in $mods) {
        Add-GridRequestDigestField 'mod.providerName' ([string]$mod.providerName)
        Add-GridRequestDigestField 'mod.selectionRole' ([string]$mod.selectionRole)
    }
    $tools = @($UnsignedEnvelope.selections.tools)
    Add-GridRequestDigestField 'tools.count' $tools.Count
    foreach ($tool in $tools) {
        Add-GridRequestDigestField 'tool.toolId' ([string]$tool.toolId)
        Add-GridRequestDigestField 'tool.selectionSource' ([string]$tool.selectionSource)
    }
    Add-GridRequestDigestField 'claims.text' ([string]$UnsignedEnvelope.claims.text)
    $provenance = @($UnsignedEnvelope.provenance)
    Add-GridRequestDigestField 'provenance.count' $provenance.Count
    foreach ($entry in $provenance) {
        Add-GridRequestDigestField 'provenance.field' ([string]$entry.field)
        Add-GridRequestDigestField 'provenance.source' ([string]$entry.source)
        Add-GridRequestDigestField 'provenance.confidence' ([double]$entry.confidence).ToString('R', [Globalization.CultureInfo]::InvariantCulture)
        Add-GridRequestDigestField 'provenance.note' ([string]$entry.note)
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash($encoding.GetBytes([string]::Join("`n", $fields))))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function ConvertTo-GridCanonicalJsonValue {
    [CmdletBinding()]
    param($Value)
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [ValueType]) { return $Value }
    if ($Value -is [Collections.IDictionary]) {
        $ordered = [ordered]@{}
        foreach ($key in @($Value.Keys | ForEach-Object { [string]$_ } | Sort-Object)) { $ordered[$key] = ConvertTo-GridCanonicalJsonValue -Value $Value[$key] }
        return $ordered
    }
    if ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) {
        return @($Value | ForEach-Object { ConvertTo-GridCanonicalJsonValue -Value $_ })
    }
    $object = [ordered]@{}
    foreach ($property in @($Value.PSObject.Properties | Sort-Object Name)) { $object[$property.Name] = ConvertTo-GridCanonicalJsonValue -Value $property.Value }
    $object
}

function New-GridRequestEnvelope {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$GameId,
        [string]$InstallationId,
        [string]$ProfileId,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ClassId,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ClassRecipeVersion,
        [string[]]$ModNames = @(),
        [string[]]$ToolIds = @(),
        [string[]]$CapabilityIds = @(),
        [AllowEmptyString()][string]$PlainText = '',
        [ValidateSet('UserSelected', 'ContextInherited')][string]$ContextSource = 'UserSelected',
        [ValidateSet('UserSelected', 'ContextInherited', 'DetectedRunning')][string]$ToolSelectionSource = 'UserSelected'
    )

    $game = $GameId.Trim().ToLowerInvariant()
    $class = $ClassId.Trim().ToLowerInvariant()
    if ($game -notmatch '^[a-z0-9]+(?:[.-][a-z0-9]+)*$') { throw "RequestEnvelopeInvalid: invalid gameId '$GameId'." }
    if ($class -notmatch '^grid\.class\.[a-z0-9]+(?:[.-][a-z0-9]+)*$') { throw "RequestEnvelopeInvalid: invalid classId '$ClassId'." }
    if ($ClassRecipeVersion -notmatch '^\d+\.\d+\.\d+$') { throw "RequestEnvelopeInvalid: invalid Class recipeVersion '$ClassRecipeVersion'." }
    if ($PlainText.Length -gt 1MB) { throw 'RequestEnvelopeInvalid: plain text exceeds 1 MiB.' }

    $mods = @(Get-GridCanonicalStringArray -Value $ModNames -MaximumCount 4096)
    $tools = @(Get-GridCanonicalStringArray -Value $ToolIds -MaximumCount 64 | ForEach-Object {
        $id = $_.ToLowerInvariant()
        if ($id -notmatch '^grid\.tool\.[a-z0-9]+(?:[.-][a-z0-9]+)*$') { throw "RequestEnvelopeInvalid: invalid toolId '$id'." }
        $id
    })
    $capabilities = @(Get-GridCanonicalStringArray -Value $CapabilityIds -MaximumCount 1 | ForEach-Object {
        $id = $_.ToLowerInvariant()
        if ($id -notmatch '^grid\.capability\.[a-z0-9]+(?:[.-][a-z0-9]+)*$') { throw "RequestEnvelopeInvalid: invalid gameplay capabilityId '$id'." }
        $id
    })
    $provenance = New-Object Collections.Generic.List[object]
    $provenance.Add([pscustomobject][ordered]@{ field = 'context.gameId'; source = $ContextSource; confidence = 1.0; note = 'Exact selected game adapter identity.' })
    if (-not [string]::IsNullOrWhiteSpace($InstallationId)) { $provenance.Add([pscustomobject][ordered]@{ field = 'context.installationId'; source = $ContextSource; confidence = 1.0; note = 'Exact selected installation identity.' }) }
    if (-not [string]::IsNullOrWhiteSpace($ProfileId)) { $provenance.Add([pscustomobject][ordered]@{ field = 'context.profileId'; source = $ContextSource; confidence = 1.0; note = 'Exact selected profile identity.' }) }
    $provenance.Add([pscustomobject][ordered]@{ field = 'class.classId'; source = 'UserSelected'; confidence = 1.0; note = 'Explicit Class selection; never inferred from claims.' })

    $modSelections = New-Object Collections.Generic.List[object]
    foreach ($name in $mods) {
        $modSelections.Add([pscustomobject][ordered]@{ providerName = $name; selectionRole = 'Subject' })
        $provenance.Add([pscustomobject][ordered]@{ field = "selections.mods[$($modSelections.Count - 1)]"; source = 'UserSelected'; confidence = 1.0; note = 'Exact MO2 provider-directory selection; not a plugin identity.' })
    }
    $toolSelections = New-Object Collections.Generic.List[object]
    foreach ($id in $tools) {
        $toolSelections.Add([pscustomobject][ordered]@{ toolId = $id; selectionSource = $ToolSelectionSource })
        $provenance.Add([pscustomobject][ordered]@{ field = "selections.tools[$($toolSelections.Count - 1)]"; source = $ToolSelectionSource; confidence = 1.0; note = 'Exact selected tool identity; selection grants no launch authority.' })
    }
    $capabilitySelections = New-Object Collections.Generic.List[object]
    foreach ($id in $capabilities) {
        $capabilitySelections.Add([pscustomobject][ordered]@{ capabilityId = $id; selectionSource = 'UserSelected' })
        $provenance.Add([pscustomobject][ordered]@{ field = "selections.capabilities[$($capabilitySelections.Count - 1)]"; source = 'UserSelected'; confidence = 1.0; note = 'Exact gameplay capability selection; never inferred from claims.' })
    }
    if (-not [string]::IsNullOrEmpty($PlainText)) { $provenance.Add([pscustomobject][ordered]@{ field = 'claims.text'; source = 'UserSelected'; confidence = 1.0; note = 'Verbatim claim text; not parsed into technical evidence.' }) }

    # Normalize numeric provenance fields before hashing. PowerShell's decimal
    # literal representation can serialize as 1.0 while the same JSON value is
    # read back and re-emitted as 1, which used to invalidate a fresh envelope.
    $canonicalProvenance = @($provenance.ToArray() | ForEach-Object {
        [pscustomobject][ordered]@{ field = [string]$_.field; source = [string]$_.source; confidence = [double]$_.confidence; note = [string]$_.note }
    })
    $unsigned = [pscustomobject][ordered]@{
        schemaVersion = $script:GridRequestEnvelopeSchemaVersion
        mode = 'Game'
        context = [pscustomobject][ordered]@{ gameId = $game; installationId = if ([string]::IsNullOrWhiteSpace($InstallationId)) { $null } else { $InstallationId }; profileId = if ([string]::IsNullOrWhiteSpace($ProfileId)) { $null } else { $ProfileId } }
        class = [pscustomobject][ordered]@{ classId = $class; recipeVersion = $ClassRecipeVersion }
        selections = [pscustomobject][ordered]@{ mods = $modSelections.ToArray(); tools = $toolSelections.ToArray(); capabilities = $capabilitySelections.ToArray() }
        claims = [pscustomobject][ordered]@{ text = $PlainText }
        provenance = $canonicalProvenance
    }
    $digest = Get-GridRequestEnvelopePortableDigest -UnsignedEnvelope $unsigned
    [pscustomobject][ordered]@{
        schemaVersion = $unsigned.schemaVersion; digestAlgorithm = 'grid.request-envelope.portable-v1'; requestId = ('request-' + $digest.Substring(0, 24).ToLowerInvariant())
        submittedAt = [DateTimeOffset]::UtcNow.ToString('o'); mode = $unsigned.mode; context = $unsigned.context
        class = $unsigned.class; selections = $unsigned.selections; claims = $unsigned.claims
        provenance = $canonicalProvenance; envelopeSha256 = $digest
    }
}

function Test-GridRequestEnvelope {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Envelope)

    $errors = New-Object Collections.Generic.List[string]
    $allowedTopLevel = @('schemaVersion','digestAlgorithm','requestId','submittedAt','mode','context','class','selections','claims','provenance','envelopeSha256')
    foreach ($property in @($Envelope.PSObject.Properties.Name)) { if ($property -notin $allowedTopLevel) { $errors.Add("Unsupported request-envelope field: $property") } }
    foreach ($field in @('schemaVersion','requestId','submittedAt','mode','context','class','selections','claims','provenance','envelopeSha256')) {
        if ($null -eq $Envelope.PSObject.Properties[$field]) { $errors.Add("Missing request-envelope field: $field") }
    }
    if ($errors.Count -gt 0) { return [pscustomobject]@{ IsValid = $false; Errors = @($errors) } }
    if ([int]$Envelope.schemaVersion -ne 1) { $errors.Add('Unsupported request-envelope schemaVersion.') }
    if ([string]$Envelope.requestId -notmatch '^request-[a-f0-9]{24}$') { $errors.Add('Invalid requestId shape.') }
    $parsedSubmittedAt = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse([string]$Envelope.submittedAt, [ref]$parsedSubmittedAt)) { $errors.Add('Invalid submittedAt timestamp.') }
    if ([string]$Envelope.envelopeSha256 -notmatch '^[A-F0-9]{64}$') { $errors.Add('Invalid envelopeSha256 shape.') }
    if ([string]$Envelope.mode -cne 'Game') { $errors.Add("Unsupported request mode '$($Envelope.mode)'.") }
    if ([string]$Envelope.context.gameId -notmatch '^[a-z0-9]+(?:[.-][a-z0-9]+)*$') { $errors.Add('Invalid request gameId.') }
    if ([string]$Envelope.class.classId -notmatch '^grid\.class\.[a-z0-9]+(?:[.-][a-z0-9]+)*$') { $errors.Add('Invalid request classId.') }
    if ([string]$Envelope.class.recipeVersion -notmatch '^\d+\.\d+\.\d+$') { $errors.Add('Invalid request Class recipeVersion.') }
    foreach ($pair in @(
        [pscustomobject]@{ Object = $Envelope.context; Allowed = @('gameId','installationId','profileId'); Name = 'context' },
        [pscustomobject]@{ Object = $Envelope.class; Allowed = @('classId','recipeVersion'); Name = 'class' },
        [pscustomobject]@{ Object = $Envelope.selections; Allowed = @('mods','tools','capabilities'); Name = 'selections' },
        [pscustomobject]@{ Object = $Envelope.claims; Allowed = @('text'); Name = 'claims' }
    )) {
        foreach ($property in @($pair.Object.PSObject.Properties.Name)) { if ($property -notin $pair.Allowed) { $errors.Add("Unsupported $($pair.Name) field: $property") } }
    }
    $seenMods = @{}
    foreach ($mod in @($Envelope.selections.mods)) {
        foreach ($property in @($mod.PSObject.Properties.Name)) { if ($property -notin @('providerName','selectionRole')) { $errors.Add("Unsupported mod-selection field: $property") } }
        $name = [string]$mod.providerName
        if ([string]::IsNullOrWhiteSpace($name) -or $name.Length -gt 512 -or [string]$mod.selectionRole -notin @('Subject','Context')) { $errors.Add('Invalid mod selection.') ; continue }
        $key = $name.ToLowerInvariant(); if ($seenMods.ContainsKey($key)) { $errors.Add("Duplicate mod selection '$name'.") } else { $seenMods[$key] = $true }
    }
    $seenTools = @{}
    foreach ($tool in @($Envelope.selections.tools)) {
        foreach ($property in @($tool.PSObject.Properties.Name)) { if ($property -notin @('toolId','selectionSource')) { $errors.Add("Unsupported tool-selection field: $property") } }
        $id = [string]$tool.toolId
        if ($id -notmatch '^grid\.tool\.[a-z0-9]+(?:[.-][a-z0-9]+)*$' -or [string]$tool.selectionSource -notin @('UserSelected','DetectedRunning','ContextInherited')) { $errors.Add("Invalid tool selection '$id'.") ; continue }
        $key = $id.ToLowerInvariant(); if ($seenTools.ContainsKey($key)) { $errors.Add("Duplicate tool selection '$id'.") } else { $seenTools[$key] = $true }
    }
    $seenCapabilities = @{}
    foreach ($capability in @(if ($Envelope.selections.PSObject.Properties['capabilities']) { $Envelope.selections.capabilities } else { @() })) {
        foreach ($property in @($capability.PSObject.Properties.Name)) { if ($property -notin @('capabilityId','selectionSource')) { $errors.Add("Unsupported capability-selection field: $property") } }
        $id = [string]$capability.capabilityId
        if ($id -notmatch '^grid\.capability\.[a-z0-9]+(?:[.-][a-z0-9]+)*$' -or [string]$capability.selectionSource -cne 'UserSelected') { $errors.Add("Invalid gameplay capability selection '$id'.") ; continue }
        $key = $id.ToLowerInvariant(); if ($seenCapabilities.ContainsKey($key)) { $errors.Add("Duplicate gameplay capability selection '$id'.") } else { $seenCapabilities[$key] = $true }
    }
    if ($seenCapabilities.Count -gt 1) { $errors.Add('At most one gameplay capability may be selected per request.') }
    foreach ($entry in @($Envelope.provenance)) {
        foreach ($property in @($entry.PSObject.Properties.Name)) { if ($property -notin @('field','source','confidence','note')) { $errors.Add("Unsupported provenance field: $property") } }
        if ([string]$entry.source -notin $script:GridRequestProvenanceSources -or [double]$entry.confidence -lt 0 -or [double]$entry.confidence -gt 1) { $errors.Add("Invalid provenance for '$($entry.field)'.") }
    }
    if (([string]$Envelope.claims.text).Length -gt 1MB) { $errors.Add('Request claim text exceeds 1 MiB.') }
    $canonicalMods = @($Envelope.selections.mods | ForEach-Object { [pscustomobject][ordered]@{ providerName = [string]$_.providerName; selectionRole = [string]$_.selectionRole } })
    $canonicalTools = @($Envelope.selections.tools | ForEach-Object { [pscustomobject][ordered]@{ toolId = [string]$_.toolId; selectionSource = [string]$_.selectionSource } })
    $canonicalProvenance = @($Envelope.provenance | ForEach-Object { [pscustomobject][ordered]@{ field = [string]$_.field; source = [string]$_.source; confidence = [double]$_.confidence; note = [string]$_.note } })
    $canonicalSelections = [pscustomobject][ordered]@{ mods = $canonicalMods; tools = $canonicalTools }
    if ($Envelope.selections.PSObject.Properties['capabilities']) {
        $canonicalSelections | Add-Member -NotePropertyName capabilities -NotePropertyValue @($Envelope.selections.capabilities | ForEach-Object { [pscustomobject][ordered]@{ capabilityId = [string]$_.capabilityId; selectionSource = [string]$_.selectionSource } })
    }
    $unsigned = [pscustomobject][ordered]@{
        schemaVersion = [int]$Envelope.schemaVersion; mode = [string]$Envelope.mode
        context = [pscustomobject][ordered]@{ gameId = [string]$Envelope.context.gameId; installationId = $Envelope.context.installationId; profileId = $Envelope.context.profileId }
        class = [pscustomobject][ordered]@{ classId = [string]$Envelope.class.classId; recipeVersion = [string]$Envelope.class.recipeVersion }
        selections = $canonicalSelections
        claims = [pscustomobject][ordered]@{ text = [string]$Envelope.claims.text }
        provenance = $canonicalProvenance
    }
    $portable = $Envelope.PSObject.Properties['digestAlgorithm']
    if ($portable -and [string]$Envelope.digestAlgorithm -cne 'grid.request-envelope.portable-v1') { $errors.Add("Unsupported request digestAlgorithm '$($Envelope.digestAlgorithm)'.") }
    $expectedDigest = if ($portable) { Get-GridRequestEnvelopePortableDigest -UnsignedEnvelope $unsigned } else { Get-GridCanonicalJsonSha256 -InputObject $unsigned }
    if ([string]$Envelope.envelopeSha256 -cne $expectedDigest) { $errors.Add('RequestEnvelopeDigestMismatch: envelope contents changed after materialization.') }
    if ([string]$Envelope.requestId -cne ('request-' + $expectedDigest.Substring(0, 24).ToLowerInvariant())) { $errors.Add('RequestEnvelopeIdMismatch: requestId is not bound to the envelope digest.') }
    [pscustomobject]@{ IsValid = ($errors.Count -eq 0); Errors = @($errors) }
}

function Test-GridClassRecipe {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Recipe, [string]$DirectoryName)

    $errors = New-Object Collections.Generic.List[string]
    $allowedRecipeFields = @('schemaVersion','classId','recipeVersion','displayName','iconId','coverage','supportedGames','selectionPolicy','evidencePolicy','gameplayCapabilities','pipelines','manifestPath')
    foreach ($property in @($Recipe.PSObject.Properties.Name)) { if ($property -notin $allowedRecipeFields) { $errors.Add("Unsupported Class recipe field: $property") } }
    foreach ($field in @('schemaVersion','classId','recipeVersion','displayName','iconId','coverage','supportedGames','selectionPolicy','evidencePolicy','pipelines')) {
        if ($null -eq $Recipe.PSObject.Properties[$field]) { $errors.Add("Missing Class recipe field: $field") }
    }
    if ($errors.Count -gt 0) { return [pscustomobject]@{ IsValid = $false; Errors = @($errors) } }
    if ([int]$Recipe.schemaVersion -ne 1) { $errors.Add('Unsupported Class recipe schemaVersion.') }
    if ([string]$Recipe.classId -notmatch '^grid\.class\.[a-z0-9]+(?:[.-][a-z0-9]+)*$') { $errors.Add("Invalid classId '$($Recipe.classId)'.") }
    if ([string]$Recipe.recipeVersion -notmatch '^\d+\.\d+\.\d+$') { $errors.Add("Invalid recipeVersion '$($Recipe.recipeVersion)'.") }
    if ([string]::IsNullOrWhiteSpace([string]$Recipe.displayName)) { $errors.Add('Class recipe displayName may not be blank.') }
    if ([string]$Recipe.iconId -notmatch '^grid\.icon\.[a-z0-9]+(?:[.-][a-z0-9]+)*$') { $errors.Add("Invalid iconId '$($Recipe.iconId)'.") }
    if ([string]$Recipe.coverage -notin $script:GridClassCoverageStates) { $errors.Add("Invalid coverage '$($Recipe.coverage)'.") }
    if (@($Recipe.supportedGames).Count -eq 0) { $errors.Add('A Class recipe requires supportedGames.') }
    $seenGames = @{}
    foreach ($game in @($Recipe.supportedGames)) {
        if ([string]$game -notmatch '^[a-z0-9]+(?:[.-][a-z0-9]+)*$') { $errors.Add("Invalid supported game '$game'.") }
        $gameKey = ([string]$game).ToLowerInvariant(); if ($seenGames.ContainsKey($gameKey)) { $errors.Add("Duplicate supported game '$game'.") } else { $seenGames[$gameKey] = $true }
    }
    foreach ($policyName in @('mods','tools')) {
        $policy = $Recipe.selectionPolicy.$policyName
        if ([int]$policy.minimum -lt 0 -or ($null -ne $policy.maximum -and [int]$policy.maximum -lt [int]$policy.minimum)) { $errors.Add("Invalid $policyName selection bounds.") }
    }
    if ($DirectoryName) {
        $leaf = ([string]$Recipe.classId -split '\.')[-1]
        if ($leaf -cne $DirectoryName) { $errors.Add("Class directory '$DirectoryName' does not match classId leaf '$leaf'.") }
    }
    foreach ($toolId in @($Recipe.selectionPolicy.tools.allowedToolIds)) {
        if ([string]$toolId -notmatch '^grid\.tool\.[a-z0-9]+(?:[.-][a-z0-9]+)*$') { $errors.Add("Invalid allowed toolId '$toolId'.") }
    }
    $seenGameplayCapabilities = @{}
    foreach ($capability in @(if ($Recipe.PSObject.Properties['gameplayCapabilities']) { $Recipe.gameplayCapabilities } else { @() })) {
        $id = [string]$capability.capabilityId
        if ($id -notmatch '^grid\.capability\.[a-z0-9]+(?:[.-][a-z0-9]+)*$' -or [string]::IsNullOrWhiteSpace([string]$capability.displayName)) { $errors.Add("Invalid gameplay capability option '$id'."); continue }
        $key = $id.ToLowerInvariant(); if ($seenGameplayCapabilities.ContainsKey($key)) { $errors.Add("Duplicate gameplay capability option '$id'.") } else { $seenGameplayCapabilities[$key] = $true }
        foreach ($property in @($capability.PSObject.Properties.Name)) { if ($property -notin @('capabilityId','displayName','intentPhrases')) { $errors.Add("Unsupported gameplay capability option field: $property") } }
        $intentPhrases = @(if ($capability.PSObject.Properties['intentPhrases']) { $capability.intentPhrases } else { @() })
        if ($intentPhrases.Count -eq 0) {
            $errors.Add("Gameplay capability option '$id' must declare at least one intent phrase.")
        }
        $seenIntentPhrases = @{}
        foreach ($intentPhrase in $intentPhrases) {
            $phrase = [string]$intentPhrase
            if ([string]::IsNullOrWhiteSpace($phrase)) {
                $errors.Add("Gameplay capability option '$id' contains an empty intent phrase.")
                continue
            }
            $phraseKey = $phrase.Trim().ToLowerInvariant()
            if ($seenIntentPhrases.ContainsKey($phraseKey)) {
                $errors.Add("Gameplay capability option '$id' contains duplicate intent phrase '$phrase'.")
            } else {
                $seenIntentPhrases[$phraseKey] = $true
            }
        }
    }
    foreach ($group in @($Recipe.evidencePolicy.requiredAnyOf)) {
        foreach ($field in @($group)) { if ([string]$field -notin $script:GridClassEvidenceFields) { $errors.Add("Unsupported evidence field '$field'.") } }
    }
    foreach ($stage in $script:GridClassPipelineStages) {
        if ($null -eq $Recipe.pipelines.PSObject.Properties[$stage]) { $errors.Add("Missing pipeline stage '$stage'.") }
    }
    $recipeText = $Recipe | ConvertTo-Json -Depth 30 -Compress
    if ($recipeText -match '"[A-Za-z]:[\\/][^"\r\n]+"' -or $recipeText -match '(?i)"[^"\r\n]+\.(esp|esm|esl)"' -or $recipeText -match '(?i)"(?:0x)?[0-9a-f]{8}"') {
        $errors.Add('Class recipes may not contain machine paths, plugin identities, or FormIDs.')
    }
    [pscustomobject]@{ IsValid = ($errors.Count -eq 0); Errors = @($errors) }
}

function Get-GridClassRecipeRegistry {
    [CmdletBinding()]
    param([string]$ClassesRoot = (Join-Path $PSScriptRoot 'classes'))

    if (-not (Test-Path -LiteralPath $ClassesRoot -PathType Container)) { throw "ClassRegistryMissing: $ClassesRoot" }
    $recipes = New-Object Collections.Generic.List[object]; $seen = @{}
    foreach ($file in @(Get-ChildItem -LiteralPath $ClassesRoot -Filter 'class.v1.json' -File -Recurse | Sort-Object FullName)) {
        try { $recipe = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -ErrorAction Stop }
        catch { throw "ClassRecipeUnreadable: '$($file.FullName)'. $($_.Exception.Message)" }
        $validation = Test-GridClassRecipe -Recipe $recipe -DirectoryName $file.Directory.Name
        if (-not $validation.IsValid) { throw "ClassRecipeInvalid: '$($file.FullName)'. $($validation.Errors -join ' ')" }
        $key = ([string]$recipe.classId).ToLowerInvariant()
        if ($seen.ContainsKey($key)) { throw "DuplicateClassId: '$($recipe.classId)'." }
        $seen[$key] = $true
        $recipe | Add-Member -NotePropertyName manifestPath -NotePropertyValue $file.FullName -Force
        $recipes.Add($recipe)
    }
    if ($recipes.Count -ne 32) { throw "ClassRegistryIncomplete: expected 32 Class recipes, found $($recipes.Count)." }
    @($recipes.ToArray() | Sort-Object classId)
}

function Resolve-GridRequestPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Envelope,
        [Parameter(Mandatory)][string]$ScriptsRoot,
        [string]$ClassesRoot = (Join-Path $PSScriptRoot 'classes')
    )

    $validation = Test-GridRequestEnvelope -Envelope $Envelope
    if (-not $validation.IsValid) { throw ('RequestEnvelopeInvalid: ' + ($validation.Errors -join ' ')) }
    $recipes = @(Get-GridClassRecipeRegistry -ClassesRoot $ClassesRoot)
    $recipe = @($recipes | Where-Object { [string]$_.classId -ieq [string]$Envelope.class.classId })
    if ($recipe.Count -ne 1) { throw "ClassNotFound: '$($Envelope.class.classId)'." }
    $recipe = $recipe[0]
    $missing = New-Object Collections.Generic.List[string]
    $gaps = New-Object Collections.Generic.List[string]
    if ([string]$Envelope.class.recipeVersion -cne [string]$recipe.recipeVersion) { $gaps.Add("Class recipe version mismatch: request '$($Envelope.class.recipeVersion)', installed '$($recipe.recipeVersion)'.") }
    if ([string]$Envelope.context.gameId -notin @($recipe.supportedGames)) { $gaps.Add("Class '$($recipe.classId)' does not support game '$($Envelope.context.gameId)'.") }
    if ([string]::IsNullOrWhiteSpace([string]$Envelope.context.installationId)) { $missing.Add('installationId') }
    if ([string]::IsNullOrWhiteSpace([string]$Envelope.context.profileId)) { $missing.Add('profileId') }
    $modCount = @($Envelope.selections.mods).Count; $toolCount = @($Envelope.selections.tools).Count
    $selectedGameplayCapabilities = @(if ($Envelope.selections.PSObject.Properties['capabilities']) { $Envelope.selections.capabilities } else { @() })
    $allowedGameplayCapabilities = @(if ($recipe.PSObject.Properties['gameplayCapabilities']) { $recipe.gameplayCapabilities } else { @() })
    if ($modCount -lt [int]$recipe.selectionPolicy.mods.minimum) { $missing.Add('modSelections') }
    if ($null -ne $recipe.selectionPolicy.mods.maximum -and $modCount -gt [int]$recipe.selectionPolicy.mods.maximum) { $gaps.Add('Selected mod count exceeds the Class recipe maximum.') }
    if ($toolCount -lt [int]$recipe.selectionPolicy.tools.minimum) { $missing.Add('toolSelections') }
    if ($null -ne $recipe.selectionPolicy.tools.maximum -and $toolCount -gt [int]$recipe.selectionPolicy.tools.maximum) { $gaps.Add('Selected tool count exceeds the Class recipe maximum.') }
    $allowedTools = @($recipe.selectionPolicy.tools.allowedToolIds)
    foreach ($tool in @($Envelope.selections.tools)) { if ([string]$tool.toolId -notin $allowedTools) { $gaps.Add("Tool '$($tool.toolId)' is not registered for Class '$($recipe.classId)'.") } }
    if ($allowedGameplayCapabilities.Count -gt 0 -and $selectedGameplayCapabilities.Count -ne 1) { $missing.Add('gameplayCapabilitySelection') }
    foreach ($selection in $selectedGameplayCapabilities) {
        if ([string]$selection.capabilityId -notin @($allowedGameplayCapabilities | ForEach-Object { [string]$_.capabilityId })) { $gaps.Add("Gameplay capability '$($selection.capabilityId)' is not registered for Class '$($recipe.classId)'.") }
    }
    if ($allowedGameplayCapabilities.Count -eq 0 -and $selectedGameplayCapabilities.Count -gt 0) { $gaps.Add("Class '$($recipe.classId)' does not accept gameplay capability selections.") }
    if ([string]$recipe.coverage -ne 'Registered') { $gaps.Add("Class '$($recipe.classId)' has no registered deterministic collector recipe yet.") }

    $availableEvidence = @{
        providerSeeds = ($modCount -gt 0); candidatePlugins = $false; observedForms = $false
        locations = $false; 'claims.text' = (-not [string]::IsNullOrWhiteSpace([string]$Envelope.claims.text)); evidenceReferences = $false
    }
    $groupIndex = 0
    foreach ($group in @($recipe.evidencePolicy.requiredAnyOf)) {
        $groupIndex++
        if (@($group | Where-Object { $availableEvidence[[string]$_] -eq $true }).Count -eq 0) {
            $missing.Add(('evidenceGroup{0}({1})' -f $groupIndex, (@($group) -join '|')))
        }
    }

    $bindings = @(); $registry = @()
    if ($gaps.Count -eq 0) {
        $registry = @(Get-GridCapabilityRegistry -ScriptsRoot $ScriptsRoot)
        $registeredToolIds = if (Get-Command Get-GridToolRegistry -ErrorAction SilentlyContinue) {
            @((Get-GridToolRegistry -ScriptsRoot $ScriptsRoot) | ForEach-Object toolId | Sort-Object -Unique)
        } else {
            @($registry | Where-Object { $_.ownerScope.PSObject.Properties['tool'] -and -not [string]::IsNullOrWhiteSpace([string]$_.ownerScope.tool) } | ForEach-Object { 'grid.tool.' + ([string]$_.ownerScope.tool).ToLowerInvariant() } | Sort-Object -Unique)
        }
        foreach ($toolId in $allowedTools) {
            if ([string]$toolId -notin $registeredToolIds) { $gaps.Add("Class recipe references unregistered tool '$toolId'.") }
        }
        $diagnose = $recipe.pipelines.diagnose
        $rootIds = @()
        if ($null -ne $diagnose) { $rootIds = @($diagnose.rootCapabilityIds) }
        if ($rootIds.Count -eq 0) { $gaps.Add("Class '$($recipe.classId)' has no diagnosis capability roots.") }
        else {
            try {
                $contracts = @(Get-GridCapabilityDependencyClosure -Registry $registry -CapabilityId $rootIds)
                foreach ($contract in $contracts) {
                    if ([string]$contract.sideEffectClassification -in @('ExternalWrite','ExternalProcessControl')) { throw "Mutation capability '$($contract.capabilityId)' is prohibited in a diagnosis pipeline." }
                }
                $bindings = @(ConvertTo-GridCapabilityBindings -Contracts $contracts)
            } catch { $gaps.Add($_.Exception.Message) }
        }
    }
    $status = if ($gaps.Count -gt 0) { 'UnsupportedCoverage' } elseif ($missing -contains 'installationId' -or $missing -contains 'profileId') { 'NeedsContext' } elseif ($missing.Count -gt 0) { 'NeedsEvidence' } else { 'ReadyToCollect' }
    $recipeForHash = $recipe | Select-Object * -ExcludeProperty manifestPath
    $baselineRequiredGates = if ($modCount -gt 0) {
        @('InstallationBaseline','ProfileBaseline','AssetBaseline')
    } else {
        @('InstallationBaseline','ProfileBaseline')
    }
    $plan = [pscustomobject][ordered]@{
        schemaVersion = 1; status = $status; requestId = [string]$Envelope.requestId
        envelopeSha256 = [string]$Envelope.envelopeSha256; recipeSha256 = (Get-GridCanonicalJsonSha256 -InputObject $recipeForHash)
        classId = [string]$recipe.classId; displayName = [string]$recipe.displayName; iconId = [string]$recipe.iconId
        capabilityBindings = @($bindings); missingInputs = $missing.ToArray(); coverageGaps = $gaps.ToArray()
        dispatch = if ($status -eq 'ReadyToCollect') {
            if ($selectedGameplayCapabilities.Count -eq 1) {
                [pscustomobject][ordered]@{ entryPoint = 'Get-GridSkyrimGameplayCapabilityAssessment'; purpose = 'GameplayCapabilityAssessment'; requiredGates = @('InstallationBaseline','ProfileBaseline'); mutationAuthorized = $false }
            } elseif ($toolCount -gt 0) {
                [pscustomobject][ordered]@{ entryPoint = 'Invoke-GridToolEvidenceOrchestration'; purpose = 'SelectedToolEvidence'; requiredGates = $baselineRequiredGates; mutationAuthorized = $false }
            } else {
                [pscustomobject][ordered]@{ entryPoint = 'Invoke-GridBaseline.ps1'; purpose = 'Baseline'; requiredGates = $baselineRequiredGates; mutationAuthorized = $false }
            }
        } else { $null }
    }
    if ($selectedGameplayCapabilities.Count -eq 1) { $plan | Add-Member -NotePropertyName requestedCapabilityId -NotePropertyValue ([string]$selectedGameplayCapabilities[0].capabilityId) }
    $plan
}

function Get-GridRegisteredCoverage {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ScriptsRoot, [string]$ClassesRoot = (Join-Path $PSScriptRoot 'classes'))

    $registry = @(Get-GridCapabilityRegistry -ScriptsRoot $ScriptsRoot)
    @((Get-GridClassRecipeRegistry -ClassesRoot $ClassesRoot) | ForEach-Object {
        $recipe = $_
        $roots = @()
        if ($null -ne $recipe.pipelines.diagnose) { $roots = @($recipe.pipelines.diagnose.rootCapabilityIds) }
        $missing = @($roots | Where-Object { $id = $_; @($registry | Where-Object { [string]$_.capabilityId -ieq [string]$id }).Count -ne 1 })
        $unsafe = @()
        if ($roots.Count -gt 0 -and $missing.Count -eq 0) {
            try { $unsafe = @(Get-GridCapabilityDependencyClosure -Registry $registry -CapabilityId $roots | Where-Object { [string]$_.sideEffectClassification -in @('ExternalWrite','ExternalProcessControl') } | ForEach-Object capabilityId) }
            catch { $missing += @($roots) }
        }
        [pscustomobject][ordered]@{
            classId = [string]$recipe.classId; displayName = [string]$recipe.displayName; iconId = [string]$recipe.iconId
            coverage = if ([string]$recipe.coverage -eq 'Registered' -and $roots.Count -gt 0 -and $missing.Count -eq 0 -and $unsafe.Count -eq 0) { 'Registered' } else { 'Unsupported' }
            rootCapabilityIds = @($roots); missingCapabilityIds = @($missing | Sort-Object -Unique); unsafeCapabilityIds = @($unsafe)
        }
    })
}
