$ErrorActionPreference = 'Stop'
$healthRoot = Split-Path -Parent $PSScriptRoot
$scriptsRoot = Split-Path -Parent $healthRoot
$repositoryRoot = Split-Path -Parent $scriptsRoot
$validator = Join-Path $healthRoot 'Test-GridCapabilityArchitecture.ps1'
$fixturePath = Join-Path $PSScriptRoot 'fixtures\capability-architecture.cases.v1.json'

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }
function Read-Manifest([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
function Write-Manifest([string]$Path, $Manifest) { $Manifest | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $Path -Encoding UTF8 }

$fixtureHash = (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash
$fixture = Get-Content -LiteralPath $fixturePath -Raw | ConvertFrom-Json
Assert-Equal 1 $fixture.schemaVersion 'Fixture schema version must be explicit.'

$tempRoot = Join-Path $env:TEMP ('grid-capability-architecture-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
try {
    foreach ($case in @($fixture.cases)) {
        $caseRoot = Join-Path $tempRoot $case.name
        New-Item -ItemType Directory -Path $caseRoot | Out-Null
        Copy-Item -LiteralPath $scriptsRoot -Destination (Join-Path $caseRoot 'scripts') -Recurse
        $sharedManifestPath = Join-Path $caseRoot 'scripts\health\capabilities.v1.json'
        $gameManifestPath = Join-Path $caseRoot 'scripts\games\skyrimspecialedition\capabilities.v1.json'

        switch ([string]$case.operation) {
            'None' { }
            'CreateRootMods' { New-Item -ItemType Directory -Path (Join-Path $caseRoot 'scripts\mods') | Out-Null }
            'MisScopeMod' {
                $manifest = Read-Manifest $sharedManifestPath
                $manifest.capabilities[0].ownerScope = [pscustomobject]@{ kind = 'Mod'; gameId = 'fixturegame'; modId = 'fixturemod' }
                Write-Manifest $sharedManifestPath $manifest
            }
            'AddConcreteCaseData' {
                $manifest = Read-Manifest $sharedManifestPath
                $manifest.capabilities[0] | Add-Member -NotePropertyName caseDefault -NotePropertyValue 'FixturePatch.esp'
                Write-Manifest $sharedManifestPath $manifest
            }
            'DuplicateCapabilityId' {
                $shared = Read-Manifest $sharedManifestPath
                $game = Read-Manifest $gameManifestPath
                $game.capabilities = @($game.capabilities) + @($shared.capabilities[0])
                Write-Manifest $gameManifestPath $game
            }
            'AddSharedGameImport' {
                Add-Content -LiteralPath (Join-Path $caseRoot 'scripts\health\Grid.Evidence.ps1') -Value "`nImport-Module 'scripts/games/fixturegame/Fixture.psm1'"
            }
            'RemoveInputSchema' {
                $manifest = Read-Manifest $sharedManifestPath
                $manifest.capabilities[0].PSObject.Properties.Remove('inputSchema')
                Write-Manifest $sharedManifestPath $manifest
            }
            'RemoveSideEffect' {
                $manifest = Read-Manifest $sharedManifestPath
                $manifest.capabilities[0].PSObject.Properties.Remove('sideEffectClassification')
                Write-Manifest $sharedManifestPath $manifest
            }
            'RemoveMutationSafety' {
                $manifest = Read-Manifest $gameManifestPath
                $target = @($manifest.capabilities | Where-Object capabilityId -eq 'grid.game.skyrimspecialedition.plugin-state.execute')[0]
                $target.requiredAuthority.kind = 'None'
                $target.rollback.mode = 'NotDeclared'
                Write-Manifest $gameManifestPath $manifest
            }
            'BreakManifestJson' { Set-Content -LiteralPath $sharedManifestPath -Value '{ invalid json' -Encoding UTF8 }
            'CreateOrphanScript' { Set-Content -LiteralPath (Join-Path $caseRoot 'scripts\health\Orphan.ps1') -Value "'synthetic'" -Encoding UTF8 }
            default { throw "Unknown fixture operation '$($case.operation)'." }
        }

        $result = & $validator -RepositoryRoot $caseRoot -PassThru
        Assert-Equal ([string]$case.expectedStatus) ([string]$result.status) "Architecture fixture '$($case.name)' returned the wrong status."
        if ($case.expectedViolation) {
            Assert-True (@($result.violations | Where-Object code -eq $case.expectedViolation).Count -gt 0) "Architecture fixture '$($case.name)' did not report '$($case.expectedViolation)'."
        }
    }
    Write-Host 'PASS: capability architecture accepts the production graph and rejects every synthetic policy violation.'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Assert-Equal $fixtureHash (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash 'Committed capability fixtures must remain unchanged by tests.'
$production = & $validator -RepositoryRoot $repositoryRoot -PassThru
Assert-Equal 'Passed' $production.status 'The production capability architecture must pass enforcement.'
$authorizationCapability = @($production.inventory | Where-Object { @($_.capabilityIds) -contains 'grid.health.remediation.authorize' })[0]
Assert-True ($null -ne $authorizationCapability) 'Authorization capability must remain registered in the production inventory.'
$sharedManifest = Read-Manifest (Join-Path $scriptsRoot 'health\capabilities.v1.json')
$authorizationContract = @($sharedManifest.capabilities | Where-Object capabilityId -eq 'grid.health.remediation.authorize')[0]
Assert-Equal 'ExplicitGrantIssuance' ([string]$authorizationContract.requiredAuthority.kind) 'Authorization issuance must use the closed ExplicitGrantIssuance authority kind.'
$gameManifest = Read-Manifest (Join-Path $scriptsRoot 'games\skyrimspecialedition\capabilities.v1.json')
$durableGrantCapabilities = @($gameManifest.capabilities | Where-Object { ([string]$_.requiredAuthority.kind) -eq 'DurableOneUseGrant' })
Assert-True ($durableGrantCapabilities.Count -ge 3) 'Mutation capabilities that consume hardened grants must remain registered with DurableOneUseGrant authority.'
Assert-Equal 90 $production.capabilityCount 'The approved semantic capability map must contain 90 operations, including the eight GTA V capabilities.'
Assert-True (@($production.inventory | Where-Object classification -eq 'Unclassified').Count -eq 0) 'Production inventory must contain no unclassified files.'
Write-Host 'PASS: production capability inventory is complete, deterministic, and read-only.'
