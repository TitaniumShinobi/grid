$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent $PSScriptRoot
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $gameRoot)
Import-Module (Join-Path $scriptsRoot 'health\Grid.Health.psm1') -Force
. (Join-Path $gameRoot 'health\actions\Resolve-GridSkyrimBaselineIntegrityDiagnosis.ps1')

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }

$inspection = [pscustomobject][ordered]@{ status = 'Complete'; semanticFingerprint = ('a' * 64) }
$dependencies = @(
    [pscustomobject][ordered]@{ pluginName='Immersive Encounters.esp'; sourceProvider='Immersive World Encounters SE'; scriptName='WEScript'; requiredVirtualPath='scripts\WEScript.pex'; referenceCount=3 },
    [pscustomobject][ordered]@{ pluginName='Immersive Encounters.esp'; sourceProvider='Immersive World Encounters SE'; scriptName='WIBardAliasScriptSette'; requiredVirtualPath='scripts\WIBardAliasScriptSette.pex'; referenceCount=3 }
)
$resolved = Resolve-GridSkyrimBaselineIntegrityDiagnosis -CaseId 'baseline-fixture' -ContextFingerprint 'context-fixture' -Inspection $inspection -Dependencies $dependencies
Assert-Equal 'Diagnosed' $resolved.DiagnosticResult.state 'Complete missing-script evidence must produce a diagnosis.'
Assert-Equal 1 @($resolved.Evidence).Count 'Each affected plugin must retain aggregate evidence without duplicating result bindings per script.'
Assert-True (@($resolved.DiagnosticResult.result.affectedMods.items | Where-Object { $_.kind -eq 'Plugin' -and $_.name -eq 'Immersive Encounters.esp' }).Count -eq 1) 'The consuming plugin must be identified.'
Assert-True (@($resolved.DiagnosticResult.result.affectedMods.items | Where-Object { $_.kind -eq 'Mod' -and $_.name -eq 'Immersive World Encounters SE' }).Count -eq 1) 'The MO2 source provider must be identified.'
Assert-True (@($resolved.DiagnosticResult.result.modRoles.items | Where-Object { $_.subjectId -eq 'Mod:IMMERSIVE WORLD ENCOUNTERS SE' -and $_.roles -contains 'OverrideProvider' }).Count -eq 1) 'The MO2 source provider must be labeled only as the plugin override source.'
Assert-True ($resolved.DiagnosticResult.result.finding.text -match 'scripts\\WEScript\.pex') 'The finding must name the exact missing virtual path.'
Assert-Equal 'Unresolved' $resolved.DiagnosticResult.result.solution.status 'Repair must remain unresolved without an exact verified source archive.'
$validation = Test-GridDiagnosticResult -DiagnosticResult $resolved.DiagnosticResult -Evidence @($resolved.Evidence)
Assert-True $validation.IsValid ('The diagnostic contract must validate: ' + ($validation.Errors -join '; '))

$again = Resolve-GridSkyrimBaselineIntegrityDiagnosis -CaseId 'baseline-fixture' -ContextFingerprint 'context-fixture' -Inspection $inspection -Dependencies @($dependencies[1],$dependencies[0])
Assert-Equal $resolved.DiagnosticResult.resultFingerprint $again.DiagnosticResult.resultFingerprint 'Input ordering must not change the semantic diagnosis.'

$clean = Resolve-GridSkyrimBaselineIntegrityDiagnosis -CaseId 'baseline-clean' -ContextFingerprint 'context-clean' -Inspection $inspection -Dependencies @()
Assert-Equal 'NeedsEvidence' $clean.DiagnosticResult.state 'A clean result for this rule must continue the remaining whole-profile rules.'

