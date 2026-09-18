# Managed xEdit collector provisioning

`Trace-GridReference.pas` is Grid's versioned, generic, read-only xEdit collector. It is not installed implicitly by diagnosis. Grid first resolves MO2's exact `SSEEdit` definition, requires an x64 `SSEEdit64.exe`, validates the bundled collector manifest, and compares the installed collector and any prior Grid receipt.

The state is one of `Ready`, `Missing`, `Outdated`, `ModifiedExternally`, or `Unavailable`. A matching collector is ready even without a receipt. A received older Grid version is outdated. An unreceived or subsequently changed file is externally modified and cannot be replaced by an ordinary update proposal.

Missing and outdated states produce an inert `XEditCollectorProvisioning` proposal. The proposal identifies the exact source, destination, version, hashes, exclusions, verification steps, and rollback behavior. Execution requires an explicitly issued durable one-use authorization grant with a random secret. The grant is bound to the exact proposal identity, provisioning inputs, capability version, target, actor/session, workspace/request context, and expiry. No token is derived from the proposal ID or a public digest. Authorization covers only `Trace-GridReference.pas`; it does not authorize diagnosis remediation or any game/profile mutation.

Existing same-name files are copied to Grid-managed rollback storage before an approved update. Rollback data and the versioned receipt live beneath `%LOCALAPPDATA%\Grid\diagnostics\tool-provisioning\xedit`, or an explicit `GRID_DATA_ROOT` used by tests. Nothing is stored beside xEdit except the approved collector itself. Failed verification restores the prior file or removes a newly created invalid file.

The diagnostic launcher accepts only the `Ready` state. It never copies, updates, or repairs the collector as a side effect of collection.
