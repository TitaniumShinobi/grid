$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent $PSScriptRoot
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $gameRoot '..\..\..'))
. (Join-Path $repositoryRoot 'scripts\health\Grid.CaseStore.ps1')
. (Join-Path $gameRoot 'health\collectors\Invoke-GridSkyrimBaseline.ps1')

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }

$tempRoot = Join-Path $env:TEMP ('grid-crash-signature-tests-' + [guid]::NewGuid().ToString('N'))
$storeRoot = Join-Path $tempRoot 'store'
$normalizedRoot = Join-Path $tempRoot 'normalized'
New-Item -ItemType Directory -Path $normalizedRoot -Force | Out-Null
try {
    $transaction = New-GridCaseStoreTransaction -StoreRoot $storeRoot -CaseId 'crash-signature-fixture'
    $logPath = Join-Path $tempRoot 'crash-fixture.log'
    [IO.File]::WriteAllText($logPath, @'
Skyrim SSE v1.6.1170
CrashLoggerSSE fixture

Unhandled exception "EXCEPTION_ACCESS_VIOLATION" at 0x7FFE00001234 OStim.dll+0001234

PROBABLE CALL STACK:
    [ 0] 0x7FFE00001234 OStim.dll+0001234
    [ 1] 0x7FFE00005678 OStim.dll+0005678

REGISTERS:
'@, ([Text.UTF8Encoding]::new($false)))
    $blob = Add-GridCaseStoreBlob -Transaction $transaction -LiteralPath $logPath
    $winner = [pscustomobject]@{virtualPath='SKSE\Plugins\OStim.dll';provider=[pscustomobject]@{id=[pscustomobject]@{value='provider.fixture'};modId=[pscustomobject]@{value='mod.fixture'};sourceName='OStim Standalone';isWinner=$true;fingerprint=('A'*64)}}
    $brokenWinner = [pscustomobject]@{virtualPath='SKSE\Plugins\Broken.dll';provider=[pscustomobject]@{id=[pscustomobject]@{value='provider.broken'};modId=[pscustomobject]@{value='mod.broken'};sourceName='Broken Native Mod';isWinner=$true;fingerprint=('B'*64)}}
    $providerLines = @($winner,$brokenWinner) | ForEach-Object { $_ | ConvertTo-Json -Depth 10 -Compress }
    [IO.File]::WriteAllLines((Join-Path $normalizedRoot 'virtualProvider.ndjson'), $providerLines, ([Text.UTF8Encoding]::new($false)))
    $skseLogPath = Join-Path $tempRoot 'skse64.log'
    [IO.File]::WriteAllText($skseLogPath, @'
plugin OStim.dll (00000001 OStim 00010002) loaded correctly (handle 1)
plugin Broken.dll (00000001 Broken Native 00020003) disabled, incompatible with current version of the game 0 (handle 0)
'@, ([Text.UTF8Encoding]::new($false)))
    $skseBlob = Add-GridCaseStoreBlob -Transaction $transaction -LiteralPath $skseLogPath
    $normalized = [pscustomobject]@{StagingDirectory=$normalizedRoot;DiagnosticOutputs=@(
        [pscustomobject]@{virtualPath='diagnostics/skse/crash-fixture.log';blobSha256=$blob.sha256;lastWriteTimeUtcTicks=123},
        [pscustomobject]@{virtualPath='diagnostics/skse/skse64.log';blobSha256=$skseBlob.sha256;lastWriteTimeUtcTicks=124}
    )}
    $summary = New-GridSkyrimCrashSignatureSummary -Transaction $transaction -Normalized $normalized
    Assert-Equal 'Complete' $summary.status 'A supported CrashLogger record must parse.'
    Assert-Equal 1 $summary.parsedCrashCount 'One crash must be retained.'
    Assert-Equal 'OStim.dll' $summary.clusters[0].exceptionModule 'The exception-address module must be exact.'
    Assert-Equal 'OStim Standalone' $summary.clusters[0].winningProvider.providerName 'The exception module must correlate to the current MO2 left-panel winner.'
    Assert-True ($summary.clusters[0].limitation -match 'not by itself') 'The result must not overclaim causal attribution.'
    Assert-True ($null -ne $normalized.WinningDllProviders) 'The winning DLL-provider map must be retained for the remaining summaries.'
    Remove-Item -LiteralPath (Join-Path $normalizedRoot 'virtualProvider.ndjson') -Force
    $load = New-GridSkyrimSksePluginLoadSummary -Transaction $transaction -Normalized $normalized
    Assert-Equal 'Complete' $load.status 'A supported skse64.log must parse.'
    Assert-Equal 2 $load.parsedPluginCount 'Both terminal SKSE plugin reports must be retained.'
    Assert-Equal 1 $load.loadedCount 'The successful native plugin load must be counted.'
    Assert-Equal 1 $load.rejectedCount 'The rejected native plugin must be counted.'
    Assert-Equal 'Broken Native Mod' $load.rejected[0].winningProvider.providerName 'The rejected DLL must reuse the already-derived MO2 left-panel winner map.'
    Assert-Equal 'disabled, incompatible with current version of the game' $load.rejected[0].errorText 'The exact SKSE rejection reason must be retained.'
    'Crash and SKSE plugin-load checks passed.'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
