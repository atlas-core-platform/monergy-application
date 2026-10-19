# T1-85 — Reviewed insurance policy records

Application base `02d6460f671e3a55fdce3cc2c1226f8685a7b880`;Architecture `90934b51b2b203795acf95da2252a2cf1a1d1f4c`.

## Planned scope and meaning

Master sections13/20/42,COV-021,MON-FIN-008/011/012/014 and M2-WS04-E02-F02 require insurance provider,policy type,sum assured,premium and maturity. This increment implements customer-reviewed policy name,provider,type,separate sum assured/premium amount,policy information date and nullable maturity/expiry,with create/list/read,version-checked corrections,exact version reads and paged immutable history. MON-FIN-008 remains original source phase2;no business acceptance or stage-gate advancement.

The record references an exact owned canonical INSURANCE_POLICY source revision. Currency,original source/provenance and evidence/document identity remain immutable through corrections;the product source facade is INSURANCE. Source amounts never fill either reviewed value. Both amounts require explicit customer input,permit zero,and enforce nonnegative bounded two-decimal precision. Provider/name/type are bounded120 without control characters. Information date uses the existing1900-through-current financial-date envelope. Maturity/expiry is explicitly nullable;past or future dates from1900 are allowed,and it can be added or cleared. Maturity is not coverage status. No active-cover/provider confirmation,premium cadence/payment,benefit eligibility,coverage advice or protection adequacy is inferred. Policy sums assured are not asset valuations and do not enter the dated asset snapshot or net worth.

## Owner,API and storage

Financial Profile owns GET/POST /local/v1/customers/{customerId}/insurance-records,GET by ID/exact revision,POST by ID/corrections and GET by ID/history. Product /product-api/v1/insurance-records exposes the matching owned-customer surface. Original creation receipts and fingerprints remain exact under replay after later corrections. Scoped operation identities,payload fingerprints,expected revisions and transaction/advisory locks prevent reuse,changed payloads and competing edits. Lists return latest versions bounded200;history returns50 contiguous descending versions with older cursor and exact version1 tail.

Migrations0017_reviewed_insurance_records.sql and0018_reviewed_insurance_corrections.sql add owner-local source-linked creation rows and immutable correction rows. Exact source type/customer/revision is rechecked on INSERT with a source-revision foreign key. Runtime SELECT/INSERT only;UPDATE/DELETE revoked. No package,new service,provider,Actions or central Audit delivery.

Current operator customer-to-tenant binding,owned-customer/session and matching Self profile.read/document.read authority are required;writes also require profile.update. Strict32KiB ingress rejects unknown,duplicate,missing,invalid or inappropriate-null fields. Nullable maturity must still be explicit in requests. Gateway confirmation binds complete normalized terms including maturity,expected saved version,operation/actor/customer and original source/currency/evidence metadata,then exact fresh saved-version readback and current authority/context reconfirmation. Lists enforce unique bounded customer records. History binds every item to original source metadata and validates contiguous versions,bounds,cursor and version1 tail. Read-only source/history access remains allowed.

## Customer experience

Financial information > Insurance offers reviewed policy creation,details,corrections and history. Policy values and dates stay explicit;blank maturity serializes null and opens as blank during editing. Exact saved values and original source/history remain accessible without current source figures. Stale409 requires refreshed review;confirmed400 remains editable;uncertain saves retain operation,payload and expected revision across retry/reopen. Current API/session/capability and ignored-cancellation guards protect lists,saves and history. The UI states that active cover,premium frequency/payment and benefits are unverified. No new professional/provider workflow or automatic policy status is presented.

## Verification

1091 Financial Profile checks pass:all971 previous cases retained/rerun plus120 new insurance cases. Actual owner/Gateway HTTP uses explicit authority doubles and in-memory owner persistence. Cases cover canonical source/category/customer boundaries,exact source versions,explicit separate/zero values,precision/date/text bounds,replay/conflicts/concurrency,current/read-only/document authority,strict ingress,forged receipts/fresh readback,history paging/source binding and malformed lists. Maturity-specific checks cover null/future/past terms,add/clear/replay across later revisions,pre1900 rejection,required nullable field presence,and altered creation/correction maturity receipts.

50 affected UI checks pass:28 Insurance policies and22 Financial Information,including the hosted Insurance category/source choice with both policy amounts initially blank. TypeScript,changed-source/test ESLint,Vite and zero-warning Gateway/physical test-project builds pass. Opt-in physical insurance restart/source/replay/history/conflict/runtime mutation-denial regression added and compiled only. Physical migration/PostgreSQL/query/grant execution,live scanner/Compose and full authenticated integrated browser UAT NOT RUN.

Unchanged T1-69 VS02/real OCR573,reprocessing40,Reporting64,Search27,Documents111;T1-70 Loan22 UI;T1-75 Income23/IncomeComparison13 UI;T1-76 Expense23/ExpenseComparison13 UI;T1-79 Liability23/Snapshot18 UI;T1-84 AssetSnapshot24/Investment20/Property25/Vehicle27 UI are carried forward,not rerun or counted as fresh passes. Parent Financial Information22 is rerun within50.

## Tracker and remaining work

After paired merges:85 numbered units=84 implementations+1 governance closure. Original29 journeys remain0 complete/19 partial/10 pending;source extraction and financial-data foundations remain Partial. Original151 IDs,81 features,126 Phase1 memberships,source phases and business acceptance retained. Business acceptance remains T1-06;source runtime remains unverified.

Insurance verification,coverage/benefit interpretation,premium cadence/payment/lifecycle,professional advice and full protection position remain open. Broader financial records,reconciliation,Financial Rules calculations,Search/Reports record integration,central Audit and physical/full UAT remain open. No production deployment.
