# T1-38 — Reviewed Tax Source Figures in Financial Passport

Status: SOURCE CANDIDATE, runtime checks outstanding. Base app main `88e1059ce5bb1a8b45b98c476034bbe771575f1e` (T1-37).

## Business outcome
The Customer can submit a supported structured-text Evidence document containing `TAX|label|numeric amount|currency|yyyy-MM-dd|source location|confidence`, review the non-authoritative Tax candidate, and explicitly add the selected record into Financial Profile. Passport provides a `Tax source figures` category linked to the exact source document and historical provenance. The displayed figure is **not** assessed taxes owed, an accepted filing, refund, income tax calculation, rate, return or tax advice.

## Ownership and security
- Evidence: original and immutable version.
- Document Intelligence: candidate only; no Tax liability authority.
- Financial Profile: normalization, source/provenance, current owner and existing Audit.
- Current customer/session, tenant boundary, permission and explicit review checks remain unchanged.
- No new contract, table, schema, access grant, provider, tax rule or aggregate computation.

## Focused execution still required
```powershell
dotnet test tests/vs02/Monergy.Vs02.Tests --configuration Release --filter FullyQualifiedName~StructuredTextLoanAmountRemainsNonAuthoritativeCandidateWithExactEvidence
pnpm --dir apps/customer-web exec vitest run tests/FinancialProfileExperience.test.tsx tests/WorkspaceHome.test.tsx tests/EvidenceExperience.test.tsx
pnpm --dir apps/customer-web exec tsc --noEmit -p tsconfig.json
```
LOCAL UAT must prove current Customer document → TAX extracted candidate → explicit confirmation → Tax source category → exact Financial Profile/Evidence history; verify cross-tenant and resource-denied requests remain denied. Native tests are authored but not executed in the present environment. No GitHub Actions in development.

## Expert sync
`work/t1-38-tax-source-expert`, pending/merged-source state strictly distinguished from accepted runtime behavior; How It Works and How It Connects use the Evidence → Document Intelligence → Financial Profile → customer Passport path.

**Nonclaims**: no tax owed/refund determination, tax filing, validated tax certificate, liability, taxation advice, Production or regulated integration acceptance.
