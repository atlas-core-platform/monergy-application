# Monergy Functional Delivery Tracker — authoritative working sequence

Status: ACTIVE · Latest bounded increment: T1-70 reviewed investment corrections and history. Original journeys remain Partial/Pending; full financial truth, central audit and integrated UAT remain open.

## Current T1-70 update — reviewed investment corrections and history

Master§13 / MON-FIN-007 / COV-020 / M2-WS04-E02-F01 now supports explicit correction of reviewed investment records with an expected version. Financial Profile appends every correction, preserves original source/currency/creation receipt and rejects stale competing edits. Latest lists show the newest version; exact saved versions and paged read-only history remain available. Replaying an old operation after newer writes returns its original receipt. Receipt: [t1-70-reviewed-investment-corrections.md](t1-70-reviewed-investment-corrections.md).

Migration0006 adds append-only version storage with SELECT/INSERT-only runtime grants. Current Self/owned-customer/setup/read/document/update authority and fresh exact-version readback remain. Unconfirmed edits freeze body/operation/version across close and missing current source. Confirmed invalid input permits editing; stale versions require explicit refresh/review. Scope/cancellation changes hide old records/history.

238 owner and57 affected UI checks pass, including39 new owner and7 new Investment UI cases. TypeScript/changed ESLint/Vite and zero-warning Gateway/physical test-project builds pass. Unchanged T1-69 source/extraction/Search/Reports/reprocessing and111 Documents UI evidence is carried forward, not rerun/counting as new passes. Native HTTP uses current authority doubles/in-memory storage. Physical PostgreSQL regression is compiled, NOT RUN.

After paired merge:70 numbered units =69 implementations +1 governance closure. Original29 journeys remain0 complete,17 partial,12 pending; Assets stays Partial. Lifecycle, institution verification, full portfolio/asset truth, central audit and physical/integrated UAT remain open. Tracker version12 reviewed before work. Original151 IDs/81 features/126 Phase1 memberships/source phases and acceptance remain unchanged.

## Previous T1-69 update — customer-reviewed investment records

Master§13 / MON-FIN-007 / COV-020 / M2-WS04-E02-F01 now has an explicit reviewed holding record beyond numeric source figures. Customers enter name, institution, type, purchase/current values and value-as-of date, linked to one exact reviewed INVESTMENT revision and its owner currency/provenance. No amount is autofilled or source figure changed. Financial Profile owns append-only records, scoped operation replay/conflict, bounded list and readback. Migration0005 retains SELECT/INSERT-only runtime grants. Receipt: [t1-69-reviewed-investment-records.md](t1-69-reviewed-investment-records.md).

Current Self session/owned-customer/setup/read/document/update controls and fresh owner readback remain. Unconfirmed saves preserve the same operation/body across close and missing current source; API/session/access changes hide prior data.903 backend checks pass. 161 UI checks pass:111 Documents,15 Financial information,22 Loan records and13 Investment records. TypeScript/changed ESLint/Vite and zero-warning Gateway/physical test-project builds pass. Native HTTP uses explicit authority doubles/in-memory storage. Physical PostgreSQL regression is compiled, NOT RUN.

Investment corrections/lifecycle, institution verification, quantity/pricing/tax lots, multi-source reconciliation, portfolio allocation/net worth, central Audit and physical/integrated UAT remain open. After paired merge:69 numbered units =68 implementations +1 governance closure. Original29 journeys remain0 complete,17 partial,12 pending; Assets stays Partial. Workbook version11 reviewed before work. Original151 IDs/81 features/126 Phase1 memberships/source phases and acceptance remain unchanged.

## Previous T1-68 update — reviewed loan status and reopen history

Customers explicitly review a loan record as Active or Closed with a date; a Closed record can be reopened as customer reported. Existing records remain Status not reviewed until an explicit review. Financial Profile appends status versions, retains source/currency/terms and rejects stale competing term/status writes. Closing does not clear balances/EMI, change end dates or verify lender repayment. Exact operation replay returns its original status version after later reopen/corrections. Receipt: [t1-68-reviewed-loan-lifecycle.md](t1-68-reviewed-loan-lifecycle.md).

