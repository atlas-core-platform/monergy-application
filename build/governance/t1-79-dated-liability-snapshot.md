# T1-79 — dated liability snapshot

Status: SOURCE_CANDIDATE_NATIVE_VERIFIED. Application base `e3ff9e55aa6149005e62961acf8d63f2e1c7009c`. Source phases, business acceptance and stage gates remain unchanged.

## Planned scope and meaning

Original Phase1 P1-21 Liabilities snapshot, Master §§20/41, COV-019, MON-DAS-001/004, MON-FIN-011/012/014, M2-WS04-E01-F03 and M2-WS08-E01-F02 require meaningful dated liability presentation beyond source counts. This increment adds a read-only snapshot of the latest reviewed liability record versions matching an explicitly chosen exact balance-as-of date and currency. It is a bounded dated-record view and does not establish a complete liability position.

The owner selects each record's latest immutable version first, then filters the complete owned record set by exact date and currency before applying the200-record view limit. Matching records outside the ordinary200-record list remain discoverable. Later corrections can change which records match. Original saved versions remain in history. This is current reviewed knowledge for a stated balance date, not reconstruction of what the system knew at a historical time.

Original balance, creditor, date, currency, saved revision/review time and source references stay visible. No older balance is carried forward, currency converted, source amount inferred, repeated record/source merged, total computed or financial arithmetic performed. Financial Rules remains calculation owner. Missing records, other dates/currencies, unverified ownership/repayment and overlap between loan and liability records remain open. No exact-date financial-comparability or completeness policy is established.

## Owner, API and storage

Financial Profile owns GET /local/v1/customers/{customerId}/liability-records/snapshot?asOfDate=YYYY-MM-DD&currency=XXX. Customer GET /product-api/v1/liability-records/snapshot uses matching query semantics. Only one canonical date and one three-letter uppercase currency are accepted; missing/unknown/duplicate/malformed/future/too-old selections reject. Dates begin in1900 and follow the existing current financial-date envelope.

In-memory filtering reads latest versions under one lock. PostgreSQL uses one statement over exact tenant/customer creation rows plus latest revisions, then applies date/currency predicates and order-by-original-creation/record-ID limit201. The response returns up to200 unchanged reviewed records and explicit hasMore. It does not use a previously capped list as its input. A false hasMore does not establish completeness of customer liabilities.

Existing operator customer-to-tenant binding, current Self financial-profile read/document-read authority, current session, active exact owned customer and ready workspace remain required. Update authority is not needed. Gateway binds returned selection, complete record/customer/status/field metadata, exact item date/currency, unique record IDs, item count and hasMore semantics, then reconfirms authority/context. Unsupported or mismatched responses fail closed. Origin/no-store protections remain. This snapshot trusts the authoritative owner query; it does not claim per-record fresh readback or distributed historical reconstruction.

The query creates no record, revision, receipt or state. Existing create/correction/history fingerprints, replay and version semantics remain unchanged. No migration, package, service, new source type or central Audit delivery is added. Existing0011/0012 tables are reused.

## Customer experience

Financial information > Liabilities offers Dated liability snapshot with explicit date/currency selection and a View action. Nothing is requested on load. Each matching balance retains customer-review status, creditor, as-of date, saved/source revisions, review time and source-history link. The first200 matching-record bound is explicit when more exist. Empty matches explicitly do not mean zero debt. Repeated source identities trigger an overlap warning while preserving separate reviewed balances.

Coverage text explains missing debts, other dates/currencies and loan/liability overlap. The view shows no combined debt total. Filter changes clear and abort pending work. API/session changes hide and abort old responses, including ignored cancellation. Refreshing saved record versions invalidates a displayed or pending snapshot until explicitly requested again. Read-only customers may use it; losing document access removes the containing view.

## Verification

686 Financial Profile checks pass, including47 new snapshot cases. Test inventory comparison retains all639 prior cases. Coverage includes latest-version date movement after correction, unchanged exact originals/source amounts, no carry-forward, zero/large cents, full-set filtering before the cap, explicit200/hasMore, repeated sources retained, separate currencies without conversion, tenant/customer scope, invalid selections, strict owner/product query ingress, current/read-only authority, authority reconfirmation and mismatched/unsupported owner-response rejection. Actual owner/Gateway HTTP uses explicit current-session/customer/PDP doubles and in-memory persistence.

61 affected UI checks pass:18 Snapshot,23 Liability records and20 Financial information. Snapshot cases cover explicit requests, unchanged source-linked values, coverage/empty/overlap/bounded states,400/403/503 errors, filter/API cancellation, saved-version refresh invalidation, malformed selection/status/revision/duplicate/hasMore responses and read-only/document-access integration. TypeScript, changed source/test ESLint, Vite and zero-warning Gateway/physical test-project builds pass.

The opt-in physical liability restart/history test now also asserts latest-version snapshot and foreign-tenant exclusion. It compiles. Physical PostgreSQL/query execution, live scanner/Compose and full authenticated integrated browser UAT are NOT RUN.

Unchanged T1-69 VS02/real OCR573,reprocessing40,Reporting64,Search27,Documents111;T1-70 Loan22/Investment20 UI;T1-75 Income23/IncomeComparison13 UI andT1-76 Expense23/ExpenseComparison13 UI evidence are carried forward,not rerun or counted as new passes. T1-78 Liability23 and Financial information20 UI are rerun. Prior Financial Profile owner cases rerun within686.

## Tracker and remaining scope

After paired merge:79 numbered units =78 implementations +1 governance closure. P1-21 liability snapshot moves Pending to Partial for the dated-record subset. Original29 journeys become0 complete/19 partial/10 pending. P1-16 Liabilities remains Partial; P1-18 Net worth remains Pending. Original151 IDs/81 features/126 Phase1 memberships/source phases/business acceptance remain unchanged. Tracker version21 and clean merged main sources were reviewed before work.

Full liability-position completeness, broader sources/types/lifecycle, loan/multi-source reconciliation, creditor verification, valuation/date normalization, totals/net worth, Financial Rules calculations, Search/Reports integration, central Audit and physical/full UAT remain open. No production deployment or acceptance advancement.
