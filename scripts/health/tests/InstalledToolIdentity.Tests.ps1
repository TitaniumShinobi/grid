#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Grid([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

$script = Join-Path (Split-Path $PSScriptRoot -Parent) 'Get-GridInstalledToolIdentity.ps1'
$executable = Join-Path $PSHOME 'powershell.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { $executable = Join-Path $PSHOME 'pwsh.exe' }
$result = (& $script -ExecutablePath $executable) | ConvertFrom-Json

Assert-Grid ($result.schemaVersion -eq 1) 'Installed-tool observation must use schema version 1.'
Assert-Grid ($result.executablePath -eq [System.IO.Path]::GetFullPath($executable)) 'Observation must remain bound to the exact selected executable.'
Assert-Grid ($result.sha256 -match '^[a-f0-9]{64}$') 'Observation must include a SHA-256 receipt.'
Assert-Grid (@($result.evidence).Count -gt 4) 'Observation must include bounded metadata provenance.'
Assert-Grid (@($result.PSObject.Properties.Name) -notcontains 'compatibleGameIds') 'Local installation evidence must not invent compatible GameIDs.'
Assert-Grid (@($result.PSObject.Properties.Name) -notcontains 'canonicalToolId') 'Local installation evidence must not invent a canonical ToolID.'

Write-Output 'PASS: installed-tool exact-file observation remains provenance-backed and compatibility-neutral.'
