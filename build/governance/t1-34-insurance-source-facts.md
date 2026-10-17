# T1-34 — Customer-reviewed Insurance Source Figures

Status: SOURCE IMPLEMENTATION CANDIDATE, runtime verification NOT EXECUTED. Application base `9d7ea378a907fbf198d35020be256f888bbf2ad7` (T1-33).

## Business outcome
Customer uploads a supported structured-text record `INSURANCE|label|numeric amount|ISO currency|date|source location|confidence`. Document Intelligence produces a non-authoritative Insurance source candidate. Customer must explicitly review and select it. Financial Profile is the sole authority for the resulting trusted Insurance source figure and Evidence exact document-version/provenance history. The Financial Passport has an Insurance source category and Overview now reports up to six supported source types.

## Strict nonclaims
This is a **document-reported figure**, not a verified policy sum insured, current cash/surrender value, claim benefit, premium due, health or insurance advice, coverage gap, or financial/net-worth total. No real insurer API/provider, OCR, contract, schema, new permission or future policy/claim business model is introduced.

## Security/owner boundaries
Reuses existing current Customer/tenant/session authorization, Evidence document access, explicit review/promotion, Financial Profile idempotency, provenance and Audit; fail closed on unknown owner/permission. Unsupported fact types remain outside current extraction/promotion.

## Essential focused checks — NOT RUN
```powershell
dotnet test tests/vs02/Monergy.Vs02.Tests --configuration Release --filter FullyQualifiedName~StructuredTextLoanAmountRemainsNonAuthoritativeCandidateWithExactEvidence
pnpm --dir apps/customer-web exec vitest run tests/FinancialProfileExperience.test.tsx tests/WorkspaceHome.test.tsx tests/EvidenceExperience.test.tsx
pnpm --dir apps/customer-web exec tsc --noEmit -p tsconfig.json
```
LOCAL/UAT: Evidence Insurance text → candidate review → explicit Financial Profile promotion → Passport category and exact source document/history; negative tenant/unauthorized/duplicate cases. No development GitHub Actions.

## Expert synchronization
Source-matched `monergy-system-expert/work/t1-34-insurance-source-sync` candidate records Document Intelligence, Evidence, Job Management, Financial Profile, Audit and customer Passport; it must not imply verified Insurance coverage or UAT acceptance.
