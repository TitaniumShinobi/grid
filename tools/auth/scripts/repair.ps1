# Tools: repair.ps1 — fix the common integration breakages.
# Idempotent. Validates desktop/server config agreement, re-runs the C# test
# runner if dotnet exists, clears only *stale* persisted state, and reports the
# loopback listener status.

param(
    [switch]$ClearState,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Continue'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path

Write-Host "== grid auth repair =="

# 1. Config agreement
$desktop = Join-Path $repoRoot 'config/grid.auth.config.json'
if (Test-Path $desktop) {
    $cfg = Get-Content -Raw $desktop | ConvertFrom-Json
    Write-Host "desktop baseUrl     : $($cfg.baseUrl)"
    Write-Host "desktop loopback    : http://$($cfg.loopbackHost):$($cfg.loopbackPort)$($cfg.loopbackPath)"
    if ($cfg.refreshCookieName -ne 'auth_rid' -or $cfg.sessionCookieName -ne 'auth_sid') {
        Write-Warning "cookie names differ from AUTH defaults — ensure AUTH_COOKIE_NAME/AUTH_REFRESH_COOKIE_NAME match (docs/reference/auth-config-schema.md)"
    }
} else {
    Write-Warning "no desktop config — run setup.ps1"
}

# 2. Loopback port free?
if (Test-Path $desktop) {
    $port = (Get-Content -Raw $desktop | ConvertFrom-Json).loopbackPort
    $busy = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
    if ($busy) {
        # Our own listener would be listed too — this is only a warning if the
        # owning process is not the Grid exe.
        $owners = $busy | ForEach-Object { (Get-Process -Id $_.OwningProcess).ProcessName }
        if ($owners -notcontains 'Grid') {
            Write-Warning "port $port is held by: $($owners -join ', ') — close that app or change loopbackPort (and AUTH allowedOrigins)"
        } else {
            Write-Host "OK  loopback listener (Grid) active on :$port"
        }
    } else {
        Write-Host "OK  loopback port :$port free (listener starts on first sign-in attempt)"
    }
}

# 3. Token store sanity
$vault = Join-Path $env:LOCALAPPDATA 'Grid/auth'
if (-not (Test-Path $vault)) {
    Write-Host "no persisted tokens yet at $vault"
} elseif ($ClearState) {
    Remove-Item (Join-Path $vault '*') -Force -ErrorAction SilentlyContinue
    Write-Warning "cleared persisted token state (user must sign in again)"
} else {
    Write-Host "persisted token store present (DPAPI vault dir: $vault); pass -ClearState to force a clean sign-in"
}

# 4. Tests
if (-not $SkipTests) {
    if (Get-Command dotnet -ErrorAction SilentlyContinue) {
        Write-Host "running tests/Grid.Auth.Tests ..."
        dotnet run --project (Join-Path $repoRoot 'tests/Grid.Auth.Tests')
    } else {
        Write-Host "dotnet not found — run the C# runner on a Windows SDK host (see docs/how-to/setup-and-verify.md)"
    }
}

Write-Host "repair done."