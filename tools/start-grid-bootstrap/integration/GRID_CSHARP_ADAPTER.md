# GRID C# startup adapter (Contract 1C)

GRID implements the bootstrap observer surface in-process. There is no Node runtime,
daemon, or START dependency at product launch.

| Bootstrap surface | GRID owner |
| --- | --- |
| `contracts/` + `schemas/` | Copied to `tools/start-grid-bootstrap/` and mirrored under `start/` |
| Event vocabulary + `progress_units` | `src/Grid.Core/Startup/StartupInstrumentation.cs` (`*.events.jsonl`) |
| Receipt digest + validation | `grid.startup-receipt.v1` via `StartupReceiptJson` + `StartupReceiptChecks` |
| Stage boundaries | `src/Grid.App/App.xaml.cs`, `MainWindow.xaml.cs` |
| Loading bar (22px thermometer) | `src/Grid.App/Controls/StartupLoadingBar.cs` |

Validation:

- Product receipts: `dotnet run --project tests/Grid.Core.Tests -- --startup-instrumentation`
- Startup regression baseline (Contract 1D): `dotnet run --project tests/Grid.Core.Tests -- --startup-regression`
- Live launch compare: set `GRID_STARTUP_COMPARE_BASELINE=1` (optional `GRID_STARTUP_AUTOMATION_CLOSE=1`)
- Bootstrap package: `node tools/start-grid-bootstrap/tests/package-surface.test.mjs`

Contract 1D persists the selected 1C baseline under `start/baselines/` with
`startup-regression-policy.v1.json` thresholds backed by observed warm-start receipts.

The Node receipt validator applies to `life-startup-receipt/v1` only; GRID receipts
remain on `grid.startup-receipt.v1` by design.
