#requires -Version 5.1
<#
.SYNOPSIS
Runs one bounded, disposable Skyrim runtime-certification session.
.DESCRIPTION
Materializes an isolated MO2 profile, launches the exact approved executable route,
executes only allowlisted ephemeral input, captures bounded evidence, closes only
revalidated owned processes, preserves the disposable profile as case evidence, and
submits the resulting receipts to Invoke-GridSkyrimRuntimeCertification.
.EXAMPLE
Invoke-GridSkyrimRuntimeCertificationSession -CaseId cert-fixture -Recipe $recipe `
    -ProfilePlan $plan -LineageBinding $lineage -PrelaunchGates $gates `
    -RollbackReceipt $rollback -LaunchInvocation $launch -Authorization $authorization `
    -ExpectedGameExecutablePath 'D:\Game\SkyrimSE.exe' -ExpectedGameExecutableSha256 $hash `
    -SessionRoot 'C:\Grid\transactions\cert-fixture'
#>
Set-StrictMode -Version Latest

$script:GridSkyrimRuntimeSessionDigestPattern = '^[A-F0-9]{64}$'

function Assert-GridSkyrimRuntimeSessionOrdinaryDirectory {
    param([Parameter(Mandatory)][string]$LiteralPath, [Parameter(Mandatory)][string]$Name)
    $full = [IO.Path]::GetFullPath($LiteralPath).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $full -PathType Container)) { throw "RuntimeSessionInvalid: $Name is missing: $full" }
    $item = Get-Item -LiteralPath $full -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "RuntimeSessionBoundaryRefused: $Name is a reparse point: $full" }
    $full
}

function Assert-GridSkyrimRuntimeSessionFileIdentity {
    param(
        [Parameter(Mandatory)][string]$LiteralPath,
        [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$ExpectedSha256,
        [Parameter(Mandatory)][string]$Name
    )
    $full = [IO.Path]::GetFullPath($LiteralPath)
    $item = Get-Item -LiteralPath $full -Force -ErrorAction Stop
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "RuntimeSessionBoundaryRefused: $Name must be one ordinary file."
    }
    $observed = (Get-FileHash -LiteralPath $full -Algorithm SHA256 -ErrorAction Stop).Hash
    if ($observed -cne $ExpectedSha256) { throw "RuntimeSessionStale: $Name hash differs from the approved identity." }
    [pscustomobject][ordered]@{ path=$full; sizeBytes=[long]$item.Length; sha256=$observed; lastWriteTimeUtc=$item.LastWriteTimeUtc.ToString('o') }
}

function Test-GridSkyrimRuntimeSessionAuthorization {
    param($Authorization, [Parameter(Mandatory)][string]$RepairFingerprint)
    if (-not $Authorization -or [string](Get-GridSkyrimRuntimePropertyValue $Authorization 'status') -ne 'Authorized') { return $false }
    if ([string](Get-GridSkyrimRuntimePropertyValue $Authorization 'scope') -ne 'ExactRepairAndRuntimeCertification') { return $false }
    if ([string](Get-GridSkyrimRuntimePropertyValue $Authorization 'repairFingerprint') -cne $RepairFingerprint) { return $false }
    [string](Get-GridSkyrimRuntimePropertyValue $Authorization 'authorizationSha256') -cmatch $script:GridSkyrimRuntimeSessionDigestPattern
}

function Test-GridSkyrimRuntimeSessionConsoleCommand {
    param([Parameter(Mandatory)][string]$Command)
    if ($Command.Length -gt 128 -or $Command -match '[\r\n\x00-\x1F]') { return $false }
    $Command -cmatch '^(?:coc [A-Za-z0-9_]{1,64}|prid [0-9A-Fa-f]{1,8}|getpos [xyz]|getangle [xyz]|getscale)$'
}