850 backend (573 VS02,146 owner,40 reprocessing,64 Reporting,27 Search) and147 UI (111 Documents,14 Financial information,22 Loan records) checks pass, with TypeScript/ESLint/Vite and zero-warning Gateway/physical test-project builds. Existing append-only persistence is reused; no migration/package/service/Actions is introduced. Current Self authority, owned-customer/setup and fresh exact-version confirmation remain required. Owner history does not close central Audit integration; full lender/current-debt truth, multi-source reconciliation, broader lifecycle and physical/integrated UAT remain open.

After paired merge:68 numbered units =67 implementations +1 governance closure. Original29 deliverables remain0 complete,17 partial,12 pending; Loans stays Partial. Workbook version10 reviewed before work. Original151 IDs/81 features/126 Phase1 memberships/source phases remain unchanged.

## Previous T1-67 update — reviewed loan corrections and history

Customers can explicitly correct previously reviewed loan terms using an expected record version. Financial Profile appends every correction, preserves original source/currency and creation receipts, and rejects stale concurrent edits. Exact operation replay returns its original version even after later changes. Current lists show the latest terms; read-only customers can inspect paged immutable history. Unconfirmed saves preserve the same body/operation/version across drawer close. Receipt: [t1-67-reviewed-loan-corrections.md](t1-67-reviewed-loan-corrections.md).

Current Self/session/owned-customer/setup/read/document/update authority and fresh exact-version readback remain required. Migration0004 retains append-only storage and SELECT/INSERT-only runtime grants. Owner history does not close central Audit integration. Closing/lifecycle, multi-source reconciliation, full asset/liability truth and physical/integrated UAT remain open. No lender verification or total debt is inferred.

816 backend (573 VS02,112 owner,40 reprocessing,64 Reporting,27 Search) and141 UI (111 Documents,14 Financial information,16 Loan records) checks pass. TypeScript, changed source/test ESLint, Vite and zero-warning Gateway/physical test-project builds pass. Paired Expert verification is recorded separately. Physical/full integrated UAT remains open.

After paired merge:67 numbered units =66 implementations +1 governance closure. Original29 deliverables remain0 complete,17 partial,12 pending; Loans stays Partial. Workbook version9 reviewed before work. Original151 IDs/81 features/126 Phase1 memberships/source phases remain unchanged.

## Previous T1-66 update — customer-reviewed loan records

Financial information > Loan information now creates/reads a customer-reviewed loan record with meaningful name, lender/type, sanction amount, outstanding/as-of date, annual rate, EMI, tenure and start/end dates. Exact previously reviewed LOAN source/revision and owner currency are retained; no term is inferred from a source figure. Financial Profile owns append-only creation with tenant/customer/actor operation identity, conflict-safe replay and source provenance. Current session/owned-customer/setup and read/evidence/update authority are rechecked; uncertain UI submissions retry the same request. Receipt: [t1-66-reviewed-loan-records.md](t1-66-reviewed-loan-records.md).

789 backend,135 UI and67 Expert checks pass after paired merge. Physical PostgreSQL regression is authored/compiled, not run. The owner creation journal is not central Audit Service closure. Editing/closing/lifecycle, multi-source reconciliation, broader terms, full asset/liability truth and live integrated UAT remain open. No lender balance or aggregate debt is inferred.

After paired merge:66 numbered units =65 implementations +1 governance closure. Original29 deliverables remain0 complete,17 partial and12 pending; Loans stays Partial. Workbook version8 reviewed through T1-65 before this increment. Original151 IDs/81 features/126 applicable memberships and source phases remain unchanged.

## Previous T1-65 update — reviewed generic asset source types

Generic ASSET text/CSV/bounded XLSX figures now require an explicit Property/Vehicle choice before current or recovered-result adoption. The shared boundary validates selected choices, preserves original source meanings/provenance and cannot override existing OCR-derived types. Canonical owner classification is visible on Financial information current and exact historical readback. Reusing one source with a different type is rejected atomically even under a new operation ID; PostgreSQL uses its existing transaction/profile lock. Receipt: [t1-65-reviewed-asset-source-types.md](t1-65-reviewed-asset-source-types.md).

746 backend,125 Documents/Financial information UI and65 Expert checks pass after paired merge. Source figures do not verify ownership or current value, create full asset records or establish net worth. Other asset kinds, full records, broader documents and live integrated UAT remain open. Physical PostgreSQL regression is authored, not run.

