$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $gameRoot 'health\Invoke-GridSkyrimRecordRelationshipInvestigation.ps1')

function Assert-True([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
function Assert-Equal($Expected,$Actual,[string]$Message){if($Expected-ne$Actual){throw "$Message Expected '$Expected', actual '$Actual'."}}
function Assert-Throws([scriptblock]$Action,[string]$Pattern,[string]$Message){try{&$Action;throw "$Message Expected '$Pattern'."}catch{if($_.Exception.Message-notmatch$Pattern){throw "$Message Unexpected: $($_.Exception.Message)"}}}

$profile='C:\Fixture\profiles\Current'
$files=@(
    [pscustomobject]@{path=(Join-Path $profile 'plugins.txt');sha256=('A'*64)},
    [pscustomobject]@{path=(Join-Path $profile 'loadorder.txt');sha256=('B'*64)},
    [pscustomobject]@{path=(Join-Path $profile 'modlist.txt');sha256=('C'*64)},
    [pscustomobject]@{path='C:\Fixture\ModOrganizer.ini';sha256=('D'*64)},
    [pscustomobject]@{path=(Join-Path $profile 'skyrimprefs.ini');sha256=('E'*64)}
)
$selected=@(Select-GridSkyrimRecordRelationshipProtectedFiles -Files $files)
Assert-Equal 3 $selected.Count 'Record collection must bind exactly the three profile files that determine the active record graph.'
Assert-True (-not @($selected|Where-Object{[IO.Path]::GetFileName([string]$_.path)-ieq'ModOrganizer.ini'}).Count) 'Volatile MO2 UI/session preferences must not invalidate an otherwise current record graph.'
Assert-Throws {Select-GridSkyrimRecordRelationshipProtectedFiles -Files @($files|Where-Object{[IO.Path]::GetFileName([string]$_.path)-ine'plugins.txt'})|Out-Null} 'exactly one plugins.txt' 'Missing plugin activation evidence must fail closed.'

Write-Host 'PASS: record-relationship protection binds only complete profile graph state and excludes volatile MO2 UI/session preferences.'
