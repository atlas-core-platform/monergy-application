# T1-25 — Trusted Loan Source Facts in Basic Customer Reports

**State:** SOURCE CANDIDATE, verification not performed. Based on Application main `396a3067bd27312ff24094e503d79ece8be347f9` (T1-24). Master §§13,20,30,41, D02 M2-WS08-E02-F01/F03 (partial), M2-WS04-E01-F03, AR-003 Financial Passport.

## Exact customer behavior
Customer with `reporting.report.generate` authorization creates a **basic source-connected summary** from current Financial Profile facts. The report now includes reviewed `LOAN` source amounts alongside Income and Expense items, each with Financial Profile fact/provenance and Evidence source references. The JSON export remains the exact generated basic report. The product page explicitly warns that Loan source amounts are not verified outstanding balances, repayments, EMI, eligibility or a net-worth calculation.

## Contract and authority
- Reporting owner reads current Financial Profile via CID-030 and each supported source via CID-033, under current tenant/customer/actor authorization.
- No new provider, table, migration, permission, contract or financial-calculation formula.
- Unsupported Investment and all unapproved financial categories remain excluded. Report remains a simple item list, not a full Personal Financial Report or Loan Information Pack.
- If the Financial Profile or provenance owner fails, report generation fails closed.

## Focused required tests (not executed)
```powershell
dotnet test tests/reporting/Monergy.Reporting.Tests --configuration Release --filter FullyQualifiedName~ProductFinancialProfileReportSourceReaderTests
pnpm --dir apps/customer-web exec vitest run tests/ProductReportsExperience.test.tsx
pnpm --dir apps/customer-web exec tsc --noEmit -p tsconfig.json
```

Follow with a focused LOCAL owner-backed Documents→Financial Profile Loan fact→Basic Reports→exact source link check and its opposite-tenant failure. No development GitHub Actions.

## Expert sync
`monergy-system-expert` must represent Reporting→Financial Profile CID-030/033 and customer Reports Loan source amount, explicitly distinct from debt/EMI and accepted formal reports. Must remain source-unverified until the linked runtime checks succeed.

## Scope exclusions
No score, debt calculation, lender evaluation, commercial product selection, Production readiness, external provider or AI recommendation. No false completion of full 81-feature roadmap.