After paired merge:65 numbered units =64 implementations +1 governance closure. Original29 deliverables remain0 complete,17 partial and12 pending journeys. Workbook version7 reconciled through T1-64 before this increment. Original151 IDs/81 features and126 Phase1-applicable memberships/source phases remain unchanged.

## Previous T1-64 update — financial source-category compatibility

Current source review exposed a real contract mismatch: customer INSURANCE/TAX categories were sent unchanged to the Financial Profile owner, whose canonical types are INSURANCE_POLICY/TAX_RECORD. Canonical responses were also omitted from category-filtered Search/Reports and appeared as unsupported types in customer source views. Two focused owner regression cases reproduced rejection before the fix.

FinancialSourceFactBoundary generalizes the existing asset adapter. Insurance/Tax map to the canonical owner types before explicit adoption; Passport/list/detail/history/Search/basic Reports translate back to existing customer categories, retaining legacy category responses and current PROPERTY/VEHICLE behavior. No automatic adoption or new domain records. The old native insurance owner test's manual canonical translation is removed so it now exercises the shared boundary used by the Gateway. Receipt: [t1-64-financial-source-category-boundary.md](t1-64-financial-source-category-boundary.md).

Untyped generic ASSET text/CSV/XLSX source figures still cannot be adopted without a canonical subtype. No property/vehicle identity is invented, and an invalid mixed batch writes nothing. This corrects the earlier seven-category adoption implication: seven categories extract; generic untyped asset adoption remains open. Existing source phases are retained.

After paired merge:64 numbered units =63 implementations +1 governance closure. Original29 deliverables remain0 complete,17 partial and12 pending journeys. Workbook version6 was reconciled through T1-63 before this increment. Original151 IDs/81 features and126 Phase1-applicable memberships remain unchanged. Live ClamAV/physical PostgreSQL/durable processing/Compose/full browser UAT remain NOT RUN.

## Previous T1-63 update — vehicle purchase invoice OCR

The planned Vehicle Document class (Master sections11–14/41) now includes supported printed-English vehicle purchase invoices in PNG, upright JPEG and unlocked single-page scanned PDF. One early VEHICLE PURCHASE INVOICE title, one Invoice Date/Date of Invoice, one explicit Purchase Value and matching supported currency yield one dated ASSET source candidate. Registration date, invoice total, insured/market/estimated values and filenames are not purchase-value/date substitutes. Mixed titles and incomplete, ambiguous, invalid, conflicting or low-confidence required fields yield no facts.

Original scan/hash/customer/tenant/native controls and explicit customer selection/confirmation remain. The existing property adapter is generalized to AssetSourceFactBoundary: exact vehicle OCR versions map ASSET to canonical VEHICLE; existing property versions keep canonical PROPERTY. Passport/list/detail/history/Search/basic Reports present both as ASSET source figures with original meaning and provenance. Current/recovered Documents identify the invoice; recovered views remain read-only. Receipt: [t1-63-vehicle-purchase-ocr.md](t1-63-vehicle-purchase-ocr.md). No ownership/authenticity/current-value/depreciation or full vehicle-record claim is made.

After paired merge:63 numbered units =62 implementations +1 governance closure. Original29 deliverables remain0 complete,17 partial and12 pending journeys. Workbook version5 was reconciled through T1-62 before this increment; the next workbook version carries T1-63. Original151 IDs/81 features and126 Phase1-applicable memberships remain unchanged. Broader financial model source phases are retained. Live ClamAV/physical PostgreSQL/durable processing/Compose/full browser UAT remain NOT RUN.

## Previous T1-62 update — property registration document OCR

The planned Registration Document class (Master sections11–13/41) now includes printed-English property registration documents in supported PNG, upright JPEG and unlocked single-page scanned PDF. One early PROPERTY REGISTRATION DOCUMENT title, one Registration Date/Date of Registration, one explicit Purchase Value and matching supported currency yield one dated ASSET source candidate. Registration date is not purchase date. Deed date, market/estimated value and filenames are not substitutes. Ambiguous, incomplete, conflicting, mixed-title and low-confidence input yields no facts.

