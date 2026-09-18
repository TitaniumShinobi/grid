#requires -Version 5.1

function Get-GridReferenceSuppressionExecutionContext {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Specification,[Parameter(Mandatory)][string]$CaseStoreRoot)
    if ([int]$Specification.schemaVersion -ne 2) { throw 'ReferenceSuppressionSpecificationVersionUnsupported: execution requires schema v2.' }
    $store=[IO.Path]::GetFullPath($CaseStoreRoot).TrimEnd('\')
    $gridDataRoot=if([IO.Path]::GetFileName($store)-ieq'v1'-and[IO.Path]::GetFileName((Split-Path -Parent $store))-ieq'cases'){Split-Path -Parent (Split-Path -Parent $store)}else{$store}
    if(-not(Test-Path -LiteralPath (Join-Path $gridDataRoot 'connections\mo2-installations.v1.json') -PathType Leaf)){throw 'ReferenceSuppressionCaseStoreRootInvalid: the connected installation store is unavailable.'}
    $resolved=Resolve-GridSkyrimRequestToolInputs -GridDataRoot $gridDataRoot -InstallationId ([string]$Specification.context.installationId) -ProfileId ([string]$Specification.context.profileId) -ToolIds @('grid.tool.sseedit')
    $tool=$resolved['grid.tool.sseedit']
    if (-not $tool) { throw 'ReferenceSuppressionContextUnresolved.' }
    $modName=[IO.Path]::GetFileNameWithoutExtension([string]$Specification.patchPluginName)
    if ([string]::IsNullOrWhiteSpace($modName) -or $modName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or $modName -in @('.','..')) { throw 'ReferenceSuppressionPatchModNameInvalid.' }
    $modsRoot=[IO.Path]::GetFullPath([string]$tool.modsRoot).TrimEnd('\')
    $overwriteRoot=[IO.Path]::GetFullPath([string]$tool.overwriteRoot).TrimEnd('\')
    $gameDataRoot=[IO.Path]::GetFullPath([string]$tool.gameDataRoot).TrimEnd('\')
    $modDirectory=[IO.Path]::GetFullPath((Join-Path $modsRoot $modName))
    if (-not $modDirectory.StartsWith($modsRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'ReferenceSuppressionPatchModEscapesRoot.' }
    [pscustomobject][ordered]@{
        gridDataRoot=$gridDataRoot;mo2Root=[IO.Path]::GetFullPath([string]$tool.mo2Root).TrimEnd('\');configurationPath=[IO.Path]::GetFullPath([string]$tool.configurationPath)
        profileName=[string]$tool.profileName;profileRoot=[IO.Path]::GetFullPath([string]$tool.profileRoot).TrimEnd('\');modsRoot=$modsRoot;overwriteRoot=$overwriteRoot;gameDataRoot=$gameDataRoot
        patchModName=$modName;patchModDirectory=$modDirectory;patchDestinationPath=Join-Path $modDirectory ([string]$Specification.patchPluginName)
        overwritePatchPath=Join-Path $overwriteRoot ([string]$Specification.patchPluginName);gameDataPatchPath=Join-Path $gameDataRoot ([string]$Specification.patchPluginName)
    }
}

function Get-GridReferenceSuppressionTargets {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Specification,[Parameter(Mandatory)]$ExecutionContext)
    @(
        @($Specification.records|Sort-Object originPlugin,localFormId|ForEach-Object{"record:$($_.originPlugin):$('{0:X6}'-f[uint32]$_.localFormId):InitiallyDisabled=true"})
        "staging:$([IO.Path]::GetFullPath([string]$ExecutionContext.overwritePatchPath))"
        "staging-game-data:$([IO.Path]::GetFullPath([string]$ExecutionContext.gameDataPatchPath))"
        "patch:$([IO.Path]::GetFullPath([string]$ExecutionContext.patchDestinationPath))"
    )
}

function Test-GridReferenceSuppressionProcessesClosed {
    [CmdletBinding()]
    param()
    $gate=Resolve-GridXEditLaunchProcessGate -Processes @(Get-Process -Name ModOrganizer,SkyrimSE,skse64_loader,SkyrimSELauncher,SSEEdit,SSEEdit64,xEdit -ErrorAction SilentlyContinue)
    if ([string]$gate.status -ne 'Ready') {
        $labels=@($gate.blockingProcesses|ForEach-Object{"$($_.processName) (PID $($_.processId))"})
        throw ('ReferenceSuppressionProcessesOpen: close the connected MO2, Skyrim, SKSE, launcher, and xEdit processes before applying this patch. Running: '+($labels -join ', ')+'.')
    }
}

function Test-GridReferenceSuppressionEvidenceCurrent {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Specification,[Parameter(Mandatory)][string]$CaseStoreRoot,[Parameter(Mandatory)]$ExecutionContext)
    $evidenceSeal=Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory ([string]$Specification.evidenceCase.directory)
    if(-not$evidenceSeal.IsValid -or [string]$evidenceSeal.Manifest.manifestSha256 -cne [string]$Specification.evidenceCase.manifestSha256){throw 'ReferenceSuppressionEvidenceCaseInvalid: sealed evidence changed.'}
    $baselineSeal=Test-GridDiagnosticCaseSeal -StoreRoot $CaseStoreRoot -CaseDirectory ([string]$Specification.context.baselineCaseDirectory)
    if(-not$baselineSeal.IsValid -or [string]$baselineSeal.Manifest.manifestSha256 -cne [string]$Specification.context.baselineManifestSha256){throw 'ReferenceSuppressionBaselineInvalid: sealed baseline changed.'}
    $graphPath=[IO.Path]::GetFullPath([string]$Specification.recordGraph.path)
    if(-not(Test-Path -LiteralPath $graphPath -PathType Leaf)-or(Get-FileHash -LiteralPath $graphPath -Algorithm SHA256).Hash.ToUpperInvariant() -cne [string]$Specification.recordGraph.sha256){throw 'ReferenceSuppressionRecordGraphChanged.'}
    $graph=Get-Content -LiteralPath $graphPath -Raw|ConvertFrom-Json -ErrorAction Stop
    $protectedPath=Join-Path ([string]$Specification.evidenceCase.directory) 'snapshots\protected-after.v1.json'
    if(-not(Test-Path -LiteralPath $protectedPath -PathType Leaf)){throw 'ReferenceSuppressionProtectedStateMissing.'}
    $protected=Get-Content -LiteralPath $protectedPath -Raw|ConvertFrom-Json -ErrorAction Stop
    $selected=@(Select-GridSkyrimRecordRelationshipProtectedFiles -Files @($protected.files))
    [void](Get-GridSkyrimRecordRelationshipProtectedState -Files $selected -RequireExpectedHash)
    foreach($plugin in @($graph.plugins)){
        $source=[IO.Path]::GetFullPath([string]$plugin.canonicalPath)
        if(-not(Test-Path -LiteralPath $source -PathType Leaf)){throw "ReferenceSuppressionSourceMissing: $source"}
        $item=Get-Item -LiteralPath $source -Force
        if(($item.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0){throw "ReferenceSuppressionSourceReparsePoint: $source"}
        if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToUpperInvariant() -cne ([string]$plugin.rawSha256).ToUpperInvariant()){throw "ReferenceSuppressionSourceChanged: $source"}
    }
    foreach($record in @($Specification.records)){
        $matches=@($graph.chains|Where-Object{[string]$_.target.originPlugin -ieq [string]$record.originPlugin -and [uint32]$_.target.localFormId -eq [uint32]$record.localFormId})
        if($matches.Count-ne1-or-not$matches[0].winner){throw "ReferenceSuppressionTargetUnresolved: $($record.displayFormId)"}
        $winner=$matches[0].winner
        if([string]$winner.signature -cne [string]$record.signature -or [string]$winner.pluginName -cne [string]$record.winningPlugin -or [uint32]$winner.recordFlags -ne [uint32]$record.expectedRecordFlags -or -not[bool]$winner.isEnabled -or [bool]$winner.isDeleted -or [bool]$winner.isInitiallyDisabled){throw "ReferenceSuppressionWinnerChanged: $($record.displayFormId)"}
    }
    foreach($root in @($ExecutionContext.modsRoot,$ExecutionContext.overwriteRoot,$ExecutionContext.gameDataRoot)){
        if(-not(Test-Path -LiteralPath $root -PathType Container)){throw "ReferenceSuppressionOutputRootMissing: $root"}
        if(((Get-Item -LiteralPath $root -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0){throw "ReferenceSuppressionOutputRootReparsePoint: $root"}
    }
    if(Test-Path -LiteralPath $ExecutionContext.patchModDirectory){throw 'ReferenceSuppressionPatchModCollision: the dedicated patch mod directory already exists.'}
    foreach($path in @($ExecutionContext.overwritePatchPath,$ExecutionContext.gameDataPatchPath,$ExecutionContext.patchDestinationPath)){if(Test-Path -LiteralPath $path){throw "ReferenceSuppressionPatchCollision: $path"}}
    $graph
}

function New-GridReferenceSuppressionTransport {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Specification,[Parameter(Mandatory)][string]$TransactionDirectory)
    $path=Join-Path $TransactionDirectory 'writer-specification.v1.tsv'
    $lines=New-Object Collections.Generic.List[string]
    $lines.Add(('GRID_REFERENCE_SUPPRESSION{0}1{0}{1}{0}{2}'-f"`t",[string]$Specification.patchPluginName,[string]$Specification.specificationSha256))
    foreach($record in @($Specification.records|Sort-Object originPlugin,localFormId)){
        $fields=@([string]$record.originPlugin,('{0:X6}'-f[uint32]$record.localFormId),[string]$record.winningPlugin,[string]$record.signature)
        foreach($field in $fields){if([string]::IsNullOrWhiteSpace($field)-or$field.IndexOfAny([char[]]@(0,9,10,13))-ge0){throw 'ReferenceSuppressionTransportFieldInvalid.'}}
        $lines.Add(($fields-join"`t"))
    }
    [IO.File]::WriteAllLines($path,$lines.ToArray(),(New-Object Text.UTF8Encoding($false)))
    [pscustomobject]@{path=$path;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant();rowCount=$lines.Count-1}
}

function New-GridMo2ReferenceSuppressionInvocation {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ExecutableTitle,[Parameter(Mandatory)][string]$PluginsPath,[Parameter(Mandatory)][string]$SpecificationPath,[Parameter(Mandatory)][string]$ReceiptPath,[Parameter(Mandatory)][string]$StatusPath,[Parameter(Mandatory)][string]$PatchName,[Parameter(Mandatory)][string]$SpecificationSha256)
    foreach($value in @($ExecutableTitle,$PluginsPath,$SpecificationPath,$ReceiptPath,$StatusPath,$PatchName,$SpecificationSha256)){if($value.Contains('"')-or$value.IndexOfAny([char[]]@(0,10,13))-ge0){throw 'ReferenceSuppressionInvocationArgumentInvalid.'}}
    $caseRoot=[IO.Path]::GetFullPath((Split-Path -Parent $SpecificationPath)).TrimEnd('\')
    foreach($path in @($SpecificationPath,$ReceiptPath,$StatusPath)){if(-not[IO.Path]::GetFullPath($path).StartsWith($caseRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'ReferenceSuppressionInvocationPathEscapesTransaction.'}}
    $xedit='-P:"{0}" -autoload -autoexit -script:"Write-GridReferenceSuppression.pas" -gridspec:"{1}" -gridreceipt:"{2}" -gridstatus:"{3}" -gridpatch:"{4}" -gridspechash:{5}'-f$PluginsPath,$SpecificationPath,$ReceiptPath,$StatusPath,$PatchName,$SpecificationSha256
    $tokens=@('run','-e',$ExecutableTitle,'-a',$xedit)
    [pscustomobject]@{xEditArguments=$xedit;argumentString=[string]::Join(' ',@($tokens|ForEach-Object{ConvertTo-GridWindowsCommandLineArgument $_}))}
}

function Invoke-GridReferenceSuppressionWriterProcess {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Specification,[Parameter(Mandatory)]$ExecutionContext,[Parameter(Mandatory)][string]$TransactionDirectory,[int]$TimeoutSeconds=900)
    $writer=Get-GridXEditReferenceSuppressionWriterProvisioningState -ConfigurationPath $ExecutionContext.configurationPath -ExecutableTitle SSEEdit -GridDataRoot $ExecutionContext.gridDataRoot
    if([string]$writer.State-ne'Ready'){throw "ReferenceSuppressionWriterNotReady: state is '$($writer.State)'; provision the reviewed writer under separate one-use authorization."}
    $context=Get-GridMO2Context -Mo2Root $ExecutionContext.mo2Root -Profile $ExecutionContext.profileName -CaseDirectory $TransactionDirectory
    $selection=New-GridXEditPluginSelection -Context $context -CaseDirectory $TransactionDirectory -Targets @($Specification.records|ForEach-Object{[string]$_.winningPlugin}|Sort-Object -Unique)
    $transport=New-GridReferenceSuppressionTransport -Specification $Specification -TransactionDirectory $TransactionDirectory
    $receiptPath=Join-Path $TransactionDirectory 'xedit-writer-receipt.v1.tsv';$statusPath=Join-Path $TransactionDirectory 'xedit-writer-status.v1.tsv'
    $invocation=New-GridMo2ReferenceSuppressionInvocation -ExecutableTitle SSEEdit -PluginsPath $selection.PluginsPath -SpecificationPath $transport.path -ReceiptPath $receiptPath -StatusPath $statusPath -PatchName ([string]$Specification.patchPluginName) -SpecificationSha256 ([string]$Specification.specificationSha256)
    $definition=Get-GridMo2ConfiguredExecutable -ConfigurationPath $ExecutionContext.configurationPath -Title SSEEdit
    $mo2Executable=Join-Path $ExecutionContext.mo2Root 'ModOrganizer.exe';$startedAt=[DateTime]::UtcNow
    $pre=@(Get-GridXEditRunningProcesses);if($pre.Count){throw 'ReferenceSuppressionXEditAlreadyRunning.'}
    $manager=Start-GridProcessWithSerializedArguments -FilePath $mo2Executable -ArgumentString $invocation.argumentString -WorkingDirectory $ExecutionContext.mo2Root
    $launch=[ordered]@{schemaVersion=1;startedAt=$startedAt.ToString('o');managerProcessId=$manager.Id;xEditProcessId=$null;configuredExecutable=$definition.Binary;arguments=$invocation.xEditArguments;outcome='Starting'}
    $bound=$null;$deadline=(Get-Date).AddSeconds($TimeoutSeconds)
    while((Get-Date)-lt$deadline){
        if(-not$bound){$resolution=Resolve-GridXEditLaunchProcess -Candidates @(Get-GridXEditRunningProcesses) -ConfiguredExecutable $definition.Binary -LaunchBoundaryUtc $startedAt -ManagerProcessId $manager.Id -PrelaunchIdentityKeys @();if([string]$resolution.Status-eq'Ambiguous'){throw 'ReferenceSuppressionXEditIdentityAmbiguous.'};if([string]$resolution.Status-eq'Bound'){$bound=$resolution.Identity;$launch.xEditProcessId=[int]$bound.ProcessId}}
        if($bound){$process=Get-Process -Id ([int]$bound.ProcessId) -ErrorAction SilentlyContinue;if(-not$process){break};if(-not(Test-GridXEditProcessOwnershipMatch -Ownership @{ProcessId=$bound.ProcessId;ExecutablePath=$bound.ExecutablePath;StartTime=$bound.StartTimeUtc} -Process $process)){throw 'ReferenceSuppressionXEditOwnershipChanged.'}}
        Start-Sleep -Milliseconds 250
    }
    if(-not$bound){$launch.outcome='XEditNotObserved';$launch|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $TransactionDirectory 'xedit-writer-launch.v1.json') -Encoding UTF8;throw 'ReferenceSuppressionXEditNotObserved.'}
    if(Get-Process -Id ([int]$bound.ProcessId) -ErrorAction SilentlyContinue){$launch.outcome='TimedOutIndeterminate';$launch|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $TransactionDirectory 'xedit-writer-launch.v1.json') -Encoding UTF8;throw 'ReferenceSuppressionXEditTimedOutIndeterminate: the owned writer is still running; Grid did not terminate it or touch its output.'}
    $launch.outcome='Exited';$launch|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $TransactionDirectory 'xedit-writer-launch.v1.json') -Encoding UTF8
    if(-not(Test-Path -LiteralPath $receiptPath -PathType Leaf)){throw 'ReferenceSuppressionWriterReceiptMissing.'}
    $receipt=@([IO.File]::ReadAllLines($receiptPath));$completed=@($receipt|Where-Object{$_-match'^Completed\t'})
    if($completed.Count-ne1-or$completed[0]-cne("Completed`t"+@($Specification.records).Count)){throw 'ReferenceSuppressionWriterReceiptInvalid.'}
    if(-not(Test-Path -LiteralPath $ExecutionContext.overwritePatchPath -PathType Leaf)){throw 'ReferenceSuppressionWriterOutputMissing: no patch appeared in the configured MO2 overwrite root.'}
    [pscustomobject]@{receiptPath=$receiptPath;statusPath=$statusPath;transport=$transport;selection=$selection;launchPath=Join-Path $TransactionDirectory 'xedit-writer-launch.v1.json'}
}

function Invoke-GridAuthorizedReferenceSuppression {
    [CmdletBinding(SupportsShouldProcess,ConfirmImpact='High')]
    param([Parameter(Mandatory)][string]$SpecificationPath,[Parameter(Mandatory)][string]$CurrentContextFingerprint,[Parameter(Mandatory)][string]$AuthorizationGrantId,[Parameter(Mandatory)][string]$AuthorizationSecret,[Parameter(Mandatory)][string]$AuthorizationStoreRoot,[Parameter(Mandatory)][string]$CaseStoreRoot,[Parameter(Mandatory)]$SemanticBinding,[Parameter(Mandatory)][string]$TransactionRoot,[int]$TimeoutSeconds=900,[switch]$PassThru)
    $path=[IO.Path]::GetFullPath($SpecificationPath);if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw "ReferenceSuppressionSpecificationMissing: $path"}
    $spec=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json -ErrorAction Stop
    if([int]$spec.schemaVersion-ne2-or[string]$spec.status-cne'AwaitingAuthorization'){throw 'ReferenceSuppressionSpecificationInvalid.'}
    $sha=Get-GridSkyrimReferenceSuppressionSpecificationHash -Specification $spec;if([string]$spec.specificationSha256-cne$sha){throw 'ReferenceSuppressionSpecificationDigestMismatch.'}
    if([string]$spec.contextFingerprint-cne$CurrentContextFingerprint){throw 'ReferenceSuppressionStaleProposal: context fingerprint changed.'}
    $execution=Get-GridReferenceSuppressionExecutionContext -Specification $spec -CaseStoreRoot $CaseStoreRoot
    [void](Test-GridReferenceSuppressionEvidenceCurrent -Specification $spec -CaseStoreRoot $CaseStoreRoot -ExecutionContext $execution)
    Test-GridReferenceSuppressionProcessesClosed
    $writer=Get-GridXEditReferenceSuppressionWriterProvisioningState -ConfigurationPath $execution.configurationPath -ExecutableTitle SSEEdit -GridDataRoot $execution.gridDataRoot
    if([string]$writer.State-ne'Ready'){throw "ReferenceSuppressionWriterNotReady: $($writer.State)."}
    $targets=@(Get-GridReferenceSuppressionTargets -Specification $spec -ExecutionContext $execution)
    if([string]$SemanticBinding.proposalOrSpecificationId-cne[string]$spec.specificationId-or[string]$SemanticBinding.proposalOrSpecificationSha256-cne[string]$spec.specificationSha256){throw 'AuthorizationBindingMismatch: reference-suppression specification identity changed.'}
    if([string]$SemanticBinding.normalizedInputSha256-cne(Get-GridCanonicalJsonSha256 -InputObject $spec)){throw 'AuthorizationBindingMismatch: reference-suppression inputs changed.'}
    if(@($SemanticBinding.capabilities|Where-Object{[string]$_.capabilityId-ceq'grid.game.skyrimspecialedition.reference-suppression.execute'-and[string]$_.capabilityVersion-ceq'1.0.0'}).Count-ne1){throw 'AuthorizationBindingMismatch: reference-suppression capability/version is not authorized.'}
    $bound=@($SemanticBinding.targets|ForEach-Object{[string]$_}|Sort-Object -Unique);$expected=@($targets|Sort-Object -Unique);if(($bound-join"`n")-cne($expected-join"`n")){throw 'AuthorizationBindingMismatch: exact reference or patch targets changed.'}
    $transaction=[IO.Path]::GetFullPath((Join-Path $TransactionRoot ('reference-suppression-'+[string]$spec.specificationSha256)))
    if(Test-Path -LiteralPath $transaction){throw 'ReferenceSuppressionTransactionCollision.'}
    $lease=Enter-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -AuthorizationSecret $AuthorizationSecret -ExpectedSemanticBinding $SemanticBinding -ConsumerId ('reference-suppression-'+[string]$spec.specificationId)
    if(-not$PSCmdlet.ShouldProcess([string]$execution.patchDestinationPath,"Create one exact reference-suppression patch for $(@($spec.records).Count) record(s)")){Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Consumed -Detail 'WhatIf: no external mutation.'|Out-Null;return [pscustomobject]@{State='Incomplete';ChangedExternalState=$false}}
    New-Item -ItemType Directory -Path $transaction -Force|Out-Null
    $moved=$false
    try{
        Test-GridReferenceSuppressionProcessesClosed
        [void](Test-GridReferenceSuppressionEvidenceCurrent -Specification $spec -CaseStoreRoot $CaseStoreRoot -ExecutionContext $execution)
        $writerResult=Invoke-GridReferenceSuppressionWriterProcess -Specification $spec -ExecutionContext $execution -TransactionDirectory $transaction -TimeoutSeconds $TimeoutSeconds
        New-Item -ItemType Directory -Path $execution.patchModDirectory -ErrorAction Stop|Out-Null
        Move-Item -LiteralPath $execution.overwritePatchPath -Destination $execution.patchDestinationPath -ErrorAction Stop;$moved=$true
        $patchSha=(Get-FileHash -LiteralPath $execution.patchDestinationPath -Algorithm SHA256).Hash.ToUpperInvariant();$afterImage=Join-Path $transaction 'patch.after.esp'
        Copy-Item -LiteralPath $execution.patchDestinationPath -Destination $afterImage -ErrorAction Stop
        if((Get-FileHash -LiteralPath $afterImage -Algorithm SHA256).Hash.ToUpperInvariant()-cne$patchSha){throw 'ReferenceSuppressionAfterImageVerificationFailed.'}
        $receiptPath=Join-Path $transaction 'receipt.v1.json'
        $receipt=[pscustomobject][ordered]@{schemaVersion=1;transactionId=('reference-suppression-'+[string]$spec.specificationSha256);specificationId=[string]$spec.specificationId;specificationSha256=[string]$spec.specificationSha256;state='Applied';patchModDirectory=[string]$execution.patchModDirectory;patchPath=[string]$execution.patchDestinationPath;patchSha256=$patchSha;recordCount=@($spec.records).Count;records=@($spec.records|ForEach-Object{[pscustomobject]@{displayFormId=$_.displayFormId;action=$_.action;requestedInitiallyDisabled=$_.requestedInitiallyDisabled}});xEditReceiptPath=[string]$writerResult.receiptPath;createdAt=[DateTimeOffset]::UtcNow.ToString('o');modEnabled=$false;pluginEnabled=$false;runtimeVerified=$false}
        Write-GridJsonAtomic -InputObject $receipt -LiteralPath $receiptPath
        Write-GridJsonAtomic -InputObject ([pscustomobject][ordered]@{schemaVersion=1;transactionId=$receipt.transactionId;state='Applied';revision=0;currentPatchSha256=$patchSha;lastReceiptPath=$receiptPath}) -LiteralPath (Join-Path $transaction 'history-state.v1.json')
        Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Consumed -Detail "Created and verified inert patch $($execution.patchDestinationPath); activation and runtime verification remain separate."|Out-Null
        $result=[pscustomobject][ordered]@{State='Applied';ChangedExternalState=$true;PatchPath=$execution.patchDestinationPath;PatchSha256=$patchSha;ReceiptPath=$receiptPath;ModEnabled=$false;PluginEnabled=$false;RuntimeVerified=$false}
    }catch{
        $failure=$_.Exception.Message
        $running=@(Get-GridXEditRunningProcesses)
        if($running.Count){try{Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Failed -Detail ('Indeterminate while xEdit remains open: '+$failure)|Out-Null}catch{};throw "ReferenceSuppressionIndeterminate: $failure"}
        try{if($moved-and(Test-Path -LiteralPath $execution.patchDestinationPath -PathType Leaf)){Remove-Item -LiteralPath $execution.patchDestinationPath -Force};if(Test-Path -LiteralPath $execution.overwritePatchPath -PathType Leaf){Remove-Item -LiteralPath $execution.overwritePatchPath -Force};if(Test-Path -LiteralPath $execution.gameDataPatchPath -PathType Leaf){Remove-Item -LiteralPath $execution.gameDataPatchPath -Force};if(Test-Path -LiteralPath $execution.patchModDirectory -PathType Container){$remaining=@(Get-ChildItem -LiteralPath $execution.patchModDirectory -Force);if($remaining.Count-eq0){Remove-Item -LiteralPath $execution.patchModDirectory -Force}}}catch{$failure+=' Rollback failed: '+$_.Exception.Message}
        try{Complete-GridAuthorizationLease -StoreRoot $AuthorizationStoreRoot -GrantId $AuthorizationGrantId -LeaseId ([string]$lease.Lease.leaseId) -Result Failed -Detail $failure|Out-Null}catch{}
        throw "ReferenceSuppressionMutationFailedRolledBack: $failure"
    }
    if($PassThru){$result}else{$result|ConvertTo-Json -Depth 30}
}
