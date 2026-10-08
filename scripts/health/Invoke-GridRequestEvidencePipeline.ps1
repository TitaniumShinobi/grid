#requires -Version 5.1
<#
.SYNOPSIS
Dispatches one authorized game-owned diagnostic evidence pipeline.
.DESCRIPTION
The selected request plan and account-bound context choose the adapter and
entry point. User prose is passed through as a claim and is never used for
adapter, Class, or capability selection.
#>
Set-StrictMode -Version Latest

function Invoke-GridRequestEvidencePipeline {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Envelope,
        [Parameter(Mandatory)]$RequestPlan,
        [Parameter(Mandatory)]$AuthorizationScope,
        [Parameter(Mandatory)][string]$ScriptsRoot,
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)][string]$CaseDirectory
    )

    if ([string]$RequestPlan.status -cne 'ReadyToCollect' -or $null -eq $RequestPlan.dispatch) {
        throw 'RequestEvidencePipelineUnavailable: the request plan has no ready diagnostic dispatch.'
    }
    if ([bool]$RequestPlan.dispatch.mutationAuthorized) {
        throw 'RequestEvidencePipelineUnsafe: a read-only diagnostic dispatch is required.'
    }
    if ([string]$AuthorizationScope.toolId -cne 'grid.intake.game-context') {
        throw 'RequestEvidencePipelineAuthorizationMismatch: the account-owned game context is not authorized.'
    }
    $adapterGameId = Resolve-GridRequestAdapterGameId -GameId ([string]$Envelope.context.gameId)
    # The adapter is selected solely by the structured game identity. Preserve
    # the verbatim claim only to satisfy the resolver's non-empty request
    # contract; explicit -Game prevents prose from participating in routing.
    $adapter = Resolve-GridGameAdapter -ScriptsRoot $ScriptsRoot -Request ([string]$Envelope.claims.text) -Game $adapterGameId
    $modulePath = Join-Path $adapter.AdapterRoot $adapter.Module
    $module = Import-Module -Name $modulePath -Force -PassThru -DisableNameChecking
    $entryPoint = [string]$RequestPlan.dispatch.entryPoint
    $command = Get-Command -Name $entryPoint -Module $module.Name -ErrorAction SilentlyContinue
    if (-not $command) {
        throw "RequestEvidencePipelineUnavailable: adapter '$($adapter.Id)' does not export planned entry point '$entryPoint'."
    }
    $resolvedContext = if ($AuthorizationScope.inputs.PSObject.Properties['resolvedContext']) {
        $AuthorizationScope.inputs.resolvedContext
    } else { $null }
    if ($null -eq $resolvedContext) {
        throw 'RequestEvidencePipelineAuthorizationMismatch: the authorized resolved game context is absent.'
    }
    $connectedRegistration = if ($AuthorizationScope.inputs.PSObject.Properties['connectedRegistration']) {
        $AuthorizationScope.inputs.connectedRegistration
    } else { $null }
    if ($connectedRegistration -and $connectedRegistration.PSObject.Properties['externalValidationDeferred'] -and
        [bool]$connectedRegistration.externalValidationDeferred) {
        # The registration store is account-owned and may be read while the
        # review is prepared. The external installation is touched only here,
        # after the exact one-use read grant has been consumed.
        $registeredRoot = [IO.Path]::GetFullPath([string]$connectedRegistration.installRoot).TrimEnd('\')
        $registeredExecutable = [IO.Path]::GetFullPath([string]$connectedRegistration.executablePath)
        $approved = @($AuthorizationScope.exactReadPaths | ForEach-Object { [IO.Path]::GetFullPath([string]$_).TrimEnd('\') })
        if ($registeredRoot -notin $approved -or $registeredExecutable.TrimEnd('\') -notin $approved) {
            throw 'RequestEvidencePipelineAuthorizationMismatch: the registered installation and executable were not both reviewed.'
        }
        if (-not (Test-Path -LiteralPath $registeredRoot -PathType Container) -or
            -not (Test-Path -LiteralPath $registeredExecutable -PathType Leaf)) {
            throw 'InstallationContextUnresolved: the exact registered installation or executable is unavailable.'
        }
        $rootItem = Get-Item -LiteralPath $registeredRoot -Force -ErrorAction Stop
        $executableItem = Get-Item -LiteralPath $registeredExecutable -Force -ErrorAction Stop
        if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            ($executableItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'InstallationContextUnresolved: registered diagnostic roots may not be reparse points.'
        }
    }

    $available = $command.Parameters
    $arguments = @{}
    if ($available.ContainsKey('Request')) { $arguments.Request = [string]$Envelope.claims.text }
    if ($available.ContainsKey('CaseId')) { $arguments.CaseId = $CaseId }
    if ($available.ContainsKey('CaseDirectory')) { $arguments.CaseDirectory = $CaseDirectory }
    if ($available.ContainsKey('ResolvedContext')) { $arguments.ResolvedContext = $resolvedContext }
    if ($available.ContainsKey('RequestPlan')) { $arguments.RequestPlan = $RequestPlan }
    if ($available.ContainsKey('InvestigationPlan')) { $arguments.InvestigationPlan = $RequestPlan }
    if ($available.ContainsKey('GameRoot')) { $arguments.GameRoot = @([string]$resolvedContext.gameRoot) }
    if ($available.ContainsKey('InstallationId')) { $arguments.InstallationId = [string]$Envelope.context.installationId }

    $result = & $command @arguments
    if ($null -eq $result) {
        throw "RequestEvidencePipelineFailed: '$entryPoint' returned no result."
    }
    if ($result.PSObject.Properties['ChangedExternalState'] -and [bool]$result.ChangedExternalState) {
        throw "RequestEvidencePipelineUnsafe: '$entryPoint' reported an external state change."
    }
    $result
}
