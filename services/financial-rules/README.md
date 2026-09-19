# Financial Rules — MWP-03-D05 candidate

Six READY Features, CID-037–CID-041, SIMULATOR only; pending CTO review.
Financial Profile remains the authoritative owner of inputs and provenance.
Rules owns immutable calculations, captured input lineage and outcome decisions.
Audit owns separate append-only evidence.

## Execute the reference evidence

Run `dotnet test tests/financial-rules/Monergy.FinancialRules.Tests/Monergy.FinancialRules.Tests.csproj --configuration Release`.
The harness establishes explicit synthetic policy grants and calls serialized
owner contracts. The full lineage test uses actual Evidence, Document
Intelligence, Financial Profile, Rules, Job Management and Audit applications.
It does not query another owner's repository for input business data.

The host maps POST `contracts/cid-037/v1`, `cid-038/v1`, `cid-039/v1` only with
`Monergy:ReferenceAdapters=true` and existing `Monergy:ExecutionZone` LOCAL,
CI_EPHEMERAL or CI/EPHEMERAL. Other zones fail closed. Its default policy has
no grants; it is not a public identity/authentication implementation. Typed
in-process reference composition grants test contexts. No grant endpoint exists.
`Monergy:FinancialProfileBaseAddress` configures the governed CID-032/033 HTTP
client; its 10-second timeout is an engineering fixture limit, not a client SLO.

## Stable behavior

- Three typed engineering fixtures: sum v1, sum v2 (doubled sum), ratio v1.
- Decimal checked arithmetic; explicit identical three-letter uppercase units;
  ratio is dimensionless with four decimal places, midpoint ToEven. No FX conversion.
- Ordinal input roles `left` and `right`; at most 32 request references. No caller values.
- Exact rule/version, assembly implementation digest, definition hash and consumed
  fact revisions/provenance retained. No latest-version substitution.
- Original rule/input values reproduce historical output; access and applicable
  consent are checked now, not only at execution. Missing history fails closed.
- Operation/version/trusted customer/key define replay identity; canonical payload
  fingerprint detects conflicting reuse, never replaces caller identity.
- Application transition defines success/failure and CID-040/041; one repository
  lock publishes result/lineage/idempotency/event intent atomically.
- Post-commit dispatcher retains intent if Audit is unavailable or an acknowledgement
  is lost. Receiver event deduplication tolerates delivery retries.

Reference storage is process-local, non-durable and non-production. Separate
fact reads are individually revision/provenance consistent, not a distributed
point-in-time snapshot. No database, broker, client formula, score, recommendation,
frontend Feature or provider integration was added. OD-08/C-10 remain unresolved.
