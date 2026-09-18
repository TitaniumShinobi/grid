# Authorization and receipt binding

Status: Normative security baseline  
Scope: Deterministic read and mutation authority, durable grant consumption, receipt reuse, and historical compatibility

## Security objective

GRID treats authorization as a one-use capability bound to an exact deterministic context. A public review, proposal ID, specification ID, SHA-256 digest, or other predictable identifier is **not** an approval credential.

Current authorization uses a randomly generated secret returned only at explicit grant issuance. The durable store persists only a salted protected proof of that secret together with the grant's semantic identity and lifecycle state. The secret itself must not be written to case artifacts, receipts, transcripts, logs, or configuration.

The protected proof is a local secret-verification mechanism. Ordinary unkeyed SHA-256 digests elsewhere in GRID provide deterministic identity/integrity comparisons; they are not described as hostile-party authenticity proofs.

## Closed contracts

Current writes use:

- `authorization-review.v2.schema.json`;
- `authorization-grant.v2.schema.json`;
- `authorization-lease.v1.schema.json`;
- `authorization-consumption.v1.schema.json`;
- `authorization-failure.v1.schema.json`;
- `authorization-semantic-binding.v1.schema.json`;
- request-specific `request-read-authorization.v2.schema.json`;
- `tool-evidence-receipt.v2.schema.json` and `tool-evidence-run.v2.schema.json`;
- `repair-transaction-receipt.v2.schema.json` for current mod-chain transactions.

The v1 read-authorization and receipt contracts remain historical read formats. They cannot confer current execution authority and cannot be resumed as if they were v2 grants/receipts.

## Semantic grant binding

A current grant binds exactly:

- actor and application session identity;
- workspace, request, and submission identity;
- request envelope digest;
- plan digest;
- authorized scope digest;
- proposal or specification identity and digest;
- capability identity and version, plus adapter identity/version when applicable;
- exact targets;
- normalized input digest;
- authority class (`Read` or `Mutation`);
- issuance and expiry times.

Changing any bound field requires a new review/grant. Callers cannot repair a mismatch by recomputing a public digest.

## Durable one-use lifecycle

`Grid.AuthorizationStore.ps1` owns the grant lifecycle:

```text
Issued -> Executing -> Consumed
                    -> Failed
```

Entering `Executing` requires the one-time secret, an exact semantic binding match, a non-expired grant, and an exclusive durable transition lock. A grant already `Executing`, `Consumed`, or `Failed` refuses another consumer. Completion is terminal. A failed authorization is not reset to `Issued`.

The auxiliary lease, consumption, and failure records are audit records. The durable grant state is authoritative for whether authority remains available.

## Receipt binding and substitution resistance

Current tool receipts bind workspace, request, submission, envelope, plan, authorization grant, authorization semantic digest, capability/version, adapter/version, normalized input, bounded output, and evidence digests. Resume validates all of those fields and recomputes the receipt, output, and evidence digests before accepting a checkpoint.

A receipt copied to another workspace, paired with another grant, replayed under another capability/adapter version, or paired with altered normalized inputs is refused even when the copied JSON remains structurally valid.

Current mod-chain repair receipts bind the same execution context plus the exact repair specification, baseline manifest, transaction journal, output, and evidence digests.

## Sealed-case reuse

A sealed request case is reusable only when its seal is valid **and** the current semantic baseline fingerprint matches the sealed semantic baseline. The current envelope, plan, grant ID, and authorization semantic digest are then compared with the sealed request artifacts before `ResumedSealed` can be returned.

Historical sealed cases remain readable. A case sealed under a v1 public-digest authorization record does not become executable current authority merely because its integrity seal is valid.

## Authority boundaries

- Review is descriptive and public; it is not approval.
- Grant issuance is explicit and returns a random secret once.
- Execution requires the grant ID plus the corresponding secret and exact current binding.
- Bare mutation executors remain subordinate to their authorized wrappers.
- AI/model output cannot issue a grant, choose targets, alter bindings, consume a lease, or validate a receipt.