$assetInspection = [pscustomobject][ordered]@{ status = 'Complete'; semanticFingerprint = ('e' * 64) }
$assetDependencies = @(
    [pscustomobject][ordered]@{
        pluginName='Wearables.esp'; sourceProvider='Wearables Collection'; requiredVirtualPath='meshes\armor\missing.nif'; kind='Mesh'; referenceCount=2
        samples=@([pscustomobject][ordered]@{ recordSignature='ARMO'; rawFormId=16777488; subrecordSignature='MODL' })
    },
    [pscustomobject][ordered]@{
        pluginName='Wearables.esp'; sourceProvider='Wearables Collection'; requiredVirtualPath='textures\armor\missing.dds'; kind='Texture'; referenceCount=1
        samples=@([pscustomobject][ordered]@{ recordSignature='ARMO'; rawFormId=16777488; subrecordSignature='NIF_TEXTURE'; discoveredThroughVirtualPath='meshes\armor\wearable.nif' })
    }
)
$assetResolved = Resolve-GridSkyrimBaselineIntegrityDiagnosis -CaseId 'baseline-assets' -ContextFingerprint 'context-assets' -Inspection $inspection -Dependencies @() -AssetInspection $assetInspection -AssetDependencies $assetDependencies
Assert-Equal 'Diagnosed' $assetResolved.DiagnosticResult.state 'Complete missing plugin asset evidence must produce a diagnosis.'
Assert-Equal 2 @($assetResolved.Evidence).Count 'Mesh and texture defects must remain distinct evidence claims.'
Assert-True (@($assetResolved.Evidence | Where-Object parameter -eq 'missingPluginMeshDependency').Count -eq 1) 'The missing mesh must use the mesh-specific evidence parameter.'
Assert-True (@($assetResolved.Evidence | Where-Object parameter -eq 'missingPluginTextureDependency').Count -eq 1) 'The missing texture must use the texture-specific evidence parameter.'
Assert-True (@($assetResolved.Evidence | Where-Object parameter -eq 'missingPluginTextureDependency')[0].native.samples[0].recordSamples[0].discoveredThroughVirtualPath -eq 'meshes\armor\wearable.nif') 'A texture reached through a winning NIF must retain that NIF path in normalized evidence.'
Assert-True ($assetResolved.DiagnosticResult.result.finding.text -match 'meshes\\armor\\missing\.nif') 'The finding must retain the exact missing mesh path.'
Assert-True ($assetResolved.DiagnosticResult.result.solution.text -match 'do not synthesize placeholder assets') 'Asset repair must preserve exact lineage and prohibit placeholders.'
$assetValidation = Test-GridDiagnosticResult -DiagnosticResult $assetResolved.DiagnosticResult -Evidence @($assetResolved.Evidence)
Assert-True $assetValidation.IsValid ('The asset diagnostic contract must validate: ' + ($assetValidation.Errors -join '; '))
$assetReplay = Resolve-GridSkyrimBaselineIntegrityDiagnosis -CaseId 'baseline-assets' -ContextFingerprint 'context-assets' -Inspection $inspection -Dependencies @() -AssetInspection $assetInspection -AssetDependencies @($assetDependencies[1],$assetDependencies[0])
Assert-Equal $assetResolved.DiagnosticResult.resultFingerprint $assetReplay.DiagnosticResult.resultFingerprint 'Asset input ordering must not change the semantic diagnosis.'

$spidInspection = [pscustomobject][ordered]@{ status='Complete'; semanticFingerprint=('c' * 64) }
$spidIssues = @(
    [pscustomobject][ordered]@{ virtualPath='SPID\Panties of SPID_DISTR.ini'; providerName='Panties SPID'; pluginName='Panties of SPID.esp'; status='Disabled'; ruleCount=178; sampleLines=@(2,5,8) },
    [pscustomobject][ordered]@{ virtualPath='AnimalPajamaOutfits_DISTR.ini'; providerName='Animal Pajamas'; pluginName='AnimalPajamaOutfits.esp'; status='Missing'; ruleCount=59; sampleLines=@(16,17,18) }
)
$spidResolved = Resolve-GridSkyrimBaselineIntegrityDiagnosis -CaseId 'baseline-spid' -ContextFingerprint 'context-spid' -Inspection $inspection -Dependencies @() -SpidInspection $spidInspection -SpidSourceIssues $spidIssues
Assert-Equal 'Diagnosed' $spidResolved.DiagnosticResult.state 'Complete SPID source-plugin defects must produce a diagnosis.'
Assert-Equal 2 @($spidResolved.Evidence).Count 'Each SPID configuration/plugin defect group must retain one aggregate evidence item.'
Assert-True (@($spidResolved.DiagnosticResult.result.affectedMods.items | Where-Object { $_.kind -eq 'Mod' -and $_.name -eq 'Panties SPID' }).Count -eq 1) 'The winning SPID provider must be identified.'
Assert-True (@($spidResolved.DiagnosticResult.result.affectedMods.items | Where-Object { $_.kind -eq 'Plugin' -and $_.name -eq 'Panties of SPID.esp' }).Count -eq 1) 'The disabled source plugin must be identified.'
Assert-True ($spidResolved.DiagnosticResult.result.finding.text -match '178') 'The finding must retain the exact affected SPID rule count.'
Assert-Equal 'Unresolved' $spidResolved.DiagnosticResult.result.solution.status 'SPID repair must remain unresolved until intended enablement or removal is proven.'
$spidReplay = Resolve-GridSkyrimBaselineIntegrityDiagnosis -CaseId 'baseline-spid' -ContextFingerprint 'context-spid' -Inspection $inspection -Dependencies @() -SpidInspection $spidInspection -SpidSourceIssues @($spidIssues[1],$spidIssues[0])
Assert-Equal $spidResolved.DiagnosticResult.resultFingerprint $spidReplay.DiagnosticResult.resultFingerprint 'SPID input ordering must not change the semantic diagnosis.'

