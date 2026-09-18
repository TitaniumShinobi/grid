# Disposable Wabbajack/MO2 test lab

## Purpose and authority boundary

The test lab gives Grid a small, legitimate, disposable MO2 environment for local integration testing. It is not a production default and no local lab path may enter application code. The selected list is **Al and Styyx's Setup of Skyrim (ASSOS) 1.3.0**, installed with official Wabbajack **4.2.1.4**. The Wabbajack release and the exact list artifact must be obtained from their official distribution locations and verified before use; a version inferred from documentation, an existing local Wabbajack installation, or a later runtime downloaded by the launcher is not an approved substitute.

The lab is isolated under `C:\Grid-Test-Lab`. The following are always outside its authority:

- every path on `D:`;
- `%LOCALAPPDATA%\Grid`;
- the pre-existing `C:\Wabbajack` directory;
- Steam Skyrim and `Documents\My Games` unless a later, separately approved task explicitly authorizes a prerequisite change.

The lab must never be used to redistribute downloaded mods. Nexus and other credentials stay in the supplying application's private storage and must not be copied into manifests, snapshots, logs, or the repository.

## Owned layout

Use a dated lab identifier such as `assos-1.3.0-20260826` and keep roles separate:

```text
C:\Grid-Test-Lab\tools\wabbajack\4.2.1.4
C:\Grid-Test-Lab\downloads\assos\1.3.0
C:\Grid-Test-Lab\installations\assos\1.3.0
C:\Grid-Test-Lab\grid-data\assos-1.3.0-<date>
C:\Grid-Test-Lab\evidence\assos-1.3.0-<date>
```

The approved executable and list-artifact locations are:

```text
C:\Grid-Test-Lab\tools\wabbajack\4.2.1.4\Wabbajack.exe
C:\Grid-Test-Lab\downloads\assos\1.3.0\ASSOS.wabbajack
```

Wabbajack payload downloads remain under `C:\Grid-Test-Lab\downloads\assos\1.3.0`; the portable MO2 result remains under `C:\Grid-Test-Lab\installations\assos\1.3.0`. Do not reuse `C:\Wabbajack`, another Wabbajack cache, another list installation, or any path on `D:`.

Reserve at least 50 GiB. Official list metadata and the downloaded `.wabbajack` artifact must be captured and hashed before payload download. Inspect the embedded download manifest, record every origin host in `archiveSourceDomains`, and stop if the list identity, executable sources, or required space materially differs from approval.

## Read-only prerequisite gate

Prerequisites are derived from the downloaded, hash-recorded `ASSOS.wabbajack` artifact and the official documentation referenced by that exact artifact. Do not pre-assume Skyrim runtime `1.6.1170`, a Creation Club inventory, language, Visual C++ runtime, .NET runtime, or another requirement from an earlier or later ASSOS release.

After downloading the small list artifact, but before Wabbajack downloads any archive payload, inspect its embedded metadata and download manifest. Record the artifact identity, version, hash, declared game/runtime and content requirements, required installer/runtime components, archive-source domains, and storage estimate. Then perform a read-only comparison against the installed game and machine prerequisites. The prerequisite report must distinguish:

- requirements stated by the exact artifact;
- requirements stated by its matching official documentation;
- locally observed evidence;
- anything unavailable or ambiguous.

Stop if satisfying any derived requirement would require deleting, reinstalling, updating, downgrading, or changing the language of Skyrim; replacing or downloading Creation Club/Bethesda content; installing or changing a runtime; modifying Steam configuration; or modifying My Games. Record the blocker in the manifest and request separate approval. The lab task may observe prerequisites, but it may not remediate them.

### Preflight-blocked manifest

A blocked prerequisite check is a valid lab outcome, not a partial installation. Set `state` to `blocked`, set `runtime.prerequisiteStatus` to `blocked`, and populate `blocker` with the prerequisite phase, a stable code, expected and observed evidence, the stop time, and `requiresAdditionalApproval: true`. Do not invent artifact hashes, MO2 version/profile evidence, inventory counts, native-launch status, or installation disk usage. Those fields become mandatory only after their corresponding download or installation stage.

When preflight blocks before the `.wabbajack` artifact is downloaded:

- retain the verified Wabbajack 4.2.1.4 launcher version, Authenticode result, length, and SHA-256 in local evidence;
- retain the official list metadata URL and declared artifact size;
- leave the artifact SHA-256 absent because no local artifact was observed;
- keep `archiveSourceDomains` empty because the embedded download manifest was not inspected;
- do not create or claim a portable MO2 installation;
- do not launch Wabbajack installation work, MO2, Skyrim, or a configured tool.

