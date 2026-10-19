# T1-82 — Reviewed vehicle records

Bounded planned implementation under Master §§13/14/20,COV-022,MON-FIN-010/011/012/014,M2-WS04-E02-F02 and original P1-15 Assets/P1-22 asset-position foundations. Application base `d8da8dcc4150aae694c69df41eef3965f84ee96f`;Architecture `90934b51b2b203795acf95da2252a2cf1a1d1f4c` unchanged. No original source-phase,business-acceptance,stage-gate or deployment advancement.

## Delivered behavior

Customers can explicitly create,list and read reviewed vehicle records under Financial information > Assets. Required name and customer-described vehicle type are trimmed,bounded120 characters and reject control characters. Purchase/current values are entered separately,nonnegative,at most two decimals and999999999999999;zero is allowed without any ordering assumption. The explicit value-as-of date uses the existing financial-date envelope. The source amount never fills either value.

Registration reference is a nullable customer-reported value,trimmed to at most40 characters with no controls. Null,empty and whitespace-only input normalize to null and replay the same normalized operation. Formatting/case are preserved;there is no jurisdiction/format/registration lookup or verification. The UI permits an unknown reference and renders Not supplied,without implying vehicle registration or ownership.

Creation requires an exact owned canonical VEHICLE source revision. The product ASSET facade must carry explicit VEHICLE classification;Property,unclassified,wrong-category and foreign-customer sources do not qualify. Exact source/currency/provenance/evidence/document identity and review actor/time remain with the immutable record. New source revisions do not rewrite the record. Sources and customer-entered values stay separate.

Owner `POST/GET /local/v1/customers/{customerId}/vehicle-records` and exact ID reads have matching product routes under `/product-api/v1/vehicle-records`. Current Self profile/document read authority is required;creation additionally requires update authority. Operator customer-to-tenant binding,current session/owned customer and matching authority metadata apply. Strict32KiB unknown/duplicate/missing/null JSON rejection applies to required data;the registration value may explicitly be null. Same-origin and no-store protections remain. Product ingress binds complete normalized values,operation/actor/customer and source metadata,requires exact fresh saved readback and reconfirms current authority/context. Lists bind unique complete customer records and200/hasMore bound.

Deterministic tenant/customer/actor/operation identity plus normalized fingerprint produce replay-safe immutable creation receipts. Changed payload conflicts without overwrite. In-memory locking and PostgreSQL transactions/advisory locks preserve one receipt under concurrency. Migration `0015_reviewed_vehicle_records.sql` adds owner-local append-only storage,exact source revision foreign key,customer index and runtime SELECT/INSERT grants;UPDATE/DELETE revoked. Physical insert rechecks exact source type/customer/revision. No cross-service SQL.

The UI preserves uncertain operation/payload across retries and drawer close/reopen. Confirmed invalid400 permits editing;source changes require explicit reselection. Scope/capability changes and ignored cancellation cannot restore prior sensitive results. Read-only customers with source-document access can inspect saved details and exact source history. Vehicle and Property sections have distinct React identities;the parent integration test caught and resolved duplicate section identity during refresh.

## Verification

- **863 Financial Profile checks pass:66 new Vehicle cases,all797 previous cases retained and rerun**, verified by TRX inventory comparison.
- **39 affected UI checks pass:17 Vehicle records and22 Financial Information**. JSON report39 passed/0 failed. Parent coverage includes all21 previous cases plus Vehicle-only source selection;the existing Property integration remains. Registration-null creation/readback and no inferred source amounts are covered.
- Actual owner/Gateway HTTP uses current-session/customer/PDP doubles and in-memory owner. Exact historical sources,separate/zero values,precision/date validation,nullable registration normalization and40/41 boundary,replay/conflict/concurrency,tenant/customer/actor scope,strict ingress,current/read-only/document authority,forged receipts/fresh-readback and malformed bounded lists are covered.
- TypeScript,changed-source/test ESLint and Vite pass. Gateway and physical persistence test-project builds pass with zero warnings/errors.
- New physical restart/replay/conflict/isolation/runtime mutation-denial regression is **compiled only**. Physical migration/PostgreSQL/query/grant execution,live scanner/Compose and full integrated authenticated browser UAT are **NOT RUN**.
- Unchanged T1-69 VS02/real OCR573,reprocessing40,Reporting64,Search27,Documents111;T1-70 Loan22/Investment20 UI;T1-75 Income23/IncomeComparison13 UI;T1-76 Expense23/ExpenseComparison13 UI;T1-79 Liability23/Snapshot18 UI andT1-81 Property25 UI are carried forward,**not rerun or counted as new passes**. All21 previous parent UI cases rerun within22 here.

## Remaining and tracker treatment

These are customer-reviewed vehicle details and values. Registration,ownership,market value and insurance are not verified. Vehicle corrections/history,lifecycle,liability/loan links,broader asset kinds,reconciliation,complete asset position,asset snapshot,total assets,net worth and financial arithmetic remain open. Financial Rules retains calculation ownership. No Search/Reports record integration,central Audit delivery,provider,package,service or Actions workflow is added. Physical/full UAT remains open.

After paired merges: **82 numbered units =81 implementations plus1 governance closure**. Original29 journeys remain **0 complete/19 partial/10 pending**;Assets and asset snapshot remain **Partial**,with no original journey status change. All151 IDs,81 features,126 Phase1 memberships,source phases and business acceptance are preserved. Exact merge identities are recorded by the paired Expert receipt and tracker.
