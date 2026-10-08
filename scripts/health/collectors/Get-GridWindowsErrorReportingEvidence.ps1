#requires -Version 5.1
<#
.SYNOPSIS
Collects bounded Windows Error Reporting events and retained Report.wer metadata
for an exact executable identity.
.DESCRIPTION
Report payloads are read with a strict byte limit. Dump files are never opened.
Missing, inaccessible, and changed-during-read reports remain explicit evidence
states and are never converted into a causal conclusion.
#>

function Read-GridWindowsWerReport {
    param(
        [Parameter(Mandatory)][string]$LiteralPath,
        [ValidateRange(1024, 4194304)][int]$MaximumBytes = 1048576
    )
    $before = $null
    try { $before = Get-Item -LiteralPath $LiteralPath -Force -ErrorAction Stop }
    catch {
        return [pscustomobject][ordered]@{ status='Unavailable'; errorType=$_.Exception.GetType().FullName; values=$null; sha256=$null; before=$null; after=$null }
    }
    if (($before.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        return [pscustomobject][ordered]@{ status='ReparsePointRefused'; errorType=$null; values=$null; sha256=$null; before=$before; after=$before }
    }
    if ($before.Length -gt $MaximumBytes) {
        return [pscustomobject][ordered]@{ status='LimitExceeded'; errorType=$null; values=$null; sha256=$null; before=$before; after=$before }
    }
    try {
        $bytes = [IO.File]::ReadAllBytes($before.FullName)
        if ($bytes.Length -gt $MaximumBytes) {
            return [pscustomobject][ordered]@{ status='LimitExceeded'; errorType=$null; values=$null; sha256=$null; before=$before; after=$before }
        }
        $after = Get-Item -LiteralPath $LiteralPath -Force -ErrorAction Stop
        $changed = ($before.Length -ne $after.Length -or $before.LastWriteTimeUtc -ne $after.LastWriteTimeUtc)
        $encoding = if ($bytes.Length -ge 2 -and $bytes[0] -eq 255 -and $bytes[1] -eq 254) { [Text.Encoding]::Unicode } `
            elseif ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191) { [Text.Encoding]::UTF8 } `
            else { [Text.Encoding]::UTF8 }
        $text = $encoding.GetString($bytes).TrimStart([char]0xFEFF)
        $values = [ordered]@{}
        foreach ($line in @($text -split "`r?`n" | Select-Object -First 256)) {
            $separator = $line.IndexOf('=')
            if ($separator -le 0) { continue }
            $key = $line.Substring(0, $separator).Trim()
            if ([string]::IsNullOrWhiteSpace($key) -or $values.Contains($key)) { continue }
            if ($key.Length -gt 128) { $key = $key.Substring(0, 128) }
            $value = $line.Substring($separator + 1).Trim()
            if ($value.Length -gt 4096) { $value = $value.Substring(0, 4096) }
            $values[$key] = $value
        }
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '') }
        finally { $sha.Dispose() }
        [pscustomobject][ordered]@{
            status = if ($changed) { 'ChangedDuringRead' } else { 'Readable' }
            errorType = $null; values = $values; sha256 = $hash; before = $before; after = $after
        }
    }
    catch {
        [pscustomobject][ordered]@{ status='Unavailable'; errorType=$_.Exception.GetType().FullName; values=$null; sha256=$null; before=$before; after=$null }
    }
}

