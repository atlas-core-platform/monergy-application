# P3-06 — Property Valuer workspace

## Accepted source behavior

P3-06 gives an authenticated Property Valuer a bounded workspace for property valuation work that
Application & Case has explicitly assigned to that actor. Access Management Default Access Pack 2.10
grants only `application-case.property-valuation.manage` to the `property-valuer` role. The tenant-scoped
workspace capability is not a customer assignment, a document grant, customer Consent, or permission to
inspect any unassigned property.

Application & Case owns the assignment, inspection state, optimistic version, immutable valuation
revisions, evidence-version snapshots, operation receipts and actor provenance. The service re-evaluates
the current Access Management decision, active customer, exact assignment, customer sharing for Financial
Profile and Evidence, and source versions before every mutation. Role loss, disablement, tenant mismatch,
assignment mismatch, Consent expiry/revocation, evidence drift, stale versions and operation-ID reuse fail
closed. Browser retries cannot create duplicate inspections or valuation revisions.

The customer workspace exposes only the customer's latest professional result and its safe provenance.
Customer-reported property values and Monergy calculations remain distinct; neither is represented as a
professional valuation. OCR, AI output and customer-entered data remain supporting sources and never own
the valuer's conclusion.

## LOCAL and persistence boundary

Application & Case schema 5 adds property assignments, evidence inspection rows, append-only valuation
revisions, immutable evidence snapshots, audit events and idempotency receipts. The runtime may update only
assignment state/version and inspection columns, and may append revisions, snapshots, events and operation
receipts; it cannot insert/delete assignments or update/delete/truncate professional history.

LOCAL actor `A700` represents the Property Valuer. The onboarding fixture assigns one property valuation
per tenant and advertises `/property-valuer` only after the role and Default Access Pack 2.10 are confirmed.
The fixture is illustrative and does not replace physical PostgreSQL, retained LOCAL or installed-browser
acceptance.

## Deferred acceptance

This host has no Docker daemon or disposable PostgreSQL owner/runtime bindings. Migration 0005, runtime
least privilege, retained-profile compatibility, installed-browser journeys, 13/13 readiness and 13/13
authenticated topology therefore remain mandatory acceptance evidence before pre-production promotion.
