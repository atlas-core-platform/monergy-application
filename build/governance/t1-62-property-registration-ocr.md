# T1-62 — Property registration document OCR and explicit purchase-value review

Base Application `f8afba4bcda3c82e7459b2f4a8141a110612474e` (PR111), Expert
`dd22186d49c8684a30131942fd1044ab03578907` (PR63), Architecture
`90934b51b2b203795acf95da2252a2cf1a1d1f4c`. Branch
`work/t1-62-property-registration-ocr`.

Master sections11–13/41 list Registration Document classification, OCR/extraction
and property purchase_value. This unit covers a narrow property-registration
layout within that planned class. MON-DOC-004/005/006/007, MON-FIN-009 and the
Classification/OCR/Extraction/Assets deliverables remain Partial. Other
registration types/layouts and complete property records remain open.

Supported printed-English PNG, upright JPEG and readable unlocked single-page
scanned PDF require exactly one PROPERTY REGISTRATION DOCUMENT title in the
first ten OCR lines. Any second known title anywhere, including a property sale
deed title, rejects classification. One Registration Date/Date of Registration,
one Purchase Value and explicit matching supported currency are required.
Required words must meet the existing90-percent minimum confidence. The shared
strict money/grouping/precision, eight currencies, unambiguous ISO or English
dates, bounded TSV and native process/format/deadline/cancellation controls apply.
Deed Date, Market Value, Estimated Value and filenames are not substitutes.

One ASSET candidate preserves Purchase value stated on property registration
document, registration date, confidence and exact original Evidence/customer/
document version plus line/date provenance. Registration date is not purchase
date. Field/run/document-version identity and property-registration-{png|jpeg|pdf}-
ocr/1.0.0 versions distinguish this source. Literal zero is retained. Missing,
duplicate, ambiguous, invalid, conflicting or unclear required fields commit
zero facts and one typed nonretryable terminal failure. Existing malware/hash/
customer/tenant original-byte gates apply. No ownership/authenticity/current
value, valuation, net worth or full property-record inference is made.

Current and recovered Documents identify this registration source and explain
its date meaning. Explicit selection and confirmation precede adoption;
recovered views stay read-only. Only exact registration OCR versions are added
to the existing ASSET-to-PROPERTY owner mapping. Existing Passport/history/Search/
basic Reports preserve ASSET source presentation and original provenance. Upload
and extraction do not automatically write financial truth. No new package,
service, migration or development GitHub Actions.

## Verification

486 VS02 checks pass:53 new registration cases and3 additional actual owner HTTP
formats. Final t62-verified TRX records486 passed/0 failed. Real pinned Tesseract
5.3.4, hash-pinned English data and Poppler24.02.0 read synthetic PNG/JPEG/image-only
PDF, reject incomplete/mixed sources and preserve exact original bytes/hash/
customer/document identities. Existing Financial Profile normalization adopts
canonical PROPERTY facts only after review and retains original provenance.
Scanner and repositories are explicit doubles. The synthetic fixture was visually
inspected. Physical ClamAV tests were excluded, not counted as passing.

96 Documents UI cases pass, including7 new registration cases:three review/adoption
formats, three recovered read-only formats and a typed failure message. Final
focused UI verification covers the corrected ASSET recovered fixture and explicit
registration-versus-purchase-date explanation. TypeScript, changed-file lint and
Vite pass.40 reprocessing,5 affected Reporting and6 affected Search checks pass;
total537 backend checks. Gateway/owner build has zero warnings/errors. Existing
assets were used without tracked package/lock changes; fresh historical locked
restore is not demonstrated. SDK10.0.401 matches the pin; available Node24.19.0
does not prove the pinned24.21 runtime.

Live ClamAV benign/EICAR intake, physical PostgreSQL/durable OCR/classification,
PowerShell/Compose and full integrated browser UAT remain NOT RUN. Broader
registration/document classes/layouts/languages/pages, full property records,
ownership/authenticity/current valuation and complete financial models remain
open. No business or production acceptance is inferred.

After paired merge:62 numbered units =61 implementations +1 governance closure
(T1-04). Original29 deliverables remain0 complete,17 partial and12 pending journeys.
Original151 IDs/81 features and126 Phase1-applicable memberships remain unchanged.
Source tracker:phase1-deliverable-status.json. Exact merged source, compiled
knowledge and paired synchronization are recorded in the Expert receipt.
