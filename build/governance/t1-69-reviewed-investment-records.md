# T1-69 — customer-reviewed investment records

Status: SOURCE_CANDIDATE_NATIVE_VERIFIED. Original source phases, acceptance and stage gates are unchanged. Application base: `4cc9c06ea377874f9032701d24618df2d96137d2`.

## Planned functionality

Master requirements §13 Investment fields, MON-FIN-007 / COV-020 and M2-WS04-E02-F01 require investment records beyond numeric source figures. Financial information > Investment source figures now includes explicit customer-reviewed investment records. Customers enter a meaningful holding name, institution, investment type, purchase value, current value and value-as-of date. Investment type is customer text, not a validated provider/security taxonomy. Both values use the exact reviewed source's currency; no amount is filled or inferred from its label/value.

## Owner and security

Financial Profile owns create/list/read for an immutable reviewed investment record. An exact INVESTMENT fact revision owned by the customer and its provenance must exist. Original source value/revision/document/evidence/currency remain unchanged. Current source lists may advance while the record retains its historical source. Input rejects missing/unknown/duplicate fields, invalid identifiers, control text, negative/out-of-bound/fractional-cent values and invalid/future dates. Zero values are allowed when explicitly entered. The date uses the existing UTC+14 local-calendar boundary.

Owner: `GET/POST /local/v1/customers/{customerId}/investment-records`; `GET /local/v1/customers/{customerId}/investment-records/{investmentRecordId}`. Customer: `GET/POST /product-api/v1/investment-records`. Lists expose at most200 creations with HasMore. Multiple records may reference one source; the list does not claim deduplicated holdings or a complete portfolio.

Operator customer-to-tenant binding precedes current Self financial-read/document-read/update authority. Gateway requires current session, active exact owned customer and ready workspace, binds complete returned draft/actor/operation/customer, freshly rereads the saved owner record and reconfirms authority/context. Cross-origin writes and unbounded bodies fail closed. Failed or changed authority cannot be replaced by an advisor grant.

Migration0005 adds investment_records with exact fact revision FK, tenant/customer/actor operation uniqueness and SELECT/INSERT-only runtime grants. PostgreSQL uses a transaction and deterministic record lock for creation/replay. A changed body under the same operation returns409; an identical request returns its original receipt/time. A corresponding locked in-memory repository supports CI. Owner creation history is not central Audit delivery.

## Customer experience

Current record list/details show institution/type, separately entered purchase/current values and valuation date, original currency, and exact source history. Creation needs an explicitly selected current reviewed source. A changed source revision requires a new selection before first save. Unconfirmed saves freeze their exact operation/body across close/reopen and can retry even if the source disappears from the current list. A confirmed400 permits editing. Read-only access hides creation. Removed document access and changed API/session scope hide previous data; canceled or superseded loads do not restore it.

## Verification

903 backend checks pass:573 VS02 including real OCR,199 Financial Profile owner (53 new investment cases),40 reprocessing,64 Reporting and27 Search. Native owner/Gateway HTTP tests use explicit current-session/customer/PDP doubles and the in-memory owner. They prove exact source retention, unchanged source values, operation replay/conflict/concurrency, current authority and foreign-tenant denial, strict input, bounded lists, forged receipts, fresh readback mismatch and recovery after a committed write loses confirmation.

161 UI checks pass:111 Documents,15 Financial information,22 Loan records and13 Investment records. TypeScript, changed source/test ESLint, Vite and zero-warning Gateway/physical persistence test-project builds pass. Physical PostgreSQL restart/replay/source/grants regression is authored and compiled, NOT RUN.

## Remaining scope

This is customer-reported review, not institution verification, priced securities/quantity/tax-lot accounting, a portfolio valuation/allocation calculation or net worth. Record corrections/lifecycle, provider import, multi-source reconciliation, central Audit delivery and full authoritative financial truth remain open. Physical PostgreSQL, live ClamAV/durable processing, Compose and full authenticated integrated browser UAT remain NOT RUN. No package/service/development Actions or production deployment is added.

After paired merge:69 numbered units =68 implementations plus1 governance closure. Original29 journeys remain0 complete,17 partial,12 pending. Assets stays Partial. Original151 requirement IDs,81 features,126 Phase1 memberships/source phases and acceptance remain unchanged. Workbook version11 through T1-68 was reviewed before work.
