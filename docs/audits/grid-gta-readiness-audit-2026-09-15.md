# GRID GTA readiness audit — 2026-09-15

## Scope and source

Audited the uploaded `grid-audit-2026-09-15.zip` snapshot with SHA-256:

`1c21e9a90e66b9251ac5b5f92c805d33e851f457a101f2a69ad082e810d9b7aa`

The snapshot contains 734 files after extraction and does not contain `.git`
metadata. Worktree cleanliness, history, branch identity, and the relationship
of generated files to committed source therefore cannot be established.

## Baseline findings

1. GRID is a substantial Windows WinUI/.NET 9 product with a deterministic
   PowerShell health engine, closed capability contracts, one-use authorization
   infrastructure, and broad Skyrim/MO2 implementation.
2. Before this change, `scripts/games/grandtheftautov/` contained only
   `user-data-roots.v1.json`. It had no adapter manifest, capability manifest,
   installation collector, setup assessment, or tests.
3. The Production C# catalog contains only Skyrim. GTA appears in development
   mock/catalog code, where it is explicitly a preview without a fabricated
   installation binding.
4. The Production assistant request bridge does not provide a GTA installation
   root to adapter parameters. Consequently, adding a repository adapter alone
   does not make the current WinUI assistant capable of running GTA setup.
5. The locked architecture correctly prevents an AI/model from inventing game,
   mod, tool, installation, evidence, or execution authority from prose.
6. The uploaded snapshot includes a root `Grid.exe`, `.codex-tmp`, and 93 files
   beneath `tmp/`. They were treated as pre-existing user artifacts and were not
   modified or deleted.

## Implemented vertical slice

Added a game-owned, read-only GTA V adapter with six registered capabilities:

- connected-root probe;
- bounded installation inventory;
- deterministic setup-readiness reduction;
- reusable setup baseline;
- shared-health adapter invocation.

The follow-up multi-installation revision adds an installation-set capability.
Legacy and Enhanced roots receive distinct stable IDs and retain completely
separate inventories. Two valid installations produce `NeedsSelection`; GRID
does not silently prefer Legacy, Enhanced, or whichever path was enumerated
first.

The first supported workflows are `Foundation` and `ForeverTogether`. The
assessment detects known files and directories but requires explicit operator
confirmation for BattlEye state and the successful in-game Menyoo test. It
never equates file presence with a successful runtime test.

The direct operator entry point is:

`scripts/games/grandtheftautov/Invoke-GridGtaSetupReadiness.ps1`

No external-write, download, archive-edit, process-launch, install, or rollback
executor was added. A state-changing installer remains a later capability and
must use GRID's proposal, durable authorization, backup, verification, and
rollback lifecycle.

## Verification performed here

- Both uploaded ZIPs were SHA-256 identical.
- Both edited JSON manifests parsed successfully with `jq`.
- Every GTA supporting and implementation file declared by the capability
  manifest exists in its canonical game directory.
- Repository searches confirmed the new adapter bindings, test registration,
  documentation, and entry points.
- The source snapshot was inspected without modifying the pre-existing
  `Grid.exe`, `tmp/`, or user runtime data.

## Verification not performed here

This execution environment has neither PowerShell nor the .NET SDK and is not
Windows. Therefore the authoritative `eng/Test-Grid.ps1`, PowerShell parser,
.NET build, WinUI tests, and live GTA/OpenIV observation were not run here.
The new fixture suite is registered in the WindowsSource lane and must pass on
the Windows development machine before promotion.

## Remaining work

1. Run the full WindowsSource verification lane.
2. Add a Production GTA installation connection flow that preserves explicit
   structured selection and stores no machine path in source.
3. Bind the connected installation root into the authorized request engine.
4. Project immutable readiness checks and `nextAction` into the game workspace
   and assistant presentation surface.
5. Only then design a proposal-first transactional installer with exact file
   ownership, backup, rollback, and runtime confirmation.
6. Implement the provider-neutral Chatty CLI/AUTO connector independently of
   GTA diagnostic or execution authority.
