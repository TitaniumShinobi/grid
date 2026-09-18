# Evidence-bound ESL flagging

Grid treats plugin-slot optimization as a repair operation, not a cosmetic
conversion. The Skyrim adapter first asks the bounded xEdit collector to audit
one exact plugin. That audit classifies the plugin as already light,
header-flag-only, compaction-required, or ineligible. It also records the
ESM-plus-new-CELL engine-risk warning.

Only `HeaderFlagOnly` without an engine-risk warning can enter Grid's automatic
ESL mutation path. The eligibility result must belong to a sealed case. The
proposal then binds the current MO2 winning provider, plugin path, plugin size,
TES4 flags, exact before/after SHA-256 values, and the current `modlist.txt`,
`plugins.txt`, and `loadorder.txt` hashes.

Execution requires a fresh durable one-use authorization for exactly
`plugin:<name>:TES4.ESL=true`. The connected MO2, Skyrim, and xEdit processes
must be closed. Before mutation Grid creates and verifies a byte-exact backup.
It changes only the four-byte TES4 flags field by adding bit `0x200`, flushes
the write, verifies the exact expected after image, reparses the header, and
restores the backup if any postcondition fails.

The inert specification can be sealed as an `EslFlagPlanning` case. Grid's
normal corrective-action bridge then exposes it as a reviewable repair task:
Prepare, Authorize, and Execute bind the same exact specification. A successful
apply preserves verified before and after images plus a durable history state.
Undo and Redo each require a new one-use authorization, recheck the evidence
seal, protected profile hashes, process gate, and current plugin digest, and
verify the exact destination image.

This capability never compacts FormIDs, renames a plugin, changes its records,
or changes MO2 activation/load-order files. Plugins requiring compaction remain
manual-review work until a separate capability can bind all external FormID,
Papyrus, FaceGen, voice-file, dependent-plugin, save, and generated-output
consequences.
