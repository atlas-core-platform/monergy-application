# T1-71 — reviewed income records

Status: SOURCE_CANDIDATE_NATIVE_VERIFIED. Original phases, acceptance and stage gates remain unchanged. Application base `fbed93d79b683d94fc8f91ea18172a66fd203d71`.

## Planned functionality and meaning

Master§13 Income explicitly requires source, income_type, amount, frequency, financial_year, source_document and verification_status. MON-FIN-003 / COV-017 / M2-WS04-E01-F01 retain this missing record functionality beyond dated net-pay source figures. Financial information > Income now supports reviewed record create/list/details with name, payer/source, customer income type, amount, payment frequency, financial-year label and reporting-period start/end.

Amount means total received for the explicitly stated past/elapsed reporting period. Frequency describes the payment schedule (one-off/daily/weekly/monthly/quarterly/yearly/irregular); it does not multiply, annualise, prorate or convert the amount. The financial-year label is customer reported with no jurisdiction, calendar membership or tax-year inference. Currency and document/evidence/provenance come from an exact previously reviewed owned INCOME source revision. Nothing is autofilled from its amount/date. Owner verification status is always CUSTOMER_REVIEWED; it does not verify the payer or tax authority.

## Owner, persistence and security

Financial Profile owns GET/POST /local/v1/customers/{customerId}/income-records and GET /local/v1/customers/{customerId}/income-records/{incomeRecordId}. Customer GET/POST /product-api/v1/income-records requires current session, active exact owned customer/ready workspace and matching Self financial-read/document-read/update authority. Operator customer-to-tenant binding precedes PDP evaluation. Browser origin and bounded strict unknown/missing/duplicate JSON controls remain.

Source category/customer/exact revision/provenance are checked. Required trimmed text, supported frequency, ordered non-future reporting periods (existing UTC+14 local-date boundary), nonnegative amount/two-decimal precision and identifiers are validated before storage. The amount bound999999999999.99 preserves cent precision through browser JSON. Zero is explicit and allowed. Caller currency/customer/review status/actor metadata is rejected. Gateway binds complete reviewed fields, original source/customer/operation/actor and CUSTOMER_REVIEWED status, freshly rereads the immutable receipt and reconfirms authority/context.

Migration0007 adds income_records with exact fact revision FK, tenant/customer/actor operation uniqueness and SELECT/INSERT-only runtime grants. PostgreSQL transaction/record locking and the matching locked in-memory repository return one immutable receipt for the same operation/body; changed intent conflicts. Original source facts and values are unchanged. Lists bound200 creations with HasMore. Repeated/overlapping records and source references are not deduplicated or summed.

## Customer experience

The form requires explicit payer/type/frequency/year/period/amount review and source selection. Lists/details show total, period, source/type, frequency, reported year, currency and customer review status with original source history. A changed current source revision requires a new choice before first save. Uncertain saves freeze body/operation across drawer close and missing current source; confirmed400 permits editing. Read-only customers can inspect records. API/session/access changes hide prior records, and late canceled responses cannot restore them. Unsupported review status is rejected in confirmation and lists.

## Verification

305 Financial Profile checks pass,67 new income cases covering fields, dates, frequency/year semantics, explicit zero/cent bounds, immutable historical source/value, scoped operation replay/conflict/concurrency, bounded lists, native owner/Gateway HTTP, tenant/current authority, malformed bodies, forged receipts/status and lost confirmation. Native HTTP uses explicit current-session/customer/PDP doubles and in-memory persistence.

31 affected UI checks pass:15 Income records and16 Financial information, including category integration, explicit fields/no autofill, period readback, same-request recovery/close/missing source, revision reselection, validation, read-only/current access, late session responses and invalid review-status rejection. TypeScript, changed source/test ESLint, Vite and zero-warning Gateway/physical persistence test-project builds pass.

Unchanged T1-69 source/extraction/Search/Reports/reprocessing/111 Documents UI and T1-70 Loan/Investment UI evidence is carried forward, not rerun/counted as new passes. Physical PostgreSQL restart/replay/period/status/scoping/runtime-grants regression is authored and compiled, NOT RUN.

## Remaining scope

Income corrections/history, multi-source/duplicate/overlap reconciliation, payer verification, fiscal-calendar validation, comparable-period snapshots/dashboard/calculations and full authoritative income truth remain open. These records do not calculate household income, tax, affordability or cash flow. Immutable owner receipts are not central Audit delivery. Physical PostgreSQL/live scanner/durable processing/Compose/full authenticated browser UAT remain NOT RUN. No package/service/development Actions, production deployment or source-phase/acceptance/stage-gate advancement.

After paired merge:71 numbered units =70 implementations +1 governance closure. Original29 journeys remain0 complete/17 partial/12 pending; Income remains Partial. Original151 IDs/81 features/126 Phase1 memberships/source phases and acceptance remain. Tracker version13 through T1-70 reviewed before work.
