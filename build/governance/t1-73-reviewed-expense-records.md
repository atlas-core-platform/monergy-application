# T1-73 — reviewed expense records

Status: SOURCE_CANDIDATE_NATIVE_VERIFIED. Application base `70c0563c00de2d60995ddeffccc1b75fc6e3c397`. Original phases, acceptance and stage gates remain unchanged.

## Planned functionality and meaning

Master§13 Expense requires category, amount, period and source. MON-FIN-004 / COV-017 / M2-WS04-E01-F01 retain this record-model gap beyond dated expense source figures. Financial information > Expenses now supports create/list/details for a named, customer-reviewed expense category and explicit reporting-period total.

Amount is the total spent for the stated past/elapsed period, represented by ordered start/end dates. Currency and provenance/document/evidence come from an exact previously reviewed owned EXPENSE source revision. Category is required customer text, not inferred from the document, merchant or transaction. The source amount/date do not autofill the form or determine the reporting period. No recurrence, annualisation, category aggregation, overlap resolution or comparable-period calculation is inferred. Owner review status is CUSTOMER_REVIEWED; it does not establish payment verification with a merchant or bank.

## Owner, persistence and security

Financial Profile owns GET/POST /local/v1/customers/{customerId}/expense-records and GET /local/v1/customers/{customerId}/expense-records/{expenseRecordId}. Customer GET/POST /product-api/v1/expense-records requires current session, active exact owned customer/ready workspace and matching Self financial-read/document-read/update authority. Operator customer-to-tenant binding precedes PDP evaluation. Browser origin and bounded strict unknown/missing/duplicate JSON controls remain.

The owner checks exact source category/customer/revision/provenance, required trimmed name/category, ordered non-future period dates using the existing UTC+14 financial-date boundary, nonnegative two-decimal amount with maximum999999999999.99 and source identifiers. Explicit zero is allowed. Caller currency/customer/review-status/actor metadata rejects. Gateway binds all normalized fields, source/customer/operation/actor and CUSTOMER_REVIEWED status, freshly rereads the immutable saved record and reconfirms current authority/context.

Migration0009 adds append-only expense_records with exact fact-revision FK, tenant/customer/actor operation uniqueness and SELECT/INSERT-only runtime grants. PostgreSQL transaction/record locking and the locked in-memory repository return one original receipt for the same operation/body. Changed intent conflicts; source facts and values remain unchanged. Lists bound200 creations with HasMore. Records referring to the same source or overlapping periods are not deduplicated or summed. Owner receipts are not central Audit delivery.

## Customer experience

The form requires explicit source, name, category, amount and reporting-period review. Lists/details expose category, period total, currency, customer review and exact source history. A changed source revision requires reselection before first save. Uncertain writes freeze the exact body/operation across close and missing current source; confirmed400 permits editing. Read-only customers can inspect records. API/session/access changes hide prior data; late canceled responses cannot restore previous-session records. Unsupported review-status confirmations/lists fail closed.

## Verification

411 Financial Profile checks pass, including58 new Expense cases for required fields/period/category, explicit zero/cent bounds, historical source/value, scoped replay/conflict/concurrency, bounded lists, native owner/Gateway HTTP, tenant/current authority, malformed bodies, forged receipts/status and lost confirmation. Native HTTP uses explicit current-session/customer/PDP doubles and in-memory persistence.

32 affected UI checks pass:15 Expense records and17 Financial information, including expense category integration, explicit fields/no autofill, period readback, same-request recovery/close/missing source, revision reselection, validation, read-only/current access, late responses and invalid review-status rejection. TypeScript, changed source/test ESLint, Vite and zero-warning Gateway/physical persistence test-project builds pass. Physical PostgreSQL restart/replay/period/status/scoping/runtime-grants regression is compiled, NOT RUN.

Unchanged T1-69 VS02/real OCR573, reprocessing40, Reporting64, Search27 and Documents111, T1-70 Loan22/Investment20 UI and T1-72 Income23 UI evidence are carried forward, not rerun or counted as new passes. Existing Income owner corrections/history are rerun within411 owner checks. Physical PostgreSQL/live scanner/durable processing/Compose/full authenticated integrated browser UAT remain NOT RUN.

## Remaining scope

Expense corrections/history, multi-source/duplicate/overlap reconciliation, merchant/bank payment verification, recurring-spend detection, comparable-period presentation/dashboard/calculations, authoritative full expense truth, central Audit and physical/full UAT remain open. No household spending, cash-flow, affordability or tax totals are added. No package/service/development Actions, deployment or acceptance/source-phase advancement.

After paired merge:73 numbered units =72 implementations +1 governance closure. Original29 journeys remain0 complete/17 partial/12 pending; Expenses stays Partial. Original151 IDs/81 features/126 Phase1 memberships/source phases/acceptance remain. Tracker version15 through T1-72 reviewed before work.