function Get-GridSkyrimRuntimeSessionProcessSnapshot {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string[]]$ExecutableLeafNames)
    $leafSet=@{}; foreach($leaf in $ExecutableLeafNames){ $leafSet[$leaf.ToLowerInvariant()]=$true }
    $rows=New-Object Collections.Generic.List[object]
    foreach($process in @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object { $leafSet.ContainsKey(([string]$_.Name).ToLowerInvariant()) })) {
        $path=[string]$process.ExecutablePath
        if ([string]::IsNullOrWhiteSpace($path)) { continue }
        $start=$null
        try { $start=[Management.ManagementDateTimeConverter]::ToDateTime([string]$process.CreationDate).ToUniversalTime() } catch { continue }
        $ancestors=New-Object Collections.Generic.List[int]
        $seen=@{}; $parent=[int]$process.ParentProcessId; $depth=0
        while($parent -gt 0 -and $depth -lt 32 -and -not $seen.ContainsKey($parent)) {
            $seen[$parent]=$true; $ancestors.Add($parent); $depth++
            $parentRow=Get-CimInstance Win32_Process -Filter "ProcessId = $parent" -ErrorAction SilentlyContinue
            if (-not $parentRow) { break }
            $parent=[int]$parentRow.ParentProcessId
        }
        $rows.Add([pscustomobject][ordered]@{ processId=[int]$process.ProcessId; executablePath=[IO.Path]::GetFullPath($path); startTimeUtc=$start.ToString('o'); ancestorProcessIds=$ancestors.ToArray() })
    }
    @($rows | Sort-Object processId)
}

function New-GridSkyrimRuntimeSessionProfile {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact='High')]
    param([Parameter(Mandatory)]$ProfilePlan)
    $isolation=Test-GridSkyrimDisposableProfileIsolation -Plan $ProfilePlan -RequireExistingRoots
    if ([string]$isolation.status -ne 'Valid') { throw "RuntimeSessionBoundaryRefused: $(@($isolation.issues) -join '; ')" }
    $target=[IO.Path]::GetFullPath([string]$ProfilePlan.targetProfileDirectory)
    if (Test-Path -LiteralPath $target) {
        $existing=Test-GridSkyrimDisposableProfileMaterialization -Plan $ProfilePlan
        if ([string]$existing.status -ne 'Verified') { throw 'RuntimeSessionStale: an unverified disposable profile already occupies the deterministic target.' }
        return $existing
    }
    if (-not $PSCmdlet.ShouldProcess($target, 'Materialize isolated disposable MO2 profile')) {
        return [pscustomobject][ordered]@{ status='WhatIf'; planFingerprint=[string]$ProfilePlan.planFingerprint; targetProfileDirectory=$target; saveRootState='NotCreated'; issues=@() }
    }
    $staging=Join-Path ([string]$ProfilePlan.profilesRoot) ('.grid-runtime-staging-' + [guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $staging -ErrorAction Stop | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $staging 'saves') -ErrorAction Stop | Out-Null
        foreach($operation in @($ProfilePlan.copyOperations | Where-Object sourceState -eq 'Present')) {
            $destination=Join-Path $staging ([string]$operation.leafName)
            [IO.File]::Copy([string]$operation.sourcePath,$destination,$false)
            if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -cne [string]$operation.sourceSha256) { throw "RuntimeSessionVerificationFailed: copied profile file differs: $($operation.leafName)" }
        }
        Move-Item -LiteralPath $staging -Destination $target -ErrorAction Stop
        $receipt=Test-GridSkyrimDisposableProfileMaterialization -Plan $ProfilePlan
        if ([string]$receipt.status -ne 'Verified') { throw "RuntimeSessionVerificationFailed: $(@($receipt.issues) -join '; ')" }
        $receipt
    }
    catch {
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
        throw
    }
}

function ConvertTo-GridSkyrimRuntimeQuotedArgument {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Value)
    if ($Value -notmatch '[\s"]') { return $Value }
    '"' + ($Value -replace '(\\*)"','$1$1\"' -replace '(\\+)$','$1$1') + '"'
}

