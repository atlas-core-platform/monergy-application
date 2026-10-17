# T1-39 — Authorized Tax Source Search

**Source candidate, runtime verification pending.** Base app main `813c6af777d6dcfaa03228ab13fb71081b9af9d4` (T1-38).

Customer Search finds an explicitly reviewed `TAX` source fact from current Financial Profile via existing owner CID-030 and exact CID-033 provenance. The derived/rebuildable Search result links to `/finances?fact={financialFactId}` and identifies the value as a **Tax source figure, not assessed liability or refund**.

Existing current-customer tenant/actor/resource authorization stays in force. No new provider, API contract, table, tax calculation, tax filing, governmental confirmation, semantic search or advice.

Focused checks **authored, not run**:
```powershell
dotnet test tests/search-retrieval/Monergy.SearchRetrieval.Tests --configuration Release --filter FullyQualifiedName~ProductSearchProjectionSourceTests
pnpm --dir apps/customer-web exec vitest run tests/ProductSearchExperience.test.tsx
pnpm --dir apps/customer-web exec tsc --noEmit -p tsconfig.json
```
LOCAL UAT: confirmed reviewed Tax source item → Search → exact source history; deny other-customer/unauthorized access. No development GitHub Actions.

System Expert: `work/t1-39-tax-search-expert` candidate must trace Search → Financial Profile/Source provenance, current customer Security, and explicit absence of assessed tax. Full model recompile remains pending.
