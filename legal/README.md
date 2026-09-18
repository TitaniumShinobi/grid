# Grid licensing and provenance

Status: pre-release provenance review

Grid's compatibility with Mod Organizer 2 and Vortex is an interoperability goal. Compatibility, observation, import, launch coordination, and independently implemented parsing do not by themselves establish that Grid contains upstream implementation code. The repository nevertheless requires a file-level provenance review before a commercial license is finalized.

No repository-wide `LICENSE` is asserted by this baseline. That omission is deliberate and must not be interpreted as a third-party license grant.

The canonical machine-readable records are:

- `provenance.v1.json` — source/component classification and review status.
- `asset-provenance.v1.json` — exact asset identities and distribution disposition.
- `third-party-components.v1.json` — declared package/component inventory.
- `provenance-ledger.v1.schema.json` — closed schema for the source ledger.

The records distinguish original work and interoperability from MIT-derived, GPL-derived, copied/licensed, and unresolved material. `reviewState` is evidentiary status, not a legal conclusion. A source-verification pass proves inventory coverage only. It does not certify non-infringement or authorize commercial distribution.

## Current baseline limitation

The snapshot received on 2026-09-16 contained no Git commits or branches. All files were untracked. Consequently, commit ancestry cannot establish authorship or origin. This snapshot is the first formal Grid provenance baseline; earlier origin must be supported by development records, file content, upstream comparison, and professional review.

## Release rule

The source gate allows explicitly classified `ReviewRequired` material so work can continue visibly. The distribution gate fails closed when:

- an included source or asset has no ledger coverage;
- an asset is excluded or unresolved;
- a dependency lacks a reviewed license disposition;
- a GPL-derived component is proposed for proprietary inclusion; or
- Grid has no attorney-approved repository license.

Run the source gate through `eng/Test-Grid.ps1`. Invoke the stricter distribution gate directly only when preparing a release:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\provenance\Test-GridProvenance.ps1 -Mode Distribution
```

## Attorney package

`eng/provenance/Export-GridAttorneyAudit.ps1` creates a sanitized ZIP without a staging directory. It refuses common secret-bearing files and records whether Git history was available. The export is evidence for review, not a substitute for legal advice.
