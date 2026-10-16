# T1-20 consent request-lifecycle correction — 17 October 2026

Status: **IMPLEMENTED_CANDIDATE; T1-20 runtime completion remains pending**.

Application baseline: `36d03b08c243cf9fa5c4f381f2837e4851ce9114`.
Tested application code: `de958ad906ce63b8c68181f8879e9b8c1b1fefb3` (working-tree bytes matched the committed tree).
Expert baseline: `301e85e9fd65830deec47490222fda112d561e3c`.
Requirements: MR §§6,23,39; COV-005; M2-WS02-E03-F01. Current customer/session isolation remains mandatory for recorded sharing permissions.

## Defect and bounded correction

The mount-time request had an AbortSignal, but the manual refresh did not. A delayed refresh could restore the previous customer's grant records or error/loading state after context changed. Every request now has cancellation and latest-request checks. Display state is also bound to the current tenant, actor, authentication context, subject version, customer and API instance so it is hidden immediately on scope change, before effects run. Unmount cancels a manual refresh. Malformed grant dates remain rejected.

No backend endpoint, contract, schema, migration, permission, consent mutation or provider connection changed. An unused spy variable in the existing Evidence test was removed because it prevented the project TypeScript check; the spy itself remains installed.

## Executed checks

Environment: Linux, pinned Node `24.21.0`, pnpm `12.4.1`, .NET SDK `10.0.401`.

| Check | Result | Boundary |
| --- | --- | --- |
| `pnpm --dir apps/customer-web exec vitest run tests/ConsentExperience.test.tsx` | PASS, 8 tests | Mocked owner API; includes late success/failure, API-session replacement and unmount |
| New regression tests against the original Consent component | 4 FAIL / 4 PASS, as expected | Confirms missing manual-request cancellation; patched component then passes all 8 |
| `pnpm --dir apps/customer-web exec tsc --noEmit -p tsconfig.json` | PASS | Complete customer-web project type check |
| `pnpm --dir apps/customer-web exec eslint src/product/ConsentExperience.tsx tests/ConsentExperience.test.tsx` | PASS | Changed consent source/tests only; not repository-wide lint |
| Focused .NET tests below | PASS, 32 tests, zero skipped | Consent policy, response materialization and tenant/relationship boundary tests; not live PostgreSQL integration |
| Expert source-lock/product-reconciliation tests | PASS, 4 tests | Pure Node tests using fixtures/temp Git repos; not the complete compiled model |
| `git diff --check` | PASS | Source whitespace |

Backend command:

```sh
dotnet test tests/persistence/Monergy.AccessIntegration.Tests --configuration Release \
  --disable-build-servers -m:1 -p:UseSharedCompilation=false \
  --filter 'FullyQualifiedName~ConsentPolicyTests|FullyQualifiedName~ConsentMaterializationTests|FullyQualifiedName~TenantBoundaryTests' \
  --logger 'trx;LogFileName=t1-20-consent.trx' --results-directory .artifacts/t1-20
```

The initial multi-process MSBuild attempt hit a forbidden named-pipe socket. A single-process build with build servers disabled restored, built and ran the 32 tests successfully. No security control or package pin was changed.

Expert command:

```sh
node --test tests/source-lock-current.test.js tests/product-source-reconciliation.test.js
```

## Open checks and observed failures

- No Docker daemon/client or PowerShell is available for the documented isolated UAT profile. The live owner-backed browser/database journey has not run.
- Private Expert source files were retrieved and checked against Git blob hashes. Its 11.7 MB generated implementation graph could not be retrieved through the available connector. Complete compiled-model/accepted-deliverable validation and exact-source recompilation were not run. Historical extraction locks are unchanged.
- The combined Consent/Evidence component run produced 31 passes and 5 failures. All 8 Consent tests passed. The Evidence failures were: recent activity's ambiguous filename selector; resumed review; read-only recovered review; mixed recovered candidates; other-document-version reconciliation. The resumed-review test passed when isolated. No broad-suite PASS is claimed; remaining failures require diagnosis in that unit.
- Existing Evidence test lint also contains pre-existing errors. Only consent-file lint is claimed passing.
- The shared independent conversation was inaccessible. The master PDF and supplied video analysis were available; no missing requirement content was invented.

## Expert synchronization and next action

Paired branch: `monergy-system-expert/work/t1-20-verification-continuity`. Record this correction at its exact code revision as a candidate, keep T1-20 active while verification is pending, correct the obsolete Active label to Unexpired record, and preserve source-versus-runtime evidence boundaries. The scoped tests must not be represented as full T1-20 acceptance.

Complete the isolated owner-backed UAT and full Expert consistency/recompilation checks before closing T1-20 or merging this correction as fully verified. Then proceed to named-recipient resolution and explicit revocation subject to existing owner contracts and OD-09 constraints. No development GitHub Actions were triggered.
