# START -> GRID Bootstrap 1.0.0

This package is a portable copy of the START Contract 1A startup event, receipt,
readiness, timing, digest, findings, and explicit baseline-comparison surface.
It is not a daemon, service, supervisor, or runtime dependency on START.

## Placement

Copy the package contents into a GRID-owned integration directory, for example
`tools/start-grid-bootstrap/`. Keep the `contracts/`, `schemas/`, `runtime/`,
`validation/`, and `integration/` directories together.

## Copy versus adapt

- Copy `contracts/` and `schemas/` unchanged.
- Copy `runtime/canonical-json.mjs` and `runtime/startup-events.mjs` unchanged.
- Copy `runtime/startup-observer.mjs` unchanged unless GRID needs a language-specific adapter.
- Adapt only `integration/grid-startup-adapter.example.mjs` at real GRID startup boundaries.
- GRID owns stage meaning, timestamps, state identities, cache/rebuild evidence, and readiness assertions.

## APIs

`StartupObserver.begin(stage_id, stage_class, required, started_at)` returns
`complete`, `skip`, and `fail` operations. Pass truthful evidence, including
`progress_units`, `dependencies`, `cache_hit`, `rebuild_reason`, and `readiness`.

`StartupObserver.finish(readiness, verification, options)` validates and writes
one durable receipt. Use `readStartupReceipt` and
`compareStartupReceipt(receipt, baseline, policy)` for verification and explicit
historical comparison. Baselines are caller-supplied and never mutated.

## Configuration

The integration must supply a caller-owned receipt directory. Do not write into
GRID source files. Receipt filenames are deterministic from `startup_id`.

## Validation

```text
node validation/validate-startup-receipt.mjs <receipt.json>
```

The runtime accepts the established GRID vocabulary:

`AUTH_SESSION_RESTORE`, `ACCOUNT_LOOKUP`, `GRID_ACCOUNT_STATE`,
`LOCAL_DEVICE_STATE`, `REGISTERED_STATE_LOAD`, `STALE_STATE_ASSESSMENT`,
`PREPARED_CANONICAL_STATE_LOAD`, `SHELL_INITIALIZATION`, `READY`.

Use `INDETERMINATE` progress whenever GRID does not know truthful totals.
Do not fabricate stage completion, percentages, readiness, cache hits, or state
identities.

## Safe repair/reapply

Preserve the existing GRID integration directory and receipts before reapplying.
Reapply only the versioned package after validating its manifest and hashes.
Remove only the package-owned integration directory; never remove GRID source,
state, or historical receipts automatically.
