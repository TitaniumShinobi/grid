#requires -Version 5.1
<#
.SYNOPSIS
Observes an explicitly supplied existing LOOT output without launching LOOT.
.DESCRIPTION
The caller supplies exact candidate files already authorized for reading. The
collector refuses directories, reparse points, oversized files, and unstable
reads. Text reports are reduced to typed, uncorroborated tool assertions;
they are never treated as verified defects or executable repair instructions.
#>
function Get-GridLootFindingIdentity {
    param([Parameter(Mandatory)][string]$SourceSha256,[Parameter(Mandatory)][int]$LineNumber,[Parameter(Mandatory)][string]$Text)
    $bytes=[Text.Encoding]::UTF8.GetBytes("$SourceSha256`n$LineNumber`n$Text")
    $hasher=[Security.Cryptography.SHA256]::Create()
    try { 'loot-finding-' + (([BitConverter]::ToString($hasher.ComputeHash($bytes))).Replace('-','').Substring(0,24).ToLowerInvariant()) }
    finally { $hasher.Dispose() }
}

function ConvertFrom-GridLootTextReport {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$LiteralPath,[Parameter(Mandatory)][string]$SourceSha256,[string]$ContextFingerprint)
    $lines=[IO.File]::ReadAllLines($LiteralPath,[Text.Encoding]::UTF8)
    if($lines.Length -gt 100000){throw 'LootReportLineLimitExceeded: a text report may contain at most 100000 lines.'}
    $findings=New-Object Collections.Generic.List[object]
    $section='Unknown';$plugin=$null;$observedAt=[DateTimeOffset]::UtcNow.ToString('o')
    for($i=0;$i -lt $lines.Length;$i++){
        $text=$lines[$i].Trim()
        if(-not $text){continue}
        if($text.Length -gt 8192){throw "LootReportLineLimitExceeded: line $($i+1) is too long."}
        if($text -ceq 'Warnings:'){$section='Warnings';$plugin=$null;continue}
        if($text -ceq 'General messages'){$section='General';$plugin=$null;continue}
        if($text -ceq 'Plugins'){$section='Plugins';$plugin=$null;continue}
        if($text -match '^stats:\s'){$section='Stats';$plugin=$null;continue}
        if($section -eq 'Plugins' -and $text -match '^[^\\/:*?"<>|]+\.(?:esp|esm|esl)$'){$plugin=$text;continue}
        $kind='UnclassifiedMessage';$archive=$null
        if($text -match '^The folder and file with hashes [0-9a-f]+ and [0-9a-f]+ in "([^"]+\.bsa)" are present in another BSA\.$'){$kind='DuplicateBsaEntryReport';$archive=$matches[1]}
        elseif($text -match 'found \d+ ITM record\(s\), \d+ deleted reference\(s\) and [1-9]\d* deleted navmesh\(es\)'){$kind='DeletedNavmeshReport'}
        elseif($text -match 'have not enabled a compatibility patch|compatibility patch is (?:provided|included)|third party patch is available'){$kind='CompatibilityPatchCandidate'}
        elseif($text -match 'overwriting one of this mod.s essential files'){$kind='FileWinnerConcern'}
        elseif($text -match 'compatibility issues with|Incompatibilities:'){$kind='CompatibilityConcern'}
        elseif($text -match 'contains wild edits'){$kind='WildEditReport'}
        elseif($text -match 'Requires: New save game'){$kind='SaveRequirement'}
        elseif($text -match 'Update Patch available:'){$kind='UpdateCandidate'}
        elseif($text -match 'found \d+ ITM record\(s\)'){$kind='CleaningReport'}
        $findings.Add([pscustomobject][ordered]@{
            findingId=Get-GridLootFindingIdentity -SourceSha256 $SourceSha256 -LineNumber ($i+1) -Text $text
            kind=$kind;section=$section;lineNumber=$i+1;subjectPlugin=$plugin;subjectArchive=$archive
            reportedText=$text;sourceSha256=$SourceSha256;sourcePath=$LiteralPath;observedAtUtc=$observedAt
            collectorIdentity='Get-GridLootExistingOutput';collectorVersion=1
            contextFingerprint=if($ContextFingerprint){$ContextFingerprint}else{$null}
            contextStatus=if($ContextFingerprint){'CallerBound'}else{'Unbound'}
            verificationStatus='ReportedUncorroborated';mutationAuthorized=$false
        })
        if($findings.Count -gt 20000){throw 'LootReportFindingLimitExceeded: a report may contain at most 20000 findings.'}
    }
    [pscustomobject][ordered]@{format='TextReport';parseStatus='Complete';findingCount=$findings.Count;findings=@($findings.ToArray())}
}

function Get-GridLootExistingOutput {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$CandidatePaths,
        [ValidateRange(1, 67108864)][long]$MaximumOutputBytes = 16777216,
        [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ContextFingerprint
    )
    $records = New-Object Collections.Generic.List[object]
    foreach ($path in @($CandidatePaths | Sort-Object -Unique)) {
        if (-not [IO.Path]::IsPathRooted($path)) { throw "LOOT evidence path must be absolute: $path" }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "LOOT evidence reparse point is refused: $path" }
        if ([long]$item.Length -gt $MaximumOutputBytes) { throw "LOOT evidence exceeds the $MaximumOutputBytes-byte limit: $path" }
        $beforeLength = [long]$item.Length; $beforeWrite = $item.LastWriteTimeUtc
        $hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256 -ErrorAction Stop).Hash
        $textReport=if($item.Extension -in @('.txt','.log')){ConvertFrom-GridLootTextReport -LiteralPath $item.FullName -SourceSha256 $hash -ContextFingerprint $ContextFingerprint}else{[pscustomobject]@{format='UnsupportedStructuredFormat';parseStatus='NotParsed';findingCount=0;findings=@()}}
        $after = Get-Item -LiteralPath $item.FullName -Force -ErrorAction Stop
        if ([long]$after.Length -ne $beforeLength -or $after.LastWriteTimeUtc -ne $beforeWrite -or (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash -cne $hash) { throw "LOOT evidence changed during observation: $path" }
        $records.Add([pscustomobject][ordered]@{
            path = $item.FullName; sizeBytes = $beforeLength; sha256 = $hash
            lastWriteTimeUtc = $beforeWrite.ToString('o'); verificationStatus = 'Collected'
            collectedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');collectorIdentity='Get-GridLootExistingOutput';collectorVersion=1
            contextFingerprint=if($ContextFingerprint){$ContextFingerprint}else{$null}
            contextStatus=if($ContextFingerprint){'CallerBound'}else{'Unbound'}
            format=$textReport.format;parseStatus=$textReport.parseStatus;findingCount=$textReport.findingCount;findings=@($textReport.findings)
        })
    }
    [pscustomobject][ordered]@{
        schemaVersion = 1; collector = 'Get-GridLootExistingOutput'; collectorVersion = 1
        status = if ($records.Count -gt 0) { 'Collected' } else { 'Unavailable' }
        launchPerformed = $false; mutationAuthorized = $false; records = $records.ToArray()
        reason = if ($records.Count -eq 0) { 'No explicitly supplied readable existing LOOT output was found. LOOT was not launched because a side-effect-safe launch contract is not proven.' } else { $null }
    }
}
