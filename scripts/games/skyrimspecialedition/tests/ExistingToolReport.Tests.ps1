$ErrorActionPreference='Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'health\collectors\Get-GridSkyrimExistingToolReport.ps1')
function Assert-True([bool]$Value,[string]$Message){if(-not$Value){throw $Message}}
function Assert-Equal($Expected,$Actual,[string]$Message){if($Expected -ne $Actual){throw "$Message Expected '$Expected', got '$Actual'."}}
$root=Join-Path $env:TEMP ('grid-tool-report-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root|Out-Null
try{
    $log=Join-Path $root 'DynDOLOD_SSE_log.txt'
    Set-Content -LiteralPath $log -Encoding UTF8 -Value @('DynDOLOD version 3','Warning: missing texture textures\test.dds','Completed in 10 seconds','Warning: api_key=private-fixture-value')
    $artifact=Join-Path $root 'DynDOLOD.esp'
    Set-Content -LiteralPath $artifact -Encoding Byte -Value ([byte[]]@(1,2,3,4))
    $result=Get-GridSkyrimExistingToolReport -ToolId grid.tool.dyndolod -CandidatePaths @($log,$artifact) -ContextFingerprint ('B'*64)
    Assert-Equal Collected $result.status 'Existing report and artifact must collect.'
    Assert-True (-not$result.launchPerformed -and -not$result.mutationAuthorized) 'Report intake must not launch or mutate.'
    $text=@($result.records|Where-Object path -eq $log)[0]
    $binary=@($result.records|Where-Object path -eq $artifact)[0]
    Assert-Equal Complete $text.parseStatus 'Bounded text logs must be scanned.'
    Assert-Equal 4 $text.messageCount 'Lifecycle, warning, and credential-omission lines must remain accounted for.'
    Assert-Equal NotParsed $binary.parseStatus 'Generated plugin bytes must be fingerprinted, not interpreted as log text.'
    Assert-True (-not(@($text.messages.text)-join ' ').Contains('private-fixture-value')) 'Potential credential lines must not be copied into evidence.'
    Assert-True (@($text.messages|Where-Object verificationStatus -ne 'ReportedUncorroborated').Count -eq 0) 'Tool log lines cannot certify a diagnosis.'
    $missing=Get-GridSkyrimExistingToolReport -ToolId grid.tool.texgen -CandidatePaths @((Join-Path $root 'missing.log'))
    Assert-Equal Unavailable $missing.status 'Missing after-run output must be honest.'
    'PASS: selected Skyrim after-run tool reports and artifacts are exact, bounded, read-only, and uncorroborated.'
}finally{Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue}
