# T1-84 — Dated asset snapshot

Application base `fa39349a1662eacb49ba714792541b3b02fa9992`; Architecture `90934b51b2b203795acf95da2252a2cf1a1d1f4c`.

## Planned scope and meaning

Original P1-22 Assets snapshot, Master sections13/14/20/41,COV-022/025,MON-DAS-001/004,MON-FIN-011/012/014 and M2-WS08-E01-F02 require dated source-connected asset presentation. This increment adds a read-only view of latest customer-reviewed Investment,Property and Vehicle record versions matching one explicit exact value date and three-letter uppercase currency.

Each kind selects latest saved versions across its full owned record set,then filters by exact date/currency before applying its own200-record limit (201st match establishes hasMore). Matching records outside the ordinary capped list remain discoverable. Latest corrections may move records into or out of the selection;exact originals and history remain untouched. Values and references are unchanged,including zero values and nullable registration. There is no inferred source value,carry-forward,currency conversion,deduplication,financial arithmetic,total assets or net worth. Financial Rules retains calculation ownership.

Investment,Property and Vehicle collections are independent owner-local reads,with explicit separate bounds and coverage. They are current reviewed knowledge for a stated value date,not an atomic cross-kind snapshot or reconstruction of what the system knew at a historical time. An empty result does not mean zero assets;hasMore=false does not establish completeness. Broader asset kinds,other dates/currencies,ownership,independent valuation,overlap and reconciliation remain open. Repeated source identities remain separate and trigger a UI overlap warning.

## Owner,API and customer experience

Financial Profile owns GET /local/v1/customers/{customerId}/asset-records/snapshot?asOfDate=YYYY-MM-DD&currency=XXX. Product GET /product-api/v1/asset-records/snapshot uses the same strict selection. Missing,unknown,duplicate,noncanonical,too-old,future or malformed query values reject. Current customer-to-tenant binding,owned-customer session,matching Self profile.read/document.read authority and fresh authority/context reconfirmation are required. Update access is not required. Origin/no-store protections remain.

The Gateway validates all three required collections,selection,complete domain metadata,customer identity,exact date/currency,positive saved/source revisions,unique record IDs,200-item limits and hasMore semantics. A malformed collection fails the response;no partial position is confirmed. It trusts the authoritative owner query and does not claim per-record fresh readback or distributed snapshot consistency. Existing creation/correction/history fingerprints and receipts remain unchanged. No migration,new service/package/provider/Actions or central Audit delivery.

Financial information > Assets offers Dated asset snapshot with explicit value date/currency and View action;no snapshot request runs on load. Cards retain separate purchase/current values,domain details,saved/source revisions,review time and exact source-history links. Filters clear/abort pending work. API/session or saved-record/source refresh changes hide/abort results,including ignored cancellation. Existing Investment/Property/Vehicle list loads and saves notify the parent to invalidate a snapshot. Document-access loss removes the view;read-only customers can use it.

## Verification

971 Financial Profile checks pass:all916 previous cases retained/rerun plus55 new snapshot cases. Tests cover each of the three repository kinds:latest-version date movement,original/source preservation,full-set filtering before cap,200/hasMore,repeated sources,separate currencies,zero/large cents and tenant/customer isolation. Actual owner/Gateway HTTP with authority doubles/in-memory persistence covers strict query ingress,current/read-only/document authority,reconfirmation and mismatched/unsupported collection responses.

118 affected UI checks pass:24 Asset snapshot,20 Investment,25 Property,27 Vehicle and22 Financial Information. The final24 snapshot checks also pass after test-only lint cleanup;these overlap and are not added to118. TypeScript,changed source/test ESLint,Vite and zero-warning Gateway/physical test-project builds pass. Each physical Investment/Property/Vehicle restart regression now asserts latest-version snapshot and foreign-tenant exclusion;compiled only. Physical PostgreSQL/query/grant execution,live scanner/Compose and full authenticated integrated browser UAT NOT RUN.

Unchanged T1-69 VS02/real OCR573,reprocessing40,Reporting64,Search27,Documents111;T1-70 Loan22 UI;T1-75 Income23/IncomeComparison13 UI;T1-76 Expense23/ExpenseComparison13 UI;T1-79 Liability23/Snapshot18 UI are carried forward,not rerun or counted as fresh passes. Investment20 and Property25 UI are now rerun within118.

## Tracker and remaining work

After paired merges:84 numbered units=83 implementations+1 governance closure. Original29 journeys remain0 complete/19 partial/10 pending. P1-15 Assets and P1-22 Assets snapshot stay Partial. Original151 IDs,81 features,126 Phase1 memberships,source phases,business acceptance and stage gates remain unchanged. Business acceptance remains T1-06;source runtime remains unverified.

Full comparable asset position,broader types and lifecycle,ownership/valuation verification,liability links,multi-source reconciliation,Financial Rules totals/net worth,Search/Reports record integration,central Audit and physical/full UAT remain open. No production deployment or acceptance advancement.
