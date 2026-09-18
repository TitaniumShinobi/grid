# Grid provenance engineering

These deterministic repository tools support provenance evidence and release refusal. They do not determine legal conclusions.

- `Test-GridProvenance.ps1` validates classification coverage. `Source` permits visible review debt; `Distribution` fails closed until all material and dependencies are reviewed and a repository license exists.
- `New-GridProvenanceInventory.ps1` writes a SHA-256 inventory beneath `artifacts/provenance` by default.
- `Compare-GridUpstreamSource.ps1` compares independently supplied upstream trees using a conservative whole-file normalized hash. Matches require review; non-matches do not prove independence.
- `Export-GridAttorneyAudit.ps1` writes a sanitized ZIP directly, without a staging tree. It excludes common generated/user/secret paths and refuses high-confidence embedded secret patterns.

All tools support PowerShell 5.1. They are operator-invoked engineering tools and are not part of Grid's game diagnostic or mutation authority.
