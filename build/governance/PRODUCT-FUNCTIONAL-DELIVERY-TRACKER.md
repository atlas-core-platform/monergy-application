# Monergy Functional Delivery Tracker — authoritative working sequence

Status: ACTIVE · Source baseline is application main `36d03b08c243cf9fa5c4f381f2837e4851ce9114` (T1-20 merged, runtime verification pending). T1-19 remains the preceding baseline; merging source did not close T1-20 verification.
This file tracks bounded implementation units; the accepted 81-feature catalog and 151 requirement IDs remain authoritative for requirements identity, and the AR-003 persona and capability catalogs remain authoritative for planned journeys.
One current implementation unit may be a subset of one canonical feature. Engineering completion never proves an entire business feature complete.

## Definition of done

Each unit requires: owner-backed UI/API/data behavior; tenant/customer access, applicable Consent and fail-closed unavailable states; focused local tests and required security verification; explicit source/evidence identity; System Expert knowledge synchronization and consistency verification; updated traceability; CTO acceptance. No development-phase GitHub Actions. No acceptance or main merge based solely on source inspection.

## Queue (review continuously against dependency evidence)

| Priority | Unit | Requirement/feature | Source/owner boundary | Current state | Next dependency |
| --- | --- | --- | --- | --- | --- |
| P0 / active | T1-20 View current delegated customer consent; close request-lifecycle defect | MR §§6, 23, 39; MON-CON family; COV-005; M2-WS02-E03-F01 | Consent owner + current Customer & Identity session + customer UI | SOURCE_MERGED_RUNTIME_PENDING; request-lifecycle correction has 8 passing component tests and 32 passing owner-policy/materialization/tenant-boundary tests | Isolated owner-backed UAT and complete source-matched Expert verification remain open |
| P0 | T1-21 Named recipient and explicit revoke journey | Same | Customer & Identity recipient names + Consent revision-controlled revoke + customer UI | NOT_STARTED | Named recipient resolution; revocation confirmation and concurrent revision evidence; OD-09 constraints |
| P0 | Expand Financial Passport beyond Income/Expense by one governed fact domain per unit | MR §§2,13,20,41; M2-WS04-E01/E02 | Financial Profile owner and customer UI | FOUNDATIONS_EXIST; customer journeys partial | Approved domain input/mutation semantics and evidence source; no fabricated net worth |
| P0 | Document classification/OCR one supported file type at a time | MR §§10–12, 41; M2-WS03-E02 | Evidence → Document Intelligence → Financial Profile | STRUCTURED_TEXT_LOCAL_ONLY | Approved extraction/quality controls; production provider decision remains gated |
| P1 | Connected customer sources, one authorized provider route at a time | MR §§7–8,23–25,41; WS06 E03 | Provider catalog → tenant config → customer authorization → Gateway | NO_LIVE_PROVIDER_ACCEPTANCE | OD-04/05/06/09, licensed route and keys |
| P1 | Calculated net worth, liquidity and credit facts with source lineage | MR §§17–20; WS05 | Financial Rules + Financial Profile | RULES_REFERENCE_ONLY | Actual assets/liabilities and approved formulas OD-08 |
| P1 | Financial Q&A grounded in authorized financial truth | MR §§14–16, 29,31; WS07 | Search/Retrieval + AI Intelligence + security | NO_CUSTOMER_AI_ACCEPTANCE | OD-11, evaluated model and source truth |
| P1 | Formal financial packs/goals and actionable tasks | MR §§30,37–39; WS08 | Reporting, Financial Rules, Job Management | BASIC_SUMMARY_ONLY | Formal report scope C-14, goals/score gates |
| P2 | Advisor, DSA, lender and professional workspaces | MR §§4,32,43; AR-003, MON-RDC | Assigned business resources with role+permission+Consent | FUTURE_GATED | Owners for cases/applications/verification and OD-02/09/10 |
| P2 | Production identity, regulated integrations and operational launch | MR §§27–28,45–47; WS01/02/09 | Identity, security, deployment and operations | STAGE_GATES_BLOCKED | OD-10/12/13/14/15 and acceptance evidence |

## Prior accepted sequence

