# T1-19 — Recovered Review Reconciliation / Already Trusted State

Status: bounded implementation candidate under AR-003 / COV-016, extending T1-18 resumable Financial review.

## Purpose

Prevent a recovered completed processing result from presenting candidate facts as selectable again when those exact source facts are already represented in the current trusted Financial Profile.

T1-19 is a product-state reconciliation increment. It does not introduce a new owner, capability, mutation contract or duplicate-suppression algorithm.

## Exact reconciliation identity

A recovered candidate is considered **already trusted** only when the current Financial Profile contains an accepted Income/Expense fact whose source matches both:

- `financialFact.source.technicalSourceFactId == recoveredFact.sourceFactId`
- `financialFact.source.documentVersion == recoveredResult.documentVersionId`

Both identifiers are required.

A source-fact ID match from a different document version must **not** suppress the recovered candidate.

## Financial Profile availability

Reconciliation is allowed only after the current authorized Financial Profile has loaded successfully.

If Financial Profile is:

- still loading → selection remains unavailable;
- unavailable/error → recovered result remains read-only and reconciliation is unknown;
- successfully loaded with no matching trusted source → candidate remains eligible for explicit review/selection.

Unknown must never be interpreted as “not already trusted.”

## Customer presentation

For an exact already-trusted candidate:

- no review checkbox is shown;
- the card is marked **ALREADY TRUSTED**;
- the customer may open the exact trusted Financial fact through:
  `/finances?fact=<financialFactId>`.

For a candidate not already trusted:

- checkbox is available only when Financial Profile read/update authority is present and current Financial Profile state is known;
- the candidate remains unselected by default;
- promotion still requires explicit **Add selected information**.

If every supported candidate in the recovered result is already trusted:

- no selection controls are shown;
- no promotion action is shown;
- the UI states that all supported items from the run are already represented.

## Post-confirmation behavior

When a promotion succeeds, the returned authoritative Financial Profile replaces the local profile projection.

T1-19 then reconciles again from that returned owner state:

- newly confirmed source facts become **ALREADY TRUSTED**;
- their checkboxes disappear;
- their exact trusted-fact links become available;
- the promotion success confirmation remains visible.

## Server semantics

T1-19 does not change the existing promotion endpoint or Financial Profile normalization semantics.

This is intentional:

- idempotency/replay remains owned by the existing normalization contract;
- conflict handling remains server-authoritative;
- the client must not silently filter or rewrite a submitted selection on behalf of the user;
- the product simply prevents known already-trusted items from being selectable when current owner state proves that identity.

## Fact-type boundary

Only current accepted Financial Profile types participate:

- `INCOME`
- `EXPENSE`

Future financial domains must remain outside this reconciliation until their own domain increment is accepted.

## Explicit exclusions

T1-19 does not implement:

- automatic removal or mutation of trusted Financial facts;
- duplicate merging across different source facts;
- fuzzy matching by label/value/file name;
- inferred equivalence across document versions;
- automatic promotion;
- history reconciliation across superseded Financial fact revisions;
- retry/reprocess/recovery controls;
- new financial domains.

## Acceptance evidence

Focused evidence must prove:

- exact source-fact + document-version match renders **ALREADY TRUSTED** and no checkbox;
- trusted fact links to the exact Financial fact route;
- mixed recovered results submit only still-untrusted selected source-fact IDs;
- after successful promotion, returned owner state converts the selected candidate to trusted;
- all-trusted recovered review exposes no promotion action;
- Financial Profile unavailable keeps selection disabled and does not infer trust state;
- same source-fact ID under another document version remains selectable;
- LOCAL/UAT evidence confirms promoted Financial facts retain the exact technical source-fact IDs and document-version provenance.

No additional manually-triggered hosted CI cycle is required solely for this bounded candidate. Execution evidence and product acceptance remain separate CTO decisions.
