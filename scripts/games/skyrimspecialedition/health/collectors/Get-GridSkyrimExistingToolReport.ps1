#requires -Version 5.1
<#
.SYNOPSIS
Collects exact after-run Skyrim tool reports as uncorroborated evidence.
.DESCRIPTION
The caller selects one registered tool and exact authorized files. Text logs
are scanned for bounded message/lifecycle assertions; generated artifacts are
fingerprinted only. No tool is launched and no repair target is inferred.
#>
function Get-GridSkyrimExistingToolReport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('grid.tool.texgen','grid.tool.xlodgen','grid.tool.dyndolod','grid.tool.bodyslide','grid.tool.zedit','grid.tool.wrye-bash','grid.tool.synthesis','grid.tool.sseedit-report')][string]$ToolId,
        [Parameter(Mandatory)][string[]]$CandidatePaths,
        [ValidateRange(1,67108864)][long]$MaximumOutputBytes=16777216,
        [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ContextFingerprint
    )
    $records=New-Object Collections.Generic.List[object]
    foreach($path in @($CandidatePaths|Sort-Object -Unique)){
        if(-not[IO.Path]::IsPathRooted($path)){throw "ToolReportPathInvalid: '$path' must be absolute."}
        if(-not(Test-Path -LiteralPath $path -PathType Leaf)){continue}
        $item=Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if(($item.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0){throw "ToolReportReparsePointRefused: $path"}
        if([long]$item.Length -gt $MaximumOutputBytes){throw "ToolReportSizeLimitExceeded: $path"}
        $length=[long]$item.Length;$write=$item.LastWriteTimeUtc
        $collectedAt=[DateTimeOffset]::UtcNow.ToString('o')
        $hash=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        $messages=New-Object Collections.Generic.List[object]
        $format=if($item.Extension -in @('.txt','.log')){'TextLog'}else{'ArtifactOrUnsupportedFormat'}
        $parseStatus=if($format -eq 'TextLog'){'Complete'}else{'NotParsed'}
        if($format -eq 'TextLog'){
            $lines=[IO.File]::ReadAllLines($item.FullName,[Text.Encoding]::UTF8)
            if($lines.Length -gt 100000){throw "ToolReportLineLimitExceeded: $path"}
            for($i=0;$i -lt $lines.Length;$i++){
                $line=$lines[$i].Trim()
                if(-not$line){continue}
                if($line.Length -gt 8192){throw "ToolReportLineLimitExceeded: $path line $($i+1)"}
                $kind=if($line -match '(?i)\b(error|fatal|exception|failed|failure|missing|not found|incompatible|warning|warn|unresolved)\b'){'ToolMessage'}elseif($line -match '(?i)\b(completed|finished|success|version)\b'){'LifecycleMessage'}else{$null}
                if(-not$kind){continue}
                $safeText=if($line -match '(?i)\b(api[_-]?key|bearer|token|password|secret)\s*[:=]'){'[potential credential line omitted]'}else{$line}
                $messages.Add([pscustomobject][ordered]@{
                    kind=$kind;lineNumber=$i+1;text=$safeText;verificationStatus='ReportedUncorroborated'
                    toolId=$ToolId;sourcePath=$item.FullName;sourceSha256=$hash
                    collectorIdentity='Get-GridSkyrimExistingToolReport';collectorVersion=1;collectedAtUtc=$collectedAt
                    contextFingerprint=if($ContextFingerprint){$ContextFingerprint}else{$null}
                    contextStatus=if($ContextFingerprint){'CallerBound'}else{'Unbound'}
                    mutationAuthorized=$false
                })
                if($messages.Count -gt 5000){throw "ToolReportMessageLimitExceeded: $path"}
            }
        }
        $after=Get-Item -LiteralPath $item.FullName -Force -ErrorAction Stop
        if([long]$after.Length -ne $length -or $after.LastWriteTimeUtc -ne $write -or (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToUpperInvariant() -cne $hash){throw "ToolReportChangedDuringObservation: $path"}
        $records.Add([pscustomobject][ordered]@{
            path=$item.FullName;sizeBytes=$length;sha256=$hash;lastWriteTimeUtc=$write.ToString('o')
            collectedAtUtc=$collectedAt;collectorIdentity='Get-GridSkyrimExistingToolReport';collectorVersion=1
            format=$format;parseStatus=$parseStatus;messageCount=$messages.Count;messages=@($messages.ToArray())
            verificationStatus='Collected';contextFingerprint=if($ContextFingerprint){$ContextFingerprint}else{$null}
            contextStatus=if($ContextFingerprint){'CallerBound'}else{'Unbound'}
        })
    }
    [pscustomobject][ordered]@{
        schemaVersion=1;collector='Get-GridSkyrimExistingToolReport';collectorVersion=1;toolId=$ToolId
        status=if($records.Count){'Collected'}else{'Unavailable'};launchPerformed=$false;mutationAuthorized=$false
        records=@($records.ToArray());reason=if($records.Count){$null}else{'No exact selected after-run report or artifact was available; the tool was not launched.'}
    }
}
