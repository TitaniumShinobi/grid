<#
.SYNOPSIS
Resolves which installed game adapter should handle a request, without guessing.
.DESCRIPTION
Game-agnostic routing used by the shared health engine. Adapters are
discovered generically from scripts/games/<game>/health/adapter.psd1 manifests --
this file contains no game- or mod-specific identities. Resolution order:
1. An explicit -Game parameter.
2. A connected installation/profile context, probed via each adapter's own
   ConnectedContextScript/ConnectedContextFunction (declared in its manifest).
3. An existing case's recorded game (case continuation).
4. Each adapter's own PromptMatchers, used only as a fallback.
5. Otherwise: refuse with AmbiguousGameContext rather than guess.
#>
function Get-GridInstalledAdapters {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ScriptsRoot)
    if (-not (Get-Command Get-GridCapabilityRegistry -ErrorAction SilentlyContinue)) {
        . (Join-Path $PSScriptRoot 'Grid.Capabilities.ps1')
    }
    $scriptsPath = [IO.Path]::GetFullPath($ScriptsRoot)
    $gamesRoot = Join-Path $scriptsPath 'games'
    if (-not (Test-Path -LiteralPath $gamesRoot -PathType Container)) { return @() }
    $registry = @(Get-GridCapabilityRegistry -ScriptsRoot $scriptsPath)
    $adapters = New-Object Collections.Generic.List[pscustomobject]
    foreach ($gameRoot in @(Get-ChildItem -LiteralPath $gamesRoot -Directory -ErrorAction Stop | Sort-Object Name)) {
        $manifestPath = Get-Item -LiteralPath (Join-Path $gameRoot.FullName 'health\adapter.psd1') -ErrorAction SilentlyContinue
        if (-not $manifestPath) { continue }
        try {
            $manifest = Import-PowerShellDataFile -LiteralPath $manifestPath.FullName
            foreach ($required in @('Id','DisplayName','Module','InvocationFunction','CapabilityManifest','InvocationCapabilityId','ConnectedContextCapabilityId','DefaultPipelineCapabilityIds','ConnectedContextScript','ConnectedContextFunction')) {
                if ([string]::IsNullOrWhiteSpace([string]$manifest[$required])) { throw "Missing adapter manifest field '$required'." }
            }
            if ([int]$manifest.SchemaVersion -ne 2) { throw "Unsupported adapter SchemaVersion '$($manifest.SchemaVersion)'." }
            $adapterRoot = Split-Path -Parent $manifestPath.FullName
            $capabilityManifestPath = [IO.Path]::GetFullPath((Join-Path $adapterRoot ([string]$manifest.CapabilityManifest)))
            [void](Read-GridCapabilityManifest -LiteralPath $capabilityManifestPath)
            $invocationContract = Resolve-GridCapabilityContract -Registry $registry -CapabilityId ([string]$manifest.InvocationCapabilityId)
            $contextContract = Resolve-GridCapabilityContract -Registry $registry -CapabilityId ([string]$manifest.ConnectedContextCapabilityId)
            $baselineCapabilityId = [string]$manifest.BaselineCapabilityId
            $baselineContract = $null
            if (-not [string]::IsNullOrWhiteSpace($baselineCapabilityId)) {
                $baselineContract = Resolve-GridCapabilityContract -Registry $registry -CapabilityId $baselineCapabilityId
                if ([string]$baselineContract.ownerScope.kind -ne 'Game' -or [string]$baselineContract.ownerScope.gameId -ine [string]$manifest.Id) {
                    throw "Baseline capability '$baselineCapabilityId' is not owned by game '$($manifest.Id)'."
                }
            }
            if ([string]$invocationContract.ownerScope.kind -ne 'Game' -or [string]$invocationContract.ownerScope.gameId -ine [string]$manifest.Id) {
                throw "Invocation capability '$($manifest.InvocationCapabilityId)' is not owned by game '$($manifest.Id)'."
            }
            if ([string]$contextContract.implementation.entryPoint -ne [string]$manifest.ConnectedContextFunction) {
                throw "Connected context capability entry point does not match '$($manifest.ConnectedContextFunction)'."
            }
            $pipelineContracts = @(Get-GridCapabilityDependencyClosure -Registry $registry -CapabilityId @($manifest.DefaultPipelineCapabilityIds))
            $baselinePlanCapabilityIds = @($baselineCapabilityId | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
            foreach ($bindingName in @('RepairInspectionCapabilityId','RepairProposalCapabilityId')) {
                $bindingId = [string]$manifest[$bindingName]
                if ([string]::IsNullOrWhiteSpace($bindingId)) { continue }
                $bindingContract = Resolve-GridCapabilityContract -Registry $registry -CapabilityId $bindingId
                if ([string]$bindingContract.ownerScope.kind -ne 'Game' -or [string]$bindingContract.ownerScope.gameId -ine [string]$manifest.Id) {
                    throw "$bindingName capability '$bindingId' is not owned by game '$($manifest.Id)'."
                }
                $baselinePlanCapabilityIds += $bindingId
            }
            $adapters.Add([pscustomobject]@{
                Id = [string]$manifest.Id
                DisplayName = [string]$manifest.DisplayName
                ScriptsRoot = $scriptsPath
                ManifestPath = $manifestPath.FullName
                AdapterRoot = $adapterRoot
                Module = [string]$manifest.Module
                InvocationFunction = [string]$manifest.InvocationFunction
                CapabilityManifestPath = $capabilityManifestPath
                InvocationCapabilityId = [string]$manifest.InvocationCapabilityId
                BaselineCapabilityId = $baselineCapabilityId
                ConnectedContextCapabilityId = [string]$manifest.ConnectedContextCapabilityId
                DefaultPipelineCapabilityIds = @($manifest.DefaultPipelineCapabilityIds)
                CapabilityBindings = @(ConvertTo-GridCapabilityBindings -Contracts $pipelineContracts)
                BaselineCapabilityBindings = if ($baselineContract) {
                    @(ConvertTo-GridCapabilityBindings -Contracts @(Get-GridCapabilityDependencyClosure -Registry $registry -CapabilityId $baselinePlanCapabilityIds))
                } else { @() }
                PromptMatchers = @($manifest.PromptMatchers)
                ConnectedContextScript = [string]$manifest.ConnectedContextScript
                ConnectedContextFunction = [string]$manifest.ConnectedContextFunction
            })
        }
        catch { throw "AdapterManifestInvalid: '$($manifestPath.FullName)'. $($_.Exception.Message)" }
    }
    return @($adapters | Sort-Object Id)
}

