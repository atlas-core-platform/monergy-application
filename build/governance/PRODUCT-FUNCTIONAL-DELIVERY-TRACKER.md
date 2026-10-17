# Monergy Functional Delivery Tracker — authoritative working sequence

Status: ACTIVE · Inspected base for T1-40: Application `653b28e8271790a719ce5c630accbb180b857f98` (remediation #88), System Expert `64ef149795b4aaad30ca3c76a09df32504e1a396` (#40). T1-40 Tax basic Reports is implemented and verified by focused native checks; live owner UAT and full current-source Expert compilation remain open. Next functional unit: Tax Action Centre coverage.
This file tracks bounded implementation units; the accepted 81-feature catalog and 151 requirement IDs remain authoritative for requirements identity, and the AR-003 persona and capability catalogs remain authoritative for planned journeys.
One current implementation unit may be a subset of one canonical feature. Engineering completion never proves an entire business feature complete.

## Definition of done

Each unit requires: owner-backed UI/API/data behavior; tenant/customer access, applicable Consent and fail-closed unavailable states; focused local tests and required security verification; explicit source/evidence identity; System Expert knowledge synchronization and consistency verification; updated traceability; CTO acceptance. No development-phase GitHub Actions. No acceptance or main merge based solely on source inspection.

## Queue (review continuously against dependency evidence)

| Priority | Unit | Requirement/feature | Source/owner boundary | Current state | Next dependency |
| --- | --- | --- | --- | --- | --- |
| P0 / carry-forward verification | T1-20 View delegated consent; request-lifecycle correction | MR §§6,23,39; COV-005; M2-WS02-E03-F01 | Consent owner + current Customer & Identity session + customer UI | SOURCE_MERGED_RUNTIME_PENDING (PR #68, Expert #20); prior 8 component and 32 backend focused tests passed | Isolated owner-backed UAT and full Expert source-matched model verification remain outstanding; do not present as released |
| P0 / carry-forward verification | T1-21 Explicitly stop one recorded delegated sharing grant | MR §§6,23,39; MON-CON; COV-005; M2-WS02-E03-F01 | Existing Consent DELETE, revision/audit, customer confirmation UI | MERGED_MAIN_SOURCE_UNVERIFIED (application #69, Expert #21); standalone guarded API checks passed | React/TypeScript, full owner-backed local UAT and complete Expert recompile outstanding |
| P0 / carry-forward verification | T1-22 Show tenant-scoped recipient identity labels on recorded grants | MR §§6,39; COV-005; M2-WS02-E03-F01 | C&I principal label → Consent authorized grant list → Customer consent UI | MERGED_MAIN_SOURCE_UNVERIFIED (#70 / Expert #22; focused local/UAT outstanding) | Exact customer ownership recheck, C&I workload-only endpoint, no arbitrary actor directory, UI fallback and Expert sync |
| P0 / carry-forward verification | T1-23 through T1-28: Loan/Asset source facts and linked Search/Reports/Action Centre | Master §§12–15,20,30,41; WS03/04/07/08 partial | Evidence → Document Intelligence → Financial Profile → Search / Reporting / Customer pages | SOURCE_MERGED_RUNTIME_UNVERIFIED: App PR #71–76 and Expert PR #23–28; tests authored, no native UAT proof | Focused local testing and exact-source compiled Expert verification; maintain no-balance/no-valuation claims |
| P0 / carry-forward verification | T1-29: Asset source figures in basic Reports | Master §§13,20,30,41; M2-WS08-E02-F01/F03 partial | Reporting → Financial Profile CID-030/033 → Customer Reports | SOURCE_MERGED_RUNTIME_UNVERIFIED (App #77, Expert #29) | Owner Reporting tests, React, local UAT and current-source Expert checks pending |
| P0 / carry-forward verification | T1-30: reviewed Investment source figures | Master §§12,13,20,41; WS03/04 partial | Evidence → Document Intelligence → Financial Profile → Customer Passport | SOURCE_MERGED_RUNTIME_UNVERIFIED (#78 / Expert #30) | Focused .NET/React, owner UAT and compiled Expert validation pending |
| P0 / carry-forward verification | T1-31: authorized Investment lexical Search | Master §§13–15,20,41; WS07 partial | Search → Financial Profile CID-030/033 → Customer source history | SOURCE_MERGED_RUNTIME_UNVERIFIED (#79 / Expert #31) | Search owner/React/LOCAL UAT and compiled Expert checks pending; contradictory source test fixture corrected |
| P0 / active verification | T1-32–39: Investment Reports/coverage, Insurance facts/Search/Reports/coverage, Tax facts/Search | Master §§12–15,20,30,41; WS03/04/07/08 partial | Evidence → Document Intelligence → Financial Profile → Search / Reporting / Customer pages | SOURCE_MERGED_RUNTIME_INCOMPLETE (App #80–87, Expert #32–39) | Current local checks and remediation recorded in evidence/t1-39-local-verification-20261017.md; owner-backed UAT and full source-matched Expert compilation remain open |
| P0 / carry-forward UAT | T1-40 Tax source figures in basic Reports | Master §§13,20,30,41; WS08 partial | Reporting → Financial Profile CID-030/033 → Customer Reports/export | IMPLEMENTED_FOCUSED_NATIVE_VERIFIED: 4 owner tests, 8 Reports component tests, TypeScript/test lint passed | Owner-backed UAT and full compiled Expert remain open; see t1-40-tax-source-reporting.md |
| P0 / next functional unit | T1-41 Tax source coverage in Action Centre | Master §§13,20,39,41; WS04/08 partial | Financial Profile → Customer Action Centre | NOT_STARTED | Seventh supported source category; no assessment or due-date advice |
| P0 | Expand Financial Passport beyond Income/Expense by one governed fact domain per unit | MR §§2,13,20,41; M2-WS04-E01/E02 | Financial Profile owner and customer UI | FOUNDATIONS_EXIST; customer journeys partial | Approved domain input/mutation semantics and evidence source; no fabricated net worth |
| P0 | Document classification/OCR one supported file type at a time | MR §§10–12, 41; M2-WS03-E02 | Evidence → Document Intelligence → Financial Profile | STRUCTURED_TEXT_LOCAL_ONLY | Approved extraction/quality controls; production provider decision remains gated |
| P1 | Connected customer sources, one authorized provider route at a time | MR §§7–8,23–25,41; WS06 E03 | Provider catalog → tenant config → customer authorization → Gateway | NO_LIVE_PROVIDER_ACCEPTANCE | OD-04/05/06/09, licensed route and keys |
| P1 | Calculated net worth, liquidity and credit facts with source lineage | MR §§17–20; WS05 | Financial Rules + Financial Profile | RULES_REFERENCE_ONLY | Actual assets/liabilities and approved formulas OD-08 |
| P1 | Financial Q&A grounded in authorized financial truth | MR §§14–16, 29,31; WS07 | Search/Retrieval + AI Intelligence + security | NO_CUSTOMER_AI_ACCEPTANCE | OD-11, evaluated model and source truth |
| P1 | Formal financial packs/goals and actionable tasks | MR §§30,37–39; WS08 | Reporting, Financial Rules, Job Management | BASIC_SUMMARY_ONLY | Formal report scope C-14, goals/score gates |
| P2 | Advisor, DSA, lender and professional workspaces | MR §§4,32,43; AR-003, MON-RDC | Assigned business resources with role+permission+Consent | FUTURE_GATED | Owners for cases/applications/verification and OD-02/09/10 |
| P2 | Production identity, regulated integrations and operational launch | MR §§27–28,45–47; WS01/02/09 | Identity, security, deployment and operations | STAGE_GATES_BLOCKED | OD-10/12/13/14/15 and acceptance evidence |

## Current source-merged functional increments (verification incomplete)

| Unit | Application PR / merge SHA | Expert PR / merge SHA | End-to-end state |
| --- | --- | --- | --- |
| T1-23 | #71 · `f9c8f1afef9dd89cb2c1582ac9672b5c163b4b18` | #23 · `2f45ea47fd779a021aa48870a20b389b71a09b73` | Loan source fact / Passport; runtime pending |
| T1-24 | #72 · `396a3067bd27312ff24094e503d79ece8be347f9` | #24 · `609ee335dfa16835f3ea462f4500ed453e5181f0` | Loan source Search; runtime pending |
| T1-25 | #73 · `82beb92f21df6fb6875e8618b8a0c1d520322b1a` | #25 · `a94946ca2c02174604b1d4470a75bc13e886129f` | Loan source Reporting; runtime pending |
| T1-26 | #74 · `3b141c1894d5844cbbc6f6690958a902b2fe3ccd` | #26 · `8ca52ac1f68df882d0d55202ed31b020fffceb32` | Action Centre Loan coverage; runtime pending |
| T1-27 | #75 · `0495bd0705dca215c854765a2af3a343538529de` | #27 · `d25701389f68b7bd3574291848b6c9cb568b3a3d` | Asset source fact / Passport; runtime pending |
| T1-28 | #76 · `fbfc89d09bf54d61adc479c4b5680e6970e17f9d` | #28 · `5e951117fc6ea6c1c41786f65c4722c18de89f78` | Asset source Search; runtime pending |
| T1-29 | #77 · `652b71756d51cdc0342188fa318d7f434e2b4a76` | #29 · `b13b19c777e665e01d432957e0b89c18b270fdc2` | Asset basic Reporting; runtime pending |
| T1-30 | #78 · `7b7b47b743bfe4aec6e7398a0f9aa6c1bf8d5840` | #30 · `228afec70326a84c1668d8f93619bf64e0815bb6` | Investment source fact / Passport; runtime pending |
| T1-31 | #79 · `8d7ffdc7910beb29ff0cebb07b7e571894a938fc` | #31 · `1e6ac15334b7d209cf42fdb92338e22ed54527c0` | Investment lexical Search; runtime pending |

| T1-32 | #80 · `0dd4faca302c2bf9d64279ebe1f92cf69ecc9300` | #32 · source knowledge on inspected Expert main | Investment source facts in authorized customer basic Reports; local verification evidence below, owner-backed UAT pending |
| T1-33 | #81 · `9d7ea378a907fbf198d35020be256f888bbf2ad7` | #33 · source knowledge on inspected Expert main | Asset and Investment source coverage in customer Action Centre; local verification evidence below, owner-backed UAT pending |
| T1-34 | #82 · `005b9ae2b197e3ffac8f43667bb8e123766f89bf` | #34 · source knowledge on inspected Expert main | Insurance source figures in Customer Financial Passport; local verification evidence below, owner-backed UAT pending |
| T1-35 | #83 · `9ea58e9b6b8784e02a984ee78e60cefdc3e23c54` | #35 · source knowledge on inspected Expert main | Current-customer Insurance source Search; local verification evidence below, owner-backed UAT pending |
| T1-36 | #84 · `2e9be4595c0d1f8b116ac70d2b37a6d0203b4ea7` | #36 · source knowledge on inspected Expert main | Trusted Insurance source figures in customer basic Reports; local verification evidence below, owner-backed UAT pending |
| T1-37 | #85 · `88e1059ce5bb1a8b45b98c476034bbe771575f1e` | #37 · source knowledge on inspected Expert main | Insurance source coverage in Customer Action Centre; local verification evidence below, owner-backed UAT pending |
| T1-38 | #86 · `813c6af777d6dcfaa03228ab13fb71081b9af9d4` | #38 · source knowledge on inspected Expert main | Reviewed Tax source figures in Financial Passport; local verification evidence below, owner-backed UAT pending |
| T1-39 | #87 · `875693478d7b3d870ed8c32fc301d116a27ac89d` | #39 · source knowledge on inspected Expert main | Authorized Tax source Search; local verification evidence below, owner-backed UAT pending |

**Completion denominator remains the original 151 normalized requirements and 81 canonical features, augmented by approved AR-003 extensions, not the T1 count.** No percentage of functionally complete product is inferred from source merges. The latest source-head receipts above supersede stale historical queue labels below. No GitHub Actions in development.

## Prior accepted sequence

T1-01 through T1-19 preserve existing individual CTO acceptance and merge evidence, including PR #66 at application main `241da03742893d8c362503365a6fa8f1fad80d36`. They are **not** collectively a complete Monergy product. That historical implementation crosswalk was scoped at Income/Expense. Source-merged extensions now cover Loan, Asset, Investment, Insurance and Tax in Passport/structured-text extraction and Search; Reports now include Tax source figures with focused native verification; Action Centre still stops at Insurance. They retain source-figure semantics, without financial valuation, tax assessment or production-provider claims.

## Current linked source and correction records

- Application [PR #67](https://github.com/atlas-core-platform/monergy-application/pull/67) is merged at `36d03b08c243cf9fa5c4f381f2837e4851ce9114`; its earlier DRAFT label is historical.
- System Expert [PR #19](https://github.com/atlas-core-platform/monergy-system-expert/pull/19) is merged at `301e85e9fd65830deec47490222fda112d561e3c`. Its historical compiled graph is still stale.
- Active correction: `work/t1-20-consent-session-safety`, code revision `de958ad906ce63b8c68181f8879e9b8c1b1fefb3`. Abort every request, including manual refresh, on customer/session change or unmount; prevent stale data, errors and busy-state changes even when transport ignores cancellation. Scope displayed results to the current tenant, actor, authentication context, subject version, customer and API instance.
- Evidence: [T1-20 correction verification](evidence/t1-20-consent-session-safety-20261017.md). Frontend component/type/lint and focused backend tests passed. These do not replace live owner-backed UAT.
- Paired Expert correction: `work/t1-20-verification-continuity`; retain T1-20 as active while runtime evidence is pending and expose correction evidence without promoting acceptance or rewriting extraction locks.
- Test-only Evidence follow-up: `a113683527e3b464829a3552bed66f69568bdb56` scopes the duplicate filename assertion to loaded processing activity. SOURCE_REVIEWED_RERUN_PENDING: the workspace exec-server is disconnected; four recovered-review timeouts remain undiagnosed. Earlier passing counts apply only to the earlier tested revision.
- Completion remains pending. No development GitHub Actions, release approval or product acceptance is inferred.

## Complete scope inventory and remaining reconciliation

Use the existing [AR-003 coverage matrix at the inspected revision](https://github.com/atlas-core-platform/monergy-architecture/blob/90934b51b2b203795acf95da2252a2cf1a1d1f4c/docs/15-Architecture-Revisions/AR-003/Product-Capability-Coverage-Matrix.md) for all 62 capability rows, 151 normalized requirements, 81 features, 50 master-requirement sections and 16 recording-derived candidates. Its historical implementation-state cells are not current-source verification. This tracker owns the execution queue and new implementation evidence; the matrix owns requirement identities and approved journey/dependency mappings.

The ten queue groups above are prioritization groups, not ten exhaustive requirements or a completion denominator. Current-source reconciliation of the remaining capability rows is still outstanding. Do not report a product completion percentage or zero remaining gaps from this queue.

The master requirements and the supplied two-video analysis were inspected during this continuation. The independent shared-conversation URL could not be retrieved; no requirement was inferred from it. Provider choices, finance methodology, professional workflows and production decisions retain their existing gates. The bounded native verification remediation is recorded in the current receipt. T1-21 revocation and T1-22 recipient labels have source merges; their live owner-backed UAT remains pending. Next functional implementation is the explicitly listed T1-41 Tax Action Centre gap.

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

## T1-21 bounded unit — owner-backed revocation

The customer may stop one *unexpired, unrevoked* recorded consent grant after expanding an exact reference-specific confirmation. The current LOCAL Consent owner already implements an authenticated, customer-owned `DELETE /local/v1/consents/customers/{customerId}/grants/{grantId}` with `requestId` and `expectedVersion`; the browser must not simulate revocation or invent permissions. A completed owner receipt is identity/version-checked and the customer record is reread. When the outcome is uncertain, the UI suppresses the stale grant list and requires refresh without automatic second mutation. Recipient names are **not resolved** in this unit, so the user must explicitly recognize the technical reference before confirming; name resolution remains a separately gated next unit.

Source: `apps/customer-web/src/access/accessApi.ts`, `src/product/ConsentExperience.tsx`, `tests/ConsentExperience.test.tsx`. Focused implementation-level tests have been authored but **not executed** in this GitHub-source-only environment; exact local React/TypeScript tests, Consent-backed Docker UAT, and Expert source-matched knowledge checks are required before full functional acceptance. No development GitHub Actions.

## T1-22 — Named recipient label scope

Only a Customer's **existing Consent grant actors** may be enriched with a bounded label from that tenant's Customer & Identity `tenant_principals.normalized_email`. This is a verified email label, not a guessed name. The internal identity lookup requires the established separate `CustomerContextToken`, and the browser cannot call it. Consent validates its current owned customer before reading grants and again after retrieving labels; changed owner/session versions fail closed. Missing/unavailable labels remain null and the customer retains the explicit reference warning. There is no general user directory, change to consent authority, or new persistence table.

Code paths: `services/customer-identity/{TenantSessionEndpoints.cs,Infrastructure/PostgresTenantIdentities.cs}`, `shared/platform/Monergy.Platform/CustomerOwnerClient.cs`, `services/consent/{CustomerConsent.cs,CustomerConsentEndpoints.cs,Infrastructure/PostgresCustomerConsent.cs}`, `apps/customer-web/src/product/ConsentExperience.tsx`, and targeted component tests. No runtime verification is claimed. System Expert must describe the C&I→Consent→Customer UI read path and non-claims, with exact source and evidence identities.

## T1-23 truth and coverage

Source-specific `LOAN` typed numeric amount is deliberately distinguished from current outstanding balance, EMI, original principal, rate, repayment schedule, overall liability, Loan/credit score or net worth. Source label must retain its stated meaning. Only controlled structured-text reference extraction and exact customer-selected normalization are included. At the original T1-23 boundary Search/Reporting remained Income/Expense-only; subsequent source merges are recorded above. New `LOAN` browsing/Passport coverage counts the source-connected fact; no deterministic aggregations. See `build/governance/t1-23-loan-source-facts.md`. Source and tests are candidates until focused local execution and Expert source matching.

## Current verification receipt

See [2026-10-17 focused local verification](evidence/t1-39-local-verification-20261017.md) for the exact test coverage and remaining gaps. This supersedes historical AUTHORED_NOT_EXECUTED statements only for the listed checks and tested source. The 151-requirement/81-feature denominator and approved provider/professional/production gates are unchanged.
