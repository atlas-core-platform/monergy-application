# T1-70 — reviewed investment corrections and history

Status: SOURCE_CANDIDATE_NATIVE_VERIFIED. Original phases, acceptance and stage gates remain unchanged. Application base `70452af4dfa7b04c80bbd6f24a070368ed2f1126`.

## Planned functionality

Master§13 Investment, MON-FIN-007 / COV-020 / M2-WS04-E02-F01 require maintained investment records. T1-69 delivered reviewed create/list/read. T1-70 adds explicit version-checked correction of holding name, institution, customer text type, separate purchase/current values and value-as-of date. Customers can inspect all prior versions without losing the original reviewed source or creation receipt. No provider import or financial valuation calculation is added.

## Owner and persistence

Financial Profile appends corrections to investment_record_revisions using migration0006. Its tenant/customer/record/revision primary key, scoped tenant/customer/actor/operation uniqueness, creation FK and SELECT/INSERT-only runtime grants prevent ordinary runtime UPDATE/DELETE. Repository operation/record locks and expected-version checks serialize competing edits. Creation and correction operation namespaces are checked together. Same operation/body/expected-version replays its original saved version after later corrections; changed intent conflicts. Latest reads/lists show the latest version. Exact version and bounded50-item history pages retain prior data and expose beforeRevision/HasMore/NextBeforeRevision.

Legacy creation payloads without Revision deserialize as1. The T1-69 draft/create DTO and normalized creation fingerprint are unchanged. Original exact source fact/revision, currency, provenance/document/evidence remain immutable. Correction rechecks original owned INVESTMENT source/provenance. It never rewrites the source fact or infers a holding/value from its amount. Explicit zero values remain valid. Version/amount/text/date validation applies before mutation.

## Boundaries and customer experience

POST /product-api/v1/investment-records/{investmentRecordId}/corrections and GET /product-api/v1/investment-records/{investmentRecordId}/history use the corresponding owner paths under /local/v1/customers/{customerId}/investment-records. Owner GET/{id}?revision={revision} supports exact readback.

Operator customer-to-tenant binding, current Self read/document/update authority, current session, active exact owned customer and ready workspace remain required. History needs read/document access, not update access. Strict bounded unknown/missing/duplicate JSON fails closed. Gateway validates complete correction/source/actor/operation/version binding, rereads that exact saved version and reconfirms authority/context. Creation replay rereads version1 even after corrections. History validates record/customer/version/order/page bindings.

The editor preloads the version the customer reviewed and fixes its source/currency. Stale409 blocks the old form and requires refresh/review. A confirmed400 permits editing. Other uncertain saves freeze the exact body/operation/expected-version across drawer close and missing current source. No second correction can replace an uncertain submission. Read-only customers can browse original and corrected versions. Access/API/session changes hide prior data. Pending history loads abort on scope change; ignored cancellation cannot restore a prior session's history.

## Verification

238 Financial Profile owner checks pass, including39 new investment correction cases. Real owner/Gateway HTTP with explicit current-session/customer/PDP doubles and in-memory storage covers correction/history/readback, legacy defaults, source immutability, creation/old-correction replay after newer writes, concurrent stale edits, operation scoping, native malformed input, tenant/current authority denial, read-only history, forged correction receipts and mismatched exact readback.

57 affected UI checks pass:20 Investment records,15 Financial information and22 Loan records. Seven new investment cases cover explicit expected-version edits, immutable historical source, stale refresh, frozen uncertain retries, validation versus uncertainty, read-only paged history and scope/cancellation boundaries. TypeScript, changed source/test ESLint, Vite and zero-warning Gateway/physical persistence test-project builds pass.

Unchanged source/extraction/Search/Reports/reprocessing and111 Documents UI verification from T1-69 is carried forward, not rerun or counted as new T1-70 passes. No corresponding implementation changed. Physical PostgreSQL correction/reconstruction/exact original/replay/history/runtime-grants regression is authored and compiled, NOT RUN. These focused native tests do not prove physical database or full authenticated integrated UAT.

## Remaining scope

Investment lifecycle, institution/ownership/current-price verification, securities/quantity/tax lots, provider import, multi-source reconciliation, portfolio/allocation/net-worth calculations and full authoritative asset truth remain open. Owner history is not central Audit delivery. Physical PostgreSQL, live scanner/durable processing/Compose and full browser UAT remain NOT RUN. No package/service/development Actions, production deployment or acceptance/source-phase advancement.

After paired merge:70 numbered units =69 implementations +1 governance closure. Original29 deliverables remain0 complete/17 partial/12 pending; Assets stays Partial. Original151 IDs/81 features/126 Phase1 memberships/source phases/acceptance remain. Tracker version12 through T1-69 reviewed before work.
