# T1-31 — Authorized Search for Reviewed Investment Source Figures

**State:** source implementation candidate; no native runtime/UAT acceptance yet.
**Base Application:** `7b7b47b743bfe4aec6e7398a0f9aa6c1bf8d5840` (merged T1-30 Investment source facts).

## User outcome
Customer searches own current trusted Financial information using lexical Search and sees individually reviewed `INVESTMENT` source facts. The result links to the exact Financial Profile fact and Evidence-derived provenance at `/finances?fact={financialFactId}`. The description says **Investment source figure (not live market value)**.

## Owner and security contracts
- Search & Retrieval continues to own only `DERIVED_REBUILDABLE` representations. Financial Profile remains owner of trusted fact and CID-033 provenance; Evidence owns originating source version.
- Existing Search `search.query.execute` authorization, tenant/customer/session proof, CID-030 and CID-033 response validation, and lexical-only behavior remain unchanged.
- Included categories: INCOME, EXPENSE, LOAN, ASSET, INVESTMENT. Unsupported INSURANCE source facts remain excluded.
- No investment account synchronization, brokerage connection, tax lots, security identifiers, unit quantities, NAV, live price, portfolio returns, advice, calculated investment value, net worth or cross-currency aggregate.

## Focused local tests required — not executed here
```powershell
dotnet test tests/search-retrieval/Monergy.SearchRetrieval.Tests --configuration Release --filter FullyQualifiedName~ProductSearchProjectionSourceTests
pnpm --dir apps/customer-web exec vitest run tests/ProductSearchExperience.test.tsx
pnpm --dir apps/customer-web exec tsc --noEmit -p tsconfig.json
```
The Search owner regression tests also correct previously contradictory simultaneous counts in `RefreshingOneSearchFamilyPreservesTheOtherCurrentFamily` (formerly 3 and 5 for the same result). This correction must be verified in the focused tests above, not assumed green from source inspection.
LOCAL owner-backed UAT requires reviewed Investment source fact → Financial Profile → authorized Search → exact source and history, with denied actor/customer negative cases. No GitHub Actions during development.

## System Expert
Synchronize Search & Retrieval → Financial Profile CID-030/033 and customer Search → fact/history with exact source SHA, source-merged status, original requirement trace and non-live-market-value qualification. Historical accepted T1 deltas must not be overwritten by source-level evidence.