function Get-GridWindowsErrorReportingEvidence {
    [CmdletBinding()]
    param(
        [string]$ExecutablePath,
        [string]$ExecutableName,
        [datetime]$SinceUtc = ([datetime]::UtcNow.AddDays(-30)),
        [datetime]$UntilUtc = [datetime]::UtcNow,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ContextFingerprint,
        [ValidateRange(1, 4096)][int]$MaxEvents = 256,
        [ValidateRange(1, 4096)][int]$MaxReports = 128,
        [ValidateRange(1024, 4194304)][int]$MaxReportBytes = 1048576,
        [Alias('ArchiveRoots')][string[]]$ArchiveRoot,
        [object[]]$EventRecords,
        [switch]$SkipArchiveScan
    )
    $started = [datetime]::UtcNow
    $identity = Resolve-GridWindowsExecutableIdentity -ExecutablePath $ExecutablePath -ExecutableName $ExecutableName
    Assert-GridWindowsCollectionBounds -SinceUtc $SinceUtc -UntilUtc $UntilUtc -MaximumItems ([math]::Max($MaxEvents,$MaxReports))
    if (-not $PSBoundParameters.ContainsKey('ArchiveRoot')) {
        $programData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
        $ArchiveRoot = @(
            (Join-Path $programData 'Microsoft\Windows\WER\ReportArchive'),
            (Join-Path $programData 'Microsoft\Windows\WER\ReportQueue')
        )
    }
    $canonicalRoots = @($ArchiveRoot | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { [IO.Path]::GetFullPath($_) } | Select-Object -Unique)
    $scope = [pscustomobject][ordered]@{
        channel = 'Application'; provider = 'Windows Error Reporting'; eventIds = @(1001)
        executableName = $identity.executableName; executablePath = $identity.executablePath
        sinceUtc = $SinceUtc.ToUniversalTime().ToString('o'); untilUtc = $UntilUtc.ToUniversalTime().ToString('o')
        maxEvents = $MaxEvents; maxReports = $MaxReports; maxReportBytes = $MaxReportBytes
        archiveRoots = $canonicalRoots; archiveScan = (-not $SkipArchiveScan)
    }
    $items = New-Object Collections.Generic.List[object]
    $warnings = New-Object Collections.Generic.List[string]
    $eventQueryAvailable = $true
    try {
        $records = if ($PSBoundParameters.ContainsKey('EventRecords')) { @($EventRecords) }
        else {
            if (-not (Get-Command Get-WinEvent -ErrorAction SilentlyContinue)) { throw 'Get-WinEvent is unavailable on this host.' }
            @(Get-WinEvent -FilterHashtable @{
                LogName='Application'; ProviderName='Windows Error Reporting'; Id=1001
                StartTime=$SinceUtc.ToLocalTime(); EndTime=$UntilUtc.ToLocalTime()
            } -MaxEvents $MaxEvents -ErrorAction Stop)
        }
        foreach ($record in @($records | Select-Object -First $MaxEvents)) {
            $normalized = ConvertFrom-GridWindowsEventRecord -Record $record
            if ($normalized.id -ne 1001 -or $normalized.providerName -ne 'Windows Error Reporting' -or
                -not (Test-GridWindowsEventExecutableMatch -NormalizedRecord $normalized -ExecutableName $identity.executableName)) { continue }
            $when = if ($normalized.timeCreated) { ([datetime]$normalized.timeCreated).ToUniversalTime() } else { [datetime]::UtcNow }
            if ($when -lt $SinceUtc.ToUniversalTime() -or $when -gt $UntilUtc.ToUniversalTime()) { continue }
            $items.Add((New-GridWindowsCollectorEvidence -Parameter 'windowsErrorReportingEvent' -Value 1 `
                -Claim 'Windows Error Reporting recorded an event for the exact executable within the authorized time range.' `
                -SourceType 'WindowsEventLog' -SourceIdentifier "windows-event://Application/$($normalized.recordId)" `
                -ContextFingerprint $ContextFingerprint -VerificationStatus Collected -CollectorName 'Grid.Windows.ErrorReporting' `
                -CollectedAt $when -Native ([pscustomobject][ordered]@{
                    eventId=$normalized.id; providerName=$normalized.providerName; recordId=$normalized.recordId
                    eventTimeUtc=$when.ToString('o'); executableName=$identity.executableName
                    sourceSha256=$normalized.sourceSha256; eventData=$normalized.eventData
                })))
        }
    }
    catch {
        $eventQueryAvailable = $false
        $warnings.Add("WER event collection unavailable: $($_.Exception.Message)")
        $items.Add((New-GridWindowsCollectorEvidence -Parameter 'windowsErrorReportingEvent' -Value 0 `
            -Claim 'Windows Error Reporting event evidence was unavailable for the authorized query.' `
            -SourceType 'WindowsEventLogQuery' -SourceIdentifier 'windows-event://Application/wer-query' `
            -ContextFingerprint $ContextFingerprint -VerificationStatus Unavailable -CollectorName 'Grid.Windows.ErrorReporting' `
            -Native ([pscustomobject][ordered]@{ availability='Unavailable'; errorType=$_.Exception.GetType().FullName; executableName=$identity.executableName })))
    }

    $reportScanAvailable = $true
    $reportMatches = 0
    if (-not $SkipArchiveScan) {
        foreach ($root in $canonicalRoots) {
            if (-not (Test-Path -LiteralPath $root -PathType Container)) {
                $warnings.Add("WER root is unavailable: $root")
                $items.Add((New-GridWindowsCollectorEvidence -Parameter 'windowsErrorReportingArchive' -Value 0 `
                    -Claim 'A configured Windows Error Reporting archive root was not present.' `
                    -SourceType 'WindowsErrorReportingArchive' -SourceIdentifier ('wer-root://' + (Get-GridWindowsSha256Text $root)) `
                    -ContextFingerprint $ContextFingerprint -VerificationStatus Unavailable -CollectorName 'Grid.Windows.ErrorReporting' `
                    -Native ([pscustomobject][ordered]@{ root=$root; availability='Missing'; errorType=$null })))
                continue
            }
            try {
                $directories = @(Get-ChildItem -LiteralPath $root -Directory -Force -ErrorAction Stop |
                    Where-Object { $_.LastWriteTimeUtc -ge $SinceUtc.ToUniversalTime() -and $_.LastWriteTimeUtc -le $UntilUtc.ToUniversalTime() } |
                    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First $MaxReports)
                foreach ($directory in $directories) {
                    $reportPath = Join-Path $directory.FullName 'Report.wer'
                    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) { continue }
                    $report = Read-GridWindowsWerReport -LiteralPath $reportPath -MaximumBytes $MaxReportBytes
                    $reportedName = if ($report.values) {
                        @('AppName','ApplicationName','P1') | ForEach-Object { Get-GridWindowsObjectValue $report.values $_ } |
                            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -First 1
                    } else { $null }
                    $confirmed = (-not [string]::IsNullOrWhiteSpace([string]$reportedName) -and [IO.Path]::GetFileName([string]$reportedName) -ieq $identity.executableName)
                    if (-not $confirmed) {
                        # An inaccessible report can only be associated as a candidate when WER encoded
                        # the exact executable stem in the directory identity. It remains Unavailable.
                        $stem = [IO.Path]::GetFileNameWithoutExtension($identity.executableName)
                        if ($report.status -in @('Unavailable','ReparsePointRefused','LimitExceeded') -and $directory.Name -notmatch ('(?i)(?:^|_)' + [regex]::Escape($stem) + '(?:_|$)')) { continue }
                        if ($report.status -eq 'Readable' -or $report.status -eq 'ChangedDuringRead') { continue }
                    }
                    $reportMatches++
                    $verification = switch ($report.status) {
                        'Readable' { 'Collected' }
                        'ChangedDuringRead' { 'Stale' }
                        default { 'Unavailable' }
                    }
                    $claim = switch ($report.status) {
                        'Readable' { 'A retained WER report for the exact executable was read within the authorized bounds.' }
                        'ChangedDuringRead' { 'A retained WER report changed during collection and is stale for assessment.' }
                        default { 'A candidate retained WER report could not be read within the authorized bounds.' }
                    }
                    $items.Add((New-GridWindowsCollectorEvidence -Parameter 'windowsErrorReportingArchive' -Value $(if ($confirmed) { 1 } else { 0 }) `
                        -Claim $claim -SourceType 'WindowsErrorReportingArchive' -SourceIdentifier ('wer-report://' + $directory.Name) `
                        -ContextFingerprint $ContextFingerprint -VerificationStatus $verification -CollectorName 'Grid.Windows.ErrorReporting' `
                        -CollectedAt $directory.LastWriteTimeUtc -Native ([pscustomobject][ordered]@{
                            reportDirectory=$directory.Name; executableName=$identity.executableName
                            identityConfirmed=$confirmed; readabilityStatus=$report.status; errorType=$report.errorType
                            sizeBytes=if($report.before){[int64]$report.before.Length}else{$null}
                            lastWriteTimeBefore=if($report.before){$report.before.LastWriteTimeUtc.ToString('o')}else{$null}
                            lastWriteTimeAfter=if($report.after){$report.after.LastWriteTimeUtc.ToString('o')}else{$null}
                            reportSha256=$report.sha256
                            reportValues=if($confirmed -and $report.values){$report.values}else{$null}
                        })))
                }
            }
            catch {
                $reportScanAvailable = $false
                $warnings.Add("WER archive enumeration unavailable for '$root': $($_.Exception.Message)")
                $items.Add((New-GridWindowsCollectorEvidence -Parameter 'windowsErrorReportingArchive' -Value 0 `
                    -Claim 'A configured Windows Error Reporting archive root was inaccessible.' `
                    -SourceType 'WindowsErrorReportingArchive' -SourceIdentifier ('wer-root://' + (Get-GridWindowsSha256Text $root)) `
                    -ContextFingerprint $ContextFingerprint -VerificationStatus Unavailable -CollectorName 'Grid.Windows.ErrorReporting' `
                    -Native ([pscustomobject][ordered]@{ root=$root; availability='Unavailable'; errorType=$_.Exception.GetType().FullName })))
            }
        }
    }
    if (@($items | Where-Object verificationStatus -in @('Collected','Stale')).Count -eq 0 -and $eventQueryAvailable -and ($SkipArchiveScan -or $reportScanAvailable)) {
        $items.Add((New-GridWindowsCollectorEvidence -Parameter 'windowsErrorReportingEvidence' -Value 0 `
            -Claim 'No matching Windows Error Reporting event or readable retained report was observed in the authorized range.' `
            -SourceType 'WindowsErrorReportingQuery' -SourceIdentifier 'wer-query://combined' `
            -ContextFingerprint $ContextFingerprint -VerificationStatus Collected -CollectorName 'Grid.Windows.ErrorReporting' `
            -Native ([pscustomobject][ordered]@{ executableName=$identity.executableName; matchingReports=$reportMatches; queryScope=$scope })))
    }
    $status = if (-not $eventQueryAvailable -and -not $reportScanAvailable) { 'Unavailable' } `
        elseif (@($items | Where-Object verificationStatus -in @('Unavailable','Stale')).Count -gt 0) { 'Partial' } else { 'Complete' }
    New-GridWindowsEvidenceCollectionResult -CollectorName 'Grid.Windows.ErrorReporting' -Status $status -Scope $scope `
        -Evidence $items.ToArray() -Warnings $warnings.ToArray() -StartedAt $started -CompletedAt ([datetime]::UtcNow)
}
