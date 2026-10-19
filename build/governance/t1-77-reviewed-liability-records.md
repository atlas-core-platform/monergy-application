# T1-77 — source-backed liability records

Status: SOURCE_CANDIDATE_NATIVE_VERIFIED. Application base `0ad3dcd18e7d9403d80fe7e67d8633ca332398b5`. No source-phase, acceptance, stage-gate or production-deployment advancement.

## Planned scope

Original Phase 1 P1-16 Liabilities, Master §§13/14/20/41, COV-019, MON-FIN-001/011/012/014 and M2-WS04-E01-F03 require authoritative source-linked liability records. This increment adds customer-reviewed liability create/list/detail in Financial information > Liabilities. The initial supported source is an exact owned LOAN financial fact revision. Income, expense, property and credit-card source types reject. No new canonical source type, provider connection or credit-card journey is enabled.

The customer explicitly enters a meaningful name, creditor, outstanding balance and balance-as-of date. Amounts retain cents, allow explicit zero and are bounded at 999999999999.99. Dates start in 1900 and cannot exceed the current financial date. The source supplies currency and exact fact/provenance/document/evidence references. A source figure never fills or determines the outstanding balance or its date. Text is trimmed and bounded. Status is fixed CUSTOMER_REVIEWED and cannot be supplied by the caller. The record does not verify debt, creditor ownership, repayment or whether a loan is closed.

Loan and liability records can describe the same debt. There is no automatic conversion, reconciliation, deduplication, combined liability total, snapshot, net worth or financial arithmetic. Financial Rules remains the calculation owner. Full liability lifecycle and broader sources remain open.

## Owner, persistence and APIs

Financial Profile owns append-only liability records. GET/POST /local/v1/customers/{customerId}/liability-records and GET /local/v1/customers/{customerId}/liability-records/{liabilityRecordId} use governed operator customer-to-tenant bindings before matching current Self financial-read/document-read authority. Creation also requires financial-profile update authority. No caller customer/tenant/currency/status/provenance is accepted.

Customer GET/POST /product-api/v1/liability-records resolves current session, active exact owned customer and ready workspace. Gateway retains origin/no-store, strict bounded JSON, missing/unknown/duplicate/null rejection and dependency failure handling. Creation binds every reviewed field, operation, actor and customer, freshly reads the exact saved record and requires complete equality before reconfirming current authority/context. Foreign/forged responses fail closed. Lists are owner/customer bound and capped at 200 with explicit hasMore.

Stable tenant/customer/actor/operation identity and a canonical normalized input fingerprint make creation replay safe. Concurrent identical requests return one original record; competing payloads with one operation fail409. Replay preserves the original timestamp, source revision and reviewed amount after later source revisions. Cross-tenant/customer/actor operations do not expose another record.

Migration 0011_reviewed_liability_records.sql creates the tenant/customer-keyed relational record/creation receipt, exact source-revision foreign key, actor-operation uniqueness and customer-order index. Runtime grants are SELECT/INSERT only; UPDATE/DELETE are revoked. PostgreSQL creation uses a transaction and advisory lock, exact customer/source-kind insertion and original receipt replay. No package or service is added. Central Audit delivery is not claimed.

## Customer experience

The Liabilities navigation and direct route open saved records with balances, dates, creditor, currency, customer-review status and exact source-history links. The drawer requires explicit source and field choices. Source figures remain separate and never prefill balances. Empty/limited lists, unavailable sources, access loss, invalid inputs and uncertain saves have explicit states. A lost save response retries the same immutable operation/payload; editable400 failures can be corrected. A pending confirmation can be reopened. API/session/capability changes hide old data and ignore late responses. Read-only customers can inspect; document access is required and no unsupported source enables creation. Loan and liability records are explicitly not added together.

## Verification

594 Financial Profile checks pass, including 60 new liability cases. Coverage includes validation/cent and date boundaries, exact historical source/currency, unchanged source amounts, explicit zero, replay/conflicting/concurrent requests, wrong/missing/foreign source, tenant/customer/actor scope, current Self/document/read/update authority, operator binding, strict JSON ingress, forged owner responses/fresh readback and bounded lists. Native HTTP uses actual owner/Gateway routes with explicit current-session/customer/PDP doubles and in-memory persistence.

35 affected UI checks pass: 15 Liability records and 20 Financial information. Coverage includes explicit non-prefilled fields/source revision, source-linked details, zero and invalid fields, uncertain save replay, reopening pending confirmation, failures, limited/empty lists, read-only/document access, session/capability changes and stale responses. Three parent cases exercise the liability direct route, document-access guard and real navigation using loan sources. TypeScript, changed source/test ESLint, Vite and zero-warning Gateway/physical test-project builds pass.

The opt-in physical suite gains liability restart/read/replay/conflict/tenant-scope/runtime-grant coverage and compiles. Physical PostgreSQL, migration execution, live scanner/Compose and full authenticated integrated browser UAT are NOT RUN.

Unchanged T1-69 VS02/real OCR573, reprocessing40, Reporting64, Search27 and Documents111; T1-70 Loan22/Investment20 UI, T1-75 Income23/IncomeComparison13 UI and T1-76 Expense23/ExpenseComparison13 UI evidence are carried forward, not rerun or counted as new passes. Prior Financial Profile owner cases are rerun within594.

## Tracker and remaining scope

After paired merge: 77 numbered units = 76 implementations +1 governance closure. Original P1-16 moves Pending to Partial for the delivered source-backed create/read subset. Original29 journeys become0 complete/18 partial/11 pending. Liability snapshot P1-21 and Net worth P1-18 remain Pending. Original151 IDs/81 features/126 Phase1 memberships/source phases/business acceptance remain unchanged. Tracker version19 through T1-76 and clean merged main sources were reviewed before work.

Broader liability sources/types, corrections/history/lifecycle, multi-source/loan reconciliation, creditor verification, meaningful liability snapshot/completeness, totals/net worth, Financial Rules calculations, Search/Reports integration, central Audit and physical/full UAT remain open. No complete liability truth or financial product acceptance is claimed.
