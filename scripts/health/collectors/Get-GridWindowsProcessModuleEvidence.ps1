#requires -Version 5.1
<#
.SYNOPSIS
Observes modules of an already-running process whose executable path exactly
matches the authorized executable.
.DESCRIPTION
This collector never starts, stops, attaches a debugger to, suspends, or changes
a process. Process absence, access denial, and a process changing during the
observation are first-class evidence states.
#>

function Get-GridWindowsProcessModuleEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ExecutablePath,
        [string]$ExecutableName,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ContextFingerprint,
        [ValidateRange(1, 4096)][int]$MaxModules = 512,
        [object[]]$ProcessRecords
    )
    $started = [datetime]::UtcNow
    $identity = Resolve-GridWindowsExecutableIdentity -ExecutablePath $ExecutablePath -ExecutableName $ExecutableName
    $scope = [pscustomobject][ordered]@{
        executableName=$identity.executableName; executablePath=$identity.executablePath
        maxProcesses=16; maxModules=$MaxModules; startsProcess=$false; controlsProcess=$false
    }
    $items = New-Object Collections.Generic.List[object]
    $warnings = New-Object Collections.Generic.List[string]
    $identityUnavailable = 0
    try {
        $candidates = if ($PSBoundParameters.ContainsKey('ProcessRecords')) {
            @($ProcessRecords)
        }
        else {
            @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($identity.executableName)) -ErrorAction SilentlyContinue | Select-Object -First 16)
        }
        $matched = 0
        foreach ($process in $candidates) {
            $processId = Get-GridWindowsObjectValue $process 'Id'
            $path = $null
            $startTime = $null
            try {
                $path = [string](Get-GridWindowsObjectValue $process 'Path')
                if ([string]::IsNullOrWhiteSpace($path)) {
                    $mainModule = Get-GridWindowsObjectValue $process 'MainModule'
                    if ($mainModule) { $path = [string](Get-GridWindowsObjectValue $mainModule 'FileName') }
                }
                $startTime = Get-GridWindowsObjectValue $process 'StartTime'
                if ([string]::IsNullOrWhiteSpace($path) -or [IO.Path]::GetFullPath($path) -ine $identity.executablePath) { continue }
            }
            catch {
                $warnings.Add("Process identity was inaccessible for PID $processId.")
                $identityUnavailable++
                $items.Add((New-GridWindowsCollectorEvidence -Parameter 'runningProcessModules' -Value 0 `
                    -Claim 'The executable identity of a candidate running process was inaccessible.' `
                    -SourceType 'WindowsProcessObservation' -SourceIdentifier "windows-process://$processId/identity" `
                    -ContextFingerprint $ContextFingerprint -VerificationStatus Unavailable -CollectorName 'Grid.Windows.ProcessModules' `
                    -Native ([pscustomobject][ordered]@{ processId=[int]$processId; executablePath=$identity.executablePath; availability='Unavailable'; errorType=$_.Exception.GetType().FullName })))
                continue
            }
            $matched++
            try {
                $allModuleObjects = @(Get-GridWindowsObjectValue $process 'Modules')
                if ($allModuleObjects.Count -eq 1 -and $allModuleObjects[0] -is [Collections.IEnumerable] -and -not ($allModuleObjects[0] -is [string])) {
                    $allModuleObjects = @($allModuleObjects[0])
                }
                $moduleObjects = @($allModuleObjects | Select-Object -First $MaxModules)
                $modules = New-Object Collections.Generic.List[object]
                foreach ($module in $moduleObjects) {
                    $fileName = [string](Get-GridWindowsObjectValue $module 'FileName')
                    $moduleName = [string](Get-GridWindowsObjectValue $module 'ModuleName')
                    $versionInfo = Get-GridWindowsObjectValue $module 'FileVersionInfo'
                    $modules.Add([pscustomobject][ordered]@{
                        moduleName = if ($moduleName) { $moduleName } elseif ($fileName) { [IO.Path]::GetFileName($fileName) } else { $null }
                        path = if ($fileName) { [IO.Path]::GetFullPath($fileName) } else { $null }
                        fileVersion = if ($versionInfo) { [string](Get-GridWindowsObjectValue $versionInfo 'FileVersion') } else { $null }
                        productVersion = if ($versionInfo) { [string](Get-GridWindowsObjectValue $versionInfo 'ProductVersion') } else { $null }
                    })
                }
                $unstable = $false
                if (-not $PSBoundParameters.ContainsKey('ProcessRecords')) {
                    $current = Get-Process -Id ([int]$processId) -ErrorAction SilentlyContinue
                    if ($null -eq $current) { $unstable = $true }
                    elseif ($startTime -and $current.StartTime.ToUniversalTime() -ne ([datetime]$startTime).ToUniversalTime()) { $unstable = $true }
                }
                $verification = if ($unstable) { 'Stale' } else { 'Collected' }
                $claim = if ($unstable) {
                    'The exact process changed or exited during module observation; the module list is stale.'
                } else {
                    'Modules were observed from an already-running process with the exact authorized executable path.'
                }
                $items.Add((New-GridWindowsCollectorEvidence -Parameter 'runningProcessModules' -Value 1 -Claim $claim `
                    -SourceType 'WindowsProcessObservation' -SourceIdentifier "windows-process://$processId/modules" `
                    -ContextFingerprint $ContextFingerprint -VerificationStatus $verification -CollectorName 'Grid.Windows.ProcessModules' `
                    -Native ([pscustomobject][ordered]@{
                        processId=[int]$processId; executablePath=$identity.executablePath
                        processStartTimeUtc=if($startTime){([datetime]$startTime).ToUniversalTime().ToString('o')}else{$null}
                        moduleCount=$modules.Count; truncated=($allModuleObjects.Count -gt $MaxModules); modules=$modules.ToArray()
                        stability=if($unstable){'ChangedDuringRead'}else{'Stable'}
                    })))
            }
            catch {
                $warnings.Add("Module observation was unavailable for PID ${processId}: $($_.Exception.Message)")
                $items.Add((New-GridWindowsCollectorEvidence -Parameter 'runningProcessModules' -Value 0 `
                    -Claim 'Module evidence for the exact already-running process was inaccessible.' `
                    -SourceType 'WindowsProcessObservation' -SourceIdentifier "windows-process://$processId/modules" `
                    -ContextFingerprint $ContextFingerprint -VerificationStatus Unavailable -CollectorName 'Grid.Windows.ProcessModules' `
                    -Native ([pscustomobject][ordered]@{ processId=[int]$processId; executablePath=$identity.executablePath; availability='Unavailable'; errorType=$_.Exception.GetType().FullName })))
            }
        }
        if ($matched -eq 0 -and $identityUnavailable -eq 0) {
            $items.Add((New-GridWindowsCollectorEvidence -Parameter 'runningProcessModules' -Value 0 `
                -Claim 'No already-running process with the exact authorized executable path was observed.' `
                -SourceType 'WindowsProcessObservation' -SourceIdentifier ('windows-process-query://' + (Get-GridWindowsSha256Text $identity.executablePath)) `
                -ContextFingerprint $ContextFingerprint -VerificationStatus Collected -CollectorName 'Grid.Windows.ProcessModules' `
                -Native ([pscustomobject][ordered]@{ executablePath=$identity.executablePath; matchingProcessCount=0; processStartedByCollector=$false })))
        }
        $status = if (@($items | Where-Object verificationStatus -eq 'Unavailable').Count -eq $items.Count) { 'Unavailable' } `
            elseif (@($items | Where-Object verificationStatus -in @('Unavailable','Stale')).Count -gt 0) { 'Partial' } else { 'Complete' }
        New-GridWindowsEvidenceCollectionResult -CollectorName 'Grid.Windows.ProcessModules' -Status $status -Scope $scope `
            -Evidence $items.ToArray() -Warnings $warnings.ToArray() -StartedAt $started -CompletedAt ([datetime]::UtcNow)
    }
    catch {
        $warnings.Add("Process-module collection unavailable: $($_.Exception.Message)")
        $items.Add((New-GridWindowsCollectorEvidence -Parameter 'runningProcessModules' -Value 0 `
            -Claim 'Running-process module evidence was unavailable for the authorized executable.' `
            -SourceType 'WindowsProcessObservation' -SourceIdentifier ('windows-process-query://' + (Get-GridWindowsSha256Text $identity.executablePath)) `
            -ContextFingerprint $ContextFingerprint -VerificationStatus Unavailable -CollectorName 'Grid.Windows.ProcessModules' `
            -Native ([pscustomobject][ordered]@{ executablePath=$identity.executablePath; availability='Unavailable'; errorType=$_.Exception.GetType().FullName })))
        New-GridWindowsEvidenceCollectionResult -CollectorName 'Grid.Windows.ProcessModules' -Status Unavailable -Scope $scope `
            -Evidence $items.ToArray() -Warnings $warnings.ToArray() -StartedAt $started -CompletedAt ([datetime]::UtcNow)
    }
}
