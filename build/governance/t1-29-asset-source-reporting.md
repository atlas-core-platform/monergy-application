# T1-29 — Asset Source Figures in Basic Customer Reports

**State:** SOURCE IMPLEMENTATION CANDIDATE; native runtime and owner-backed acceptance evidence pending.
**Baseline:** Application main `fbfc89d09bf54d61adc479c4b5680e6970e17f9d` (T1-28 / PR #76).
**Trace:** Master §§13, 20, 30, 41; AR-003 Customer Financial Passport; canonical M2-WS08-E02-F01/F03 partial; precondition T1-27 Asset Financial Profile source facts.

## Business outcome
A customer generates a basic, source-connected financial report from current owner-authoritative `INCOME`, `EXPENSE`, `LOAN` and now `ASSET` facts. A reported Asset figure appears as **one individual document-reported amount** with Financial Profile and Evidence provenance. The customer can follow its exact source/history link. Export remains the existing verbatim Reporting-owner JSON.

## Source and permission boundaries
- Reporting retains report identity; Financial Profile retains fact authority. Existing current customer/tenant/session permission for `reporting.report.generate`, Financial Profile CID-030 profile read, and CID-033 individual fact provenance are not bypassed or replaced.
- The report owner fails closed if a fact/provenance pair is mismatched, outside the authorized customer or unavailable. Derived customer UI never treats an absent source as a zero Asset total.
- No cross-currency summation, asset net-worth calculation, independent property valuation, collateral eligibility, new provider, professional attestation, new database object, API contract, role, or permission.
- Unsupported `INVESTMENT` facts still remain outside this bounded report.

## Focused verification — required; not executed
```powershell
dotnet test tests/reporting/Monergy.Reporting.Tests --configuration Release --filter FullyQualifiedName~ProductFinancialProfileReportSourceReaderTests
pnpm --dir apps/customer-web exec vitest run tests/ProductReportsExperience.test.tsx
pnpm --dir apps/customer-web exec tsc --noEmit -p tsconfig.json
```
A separate LOCAL owner-backed UAT must verify a reviewed ASSET document fact → authoritative Financial Profile → basic Report → exact source/history → JSON export. Recheck negative tenant/customer/permissions. No GitHub Actions during development.

## Expert synchronization
Paired `monergy-system-expert` source-candidate T1-29 must show Reporting → Financial Profile CID-030/CID-033 and customer Reports/Financial information navigation, exact source candidate SHA and **not a verified valuation or net-worth**. Do not call this entire report capability accepted from static source review.
