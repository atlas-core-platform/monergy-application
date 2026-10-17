# T1-24 — Authorized Search of Loan Source Facts

**Status:** implementation source candidate, not accepted. Application base `f9c8f1afef9dd89cb2c1582ac9672b5c163b4b18` (T1-23 merged source). Original Master §§13, 15, 20, 41; AR-003 Financial Passport; canonical M2-WS07-E01-F02 / E02-F01 / E02-F03 and partial M2-WS04-E01-F03.

## Business outcome
After a customer explicitly reviews and confirms a structured-text LOAN fact into Financial Profile, the existing authorized financial Search can find its label, exact source amount and effective date. Search results link to `/finances?fact={financialFactId}` for source-and-history review.

## Owner-preserving implementation
- Search & Retrieval product projection now admits authoritative `LOAN` alongside `INCOME` and `EXPENSE`.
- Financial Profile remains sole authority; Search re-fetches profile and exact provenance via CID-030 and CID-033 on the current authorized customer request and constructs rebuildable derived search records only.
- Loan result explicitly states **Loan source amount (not outstanding balance)**; no EMI, approved loan balance, debt computation, repayment schedule, score or eligibility is inferred.
- Customer Search explanatory text reflects this supported category. No new permission, table, endpoint, provider, client-side financial calculation or document processing type.
- Unsupported investment categories remain excluded from the product projection. Results remain lexical-only and current-customer-only.

## Focused evidence to execute
```powershell
dotnet test tests/search-retrieval/Monergy.SearchRetrieval.Tests --configuration Release --filter FullyQualifiedName~ProductSearchProjectionSourceTests
pnpm --dir apps/customer-web exec vitest run tests/ProductSearchExperience.test.tsx
pnpm --dir apps/customer-web exec tsc --noEmit -p tsconfig.json
```

Also verify one LOCAL/UAT owner-backed Loan fact from Documents → Financial information → Search → exact provenance, and negative customer/permission cases; do not manually dispatch GitHub Actions. Source inspection and authored tests do not count as passing those commands.

## Mandatory Expert sync
Companion `monergy-system-expert` branch `work/t1-24-loan-search-sync`. Its candidate status is not accepted. Synchronize the Search → Financial Profile CID-030/033 flow, customer Search/Passport link, exact source SHA and no-claims. Full source-matched Expert verification remains required for functional completion.

**No full business Feature, Phase 1 MVP, live integrations, scores or Production/UAT release gate is closed by this narrow candidate.**
