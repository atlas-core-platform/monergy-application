# P3-02 — Durable DSA / Case Manager workspace

P3-02 closes the storage and restart gap left deliberately open by P3-01.
Application & Case remains the sole owner of the bounded Customer financial-
assistance case, its Submitted → Accepted lifecycle, the accepting DSA and its
immutable event history.

## Implemented journey

- An authenticated Customer submits one tenant- and customer-bound assistance
  case using a bounded operation identifier.
- An authenticated DSA / Case Manager sees the existing privacy-safe tenant
  queue and accepts one exact case version.
- The Customer sees the accepted state and append-only lifecycle history.
- The case, idempotency receipt and event history survive service and database
  restart when the PostgreSQL adapter is selected.

## Persistence and authority

The physical adapter uses a separate tenant-bound Application & Case database
for each tenant. Startup fails closed unless the database identifies the exact
tenant and schema version and the runtime role has only the required SELECT,
INSERT and bounded case-state UPDATE privileges. Operation receipts and case
events are append-only. Customer identity/binding columns cannot be rewritten
by the runtime role.

Every mutation still re-evaluates current Access Management authority inside
the serialized database transaction. Tenant crossing, capability substitution,
changed subject/policy versions, stale versions, duplicate acceptance and
operation reuse fail safely. A database advisory lock serializes retry and
customer-create races; row locking serializes acceptance.

## Preserved boundaries

DSA case acceptance grants no financial information, document, Consent,
assignment, advice, underwriting, lender-decision, product-matching or provider
authority. The DSA queue remains free of customer identity and financial facts.

## Validation boundary

The source includes focused in-memory behavior tests, physical PostgreSQL
restart/isolation/race/least-privilege tests, React compatibility coverage and a
disposable LOCAL onboarding acceptance path. Production hosting, provider
integration and generalized professional workflows remain separate work.
