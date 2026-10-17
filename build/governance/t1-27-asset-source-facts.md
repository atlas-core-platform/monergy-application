# T1-27 — Customer-reviewed Asset Source Facts

**Source implementation candidate, runtime acceptance pending.** Application baseline `3b141c1894d5844cbbc6f6690958a902b2fe3ccd` (T1-26).

## Business value
A customer can add a supported structured-text document containing an `ASSET|label|numeric value|currency|yyyy-MM-dd|source location|confidence` record, review that non-authoritative candidate and explicitly confirm it. Financial Profile owns the resulting ASSET source figure, exact Evidence document version, fact/provenance history and existing Audit path. The Financial Passport has a bounded **Asset source figures** category and a fourth supported-type coverage indicator.

## Owner and security preservation
- Evidence still owns document/version; Document Intelligence creates only extracted candidate records.
- Financial Profile existing normalize-from-processing endpoint accepts reviewed ASSET under current tenant/customer/session and permission checks, with existing idempotency/authority/version conflict handling.
- Product summary and category details show actual reported figures only; no total asset valuation, net worth, lender assessment, professional attestation, amortization, or future asset classes.
- No new table, schema, permission, API contract or provider is required.

## Required focused verification (not executed)
```powershell
dotnet test tests/vs02/Monergy.Vs02.Tests --configuration Release --filter FullyQualifiedName~StructuredTextLoanAmountRemainsNonAuthoritativeCandidateWithExactEvidence
pnpm --dir apps/customer-web exec vitest run tests/FinancialProfileExperience.test.tsx tests/WorkspaceHome.test.tsx tests/EvidenceExperience.test.tsx
pnpm --dir apps/customer-web exec tsc --noEmit -p tsconfig.json
```
Local owner-backed UAT: Document → ASSET candidate → explicit promotion → Financial Passport Asset category → exact source revision, plus negative tenant and permission boundaries. Native test and UAT evidence must be distinguished from source review. No GitHub Actions.

## Explicit limitations
A source-stated Asset figure is **not** verified current market value. No deterministic net worth is calculated. No property-valuer workflow, IVSC certification, insurance replacement value, depreciation, currency conversion, income or liquidity recommendation. Formal acceptance remains pending.

## Required System Expert
Paired `monergy-system-expert` delta records Document Intelligence/Evidence → Financial Profile current authorized ASSET fact → Passport category and exact provenance; all behavior stays source-candidate until source-matched tests and compiled-model checks pass.
