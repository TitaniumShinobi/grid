#requires -Version 5.1
<#
.SYNOPSIS
Summarizes selected after-run tool evidence without granting repair authority.
.DESCRIPTION
A collected file hash is not a parsed finding, and a caller context fingerprint
is not proof that a report came from the selected tool run. Both distinctions
are preserved in the coverage result. This check never launches a tool or
modifies a game or MO2 profile.
#>
function Get-GridFullHouseEvidenceCoverage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$SelectedToolIds,
        [object[]]$ToolReceipts=@(),
        [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$CurrentContextFingerprint
    )
    $rows=New-Object Collections.Generic.List[object]
    foreach($toolId in @($SelectedToolIds|Sort-Object -Unique)){
        $matches=@($ToolReceipts|Where-Object{[string]$_.toolId -ceq $toolId})
        if($matches.Count -gt 1){throw "FullHouseDuplicateToolReceipt: $toolId"}
        $receipt=if($matches.Count){$matches[0]}else{$null}
        $records=@(if($receipt -and [string]$receipt.status -eq 'Collected'){
            foreach($entry in @($receipt.evidence)){
                if($entry -and $entry.PSObject.Properties['records']){@($entry.records)}
            }
        })
        $parsed=@($records|Where-Object{[string]$_.parseStatus -eq 'Complete'}).Count
        $unparsed=@($records|Where-Object{[string]$_.parseStatus -ne 'Complete'}).Count
        $contextMismatch=$false
        if($CurrentContextFingerprint){
            foreach($record in $records){
                if($record.PSObject.Properties['contextFingerprint'] -and
                   -not [string]::IsNullOrWhiteSpace([string]$record.contextFingerprint) -and
                   [string]$record.contextFingerprint -cne $CurrentContextFingerprint){$contextMismatch=$true}
            }
        }
        $status=if(-not$receipt){'MissingReceipt'}
            elseif([string]$receipt.status -ne 'Collected'){[string]$receipt.status}
            elseif($records.Count -eq 0){'NoReportRecords'}
            elseif($contextMismatch){'ContextMismatch'}
            elseif($parsed -eq 0){'UnparsedOnly'}
            else{'ParsedButRunUncorrelated'}
        $rows.Add([pscustomobject][ordered]@{
            toolId=$toolId;status=$status;receiptStatus=if($receipt){[string]$receipt.status}else{$null}
            recordCount=$records.Count;parsedRecordCount=$parsed;unparsedRecordCount=$unparsed
            currentContextFingerprint=if($CurrentContextFingerprint){$CurrentContextFingerprint}else{$null}
            toolRunCorrelated=$false;repairAuthority=$false
        })
    }
    $items=@($rows.ToArray())
    [pscustomobject][ordered]@{
        schemaVersion=1;status=if($items.Count -eq 0){'NoToolsSelected'}else{'NeedsEvidenceAndRunCorrelation'}
        selectedToolCount=$items.Count;tools=$items;toolRunCorrelated=$false
        repairAuthority=$false;mutationAuthorized=$false
        reason='Existing output may be parsed, but no registered receipt proves the selected tool run, profile, generated artifacts, and finding belong to one current FullHouse cycle.'
    }
}
