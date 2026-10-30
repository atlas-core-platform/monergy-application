# P2-28 — Customer-reported property ownership lifecycle

## Bounded journey

A Customer who already has current Self access to Financial information and the exact source document can review a saved Property record as **Owned** or **Disposed**, record the effective date, and later restore it to **Owned**. The Financial Profile owner appends a new immutable record version for every review. The Customer Web exposes the current customer-reported state and its history; it does not present that state as legal-title or registry verification.

## Owner and integrity boundary

- Financial Profile owns property records, lifecycle revisions and asset-snapshot eligibility.
- Tenant, actor and Customer are resolved from the authenticated session. The browser supplies only the canonical property record, expected revision, bounded state, date and idempotency operation.
- Current Self `financial-profile.profile.read`, `financial-profile.profile.update` and `evidence.document.read` authority is required before the owner write and rechecked before the Gateway releases success.
- Each lifecycle write revalidates the exact original Financial Fact, provenance, Evidence and document version.
- Exact replay returns the existing receipt; changed reuse and stale revisions fail closed.
- Existing payloads deserialize as `UNREVIEWED` with no invented effective date. Corrections retain the reviewed state and may not move the property value date after a recorded disposal.
- A property disposed on or before a requested asset-snapshot date is excluded from that current snapshot. Earlier immutable versions remain queryable in property history.

## Explicit exclusions

This increment does not verify legal title, registration, joint ownership percentages, encumbrances or liens, professional valuation, sale consideration or proceeds, tax, provider/registry connectivity, eligibility, recommendations or advice. Physical PostgreSQL, installed-browser, retained LOCAL and live-provider acceptance remain separate gates.
