# T1-75 — income period comparison

Status: SOURCE_CANDIDATE_NATIVE_VERIFIED. Application base `c35db8c666c342944cf1c388f2bf368f01bd12df`. Original source phases, acceptance and stage gates remain unchanged.

## Planned functionality and boundary

Master§§13/20/41, MON-FIN-003 / MON-DAS-001/004 / COV-017 / M2-WS04-E01-F01 / M2-WS08-E01-F02 and original Phase1 Income snapshot P1-19 retain meaningful period/source presentation beyond coverage counts. T1-75 adds a read-only, side-by-side view of two explicitly selected saved income records in Financial information > Income. It is a bounded record comparison, not the full monthly/household snapshot.

The customer chooses exact record IDs and saved revisions. Eligibility conservatively requires distinct records and distinct source financial fact identities, matching currency and payer/type text (case insensitive), equal inclusive reporting-period day counts and chronologically ordered non-overlapping periods. Source versions of the same financial fact cannot masquerade as independent periods. This eligibility is specific to this view, not a general financial-comparability or calendar-month policy. Unequal-length calendar-month comparison remains open.

The two original period totals, dates, frequency and financial-year labels remain unchanged. Frequency never converts/annualises amounts, and year labels establish no calendar membership. There is no sum, delta, percentage, rate, currency conversion, household total, verification, income-stability score or financial arithmetic. Financial Rules retains ownership of derived calculations. A record correction is never treated as a separate income period.

## Owner, API and security

Financial Profile owns POST /local/v1/customers/{customerId}/income-records/comparison. Customer POST /product-api/v1/income-records/comparison resolves current session, active exact owned customer/ready workspace and matching Self financial-read/document-read authority. Update access is not needed; the POST carries read-query selections and creates no receipt, record, revision or state.

Operator customer-to-tenant binding precedes PDP evaluation. Existing origin/no-store, strict bounded unknown/missing/duplicate/null JSON and identifier/version checks remain. Caller amounts/customer/source metadata are rejected. The owner reads exact immutable versions from tenant/customer scope and validates source/status/period eligibility. A selected historical pair stays stable after later corrections.

Gateway validates complete owner record bindings, requested IDs/revisions, CUSTOMER_REVIEWED status, distinct sources, payer/type/currency and period-day/order eligibility. It freshly rereads both exact selected versions, compares the complete records and reconfirms authority/context before returning the view. Unavailable/foreign versions fail closed. Existing create/correction operations, persistence, fingerprints and history semantics are unchanged. No migration, service or package is added.

## Customer experience

Two explicit choices are required; no comparison is requested on load. The result shows separate saved totals and dates, payer/type, currency, reported frequency/year, saved revision/review time, source revision and source-history links. Missing/incompatible selections have actionable messages. The200-record choice bound stays explicit and does not imply completeness. Read-only customers can compare; document-access loss hides the containing view.

Changing a selection clears/aborts its pending result. API/session changes abort prior requests and hide prior results. Ignored cancellation cannot restore old results. A refreshed record version invalidates the old selection until the customer chooses again. Unsupported review status, wrong returned revisions and invalid day-count responses remain unavailable.

## Verification

494 Financial Profile checks pass, including39 new comparison cases. Actual owner/Gateway HTTP uses explicit current-session/customer/PDP doubles and in-memory persistence. Coverage includes exact historical stability after corrections, unchanged original values, explicit zero/large cents, same-record/source rejection, mismatched currency/payer/type, overlapping/reversed/unequal periods, invalid/missing/foreign versions, current/read-only authority, reconfirmation, strict ingress and forged comparison/fresh-readback rejection.

53 affected UI checks pass:13 Comparison,23 Income records and17 Financial information. Comparison cases cover explicit choices, source-linked saved values, duplicate/missing selections,400/403/404/503 failures, refreshed versions, session/selection cancellation, malformed status/revision/day counts, bounded choices and read-only integration. TypeScript, changed source/test ESLint, Vite and zero-warning Gateway/physical persistence test-project builds pass. Physical database/integrated comparison UAT is NOT RUN.

Unchanged T1-69 VS02/real OCR573, reprocessing40, Reporting64, Search27 and Documents111; T1-70 Loan22/Investment20 UI and T1-74 Expense23 UI evidence are carried forward, not rerun or counted as new passes. All prior Financial Profile owner checks are rerun within494. Physical PostgreSQL/live scanner/durable processing/Compose/full authenticated integrated browser UAT remain NOT RUN. This read view is not central Audit delivery or deterministic-calculation acceptance.

## Remaining scope

Full monthly/household income snapshot, multi-source/duplicate/overlap reconciliation, completeness and fiscal-calendar normalization, payer verification, Financial Rules aggregates/changes/ratios, expense comparisons, complete financial truth, central Audit and physical/full UAT remain open. No source-phase, acceptance, stage-gate or production-deployment advancement.

After paired merge:75 numbered units =74 implementations +1 governance closure. Original29 journeys remain0 complete/17 partial/12 pending; Income and its snapshot stay Partial. Original151 IDs/81 features/126 Phase1 memberships/source phases/acceptance remain. Tracker version17 through T1-74 reviewed before work.