Original scan/hash/customer/tenant controls and explicit customer selection/confirmation remain. Exact registration OCR versions use the existing canonical PROPERTY boundary. Current and recovered Documents preserve registration source meaning; recovered views remain read-only. Receipt: [t1-62-property-registration-ocr.md](t1-62-property-registration-ocr.md). No ownership/authenticity/current-value or full property-record claim is made.

After paired merge:62 numbered units =61 implementations +1 governance closure. Original29 deliverables remain0 complete,17 partial and12 pending journeys. Workbook version4 was reconciled through T1-61 before this increment; the next workbook version carries T1-62. Original151 IDs/81 features and126 Phase1-applicable memberships remain unchanged. Live ClamAV/physical PostgreSQL/durable processing/Compose/full browser UAT remain NOT RUN.

## Previous T1-61 update — property sale deed OCR

The planned Property Sale Deed class (Master sections11–13/41) supports printed-English PNG, upright JPEG and unlocked single-page scanned PDF. Exactly one early title, one Deed Date/Sale Deed Date, one Purchase Value and an explicit matching supported currency yield a single dated ASSET source candidate. Mixed titles, missing/duplicate/invalid/conflicting or low-confidence required fields yield no facts. Filename, registration date and estimated/market value are not substitutes.

Original-byte malware/hash/customer/tenant and native process controls remain. Current and recovered Documents explain the source meaning. Only explicit customer selection and confirmation adopt the figure; recovered views remain read-only. No ownership, authenticity, current valuation, net worth or complete property-record claim is made. Verification and paired source identities are recorded in [t1-61-property-sale-deed-ocr.md](t1-61-property-sale-deed-ocr.md). Live ClamAV/physical PostgreSQL/Compose/full browser UAT remain unrun.

After paired merge:61 numbered units =60 implementations +1 governance closure. Original29 deliverables remain0 complete,17 partial and12 pending journeys. Workbook version3 was reconciled through T1-60 before this increment; the next workbook version carries T1-61. Original151 IDs/81 features and126 Phase1-applicable memberships remain unchanged.

## Previous T1-60 update — loan sanction letter OCR

Supported PNG, upright JPEG and single-page scanned PDF letters now yield two distinct LOAN source candidates: sanctioned amount and EMI, with explicit matching currency and Sanction Date. Exactly one early sanction title and complete high-confidence labelled fields are required. Mixed/duplicate titles, missing/duplicate/invalid/conflicting fields and unsupported native inputs produce no facts. No verified offer, disbursement, current debt, repayment status, amortization or authenticity is inferred.

Original bytes, malware/hash/tenant/customer gates and native process controls remain. Current/recovered Documents views distinguish sanction letters from loan statements. Only independently selected and confirmed terms are adopted; recovered views remain read-only. A prior reviewed outstanding balance retains its separate date/value/revision. Evidence:377 VS02 checks including53 new sanction cases and52 NativeOcr-tagged checks,40 reprocessing and82 Documents UI checks including7 new cases pass. TypeScript/lint/Vite and zero-warning Gateway/owner compilation pass. Physical PostgreSQL/live Docker/ClamAV/Compose/full browser UAT remain NOT RUN. Receipt: [t1-60-loan-sanction-ocr.md](t1-60-loan-sanction-ocr.md).

The approved29-deliverable review remains0 complete,17 partial and12 pending business journeys.59 implementation units plus1 governance closure account for60 numbered units after paired merge; T1-04 is governance. Original151 IDs/81 features and126 Phase1-applicable memberships remain unchanged. Current row evidence is phase1-deliverable-status.json; older counting snapshots are historical.

## Phase 1 counting correction — 2026-10-18

The original master section41 MVP checklist has **29 deliverables** (Customer4, Documents6, Cloud2, Financial Data5, Dashboard5, AI3, Admin4). The supplied v0.4 reconciliation workbook's `Requirements 151` worksheet has **126 Phase1-applicable requirements**:38 tagged1,23 tagged1+,65 taggedAll. Its other25 requirements are tagged later phases. These counts describe different levels of scope; the original151 requirements/81 canonical features are unchanged. The16 recording-derived candidates are not added to the original151.

At the start of T1-52, **51 numbered implementation increments had merged** (App main668f8b0 / Expert main6766ac9). T1-52 adds one bounded increment, making52 after its paired merge. An increment is neither a whole requirement nor a whole MVP deliverable and cannot be subtracted from126 or29 to count pending work.

