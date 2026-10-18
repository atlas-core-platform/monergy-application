# T1-59 — Investment statement OCR and explicit review

Base Application `45e139d64a2a9e9ac3f5d5255a5900fa5536bbc4` (PR108), Expert
`570dbf2c65ee73a0e03e38cd21c710a795982514` (PR60), Architecture
`90934b51b2b203795acf95da2252a2cf1a1d1f4c`. Branch
`work/t1-59-investment-statement-ocr`.

## Planned scope and source meaning

Master §§11–13/41 explicitly includes Investment Statement classification,
scanned extraction and purchase_value/current_value fields. This bounded document
unit advances MON-DOC-004/005/006/007 and the existing T1-30 investment source-fact
journey. Original requirement identities/phases and full investment-record scope
are unchanged; Classification/OCR/Extraction remain Partial.

Existing PNG, upright JPEG and readable unlocked single-page scanned PDF adapters
recognize exactly one INVESTMENT STATEMENT title within the first ten OCR lines.
Any second known salary/loan/insurance/investment title anywhere rejects the page
as unclassified. Printed English and the existing native input/format limits
apply; filename alone does not classify a document.

A valid page has exactly one Statement Date, Purchase Value and Current Value
label, with a supported explicit currency on each amount or one matching Currency
label. Both amounts must share currency. Existing eight-currency, nonnegative
amount/grouping/precision, exact ISO or unambiguous English date, bounded TSV and
minimum90-percent required-word confidence checks apply. Transaction Date,
Market Price, quotations and other labels are not substituted. Every required
field validates before either candidate becomes visible.

The two INVESTMENT candidates retain distinct labels: Purchase value stated on
statement and Current value stated on statement. Both carry the document's
Statement Date, exact original Evidence/customer/document version, confidence and
distinct line provenance. Identity binds original version, processing run and
field; version is investment-statement-{png|jpeg|pdf}-ocr/1.0.0. Source-location
date formatting is invariant. A zero or lower current value remains a literal
source figure. No gain/return arithmetic, live price, verified position, cost-basis
reconciliation, authoritative asset valuation or net-worth inference occurs.

Incomplete investment has safe nonretryable
processing.investment-statement.incomplete with zero committed facts and one
terminal failure event. Common unclassified/low-confidence/native/format/deadline/
cancellation behavior remains. Original-byte malware gating, SHA256, exact
customer/tenant scope and native private-input lifecycle remain unchanged.

Current and recovered Documents views recognize investment and explain dated
literal figures. Current review allows independent selection and explicit
confirmation through the existing Financial Profile INVESTMENT mapping. Recovered
read-only results cannot adopt. Upload/extraction alone never updates financial
truth. Customer guidance and typed failures describe the exact required fields.

## Verification

324 VS02 checks pass, including50 new investment cases and44 NativeOcr-tagged
checks;40 reprocessing checks pass. New cases exercise real pinned PNG/JPEG/
image-only PDF extraction with both original source figures, native incomplete/
mixed insurance-title rejection, valid date/currency forms, zero/lower current
values, missing/duplicate/invalid/conflicting fields, low confidence on every
required line, mixed/late/duplicate/unknown titles and typed zero-fact failures.
Actual Evidence HTTP transports each original format through exact binary/hash,
customer/tenant denial and processing boundaries to pinned OCR. Explicit Financial
Profile adoption and original extraction/version provenance are verified after the
existing INVESTMENT mapping. Scanner/repository dependencies are explicit doubles,
not live ClamAV or physical PostgreSQL.

75 Documents UI checks pass, including7 new cases:three format-specific independent
current-value selections with no automatic adoption,three recovered investment/
read-only views and one incomplete-statement message. TypeScript, changed frontend
source/test lint, Vite and zero-warning Gateway/owner compilation pass. The
synthetic scanned PDF was rendered and visually inspected. Native Tesseract5.3.4,
hash-pinned English traineddata and Poppler24.02.0 are existing local adapters.
No new package, service, migration or development GitHub Actions. SDK10.0.401
matches pin; available Node24.19.0 does not prove pinned24.21.

Live Docker/ClamAV benign/EICAR intake, physical PostgreSQL/durable OCR/classification,
PowerShell/Compose and full integrated browser UAT remain NOT RUN. Broader document
classes/layouts/languages, multipage statements, securities/quantity/tax lots,
pricing/provider feeds and complete investment/account records remain open. No
business or production acceptance is inferred.

After paired merge,59 numbered units comprise58 implementation units and1
governance closure (T1-04). Original29 deliverables remain0 complete,17 partial
and12 pending journeys. Original151 IDs/81 features and126 Phase1-applicable
membership are unchanged. Exact merged Application source/tree and compiled
knowledge are in the paired Expert receipt.
