#requires -Version 5.1

<#
.SYNOPSIS
Safely inspects or changes one mod entry in an MO2 profile.
.DESCRIPTION
This is Grid's canonical low-level mod-state executor. Mutations require an
authorized current ModStateChange proposal. The executor changes only the
matching + or - marker in modlist.txt, preserves UTF-8 BOM/newline form,
creates a timestamped backup, verifies the exact result, and restores the
backup automatically if verification fails.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact='Medium')]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ModName,
    [Parameter()][ValidateSet('Status','Enable','Disable')][string]$Action='Status',
    [Parameter(Mandatory)][string]$Mo2Root,
    [Parameter(Mandatory)][string]$Profile,
    $AuthorizedProposal,
    [string]$CurrentContextFingerprint,
    [string]$CurrentEvidenceFingerprint,
    [switch]$PassThru
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($Mo2Root) -or -not [IO.Path]::IsPathRooted($Mo2Root)) { throw 'Mo2Root must be an existing absolute directory.' }
if ([string]::IsNullOrWhiteSpace($Profile) -or [IO.Path]::GetFileName($Profile) -cne $Profile -or $Profile -in @('.','..') -or $Profile.IndexOfAny([char[]]@(0,10,13)) -ge 0) { throw 'Profile must be one immediate MO2 profile-directory name, not a path.' }
if ([string]::IsNullOrWhiteSpace($ModName) -or [IO.Path]::GetFileName($ModName) -cne $ModName -or $ModName -in @('.','..') -or $ModName.EndsWith('_separator',[StringComparison]::Ordinal) -or $ModName.IndexOfAny([char[]]@(0,10,13)) -ge 0) { throw 'ModName must be one non-separator MO2 mod name, not a path.' }
$canonicalRoot=[IO.Path]::GetFullPath($Mo2Root).TrimEnd('\')
if (-not (Test-Path -LiteralPath $canonicalRoot -PathType Container)) { throw "MO2 root not found: $canonicalRoot" }
$profilesRoot=[IO.Path]::GetFullPath((Join-Path $canonicalRoot 'profiles')).TrimEnd('\')
$profileDirectory=[IO.Path]::GetFullPath((Join-Path $profilesRoot $Profile))
if (-not $profileDirectory.StartsWith($profilesRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Resolved profile path escapes the MO2 profiles directory.' }
$modListFile=Join-Path $profileDirectory 'modlist.txt'
$safeName=$ModName -replace '[^A-Za-z0-9._-]','_'
$backupDirectory=Join-Path $profileDirectory "grid-backups\mod-state\$safeName"

function Assert-AuthorizedMutation {
    if ($Action -eq 'Status') { return }
    if (-not $AuthorizedProposal -or [string]::IsNullOrWhiteSpace($CurrentContextFingerprint) -or [string]::IsNullOrWhiteSpace($CurrentEvidenceFingerprint)) { throw 'AuthorizedProposalRequired: mutation is reachable only through Grid proposal authorization.' }
    $gameRoot=Split-Path -Parent $PSScriptRoot
    $scriptsRoot=Split-Path -Parent (Split-Path -Parent $gameRoot)
    $modulePath=[IO.Path]::GetFullPath((Join-Path $scriptsRoot 'health\Grid.Health.psm1'))
    if (-not (Get-Module | Where-Object { $_.Path -and [IO.Path]::GetFullPath([string]$_.Path) -eq $modulePath } | Select-Object -First 1)) { Import-Module $modulePath -ErrorAction Stop }
    $validation=Test-GridRemediationProposal -Proposal $AuthorizedProposal -CurrentContextFingerprint $CurrentContextFingerprint -CurrentEvidenceFingerprint $CurrentEvidenceFingerprint
    if (-not $validation.IsValid) { throw ($validation.Errors -join ' ') }
    if ($validation.IsStale) { throw 'StaleProposal: context or evidence changed after authorization.' }
    if ($AuthorizedProposal.status -ne 'Executing' -or $AuthorizedProposal.authorization.status -ne 'Authorized') { throw 'AuthorizedProposalRequired: proposal is not in its single executing transition.' }
    if ($AuthorizedProposal.actionType -ne 'ModStateChange') { throw 'UnsupportedAction: proposal is not a ModStateChange.' }
    if (@($AuthorizedProposal.targets) -notcontains "modName=$ModName") { throw 'ProposalTargetMismatch: authorized mod does not match this request.' }
    $expected=if($Action -eq 'Enable'){'Enabled'}else{'Disabled'}
    if (@($AuthorizedProposal.targets) -notcontains "desiredState=$expected") { throw 'ProposalTargetMismatch: authorized targets do not match this mod-state request.' }
}
function Assert-ModListFile { if (-not (Test-Path -LiteralPath $modListFile -PathType Leaf)) { throw "MO2 mod list not found: $modListFile" } }
function Assert-ApplicationsClosed {
    $blocked=@('ModOrganizer','SkyrimSE','skse64_loader','SkyrimSELauncher','SSEEdit','xEdit')
    $running=@(Get-Process -ErrorAction SilentlyContinue | Where-Object { $blocked -contains $_.ProcessName } | Select-Object -ExpandProperty ProcessName -Unique)
    if($running.Count -gt 0){throw "Close MO2, Skyrim, and xEdit before changing modlist.txt. Running: $($running -join ', ')"}
}
function Read-ModListDocument {
    Assert-ModListFile
    $bytes=[IO.File]::ReadAllBytes($modListFile)
    $bom=$bytes.Length -ge 3 -and $bytes[0]-eq 0xEF -and $bytes[1]-eq 0xBB -and $bytes[2]-eq 0xBF
    $offset=if($bom){3}else{0}
    $encoding=[Text.UTF8Encoding]::new($false,$true)
    try{$text=$encoding.GetString($bytes,$offset,$bytes.Length-$offset)}catch{throw 'modlist.txt is not valid UTF-8; Grid refused to rewrite it.'}
    [pscustomobject]@{Bytes=$bytes;Text=$text;HasBom=$bom;Sha256=(Get-FileHash -LiteralPath $modListFile -Algorithm SHA256).Hash}
}
function Get-ModEntry($Document) {
    $matches=[regex]::Matches($Document.Text,'(?m)^(?<content>[^\r\n]*)(?<ending>\r\n|\n|\r|$)') | Where-Object {
        $value=$_.Groups['content'].Value.Trim(); if($value.Length -eq 0 -or $value.StartsWith('#')){$false}else{$marker=$value[0];$name=if($marker -in @('+','-','*')){$value.Substring(1).Trim()}else{$value};$name -ieq $ModName}
    }
    if(@($matches).Count -eq 0){return [pscustomobject]@{State='Missing';Marker=$null;Match=$null}}
    if(@($matches).Count -gt 1){throw "Found duplicate entries for '$ModName' in $modListFile"}
    $value=$matches[0].Groups['content'].Value.Trim();$marker=if($value[0] -in @('+','-','*')){[string]$value[0]}else{''}
    if($marker -eq '*'){return [pscustomobject]@{State='Foreign';Marker=$marker;Match=$matches[0]}}
    [pscustomobject]@{State=if($marker -eq '-'){'Disabled'}else{'Enabled'};Marker=$marker;Match=$matches[0]}
}
function New-Result([string]$State,[bool]$Changed,[string]$Backup,[string]$Message,$Document){
    [pscustomobject][ordered]@{Tool='Set-GridModState';Action=$Action;ModName=$ModName;State=$State;Changed=$Changed;Mo2Root=$canonicalRoot;Profile=$Profile;ModListFile=$modListFile;ModListSha256=$Document.Sha256;Backup=$Backup;Message=$Message;Timestamp=[DateTimeOffset]::UtcNow.ToString('o')}
}
function Write-DocumentAtomically([string]$Text,[bool]$HasBom){
    $temporary=Join-Path $profileDirectory ('.grid-modlist-'+[Guid]::NewGuid().ToString('N')+'.tmp')
    try{
        $content=[Text.UTF8Encoding]::new($false).GetBytes($Text)
        $payload=if($HasBom){[byte[]](@(0xEF,0xBB,0xBF)+@($content))}else{$content}
        [IO.File]::WriteAllBytes($temporary,$payload)
        Move-Item -LiteralPath $temporary -Destination $modListFile -Force
    }finally{if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Force}}
}
function Set-DesiredState([ValidateSet('Enabled','Disabled')][string]$DesiredState){
    $document=Read-ModListDocument;$entry=Get-ModEntry $document
    if($entry.State -eq 'Missing'){throw "Mod entry not found in $modListFile`: $ModName"}
    if($entry.State -eq 'Foreign'){throw "Foreign/core entry '$ModName' uses MO2's * marker and cannot be enabled or disabled by Grid."}
    if($entry.State -eq $DesiredState){return New-Result $DesiredState $false $null "$ModName is already $DesiredState." $document}
    if(-not $PSCmdlet.ShouldProcess("$ModName in profile $Profile","Set state to $DesiredState")){return New-Result $entry.State $false $null 'No change made.' $document}
    Assert-ApplicationsClosed
    New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
    $backup=Join-Path $backupDirectory ('modlist-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff')+'.txt')
    Copy-Item -LiteralPath $modListFile -Destination $backup -ErrorAction Stop
    $marker=if($DesiredState -eq 'Enabled'){ '+' }else{ '-' }
    $replacement=$marker+$ModName+$entry.Match.Groups['ending'].Value
    $updated=$document.Text.Substring(0,$entry.Match.Index)+$replacement+$document.Text.Substring($entry.Match.Index+$entry.Match.Length)
    try{
        Write-DocumentAtomically $updated $document.HasBom
        $verifiedDocument=Read-ModListDocument;$verified=Get-ModEntry $verifiedDocument
        if($verified.State -ne $DesiredState){throw "Verification returned '$($verified.State)' instead of '$DesiredState'."}
    }catch{Copy-Item -LiteralPath $backup -Destination $modListFile -Force;throw "Mod state change failed and modlist.txt was restored. $($_.Exception.Message)"}
    New-Result $DesiredState $true $backup "$ModName is now $DesiredState." $verifiedDocument
}

Assert-AuthorizedMutation
$result=if($Action -eq 'Status'){$doc=Read-ModListDocument;$entry=Get-ModEntry $doc;New-Result $entry.State $false $null "${ModName}: $($entry.State)" $doc}elseif($Action -eq 'Enable'){Set-DesiredState Enabled}else{Set-DesiredState Disabled}
if($PassThru){$result}else{$result.Message}