The workbook's implementation observations stop at **T1-42** and every requirement's business acceptance is `NOT VERIFIED`. Therefore **0 of the126 applicable requirements have recorded end-to-end business closure;126 remain formally open, including implemented/partial work**. This is a register/acceptance count, not a finding that no code works. Fully implemented/partial/unstarted requirement counts and the number of future implementation increments are **not reconciled**, not zero. Existing unit CTO acceptances and focused native receipts remain valid within their stated boundaries. No product completion percentage is asserted.

The reproducible pre-T1-52 snapshot, original phase membership and exact source references are in [phase1-closure-status.json](phase1-closure-status.json). This correction supersedes stale counting/verification wording below; historical records remain evidence for their own revisions.

This file tracks bounded implementation units; the accepted 81-feature catalog and 151 requirement IDs remain authoritative for requirements identity, and the AR-003 persona and capability catalogs remain authoritative for planned journeys.
One current implementation unit may be a subset of one canonical feature. Engineering completion never proves an entire business feature complete.

## Definition of done

Each unit requires: owner-backed UI/API/data behavior; tenant/customer access, applicable Consent and fail-closed unavailable states; focused local tests and required security verification; explicit source/evidence identity; System Expert knowledge synchronization and consistency verification; updated traceability; CTO acceptance. No development-phase GitHub Actions. No acceptance or main merge based solely on source inspection.

## Current execution queue — complete Phase 1 journeys

This section supersedes the historical priority/verification labels below. Latest unit receipts establish focused native evidence; they do not erase unexecuted integrated UAT or close compound requirements.

