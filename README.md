# Monergy Application

`monergy-application` is the bounded product monorepo defined by the accepted
MWP-02-D05 engineering-platform baseline. Accepted MWP-03-D03 implements the
eight canonical VS-02 Evidence-to-Financial-Truth Features. Accepted
MWP-03-D04 completes the Financial Profile Authority Expansion across seven
READY Features, using provider-neutral reference adapters restricted to LOCAL
and CI / EPHEMERAL execution.

## Current state

- Accepted foundation: MWP-03-D03 and MWP-03-D04 Accepted / Complete at SIMULATOR
- MWP-03-D05: business implementation accepted at SIMULATOR — six Features and CID-037–CID-041; closure pending frontend test-lifecycle remediation verification and new exact-head approval
- D05 frontend business change: NONE REQUIRED BY D05 FEATURE SCOPE
- Architecture basis: Monergy Architecture Baseline v1.0
- Required architecture publication: `193667fc7ad4d7f919f213f9a96260afa0f09fb9`
- Backend: .NET SDK 10.0.401, .NET runtime 10.0.12, C# 14, ASP.NET Core and
  Worker Service according to accepted service responsibilities
- Frontend: Node.js 24.21.0, pnpm 12.4.1, TypeScript 6.0.3, React 19.3.0,
  Vite 8.3.0, Ant Design 6.6.3 and Tailwind CSS 4.3.3
- Hosted repository: private GitHub repository at
  `https://github.com/atlas-core-platform/monergy-application`
- Hosted CI: GitHub Actions configured for D01-D05 gates and evidence
- Governance: GitHub Free exception accepted; branch protection is
  `NOT_IMPLEMENTED — GITHUB FREE PLAN LIMITATION`; CODEOWNERS is advisory and CI
  flags direct pushes to `main`
- Bootstrap exception: the reviewed initial baseline was published directly to
  establish `main`; normal pull-request flow remains mandatory once protection
  is available
- Deployment: none
- VS-02: implementation accepted at `SIMULATOR` — 8/8 Features, 14/14
  compatibility-evidenced contracts, 5/5 service boundaries
- D04: implementation accepted at `SIMULATOR` — 7/7 Features and the exact
  applicable CID-030 through CID-036 set (7/7); CID-031, CID-033, CID-034 and
  CID-035 overlap the accepted D03 set, so the D03/D04 union is 17 rather
  than 21; Financial Profile Service owns authority and Audit participates only
  through governed fact events
- D05 adds five contracts to that union: 22 distinct contracts, with consumed
  CID-032/CID-033 counted only once. The program has 8 D03 + 7 D04 + 6 D05
  accepted implementation Features at SIMULATOR; this is not full VS-03 completion.
- Achieved integration-evidence level: `SIMULATOR` through non-production
  reference adapters; no provider sandbox or Production compatibility claim

## D05 application acceptance boundary

The initial acceptance head `e669bf248ac2a8f362c538cbddf2e2d5a65174ef` failed both
fresh hosted workflows (`35474527894`, `35474529730`); those failures and earlier
`35471222663` remain FAIL. [The bounded lifecycle remediation](build/governance/d05-frontend-lifecycle.md)
changes test-clock ownership only, preserves business behavior/dependencies, and
requires a fail-fast twenty-execution Linux probe before renewed full verification.
Its native watcher-port diagnostic is retained separately, not claimed fixed.

`build/governance/d05-acceptance.json` records the exact CTO-reviewed candidates,
Feature/contract union, prior run/artifact identities and explicit limits. The
original `d05-scope-lock.json` remains the reviewed candidate scope, not a mutable
acceptance record. Fresh local and hosted evidence must identify the new
application acceptance commit; reviewed-candidate runs are not substituted.

Application PR #3 and architecture PR #9 remain draft and unmerged. Architecture
closure is not authorized in this step. The approved architecture branch base
`866c05122a0823d38dcaf243d3164b5b2b0947d9` is distinct from requirements baseline
`5e7fb1cc9a56cc7b0411640bbb63c13c02c83657`. Its known 390-line local source append
is explicitly excepted from clean-worktree preflight, must remain untouched and
uncommitted, and is not accepted architecture evidence. The exception expires
after this application-acceptance step; the existing provenance stash is untouched.

SG-01 remains READY; SG-02 CONDITIONALLY_READY; SG-03/SG-04 BLOCKED. VS-03 remains
CONDITIONALLY_READY and OD-08/C-10 remain open. `engineering.sum` and
`engineering.ratio` are verification fixtures, not approved client financial
methodology, formulas or rounding policy. Reference persistence and outbox state
are not durable, and no provider or persistent environment is selected.

The reviewed OCI set contains 120 inherited findings (108 Medium, 12 Negligible;
zero Critical/High), unchanged from D04. Six unique Medium fixes are available
but not installed. Retained run 35471222663 remains FAIL despite subsequent
successful independent evidence. Recurrence of its frontend teardown defect or
any vulnerability finding delta requires STOP and CTO review, not retries until green.

D01-TD-01 is resolved by D02-PT-01 through D02-PT-05. D01-TD-02 remains
resolved through GitHub, GitHub Actions, advisory CODEOWNERS and the accepted
GitHub Free governance exception. D01-TD-03 is resolved by D02-PT-06 through
D02-PT-08. Physical cloud, database, broker, storage, search, OCR, AI, identity,
secret-manager and orchestration products remain deliberately unselected.

The pre-existing `Monergy/Application/monergy-poc` and `Prototype` folders are
outside this repository and are intentionally untouched. They are not treated as
accepted implementation inputs.

## Candidate verification

Run from the repository root:

```powershell
./build/Invoke-Toolchain.ps1 -Task Verify
```

The task surface performs locked restore, formatting, lint, strict type checks,
build, tests, security evidence, packaging, acceptance-evidence manifest checks,
and D01/D02/D03/D04/D05 verification. Generated evidence stays under ignored `.artifacts/`. OCI
packaging is `BLOCKED`, not `PASS`, when a Linux Docker engine is unavailable.
The hosted workflow supplies the approved Linux evidence path: it builds all
twelve images without pushing, records image digests, generates image CycloneDX
SBOMs, runs fail-closed Grype scans, and uploads only evidence. Nothing in D03
represents UAT, Production, provider-compatibility, publication, or deployment
evidence. The VS-02 browser route remains unchanged. Frontend business change:
NONE REQUIRED BY D04 FEATURE SCOPE.
