# T1-72 — reviewed income corrections and history

Status: SOURCE_CANDIDATE_NATIVE_VERIFIED. Application base `4863af4cce475b870f74321380a175f2a95e886c`. Original source phases, acceptance and stage gates remain unchanged.

## Planned functionality and meaning

Master§13 Income / MON-FIN-003 / COV-017 / M2-WS04-E01-F01 require maintained income records. T1-71 provided create/list/details. T1-72 adds explicit version-checked corrections of name, payer/source, customer income type, period total, payment frequency, reported financial-year label and reporting-period dates, with all prior versions available.

Amount remains the total received for the explicitly stated past/elapsed reporting period. Frequency describes the payment schedule and never multiplies, prorates or annualises that total. The year label is customer reported with no jurisdiction/calendar membership inference. Original source fact/revision, currency, provenance/document/evidence and CUSTOMER_REVIEWED status remain immutable. Customer review does not verify the payer or tax authority. Source amounts/dates are never mapped into the record.

## Owner, persistence and API

Financial Profile owns append-only income_record_revisions through migration0008. Tenant/customer/record/revision keys, scoped actor/operation uniqueness, creation FK and SELECT/INSERT-only runtime grants preserve creation and correction receipts. Operation then record locks enforce atomic expected-version checks. Same normalized body/operation/expected-version replays its original version after newer writes; changed intent or cross-record/create-correction operation reuse conflicts. Latest reads/lists show the latest version. Exact reads and descending history pages of50 preserve all originals and expose beforeRevision/HasMore/NextBeforeRevision.

Legacy creation payloads default to Revision1. T1-71 draft/create DTO and normalized creation fingerprint are unchanged. Corrections recheck the original owned INCOME source/provenance and customer review status. Existing trimmed text, frequency, year, ordered non-future reporting period, nonnegative two-decimal amount (maximum999999999999.99), identifier and source checks remain.

POST /product-api/v1/income-records/{incomeRecordId}/corrections and GET /product-api/v1/income-records/{incomeRecordId}/history use matching owner routes under /local/v1/customers/{customerId}/income-records. Owner GET/{id}?revision={revision} supports exact readback. Current session, active exact owned customer/ready workspace, operator tenant binding and matching Self read/document/update authority precede correction. History requires read/document access. Strict bounded unknown/missing/duplicate JSON rejects caller metadata. Gateway binds every corrected field and original source/currency/status/customer/actor/operation/version, freshly rereads the exact saved version and reconfirms authority/context. Creation replay rereads version1.

## Customer experience

The editor starts from the reviewed version and fixes its source/currency/status. A stale409 blocks the old form and requires refresh/review; a confirmed400 permits editing. Uncertain saves freeze the exact body/operation/expected-version across close and absent current source, blocking another edit until confirmation. Read-only customers can browse paged history and original period totals, schedule, year label, status and source. API/session/access changes hide prior records; aborted or late responses cannot restore prior-session history. Unsupported review-status history fails closed.

## Verification and limits

353 Financial Profile checks pass, including48 new correction/history cases. Actual owner/Gateway HTTP uses explicit current-session/customer/PDP doubles and in-memory persistence. Coverage includes legacy defaults, exact original/old-correction replay after later changes, history paging, concurrent stale edits, operation scoping, immutable source/status, period/frequency/year semantics, validation, tenant/current authority, malformed input, forged receipts/status and fresh-version readback.

39 affected UI checks pass:23 Income records and16 Financial information. Eight new cases cover editing without current source, stale refresh, frozen uncertain retries, validation versus uncertainty, read-only paged history, session/cancellation guards and invalid review status. TypeScript, changed source/test ESLint, Vite and zero-warning Gateway/physical persistence test-project builds pass. Physical PostgreSQL correction/restart/replay/history/runtime-grants regression is compiled, NOT RUN.

Unchanged T1-69 VS02/real OCR573, reprocessing40, Reporting64, Search27 and Documents111, plus T1-70 Loan22/Investment20 UI evidence, are carried forward, not rerun or counted as new passes. Physical PostgreSQL/live scanner/durable processing/Compose/full authenticated integrated browser UAT remain NOT RUN. Owner history is not central Audit delivery.

## Remaining scope

Multi-source/duplicate/overlap reconciliation, payer verification, fiscal-calendar validation, comparable-period snapshots/dashboard/calculations, full authoritative income truth, central Audit and physical/full UAT remain open. No household income/tax/affordability/cash-flow totals, package/service/development Actions, deployment or acceptance advancement are added.

After paired merge:72 numbered units =71 implementations +1 governance closure. Original29 journeys remain0 complete/17 partial/12 pending; Income stays Partial. Original151 IDs/81 features/126 Phase1 memberships/source phases/acceptance remain. Tracker version14 through T1-71 reviewed before work.
