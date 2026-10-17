# T1-22 — Verified recipient labels in Customer Consent

Status: **BOUNDED SOURCE CANDIDATE — NOT LOCAL-UAT VERIFIED**.
Application baseline `375ed677d13e075eb666df78ba5626f44ff34ff7` (T1-21 application PR #69).
Requirements: Master §§6,39; COV-005; M2-WS02-E03-F01 (partial).

## Business outcome
A Customer reviewing recorded delegated sharing permission sees the recipient's **tenant-verified normalized email label** when the recipient has a C&I principal record. The UI continues to show the exact recipient/grant reference during consent revocation. If C&I has no label, the current explicit reference-only warning remains. No invented display name or universal directory.

## Exact ownership and data path
1. Product customer session visits `/consent` and requests its existing Consent-owned list.
2. Consent validates current session and customer ownership before fetching the customer's at-most-100 granted records. These are the **only** actor IDs eligible for label lookup.
3. Consent invokes a bounded concurrent (max six) internal C&I label operation for each eligible actor ID.
4. C&I checks the separately configured `CustomerContextToken` workload credential and the trusted tenant binding, and reads its own `tenant_principals.normalized_email` from that tenant's isolated C&I database.
5. Consent rechecks current Customer & Identity session, customer ownership and owner version after resolving labels. Changed identity/owner version aborts disclosure.
6. Consent attaches optional `recipientLabel` to an existing grant projection. The Customer view renders that label; absent labels remain explicitly unavailable.

## Privacy, security and data ownership
- Never accept arbitrary actor IDs from the customer UI as lookup requests.
- Never expose `/internal/v1/consent-recipient-label/{actorId}` through the browser/UAT Gateway.
- Existing C&I workload token, not tenant administrator authority or a new customer permission, protects the internal route.
- No new SQL table, index or permission. The C&I principal database is authoritative for normalized email; Consent remains authoritative for grants/version/revocation.
- The recipient email is only disclosed to the owner of the existing grant relationship. Cross-tenant customer/actor binding is enforced at both owner and C&I boundaries.
- Absent C&I record and C&I read failure should not produce fabricated names; current grant rights are unchanged.
- No lending provider, Account Aggregator, OAuth, email invitation or grant/edit consent functionality.

## Acceptance gate
- Unit/persistence tests: tenant mismatch, missing actor, invalid C&I workload token, no arbitrary directory, changed customer owner version after lookup.
- API test: owner-only grants enriched in current tenant; no label on unrelated customer grant; fail-closed on failed ownership recheck.
- UI tests: recognizable email labels, null fallback, malformed label fail closed, same recipient/grant confirmation in revocation.
- Focused local test suite and consent-enabled Docker profile; System Expert exact-source overlay and relationship verification.
- No development GitHub Actions. Source-only code review is **not** acceptance.

No cosmetic UI review is required for this bounded functional unit.