function Test-GridAdapterConnectedContext {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Adapter, [hashtable]$ContextParameters = @{})
    if ([string]::IsNullOrWhiteSpace($Adapter.ConnectedContextScript) -or [string]::IsNullOrWhiteSpace($Adapter.ConnectedContextFunction)) { return $false }
    $scriptPath = Join-Path $Adapter.AdapterRoot $Adapter.ConnectedContextScript
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { return $false }
    try {
        . $scriptPath
        $function = Get-Item "Function:\$($Adapter.ConnectedContextFunction)" -ErrorAction SilentlyContinue
        if (-not $function) { return $false }
        return [bool](& $function.ScriptBlock @ContextParameters)
    } catch { return $false }
}

function Resolve-GridGameAdapter {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ScriptsRoot,
        [Parameter(Mandatory)][string]$Request,
        [string]$Game = 'Auto',
        [hashtable]$ConnectedContextParameters = @{},
        [string]$ExistingCasePath
    )

    $adapters = @(Get-GridInstalledAdapters -ScriptsRoot $ScriptsRoot)
    if ($adapters.Count -eq 0) { throw 'AmbiguousGameContext: no installed game adapters were found.' }

    if ($Game -and $Game -ne 'Auto') {
        $explicit = @($adapters | Where-Object { $_.Id -eq $Game })
        if ($explicit.Count -eq 1) { return $explicit[0] }
        throw "AmbiguousGameContext: explicit -Game '$Game' does not match an installed adapter."
    }

    $connected = @($adapters | Where-Object { Test-GridAdapterConnectedContext -Adapter $_ -ContextParameters $ConnectedContextParameters })
    if ($connected.Count -eq 1) { return $connected[0] }
    if ($connected.Count -gt 1) { throw 'AmbiguousGameContext: more than one adapter reports a connected installation/profile context.' }

    if ($ExistingCasePath -and (Test-Path -LiteralPath $ExistingCasePath -PathType Leaf)) {
        try {
            $existingCase = Get-Content -LiteralPath $ExistingCasePath -Raw | ConvertFrom-Json
            $caseGame = [string]$existingCase.game
            if ($caseGame) {
                $matched = @($adapters | Where-Object { $_.Id -eq $caseGame })
                if ($matched.Count -eq 1) { return $matched[0] }
            }
        } catch { }
    }

    $promptMatched = @($adapters | Where-Object {
        $pattern = ($_.PromptMatchers | Where-Object { $_ }) -join '|'
        $pattern -and ($Request -match "(?i)($pattern)")
    })
    if ($promptMatched.Count -eq 1) { return $promptMatched[0] }
    if ($promptMatched.Count -gt 1) { throw 'AmbiguousGameContext: more than one adapter matched generic prompt keywords.' }

    throw 'AmbiguousGameContext: no explicit -Game, connected installation, existing case, or prompt keyword identified a game adapter. Grid will not guess.'
}

