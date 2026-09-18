# Vortex-backed GTA V catalog

Grid can attach a registered GTA V installation to a Vortex mod staging directory. The connection is stored in Grid-owned local state at `connections/vortex-installations.v1.json`; Vortex and game files are not rewritten.

## User flow

1. Choose **Add game** and review the detected GTA V installation.
2. When Vortex is detected, choose **Browse** under **Vortex catalog** and select Vortex's GTA V mod staging folder.
3. Register the installation. Re-registering an existing installation is idempotent and can be used to add or update its Vortex staging connection.
4. Grid creates a **Vortex staging** profile and refreshes its projected directory inventory while Grid is open. Restart and manual catalog refresh also rebuild the inventory.

## Evidence boundary

The staging-directory observer is read-only and Grid-derived. A directory proves that a staged package exists; it does not establish Vortex's authoritative enabled, deployed, profile, version, update, or conflict state. Projected entries therefore use `UnlistedDirectory`, have no manager priority, and expose partial observation status. A later Vortex state bridge can replace this partial projection without changing the persisted installation identity.

## Ownership and recovery

- Game registration: `%LOCALAPPDATA%\Grid\connections\game-installations.v1.json`
- Vortex relationship: `%LOCALAPPDATA%\Grid\connections\vortex-installations.v1.json`
- Disconnecting the Vortex relationship removes only Grid's connection record.
- Existing Vortex staging content and GTA V files remain untouched.

## Verification

`tests/Grid.Core.Tests` covers connection persistence, initial projection, growth after adding another staged mod directory, non-fabrication of enabled state, and connection removal. Run it on the supported Windows/.NET toolchain:

```powershell
dotnet run --project .\tests\Grid.Core.Tests\Grid.Core.Tests.csproj
```
