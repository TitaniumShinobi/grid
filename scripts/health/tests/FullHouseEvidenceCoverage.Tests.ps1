#requires -Version 5.1
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'Grid.FullHouseEvidenceCoverage.ps1')
function Assert-GridFullHouseTest($condition,$message){if(-not$condition){throw $message}}
$current='A'*64
$other='B'*64
$receipts=@(
    [pscustomobject]@{toolId='grid.tool.loot';status='Collected';evidence=@([pscustomobject]@{records=@([pscustomobject]@{parseStatus='Complete';contextFingerprint=$current})})},
    [pscustomobject]@{toolId='grid.tool.dyndolod';status='Collected';evidence=@([pscustomobject]@{records=@([pscustomobject]@{parseStatus='NotParsed';contextFingerprint=$current})})},
    [pscustomobject]@{toolId='grid.tool.synthesis';status='Collected';evidence=@([pscustomobject]@{records=@([pscustomobject]@{parseStatus='Complete';contextFingerprint=$other})})}
)
$result=Get-GridFullHouseEvidenceCoverage -SelectedToolIds @('grid.tool.loot','grid.tool.dyndolod','grid.tool.synthesis','grid.tool.texgen') -ToolReceipts $receipts -CurrentContextFingerprint $current
Assert-GridFullHouseTest ($result.status -eq 'NeedsEvidenceAndRunCorrelation') 'No current after-run receipts may authorize FullHouse repair.'
Assert-GridFullHouseTest (($result.tools|Where-Object toolId -eq 'grid.tool.loot').status -eq 'ParsedButRunUncorrelated') 'Parsed LOOT report must remain uncorrelated.'
Assert-GridFullHouseTest (($result.tools|Where-Object toolId -eq 'grid.tool.dyndolod').status -eq 'UnparsedOnly') 'A hashed artifact must not count as parsed.'
Assert-GridFullHouseTest (($result.tools|Where-Object toolId -eq 'grid.tool.synthesis').status -eq 'ContextMismatch') 'Mismatched profile context must be visible.'
Assert-GridFullHouseTest (($result.tools|Where-Object toolId -eq 'grid.tool.texgen').status -eq 'MissingReceipt') 'Absent tool receipts must remain missing.'
Assert-GridFullHouseTest (-not $result.mutationAuthorized -and -not $result.repairAuthority) 'Coverage must not authorize mutation.'
'FullHouse evidence coverage tests passed.'
