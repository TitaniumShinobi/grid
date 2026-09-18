#requires -Version 5.1
<# .SYNOPSIS Plans or explicitly executes one structured Grid Class request. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Game,
    [string]$InstallationId,
    [string]$ProfileId,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ClassId,
    [Alias('Mod','Provider')][string[]]$ModNames = @(),
    [Alias('Tool')][string[]]$ToolIds = @(),
    [AllowEmptyString()][string]$Request = '',
    [string]$CaseStoreRoot,
    [string]$AuthorizationInputsJson,
    [string]$ActorId,
    [string]$SessionId,
    [string]$AuthorizationGrantId,
    [string]$AuthorizationSecret,
    [string]$FinalizationCaseId,
    [switch]$IssueAuthorization,
    [switch]$Execute,
    [switch]$PassThru
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Grid.Health.psm1') -Force -DisableNameChecking

$selectedRecipe = @(Get-GridClassRecipeRegistry | Where-Object { [string]$_.classId -ieq $ClassId })
if ($selectedRecipe.Count -ne 1) { throw "ClassNotFound: '$ClassId'." }
$envelope = New-GridRequestEnvelope -GameId $Game -InstallationId $InstallationId -ProfileId $ProfileId `
    -ClassId $ClassId -ClassRecipeVersion ([string]$selectedRecipe[0].recipeVersion) -ModNames $ModNames -ToolIds $ToolIds -PlainText $Request
$plan = Resolve-GridRequestPlan -Envelope $envelope -ScriptsRoot (Split-Path -Parent $PSScriptRoot)
$execution = $null
$toolInputs = @{}
if (-not [string]::IsNullOrWhiteSpace($AuthorizationInputsJson)) {
    try {
        $inputDocument = $AuthorizationInputsJson | ConvertFrom-Json -ErrorAction Stop
        foreach ($property in @($inputDocument.PSObject.Properties)) { $toolInputs[[string]$property.Name] = $property.Value }
    } catch { throw "AuthorizationInputsInvalid: $($_.Exception.Message)" }
}
$authorizationReview = $null
$authorizationGrant = $null
if ([string]$plan.status -eq 'ReadyToCollect' -and [string]$plan.dispatch.entryPoint -eq 'Invoke-GridToolEvidenceOrchestration') {
    if ([string]::IsNullOrWhiteSpace($ActorId) -or [string]::IsNullOrWhiteSpace($SessionId)) { throw 'ActorSessionRequired: read authorization review requires explicit actorId and sessionId.' }
    $authorizationReview = New-GridRequestAuthorizationReview -Envelope $envelope -RequestPlan $plan -ScriptsRoot (Split-Path -Parent $PSScriptRoot) -ActorId $ActorId -SessionId $SessionId -ToolInputs $toolInputs
    if ($IssueAuthorization) {
        if ([string]::IsNullOrWhiteSpace($CaseStoreRoot)) { $CaseStoreRoot = Get-GridDiagnosticStoreRoot }
        $authorizationGrant = Grant-GridRequestAuthorization -AuthorizationReview $authorizationReview -StoreRoot $CaseStoreRoot
    }
}
if ($Execute -and [string]$plan.status -eq 'ReadyToCollect') {
    if ([string]$plan.dispatch.entryPoint -eq 'Invoke-GridToolEvidenceOrchestration') {
        if ([string]::IsNullOrWhiteSpace($AuthorizationGrantId) -or [string]::IsNullOrWhiteSpace($AuthorizationSecret)) { throw 'AuthorizationRequired: issue a current grant for the displayed review, then provide its grantId and one-time random secret.' }
        $execution = Invoke-GridAuthorizedRequestExecution -Envelope $envelope -RequestPlan $plan -AuthorizationReview $authorizationReview -AuthorizationGrantId $AuthorizationGrantId -AuthorizationSecret $AuthorizationSecret -ActorId $ActorId -SessionId $SessionId `
            -ScriptsRoot (Split-Path -Parent $PSScriptRoot) -CaseStoreRoot $CaseStoreRoot
    }
    elseif ([string]$plan.dispatch.entryPoint -eq 'Invoke-GridBaseline.ps1') {
        $baselineRequest = if ([string]::IsNullOrWhiteSpace($Request)) { [string]$plan.displayName } else { $Request }
        $arguments = @{
            Request = $baselineRequest; Game = $Game; InstallationId = $InstallationId; ProfileId = $ProfileId
            StatedProviderNames = @($envelope.selections.mods | ForEach-Object providerName)
            RequiredGates = @($plan.dispatch.requiredGates); PassThru = $true
        }
        if (-not [string]::IsNullOrWhiteSpace($CaseStoreRoot)) { $arguments.CaseStoreRoot = $CaseStoreRoot }
        if (-not [string]::IsNullOrWhiteSpace($FinalizationCaseId)) { $arguments.FinalizationCaseId = $FinalizationCaseId }
        $execution = & (Join-Path $PSScriptRoot 'Invoke-GridBaseline.ps1') @arguments
    }
    else { throw "UnsupportedDispatch: '$($plan.dispatch.entryPoint)'." }
}
$publicStatus = if ($null -ne $execution -and $execution.PSObject.Properties['Status']) { [string]$execution.Status } else { [string]$plan.status }
$collectorStarted = $null -ne $execution -and $execution.PSObject.Properties['CaseId'] -and -not [string]::IsNullOrWhiteSpace([string]$execution.CaseId)
$result = [pscustomobject][ordered]@{
    Tool = 'Invoke-GridClassRequest'; Status = $publicStatus; PlannerStatus = [string]$plan.status; RequestEnvelope = $envelope
    Plan = $plan; AuthorizationReview = $authorizationReview; AuthorizationGrant = $authorizationGrant; Executed = [bool]$collectorStarted; ExecutionResult = $execution
}
if ($PassThru) { $result } else { $result | ConvertTo-Json -Depth 50 }
