# Application & Case Service

P3-01 introduces the first bounded Phase 3 owner journey: a Customer opens one active
financial-assistance case and an eligible DSA / Case Manager in the same tenant accepts it.
P3-02 adds the tenant-bound PostgreSQL owner, append-only lifecycle/operation evidence and
restart recovery while preserving the exact P3-01 authority and privacy boundaries.

The owner derives tenant, actor and Customer identity from the verified session. The browser
cannot post those identifiers or select an assignee. Acceptance is atomic, version checked,
idempotent by actor-bound operation ID and recorded in append-only event history.

LOCAL/CI can select either the explicitly temporary reference adapter or the tenant-bound
PostgreSQL adapter. The physical adapter preserves cases, idempotency receipts and event
history across restart; installed-browser and production acceptance remain separate gates.
Case acceptance grants no financial data access, Consent, customer/resource assignment,
advice, underwriting or lender decision authority.
