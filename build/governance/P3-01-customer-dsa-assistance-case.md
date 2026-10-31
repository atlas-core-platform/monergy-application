# P3-01 — Customer to DSA assistance-case handoff

## Bounded implementation

P3-01 introduces the post-baseline Application & Case Service and one complete
LOCAL/CI journey: an authenticated Customer submits a financial-assistance case,
the tenant DSA / Case Manager queue shows the safe request, and the first
currently eligible DSA accepts it atomically. Customer and DSA identities,
tenant, capability and policy versions are resolved from current owner sessions;
requests cannot post them.

Access Management Default Access Pack 2.6 adds only
`application-case.case.create` to Customer and
`application-case.case.accept` to DSA / Case Manager. Neither capability grants
financial facts, documents, Consent, customer/resource assignment, advice,
underwriting, lender matching or lender decision authority.

## Integrity

- operation identifiers are actor- and tenant-bound and retry-idempotent;
- one active case is allowed per Customer;
- acceptance requires the exact visible version and current DSA authority;
- concurrent acceptance has one winner and stale attempts fail closed;
- cross-tenant reads return no case;
- the Customer sees an append-only Submitted → Accepted history;
- the DSA queue contains the request type, age, state and opaque case reference,
  but no Customer identity, document or financial fact.

## Acceptance boundary

The current owner adapter is deliberately volatile and is guarded to LOCAL/CI.
Source acceptance does not claim durable case persistence, restart recovery,
notifications, lender progression, marketplace behavior or production release
acceptance. Those remain later Phase 3 journeys and physical acceptance gates.

The current LOCAL topology advances from thirteen to fourteen authenticated
boundaries: thirteen domain services plus the cross-cutting Access Management
security service.
