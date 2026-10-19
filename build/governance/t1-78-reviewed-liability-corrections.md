# T1-78 — liability corrections and history

Status: SOURCE_CANDIDATE_NATIVE_VERIFIED. Application base `3b61877ebf1259445b795938e22b2e209382c9bd`. Original source phases, business acceptance and stage gates remain unchanged.

## Planned scope

Original Phase1 P1-16 Liabilities, Master §§13/14/20/41, COV-019, MON-FIN-001/011/012/014 and M2-WS04-E01-F03 retain correction/history gaps after T1-77 create/read. This increment adds version-checked edits, immutable saved history and exact-version reads for reviewed liabilities. It does not add liability lifecycle status, reconciliation, totals or a liability snapshot.

Customers may correct the name, creditor, outstanding balance and balance-as-of date. Existing cents/range/date validation applies. The exact original LOAN source fact/revision, provenance, document/evidence, currency and CUSTOMER_REVIEWED status remain immutable. Corrections never rewrite the source figure or any earlier saved version. Revision order represents review history; a corrected as-of date may be earlier and does not assert a newer verified balance.

## Owner, API and storage

Financial Profile adds POST /local/v1/customers/{customerId}/liability-records/{liabilityRecordId}/corrections and GET /local/v1/customers/{customerId}/liability-records/{liabilityRecordId}/history?beforeRevision=... . Existing exact-owner reads accept an optional positive revision. Customer product routes expose matching corrections/history beneath /product-api/v1/liability-records/{liabilityRecordId}. Lists return latest saved versions, bounded at200 by original creation order. History returns up to50 descending contiguous versions with an explicit older-page cursor.

Creation DTO/fingerprint/identity remain unchanged. Response revision defaults to1, preserving older creation payloads without a revision property. Creation replay returns the original version even after corrections. Corrections carry a fresh operation ID, expected revision and complete reviewed draft. Same-operation/same-payload retries replay the original correction receipt after newer versions. Changed payload/version or cross-record/creation reuse of an actor operation fails409. A different stale operation cannot overwrite a newer saved version. Invalid/max expected revisions fail before writes.

Migration0012_reviewed_liability_corrections.sql appends immutable revisions referencing the original tenant/customer record, with record/revision and actor-operation uniqueness. Existing creation rows stay intact. PostgreSQL transaction/advisory locks bind operation and record, enforce expected revision and atomically append receipt/payload. Runtime grants remain SELECT/INSERT only; UPDATE/DELETE are revoked. In-memory behavior follows the same atomic replay/conflict rules. No package/service is added. Central Audit delivery is not claimed.

## Authority and binding

Existing operator customer-to-tenant binding precedes matching current Self financial-profile read/document read authority. Corrections also require update authority. Source replacement, caller status/currency/customer and unknown/missing/duplicate/null/oversized JSON reject. Gateway checks the original exact version, requested record/expected+1 revision, normalized complete reviewed fields, operation/actor/customer, and unchanged original source/currency/status metadata. It freshly rereads the exact saved correction, requires complete equality and reconfirms current authority/context before returning.

History requires read/document access without update authority. Owner/customer/record/status/metadata, page size, descending contiguous versions and cursor meaning are bound at Gateway. Foreign, missing, forged, stale and unsupported responses fail closed. Origin/no-store protections remain. Missing/invalid versions create no state.

## Customer experience

Existing Liabilities offers Edit and View history. Editing starts from an exact saved version, preserves its original source even if newer source figures exist, and sends the complete reviewed draft plus expected revision. A409 prompts review of the refreshed version rather than an automatic overwrite. A400 permits correction. An uncertain save retains and retries the same operation/payload, including when its pending drawer is reopened. Version numbers, balance/as-of date, creditor, review time, original currency/source revision and source-history links stay visible.

History supports older pages and exact-version details. API/session, selected record, read/document/update authority changes abort or hide stale work; late results cannot restore old data. Read-only customers can inspect history but cannot edit. Loan/liability duplicates are not reconciled or totaled, and history does not verify debt or repayment.

## Verification

639 Financial Profile checks pass, including45 new correction/history cases. Comparison against the T1-77 test inventory confirms all594 prior cases remain and pass. Coverage includes legacy version1 payloads, original creation replay after corrections, exact immutable versions, replay after newer versions, same-operation conflicts, stale/concurrent edits, source/status/currency retention, paged history, invalid/missing/foreign scopes, current/read-only authority, strict ingress and forged correction/fresh-readback rejection. Actual owner/Gateway HTTP uses explicit current-session/customer/PDP doubles and in-memory persistence.

43 affected UI checks pass:23 Liability records and20 Financial information. They cover explicit create/edit values, immutable historic sources, stale409 handling, uncertain-save replay/reopen, read-only history, exact-version details, older-page requests, failure/access/cancellation and parent navigation/document-access integration. TypeScript, changed source/test ESLint, Vite and zero-warning Gateway/physical persistence test-project builds pass.

The opt-in physical liability test now covers corrected restart reads, exact original reads, creation/correction replay, stale conflicts, history, tenant boundaries and runtime correction-table update denial. It compiles. Physical PostgreSQL/migration execution, live scanner/Compose and full authenticated integrated browser UAT are NOT RUN.

Unchanged T1-69 VS02/real OCR573, reprocessing40, Reporting64, Search27 and Documents111; T1-70 Loan22/Investment20 UI; T1-75 Income23/IncomeComparison13 UI and T1-76 Expense23/ExpenseComparison13 UI evidence are carried forward, not rerun or counted as new passes. T1-77 Liability15 UI is rerun within23. Prior Financial Profile owner checks rerun within639.

## Tracker and remaining scope

After paired merge:78 numbered units =77 implementations +1 governance closure. Original29 journeys remain0 complete/18 partial/11 pending. Liability records stay Partial; liability snapshot P1-21 and net worth P1-18 stay Pending. Original151 IDs/81 features/126 Phase1 memberships/source phases/business acceptance remain. Tracker version20 and clean paired merged main sources were reviewed before work.

Broader liability sources/types/lifecycle, loan/multi-source reconciliation, creditor verification, meaningful liability snapshot/completeness, totals/net worth, Financial Rules calculations, Search/Reports record integration, central Audit and physical/full UAT remain open. No production deployment or acceptance advancement.