$crashSignatures = [pscustomobject][ordered]@{
    status = 'Complete'
    clusters = @(
        [pscustomobject][ordered]@{
            exceptionModule='OStim.dll'; exceptionType='EXCEPTION_ACCESS_VIOLATION'; crashCount=3
            latestCrashEvidenceId='crash-log.fixture'; evidenceStrength='DirectExceptionAddressModule'
            limitation='The exception address proves where execution failed, not by itself why the module received invalid state.'
            winningProvider=[pscustomobject][ordered]@{ providerName='OStim Standalone' }
        }
    )
}
$crashResolved = Resolve-GridSkyrimBaselineIntegrityDiagnosis -CaseId 'baseline-crash' -ContextFingerprint 'context-crash' -Inspection $inspection -Dependencies @() -CrashSignatures $crashSignatures
Assert-Equal 'Diagnosed' $crashResolved.DiagnosticResult.state 'A current crash-address module with an exact winning provider must produce a bounded diagnosis.'
Assert-Equal 1 @($crashResolved.Evidence).Count 'One crash cluster must produce one aggregate evidence item.'
Assert-True (@($crashResolved.DiagnosticResult.result.affectedMods.items | Where-Object { $_.kind -eq 'Mod' -and $_.name -eq 'OStim Standalone' }).Count -eq 1) 'The winning DLL provider must be identified without calling the DLL causal.'
Assert-True (@($crashResolved.DiagnosticResult.result.modRoles.items | Where-Object { $_.roles -contains 'RuntimeModuleProvider' }).Count -eq 1) 'The winning DLL provider must carry the runtime-module role.'
Assert-True ($crashResolved.DiagnosticResult.result.finding.text -match 'not the originating cause') 'The public finding must preserve the causal limitation.'
Assert-True ($crashResolved.DiagnosticResult.result.solution.text -match 'controlled reproduction') 'Crash remediation must require corroboration before mutation.'
$crashValidation = Test-GridDiagnosticResult -DiagnosticResult $crashResolved.DiagnosticResult -Evidence @($crashResolved.Evidence)
Assert-True $crashValidation.IsValid ('The crash diagnostic contract must validate: ' + ($crashValidation.Errors -join '; '))
$crashReplay = Resolve-GridSkyrimBaselineIntegrityDiagnosis -CaseId 'baseline-crash' -ContextFingerprint 'context-crash' -Inspection $inspection -Dependencies @() -CrashSignatures $crashSignatures
Assert-Equal $crashResolved.DiagnosticResult.resultFingerprint $crashReplay.DiagnosticResult.resultFingerprint 'Repeated crash evidence must retain a stable semantic diagnosis.'

