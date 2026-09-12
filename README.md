# Monergy Application

`monergy-application` is the bounded product monorepo defined by the accepted
MWP-02-D05 engineering-platform baseline. This MWP-03-D01 candidate contains an
executable, technology-neutral repository bootstrap; it contains no implemented
Monergy business Feature.

## Current state

- Status: Candidate — Pending CTO Review
- Architecture basis: Monergy Architecture Baseline v1.0
- Required architecture publication: `193667fc7ad4d7f919f213f9a96260afa0f09fb9`
- Product runtime/language/framework: unresolved; no prototype choice is inferred
- Hosted repository and CI product: unresolved; this repository is local only
- Deployment: none

The pre-existing `Monergy/Application/monergy-poc` and `Prototype` folders are
outside this repository and are intentionally untouched. They are not treated as
accepted implementation inputs.

## Bootstrap verification

Run from the repository root:

```powershell
./build/Invoke-Bootstrap.ps1 -Task Restore
./build/Invoke-Bootstrap.ps1 -Task FormatCheck
./build/Invoke-Bootstrap.ps1 -Task Lint
./build/Invoke-Bootstrap.ps1 -Task StaticAnalysis
./build/Invoke-Bootstrap.ps1 -Task Test
./build/Invoke-Bootstrap.ps1 -Task CiConfig
./build/Invoke-Bootstrap.ps1 -Task SecretScan
./build/Invoke-Bootstrap.ps1 -Task DependencyScan
./build/Invoke-Bootstrap.ps1 -Task Build
```

`Build` emits local bootstrap descriptors, dependency inventory, and provenance
under ignored `.artifacts/`. These are not deployable application artifacts and
must not be represented as Feature, Integration, UAT, or Production evidence.

