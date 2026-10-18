# T1-52 — Planned Loan Statement OCR and Phase1 count correction

Base App main: `668f8b06f90adf1a248b65aca2cb7d3edfe7bb8a` (PR101); Expert base `6766ac966b684723190c67617e45283061b4001c` (PR53). Architecture `90934b51b2b203795acf95da2252a2cf1a1d1f4c`. Branch `work/t1-52-loan-statement-ocr`.

## Scope and source meaning

Master sections11/12 explicitly plan Loan Statement classification and OCR, with section41 document/financial-data deliverables; AR-003 COV-011/013/014 and MON-DOC-004/005/006 partial coverage. MON-FIN-006 full loan account remains open. This extends the existing salary OCR path to printed English loan statements in PNG, upright JPEG and unlocked single-page scanned PDF. No provider, dependency/pin/image, permission/role, Consent authority, schema/migration or cross-service SQL changes.

The existing three format adapters now use one bounded TSV classifier/field validator. Exactly one supported title in the first ten lines, no second known title anywhere, exact explicit outstanding balance/date/currency and >=90 minimum selected-word confidence are required. Duplicate/missing/conflicting/ambiguous fields and mixed known classes fail with zero facts. Shared currency validation now explicitly refuses both prefix and suffix codes; a new negative fixture exposed the old repeated-capture check's gap under the nonbacktracking regex engine. Salary source IDs, labels and extraction versions remain stable for valid inputs.

One LOAN candidate retains `Outstanding loan balance stated on document` or the distinct `Principal outstanding stated on document` meaning, original Evidence/customer/document version, statement date, currency, confidence and source location. Version is `loan-statement-{png|jpeg|pdf}-ocr/1.0.0`. Sanctioned amount/EMI/transaction figures are not another balance or calculated into debt. It does not independently establish current debt, a complete loan account or net worth. Native preflight, exact hash, private temporary input/deadlines/cleanup and current owner/session authorization remain unchanged.

Current and recovered customer views recognize Loan Statement, explain the dated source meaning and require explicit selection/adoption through the existing Financial Profile owner. No automatic financial write. Incomplete loans have safe nonretryable `processing.loan-statement.incomplete`; common unclassified/low-confidence and native/format failures retain typed behavior.

## Count correction

The source workbook v0.4 version1 worksheet Requirements151 and Master section41 were reread. Original Phase1 MVP: **29 checklist deliverables**. Applicable original requirements: **126** (38 phase1,23 phase1+,65 All);25 later-phase rows are excluded. The original151/81 denominator remains unchanged, and16 recording candidates are separate.

Pre-unit source had **51 merged increments**; this adds T1-52. Workbook observations stop at T1-42, with `NOT VERIFIED` business acceptance on every original row. **0 recorded applicable requirement closures /126 formally open** does not mean zero code implemented. Current implemented/partial/unstarted requirement counts and future increment count are unreconciled (null), not invented by subtracting51 from126/29. Existing individual unit acceptance is preserved. `phase1-closure-status.json` records the exact pre-T1-52 source pins, memberships and counts; tracker now explains these units and current OCR evidence.

## Verification

187 VS02 checks pass (33 added:29 loan field/classification/native/failure cases,3 loan owner HTTP extensions,1 salary duplicate-inline-currency regression).40 reprocessing checks pass. Actual pinned Tesseract5.3.4/hash-pinned English data and Poppler24.02.0 read synthetic PNG/JPEG/image-only PDF with exact original provenance and distinct version identity. Actual Evidence HTTP transports each original format through customer/tenant-scoped processing; scanner fixtures are explicit doubles. Negative cases cover missing/conflicting/duplicate fields, ambiguous dates, invalid currencies/amounts/grouping, unknown/mixed/duplicate/out-of-position titles and typed zero-fact terminal failures. The PDF fixture was rendered and visually inspected.

54 Documents UI cases pass (7 added:3 format-specific explicit loan adoption cases,3 recovered loan recognition/read-only cases,1 incomplete-loan message). TypeScript, all changed source/test lint, Vite and Gateway build pass; Gateway zero warnings/errors. Evidence/Document Intelligence/VS02/reprocessing compile. JSON membership/count consistency and diff checks pass. No lockfiles or development GitHub Actions change/run.

The initial broad VS02 command used the wrong physical-test exclusion and hit the live-ClamD test's missing-fixture guard (187 other checks passed). The corrected explicit PhysicalClamdTests exclusion is the reported187-case run. This is not live scanner evidence.

## Remaining acceptance

Live Docker/ClamAV benign/EICAR, final browser -> native OCR -> explicit adoption, PostgreSQL UAT, full PowerShell/Compose and pinned Node24.21 remain NOT RUN. Native runtime SDK10.0.401 matches its pin; available Node24.19.0 is recorded. Broader loan classes/layouts/languages, full account fields/current-debt semantics, persistent OCR/classification and operations review remain planned. Source/native proof does not close entire document/loan requirements or confer production/business acceptance. Exact merged App identity and mandatory Expert sync are recorded in the matching T1-52 Expert receipt/delta.
