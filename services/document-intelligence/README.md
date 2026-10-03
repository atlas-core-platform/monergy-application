# Document Intelligence reference implementation

MWP-03-D12 adds a bounded `SIMULATOR` candidate for
`M2-WS03-E02-F03 — Reprocessing & explicit failure handling`.

The service owns processing and reprocessing state, execution-attempt identity,
immutable predecessor linkage, extracted candidate facts, and terminal event
intent. Evidence remains the authority for customer/evidence/version identity;
the emitted facts remain non-authoritative inputs to later normalization.

## Semantics

- CID-026 `ReprocessDocument` creates a new operation linked to a terminal
  predecessor for `FAILED_PROCESSING` or `UPDATED_EVIDENCE` intent.
- Transport redelivery replays the existing operation after current policy is
  checked. It does not extract or append terminal intent again.
- Status disclosure and extraction both re-evaluate the current owner-local
  reference policy; protected extraction is rechecked after the Evidence read.
- CID-025 execution retry remains the existing retry mechanism and is distinct
  from a new CID-026 business operation.
- CID-028 records successful validated-source-fact intent; CID-029 records a
  safe, classified terminal failure intent. State and event intent commit under
  the same reference-repository lock.
- Idempotency is scoped by contract/version, customer, and caller key and is
  bound to the complete canonical semantic payload using an unambiguous typed
  identity. Predecessor eligibility and snapshot capture are revalidated at
  atomic child admission.
- Committed fact collections and success-event payloads are owner-frozen
  snapshots; repository reads do not expose mutable collection aliases.

## Targeted verification

Run from the repository root:

```powershell
dotnet test tests/document-reprocessing/Monergy.DocumentReprocessing.Tests/Monergy.DocumentReprocessing.Tests.csproj --no-restore
dotnet test tests/vs02/Monergy.Vs02.Tests/Monergy.Vs02.Tests.csproj --no-restore
./build/verify-document-reprocessing.ps1 -SelfTest
./build/verify-document-reprocessing.ps1
git diff --check
```

The scenario inventory is maintained in
`build/governance/d12-scenario-matrix.json`.

## Limits

This implementation is in-memory reference evidence restricted to `LOCAL` and
`CI_EPHEMERAL`. It does not establish restart durability, exactly-once delivery,
a production broker, OCR/AI/provider compatibility, automatic financial
correction, a frontend workflow, deployment readiness, or acceptance. D11 is
not a source dependency; its deferred acceptance and recorded OCI security
failure remain unchanged.
