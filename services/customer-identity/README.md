# Customer & Identity reference implementation

MWP-03-D14 advances the accepted D13 provider-neutral boundary with a bounded
`SIMULATOR / LOCAL / CI_EPHEMERAL` candidate for `M2-WS02-E01-F02 — Trusted
session lifecycle`. Customer & Identity Service remains the sole changed domain
authority. D14 preserves CID-001, advances CID-002, and realizes the owner-side
producer for CID-005 without adding a contract identifier or HTTP route.

## Semantics

- CID-001 continues to return a minimal canonical Customer projection only when
  the requested Customer matches the trusted customer context. Cross-customer
  queries fail before repository lookup.
- CID-002 resolves an opaque provider-neutral authentication reference through
  an internal replaceable port, then establishes or evaluates an owner-controlled
  trusted session. It returns only the canonical `ActorContext` while that exact
  Customer, actor, and authentication context remains current. Exact expiry,
  revocation, mismatch, malformed state, and dependency failure all fail closed;
  stale D13 provider success cannot bypass session state.
- The injected reference policy uses a deterministic 30-minute lifetime. This is
  LOCAL/CI simulator configuration, not a Production or architecture policy.
  Expiry is evaluated through `TimeProvider` without sleeping or a scheduler.
- Revocation is an irreversible, idempotent owner-local reference control. It is
  deliberately not exposed as a public Monergy contract or `/session` API. A new
  authentication context may establish a new session; an old revoked context is
  never reactivated.
- CID-005 facts are immutable, replay-safe, Customer-scoped and correlated. The
  reference payload carries only a bounded lifecycle change kind and revision;
  it excludes the authentication reference, authentication-context identifier,
  actor identity, Customer projection, provider data, credentials and tokens.
  The envelope maps directly to the existing `AuditableEvent` observation shape
  without changing Audit. The sink is owner-local and makes no durable transport
  or outbox claim. Passive expiry does not emit a timer-driven event.
- Successful authentication/session trust grants neither authorization nor
  consent, and human actor identity remains distinct from workload identity.
- CID-003 Customer mutation and CID-004 KYC history semantics remain unchanged,
  including typed idempotency, revision checks and immutable history snapshots.
- Ordinary telemetry contains only operation, outcome, Customer identifier,
  request identifier, and correlation identifier. It excludes authentication
  references, session state, actor details, Customer display values, verification
  identifiers, and KYC result values.

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
./build/verify-trusted-session-lifecycle.ps1 -SelfTest
./build/verify-trusted-session-lifecycle.ps1
./build/d14/Get-D14SourceIdentity.ps1
./build/Invoke-Toolchain.ps1 -Task FormatCheck
git diff --check
```

The exact scenario inventory is maintained in
`build/governance/d14-scenario-matrix.json`. Source identity uses
`StringComparer.Ordinal` path ordering and unambiguous length-prefixed hashing.

## Limits

This candidate does not implement MFA or step-up, authorization roles or policy,
consent, workload identity, privilege-policy refresh, CID-006, CID-007 changes,
durable CID-005 propagation, a broker/outbox, physical session persistence,
frontend/login flows, Production provider compatibility, token issuance,
deployment, acceptance, or architecture/System Expert updates. D11, D12 and the
D13 source-identity evidence remain unchanged; D13 behavior remains regression
covered.
