# T1-32 — Investment Source Figures in Customer Basic Reports

**Status:** SOURCE IMPLEMENTATION CANDIDATE; not end-to-end accepted.
**Application base:** `8d7ffdc7910beb29ff0cebb07b7e571894a938fc` (T1-31 merged Search).
**Requirements:** Master §§13,20,30,41; AR-003 Financial Passport; canonical WS08 Reporting/F01/F03 partial.

## Business result
Authorized Customer basic Report lists individually trusted Income, Expense, Loan, Asset and Investment **source facts** from Financial Profile. Existing Reporting reads CID-030 current customer owner state and exact CID-033 fact provenance, constructs existing Reporting-owned immutable source-linked items and exports the exact JSON payload. Its React page links each item to `/finances?fact={financialFactId}`.

The UI explicitly says that source-stated Investment amounts are **not** current securities holdings, NAV, current market values or portfolio returns.

## Invariants
- Existing `reporting.report.generate` / read authorization, tenant/customer/actor scope, Financial Profile authority, Evidence source references and audit semantics remain unchanged.
- No Investment account model, positions, unit amounts, cost basis, security identifier, real-time price feed, accounting period aggregation, currency conversion, net worth, score, AI advice, loan eligibility or professional investment recommendation.
- Unsupported INSURANCE facts remain excluded.
- No new table, API endpoint, contract, role, provider or external data source.

## Essential focused tests; source only, not executed
```powershell
dotnet test tests/reporting/Monergy.Reporting.Tests --configuration Release --filter FullyQualifiedName~ProductFinancialProfileReportSourceReaderTests
pnpm --dir apps/customer-web exec vitest run tests/ProductReportsExperience.test.tsx
pnpm --dir apps/customer-web exec tsc --noEmit -p tsconfig.json
```
LOCAL owner-backed UAT: reviewed Investment document fact → Financial Profile → generate/reopen/export Basic Report → exact Investment fact source/history. Denied customer / profile unavailable / bad provenance must fail closed. Do not run GitHub Actions.

## Expert coupling
Paired T1-32 System Expert candidate must trace Reporting → Financial Profile CID-030/033 → Customer Reports / Financial fact history. Clearly mark SOURCE_MERGED_RUNTIME_UNVERIFIED until native owner tests and source-matched compiled Expert are verified.
