$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent $PSScriptRoot
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $gameRoot)
Import-Module (Join-Path $scriptsRoot 'health\Grid.Health.psm1') -Force
$collectorRoot = Join-Path $gameRoot 'health\collectors'
. (Join-Path $collectorRoot 'Get-GridXEditLoaderProgress.ps1')
$collectorSource = Get-Content -Raw -LiteralPath (Join-Path $collectorRoot 'Get-GridXEditLoaderProgress.ps1')

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }

Assert-True (-not $collectorSource.Contains('Get-GridProcessWindowSnapshot')) 'The 500 ms xEdit hot loop must not call blocking native child-window enumeration.'
Write-Host 'PASS: loader-progress hot-loop telemetry is bounded to direct process state.'

$tempRoot = Join-Path $env:TEMP ('grid-loader-progress-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
$fixtureProcess = $null
try {
    # A dedicated WinForms host is used instead of a real application so the fixture is deterministic across Windows versions.
    $fixtureScript = Join-Path $tempRoot 'fixture.ps1'
    Set-Content -LiteralPath $fixtureScript -Encoding UTF8 -Value @'
Add-Type -AssemblyName System.Windows.Forms
$form = New-Object System.Windows.Forms.Form
$form.Text = 'GridLoaderProgressFixture'
$form.Width = 200
$form.Height = 100
[System.Windows.Forms.Application]::Run($form)
'@
    $fixtureProcess = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $fixtureScript) -PassThru
    $deadline = (Get-Date).AddSeconds(15)
    while ($fixtureProcess.MainWindowHandle -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200; $fixtureProcess.Refresh() }
    Assert-True ($fixtureProcess.MainWindowHandle -ne [IntPtr]::Zero) 'Fixture process must expose a main window before sampling.'

    $sample1 = Get-GridXEditLoaderProgressSample -XEditProcess $fixtureProcess -StartedAt (Get-Date).AddSeconds(-1)
    Assert-True ($null -eq $sample1.CpuDeltaSeconds) 'First sample without a previous CPU time must have a null delta.'
    Assert-Equal $fixtureProcess.Id $sample1.ProcessId 'Sample must record the sampled process ID.'
    Assert-True ($sample1.WindowCount -ge 1) 'A running process with a visible window must report at least one window.'
    Assert-True (-not $sample1.ModalDialogDetected) 'A single plain window must not be classified as a blocking modal dialog.'
    Assert-True ($sample1.Responding -is [bool]) 'Sample must record the process Responding state.'

    $sample2 = Get-GridXEditLoaderProgressSample -XEditProcess $fixtureProcess -StartedAt (Get-Date).AddSeconds(-2) -PreviousCpuTime $sample1.CpuTime
    Assert-True ($null -ne $sample2.CpuDeltaSeconds) 'A sample with a previous CPU time must compute a delta.'
    Write-Host 'PASS: loader-progress sampling reports bounded main-window and CPU/memory evidence.'


    $progressPath = Join-Path $tempRoot 'xedit-loader-progress.tsv'
    Add-GridXEditLoaderProgressRow -StatusPath $progressPath -Sample $sample1 -IncludeHeader
    Add-GridXEditLoaderProgressRow -StatusPath $progressPath -Sample $sample2
    $lines = @(Get-Content -LiteralPath $progressPath)
    Assert-Equal 3 $lines.Count 'Loader-progress TSV must contain a header plus one row per sample.'
    Assert-True ($lines[0].StartsWith('Timestamp')) 'Loader-progress TSV must begin with a header row.'
    Assert-True ($lines[0].Contains('ProcessId') -and $lines[0].Contains('Responding')) 'Loader-progress TSV must record ProcessId and Responding.'
    Write-Host 'PASS: loader-progress rows are appended with a stable TSV schema.'

    Assert-Equal 'ScriptInvoked' (Get-GridXEditLoaderInference -ScriptInvoked $true -Samples @($sample1, $sample2)) 'A status file entry must be reported as ScriptInvoked regardless of samples.'
    $busySample = [pscustomobject]@{ ModalDialogDetected = $false; CpuDeltaSeconds = 1.0 }
    Assert-Equal 'StillLoading' (Get-GridXEditLoaderInference -ScriptInvoked $false -Samples @($busySample)) 'Sustained CPU activity without a status file must infer StillLoading.'
    $idleSample = [pscustomobject]@{ ModalDialogDetected = $false; CpuDeltaSeconds = 0.0 }
    Assert-Equal 'PossiblyHung' (Get-GridXEditLoaderInference -ScriptInvoked $false -Samples @($idleSample)) 'No CPU activity and no modal window must infer PossiblyHung.'
    $modalSample = [pscustomobject]@{ ModalDialogDetected = $true; CpuDeltaSeconds = 0.0 }
    Assert-Equal 'ModalDialogBlocking' (Get-GridXEditLoaderInference -ScriptInvoked $false -Samples @($modalSample)) 'A detected modal window must infer ModalDialogBlocking.'
    Write-Host 'PASS: loader inference distinguishes still-loading, possibly-hung, modal-blocking, and script-invoked.'
}
finally {
    if ($fixtureProcess -and -not $fixtureProcess.HasExited) { Stop-Process -Id $fixtureProcess.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
