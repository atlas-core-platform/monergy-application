# T1-60 — Loan sanction letter OCR and explicit review

Base Application `da15b2d989346cafd6085b7af83e6215d7fdad5a` (PR109), Expert
`e77d2bd62dab1bafdd716cdc0c0e4589f9c4fc74` (PR61), Architecture
`90934b51b2b203795acf95da2252a2cf1a1d1f4c`. Branch
`work/t1-60-loan-sanction-ocr`.

## Planned scope and source meaning

Master §§11–13/41 explicitly includes Loan Sanction Letter classification,
scanned extraction and loan sanction_amount/emi fields. This bounded document
unit advances MON-DOC-004/005/006/007 and the existing partial MON-FIN-006 loan
source-fact journey. Full loan records and current-debt semantics remain open.

Existing PNG, upright JPEG and readable unlocked single-page scanned PDF adapters
recognize exactly one LOAN SANCTION LETTER title within the first ten OCR lines.
Any second known salary/loan-statement/sanction/insurance/investment title anywhere
rejects classification. Printed English and existing native preflight limits
apply. Filename alone does not classify a document; Loan Offer/Application is
not substituted for the explicitly supported title.

A valid page has exactly one Sanction Date, Sanctioned Amount (or Sanction Amount)
and EMI (or EMI Amount) label, with a supported explicit currency on each amount
or one matching Currency label. Both amounts share currency. Existing eight-
currency, nonnegative amount/grouping/precision, exact ISO or unambiguous English
date, bounded TSV and minimum90-percent required-word confidence checks apply.
Disbursement Date and Outstanding Balance are not substituted. Both terms
validate before either candidate becomes visible.

The two LOAN candidates retain distinct labels: Sanctioned loan amount stated on
letter and EMI stated on sanction letter. They carry the letter's Sanction Date,
original Evidence/customer/document version, confidence and distinct line
provenance. Identity binds original version, processing run and field; version is
loan-sanction-{png|jpeg|pdf}-ocr/1.0.0. Source date formatting is invariant.
Neither is an outstanding balance or a verified offer/disbursement/current debt/
repayment status/authenticity assertion. Zero EMI remains a literal source term;
no tenure, rate, amortization or balance arithmetic is inferred.

Incomplete sanctions have safe nonretryable processing.loan-sanction.incomplete
with zero committed facts and one terminal failure event. Common unclassified,
low-confidence, native unavailable/format/deadline/cancellation behavior remains.
Original-byte malware gating, SHA256, exact customer/tenant scope and native
private-input lifecycle remain unchanged.

Current and recovered Documents views distinguish a sanction letter from a loan
statement and explain source meaning. Current review allows independent selection
and explicit confirmation through the existing Financial Profile LOAN mapping;
recovered read-only views cannot adopt. Upload/extraction alone never updates
financial truth. Sanction/EMI labels remain separate from prior reviewed
outstanding-balance labels and do not replace that balance merely by adoption.

## Verification

377 VS02 checks pass, including53 new sanction cases and52 NativeOcr-tagged checks;
40 reprocessing checks pass. Final backend TRX counters confirm377 total/377 passed/0 failed. New cases include real pinned PNG/JPEG/image-only PDF
extraction, native incomplete/mixed loan-statement rejection, explicit aliases,
zero EMI, missing/duplicate/invalid/conflicting fields, low confidence on every
required line, mixed/late/duplicate/unknown titles and typed zero-fact failures.
A Financial Profile test proves that adopting the two terms preserves a previously
reviewed outstanding balance, date and revision as a separate third figure. The
old unknown-title regression now uses Loan Application because Sanction Letter
is newly supported; malformed sanctions have their own typed failure cases.

Actual Evidence HTTP transports each original format through exact binary/hash,
customer/tenant denial and processing boundaries to pinned OCR. Explicit Financial
Profile adoption and original provenance are verified. Native fixtures also
contain outstanding/disbursement fields that are not adopted. Scanner/repository
dependencies are explicit doubles, not live ClamAV or physical PostgreSQL.

82 Documents UI checks pass, including7 new cases:three format-specific independent
EMI selections with no automatic adoption,three recovered sanction/read-only views
and one incomplete-sanction message. TypeScript, changed source/test lint, Vite
and zero-warning Gateway/owner compilation pass. The synthetic scanned PDF was
rendered and visually inspected. Native Tesseract5.3.4, hash-pinned English data
and Poppler24.02.0 are existing local adapters. No new package, service, migration
or development GitHub Actions. SDK10.0.401 matches pin; available Node24.19.0 does
not prove pinned24.21.

Live Docker/ClamAV benign/EICAR intake, physical PostgreSQL/durable OCR/classification,
PowerShell/Compose and full integrated browser UAT remain NOT RUN. Broader document
classes/layouts/languages, multipage letters, lender/type/rate/tenure/start/end
records, loan linking and complete loan/account records remain open. No business
or production acceptance is inferred.

After paired merge,60 numbered units comprise59 implementation units and1
governance closure (T1-04). Original29 deliverables remain0 complete,17 partial
and12 pending journeys. Original151 IDs/81 features and126 Phase1-applicable
membership are unchanged. Exact merged Application source/tree and compiled
knowledge are in the paired Expert receipt.
