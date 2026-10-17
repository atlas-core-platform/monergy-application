# T1-35 — Authorized Search of Insurance Source Figures

**Status:** SOURCE IMPLEMENTATION CANDIDATE; no native runtime acceptance. App base `005b9ae2b197e3ffac8f43667bb8e123766f89bf` (T1-34 merged).

Customer uses current authorized lexical Search to find reviewed Insurance source figures from Financial Profile. Search & Retrieval retrieves the current customer profile through CID-030 and exact per-fact provenance through CID-033; the derived result links to `/finances?fact={financialFactId}` for Evidence source history.

## No-claims boundary
The displayed amount is a document-stated Insurance source figure. It is not independently verified policy coverage, claims benefit, insured sum, cash/surrender value, premium owed or insurance advice. No insurer provider, OCR, semantic/vector AI Search, new contract, permission, schema or financial calculation is introduced.

## Tests required (authored, NOT executed)
```powershell
dotnet test tests/search-retrieval/Monergy.SearchRetrieval.Tests --configuration Release --filter FullyQualifiedName~ProductSearchProjectionSourceTests
pnpm --dir apps/customer-web exec vitest run tests/ProductSearchExperience.test.tsx
pnpm --dir apps/customer-web exec tsc --noEmit -p tsconfig.json
```
LOCAL/UAT: Documents INSURANCE → explicit Financial Profile confirmation → Search → exact source/history; check denied tenant and unknown owner state. No development GitHub Actions.

## Expert
Linked `monergy-system-expert/work/t1-35-insurance-search-sync` source candidate to model Search/CID-030/CID-033/Financial Profile/Evidence and UI; must not classify as accepted end-to-end or Production behavior absent focused execution and source-matched compiled checks.
