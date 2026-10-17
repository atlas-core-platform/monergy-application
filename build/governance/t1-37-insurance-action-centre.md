# T1-37 — Insurance Source Coverage in Customer Action Centre

Status: SOURCE CANDIDATE, runtime acceptance pending. Base main `2e9be4595c0d1f8b116ac70d2b37a6d0203b4ea7` (T1-36).

The existing customer Action Centre derives supported financial-source coverage from current authorized Financial Profile facts. Include `INSURANCE` alongside Income, Expense, Loan, Asset and Investment: n of 6. When a trusted Insurance source figure is absent, offer the bounded Documents review journey without asserting no insurance policy, no claim entitlement, policy value or coverage status.

This remains a current-customer deterministic read-only view, not a durable task, score, advice, insurance policy verification or provider connection. Existing Evidence and Job Management owner states remain fail-closed when unknown. No new endpoint, table, RBAC, consent or financial calculation.

Required focused tests **not executed in this environment**:
```powershell
pnpm --dir apps/customer-web exec vitest run tests/ProductActionCentreExperience.test.tsx
pnpm --dir apps/customer-web exec tsc --noEmit -p tsconfig.json
```
LOCAL UAT must verify a source-reviewed INSURANCE fact updates n-of-6 coverage, while unauthorised/unavailable owner data never yields false zero or all-clear. No development GitHub Actions.

System Expert: pair `work/t1-37-insurance-action-expert` to preserve Insurance source-only semantics, exact source head, Financial Profile ownership and source-versus-runtime evidence boundary.
