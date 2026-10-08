$ErrorActionPreference = 'Stop'
$healthRoot = Split-Path -Parent $PSScriptRoot
$scriptsRoot = Split-Path -Parent $healthRoot
$repositoryRoot = Split-Path -Parent $scriptsRoot

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }

. (Join-Path $healthRoot 'Grid.MdboCapabilities.ps1')

$registry = @(Get-GridMdboCapabilityRegistry -ScriptsRoot $scriptsRoot)
Assert-Equal 6 $registry.Count 'Health MDBO registry must expose six Contract 2 capabilities.'

$plan = Resolve-GridMdboRegistrationPlan -ScriptsRoot $scriptsRoot -GoalCapabilityIds @(
    'grid.registration.relationship.validate-graph'
)
Assert-Equal 6 $plan.capabilityCount 'Registration plan must resolve against the full registry.'
Assert-Equal 'grid.registration.relationship.discover-evidence' $plan.executionOrder[0] 'MDBO execution order must start at discover-evidence.'
Assert-Equal 'grid.registration.relationship.validate-graph' $plan.executionOrder[-1] 'Graph validation goal must terminate the plan.'

$containment = Resolve-GridMdboRegistrationPlan -ScriptsRoot $scriptsRoot -GoalCapabilityIds @(
    'grid.registration.location-relationship.resolve'
)
Assert-True ($containment.executionOrder -contains 'grid.registration.location-relationship.resolve') 'Location containment goal must remain in the MDBO plan.'

Write-Host 'PASS: health MDBO registry discovery and AUTO execution planning.'