function Get-GridAdapterInvocationCommand {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Adapter)
    $modulePath = Join-Path $Adapter.AdapterRoot $Adapter.Module
    if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf)) { throw "Adapter module not found: $modulePath" }
    $module = Import-Module -Name $modulePath -Force -PassThru
    $name = [string]$Adapter.InvocationFunction
    if ($name) {
        $command = Get-Command -Name $name -Module $module.Name -ErrorAction SilentlyContinue
        if (-not $command) { throw "Adapter invocation function '$name' was not exported by $modulePath." }
        return $command
    }
    $commands = @(Get-Command -Module $module.Name -CommandType Function | Where-Object { $_.Name -like 'Invoke-Grid*HealthAdapter' })
    if ($commands.Count -ne 1) { throw "Adapter '$($Adapter.Id)' must declare InvocationFunction or export exactly one Invoke-Grid*HealthAdapter function." }
    $commands[0]
}

function Get-GridAdapterBaselineCommand {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Adapter)

    if (-not (Get-Command Get-GridCapabilityRegistry -ErrorAction SilentlyContinue)) {
        . (Join-Path $PSScriptRoot 'Grid.Capabilities.ps1')
    }
    if ([string]::IsNullOrWhiteSpace([string]$Adapter.BaselineCapabilityId)) {
        throw "Adapter '$($Adapter.Id)' does not declare BaselineCapabilityId."
    }
    $registry = @(Get-GridCapabilityRegistry -ScriptsRoot ([string]$Adapter.ScriptsRoot))
    $contract = Resolve-GridCapabilityContract -Registry $registry -CapabilityId ([string]$Adapter.BaselineCapabilityId)
    $modulePath = Join-Path $Adapter.AdapterRoot $Adapter.Module
    if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf)) { throw "Adapter module not found: $modulePath" }
    $module = Import-Module -Name $modulePath -Force -PassThru
    $name = [string]$contract.implementation.entryPoint
    $command = Get-Command -Name $name -Module $module.Name -ErrorAction SilentlyContinue
    if (-not $command) { throw "Baseline capability entry point '$name' was not exported by $modulePath." }
    $command
}
