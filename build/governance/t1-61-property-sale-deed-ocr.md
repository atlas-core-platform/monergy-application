# T1-61 — Property sale deed OCR and explicit purchase-value review

Base Application `14f0ac8cc98e5ef167133fbf8d0354c02d6a3243` (PR110),
Expert `3918e8680db287126f68fcd10f58361a0804bbae` (PR62), Architecture
`90934b51b2b203795acf95da2252a2cf1a1d1f4c`. Branch
`work/t1-61-property-sale-deed-ocr`.

Master sections11–13/41 explicitly includes Property Sale Deed classification,
OCR/extraction and property purchase_value. This unit advances the partial
MON-DOC-004/005/006/007 and existing ASSET source-fact journey. It does not deliver
ownership, location, type, current valuation or complete property records.

Supported printed-English PNG, upright JPEG and readable unlocked single-page
scanned PDF require exactly one PROPERTY SALE DEED title within the first ten
OCR lines. Any second known salary/loan statement/sanction/insurance/investment/
property title anywhere rejects classification. Exactly one Deed Date or Sale
Deed Date, one Purchase Value and explicit matching supported currency are
required. Required words must meet the existing90-percent minimum confidence.
Existing strict amount/grouping/precision, eight currencies, unambiguous ISO or
English date, bounded TSV and native process/format/deadline/cancellation controls
remain. Filename, Registration Date and Market/Estimated Value are not substitutes.

One ASSET candidate retains the label Purchase value stated on property sale deed,
deed date, confidence, exact Evidence/customer/document version and original line
provenance. Identity binds document version, processing run and purchase field.
Versions are property-sale-deed-{png|jpeg|pdf}-ocr/1.0.0. Source dates are invariant.
Zero remains a literal stated figure. No ownership/authenticity/current-value,
valuation, net-worth or complete property-record inference occurs. Missing,
ambiguous, conflicting, invalid or unclear required fields commit zero facts and
one typed nonretryable terminal failure. Original-byte malware/hash/customer/
tenant gates remain.

Current and recovered Documents identify the property sale deed and retain its
dated source meaning. Independent explicit selection and confirmation precede
adoption. Recovered views are read-only. Upload/extraction cannot write financial
truth. A necessary boundary correction maps only these exact ASSET extraction
versions to the existing canonical PROPERTY owner type. Other source versions
and object types remain unchanged. Passport list/detail/history, Search and basic
Reports present canonical PROPERTY facts in the existing ASSET source category,
retaining original identifiers, labels, dates and provenance. This is a source
figure, not an authoritative owned-property record.

## Verification

430 VS02 checks pass (50 new property cases and3 additional owner HTTP formats);
the final t61-verified TRX records430 passed/0 failed. Real pinned Tesseract5.3.4,
hash-pinned English data and Poppler24.02.0 read synthetic PNG/JPEG/image-only PDF.
Native incomplete/mixed titles reject. Actual Evidence HTTP verifies exact bytes,
hash, customer/tenant denials and explicit Financial Profile normalization using
the same property category boundary as the gateway. Original provenance survives.
Scanner and repository dependencies are explicit doubles. The synthetic image
was visually inspected. Physical ClamAV is excluded from this native test run.

89 Documents UI checks pass, including7 new property cases: three explicit review/
adoption formats, three recovered read-only formats and one typed failure message.
TypeScript, changed-file lint and Vite pass. Affected Reporting5 and Search6 checks pass for both legacy ASSET and canonical
PROPERTY responses, preserving boundary/provenance behavior.40 reprocessing checks
pass, and Gateway/owner compilation has zero warnings/errors. Total backend
checks:481. Search required a fresh cache-only restore with a separate temporary
lock path; no tracked package or lock files changed. A fresh restore against its
historical tracked dependency lock was not demonstrated. No new package, service, migration or
development GitHub Actions. SDK10.0.401 matches the pin; Node24.19.0 does not prove
the pinned24.21 runtime.

Live ClamAV benign/EICAR intake, physical PostgreSQL/durable OCR/classification,
PowerShell/Compose and full integrated browser UAT remain NOT RUN. Broader layouts,
languages/pages, complete property records, ownership/authenticity/current-value
verification and broader financial-model completion remain open. No business or
production acceptance is inferred.

After paired merge:61 numbered units =60 implementations +1 governance closure
(T1-04). Original29 deliverables remain0 complete,17 partial and12 pending journeys.
Classification/OCR/Extraction/Assets remain Partial. Original151 IDs/81 features
and126 Phase1-applicable membership remain unchanged. Source tracker:
build/governance/phase1-deliverable-status.json. Exact merged source, compiled
knowledge and paired synchronization are in the Expert receipt.
