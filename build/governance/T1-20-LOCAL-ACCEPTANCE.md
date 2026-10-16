# T1-20 — Focused Local Acceptance & System Expert Synchronization

Status: **CANDIDATE; DO NOT MERGE BEFORE REQUIRED EVIDENCE**. No development GitHub Actions.
Original base: `monergy-application@241da03742893d8c362503365a6fa8f1fad80d36` (PR #66).
Candidate app branch: `work/t1-20-customer-consent-visibility`.
Paired Expert branch: `work/t1-20-source-synchronization`, PR #19.

## 1. Focused frontend checks

On a clean local checkout of the application candidate using the repository-pinned Node/pnpm toolchains and installed dependencies:

```powershell
pnpm --dir apps/customer-web exec vitest run tests/ConsentExperience.test.tsx
pnpm --dir apps/customer-web exec tsc --noEmit -p tsconfig.json
```

Required: all T1-20 tests green; no type errors. Do not substitute static inspection.

## 2. LOCAL owner-backed runtime

The existing `default` UAT profile might not include Consent databases. For Consent testing, use an **isolated named profile** that was created with `-EnableOnboarding`. Do not reset or modify an existing retained default profile.

```powershell
.\build\uat\Invoke-MonergyUat.ps1 -Action Start -Profile consent-t120 -EnableOnboarding -Port 4183
.\build\uat\Invoke-MonergyUat.ps1 -Action Verify -Profile consent-t120
.\build\uat\Invoke-MonergyUat.ps1 -Action Credentials -Profile consent-t120 -Tenant T001 -Actor A100
```

The new named profile may leave A100 without a business role by default. First use the Organization Administrator account to assign the implemented Customer role/resource access where needed, then sign out and establish a new A100 session; an administrator role by itself never supplies customer financial access.

Sign in using **your own local key** without recording it. Visit `http://127.0.0.1:4183/consent`. Verify authenticated/customer-owned entry, known empty versus service unavailable (never a fabricated empty state), refresh, and test-only temporary-data labeling. If applicable existing delegated grants exist, verify exact unexpired/expired/revoked **recorded** states. An unexpired grant is **not** proof of effective authorization; the current owner, actor and permissions are evaluated separately on protected actions.

Verify denied/cross-tenant requests and no disclosed other-customer records with the existing Consent owner integration checks. Do not seed grants in a retained customer profile solely to make a screen look populated. Run minimal extra owner tests only if a failure suggests a boundary issue.

## 3. System Expert focused checks

Use a clean Expert candidate checkout and the exact clean app and architecture Git source revisions you intend to inspect. The following tests require Node >=22 and Git but **no hosted Actions**:

```powershell
npm ci
node --test tests/source-lock-current.test.js tests/product-source-reconciliation.test.js tests/accepted-deliverables.test.js
node .\bin\monergy-expert.js validate
```

Compile against clean, exact reviewed source commits (substitute verified SHA values from local `git rev-parse HEAD`):

```powershell
node .\bin\monergy-expert.js compile --app "E:\Projects\Atlas\monergy-application" --arch "E:\Projects\Atlas\monergy-architecture" --expected-app-head "<reviewed-application-SHA>" --expected-arch-head "<reviewed-architecture-SHA>"
```

This must fail on mismatched or dirty inputs. The compiler now stamps extracted entities, SQL and code references with the exact verified Application checkout commit. Validate the regenerated Expert model and inspect **How Monergy Works / How Things Connect** for T1-19 source behavior and candidate-only T1-20. The generated `sources.lock.json` is the extraction provenance; accepted historical T1-06 locks are preserved separately, never repinned to pretend they were re-extracted.

## 4. Completion decision

Only after the application tests, LOCAL consent owner integration, Expert source-match and focused knowledge tests pass:
- record exact application+Expert candidate heads, test commands, result counts, local environment and any failure remediation;
- update Product tracker and accepted Expert state **only after** acceptance;
- merge both approved units to `main` in governed order; reverify source references to the resulting merge commits;
- start next unit. Do not trigger GitHub Actions.

Blocked or failed verification means **CANDIDATE**, not `ACCEPTED_COMPLETE`.
