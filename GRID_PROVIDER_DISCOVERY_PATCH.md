# Grid provider discovery implementation slice

Patch revision: v5. Adds the production Add Game workflow, reviewed registration persistence, catalog projection, Browse fallback, manager relationship display, restart persistence, and Grid-only disconnect behavior.

This cumulative patch adds the first production-shaped provider discovery and GTA V registration foundation. It also includes the PowerShell 5.1 corrections already validated for GTA deployment.

## Included

- Steam client and multi-library discovery without fixed drive letters.
- Declarative Steam game definitions, beginning with GTA V Legacy and GTA V Enhanced as separate identities.
- Read-only MO2 and Vortex discovery from explicit locations, active executable paths, Windows registrations, and common installation locations.
- MO2 portable/global instance and profile enumeration.
- Canonical installation reconciliation so multiple provider observations do not become duplicate games.
- A GTA V production catalog entry with no fabricated installation.
- A Windows PowerShell 5.1 contract suite and a real-device scan command.
- GTA deployment rollback and JSON-receipt compatibility corrections.
- A WinUI Add Game review surface backed by the packaged discovery engine.
- Grid-owned, idempotent game registration persistence and production catalog projection.

## Apply

Extract the ZIP, then copy the contents of its `grid` directory over the Grid repository root.

## Test on Windows

From the Grid repository root:

```powershell
powershell.exe -ExecutionPolicy Bypass -File `
  ".\scripts\health\tests\ProviderDiscovery.Tests.ps1"

powershell.exe -ExecutionPolicy Bypass -File `
  ".\scripts\games\grandtheftautov\tests\GtaSetupReadiness.Tests.ps1"

powershell.exe -ExecutionPolicy Bypass -File `
  ".\scripts\games\grandtheftautov\tests\GtaDeployment.Tests.ps1"

dotnet build ".\Grid.sln" -c Debug -p:Platform=x64

dotnet run --project ".\tests\Grid.Core.Tests\Grid.Core.Tests.csproj" -c Debug
```

Run read-only discovery against the actual device:

```powershell
powershell.exe -ExecutionPolicy Bypass -File `
  ".\scripts\health\Invoke-GridProviderDiscovery.ps1" `
  -OutputPath "$env:LOCALAPPDATA\Grid\discovery\provider-scan.v1.json"
```

Optional path parameters remain available when a portable installation is closed or not registered:

```powershell
.\scripts\health\Invoke-GridProviderDiscovery.ps1 `
  -SteamRoot "D:\SteamLibrary" `
  -Mo2Root "D:\Path\To\MO2" `
  -Mo2InstanceRoot "D:\Path\To\MO2 Instance" `
  -VortexRoot "$env:LOCALAPPDATA\Programs\Vortex"
```

## Current boundary

This slice binds reviewed candidates into the desktop registration flow. An authoritative Vortex state adapter remains a later slice; until then, Grid displays the detected Vortex executable relationship without claiming authoritative Vortex profile state. Manual Browse remains the fallback for closed portable installations that provide no deterministic system evidence.
