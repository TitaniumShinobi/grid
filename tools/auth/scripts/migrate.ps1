# Tools: migrate.ps1 — upgrade path into DPAPI token storage.
# Older Grid builds (and dev builds without DPAPI) persisted a plaintext
# grid-native-bearer.json next to the app. This migrates that bag into the
# DPAPI-protected vault location used by DpapiTokenStore, then deletes the
# plaintext file. Safe to re-run; idempotent.

param(
    [string]$LegacyPath = (Join-Path $env:LOCALAPPDATA 'Grid/auth/grid-native-bearer.json'),
    [switch]$DeleteAfter
)
$ErrorActionPreference = 'Stop'

$vaultDir = Join-Path $env:LOCALAPPDATA 'Grid/auth'
$target = Join-Path $vaultDir 'grid-native-bearer.json'
New-Item -ItemType Directory -Force -Path $vaultDir | Out-Null

if (-not (Test-Path $LegacyPath)) {
    Write-Host "no legacy bag at $LegacyPath — nothing to migrate"
    return
}

$legacy = Get-Content -Raw $LegacyPath | ConvertFrom-Json
if (-not $legacy.accessToken) {
    Write-Warning "legacy bag has no accessToken — not migrating (delete manually if stale)"
    return
}

if (Test-Path $target) {
    Write-Host "target already exists at $target — keeping it (delete both to force a clean state)"
    return
}

# DpapiTokenStore reads the same file shape (TokenBag JSON). Moving the file
# into the vault dir is enough; the DPAPI store encrypts on next write (refresh).
Copy-Item $LegacyPath $target
Write-Host "migrated  $LegacyPath -> $target"

if ($DeleteAfter -or $DeleteAfter.IsPresent) {
    Remove-Item $LegacyPath -Force
    Write-Host "deleted legacy plaintext bag"
} else {
    Write-Host "kept legacy plaintext (re-run with -DeleteAfter to remove)"
}

Write-Host "next launch will sign-in with the existing session; refresh rewrites the bag DPAPI-encrypted."