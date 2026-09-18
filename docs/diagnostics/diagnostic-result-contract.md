# Deterministic diagnostic result contract

The default public result is exactly four fields in this order:

```text
Affected mod(s):
Mod role(s):
Finding:
Solution:
```

## Resolution rules

### Affected mod(s)

Resolve only identities supported by current deterministic evidence. Otherwise `UNRESOLVED`.

### Mod role(s)

Resolve only evidence-backed roles for the affected identities. Otherwise `UNRESOLVED`.

### Finding

Resolve only when proof obligations are satisfied by current evidence. Confidence/scoring alone cannot publish a finding. Otherwise `UNRESOLVED`.

### Solution

Resolve only a deterministic evidence-backed remedy that is supported by the current capability/lifecycle state. When no remedy is proved, state the smallest bounded next investigation step. A rendered Solution is not execution authority.

## Semantic determinism

Semantic identity binds normalized context, semantic evidence, resolver version, and policy version while excluding case IDs, timestamps, local paths, GUIDs, and collection order.

## Structured selections and prose

Game, Class, Mods, and Tools are structured request fields. Plain text is preserved as a claim/request and cannot create or alter those selections or technical targets.

## AI boundary

No AI/model/assistant may create evidence, resolve a field, select remediation, authorize/execute a change, or mark verification complete. A presentation surface may render the immutable result.
