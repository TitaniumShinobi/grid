# GTA V guided setup

Status: Readiness plus authorized recipe-driven deployment implemented; Production UI composition is not yet established.

GRID now owns a read-only Grand Theft Auto V adapter under
`scripts/games/grandtheftautov/`. It accepts one or more explicit installation
roots, distinguishes Legacy and Enhanced, gives every exact root/edition pair a
stable non-path installation ID, and inventories every installation separately.
When more than one valid installation exists, setup stops at `NeedsSelection`
until the caller supplies one exact `InstallationId`.

The initial supported targets are `Foundation` and `ForeverTogether`.
Readiness covers the Legacy executable, BattlEye operator confirmation, the
OpenIV mods folder, ASI Loader, OpenIV.ASI, Script Hook V, Menyoo files and
in-game confirmation, ScriptHookVDotNet 3, NativeUI, iFruitAddon2, the required
script timeout, and Forever Together.

The adapter never downloads or launches anything. Readiness remains read-only. A
separate recipe-driven deployment capability can hash user-supplied artifact
directories, produce an inert exact-file plan, and—only after a matching v2
random-secret one-use grant—copy reviewed files, apply reviewed INI values,
verify postconditions, write a receipt, and roll back a failed transaction.

The first recipe is `ForeverTogether`. It places ScriptHookVDotNet runtime files
beside `GTA5.exe`, places NativeUI, iFruitAddon2, and Forever Together beneath
`scripts`, and enforces `ScriptTimeoutThreshold=60000`. Adding another supported
setup means adding a versioned recipe rather than another case-specific copier.
BattlEye state and successful in-game Menyoo behavior cannot be established
from file presence and therefore remain explicit operator confirmations.

Run the current setup assessment on Windows:

```powershell
& ".\scripts\games\grandtheftautov\Invoke-GridGtaSetupReadiness.ps1" `
  -GameRoot @( `
    "D:\SteamLibrary\steamapps\common\Grand Theft Auto V", `
    "D:\SteamLibrary\steamapps\common\Grand Theft Auto V Enhanced" `
  ) `
  -Target ForeverTogether `
  -BattleEyeDisabled `
  -MenyooInGameVerified
```

With two installations, the first invocation prints both installation IDs and
makes no selection. Rerun the same command with:

```powershell
  -InstallationId "gta-legacy-<observed-id>"
```

Add `-AsJson` for structured output suitable for future Production UI or
provider-neutral connector composition.

## Authority and safety

- The selected root is structured input; it is never inferred from prose.
- Legacy and Enhanced never share inventory, readiness, receipts, or selection.
- Collection is bounded to known root files and top-level files in `scripts`.
- Saves and Rockstar profile contents are excluded.
- Deployment accepts only an exact selected Legacy root and explicit artifact
  roots; ambiguous same-name artifacts with different hashes fail closed.
- Existence of the executor does not grant Production composition authority.

## Verification

`GtaSetupReadiness.Tests.ps1` creates a synthetic installation, confirms the
incomplete-to-ready transition, and verifies that readiness reports no external
state change. It is registered in `eng/verification.manifest.v1.json` for the
WindowsSource lane.
