# T1-117 — tenant-administration recording remediation

Date: 2026-10-22

## Recording evidence

The supplied 88.5-second LOCAL UAT recording showed the retained `default` profile at `127.0.0.1:4173/access`:

- Access overview and People loaded for tenant `T001` as administrator `A900`.
- Customers returned a generic request failure and no rows.
- Processing Status correctly reported zero matching jobs; no failed request was shown.
- Opening Audit Log returned `503` through the feature owner and the browser discarded the otherwise-current administrator session, forcing the operator to enter the one-use local key again.
- After a fresh connection, Access Activity and Security & Sessions loaded, proving that the tenant session and Audit owner were not globally unavailable.

## Root cause and correction

1. Customer Administration was registered and mapped only when the wider customer-onboarding composition enabled `CustomerRelationships`. The UI route is available in every administration profile, so the retained six-database `default` profile reached an owner route that did not exist. Customer Administration is now mapped whenever tenant integration is enabled. The database bootstrap additively seeds the two bounded LOCAL customer-owner fixtures for both retained and onboarding profiles. Existing credentials, databases, and customer state are not reset or replaced.
2. `AccessApi` treated every feature-owner `503` as a session-level failure. A failed Audit, Customer, Evidence, Jobs, or other feature read therefore destroyed a valid administrator session. Only current authentication/authority failures (`401`/`403`) now end the administration view. Owner unavailability remains visible on the affected page and can be retried without re-entering credentials.
3. The retained LOCAL audit runtime role had read access to `audit.product_activity`, but not the insert authority required by the gateway's product-activity capture. The additive bootstrap grant now includes that append-only table; update/delete/truncate authority remains denied. Disposable UAT explicitly reads the product activity Audit Log after restart and checks tenant isolation.
4. The existing zero-job Processing Status result is preserved as a valid empty state; no jobs or documents are fabricated.

## Verification

- `AccessExperience.test.tsx`, `CustomerManagement.test.tsx`, `AuditLog.test.tsx`: 51 passed.
- Nine wider paging/comparison UI files: 194 passed, with the two existing period-comparison failures reproduced unchanged on the exact pre-change `main` revision (`3c483715`).
- Customer & Identity focused backend suite (`CustomerManagementTests`): 54 passed.
- Customer Web TypeScript: passed.
- Focused lint for `accessApi.ts` and `AccessExperience.test.tsx`: passed.
- Database Migrator Release build: passed with zero warnings/errors.
- Access Integration test-project Release build, including the new physical list/isolation assertion: passed with zero warnings/errors.
- Repository-wide Customer Web lint remains blocked by eight pre-existing errors in `ProductActionCentreExperience.tsx`, `ProductReportsExperience.tsx`, and `WorkspaceHome.test.tsx`; none are touched by T1-117.
- Docker Desktop and PostgreSQL are not available in this execution environment, so the corrected retained-profile Start and physical database test remain operator UAT checks.

## Operator replay

After pulling the merged change, rerun the existing command; do not reset the profile:

```powershell
.\build\uat\Invoke-MonergyUat.ps1 -Action Start
```

Then use the retained administrator key. The next bootstrap is additive and idempotent: it applies the owner route and missing LOCAL customer fixtures without rotating the key or deleting tenant data.
