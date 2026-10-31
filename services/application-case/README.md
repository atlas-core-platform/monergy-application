# Application & Case Service

P3-01 introduces the first bounded Phase 3 owner journey: a Customer opens one active
financial-assistance case and an eligible DSA / Case Manager in the same tenant accepts it.

The owner derives tenant, actor and Customer identity from the verified session. The browser
cannot post those identifiers or select an assignee. Acceptance is atomic, version checked,
idempotent by actor-bound operation ID and recorded in append-only event history.

This source increment deliberately stores case state in the LOCAL reference adapter. Physical
PostgreSQL persistence, restart readback and retained browser acceptance remain Phase 3
acceptance gates. Case acceptance grants no financial data access, Consent, customer/resource
assignment, advice, underwriting or lender decision authority.
