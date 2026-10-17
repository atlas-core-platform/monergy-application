# T1-36 — Insurance Source Figures in Basic Customer Reports

Status: SOURCE IMPLEMENTATION CANDIDATE; native runtime acceptance pending. Base Application main `9ea58e9b6b8784e02a984ee78e60cefdc3e23c54` (T1-35 Insurance source Search).

## Customer outcome
A currently authorized customer generates a basic Reporting-owned summary from trusted Financial Profile Income, Expense, Loan, Asset, Investment and Insurance **source facts**. Each displayed Insurance item preserves exact Financial Profile fact ID, Evidence reference and CID-033 provenance, and links back to the owner source/history. JSON export remains exact generated report content.

## Authority boundaries
- Reporting reads current customer facts via CID-030 and current fact provenance via CID-033.
- Insurance value is a document-stated numeric **source figure**, not a verified policy coverage sum, claim benefit, premium due, surrender/cash value or financial-health/insurance recommendation.
- No insurer connection, OCR/semantic AI, new database/contract/permission, verified policy model, net worth, score or provider.
- Owner unavailable, cross-tenant provenance or invalid consent/authorization remains fail-closed by existing boundaries.

## Required focused tests — NOT EXECUTED
```powershell
dotnet test tests/reporting/Monergy.Reporting.Tests --configuration Release --filter FullyQualifiedName~ProductFinancialProfileReportSourceReaderTests
pnpm --dir apps/customer-web exec vitest run tests/ProductReportsExperience.test.tsx
pnpm --dir apps/customer-web exec tsc --noEmit -p tsconfig.json
```

LOCAL/UAT: explicitly reviewed Insurance document source → Financial Profile → generate/reopen/export basic report → exact source history. Check unauthorized customer and provenance failure. No GitHub Actions.

## Expert sync
`monergy-system-expert/work/t1-36-insurance-report-sync` must show Reporting → Financial Profile CID-030/CID-033 → customer Reports/source history, with source-only status and explicit policy/claim nonclaims.