| Priority | Outcome / current implementation | Next dependency or meaningful unit |
| --- | --- | --- |
| P0 / implemented T1-43 | Email-code access for an existing imported customer with exact active ownership; no browser code retrieval or role creation | Application #93 and Expert #45 merged with focused native evidence. Actual SMTP delivery and owner PostgreSQL UAT remain open |
| P0 / implemented T1-44, T1-55–56 | Customer-owned profile: name, optional unverified contact, self-declared DOB and protected masked PAN; email read-only | Owner validation, versioned edits, exact readback and PAN encryption/replay/audit. Authoritative KYC/profile completion, physical PostgreSQL and integrated UAT remain open; evidence in t1-56-protected-customer-pan.md |
| P0 / implemented T1-45 | Own session activity and selected other-session termination under Settings; exact actor scope, operation receipt and fresh owner readback | Application #95 and Expert #47 merged; focused native evidence in t1-45-self-sessions.md. Actual PostgreSQL replay/concurrency/grants/UAT remain open; devices/MFA and complete Settings are not implied |
| P0 / implemented T1-46 | Create a scoped customer-advice sharing permission for an existing organization email recipient; confirm exact scope/expiry and fresh owner readback; existing revoke uses the new revision | Application #96 and Expert #48 merged. Native checks in t1-46-customer-sharing.md. Changes/renewal advance in T1-47; PostgreSQL UAT, provider consent and effective recipient-workspace acceptance remain open |
| P0 / implemented T1-47 | Change an unrevoked grant's scope/expiry or renew an expired grant for the same recipient; atomic revoke/create with preserved history, exact receipts and fresh view | Application #97 and Expert #49 merged; native checks in t1-47-sharing-lifecycle.md. Five physical database tests authored, not executed; provider connection lifecycle and complete Consent acceptance remain open |
| P0 / implemented T1-53–54 | Existing-customer account agreements and opt-in guided setup with server financial admission | Public registration/account creation and complete activation remain pending. Reuse the owner authority; actual SMTP/PostgreSQL/Compose UAT and approved legal content remain required. MON-ID-001–005/012 remain partial/open |
| P0 / implemented T1-48 | LOCAL customer upload malware-check gate and pinned ClamAV sidecar wiring; no accepted intake without current verified bytes | Focused native scan protocol evidence is retained; live Docker/ClamAV/EICAR is NOT RUN. MON-SEC-008, durable scan evidence, quarantine/rescanning and production operations remain partial/open |
| P0 / implemented T1-49–52 | Salary Slip PNG/PDF/JPEG OCR, then Loan Statement classification/OCR across those same formats; original source and explicit review/adoption | T1-52 focused native187 VS02 +40 reprocessing,54 Documents UI cases pass. Broader classes/layouts/languages, persistent classification and integrated UAT remain open; MON-DOC-004/005/006 and MON-FIN-006 remain partial |
| P0 / implemented T1-57 | Download/upload a bounded financial information XLSX template; original bytes, seven source categories and explicit review/adoption | Application and paired Expert receipts record exact source. Legacy/macro workbooks, arbitrary bank layouts, broader file matrix and live scanner/durable integration remain open |
| P0 / implemented T1-58 | Insurance Policy OCR in PNG/upright JPEG/single-page PDF; dated sum assured and premium retain separate meanings and explicit selection/adoption | Receipt t1-58-insurance-policy-ocr.md records pinned native and owner HTTP proof. Complete policy records, verification, broader layouts/classes, persistent classification and live integrated UAT remain open |
| P0 / implemented T1-59 | Investment Statement OCR in PNG/upright JPEG/single-page PDF; purchase/current values retain distinct dated meanings and independent customer selection | Receipt t1-59-investment-statement-ocr.md records pinned native and owner HTTP proof. Securities/quantity/pricing/tax lots, full records, broader classes/layouts, persistent classification and live integrated UAT remain open |
| P0 / implemented T1-60 | Loan Sanction Letter OCR in PNG/upright JPEG/single-page PDF; sanctioned amount and EMI preserve dated source meaning and independent review | Receipt t1-60-loan-sanction-ocr.md records pinned native/owner HTTP proof and coexistence with prior outstanding balances. Full loan records, disbursement/current debt, verification, broader classes/layouts and live integrated UAT remain open |
| P0 / implemented T1-61 | Property sale deed OCR in supported PNG/JPEG/single-page PDF; dated purchase value with explicit review/adoption | Exact evidence is in t1-61-property-sale-deed-ocr.md. Complete property records, ownership/valuation verification, wider document coverage and integrated UAT remain open |
| P0 / implemented T1-62 | Property registration document OCR; explicit stated purchase value and registration date with independent review/adoption | Receipt t1-62-property-registration-ocr.md. Registration date is not purchase date; full records, ownership/valuation verification and integrated UAT remain open |
| P0 / implemented T1-63 | Vehicle purchase invoice OCR; explicitly stated purchase figure and invoice date with independent review/adoption | Receipt t1-63-vehicle-purchase-ocr.md. Canonical VEHICLE owner storage and ASSET source presentation; full records/current valuation/ownership and integrated UAT remain open |
| P0 / implemented T1-64 | Insurance/Tax review/adoption, canonical owner types and customer source views | Receipt t1-64-financial-source-category-boundary.md. Owner HTTP/replay/provenance and canonical/legacy projection checks; generic untyped ASSET adoption, full records and integrated UAT remain open |
| P0 / implemented T1-65 | Explicit generic asset Property/Vehicle review and canonical type readback | Receipt t1-65-reviewed-asset-source-types.md. Text/CSV/XLSX choices and historical source type immutability; other asset kinds, full records and live integrated UAT remain open |
| P0 / implemented T1-66–68 | Reviewed source-linked loan create/read, corrections/history and dated Active/Closed/reopen review | Receipts t1-66-reviewed-loan-records.md through t1-68-reviewed-loan-lifecycle.md. Original source/currency/amounts and all prior versions retained; exact operation/version confirmation. Lender/current-debt truth, multi-source reconciliation, broader lifecycle, central audit and physical/integrated UAT remain open |
| P0 | Google Drive / OneDrive customer storage journey | Implement actual authorized provider import/status/refresh/disconnect, preserving exact folder/file/document identities; reference gateway is insufficient |
| P0 / implemented T1-69–70 | Customer-reviewed investment create/list/read/corrections/history | Explicit fields, version checks, exact original source/currency and immutable replay/history. Lifecycle, provider verification, security/quantity/pricing, portfolio calculations and integrated UAT remain open |
| P0 | Full Loan account and financial truth | Seven source categories exist. Complete lender/type/sanction/outstanding/rate/EMI/tenure/dates and required asset/liability records; a typed source amount does not complete MON-FIN-006 |
| P0 | Deterministic net worth | Assets minus Liabilities is already specified. Complete authoritative dated/currency/valuation inputs and customer calculation/provenance; scoring approval is a distinct gate |
| P0 | Basic authorized financial Q&A | Seven-category lexical Search/basic Reports exist; complete grounded Q&A, source citations and insufficient-data behavior under the approved model/privacy boundary |
| P0 | Phase 1 business administration / notifications | Complete customer/document/processing/audit operating journeys and the explicitly baselined notification subset; RBAC setup alone is insufficient |
| Carry-forward verification | Source-matched System Expert through latest paired receipt; focused native suites for T1-20..60 supersede old authored-only/stale-compile statements | Live PostgreSQL integrated UAT remains NOT RUN. Do not repeat already passed full checks without a new defect/source change or mark native test adapters as database UAT |
| Later source phases / approved direction | AA/bank/CIC, scoring, formal packs, advanced AI, professional and marketplace workflows | Preserve specific provider/methodology/assignment/case ownership gates and source phases; accepted role foundations are not complete professional workspaces |

