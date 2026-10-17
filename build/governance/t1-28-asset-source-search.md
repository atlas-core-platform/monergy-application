# T1-28 — Authorized Asset Source Search

**Status:** source implementation candidate; native runtime verification pending. Base Application main `0495bd0705dca215c854765a2af3a343538529de` (T1-27 Asset candidate merged).

Customer can search the exact current Financial Profile ASSET source fact via the existing lexical Search API. Search & Retrieval maintains only derived/rebuildable representations. It reads owner CID-030 and exact CID-033 provenance under current resource/tenant/actor authorization and links to `/finances?fact={financialFactId}`. Search results say **Asset source figure (not verified valuation)**, without net-worth or certified asset claims.

Scope: add ASSET to supported Income/Expense/Loan source projection; preserve unsupported Investment exclusion, failed owner/tenancy proof handling, current customer boundary, source reference exactness, no new permission, contract, table or provider.

Required focused checks (not executed here):
```powershell
dotnet test tests/search-retrieval/Monergy.SearchRetrieval.Tests --configuration Release --filter FullyQualifiedName~ProductSearchProjectionSourceTests
pnpm --dir apps/customer-web exec vitest run tests/ProductSearchExperience.test.tsx
pnpm --dir apps/customer-web exec tsc --noEmit -p tsconfig.json
```
LOCAL owner-backed UAT must verify one reviewed Asset fact from Documents → Financial information → Search → exact source history, including cross-customer denial. No GitHub Actions.

Paired System Expert sync: `work/t1-28-asset-search-sync`, preserving current Search→Financial Profile, source-only status, and no-valuation boundary.

**Nonclaims:** no verified market value, no asset valuation professional, no net-worth, collateral eligibility or lending assessment, no semantic or provider search.
