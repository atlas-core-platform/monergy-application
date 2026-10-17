# T1-39 baseline — focused local verification and remediation

Inspected Application main: `875693478d7b3d870ed8c32fc301d116a27ac89d` (PR #87).
Inspected System Expert main: `d3c4bc4622200525d6c630c58cbbfbee5eb682b1` (PR #39).
Tested code revision: `1232979ab2f188d7b25f669c69fa7ffaf8ed2bdd`; tested code tree `5da11eaa192f5010c235d16a22565e01b4b63e36`. The remote tree was checked against the local Git index.
Date: 2026-10-17. Development GitHub Actions: **not run**.

## Corrections

- Financial Profile Audit delivery calls the revision-aware repository with an explicit null revision when resolving the source fact's tenant binding. The prior call did not compile. This changes no event payload, ownership or authorization rule.
- Search/Reporting test fixtures parse timestamps with invariant culture and satisfy the configured analyzer policy. Search uses an actually unsupported type for its negative fixture; TAX is now supported.
- Customer component tests check styled checkbox selectability and wait for drawer content visibility, click the visible radio label, and restrict negative financial-claim assertions to result items. Evidence fixtures distinguish unsupported source types from implemented Investment facts. Document-list waits observe loaded filenames before querying expensive accessible button names. No timeout increase or permission relaxation.
- Expert retains merged-but-unverified states, rejects duplicate ingestion before a changed source cursor can obscure that cause, and explicitly says pending increments are not verified end to end. Candidate-only and ordered T1-21–39 source-chain checks preserve historical acceptance/source locks.

## Executed checks

| Check | Result |
| --- | --- |
| Search `ProductSearchProjectionSourceTests` | 5 passed, 0 failed |
| Reporting `ProductFinancialProfileReportSourceReaderTests` | 3 passed, 0 failed |
| Financial Profile test project | 39 passed, 0 failed |
| VS-02 `StructuredTextLoanAmountRemainsNonAuthoritativeCandidateWithExactEvidence` | 1 passed, 0 failed; includes Loan/Asset/Investment/Insurance/Tax source candidates |
| Consent, Passport, Action Centre, Reports, Search, workspace components | 50 unique tests passed across the combined and repaired Reports runs; Reports 7/7 independently passed |
| System Expert delta/reconciliation/lifecycle tests | 40 passed, 0 failed |
| Customer TypeScript | passed |
| Lint on modified customer component tests | passed |
| Evidence source lint baseline comparison | 15 existing findings, same rule/line signatures before and after this patch; no new finding; source lint debt remains open |
| Evidence component suite | 29 passed, 0 failed |
| Consent policy/materialization/tenant boundary | 32 passed, 0 failed |
| UAT Gateway build | passed; 0 warnings, 0 errors; Search adapter return type matches immutable owner result |

Commands use .NET SDK 10.0.401, Node 24.19.0, the repository's installed package versions, `dotnet test ... --configuration Release -m:1 -p:UseSharedCompilation=false`, and local Vitest/TypeScript/ESLint binaries. No package or lockfile changes. Node differs from the declared 24.21.0 toolchain and is recorded as the actual test environment.

## Verification boundary

Totals: 79 unique customer component tests, 80 backend tests, 40 Expert tests. These are native focused component and service tests. They are not a Docker/PostgreSQL-backed owner UAT, browser UAT, release approval, or current-source re-extraction of the full Expert graph. Docker/PostgreSQL executables are unavailable in this workspace. Historical generated Expert source locks are preserved. T1-20–39 retain their outstanding end-to-end/compiled-source verification status; no full business feature or product completion percentage is inferred.

The active tracker now includes source merges through T1-39. The next bounded functional gap is Tax source figures in basic Reports, followed by Tax source coverage in Action Centre. Neither represents tax assessment, filing or advice.
