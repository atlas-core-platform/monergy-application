# T1-30 — Reviewed Investment Source Figures in Financial Passport

**State:** SOURCE CANDIDATE; essential runtime verification pending. Base Application main `652b71756d51cdc0342188fa318d7f434e2b4a76` (T1-29 Asset Reporting).

## Original trace and purpose
Master §§12–13,20,41; AR-003 Customer Financial Passport; canonical WS03 Document Intelligence and WS04 Financial Profile realization **partial only**.

Customer supplies a supported LOCAL structured-text record:
`INVESTMENT|label|numeric amount|ISO currency|yyyy-MM-dd|source location|confidence`.
Document Intelligence creates a non-authoritative candidate with exact Evidence document version, source identity and confidence. Customer explicitly selects and confirms the source fact through the existing authorized Financial Profile normalization endpoint. Financial Profile remains owner of the trusted `INVESTMENT` fact, revision, provenance and Audit delivery. The customer can view the source figure and exact history in `/finances?type=INVESTMENT` and the source-backed Financial Passport overview.

## Guardrails
- A source-stated Investment number is **not** a live brokerage position, current mark-to-market price, NAV, return, accrued interest, capital gain, verified asset valuation, or portfolio-performance calculation.
- This increment does not implement securities identifiers, units, asset class, quantity, cost basis, pricing feed, position reconciliation, tax lots, multi-currency totals or Investment account APIs.
- No new database, schema, permissions, policy, provider, contract, AI advice or cross-customer access.
- Search, Reporting and Action Centre will not present this source type until their **separate**, bounded consumer increments are accepted.

## Focused verification required (not run here)
```powershell
dotnet test tests/vs02/Monergy.Vs02.Tests --configuration Release --filter FullyQualifiedName~StructuredTextLoanAmountRemainsNonAuthoritativeCandidateWithExactEvidence
pnpm --dir apps/customer-web exec vitest run tests/FinancialProfileExperience.test.tsx tests/WorkspaceHome.test.tsx tests/EvidenceExperience.test.tsx
pnpm --dir apps/customer-web exec tsc --noEmit -p tsconfig.json
```
Live LOCAL owner-backed UAT: Evidence Upload → Document Intelligence Investment extraction → explicit human confirmation → Financial Profile current revision → Passport category/exact Evidence source-history. Verify denied customer, expired session, replay, duplicate source, no unauthorized confidence promotion, and fail-closed owner outage. Existing T1-23–29 runtime verification debt is not closed by source inspection; no GitHub Actions.

## Mandatory System Expert
Represent Document Intelligence and Evidence → Financial Profile → Customer Financial Passport/Investment source figure, preserving the accepted architecture and true evidence state. Paired `monergy-system-expert` delta must pin the exact reviewed application candidate and later merge, but never infer accepted implementation or a live investment account.
