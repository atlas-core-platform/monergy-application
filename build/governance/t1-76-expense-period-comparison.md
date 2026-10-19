# T1-76 — selected expense period comparison

Status: SOURCE_CANDIDATE_NATIVE_VERIFIED. Application base `66be12a8a7a1054e351418c5651bef0d1945c047`. Original source phases, acceptance and stage gates remain unchanged.

## Planned functionality and boundary

Master §§13/20/41, MON-FIN-004 / MON-DAS-001/004 / COV-017 / M2-WS04-E01-F01 / M2-WS08-E01-F02 and original Phase 1 Expense snapshot P1-20 retain meaningful period/source presentation beyond coverage counts. T1-76 adds a read-only side-by-side view of two explicitly selected saved expense versions in Financial information > Expenses. Full monthly/household snapshots remain open.

The customer chooses exact record IDs and saved revisions. Eligibility requires distinct records and distinct source financial fact identities, matching currency and normalized customer category (case insensitive), equal inclusive reporting-period day counts and chronologically ordered non-overlapping periods. Different source revisions of the same financial fact cannot masquerade as independent periods. This is conservative eligibility specific to this view. It does not establish general financial comparability, category classification or calendar-month policy. Unequal-length calendar-month comparison remains open.

Original period totals, dates, names, categories and provenance stay unchanged. There is no sum, delta, percentage, rate, currency conversion, recurrence inference, category completeness, household total, verification or financial arithmetic. Financial Rules retains ownership of derived calculations. A record correction is never treated as a separate expense period.

## Owner, API and security

Financial Profile owns POST /local/v1/customers/{customerId}/expense-records/comparison. Customer POST /product-api/v1/expense-records/comparison resolves current session, active exact owned customer/ready workspace and matching Self financial-read/document-read authority. Update access is not needed. The POST carries read-query selections and creates no receipt, record, revision, operation journal or state.

Operator customer-to-tenant binding precedes PDP evaluation. Existing origin/no-store, strict bounded unknown/missing/duplicate/null JSON and identifier/version checks remain. Caller amounts, customer and source metadata reject. Owner reads exact immutable versions within tenant/customer scope and validates status/source/category/currency/period eligibility. Selected historical pairs remain stable after later corrections.

Gateway validates complete owner record bindings, requested IDs/revisions, CUSTOMER_REVIEWED status and pair eligibility. Both exact immutable versions are freshly reread. Complete equality and current authority/context are reconfirmed before returning. Missing/foreign versions and forged owner responses fail closed. Existing creation/correction fingerprints and history remain unchanged. No migration, package or service is added.

## Customer experience

Two explicit saved-version choices are required. No comparison is requested on load. Separate cards show unchanged totals/dates, names/categories/currency, saved revisions/review times, source revisions and source-history links. Missing/incompatible selections have actionable messages. The 200-record choice bound stays explicit and implies no completeness. Read-only customers can compare. Existing document-access boundaries govern the containing view.

Selection changes clear and abort pending results. API/session changes abort previous requests and hide previous results. Ignored cancellation cannot restore old results. Refreshed versions invalidate old selections until the customer chooses again. Unsupported status, wrong revisions and invalid day-count responses remain unavailable.

## Verification

534 Financial Profile checks pass, including 40 new comparison cases. Actual owner/Gateway HTTP uses explicit current-session/customer/PDP doubles and in-memory persistence. Coverage includes exact historical stability after corrections, unchanged source/amounts/state, explicit zero/large cents, distinct-record/source enforcement, category/currency mismatches, overlapping/reversed/unequal periods, single-day eligibility and shared-boundary overlap, invalid/missing/foreign versions, current/read-only authority, reconfirmation, strict ingress and forged comparison/fresh-readback rejection.

53 affected UI checks pass: 13 Comparison, 23 Expense records and 17 Financial information. Comparison cases cover explicit choices, source-linked values, duplicate/missing choices, 400/403/404/503 failures, refreshed versions, session/selection cancellation, malformed status/revision/day counts, bounded choices and read-only integration. TypeScript, changed source/test ESLint, Vite and zero-warning Gateway/physical test-project builds pass. Physical database/integrated comparison UAT is NOT RUN.

Unchanged T1-69 VS02/real OCR573, reprocessing40, Reporting64, Search27 and Documents111; T1-70 Loan22/Investment20 UI and T1-75 Income23/IncomeComparison13 UI evidence are carried forward, not rerun or counted as new passes. All prior Financial Profile owner checks rerun within 534. Physical PostgreSQL/live scanner/Compose/full authenticated integrated browser UAT remain NOT RUN. This view does not advance central Audit delivery or deterministic calculation acceptance.

## Remaining scope

Full monthly/household income and expense snapshots, multi-source/duplicate/overlap reconciliation, completeness and fiscal-calendar normalization, category classification/recurring-spend inference, financial verification, Financial Rules aggregates/changes/ratios, unequal-length calendar-month comparison, complete financial truth, central Audit and physical/full UAT remain open. No source-phase, acceptance, stage-gate or production-deployment advancement.

After paired merge: 76 numbered units = 75 implementations + 1 governance closure. Original 29 journeys remain 0 complete/17 partial/12 pending. Expense and its snapshot stay Partial. Original 151 IDs/81 features/126 Phase 1 memberships/source phases/acceptance remain. Tracker version18 through T1-75 and both clean merged main sources reviewed before work.
