# T1-74 — reviewed expense corrections and history

Status: SOURCE_CANDIDATE_NATIVE_VERIFIED. Application base `8d6e3b1dd315b69b5353e62f6dbe791d3fc03a14`. Original source phases, acceptance and stage gates remain unchanged.

## Planned functionality and meaning

Master§13 Expense / MON-FIN-004 / COV-017 / M2-WS04-E01-F01 require maintained expense records. T1-73 provided create/list/details. T1-74 adds explicit version-checked corrections of name, customer category, period total and reporting dates, with all prior versions available.

Amount remains total spent for the explicitly stated past/elapsed reporting period. Category is customer text; no merchant/transaction classification, recurrence, annualisation, overlap resolution or category aggregation is inferred. Original source fact/revision, currency, provenance/document/evidence and CUSTOMER_REVIEWED status remain immutable. Customer review does not verify payment with a merchant or bank. Source amounts/dates are never mapped into the record.

## Owner, persistence and API

Financial Profile owns append-only expense_record_revisions through migration0010. Tenant/customer/record/revision keys, scoped actor/operation uniqueness, creation FK and SELECT/INSERT-only runtime grants preserve creation/correction receipts. Operation then record locks enforce atomic expected-version checks. The same normalized body/operation/expected-version replays its original version after newer writes; changed intent or cross-record/create-correction operation reuse conflicts. Latest reads/lists show the newest version. Exact reads and descending history pages of50 preserve originals and expose beforeRevision/HasMore/NextBeforeRevision.

Legacy creation payloads default to Revision1. T1-73 draft/create DTO and existing creation validation/normalization/fingerprint are unchanged. Corrections recheck the original owned EXPENSE source/provenance and CUSTOMER_REVIEWED status. Required trimmed name/category, ordered non-future periods, nonnegative two-decimal amount (maximum999999999999.99), operation/identifier and exact-source checks apply before append.

POST /product-api/v1/expense-records/{expenseRecordId}/corrections and GET /product-api/v1/expense-records/{expenseRecordId}/history use matching owner routes under /local/v1/customers/{customerId}/expense-records. Owner GET/{id}?revision={revision} supports exact readback. Current session, active exact owned customer/ready workspace, operator tenant binding and matching Self read/document/update authority precede correction. History requires read/document access without update. Strict bounded unknown/missing/duplicate JSON rejects caller metadata. Gateway binds all corrected fields and original source/currency/status/customer/actor/operation/version, freshly rereads the exact saved version and reconfirms current authority/context. Creation replay rereads version1.

## Customer experience

The editor preloads the reviewed version and fixes source/currency/status. A stale409 blocks the old form and requires refresh/review; a confirmed400 permits editing. Uncertain saves freeze the exact body/operation/expected-version across close and missing current source, blocking another edit until confirmation. Read-only customers can browse original and corrected category/period totals, status and source in paged history. API/session/access changes hide previous records; aborted or late responses cannot restore previous-session history. Unsupported review status in history fails closed.

## Verification and limits

455 Financial Profile checks pass, including44 new correction/history cases. Actual owner/Gateway HTTP uses explicit current-session/customer/PDP doubles and in-memory persistence. Coverage includes legacy defaults, original/old-correction replay after later changes, paged history, concurrent stale edits, scoped operations, immutable source/status, trimmed category/period/explicit-total semantics, validation, tenant/current authority, malformed input, forged receipts/status and exact-version fresh readback.

40 affected UI checks pass:23 Expense records and17 Financial information. Eight new cases cover editing without current source, stale refresh, frozen uncertain retries, validation versus uncertainty, read-only paged history, session/cancellation boundaries and invalid review status. TypeScript, changed source/test ESLint, Vite and zero-warning Gateway/physical persistence test-project builds pass. Physical PostgreSQL correction/restart/replay/history/runtime-grants regression is compiled, NOT RUN.

Unchanged T1-69 VS02/real OCR573, reprocessing40, Reporting64, Search27 and Documents111; T1-70 Loan22/Investment20 UI and T1-72 Income23 UI evidence are carried forward, not rerun or counted as new passes. Prior Income/Loan/Investment owner checks are rerun within455. Physical PostgreSQL/live scanner/durable processing/Compose/full authenticated integrated browser UAT remain NOT RUN. Owner history is not central Audit delivery.

## Remaining scope

Multi-source/duplicate/overlap reconciliation, merchant/bank payment verification, recurring-spend detection, comparable-period presentation/dashboard/calculations, full authoritative expense truth, central Audit and physical/full UAT remain open. No household spending/cash-flow/affordability/tax totals, package/service/development Actions, deployment or acceptance advancement are added.

After paired merge:74 numbered units =73 implementations +1 governance closure. Original29 journeys remain0 complete/17 partial/12 pending; Expenses stays Partial. Original151 IDs/81 features/126 Phase1 memberships/source phases/acceptance remain. Tracker version16 through T1-73 reviewed before work.
