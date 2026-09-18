# Offline Papyrus state analysis

GRID can inspect one exact Skyrim `.pex` or `.psc` script without an agent, a
model, network access, or a third-party decompiler. The game-owned capability is
`grid.game.skyrimspecialedition.papyrus-state.inspect`.

The collector records source identity (absolute path, size, SHA-256, timestamps),
script metadata, state variables and defaults, assignments, properties,
routines, calls, reference `Enable`/`Disable` actions, and recognized persistence
reads/writes. PEX instructions retain their
state, routine, instruction index, source line when debug data exists, and
arguments. Simple compiler temporaries are resolved back to their source
properties when the bytecode uses assignment, cast, or property-get aliases.

Run it from a built source tree with an exact script and case directory:

```powershell
. .\scripts\games\skyrimspecialedition\health\collectors\Get-GridSkyrimPapyrusStateEvidence.ps1
Get-GridSkyrimPapyrusStateEvidence `
  -ScriptPath 'C:\exact\path\Scripts\_DA_Skyship_MCM_ConfigMenu_v1.pex' `
  -CaseDirectory 'C:\exact\case' `
  -ContextFingerprint ('0' * 64)
```

The repository-owned `Grid.Diagnostics` command can also be called directly:

```text
Grid.Diagnostics papyrus-inspect --input <exact.pex|exact.psc> [--expected-sha256 <hex>]
```

The internal case pipeline may additionally pass `--format pex|psc` when the
input is an already imported content-addressed blob whose filename has no
extension.

In the application, attaching an exact `.pex` or `.psc` to a Skyrim request adds
this capability to the displayed authorization binding. After the user grants
that exact read, GRID imports the content-addressed attachment, runs the bundled
collector against the imported bytes, and seals the derived evidence in the
case. This path does not require an assistant or network connection.

This is static evidence. It can establish that a routine reads a selector and
enables or disables named references, including whether the same pattern occurs
in a load/init routine. It cannot establish the current value stored in a save,
prove which branch ran, identify an attached runtime script instance, or prove a
repair. Those remain separate evidence requirements. The collector never edits
the script, plugin, save, MO2 profile, or game files.
