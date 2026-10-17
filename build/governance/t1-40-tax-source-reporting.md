# T1-40 — Tax Source Figures in Basic Customer Report

Source candidate only. Based on Application main `875693478d7b3d870ed8c32fc301d116a27ac89d` (T1-39 Tax Search).

Customer generates existing basic source-connected Report. Reporting Service uses current authorized Financial Profile CID-030 and exact CID-033 source provenance. Reviewed TAX source figures join Income, Expense, Loan, Asset, Investment and Insurance records as individual report line items. The JSON export continues to represent the exact generated report, not assessed taxes, return status, tax refund, tax payable, fiscal liability, taxpayer certification or advice.

No new report type, service contract, persistent table, user permission, tax formula, external provider or tax-administration workflow.

## Focused verification required, not executed
```powershell
dotnet test tests/reporting/Monergy.Reporting.Tests --configuration Release --filter FullyQualifiedName~ProductFinancialProfileReportSourceReaderTests
pnpm --dir apps/customer-web exec vitest run tests/ProductReportsExperience.test.tsx
pnpm --dir apps/customer-web exec tsc --noEmit -p tsconfig.json
```
LOCAL UAT: explicitly reviewed Tax source → Financial Profile → customer Basic Reports → exact source and history, plus negative other-customer cases. No development GitHub Actions.

System Expert companion: `work/t1-40-tax-report-expert`. Report is a bounded source-linked current-customer fact list, not assessed tax or professional acceptance. Compiled Expert consistency and local owner execution pending.
