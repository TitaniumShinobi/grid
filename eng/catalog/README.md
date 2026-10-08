# Vortex bundled catalog foundation v1

This patch establishes the deterministic upstream boundary for GRID's game catalog.
It does **not** claim that Vortex v2.6.3's bundled source contains the full ~607-game
community population.

Frozen upstream evidence:
- repository: Nexus-Mods/Vortex
- tag: v2.6.3
- commit: aa459b9499224bde07e5119d715b463bfae92c5e
- bundled game packages observed: 86
- package license declarations: GPL-3.0

`config/catalog/vortex-bundled.v1.json` is normalized metadata/provenance, not copied
Vortex implementation code. Catalog membership is explicitly separate from local
installation and account connection state.

GRID-native identities override upstream identities. The first explicit override is
Skyrim Special Edition. GTA V Legacy/Enhanced remain GRID-native and are not collapsed.

The importer fails closed if the frozen bundled package count or license expectation
changes. A later patch can add the broader community extension catalog once that
upstream is independently frozen.

## GTA V Enhanced deterministic registration

`Invoke-GridGtaVEnhancedRegistration.ps1` is the preproduction-only entry point for the
currently supported GTA V Enhanced source families. It acquires exact members through the
pinned FiveFury boundary, verifies the original containers, builds the canonical four-kind
Candidate twice, imports each replay into an isolated copy of the supplied historical
library, and fails unless the acquisition and registration artifacts are byte-identical.

The command also acquires and independently verifies the registered mounted-ped source
family. It resolves the effective resident `peds.ymt`, follows the exact DLC mount graph,
freezes each supported `peds.meta` member, and supplies one immutable Actor corpus index to
the batch adapters. This processing remains registration-time only; GRID runtime does not
traverse mounted Rockstar sources.

The same command separately acquires the bounded mounted MLO/interior graph. It follows
effective DLC spatial RPF declarations, freezes supported YTYP/YMAP/YMF members, verifies
one receipt-bound spatial corpus index, and admits only source-native MLO archetypes,
rooms, placed instances, and portal/instance relationships. Rendering entities,
coordinates, and geometric overlap remain outside Location eligibility.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\eng\catalog\Invoke-GridGtaVEnhancedRegistration.ps1 `
  -GameRoot 'D:\SteamLibrary\steamapps\common\Grand Theft Auto V Enhanced' `
  -FiveFuryWheel '<pinned fivefury 0.5.1 wheel>' `
  -UvExecutable '<pinned uv.exe>' `
  -HistoricalLibrary '<shared-canonical-library.v5.json>' `
  -RockstarCloudSnapshotBundle '<authorized immutable cloud-job snapshot bundle>' `
  -ObservedAtUtc '<exact UTC observation captured for this acquisition>' `
  -ActorObservedAtUtc '<exact UTC observation captured for the mounted Actor acquisition>' `
  -SpatialObservedAtUtc '<exact UTC observation captured for the mounted spatial acquisition>' `
  -OutputDirectory '<new empty output directory>'
```

The output contains two isolated replays, `registration-source-index.v1.json` and
`registration-coverage.v1.json` for each replay, and a root `registration-qcs.v1.json`
written only after every deterministic comparison passes. The command does not install
the resulting library, publish or approve its
Candidate package, alter runtime bindings, or copy Rockstar bytes into package/report
documents. `ObservedAtUtc` is explicit because source observation time is evidence and the
same value must be used by both replays. When extending an existing shared library with
unchanged source revisions, use the exact observation already retained by those revisions;
the store rejects a changed observation as an attempted immutable-history rewrite.
`ActorObservedAtUtc` is independent because the mounted Actor corpus is newly observed; the
same Actor timestamp is reused across its two replays but must never be backdated to the
historical local-source observation.
`SpatialObservedAtUtc` is independently fixed for the mounted spatial receipt and likewise
must be reused byte-for-byte across the two replays.

`RockstarCloudSnapshotBundle` is optional for historical local-only registration and required
for the bounded cloud MissionQuest wave. The bundle is acquired outside GRID's credential
boundary and must contain exactly the v1 snapshot envelope, receipt, and schema descriptor.
GRID verifies the checked-in schema and immutable bundle digests once, then gives the same
frozen bytes to both offline replays. Tokens, cookies, account/profile identities, authorization
headers, local paths, and raw provider responses are never copied into the Candidate package or
registration reports.

The schema-v2 source-family manifest additionally acquires Rockstar's complete shipped
`common.rpf!/data/ugc/*.ugc` registry family. The batch adapter registers every supported
generated-activity, playlist, and playlist-entry object with exact titles, Online
classification, and exact playlist organization. It does not guess a join to the 987
existing `fmnm` identities. Unsupported and unjoined objects remain explicit in coverage.

The additive registration-source registry v5 pins the historical local source-family v2,
mounted-Actor v1, and mounted-spatial v1 manifests. It preserves the optional cloud Mission
declaration unchanged, so one invocation can compose local Rockstar bytes, mounted-ped and
mounted-spatial corpora, and an authorized frozen reference snapshot without confusing
their evidence classes.

`HistoricalLibrary` is an append base and retains all historical v1/v2 packages. Semantic
adapter identity v2 permits equivalent builds with different DLL bytes; exact producing
build bytes remain in per-package build provenance while the append-only store retains its
first descriptor representation. Semantic/configuration changes still produce another
adapter revision. A dirty worktree produces a non-publishable Candidate with exact
development input closure and never claims reviewed build provenance.
