# Lightweight binding exporter (repo-local)

Requires Node 20+, Git, and the installed durability kit. Uses `.auth-kit/load-active-product-contract.mjs` so export matches the upgrade-aware verifier path. Preserve `AUTH_GIT_EXECUTABLE` on Windows if needed.

## Fast path

Use the most recent **current** reviewed product VERIFY report. Review binding and report SHA-256 locally before export.

```powershell
$Grid = 'C:\Users\woods\Documents\GitHub\grid'
$Exporter = Join-Path $Grid 'scripts\auth-authority-binding-export\auth-export-authority-binding.mjs'
$env:GRID_AUTH_AUTHORITY_ID = 'https://grid.thewreck.org'
$env:GRID_AUTH_AUTHORITY_REVISION = '<40-64-hex-serving-authority-revision>'
$env:AUTH_GIT_EXECUTABLE = (Get-Command git.exe -CommandType Application).Source
$Report = Get-ChildItem "$Grid\.auth-kit\reports\*.json" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$ReportHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Report.FullName).Hash.ToLowerInvariant()
$Out = Join-Path $env:TEMP ('grid-authority-binding-' + [guid]::NewGuid() + '.json')
node $Exporter $Grid $Out --report $Report.FullName --report-sha256 $ReportHash
```

Output must be outside the product tree. No observer run on the fast path unless `--refresh-source` is used.

## Deliberate refresh

```powershell
node $Exporter $Grid $Out --refresh-source
```

Invokes canonical full verification and may create verification evidence/reports under `.auth-kit/reports/`; use only when that operation is separately authorized. Can take minutes.
