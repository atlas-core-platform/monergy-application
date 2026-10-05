# Customer & Identity reference implementation

MWP-03-D13 adds a bounded `SIMULATOR / LOCAL / CI_EPHEMERAL` candidate for
`M2-WS02-E01-F01 — Authentication-provider boundary`. The service is the sole
authority changed by D13 and realizes only CID-001 through CID-004.

## Semantics

- CID-001 returns a minimal canonical Customer projection only when the
  requested Customer matches the trusted customer context. Cross-customer
  queries fail before repository lookup.
- CID-002 resolves an opaque provider-neutral authentication reference through
  an internal replaceable port and returns only the canonical `ActorContext`.
  Successful authentication grants neither authorization nor consent, and
  human actor identity remains distinct from workload identity.
- CID-003 owns create and update effects. A typed identity made from contract,
  version, Customer, and caller idempotency key avoids delimiter ambiguity; a
  SHA-256 semantic payload fingerprint detects key reuse. Updates require an
  exact expected revision so concurrent stale mutations cannot silently win.
- CID-004 appends canonical KYC verification history under Customer & Identity
  authority. `StatusCode` is bounded provider-neutral reference vocabulary for
  this simulator, not a normative architecture enumeration. Re-delivery is
  idempotent and later results never erase prior entries.
- Ordinary telemetry contains only operation, outcome, Customer identifier,
  request identifier, and correlation identifier. It excludes authentication
  references, Customer display values, verification identifiers, and KYC result
  values.

All reference adapters are guarded by the repository's established execution-
zone guard and can run only in `LOCAL` or `CI_EPHEMERAL`. Contract endpoints are
available only when `Monergy:ReferenceAdapters=true` and use the existing strict
`ContractJson.Options` behavior.

## Targeted verification

Run from the repository root:

```powershell
dotnet test tests/customer-identity/Monergy.CustomerIdentity.Tests/Monergy.CustomerIdentity.Tests.csproj --configuration Release
dotnet test tests/vs02/Monergy.Vs02.Tests/Monergy.Vs02.Tests.csproj --configuration Release
dotnet test tests/architecture/Monergy.Architecture.Tests/Monergy.Architecture.Tests.csproj --configuration Release
./build/verify-customer-identity-boundary.ps1 -SelfTest
./build/verify-customer-identity-boundary.ps1
./build/d13/Get-D13SourceIdentity.ps1
pnpm format:check
git diff --check
```

The exact scenario inventory is maintained in
`build/governance/d13-scenario-matrix.json`. Source identity uses
`StringComparer.Ordinal` path ordering and unambiguous length-prefixed hashing.

## Limits

This candidate does not implement current-session lifecycle, expiry, revocation,
privilege/risk changes, CID-005 through CID-007, durable event propagation,
physical persistence, frontend flows, Production provider compatibility,
deployment, acceptance, or architecture/System Expert updates. D11 and D12
source and behavior remain unchanged.
