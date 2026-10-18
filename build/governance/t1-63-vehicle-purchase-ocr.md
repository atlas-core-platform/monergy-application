# T1-63 — Vehicle purchase invoice OCR and explicit review

Base Application `fcd8aa6ea47dc23a5430a92258020b150b1d8844` (PR112), Expert
`8d892728258b0656ad320034d3dc8fa1fd232a69` (PR64), Architecture
`90934b51b2b203795acf95da2252a2cf1a1d1f4c`. Branch
`work/t1-63-vehicle-purchase-ocr`.

Master sections11–14/41 includes Vehicle Document classification/OCR/extraction
and asset source figures. This unit covers a bounded vehicle purchase invoice
layout. MON-DOC-004/005/006/007 and Classification/OCR/Extraction/Assets remain
Partial. The broader MON-FIN-010 source phase and all original phase memberships
remain unchanged; full vehicle records and financial models are not delivered.

Supported printed-English PNG, upright JPEG and readable unlocked single-page
scanned PDF require exactly one VEHICLE PURCHASE INVOICE title in the first ten
OCR lines. Any second known title anywhere, including property sale deeds and
property registration documents, rejects classification. One Invoice Date/Date
of Invoice, one Purchase Value and explicit matching supported currency are
required. Required words meet the existing90-percent minimum confidence. Shared
strict amount/grouping/precision, eight currencies, unambiguous ISO or English
dates, bounded TSV and native process/format/deadline/cancellation controls apply.
Registration Date, Invoice Total, insured/market/estimated values and filenames
are not substitutes. Missing/duplicate/ambiguous/invalid/conflicting/unclear
required fields commit zero facts and a typed nonretryable terminal failure.

One ASSET candidate retains Purchase value stated on vehicle purchase invoice,
invoice date, confidence, exact original Evidence/customer/document version and
line/date provenance. Identity binds field/run/document version. Versions are
vehicle-purchase-invoice-{png|jpeg|pdf}-ocr/1.0.0. Literal zero remains literal.
No registration date, ownership/authenticity/current value, depreciation,
valuation, net worth or complete vehicle record is inferred. Original-byte
malware/hash/customer/tenant gates remain.

Current and recovered Documents identify the invoice and preserve its source
meaning. Explicit selection and confirmation precede adoption; recovered views
remain read-only. The existing property source adapter is generalized to
AssetSourceFactBoundary. Only exact vehicle OCR versions map customer ASSET to
canonical VEHICLE; existing property OCR versions retain PROPERTY. Arbitrary
ASSET sources and other object types are unchanged. Passport list/detail/history,
Search and basic Reports present PROPERTY/VEHICLE as ASSET source figures without
changing their original labels, dates, identifiers or provenance. Upload and
extraction cannot automatically write financial truth. No new package, service,
migration or development GitHub Actions.

## Verification

547 VS02 checks pass:58 new vehicle cases and3 owner HTTP formats. Final
 t63-verified TRX records547 passed/0 failed. Real pinned Tesseract5.3.4,
hash-pinned English data and Poppler24.02.0 read synthetic PNG/JPEG/image-only PDF
and reject incomplete/mixed sources. The positive fixture contains a distinct
registration date and market value, neither of which replaces invoice date or
purchase value. The synthetic image was visually inspected. Actual Evidence
HTTP checks original bytes/hash/customer/tenant and explicit Financial Profile
normalization to canonical VEHICLE with retained source provenance. Scanner and
repositories are explicit doubles; physical ClamAV is excluded, not passed.

103 Documents UI cases pass, including7 new vehicle cases:three review/adoption
formats, three recovered read-only formats and a typed failure message.
TypeScript, changed-file lint and Vite pass.40 reprocessing,6 affected Reporting
and7 affected Search checks pass,600 backend total. Reporting/Search exercise
legacy ASSET and canonical PROPERTY/VEHICLE owner responses while retaining
provenance and supported category filtering. Gateway/owner build has zero
warnings/errors. Existing assets used without tracked package/lock changes; a
fresh historical locked restore is not demonstrated. SDK10.0.401 matches pin;
available Node24.19.0 does not prove pinned24.21.

Live ClamAV benign/EICAR, physical PostgreSQL/durable OCR/classification,
PowerShell/Compose and full integrated browser UAT remain NOT RUN. Other vehicle
layouts/types, broader classes/languages/pages, registration/ownership/current
valuation verification and full vehicle/financial models remain open. No
business/production acceptance or source-phase/stage-gate advancement is inferred.

After paired merge:63 numbered units =62 implementations +1 governance closure
(T1-04). Original29 deliverables remain0 complete,17 partial and12 pending journeys.
Original151 IDs/81 features and126 Phase1-applicable memberships retained. Source
tracker:phase1-deliverable-status.json. Exact merged source and paired knowledge
compilation/synchronization are recorded in the Expert receipt.