function Start-GridSkyrimRuntimeSessionProcess {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$LaunchInvocation)
    $startInfo=New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName=[string]$LaunchInvocation.executablePath
    $quotedArguments=@($LaunchInvocation.arguments | ForEach-Object { ConvertTo-GridSkyrimRuntimeQuotedArgument ([string]$_) })
    $startInfo.Arguments=$quotedArguments -join ' '
    $startInfo.UseShellExecute=$false
    $startInfo.WorkingDirectory=[IO.Path]::GetDirectoryName([string]$LaunchInvocation.executablePath)
    $process=New-Object Diagnostics.Process
    $process.StartInfo=$startInfo
    if (-not $process.Start()) { throw 'RuntimeSessionLaunchFailed: Mod Organizer did not start.' }
    $process
}

function Invoke-GridSkyrimRuntimeSessionInput {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$GameProcessId,
        [Parameter(Mandatory)]$Recipe,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$EphemeralConsoleCommands,
        [Parameter(Mandatory)][string]$CaptureRoot
    )
    Add-Type -AssemblyName System.Windows.Forms,System.Drawing -ErrorAction Stop
    if (-not ('GridRuntimeNativeWindow' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class GridRuntimeNativeWindow {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
  [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
  public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
}
'@
    }
    if ($EphemeralConsoleCommands.Count -gt 32) { throw 'RuntimeSessionBudgetExceeded: more than 32 console commands were requested.' }
    foreach($command in $EphemeralConsoleCommands) { if (-not (Test-GridSkyrimRuntimeSessionConsoleCommand $command)) { throw "RuntimeSessionInputRefused: console command is not allowlisted: $command" } }
    $process=Get-Process -Id $GameProcessId -ErrorAction Stop
    if ($process.MainWindowHandle -eq [IntPtr]::Zero -or -not [GridRuntimeNativeWindow]::SetForegroundWindow($process.MainWindowHandle)) { throw 'RuntimeSessionInputRefused: the exact owned game window could not be focused.' }
    Start-Sleep -Milliseconds 500
    $audit=New-Object Collections.Generic.List[object]
    foreach($command in $EphemeralConsoleCommands) {
        [GridRuntimeNativeWindow]::keybd_event(0xC0,0,0,[UIntPtr]::Zero); [GridRuntimeNativeWindow]::keybd_event(0xC0,0,2,[UIntPtr]::Zero)
        Start-Sleep -Milliseconds 200
        [Windows.Forms.SendKeys]::SendWait($command); [Windows.Forms.SendKeys]::SendWait('{ENTER}')
        $audit.Add([pscustomobject][ordered]@{ kind='ConsoleCommand'; value=$command; status='Sent'; timestampUtc=[DateTimeOffset]::UtcNow.ToString('o') })
        Start-Sleep -Milliseconds 750
    }
    $screens=New-Object Collections.Generic.List[string]
    $keyMap=@{MoveForward='W';MoveBackward='S';StrafeLeft='A';StrafeRight='D';TurnLeft='{LEFT}';TurnRight='{RIGHT}';LookUp='{UP}';LookDown='{DOWN}';Jump=' ';ToggleHud='';CloseMenu='{ESC}'}
    foreach($step in @($Recipe.navigation | Sort-Object sequence)) {
        $action=[string]$step.action; $duration=[int]$step.durationMilliseconds
        if ($action -eq 'Wait' -or $action -eq 'ObserveRuntimeReference') { if($duration -gt 0){Start-Sleep -Milliseconds $duration}; $audit.Add([pscustomobject][ordered]@{kind='RecipeAction';value=$action;status='Observed';timestampUtc=[DateTimeOffset]::UtcNow.ToString('o')}); continue }
        if ($action -eq 'CaptureScreenshot') {
            if ($screens.Count -ge [int]$Recipe.captures.maximumScreenshots) { throw 'RuntimeSessionBudgetExceeded: screenshot count exceeds the recipe.' }
            $process.Refresh(); $rect=New-Object GridRuntimeNativeWindow+Rect
            if (-not [GridRuntimeNativeWindow]::GetWindowRect($process.MainWindowHandle,[ref]$rect)) { throw 'RuntimeSessionCaptureFailed: game window bounds are unavailable.' }
            $width=$rect.Right-$rect.Left; $height=$rect.Bottom-$rect.Top
            if ($width -le 0 -or $height -le 0 -or [long]$width*$height -gt 67108864) { throw 'RuntimeSessionCaptureFailed: game window bounds are invalid or oversized.' }
            $bitmap=New-Object Drawing.Bitmap($width,$height,[Drawing.Imaging.PixelFormat]::Format24bppRgb)
            try { $graphics=[Drawing.Graphics]::FromImage($bitmap); try { $graphics.CopyFromScreen($rect.Left,$rect.Top,0,0,$bitmap.Size) } finally { $graphics.Dispose() }; $path=Join-Path $CaptureRoot ('view-{0:D3}.png' -f ([int]$step.sequence)); $bitmap.Save($path,[Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
            if ((Get-Item -LiteralPath $path).Length -gt [long]$Recipe.captures.maximumScreenshotBytes) { throw 'RuntimeSessionBudgetExceeded: screenshot exceeds the recipe byte ceiling.' }
            $screens.Add($path); $audit.Add([pscustomobject][ordered]@{kind='RecipeAction';value=$action;status='Captured';timestampUtc=[DateTimeOffset]::UtcNow.ToString('o')}); continue
        }
        $key=[string]$keyMap[$action]
        if ([string]::IsNullOrWhiteSpace($key)) { throw "RuntimeSessionInputRefused: action '$action' has no safe input mapping." }
        [Windows.Forms.SendKeys]::SendWait($key); if($duration -gt 0){Start-Sleep -Milliseconds $duration}
        $audit.Add([pscustomobject][ordered]@{kind='RecipeAction';value=$action;status='Sent';timestampUtc=[DateTimeOffset]::UtcNow.ToString('o')})
    }
    [pscustomobject][ordered]@{ audit=$audit.ToArray(); screenshots=$screens.ToArray() }
}

function Stop-GridSkyrimOwnedRuntimeSessionProcess {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$OwnershipObservation, [Parameter(Mandatory)][int]$ManagerProcessId, [ValidateRange(1,60)][int]$CloseSeconds=15)
    if ([string]$OwnershipObservation.state -ne 'Owned') { throw 'RuntimeSessionProcessControlRefused: game ownership is not exact.' }
    $game=Get-Process -Id ([int]$OwnershipObservation.process.processId) -ErrorAction SilentlyContinue
    if ($game) { [void]$game.CloseMainWindow(); if (-not $game.WaitForExit($CloseSeconds*1000)) { return [pscustomobject]@{status='CloseTimedOut';gameProcessId=$game.Id;managerProcessId=$ManagerProcessId} } }
    $manager=Get-Process -Id $ManagerProcessId -ErrorAction SilentlyContinue
    if ($manager) { [void]$manager.CloseMainWindow(); if (-not $manager.WaitForExit($CloseSeconds*1000)) { return [pscustomobject]@{status='CloseTimedOut';gameProcessId=[int]$OwnershipObservation.process.processId;managerProcessId=$ManagerProcessId} } }
    [pscustomobject]@{status='Closed';gameProcessId=[int]$OwnershipObservation.process.processId;managerProcessId=$ManagerProcessId}
}

function Move-GridSkyrimDisposableProfileToEvidence {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact='High')]
    param([Parameter(Mandatory)]$ProfilePlan, [Parameter(Mandatory)][string]$SessionRoot)
    $target=[IO.Path]::GetFullPath([string]$ProfilePlan.targetProfileDirectory).TrimEnd('\')
    $profiles=[IO.Path]::GetFullPath([string]$ProfilePlan.profilesRoot).TrimEnd('\')
    if (-not (Test-GridSkyrimRuntimeContainedPath -Path $target -Root $profiles -ImmediateChild) -or
        [IO.Path]::GetFileName($target) -notlike 'Grid Runtime *') { throw 'RuntimeSessionBoundaryRefused: only the exact Grid disposable profile may be preserved and removed.' }
    $materialization=Test-GridSkyrimDisposableProfileMaterialization -Plan $ProfilePlan
    if ([string]$materialization.status -ne 'Verified') { throw "RuntimeSessionVerificationFailed: disposable profile cannot be preserved: $(@($materialization.issues) -join '; ')" }
    $destination=Join-Path ([IO.Path]::GetFullPath($SessionRoot).TrimEnd('\')) 'disposable-profile'
    if (Test-Path -LiteralPath $destination) { throw 'RuntimeSessionReplayRefused: disposable-profile evidence already exists.' }
    if (-not $PSCmdlet.ShouldProcess($target,'Preserve the exact disposable profile as session evidence and remove it from MO2')) {
        return [pscustomobject]@{status='WhatIf';sourceProfileDirectory=$target;evidenceDirectory=$destination}
    }
    New-Item -ItemType Directory -Path $destination -ErrorAction Stop | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $destination 'saves') -ErrorAction Stop | Out-Null
    foreach($operation in @($ProfilePlan.copyOperations | Where-Object sourceState -eq 'Present')) {
        $source=Join-Path $target ([string]$operation.leafName); $copy=Join-Path $destination ([string]$operation.leafName)
        [IO.File]::Copy($source,$copy,$false)
        if ((Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash -cne [string]$operation.sourceSha256) { throw "RuntimeSessionVerificationFailed: preserved profile file differs: $($operation.leafName)" }
    }
    if (@(Get-ChildItem -LiteralPath (Join-Path $destination 'saves') -Force -ErrorAction Stop).Count -ne 0) { throw 'RuntimeSessionVerificationFailed: preserved disposable save root is not empty.' }
    Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop
    if (Test-Path -LiteralPath $target) { throw 'RuntimeSessionVerificationFailed: disposable profile remained in the MO2 profiles root.' }
    $files=@(Get-ChildItem -LiteralPath $destination -File -Force | Sort-Object Name | ForEach-Object { [pscustomobject][ordered]@{leafName=$_.Name;sizeBytes=[long]$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
    [pscustomobject][ordered]@{status='Preserved';sourceProfileDirectory=$target;evidenceDirectory=$destination;saveRootState='Empty';files=$files;semanticFingerprint=(Get-GridSkyrimRuntimeFingerprint @($files | ForEach-Object { "$($_.leafName)|$($_.sizeBytes)|$($_.sha256)" }))}
}

function Invoke-GridSkyrimRuntimeCertificationSession {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact='High')]
    param(
        [Parameter(Mandatory)][string]$CaseId,
        [Parameter(Mandatory)]$Recipe,
        [Parameter(Mandatory)]$ProfilePlan,
        [Parameter(Mandatory)]$LineageBinding,
        [Parameter(Mandatory)][object[]]$PrelaunchGates,
        [Parameter(Mandatory)]$RollbackReceipt,
        [Parameter(Mandatory)]$LaunchInvocation,
        [Parameter(Mandatory)]$Authorization,
        [Parameter(Mandatory)][string]$ExpectedGameExecutablePath,
        [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$ExpectedGameExecutableSha256,
        [Parameter(Mandatory)][string]$SessionRoot,
        [AllowEmptyCollection()][string[]]$EphemeralConsoleCommands=@(),
        [string]$OracleReceiptPath,
        [ValidateRange(10,300)][int]$LaunchTimeoutSeconds=90,
        [ValidateRange(1,60)][int]$CloseSeconds=15
    )
    [void](Assert-GridSkyrimRuntimeRecipe $Recipe)
    if (-not (Test-GridSkyrimRuntimeSessionAuthorization -Authorization $Authorization -RepairFingerprint ([string]$ProfilePlan.repairFingerprint))) { throw 'RuntimeSessionAuthorizationRequired: exact repair-bound runtime authority is absent or stale.' }
    if (-not (Test-GridSkyrimTypedProfileInvocation -Invocation $LaunchInvocation -ExpectedProfileName ([string]$ProfilePlan.profileName))) { throw 'RuntimeSessionInvalid: the typed MO2 profile invocation is invalid.' }
    if ([string](Get-GridSkyrimRuntimePropertyValue $LaunchInvocation 'executableSourceIndex') -notmatch '^\d+$') { throw 'RuntimeSessionInvalid: the configured executable source index is missing.' }
    $mo2Identity=Assert-GridSkyrimRuntimeSessionFileIdentity -LiteralPath ([string]$LaunchInvocation.executablePath) -ExpectedSha256 ([string]$LaunchInvocation.executableIdentity) -Name 'ModOrganizer.exe'
    $gameIdentity=Assert-GridSkyrimRuntimeSessionFileIdentity -LiteralPath $ExpectedGameExecutablePath -ExpectedSha256 $ExpectedGameExecutableSha256 -Name 'SkyrimSE.exe'
    $session=[IO.Path]::GetFullPath($SessionRoot).TrimEnd('\')
    if (Test-Path -LiteralPath $session) { throw 'RuntimeSessionReplayRefused: the deterministic session root already exists.' }
    $preflight=Invoke-GridSkyrimRuntimeCertification -CaseId $CaseId -Recipe $Recipe -ProfilePlan $ProfilePlan -LineageBinding $LineageBinding -PrelaunchGates $PrelaunchGates -RollbackReceipt $RollbackReceipt
    if ([string]$preflight.certificationEligibility -ne 'Eligible') { return $preflight }
    $before=Get-GridSkyrimRuntimeSessionProcessSnapshot -ExecutableLeafNames @([IO.Path]::GetFileName($mo2Identity.path),[IO.Path]::GetFileName($gameIdentity.path))
    if (@($before | Where-Object { [string]$_.executablePath -ieq [string]$gameIdentity.path }).Count -gt 0) { throw 'RuntimeSessionProcessRefused: a matching Skyrim process already exists.' }
    if (-not $PSCmdlet.ShouldProcess($session,'Create the disposable profile, launch the exact MO2/SKSE route, collect evidence, and close owned processes')) { return [pscustomobject][ordered]@{status='WhatIf';readyToPlay='NotEvaluated';runtimeValidation='NotStarted';sessionRoot=$session} }
    New-Item -ItemType Directory -Path $session -ErrorAction Stop | Out-Null
    $captureRoot=Join-Path $session 'screenshots'; $logRoot=Join-Path $session 'logs'
    New-Item -ItemType Directory -Path $captureRoot -ErrorAction Stop | Out-Null
    New-Item -ItemType Directory -Path $logRoot -ErrorAction Stop | Out-Null
    $profileReceipt=New-GridSkyrimRuntimeSessionProfile -ProfilePlan $ProfilePlan -Confirm:$false
    $manager=$null; $ownership=$null; $closeReceipt=$null; $inputReceipt=$null; $profileEvidence=$null
    try {
        $launchStarted=[datetime]::UtcNow
        $manager=Start-GridSkyrimRuntimeSessionProcess -LaunchInvocation $LaunchInvocation
        $deadline=[datetime]::UtcNow.AddSeconds($LaunchTimeoutSeconds)
        do {
            Start-Sleep -Milliseconds 500
            $after=Get-GridSkyrimRuntimeSessionProcessSnapshot -ExecutableLeafNames @([IO.Path]::GetFileName($gameIdentity.path))
            $ownership=Resolve-GridSkyrimOwnedRuntimeProcess -BeforeSnapshot $before -AfterSnapshot $after -ExpectedExecutablePath $gameIdentity.path -ManagerProcessId $manager.Id -LaunchStartedUtc $launchStarted
        } while ([string]$ownership.state -eq 'NotObserved' -and [datetime]::UtcNow -lt $deadline -and -not $manager.HasExited)
        if ([string]$ownership.state -ne 'Owned') { throw "RuntimeSessionLaunchFailed: $($ownership.reason)" }
        $inputReceipt=Invoke-GridSkyrimRuntimeSessionInput -GameProcessId ([int]$ownership.process.processId) -Recipe $Recipe -EphemeralConsoleCommands $EphemeralConsoleCommands -CaptureRoot $captureRoot
    }
    finally {
        if ($ownership -and [string]$ownership.state -eq 'Owned' -and $manager) { $closeReceipt=Stop-GridSkyrimOwnedRuntimeSessionProcess -OwnershipObservation $ownership -ManagerProcessId $manager.Id -CloseSeconds $CloseSeconds }
        elseif ($manager -and -not $manager.HasExited) { [void]$manager.CloseMainWindow() }
    }
    if (-not $closeReceipt -or [string]$closeReceipt.status -ne 'Closed') { throw 'RuntimeSessionProcessControlIncomplete: owned processes did not close normally; disposable profile remains for bounded recovery.' }
    $profileEvidence=Move-GridSkyrimDisposableProfileToEvidence -ProfilePlan $ProfilePlan -SessionRoot $session -Confirm:$false
    $recipeFingerprint=Get-GridSkyrimRuntimeRecipeFingerprint $Recipe
    $screens=Get-GridSkyrimRuntimeFileAudit -Kind Screenshot -LiteralPath @($inputReceipt.screenshots) -AllowedRoot $captureRoot -RecipeFingerprint $recipeFingerprint -ContextFingerprint ([string]$ProfilePlan.contextFingerprint) -RepairFingerprint ([string]$ProfilePlan.repairFingerprint) -MaximumFiles ([int]$Recipe.captures.maximumScreenshots) -MaximumFileBytes ([long]$Recipe.captures.maximumScreenshotBytes)
    $auditPath=Join-Path $logRoot 'runtime-input-audit.json'
    [IO.File]::WriteAllText($auditPath,($inputReceipt.audit | ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding($false)))
    $logs=Get-GridSkyrimRuntimeFileAudit -Kind Log -LiteralPath @($auditPath) -AllowedRoot $logRoot -RecipeFingerprint $recipeFingerprint -ContextFingerprint ([string]$ProfilePlan.contextFingerprint) -RepairFingerprint ([string]$ProfilePlan.repairFingerprint) -MaximumFiles 32 -MaximumFileBytes ([long]$Recipe.captures.maximumAggregateLogBytes) -MaximumAggregateBytes ([long]$Recipe.captures.maximumAggregateLogBytes)
    $oracle=$null
    if (-not [string]::IsNullOrWhiteSpace($OracleReceiptPath)) {
        $oracleFull=[IO.Path]::GetFullPath($OracleReceiptPath)
        if (-not (Test-GridSkyrimRuntimeContainedPath -Path $oracleFull -Root $session) -or -not (Test-Path -LiteralPath $oracleFull -PathType Leaf)) { throw 'RuntimeSessionBoundaryRefused: oracle receipt must be an exact file beneath the session root.' }
        $oracle=Get-Content -Raw -LiteralPath $oracleFull -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    }
    $result=Invoke-GridSkyrimRuntimeCertification -CaseId $CaseId -Recipe $Recipe -ProfilePlan $ProfilePlan -LineageBinding $LineageBinding -PrelaunchGates $PrelaunchGates -RollbackReceipt $RollbackReceipt -ProfileIsolationReceipt $profileReceipt -LaunchInvocation $LaunchInvocation -OwnershipObservation $ownership -OracleResult $oracle -ScreenshotAudit $screens -LogAudit $logs
    $result | Add-Member -NotePropertyName sessionRoot -NotePropertyValue $session
    $result | Add-Member -NotePropertyName processClose -NotePropertyValue $closeReceipt
    $result | Add-Member -NotePropertyName preservedDisposableProfile -NotePropertyValue $profileEvidence
    $result | Add-Member -NotePropertyName inputAuditSha256 -NotePropertyValue ((Get-FileHash -LiteralPath $auditPath -Algorithm SHA256).Hash)
    $result
}
