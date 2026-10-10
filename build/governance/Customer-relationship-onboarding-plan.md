# Customer relationships, Tenant Admin setup, and integrated onboarding

Status: Proposed integration design; implementation is blocked at the Consent owner dependency.
Authority: continuation request of 2026-10-10, AR-002, AM-08, UI-05, CP-01.
This record does not accept or merge the four prerequisite draft PRs.

## Current evidence

- AR-002 is architecture PR #23; AM-08 is access-management PR #9; UI-05 and CP-01 are application PRs #41 and #42.
- UI-05's directory was registered in the wrong composition mode and returned customer IDs absent from Docker UAT's owner bindings. The continuation corrects this, adds directory tests, and extends the disposable UAT checks.
- CP-01's registry did not enforce its documented receipt immutability; separate registry instances could overwrite one another. The continuation enforces the ordered append-only receipts, lifecycle transitions, identity immutability, file locking, and stale-write rejection.
- CP-01 activation previously required only provisioning receipts. It now requires a tenant-matched setup receipt from `ITenantSetupReadiness`. Without an adapter it stays READY_FOR_ADMIN. A test stub is not an operational setup implementation.
- `services/consent/Program.cs` contains only health endpoints. It has no decision, grant, expiry, or revocation implementation.
- C&I has tenant principals and trusted sessions, but no persisted canonical Customer-to-actor ownership model. UI-05's LOCAL/CI directory contains display fixtures only; it must never become proof of self-access.
- The UAT bootstrap provisions fixed local tenants using owner migrations; CP-01 has no real provisioning adapters. A healthy container is not proof of completed onboarding.

## Relationship model

| Responsibility | Authority | Required behavior |
| --- | --- | --- |
| Customer identity and labels | Customer & Identity | Tenant-local canonical Customer ID, display details, optional verified owner actor. No email-based self-access inference. |
| Advisor-to-Customer assignment | Access Management | One durable relationship per tenant, advisor actor, and customer; assign/remove by authenticated Tenant Admin; optimistic policy version, audit/outbox, immediate revocation. |
| Business capabilities | Access Management | Existing optional single business role and additive permissions. Administration and groups confer no financial access. |
| Resource-to-Customer ownership | Each resource owner | Resolve or verify the persisted owner customer before returning a document, report, financial record, or derived result. Caller-supplied customer IDs are not ownership evidence. |
| Permission to use customer information | Consent | Current purpose/capability-specific decision, expiry and revocation; unavailable, missing, stale, mismatched, or denied decisions fail closed. |
| Setup and lifecycle | Platform Control Plane | Durable setup receipts and explicit activation after provisioning and required organization/access review. |

Customer self-access requires an authoritative C&I ownership link **and** a granted business capability. Advisor access requires an active relationship, a granted capability, and applicable current Consent. Neither path creates per-document, per-report, or per-account assignments.

Relationship removal increments the tenant policy and affected subject versions in the same AM transaction as audit/outbox. Role changes must not revive an earlier removed relationship. Owner labels remain outside AM; no cross-service joins, foreign keys, or transactions are introduced.

Existing tenant policies are not rewritten. AM-08 pack 1.0 remains unchanged; document/report reads remain excluded from starter roles until propagation, owner checks, and Consent have passing evidence. Any subsequent pack is versioned and explicitly adopted.

## Enforcement and contracts

Keep the existing login/session authority and authentication lifecycle. Do not introduce a Production identity provider or convert the local reference key into a new authentication design.

For a customer business operation:

1. Resolve the trusted session and active tenant membership.
2. Require the role's capability and current policy/subject versions.
3. Resolve the canonical customer or validate the resource's persisted customer at its owning service.
4. Require authoritative self-ownership or an active advisor relationship.
5. Obtain the applicable current Consent decision, bound to tenant, actor, customer, purpose, capability, and evaluation time.
6. Execute inside the owner boundary; filter every search/derived result by that boundary.

An AM customer-scope decision is not a stand-alone resource-ownership proof. New customer-context evaluation contracts must be explicit and independently tested; do not overload `ResourceId` or trust an extra browser header. Preserve the existing exact-resource evaluator for its current callers. The relationship-enabled owner route must not fall back to legacy tenant-wide permission when relationship or Consent checks fail.

## Tenant Admin first login

After the existing trusted identity, tenant, session, and Tenant Admin checks succeed, show a compact setup workspace:

1. Review organization name, country, time zone, and tenant context.
2. Review starter roles and the separate Tenant Admin authority. Explain that administrator access does not grant financial access.
3. Add team members, choose their optional business role, and assign advisors to named customers when the relationship owner is available. No minimum team/customer count should force fictitious data.
4. Review a summary and complete setup. The server validates the current policy version and required acknowledgements, writes a durable tenant/actor-bound receipt, then requests activation.

Setup survives reload/reconnect through server state; authentication credentials remain memory-only. A failed save leaves the tenant READY_FOR_ADMIN. Replaying completion returns the original receipt, while a changed request with the same idempotency key conflicts. A missing owner is an actionable blocked step, never a pre-checked success indicator.

## Integrated UAT gate

Use a separately named disposable Docker profile; never reset the user's retained default volume.

- New tenant: owner provisioning, migrations, canonical initial admin, AM admin-only bootstrap and pack, readiness, first login, setup receipt, activation.
- Replay and restart: no duplicate tenant, role, principal, relationship, or receipt; resume from the failed owner step.
- Identity: no shell before authorization; direct-route reload, sign-out, expiry, invalid credentials, tenant mismatch, and non-admin setup mutation fail safely.
- Customer self: verified ownership allows only granted capabilities; no role, disabled membership, unknown ownership, or another customer's resource denies.
- Advisor: one relationship propagates across supported owners; assignment removal, expired/revoked Consent, missing capability, or dependency failure denies immediately.
- Resource tampering: another customer's document/report ID cannot be accessed using an assigned customer's envelope. Search, AI, and derived results cannot broaden scope.
- Concurrency: stale revisions conflict and parallel requests preserve all registry records/receipts.
- Presentation: names instead of raw IDs, clear change review, compact Midnight components, keyboard navigation and reduced-motion behavior.

Positive Consent-dependent Advisor journeys cannot be claimed until the Consent owner exists. Existing boundary probes with `operationExecuted=false` do not count as those journeys.

## Concrete dependency decision

Recommended next scope: implement a bounded **LOCAL/UAT Consent owner adapter**, with tenant-owned persisted grants, purpose/capability binding, expiry, revocation, workload-authenticated evaluation, and audit evidence. Keep it behind the existing Development LOCAL/CI guard. Do not select Production consent policy, legal basis, cloud infrastructure, identity provider, or release authority.

This is additional Consent-service functionality, beyond the approved access-management presentation and CP-01 orchestrator scope. Per the user's requirement to obtain approval before expanding functional scope or authorization contracts, request approval for that adapter (or an existing governed owner contract) before enabling relationship-derived Advisor access. Until then, do not fabricate successful consent decisions or mark integrated onboarding accepted.
