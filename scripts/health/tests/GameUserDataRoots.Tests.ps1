#requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$healthRoot = Split-Path -Parent $PSScriptRoot
$scriptsRoot = Split-Path -Parent $healthRoot
. (Join-Path $healthRoot 'Grid.GameUserDataRoots.ps1')

function Assert-Grid([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-GridThrows([scriptblock]$Action, [string]$Pattern) {
    try { & $Action; throw 'Expected the operation to fail.' }
    catch {
        if ($_.Exception.Message -eq 'Expected the operation to fail.' -or $_.Exception.Message -notmatch $Pattern) { throw }
    }
}

$knownFolders = @{
    Documents = 'C:\Users\Fixture\Documents'
    LocalApplicationData = 'C:\Users\Fixture\AppData\Local'
}

$skyrim = Resolve-GridGameUserDataRoots -ScriptsRoot $scriptsRoot -GameId 'skyrimspecialedition' -KnownFolders $knownFolders
Assert-Grid ($skyrim.roots.Count -eq 2) 'Skyrim resolves one protected user-data root and one diagnostic root.'
$skyrimUser = @($skyrim.roots | Where-Object rootId -eq 'skyrimUserData')[0]
$skse = @($skyrim.roots | Where-Object rootId -eq 'skseDiagnostics')[0]
Assert-Grid ($skyrimUser.path -eq 'C:\Users\Fixture\Documents\My Games\Skyrim Special Edition') 'Skyrim user data resolves beneath Documents/My Games.'
Assert-Grid (@($skyrimUser.artifacts | Where-Object artifactId -eq 'saves')[0].observation -eq 'PresenceOnly') 'Skyrim save contents remain excluded.'
Assert-Grid ($skse.path -eq 'C:\Users\Fixture\Documents\My Games\Skyrim Special Edition\SKSE') 'SKSE diagnostics are distinct from Skyrim INIs and saves.'
Assert-Grid (@($skse.artifacts | ForEach-Object { Split-Path -Leaf $_.path }) -contains 'crash-*.log') 'Skyrim includes the observed crash-log leaf pattern.'
Assert-Grid ($skse.collectionPolicy -eq 'BoundedTopLevelFiles' -and $skse.maxFiles -eq 256) 'SKSE diagnostic reads are explicitly bounded.'

$gta = Resolve-GridGameUserDataRoots -ScriptsRoot $scriptsRoot -GameId 'grandtheftautov' -KnownFolders $knownFolders
$gtaUser = @($gta.roots | Where-Object rootId -eq 'gtavUserData')[0]
$gtaLogs = @($gta.roots | Where-Object rootId -eq 'gtavDiagnostics')[0]
Assert-Grid ($gtaUser.path -eq 'C:\Users\Fixture\Documents\Rockstar Games\GTA V') 'The observed GTA variant resolves its exact game-owned Documents root.'
Assert-Grid ($gtaUser.authority -eq 'ObservedLocal' -and $gtaUser.confidence -eq 'Observed') 'The GTA V path is not promoted to an unsupported official claim.'
Assert-Grid ($gtaUser.excludedPaths -contains 'C:\Users\Fixture\Documents\Rockstar Games\GTA V\Profiles') 'GTA profile/save contents are explicitly excluded.'
Assert-Grid (@($gtaUser.artifacts | Where-Object { $_.path -like '*\Profiles\*' }).Count -eq 0) 'No GTA profile or save artifact is collectible.'
Assert-Grid ($gtaLogs.collectionPolicy -eq 'BoundedTopLevelFiles' -and $gtaLogs.maxFiles -eq 16) 'GTA diagnostic reads are bounded and top-level only.'
Assert-Grid ((ConvertTo-Json $gta -Depth 10 -Compress) -ceq (ConvertTo-Json (Resolve-GridGameUserDataRoots -ScriptsRoot $scriptsRoot -GameId 'grandtheftautov' -KnownFolders $knownFolders) -Depth 10 -Compress)) 'Root resolution is deterministic.'

Assert-GridThrows { Resolve-GridGameUserDataRoots -ScriptsRoot $scriptsRoot -GameId 'skyrimspecialedition' -KnownFolders @{ Documents = '\\server\share'; LocalApplicationData = $knownFolders.LocalApplicationData } } 'explicit local fully-qualified path'

$maliciousRoot = Join-Path ([IO.Path]::GetTempPath()) ('grid-game-roots-' + [guid]::NewGuid().ToString('N'))
try {
    $definitionDirectory = Join-Path $maliciousRoot 'games\fixturegame'
    New-Item -ItemType Directory -Path $definitionDirectory -Force | Out-Null
    @'
{"schemaVersion":1,"gameId":"fixturegame","displayName":"Fixture","roots":[{"rootId":"fixtureRoot","anchor":"Documents","relativePath":"../escape","role":"UserData","collectionPolicy":"ProtectedMetadataOnly","authority":"ObservedLocal","confidence":"Observed","sourceIds":["fixture"],"artifacts":[],"excludedRelativePaths":[]}],"evidenceSources":[{"sourceId":"fixture","authority":"ObservedLocal","claim":"Fixture."}]}
'@ | Set-Content -LiteralPath (Join-Path $definitionDirectory 'user-data-roots.v1.json') -Encoding UTF8
    Assert-GridThrows { Resolve-GridGameUserDataRoots -ScriptsRoot $maliciousRoot -GameId 'fixturegame' -KnownFolders $knownFolders } 'traversal'
}
finally {
    if (Test-Path -LiteralPath $maliciousRoot) { Remove-Item -LiteralPath $maliciousRoot -Recurse -Force }
}

'Game user-data root tests passed.'
