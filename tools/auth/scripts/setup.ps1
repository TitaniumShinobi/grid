# Tools: setup.ps1 — one-shot provisioning for Grid desktop auth.
# Run from the Grid repo root AFTER the payload has been placed (see INSTALL.md
# for the deliberate copy step). Creates real config files from the payload
# examples (never overwrites existing), validates secrets, prints a checklist.

param()
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
Write-Host "repo root: $repoRoot"

# 1. Desktop config
$cfgDir = Join-Path $repoRoot 'config'
$example = Join-Path $cfgDir 'grid.auth.config.example.json'
$real = Join-Path $cfgDir 'grid.auth.config.json'
if (Test-Path $real) {
    Write-Host "OK  $real already present"
} elseif (Test-Path $example) {
    Copy-Item $example $real
    Write-Host "created  $real  (review baseUrl + clientId)"
} else {
    Write-Warning "missing both config files — copy the payload's config/ into the repo first"
}

# 2. Secret-length gate (AUTH_MAGIC_DELIVERY_SECRET is a bearer delivery secret)
foreach ($var in 'AUTH_MAGIC_DELIVERY_SECRET') {
    if (Test-Path Env:$var) {
        if ((Get-Item Env:$var).Value.Length -lt 32) {
            Write-Warning "$var present but < 32 chars — AUTH delivery will reject it"
        } else {
            Write-Host "OK  $var configured"
        }
    } else {
        Write-Warning "$var not set in this shell (set it in the AUTH deployment environment)"
    }
}

Write-Host ""
Write-Host "Checklist:"
Write-Host "  1. AUTH deployment env: GRID_MICROSOFT_CLIENT_ID/SECRET, GRID_GITHUB_*, GRID_GOOGLE_*,"
Write-Host "     AUTH_COOKIE_NAME, AUTH_REFRESH_COOKIE_NAME."
Write-Host "  2. AuthAppConfig allowedOrigins contains http://127.0.0.1:51706; publicClients[] has grid-windows."
Write-Host "  3. dotnet run --project tests/Grid.Auth.Tests   (net9.0, no NuGet)."
Write-Host "  4. Grid.csproj uses default Compile globbing (core/**, excluding core/features/users/auth/ui from any verification project)."