## Historical queue context — superseded by current queue and exact receipts

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
| P0 / carry-forward UAT | T1-40 Tax source figures in basic Reports | Master §§13,20,30,41; WS08 partial | Reporting → Financial Profile CID-030/033 → Customer Reports/export | IMPLEMENTED_FOCUSED_NATIVE_VERIFIED: 4 owner tests, 8 Reports component tests, TypeScript/test lint passed | Owner-backed PostgreSQL UAT remains open; full compilation is recorded in the current receipt; see t1-40-tax-source-reporting.md |
| P0 / carry-forward UAT | T1-41 Tax source coverage in Action Centre | Master §§13,20,39,41; WS04/08 partial | Financial Profile → Customer Action Centre | IMPLEMENTED_FOCUSED_NATIVE_VERIFIED: 10 Action Centre component tests, TypeScript and test lint passed | Owner-backed PostgreSQL UAT remains open; full compilation is recorded in the current receipt; see t1-41-tax-action-centre.md |
| P0 | Expand Financial Passport beyond Income/Expense by one governed fact domain per unit | MR §§2,13,20,41; M2-WS04-E01/E02 | Financial Profile owner and customer UI | FOUNDATIONS_EXIST; customer journeys partial | Approved domain input/mutation semantics and evidence source; no fabricated net worth |
| P0 / carry-forward UAT | T1-42 Financial information CSV intake and candidate extraction | Master §§9–12,13,41; COV-011/014 partial | Evidence original bytes/hash → Document Intelligence explicit CSV → customer review → existing Financial Profile normalization | IMPLEMENTED_FOCUSED_NATIVE_VERIFIED: 54 VS-02, 34 Documents component tests; gateway/TypeScript/test lint passed | Paired Expert receipt records source/compilation; owner-backed UAT remains open; no OCR/Excel/provider claim |
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

T1-01 through T1-19 preserve existing individual CTO acceptance and merge evidence, including PR #66 at application main `241da03742893d8c362503365a6fa8f1fad80d36`. They are **not** collectively a complete Monergy product. That historical implementation crosswalk was scoped at Income/Expense. Source-merged extensions now cover Loan, Asset, Investment, Insurance and Tax in Passport/structured-text extraction and Search; Reports now include Tax source figures with focused native verification; Action Centre now includes Tax source coverage as its seventh category. They retain source-figure semantics, without financial valuation, tax assessment or production-provider claims.

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

The master requirements and the supplied two-video analysis were inspected during this continuation. The independent shared-conversation URL could not be retrieved; no requirement was inferred from it. Provider choices, finance methodology, professional workflows and production decisions retain their existing gates. The bounded native verification remediation is recorded in the current receipt. T1-21 revocation and T1-22 recipient labels have source merges; their live owner-backed UAT remains pending. Next priority is owner-backed PostgreSQL UAT for the implemented customer journey; document extraction/provider, financial-methodology, professional-workflow and production work retain their listed approved dependency gates.

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

## Full Expert compilation receipt

[2026-10-17 full current-source compilation](evidence/t1-41-full-expert-compile-20261017.md) supersedes earlier NOT_RUN/STale/full-compile-pending statements for the documented source revisions. Historical acceptance and source locks are retained. Current-source artifacts are separate from the historical baseline. Live owner UAT and product acceptance remain pending.
