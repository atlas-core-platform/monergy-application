# Monergy Application

`monergy-application` is the bounded product monorepo defined by the accepted
MWP-02-D05 engineering-platform baseline. MWP-03-D03 implements the eight
canonical VS-02 Evidence-to-Financial-Truth Features over the accepted D01/D02
foundation, using provider-neutral reference adapters restricted to LOCAL and
CI / EPHEMERAL execution.

## Current state

- Status: MWP-03-D03 Candidate — Pending CTO Review
- Architecture basis: Monergy Architecture Baseline v1.0
- Required architecture publication: `193667fc7ad4d7f919f213f9a96260afa0f09fb9`
- Backend: .NET SDK 10.0.401, .NET runtime 10.0.12, C# 14, ASP.NET Core and
  Worker Service according to accepted service responsibilities
- Frontend: Node.js 24.21.0, pnpm 12.4.1, TypeScript 6.0.3, React 19.3.0,
  Vite 8.3.0, Ant Design 6.6.3 and Tailwind CSS 4.3.3
- Hosted repository: private GitHub repository at
  `https://github.com/atlas-core-platform/monergy-application`
- Hosted CI: GitHub Actions configured for D01-D03 gates and evidence
- Governance: GitHub Free exception accepted; branch protection is
  `NOT_IMPLEMENTED — GITHUB FREE PLAN LIMITATION`; CODEOWNERS is advisory and CI
  flags direct pushes to `main`
- Bootstrap exception: the reviewed initial baseline was published directly to
  establish `main`; normal pull-request flow remains mandatory once protection
  is available
- Deployment: none
- VS-02: Implementation Candidate — 8/8 Features, 14/14 contracts, 5/5 service
  boundaries
- Achieved integration-evidence level: `SIMULATOR` through non-production
  reference adapters; no provider sandbox or Production compatibility claim

D01-TD-01 is resolved by D02-PT-01 through D02-PT-05. D01-TD-02 remains
resolved through GitHub, GitHub Actions, advisory CODEOWNERS and the accepted
GitHub Free governance exception. D01-TD-03 is resolved by D02-PT-06 through
D02-PT-08. Physical cloud, database, broker, storage, search, OCR, AI, identity,
secret-manager and orchestration products remain deliberately unselected.

The pre-existing `Monergy/Application/monergy-poc` and `Prototype` folders are
outside this repository and are intentionally untouched. They are not treated as
accepted implementation inputs.

## D03 verification

Run from the repository root:

```powershell
./build/Invoke-Toolchain.ps1 -Task Verify
```

The task surface performs locked restore, formatting, lint, strict type checks,
build, tests, security evidence, packaging, candidate-manifest checks, and D01/
D02/D03 verification. Generated evidence stays under ignored `.artifacts/`. OCI
packaging is `BLOCKED`, not `PASS`, when a Linux Docker engine is unavailable.
The D03 branch workflow supplies the approved hosted Linux path: it builds all
twelve images without pushing, records image digests, generates image CycloneDX
SBOMs, runs fail-closed Grype scans, and uploads only evidence. Nothing in D03
represents UAT, Production, provider-compatibility, publication, or deployment
evidence. The VS-02 browser route is `/vs02`; its labels deliberately distinguish
extracted observations from Financial Profile-owned authoritative facts.
