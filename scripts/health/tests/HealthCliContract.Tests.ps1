$ErrorActionPreference = 'Stop'
$healthRoot = Split-Path -Parent $PSScriptRoot
$entryPoint = Join-Path $healthRoot 'Invoke-GridHealth.ps1'

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }

$tokens = $null; $parseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile($entryPoint, [ref]$tokens, [ref]$parseErrors)
Assert-Equal 0 @($parseErrors).Count 'Invoke-GridHealth.ps1 must parse without errors.'

$command = Get-Command $entryPoint
Assert-True $command.Parameters.ContainsKey('Request') 'Legacy Request parameter must remain public.'
Assert-True $command.Parameters.ContainsKey('StatedPluginNames') 'Legacy StatedPluginNames parameter must remain public.'
Assert-True (@($command.Parameters.StatedPluginNames.Aliases) -contains 'CandidatePlugin') 'CandidatePlugin alias must remain public.'
Assert-True (@($command.Parameters.StatedFormIds.Aliases) -contains 'ObservedFormId') 'ObservedFormId alias must remain public.'
Assert-True (@($command.Parameters.StatedEditorIds.Aliases) -contains 'ObservedEditorId') 'ObservedEditorId alias must remain public.'
Assert-True $command.Parameters.ContainsKey('StatedProviderNames') 'Exact MO2 providers must be available without conflating them with plugins.'

$root = Join-Path $env:TEMP ('grid-health-cli-contract-' + [guid]::NewGuid().ToString('N'))
try {
    $request = 'Verbatim legacy positional request.'
    $result = & $entryPoint $request -Game SkyrimSpecialEdition -CandidatePlugin 'Fixture.Plugin.esp' `
        -ObservedFormId '0x00001234' -ObservedEditorId 'FixtureEditor' -Mod 'Fixture Provider' -OutputRoot $root -PassThru
    Assert-Equal 'Invoke-GridHealth' $result.Tool 'Legacy pass-through Tool field must remain stable.'
    Assert-Equal 'NeedsContext' $result.Status 'Missing installation/profile context must remain fail-closed.'
    foreach ($property in @('CaseId','CaseDirectory','CasePath','InvestigationPlanPath','DiagnosticResultPath','DiagnosticResult','ReportPath','MissingInputs')) {
        Assert-True ($null -ne $result.PSObject.Properties[$property]) "Legacy pass-through property '$property' must remain present."
    }
    $case = Get-Content -LiteralPath $result.CasePath -Raw | ConvertFrom-Json
    Assert-Equal $request $case.rawPrompt 'Raw positional request must remain verbatim.'
    $plan = Get-Content -LiteralPath $result.InvestigationPlanPath -Raw | ConvertFrom-Json
    Assert-Equal 'Fixture.Plugin.esp' $plan.candidatePlugins[0].name 'CandidatePlugin alias must reach the plan.'
    Assert-Equal 'Fixture Provider' $plan.providerSeeds[0].name 'Mod/provider input must remain distinct from plugin input.'
    Assert-True (@($plan.observedForms | Where-Object formId -eq '0x00001234').Count -eq 1) 'ObservedFormId alias must reach the plan.'
    Assert-True (@($plan.observedForms | Where-Object editorId -eq 'FixtureEditor').Count -eq 1) 'ObservedEditorId alias must reach the plan.'

    $defaultRoot = Join-Path $root 'default-output'
    $rendered = (& $entryPoint 'Render the unresolved contract.' -Game SkyrimSpecialEdition -OutputRoot $defaultRoot | Out-String)
    foreach ($heading in @('Affected mod(s):','Mod role(s):','Finding:','Solution:')) { Assert-True ($rendered -match [regex]::Escape($heading)) "Default output must contain '$heading'." }
    Write-Host 'PASS: legacy positional input, aliases, pass-through shape, provider separation, and four-field output remain compatible.'
}
finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