Resume only after a new approval explicitly covers the prerequisite remedy. A later run updates the manifest with new observations; it must not rewrite the earlier blocked evidence as though the stop never occurred.

## Stage A bridge gate

The disposable lab is a prerequisite for, not part of, the MO2 bridge. No Grid bridge plugin, IPC endpoint, profile mutation, mod/plugin mutation, VFS query, refresh command, or ProcessRunner test may begin until all Stage A entry evidence is present:

- the exact official ASSOS artifact and Wabbajack 4.2.1.4 identities and hashes are recorded;
- artifact-derived prerequisites pass without remediation;
- Wabbajack completes into the approved installation directory;
- the resulting portable MO2 identity, application version, uibase boundary, profiles, and selected-profile evidence are recorded;
- the disposable MO2 frontend starts successfully, shows the expected instance/profile, and closes normally without launching Skyrim or a configured tool;
- pre-launch and post-launch semantic observations are compared and the reviewed post-validation observation is designated as the baseline;
- a repeated read-only snapshot is deterministic for unchanged semantic state;
- the manifest defines exact cleanup and baseline-restoration boundaries.

If any item is absent, ambiguous, or inconsistent, Stage A remains blocked. Bridge work must report the capability unavailable; it must not use filesystem parsing as authoritative control, substitute a synthetic fixture, or use UNDEFEATED.

## Manifest and observations

Validate the local manifest against [`../test-lab/manifest.v1.schema.json`](../test-lab/manifest.v1.schema.json). Store the populated manifest in the dated evidence directory; a copy under ignored `artifacts/test-lab` is optional. Do not commit the populated file because it contains local absolute paths.

Create the pre-launch snapshot from PowerShell 7 or newer:

```powershell
./eng/test-lab/New-GridTestLabSemanticSnapshot.ps1 `
  -InstallationRoot 'C:\Grid-Test-Lab\installations\assos\1.3.0' `
  -OutputPath 'C:\Grid-Test-Lab\evidence\assos-1.3.0-<date>\pre-launch.snapshot.json'
```

The observer:

- reads only the supplied installation root;
- rejects a reparse-point root, records nested reparse points, and never follows them;
- records relative structural membership, sizes, attributes, and timestamps;
- hashes MO2 configuration, profile state, and top-level mod `meta.ini` evidence within bounded limits;
- records a profile `saves` directory but never enumerates its contents;
- does not hash archive payloads or ordinary large assets;
- writes atomically only to the separately supplied output path.

After the first successful installation, launch only that lab's `ModOrganizer.exe`. Confirm the intended instance and profile visually, do not start Skyrim or any configured tool, and close MO2 normally. Capture a second snapshot, then compare:

```powershell
./eng/test-lab/Compare-GridTestLabSemanticSnapshot.ps1 `
  -BeforePath 'C:\Grid-Test-Lab\evidence\assos-1.3.0-<date>\pre-launch.snapshot.json' `
  -AfterPath 'C:\Grid-Test-Lab\evidence\assos-1.3.0-<date>\post-launch.snapshot.json' `
  -OutputPath 'C:\Grid-Test-Lab\evidence\assos-1.3.0-<date>\native-launch.comparison.json'
```

MO2 may create legitimate lab-owned logs or caches during native startup. The comparison must disclose them rather than treating the post-launch tree as identical. After reviewing expected differences, designate the post-validation snapshot as the integration baseline. `no-change-observed` is evidence about the bounded snapshots, not proof that an external environment could never have changed.

Use an isolated Grid data root for later tests:

```powershell
$env:GRID_DATA_ROOT = 'C:\Grid-Test-Lab\grid-data\assos-1.3.0-<date>'
```

Never persist that value in production settings or source.

## Cleanup boundary

Cleanup is manual. Before removing anything, resolve each literal target, require it to be an immediate or declared descendant of `C:\Grid-Test-Lab`, and reject a target or parent carrying the `ReparsePoint` attribute. Review the manifest's `cleanup.ownedRoots`; remove only those exact roots. Do not use wildcards and do not derive a delete target from list metadata.

The removable set is limited to the declared Wabbajack-version directory, ASSOS download directory, ASSOS installation directory, dated Grid data/evidence directories, and matching ignored repository evidence. Cleanup never includes another drive, Steam, My Games, `%LOCALAPPDATA%\Grid`, or `C:\Wabbajack`.

No cleanup command is supplied or run automatically. Deletion requires a separate deliberate user action after the literal targets have been reviewed.