$sksePluginLoad = [pscustomobject][ordered]@{
    status = 'Complete'
    rejected = @(
        [pscustomobject][ordered]@{
            evidenceId='skse-log.fixture.42'; dllName='Broken.dll'; pluginName='Broken Native'; pluginVersionHex='00020003'
            errorText='disabled, incompatible with current version of the game'; errorCode=0; lineNumber=42
            winningProvider=[pscustomobject][ordered]@{ providerName='Broken Native Mod' }
        }
    )
}
$skseResolved = Resolve-GridSkyrimBaselineIntegrityDiagnosis -CaseId 'baseline-skse' -ContextFingerprint 'context-skse' -Inspection $inspection -Dependencies @() -SksePluginLoad $sksePluginLoad
Assert-Equal 'Diagnosed' $skseResolved.DiagnosticResult.state 'An exact SKSE plugin-manager rejection with a current winning provider must produce a diagnosis.'
Assert-Equal 1 @($skseResolved.Evidence).Count 'One SKSE-rejected module must produce one evidence item.'
Assert-True (@($skseResolved.DiagnosticResult.result.affectedMods.items | Where-Object { $_.kind -eq 'Mod' -and $_.name -eq 'Broken Native Mod' }).Count -eq 1) 'The rejected DLL current winner must be identified.'
Assert-True ($skseResolved.DiagnosticResult.result.finding.text -match 'incompatible with current version') 'The exact SKSE rejection reason must reach the public finding.'
Assert-True ($skseResolved.DiagnosticResult.result.solution.text -match 'refresh skse64\.log') 'Repair must require a current selected-profile log before mutation.'
$skseValidation = Test-GridDiagnosticResult -DiagnosticResult $skseResolved.DiagnosticResult -Evidence @($skseResolved.Evidence)
Assert-True $skseValidation.IsValid ('The SKSE diagnostic contract must validate: ' + ($skseValidation.Errors -join '; '))

$partial = Resolve-GridSkyrimBaselineIntegrityDiagnosis -CaseId 'baseline-partial' -ContextFingerprint 'context-partial' -Inspection ([pscustomobject]@{ status='Partial'; semanticFingerprint=('b' * 64) }) -Dependencies @()
Assert-Equal 'NeedsEvidence' $partial.DiagnosticResult.state 'Partial inspection must fail closed.'

$assetPartial = Resolve-GridSkyrimBaselineIntegrityDiagnosis -CaseId 'baseline-asset-partial' -ContextFingerprint 'context-asset-partial' -Inspection $inspection -Dependencies @() -AssetInspection ([pscustomobject]@{ status='Partial'; semanticFingerprint=('f' * 64) }) -AssetDependencies @()
Assert-Equal 'NeedsEvidence' $assetPartial.DiagnosticResult.state 'Partial plugin asset inspection must fail closed.'

$partialAssetEvidence = Resolve-GridSkyrimBaselineIntegrityDiagnosis -CaseId 'baseline-asset-partial-evidence' -ContextFingerprint 'context-asset-partial-evidence' -Inspection $inspection -Dependencies @() `
    -AssetInspection ([pscustomobject]@{ status='Partial'; directRecordStatus='Complete'; nifTextureStatus='Partial'; semanticFingerprint=('9' * 64) }) `
    -AssetDependencies @($assetDependencies[0])
Assert-Equal 'Diagnosed' $partialAssetEvidence.DiagnosticResult.state 'A partial component must not suppress an independently verified missing asset record.'
Assert-True ($partialAssetEvidence.DiagnosticResult.result.finding.text -match 'winning-NIF embedded-texture coverage is incomplete') 'The diagnosis must disclose the exact incomplete asset component.'
Assert-True ($partialAssetEvidence.DiagnosticResult.result.finding.text -match 'unobserved items remain unknown') 'Partial coverage must never become a whole-profile healthy claim.'
Assert-True ($partialAssetEvidence.DiagnosticResult.result.solution.text -match 'does not block repair') 'Verified repair candidates must remain actionable while incomplete coverage resumes.'

$partialScriptEvidence = Resolve-GridSkyrimBaselineIntegrityDiagnosis -CaseId 'baseline-script-partial-evidence' -ContextFingerprint 'context-script-partial-evidence' `
    -Inspection ([pscustomobject]@{ status='Partial'; semanticFingerprint=('8' * 64) }) -Dependencies @($dependencies[0])
Assert-Equal 'Diagnosed' $partialScriptEvidence.DiagnosticResult.state 'A partial plugin scan must retain independently verified missing-script evidence.'
Assert-True ($partialScriptEvidence.DiagnosticResult.result.finding.text -match 'attached-script coverage is incomplete') 'The partial script diagnosis must disclose incomplete coverage.'

$spidPartial = Resolve-GridSkyrimBaselineIntegrityDiagnosis -CaseId 'baseline-spid-partial' -ContextFingerprint 'context-spid-partial' -Inspection $inspection -Dependencies @() -SpidInspection ([pscustomobject]@{status='Partial';semanticFingerprint=('d' * 64)}) -SpidSourceIssues @()
Assert-Equal 'NeedsEvidence' $spidPartial.DiagnosticResult.state 'Partial SPID inspection must fail closed.'

'Baseline integrity diagnosis checks passed.'
