$ErrorActionPreference = 'Stop'
$collector = Join-Path (Split-Path -Parent $PSScriptRoot) 'health\collectors\Get-GridLootExistingOutput.ps1'
. $collector
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }
$root = Join-Path $env:TEMP ('grid-loot-existing-output-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $path = Join-Path $root 'loot-report.json'
    Set-Content -LiteralPath $path -Value '{"messages":[]}' -Encoding UTF8
    $result = Get-GridLootExistingOutput -CandidatePaths @($path)
    Assert-Equal 'Collected' $result.status 'An exact stable existing LOOT output must be observed.'
    Assert-Equal 1 @($result.records).Count 'One input must produce one record.'
    Assert-True (-not $result.launchPerformed) 'Existing-output observation must never launch LOOT.'
    Assert-True (-not $result.mutationAuthorized) 'Existing-output observation grants no mutation.'
    Assert-Equal 'NotParsed' $result.records[0].parseStatus 'An arbitrary JSON file must not be invented into LOOT findings.'
    $textPath=Join-Path $root 'loot-report.txt'
    Set-Content -LiteralPath $textPath -Encoding UTF8 -Value @(
        'Warnings:',
        'The folder and file with hashes 82d562074277265 and 92e8981c6208dfee in "D:/Skyrim/Data/Textures.bsa" are present in another BSA.',
        'General messages',
        'Latest LOOT thread.',
        'Plugins',
        'InnCredible.esp',
        'SSEEdit v4.0.2f found 6 ITM record(s), 0 deleted reference(s) and 3 deleted navmesh(es).',
        'Immersive Encounters.esp',
        'You seem to be using The Great City of Solitude, but you have not enabled a compatibility patch for this mod.',
        'SkyUI_SE.esp',
        "Warning: Another mod seems to be overwriting one of this mod's essential files."
    )
    $typed=Get-GridLootExistingOutput -CandidatePaths @($textPath) -ContextFingerprint ('A'*64)
    Assert-Equal 'Complete' $typed.records[0].parseStatus 'A bounded text report must parse completely.'
    Assert-Equal 5 @($typed.records[0].findings).Count 'Every report message must remain visible.'
    Assert-Equal 'DuplicateBsaEntryReport' $typed.records[0].findings[0].kind 'Duplicate archive entries are tool assertions, not proven corruption.'
    Assert-Equal 'DeletedNavmeshReport' $typed.records[0].findings[2].kind 'Deleted navmesh reports must be typed.'
    Assert-Equal 'InnCredible.esp' $typed.records[0].findings[2].subjectPlugin 'A plugin finding must retain its exact heading.'
    Assert-Equal 'CompatibilityPatchCandidate' $typed.records[0].findings[3].kind 'Missing patch messages are candidates, not enabled repairs.'
    Assert-Equal 'FileWinnerConcern' $typed.records[0].findings[4].kind 'Overwritten essential-file warnings must be typed.'
    Assert-True (@($typed.records[0].findings|Where-Object verificationStatus -ne 'ReportedUncorroborated').Count -eq 0) 'LOOT assertions alone cannot verify a repair diagnosis.'
    $unbound=Get-GridLootExistingOutput -CandidatePaths @($textPath)
    Assert-Equal 'Unbound' $unbound.records[0].findings[0].contextStatus 'A report without current-profile fingerprint cannot drive repair.'
    $missing = Get-GridLootExistingOutput -CandidatePaths @((Join-Path $root 'missing.json'))
    Assert-Equal 'Unavailable' $missing.status 'Missing optional existing output must be honest.'
    Write-Host 'PASS: LOOT reports are bounded, hash-bound, typed as uncorroborated evidence, and never launch LOOT.'
} finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