T1-01 through T1-19 preserve existing individual CTO acceptance and merge evidence, including PR #66 at application main `241da03742893d8c362503365a6fa8f1fad80d36`. They are **not** collectively a complete Monergy product. The current implementation crosswalk remains scoped at Income/Expense for the customer-facing Financial Passport and structured-text extraction.

## Current linked source and correction records

- Application [PR #67](https://github.com/atlas-core-platform/monergy-application/pull/67) is merged at `36d03b08c243cf9fa5c4f381f2837e4851ce9114`; its earlier DRAFT label is historical.
- System Expert [PR #19](https://github.com/atlas-core-platform/monergy-system-expert/pull/19) is merged at `301e85e9fd65830deec47490222fda112d561e3c`. Its historical compiled graph is still stale.
- Active correction: `work/t1-20-consent-session-safety`, code revision `de958ad906ce63b8c68181f8879e9b8c1b1fefb3`. Abort every request, including manual refresh, on customer/session change or unmount; prevent stale data, errors and busy-state changes even when transport ignores cancellation. Scope displayed results to the current tenant, actor, authentication context, subject version, customer and API instance.
- Evidence: [T1-20 correction verification](evidence/t1-20-consent-session-safety-20261017.md). Frontend component/type/lint and focused backend tests passed. These do not replace live owner-backed UAT.
- Paired Expert correction: `work/t1-20-verification-continuity`; retain T1-20 as active while runtime evidence is pending and expose correction evidence without promoting acceptance or rewriting extraction locks.
- Completion remains pending. No development GitHub Actions, release approval or product acceptance is inferred.

## Complete scope inventory and remaining reconciliation

Use the existing [AR-003 coverage matrix at the inspected revision](https://github.com/atlas-core-platform/monergy-architecture/blob/90934b51b2b203795acf95da2252a2cf1a1d1f4c/docs/15-Architecture-Revisions/AR-003/Product-Capability-Coverage-Matrix.md) for all 62 capability rows, 151 normalized requirements, 81 features, 50 master-requirement sections and 16 recording-derived candidates. Its historical implementation-state cells are not current-source verification. This tracker owns the execution queue and new implementation evidence; the matrix owns requirement identities and approved journey/dependency mappings.

The ten queue groups above are prioritization groups, not ten exhaustive requirements or a completion denominator. Current-source reconciliation of the remaining capability rows is still outstanding. Do not report a product completion percentage or zero remaining gaps from this queue.

The master requirements and the supplied two-video analysis were inspected during this continuation. The independent shared-conversation URL could not be retrieved; no requirement was inferred from it. Provider choices, finance methodology, professional workflows and production decisions retain their existing gates. Next executable work remains T1-20 verification/remediation; named-recipient/revocation work follows its applicable dependencies.

## T1-20 boundary and evidence

- View only **up to 100 recent** Consent-owned grant records for the authenticated owned customer, via existing `GET /local/v1/consents/customers/{customerId}`.
- Customer may inspect unexpired, expired and revoked **records**; these are derived from expiry/revocation fields and do not claim effective authorization after current owner/version checks. No live provider connection inference.
- Actor reference remains under Technical details pending a real owner-resolved recipient name; no generic role/tenant wide access is invented.
- Consent owner verifies current trusted session, active customer and exact owner actor. Client also rejects malformed or other-customer response records.
- No grant/revoke/mutate functionality, self-consent fabrication, new service contract, new database, or product authority added.
- Source evidence: `apps/customer-web/src/product/ConsentExperience.tsx`, `apps/customer-web/src/access/accessApi.ts`, `apps/customer-web/tests/ConsentExperience.test.tsx`, UAT Gateway SPA route.
- Status **SOURCE_MERGED_RUNTIME_PENDING**. Focused component and owner-policy tests now have evidence; local UAT and complete System Expert consistency proof remain outstanding. See `build/governance/T1-20-LOCAL-ACCEPTANCE.md` for the bounded no-Actions verification path. The correction remains a candidate until its outstanding checks are satisfied.

## Mandatory System Expert coupling

T1-20 sync partner: `monergy-system-expert`. It must show application main through source-merged T1-20, preserve runtime-verification gaps, and keep T1-20 active until those gaps close. Existing accepted T1-01 to T1-06 source locks are historical, never overwritten as if newly extracted.
