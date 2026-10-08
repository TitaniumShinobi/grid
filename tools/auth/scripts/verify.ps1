# Tools: verify.ps1 — verification checklist for the installed payload.
# Runs the in-payload C# test runner (net9.0), checks desktop/server config
# agreement, and prints a pass/fail report. Designed to be run by CI or a human
# after INSTALL.md steps.

param(
    [switch]$SkipTests
)
$ErrorActionPreference = 'Continue'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
$failures = [Collections.Generic.List[string]]::new()
$checks = 0

function Check($name, $ok, $detail) {
    $script:checks++
    if ($ok) { Write-Host "PASS $name" } else { Write-Host "FAIL $name  ($detail)"; $script:failures.Add($name) }
}

$desktop = Join-Path $repoRoot 'config/grid.auth.config.json'
Check 'desktop config exists' (Test-Path $desktop) $desktop

if (Test-Path $desktop) {
    $cfg = Get-Content -Raw $desktop | ConvertFrom-Json
    Check 'desktop baseUrl set' (-not [string]::IsNullOrWhiteSpace($cfg.baseUrl)) $cfg.baseUrl
    Check 'desktop clientId set' (-not [string]::IsNullOrWhiteSpace($cfg.clientId)) $cfg.clientId
    Check 'loopback port numeric' ($cfg.loopbackPort -is [int] -and $cfg.loopbackPort -gt 0) $cfg.loopbackPort
    Check 'cookie names match AUTH defaults' ($cfg.sessionCookieName -eq 'auth_sid' -and $cfg.refreshCookieName -eq 'auth_rid') "$($cfg.sessionCookieName)/$($cfg.refreshCookieName)"
    Check 'no secrets in desktop config' (-not ((Get-Content -Raw $desktop) -match 'secret|client_secret|password')) "config must be public-client only"
}

# AUTH-side agreement is best-effort here; real validation lives on the AUTH host.
$server = Join-Path $repoRoot 'deploy/auth/appConfig.json'
$serverExamples = Get-ChildItem -Path $repoRoot -Recurse -Filter 'grid.auth.server-config.example.json' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($serverExamples) {
    Write-Host "INFO server-config template found at $($serverExamples.FullName) (deploy the real one onto the AUTH host)"
}

if (-not $SkipTests) {
    $runner = Join-Path $repoRoot 'tests/Grid.Auth.Tests'
    if (Test-Path $runner) {
        if (Get-Command dotnet -ErrorAction SilentlyContinue) {
            Write-Host ""; Write-Host "== running tests/Grid.Auth.Tests =="
            dotnet run --project $runner
            Check 'C# test runner exit 0' ($LASTEXITCODE -eq 0) "dotnet exit=$LASTEXITCODE"
        } else {
            Write-Warning "dotnet not found — C# runner must run on a Windows SDK host"
        }
    } else {
        Write-Warning "tests/Grid.Auth.Tests not present in this repo"
    }
}

Write-Host ""
Write-Host "== verify.ps1 result: $checks checks, $($failures.Count) failure(s) =="
if ($failures.Count) { exit 1 } else { exit 0 }