#requires -Version 5.1
<#
.SYNOPSIS
Collects bounded Windows Application Error and Application Hang records for an
exact executable identity.
.DESCRIPTION
The collector reads only the Application event channel. It never launches,
stops, debugs, or modifies a process and does not turn an event into a causal
claim.
#>

function Get-GridWindowsApplicationFailureEvidence {
    [CmdletBinding()]
    param(
        [string]$ExecutablePath,
        [string]$ExecutableName,
        [datetime]$SinceUtc = ([datetime]::UtcNow.AddDays(-30)),
        [datetime]$UntilUtc = [datetime]::UtcNow,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ContextFingerprint,
        [ValidateRange(1, 4096)][int]$MaxEvents = 256,
        [object[]]$EventRecords
    )
    $started = [datetime]::UtcNow
    $identity = Resolve-GridWindowsExecutableIdentity -ExecutablePath $ExecutablePath -ExecutableName $ExecutableName
    Assert-GridWindowsCollectionBounds -SinceUtc $SinceUtc -UntilUtc $UntilUtc -MaximumItems $MaxEvents
    $scope = [pscustomobject][ordered]@{
        channel = 'Application'
        providers = @('Application Error','Application Hang')
        eventIds = @(1000,1002)
        executableName = $identity.executableName
        executablePath = $identity.executablePath
        sinceUtc = $SinceUtc.ToUniversalTime().ToString('o')
        untilUtc = $UntilUtc.ToUniversalTime().ToString('o')
        maxEvents = $MaxEvents
    }
    $items = New-Object Collections.Generic.List[object]
    $warnings = New-Object Collections.Generic.List[string]
    try {
        $records = if ($PSBoundParameters.ContainsKey('EventRecords')) {
            @($EventRecords)
        }
        else {
            if (-not (Get-Command Get-WinEvent -ErrorAction SilentlyContinue)) { throw 'Get-WinEvent is unavailable on this host.' }
            @(Get-WinEvent -FilterHashtable @{
                LogName = 'Application'
                ProviderName = @('Application Error','Application Hang')
                Id = @(1000,1002)
                StartTime = $SinceUtc.ToLocalTime()
                EndTime = $UntilUtc.ToLocalTime()
            } -MaxEvents $MaxEvents -ErrorAction Stop)
        }
        $matched = 0
        foreach ($record in @($records | Select-Object -First $MaxEvents)) {
            $normalized = ConvertFrom-GridWindowsEventRecord -Record $record
            if ($normalized.id -notin @(1000,1002) -or $normalized.providerName -notin @('Application Error','Application Hang')) { continue }
            if (-not (Test-GridWindowsEventExecutableMatch -NormalizedRecord $normalized -ExecutableName $identity.executableName)) { continue }
            $when = if ($normalized.timeCreated) { ([datetime]$normalized.timeCreated).ToUniversalTime() } else { [datetime]::UtcNow }
            if ($when -lt $SinceUtc.ToUniversalTime() -or $when -gt $UntilUtc.ToUniversalTime()) { continue }
            $kind = if ($normalized.id -eq 1002 -or $normalized.providerName -eq 'Application Hang') { 'ApplicationHang' } else { 'ApplicationError' }
            $items.Add((New-GridWindowsCollectorEvidence -Parameter 'windowsApplicationFailureEvent' -Value 1 `
                -Claim "$kind was recorded for the exact executable within the authorized time range." `
                -SourceType 'WindowsEventLog' -SourceIdentifier "windows-event://Application/$($normalized.recordId)" `
                -ContextFingerprint $ContextFingerprint -VerificationStatus Collected -CollectorName 'Grid.Windows.ApplicationFailure' `
                -CollectedAt $when -Native ([pscustomobject][ordered]@{
                    eventKind = $kind; eventId = $normalized.id; providerName = $normalized.providerName
                    recordId = $normalized.recordId; eventTimeUtc = $when.ToString('o')
                    executableName = $identity.executableName; sourceSha256 = $normalized.sourceSha256
                    eventData = $normalized.eventData
                })))
            $matched++
        }
        if ($matched -eq 0) {
            $items.Add((New-GridWindowsCollectorEvidence -Parameter 'windowsApplicationFailureEvent' -Value 0 `
                -Claim 'No matching Application Error or Application Hang record was observed in the authorized query range.' `
                -SourceType 'WindowsEventLogQuery' -SourceIdentifier 'windows-event://Application/query' `
                -ContextFingerprint $ContextFingerprint -VerificationStatus Collected -CollectorName 'Grid.Windows.ApplicationFailure' `
                -Native ([pscustomobject][ordered]@{ executableName=$identity.executableName; matchedEventCount=0; queryScope=$scope })))
        }
        New-GridWindowsEvidenceCollectionResult -CollectorName 'Grid.Windows.ApplicationFailure' -Status Complete -Scope $scope `
            -Evidence $items.ToArray() -Warnings $warnings.ToArray() -StartedAt $started -CompletedAt ([datetime]::UtcNow)
    }
    catch {
        $warnings.Add("Application event collection unavailable: $($_.Exception.Message)")
        $items.Add((New-GridWindowsCollectorEvidence -Parameter 'windowsApplicationFailureEvent' -Value 0 `
            -Claim 'Windows Application failure evidence was unavailable for the authorized query.' `
            -SourceType 'WindowsEventLogQuery' -SourceIdentifier 'windows-event://Application/query' `
            -ContextFingerprint $ContextFingerprint -VerificationStatus Unavailable -CollectorName 'Grid.Windows.ApplicationFailure' `
            -Native ([pscustomobject][ordered]@{ executableName=$identity.executableName; availability='Unavailable'; errorType=$_.Exception.GetType().FullName })))
        New-GridWindowsEvidenceCollectionResult -CollectorName 'Grid.Windows.ApplicationFailure' -Status Unavailable -Scope $scope `
            -Evidence $items.ToArray() -Warnings $warnings.ToArray() -StartedAt $started -CompletedAt ([datetime]::UtcNow)
    }
}
